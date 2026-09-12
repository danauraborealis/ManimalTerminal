using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.GameTriggers;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using HarmonyLib;

namespace Manimal.Terminal.Fika
{
    [HarmonyPatch(typeof(CoopGame), "Create")]
    internal static class CoopCreatePatch
    {
        private static void Prefix(JsonType.LocationSettings.Location location)
        {
            TerminalGate.Patch_CaptureLocationId.Prefix(location);
            TerminalCoop.MarkFikaRaid();
        }

        // BaseLocalGame construction can invoke other location/reset hooks. Assert
        // the definitive CoopGame identity again after Create has completed.
        private static void Postfix()
        {
            TerminalCoop.MarkFikaRaid();
            FikaAddonPlugin.Log.LogInfo("[TerminalSync] CoopGame.Create confirmed — solo story startup suppressed until Fika raid-start");
        }
    }

    [HarmonyPatch(typeof(CoopGame), nameof(CoopGame.Stop))]
    internal static class CoopEndingPatch
    {
        private static bool Prefix(CoopGame __instance, string profileId, ExitStatus exitStatus, string exitName, float delay)
            => TerminalEndingCutscene.Patch_InterceptExtraction.Prefix(__instance, profileId, exitStatus, exitName, delay);
    }

    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
    internal static class RaidStartedPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            if (TerminalGate.On)
            {
                TerminalCoop.MarkFikaRaid();
                FikaAddonPlugin.Sync?.StartRaid();
            }
        }
    }

    // The visual host owns Fika's manual Start Raid button. Terminal's optional
    // loading movie is deliberately removed there before Fika clones the cancel
    // control into FikaStartButton; clients may keep the presentation because they
    // do not own that control or hold the raid at the manual-start barrier.
    [HarmonyPatch]
    internal static class HostLoadingVideoPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("EFT.UI.Matchmaker.MatchmakerTimeHasCome");
            if (type == null) return null;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var parameters = method.GetParameters();
                if (method.Name == "Show" && parameters.Length == 3
                    && parameters[1].ParameterType.Name == "RaidSettings") return method;
            }
            return null;
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            if (!FikaBackendUtils.IsServer) return;
            TerminalLoadingVideo.StopImmediate();
            FikaAddonPlugin.Log.LogInfo("[TerminalSync] visual host loading movie suppressed — Fika Start Raid UI remains native");
        }
    }

    [HarmonyPatch]
    internal static class TriggerEmitPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(EFT.GameTriggers.ServerTriggersEmitter), nameof(EFT.GameTriggers.ServerTriggersEmitter.Emit));
            yield return AccessTools.Method(typeof(EFT.GameTriggers.LocalTriggersEmitter), nameof(EFT.GameTriggers.LocalTriggersEmitter.Emit));
            yield return AccessTools.Method(typeof(EFT.GameTriggers.ClientTriggersEmitter), nameof(EFT.GameTriggers.ClientTriggersEmitter.Emit));
        }
        private static bool Prefix(string __0, string __1)
        {
            if (!TerminalGate.On || !TerminalCoop.Active || !TerminalSync.SharedTriggers.Contains(__0)) return true;
            // Native cascades are also routed through the authority. Remote apply
            // uses TriggerEvent directly, so this never echoes the incoming event.
            if (TerminalCoop.Authority)
                FikaAddonPlugin.Sync?.Request(TerminalEvent.Trigger, __0, __1, 0);
            return false;
        }
    }

    [HarmonyPatch(typeof(HandlerExplosion), nameof(HandlerExplosion.OnExplosionTriggerEvent))]
    internal static class ReplayExplosionPatch
    {
        private static bool Prefix() => !TerminalCoop.Active || !TerminalCoop.Replaying;
    }
}
