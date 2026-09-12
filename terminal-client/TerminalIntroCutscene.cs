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
    // retail 1.0's PortCutscene intro (Terminal_area_PortCutscene_00_Intro, timeline
    // rebuilt via SDK Author 21) played at MAP START, unskippable — retail plays it
    // the moment the raid begins, unlike icebreaker's story-beat trigger.
    //
    // trimmed port of IcebreakerTimelineCutscene: same additive-load/drive/unload
    // shape, same onPreCull camera copy (fires after every LateUpdate writer, so the
    // rig gets the last word while the real camera keeps its post stack). the
    // icebreaker-specific holds (volumetrics, volfog profile, native-culling
    // LockState/ForceEnable, interior draw-off) come back as those systems get
    // ported — the culling hold especially: wide shots WILL wedge lamps dark once
    // CullingManager content is restored (see docs/memory + the dark-stern bug).
    public class TerminalIntroCutscene : MonoBehaviour
    {
        // scene ships in the SCENE bundle but NOT the preset — zero cost outside
        // playback, loaded additively here
        private const string SceneName = "Terminal_area_PortCutscene_00_Intro";
        private const float FadeDur = 0.7f;

        // map props the cutscene camera must not see — hidden under the fade before
        // the first cutscene frame, restored on exit (icebreaker's helipad pattern).
        // exact GO name match, searched in the named preset scene only.
        private static readonly (string scene, string name)[] HideDuringCutscene =
        {
            ("Terminal_Design_Stuff", "Inside_Door_Plastic_07_R_210-100 (1)"),
        };

        private static bool _playedThisRaid;
        // when the intro handed control back (realtime) — the attack cutscene's
        // trigger anchors on this (retail: attack fires ~45s after intro spawn-in).
        // set once by Restore(), bail paths included.
        internal static float FinishedAt = -1f;

        internal static void PlayCoop(bool skip)
        {
            if (_playedThisRaid) return;
            _playedThisRaid = true;
            if (skip || !TerminalCoop.LocalHuman || !Plugin.IntroCutscene.Value || !Available)
            {
                FinishedAt = Time.realtimeSinceStartup;
                if (TerminalCoop.LocalHuman) TerminalCoop.Request(TerminalEvent.IntroDone);
                return;
            }
            new GameObject("Terminal_IntroCutscene").AddComponent<TerminalIntroCutscene>();
        }

        internal static void ResetForNewRaid()
        {
            _playedThisRaid = false;
            FinishedAt = -1f;
        }

        public static bool Available
        {
            get
            {
                try { return Application.CanStreamedLevelBeLoaded(SceneName); }
                catch { return false; }
            }
        }

        // map start: OnGameStarted fires once the world is up, before the player has
        // done anything — the retail timing for this cutscene
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_PlayAtRaidStart
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (TerminalCoop.Active) return; // addon starts every peer from one raid event
                if (!TerminalGate.On || _playedThisRaid) return;
                if (!Plugin.IntroCutscene.Value)
                {
                    _playedThisRaid = true;
                    if (FinishedAt < 0f) FinishedAt = Time.realtimeSinceStartup; // downstream phases key off this
                    Plugin.Log.LogInfo("[IntroCutscene] disabled by config — skipped");
                    return;
                }
                if (!Available)
                {
                    Plugin.Log.LogWarning($"[IntroCutscene] '{SceneName}' not loadable (bundle missing the scene?) — skipped");
                    return;
                }
                _playedThisRaid = true;
                var go = new GameObject("Terminal_IntroCutscene");
                go.AddComponent<TerminalIntroCutscene>();
            }
        }

        private Scene _scene;
        private bool _sceneLoaded;
        private PlayableDirector _director;
        private Camera _rigCam;            // the scene's animated camera (disabled, pose source)
        private Camera _realCam;
        private GameObject _fadeCanvas;    // CutsceneRoot/UI_FadeToBlack — retail's own fade rig

        // THE PLAYER'S OWN TOP ON THE CUTSCENE ACTOR (user 2026-08-15): Actor_Top ships
        // a generic packed torso mesh; the PMC's equipped top rides the same skeleton,
        // so the swap is sharedMesh + materials + bones REMAPPED BY NAME onto the actor
        // rig (a SkinnedMeshRenderer's bones array points at ITS OWN skeleton's
        // transforms — copying the mesh without remapping explodes the deformation).
        // any missing bone aborts cleanly: generic top beats a broken one.
        private void TrySwapActorTop()
        {
            if (!Plugin.CutscenePlayerTop.Value) return;
            try
            {
                var body = Singleton<GameWorld>.Instance?.MainPlayer?.PlayerBody;
                EFT.Visual.LoddedSkin skin = null;
                if (!body || !body.BodySkins.TryGetValue(EBodyModelPart.Body, out skin) || !skin)
                { Plugin.Log.LogDebug("[IntroCutscene] no player Body skin — actor keeps the packed top"); return; }

                SkinnedMeshRenderer src = null;
                foreach (var r in skin.GetRenderers())
                    if (r is SkinnedMeshRenderer s && s.sharedMesh
                        && (!src || s.sharedMesh.vertexCount > src.sharedMesh.vertexCount))
                        src = s; // highest-detail LOD
                if (!src) { Plugin.Log.LogDebug("[IntroCutscene] Body skin has no skinned mesh — packed top kept"); return; }

                Transform actorTop = null, actorRoot = null;
                foreach (var rgo in _scene.GetRootGameObjects())
                {
                    actorTop = FindDeep(rgo.transform, "Actor_Top");
                    if (actorTop)
                    {
                        actorRoot = FindDeep(rgo.transform, "Actor_Player");
                        if (!actorRoot) actorRoot = actorTop.root;
                        break;
                    }
                }
                SkinnedMeshRenderer dst = null;
                if (actorTop)
                {
                    dst = actorTop.GetComponent<SkinnedMeshRenderer>();
                    if (!dst) dst = actorTop.GetComponentInChildren<SkinnedMeshRenderer>(true);
                }
                if (!dst) { Plugin.Log.LogDebug("[IntroCutscene] Actor_Top has no SkinnedMeshRenderer — packed top kept"); return; }

                var srcBones = src.bones;
                if (!TerminalCutsceneClothing.TryBind(src, dst, actorRoot, "IntroCutscene",
                    out var mapped, out var mappedRoot, out int auxiliaryBones, out string bindFailure))
                {
                    Plugin.Log.LogWarning($"[IntroCutscene] top swap aborted — {bindFailure}; packed top kept");
                    return;
                }

                dst.sharedMesh = src.sharedMesh;
                dst.sharedMaterials = src.sharedMaterials;
                dst.bones = mapped;
                if (mappedRoot) dst.rootBone = mappedRoot;
                dst.localBounds = src.localBounds;
                dst.updateWhenOffscreen = true;
                Plugin.Log.LogInfo($"[IntroCutscene] actor wears the player's top: '{src.sharedMesh.name}' "
                    + $"({srcBones.Length} bones remapped, {auxiliaryBones} clothing helper bone(s) carried, {src.sharedMaterials.Length} material(s))");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[IntroCutscene] top swap failed: {e.Message}"); }
        }

        private static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
        private UnityEngine.UI.Image _fadeImage;
        private bool _driving;
        private bool _inputLocked;
        private readonly List<Canvas> _hiddenCanvases = new List<Canvas>();
        private readonly List<Renderer> _hiddenRenderers = new List<Renderer>();
        private readonly List<(GameObject go, bool wasActive)> _hiddenProps = new List<(GameObject, bool)>();
        private readonly List<Behaviour> _pausedFx = new List<Behaviour>();
        private float _fade;
        private Texture2D _black;
        private bool _restored;
        private float _savedFov = -1f;     // DriveCamera stomps fov — restore on exit

        private void Start() => StartCoroutine(Run());

        private IEnumerator Run()
        {
            GamePlayerOwner.SetIgnoreInputInNPCDialog(true);
            _inputLocked = true;
            yield return Fade(0f, 1f);

            var load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
            if (load == null) { Bail("LoadSceneAsync returned null"); yield break; }
            while (!load.isDone) yield return null;
            _scene = SceneManager.GetSceneByName(SceneName);
            _sceneLoaded = _scene.IsValid() && _scene.isLoaded;
            if (!_sceneLoaded) { Bail("cutscene scene failed to load"); yield break; }

            // the scene arrived after the raid-start rebind pass — its materials still
            // hold the bundle's broken blobs (white in deferred). rebind now, under
            // the fade; the sceneLoaded capture hook already fenced them as ours.
            try { TerminalShaderRebind.RebindNow(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[IntroCutscene] shader rebind failed: {e.Message}"); }

            // any beams authored inside the cutscene scene were deferred at raid start
            // (the scene didn't exist yet) — idempotent, claims only new lights
            try { TerminalVolumetricLights.Restore(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[IntroCutscene] volumetric pass failed: {e.Message}"); }

            // Track a bounded light bubble around the animated camera. The native
            // culler otherwise follows the player's body, but enabling every map
            // light at once makes the cutscene GPU-bound.
            try { TerminalLights.CutsceneBeginLightTracking(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[IntroCutscene] culler hold failed: {e.Message}"); }

            // director sits at the scene root ("TimeLineDirector") — search all roots,
            // inactive included
            foreach (var rgo in _scene.GetRootGameObjects())
            {
                _director = rgo.GetComponentInChildren<PlayableDirector>(true);
                if (_director != null) break;
            }
            if (_director == null || _director.playableAsset == null)
            { Bail("TimeLineDirector missing or playableAsset empty (Author 21 not run?)"); yield break; }

            // rig camera: first camera anywhere in the scene
            foreach (var rgo in _scene.GetRootGameObjects())
            {
                _rigCam = rgo.GetComponentInChildren<Camera>(true);
                if (_rigCam != null) break;
            }
            if (_rigCam == null) { Bail("no camera in cutscene scene"); yield break; }
            // pose/FOV source only — must never render or listen
            _rigCam.enabled = false;
            var lst = _rigCam.GetComponent<AudioListener>();
            if (lst != null) lst.enabled = false;

            _realCam = EFT.CameraControl.CameraManager.Instance?.Camera;
            if (_realCam == null) _realCam = Camera.main;
            if (_realCam == null) { Bail("no main camera"); yield break; }

            // pull user-flagged map props before the first frame is ever drawn —
            // we're still under the black fade here
            foreach (var (sceneName, goName) in HideDuringCutscene)
            {
                var scn = SceneManager.GetSceneByName(sceneName);
                var go = FindInScene(scn, goName);
                if (go == null)
                {
                    Plugin.Log.LogWarning($"[IntroCutscene] hide target '{goName}' not found in '{sceneName}'");
                    continue;
                }
                _hiddenProps.Add((go, go.activeSelf));
                go.SetActive(false);
                Plugin.Log.LogDebug($"[IntroCutscene] hid '{goName}' for the cutscene");
            }

            // retail's own fade rig — activate for playback, off on exit (the timeline
            // drives FadeToBlackImage's alpha itself). the rip loses the canvas setup
            // AND the image color (first raid: WHITE fade in a screen-corner rect) —
            // rebuild both: overlay canvas, stretch rect, black rgb.
            var fadeTf = FindInScene(_scene, "UI_FadeToBlack");
            _fadeCanvas = fadeTf ? fadeTf : null;
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
                catch (Exception e) { Plugin.Log.LogWarning($"[IntroCutscene] fade rig fix failed: {e.Message}"); }
            }

            // pause player screen effects (painkiller B&W, contusion vignette...) —
            // CC_* image effects driven by EffectsController would grade the cutscene.
            // disable the driver first so it can't re-enable them mid-playback.
            foreach (var b in _realCam.GetComponents<Behaviour>())
                if (b != null && b.enabled && (b is EffectsController || b is CC_Base))
                {
                    b.enabled = false;
                    _pausedFx.Add(b);
                }

            // hide the player — the flying camera would film them standing at spawn
            TrySwapActorTop(); // before the body hides — its renderers must be readable

            var player = Singleton<GameWorld>.Instance?.MainPlayer;
            if (player != null)
                foreach (var r in player.gameObject.GetComponentsInChildren<Renderer>(false))
                    if (r != null && r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }

            // hide the HUD, but never the cutscene's own canvases
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

            // STAGING BARRIER (2026-08-15 + expanded 2026-08-19: user reports
            // ambient popping in, FOV zoom, huge stutters, bot spawn hitches at
            // playback start — all symptoms of the map still loading in). we wait
            // for every known raid-start staging phase to complete AND for the
            // bot count to stabilize AND for the sky's TOD_Sky to be initialized,
            // then do a shader warmup by evaluating the director at t=0 for a
            // couple of frames so all shaders on the first shot compile off-camera.
            // capped hard so a broken stage cant hold the intro hostage.
            {
                float barrier = Time.realtimeSinceStartup + 30f;
                var gw = Singleton<GameWorld>.Instance;
                int lastBotCount = -1;
                float botsStableAt = Time.realtimeSinceStartup;
                const float BotStableFor = 3f;
                bool AmbientReady() => TerminalAcoustics.AmbientStaged;
                bool WeatherReady() => TerminalWeather.TryEnsureRainVisualReady();
                bool SkyReady() { try { var s = UnityEngine.Object.FindObjectOfType<TOD_Sky>(); return s != null && s.Initialized; } catch { return false; } }
                bool BotsReady()
                {
                    int n = 0;
                    try
                    {
                        var w = Singleton<GameWorld>.Instance;
                        if (w?.AllAlivePlayersList != null) n = w.AllAlivePlayersList.Count;
                    }
                    catch { }
                    if (n != lastBotCount) { lastBotCount = n; botsStableAt = Time.realtimeSinceStartup; return false; }
                    return Time.realtimeSinceStartup - botsStableAt >= BotStableFor;
                }
                while (Time.realtimeSinceStartup < barrier)
                {
                    if (AmbientReady() && WeatherReady() && SkyReady() && BotsReady()) break;
                    yield return null;
                }
                // pre-apply lights so ambient is already correct on the first drawn
                // frame instead of popping in on the next 30-frame tick
                try { TerminalLights.ApplyAmbient(); } catch { }
                // sync the real camera to the rig up-front so the FOV matches even
                // before the first onPreCull fires — no snap-in on frame 1
                try
                {
                    float wantFov = _rigCam.fieldOfView;
                    if (wantFov > 1f && wantFov < 179f) _realCam.fieldOfView = wantFov;
                    _realCam.transform.SetPositionAndRotation(_rigCam.transform.position, _rigCam.transform.rotation);
                }
                catch { }
                // shader warmup: evaluate the director at t=0 so every material on
                // the opening frame compiles now (offscreen, we havent Play()d yet)
                try { _director.time = 0.001; _director.Evaluate(); } catch { }
                yield return null;
                yield return null;
                yield return null;
                Plugin.Log.LogInfo($"[IntroCutscene] staging barrier released — ambient={TerminalAcoustics.AmbientStaged} "
                    + $"weather={TerminalWeather.Staged} sky={SkyReady()} bots={lastBotCount} "
                    + $"(waited {(30f - (barrier - Time.realtimeSinceStartup)):0.0}s)");
            }

            _director.extrapolationMode = DirectorWrapMode.Hold; // no loop surprises
            // AUDIO CLOCK, not frame time (2026-08-11: a hitch mid-cutscene left the
            // picture running behind its own soundtrack for the rest of the take).
            // GameTime advances by delta, so every dropped frame is picture time LOST
            // and the drift never recovers; DSPClock drives the timeline off the same
            // clock the audio plays on, so a hitch costs frames, not sync.
            _director.timeUpdateMode = DirectorUpdateMode.DSPClock;
            TerminalAcoustics.SetAmbientSilenced(true); // the timeline owns these seconds
            _director.Play();
            if (TerminalCoop.Active && TerminalCoop.IntroStarted >= 0)
                _director.time = Math.Min(_director.duration - 0.05, Math.Max(0, TerminalCoop.Now - TerminalCoop.IntroStarted));
            TerminalSubtitles.Show("intro_nocase", _director);
            Plugin.Log.LogInfo($"[IntroCutscene] playing '{_director.playableAsset.name}' " +
                               $"({_director.duration:0.0}s, unskippable)");
            yield return Fade(1f, 0f);

            // skippable by config (user ask 2026-08-18) — SPACE bails to the raid,
            // same teardown path as a natural finish. hard stop stays as the net.
            double dur = _director.duration;
            float hardStop = Time.realtimeSinceStartup + (float)dur + 10f;
            while (Time.realtimeSinceStartup < hardStop)
            {
                if (_director == null) break;
                if (_director.state != PlayState.Playing) break;
                if (_director.time >= dur - 0.05) break;
                if (Plugin.IntroCutsceneSkippable.Value && Input.GetKeyDown(KeyCode.Space))
                { Plugin.Log.LogInfo("[IntroCutscene] skipped by player"); break; }
                // the timeline may animate the fade image's FULL color (white keys from
                // the rip) — enforce black rgb per frame, alpha stays the timeline's
                if (_fadeImage != null)
                {
                    var fc = _fadeImage.color;
                    if (fc.r > 0f || fc.g > 0f || fc.b > 0f)
                        _fadeImage.color = new Color(0f, 0f, 0f, fc.a);
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
            // brief linger at black, then reveal — gives late systems a few frames to
            // settle for the returned camera before anything pops on screen
            yield return new WaitForSecondsRealtime(0.8f);
            yield return Fade(1f, 0f, 1.6f);
            Destroy(gameObject);
        }

        // by name anywhere in the loaded scene, inactive included
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

        // FOV POP GUARD (2026-08-11: "the cutscene camera sometimes zooms out and comes
        // back"). the rig camera's own fov is timeline-animated, and a rip-damaged curve
        // (or a frame where the binding hasn't evaluated yet) reads as a wild value for
        // a few frames. retail cutscene framing never swings more than a few degrees per
        // frame, so clamp the RATE: big jumps are followed smoothly instead of snapping,
        // which turns a jarring zoom into an imperceptible correction. DevMode logs the
        // outliers so the offending curve can be found if it's authored damage.
        private float _lastFov = -1f;

        private void DriveCamera(Camera cam)
        {
            if (cam != _realCam || _rigCam == null) return;
            var t = _rigCam.transform;
            cam.transform.SetPositionAndRotation(t.position, t.rotation);
			TerminalLights.TrackCutsceneCamera(t.position);

            // RATE CLAMP REVERTED 2026-08-11 (user: "the fov thing got made worse, i saw
            // the zoom MORE frequently"). smoothing turned a one-frame flicker into a
            // multi-frame ramp — far more visible than the artefact it was hiding. take
            // the authored value straight, and only reject values that cannot be real.
            float want = _rigCam.fieldOfView;
            if (want <= 1f || want >= 179f)
            {
                if (Plugin.DevMode.Value)
                    Plugin.Log.LogWarning($"[IntroCutscene] rig fov absurd ({want:0.0}) at t={_director?.time:0.00}s — holding {_lastFov:0.0}");
                if (_lastFov > 0f) cam.fieldOfView = _lastFov;
                return;
            }
            if (Plugin.DevMode.Value && _lastFov > 0f && Mathf.Abs(want - _lastFov) > 5f)
                Plugin.Log.LogWarning($"[IntroCutscene] fov jump {_lastFov:0.0} -> {want:0.0} at t={_director?.time:0.00}s "
                    + $"(frame {Time.unscaledDeltaTime * 1000f:0}ms) — passed through");
            _lastFov = want;
            cam.fieldOfView = want;
        }

        private void Bail(string why)
        {
            // a broken timeline must never brick raid start — restore and play on
            Plugin.Log.LogWarning($"[IntroCutscene] {why} — cutscene skipped");
            Restore();
            if (_sceneLoaded) { SceneManager.UnloadSceneAsync(_scene); _sceneLoaded = false; }
            Destroy(gameObject);
        }

        // idempotent — scripted exit + OnDestroy teardown net
        private void Restore()
        {
            if (_restored) return;
            _restored = true;
            TerminalSubtitles.Hide();
            if (FinishedAt < 0f) FinishedAt = Time.realtimeSinceStartup;
            if (TerminalCoop.Active) TerminalCoop.Request(TerminalEvent.IntroDone);
            if (_driving) { Camera.onPreCull -= DriveCamera; _driving = false; }
            _lastFov = -1f;
            try { TerminalAcoustics.SetAmbientSilenced(false); } catch { }
            try { TerminalAcoustics.RepairInteractiveLayers(); } catch { }
            try { TerminalInteractables.ReassertDoors("after intro cutscene"); } catch { }
            try { TerminalLights.CutsceneRelease(); } catch { }
            if (_realCam != null && _savedFov > 0f) _realCam.fieldOfView = _savedFov;
            if (_director != null && _director.state == PlayState.Playing) _director.Stop();
            if (_fadeCanvas != null) { _fadeCanvas.SetActive(false); _fadeCanvas = null; }
            // hand the props back as we found them — a bail must not leave the map
            // short a door
            foreach (var (go, wasActive) in _hiddenProps) if (go != null) go.SetActive(wasActive);
            _hiddenProps.Clear();
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
