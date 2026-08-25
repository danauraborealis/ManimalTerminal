using System;
using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // WEATHER STACK RESURRECTION, take 2 — the first attempt was a scalar-only filler
    // and it face-planted exactly where icebreaker's port predicted: WeatherController's
    // serialized OBJECT fields (WeatherDebug), component cross-refs (CloudController /
    // TimeOfDayController / RainController), AnimationCurves and external assets all
    // stayed null. result: Awake threw on WeatherDebug.SavePreset, the controller died
    // half-born, and the shader-less renderers spammed 7248 null-shader Material NREs
    // in one raid. this is the PROVEN icebreaker machinery instead, param-ported:
    // pass 1 create (deferred deps last, get-or-add), pass 2 FillFields with full ref
    // resolution (path_id -> rebuilt component, scene_objects table, external assets by
    // recovered name, settings ScriptableObjects rebuilt from values), pass 3 wake.
    //
    // terminal deviations from icebreaker, on purpose:
    //  - TOD_* rows are SKIPPED — TerminalSceneScrub already healed the live sky and
    //    TOD_Time is deliberately disabled (TerminalTimeWeather owns the clock); a
    //    second filler racing that would re-break what finally works.
    //  - snow/winter rows are SKIPPED — terminal is a rain map; staging SnowFlakes only
    //    buys missing-texture warnings.
    //  - a render-asset guard disables any staged component whose Shader fields (or
    //    RainFallDrops' _close material) didn't resolve, BEFORE its Awake runs — one
    //    honest log line instead of a per-frame NRE firehose.
    internal static class TerminalWeather
    {
        private static JObject _sidecar;
        private static bool _sidecarTried;
        private static GameObject _marker; // liveness — dies with the raid scenes
        private static bool _rainVisualReady;

        private static readonly Dictionary<long, Component> _comps = new Dictionary<long, Component>();
        private static readonly Dictionary<string, Transform> _pathIndex = new Dictionary<string, Transform>();
        private static readonly Dictionary<string, UnityEngine.Object> _assetCache = new Dictionary<string, UnityEngine.Object>();
        private static readonly Dictionary<string, UnityEngine.Object> _settingsCache = new Dictionary<string, UnityEngine.Object>();
        private static readonly List<string> _missingAssets = new List<string>();
        private static Texture2D _terminalCloudMap;

        private static readonly HashSet<string> SkipClasses = new HashSet<string>
        {
            "TOD_Sky", "TOD_Components", "TOD_Resources", "TOD_Time",
            "SnowFlakes", "SnowRippleController", "SnowWetRenderer", "WinterEventVisual",
            // 2026-08-15 fps kill: SceneLights drives LampSystem.UpdateComponent over
            // EVERY LampController each frame — and all 1164 of terminal's lamps are
            // dead-ref husks that throw. 76k logged exceptions in one raid = single-digit
            // fps. the lamp reviver owns terminal's lights; this system has nothing to run.
            "SceneLights",
        };

        // 2026-08-15, the cel-shading/stutter pair of raids: ENABLED, these render
        // retail's image-effect stack over 4.0's own post (hard-edged posterized
        // black). fully SKIPPED, ToDController.Update NREs per frame on its
        // null-unchecked `AmbientLightScript.SetSH(SH)` — 36k exceptions, killing
        // WeatherController.LateUpdate before it drives rain/clouds at all. the
        // resolution: the components must EXIST (SetSH writes plain fields) but never
        // render — created and filled so refs resolve, then force-disabled pre-Awake.
        private static readonly HashSet<string> ForceDisabled = new HashSet<string>
        {
            "Tonemapping", "AmbientLight", "AmbientHighlight",
        };

        internal static bool Staged => _marker;

        internal static void ResetForRaid()
        {
            _marker = null;
            _rainVisualReady = false;
        }

        private static JObject Sidecar()
        {
            if (_sidecarTried) return _sidecar;
            _sidecarTried = true;
            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_weather.json");
                if (!System.IO.File.Exists(path))
                {
                    Plugin.Log.LogDebug($"[Weather] no sidecar at {path} — weather stays off");
                    return null;
                }
                _sidecar = JObject.Parse(System.IO.File.ReadAllText(path));
                Plugin.Log.LogInfo("[Weather] sidecar loaded");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Weather] sidecar parse failed: {e.Message}");
            }
            return _sidecar;
        }

        public static bool TryStage()
        {
            if (_marker) return true;
            if (!Plugin.WeatherStack.Value) return false;
            var sc = Sidecar();
            var comps = sc?["components"] as JObject;
            if (comps == null || !TerminalLoaded.Check()) return false;

            try
            {
                _comps.Clear(); _pathIndex.Clear(); _assetCache.Clear();
                _settingsCache.Clear(); _missingAssets.Clear();

                // the two subtree roots survive in the bundled Scripts scene
                var roots = new List<Transform>();
                foreach (var rootName in new[] { "Weather", "Sky Dome" })
                {
                    var t = FindSceneGo(rootName);
                    if (!t) { Plugin.Log.LogDebug($"[Weather] root '{rootName}' not in scene — aborting"); return false; }
                    roots.Add(t);
                    IndexSubtree(t, rootName);
                }

                var reactivate = new List<GameObject>();
                foreach (var r in roots)
                    if (r.gameObject.activeSelf) { r.gameObject.SetActive(false); reactivate.Add(r.gameObject); }

                // pass 1 — create all components (fields land before any Awake runs).
                // ORDER + REUSE MATTER (icebreaker lesson): dependees added LAST, every
                // add is get-or-add so a RequireComponent auto-add gets FILLED, never
                // duplicated.
                int created = 0, missingGo = 0, skippedRows = 0;
                var rows = new List<(JToken row, Component comp)>();
                // no LINQ (client code style): flatten then stable two-pass ordering —
                // everything else first, deferred dependees (WeatherController) last
                var ordered = new List<JToken>();
                foreach (var prop in comps.Properties())
                    if (prop.Value is JArray arr)
                        foreach (var r in arr)
                            if ((r.Value<string>("class") ?? "") != "EFT.Weather.WeatherController")
                                ordered.Add(r);
                foreach (var prop in comps.Properties())
                    if (prop.Value is JArray arr)
                        foreach (var r in arr)
                            if ((r.Value<string>("class") ?? "") == "EFT.Weather.WeatherController")
                                ordered.Add(r);
                foreach (var row in ordered)
                {
                    var clsFull = row.Value<string>("class") ?? "";
                    var clsShort = clsFull.Contains('.') ? clsFull.Substring(clsFull.LastIndexOf('.') + 1) : clsFull;
                    if (SkipClasses.Contains(clsShort)) { skippedRows++; continue; }
                    var type = AccessTools.TypeByName(clsFull);
                    if (type == null) { Plugin.Log.LogDebug($"[Weather] type not in 4.0: {clsFull}"); continue; }
                    GameObject go = _pathIndex.TryGetValue(row.Value<string>("go") ?? "", out var t) ? t.gameObject : null;
                    if (!go) { missingGo++; continue; }
                    Component c = go.GetComponent(type);
                    if (!c) c = go.AddComponent(type);
                    if (!c) continue; // unity refused (abstract/invalid)
                    _comps[row.Value<long>("path_id")] = c;
                    rows.Add((row, c));
                    created++;
                }
                if (missingGo > 0) Plugin.Log.LogWarning($"[Weather] {missingGo} component GOs not found in bundled tree");

                // pass 2 — fields + refs
                foreach (var (row, c) in rows)
                    if (row["fields"] is JObject f)
                        FillFields(c, f);

                // WeatherDebug is the first thing Awake touches (SavePreset) — a null
                // here is how last raid's controller died mid-Awake with Instance set
                foreach (var (_, comp) in rows)
                    if (comp is EFT.Weather.WeatherController wcc && wcc.WeatherDebug == null)
                    {
                        wcc.WeatherDebug = new EFT.Weather.WeatherDebug();
                        Plugin.Log.LogWarning("[Weather] WeatherDebug didn't fill from the sidecar — fresh instance substituted");
                    }

                // render-asset guard — BEFORE reactivation so a disabled component never
                // runs its Awake. a null Shader field means the asset didn't resolve and
                // the component would new Material(null) EVERY FRAME (7248 NREs last raid)
                int disabled = 0;
                foreach (var (row, c) in rows)
                {
                    if (!(c is Behaviour b) || c is EFT.Weather.WeatherController) continue;
                    var tn = c.GetType().Name;
                    if (ForceDisabled.Contains(tn))
                    {
                        // AmbientLight graduates when the stencil system is on (user
                        // 2026-08-16): with StencilShadow volumes + AnalyticSource
                        // portals + resolved shaders + live SH, the screen ambient pass
                        // has all its retail inputs — the cel-shading raid ran it with
                        // Tonemapping stacked on top and NO stencils. those two stay
                        // forced off.
                        {
                            b.enabled = false;
                            disabled++;
                            continue;
                        }
                    }
                    string missing = null;
                    foreach (var fi in c.GetType().GetFields(System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    {
                        if (fi.FieldType == typeof(Shader) && !(fi.GetValue(c) as Shader)) { missing = $"Shader {fi.Name}"; break; }
                        if (fi.Name == "_close" && fi.FieldType == typeof(Material) && !(fi.GetValue(c) as Material)) { missing = "Material _close"; break; }
                    }
                    if (missing != null)
                    {
                        b.enabled = false;
                        disabled++;
                        _missingAssets.Add($"{c.GetType().Name} ({missing})");
                        // disabling is not enough: RainController's inner classes call
                        // straight into _wetRenderer's lazy material getter regardless
                        // of enabled state — a null shader there was 49k Material-ctor
                        // exceptions in one raid. stuff a benign build shader so any
                        // cross-component access constructs a harmless material instead
                        // of throwing per frame.
                        var stuffing = Shader.Find("Hidden/BlitCopy");
                        if (!stuffing) stuffing = Shader.Find("Sprites/Default");
                        if (stuffing)
                            foreach (var fi in c.GetType().GetFields(System.Reflection.BindingFlags.Instance
                                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                                if (fi.FieldType == typeof(Shader) && !(fi.GetValue(c) as Shader))
                                    fi.SetValue(c, stuffing);
                    }
                }

                // pass 3 — wake it all up (unity logs+continues on any individual Awake throw)
                foreach (var go in reactivate) go.SetActive(true);

                _probeAt = Time.time + 25f;
                _probePasses = 0;
                _rainAudioHealed = false;
                _marker = new GameObject("Terminal_WeatherStaged");
                var scr = SceneManager.GetSceneByName("Terminal_Scripts");
                if (scr.IsValid() && scr.isLoaded) SceneManager.MoveGameObjectToScene(_marker, scr);

                if (_missingAssets.Count > 0)
                    {
                    // no LINQ: Distinct here resolves to EFT's shadowing GClass1518
                    // extension (different signature) the moment System.Linq leaves
                    var uniq = new HashSet<string>(_missingAssets);
                    Plugin.Log.LogWarning($"[Weather] MISSING ASSETS ({uniq.Count}) — add via editor carrier pass: {string.Join(", ", uniq)}");
                }
                Plugin.Log.LogWarning($"[Weather] WEATHER STACK REBUILT: {created} component(s) "
                    + $"({skippedRows} sky/snow row(s) skipped by design{(disabled > 0 ? $", {disabled} disabled for unresolved render assets" : "")}) — "
                    + $"WeatherController.Instance {(EFT.Weather.WeatherController.Instance ? "OK" : "STILL NULL")}");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Weather] rebuild failed: {e}");
                return false;
            }
        }

        // RAIN PROBE — the rain chain has failed silently across several raids ("still
        // no rain"); the end-of-raid stacks show the state machine CONSTRUCTING inside
        // OnDestroy, i.e. it never built during play. one state dump 25s after staging
        // names the null link instead of another blind fix. reflection-only: never call
        // Class668_0's GETTER (it lazily constructs — that's the thing we're probing).
        private static float _probeAt = -1f;

        private static int _probePasses;

        // RainController.Awake subscribes to CameraClass.OnCameraChanged, but Terminal's
        // reconstructed weather stack wakes before the raid camera exists. The one event
        // which normally supplies its camera anchor is therefore missed forever. The old
        // diagnostic repaired that only on its first 25-second probe, visibly switching
        // rain on in the middle of the intro. Heal it as a startup prerequisite instead.
        internal static bool TryEnsureRainVisualReady()
        {
            if (_rainVisualReady) return true;
            if (!_marker) return false;
            try
            {
                var rc = UnityEngine.Object.FindObjectOfType<RainController>();
                if (!rc || !rc.enabled || !rc.gameObject.activeInHierarchy) return false;

                var camera = CameraClass.Instance?.Camera;
                if (!camera) return false;
                var anchorField = AccessTools.Field(typeof(RainController), "transform_0");
                var anchor = anchorField?.GetValue(rc) as Transform;
                if (!anchor)
                {
                    // method_0 constructs the summer state, initializes RainFallDrops,
                    // binds the camera, and renders the roof-depth texture. It can throw
                    // later while looking for optional screen-droplet effects; the anchor
                    // is assigned before that point, so retain it and use a direct-set
                    // backstop exactly as the delayed diagnostic previously did.
                    try { AccessTools.Method(typeof(RainController), "method_0")?.Invoke(rc, null); }
                    catch { }
                    anchor = anchorField?.GetValue(rc) as Transform;
                    if (!anchor && anchorField != null)
                    {
                        anchorField.SetValue(rc, camera.transform);
                        anchor = camera.transform;
                    }
                    if (anchor)
                        Plugin.Log.LogInfo($"[Weather] rain visuals armed before playback on camera '{anchor.name}'");
                }

                bool stateBuilt = AccessTools.Field(typeof(RainController), "class668_0")?.GetValue(rc) != null;
                bool fallReady = AccessTools.Field(typeof(RainController), "_rainFallDrops")?.GetValue(rc) != null;
                bool depthReady = AccessTools.Field(typeof(RainController), "_depthPhotograper")?.GetValue(rc) != null;
                _rainVisualReady = anchor && stateBuilt && fallReady && depthReady;
                return _rainVisualReady;
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[Weather] early rain visual hookup waiting: {e.Message}");
                return false;
            }
        }

        internal static void TickProbe()
        {
            if (_probeAt < 0f || Time.time < _probeAt) return;
            // three passes: post-stage, post-heal, and well after the intro cutscene —
            // the audio state differs across all three
            _probeAt = ++_probePasses < 3 ? Time.time + 60f : -1f;
            try
            {
                var rc = UnityEngine.Object.FindObjectOfType<RainController>();
                if (!rc) { Plugin.Log.LogWarning("[Weather] RAIN PROBE: no RainController instance in scene"); return; }
                string F(string name)
                {
                    var fi = AccessTools.Field(typeof(RainController), name);
                    if (fi == null) return $"{name}=?";
                    var v = fi.GetValue(rc);
                    bool alive = v is UnityEngine.Object uo ? (bool)uo : v != null;
                    return $"{name}={(alive ? "ok" : "NULL")}";
                }
                var wc = EFT.Weather.WeatherController.Instance;
                float curveRain = -1f;
                try { curveRain = wc ? wc.WeatherCurve.Rain : -1f; } catch { }
                Plugin.Log.LogWarning("[Weather] RAIN PROBE: "
                    + $"enabled={rc.enabled} active={rc.gameObject.activeInHierarchy} "
                    + $"{F("_rainFallDrops")} {F("_rippleController")} {F("_rainSplashController")} "
                    + $"{F("_wetRenderer")} {F("_depthPhotograper")} {F("transform_0")} "
                    + $"stateMachine={(AccessTools.Field(typeof(RainController), "class668_0")?.GetValue(rc) != null ? "BUILT" : "NOT BUILT")} "
                    + $"staticIntensity={RainController.Intensity:0.00} curveRain={curveRain:0.00} "
                    + $"debugEnabled={(wc && wc.WeatherDebug != null ? wc.WeatherDebug.isEnabled.ToString() : "n/a")}");

                // THE 2026-08-15 PROBE VERDICT: everything healthy except transform_0 —
                // the rain system's position anchor, assigned at runtime by the camera
                // hookup (Class668.vmethod_8: transform_0 = cameraManager.Camera.transform)
                // which ran during load with no camera, NREd on .Camera, got swallowed,
                // and never retried. rain computed at full intensity around a null
                // anchor. heal: re-run the hookup now that the camera exists; if the
                // screen-effects tail still throws (Cam2 lacks RainScreenDrops), the
                // anchor lands BEFORE that line — direct field set as the backstop.
                var t0 = AccessTools.Field(typeof(RainController), "transform_0");
                if (t0 != null && !(t0.GetValue(rc) as Transform))
                {
                    var cam = CameraClass.Instance?.Camera;
                    if (cam)
                    {
                        try { AccessTools.Method(typeof(RainController), "method_0")?.Invoke(rc, null); } catch { }
                        if (!(t0.GetValue(rc) as Transform)) t0.SetValue(rc, cam.transform);
                        Plugin.Log.LogWarning($"[Weather] rain camera anchor healed (transform_0 was null — the load-time "
                            + $"hookup ran before the camera existed): now '{(t0.GetValue(rc) as Transform)?.name}'");
                        _probeAt = Time.time + 20f; // pull the next pass in for the heal confirm
                    }
                    else Plugin.Log.LogWarning("[Weather] rain anchor still unhealable — no camera yet");
                }

                // RAIN AUDIO probe (2026-08-15: "i see raindrops now but i dont hear
                // rain") — the blender crossfades dry<->rain ambient loops off
                // RainController.IntensityType; log whether it exists, ticks, and what
                // it believes, so the silent link names itself
                try
                {
                    // FindObjectsOfTypeAll: the ambient root may be cutscene-deactivated
                    // at probe time — the plain Find missed staged blenders entirely
                    // (2026-08-15 false negative "no blender in scene")
                    var blenders = Resources.FindObjectsOfTypeAll<Audio.AmbientSubsystem.PrecipitationAmbientBlender>();
                    if (blenders.Length == 0)
                        Plugin.Log.LogWarning("[Weather] RAIN AUDIO: no PrecipitationAmbientBlender exists AT ALL — staging never built them");
                    foreach (var bl in blenders)
                    {
                        if (!bl || !bl.gameObject.scene.IsValid()) continue; // prefab assets
                        string FF(string n)
                        {
                            var v = AccessTools.Field(bl.GetType(), n)?.GetValue(bl);
                            bool alive = v is UnityEngine.Object uo ? (bool)uo : v != null;
                            return $"{n}={(alive ? "ok" : "NULL")}";
                        }
                        Plugin.Log.LogWarning($"[Weather] RAIN AUDIO: blender '{bl.name}' enabled={bl.enabled} "
                            + $"activeInHierarchy={bl.gameObject.activeInHierarchy} "
                            + $"{FF("_precipitationSource")} {FF("_precipitationMixSource")} {FF("_outputMixerGroup")} "
                            + $"lastSeen={AccessTools.Field(bl.GetType(), "erainIntensity_0")?.GetValue(bl) ?? "?"} "
                            + $"controllerSays={RainController.IntensityType}");
                    }
                    Audio.AmbientSubsystem.EnvironmentSoundBlendSystem ebs = null;
                    foreach (var cand in Resources.FindObjectsOfTypeAll<Audio.AmbientSubsystem.EnvironmentSoundBlendSystem>())
                        if (cand && cand.gameObject.scene.IsValid()) { ebs = cand; break; }
                    Plugin.Log.LogWarning($"[Weather] RAIN AUDIO: EnvironmentSoundBlendSystem "
                        + $"{(!ebs ? "MISSING — nothing drives the blenders" : $"present, enabled={ebs.enabled}, active={ebs.gameObject.activeInHierarchy}")}");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Weather] rain audio probe failed: {e.Message}"); }

                TryHealRainAudio();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Weather] rain probe failed: {e.Message}"); }
        }

        // RAIN AUDIO HEAL (2026-08-15 probe verdict: blenders alive and driven,
        // controller says Med, but _precipitationSource/_precipitationMixSource/
        // _outputMixerGroup all NULL — they're engine AudioSources + a mixer-group
        // ASSET, which the MonoBehaviour-focused ambient wiring can't resolve). the
        // clip catalog is a second gap: PrecipitationAmbientBlender.Init pulls
        // AmbientAudioSystem.Instance.AmbientSoundData (Audio.AmbientSubsystem.Data.SeasonAmbientSoundDataSO),
        // which our resurrected system builds EMPTY when its _ambientData asset is
        // null (AmbientAudioSystem.cs:65). heal all three, then re-run Init.
        private static bool _rainAudioHealed;

        private static void TryHealRainAudio()
        {
            if (_rainAudioHealed) return;
            try
            {
                var sys = MonoBehaviourSingleton<Audio.AmbientSubsystem.AmbientAudioSystem>.Instance;
                if (!sys) return;

                // the clip catalog: any loaded Audio.AmbientSubsystem.Data.SeasonAmbientSoundDataSO with actual
                // content beats the empty CreateInstance fallback
                if (!sys.AmbientSoundData || !HasPrecipClips(sys.AmbientSoundData))
                {
                    Audio.AmbientSubsystem.Data.SeasonAmbientSoundDataSO best = null;
                    foreach (var so in Resources.FindObjectsOfTypeAll<Audio.AmbientSubsystem.Data.SeasonAmbientSoundDataSO>())
                        if (so && HasPrecipClips(so)) { best = so; break; }
                    if (best)
                    {
                        sys.AmbientSoundData = best;
                        Plugin.Log.LogWarning($"[Weather] RAIN AUDIO heal: clip catalog '{best.name}' assigned to AmbientAudioSystem");
                    }
                    else
                    {
                        Plugin.Log.LogWarning(TerminalRainBed.Active
                            ? "[Weather] RAIN AUDIO: season catalog is empty; shipped Terminal fallback bed is active"
                            : "[Weather] RAIN AUDIO heal blocked: no populated Audio.AmbientSubsystem.Data.SeasonAmbientSoundDataSO loaded "
                                + "and the Terminal fallback bed is not ready");
                        _rainAudioHealed = true; // nothing more to do this raid
                        return;
                    }
                }

                // Use Tarkov's explicit outdoor ambience bus. Name-scanning could pick
                // the parent "Ambient" group and bypass the exposed AmbientOut fader.
                UnityEngine.Audio.AudioMixerGroup mixGroup = TerminalAudioRouting.AmbientBed();

                var fSrc = AccessTools.Field(typeof(Audio.AmbientSubsystem.PrecipitationAmbientBlender), "_precipitationSource");
                var fMix = AccessTools.Field(typeof(Audio.AmbientSubsystem.PrecipitationAmbientBlender), "_precipitationMixSource");
                var fOut = AccessTools.Field(typeof(Audio.AmbientSubsystem.PrecipitationAmbientBlender), "_outputMixerGroup");
                int healed = 0;
                foreach (var bl in Resources.FindObjectsOfTypeAll<Audio.AmbientSubsystem.PrecipitationAmbientBlender>())
                {
                    if (!bl || !bl.gameObject.scene.IsValid()) continue;
                    if (fSrc != null && !(fSrc.GetValue(bl) as AudioSource))
                        fSrc.SetValue(bl, MakeRainSource(bl.transform, "PrecipSource"));
                    if (fMix != null && !(fMix.GetValue(bl) as AudioSource))
                        fMix.SetValue(bl, MakeRainSource(bl.transform, "PrecipMixSource"));
                    if (fOut != null && mixGroup && !(fOut.GetValue(bl) as UnityEngine.Audio.AudioMixerGroup))
                        fOut.SetValue(bl, mixGroup);
                    try { bl.Init(); healed++; }
                    catch (Exception e) { Plugin.Log.LogWarning($"[Weather] blender '{bl.name}' re-Init failed: {e.Message}"); }
                }
                if (healed > 0)
                {
                    _rainAudioHealed = true;
                    Plugin.Log.LogWarning($"[Weather] RAIN AUDIO heal: {healed} blender(s) given sources"
                        + $"{(mixGroup ? $" + mixer '{mixGroup.name}'" : " (no ambient mixer group found — master out)")} and re-initialized");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Weather] rain audio heal failed: {e.Message}"); }
        }

        private static bool HasPrecipClips(Audio.AmbientSubsystem.Data.SeasonAmbientSoundDataSO so)
        {
            // an SO with any rain clip for the common case = usable; the empty
            // CreateInstance fallback fails this for every combination
            try
            {
                AudioClip c;
                return so.TryGetPrecipitationClip(ESeasonStatus.Summer,
                    RainController.ERainIntensity.Med, EnvironmentType.Outdoor, out c) && c;
            }
            catch { return false; }
        }

        private static AudioSource MakeRainSource(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = true;
            src.spatialBlend = 0f; // the precipitation bed is 2D ambience
            src.volume = 1f;
            TerminalAudioRouting.Route(src);
            return src;
        }

        // ------------------------------------------------------------- scene lookup
        private static Transform FindSceneGo(string name)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scn = SceneManager.GetSceneAt(i);
                if (!scn.isLoaded || !scn.name.StartsWith("Terminal")) continue;
                foreach (var root in scn.GetRootGameObjects())
                {
                    if (root.name == name) return root.transform;
                    var t = FindChildRecursive(root.transform, name);
                    if (t != null) return t;
                }
            }
            return null;
        }

        private static Transform FindChildRecursive(Transform t, string name)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c.name == name) return c;
                var r = FindChildRecursive(c, name);
                if (r != null) return r;
            }
            return null;
        }

        private static void IndexSubtree(Transform t, string path)
        {
            _pathIndex[path] = t;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                IndexSubtree(c, path + "/" + c.name);
            }
        }

        // ------------------------------------------------------------- ref resolution
        private static UnityEngine.Object ResolveRef(JObject tok, Type want)
        {
            long id = tok.Value<long?>("ref") ?? 0;
            int file = tok.Value<int?>("file") ?? 0;
            if (id == 0) return null;

            if (file == 0)
            {
                // scene-local: a rebuilt component, or an engine object from the table
                if (_comps.TryGetValue(id, out var c))
                    return want.IsInstanceOfType(c) ? c
                        : want == typeof(GameObject) ? (UnityEngine.Object)c.gameObject
                        : c.GetComponent(want);
                var so = _sidecar?["scene_objects"]?[id.ToString()];
                if (so != null && _pathIndex.TryGetValue(so.Value<string>("path") ?? "", out var t))
                {
                    if (want == typeof(GameObject)) return t.gameObject;
                    if (want == typeof(Transform) || want.IsAssignableFrom(typeof(Transform))) return t;
                    if (typeof(Component).IsAssignableFrom(want)) return t.GetComponent(want);
                }
                return null;
            }

            // external asset: resolve by recovered type+name
            var ext = _sidecar?["external_assets"]?[$"{file}:{id}"];
            if (ext == null) return null;
            string etype = ext.Value<string>("type"), name = ext.Value<string>("name");
            if (string.IsNullOrEmpty(name)) return null;

            if (etype == "Shader") return Shader.Find(name);
            if (etype == "MonoBehaviour") return GetOrBuildSettingsAsset(name);
            return FindAssetByName(etype, name);
        }

        private static UnityEngine.Object FindAssetByName(string unityType, string name)
        {
            string key = unityType + ":" + name;
            if (_assetCache.TryGetValue(key, out var cached)) return cached;
            Type t = unityType == "Material" ? typeof(Material)
                : unityType == "Mesh" ? typeof(Mesh)
                : unityType == "Texture2D" ? typeof(Texture2D)
                : unityType == "ComputeShader" ? typeof(ComputeShader)
                : null;
            UnityEngine.Object found = null;
            if (t != null)
                foreach (var o in Resources.FindObjectsOfTypeAll(t))
                    if (o.name == name) { found = o; break; }
            if (found == null) _missingAssets.Add($"{unityType} '{name}'");
            _assetCache[key] = found;
            return found;
        }

        // settings assets (fog remap tables, cloud settings) — rebuilt from values,
        // exactly like the acoustics occlusion preset
        private static UnityEngine.Object GetOrBuildSettingsAsset(string name)
        {
            if (_settingsCache.TryGetValue(name, out var cached)) return cached;
            UnityEngine.Object result = null;
            var entry = _sidecar?["settings_assets"]?[name];
            if (entry != null)
            {
                var type = AccessTools.TypeByName(entry.Value<string>("class"));
                if (type != null && typeof(ScriptableObject).IsAssignableFrom(type))
                {
                    var so = ScriptableObject.CreateInstance(type);
                    so.name = name;
                    if (entry["fields"] is JObject f) FillFields(so, f);
                    // Terminal's old Mono typetree decoded this retail settings asset one
                    // field out of alignment (Altitude became Rotation, Rotation became
                    // Tint.r, etc.). Icebreaker's independently recovered copy is the same
                    // CloudLayerSettings_main asset and provides the intact layout. Apply
                    // those values explicitly and load the real lat/long cloud map shipped
                    // in plugin-data. A flat gray fallback cannot form cloud shapes at all.
                    if (name == "CloudLayerSettings_main") ApplyTerminalCloudSettings(type, so);
                    result = so;
                }
            }
            if (result == null) _missingAssets.Add($"SettingsAsset '{name}'");
            _settingsCache[name] = result;
            return result;
        }

        private static void ApplyTerminalCloudSettings(Type type, ScriptableObject so)
        {
            void Set(object target, string field, object value)
            {
                var fi = AccessTools.Field(target.GetType(), field);
                if (fi != null) fi.SetValue(target, value);
            }

            Set(so, "Opacity", 1f);
            Set(so, "ShadowMultiplier", 1.09f);
            Set(so, "ShadowTint", new Color(0.2264151f, 0.2264151f, 0.2264151f, 0f));
            Set(so, "ShadowSize", 500f);
            Set(so, "PlanetCenterRadius", new Vector4(0f, -6704000f, 0f, 6704000f));

            var layerA = AccessTools.Field(type, "LayerA")?.GetValue(so);
            if (layerA == null) return;
            var cloudMap = LoadTerminalCloudMap();
            Set(layerA, "CloudTexture", cloudMap ? cloudMap : Texture2D.grayTexture);
            Set(layerA, "Altitude", 6000f);
            Set(layerA, "Rotation", 191f);
            Set(layerA, "Tint", Color.white);
            Set(layerA, "Exposure", 2.1f);
            Set(layerA, "ScrollOrientation", 180f);
            Set(layerA, "MinScrollSpeed", 20f);
            Set(layerA, "MaxScrollSpeed", 100f);
            Set(layerA, "Lighting", true);
            Set(layerA, "Thickness", 1f);
            Set(layerA, "AmbientProbeDimmer", 0.675f);

            Plugin.Log.LogWarning(cloudMap
                ? $"[Weather] restored retail cloud layer with '{cloudMap.name}' ({cloudMap.width}x{cloudMap.height})"
                : "[Weather] retail cloud PNG missing/unreadable — emergency gray fallback remains active");
        }

        private static Texture2D LoadTerminalCloudMap()
        {
            if (_terminalCloudMap) return _terminalCloudMap;

            // Prefer an already-loaded native copy if another location/mod supplied it.
            foreach (var tex in Resources.FindObjectsOfTypeAll<Texture2D>())
                if (tex && tex.name == "CloudMapWoHorizonBorder2")
                    return _terminalCloudMap = tex;

            try
            {
                string path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "weather", "CloudMapWoHorizonBorder2.png");
                if (!System.IO.File.Exists(path)) return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, false)
                {
                    name = "CloudMapWoHorizonBorder2",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 1,
                };
                if (!ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(path), false))
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                return _terminalCloudMap = tex;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Weather] cloud PNG load failed: {e.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------- generic filler
        private static void FillFields(object target, JObject fields)
        {
            var type = target.GetType();
            foreach (var prop in fields.Properties())
            {
                var fi = AccessTools.Field(type, prop.Name);
                if (fi == null) continue;
                try
                {
                    var v = ConvertToken(prop.Value, fi.FieldType, fi.GetValue(target));
                    if (v != null || !fi.FieldType.IsValueType) fi.SetValue(target, v);
                }
                catch { /* field drifted or unresolvable — keep the class default */ }
            }
        }

        private static object ConvertToken(JToken tok, Type type, object existing)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;

            if (type == typeof(string)) return tok.Value<string>();
            if (type == typeof(bool)) return tok.Type == JTokenType.Boolean ? tok.Value<bool>() : tok.Value<int>() != 0;
            if (type == typeof(short)) return (short)tok.Value<int>();
            if (type.IsEnum) return Enum.ToObject(type, tok.Value<long>());
            if (type.IsPrimitive) return Convert.ChangeType(((JValue)tok).Value, type,
                System.Globalization.CultureInfo.InvariantCulture);

            if (tok is JObject o)
            {
                // by-name external token (refs inside settings assets, rewritten during
                // enrichment because their externals table differs from level600's)
                if (o["extname"] != null)
                    return FindAssetByName(o.Value<string>("exttype"), o.Value<string>("extname"));
                if (o["ref"] != null && o.Count <= 2)
                    return typeof(UnityEngine.Object).IsAssignableFrom(type) ? ResolveRef(o, type) : null;
                if (type == typeof(Vector2)) return new Vector2(o.Value<float>("x"), o.Value<float>("y"));
                if (type == typeof(Vector3)) return new Vector3(o.Value<float>("x"), o.Value<float>("y"), o.Value<float>("z"));
                if (type == typeof(Vector4)) return new Vector4(o.Value<float>("x"), o.Value<float>("y"), o.Value<float>("z"), o.Value<float>("w"));
                if (type == typeof(Quaternion)) return new Quaternion(o.Value<float>("x"), o.Value<float>("y"), o.Value<float>("z"), o.Value<float>("w"));
                if (type == typeof(Color)) return new Color(o.Value<float>("r"), o.Value<float>("g"), o.Value<float>("b"), o.Value<float>("a"));
                if (type == typeof(Bounds)) return new Bounds(
                    (Vector3)ConvertToken(o["m_Center"], typeof(Vector3), null),
                    (Vector3)ConvertToken(o["m_Extent"], typeof(Vector3), null) * 2f);
                if (type == typeof(LayerMask)) return (LayerMask)(o.Value<int?>("m_Bits") ?? 0);
                if (type == typeof(AnimationCurve)) return BuildCurve(o) ?? existing;
                if (type == typeof(Gradient)) return BuildGradient(o) ?? existing;
                if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return null; // unresolvable object shape
                var inst = existing ?? Activator.CreateInstance(type);
                FillFields(inst, o);
                return inst;
            }

            if (tok is JArray arr)
            {
                Type elem = type.IsArray ? type.GetElementType()
                    : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0]
                    : null;
                if (elem == null) return null;
                var items = new List<object>(arr.Count);
                foreach (var e in arr)
                    items.Add(ConvertToken(e, elem, null));
                if (type.IsArray)
                {
                    var a = Array.CreateInstance(elem, items.Count);
                    for (int i = 0; i < items.Count; i++) a.SetValue(items[i], i);
                    return a;
                }
                var list = (System.Collections.IList)Activator.CreateInstance(type);
                foreach (var it in items) list.Add(it);
                return list;
            }
            return null;
        }

        private static AnimationCurve BuildCurve(JObject o)
        {
            var keys = o["m_Curve"] as JArray;
            if (keys == null) return null;
            var kf = new Keyframe[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                kf[i] = new Keyframe(
                    k.Value<float?>("time") ?? 0f, k.Value<float?>("value") ?? 0f,
                    k.Value<float?>("inSlope") ?? 0f, k.Value<float?>("outSlope") ?? 0f);
            }
            var c = new AnimationCurve(kf);
            c.preWrapMode = (WrapMode)(o.Value<int?>("m_PreInfinity") ?? 8);
            c.postWrapMode = (WrapMode)(o.Value<int?>("m_PostInfinity") ?? 8);
            return c;
        }

        // unity's serialized gradient: key0..key7 colors, ctime0..7 / atime0..7 as 0..65535
        private static Gradient BuildGradient(JObject o)
        {
            if (o["ctime0"] == null && o["key0"] == null) return null;
            int nc = o.Value<int?>("m_NumColorKeys") ?? 2;
            int na = o.Value<int?>("m_NumAlphaKeys") ?? 2;
            var colors = new GradientColorKey[Mathf.Clamp(nc, 1, 8)];
            var alphas = new GradientAlphaKey[Mathf.Clamp(na, 1, 8)];
            for (int i = 0; i < colors.Length; i++)
            {
                var col = o[$"key{i}"] is JObject k
                    ? new Color(k.Value<float>("r"), k.Value<float>("g"), k.Value<float>("b"), k.Value<float>("a"))
                    : Color.white;
                colors[i] = new GradientColorKey(col, (o.Value<int?>($"ctime{i}") ?? 0) / 65535f);
            }
            for (int i = 0; i < alphas.Length; i++)
            {
                var col = o[$"key{i}"] is JObject k ? k.Value<float>("a") : 1f;
                alphas[i] = new GradientAlphaKey(col, (o.Value<int?>($"atime{i}") ?? 0) / 65535f);
            }
            var g = new Gradient();
            g.SetKeys(colors, alphas);
            g.mode = (GradientMode)(o.Value<int?>("m_Mode") ?? 0);
            return g;
        }
    }
}
