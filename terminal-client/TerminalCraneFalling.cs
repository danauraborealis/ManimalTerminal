using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Manimal.Terminal
{
    // THE CRANE-FALL RESTORE (Terminal_Crane_Falling, Design_Stuff / level628):
    // a pure trigger-network prop — TriggerZone emits Switch_crane_enter when a
    // player walks under the crane, then authored HandlerDelays fan out: 1s ->
    // Fall_01_* (HandlerAnimator IsFall, crane falls + HandlerPlaySoundSequenced
    // crash sound), 1s -> Play_Crane_Explosive (fire burst foley), 1.93s more ->
    // contusion effect via HandlerFilterOnce, 3s -> Mortar_Crane dust particles,
    // 20s -> the animated crane swaps for the static Terminal_Crash_set wreck.
    // Damage_zones under the fall path deal 1000/0.5s while Crane_fall_01 runs.
    //
    // everything is native 4.0 — no interaction layer at all, just resurrection
    // from terminal_crane.json. the only 1.0-only class, HandlerPlayerCameraShake,
    // is authored with BOTH strengths 0.0 (a no-op even in retail) — dropped.
    // retail fired the crash sound through MP state-sync (ApplyState); offline we
    // give it the fall trigger id instead so it plays the moment the crane lets go.
    internal static class TerminalCraneFalling
    {
        private const string FallTrigger = "Fall_01_2944538615";

        private static bool _staged;
        internal static bool Ready => _staged;

        // HandlerDelay deliberately OUT — gutted in this build, shimmed instead
        private static readonly HashSet<string> NativeSet = new HashSet<string>
        {
            "TriggerZone", "HandlerDamage", "HandlerGOState",
            "HandlerGameObjectState", "HandlerEffect", "HandlerAnimator",
            "HandlerFilterOnce", "HandlerPlaySoundAdvanced", "HandlerPlaySoundSequenced",
        };

        internal static void ResetForRaid() => _staged = false;

        internal static void TryStage()
        {
            if (_staged || !Plugin.CraneFalling.Value) return;
            if (!TerminalGate.On) return;
            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (gw == null || gw.TriggersEmitter == null) return;

            var root = TerminalRigFill.FindRootNamed("Terminal_Crane_Falling");
            if (!root) return; // scenes not up yet

            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_crane.json");
                if (!System.IO.File.Exists(path))
                {
                    Plugin.Log.LogWarning("[Crane] terminal_crane.json missing — crane stays inert");
                    _staged = true;
                    return;
                }
                var sc = JObject.Parse(System.IO.File.ReadAllText(path));

                var index = new Dictionary<string, Transform>();
                TerminalRigFill.IndexTree(root, "Terminal_Crane_Falling", index);
                TerminalRigFill.StageDelayShims(sc["rows"] as JArray, "Crane");
                TerminalRigFill.StageRows(sc["rows"] as JArray, NativeSet, index, "Crane", (comp, row) =>
                {
                    // the crash-sound handler's play trigger is EMPTY in retail (MP
                    // state-sync drove it) — wire it to the fall trigger for offline
                    if (row.Value<string>("cls") == "HandlerPlaySoundSequenced")
                    {
                        var fi = HarmonyLib.AccessTools.Field(comp.GetType(), "_playTriggerId");
                        if (fi != null && string.IsNullOrEmpty(fi.GetValue(comp) as string))
                            fi.SetValue(comp, FallTrigger);
                    }
                });

                // the wreck stays hidden until the 20s handoff no matter how the rip shipped it
                if (index.TryGetValue("Terminal_Crane_Falling/INTERACTIVE_Terminal_Crash_Set_VFX/Terminal_Crash_set", out var wreck))
                    wreck.gameObject.SetActive(false);

                // WHITE-BLOCKS FIX (raid report 2026-08-18): staging activated rig GOs
                // the rip shipped inactive, exposing meshes the raid-start passes never
                // treated — SHADOW meshes drawing as opaque white shells (the icebreaker
                // heli lesson: rip drops the caster-only flag) and dead-shader materials.
                // both passes re-run scoped to the rig, AFTER activation.
                int shadowFixed = 0;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    if (r && r.name.Contains("SHADOW")
                        && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                    {
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                        shadowFixed++;
                    }
                try { TerminalShaderRebind.RebindNow(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Crane] post-stage rebind failed: {e.Message}"); }
                if (shadowFixed > 0) Plugin.Log.LogInfo($"[Crane] {shadowFixed} SHADOW mesh(es) set caster-only");

                // white-box forensics (2026-08-18): name every visible material on the
                // rig so the next raid log convicts the broken ones by name — the
                // water lesson says a WRONG-but-valid shader survives the rebind
                int dumped = 0;
                var seenMats = new HashSet<Material>();
                foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.name.Contains("SHADOW")) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (!m || !seenMats.Add(m) || dumped >= 20) continue;
                        dumped++;
                        Plugin.Log.LogInfo($"[Crane] mat '{m.name}' on '{r.name}': shader "
                            + $"'{(m.shader ? m.shader.name : "NULL")}' supported={(m.shader && m.shader.isSupported)}");
                    }
                }

                // our own ears on the crane chain: proves whether the TriggerZone emits
                // and whether anything consumes (2026-08-18 raid: rig fully inert)
                try
                {
                    EFT.GameTriggers.TriggersEmitter.Instance.Subscribe("Switch_crane_enter", new Action(() =>
                        Plugin.Log.LogInfo("[Crane] TRIGGERED — player under the crane, fall chain should run")));
                    EFT.GameTriggers.TriggersEmitter.Instance.Subscribe(FallTrigger, new Action(() =>
                        Plugin.Log.LogInfo("[Crane] fall trigger fired — animator should be dropping the crane")));
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Crane] diag subscribe failed: {e.Message}"); }

                _staged = true;
                TerminalRigFill.ScheduleSubscriberDump("Crane", "Switch_crane_enter", FallTrigger, "Crane_fall_01", "Mortar_Crane_Play");
                Plugin.Log.LogInfo("[Crane] CRANE_FALLING RESTORED — walk-under trigger armed, fall/damage/foley native");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Crane] staging failed: {e}");
                _staged = true;
            }
        }
    }
}
