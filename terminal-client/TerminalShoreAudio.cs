using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Audio.AmbientSubsystem;
using BezierSplineTools;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // Terminal_Sound retains the two retail sea splines, their native
    // SoundAmbientZoneCalculators and six LoopAmbientSoundPlayers (three belong to
    // an inactive alternate branch). The three sharedassets clips are absent
    // from the custom bundle. Supply those assets and
    // project dedicated sources onto the original splines. Do not let the partially
    // initialized native zone/culling stack also own these playback sources.
    internal static class TerminalShoreAudio
    {
        private static TerminalShoreAudioLoader _instance;
        private static bool _assetsReady;

        internal static bool AssetsReady => _assetsReady;

        internal static void ResetForRaid()
        {
            if (_instance) UnityEngine.Object.Destroy(_instance.gameObject);
            _instance = null;
            _assetsReady = false;
            TerminalAcoustics.ResetRecoveredAudioForRaid();
        }

        internal static void TryStage()
        {
            if (_instance || !TerminalGate.On) return;
            var sound = SceneManager.GetSceneByName("Terminal_Sound");
            if (!sound.IsValid() || !sound.isLoaded) return;

            var go = new GameObject("Terminal_ShoreAudioAssets");
            SceneManager.MoveGameObjectToScene(go, sound);
            _instance = go.AddComponent<TerminalShoreAudioLoader>();
        }

        internal static void MarkReady() => _assetsReady = true;
        internal static void MarkReloading() => _assetsReady = false;
    }

    // Run after Tarkov's default-order AmbientAudioSystem.LateUpdate so the safety
    // projection is the final authority for these sources each frame.
    [DefaultExecutionOrder(10000)]
    internal sealed class TerminalShoreAudioLoader : MonoBehaviour
    {
        // The native ambient dependency graph is still incomplete. Until it owns
        // playback, position these sources on the authored splines and retain each
        // player's own distance/rolloff pair. Unity normalizes custom rolloff by
        // maxDistance: replacing the authored 85/100m with 220m moved the loud
        // offshore layers inland, including the spawn room at ~104m.
        private const int SplineSamples = 160;

        private sealed class ShoreSource
        {
            internal AudioSource Source;
            internal AudioClip Clip;
            internal float AuthoredVolume;
            internal AudioSource NativeSource;
            internal float NextStartAttempt;
            internal int PreviousSample = -1;
        }

        private sealed class ShoreBranch
        {
            internal BezierSpline Spline;
            internal Vector3[] SamplePoints;
            internal readonly List<ShoreSource> Sources = new List<ShoreSource>();
        }

        private readonly List<AudioClip> _ownedClips = new List<AudioClip>();
        private readonly List<ShoreBranch> _branches = new List<ShoreBranch>();
        private float _nextRoutingCheck;
        private AudioMixerGroup _mixer;
        private float _nextPlaybackDiagnostic;
        private bool _guardLogged;
        private bool _guardStateKnown;
        private bool _lastGuardAudible;
        private int _audioGeneration;
        private bool _reloadClips;
        private bool _loadingClips;

        private void Awake()
        {
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
        }

        private void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            // LocalGame resets the audio engine after scene loading. Downloaded
            // clips then report Loaded but have zero samples; LoadAudioData cannot
            // recover them. Read the WAVs again, including after device changes.
            _audioGeneration++;
            _reloadClips = true;
            TerminalShoreAudio.MarkReloading();
            Plugin.Log.LogInfo($"[ShoreAudio] audio engine reset; reloading shoreline WAVs (deviceChanged={deviceWasChanged})");
        }

        private IEnumerator Start()
        {
            yield return ReloadClips();
        }

        private IEnumerator ReloadClips()
        {
            _loadingClips = true;
            do
            {
                _reloadClips = false;
                int generation = _audioGeneration;
                yield return Load("amb_terminal_spline_sea_close_quite.wav", "amb_terminal_spline_sea_close_quite", generation);
                yield return Load("amb_terminal_spline_sea_distant_quite.wav", "amb_terminal_spline_sea_distant_quite", generation);
                yield return Load("amb_terminal_spline_sea_far_quite.wav", "amb_terminal_spline_sea_far_quite", generation);
                // A reset during an asynchronous read invalidates that whole pass.
            } while (_reloadClips);
            _loadingClips = false;
            TerminalShoreAudio.MarkReady();
            int valid = _ownedClips.FindAll(c => c && c.samples > 0).Count;
            Plugin.Log.LogInfo($"[ShoreAudio] native shoreline assets ready: {valid}/3 clip(s); "
                + "dedicated playback will follow the authored sea group membership");
        }

        private IEnumerator Load(string fileName, string clipName, int generation)
        {
            if (generation != _audioGeneration) yield break;
            string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                "plugin-data", "audio", fileName);
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"[ShoreAudio] recovered shoreline clip missing: {path}");
                yield break;
            }

            using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.WAV))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogWarning($"[ShoreAudio] failed loading '{fileName}': {request.error}");
                    yield break;
                }

                var clip = DownloadHandlerAudioClip.GetContent(request);
                if (!clip) yield break;
                if (generation != _audioGeneration || clip.samples <= 0)
                {
                    Destroy(clip);
                    yield break;
                }
                clip.name = clipName;
                var previous = _ownedClips.Find(c => c && c.name == clipName);
                if (previous) _ownedClips.Remove(previous);
                _ownedClips.Add(clip);
                // Rebind both native authoring and any already-created playback
                // sources without waking native players that we have taken over.
                TerminalAcoustics.RegisterRecoveredLoopClip(clipName, clip, _branches.Count == 0);
                foreach (var branch in _branches)
                    foreach (var state in branch.Sources)
                        if (state.Clip && state.Clip.name == clipName)
                        {
                            state.Clip = clip;
                            state.NextStartAttempt = 0f;
                            state.PreviousSample = -1;
                            if (state.Source)
                            {
                                state.Source.Stop();
                                state.Source.clip = clip;
                            }
                        }
                if (previous) Destroy(previous);
                Plugin.Log.LogInfo($"[ShoreAudio] loaded '{clipName}': samples={clip.samples}, channels={clip.channels}, frequency={clip.frequency}");
            }
        }

        private void Update()
        {
            if (_reloadClips && !_loadingClips) StartCoroutine(ReloadClips());
            if (!TerminalShoreAudio.AssetsReady || Time.realtimeSinceStartup < _nextRoutingCheck) return;
            _nextRoutingCheck = Time.realtimeSinceStartup + 1f;
            // Cache mixer lookup, especially while BetterAudio is still starting.
            // No global Resources scans in the per-frame positioning path.
            _mixer = TerminalAudioRouting.AmbientBed();

            if (_branches.Count == 0 && TerminalAcoustics.AmbientStaged)
                DiscoverBranches();
        }

        private void LateUpdate()
        {
            if (_branches.Count == 0 || !TerminalShoreAudio.AssetsReady) return;

            Player player = Singleton<GameWorld>.Instantiated
                ? Singleton<GameWorld>.Instance?.MainPlayer
                : null;
            bool masterAudible = true;
            try
            {
                if (Singleton<EFT.Settings.SettingsManager>.Instantiated)
                    masterAudible = Singleton<EFT.Settings.SettingsManager>.Instance
                        ?.Sound?.Settings?.OverallVolume?.Value > 0;
            }
            catch { }

            Vector3 listener = player != null ? player.Position : Vector3.zero;
            float nearestSq = float.MaxValue;
            bool anyBranchInRange = false;
            int playingSources = 0;
            bool diagnose = Time.realtimeSinceStartup >= _nextPlaybackDiagnostic;
            if (diagnose) _nextPlaybackDiagnostic = Time.realtimeSinceStartup + 30f;
            foreach (var branch in _branches)
            {
                if (branch?.Spline == null) continue;
                Vector3 closest = Vector3.zero;
                float closestSq = float.MaxValue;
                var samplePoints = branch.SamplePoints;
                if (samplePoints == null || samplePoints.Length == 0) continue;
                for (int i = 0; i < samplePoints.Length; i++)
                {
                    Vector3 point = samplePoints[i];
                    float dx = point.x - listener.x;
                    float dz = point.z - listener.z;
                    float sq = dx * dx + dz * dz;
                    if (sq >= closestSq) continue;
                    closestSq = sq;
                    closest = point;
                }

                if (closestSq < nearestSq) nearestSq = closestSq;
                float sourceDistanceSq = (closest - listener).sqrMagnitude;
                foreach (var state in branch.Sources)
                {
                    var source = state.Source;
                    if (!source) continue;
                    bool inRange = player != null && sourceDistanceSq <= source.maxDistance * source.maxDistance;
                    anyBranchInRange |= inRange;
                    // The original source is no longer a playback owner. In particular,
                    // AudioSourceCulling may toggle its enabled flag but cannot restart
                    // an inactive GO, and cannot touch this unregistered replacement.
                    if (state.NativeSource && state.NativeSource.gameObject.activeSelf)
                        state.NativeSource.gameObject.SetActive(false);
                    source.transform.position = closest;
                    // Terminal's F12 sound-rig volume is a continuous local trim;
                    // Tarkov's overall-volume slider continues to act in the mixer.
                    source.volume = state.AuthoredVolume * Mathf.Clamp01(Plugin.SoundRigVolume.Value);
                    source.outputAudioMixerGroup = _mixer;
                    source.mute = !masterAudible || !inRange || !_mixer;
                    bool shouldPlay = !source.mute && source.volume > 0f && source.gameObject.activeInHierarchy;
                    if (shouldPlay && !source.isPlaying && Time.realtimeSinceStartup >= state.NextStartAttempt)
                    {
                        state.NextStartAttempt = Time.realtimeSinceStartup + 1f;
                        source.enabled = true;
                        if (state.Clip.loadState == AudioDataLoadState.Unloaded) state.Clip.LoadAudioData();
                        if (state.Clip.loadState == AudioDataLoadState.Loaded && state.Clip.samples > 0) source.Play();
                    }
                    else if (!shouldPlay && source.isPlaying) source.Stop();
                    if (source.isPlaying && !source.mute) playingSources++;
                    if (diagnose)
                    {
                        int sample = source.timeSamples;
                        Plugin.Log.LogInfo($"[ShoreAudio][playback] '{state.Clip.name}' playing={source.isPlaying}"
                            + $" enabled={source.enabled} active={source.gameObject.activeInHierarchy} mute={source.mute}"
                            + $" volume={source.volume:F2} pitch={source.pitch:F2} load={state.Clip.loadState}"
                            + $" clipSamples={state.Clip.samples} listenerPaused={AudioListener.pause}"
                            + $" distance={Mathf.Sqrt(sourceDistanceSq):F1}m maxDistance={source.maxDistance:F1}m"
                            + $" sample={sample} previousSample={state.PreviousSample}"
                            + $" mixer='{(_mixer ? _mixer.name : "missing")}'");
                        state.PreviousSample = sample;
                    }
                }
            }

            bool guardAudible = playingSources > 0;
            if (!_guardStateKnown || guardAudible != _lastGuardAudible)
            {
                _guardStateKnown = true;
                _lastGuardAudible = guardAudible;
                Plugin.Log.LogInfo($"[ShoreAudio] shoreline {(guardAudible ? "PLAYING" : "STOPPED")}: "
                    + $"nearestSpline={(nearestSq < float.MaxValue ? Mathf.Sqrt(nearestSq).ToString("F0") : "n/a")}m, "
                    + $"masterAudible={masterAudible}, inRange={anyBranchInRange}, playingSources={playingSources}");
            }
        }

        private void DiscoverBranches()
        {
            // Never use Resources.FindObjectsOfTypeAll here. Terminal has ~200k
            // transforms; retrying that global scan once per second caused a 400ms
            // freeze and ~65MB of transient allocations every pass.
            var ambientRoot = TerminalAcoustics.AmbientRootTransform;
            if (!ambientRoot) return;
            foreach (var root in ambientRoot.GetComponentsInChildren<Transform>(true))
            {
                if (!root || !string.Equals(root.name, "AmbientSplineEmitterSeaGroup",
                        StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var spline in root.GetComponentsInChildren<BezierSpline>(true))
                {
                    if (!spline) continue;
                    var samples = new Vector3[SplineSamples + 1];
                    for (int i = 0; i <= SplineSamples; i++)
                        samples[i] = spline.GetPoint((float)i / SplineSamples);
                    var branch = new ShoreBranch { Spline = spline, SamplePoints = samples };
                    var emitterRoot = spline.transform.parent;
                    if (!emitterRoot) continue;
                    foreach (var player in emitterRoot.GetComponentsInChildren<BaseAmbientSoundPlayer>(true))
                    {
                        // Hierarchy presence is not playback membership. Retail's
                        // group lists three players; the louder alternate branch is
                        // authored inactive and must not be woken by this fallback.
                        if (!player || !TerminalAcoustics.IsConfiguredShorePlayer(player)) continue;
                        var clip = player.GetClip();
                        if (!clip || !clip.name.StartsWith("amb_terminal_spline_sea_",
                                StringComparison.OrdinalIgnoreCase)) continue;
                        var source = player.Source;
                        if (!source) continue;
                        // Activate only the branch selected by the authored group.
                        for (var t = player.transform; t != null && t != ambientRoot; t = t.parent)
                            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                        float authoredVolume = player.GetBaseVolume();
                        // The native calculator can temporarily expand maxDistance
                        // inside a closed zone. Its public reset uses _maxDistance,
                        // restoring the player's serialized value before we copy it.
                        player.ScaleMaxDistance(0f);
                        // Copy the authored layered rolloff, not the native calculator's
                        // mutated 2D blend or infinite maxDistance. Parent under the sea
                        // group so cutscene/root silencing still applies automatically.
                        var go = new GameObject("Terminal_Shore_" + clip.name);
                        go.SetActive(false);
                        go.transform.SetParent(root, false);
                        var playback = go.AddComponent<AudioSource>();
                        playback.playOnAwake = false;
                        playback.loop = true;
                        playback.clip = clip;
                        playback.pitch = player.GetPitch();
                        playback.spatialBlend = 1f;
                        playback.dopplerLevel = 0f;
                        playback.minDistance = source.minDistance;
                        playback.maxDistance = source.maxDistance;
                        playback.rolloffMode = AudioRolloffMode.Custom;
                        playback.SetCustomCurve(AudioSourceCurveType.CustomRolloff,
                            source.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
                        playback.spread = source.spread;
                        playback.SetCustomCurve(AudioSourceCurveType.Spread,
                            source.GetCustomCurve(AudioSourceCurveType.Spread));
                        playback.volume = 0f;
                        playback.mute = true;
                        playback.outputAudioMixerGroup = _mixer;
                        Plugin.Log.LogInfo($"[ShoreAudio] taking over '{clip.name}': nativeEnabled={source.enabled},"
                            + $" nativePlaying={source.isPlaying}, nativePitch={source.pitch:F2}; "
                            + $"dedicated source, authored range={playback.minDistance:F1}-{playback.maxDistance:F1}m, same spline/rolloff");
                        player.Stop(true); // cancels fader/play coroutines and unregisters from culling
                        source.gameObject.SetActive(false);
                        go.SetActive(true);
                        branch.Sources.Add(new ShoreSource
                        {
                            Source = playback,
                            NativeSource = source,
                            Clip = clip,
                            AuthoredVolume = authoredVolume,
                        });
                    }
                    if (branch.Sources.Count > 0) _branches.Add(branch);
                }
            }

            if (_branches.Count > 0 && !_guardLogged)
            {
                _guardLogged = true;
                int sources = 0;
                foreach (var branch in _branches) sources += branch.Sources.Count;
                _nextPlaybackDiagnostic = Time.realtimeSinceStartup + 10f;
                Plugin.Log.LogInfo($"[ShoreAudio] dedicated spline playback ready: {_branches.Count} authored branch(es), "
                    + $"{sources} source(s), per-player authored ranges, AmbientSplines={Plugin.AmbientSplines.Value}, AmbientOut/master-volume gate");
            }
        }

        private void OnDestroy()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
            foreach (var branch in _branches)
                foreach (var state in branch.Sources)
                    if (state.Source) Destroy(state.Source.gameObject);
            foreach (var clip in _ownedClips)
                if (clip) Destroy(clip);
            _ownedClips.Clear();
            _branches.Clear();
        }
    }
}
