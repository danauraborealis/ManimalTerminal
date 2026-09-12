using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Manimal.Terminal
{
    // THE ARTILLERY BARRAGE (ArtilleryTriggers, level628): four authored trigger
    // boxes, each with a native TriggerZone (artillery_trigger_1..4) plus a
    // 1.0-only ArtilleryChangeTriggerHandler. 4.0 SHIPS the full shelling system
    // (ArtilleryShellingControllerServer + client FX/audio — the Streets mortar event)
    // gated on ArtilleryMapsConfigs containing the location id, so:
    //  - InjectConfig (at location capture, BEFORE BaseLocalGame checks) plants a
    //    Terminal map config built from terminal_artillery.json — 4 shelling zones
    //    centered on the authored trigger boxes, guns as an offshore battery
    //  - TryStage resurrects the TriggerZones; entering one emits its trigger and
    //    we call StartImmediateShellingZone on that zone: 10s warning whistle,
    //    then the barrage walks the area you're in. run.
    internal static class TerminalArtillery
    {
        private static bool _staged;
        private static bool _injected;

        private static readonly HashSet<string> NativeSet = new HashSet<string> { "TriggerZone" };

        internal static void ResetForRaid()
        {
            _staged = false;
            _pumpUntil = -1f;
            _stopped = true;
            // config injection persists across raids by design — the dict entry
            // survives and BaseLocalGame re-checks it every game creation
        }

        // must run BEFORE BaseLocalGame.method_4's ArtilleryMapsConfigs check —
        // called from the location-capture prefix, only for Terminal raids
        internal static void InjectConfig()
        {
            if (_injected || !Plugin.Artillery.Value) return;
            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_artillery.json");
                if (!System.IO.File.Exists(path))
                {
                    Plugin.Log.LogWarning("[Artillery] terminal_artillery.json missing — no barrage");
                    _injected = true;
                    return;
                }
                var cfgJson = JObject.Parse(System.IO.File.ReadAllText(path));

                var backend = Singleton<EFT.GlobalConfiguration>.Instance;
                var shelling = backend != null ? backend.ArtilleryShelling : null;
                if (shelling == null || shelling.ArtilleryMapsConfigs == null)
                {
                    Plugin.Log.LogWarning("[Artillery] backend ArtilleryShelling config absent — no barrage");
                    return; // retry next raid, backend may not be up yet
                }

                var cfgType = AccessTools.TypeByName("CommonAssets.Scripts.ArtilleryShelling.ArtilleryShellingMapConfiguration")
                    ?? AccessTools.TypeByName("ArtilleryShellingMapConfiguration");
                if (cfgType == null) { Plugin.Log.LogWarning("[Artillery] map-config type missing"); _injected = true; return; }
                object cfg = Activator.CreateInstance(cfgType);
                TerminalRigFill.FillObject(cfg, cfgJson, new Dictionary<string, Transform>());

                var dict = (System.Collections.IDictionary)shelling.ArtilleryMapsConfigs;
                dict["Terminal"] = cfg;
                _injected = true;
                Plugin.Log.LogInfo("[Artillery] Terminal shelling config injected — native controllers will arm at game start");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Artillery] config inject failed: {e}"); }
        }

        // NOBODY pumps ServerShellingController.OnUpdate in the offline game path
        // (proved 2026-08-18: zones tripped, countdown ran, TargetShellingOn set —
        // and the firing loop never executed). we are the pump — but a BOUNDED one:
        // pumping forever left the controller grinding accumulated shelling state all
        // raid (A/B-proved 2026-08-18: the endgame 36ms chop died with artillery off).
        // pump only through a barrage window after a trip, then StopShelling and idle.
        private const float BarrageWindow = 60f; // 10s whistle + the barrage; user-tuned 2026-08-18
        private static float _pumpUntil = -1f;
        private static bool _stopped = true;

        internal static void Pump()
        {
            if (!Plugin.Artillery.Value || !TerminalGate.On) return;
            try
            {
                var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
                var ctrl = gw?.ServerShellingController;
                if (ctrl == null) return;
                if (Time.realtimeSinceStartup < _pumpUntil)
                {
                    ctrl.OnUpdate();
                    _stopped = false;
                }
                else if (!_stopped)
                {
                    _stopped = true;
                    try { ctrl.StopShelling(); } catch { }
                    // the bounded stop means the natural OnArtilleryEnd never fires,
                    // and zones register with Duration -1 (never self-expire) — a
                    // lingering ActiveZonesOnMap entry makes every nearby bot grind
                    // cover scans vs the zone forever (2026-08-18: the 36ms endgame
                    // chop began 45s after zone 4's window closed and never stopped)
                    ClearLingeringZones();
                    Plugin.Log.LogInfo("[Artillery] barrage window closed — shelling stopped, pump idle until the next zone trip");
                }
            }
            catch (Exception e)
            {
                if (!_pumpWarned) { _pumpWarned = true; Plugin.Log.LogWarning($"[Artillery] pump threw (once): {e}"); }
            }
        }
        private static bool _pumpWarned;

        private static void ClearLingeringZones()
        {
            try
            {
                var dict = ActiveZonesDict();
                if (dict == null)
                {
                    // reflection miss vs genuinely empty must be distinguishable —
                    // 2026-08-19 raid: no clear line ever logged and we couldnt tell
                    Plugin.Log.LogWarning("[Artillery] zones controller/dict not reachable — clear skipped (reflection miss?)");
                    return;
                }
                int n = dict.Count;
                if (n > 0)
                {
                    dict.Clear();
                    Plugin.Log.LogInfo($"[Artillery] {n} lingering danger zone(s) cleared — bots stop scanning them");
                }
                else Plugin.Log.LogInfo("[Artillery] no lingering zones at window close (registry was empty)");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Artillery] zone clear failed: {e.Message}"); }
        }

        // shared accessor for the client-side artillery zone registry — also used
        // by the perf watcher to annotate spikes.
        //
        // reflection is cached per BotsController type: on 4.0.13 there is no
        // `ArtilleryZonesController` member on BotsController at all, and the
        // uncached AccessTools.Property lookup emits a WARNING every call
        // (HarmonyX logs cache misses at Warning level — 2026-08-21 raid log:
        // one warning per PerfWatch spike-log). resolve once, flag "missing",
        // then never touch reflection again.
        private static Type _bcTypeCached;
        private static System.Reflection.PropertyInfo _bcZcProp;
        private static System.Reflection.FieldInfo _bcZcField;
        private static Type _zcTypeCached;
        private static System.Reflection.PropertyInfo _zcMapProp;
        private static System.Reflection.FieldInfo _zcMapField;
        internal static System.Collections.IDictionary ActiveZonesDict()
        {
            try
            {
                var game = Singleton<IBotGame>.Instantiated ? Singleton<IBotGame>.Instance : null;
                var bc = game?.BotsController;
                if (bc == null) return null;

                var bcT = bc.GetType();
                if (_bcTypeCached != bcT)
                {
                    _bcTypeCached = bcT;
                    _bcZcProp = bcT.GetProperty("ArtilleryZonesController",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    _bcZcField = bcT.GetField("ArtilleryZonesController",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                }
                object zc = _bcZcProp?.GetValue(bc) ?? _bcZcField?.GetValue(bc);
                if (zc == null) return null;

                var zcT = zc.GetType();
                if (_zcTypeCached != zcT)
                {
                    _zcTypeCached = zcT;
                    _zcMapProp = zcT.GetProperty("ActiveZonesOnMap",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    _zcMapField = zcT.GetField("ActiveZonesOnMap",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                }
                return (_zcMapProp?.GetValue(zc) ?? _zcMapField?.GetValue(zc)) as System.Collections.IDictionary;
            }
            catch { return null; }
        }

        internal static bool Pumping => !_stopped;

        internal static void TryStage()
        {
            if (_staged || !Plugin.Artillery.Value) return;
            if (!TerminalGate.On) return;
            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (gw == null || gw.TriggersEmitter == null) return;

            var root = TerminalRigFill.FindRootNamed("ArtilleryTriggers");
            if (!root) return;

            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_endgame.json");
                var sc = JObject.Parse(System.IO.File.ReadAllText(path));

                var index = new Dictionary<string, Transform>();
                TerminalRigFill.IndexTree(root, "ArtilleryTriggers", index);
                TerminalRigFill.StageRows(sc["rows"] as JArray, NativeSet, index, "Artillery");

                if (gw.ServerShellingController == null)
                    Plugin.Log.LogWarning("[Artillery] shelling controller NOT created — config injection missed the game-create window?");

                for (int i = 1; i <= 4; i++)
                {
                    var zoneId = i.ToString();
                    EFT.GameTriggers.TriggersEmitter.Instance.Subscribe($"artillery_trigger_{i}", new Action(() => OnZoneTripped(zoneId)));
                }

                _staged = true;
                Plugin.Log.LogInfo("[Artillery] ARTILLERY RESTORED — 4 trigger zones armed, barrage on entry");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Artillery] staging failed: {e}");
                _staged = true;
            }
        }

        private static void OnZoneTripped(string zoneId)
        {
            try
            {
                var ctrl = ArtilleryShellingControllerServer.Instance;
                if (ctrl == null) { Plugin.Log.LogWarning("[Artillery] zone tripped but no shelling controller"); return; }
                ctrl.StartImmediateShellingZone(zoneId);
                _pumpUntil = Time.realtimeSinceStartup + BarrageWindow;
                Plugin.Log.LogInfo($"[Artillery] zone {zoneId} tripped — shelling called in ({BarrageWindow:0}s pump window)");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Artillery] shelling start failed: {e}"); }
        }
    }
}
