using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // the icebreaker bot-hardening set, ported wholesale (user call 2026-08-10: copy
    // everything except the synth/crew-specific pieces). every patch here is a scar
    // from an icebreaker raid autopsy; terminal shares the same 4.0 assembly and the
    // same backported-map failure modes. NOT ported: IceCrewJobs (their bigbrain idle
    // layer — fed by icebreaker's crew system, only needed under SAIN; our restored
    // retail patrols drive vanilla PatrollingData) and the synthesized cover/zone
    // generation (terminal has the retail bake).
    internal static class TerminalBotFixes
    {
        // CachePoints pre-caches 30 bots' navigation points — an exception here (a
        // GroupPoint member the fill left null, a manual-point surprise) would abort
        // BotsController.Init; degrade to lazy per-bot cache misses instead
        [HarmonyPatch(typeof(AICoversData), "CachePoints")]
        internal static class Patch_CoversCache
        {
            private static Exception Finalizer(Exception __exception)
            {
                if (__exception == null) return null;
                if (!TerminalGate.On) return __exception;
                Plugin.Log.LogWarning($"[BotFix] swallowed AICoversData.CachePoints: {__exception.Message}");
                return null;
            }
        }

        // bot door graph refresh over our rebuilt NavMeshDoorLinks — a single bad link
        // must not cost the raid
        [HarmonyPatch(typeof(BotDoorsController), "RefreshData")]
        internal static class Patch_BotDoorsRefresh
        {
            private static Exception Finalizer(Exception __exception)
            {
                if (__exception == null) return null;
                if (!TerminalGate.On) return __exception;
                Plugin.Log.LogWarning($"[BotFix] swallowed BotDoorsController.RefreshData: {__exception.Message}");
                return null;
            }
        }

        // bot hearing: dangling despawned-bot refs in the sound graph NRE inside
        // PlaySound and abort the CALLER's flow (player shots, door slams) — swallow,
        // rate-limited log
        [HarmonyPatch(typeof(GlobalEventDispatcher), nameof(GlobalEventDispatcher.PlaySound))]
        internal static class Patch_PlaySoundAirbag
        {
            private static float _lastLog;

            private static Exception Finalizer(Exception __exception)
            {
                if (__exception == null) return null;
                if (!TerminalGate.On) return __exception;
                if (Time.unscaledTime - _lastLog > 30f)
                {
                    _lastLog = Time.unscaledTime;
                    Plugin.Log.LogWarning($"[BotFix] swallowed a bot-hearing exception (dangling despawned-bot ref) — player actions protected. inner: {__exception.Message}");
                }
                return null;
            }
        }

        // opening a door emits state-change triggers via a quest/event singleton that
        // is a dead shell on a backported map — every door interaction NREs AFTER the
        // swing. our map has no quest triggers to fire; vanilla maps keep the exception
        // (masking theirs would silently break quests)
        [HarmonyPatch(typeof(WorldInteractiveObject), "PushTriggers")]
        internal static class Patch_DoorTriggerEmit
        {
            private static Exception Finalizer(Exception __exception)
                => __exception == null || TerminalGate.On ? null : __exception;
        }

        // THE activation fix (icebreaker's hardest-won scar): BSG's method_10 aborts
        // the ENTIRE bot activation on the first throw (internal catch -> silent
        // ActiveFail statue with no log). run the mirrored step list ourselves: each
        // step individually guarded, failures logged BY NAME, activation continues —
        // one broken subsystem no longer statues the bot. mirrors the 4.0 method_10
        // exactly (order matters: BotState=Active lands mid-list where BSG set it).
        [HarmonyPatch(typeof(BotOwner), "method_10")]
        internal static class Patch_BotActivationStepwise
        {
            private static readonly MethodInfo _m2 = AccessTools.Method(typeof(BotOwner), "AfterActivationSubscribe");
            private static readonly MethodInfo _m11 = AccessTools.Method(typeof(BotOwner), "method_11");

            private static bool Prefix(BotOwner __instance)
            {
                if (!TerminalGate.On) return true; // vanilla maps: BSG's original behavior
                var b = __instance;
                int failed = 0;
                void Step(string name, Action a)
                {
                    try { a(); }
                    catch (Exception e)
                    {
                        failed++;
                        Plugin.Log.LogError($"[BotFix] '{b.name}' activation step {name} FAILED: {e}");
                    }
                }

                Step("VoxelesPersonalData", () => b.VoxelesPersonalData.Activate(b.BotsGroup.BotGame.BotsController.CoversData));
                Step("LookSensor", () => b.LookSensor.Activate());
                Step("Settings", () => b.Settings.Activate());
                Step("ExternalItemsController", () => b.ExternalItemsController.Activate());
                Step("ItemTaker", () => b.ItemTaker.Activate());
                Step("BewarePlantedMine", () => b.BewarePlantedMine.Activate());
                Step("EnemyChooser", () => b.EnemyChooser.Activate());
                Step("PlanDropItem", () => b.PlanDropItem.Activate());
                Step("MinesData", () => b.MinesData.Activate());
                Step("ItemDropper", () => b.ItemDropper.Activate());
                Step("SuppressStationary", () => b.SuppressStationary.Activate());
                Step("NavMeshCutterController", () => b.NavMeshCutterController.Activate());
                Step("BotFollower", () => b.BotFollower.Activate());
                Step("FriendlyTilt", () => b.FriendlyTilt.Activate());
                Step("RandomPlanItemDropper", () => b.RandomPlanItemDropper.Activate());
                Step("Tactic", () => b.Tactic.Activate());
                Step("EnemiesController", () => b.EnemiesController.Activate(b.BotsGroup.BotGame.BotsController.OnlineDependenceSettings.CanPersueAxeman));
                Step("HearingSensor", () => b.HearingSensor.Init());
                Step("LeaveData", () => b.LeaveData.Activate(b.BotsGroup.BotZone.Modifier.LeaveDist));
                Step("Receiver", () => b.Receiver.Init());
                Step("Mover", () => b.Mover.Activate());
                Step("BotTalk", () => b.BotTalk.Activate());
                Step("LoyaltyData", () => b.LoyaltyData.Activate());
                Step("AssaultDangerArea", () => b.AssaultDangerArea.Activate());
                Step("DangerArea", () => b.DangerArea.Activate());
                Step("BotPersonalStats", () => b.BotPersonalStats.Init(b, b.BotsGroup.BotZone.name));
                Step("StandBy.InitPoints", () => b.StandBy.InitPoints(b.BotsGroup.BotZone.Modifier.DistToActivate, b.BotsGroup.BotZone.Modifier.DistToSleep));
                Step("method_2", () => _m2.Invoke(b, null));
                Step("FlashGrenade", () => b.FlashGrenade.Activate());
                Step("PeaceHardAim", () => b.PeaceHardAim.Activate());
                Step("ShootData", () => b.ShootData.Activate());
                Step("PeaceLook", () => b.PeaceLook.Activate());
                Step("NearDoorData", () => b.NearDoorData.Activate());
                Step("AIData", () => b.AIData.Activate());
                Step("UnityEditorRunChecker", () => b.UnityEditorRunChecker.Activate());
                Step("NightVision", () => b.NightVision.Activate());
                Step("SearchData", () => b.SearchData.Activate());
                Step("Medecine", () => b.Medecine.Activate());
                b.BotState = EBotState.Active;
                Step("Memory", () => b.Memory.Activate());
                Step("SuppressShoot", () => b.SuppressShoot.Activate());
                Step("EatDrinkData", () => b.EatDrinkData.Activate());
                Step("SecondWeaponData", () => b.SecondWeaponData.Activate());
                Step("BotLay", () => b.BotLay.Activate());
                Step("SuppressGrenade", () => b.SuppressGrenade.Activate());
                Step("method_11", () => _m11.Invoke(b, null));
                Step("Brain", () => b.Brain.Activate());
                Step("PatrollingData", () => b.PatrollingData.Activate());

                if (failed > 0)
                    Plugin.Log.LogWarning($"[BotFix] '{b.name}' activated with {failed} failed step(s) — degraded but alive");
                return false; // we ran the whole list
            }
        }
        // PATROL SUB-POINTS, ported from icebreaker 08-13 (34,090 -> 45 NREs there).
        // a follower joining a boss lands in PatrolPointChooserBasic.FindPointForFollower,
        // which calls SetTarget(container, follower.BotFollower.Index) — a formation slot,
        // so ALWAYS >= 0. BotPointControl.method_0 therefore takes its `index >= 0` branch and
        // does `new PatrolPointContainer(p.TargetPoint.GetSubPoint(index))`. GetSubPoint is
        // `subPoints[Mathf.Clamp(index, 0, Count-1)]`: the INDEX is safe, the CONTENTS are
        // not. a dead entry comes back, gets wrapped, and PatrollingData.PointSetted reads
        // .Position off a null TargetPoint — every frame, from PatrolMoveRoundBoss.Update.
        //
        // terminal is MORE exposed than icebreaker was: its patrol points are rebuilt from
        // the retail dump by TerminalAIBake (FillFields restoring subPoints refs), and any
        // ref that fails to resolve leaves a hole in the list. nothing here ever called
        // CreateSubPoints either, so a point restored with an empty list stayed empty.
        //
        // ORDER MATTERS (measured on icebreaker): scrub BEFORE building. a point whose
        // entries are ALL dead still reports SubPointsCount > 0, so a build-then-scrub pass
        // skips regeneration and then empties the list — that run stripped 546 dead entries
        // and left 63 points with no formation offsets at all.
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_PatrolSubPoints
        {
            private static readonly FieldInfo SubPointsField = AccessTools.Field(typeof(PatrolPoint), "subPoints");

            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On) return;
                int ways = 0, built = 0, failed = 0, scrubbed = 0, emptied = 0;
                try
                {
                    foreach (var zone in UnityEngine.Object.FindObjectsOfType<BotZone>(true))
                    {
                        if (zone.PatrolWays == null) continue;
                        foreach (var way in zone.PatrolWays)
                        {
                            if (way == null || way.Points == null) continue;
                            ways++;
                            foreach (var p in way.Points)
                            {
                                if (p == null) continue;
                                scrubbed += Scrub(p);
                                if (p.SubPointsCount == 0)
                                {
                                    try { p.CreateSubPoints(way); built++; }
                                    catch { failed++; }
                                    scrubbed += Scrub(p);
                                }
                                if (p.SubPointsCount == 0) emptied++;
                            }
                        }
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[SubPoints] sweep failed: {e.Message}"); }
                Plugin.Log.LogWarning($"[SubPoints] built formation sub-points on {built} patrol point(s) ({ways} ways, {failed} failed)"
                    + $"; scrubbed {scrubbed} dead sub-point(s)"
                    + (emptied > 0 ? $", {emptied} point(s) left with none (GetSubPoint returns the point itself)" : ""));
            }

            // `x == null` is Unity's overload deliberately: it catches a destroyed-but-
            // still-referenced component as well as a real null, and CreateSubPoints
            // DestroyImmediates the previous generation before rebuilding.
            private static int Scrub(PatrolPoint p)
            {
                try
                {
                    if (SubPointsField == null) return 0;
                    if (!(SubPointsField.GetValue(p) is List<PatrolPoint> list) || list.Count == 0) return 0;
                    int before = list.Count;
                    list.RemoveAll(x => x == null);
                    return before - list.Count;
                }
                catch { return 0; }
            }
        }

        // backstop for a point the generator genuinely cannot fix (no navmesh around it):
        // an empty sub-point list returns the point ITSELF rather than indexing [-1], so
        // the follower stands on the point and the formation degrades instead of throwing
        [HarmonyPatch(typeof(PatrolPoint), nameof(PatrolPoint.GetSubPoint))]
        internal static class Patch_GetSubPointEmptyGuard
        {
            [HarmonyPrefix]
            private static bool Prefix(PatrolPoint __instance, ref PatrolPoint __result)
            {
                if (__instance.SubPointsCount > 0) return true;
                __result = __instance;
                return false;
            }
        }

    }
}
