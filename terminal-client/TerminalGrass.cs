using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using EFT;
using GPUInstancer;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // Rebuilds the tiny retail Terminal_Grass scene at runtime. AssetRipper kept the
    // hierarchy but lost every serialized GPU Instancer field; the original PCL
    // placement data survived in StreamingAssets and is still the source of truth.
    internal static class TerminalGrass
    {
        private const string BundleFile = "terminal_grass.bundle";
        private const string PlacementFile = "TerminalGrassPrefabs.pcl";
        private const string FoliageShader = "GPUInstancer/Foliage_Simple";
        private const string VariationBuffer = "_ArrayIndexBuffer";

        private static AssetBundle _bundle;
        private static GameObject _host;
        private static GPUInstancerPrefabPrototype _prototype;

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_RestoreAtRaidStart
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On || !Plugin.GrassEnabled.Value) return;
                var runner = new GameObject("Manimal_TerminalGrassLoader");
                runner.AddComponent<Loader>();
            }
        }

        private sealed class Loader : MonoBehaviour
        {
            private IEnumerator Start()
            {
                yield return null;
                Restore();
                Destroy(gameObject);
            }
        }

        private static void Restore()
        {
            try
            {
                if (_host != null)
                {
                    Plugin.Log.LogDebug("[Grass] retail grass is already active");
                    return;
                }

                string pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                string bundlePath = Path.Combine(pluginDir, BundleFile);
                string placementPath = Path.Combine(Application.streamingAssetsPath, "Grass", PlacementFile);
                if (!File.Exists(bundlePath))
                {
                    Plugin.Log.LogWarning($"[Grass] {BundleFile} is missing beside the client DLL; grass remains disabled");
                    return;
                }
                if (!File.Exists(placementPath))
                {
                    Plugin.Log.LogWarning($"[Grass] retail placement file is missing: {placementPath}");
                    return;
                }

                // Prefer the game's already-loaded original materials. They carry two
                // auxiliary noise textures that AssetRipper did not export. The grass
                // bundle's materials remain a complete fallback for mesh/array data.
                Shader gameShader = FindGameShader(FoliageShader);
                Material nativeMain = FindNativeMaterial("Foliage_Simple", gameShader);
                Material nativeBillboard = FindNativeMaterial("Foliage_Simple_Billboard", gameShader);

                if (_bundle == null) _bundle = AssetBundle.LoadFromFile(bundlePath);
                if (_bundle == null) throw new InvalidOperationException("Unity could not load the grass asset bundle");

                GameObject source = null;
                GameObject[] assets = _bundle.LoadAllAssets<GameObject>();
                for (int i = 0; i < assets.Length; i++)
                {
                    if (assets[i] != null && assets[i].name == "crossGrass_clean")
                    {
                        source = assets[i];
                        break;
                    }
                }
                if (source == null) throw new InvalidOperationException("crossGrass_clean is missing from the grass bundle");

                var template = UnityEngine.Object.Instantiate(source);
                template.name = "crossGrass_TerminalRetailTemplate";
                RebindTemplateMaterials(template, gameShader, nativeMain, nativeBillboard);
                // GPUI's prototype generator uses GetComponentsInChildren without
                // includeInactive. Keep the hierarchy active for discovery, but turn
                // off its ordinary renderers so no template clump appears in-world.
                Renderer[] templateRenderers = template.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < templateRenderers.Length; i++) templateRenderers[i].enabled = false;

                _prototype = BuildPrototype(template);
                var prefab = template.GetComponent<GPUInstancerPrefab>();
                if (prefab == null) prefab = template.AddComponent<GPUInstancerPrefab>();
                prefab.prefabPrototype = _prototype;

                _host = new GameObject("Manimal_RestoredTerminalGrass");
                _host.SetActive(false);
                MoveIntoGrassScene(_host);

                GPUInstancerPrefabManager optic = AddManager(_host, template, true);
                GPUInstancerPrefabManager world = AddManager(_host, template, false);
                _host.SetActive(true); // manager Awake/OnEnable builds native runtime data

                byte[] bytes = File.ReadAllBytes(placementPath);
                int stride = Marshal.SizeOf(typeof(GStruct116));
                if (bytes.Length == 0 || bytes.Length % stride != 0)
                    throw new InvalidDataException($"PCL length {bytes.Length} is not divisible by native stride {stride}");

                int count = bytes.Length / stride;
                var overlay = new GStruct115 { bytes = bytes };
                var matrices = new Matrix4x4[count];
                var variants = new int[count];
                for (int i = 0; i < count; i++)
                {
                    matrices[i] = overlay.prefabs[i].matrix;
                    variants[i] = overlay.prefabs[i].arrayIndex;
                }

                _prototype.enableRuntimeModifications = true;
                InitializeManager(optic, _prototype, matrices, variants);
                InitializeManager(world, _prototype, matrices, variants);

                // Move the now-discovered template into the grass scene so normal
                // scene teardown owns it. Renderers remain disabled; GPUI holds the
                // mesh/material runtime data used by the indirect draws.
                template.transform.SetParent(_host.transform, false);

                Plugin.Log.LogInfo($"[Grass] restored {count:N0} retail placements (world + optic managers, max distance 150m)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Grass] restoration failed: {e}");
                if (_host != null) UnityEngine.Object.Destroy(_host);
                _host = null;
            }
        }

        private static GPUInstancerPrefabPrototype BuildPrototype(GameObject template)
        {
            var p = ScriptableObject.CreateInstance<GPUInstancerPrefabPrototype>();
            p.name = "crossGrass_65406_TerminalRetail";
            p.prefabObject = template;
            p.isShadowCasting = true;
            p.useCustomShadowDistance = true;
            p.shadowDistance = 25f;
            p.shadowLODMap = new[] { 0f, 4f, 0f, 0f, 9f, 5f, 0f, 0f, 2f, 6f, 0f, 0f, 3f, 7f, 0f, 0f };
            p.useOriginalShaderForShadow = true;
            p.cullShadows = true;
            p.minDistance = 0f;
            p.maxDistance = 150f;
            p.minDistanceOptic = 0f;
            p.maxDistanceOptic = 500f;
            p.isFrustumCulling = true;
            p.isOcclusionCulling = true;
            p.minCullingDistance = 0f;
            p.occlusionOffset = 0f;
            p.occlusionAccuracy = 1;
            p.boundsOffset = Vector3.zero;
            p.isLODCrossFade = false;
            p.isLODCrossFadeAnimate = false;
            p.lodFadeTransitionWidth = 0.1f;
            p.lodBiasAdjustment = 1f;
            p.billboard = new GPUInstancerBillboard();
            p.isBillboardDisabled = false;
            p.useGeneratedBillboard = false;
            p.checkedForBillboardExtensions = true;
            p.autoUpdateTransformData = false;
            p.treeType = GPUInstancerTreeType.None;
            p.enableRuntimeModifications = true;
            p.startWithRigidBody = false;
            p.addRemoveInstancesAtRuntime = false;
            p.extraBufferSize = 0;
            p.addRuntimeHandlerScript = false;
            p.hasRigidBody = false;
            p.meshRenderersDisabled = false;
            p.isTransformsSerialized = false;
            return p;
        }

        private static GPUInstancerPrefabManager AddManager(GameObject host, GameObject template, bool optic)
        {
            var manager = host.AddComponent<GPUInstancerPrefabManager>();
            manager.IsOptic = optic;
            manager.prototypeList = new List<GPUInstancerPrototype> { _prototype };
            manager.prefabList = new List<GameObject> { template };
            manager.instancingBounds = new Bounds(Vector3.zero, new Vector3(10000f, 10000f, 10000f));
            manager.isFrustumCulling = true;
            manager.isOcclusionCulling = true;
            manager.minCullingDistance = 0f;
            manager.maxThreads = 3;
            manager.layerMask = -1;
            manager.enableMROnManagerDisable = true;
            manager.enableMROnRemoveInstance = true;
            manager.IsActive = true;
            return manager;
        }

        private static void InitializeManager(GPUInstancerPrefabManager manager,
            GPUInstancerPrefabPrototype prototype, Matrix4x4[] matrices, int[] variants)
        {
            manager.SetColorBuffers(
                new List<Color> { new Color(1f, 0.9411765f, 0.0784314f, 0f), new Color(0.4745098f, 0.5607843f, 0.35686275f, 0f) },
                new List<Color> { new Color(1f, 0.6156863f, 0.20000002f, 0f), Color.white });
            GClass1257.InitializeWithMatrix4x4Array(manager, prototype, matrices);
            GClass1257.SetInstanceCount(manager, prototype, matrices.Length);
            GClass1257.DefineAndAddVariationFromArray(manager, prototype, VariationBuffer, variants);
        }

        private static void MoveIntoGrassScene(GameObject host)
        {
            Scene grassScene = SceneManager.GetSceneByName("Terminal_Grass");
            if (grassScene.IsValid() && grassScene.isLoaded)
            {
                SceneManager.MoveGameObjectToScene(host, grassScene);
                return;
            }
            Plugin.Log.LogWarning("[Grass] Terminal_Grass scene was not found; manager attached to the active raid scene");
        }

        private static void RebindTemplateMaterials(GameObject template, Shader gameShader,
            Material nativeMain, Material nativeBillboard)
        {
            if (gameShader == null) throw new InvalidOperationException($"game shader '{FoliageShader}' is unavailable");
            Renderer[] renderers = template.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                Material[] materials = renderers[r].sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material old = materials[i];
                    if (old == null) continue;
                    Material native = old.name.IndexOf("Billboard", StringComparison.OrdinalIgnoreCase) >= 0
                        ? nativeBillboard : nativeMain;
                    if (native != null)
                    {
                        materials[i] = native;
                    }
                    else
                    {
                        var clone = new Material(old) { name = old.name + "_TerminalRuntime", shader = gameShader };
                        materials[i] = clone;
                    }
                }
                renderers[r].sharedMaterials = materials;
            }
        }

        private static Shader FindGameShader(string name)
        {
            Shader shader = Shader.Find(name);
            if (shader == null)
            {
                try { shader = GClass872.Find(name); }
                catch { }
            }
            return shader;
        }

        private static Material FindNativeMaterial(string name, Shader shader)
        {
            if (shader == null) return null;
            Material[] all = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == name && all[i].shader == shader) return all[i];
            }
            return null;
        }
    }
}
