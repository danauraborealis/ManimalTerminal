using System;
using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // the SECOND retail cutscene: PortCutscene_01_Attack (the port assault — five
    // RUAF actors + camera, timeline rebuilt via SDK Author 21A). retail plays it as
    // a TIMED mid-raid event; here it fires a configurable number of minutes into
    // the raid. same playback shape as the intro driver, two differences: the timer
    // trigger, and it is SKIPPABLE by default (a forced minute of cutscene mid-raid
    // is a death sentence once bots exist — retail-faithful players can turn that off).
    public class TerminalAttackCutscene : MonoBehaviour
    {
        private const string SceneName = "Terminal_area_PortCutscene_01_Attack";
        private const float FadeDur = 0.7f;

        private static bool _playedThisRaid;

        // phase anchors for the SOUND rig — mirrors TerminalIntroCutscene.FinishedAt.
        // FinishedAt is also set on bail/skip so ambience never deadlocks waiting
        internal static float FinishedAt = -1f;
        internal static bool PlayingNow;

        internal static void PlayCoop(bool skip)
        {
            if (_playedThisRaid) return;
            _playedThisRaid = true;
            if (skip || !TerminalCoop.LocalHuman || !Plugin.AttackCutscene.Value || !Available)
            {
                FinishedAt = Time.realtimeSinceStartup;
                if (TerminalCoop.LocalHuman) TerminalCoop.Request(TerminalEvent.AttackDone);
                return;
            }
            new GameObject("Terminal_AttackCutscene").AddComponent<TerminalAttackCutscene>();
        }

        internal static void ResetForNewRaid()
        {
            _playedThisRaid = false;
            FinishedAt = -1f;
            PlayingNow = false;
        }

        public static bool Available
        {
            get
            {
                try { return Application.CanStreamedLevelBeLoaded(SceneName); }
                catch { return false; }
            }
        }

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_ArmAttackTimer
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (TerminalCoop.Active) return;
                if (!TerminalGate.On || _playedThisRaid) return;
                if (!Plugin.AttackCutscene.Value) return;
                var go = new GameObject("Terminal_AttackCutsceneTimer");
                go.AddComponent<TimerHost>();
            }
        }

        // retail timing (user-measured off live footage): the attack fires ~45s after
        // the INTRO hands control back. anchor on TerminalIntroCutscene.FinishedAt;
        // if the intro never runs (disabled/unavailable), fall back to raid start.
        internal class TimerHost : MonoBehaviour
        {
            private float _armedAt;

            private void Start() => _armedAt = Time.realtimeSinceStartup;

            private void Update()
            {
                if (_playedThisRaid) { Destroy(gameObject); return; }
                float anchor = TerminalIntroCutscene.FinishedAt;
                if (anchor < 0f)
                {
                    // intro still playing (or never armed) — wait, with the raid-start
                    // fallback if nothing ever finishes (intro disabled/bailed silently)
                    if (Time.realtimeSinceStartup - _armedAt < 180f) return;
                    anchor = _armedAt;
                }
                if (Time.realtimeSinceStartup - anchor < Plugin.AttackCutsceneDelay.Value) return;
                if (!Available)
                {
                    Plugin.Log.LogWarning($"[AttackCutscene] '{SceneName}' not loadable (bundle missing the scene?) — skipped");
                    _playedThisRaid = true;
                    Destroy(gameObject);
                    return;
                }
                _playedThisRaid = true;
                var go = new GameObject("Terminal_AttackCutscene");
                go.AddComponent<TerminalAttackCutscene>();
                Destroy(gameObject);
            }
        }

        private Scene _scene;
        private bool _sceneLoaded;
        private PlayableDirector _director;
        private Camera _rigCam;
        private Camera _realCam;
        private GameObject _fadeCanvas;
        private UnityEngine.UI.Image _fadeImage;
        private bool _driving;
        private bool _inputLocked;
        private readonly List<Canvas> _hiddenCanvases = new List<Canvas>();
        private readonly List<Renderer> _hiddenRenderers = new List<Renderer>();
        private readonly List<Behaviour> _pausedFx = new List<Behaviour>();
        private float _fade;
        private Texture2D _black;
        private bool _restored;
        private float _savedFov = -1f;

        // camera shake at the timeline's own CameraShake_01 signal times — retail
        // routed these through the SOUND rig's ExplosionShaker chain (dead in 4.0),
        // so the shake is applied straight in DriveCamera instead
        private readonly List<double> _shakeTimes = new List<double>();
        private int _nextShake;
        private float _shakeLeft;
        private const float ShakeDur = 0.55f;
        private const float ShakeDeg = 0.8f;

        private void Start() => StartCoroutine(Run());

        private IEnumerator Run()
        {
            PlayingNow = true;
            TerminalAcoustics.SetAmbientSilenced(true); // the timeline owns these seconds
            GamePlayerOwner.SetIgnoreInputInNPCDialog(true);
            _inputLocked = true;
            yield return Fade(0f, 1f);

            var load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
            if (load == null) { Bail("LoadSceneAsync returned null"); yield break; }
            while (!load.isDone) yield return null;
            _scene = SceneManager.GetSceneByName(SceneName);
            _sceneLoaded = _scene.IsValid() && _scene.isLoaded;
            if (!_sceneLoaded) { Bail("cutscene scene failed to load"); yield break; }

            RestoreMachineGunWorldLayer();

            try { TerminalShaderRebind.RebindNow(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[AttackCutscene] shader rebind failed: {e.Message}"); }
            try { TerminalVolumetricLights.Restore(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[AttackCutscene] volumetric pass failed: {e.Message}"); }
            try { TerminalLights.CutsceneBeginLightTracking(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[AttackCutscene] culler hold failed: {e.Message}"); }

            foreach (var rgo in _scene.GetRootGameObjects())
            {
                _director = rgo.GetComponentInChildren<PlayableDirector>(true);
                if (_director != null) break;
            }
            if (_director == null || _director.playableAsset == null)
            { Bail("TimeLineDirector missing or playableAsset empty (Author 21A not run?)"); yield break; }

            try
            {
                var tl = _director.playableAsset as UnityEngine.Timeline.TimelineAsset;
                if (tl != null)
                    foreach (var track in tl.GetOutputTracks())
                        foreach (var m in track.GetMarkers())
                            if (m is UnityEngine.Timeline.SignalEmitter se && se.asset != null
                                && se.asset.name.StartsWith("CameraShake"))
                                _shakeTimes.Add(se.time);
                _shakeTimes.Sort();
                if (_shakeTimes.Count > 0)
                    Plugin.Log.LogInfo($"[AttackCutscene] {_shakeTimes.Count} camera-shake signal(s) armed");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[AttackCutscene] shake marker scan failed: {e.Message}"); }

            foreach (var rgo in _scene.GetRootGameObjects())
            {
                _rigCam = rgo.GetComponentInChildren<Camera>(true);
                if (_rigCam != null) break;
            }
            if (_rigCam == null) { Bail("no camera in cutscene scene"); yield break; }
            _rigCam.enabled = false;
            var lst = _rigCam.GetComponent<AudioListener>();
            if (lst != null) lst.enabled = false;

            _realCam = EFT.CameraControl.CameraManager.Instance?.Camera;
            if (_realCam == null) _realCam = Camera.main;
            if (_realCam == null) { Bail("no main camera"); yield break; }

            // retail fade rig — same rip losses as the intro: rebuild canvas + black rgb
            var fadeTf = FindInScene(_scene, "UI_FadeToBlack");
            _fadeCanvas = fadeTf;
            if (_fadeCanvas != null)
            {
                _fadeCanvas.SetActive(true);
                try
                {
                    foreach (var cv in _fadeCanvas.GetComponentsInChildren<Canvas>(true))
                    {
                        cv.renderMode = RenderMode.ScreenSpaceOverlay;
                        cv.sortingOrder = 9999;
                    }
                    foreach (var cs in _fadeCanvas.GetComponentsInChildren<UnityEngine.UI.CanvasScaler>(true))
                    {
                        cs.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                        cs.referenceResolution = new Vector2(1920f, 1080f);
                    }
                    _fadeImage = _fadeCanvas.GetComponentInChildren<UnityEngine.UI.Image>(true);
                    if (_fadeImage != null)
                    {
                        var rt = _fadeImage.rectTransform;
                        rt.anchorMin = Vector2.zero;
                        rt.anchorMax = Vector2.one;
                        rt.offsetMin = Vector2.zero;
                        rt.offsetMax = Vector2.zero;
                        _fadeImage.color = new Color(0f, 0f, 0f, _fadeImage.color.a);
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[AttackCutscene] fade rig fix failed: {e.Message}"); }
            }

            foreach (var b in _realCam.GetComponents<Behaviour>())
                if (b != null && b.enabled && (b is EffectsController || b is CC_Base))
                {
                    b.enabled = false;
                    _pausedFx.Add(b);
                }

            var player = Singleton<GameWorld>.Instance?.MainPlayer;
            if (player != null)
                foreach (var r in player.gameObject.GetComponentsInChildren<Renderer>(false))
                    if (r != null && r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }

            foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>())
                if (cv != null && cv.enabled && cv.isRootCanvas
                    && cv.renderMode == RenderMode.ScreenSpaceOverlay
                    && cv.gameObject.scene != _scene)
                {
                    cv.enabled = false;
                    _hiddenCanvases.Add(cv);
                }

            _savedFov = _realCam.fieldOfView;
            Camera.onPreCull += DriveCamera;
            _driving = true;

            _director.extrapolationMode = DirectorWrapMode.Hold;
            _director.timeUpdateMode = DirectorUpdateMode.DSPClock; // see the intro's note: a hitch must not desync audio
            _director.Play();
            if (TerminalCoop.Active && TerminalCoop.AttackStarted >= 0)
                _director.time = Math.Min(_director.duration - 0.05, Math.Max(0, TerminalCoop.Now - TerminalCoop.AttackStarted));
            TerminalSubtitles.Show("attack", _director);
            Plugin.Log.LogInfo($"[AttackCutscene] playing '{_director.playableAsset.name}' " +
                               $"({_director.duration:0.0}s{(Plugin.AttackCutsceneSkippable.Value ? ", SPACE skips" : "")})");
            yield return Fade(1f, 0f);

            double dur = _director.duration;
            float hardStop = Time.realtimeSinceStartup + (float)dur + 10f;
            while (Time.realtimeSinceStartup < hardStop)
            {
                if (_director == null) break;
                if (_director.state != PlayState.Playing) break;
                if (_director.time >= dur - 0.05) break;
                if (_fadeImage != null)
                {
                    var fc = _fadeImage.color;
                    if (fc.r > 0f || fc.g > 0f || fc.b > 0f)
                        _fadeImage.color = new Color(0f, 0f, 0f, fc.a);
                }
                while (_nextShake < _shakeTimes.Count && _director.time >= _shakeTimes[_nextShake])
                {
                    _shakeLeft = ShakeDur;
                    _nextShake++;
                }
                if (Plugin.AttackCutsceneSkippable.Value && Input.GetKeyDown(KeyCode.Space))
                {
                    Plugin.Log.LogInfo("[AttackCutscene] skipped");
                    break;
                }
                yield return null;
            }

            yield return Fade(0f, 1f);
            Restore();
            if (_sceneLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(_scene);
                while (unload != null && !unload.isDone) yield return null;
                _sceneLoaded = false;
            }
            yield return new WaitForSecondsRealtime(0.8f);
            yield return Fade(1f, 0f, 1.6f);
            Destroy(gameObject);
        }

        private void RestoreMachineGunWorldLayer()
        {
            // The authored PKM prop (including its LOD meshes) retained the item
            // inspection layer. The raid camera does not render Weapon Preview;
            // the other attack-scene weapons are on Default. Fix only this prop,
            // including inactive LOD children, without changing activation or LODs.
            var gun = FindInScene(_scene, "Cutscene_weapon_pkm");
            if (gun == null)
            {
                Plugin.Log.LogWarning("[AttackCutscene] Cutscene_weapon_pkm missing; machine-gun layer repair skipped");
                return;
            }
            int previewLayer = LayerMask.NameToLayer("Weapon Preview");
            int worldLayer = LayerMask.NameToLayer("Default");
            if (previewLayer < 0 || worldLayer < 0)
            {
                Plugin.Log.LogWarning("[AttackCutscene] weapon-preview/world layer unavailable; machine-gun layer repair skipped");
                return;
            }

            int changed = 0;
            foreach (var child in gun.GetComponentsInChildren<Transform>(true))
            {
                if (child.gameObject.layer != previewLayer) continue;
                child.gameObject.layer = worldLayer;
                changed++;
            }
            if (changed > 0)
                Plugin.Log.LogInfo($"[AttackCutscene] restored machine gun to world layer: {changed} preview-layer object(s)");
        }

        private static GameObject FindInScene(Scene scn, string name)
        {
            if (!scn.IsValid() || !scn.isLoaded) return null;
            foreach (var rgo in scn.GetRootGameObjects())
            {
                if (rgo.name == name) return rgo;
                foreach (var t in rgo.GetComponentsInChildren<Transform>(true))
                    if (t != null && t.name == name) return t.gameObject;
            }
            return null;
        }

        private void DriveCamera(Camera cam)
        {
            if (cam != _realCam || _rigCam == null) return;
            var t = _rigCam.transform;
            var rot = t.rotation;
            if (_shakeLeft > 0f)
            {
                _shakeLeft -= Time.unscaledDeltaTime;
                // decaying perlin wobble — enough to sell a near explosion without
                // fighting the authored camera motion
                float k = Mathf.Clamp01(_shakeLeft / ShakeDur);
                float amp = ShakeDeg * k * k;
                float tt = Time.unscaledTime * 23f;
                rot *= Quaternion.Euler(
                    (Mathf.PerlinNoise(tt, 0.3f) - 0.5f) * 2f * amp,
                    (Mathf.PerlinNoise(0.7f, tt) - 0.5f) * 2f * amp,
                    (Mathf.PerlinNoise(tt, tt) - 0.5f) * amp);
            }
            cam.transform.SetPositionAndRotation(t.position, rot);
			TerminalLights.TrackCutsceneCamera(t.position);

            // same fov-pop guard as the intro: clamp the RATE so a bad curve frame
            // becomes a smooth correction instead of a zoom-out and back
            // clamp reverted — see the intro's note; smoothing was worse than the pop
            float want = _rigCam.fieldOfView;
            if (want <= 1f || want >= 179f) { if (_lastFov > 0f) cam.fieldOfView = _lastFov; return; }
            if (Plugin.DevMode.Value && _lastFov > 0f && Mathf.Abs(want - _lastFov) > 5f)
                Plugin.Log.LogWarning($"[AttackCutscene] fov jump {_lastFov:0.0} -> {want:0.0} at t={_director?.time:0.00}s "
                    + $"(frame {Time.unscaledDeltaTime * 1000f:0}ms) — passed through");
            _lastFov = want;
            cam.fieldOfView = want;
        }

        private float _lastFov = -1f;

        private void Bail(string why)
        {
            Plugin.Log.LogWarning($"[AttackCutscene] {why} — cutscene skipped");
            Restore();
            if (_sceneLoaded) { SceneManager.UnloadSceneAsync(_scene); _sceneLoaded = false; }
            Destroy(gameObject);
        }

        private void Restore()
        {
            if (_restored) return;
            _restored = true;
            PlayingNow = false;
            if (FinishedAt < 0f) FinishedAt = Time.realtimeSinceStartup;
            if (TerminalCoop.Active) TerminalCoop.Request(TerminalEvent.AttackDone);
            if (_driving) { Camera.onPreCull -= DriveCamera; _driving = false; }
            _lastFov = -1f;
            try { TerminalAcoustics.SetAmbientSilenced(false); } catch { }
            // the cutscene hides/restores props and the door beat runs right here — a
            // door that comes back on the wrong layer has no prompt at all, so re-assert
            // the interaction layers once the map is handed back (2026-08-11: "the door
            // was fine before the attack cutscene, dead after")
            try { TerminalAcoustics.RepairInteractiveLayers(); } catch { }
            try { TerminalInteractables.ReassertDoors("after attack cutscene"); } catch { }
            try { TerminalLights.CutsceneRelease(); } catch { }
            if (_realCam != null && _savedFov > 0f) _realCam.fieldOfView = _savedFov;
            if (_director != null && _director.state == PlayState.Playing) _director.Stop();
            if (_fadeCanvas != null) { _fadeCanvas.SetActive(false); _fadeCanvas = null; }
            foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
            _hiddenRenderers.Clear();
            foreach (var b in _pausedFx) if (b != null) b.enabled = true;
            _pausedFx.Clear();
            foreach (var cv in _hiddenCanvases) if (cv != null) cv.enabled = true;
            _hiddenCanvases.Clear();
            if (_inputLocked) { GamePlayerOwner.SetIgnoreInputInNPCDialog(false); _inputLocked = false; }
        }

        private IEnumerator Fade(float from, float to, float dur = FadeDur)
        {
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                _fade = Mathf.Lerp(from, to, t / dur);
                yield return null;
            }
            _fade = to;
        }

        private void OnGUI()
        {
            if (_fade <= 0f) return;
            if (_black == null)
            {
                _black = new Texture2D(1, 1);
                _black.SetPixel(0, 0, Color.black);
                _black.Apply();
            }
            GUI.depth = -10000;
            GUI.color = new Color(0f, 0f, 0f, _fade);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _black);
            GUI.color = Color.white;
        }

        private void OnDestroy()
        {
            Restore();
            if (_sceneLoaded) { SceneManager.UnloadSceneAsync(_scene); _sceneLoaded = false; }
            if (_black != null) Destroy(_black);
        }
    }
}
