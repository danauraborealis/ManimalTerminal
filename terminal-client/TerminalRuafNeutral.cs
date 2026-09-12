using System;
using EFT;
using HarmonyLib;

namespace Manimal.Terminal
{
    // RUAF NEUTRALITY, ENFORCED AT THE CHOKE — the server-side hostility matrix
    // (AdditionalHostilitySettings, RUAF Neutral to players) doesnt survive contact:
    // RUAFComeHome's own client manager wires hostility after it on every map, and the
    // engine's vision check (CheckAndAddEnemy) adds seen players regardless (armory
    // firefight, 2026-08-10 — brain-layer filter verified working in the same log, the
    // shooting came from the enemy list). every hostility path in the engine funnels
    // through BotsGroup.AddEnemy WITH A CAUSE, so the gate is surgical: ruaf groups
    // never add a HUMAN enemy unless the cause is retaliation — shot at (followGetHit),
    // a member killed (byKill/death). bots stay fully hostile to scavs/blackdiv, and
    // shooting first still starts the war. terminal-only.
    internal static class TerminalRuafNeutral
    {
        [HarmonyPatch(typeof(BotsGroup), nameof(BotsGroup.AddEnemy))]
        internal static class Patch_RuafNeutralToHumans
        {
            [HarmonyPrefix]
            private static bool Prefix(BotsGroup __instance, IPlayer person, EBotEnemyCause cause, ref bool __result)
            {
                try
                {
                    if (!TerminalGate.On || !Plugin.RuafNeutral.Value) return true;
                    if (person == null || person.IsAI) return true; // bot-vs-bot untouched
                    var role = __instance?._initialBot?.Profile?.Info?.Settings?.Role.ToString() ?? "";
                    if (role.IndexOf("ruaf", StringComparison.OrdinalIgnoreCase) < 0
                        && role.IndexOf("vsrf", StringComparison.OrdinalIgnoreCase) < 0) return true;
                    if (cause == EBotEnemyCause.followGetHit || cause == EBotEnemyCause.byKill || cause == EBotEnemyCause.death)
                        return true; // player drew blood — war is on
                    Plugin.Log.LogDebug($"[RuafNeutral] blocked enemy-add: role='{role}' cause={cause} target=human");
                    __result = false;
                    return false;
                }
                catch { return true; }
            }
        }
    }
}
