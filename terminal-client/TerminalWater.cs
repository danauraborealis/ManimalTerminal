using System;
using System.Collections.Generic;
using System.IO;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // Terminal's retail water is not ordinary MeshRenderer water. The fifteen
    // renderers in Terminal_Design_Main are merely geometry/culling handles; an
    // EFT.Water.WaterContainer handed their MeshFilters to the native deferred
    // "Draw Water" command buffer. AssetRipper lost that component and assigned
    // placeholder materials, which made the sea look like transparent fog.
    //
    // SPT 4 retains the same pipeline as WaterForSSR + WaterRenderer. Rebuild it
    // here with the values recovered from retail level629, and keep the pumping
    // station's dirt decal as a separate renderer pass over the water.
    internal static class TerminalWater
    {
        private const string NativeWaterShader = "Hidden/WaterForSSR";
        private const string DirtDecalShader = "Decal/Ultra Deferred Decal Of God 3000";

        private static bool _staged;
        private static GameObject _runtimeWaterRoot;
        private static GameObject _runtimeRendererRoot;

        internal static void ResetForRaid()
        {
            _staged = false;
            _runtimeWaterRoot = null;
            _runtimeRendererRoot = null;
        }

        internal static void TryStage()
        {
            if (_staged || !TerminalGate.On) return;
            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (gw == null) return;
            var seaRoot = TerminalRigFill.FindRootNamed("Water_planes_withoutculling");
            if (!seaRoot) return;
            _staged = true;

            try
            {
                // Both water systems use the game's deferred water renderer. Keep
                // their material inputs separate: preserve the tested pump treatment
                // while restoring the sea's actual sharedassets texture references.
                var seaFilters = new List<MeshFilter>(9);
                var seaRenderers = new List<Renderer>(9);
                var pumpFilters = new List<MeshFilter>(6);
                var pumpRenderers = new List<Renderer>(6);
                CollectWaterGeometry(seaRoot, seaFilters, seaRenderers);
                CollectWaterGeometry(TerminalRigFill.FindRootNamed("Pamp_Station"), pumpFilters, pumpRenderers);
                CollectWaterGeometry(TerminalRigFill.FindRootNamed("Pumping_station_water_dirt"), pumpFilters, pumpRenderers);

                var renderers = new List<Renderer>(seaRenderers.Count + pumpRenderers.Count);
                renderers.AddRange(seaRenderers);
                renderers.AddRange(pumpRenderers);

                // Renderer placeholders must not draw on top of the deferred water.
                // The dirt plane is the one exception: it is an authored decal pass.
                bool show = Plugin.WaterPlanes.Value;
                MeshRenderer dirtRenderer = null;
                for (int i = 0; i < renderers.Count; i++)
                {
                    var r = renderers[i] as MeshRenderer;
                    if (!r) continue;
                    if (r.name == "Pumping_station_water_dirt_LOD0") dirtRenderer = r;
                    r.enabled = false;
                    r.forceRenderingOff = true;
                }

                if (dirtRenderer && show)
                {
                    RestorePumpDirtDecal(dirtRenderer);
                    dirtRenderer.forceRenderingOff = false;
                    dirtRenderer.enabled = true;
                }

                if (!show)
                {
                    TerminalPerfWatch.WatchWater(renderers);
                    Plugin.Log.LogInfo($"[Water] water disabled by WaterPlanes config ({seaFilters.Count + pumpFilters.Count} plane(s) staged off)");
                    return;
                }

                var normal = FindTexture("Lighthouse_Swamp_Water_NM");
                if (!normal) normal = Texture2D.normalTexture;

                var shader = FindShader(NativeWaterShader);
                if (!shader || !shader.isSupported)
                {
                    Plugin.Log.LogWarning($"[Water] native shader '{NativeWaterShader}' unavailable; water surfaces remain hidden");
                    return;
                }

                var foam = FindTexture("Lighthouse_Swamp_Water") ?? FindTexture("perlin_noise");
                var ripple = FindTexture("WaterFlowsOld") ?? FindTexture("perlin_noise") ?? foam;
                if (!foam) foam = Texture2D.whiteTexture;
                if (!ripple) ripple = Texture2D.grayTexture;

                // AddComponent invokes OnEnable immediately on an active object;
                // WaterForSSR creates its Material there, so configure it inactive.
                _runtimeWaterRoot = new GameObject("Terminal_RuntimeWaterSSR");
                _runtimeWaterRoot.SetActive(false);
                SceneManager.MoveGameObjectToScene(_runtimeWaterRoot, seaRoot.gameObject.scene);
                _runtimeWaterRoot.transform.SetParent(seaRoot.parent, false);
                var assets = _runtimeWaterRoot.AddComponent<TerminalWaterAssets>();
                _runtimeWaterRoot.AddComponent<TerminalWaterReflectionDriver>();
                // Resolved against retail level629's WaterContainer PPtrs and the
                // sharedassets path-ID catalog, not guessed from texture names.
                var seaNormal = LoadWaterTexture(assets, "Static Voronoi - Tileable - NM2", true);
                var seaDetails = LoadWaterTexture(assets, "detailedWavesMap hd2", true);
                var seaFoam = LoadWaterTexture(assets, "Foam 01", false);
                var seaRipple = LoadWaterTexture(assets, "circles", false);
                if (seaNormal && seaDetails && seaFoam && seaRipple)
                    CreateContainer("Sea", shader, seaNormal, seaDetails, seaFoam, seaRipple, seaFilters);
                else
                    Plugin.Log.LogError("[Water] recovered sea textures incomplete; check plugin-data/water deployment");
                CreateContainer("Pump", shader, normal, normal, foam, ripple, pumpFilters);
                _runtimeWaterRoot.SetActive(true);

                EnsureNativeRenderer(_runtimeWaterRoot.transform);
                TerminalPerfWatch.WatchWater(renderers);
                Plugin.Log.LogInfo($"[Water] restored native water: sea={seaFilters.Count} +"
                    + $" pump={pumpFilters.Count} deferred '{shader.name}', per-plane matrices=yes,"
                    + $" seaNormal='{seaNormal?.name}', detail='{seaDetails?.name}', foam='{seaFoam?.name}',"
                    + $" ripple='{seaRipple?.name}', pump material preserved, dirtDecal={(dirtRenderer ? "yes" : "missing")}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Water] native restore failed: {e}");
            }
        }

        private static void CreateContainer(string label, Shader shader, Texture2D normal,
            Texture2D details, Texture2D foam, Texture2D ripple, List<MeshFilter> filters)
        {
            if (filters.Count == 0) return;
            var child = new GameObject("Terminal_Water_" + label);
            child.transform.SetParent(_runtimeWaterRoot.transform, false);
            var driver = child.AddComponent<TerminalWaterMatrixDriver>();
            var water = child.AddComponent<WaterForSSR>();
            ConfigureRetailWater(water, shader, normal, details, foam, ripple, filters);
            driver.Water = water;
        }

        private static Texture2D LoadWaterTexture(TerminalWaterAssets assets, string name, bool normalMap)
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                "plugin-data", "water", name + ".png");
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, normalMap)
            {
                name = "Terminal_" + name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4
            };
            assets.Textures.Add(texture);
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false)) return null;
            if (normalMap)
            {
                // AssetRipper exports normal maps as conventional RGB normals.
                // Runtime PNG loading bypasses Unity's normal-map importer; pack
                // x in alpha/y in green for the desktop water shader's DXT5nm path.
                var pixels = texture.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(255, pixels[i].g, 255, pixels[i].r);
                texture.SetPixels32(pixels);
            }
            texture.Apply(true, true);
            return texture;
        }

        [HarmonyPatch(typeof(WaterForSSR), nameof(WaterForSSR.InitWaterMatrices))]
        internal static class Patch_PerPlaneMatrices
        {
            private static bool Prefix(WaterForSSR __instance, ref List<WaterForSSR.WaterObject> ____waterHolder)
            {
                // Only our containers opt in. Native maps keep their original path.
                if (!__instance.GetComponent<TerminalWaterMatrixDriver>()) return true;
                var planes = __instance.WaterPlanes;
                if (____waterHolder == null) ____waterHolder = new List<WaterForSSR.WaterObject>(planes.Length);
                int draw = 0;
                foreach (var plane in planes)
                {
                    if (!plane || !plane.sharedMesh) continue;
                    if (draw == ____waterHolder.Count)
                        ____waterHolder.Add(new WaterForSSR.WaterObject(plane.sharedMesh, plane.transform.localToWorldMatrix));
                    else
                    {
                        // Native method_2 uses Find(sharedMesh) and collapses repeated
                        // tiles onto the first entry. Preserve one entry PER FILTER.
                        ____waterHolder[draw].Mesh = plane.sharedMesh;
                        ____waterHolder[draw].Matrix = plane.transform.localToWorldMatrix;
                    }
                    draw++;
                }
                if (draw < ____waterHolder.Count) ____waterHolder.RemoveRange(draw, ____waterHolder.Count - draw);
                return false;
            }
        }

        private static void CollectWaterGeometry(Transform root, List<MeshFilter> filters, List<Renderer> renderers)
        {
            if (!root) return;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter || filter.name.IndexOf("BALLISTIC", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!filter.sharedMesh || filters.Contains(filter)) continue;
                filters.Add(filter);
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer && !renderers.Contains(renderer)) renderers.Add(renderer);
            }
        }

        private static void ConfigureRetailWater(
            WaterForSSR water,
            Shader shader,
            Texture2D normal,
            Texture2D details,
            Texture2D foam,
            Texture2D ripple,
            List<MeshFilter> filters)
        {
            water.WaterShader = shader;
            water.RippleTexture = ripple;
            water.Normals = normal;
            water.NormalsDetails = details;
            water.NormalsDetailsMipMapBias = -2f;
            water.Foam = foam;

            water.NormalsA = Blend(0.071f, 7.9f, -5.3f, 0.36708263f);
            water.NormalsB = Blend(0.0973f, -6.7f, 4.7f, 0.36067685f);
            water.NormalsDetails0 = Blend(0.5f, -2.3f, 1.7f, 0.27224058f);
            water.FoamA = Blend(0.3f, -2f, 2f, 0.51104039f);
            water.FoamB = Blend(1f, 2f, -2f, 0.48895958f);

            water.BorderFade = 2f;
            water.BorderFadeDistStart = 5f;
            water.BorderFadeDistRange = 66f;
            water.DepthFade = 2f;
            water.DepthColorFade = 0.09f;
            water.DepthRefractions = 0.1f;
            water.DepthColorDeep = new Color(0f, 0.13626835f, 0.14150941f, 1f);
            water.DepthColorShallow = new Color(0.25062302f, 0.31132078f, 0.17475082f, 1f);
            water.Bumpiness = 0.5f;
            water.RippleScale = 11f;
            water.RippleBumpness = 0.25f;
            water.FoamSize = 0.12f;
            water.FoamIntensity = 7f;
            water.FoamColor = new Color(1f, 1f, 1f, 0f);
            water.FresnelIntensity = 0.44f;
            water.FresnelPower = 5f;
            water.AdditionalCubemapReflectionMinWetting = 0.05f;
            water.AdditionalCubemapReflectionMaxWetting = 0.35f;
            water.ReflectionWettingFunc = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
            water.ReflectionColor = Color.white;
            water.DiffuseColor = new Color(0f, 0f, 0f, 1f);
            water.WaterPlanes = filters.ToArray();
        }

        private static WaterForSSR.TextureBlendSetting Blend(float scale, float x, float y, float blend)
        {
            return new WaterForSSR.TextureBlendSetting
            {
                Scale = scale,
                MovementDirection = new Vector2(x, y),
                Blend = blend
            };
        }

        private static void EnsureNativeRenderer(Transform parent)
        {
            WaterRenderer renderer = null;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<WaterRenderer>())
            {
                if (!candidate || !candidate.gameObject.scene.IsValid()) continue;
                renderer = candidate;
                break;
            }

            if (renderer)
            {
                // Enabling it performs a complete WaterForSSR inventory. An already
                // active renderer received this container through WaterForSSR.OnAdd.
                if (!renderer.enabled) renderer.enabled = true;
                Plugin.Log.LogInfo($"[Water] using native WaterRenderer '{renderer.name}'");
                return;
            }

            _runtimeRendererRoot = new GameObject("Terminal_RuntimeWaterRenderer");
            _runtimeRendererRoot.SetActive(false);
            _runtimeRendererRoot.transform.SetParent(parent, false);
            _runtimeRendererRoot.AddComponent<WaterRenderer>();
            _runtimeRendererRoot.SetActive(true); // OnEnable inventories the container
            Plugin.Log.LogInfo("[Water] created native WaterRenderer ('Draw Water')");
        }

        private static void RestorePumpDirtDecal(MeshRenderer renderer)
        {
            var shader = FindShader(DirtDecalShader);
            if (!shader || !shader.isSupported)
            {
                Plugin.Log.LogWarning($"[Water] pump dirt decal shader '{DirtDecalShader}' unavailable");
                return;
            }

            var material = new Material(shader)
            {
                name = "Terminal_City_Decal_Dirt_Large_green",
                renderQueue = 2005
            };
            var main = FindTexture("City_Dirt_Decal_Large_white_D");
            var bump = FindTexture("Reserve_Dirt_Decal_Largel_N");
            if (main && material.HasProperty("_MainTex")) material.SetTexture("_MainTex", main);
            if (bump && material.HasProperty("_BumpMap")) material.SetTexture("_BumpMap", bump);

            SetFloat(material, "_AlphaMultiplier", 1.39f);
            SetFloat(material, "_BumpScale", 1f);
            SetFloat(material, "_Cutoff", 0.5f);
            SetFloat(material, "_NormalIntensity", 0.631f);
            SetFloat(material, "_NormalPower", 0.52f);
            SetFloat(material, "_SpecSmoothness", 0f);
            SetFloat(material, "_ZWrite", 1f);
            SetColor(material, "_Color", new Color(0.766578f, 0.803922f, 0.225098f, 0.647059f));
            SetColor(material, "_SpecColor", new Color(1f, 1f, 1f, 0f));
            SetColor(material, "_SpecularColor", new Color(0.189f, 0.189f, 0.189f, 1f));
            SetColor(material, "_Temperature", new Color(0.1f, 0.12f, 0.28f, 0f));
            renderer.sharedMaterial = material;
            Plugin.Log.LogInfo($"[Water] pump dirt decal restored on '{shader.name}'"
                + $" (main='{(main ? main.name : "missing")}', bump='{(bump ? bump.name : "missing")}')");
        }

        private static Shader FindShader(string exactName)
        {
            var shader = Shader.Find(exactName);
            if (shader) return shader;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<Shader>())
                if (candidate && candidate.name == exactName) return candidate;
            return null;
        }

        private static Texture2D FindTexture(string exactName)
        {
            foreach (var texture in Resources.FindObjectsOfTypeAll<Texture2D>())
                if (texture && texture.name == exactName) return texture;
            return null;
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        private static void SetColor(Material material, string property, Color value)
        {
            if (material.HasProperty(property)) material.SetColor(property, value);
        }

        private static void SetTexture(Material material, string property, Texture value)
        {
            if (material.HasProperty(property)) material.SetTexture(property, value);
        }

        private static void SetVector(Material material, string property, Vector4 value)
        {
            if (material.HasProperty(property)) material.SetVector(property, value);
        }
    }

    // Terminal keeps AmbientLight disabled to omit its screen ambient/stencil
    // passes. Native LateUpdate also owns _MyGlobalReflectionProbe, however;
    // SetSH alone does not create or render it. Drive only that native capture
    // after weather has published the current raid's SH, never the screen passes.
    [DefaultExecutionOrder(10000)]
    internal sealed class TerminalWaterReflectionDriver : MonoBehaviour
    {
        private static readonly int ProbeId = Shader.PropertyToID("_MyGlobalReflectionProbe");
        private static readonly System.Reflection.FieldInfo CubeField = AccessTools.Field(typeof(AmbientLight), "_cubeRT");
        private static readonly System.Reflection.FieldInfo FaceField = AccessTools.Field(typeof(AmbientLight), "_faceNum");
        private static readonly System.Reflection.FieldInfo NextCaptureField = AccessTools.Field(typeof(AmbientLight), "_nextRenderTime");
        private AmbientLight _source;
        private RenderTexture _cube;
        private Texture _previousProbe;
        private bool _ownsCube;

        private void LateUpdate()
        {
            if (!TerminalGate.On) { enabled = false; return; }
            if (!TerminalWeather.Staged) return;
            var source = EFT.Weather.WeatherController.Instance?.TimeOfDayController?.AmbientLightScript;
            // Native SetSH initializes the component even while it is disabled.
            // If another owner enables it, its own LateUpdate handles capture.
            if (!source || !source.IsInitialized) return;
            if (source.isActiveAndEnabled)
            {
                if (_source == source)
                {
                    // Ownership passed back to the native component. Its own
                    // OnDisable will release the target; do not dispose it later.
                    _ownsCube = false;
                    _source = null;
                    _cube = null;
                    _previousProbe = null;
                }
                return;
            }
            try
            {
                if (_source != source)
                {
                    ReleaseCapture();
                    if (CubeField == null || FaceField == null || NextCaptureField == null)
                        throw new MissingFieldException("AmbientLight reflection capture layout changed");
                    _source = source;
                    _previousProbe = Shader.GetGlobalTexture(ProbeId);
                    _cube = CubeField.GetValue(source) as RenderTexture;
                    _ownsCube = !_cube;
                    if (_ownsCube)
                    {
                        // A recreated target must initialize ALL faces. Native's
                        // face cursor otherwise survives a disable/re-enable cycle.
                        FaceField.SetValue(source, -1);
                        NextCaptureField.SetValue(source, 0f);
                    }
                    source.method_2();
                    _cube = CubeField.GetValue(source) as RenderTexture;
                    if (!_cube || !_cube.IsCreated())
                        throw new InvalidOperationException("native reflection cubemap was not created");
                    Plugin.Log.LogInfo($"[Water] reflection capture ready: source='{source.name}' "
                        + $"cube={(_cube ? _cube.width : 0)}px created={(_cube && _cube.IsCreated())} "
                        + $"faceInterval={source.RenderDelay:F2}s screenAmbientEnabled={source.enabled} "
                        + $"previousProbe='{(_previousProbe ? _previousProbe.name : "missing")}'");
                    return;
                }
                // This method applies the authored RenderDelay itself: Terminal's
                // sidecar is 128px, one face per second after the first full cube.
                source.method_2();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Water] reflection capture stopped: {e.Message}");
                enabled = false;
            }
        }

        private void OnDisable() => ReleaseCapture();
        private void OnDestroy() => ReleaseCapture();

        private void ReleaseCapture()
        {
            // Do not overwrite a probe installed by a newer scene or another owner.
            // Read the field on failure too: method_2 may allocate before throwing.
            if (!_cube && _source && _ownsCube)
                _cube = CubeField?.GetValue(_source) as RenderTexture;
            if (_cube && Shader.GetGlobalTexture(ProbeId) == _cube)
                Shader.SetGlobalTexture(ProbeId, _previousProbe ? _previousProbe : null);
            if (_ownsCube && _cube)
            {
                if (_source)
                {
                    var camera = _source.GetComponent<Camera>();
                    if (camera && camera.targetTexture == _cube) camera.targetTexture = null;
                    if (CubeField.GetValue(_source) as RenderTexture == _cube)
                        CubeField.SetValue(_source, null);
                }
                _cube.Release();
                Destroy(_cube);
            }
            _source = null;
            _cube = null;
            _previousProbe = null;
            _ownsCube = false;
        }
    }

    // Refresh our small per-filter lists after pump animation, without native's
    // shared-mesh lookup (several sea tiles intentionally share a single mesh).
    [DefaultExecutionOrder(10000)]
    internal sealed class TerminalWaterMatrixDriver : MonoBehaviour
    {
        internal WaterForSSR Water;

        private void LateUpdate()
        {
            try { if (Water) Water.InitWaterMatrices(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Water] per-plane matrix refresh stopped: {e.Message}");
                enabled = false;
            }
        }

        private void OnDestroy()
        {
            // WaterForSSR.OnDestroy unregisters but does not dispose its material.
            if (Water && AccessTools.Field(typeof(WaterForSSR), "_waterMaterial")?.GetValue(Water) is Material material)
                Destroy(material);
        }
    }

    internal sealed class TerminalWaterAssets : MonoBehaviour
    {
        internal readonly List<Texture2D> Textures = new List<Texture2D>();

        private void OnDestroy()
        {
            foreach (var texture in Textures)
                if (texture) Destroy(texture);
            Textures.Clear();
        }
    }
}
