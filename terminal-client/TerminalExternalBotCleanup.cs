using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // MoreBotsAPI and RUAFComeHome live on persistent BepInEx objects. Their 4.0
    // releases keep several raid-owned BotOwner/BotsGroup references after the
    // world unloads. Clear only the mutable per-raid collections; registration,
    // faction tables and custom brain/layer definitions remain untouched.
    internal static class TerminalExternalBotCleanup
    {
        internal static void ResetForRaid()
        {
            int ruafBots = ClearStaticDictionary(
                "RUAFComeHome.Controllers.BotRuafManagerFactory", "managers");
            int checkpoints = ClearSingletonDictionary(
                "RUAFComeHome.Components.RuafCheckpointManager", "ZoneCheckpoints");
            int hunts = ClearSingletonDictionary(
                "MoreBotsAPI.Components.HuntManager", "huntGroups");

            if (ruafBots + checkpoints + hunts > 0)
                Plugin.Log.LogWarning($"[RaidCleanup] released persistent external AI state: "
                    + $"RUAF managers={ruafBots}, checkpoint zones={checkpoints}, hunt groups={hunts}");
        }

        private static int ClearStaticDictionary(string typeName, string fieldName)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                var field = type == null ? null : AccessTools.Field(type, fieldName);
                return Clear(field?.GetValue(null));
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[RaidCleanup] {typeName}.{fieldName}: {e.Message}");
                return 0;
            }
        }

        private static int ClearSingletonDictionary(string typeName, string fieldName)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) return 0;
                var field = AccessTools.Field(type, fieldName);
                if (field == null) return 0;
                int cleared = 0;
                foreach (var instance in Resources.FindObjectsOfTypeAll(type))
                    cleared += Clear(field.GetValue(instance));
                return cleared;
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[RaidCleanup] {typeName}.{fieldName}: {e.Message}");
                return 0;
            }
        }

        private static int Clear(object collection)
        {
            if (!(collection is IDictionary dictionary)) return 0;
            int count = dictionary.Count;
            dictionary.Clear();
            return count;
        }
    }
}
