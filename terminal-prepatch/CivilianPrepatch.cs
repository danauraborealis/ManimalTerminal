using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using Mono.Cecil;
using MoreBotsAPI;

namespace Manimal.Terminal.Prepatch
{
    // registers the terminal civilian WildSpawnType into Assembly-CSharp via
    // MoreBotsAPI's enum injection (ported from MitsuruMod 2026-08-18). value
    // 776701 kept from the source so any surviving profile data stays valid.
    // brainId 1 = Assault — the client's BigBrain layers (passive+flee) bind on
    // top of it and suppress all combat.
    public static class CivilianPrepatch
    {
        public const int WildSpawnTypeValue = 776701;
        public const string BotTypeName = "civilian";
        public const string ScavRole = "Civilian";

        public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

        public static void Patch(AssemblyDefinition assembly)
        {
            var log = Logger.CreateLogSource("Terminal Prepatch");

            if (!MoreBotsApiInstalled())
            {
                log.LogError("MoreBotsAPI plugin not detected — terminal civilians will not be registered.");
                return;
            }

            const int assaultBrainId = 1;
            var customType = new CustomWildSpawnType(
                WildSpawnTypeValue,
                BotTypeName,
                ScavRole,
                assaultBrainId,
                isBoss: false,
                isFollower: false,
                isHostileToEverybody: false);

            CustomWildSpawnTypeManager.RegisterWildSpawnType(customType, assembly);
            log.LogInfo($"Registered {BotTypeName} WildSpawnType ({WildSpawnTypeValue}).");
        }

        private static bool MoreBotsApiInstalled()
        {
            var patcherLoc = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var bepDir = Directory.GetParent(patcherLoc)?.Parent;
            if (bepDir == null) return false;
            var plugins = Path.Combine(bepDir.FullName, "plugins");
            return File.Exists(Path.Combine(plugins, "MoreBotsPlugin", "MoreBotsPlugin.dll"))
                || File.Exists(Path.Combine(plugins, "MoreBotsAPI", "MoreBotsPlugin.dll"));
        }
    }
}
