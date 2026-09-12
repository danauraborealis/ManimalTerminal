using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Comfort.Common;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // retail runs BSG's VolumetricLight (the Sandbox raymarcher) on 1051 of terminal's
    // lights (708 unique path keys — vs icebreaker's hand-picked 49; a port at night
    // is all beams). the component is a MonoBehaviour so it died with the il2cpp
    // script data on export; the LIGHTS survived, so we put back the component plus
    // its authored fields. 1.0 and 4.0 share the class layout byte-for-byte (verified
    // on icebreaker; terminal's carve passed the same plausibility checks).
    //
    // unlike icebreaker's 49-row inline table, terminal's 708 rows ship as a sidecar
    // (plugin-data/volumetric_lights.json, from extract_terminal_volumetric.py) —
    // loaded relative to the dll so the folder stays relocatable.
    internal static class TerminalVolumetricLights
    {
#pragma warning disable 0649 // fields are json-assigned
        private class Row
        {
            public string scene;
            public string path;
            public float range, spot;
            public int sample;
            public float scatter, extinction, skybox, mieG;
            public bool noise;
            public float noiseScale, noiseIntensity, noiseVel, maxRayLength;
        }
#pragma warning restore 0649

        private static readonly FieldInfo _cloVol = AccessTools.Field(typeof(CullingLightObject), "_volumetricLight");
        private static readonly FieldInfo _lampVol = AccessTools.Field(typeof(LampController), "list_2");

        private static List<Row> LoadRows()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
                var file = System.IO.Path.Combine(dir, "plugin-data", "volumetric_lights.json");
                if (!System.IO.File.Exists(file))
                {
                    Plugin.Log.LogWarning($"[Volumetric] sidecar missing: {file} — no beams restored");
                    return null;
                }
                return JsonConvert.DeserializeObject<List<Row>>(System.IO.File.ReadAllText(file));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Volumetric] sidecar unreadable: {e.Message}");
                return null;
            }
        }

        internal static void Restore()
        {
            if (!TerminalGate.On) return;
            // the component destroys itself in Awake when the graphics setting is off —
            // adding hundreds of them would just be churn
            if (!SettingOn())
            {
                Plugin.Log.LogInfo("[Volumetric] graphics setting off — skipping restore");
                return;
            }
            var rows = LoadRows();
            if (rows == null || rows.Count == 0) return;

            // index every terminal light by hierarchy path in one sweep — cheaper than
            // resolving 708 paths by name-walk, and FindObjectsOfTypeAll catches lamps
            // that are still inactive (component lands now, Awake fires when the lamp
            // comes up)
            var byPath = new Dictionary<string, List<Light>>();
            foreach (var l in Resources.FindObjectsOfTypeAll<Light>())
            {
                if (l == null) continue;
                var sc = l.gameObject.scene;
                if (!sc.IsValid() || sc.name == null || !sc.name.StartsWith("Terminal")) continue;
                var p = PathOf(l.transform);
                if (!byPath.TryGetValue(p, out var bucket)) byPath[p] = bucket = new List<Light>();
                bucket.Add(l);
            }

            int added = 0, already = 0, unmatched = 0, deferred = 0;
            foreach (var e in rows)
            {
                // rows from a cutscene scene only exist while that scene is loaded —
                // the intro driver calls back in once its up (we're idempotent)
                if (e.scene != null && e.scene.Contains("PortCutscene")
                    && !SceneManager.GetSceneByName(e.scene).isLoaded) { deferred++; continue; }
                if (!byPath.TryGetValue(e.path, out var cands)) { unmatched++; continue; }
                bool hit = false;
                foreach (var l in cands)
                {
                    // range+spot disambiguate same-named siblings sharing a path
                    if (Mathf.Abs(l.range - e.range) > 0.01f) continue;
                    if (Mathf.Abs(l.spotAngle - e.spot) > 0.05f) continue;
                    hit = true;
                    if (l.GetComponent<VolumetricLight>() != null) { already++; continue; }
                    if (Attach(l, e)) added++;
                }
                if (!hit) unmatched++;
            }

            if (unmatched > 0)
                Plugin.Log.LogWarning($"[Volumetric] {unmatched}/{rows.Count} entries matched no light — scene drift, report this");
            Plugin.Log.LogInfo($"[Volumetric] {added} volumetric lights restored" +
                               (already > 0 ? $" ({already} already had one)" : "") +
                               (deferred > 0 ? $", {deferred} deferred to cutscene load" : ""));
        }

        private static bool Attach(Light light, Row e)
        {
            var go = light.gameObject;
            // Awake bakes the fields into the material the instant AddComponent lands —
            // set them after and you're tuning a material that's already built. build
            // inactive, populate, let activation run Awake once with the real values.
            bool wasActive = go.activeSelf;
            try
            {
                if (wasActive) go.SetActive(false);
                var vl = go.AddComponent<VolumetricLight>();
                // terminal's data varies fields icebreaker's never did (sample 8/10,
                // extinction 0.01/0.0119) — set those too. maxRayLength 0 rows keep
                // the class default (an authored 0 would kill the ray).
                vl.SampleCount = e.sample;
                vl.ScatteringCoef = e.scatter;
                vl.ExtinctionCoef = e.extinction;
                vl.MieG = e.mieG;
                vl.Noise = e.noise;
                vl.NoiseScale = e.noiseScale;
                vl.NoiseIntensity = e.noiseIntensity;
                vl.NoiseVelocity = new Vector2(e.noiseVel, e.noiseVel);
                if (e.maxRayLength > 0f) vl.MaxRayLength = e.maxRayLength;
                Rebind(light, vl);
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Volumetric] attach failed on {e.path}: {ex.Message}");
                return false;
            }
            finally { if (wasActive) go.SetActive(true); }
        }

        // both native consumers cache their VolumetricLight once at init and we always
        // arrive after them — without this the beam ignores everything that drives it:
        // CullingLightObject re-checks intensity on distance fade, LampController
        // toggles the beam with the lamp. an unregistered beam keeps burning from a
        // lamp that's been switched off.
        private static void Rebind(Light light, VolumetricLight vl)
        {
            var clo = light.GetComponent<CullingLightObject>();
            if (clo != null) _cloVol?.SetValue(clo, vl);

            var lamp = light.GetComponentInParent<LampController>();
            if (lamp == null || _lampVol == null) return;
            // only claim the lamp that actually owns this light — GetComponentInParent
            // walks past the immediate rig on nested lamp hierarchies
            bool owns = false;
            var lights = lamp.Lights;
            if (lights != null)
                foreach (var l in lights) if (l == light) { owns = true; break; }
            if (!owns) return;
            if (_lampVol.GetValue(lamp) is List<VolumetricLight> list && !list.Contains(vl))
            {
                list.Add(vl);
                // the lamp mirrors its state onto light.enabled — a free read of
                // whether this beam should be live right now
                vl.enabled = light.enabled;
            }
        }

        private static bool SettingOn()
        {
            try
            {
                var s = Singleton<EFT.Settings.SettingsManager>.Instance;
                return s == null || s.Graphics.Settings.VolumetricLight.Value;
            }
            catch { return true; }
        }

        private static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
    }
}
