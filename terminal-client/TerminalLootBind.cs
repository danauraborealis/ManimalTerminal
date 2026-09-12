using System;
using System.Collections;
using System.Reflection;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // CONTAINER LOOT BIND DEFERRAL (raid reports 2026-08-18: search prompt works,
    // window never opens, gear tax 'no bound valberg safe', 281x 'container_X is
    // missing'): GameWorld.method_5 matches server container loot to scene
    // containers via LocationScene.GetAllObjects — enabled-only, registry-built at
    // LocationScene.Awake. on this map the bind was racing scene activation: the
    // registries were empty when the loot arrived, every container logged missing,
    // nothing ever bound (loose loot is position-spawned and survived — the exact
    // observed split). hold the bind until Design_Stuff's registry is live.
    internal static class TerminalLootBind
    {
        private static bool _passThrough;

        internal static void ResetForRaid() => _passThrough = false;

        private static int BiggestContainerRegistry()
        {
            int best = 0;
            try
            {
                foreach (var ls in LocationScene.LoadedScenes)
                {
                    if (ls == null || ls.LootableContainers == null) continue;
                    int alive = 0;
                    foreach (var c in ls.LootableContainers) if (c) alive++;
                    if (alive > best) best = alive;
                }
            }
            catch { }
            return best;
        }

        [HarmonyPatch(typeof(GameWorld), "SpawnLoot")]
        internal static class Patch_DeferLootBind
        {
            [HarmonyPrefix]
            private static bool Prefix(GameWorld __instance, object[] __args)
            {
                try
                {
                    if (!TerminalGate.On || _passThrough) return true;
                    TerminalContainerTemplates.ApplyLoadedContainers();
                    int have = BiggestContainerRegistry();
                    // Design_Stuff alone carries 282 containers — a registry that big
                    // means the scenes are awake and the bind is safe
                    if (have >= 150) return true;
                    Plugin.Log.LogWarning($"[LootBind] loot arrived before the scene registries "
                        + $"(biggest container registry: {have}) — DEFERRING the bind");
                    var host = new GameObject("Terminal_LootBindDefer").AddComponent<DeferHost>();
                    host.Game = __instance;
                    host.Args = __args;
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[LootBind] prefix threw, binding normally: {e.Message}");
                    return true;
                }
            }
        }

        internal class DeferHost : MonoBehaviour
        {
            public GameWorld Game;
            public object[] Args;

            private void Start() => StartCoroutine(Run());

            private IEnumerator Run()
            {
                // short grace for a late-but-healthy registration
                float deadline = Time.realtimeSinceStartup + 6f;
                while (Time.realtimeSinceStartup < deadline && BiggestContainerRegistry() < 150)
                    yield return null;

                if (BiggestContainerRegistry() < 150)
                {
                    DumpAuthoredState();
                    BuildSyntheticRegistry();
                    yield return null; // let the synthetic Awake run
                }

                int have = BiggestContainerRegistry();
                Plugin.Log.LogInfo($"[LootBind] releasing the deferred bind (registry: {have})");
                try
                {
                    _passThrough = true;
                    AccessTools.Method(typeof(GameWorld), "SpawnLoot").Invoke(Game, Args);
                }
                catch (Exception e) { Plugin.Log.LogError($"[LootBind] deferred bind FAILED: {e}"); }
                finally { _passThrough = false; }
                Destroy(gameObject);
            }

            // root-cause forensics: what does the authored Location_scene look like
            // at runtime (2026-08-18: bundle data perfect, runtime registry empty)
            private static void DumpAuthoredState()
            {
                try
                {
                    var scn = UnityEngine.SceneManagement.SceneManager.GetSceneByName("Terminal_Design_Stuff");
                    if (!scn.IsValid() || !scn.isLoaded) { Plugin.Log.LogWarning("[LootBind] Design_Stuff scene not loaded?!"); return; }
                    foreach (var root in scn.GetRootGameObjects())
                    {
                        if (root.name != "Location_scene") continue;
                        var comps = root.GetComponents<Component>();
                        int nulls = 0;
                        foreach (var c in comps) if (c == null) nulls++;
                        var ls = root.GetComponent<LocationScene>();
                        Plugin.Log.LogWarning($"[LootBind] authored Location_scene: active={root.activeInHierarchy} "
                            + $"comps={comps.Length} nullComps={nulls} LocationScene={(ls ? $"alive enabled={ls.enabled} containers={(ls.LootableContainers?.Length ?? -1)}" : "MISSING/HUSK")}");
                        return;
                    }
                    Plugin.Log.LogWarning("[LootBind] no 'Location_scene' root in Design_Stuff at runtime");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[LootBind] state dump failed: {e.Message}"); }
            }

            // the authored registry is dead — rebuild it from the live scene objects
            // (they all exist: prompts raycast them fine). inactive-GO build so the
            // arrays are set before Awake registers them.
            private static void BuildSyntheticRegistry()
            {
                try
                {
                    var go = new GameObject("Terminal_SyntheticLocationScene");
                    go.SetActive(false);
                    var ls = go.AddComponent<LocationScene>();
                    ls.LootableContainers = UnityEngine.Object.FindObjectsOfType<LootableContainer>(true);
                    ls.StaticLoot = UnityEngine.Object.FindObjectsOfType<StaticLoot>(true);
                    ls.WorldInteractiveObjects = UnityEngine.Object.FindObjectsOfType<WorldInteractiveObject>(true);
                    // everything else empty on purpose — bots/exfils/lamps are long
                    // initialized by now, and Awake NREs on null arrays
                    ls.SyncAbles = Array.Empty<ISyncAble>();
                    ls.TriggerEntities = Array.Empty<GameObject>();
                    ls.ControlledLampGroups = Array.Empty<ControlledLampGroup>();
                    ls.NavMeshLinks = Array.Empty<NavMeshDoorLink>();
                    ls.SpawnPointMarkers = Array.Empty<EFT.Game.Spawning.SpawnPointMarker>();
                    ls.BotZones = Array.Empty<BotZone>();
                    ls.ExfiltrationPoints = Array.Empty<ExfiltrationPoint>();
                    var aiField = AccessTools.Field(typeof(LocationScene), "AIPlaceInfos");
                    if (aiField != null && aiField.GetValue(ls) == null)
                        aiField.SetValue(ls, Array.CreateInstance(aiField.FieldType.GetElementType(), 0));
                    go.SetActive(true); // Awake registers the rebuilt arrays
                    Plugin.Log.LogWarning($"[LootBind] SYNTHETIC registry built: {ls.LootableContainers.Length} containers, "
                        + $"{ls.StaticLoot.Length} static loot, {ls.WorldInteractiveObjects.Length} interactives");
                }
                catch (Exception e) { Plugin.Log.LogError($"[LootBind] synthetic registry FAILED: {e}"); }
            }
        }
    }
}
