using System;
using Comfort.Common;
using EFT;
using HarmonyLib;

namespace Manimal.Terminal
{
    // are we on our map? every terminal system gates on this so the plugin is inert
    // on any other location. ported from icebreaker's IceGate — terminal's slot is
    // native so the id is simply "Terminal".
    internal static class TerminalGate
    {
        internal const string LocationId = "Terminal";

        // set by Patch_CaptureLocationId below at raid creation, before GameWorld
        internal static string PendingLocationId;

        // TerminalGate.On sits in hot Harmony/BigBrain paths. Resolve the singleton
        // at most once per Unity frame; every later query is a plain cached bool.
        private static int _cachedFrame = -1;
        private static bool _cachedOn;

        internal static bool On
        {
            get
            {
                int frame;
                try { frame = UnityEngine.Time.frameCount; }
                catch { frame = -1; }
                if (frame >= 0 && frame == _cachedFrame) return _cachedOn;

                bool on = false;
                try
                {
                    var w = Singleton<GameWorld>.Instance;
                    if (w != null && !string.IsNullOrEmpty(w.LocationId))
                        on = string.Equals(w.LocationId, LocationId, StringComparison.OrdinalIgnoreCase);
                    else
                        on = !string.IsNullOrEmpty(PendingLocationId)
                            && string.Equals(PendingLocationId, LocationId, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    on = !string.IsNullOrEmpty(PendingLocationId)
                        && string.Equals(PendingLocationId, LocationId, StringComparison.OrdinalIgnoreCase);
                }

                _cachedFrame = frame;
                _cachedOn = on;
                return on;
            }
        }

        // the game hands smethod_6 the authoritative Location object before any other
        // identity exists — capture the id so construction-time patches can gate on it
        [HarmonyPatch(typeof(LocalGame), "smethod_6")]
        internal static class Patch_CaptureLocationId
        {
            private static void Prefix(LocationSettingsClass.Location location)
            {
                bool previousWasTerminal = string.Equals(PendingLocationId, LocationId, StringComparison.OrdinalIgnoreCase);
                PendingLocationId = location?.Id;
                _cachedFrame = -1;
                bool enteringTerminal = string.Equals(PendingLocationId, LocationId, StringComparison.OrdinalIgnoreCase);

                if (!enteringTerminal)
                {
                    // Diagnostics alter global Unity callbacks/player-loop state.
                    // Tear them down before a vanilla map begins construction.
                    TerminalRenderProfiler.Disable();
                    TerminalFramePhase.Disable();
                    TerminalLoopProbe.Restore();
                }
                // All BepInEx plugins are loaded by raid creation. This second chance
                // arms the optional AI Limit hook even when it loaded after us.
                if (enteringTerminal)
                {
                    TerminalAILimitFirewall.TryInstall();
                    TerminalSainCompat.TryInstall();
                    TerminalCrewJobs.Register();
                }
                if (previousWasTerminal || enteringTerminal)
                    TerminalExternalBotCleanup.ResetForRaid();
                TerminalIntroCutscene.ResetForNewRaid();
                TerminalAttackCutscene.ResetForNewRaid();
                TerminalLights.ResetForNewRaid();
                TerminalFlares.ResetForRaid();
                TerminalCullingDriver.ResetForNewRaid();
                TerminalSoundRig.ResetForNewRaid();
                TerminalAIPlaces.ResetForNewRaid();
                TerminalGatesExplosion.ResetForRaid();
                TerminalCraneFalling.ResetForRaid();
                TerminalFinalExit.ResetForRaid();
                TerminalArtillery.ResetForRaid();
                TerminalPumpStation.ResetForRaid();
                TerminalEndingCutscene.ResetForRaid();
                TerminalEpilogueScreen.ResetForRaid();
                TerminalWater.ResetForRaid();
                TerminalLootBind.ResetForRaid();
                TerminalDryPlanes.ResetForRaid();
                TerminalShoreAudio.ResetForRaid();
                TerminalRainAudio.ResetForRaid();
                TerminalHoldLock.ResetForRaid();
                TerminalPerfWatch.ResetForRaid();
                TerminalShadowGuard.ResetForRaid();
                TerminalSceneScrub.ResetForRaid();
                Civilian.CivilianFleeState.ClearAll();
                Civilian.CivilianUnstickHelper.ClearAll();
                Civilian.CivilianMeleeEnforcer.ClearAll();
                TerminalCrewJobs.Reset();
                TerminalShaderRebind.ResetForRaid();
                TerminalSpawnGate.ResetForRaid();
                TerminalPopulationDirector.ResetForRaid();
                if (PendingLocationId == "Terminal")
                {
                    TerminalArtillery.InjectConfig();
                    TerminalBossRoll.Roll(location);
                }
                Plugin.Log.LogInfo($"[TerminalGate] raid location: '{PendingLocationId ?? "<null>"}'");
            }
        }
    }
}
