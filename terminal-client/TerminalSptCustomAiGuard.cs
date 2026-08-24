using EFT;
using HarmonyLib;

namespace Manimal.Terminal
{
    // SPT CustomAiPatch randomises assault/player-scav/PMC brains from a per-map
    // server dictionary.  Custom maps have no entry: after its 15-minute cache refresh
    // it indexes ["terminal"] and logs an exception for every subsequent activation.
    // Keep the profile's authored role on Terminal; stock maps use SPT unchanged.
    internal static class TerminalSptCustomAiGuard
    {
        internal static void TryPatch(Harmony harmony)
        {
            var type = AccessTools.TypeByName("SPT.Custom.CustomAI.AIBrainSpawnWeightAdjustment");
            if (type == null) return;
            int patched = 0;
            foreach (var name in new[]
            {
                "GetAssaultScavWildSpawnType",
                "GetRandomisedPlayerScavType",
                "GetPmcWildSpawnType",
            })
            {
                var method = AccessTools.DeclaredMethod(type, name);
                if (method == null) continue;
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(TerminalSptCustomAiGuard), nameof(ReturnAuthoredRole)));
                patched++;
            }
            if (patched > 0)
                Plugin.Log.LogInfo($"[CustomAIGuard] {patched} SPT brain-weight lookup(s) guarded for Terminal's missing map cache entry");
        }

        private static bool ReturnAuthoredRole(object[] __args, ref WildSpawnType __result)
        {
            if (!TerminalGate.On) return true;
            var bot = __args != null && __args.Length > 0 ? __args[0] as BotOwner : null;
            var settings = bot?.Profile?.Info?.Settings;
            if (settings == null) return true;
            __result = settings.Role;
            return false;
        }
    }
}
