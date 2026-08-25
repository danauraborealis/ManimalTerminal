using System;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // AI Limit's population model conflicts with Terminal's authored stage director:
    // it disables distant bot GameObjects (and marks their BotOwners NonActive), while
    // Terminal deliberately keeps a small, exact living population and moves survivors
    // forward as later stages request them.  Patch the optional mod by reflection so we
    // do not acquire a hard DLL dependency, and suppress it only while TerminalGate is
    // on. Every other location continues through AI Limit's original code unchanged.
    internal static class TerminalAILimitFirewall
    {
        private const string ComponentTypeName = "AILimit.AILimitComponent";
        private static readonly object Sync = new object();
        private static Harmony _harmony;
        private static Type _componentType;
        private static bool _installed;

        internal static void Install(Harmony harmony)
        {
            _harmony = harmony;
            TryInstall();
        }

        internal static void TryInstall()
        {
            if (_installed || _harmony == null) return;
            lock (Sync)
            {
                if (_installed) return;
                var type = AccessTools.TypeByName(ComponentTypeName);
                if (type == null) return; // optional mod is absent or has not loaded yet

                try
                {
                    var enable = AccessTools.Method(type, "Enable");
                    var update = AccessTools.Method(type, "Update");
                    if (enable == null || update == null)
                    {
                        Plugin.Log.LogWarning("[Compat] AI Limit detected, but its Enable/Update layout is unknown; Terminal firewall could not arm");
                        return;
                    }

                    var prefix = new HarmonyMethod(AccessTools.Method(
                        typeof(TerminalAILimitFirewall), nameof(AllowAILimitOutsideTerminal)));
                    _harmony.Patch(enable, prefix: prefix);
                    _harmony.Patch(update, prefix: prefix);
                    _componentType = type;
                    _installed = true;
                    Plugin.Log.LogWarning("[Compat] AI Limit detected — Terminal-only firewall armed; AI Limit remains unchanged on every other map");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[Compat] AI Limit firewall patch failed: {e}");
                }
            }
        }

        // Harmony prefix for both the static Enable and instance Update methods.
        private static bool AllowAILimitOutsideTerminal() => !TerminalGate.On;

        internal static void EnforceAtRaidStart()
        {
            TryInstall();
            if (!TerminalGate.On || !_installed) return;

            int reactivated = 0;
            try
            {
                var world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
                if (!world) return;

                // Normally Enable was blocked, so no component exists. If plugin load
                // order allowed it to be added first, disable it before its first Update.
                var component = world.GetComponent(_componentType) as Behaviour;
                if (component != null) component.enabled = false;

                var spawner = Singleton<IBotGame>.Instantiated
                    ? Singleton<IBotGame>.Instance.BotsController?.BotSpawner : null;
                var bots = spawner?.Bots?.BotOwners;
                if (bots != null)
                {
                    foreach (var bot in bots)
                    {
                        try
                        {
                            if (!bot || bot.IsDead) continue;
                            var player = bot.GetPlayer;
                            if (!player || player.HealthController == null || !player.HealthController.IsAlive) continue;
                            bool wasLimited = !player.gameObject.activeSelf || bot.BotState != EBotState.Active;
                            var standBy = bot.StandBy;
                            if (standBy != null)
                            {
                                standBy.Activate();
                                standBy.NextCheckTime = Time.time + 10f;
                            }
                            if (!player.gameObject.activeSelf) player.gameObject.SetActive(true);
                            bot.BotState = EBotState.Active;
                            if (wasLimited) reactivated++;
                        }
                        catch (Exception e) { Plugin.Log.LogDebug($"[Compat] could not wake one AI Limit bot: {e.Message}"); }
                    }
                }
                Plugin.Log.LogWarning($"[Compat] AI Limit suppressed for Terminal raid; restored {reactivated} previously limited bot(s)");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Compat] AI Limit Terminal enforcement failed: {e.Message}"); }
        }
    }
}
