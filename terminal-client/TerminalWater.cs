using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

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
        private const string SeaSurfaceShader = "FX/SimpleWater4";
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
                // The native hidden deferred pass is correct for the small pumping
                // station pool, but it relies on retail camera/GBuffer plumbing which
                // the custom location cannot supply for the huge offshore planes. In
                // practice those planes became a flat white/fog sheet. Keep the pump
                // on its authored pass and give the sea a supported forward water
                // material from EFT's own shader bundle.
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
                }

                if (dirtRenderer && show)
                {
                    RestorePumpDirtDecal(dirtRenderer);
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
                RestoreSeaSurface(seaRenderers, normal);

                var shader = FindShader(NativeWaterShader);
                if (!shader || !shader.isSupported)
                {
                    Plugin.Log.LogWarning($"[Water] pump shader '{NativeWaterShader}' unavailable; shoreline forward water remains active");
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
                _runtimeWaterRoot.transform.SetParent(seaRoot.parent, false);
                var water = _runtimeWaterRoot.AddComponent<WaterForSSR>();
                ConfigureRetailWater(water, shader, normal, foam, ripple, pumpFilters);
                var matrixDriver = _runtimeWaterRoot.AddComponent<TerminalWaterMatrixDriver>();
                matrixDriver.Water = water;
                _runtimeWaterRoot.SetActive(true);

                EnsureNativeRenderer(_runtimeWaterRoot.transform.parent);
                TerminalPerfWatch.WatchWater(renderers);
                Plugin.Log.LogInfo($"[Water] restored split water: sea={seaFilters.Count} forward '{SeaSurfaceShader}',"
                    + $" pump={pumpFilters.Count} deferred '{shader.name}', live drain matrices=yes,"
                    + $" normal='{normal.name}', foam='{foam.name}', ripple='{ripple.name}', dirtDecal={(dirtRenderer ? "yes" : "missing")}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Water] native restore failed: {e}");
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
            Texture2D foam,
            Texture2D ripple,
            List<MeshFilter> filters)
        {
            water.WaterShader = shader;
            water.RippleTexture = ripple;
            water.Normals = normal;
            water.NormalsDetails = normal;
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

        private static void RestoreSeaSurface(List<Renderer> renderers, Texture2D normal)
        {
            var shader = FindShader(SeaSurfaceShader);
            if (!shader || !shader.isSupported)
            {
                Plugin.Log.LogWarning($"[Water] shoreline shader '{SeaSurfaceShader}' unavailable; sea planes remain off instead of drawing the white placeholder");
                return;
            }

            var material = new Material(shader)
            {
                name = "Terminal_Shoreline_Sea_Water_Runtime",
                renderQueue = 2990,
            };
            SetTexture(material, "_BumpMap", normal);
            SetTexture(material, "_MainTex", Texture2D.blackTexture);
            SetTexture(material, "_ReflectionTex", Texture2D.grayTexture);
            SetColor(material, "_BaseColor", new Color(0.006f, 0.055f, 0.07f, 0.93f));
            SetColor(material, "_ReflectionColor", new Color(0.11f, 0.20f, 0.22f, 0.72f));
            SetColor(material, "_SpecularColor", new Color(0.55f, 0.66f, 0.69f, 1f));
            SetFloat(material, "_FresnelScale", 0.42f);
            SetFloat(material, "_Shininess", 110f);
            SetVector(material, "_DistortParams", new Vector4(0.35f, 0.45f, 3.5f, 0.18f));
            SetVector(material, "_InvFadeParemeter", new Vector4(0.15f, 0.35f, 0.12f, 1f));
            SetVector(material, "_AnimationTiling", new Vector4(2.2f, 2.2f, -1.1f, -1.1f));
            SetVector(material, "_AnimationDirection", new Vector4(1f, 0.35f, -0.55f, 0.8f));
            SetVector(material, "_BumpTiling", new Vector4(0.075f, 0.075f, 0.13f, 0.13f));
            SetVector(material, "_BumpDirection", new Vector4(1f, 0.25f, -0.4f, 0.75f));
            // These offshore planes are static; displacement at their enormous scale
            // creates horizon cracks. Normal-map motion provides the surface detail.
            SetFloat(material, "_GerstnerIntensity", 0f);

            int enabled = 0;
            foreach (var renderer in renderers)
            {
                var mesh = renderer as MeshRenderer;
                if (!mesh) continue;
                mesh.sharedMaterial = material;
                mesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mesh.receiveShadows = false;
                mesh.enabled = true;
                enabled++;
            }
            Plugin.Log.LogInfo($"[Water] shoreline forward surface enabled on {enabled} renderer(s) with '{shader.name}'");
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

    // WaterForSSR snapshots each plane's local-to-world matrix only during validate.
    // The retail pump animates the Water_station hierarchy after that snapshot, so the
    // collision moved while the rendered water stayed at its starting height. Refresh
    // the small pump-only list after animation each frame.
    internal sealed class TerminalWaterMatrixDriver : MonoBehaviour
    {
        internal WaterForSSR Water;

        private void LateUpdate()
        {
            try { if (Water) Water.method_2(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Water] live pump matrix refresh stopped: {e.Message}");
                enabled = false;
            }
        }
    }
}
