using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Audio.SpatialSystem;
using EFT;
using EFT.EnvironmentEffect;
using EFT.Interactive;
using HarmonyLib;

namespace Manimal.Terminal
{
    // raid-flow fixes for the first-raid failure set, all icebreaker-proven.

    // scene-based map check for load-time patches — TerminalGate can't answer during
    // the load window (LocalGame doesn't exist yet on this path)
    internal static class TerminalLoaded
    {
        internal static bool Check()
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (sc.isLoaded && sc.name != null && sc.name.StartsWith("Terminal", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    // RETIRED 2026-08-11 — this was the pre-port stopgap: spatial audio needed baked
    // data + room/portal components + occlusion assets that didn't survive the rip, so
    // Initialize NRE'd and faulted the whole raid-load task, and skipping it was the
    // only way to matching. its own comment promised "the acoustics port later stages
    // real data and lets BSG's init run" — that port has landed
    // (TerminalAcoustics.TryPrepareSpatialAudio), and TerminalAudioFixes.
    // Patch_SpatialAudioInitSkip now owns this decision: stage first, and skip ONLY
    // when staging is unavailable.
    //
    // leaving this class registered was the bug: it ran as a SECOND prefix after the
    // staging one, returned false, and skipped the original for good — so every raid
    // staged 607 components and then never initialized them
    // (Initialized=False, rooms untracked, every ambient player ungated =
    // "the same zone audio everywhere"). do not reintroduce it; the airbags below
    // already cover the uninitialized case.

    // followup: BetterAudio.PlayAtPoint routes every impact/gunshot through
    // ProcessSourceOcclusion, which NREs on the never-initialized internals —
    // thousands of NREs per mag dump. return -1, the same "no occlusion" result
    // BSG's own EOcclusionTest.None path uses. silent on purpose: fires per sound.
    [HarmonyPatch]
    internal static class Patch_OcclusionWhenUninitialized
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(SpatialAudioSystem).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "ProcessSourceOcclusion");
        }

        private static bool Prefix(ref int __result)
        {
            if (SpatialAudioSystem.Initialized)
                return true; // real maps: run the original untouched
            __result = -1;
            return false;
        }
    }

    // THE infinite-load fix: Terminal_Culling's Adaptive_grid husk got its script ref
    // repaired by the guid rebind, so BSG's PerfectCullingAdaptiveGrid comes alive and
    // hard-reads StreamingAssets/Culling_Data/<hash>_packed_cull.bytes at scene
    // activation — the hash didn't survive the rip (empty), the file can't exist, and
    // the failed load stalls activation forever (icebreaker field logs: the
    // infinite-load-with-ambient-audio crash). skip its Awake on terminal scenes.
    // manual patch: the type is BSG's EFT-layer fork, resolved EXPLICITLY from
    // Assembly-CSharp — TypeByName can land on our shipped PerfectCullingRuntime twin
    // and a wrong-twin patch gates nothing, silently (the icebreaker known weakness).
    // proper fix is deleting the Adaptive_grid GO in the editor + rebundle; this is
    // the backstop that also covers old bundles.
    internal static class Patch_NativeCullingGate
    {
        internal static void TryPatch(Harmony harmony)
        {
            var t = typeof(GameWorld).Assembly.GetType("Koenigz.PerfectCulling.EFT.PerfectCullingAdaptiveGrid");
            if (t == null) { Plugin.Log.LogDebug("[RaidFix] no native PerfectCullingAdaptiveGrid class — gate not needed"); return; }
            var awake = AccessTools.Method(t, "Awake");
            if (awake == null) { Plugin.Log.LogWarning("[RaidFix] PerfectCullingAdaptiveGrid.Awake not found — native culling gate NOT armed"); return; }
            harmony.Patch(awake, prefix: new HarmonyMethod(typeof(Patch_NativeCullingGate), nameof(Prefix)));
            Plugin.Log.LogInfo("[RaidFix] native adaptive-grid gate armed (Assembly-CSharp class, explicit bind)");
        }

        private static bool Prefix(UnityEngine.MonoBehaviour __instance)
        {
            try
            {
                var sc = __instance != null ? __instance.gameObject.scene.name : null;
                if (sc == null || !sc.StartsWith("Terminal", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { return true; }
            Plugin.Log.LogWarning("[RaidFix] suppressed native PerfectCullingAdaptiveGrid.Awake (terminal scene — its packed bake cannot exist; sidecar culling owns this map)");
            return false;
        }
    }

    // the glass-break prefab ref didn't survive the rip — method_0 instantiates null
    // and the exception would otherwise escape into raid init. every later use is
    // null-guarded (verified on icebreaker); the loss is breakable window glass.
    // scene-based gate (transit-gate-blindness): this manager Awakes during preload
    // windows where the location gate can still answer the old map.
    [HarmonyPatch(typeof(WindowBreakerManager), "method_0")]
    internal static class Patch_WindowBreakerPrewarm
    {
        private static Exception Finalizer(Exception __exception, WindowBreakerManager __instance)
        {
            if (__exception == null) return null;
            bool ours;
            try { ours = __instance != null && __instance.gameObject.scene.name != null
                          && __instance.gameObject.scene.name.StartsWith("Terminal", StringComparison.OrdinalIgnoreCase); }
            catch { ours = false; }
            if (!ours && !TerminalGate.On) return __exception;
            Plugin.Log.LogWarning($"[RaidFix] swallowed WindowBreakerManager.method_0: {__exception.Message}");
            return null;
        }
    }

    // SpawnPointManagerClass.smethod_3 sets each BotZone.HasPmcBotSpawns by scanning
    // its markers' categories — a marker with a null SpawnPoint NREs the whole raid
    // init. only matters for bot-PMC spawning; our player spawns are zone-less.
    [HarmonyPatch(typeof(SpawnPointManagerClass), "smethod_3")]
    internal static class Patch_SpawnPmcScan
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!TerminalGate.On) return __exception;
            Plugin.Log.LogWarning($"[RaidFix] swallowed SpawnPointManagerClass.smethod_3: {__exception.Message}");
            return null;
        }
    }

    // BotsController.Init does FindUnityObjectOfType<AIStationaryController>() then
    // calls .Init on it with NO null check — a scene without one NREs inside Init
    // itself. create an empty controller up front. ObservedCullingManager likewise
    // must exist BEFORE any bot spawns (bot body visibility — without it every bot
    // body resolves invisible, the floating-gear ghosts).
    [HarmonyPatch(typeof(BotsController), "Init")]
    internal static class Patch_EnsureStationaryController
    {
        private static void Prefix()
        {
            if (!TerminalGate.On) return;
            if (UnityEngine.Object.FindObjectOfType<AIStationaryController>() == null)
            {
                new UnityEngine.GameObject("Terminal_AIStationary_Fix").AddComponent<AIStationaryController>();
                Plugin.Log.LogWarning("[RaidFix] created missing AIStationaryController (no stationary weapons on map)");
            }
            if (!Comfort.Common.Singleton<ObservedCullingManager>.Instantiated)
            {
                new UnityEngine.GameObject("Terminal_ObservedCullingManager_Fix").AddComponent<ObservedCullingManager>();
                Plugin.Log.LogWarning("[RaidFix] created missing ObservedCullingManager (bot body visibility)");
            }

            // fresh AI holders ship null collection fields — AIVoxelesData.VoxelsList
            // has NO initializer and RestoreData LINQs it (Count(...)): the
            // ArgumentNullException that aborted Init on the first raids, its frame
            // inlined into the DMD so the log never named it. create the covers graph
            // ourselves (idempotent — Init's own CreateOrFind finds it) and initialize
            // every null List/array field on it + its holder components. empty nav
            // data is correct until the AI bake restore lands.
            try
            {
                var covers = AICoversData.CreateOrFind(true);
                int healed = HealNullCollections(covers);
                foreach (var f in typeof(AICoversData).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!typeof(UnityEngine.Component).IsAssignableFrom(f.FieldType)) continue;
                    var c = f.GetValue(covers) as UnityEngine.Component;
                    if (c != null) healed += HealNullCollections(c);
                }
                Plugin.Log.LogWarning($"[RaidFix] AI covers graph pre-healed: {healed} null collection field(s) initialized (empty nav data until the AI bake port)");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RaidFix] covers pre-heal failed: {e.Message}"); }
        }

        // null List<T>/T[] fields -> empty instances; rank-1 arrays only (RestoreData
        // builds the 3D voxel array itself from the zeroed bounds)
        private static int HealNullCollections(object o)
        {
            int n = 0;
            foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var ft = f.FieldType;
                try
                {
                    if (ft.IsArray && ft.GetArrayRank() == 1 && f.GetValue(o) == null)
                    { f.SetValue(o, Array.CreateInstance(ft.GetElementType(), 0)); n++; }
                    else if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(System.Collections.Generic.List<>) && f.GetValue(o) == null)
                    { f.SetValue(o, Activator.CreateInstance(ft)); n++; }
                }
                catch { }
            }
            return n;
        }
    }

    [HarmonyPatch(typeof(AIStationaryController), "Init")]
    internal static class Patch_StationaryInit
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!TerminalGate.On) return __exception; // vanilla maps keep their real exceptions
            Plugin.Log.LogWarning($"[RaidFix] swallowed AIStationaryController.Init: {__exception.Message}");
            return null;
        }
    }

    // ripped BotZones arrive with broken lists: null/destroyed SpawnPointMarkers
    // (get_SpawnPoints NREs mapping marker.SpawnPoint — a computed property, so
    // swallowing Init wouldn't save later callers) and possibly null PatrolWays.
    // heal both before Init consumes them.
    [HarmonyPatch(typeof(BotZone), "Init")]
    internal static class Patch_BotZonePruneMarkers
    {
        private static void Prefix(BotZone __instance)
        {
            if (!TerminalGate.On) return; // vanilla zones are healthy — dont touch their lists
            if (__instance.PatrolWays == null) __instance.PatrolWays = new PatrolWay[0];
            var list = __instance.SpawnPointMarkers;
            if (list == null)
            {
                __instance.SpawnPointMarkers = new System.Collections.Generic.List<EFT.Game.Spawning.SpawnPointMarker>();
                return;
            }
            int removed = list.RemoveAll(m =>
            {
                if (m == null) return true; // unity fake-null catches destroyed markers
                try { return m.SpawnPoint == null; }
                catch { return true; }
            });
            if (removed > 0)
                Plugin.Log.LogDebug($"[RaidFix] pruned {removed} dead spawn markers from BotZone '{__instance.name}' ({list.Count} left)");
        }
    }

    // bot door graph refresh — walks doors against a possibly-empty covers graph
    [HarmonyPatch(typeof(BotDoorsController), "RefreshData")]
    internal static class Patch_BotDoorsRefresh
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!TerminalGate.On) return __exception;
            Plugin.Log.LogWarning($"[RaidFix] swallowed BotDoorsController.RefreshData: {__exception.Message}");
            return null;
        }
    }

    // BOT-INIT FIREWALL — the client half of the choke-point defense. other mods'
    // patches on BotsController.Init run inside the same call, and a mod's per-map
    // table with no 'Terminal' entry kills raid creation outright. mod-agnostic: on
    // OUR map only, any exception escaping Init is swallowed WITH THE CULPRIT NAMED,
    // so that mod's feature degrades instead of the raid dying. vanilla maps keep
    // their exceptions.
    [HarmonyPatch(typeof(BotsController), "Init")]
    internal static class Patch_BotsInitFirewall
    {
        private static void Prefix()
        {
            if (!TerminalGate.On) return;
            RaidFirewall.WrapForeignPostfixes(AccessTools.Method(typeof(BotsController), "Init"));
            LateWaypointsPatch.Apply();
            TerminalOrbitFirewall.Apply();
        }

        private static Exception Finalizer(Exception __exception)
            => RaidFirewall.Swallow(__exception, "BotsController.Init");
    }

    // TRUE ORBIT MAP FIREWALL. ORBIT registers its BigBrain layer globally during its
    // delayed startup, before a raid/map exists. Blocking only OrbitInit therefore
    // left that layer registered: every Terminal bot still constructed OrbitBrainLayer,
    // which immediately dereferenced the manager we had deliberately prevented ORBIT
    // from creating. Besides the NRE spam, that repeated failed construction could
    // leave partially-built brain state behind and was a credible sawtooth contributor.
    //
    // Make ORBIT's own private role-exclusion check report "excluded" on Terminal.
    // Its constructor then follows ORBIT's intended inert path (base constructed,
    // _excluded=true, IsActive=false) instead of touching OrbitManager. We also gate
    // every live ORBIT hook that can mutate or repeatedly inspect raid state. Hooks
    // which are themselves Harmony prefixes return TRUE when bypassed so Tarkov's
    // original method still runs. OrbitDispose is deliberately left alone: it is the
    // cleanup net for stale state after a preceding ORBIT-enabled raid. All gates are
    // map-scoped, so ORBIT remains completely unchanged everywhere except Terminal.
    internal static class TerminalOrbitFirewall
    {
        private static bool _done;
        private static bool _logged;
        private static bool _layerLogged;

        internal static void Apply()
        {
            if (_done) return;

            var targets = new[]
            {
                ("Orbit.Patches.OrbitInitPatch", "Postfix"),
                ("Orbit.Patches.OrbitTickPatch", "Postfix"),
                ("Orbit.Patches.DoorCarverShrinkPatch", "Patch"),
                ("Orbit.Patches.DoorUnlockTracePatch", "Patch"),
                ("Orbit.Patches.SoftTeleportTracePatch", "Patch"),
                ("Orbit.Patches.HardTeleportTracePatch", "Patch"),
                ("Orbit.Patches.BotVaultingPatch", "Patch"),
                ("Orbit.Patches.MovementContextHumanizePatch", "Patch"),
                ("Orbit.Patches.ManualFixedUpdateSkipPatch", "PatchPrefix"),
                ("Orbit.Patches.AirdropLandedPatch", "Postfix"),
                ("Orbit.Patches.InventoryChangePatch", "Postfix"),
                ("Orbit.Patches.CorpseRegistrationPatch", "Postfix"),
                ("Orbit.Patches.RescueInterceptPatch", "Prefix"),
                ("Orbit.Patches.AssaultEnemyFarBypassPatch", "Patch"),
                ("Orbit.Patches.ExfilLayerBypassPatch", "Patch"),
                ("Orbit.Patches.PtrlBirdEyeBypassPatch", "Patch"),
            };
            var methods = new List<MethodInfo>();
            foreach (var spec in targets)
            {
                var type = AccessTools.TypeByName(spec.Item1);
                var method = type != null ? AccessTools.Method(type, spec.Item2) : null;
                if (method != null) methods.Add(method);
            }
            if (methods.Count == 0) return; // ORBIT is not installed

            var layerType = AccessTools.TypeByName("Orbit.Brain.OrbitBrainLayer");
            var exclusion = layerType != null
                ? AccessTools.Method(layerType, "IsExcludedRole", new[] { typeof(BotOwner) })
                : null;
            if (exclusion == null)
            {
                // Do not partially arm and then duplicate-patch the hooks on a retry.
                Plugin.Log.LogWarning("[OrbitFirewall] ORBIT hooks found but OrbitBrainLayer exclusion was not; firewall deferred until next BotsController.Init");
                return;
            }

            var harmony = new Harmony("com.manimal.terminal.orbit-firewall");
            var voidGate = new HarmonyMethod(AccessTools.Method(typeof(TerminalOrbitFirewall), nameof(VoidHookPrefix)));
            var boolGate = new HarmonyMethod(AccessTools.Method(typeof(TerminalOrbitFirewall), nameof(BoolHookPrefix)));
            foreach (var method in methods)
                harmony.Patch(method, prefix: method.ReturnType == typeof(bool) ? boolGate : voidGate);
            harmony.Patch(exclusion, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(TerminalOrbitFirewall), nameof(ForceExcludedPrefix))));

            _done = true;
            Plugin.Log.LogInfo($"[OrbitFirewall] armed layer exclusion + {methods.Count}/{targets.Length} Terminal-only ORBIT hook gates");
        }

        private static bool VoidHookPrefix()
        {
            if (!TerminalGate.On) return true;
            LogRuntimeBlockOnce();
            return false;
        }

        // These are prefixes around ORBIT's Harmony-prefix methods. Returning false
        // skips ORBIT's hook body; __result=true tells Harmony to continue into the
        // original EFT method. Defaulting this to false would freeze/suppress AI.
        private static bool BoolHookPrefix(ref bool __result)
        {
            if (!TerminalGate.On) return true;
            LogRuntimeBlockOnce();
            __result = true;
            return false;
        }

        private static bool ForceExcludedPrefix(ref bool __result)
        {
            if (!TerminalGate.On) return true;
            __result = true;
            if (!_layerLogged)
            {
                _layerLogged = true;
                Plugin.Log.LogWarning("[OrbitFirewall] ORBIT brain layer made inert on Terminal before manager/agent construction");
            }
            return false;
        }

        private static void LogRuntimeBlockOnce()
        {
            if (_logged) return;
            _logged = true;
            Plugin.Log.LogWarning("[OrbitFirewall] ORBIT runtime disabled on Terminal — stage director and native bot movement retain ownership");
        }
    }

    // GAME-START FIREWALL — same defense, second choke point: an exception in a
    // GameWorld.OnGameStarted postfix climbs the TarkovApplication async chain and
    // pops the error dialog mid-raid while the game half-tears down.
    [HarmonyPatch(typeof(GameWorld), "OnGameStarted")]
    internal static class Patch_GameStartFirewall
    {
        private static void Prefix() => RaidFirewall.WrapForeignPostfixes(
            AccessTools.Method(typeof(GameWorld), "OnGameStarted"));

        private static Exception Finalizer(Exception __exception)
            => RaidFirewall.Swallow(__exception, "GameWorld.OnGameStarted");
    }

    // shared machinery for the choke points above (1:1 icebreaker port)
    internal static class RaidFirewall
    {
        private const string OwnPrefix = "com.manimal.terminal";
        private static readonly System.Collections.Generic.HashSet<MethodBase> Wrapped = new System.Collections.Generic.HashSet<MethodBase>();

        // PER-POSTFIX AIRBAG: a postfix that throws aborts every postfix queued after
        // it — one mod's missing-map table would cost innocent mods their hook too.
        // each foreign postfix gets its OWN finalizer so the chain continues.
        internal static void WrapForeignPostfixes(MethodBase chokePoint)
        {
            if (chokePoint == null || !TerminalGate.On) return;
            try
            {
                var info = Harmony.GetPatchInfo(chokePoint);
                if (info?.Postfixes == null) return;
                var h = new Harmony(OwnPrefix + ".postfixairbag");
                var airbag = new HarmonyMethod(AccessTools.Method(typeof(RaidFirewall), nameof(PostfixAirbag)));
                foreach (var p in info.Postfixes)
                {
                    if (p.owner.StartsWith(OwnPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Wrapped.Add(p.PatchMethod)) continue;
                    try
                    {
                        h.Patch(p.PatchMethod, finalizer: airbag);
                        Plugin.Log.LogDebug($"[Firewall] airbag on {p.PatchMethod.DeclaringType?.FullName}.{p.PatchMethod.Name} (owner {p.owner})");
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogDebug($"[Firewall] couldnt wrap {p.PatchMethod.Name} of {p.owner}: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Firewall] postfix sweep failed on {chokePoint.Name}: {e.Message}");
            }
        }

        private static Exception PostfixAirbag(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null) return null;
            if (!TerminalGate.On) return __exception;
            var asm = __originalMethod?.DeclaringType?.Assembly.GetName().Name ?? "unknown";
            Plugin.Log.LogError($"[Firewall] {asm}'s hook {__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name} "
                + $"threw on terminal — swallowed; only THAT mod's hook is skipped, later mods' hooks still run "
                + $"(likely a per-map table with no 'Terminal' entry — report it to {asm}'s author). inner: {__exception.Message}");
            return null;
        }

        // choke-point backstop: swallow-and-name for anything the airbags missed
        internal static Exception Swallow(Exception ex, string site)
        {
            if (ex == null) return null;
            if (!TerminalGate.On) return ex;
            string culprit = "unknown";
            foreach (var line in (ex.StackTrace ?? "").Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line,
                    @"at (?!EFT\.|System\.|UnityEngine\.|Manimal\.|DMD|\(wrapper)([A-Za-z_][\w]*)[\w.]*\.");
                if (m.Success) { culprit = m.Groups[1].Value; break; }
            }
            Plugin.Log.LogError($"[Firewall] a mod threw inside {site} on terminal — "
                + $"swallowed so the raid can start; that mod is INACTIVE this raid. culprit: {culprit} "
                + $"(likely a per-map table with no 'Terminal' entry — report it to that mod's author). "
                + $"FULL: {ex}");
            return null;
        }
    }

    // DrakiaXYZ Waypoints postfixes BotsController.Init and NREs on our map's
    // door/nav state. applied lazily from the Init prefix (our plugin loads before
    // Waypoints, so TypeByName finds nothing at startup). no-op if not installed.
    internal static class LateWaypointsPatch
    {
        private static bool _done;

        internal static void Apply()
        {
            if (_done) return;
            _done = true;
            var t = AccessTools.TypeByName("DrakiaXYZ.Waypoints.Patches.DoorLinkPatch");
            if (t == null) return;
            var target = AccessTools.Method(t, "PatchPostfix");
            if (target == null)
            {
                Plugin.Log.LogWarning("[RaidFix] Waypoints DoorLinkPatch found but PatchPostfix missing — layout changed?");
                return;
            }
            new Harmony("com.manimal.terminal.raidfix-late").Patch(target,
                finalizer: new HarmonyMethod(typeof(LateWaypointsPatch), nameof(SwallowFinalizer)));
            Plugin.Log.LogDebug("[RaidFix] late-patched Waypoints DoorLinkPatch with finalizer");
        }

        private static Exception SwallowFinalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!TerminalGate.On) return __exception;
            Plugin.Log.LogWarning($"[RaidFix] swallowed Waypoints DoorLinkPatch: {__exception.Message}");
            return null;
        }
    }

    // Player.Init dereferences EnvironmentManager.Instance — the scrub killed the
    // broken ripped instance, so rebuild a bare one (always Outdoor) before any
    // player initializes. icebreaker lesson: anchor on Player.Init, not a per-path
    // Create method — every player-creation flavor funnels through it, and the
    // ensure is idempotent. ObservedCullingManager drives observed BODY visibility
    // (bots; remote players under fika) — retail scenes ship it, ours doesn't;
    // without it every observed body resolves invisible (floating-gear ghosts).
    [HarmonyPatch(typeof(Player), nameof(Player.Init))]
    internal static class Patch_EnsureEnvBeforeAnyPlayerInit
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            if (!TerminalGate.On) return; // vanilla maps own their EnvironmentManager

            if (!Comfort.Common.Singleton<ObservedCullingManager>.Instantiated)
            {
                new UnityEngine.GameObject("Terminal_ObservedCullingManager_Fix").AddComponent<ObservedCullingManager>();
                Plugin.Log.LogWarning("[RaidFix] created missing ObservedCullingManager (observed body visibility)");
            }

            if (EnvironmentManager.Instance != null) return;
            new UnityEngine.GameObject("Terminal_EnvManager_Fix").AddComponent<EnvironmentManager>();
            Plugin.Log.LogWarning("[RaidFix] created missing EnvironmentManager singleton (bare — no indoor triggers yet)");
        }
    }
}
