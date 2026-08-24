using System;
using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace Manimal.Terminal
{
    // THE ENDING CUTSCENE (Terminal_area_PortCutscene_02_Ending_00, retail level650,
    // timeline rebuilt via SDK Author 21B): retail plays one of four evac endings
    // when the ZUBR leaves; offline we play Ending_00 for every survivor. flow:
    // the Zubr exfil countdown completes -> LocalGame.Stop intercepted -> fade to
    // black -> ending plays (you and Yuri on the boat) -> the held Stop releases
    // and the raid ends Survived behind black (retail's exit_survived).
    //
    // variant layers, applied as authored track mutes (retail's ManageTimelineAssets
    // ByExistQuest / ActivateAudioTrackBySeason / ByPlayerVoiceKey actions are
    // 1.0-only):
    //  - quest Case/NoCase: NoCase default — SPT has no 1.0 final quest
    //  - season: Winter_* tracks only when the raid is winter
    //  - voice: the Bear_N(_Eng) dialogue track matching the profile voice, others off
    internal static class TerminalEndingCutscene
    {
        internal const string SceneName = "Terminal_area_PortCutscene_02_Ending_00";

        internal static bool Available
        {
            get
            {
                try { return Application.CanStreamedLevelBeLoaded(SceneName); }
                catch { return false; }
            }
        }

        private static bool _played;
        internal static bool PassThrough; // re-entry latch for the released Stop
        internal static bool PlayingNow;  // stencil pass + friends detach while true

        internal static void ResetForRaid()
        {
            _played = false;
            PassThrough = false;
            PlayingNow = false;
        }

        [HarmonyPatch(typeof(LocalGame), nameof(LocalGame.Stop))]
        internal static class Patch_InterceptExtraction
        {
            [HarmonyPrefix]
            private static bool Prefix(LocalGame __instance, string profileId, ExitStatus exitStatus, string exitName, float delay)
            {
                try
                {
                    if (PassThrough) return true;
                    if (!TerminalGate.On || !Plugin.EndingCutscene.Value) return true;
                    // the 3-minute evac timer keeps RUNNING under the take — its
                    // MissingInAction stop raced in mid-cutscene and ended the raid
                    // under us (2026-08-18 raid: MIA screen + truncated ending). while
                    // the take plays, every stop that isn't ours gets eaten; the
                    // runner's release is the only exit.
                    if (PlayingNow)
                    {
                        Plugin.Log.LogWarning($"[Ending] swallowed a {exitStatus} stop during the take — the runner owns the raid end");
                        return false;
                    }
                    if (_played) return true;
                    if (exitStatus != ExitStatus.Survived || exitName != TerminalFinalExit.ExitName) return true;
                    if (!Available)
                    {
                        Plugin.Log.LogInfo("[Ending] ending scene not in bundle — extracting without the cutscene");
                        return true;
                    }
                    var player = Singleton<GameWorld>.Instance?.MainPlayer;
                    if (player == null || player.ProfileId != profileId) return true;

                    _played = true;
                    Plugin.Log.LogInfo("[Ending] Zubr extraction — holding raid end for the ending cutscene");
                    var go = new GameObject("Terminal_EndingCutscene");
                    var c = go.AddComponent<EndingRunner>();
                    c.Game = __instance;
                    c.ProfileId = profileId;
                    c.ExitName = exitName;
                    return false; // the runner releases this Stop when the take ends
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Ending] intercept failed, extracting normally: {e.Message}");
                    return true;
                }
            }
        }
    }

    internal class EndingRunner : MonoBehaviour
    {
        public LocalGame Game;
        public string ProfileId;
        public string ExitName;

        private Scene _scene;
        private bool _sceneLoaded;
        private PlayableDirector _director;
        private Camera _rigCam, _realCam;
        private GameObject _fadeCanvas;
        private UnityEngine.UI.Image _fadeImage;
        private readonly List<Canvas> _hiddenCanvases = new List<Canvas>();
        private readonly List<Renderer> _hiddenRenderers = new List<Renderer>();
        private readonly List<Behaviour> _pausedFx = new List<Behaviour>();
        private readonly List<(GameObject go, bool wasActive)> _hiddenProps = new List<(GameObject, bool)>();

        // retail's authored hide list for the endings (level628 action
        // 'deactivate_objects_ending_1'): the docked Zubr + its trap zone, both
        // container ships, a pallet crate — the cutscene's own Zubr rig plays the
        // departure and the parked copies would duplicate in the wide sea shots.
        // paths relative to the Design_Stuff root GO.
        private static readonly string[] HideDuringCutscene =
        {
            "OO/Ships/Zubr_01/Zubr",
            "OO/Ships/Zubr_01/Trap",
            "OO/Ships/Container_ship_01",
            "OO/Ships/Container_ship_02",
            "OO/pallet_weapon_box_1_update (6)",
        };
        private bool _driving;
        private bool _released;
        private bool _inputLocked;

        // own full-screen fade (the raid is ending — keep it self-contained)
        private Canvas _ownCanvas;
        private UnityEngine.UI.Image _ownFade;

        private void Start()
        {
            TerminalEndingCutscene.PlayingNow = true;
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            BuildOwnFade();
            yield return Fade(0f, 1f, 0.6f);

            var load = SceneManager.LoadSceneAsync(TerminalEndingCutscene.SceneName, LoadSceneMode.Additive);
            if (load == null) { Bail("LoadSceneAsync returned null"); yield break; }
            while (!load.isDone) yield return null;
            _scene = SceneManager.GetSceneByName(TerminalEndingCutscene.SceneName);
            _sceneLoaded = _scene.IsValid() && _scene.isLoaded;
            if (!_sceneLoaded) { Bail("ending scene failed to load"); yield break; }

            try { TerminalShaderRebind.RebindNow(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ending] shader rebind failed: {e.Message}"); }
            try { TerminalLights.CutsceneShowAll(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ending] culler hold failed: {e.Message}"); }

            foreach (var rgo in _scene.GetRootGameObjects())
            {
                _director = rgo.GetComponentInChildren<PlayableDirector>(true);
                if (_director != null) break;
            }
            if (_director == null || _director.playableAsset == null)
            { Bail("director missing or playableAsset empty (Author 21B not run?)"); yield break; }

            // offline audit (2026-08-18) proved tracks/clips/bindings all healthy in the
            // shipped scene, so the static actors are a runtime effect — kill the two
            // classic silent stoppers wholesale before playback: animator culling and
            // skinned-mesh offscreen culling (ripped rigs carry drift-suspect bounds)
            int anims = 0, smrs = 0;
            foreach (var rgo in _scene.GetRootGameObjects())
            {
                foreach (var a in rgo.GetComponentsInChildren<Animator>(true))
                { a.cullingMode = AnimatorCullingMode.AlwaysAnimate; anims++; }
                foreach (var s in rgo.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                { s.updateWhenOffscreen = true; smrs++; }
            }
            Plugin.Log.LogInfo($"[Ending] anti-cull: {anims} animator(s) AlwaysAnimate, {smrs} skinned mesh(es) updateWhenOffscreen");

            // NO runtime avatar donor (removed 2026-08-18): the 'has a human pelvis
            // descendant' test also matched the Zubr/scenery ROOT animators (the cast
            // nests under them) — an avatar on those flips them humanoid and kills
            // their generic prop tracks (the Zubr door + pry tube stopped animating).
            // the actor avatars are authored in the editor scene now.

            ApplyVariantMutes();
            RepairBindings();

            foreach (var rgo in _scene.GetRootGameObjects())
            {
                _rigCam = rgo.GetComponentInChildren<Camera>(true);
                if (_rigCam != null) break;
            }
            if (_rigCam == null) { Bail("no camera in ending scene"); yield break; }
            _rigCam.enabled = false;
            var lst = _rigCam.GetComponent<AudioListener>();
            if (lst != null) lst.enabled = false;

            _realCam = CameraClass.Instance?.Camera;
            if (_realCam == null) _realCam = Camera.main;
            if (_realCam == null) { Bail("no main camera"); yield break; }

            // retail's own fade rig — same rip damage as the intro's (overlay + black)
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
                        cv.sortingOrder = 9998; // under our own fade
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
                catch (Exception e) { Plugin.Log.LogWarning($"[Ending] fade rig fix failed: {e.Message}"); }
            }

            foreach (var b in _realCam.GetComponents<Behaviour>())
                if (b != null && b.enabled && (b is EffectsController || b is CC_Base))
                {
                    b.enabled = false;
                    _pausedFx.Add(b);
                }

            // the authored map hides, under the black fade
            HideRetailProps();

            // the actor wears YOUR gear (intro pattern, both halves): before the body
            // hides — its renderers must be readable
            TrySwapActorPart(EBodyModelPart.Body, "Actor_Top");
            TrySwapActorPart(EBodyModelPart.Feet, "Actor_Pants");

            // lock input — the raid still simulates under the take and a controllable
            // invisible player waves his very visible gun through the shots (2026-08-18)
            try { GamePlayerOwner.SetIgnoreInputInNPCDialog(true); _inputLocked = true; } catch { }

            // hide EVERY live body (bots included), not just the main player
            var gw2 = Singleton<GameWorld>.Instance;
            if (gw2 != null && gw2.AllAlivePlayersList != null)
                foreach (var pl in gw2.AllAlivePlayersList)
                {
                    if (pl == null) continue;
                    foreach (var r in pl.gameObject.GetComponentsInChildren<Renderer>(false))
                        if (r != null && r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }
                }
            // the FPS viewmodel (hands + weapon) lives under the camera, not the player
            if (_realCam)
                foreach (var r in _realCam.GetComponentsInChildren<Renderer>(true))
                    if (r != null && r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }

            foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>())
                if (cv != null && cv.enabled && cv.isRootCanvas
                    && cv.renderMode == RenderMode.ScreenSpaceOverlay
                    && cv.gameObject.scene != _scene && cv != _ownCanvas)
                {
                    cv.enabled = false;
                    _hiddenCanvases.Add(cv);
                }

            Camera.onPreCull += DriveCamera;
            StartCoroutine(RehideSweep());
            _driving = true;

            _director.extrapolationMode = DirectorWrapMode.Hold;
            _director.timeUpdateMode = DirectorUpdateMode.DSPClock; // hitches cost frames, not sync
            TerminalAcoustics.SetAmbientSilenced(true);
            _director.Play();
            TerminalSubtitles.Show("ending_00", _director);
            Plugin.Log.LogInfo($"[Ending] playing '{_director.playableAsset.name}' ({_director.duration:0.0}s, SPACE skips)");
            StartCoroutine(BindingProbe());
            yield return Fade(1f, 0f, 0.8f);

            double dur = _director.duration;
            float hardStop = Time.realtimeSinceStartup + (float)dur + 10f;

            // retail's UI_FadeToBlack alpha is a timeline track. on this rip it can
            // arrive PINNED OPAQUE, which blacks out the entire take (2026-08-21:
            // black screen, a couple frames of the very end, black again — the
            // director ran to 87s the whole time). we can't tell a dead binding from
            // an authored hold up front, so watch it: alpha that never moves for 2s
            // while opaque is not being driven, and we take it out. our own _ownFade
            // owns the real fade in/out regardless, so nothing is lost either way.
            float lastAlpha = _fadeImage != null ? _fadeImage.color.a : 0f;
            float lastAlphaMove = Time.realtimeSinceStartup;
            bool fadeNeutered = false;

            while (Time.realtimeSinceStartup < hardStop)
            {
                if (_director == null) break;
                if (_director.state != PlayState.Playing) break;
                if (_director.time >= dur - 0.05) break;
                if (Input.GetKeyDown(KeyCode.Space)) { Plugin.Log.LogInfo("[Ending] skipped"); break; }
                if (_fadeImage != null)
                {
                    var fc = _fadeImage.color;
                    if (fc.r > 0f || fc.g > 0f || fc.b > 0f)
                    {
                        _fadeImage.color = new Color(0f, 0f, 0f, fc.a);
                        fc = _fadeImage.color;
                    }

                    if (fadeNeutered)
                    {
                        if (fc.a > 0.001f) _fadeImage.color = new Color(0f, 0f, 0f, 0f);
                    }
                    else if (Mathf.Abs(fc.a - lastAlpha) > 0.002f)
                    {
                        lastAlpha = fc.a;
                        lastAlphaMove = Time.realtimeSinceStartup;   // track is alive, leave it alone
                    }
                    else if (fc.a > 0.5f && Time.realtimeSinceStartup - lastAlphaMove > 2f)
                    {
                        fadeNeutered = true;
                        _fadeImage.color = new Color(0f, 0f, 0f, 0f);
                        Plugin.Log.LogWarning($"[Ending] retail UI_FadeToBlack sat opaque (a={fc.a:0.00}) for 2s with no "
                            + "timeline movement — its alpha track is dead on this rip. neutralised for the rest of the "
                            + "take; our own fade still handles the in/out.");
                    }
                }
                yield return null;
            }

            yield return Fade(0f, 1f, 0.6f);
            Release("finished");
        }

        // retail's variant selection, reduced to authored track mutes
        private void ApplyVariantMutes()
        {
            try
            {
                var tl = _director.playableAsset as TimelineAsset;
                if (tl == null) return;

                string voice = "";
                try
                {
                    var prof = Singleton<GameWorld>.Instance?.MainPlayer?.Profile;
                    if (prof != null)
                        voice = Singleton<CustomizationSolverClass>.Instance
                            .GetVoice(prof.Customization[EBodyModelPart.Voice])?.Name ?? "";
                }
                catch { }
                bool winter = false;
                try
                {
                    var gw = Singleton<GameWorld>.Instance;
                    var seasonProp = gw != null ? AccessTools.Property(gw.GetType(), "Season") : null;
                    var v = seasonProp != null ? seasonProp.GetValue(gw) : null;
                    winter = v != null && v.ToString().IndexOf("Winter", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch { }

                // dialogue tracks are '<VoiceKey>_Voice' — full USEC + BEAR sets exist
                // (2026-08-18 raid: profile Usec_3 has an authored Usec_3_Voice track).
                // exact key wins; no match falls back to the faction's _1
                var trackNames = new List<string>();
                var all = new List<TrackAsset>();
                foreach (var t in tl.GetOutputTracks()) { all.Add(t); trackNames.Add(t.name); }
                foreach (var t in tl.GetRootTracks()) if (!all.Contains(t)) { all.Add(t); trackNames.Add(t.name); }
                string wantTrack = null;
                foreach (var n in trackNames) if (n == voice + "_Voice") { wantTrack = n; break; }
                if (wantTrack == null)
                {
                    var faction = voice.StartsWith("Usec", StringComparison.OrdinalIgnoreCase) ? "Usec" : "Bear";
                    foreach (var n in trackNames) if (n.StartsWith(faction) && n.EndsWith("_Voice")) { wantTrack = n; break; }
                }

                // the built asset ships every variant track PRE-MUTED (2026-08-18
                // raid: 0/67 changed and no voice played) — selection here means
                // UNMUTING the chosen ones, not muting the rest
                int muted = 0, unmuted = 0;
                foreach (var track in all)
                {
                    var n = track.name ?? "";
                    bool? want = null;
                    if (n.EndsWith("_Voice")) want = (n == wantTrack);
                    if (n.Contains("Winter_Additional")) want = winter;
                    if (n.Contains("Summer_Additional")) want = !winter;
                    if (n.Contains("Case") && !n.Contains("NoCase")) want = false;
                    if (want == null) continue;
                    if (track.muted == !want.Value) continue;
                    track.muted = !want.Value;
                    if (want.Value) unmuted++; else muted++;
                }
                Plugin.Log.LogInfo($"[Ending] variants applied: voice track '{wantTrack ?? "none"}' (profile '{voice}'), "
                    + $"{(winter ? "winter" : "summer")}, NoCase — {unmuted} unmuted, {muted} muted of {all.Count}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ending] variant mutes failed: {e.Message}"); }
        }


        private void HideRetailProps()
        {
            try
            {
                var root = TerminalRigFill.FindRootNamed("SBG_Terminal_Design_Stuff");
                if (!root)
                {
                    // rip variants drop the SBG_ prefix — fall back to a scene-root walk
                    var scn = SceneManager.GetSceneByName("Terminal_Design_Stuff");
                    if (scn.IsValid() && scn.isLoaded && scn.GetRootGameObjects().Length > 0)
                        root = scn.GetRootGameObjects()[0].transform;
                }
                if (!root) { Plugin.Log.LogWarning("[Ending] Design_Stuff root not found — retail hide list skipped"); return; }
                foreach (var rel in HideDuringCutscene)
                {
                    var t = root.Find(rel);
                    if (t == null) { Plugin.Log.LogDebug($"[Ending] hide target '{rel}' not found"); continue; }
                    _hiddenProps.Add((t.gameObject, t.gameObject.activeSelf));
                    t.gameObject.SetActive(false);
                }
                Plugin.Log.LogInfo($"[Ending] retail hide list applied ({_hiddenProps.Count}/{HideDuringCutscene.Length})");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ending] retail hides failed: {e.Message}"); }
        }

        // the player's own clothing on the cutscene actor — the intro's top swap
        // generalized to any body part / actor GO pair: sharedMesh + materials +
        // bones REMAPPED BY NAME onto the actor rig; any missing bone aborts
        // cleanly (generic clothing beats a broken one)
        private void TrySwapActorPart(EBodyModelPart part, string actorGoName)
        {
            if (!Plugin.CutscenePlayerTop.Value) return;
            try
            {
                var body = Singleton<GameWorld>.Instance?.MainPlayer?.PlayerBody;
                EFT.Visual.LoddedSkin skin = null;
                if (!body || !body.BodySkins.TryGetValue(part, out skin) || !skin)
                { Plugin.Log.LogDebug($"[Ending] no player {part} skin — actor keeps packed {actorGoName}"); return; }

                SkinnedMeshRenderer src = null;
                foreach (var r in skin.GetRenderers())
                    if (r is SkinnedMeshRenderer s && s.sharedMesh
                        && (!src || s.sharedMesh.vertexCount > src.sharedMesh.vertexCount))
                        src = s; // highest-detail LOD
                if (!src) { Plugin.Log.LogDebug($"[Ending] {part} skin has no skinned mesh — packed {actorGoName} kept"); return; }

                Transform actorPart = null, actorRoot = null;
                foreach (var rgo in _scene.GetRootGameObjects())
                {
                    actorPart = FindDeep(rgo.transform, actorGoName);
                    if (actorPart)
                    {
                        actorRoot = FindDeep(rgo.transform, "Actor_Player");
                        if (!actorRoot) actorRoot = actorPart.root;
                        break;
                    }
                }
                SkinnedMeshRenderer dst = null;
                if (actorPart)
                {
                    dst = actorPart.GetComponent<SkinnedMeshRenderer>();
                    if (!dst) dst = actorPart.GetComponentInChildren<SkinnedMeshRenderer>(true);
                }
                if (!dst) { Plugin.Log.LogDebug($"[Ending] {actorGoName} has no SkinnedMeshRenderer — packed kept"); return; }

                // bone source priority (user video 2026-08-18: swapped body froze
                // while the rest of the cast animated): the packed mesh's OWN bones
                // are by construction the animated skeleton — a whole-subtree walk
                // can hit dead duplicate bone chains first (clothing armature
                // copies share names) and bind the swap to a skeleton nothing drives
                var boneByName = new Dictionary<string, Transform>();
                foreach (var b in dst.bones)
                    if (b && !boneByName.ContainsKey(b.name)) boneByName[b.name] = b;
                if (dst.rootBone && !boneByName.ContainsKey(dst.rootBone.name))
                    boneByName[dst.rootBone.name] = dst.rootBone;
                var actorAnim = actorRoot.GetComponentInChildren<Animator>(true);
                var skelRoot = actorAnim ? actorAnim.transform : actorRoot;
                foreach (var t in skelRoot.GetComponentsInChildren<Transform>(true))
                    if (!boneByName.ContainsKey(t.name)) boneByName[t.name] = t;

                var srcBones = src.bones;
                var mapped = new Transform[srcBones.Length];
                var missing = new List<string>();
                for (int i = 0; i < srcBones.Length; i++)
                {
                    var n = srcBones[i] ? srcBones[i].name : null;
                    if (n == null || !boneByName.TryGetValue(n, out mapped[i])) missing.Add(n ?? $"#{i}");
                }
                if (missing.Count > 0)
                {
                    Plugin.Log.LogWarning($"[Ending] {actorGoName} swap aborted — {missing.Count} bone(s) not on the actor rig "
                        + $"({string.Join(", ", missing.GetRange(0, Math.Min(8, missing.Count)))}"
                        + (missing.Count > 8 ? "...)" : ")") + " — skeleton mismatch, packed kept");
                    return;
                }

                dst.sharedMesh = src.sharedMesh;
                dst.sharedMaterials = src.sharedMaterials;
                dst.bones = mapped;
                if (src.rootBone && boneByName.TryGetValue(src.rootBone.name, out var rb)) dst.rootBone = rb;
                dst.localBounds = src.localBounds;
                // freeze forensics: which rig did the bones land on
                int onAnim = 0;
                for (int i = 0; i < mapped.Length; i++)
                    if (mapped[i] && mapped[i].IsChildOf(skelRoot)) onAnim++;
                Plugin.Log.LogInfo($"[Ending] actor wears the player's {part}: '{src.sharedMesh.name}' "
                    + $"({srcBones.Length} bones remapped, {onAnim} on the animated rig '{skelRoot.name}', "
                    + $"{src.sharedMaterials.Length} material(s))");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ending] {actorGoName} swap failed: {e.Message}"); }
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

        // the editor preview animates but the raid doesn't — the bundle pass loses
        // some director bindings. re-wire null AnimationTrack/AudioTrack bindings
        // from the authored table (terminal_ending_bindings.json: track name ->
        // scene path + component kind), adding the Animator when the rip dropped it.
        private void RepairBindings()
        {
            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_ending_bindings.json");
                if (!System.IO.File.Exists(path)) return;
                var rows = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path))["bindings"]
                    as Newtonsoft.Json.Linq.JArray;
                if (rows == null) return;

                // scene path index
                var index = new Dictionary<string, Transform>();
                foreach (var rgo in _scene.GetRootGameObjects())
                    TerminalRigFill.IndexTree(rgo.transform, rgo.name, index);

                var tl = _director.playableAsset as TimelineAsset;
                if (tl == null) return;
                int nulls = 0, wired = 0, addedAnim = 0;
                var used = new HashSet<int>();
                foreach (var track in tl.GetOutputTracks())
                {
                    var bound = _director.GetGenericBinding(track);
                    if (bound != null) continue;
                    if (!(track is AnimationTrack) && !(track is AudioTrack)) continue;
                    nulls++;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (used.Contains(i)) continue;
                        var row = (Newtonsoft.Json.Linq.JObject)rows[i];
                        if (row.Value<string>("track") != track.name) continue;
                        var sp = row.Value<string>("path");
                        var kind = row.Value<string>("kind");
                        if (string.IsNullOrEmpty(sp) || !index.TryGetValue(sp, out var t)) continue;
                        used.Add(i);
                        UnityEngine.Object obj = null;
                        if (kind == "GameObject") obj = t.gameObject;
                        else if (kind == "Transform") obj = t;
                        else if (kind == "Animator")
                        {
                            var a = t.GetComponent<Animator>();
                            if (!a) { a = t.gameObject.AddComponent<Animator>(); addedAnim++; }
                            obj = a;
                        }
                        else if (kind == "AudioSource")
                        {
                            var a = t.GetComponent<AudioSource>();
                            if (!a) a = t.gameObject.AddComponent<AudioSource>();
                            obj = a;
                        }
                        if (obj != null) { _director.SetGenericBinding(track, obj); wired++; }
                        break;
                    }
                }
                if (nulls > 0)
                    Plugin.Log.LogWarning($"[Ending] binding repair: {nulls} null binding(s) found, {wired} re-wired"
                        + (addedAnim > 0 ? $", {addedAnim} Animator(s) added" : ""));
                if (wired > 0) _director.RebuildGraph();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ending] binding repair failed: {e.Message}"); }
        }

        // runtime truth for the static-actor hunt: the shipped data audits clean, so
        // whatever kills the animation only exists in-game. sample every animation
        // track's bound animator + a deep bone, then report who actually moved.
        private IEnumerator BindingProbe()
        {
            yield return new WaitForSecondsRealtime(1f);
            var names = new List<string>();
            var probes = new List<Transform>();
            var starts = new List<Vector3>();
            try
            {
                var tl = _director != null ? _director.playableAsset as TimelineAsset : null;
                if (tl == null) yield break;
                foreach (var track in tl.GetOutputTracks())
                {
                    if (!(track is AnimationTrack)) continue;
                    var a = _director.GetGenericBinding(track) as Animator;
                    if (!a)
                    {
                        Plugin.Log.LogWarning($"[Ending][probe] '{track.name}': binding null or not an Animator");
                        continue;
                    }
                    var smr = a.GetComponentInChildren<SkinnedMeshRenderer>(true);
                    var probe = smr && smr.rootBone ? smr.rootBone : a.transform;
                    names.Add(track.name); probes.Add(probe); starts.Add(probe.position);
                    Plugin.Log.LogInfo($"[Ending][probe] '{track.name}' -> {a.gameObject.name}"
                        + $" active={a.gameObject.activeInHierarchy} enabled={a.enabled}"
                        + $" avatar={(a.avatar ? a.avatar.name : "none")} bone='{probe.name}'");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ending][probe] setup failed: {e.Message}"); yield break; }

            // the swapped gear itself — the one body the 2026-08-18 video shows frozen
            try
            {
                foreach (var nm in new[] { "Actor_Top", "Actor_Pants" })
                    foreach (var rgo in _scene.GetRootGameObjects())
                    {
                        var t = FindDeep(rgo.transform, nm);
                        var smr = t ? t.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
                        if (!smr || smr.bones == null || smr.bones.Length == 0 || !smr.bones[0]) continue;
                        var b = smr.bones[0];
                        names.Add("swap:" + nm); probes.Add(b); starts.Add(b.position);
                        Plugin.Log.LogInfo($"[Ending][probe] swap '{nm}' mesh='{(smr.sharedMesh ? smr.sharedMesh.name : "null")}'"
                            + $" bone0='{b.name}' visible={smr.isVisible} enabled={smr.enabled}");
                        break;
                    }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ending][probe] swap probe failed: {e.Message}"); }

            yield return new WaitForSecondsRealtime(2f);
            try
            {
                for (int i = 0; i < probes.Count; i++)
                {
                    if (!probes[i]) continue;
                    float moved = (probes[i].position - starts[i]).magnitude;
                    Plugin.Log.LogInfo($"[Ending][probe] '{names[i]}' bone '{probes[i].name}' moved {moved:F3}m in 2s"
                        + (moved < 0.001f ? "  << STATIC" : ""));
                }
                if (_director != null)
                    Plugin.Log.LogInfo($"[Ending][probe] director time={_director.time:F2} state={_director.state}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ending][probe] readback failed: {e.Message}"); }
        }

        // the one-shot hide misses what the extract-era churn re-enables — the
        // third-person rifle floats at the exfil through the opening shots
        // (2026-08-18). re-sweep players, camera children and the weapon root for
        // the first seconds of the take.
        private IEnumerator RehideSweep()
        {
            for (int i = 0; i < 10; i++)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                if (_released) yield break;
                try
                {
                    var gw = Singleton<GameWorld>.Instance;
                    if (gw != null && gw.AllAlivePlayersList != null)
                        foreach (var pl in gw.AllAlivePlayersList)
                        {
                            if (pl == null) continue;
                            foreach (var r in pl.gameObject.GetComponentsInChildren<Renderer>(true))
                                if (r != null && r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }
                            var hc = pl.HandsController;
                            var wr = hc != null
                                ? HarmonyLib.AccessTools.Property(hc.GetType(), "WeaponRoot")?.GetValue(hc) as Transform
                                : null;
                            if (wr)
                                foreach (var r in wr.GetComponentsInChildren<Renderer>(true))
                                    if (r != null && r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }
                        }
                    if (_realCam)
                        foreach (var r in _realCam.GetComponentsInChildren<Renderer>(true))
                            if (r != null && r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }

                    // tactical lasers draw a PROCEDURAL mesh (LaserBeam builds its
                    // own — no Renderer to disable; user 2026-08-18: green beam
                    // haunting the exfil). kill the behaviours; flashlight Lights
                    // under players go with them. restored on release via _pausedFx.
                    foreach (var lb in UnityEngine.Object.FindObjectsOfType<LaserBeam>())
                        if (lb != null && lb.enabled) { lb.enabled = false; _pausedFx.Add(lb); }
                    var gw3 = Singleton<GameWorld>.Instance;
                    if (gw3 != null && gw3.AllAlivePlayersList != null)
                        foreach (var pl in gw3.AllAlivePlayersList)
                        {
                            if (pl == null) continue;
                            foreach (var li in pl.gameObject.GetComponentsInChildren<Light>(true))
                                if (li != null && li.enabled) { li.enabled = false; _pausedFx.Add(li); }
                        }
                }
                catch { }
            }
        }

        private void DriveCamera(Camera cam)
        {
            if (cam != _realCam || _rigCam == null) return;
            var t = _rigCam.transform;
            cam.transform.SetPositionAndRotation(t.position, t.rotation);
            float want = _rigCam.fieldOfView;
            if (want > 1f && want < 179f) cam.fieldOfView = want;
        }

        private void BuildOwnFade()
        {
            var go = new GameObject("Terminal_EndingFade");
            go.transform.SetParent(transform, false);
            _ownCanvas = go.AddComponent<Canvas>();
            _ownCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _ownCanvas.sortingOrder = 9999;
            var img = new GameObject("Black");
            img.transform.SetParent(go.transform, false);
            _ownFade = img.AddComponent<UnityEngine.UI.Image>();
            _ownFade.color = new Color(0f, 0f, 0f, 0f);
            var rt = _ownFade.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private IEnumerator Fade(float from, float to, float dur = 0.6f)
        {
            for (float t = 0f; t < dur; t += Time.unscaledDeltaTime)
            {
                if (_ownFade != null) _ownFade.color = new Color(0f, 0f, 0f, Mathf.Lerp(from, to, t / dur));
                yield return null;
            }
            if (_ownFade != null) _ownFade.color = new Color(0f, 0f, 0f, to);
        }

        private void Bail(string why)
        {
            Plugin.Log.LogWarning($"[Ending] {why} — extracting without the cutscene");
            Release("bail");
        }

        // release the held raid end — EVERY path lands here, extraction must never be lost
        private void Release(string how)
        {
            if (_released) return;
            _released = true;
            try { Restore(); } catch { }
            try
            {
                TerminalEndingCutscene.PassThrough = true;
                if (Game != null) Game.Stop(ProfileId, ExitStatus.Survived, ExitName, 0f);
                Plugin.Log.LogInfo($"[Ending] raid end released ({how})");
            }
            catch (Exception e) { Plugin.Log.LogError($"[Ending] releasing Stop FAILED ({how}): {e}"); }
            // arm the epilogue overlay — it'll wait for the survived jingle, then
            // layer over the results screen. DontDestroyOnLoad'd, so it survives
            // this GO being torn down with the raid scene.
            try { TerminalEpilogueScreen.ArmAfterExtract(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ending] epilogue arm failed: {e.Message}"); }
            Destroy(gameObject, 1f); // keep our black fade up while the end screen arrives
        }

        private void Restore()
        {
            TerminalEndingCutscene.PlayingNow = false;
            if (_inputLocked) { try { GamePlayerOwner.SetIgnoreInputInNPCDialog(false); } catch { } _inputLocked = false; }
            if (_driving) { Camera.onPreCull -= DriveCamera; _driving = false; }
            foreach (var cv in _hiddenCanvases) if (cv) cv.enabled = true;
            _hiddenCanvases.Clear();
            foreach (var r in _hiddenRenderers) if (r) r.enabled = true;
            _hiddenRenderers.Clear();
            foreach (var b in _pausedFx) if (b) b.enabled = true;
            _pausedFx.Clear();
            foreach (var (go, was) in _hiddenProps) if (go) go.SetActive(was);
            _hiddenProps.Clear();
            try { TerminalAcoustics.SetAmbientSilenced(false); } catch { }
            try { TerminalLights.CutsceneRelease(); } catch { }
            if (_director != null && _director.state == PlayState.Playing) _director.Stop();
        }

        private void OnDestroy()
        {
            if (!_released) Release("destroyed");
            else { try { Restore(); } catch { } }
        }
    }
}
