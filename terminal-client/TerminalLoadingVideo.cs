using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using EFT;
using EFT.UI.Matchmaker;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace Manimal.Terminal
{
    // Terminal-only raid-loading movie. This is deliberately PRESENTATIONAL:
    // VideoPlayer never participates in the raid-load task and can neither delay
    // nor fail map startup. If the file/codec fails, the untouched native banners
    // remain underneath as the immediate fallback.
    internal static class TerminalLoadingVideo
    {
        private const string VideoFileName = "term_intro.mp4";
        private static LoadingVideoRunner _active;

        private static string VideoPath => Path.Combine(
            Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty,
            "plugin-data", "video", VideoFileName);

        private static bool IsTerminal(RaidSettings settings)
        {
            return settings?.SelectedLocation != null
                && string.Equals(settings.SelectedLocation.Id, TerminalGate.LocationId,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void Show(MatchmakerTimeHasCome screen, RaidSettings settings)
        {
            StopImmediate();
            if (!Plugin.LoadingCutscene.Value || !IsTerminal(settings)) return;

            string path = VideoPath;
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"[LoadingVideo] missing {path} — using native banners");
                return;
            }

            try
            {
                var root = new GameObject("Terminal_Loading_Video", typeof(RectTransform));
                root.transform.SetParent(screen.transform, false);
                var rect = (RectTransform)root.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                rect.SetAsLastSibling();

                _active = root.AddComponent<LoadingVideoRunner>();
                _active.Initialize(screen, path);
                Plugin.Log.LogInfo("[LoadingVideo] Terminal intro preparing (non-blocking, native banners are fallback)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LoadingVideo] could not create player — using native banners: {e.Message}");
                StopImmediate();
            }
        }

        internal static void StopImmediate()
        {
            if (_active == null) return;
            var active = _active;
            _active = null;
            if (active != null) UnityEngine.Object.Destroy(active.gameObject);
        }

        internal static void Detached(LoadingVideoRunner runner)
        {
            if (_active == runner) _active = null;
        }

        [HarmonyPatch(typeof(MatchmakerTimeHasCome), nameof(MatchmakerTimeHasCome.Show),
            new[] { typeof(ISession), typeof(RaidSettings), typeof(MatchmakerPlayerControllerClass) })]
        internal static class Patch_PlayDuringTerminalLoad
        {
            [HarmonyPostfix]
            private static void Postfix(MatchmakerTimeHasCome __instance, RaidSettings raidSettings)
            {
                Show(__instance, raidSettings);
            }
        }

        // Belt-and-suspenders teardown. Normally the overlay is a child of the
        // loading screen and dies with it; this guarantees no video audio can leak
        // into the playable raid if a mod keeps that screen alive or merely hides it.
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_StopAtRaidStart
        {
            [HarmonyPrefix]
            private static void Prefix()
            {
                StopImmediate();
            }
        }

        internal sealed class LoadingVideoRunner : MonoBehaviour
        {
            private readonly List<PromotedCanvas> _promoted = new List<PromotedCanvas>();
            private readonly Dictionary<AudioSource, bool> _mutedMusic = new Dictionary<AudioSource, bool>();
            private VideoPlayer _player;
            private AudioSource _audio;
            private RenderTexture _texture;
            private RawImage _videoImage;
            private CanvasGroup _group;
            private bool _tearingDown;
            private bool _firstFrameReady;
            private bool _ownsSoundtrack;
            private float _nextMusicSweepAt;

            internal void Initialize(MatchmakerTimeHasCome screen, string path)
            {
                var parentCanvas = screen.GetComponentInParent<Canvas>();
                int overlayOrder = (parentCanvas != null ? parentCanvas.rootCanvas.sortingOrder : 0) + 100;

                var canvas = gameObject.AddComponent<Canvas>();
                canvas.overrideSorting = true;
                canvas.sortingOrder = overlayOrder;
                _group = gameObject.AddComponent<CanvasGroup>();
                _group.alpha = 0f; // do not cover the fallback until a frame is ready
                _group.blocksRaycasts = false;
                _group.interactable = false;

                AddBackdrop();
                AddVideoImage();
                PromoteNativeUi(screen, overlayOrder + 1);

                _texture = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32)
                {
                    name = "Terminal_Loading_Video_RT",
                    useMipMap = false,
                    autoGenerateMips = false,
                };
                _texture.Create();
                _videoImage.texture = _texture;

                _audio = gameObject.AddComponent<AudioSource>();
                _audio.playOnAwake = false;
                _audio.loop = false;
                _audio.spatialBlend = 0f;
                _audio.volume = 1f;
                RouteToMasterMixer(_audio);

                _player = gameObject.AddComponent<VideoPlayer>();
                _player.playOnAwake = false;
                _player.waitForFirstFrame = true;
                // A raid load can hitch hard on the first few frames. Dropping video
                // frames here makes VideoPlayer catch up by cutting off the opening;
                // this short presentation should instead preserve every frame.
                _player.skipOnDrop = false;
                _player.isLooping = false;
                _player.source = VideoSource.Url;
                _player.url = path;
                _player.renderMode = VideoRenderMode.RenderTexture;
                _player.targetTexture = _texture;
                _player.audioOutputMode = VideoAudioOutputMode.AudioSource;
                _player.prepareCompleted += OnPrepared;
                _player.loopPointReached += OnFinished;
                _player.errorReceived += OnError;
                _player.frameReady += OnFirstFrameReady;
                _player.sendFrameReadyEvents = true;
                try
                {
                    _player.EnableAudioTrack(0, true);
                    _player.SetTargetAudioSource(0, _audio);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[LoadingVideo] audio track unavailable: {e.Message}");
                }
                _player.Prepare();
            }

            private void AddBackdrop()
            {
                var go = new GameObject("Backdrop", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(transform, false);
                Stretch((RectTransform)go.transform);
                var image = go.GetComponent<Image>();
                image.color = Color.black;
                image.raycastTarget = false;
            }

            private void AddVideoImage()
            {
                var go = new GameObject("Video", typeof(RectTransform), typeof(CanvasRenderer),
                    typeof(RawImage), typeof(AspectRatioFitter));
                go.transform.SetParent(transform, false);
                Stretch((RectTransform)go.transform);
                _videoImage = go.GetComponent<RawImage>();
                _videoImage.color = Color.white;
                _videoImage.raycastTarget = false;
                var fitter = go.GetComponent<AspectRatioFitter>();
                fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                fitter.aspectRatio = 16f / 9f;
            }

            private static void Stretch(RectTransform rect)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }

            private void PromoteNativeUi(MatchmakerTimeHasCome screen, int sortingOrder)
            {
                // Keep the authoritative location, progress/timer and cancel button
                // visible and functional over the movie. These remain EFT's own UI;
                // no status polling or duplicated strings can drift out of sync.
                PromoteField(screen, "_subCaption", sortingOrder);
                PromoteField(screen, "_deployingText", sortingOrder);
                PromoteField(screen, "_cancelButton", sortingOrder);
            }

            private void PromoteField(MatchmakerTimeHasCome screen, string fieldName, int sortingOrder)
            {
                var component = AccessTools.Field(typeof(MatchmakerTimeHasCome), fieldName)?.GetValue(screen) as Component;
                if (component == null) return;
                var canvas = component.gameObject.GetComponent<Canvas>();
                bool added = canvas == null;
                if (added) canvas = component.gameObject.AddComponent<Canvas>();
                _promoted.Add(new PromotedCanvas(canvas, added, canvas.overrideSorting, canvas.sortingOrder));
                canvas.overrideSorting = true;
                canvas.sortingOrder = sortingOrder;
            }

            private static void RouteToMasterMixer(AudioSource source)
            {
                if (!TerminalAudioRouting.RouteMaster(source))
                    Plugin.Log.LogWarning("[LoadingVideo] Master mixer group unavailable; using Unity output");
            }

            private void OnPrepared(VideoPlayer player)
            {
                if (_tearingDown || player == null) return;

                // Decode once while invisible and muted, then rewind. This absorbs
                // the decoder's first-use hitch before the user sees or hears it.
                StartCoroutine(PrerollAndPlay(player));
            }

            private void OnFirstFrameReady(VideoPlayer player, long frameIndex)
            {
                _firstFrameReady = true;
            }

            private IEnumerator PrerollAndPlay(VideoPlayer player)
            {
                // The game commonly blocks the main thread as the map load begins.
                // Keep the native banner visible until that startup stall has passed,
                // otherwise VideoPlayer's clock begins before Unity can render it.
                float notBefore = Time.realtimeSinceStartup + 1f;
                int smoothFrames = 0;
                while (!_tearingDown && player != null
                    && (Time.realtimeSinceStartup < notBefore || smoothFrames < 4))
                {
                    smoothFrames = Time.unscaledDeltaTime <= 0.1f ? smoothFrames + 1 : 0;
                    yield return null;
                }
                if (_tearingDown || player == null) yield break;

                if (_audio != null) _audio.mute = true;
                _firstFrameReady = false;
                player.Play();

                // Frame callbacks are platform/codec dependent, so never let a
                // missing callback hold the presentation indefinitely.
                float deadline = Time.realtimeSinceStartup + 1f;
                while (!_tearingDown && player != null && !_firstFrameReady
                    && Time.realtimeSinceStartup < deadline)
                    yield return null;

                if (_tearingDown || player == null) yield break;
                player.Pause();
                player.time = 0d;
                player.frame = 0;

                // Give the rewind and UI canvas one render turn to settle before
                // opening the audio path and revealing the movie.
                yield return null;
                if (_tearingDown || player == null) yield break;
                _ownsSoundtrack = true;
                MuteMusicSources();
                if (_audio != null) _audio.mute = false;
                player.Play();
                StartCoroutine(Fade(0f, 1f, 0.25f, false));
                Plugin.Log.LogInfo($"[LoadingVideo] playing {VideoFileName} ({player.width}x{player.height}, {player.length:0.0}s)");
            }

            private void OnFinished(VideoPlayer player)
            {
                if (_tearingDown) return;
                RestoreMusicSources();
                StartCoroutine(Fade(_group != null ? _group.alpha : 1f, 0f, 0.4f, true));
                Plugin.Log.LogInfo("[LoadingVideo] movie complete — revealing native banners");
            }

            private void Update()
            {
                if (!_ownsSoundtrack || Time.realtimeSinceStartup < _nextMusicSweepAt) return;
                MuteMusicSources();
            }

            private void MuteMusicSources()
            {
                _nextMusicSweepAt = Time.realtimeSinceStartup + 0.5f;
                foreach (var source in Resources.FindObjectsOfTypeAll<AudioSource>())
                {
                    if (!source || source == _audio) continue;
                    if (!TerminalAudioRouting.IsMusicSource(source)) continue;

                    if (!_mutedMusic.ContainsKey(source)) _mutedMusic.Add(source, source.mute);
                    source.mute = true;
                }
            }

            private void RestoreMusicSources()
            {
                _ownsSoundtrack = false;
                foreach (var entry in _mutedMusic)
                    if (entry.Key) entry.Key.mute = entry.Value;
                _mutedMusic.Clear();
            }

            private void OnError(VideoPlayer player, string message)
            {
                Plugin.Log.LogWarning($"[LoadingVideo] decoder error — using native banners: {message}");
                Teardown();
            }

            private IEnumerator Fade(float from, float to, float seconds, bool destroyAfter)
            {
                float started = Time.realtimeSinceStartup;
                while (!_tearingDown && _group != null)
                {
                    float t = Mathf.Clamp01((Time.realtimeSinceStartup - started) / seconds);
                    _group.alpha = Mathf.Lerp(from, to, t);
                    if (t >= 1f) break;
                    yield return null;
                }
                if (destroyAfter) Teardown();
            }

            private void Teardown()
            {
                if (_tearingDown) return;
                _tearingDown = true;
                TerminalLoadingVideo.Detached(this);
                Destroy(gameObject);
            }

            private void OnDestroy()
            {
                _tearingDown = true;
                TerminalLoadingVideo.Detached(this);
                RestoreMusicSources();
                if (_player != null)
                {
                    _player.prepareCompleted -= OnPrepared;
                    _player.loopPointReached -= OnFinished;
                    _player.errorReceived -= OnError;
                    _player.frameReady -= OnFirstFrameReady;
                    _player.sendFrameReadyEvents = false;
                    _player.Stop();
                    _player.targetTexture = null;
                }
                if (_audio != null) _audio.Stop();
                foreach (var promoted in _promoted) promoted.Restore();
                _promoted.Clear();
                if (_texture != null)
                {
                    _texture.Release();
                    Destroy(_texture);
                    _texture = null;
                }
            }

            private sealed class PromotedCanvas
            {
                private readonly Canvas _canvas;
                private readonly bool _added;
                private readonly bool _oldOverride;
                private readonly int _oldOrder;

                internal PromotedCanvas(Canvas canvas, bool added, bool oldOverride, int oldOrder)
                {
                    _canvas = canvas;
                    _added = added;
                    _oldOverride = oldOverride;
                    _oldOrder = oldOrder;
                }

                internal void Restore()
                {
                    if (_canvas == null) return;
                    if (_added) Destroy(_canvas);
                    else
                    {
                        _canvas.overrideSorting = _oldOverride;
                        _canvas.sortingOrder = _oldOrder;
                    }
                }
            }
        }
    }
}
