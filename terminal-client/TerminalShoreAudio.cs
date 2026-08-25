using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BezierSplineTools;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // The Terminal sound scene contains the retail shoreline splines and all six
    // close/distant/far LoopAmbientSoundPlayers, but custom-location bundles do not
    // retain the three sharedassets AudioClips they reference. Load the recovered
    // retail loops from disk and hand them back to TerminalAcoustics. The original
    // SoundAmbientZoneCalculators then keep the emitters on the closest shoreline
    // point and perform the authored distance/spread blend.
    internal static class TerminalShoreAudio
    {
        private static TerminalShoreAudioLoader _instance;
        private static bool _silenced;

        internal static void ResetForRaid()
        {
            if (_instance) UnityEngine.Object.Destroy(_instance.gameObject);
            _instance = null;
            _silenced = false;
            TerminalAcoustics.ResetRecoveredAudioForRaid();
        }

        internal static void SetSilenced(bool silent)
        {
            _silenced = silent;
            if (_instance) _instance.SetSilenced(silent);
        }

        internal static bool Silenced => _silenced;

        internal static void TryStage()
        {
            if (_instance || !TerminalGate.On) return;
            var sound = SceneManager.GetSceneByName("Terminal_Sound");
            if (!sound.IsValid() || !sound.isLoaded) return;

            var go = new GameObject("Terminal_ShoreAudioLoader");
            SceneManager.MoveGameObjectToScene(go, sound);
            _instance = go.AddComponent<TerminalShoreAudioLoader>();
        }
    }

    internal sealed class TerminalShoreAudioLoader : MonoBehaviour
    {
        private readonly List<AudioClip> _ownedClips = new List<AudioClip>();
        private readonly List<AudioSource> _layers = new List<AudioSource>();
        private readonly List<BezierSpline> _shoreSplines = new List<BezierSpline>();
        private float _nextTrack;
        private bool _loggedMissingSpline;

        private IEnumerator Start()
        {
            yield return Load("amb_terminal_spline_sea_close_quite.wav", "amb_terminal_spline_sea_close_quite");
            yield return Load("amb_terminal_spline_sea_distant_quite.wav", "amb_terminal_spline_sea_distant_quite");
            yield return Load("amb_terminal_spline_sea_far_quite.wav", "amb_terminal_spline_sea_far_quite");
            FindShoreSplines();
            SetSilenced(TerminalShoreAudio.Silenced);
            Plugin.Log.LogInfo($"[ShoreAudio] direct shoreline emitter ready: clips={_ownedClips.Count}/3, authoredSplines={_shoreSplines.Count}, routedLayers={_layers.Count}");
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
                AddLayer(clip);
            }
        }

        private void AddLayer(AudioClip clip)
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 8f;
            source.maxDistance = 105f;
            // Retail's three files are already close/distant/far perspective mixes.
            // Layering them quietly at the same closest shoreline point preserves
            // that texture while Unity handles the actual player-distance rolloff.
            source.volume = _layers.Count == 0 ? 0.48f : _layers.Count == 1 ? 0.30f : 0.20f;
            TerminalAudioRouting.Route(source);
            _layers.Add(source);
            if (!TerminalShoreAudio.Silenced) source.Play();
        }

        private void FindShoreSplines()
        {
            _shoreSplines.Clear();
            var sound = SceneManager.GetSceneByName("Terminal_Sound");
            if (!sound.IsValid() || !sound.isLoaded) return;
            foreach (var root in sound.GetRootGameObjects())
            {
                foreach (var spline in root.GetComponentsInChildren<BezierSpline>(true))
                {
                    if (!spline || spline.CurveCount <= 0) continue;
                    bool sea = false;
                    for (var t = spline.transform; t != null; t = t.parent)
                    {
                        if (t.name.IndexOf("AmbientSplineEmitterSeaGroup", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            sea = true;
                            break;
                        }
                    }
                    if (sea && !_shoreSplines.Contains(spline)) _shoreSplines.Add(spline);
                }
            }
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < _nextTrack) return;
            _nextTrack = Time.realtimeSinceStartup + 0.2f;
            if (_shoreSplines.Count == 0)
            {
                FindShoreSplines();
                if (_shoreSplines.Count == 0 && !_loggedMissingSpline)
                {
                    _loggedMissingSpline = true;
                    Plugin.Log.LogWarning("[ShoreAudio] authored sea spline not found; shoreline layers cannot position themselves");
                }
                return;
            }

            var world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            var player = world?.MainPlayer;
            if (!player) return;
            Vector3 listener = player.Position;
            Vector3 nearest = transform.position;
            float bestSq = float.MaxValue;
            foreach (var spline in _shoreSplines)
            {
                if (!spline || spline.CurveCount <= 0) continue;
                // Coarse authored-curve search followed by a local refinement. This is
                // only five times a second and avoids the broken network spline mover.
                float coarseStep = 1f / Mathf.Max(12f, spline.CurveCount * 8f);
                float t = spline.ClosestTimeOnBezier(listener, 0f, coarseStep, 1f);
                float lo = Mathf.Max(0f, t - coarseStep);
                float hi = Mathf.Min(1f, t + coarseStep);
                float fineStep = Mathf.Max(0.0025f, coarseStep / 8f);
                t = spline.ClosestTimeOnBezier(listener, lo, fineStep, hi);
                Vector3 point = spline.GetPoint(t);
                float sq = (point - listener).sqrMagnitude;
                if (sq >= bestSq) continue;
                bestSq = sq;
                nearest = point;
            }
            transform.position = nearest;
        }

        internal void SetSilenced(bool silent)
        {
            foreach (var source in _layers)
            {
                if (!source) continue;
                if (silent)
                {
                    if (source.isPlaying) source.Pause();
                }
                else if (source.clip && !source.isPlaying)
                {
                    source.UnPause();
                    if (!source.isPlaying) source.Play();
                }
            }
        }

        private void OnDestroy()
        {
            foreach (var clip in _ownedClips)
                if (clip) Destroy(clip);
            _ownedClips.Clear();
            _layers.Clear();
            _shoreSplines.Clear();
        }
    }
}
