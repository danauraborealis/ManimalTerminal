using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Communications;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Manimal.Terminal
{
    // THE PUMP-STATION POWER PUZZLE (Water_station + Electric_box(AVR)_for_key_02,
    // Design_Main / level629), decoded from retail 1.0:
    //   1. ElectricalCabinetSelector breaks N random cabinets by group size
    //      (authored table: solo = 1 broken from a 2-cabinet pool) — broken shows
    //      the with_logic rig (red light, sparking loop), the rest show the plain
    //      Closed shells (green light, working loop)
    //   2. each broken cabinet repairs via its Electric_box_Switch: WITH a toolkit
    //      -> safe 5s repair; bare-handed -> unsafe 5s repair at the authored 10%
    //      chance ('INTERACTIVES_TERMINAL_ELECTRIC_BOX_UNSAFE_CHANCE'), failure =
    //      electric shock (native HandlerDamage, 10dmg to both arms) + notification
    //   3. all broken repaired -> 'on_electric_boxes_fixed' -> native HandlerGOState
    //      pair swaps the water-station panel buttons (powered ON)
    //   4. the panel button -> 'Water_remove_*' -> native HandlerAnimator drains the
    //      water (IsRemoved), pump sounds start, the reservoir empties
    // native 4.0 classes are resurrected from terminal_pump.json; the 1.0-only
    // selector/plant/chance/notification/AND-gate layer is ours. quest-condition
    // rows are authored EMPTY in retail (test leftovers) — dropped.
    internal static class TerminalPumpStation
    {
        private const string BoxesFixedTrigger = "on_electric_boxes_fixed";
        private const string WaterRemoveTrigger = "Water_remove_1178689405";
        internal const float RepairSeconds = 5f;
        private const float UnsafeChance = 0.1f;

        // the safe-repair tool: the toolset (user-confirmed — the odin requirement
        // blob hid the tpl)
        private const string MultitoolTpl = "590c2e1186f77425357b6124";

        private static bool _staged;
        internal static bool PowerFixed;
        internal static bool WaterDrained;
        private static readonly HashSet<string> _broken = new HashSet<string>();   // cabinet paths still broken
        private static readonly Dictionary<string, CabinetInfo> _cabinets = new Dictionary<string, CabinetInfo>();

        internal class CabinetInfo
        {
            public string Path;      // with_logic root relpath
            public string Suffix;    // per-cabinet trigger suffix
            public Transform Root;
        }

        private static readonly HashSet<string> NativeSet = new HashSet<string>
        {
            "HandlerAnimator", "HandlerGOState", "HandlerGameObjectState",
            "HandlerPlaySoundAdvanced", "HandlerPlaySoundSequenced", "HandlerDamage",
        };

        internal static void ResetForRaid()
        {
            _staged = false;
            PowerFixed = false;
            WaterDrained = false;
            _broken.Clear();
            _cabinets.Clear();
        }

        internal static void TryStage()
        {
            if (_staged || !Plugin.PumpStation.Value) return;
            if (!TerminalGate.On) return;
            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (gw == null || gw.TriggersEmitter == null) return;

            var water = TerminalRigFill.FindRootNamed("Water_station");
            var ebox = TerminalRigFill.FindRootNamed("Electric_box(AVR)_for_key_02");
            if (!water || !ebox) return; // scenes not up yet

            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_pump.json");
                var sc = JObject.Parse(System.IO.File.ReadAllText(path));
                var rows = sc["rows"] as JArray;

                var index = new Dictionary<string, Transform>();
                TerminalRigFill.IndexTree(water, "Water_station", index);
                TerminalRigFill.IndexTree(ebox, "Electric_box(AVR)_for_key_02", index);
                TerminalRigFill.StageRows(rows, NativeSet, index, "Pump");

                SelectBrokenCabinets(rows, index);

                // powered-off panel state at start: Button_off shown, Button hidden —
                // the resurrected HandlerGOState pair flips them on the fixed trigger
                if (index.TryGetValue("Water_station/Water/Interactive_Button", out var btn))
                    btn.gameObject.SetActive(false);
                if (index.TryGetValue("Water_station/Water/Interactive_Button_off", out var btnOff))
                    btnOff.gameObject.SetActive(true);

                _staged = true;
                Plugin.Log.LogInfo($"[Pump] PUMP STATION RESTORED — {_broken.Count} cabinet(s) broken, repair to power the panel");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Pump] staging failed: {e}");
                _staged = true;
            }
        }

        // the 1.0 selector, offline edition: authored group table said solo breaks 1
        // cabinet from a dedicated 2-cabinet pool — use the maxGroupSize=1 row
        private static void SelectBrokenCabinets(JArray rows, Dictionary<string, Transform> index)
        {
            var pool = new List<string>();
            int toBreak = 1;
            foreach (var row in rows)
            {
                if (row.Value<string>("cls") != "ElectricalCabinetSelector") continue;
                var cfgs = row["fields"]?["_groupConfigurations"] as JArray;
                if (cfgs == null) break;
                foreach (var cfg in cfgs)
                {
                    if (cfg.Value<int?>("MaxGroupSize") != 1) continue;
                    toBreak = cfg.Value<int?>("CabinetsToBreak") ?? 1;
                    foreach (var ac in (cfg["AvailableCabinets"] as JArray) ?? new JArray())
                    {
                        var p = ac["$ref"]?.Value<string>("path");
                        if (p != null) pool.Add(p);
                    }
                    break;
                }
                break;
            }

            // every with_logic cabinet's repair suffix comes from its plant-action row
            foreach (var row in rows)
            {
                if (row.Value<string>("cls") != "HandlerPlantAction") continue;
                var rp = row.Value<string>("path") ?? "";
                var trig = row["fields"]?.Value<string>("_triggerId") ?? "";
                if (!trig.StartsWith("start_safe_repair_electricity_terminal_")) continue;
                var root = rp.Substring(0, rp.IndexOf("/TriggersLogic", StringComparison.Ordinal));
                var suffix = trig.Substring("start_safe_repair_electricity_terminal_".Length);
                if (!_cabinets.ContainsKey(root) && index.TryGetValue(root, out var t))
                    _cabinets[root] = new CabinetInfo { Path = root, Suffix = suffix, Root = t };
            }

            // choose the broken set (from the authored pool when present) — pool paths
            // that don't resolve to a known cabinet are dropped, and if the whole pool
            // misses we fall back to any cabinet: zero broken would strand the puzzle
            var candidates = new List<string>();
            foreach (var p in pool) if (_cabinets.ContainsKey(p)) candidates.Add(p);
            if (candidates.Count == 0) candidates.AddRange(_cabinets.Keys);
            for (int i = 0; i < toBreak && candidates.Count > 0; i++)
            {
                var pick = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                candidates.Remove(pick);
                _broken.Add(pick);
            }

            // set scene states: broken -> with_logic rig on; healthy -> Closed shell on.
            // pairing is positional (the shells are siblings placed on the same boxes)
            var shells = new List<Transform>();
            foreach (var kv in index)
                if (kv.Key.Contains("/Electric_box/Electric_box_07_Closed") && kv.Key.Split('/').Length == 3)
                    shells.Add(kv.Value);
            foreach (var cab in _cabinets.Values)
            {
                bool broken = _broken.Contains(cab.Path);
                cab.Root.gameObject.SetActive(broken);
                Transform shell = null;
                float best = 4f; // meters — same-box pairing
                foreach (var s in shells)
                {
                    float d = (s.position - cab.Root.position).sqrMagnitude;
                    if (d < best) { best = d; shell = s; }
                }
                if (shell) shell.gameObject.SetActive(!broken);
                else Plugin.Log.LogDebug($"[Pump] no Closed shell within 2m of '{cab.Path}' — visual pair incomplete");
            }
        }

        internal static CabinetInfo CabinetForSwitch(Transform sw)
        {
            foreach (var cab in _cabinets.Values)
                if (sw.IsChildOf(cab.Root)) return cab;
            return null;
        }

        internal static bool IsBroken(CabinetInfo cab) => cab != null && _broken.Contains(cab.Path);

        internal static bool HasMultitool(Player player)
        {
            if (player?.Profile?.Inventory == null) return false;
            foreach (var it in player.Profile.Inventory.AllRealPlayerItems)
                if (it.TemplateId == MultitoolTpl) return true;
            return false;
        }

        internal static void OnRepaired(CabinetInfo cab)
        {
            _broken.Remove(cab.Path);
            // native rows on the cabinet listen for its repaired trigger (green light,
            // spark loop off, working loop on)
            TerminalGatesExplosion.Emit("repaired_electicity_terminal_" + cab.Suffix);
            Plugin.Log.LogInfo($"[Pump] cabinet repaired ({_broken.Count} left)");
            if (_broken.Count == 0 && !PowerFixed)
            {
                PowerFixed = true;
                // the AND gate's authored output: powers the water panel via the
                // resurrected HandlerGOState pair + generator start foley
                TerminalGatesExplosion.Emit(BoxesFixedTrigger);
                TerminalGatesExplosion.PlayAt(TerminalFxBundle.FindClip("amb_terminal_interactive_generator_start_electricity"),
                    cab.Root.position, 30f);
                NotificationManagerClass.DisplayMessageNotification(
                    "Power restored — the pump station panel is live",
                    ENotificationDurationType.Long, ENotificationIconType.Default, Color.green);
            }
        }

        internal static void OnUnsafeRepairFailed(CabinetInfo cab)
        {
            // the authored shock: native HandlerDamage on the cabinet listens for the
            // failed trigger (10dmg to both arms, once)
            TerminalGatesExplosion.Emit("repair_unsafe_failed_electicity_terminal_" + cab.Suffix);
            NotificationManagerClass.DisplayMessageNotification(
                "Repair failed — the cabinet shocked you",
                ENotificationDurationType.Default, ENotificationIconType.Alert, Color.red);
        }
    }

    // interaction layer for the pump puzzle switches, same pattern as the gate:
    // matched by GO name in a Terminal scene, hold sessions, triggers emitted into
    // the resurrected native graph
    [HarmonyPatch(typeof(GetActionsClass), "smethod_11")]
    internal static class Patch_PumpSwitchActions
    {
        private static void Replace(ref ActionsReturnClass result, ActionsTypesClass act)
        {
            if (result == null) result = new ActionsReturnClass { Actions = new List<ActionsTypesClass> { act } };
            else { result.Actions.Clear(); result.Actions.Add(act); }
        }

        private static bool Ours(EFT.Interactive.Switch sw)
        {
            try
            {
                var sc = sw.gameObject.scene.name;
                return sc != null && sc.StartsWith("Terminal", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void Postfix(ref ActionsReturnClass __result, GamePlayerOwner owner, EFT.Interactive.Switch interactiveSwitch)
        {
            try
            {
                if (!Plugin.PumpStation.Value || interactiveSwitch == null || !Ours(interactiveSwitch)) return;
                var goName = interactiveSwitch.gameObject.name;

                if (goName == "Electric_box_Switch")
                {
                    var cab = TerminalPumpStation.CabinetForSwitch(interactiveSwitch.transform);
                    if (cab == null) return;
                    if (!TerminalPumpStation.IsBroken(cab))
                    {
                        if (__result != null) __result.Actions.Clear();
                        __result = null;
                        return;
                    }
                    var player = Singleton<GameWorld>.Instance?.MainPlayer;
                    bool hasTool = player != null && TerminalPumpStation.HasMultitool(player);
                    var sw = interactiveSwitch;
                    Replace(ref __result, new ActionsTypesClass
                    {
                        Name = hasTool ? "Repair" : "Repair without tools (risky)",
                        Disabled = false,
                        Action = () =>
                        {
                            // Retail's HandlerPlaySoundAdvanced begins the 6.42s
                            // cabinet-repair clip on this trigger and stops it only
                            // when a hold is dropped. Previously we played the clip
                            // in OnRepaired, five seconds late.
                            string repairMode = hasTool ? "safe" : "unsafe";
                            TerminalGatesExplosion.Emit($"start_{repairMode}_repair_electricity_terminal_{cab.Suffix}");
                            var go = new GameObject("Terminal_CabinetRepair");
                            var s = go.AddComponent<GateHoldSession>();
                            s.Owner = owner;
                            s.Anchor = sw.transform;
                            s.Seconds = TerminalPumpStation.RepairSeconds;
                            s.PanelText = "Repairing {0:F1}";
                            s.OnCancel = () => TerminalGatesExplosion.Emit(
                                $"dropped_{repairMode}_repair_electricity_{cab.Suffix}");
                            s.OnSuccess = () =>
                            {
                                bool toolNow = false;
                                try
                                {
                                    var p = Singleton<GameWorld>.Instance?.MainPlayer;
                                    toolNow = p != null && TerminalPumpStation.HasMultitool(p);
                                }
                                catch { }
                                if (toolNow || UnityEngine.Random.value < 0.1f)
                                    TerminalPumpStation.OnRepaired(cab);
                                else
                                    TerminalPumpStation.OnUnsafeRepairFailed(cab);
                                try { owner?.ClearInteractionState(); } catch { }
                            };
                        },
                    });
                }
                else if (goName == "Interactive_Button")
                {
                    if (TerminalPumpStation.WaterDrained)
                    {
                        if (__result != null) __result.Actions.Clear();
                        __result = null;
                        return;
                    }
                    Replace(ref __result, new ActionsTypesClass
                    {
                        Name = "Drain the reservoir",
                        Disabled = false,
                        Action = () =>
                        {
                            TerminalPumpStation.WaterDrained = true;
                            // native takes it from here: animator IsRemoved, pump
                            // start/drain sounds, loop stop — all authored listeners
                            TerminalGatesExplosion.Emit("Water_remove_1178689405");
                            try { owner?.ClearInteractionState(); } catch { }
                        },
                    });
                }
                else if (goName == "Interactive_Button_off")
                {
                    // powered-down panel: retail shows 'needs electric power' on use
                    Replace(ref __result, new ActionsTypesClass
                    {
                        Name = "Inspect panel",
                        Disabled = false,
                        Action = () =>
                        {
                            NotificationManagerClass.DisplayMessageNotification(
                                "The panel has no power — repair the broken electrical cabinet first",
                                ENotificationDurationType.Default, ENotificationIconType.Default, Color.white);
                        },
                    });
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Pump] actions patch threw: {e.Message}"); }
        }
    }
}
