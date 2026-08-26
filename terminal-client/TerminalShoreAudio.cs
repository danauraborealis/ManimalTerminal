using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Audio.AmbientSubsystem;
using BezierSplineTools;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // Terminal_Sound retains the two retail sea splines, their native
    // SoundAmbientZoneCalculators and all six LoopAmbientSoundPlayers. Only the three
    // sharedassets clips are absent from the custom bundle. This loader supplies those
    // assets before TerminalAcoustics stages the retail ambient tree; Tarkov's native
    // spline controllers own playback, positioning, spread and distance from then on.
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
    }

    // Run after Tarkov's default-order AmbientAudioSystem.LateUpdate so the safety
    // projection is the final authority for these six sources each frame.
    [DefaultExecutionOrder(10000)]
    internal sealed class TerminalShoreAudioLoader : MonoBehaviour
    {
        // Retail's two sea splines are closed. In the original 1.0 location their
        // surrounding ambient stack supplies a correctly initialized listener and
        // zone state. In the custom-map reconstruction an incomplete native init can
        // instead take SoundAmbientZoneCalculator's "inside loop" branch, which sets
        // maxDistance=float.MaxValue and spatialBlend=0. That is the global,
        // deafening ocean reported by testers. Keep the retail spline geometry and
        // authored player rolloffs, but deterministically project each branch onto
        // its own spline and enforce a physical shoreline cutoff.
        private const float HardCutoffMeters = 115f;
        private const int SplineSamples = 160;

        private sealed class ShoreSource
        {
            internal AudioSource Source;
            internal float AuthoredMaxDistance;
            internal float AuthoredVolume;
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
        private bool _nativeRoutingLogged;
        private bool _guardLogged;
        private bool _guardStateKnown;
        private bool _lastGuardAudible;

        private IEnumerator Start()
        {
            yield return Load("amb_terminal_spline_sea_close_quite.wav", "amb_terminal_spline_sea_close_quite");
            yield return Load("amb_terminal_spline_sea_distant_quite.wav", "amb_terminal_spline_sea_distant_quite");
            yield return Load("amb_terminal_spline_sea_far_quite.wav", "amb_terminal_spline_sea_far_quite");
            TerminalShoreAudio.MarkReady();
            Plugin.Log.LogInfo($"[ShoreAudio] native shoreline assets ready: {_ownedClips.Count}/3 clip(s); "
                + "retail SoundAmbientZoneCalculators now own playback and positioning");
        }

        private IEnumerator Load(string fileName, string clipName)
        {
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
                clip.name = clipName;
                _ownedClips.Add(clip);
                TerminalAcoustics.RegisterRecoveredLoopClip(clipName, clip);
            }
        }

        private void Update()
        {
            if (!TerminalShoreAudio.AssetsReady || Time.realtimeSinceStartup < _nextRoutingCheck) return;
            _nextRoutingCheck = Time.realtimeSinceStartup + 1f;
            if (!_nativeRoutingLogged)
            {
                int nativeSources = TerminalAcoustics.EnsureNativeShoreRouting();
                if (nativeSources > 0)
                {
                    _nativeRoutingLogged = true;
                    Plugin.Log.LogInfo($"[ShoreAudio] {nativeSources} native shoreline source(s) verified on Tarkov's AmbientOut mixer");
                }
            }

            if (_branches.Count == 0 && TerminalAcoustics.AmbientStaged)
                DiscoverBranches();
        }

        private void LateUpdate()
        {
            if (_branches.Count == 0) return;

            Player player = Singleton<GameWorld>.Instantiated
                ? Singleton<GameWorld>.Instance?.MainPlayer
                : null;
            bool masterAudible = true;
            try
            {
                if (Singleton<SharedGameSettingsClass>.Instantiated)
                    masterAudible = Singleton<SharedGameSettingsClass>.Instance
                        ?.Sound?.Settings?.OverallVolume?.Value > 0;
            }
            catch { }

            Vector3 listener = player != null ? player.Position : Vector3.zero;
            float nearestSq = float.MaxValue;
            bool anyBranchInRange = false;
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

                bool inRange = player != null && closestSq <= HardCutoffMeters * HardCutoffMeters;
                if (closestSq < nearestSq) nearestSq = closestSq;
                anyBranchInRange |= inRange;
                foreach (var state in branch.Sources)
                {
                    var source = state.Source;
                    if (!source) continue;
                    // Run after AmbientAudioSystem.LateUpdate so its broken closed-loop
                    // "inside" result cannot put these sources back into 2D/global mode.
                    source.transform.position = closest;
                    source.spatialBlend = 1f;
                    source.maxDistance = state.AuthoredMaxDistance;
                    // Terminal's F12 sound-rig volume is a continuous local trim;
                    // Tarkov's overall-volume slider continues to act in the mixer.
                    source.volume = state.AuthoredVolume * Mathf.Clamp01(Plugin.SoundRigVolume.Value);
                    source.mute = !masterAudible || !inRange || !TerminalAudioRouting.Route(source);
                }
            }

            bool guardAudible = masterAudible && anyBranchInRange;
            if (!_guardStateKnown || guardAudible != _lastGuardAudible)
            {
                _guardStateKnown = true;
                _lastGuardAudible = guardAudible;
                Plugin.Log.LogInfo($"[ShoreAudio] shoreline {(guardAudible ? "AUDIBLE" : "MUTED")}: "
                    + $"nearestSpline={(nearestSq < float.MaxValue ? Mathf.Sqrt(nearestSq).ToString("F0") : "n/a")}m, "
                    + $"masterAudible={masterAudible}");
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
                        if (!player) continue;
                        var clip = player.GetClip();
                        if (!clip || !clip.name.StartsWith("amb_terminal_spline_sea_",
                                StringComparison.OrdinalIgnoreCase)) continue;
                        var source = player.Source;
                        if (!source) continue;
                        // Native inside-loop handling may already have replaced this
                        // with float.MaxValue, so recover the retail per-layer values.
                        float authoredVolume = player.GetBaseVolume();
                        float authoredMax = clip.name.IndexOf("far", StringComparison.OrdinalIgnoreCase) >= 0
                            && authoredVolume < 0.3f ? 100f : 85f;
                        branch.Sources.Add(new ShoreSource
                        {
                            Source = source,
                            AuthoredMaxDistance = authoredMax,
                            AuthoredVolume = authoredVolume,
                        });
                        source.mute = true;
                    }
                    if (branch.Sources.Count > 0) _branches.Add(branch);
                }
            }

            if (_branches.Count > 0 && !_guardLogged)
            {
                _guardLogged = true;
                int sources = 0;
                foreach (var branch in _branches) sources += branch.Sources.Count;
                Plugin.Log.LogInfo($"[ShoreAudio] spline guard ready: {_branches.Count} authored branch(es), "
                    + $"{sources} source(s), {HardCutoffMeters:F0}m hard cutoff + game master-volume zero gate");
            }
        }

        private void OnDestroy()
        {
            foreach (var clip in _ownedClips)
                if (clip) Destroy(clip);
            _ownedClips.Clear();
            _branches.Clear();
        }
    }
}
