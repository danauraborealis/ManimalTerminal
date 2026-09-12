using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace Manimal.Terminal.Fika
{
    [BepInPlugin(BuildInfo.ModGuid, BuildInfo.PluginName, BuildInfo.Version)]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(BuildInfo.CoreModGuid, BuildInfo.Version)]
    public sealed class FikaAddonPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static TerminalSync Sync;
        private Harmony _harmony;
        private void Awake()
        {
            Log = Logger;
            Sync = new TerminalSync();
            _harmony = new Harmony(BuildInfo.ModGuid);
            // Explicit registration: a failing optional patch must not hide the rest.
            Patch(typeof(CoopCreatePatch));
            Patch(typeof(CoopEndingPatch));
            Patch(typeof(RaidStartedPatch));
            Patch(typeof(HostLoadingVideoPatch));
            Patch(typeof(TriggerEmitPatch));
            Patch(typeof(ReplayExplosionPatch));
            Log.LogInfo($"Terminal Fika {BuildInfo.Version} loaded (protocol {TerminalPacket.Protocol})");
        }
        private void Patch(System.Type type)
        {
            try { _harmony.CreateClassProcessor(type).Patch(); }
            catch (System.Exception e) { Log.LogError($"Required sync patch {type.Name} FAILED: {e}"); }
        }
        private void OnDestroy()
        {
            Sync?.Dispose(); Sync = null;
            _harmony?.UnpatchSelf();
        }
    }
}
