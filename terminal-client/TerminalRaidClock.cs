using System;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // pin the raid START time to night — terminal's whole event is authored for
    // darkness. the clock is re-anchored once and keeps TICKING afterward (dynamic
    // time, not a freeze). there are TWO clock objects and they appear at different
    // times: GameWorld.GameDateTime (raid watch) exists at OnGameStarted, but
    // TODSkyProvider.CurrentTime's — the one TerminalLights.TickSkyTime feeds the TOD sky
    // from — may not, and a single-shot postfix reset only ever caught the first
    // (2026-08-10 raid: "1 clock(s)" anchored, sky stayed daytime). so a small host
    // retries both for up to 20s and reports which landed.
    internal static class TerminalRaidClock
    {
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_PinRaidClock
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On) return;
                if (Plugin.RaidStartHour.Value < 0f) return;
                var go = new GameObject("Terminal_RaidClock");
                go.AddComponent<ClockHost>();
            }
        }

        internal class ClockHost : MonoBehaviour
        {
            private float _started;
            private float _next;
            private bool _worldDone, _skyDone;

            private void Start() => _started = Time.realtimeSinceStartup;

            private void Update()
            {
                if (Time.realtimeSinceStartup < _next) return;
                _next = Time.realtimeSinceStartup + 0.5f;

                float hour = Plugin.RaidStartHour.Value;
                int h = (int)hour;
                int m = (int)((hour - h) * 60f);

                if (!_worldDone)
                    _worldDone = TryAnchor(Singleton<GameWorld>.Instance?.GameDateTime, h, m, "GameWorld");
                if (!_skyDone)
                {
                    var skyClock = TODSkyProvider.Instance?.CurrentTime?.GameDateTime;
                    // same object as the world clock = already anchored by that reset
                    if (skyClock != null && _worldDone && ReferenceEquals(skyClock, Singleton<GameWorld>.Instance?.GameDateTime))
                        _skyDone = true;
                    else if (skyClock != null)
                        _skyDone = TryAnchor(skyClock, h, m, "sky/TODSkyProvider");
                }

                if (_worldDone && _skyDone)
                {
                    Plugin.Log.LogInfo($"[RaidClock] both clocks anchored to {h:00}:{m:00} — night raid, still ticking");
                    Destroy(gameObject);
                }
                else if (Time.realtimeSinceStartup - _started > 20f)
                {
                    Plugin.Log.LogWarning($"[RaidClock] gave up after 20s (world={_worldDone}, sky={_skyDone}) — "
                                        + "whichever missed keeps natural time");
                    Destroy(gameObject);
                }
            }

            private static bool TryAnchor(GameDateTime gdt, int h, int m, string label)
            {
                if (gdt == null) return false;
                try
                {
                    var now = gdt.Calculate();
                    var target = new DateTime(now.Year, now.Month, now.Day, h, m, 0);
                    gdt.Reset(target);
                    Plugin.Log.LogDebug($"[RaidClock] {label} clock anchored to {h:00}:{m:00}");
                    return true;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[RaidClock] {label} clock anchor failed: {e.Message}");
                    return true; // don't retry a throwing clock forever
                }
            }
        }
    }
}
