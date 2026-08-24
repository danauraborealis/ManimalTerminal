using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // Retail AIPlaceTerminalWavesController, coordinated with the population ledger.
    // The decoded component authors three stages at RemainPercent=.10 / MinimumCount=1:
    //   stage 1: 14 Zone1 places
    //   stage 2: 18 Zone2 places
    //   stage 3: BDPort32 / VSRFExit33 / CivShip34
    // Only stage 1 has an authored unblock (T0) and migration table. Stages 2/3 are
    // still tracked so recycler eligibility and diagnostics share retail's boundaries;
    // no invented trigger is raised when either later stage completes.
    internal static class TerminalStageDirector
    {
        private static readonly Dictionary<string, string[]> Migrate = new Dictionary<string, string[]>
        {
            ["Zone1ScavMiddleAmbush5"] = new[] { "Zone2ScavContainers17" },
            ["Zone1ScavHangarStorm14"] = new[] { "Zone2ScavContainers17", "Zone2ScavsWarehouse16" },
            ["Zone1ScavEnterStorm4"] = new[] { "Zone2ScavContainers17" },
            ["Zone1ScavEnterStorm7"] = new[] { "Zone2ScavContainers17", "Zone2ScavsWarehouse16" },
            ["Zone1ScavStoreStorm9"] = new[] { "Zone2ScavContainers17", "Zone2ScavsWarehouse16" },
            ["Zone1ScavPortStorm6"] = new[] { "Zone2ScavContainers17", "Zone2ScavsWarehouse16" },
            ["Zone1ScavMiddleAmbush8"] = new[] { "Zone2ScavContainers17", "Zone2ScavsWarehouse16" },
            ["Zone1BDGateAmbush13"] = new[] { "Zone2BDWarehouse18", "Zone2BDWarehouse19" },
            ["Zone1BD1HangarBD11"] = new[] { "Zone2BDWarehouse18", "Zone2BDWarehouse19" },
            ["Zone1BD1PortAmbush1"] = new[] { "Zone2BDWarehouse18", "Zone2BDWarehouse19" },
            ["Zone1BD1HangarAmbush11"] = new[] { "Zone2BDWarehouse18", "Zone2BDWarehouse19" },
            ["Zone1VSRF1Spawn3"] = new[] { "Zone2VSRFPassMiddle15" },
            ["Zone1VSRFStoreAmbush10"] = new[] { "Zone2VSRFPassMiddle15" },
            ["Zone1VSRFSnipeRoofPort2"] = new[] { "Zone2VSRFPassMiddle15" },
        };

        private const float RemainPercent = 0.10f;
        private const int MinimumCount = 1;
        // Retail knows the complete AIPlace roster. SPT exposes bots only once their
        // profile is placed, so this is the compatibility guard that prevents a pair
        // of opening spawns from clearing an entire stage before its waves run.
        private const int MinSeenToComplete = 8;

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_Arm
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On || !Plugin.StageDirector.Value) return;
                new GameObject("Terminal_StageDirector").AddComponent<Host>();
            }
        }

        internal sealed class Host : MonoBehaviour
        {
            private float _next;
            private bool _stage1Done;
            private bool _stage2Done;
            private bool _stage3Done;

            private void Update()
            {
                if (Time.time < _next) return;
                _next = Time.time + 3f;
                try
                {
                    if (!_stage1Done) TryCompleteStage1();
                    if (!_stage2Done && TerminalPopulationDirector.HasReachedTier(5)) TryCompletePassiveStage(2, ref _stage2Done);
                    if (!_stage3Done && TerminalPopulationDirector.HasReachedTier(5)) TryCompletePassiveStage(3, ref _stage3Done);
                    if (_stage1Done && _stage2Done && _stage3Done) Destroy(gameObject);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[StageDirector] tick failed: {e.Message}"); }
            }

            private void TryCompleteStage1()
            {
                int seen = TerminalPopulationDirector.SeenInStage(1);
                if (seen < MinSeenToComplete) return;
                var alive = TerminalPopulationDirector.LivingInLogicalStage(1);
                int threshold = Mathf.Max(MinimumCount, Mathf.CeilToInt(seen * RemainPercent));
                if (alive.Count > threshold) return;

                _stage1Done = true;
                bool t0AlreadyRaised = TerminalPopulationDirector.HasReachedTier(0);
                Plugin.Log.LogWarning($"[StageDirector] STAGE 1 CLEARED — {alive.Count} of {seen} seen bot(s) remain "
                    + $"(threshold {threshold}); " + (t0AlreadyRaised
                        ? "T0 was already raised by the walk-in route"
                        : "raising retail's T0 unblock")
                    + "; survivors fall back to authored Zone2 posts");
                if (!t0AlreadyRaised) RaiseT0();
                MigrateSurvivors(alive);
            }

            private static void TryCompletePassiveStage(int stage, ref bool done)
            {
                int seen = TerminalPopulationDirector.SeenInStage(stage);
                if (seen < MinSeenToComplete) return;
                int alive = TerminalPopulationDirector.LivingInLogicalStage(stage).Count;
                int threshold = Mathf.Max(MinimumCount, Mathf.CeilToInt(seen * RemainPercent));
                if (alive > threshold) return;
                done = true;
                Plugin.Log.LogWarning($"[StageDirector] RETAIL STAGE {stage} CLEARED — {alive} of {seen} seen bot(s) remain "
                    + $"(threshold {threshold}); no authored unblock/migration action for this stage");
            }

            private static void RaiseT0()
            {
                try
                {
                    // Record progression first: AnyEvent can synchronously activate T0
                    // waves, and their recycler must know the tier is now legal.
                    TerminalCrewJobs.NoteEvent("T0 (stage-1 attrition)");
                    Singleton<BotEventHandler>.Instance?.AnyEvent("T0");
                    TerminalPopulationDirector.EnsureProgressWavesActivated("T0");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[StageDirector] T0 raise failed: {e.Message}"); }
            }

            private static string ZoneNameOf(BotOwner bot)
            {
                try
                {
                    if (!bot) return "";
                    var zone = bot.BotsGroup?.BotZone;
                    return zone ? zone.name : "";
                }
                catch { return ""; }
            }

            private static void MigrateSurvivors(List<BotOwner> alive)
            {
                var zonePos = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
                foreach (var zone in FindObjectsOfType<BotZone>())
                {
                    if (!zone || zonePos.ContainsKey(zone.name)) continue;
                    var marker = zone.SpawnPointMarkers != null && zone.SpawnPointMarkers.Count > 0 ? zone.SpawnPointMarkers[0] : null;
                    zonePos[zone.name] = marker ? marker.Position : zone.transform.position;
                }

                int moved = 0;
                foreach (var bot in alive)
                {
                    try
                    {
                        var source = ZoneNameOf(bot);
                        if (!Migrate.TryGetValue(source, out var destinations) || destinations.Length == 0) continue;
                        var destination = destinations[UnityEngine.Random.Range(0, destinations.Length)];
                        if (!zonePos.TryGetValue(destination, out var position)) continue;
                        TerminalCrewJobs.ByProfile[bot.ProfileId] = new TerminalCrewJobs.Rec
                        {
                            Job = TerminalCrewJobs.Job.Guard,
                            Zone = new Bounds(position, new Vector3(30f, 10f, 30f)),
                            RushTo = position,
                            RushUntil = Time.time + 180f,
                        };
                        TerminalPopulationDirector.MarkLogicalZone(bot, destination);
                        moved++;
                        Plugin.Log.LogDebug($"[StageDirector] {bot.name}: migrating {source} -> {destination}");
                    }
                    catch { }
                }
                Plugin.Log.LogInfo($"[StageDirector] {moved} survivor(s) migrating to Zone2 fallback posts");
            }
        }
    }
}
