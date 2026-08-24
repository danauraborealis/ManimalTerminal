using System;
using System.Collections.Generic;
using UnityEngine;

namespace Manimal.Terminal
{
    // WEAPON-LIGHT SHADOW GUARD (2026-08-21, the chop).
    //
    // the north-half chop is GPU stall time (FinishFrameRendering, 3ms -> 12ms,
    // peak 227ms) and it tracks ONE number: the count of real-time SHADOW-CASTING
    // lights, which climbs 1 -> 23 over a raid while total light count barely
    // moves. these are bot weapon flashlights and laser/IR illuminators — every
    // one of them re-renders shadow casters into a shadow map each frame, against
    // a 208k-renderer scene.
    //
    // it also explains the result that broke every earlier theory: killing all the
    // bots didn't help, because CORPSES KEEP THEIR LIGHTS ON. the shadow casters
    // survive the bots.
    //
    // map lamps are left alone — they're driven by TerminalLights/LampShadows and
    // are authored content. only lights living OUTSIDE the Terminal_* scenes
    // (weapons, players, bots — they load into DontDestroyOnLoad) get their
    // shadows dropped.
    internal static class TerminalShadowGuard
    {
        private static float _next;
        private static float _nextRefresh;
        private static bool _dumped;
        private static int _lastKilled;
        private static int _lastAlive = -1;
        private static readonly List<Light> _candidates = new List<Light>();   // weapon / actor lights
        private static readonly List<Light> _lamps = new List<Light>();        // CullingLightObject-owned map lamps

        internal static void ResetForRaid()
        {
            _next = 0f;
            _nextRefresh = 0f;
            _dumped = false;
            _lastKilled = 0;
            _lastAlive = -1;
            _candidates.Clear();
        }

        // MAP LAMP TEST, v2. v1 keyed on scene name ("is it in a Terminal_* scene")
        // and that was wrong twice over: the ENDING CUTSCENE's actors are spawned
        // into a Terminal scene, so their weapon lights were treated as authored
        // map lamps and kept their shadows — the chop came straight back for the
        // whole take.
        //
        // the real discriminator is ownership: every one of terminal's authored
        // lamps is CullingLightObject-owned (the lamp reviver logs "0 plain lamps +
        // 1854 native culling lights"). weapon/actor lights never are. so walk for
        // a CullingLightObject instead of trusting the scene.
        private static bool IsMapLight(Light l)
        {
            try
            {
                for (var t = l.transform; t != null; t = t.parent)
                    if (t.GetComponent<CullingLightObject>() != null) return true;
                return false;
            }
            catch { return false; }
        }

        internal static void Tick()
        {
            if (!TerminalGate.On) return;
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1f;

            try
            {
                // v1 ran FindObjectsOfType<Light>() every 3s over ~1900 lights and
                // cost 28ms in the frame it landed on — a self-inflicted stutter.
                // now the expensive discovery pass only runs when something could
                // have CHANGED (a spawn shifts the alive count) or on a slow timer,
                // and the per-second pass just walks the small cached candidate list.
                int alive = -1;
                try
                {
                    var gw = Comfort.Common.Singleton<EFT.GameWorld>.Instantiated
                        ? Comfort.Common.Singleton<EFT.GameWorld>.Instance : null;
                    alive = gw != null && gw.AllAlivePlayersList != null ? gw.AllAlivePlayersList.Count : -1;
                }
                catch { }

                if (alive != _lastAlive || Time.realtimeSinceStartup >= _nextRefresh)
                {
                    _lastAlive = alive;
                    _nextRefresh = Time.realtimeSinceStartup + 20f;
                    Refresh();
                }

                int killed = 0;

                if (!Plugin.WeaponLightShadows.Value)
                    killed += Sweep(_candidates);

                // MAP LAMPS TOO (2026-08-22). v2 exempted these on the assumption
                // that TerminalLights/LampShadows already owned them — it doesn't.
                // DriveLamp is the only thing that writes light.shadows, and it is
                // SKIPPED for CullingLightObject-owned lamps whenever CullingManager
                // exists, which is all 1785 of them. so LampShadows=false never
                // reached them, CullingLightObject switches them on as the player
                // approaches, and the shadow-caster count climbs 1 -> 16 while the
                // frame goes 12ms -> 40ms. exactly the same blind spot that made
                // LampIntensity=0 look like it "ruled lamps out".
                if (!Plugin.LampShadows.Value)
                    killed += Sweep(_lamps);

                if (killed > 0 && killed != _lastKilled)
                {
                    _lastKilled = killed;
                    Plugin.Log.LogInfo($"[ShadowGuard] shadows dropped on {killed} light(s) this pass "
                        + $"({_candidates.Count} weapon/actor + {_lamps.Count} lamp(s) tracked)");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[ShadowGuard] sweep failed: {e.Message}"); }
        }

        private static int Sweep(List<Light> list)
        {
            int killed = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var l = list[i];
                if (l == null) { list.RemoveAt(i); continue; }
                if (l.shadows == LightShadows.None) continue;
                l.shadows = LightShadows.None;
                killed++;
            }
            return killed;
        }

        // the expensive half — kept off the per-second path. classification is
        // cached here so the per-second sweep never walks parents again.
        private static void Refresh()
        {
            _candidates.Clear();
            _lamps.Clear();
            var offenders = _dumped ? null : new List<string>();
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null) continue;
                bool lamp = IsMapLight(l);
                (lamp ? _lamps : _candidates).Add(l);

                if (offenders != null && l.shadows != LightShadows.None && offenders.Count < 14)
                {
                    var path = l.gameObject.name;
                    for (var t = l.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                    offenders.Add($"\n    [{(lamp ? "LAMP" : "weapon/actor")}] {l.type} shadows={l.shadows} "
                        + $"range={l.range:0.#} intensity={l.intensity:0.##} {path}");
                }
            }
            if (offenders != null && offenders.Count > 0)
            {
                _dumped = true;
                Plugin.Log.LogWarning($"[ShadowGuard] shadow-casting light(s) found — each renders a shadow map "
                    + $"per frame ({_lamps.Count} lamps / {_candidates.Count} weapon-actor tracked):"
                    + string.Join("", offenders.ToArray()));
            }
        }
    }
}
