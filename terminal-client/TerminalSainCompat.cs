using System;
using System.Reflection;
using HarmonyLib;

namespace Manimal.Terminal
{
    // SAIN 4.4.3 recognizes ELocation.Terminal, but deliberately omits Terminal
    // from its LocationSettings dictionary.  Present only SAIN's location parser
    // with Customs: a mixed indoor/outdoor preset that suits the port. The real
    // GameWorld.LocationId remains Terminal for every game and map system.
    internal static class TerminalSainCompat
    {
        private const string LocationTypeName = "SAIN.Components.LocationClass";
        private static readonly object Sync = new object();
        private static Harmony _harmony;
        private static FieldInfo _foundLocation;
        private static bool _installed;
        private static object _lastAppliedInstance;

        // Registration-time compatibility decisions must work even if SAIN's
        // private location parser changes and the optional patch cannot install.
        // BepInEx has loaded plugin assemblies before Awake, so the type itself is
        // the reliable signal that SAIN owns this client's combat layers.
        internal static bool Detected => _installed || AccessTools.TypeByName(LocationTypeName) != null;

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
                var locationType = AccessTools.TypeByName(LocationTypeName);
                if (locationType == null) return; // optional mod absent/not loaded yet

                try
                {
                    var target = AccessTools.Method(locationType, "parseLocation");
                    if (target == null)
                    {
                        Plugin.Log.LogWarning("[SainCompat] SAIN detected but LocationClass.parseLocation was not found — compatibility layout changed");
                        return;
                    }

                    _foundLocation = AccessTools.Field(locationType, "_foundLocation");
                    _harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(
                        typeof(TerminalSainCompat), nameof(ParseLocationPrefix))));
                    _installed = true;
                    string version = locationType.Assembly.GetName().Version?.ToString() ?? "unknown";
                    Plugin.Log.LogWarning($"[SainCompat] SAIN {version} detected — Terminal will use SAIN's Customs location preset; real map ID remains Terminal");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[SainCompat] SAIN location compatibility patch failed: {e}");
                }
            }
        }

        private static bool ParseLocationPrefix(object __instance, ref object __result)
        {
            if (!TerminalGate.On) return true;
            try
            {
                // 4.4.x keeps ELocation in SAIN; 4.5 moved it into the shared
                // preset assembly. Harmony normally supplies a boxed default result,
                // so that remains the preferred version-independent source.
                var enumType = __result?.GetType()
                    ?? AccessTools.TypeByName("SAIN.Preset.Shared.Enums.ELocation")
                    ?? AccessTools.TypeByName("SAIN.ELocation")
                    ?? AccessTools.TypeByName("ELocation");
                if (enumType == null || !enumType.IsEnum) return true;

                __result = Enum.Parse(enumType, "Customs");
                _foundLocation?.SetValue(__instance, true);
                if (!ReferenceEquals(_lastAppliedInstance, __instance))
                {
                    _lastAppliedInstance = __instance;
                    Plugin.Log.LogWarning("[SainCompat] Terminal resolved as Customs inside SAIN for this raid");
                }
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[SainCompat] Customs substitution failed; using SAIN's native location: {e.Message}");
                return true;
            }
        }
    }
}
