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
        private static bool _raidStarted;
        private static int _raidPlayerCount;
        private static int _raidSeed;
        private static bool _hasCoopLayout;
        internal static bool Ready => _staged;
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
            _raidStarted = false;
            _raidPlayerCount = 0;
            _raidSeed = 0;
            _hasCoopLayout = false;
            PowerFixed = false;
            WaterDrained = false;
            _broken.Clear();
            _cabinets.Clear();
        }

        internal static void TryStage()
        {
            if (_staged || !_raidStarted || !Plugin.PumpStation.Value) return;
            if (!TerminalGate.On) return;
            if (TerminalCoop.Active && !_hasCoopLayout) return;
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

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_SelectAtRaidStart
        {
            private static void Postfix(GameWorld __instance)
            {
                if (!TerminalGate.On || _raidStarted || __instance == null) return;
                // Snapshot the humans once the raid roster exists, not during scene
                // loading. Bot spawns/deaths and later repair ticks must not reroll it.
                _raidStarted = true;
                if (TerminalCoop.Active) return; // host sends roster count AND seed
                try
                {
                    var humans = new HashSet<string>(StringComparer.Ordinal);
                    void AddHuman(IPlayer player)
                    {
                        if (player == null) return;
                        string key = TerminalPumpSelection.HumanKey(player.IsAI, player.Profile?.Info?.Nickname,
                            player.ProfileId, player.Id);
                        if (key != null) humans.Add(key);
                    }
                    if (__instance.RegisteredPlayers != null)
                        foreach (var player in __instance.RegisteredPlayers) AddHuman(player);
                    AddHuman(__instance.MainPlayer); // same profile is counted only once
                    _raidPlayerCount = Math.Max(1, humans.Count);
                    _raidSeed = CreateRaidSeed();
                    TryStage();
                }
                catch (Exception e)
                {
                    _staged = true;
                    Plugin.Log.LogError($"[Pump] raid selection initialization failed: {e}");
                }
            }
        }

        internal static void ApplyCoopLayout(int humans, int seed)
        {
            if (_hasCoopLayout) return;
            _hasCoopLayout = true;
            _raidPlayerCount = Math.Max(1, humans);
            _raidSeed = seed;
            TryStage();
        }

        internal static bool ApplyCoopRepair(string path)
        {
            if (!_staged) return false;
            if (_cabinets.TryGetValue(path, out var cab) && IsBroken(cab)) OnRepaired(cab);
            return true;
        }

        internal static CabinetInfo FindBroken(string path)
            => _cabinets.TryGetValue(path, out var cab) && IsBroken(cab) ? cab : null;

        internal static void ApplyCoopShock(string path)
        {
            if (_cabinets.TryGetValue(path, out var cab)) OnUnsafeRepairFailed(cab);
        }

        internal static void Drain()
        {
            if (!PowerFixed || WaterDrained) return;
            if (TerminalCoop.Active && !TerminalCoop.Applying)
            {
                TerminalCoop.Request(TerminalEvent.PumpDrain);
                return;
            }
            WaterDrained = true;
            TerminalGatesExplosion.Emit(WaterRemoveTrigger);
        }

        private static int CreateRaidSeed()
        {
            // Unity's global RNG is shared with scene scripts and other mods. A
            // dedicated per-raid RNG cannot be reset by somebody else's InitState.
            var game = Singleton<AbstractGame>.Instantiated ? Singleton<AbstractGame>.Instance : null;
            if (game != null && game.GetType().Name.IndexOf("Coop", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var backend = AccessTools.TypeByName("FikaBackendUtils");
                string code = backend == null ? null : AccessTools.Property(backend, "RaidCode")?.GetValue(null)?.ToString();
                string server = backend == null ? null : AccessTools.Property(backend, "ServerGuid")?.GetValue(null)?.ToString();
                if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(server))
                    return TerminalPumpSelection.RaidSeed(code + "|" + server);
                Plugin.Log.LogWarning("[Pump] co-op match seed unavailable; cabinet choices cannot be synchronized across peers");
            }
            return TerminalPumpSelection.RaidSeed(Guid.NewGuid().ToString("N"));
        }

        private static void SelectBrokenCabinets(JArray rows, Dictionary<string, Transform> index)
        {
            var configuration = TerminalPumpSelection.Configuration(rows, _raidPlayerCount);
            var pool = TerminalPumpSelection.Pool(configuration);
            int toBreak = configuration.Value<int>("CabinetsToBreak");

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

            // Never silently turn the authored two-cabinet solo pool into one, or
            // fall back to unrelated cabinets elsewhere on the map.
            foreach (var p in pool)
                if (!_cabinets.ContainsKey(p) || !index.ContainsKey(p + "/Electric_box_Switch"))
                    throw new InvalidOperationException($"[Pump] authored pool incomplete ({pool.Count} expected): '{p}' or its repair switch is missing");
            foreach (var pick in TerminalPumpSelection.Choose(pool, toBreak, new System.Random(_raidSeed)))
                _broken.Add(pick);

            // Use the actual selector targets: solo (7) pairs with Closed (17),
            // solo (8) with Closed (22). Proximity guessing misses their authored
            // green-light states and can leave the selected broken panel covered.
            foreach (var cab in _cabinets.Values)
            {
                bool broken = _broken.Contains(cab.Path);
                var states = TerminalPumpSelection.InitialStates(rows, cab.Path, broken);
                cab.Root.gameObject.SetActive(false);
                foreach (var state in states)
                {
                    if (state.Key == cab.Path) continue;
                    if (index.TryGetValue(state.Key, out var target)) target.gameObject.SetActive(state.Value);
                    else Plugin.Log.LogWarning($"[Pump] initial-state target missing: '{state.Key}'");
                }
                cab.Root.gameObject.SetActive(broken);
            }
            Plugin.Log.LogInfo($"[Pump] selection: humans={_raidPlayerCount}, bracket={configuration.Value<int>("MaxGroupSize")},"
                + $" pool={pool.Count}/{pool.Count}, broken={_broken.Count}, seed={_raidSeed}; candidates=[{string.Join(", ", pool)}]; selected=[{string.Join(", ", _broken)}]");
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
            if (!IsBroken(cab)) return;
            if (TerminalCoop.Active && !TerminalCoop.Applying)
            {
                TerminalCoop.Request(TerminalEvent.PumpRepair, cab.Path);
                return;
            }
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
                EFT.Communications.NotificationManager.DisplayMessageNotification(
                    "Power restored — the pump station panel is live",
                    ENotificationDurationType.Long, ENotificationIconType.Default, Color.green);
            }
        }

        internal static void OnUnsafeRepairFailed(CabinetInfo cab)
        {
            // the authored shock: native HandlerDamage on the cabinet listens for the
            // failed trigger (10dmg to both arms, once)
            TerminalGatesExplosion.Emit("repair_unsafe_failed_electicity_terminal_" + cab.Suffix);
            EFT.Communications.NotificationManager.DisplayMessageNotification(
                "Repair failed — the cabinet shocked you",
                ENotificationDurationType.Default, ENotificationIconType.Alert, Color.red);
        }
    }

    // interaction layer for the pump puzzle switches, same pattern as the gate:
    // matched by GO name in a Terminal scene, hold sessions, triggers emitted into
    // the resurrected native graph
    [HarmonyPatch(typeof(EFT.InteractionContextHelper), nameof(EFT.InteractionContextHelper.GetAvailableActions),
        typeof(GamePlayerOwner), typeof(EFT.Interactive.Switch))]
    internal static class Patch_PumpSwitchActions
    {
        private static void Replace(ref EFT.UI.AvailableInteractionState result, EFT.UI.InteractionAction act)
        {
            if (result == null) result = new EFT.UI.AvailableInteractionState { Actions = new List<EFT.UI.InteractionAction> { act } };
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

        private static void Postfix(ref EFT.UI.AvailableInteractionState __result, GamePlayerOwner owner, EFT.Interactive.Switch interactiveSwitch)
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
                    Replace(ref __result, new EFT.UI.InteractionAction
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
                                if (TerminalCoop.Active)
                                    TerminalCoop.Request(TerminalEvent.PumpRepair, cab.Path);
                                else if (toolNow || UnityEngine.Random.value < 0.1f)
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
                    Replace(ref __result, new EFT.UI.InteractionAction
                    {
                        Name = "Drain the reservoir",
                        Disabled = false,
                        Action = () =>
                        {
                            TerminalPumpStation.Drain();
                            // native takes it from here: animator IsRemoved, pump
                            // start/drain sounds, loop stop — all authored listeners
                            try { owner?.ClearInteractionState(); } catch { }
                        },
                    });
                }
                else if (goName == "Interactive_Button_off")
                {
                    // powered-down panel: retail shows 'needs electric power' on use
                    Replace(ref __result, new EFT.UI.InteractionAction
                    {
                        Name = "Inspect panel",
                        Disabled = false,
                        Action = () =>
                        {
                            EFT.Communications.NotificationManager.DisplayMessageNotification(
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
