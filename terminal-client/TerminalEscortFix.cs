using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Manimal.Terminal
{
    // BOSS ESCORTS ARRIVE WITH THEIR BOSS (2026-08-11: the TB8 hangar squad delivered
    // its boss instantly and its 3 escorts minutes later, or never).
    //
    // from the decompile: a wave's own boss honours BossLocationSpawn.IgnoreMaxBots
    // (BotBossSpawn.Spawn skips CheckOnMax entirely when it's set), but the
    // FOLLOWERS are spawned separately by method_6 through
    // TryToSpawnInZoneAndDelay(..., forceSpawn) — and for every non-start wave that
    // flag arrives FALSE, so escorts fall back into the capped/delayed queue while
    // the boss walks free. on a busy port that queue drains minutes later.
    //
    // the fix keeps retail's intent instead of inventing one: if the wave itself says
    // IgnoreMaxBots, its escorts ignore the cap too. terminal-only, and waves that
    // DON'T set the flag keep queueing exactly as before.
    internal static class TerminalEscortFix
    {
        [HarmonyPatch]
        internal static class Patch_EscortsHonourIgnoreMaxBots
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                // method_5 (plain escort count) and method_6 (the Supports list) both
                // carry the (wave, forceSpawn) pair
                foreach (var name in new[] { "SpawnBossSupports", "SpawnEscort" })
                {
                    var m = AccessTools.Method(typeof(BotBossSpawn), name);
                    if (m != null) yield return m;
                }
            }

            [HarmonyPrefix]
            private static void Prefix(BossLocationSpawn wave, ref bool forceSpawn)
            {
                try
                {
                    if (!TerminalGate.On || forceSpawn || wave == null) return;
                    if (!wave.IgnoreMaxBots) return;
                    forceSpawn = true;
                    Plugin.Log.LogDebug($"[EscortFix] '{wave.BossName}' escorts into '{wave.BossZone}' forced "
                        + $"(wave IgnoreMaxBots) — {wave.EscortCount} slot(s) reserved");
                }
                catch { }
            }
        }
    }
}
