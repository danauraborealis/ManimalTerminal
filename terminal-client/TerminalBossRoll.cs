using System.Collections.Generic;
using UnityEngine;

namespace Manimal.Terminal
{
    // THE BOSS ROLL (user-preferred implementation, 2026-08-18 — replaces the old
    // parse-time PortBossPool): retail terminal spawns exactly ONE of five bosses
    // per raid at the container ship berths (Glukhar / Killa / Reshala / Sanitar /
    // Tagilla). the db ships all five rows on the same zone/trigger with the
    // live-dump-verified escorts; at location capture we pick one and REMOVE the
    // rest — array surgery, not chance edits, because the rows carry
    // ForceSpawn=true which can ignore BossChance.
    internal static class TerminalBossRoll
    {
        private static readonly string[] Candidates =
            { "bossGluhar", "bossKilla", "bossBully", "bossSanitar", "bossTagilla" };

        internal static void Roll(LocationSettingsClass.Location location)
        {
            try
            {
                var rows = location?.BossLocationSpawn;
                if (rows == null || rows.Length == 0) return;

                // Terminal authors every population source through this array — not
                // just named bosses, but assaults, civilians, RUAF and Black Division
                // event forces too. Emptying it is the earliest and cleanest botless
                // control; TerminalSpawnGate also blocks runtime calls as a safety net.
                if (Plugin.DisableAllBots != null && Plugin.DisableAllBots.Value)
                {
                    location.BossLocationSpawn = System.Array.Empty<BossLocationSpawn>();
                    Plugin.Log.LogWarning($"[BotControl] DIAGNOSTIC BOTLESS — removed all {rows.Length} authored boss/event/wave rows");
                    return;
                }

                var pool = new List<int>();
                for (int i = 0; i < rows.Length; i++)
                {
                    var n = rows[i]?.BossName;
                    foreach (var c in Candidates) if (n == c) { pool.Add(i); break; }
                }
                if (pool.Count == 0) return;

                // Diagnostic A/B control. Removing the candidate rows is important:
                // merely skipping Roll would leave all five ForceSpawn rows intact.
                // Non-Terminal bosses/waves remain untouched, as do the T4 triggers.
                if (Plugin.TerminalBosses != null && !Plugin.TerminalBosses.Value)
                {
                    var withoutBosses = new List<BossLocationSpawn>(rows.Length - pool.Count);
                    for (int i = 0; i < rows.Length; i++)
                        if (!pool.Contains(i)) withoutBosses.Add(rows[i]);
                    location.BossLocationSpawn = withoutBosses.ToArray();
                    Plugin.Log.LogWarning($"[BossRoll] DIAGNOSTIC CONTROL — removed all {pool.Count} Terminal boss candidates; regular event/T4 waves remain enabled");
                    return;
                }

                if (pool.Count == 1) return; // already reduced elsewhere
                int winner = pool[Random.Range(0, pool.Count)];

                var kept = new List<BossLocationSpawn>(rows.Length);
                for (int i = 0; i < rows.Length; i++)
                    if (i == winner || !pool.Contains(i)) kept.Add(rows[i]);
                location.BossLocationSpawn = kept.ToArray();
                Plugin.Log.LogInfo($"[BossRoll] {pool.Count} candidates -> '{rows[winner].BossName}' "
                    + $"(escort {rows[winner].BossEscortAmount}x {rows[winner].BossEscortType}) spawns this raid");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BossRoll] failed: {e.Message}"); }
        }
    }
}
