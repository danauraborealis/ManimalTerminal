using System;
using System.Collections;
using System.IO;
using Audio.AmbientSubsystem;
using Audio.AmbientSubsystem.Data;
using EFT.EnvironmentEffect;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // RAIN AMBIENT SOUND (user report, twice): the PrecipitationAmbientBlenders in
    // our rebuilt ambient layer sit with NULL AudioSources/mixer, and their Init()
    // pulls the season sound data from AmbientAudioSystem.AmbientSoundData — also
    // null in the rebuild. this pass fills all of it: donor SeasonAmbientSoundDataSO
    // from loaded resources, two AudioSources per blender, an ambience mixer group,
    // then re-runs Init. every step logs so a miss names itself.
    internal static class TerminalRainAudio
    {
        private static bool _staged;

        internal static void ResetForRaid()
        {
            _staged = false;
            TerminalRainBed.ResetForRaid();
        }

        internal static void TryStage()
        {
            if (_staged || !TerminalGate.On) return;
            var sys = MonoBehaviourSingleton<AmbientAudioSystem>.Instantiated
                ? MonoBehaviourSingleton<AmbientAudioSystem>.Instance : null;
            if (sys == null) return; // ambient layer not staged yet
            var blenders = UnityEngine.Object.FindObjectsOfType<PrecipitationAmbientBlender>(true);
            if (blenders.Length == 0) return;
            _staged = true;

            try
            {
                // season sound data: the system's own, else any loaded donor SO
                var dataProp = AccessTools.Property(typeof(AmbientAudioSystem), "AmbientSoundData")
                    ?? null;
                object soData = dataProp?.GetValue(sys);
                if (soData == null)
                {
                    var sos = Resources.FindObjectsOfTypeAll<SeasonAmbientSoundDataSO>();
                    if (sos.Length > 0)
                    {
                        soData = sos[0];
                        if (dataProp != null && dataProp.CanWrite) dataProp.SetValue(sys, soData);
                        else AccessTools.Field(typeof(AmbientAudioSystem), "AmbientSoundData")?.SetValue(sys, soData);
                    }
                }
                bool catalogUsable = HasPrecipitationClips(soData as SeasonAmbientSoundDataSO);
                Plugin.Log.LogInfo($"[RainAudio] season sound data: {(soData != null ? soData.ToString() : "NONE LOADED")} "
                    + $"({(catalogUsable ? "precipitation clips present" : "empty precipitation catalog — Terminal fallback bed enabled")})");
                if (!catalogUsable) TerminalRainBed.Ensure();

                UnityEngine.Audio.AudioMixerGroup mixer = TerminalAudioRouting.AmbientBed();

                int fixedN = 0;
                foreach (var b in blenders)
                {
                    var fSrc = AccessTools.Field(typeof(PrecipitationAmbientBlender), "_precipitationSource");
                    var fMix = AccessTools.Field(typeof(PrecipitationAmbientBlender), "_precipitationMixSource");
                    var fOut = AccessTools.Field(typeof(PrecipitationAmbientBlender), "_outputMixerGroup");
                    if (fSrc.GetValue(b) == null)
                    {
                        var s1 = b.gameObject.AddComponent<AudioSource>();
                        s1.playOnAwake = false; s1.loop = true; s1.spatialBlend = 0f;
                        TerminalAudioRouting.Route(s1);
                        fSrc.SetValue(b, s1);
                    }
                    if (fMix.GetValue(b) == null)
                    {
                        var s2 = b.gameObject.AddComponent<AudioSource>();
                        s2.playOnAwake = false; s2.loop = true; s2.spatialBlend = 0f;
                        TerminalAudioRouting.Route(s2);
                        fMix.SetValue(b, s2);
                    }
                    if (fOut.GetValue(b) == null && mixer != null) fOut.SetValue(b, mixer);
                    try { b.Init(); fixedN++; }
                    catch (Exception ie) { Plugin.Log.LogWarning($"[RainAudio] blender '{b.gameObject.name}' Init threw: {ie.Message}"); }
                }
                Plugin.Log.LogInfo($"[RainAudio] {fixedN}/{blenders.Length} precipitation blender(s) wired "
                    + $"(mixer: {(mixer ? mixer.name : "none")})");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RainAudio] staging failed: {e}"); }
        }

        private static bool HasPrecipitationClips(SeasonAmbientSoundDataSO data)
        {
            if (!data) return false;
            try
            {
                AudioClip clip;
                return data.TryGetPrecipitationClip(ESeasonStatus.Summer,
                    RainController.ERainIntensity.Med, EnvironmentType.Outdoor, out clip) && clip;
            }
            catch { return false; }
        }
    }

    // The retail map references precipitation through a SeasonAmbientSoundDataSO that
    // is not part of the custom location bundle.  The reconstructed object consequently
    // exists but contains no clips, so perfectly healthy blenders crossfade NULL forever.
    // Ship Terminal's recovered retail indoor/outdoor rain loops and drive them from the
    // real RainController instead. This remains a fallback: a populated game catalog wins.
    internal sealed class TerminalRainBed : MonoBehaviour
    {
        private static TerminalRainBed _instance;
        private AudioSource _outdoor;
        private AudioSource _indoor;
        private bool _ready;
        private bool _reportedLive;
        private bool _selectorInitialised;
        private bool _resolvedIndoors;
        private bool _candidateIndoors;
        private float _candidateSince;

        internal static bool Active => _instance && _instance._ready;

        internal static void ResetForRaid() => _instance = null;

        internal static void Ensure()
        {
            if (_instance || !TerminalGate.On) return;
            var go = new GameObject("Terminal_RainAudioFallback");
            var scripts = SceneManager.GetSceneByName("Terminal_Scripts");
            if (scripts.IsValid() && scripts.isLoaded) SceneManager.MoveGameObjectToScene(go, scripts);
            _instance = go.AddComponent<TerminalRainBed>();
        }

        private void Awake()
        {
            _outdoor = MakeSource("OutdoorRain");
            _indoor = MakeSource("IndoorRain");
            StartCoroutine(Load());
        }

        private AudioSource MakeSource(string sourceName)
        {
            var go = new GameObject(sourceName);
            go.transform.SetParent(transform, false);
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f;
            source.volume = 0f;
            TerminalAudioRouting.Route(source);
            return source;
        }

        private IEnumerator Load()
        {
            yield return LoadClip(_outdoor, "amb_rain_outdoor_strong.wav", "amb_rain_outdoor_strong");
            yield return LoadClip(_indoor, "amb_rain_indoor_strong.wav", "amb_rain_indoor_strong");
            _ready = _outdoor && _outdoor.clip && _indoor && _indoor.clip;
            if (_ready)
            {
                _outdoor.Play();
                _indoor.Play();
                Plugin.Log.LogInfo($"[RainAudio] fallback rain bed ready: outdoor='{_outdoor.clip.name}', "
                    + $"indoor='{_indoor.clip.name}', mixer='{(_outdoor.outputAudioMixerGroup ? _outdoor.outputAudioMixerGroup.name : "BYPASS/NULL")}'");
            }
            else Plugin.Log.LogWarning("[RainAudio] fallback rain bed incomplete — one or both shipped clips failed to load");
        }

        private IEnumerator LoadClip(AudioSource target, string fileName, string clipName)
        {
            // Prefer an already loaded bundle instance, but the disk copy makes this
            // deterministic even when Unity has not retained the sharedassets clip.
            foreach (var clip in Resources.FindObjectsOfTypeAll<AudioClip>())
                if (clip && string.Equals(clip.name, clipName, StringComparison.OrdinalIgnoreCase))
                {
                    target.clip = clip;
                    yield break;
                }

            string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                "plugin-data", "audio", fileName);
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"[RainAudio] fallback clip missing: {path}");
                yield break;
            }

            using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.WAV))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogWarning($"[RainAudio] failed loading '{fileName}': {request.error}");
                    yield break;
                }
                var clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip)
                {
                    clip.name = clipName;
                    target.clip = clip;
                }
            }
        }

        private void Update()
        {
            if (!_ready || !TerminalGate.On) return;
            float rain = Mathf.Clamp01(RainController.Intensity);
            bool triggerIndoors = EnvironmentManager.Instance != null
                && EnvironmentManager.Instance.Environment == EnvironmentType.Indoor;

            // Terminal's reconstructed environment switcher does not necessarily receive
            // an overlap callback when the player is placed inside a trigger by the intro.
            // Tarkov's rain controller independently photographs the camera every frame to
            // decide whether rain can actually reach it.  Treat roof cover as indoor audio,
            // while retaining the authored trigger as an additional indoor veto.  A short
            // debounce prevents doorway/roof-edge samples from rapidly flipping both beds.
            bool cameraUnderRain = RainController.IsCameraUnderRain;
            bool candidateIndoors = triggerIndoors || !cameraUnderRain;
            if (!_selectorInitialised)
            {
                _selectorInitialised = true;
                _candidateIndoors = candidateIndoors;
                _resolvedIndoors = candidateIndoors;
                _candidateSince = Time.unscaledTime;
            }
            else if (_candidateIndoors != candidateIndoors)
            {
                _candidateIndoors = candidateIndoors;
                _candidateSince = Time.unscaledTime;
            }
            else if (_resolvedIndoors != candidateIndoors
                && Time.unscaledTime - _candidateSince >= 0.25f)
            {
                _resolvedIndoors = candidateIndoors;
                if (rain > 0.01f)
                    Plugin.Log.LogInfo($"[RainAudio] acoustic rain -> {(_resolvedIndoors ? "Indoor" : "Outdoor")} "
                        + $"(trigger={(triggerIndoors ? "Indoor" : "Outdoor")}, cameraUnderRain={cameraUnderRain})");
            }
            bool indoors = _resolvedIndoors;

            float outdoorTarget = indoors ? 0f : rain * 0.85f;
            float indoorTarget = indoors ? rain * 0.52f : 0f;
            float step = Time.unscaledDeltaTime * 0.8f;
            _outdoor.volume = Mathf.MoveTowards(_outdoor.volume, outdoorTarget, step);
            _indoor.volume = Mathf.MoveTowards(_indoor.volume, indoorTarget, step);

            if (!_reportedLive && rain > 0.01f)
            {
                _reportedLive = true;
                Plugin.Log.LogInfo($"[RainAudio] fallback precipitation audible: intensity={rain:0.00}, "
                    + $"environment={(indoors ? "Indoor" : "Outdoor")}, "
                    + $"trigger={(triggerIndoors ? "Indoor" : "Outdoor")}, cameraUnderRain={cameraUnderRain}");
            }
        }
    }
}
