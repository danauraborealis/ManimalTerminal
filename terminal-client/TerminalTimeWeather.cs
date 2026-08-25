using System;
using Comfort.Common;
using EFT;
using EFT.Weather;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // TIME + WEATHER, THE WAY THE TIME&WEATHER CHANGER DOES IT (user call 2026-08-11:
    // "why arent we just setting the time and weather at start of raid using whatever
    // method time and weather changer uses"). they're right — it's two calls:
    //   time:    GameWorld.GameDateTime.Reset(target)
    //   weather: WeatherController.Instance.WeatherDebug.isEnabled = true + the fields,
    //            held briefly for the rainy opening and then disabled so the server's
    //            native WeatherCurve owns the rest of the raid
    //
    // WHY OUR TIME NEVER STUCK, despite Reset() reporting success every raid: TOD_Time
    // holds its OWN GameDateTime reference and RECOMPUTES the sky's cycle from it every
    // update (TOD_Time:245 -> GameDateTime.CalculateTaxonomyDate, :327 writes the cycle
    // back). in vanilla that reference IS the GameWorld clock, so TWC's single Reset
    // moves the sky too. on a resurrected rip nothing ever wired it, so TOD_Time kept
    // recomputing from an unanchored clock and stomped our per-second Cycle writes —
    // the sky sat in daylight while every log line said 22:00. wiring that one field is
    // what makes the standard approach work here.
    internal static class TerminalTimeWeather
    {
        private static bool _done;

        internal static void ResetForRaid() => _done = false;

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_Arm
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On) return;
                ResetForRaid();
                new GameObject("Terminal_TimeWeather").AddComponent<Host>();
            }
        }

        internal class Host : MonoBehaviour
        {
            private float _next;
            private float _deadline;
            private bool _skyWired;
            private bool _timeSet;
            private bool _weatherSeeded;
            private float _weatherReleaseAt;
            private WeatherDebug _seededWeatherDebug;

            private void Start() => _deadline = Time.realtimeSinceStartup + 60f;

            private void Update()
            {
                if (_done) { Destroy(gameObject); return; }

                // WeatherDebug is an override selector, not a one-shot setter. Leaving
                // it enabled permanently pins rain/clouds/fog for the whole raid. Once
                // the opening has had time to establish the retail storm, turn it off
                // and WeatherController immediately returns to its populated native
                // WeatherCurve (the weather request was installed before OnGameStarted).
                if (_weatherSeeded)
                {
                    if (!Plugin.ForceWeather.Value || Time.realtimeSinceStartup >= _weatherReleaseAt)
                        ReleaseStartupWeather();
                    return;
                }

                if (Time.realtimeSinceStartup < _next) return;
                _next = Time.realtimeSinceStartup + 0.5f;

                // the sky registers late — keep trying rather than giving up (the old
                // 20s "gave up (sky=False)" was this same race, quietly losing)
                if (Time.realtimeSinceStartup > _deadline)
                {
                    Plugin.Log.LogWarning("[TimeWeather] gave up after 60s — "
                        + $"sky clock wired={_skyWired}, weather stack up={(bool)WeatherController.Instance}");
                    Destroy(gameObject);
                    return;
                }

                var world = Singleton<GameWorld>.Instance;
                var gdt = world ? world.GameDateTime : null;
                if (gdt == null) return;

                if (!_skyWired) _skyWired = WireSkyClock(gdt);
                if (!_skyWired) return; // no sky yet — try again shortly

                if (!_timeSet && Plugin.RaidStartHour.Value >= 0f) { SetTime(gdt); _timeSet = true; }

                // the weather stack is resurrected on its own schedule (TerminalWeather),
                // so don't walk away the first time WeatherController is missing — keep
                // the host alive until it appears or the deadline passes. the old
                // give-up-immediately is why "no WeatherController — weather left alone"
                // was the last word on rain.
                if (!SeedStartupWeather())
                {
                    TerminalWeather.TryStage(); // nudge it along
                    return;
                }

                if (!_weatherSeeded)
                {
                    // ForceWeather is off: clock work is complete and there is no
                    // temporary weather override to supervise.
                    _done = true;
                    Destroy(gameObject);
                }
            }

            // THE missing link: point TOD_Time at the same clock instance the raid uses,
            // so its own recompute lands on our anchored time instead of fighting it.
            private static bool WireSkyClock(GameDateTime gdt)
            {
                try
                {
                    var todTime = UnityEngine.Object.FindObjectOfType<TOD_Time>();
                    if (!todTime) return false;
                    var fi = AccessTools.Field(typeof(TOD_Time), "GameDateTime");
                    if (fi == null) return false;
                    var current = fi.GetValue(todTime) as GameDateTime;
                    if (!ReferenceEquals(current, gdt))
                    {
                        fi.SetValue(todTime, gdt);
                        Plugin.Log.LogWarning($"[TimeWeather] TOD_Time.GameDateTime rewired to the raid clock "
                            + $"(was {(current == null ? "NULL" : "a different instance")})");
                    }

                    // AND TAKE THE WHEEL. user 2026-08-11: "its been dark and night
                    // before, now randomly its always brighter — idk if its just the
                    // time im going into the map". that IS the mechanism: TOD_Time.Start
                    // seeds the sky's cycle from EFTDateTimeClass.Now (real-world-ish
                    // time) and then keeps advancing it every frame, overwriting our
                    // per-second Cycle writes. play at night IRL -> dark map; play in the
                    // afternoon -> daylight, exactly as observed, and no amount of
                    // Reset()ing the raid clock changes it while this component keeps
                    // driving. disable it and the cycle belongs to TickSkyTime alone,
                    // which follows the anchored raid clock — so the real-world hour
                    // stops mattering, which is the whole point of a manual setting.
                    if (todTime.enabled)
                    {
                        todTime.enabled = false;
                        Plugin.Log.LogWarning("[TimeWeather] TOD_Time disabled — it seeds the sky from the REAL clock "
                            + "and re-stomps it every frame; the raid clock owns the sky now");
                    }
                    return true;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[TimeWeather] sky clock wiring failed: {e.Message}");
                    return false;
                }
            }

            // exactly TimeWeatherChanger's Set button: compute the delta from the live
            // time and Reset() the clock. the raid keeps ticking from there.
            private static void SetTime(GameDateTime gdt)
            {
                try
                {
                    float hour = Plugin.RaidStartHour.Value;
                    int h = (int)hour, m = (int)((hour - h) * 60f);
                    var now = gdt.Calculate();
                    var target = now.AddHours(h - now.Hour).AddMinutes(m - now.Minute);
                    gdt.Reset(target);
                    Plugin.Log.LogWarning($"[TimeWeather] time set to {target:HH:mm} (was {now:HH:mm}) — retail terminal runs 21:00-06:00");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[TimeWeather] time set failed: {e.Message}"); }
            }

            // Seed the opening with the weather sliders, but keep the host alive to
            // release WeatherDebug after the configured startup window.
            private bool SeedStartupWeather()
            {
                try
                {
                    if (!Plugin.ForceWeather.Value) return true;
                    var wc = WeatherController.Instance;
                    if (!wc) return false; // not up yet — the caller retries
                    var dbg = wc.WeatherDebug;
                    if (dbg == null) { Plugin.Log.LogDebug("[TimeWeather] no WeatherDebug — weather left alone"); return true; }

                    dbg.isEnabled = true;
                    dbg.Rain = Plugin.WeatherRain.Value;
                    dbg.CloudDensity = Plugin.WeatherClouds.Value;
                    dbg.Fog = Plugin.WeatherFog.Value;
                    dbg.WindMagnitude = Plugin.WeatherWind.Value;
                    dbg.LightningThunderProbability = Plugin.WeatherThunder.Value;
                    _seededWeatherDebug = dbg;
                    _weatherSeeded = true;
                    float hold = Mathf.Clamp(Plugin.WeatherStartHoldSeconds.Value, 5f, 300f);
                    _weatherReleaseAt = Time.realtimeSinceStartup + hold;
                    Plugin.Log.LogWarning($"[TimeWeather] startup weather seeded for {hold:0}s — "
                        + $"rain={dbg.Rain:0.00} clouds={dbg.CloudDensity:0.00} fog={dbg.Fog:0.000} "
                        + $"wind={dbg.WindMagnitude:0.00} thunder={dbg.LightningThunderProbability:0.00}; "
                        + "native weather will resume afterward");
                    return true;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[TimeWeather] weather set failed: {e.Message}");
                    return true; // don't spin on a throwing weather system
                }
            }

            private void ReleaseStartupWeather()
            {
                try
                {
                    if (_seededWeatherDebug != null)
                        _seededWeatherDebug.Enabled = false;

                    var wc = WeatherController.Instance;
                    if (wc)
                    {
                        var natural = wc.WeatherCurve;
                        Plugin.Log.LogWarning("[TimeWeather] startup weather released — native weather curve resumed: "
                            + $"rain={natural.Rain:0.00} clouds={natural.Cloudiness:0.00} fog={natural.Fog:0.000} "
                            + $"wind={natural.Wind.magnitude:0.00} thunder={natural.LightningThunderProbability:0.00}");
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[TimeWeather] startup weather released; WeatherController was already gone");
                    }
                }
                catch (Exception e)
                {
                    // Always clear the selector even if a diagnostic property throws.
                    if (_seededWeatherDebug != null) _seededWeatherDebug.Enabled = false;
                    Plugin.Log.LogWarning($"[TimeWeather] startup weather release completed with a diagnostic error: {e.Message}");
                }
                finally
                {
                    _weatherSeeded = false;
                    _seededWeatherDebug = null;
                    _done = true;
                    Destroy(gameObject);
                }
            }
        }
    }
}
