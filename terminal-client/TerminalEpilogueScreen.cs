using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.UI.SessionEnd;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace Manimal.Terminal
{
    // THE EPILOGUE SCREEN — REPLACES the standard SessionEndUI post-raid flow
    // when the player survives via the Zubr exfil. rather than layering our own
    // canvas over the results (an earlier design that revealed the vanilla
    // screens on close), we now HIJACK the ExitStatus screen itself: postfix
    // SessionResultExitStatus.Show, hide every default child, mount our own
    // 7-slide UI inside its transform, and on the final slide OK invoke the
    // shell's own _mainMenuButton to jump straight to the main menu — skipping
    // KillList / Statistics / Experience entirely.
    //
    // benefits vs the old layered-canvas design:
    //   - profile data is passed IN (Show's arg) — no 4-second grace, no null
    //     GameWorld hunt for Victims / EftStats / OverallCounters
    //   - the shell's Main Menu transition just works (we click its button)
    //   - the vanilla results screens never render — full replacement
    //
    // slide layout (persistent left panel + swappable right side) matches the
    // retail 1.0 FinalStatisticsScreen and the streamer screenshots the user
    // referenced. text + reward table live in plugin-data/epilogue/terminal_epilogue.json.
    internal static class TerminalEpilogueScreen
    {
        internal const string DirName = "epilogue";
        internal const string JsonName = "terminal_epilogue.json";

        // set true in EndingRunner.Release right before Game.Stop; cleared by
        // the postfix once it takes over the ExitStatus screen (so re-entering
        // the results doesn't fire the epilogue twice)
        internal static bool Pending;

        internal static void ResetForRaid()
        {
            Pending = false;
        }

        internal static void ArmAfterExtract()
        {
            if (!Plugin.Epilogue.Value) return;
            Pending = true;
            Plugin.Log.LogInfo("[Epilogue] armed — will take over SessionResultExitStatus.Show");
        }

        // DEV: LocalGame.Stop prefix — on any Survived extract with EpilogueTestMode
        // on, arm the epilogue immediately. bypasses the Zubr-exit / TerminalGate.On
        // gates so you can validate the results-screen UI on a quick Factory raid
        // instead of running the full Terminal ending each iteration.
        [HarmonyPatch(typeof(LocalGame), nameof(LocalGame.Stop))]
        internal static class Patch_TestArmOnAnyExit
        {
            [HarmonyPrefix]
            private static void Prefix(ExitStatus exitStatus)
            {
                try
                {
                    if (!Plugin.EpilogueTestMode.Value) return;
                    if (exitStatus != ExitStatus.Survived) return;
                    ArmAfterExtract();
                    Plugin.Log.LogInfo("[Epilogue] TEST MODE — armed on Survived extract (any map)");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] test-mode arm failed: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(SessionResultExitStatus), nameof(SessionResultExitStatus.Show),
            new[] { typeof(Profile), typeof(LastPlayerStateClass), typeof(ESideType),
                    typeof(ExitStatus), typeof(TimeSpan), typeof(ISession), typeof(bool) })]
        internal static class Patch_HijackExitStatus
        {
            [HarmonyPostfix]
            private static void Postfix(SessionResultExitStatus __instance, Profile activeProfile,
                LastPlayerStateClass lastPlayerState, ESideType side, ExitStatus exitStatus,
                TimeSpan raidTime, ISession session, bool isOnline)
            {
                try
                {
                    if (!Pending) return;
                    if (exitStatus != ExitStatus.Survived) return;
                    Pending = false;

                    var go = new GameObject("Terminal_Epilogue_Runner");
                    go.transform.SetParent(__instance.transform, false);
                    var rt = go.AddComponent<RectTransform>();
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;

                    var runner = go.AddComponent<EpilogueRunner>();
                    runner.Init(__instance, activeProfile, raidTime, session);
                    // hide the vanilla children RIGHT NOW, synchronously — the
                    // coroutine's async LoadAssets waits on a music download
                    // (~1s) and previously left the vanilla results screen
                    // visible during that window (2026-08-20 raid: flash of
                    // the standard 'RAID ENDED / Survived / EXP' before our
                    // layout replaced it)
                    runner.HideVanillaImmediately();
                    Plugin.Log.LogInfo($"[Epilogue] hijacked ExitStatus screen — {(activeProfile.EftStats.Victims?.Count ?? 0)} victim(s) available");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] hijack failed, falling back to vanilla results: {e.Message}"); }
            }
        }

        // EFT's Bender font pair — same lookup TerminalSubtitles uses. cached
        // across slides so we hit Resources.FindObjectsOfTypeAll once.
        private static Font _benderRegular;
        private static Font _benderBold;
        private static void EnsureFonts()
        {
            if (_benderBold != null && _benderRegular != null) return;
            try
            {
                foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (f == null || string.IsNullOrEmpty(f.name)) continue;
                    if (_benderBold == null && f.name.EndsWith("Bender Bold")) _benderBold = f;
                    else if (_benderRegular == null && f.name.EndsWith("Bender")) _benderRegular = f;
                }
            }
            catch { }
        }

        internal class EpilogueRunner : MonoBehaviour
        {
            private SessionResultExitStatus _host;
            private Profile _profile;
            private TimeSpan _raidTime;
            private ISession _session;
            private JObject _cfg;
            private JArray _slides;
            private JObject _leftPanel;
            private Sprite _bg, _medal;
            private AudioClip _music;
            private AudioSource _audio;
            private RectTransform _rightRoot;
            private int _slideIx;

            private readonly List<GameObject> _hiddenChildren = new List<GameObject>();
            private readonly HashSet<GameObject> _preserved = new HashSet<GameObject>();

            internal void Init(SessionResultExitStatus host, Profile profile, TimeSpan raidTime, ISession session)
            {
                _host = host; _profile = profile; _raidTime = raidTime; _session = session;
                StartCoroutine(Run());
            }

            // called by the postfix immediately (same frame as Show) so the
            // vanilla content is gone before Unity's next render tick and the
            // player never sees the RAID ENDED / Survived flash while music
            // loads in the coroutine
            internal void HideVanillaImmediately()
            {
                HideDefaultChildren();
                // and paint a full-screen black over our own transform so any
                // shell UI outside SessionResultExitStatus that renders during
                // the async LoadAssets window (~1s) is also covered
                var early = new GameObject("EarlyBlackout");
                early.transform.SetParent(transform, false);
                var img = early.AddComponent<Image>();
                img.color = new Color(0f, 0f, 0f, 1f);
                img.raycastTarget = true;
                var rt = img.rectTransform;
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                _earlyBlackout = early;
            }
            private GameObject _earlyBlackout;

            private IEnumerator Run()
            {
                if (!LoadConfig()) { UnityEngine.Object.Destroy(gameObject); yield break; }
                yield return LoadAssets();
                EnsureFonts();
                CaptureAndRerouteButtons();
                // HideDefaultChildren already ran synchronously in the postfix
                // via HideVanillaImmediately — don't hide twice
                Build();
                // real layout is up; drop the early blackout that was covering
                // the vanilla screen during LoadAssets
                if (_earlyBlackout != null) { UnityEngine.Object.Destroy(_earlyBlackout); _earlyBlackout = null; }
                ShowSlide(0);
                if (_music != null && _audio != null)
                {
                    _audio.clip = _music;
                    _audio.volume = 0.85f;
                    _audio.loop = false;
                    _audio.Play();
                }
            }

            private bool LoadConfig()
            {
                try
                {
                    var dir = AssetsDir();
                    var p = Path.Combine(dir, JsonName);
                    if (!File.Exists(p)) { Plugin.Log.LogWarning($"[Epilogue] no config at {p}"); return false; }
                    _cfg = JObject.Parse(File.ReadAllText(p));
                    _slides = _cfg["slides"] as JArray;
                    _leftPanel = _cfg["left_panel"] as JObject;
                    return _slides != null && _slides.Count > 0 && _leftPanel != null;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] config load failed: {e.Message}"); return false; }
            }

            private static string AssetsDir() => Path.Combine(
                Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                "plugin-data", DirName);

            private IEnumerator LoadAssets()
            {
                var dir = AssetsDir();
                _bg = LoadSprite(Path.Combine(dir, _cfg.Value<string>("background") ?? "background.png"));
                _medal = LoadSprite(Path.Combine(dir, _cfg.Value<string>("medal") ?? "medal.png"));

                var mp = Path.Combine(dir, _cfg.Value<string>("music") ?? "music.ogg");
                if (File.Exists(mp))
                {
                    var url = "file:///" + mp.Replace('\\', '/');
                    using (var req = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.OGGVORBIS))
                    {
                        yield return req.SendWebRequest();
                        if (req.result == UnityWebRequest.Result.Success)
                            _music = DownloadHandlerAudioClip.GetContent(req);
                        else Plugin.Log.LogWarning($"[Epilogue] music load failed: {req.error}");
                    }
                }
            }

            private static Sprite LoadSprite(string path)
            {
                try
                {
                    if (!File.Exists(path)) { Plugin.Log.LogWarning($"[Epilogue] image missing: {path}"); return null; }
                    var bytes = File.ReadAllBytes(path);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!tex.LoadImage(bytes)) { return null; }
                    tex.wrapMode = TextureWrapMode.Clamp;
                    return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] sprite load ({path}): {e.Message}"); return null; }
            }

            // rather than trying to preserve the shell's own NEXT/MAIN MENU
            // buttons in-place (their prefab hierarchy has ancestors that stay
            // deactivated after our hide sweep — verified in 2026-08-20 raid
            // log), we CLONE them: Instantiate a copy of each button GO into
            // our own layout root, position them bottom-center, retext, and
            // wire their OnClick to Advance/Regress. the clones carry the
            // DefaultUIButton component with its hover/click sounds, Bender
            // font, and interactable-state visuals baked in.
            private object _nextBtnClone, _menuBtnClone;
            // (kept as no-op so the Run coroutine's call site keeps compiling —
            //  actual clones are built in BuildFooterButtons after _layoutRoot exists)
            private void CaptureAndRerouteButtons() { }

            private void BuildFooterButtons()
            {
                try
                {
                    var srcNext = AccessTools.Field(typeof(SessionResultExitStatus), "_nextButton")?.GetValue(_host) as Component;
                    var srcMenu = AccessTools.Field(typeof(SessionResultExitStatus), "_mainMenuButton")?.GetValue(_host) as Component;

                    _nextBtnClone = CloneShellButton(srcNext, "NEXT",
                        new Vector2(0.42f, 0.055f), new Vector2(0.58f, 0.095f), Advance);
                    _menuBtnClone = CloneShellButton(srcMenu, "BACK",
                        new Vector2(0.42f, 0.010f), new Vector2(0.58f, 0.050f), Regress);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] footer buttons failed: {e.Message}"); }
            }

            private object CloneShellButton(Component src, string label, Vector2 anchorMin, Vector2 anchorMax, UnityEngine.Events.UnityAction click)
            {
                if (src == null) { Plugin.Log.LogWarning($"[Epilogue] shell button source null for '{label}'"); return null; }
                var clone = UnityEngine.Object.Instantiate(src.gameObject, _buttonRoot, false);
                clone.name = $"Epilogue_{label}";
                clone.SetActive(true);

                var rt = clone.GetComponent<RectTransform>();
                if (rt == null) rt = clone.AddComponent<RectTransform>();
                rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                rt.localScale = Vector3.one;

                // find the DefaultUIButton on the clone and:
                //   (a) SetHeaderText(label)
                //   (b) clear OnClick listeners and add ours
                var btnComp = clone.GetComponent(src.GetType());
                if (btnComp != null)
                {
                    try
                    {
                        var setText = AccessTools.Method(btnComp.GetType(), "SetHeaderText", new[] { typeof(string) });
                        setText?.Invoke(btnComp, new object[] { label });
                    }
                    catch { }
                    try
                    {
                        var onClick = AccessTools.Property(btnComp.GetType(), "OnClick")?.GetValue(btnComp)
                                   ?? AccessTools.Field(btnComp.GetType(), "OnClick")?.GetValue(btnComp);
                        var remove = onClick?.GetType().GetMethod("RemoveAllListeners");
                        remove?.Invoke(onClick, null);
                        var add = onClick?.GetType().GetMethod("AddListener", new[] { typeof(UnityEngine.Events.UnityAction) });
                        add?.Invoke(onClick, new object[] { click });
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] onClick rewire ('{label}') failed: {e.Message}"); }
                }
                return btnComp;
            }

            // hide ALL direct children of the ExitStatus screen — no shell
            // widgets bleed through. our clones give us the buttons we need.
            private void HideDefaultChildren()
            {
                try
                {
                    foreach (Transform t in _host.transform)
                    {
                        if (t == null || t.gameObject == gameObject) continue;
                        if (!t.gameObject.activeSelf) continue;
                        t.gameObject.SetActive(false);
                        _hiddenChildren.Add(t.gameObject);
                    }
                    Plugin.Log.LogInfo($"[Epilogue] hid {_hiddenChildren.Count} default child(ren)");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] hide failed: {e.Message}"); }
            }

            private void Build()
            {
                // black backdrop wipes anything the shell left visible
                MakeStretch(transform, "Backdrop", new Color(0f, 0f, 0f, 1f));

                if (_bg != null)
                {
                    var bgGo = new GameObject("Background");
                    bgGo.transform.SetParent(transform, false);
                    var img = bgGo.AddComponent<Image>();
                    img.sprite = _bg;
                    img.preserveAspect = true;
                    img.raycastTarget = false;
                    var rt = img.rectTransform;
                    rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                }

                // dim overlay + audio live on the runner root; layout root (which
                // holds header + left panel + right content) is rebuilt per slide
                // so the transitions between full/centered/side layouts are clean
                _dim = MakeStretch(transform, "Dim", new Color(0f, 0f, 0f, 0.40f));
                _dim.GetComponent<Image>().raycastTarget = false;

                _audio = gameObject.AddComponent<AudioSource>();
                _audio.playOnAwake = false;
                _audio.spatialBlend = 0f;
                // Presentation audio follows overall volume, never Music.
                if (!TerminalAudioRouting.RouteMaster(_audio))
                    Plugin.Log.LogWarning("[Epilogue] Master mixer group unavailable; using Unity output");

                var layoutGo = new GameObject("Layout");
                layoutGo.transform.SetParent(transform, false);
                _layoutRoot = layoutGo.AddComponent<RectTransform>();
                _layoutRoot.anchorMin = Vector2.zero; _layoutRoot.anchorMax = Vector2.one;
                _layoutRoot.offsetMin = Vector2.zero; _layoutRoot.offsetMax = Vector2.zero;

                // buttons live in a SEPARATE root that survives ShowSlide's
                // teardown of _layoutRoot — previous bug (2026-08-20 raid log:
                // clones instantiated but never rendered) was that
                // `foreach (t in _layoutRoot) Destroy(t.gameObject)` nuked the
                // buttons on every slide change
                var btnGo = new GameObject("ButtonRoot");
                btnGo.transform.SetParent(transform, false);
                _buttonRoot = btnGo.AddComponent<RectTransform>();
                _buttonRoot.anchorMin = Vector2.zero; _buttonRoot.anchorMax = Vector2.one;
                _buttonRoot.offsetMin = Vector2.zero; _buttonRoot.offsetMax = Vector2.zero;
                BuildFooterButtons();
            }

            private GameObject _dim;
            private RectTransform _layoutRoot;
            private RectTransform _buttonRoot;

            private static GameObject MakeStretch(Transform parent, string name, Color color)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                var img = go.AddComponent<Image>();
                img.color = color;
                var rt = img.rectTransform;
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                return go;
            }

            private void BuildHeader(JObject slide)
            {
                var title = slide.Value<string>("title") ?? "COMPLETION RESULTS";
                var sub   = slide.Value<string>("subtitle") ?? "REACHED ENDING";

                var t = MakeText(_layoutRoot, "HeaderTitle", title, 40, TextAnchor.UpperCenter);
                t.fontStyle = FontStyle.Bold;
                t.color = new Color(0.95f, 0.95f, 0.9f, 1f);
                var rt = t.rectTransform;
                rt.anchorMin = new Vector2(0f, 0.90f); rt.anchorMax = new Vector2(1f, 0.98f);
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                t.raycastTarget = false;

                var s = MakeText(_layoutRoot, "HeaderSub", sub, 18, TextAnchor.UpperCenter);
                s.color = new Color(0.8f, 0.8f, 0.75f, 1f);
                var srt = s.rectTransform;
                srt.anchorMin = new Vector2(0f, 0.86f); srt.anchorMax = new Vector2(1f, 0.90f);
                srt.offsetMin = Vector2.zero; srt.offsetMax = Vector2.zero;
                s.raycastTarget = false;
            }

            private void BuildLeftPanel()
            {
                var panel = new GameObject("LeftPanel");
                panel.transform.SetParent(_layoutRoot, false);
                var pImg = panel.AddComponent<Image>();
                pImg.color = new Color(0f, 0f, 0f, 0.72f);
                var prt = pImg.rectTransform;
                prt.anchorMin = new Vector2(0.02f, 0.15f);
                prt.anchorMax = new Vector2(0.30f, 0.87f);
                prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;

                if (_medal != null)
                {
                    var mGo = new GameObject("Medal");
                    mGo.transform.SetParent(panel.transform, false);
                    var img = mGo.AddComponent<Image>();
                    img.sprite = _medal;
                    img.preserveAspect = true;
                    img.raycastTarget = false;
                    var mrt = img.rectTransform;
                    mrt.anchorMin = new Vector2(0.15f, 0.60f);
                    mrt.anchorMax = new Vector2(0.85f, 0.95f);
                    mrt.offsetMin = Vector2.zero; mrt.offsetMax = Vector2.zero;
                }

                var v1 = MakeText(panel.transform, "Verdict1", _leftPanel.Value<string>("verdict_line1") ?? "", 22, TextAnchor.UpperCenter);
                v1.fontStyle = FontStyle.Bold; v1.color = new Color(0.95f, 0.95f, 0.9f, 1f); v1.raycastTarget = false;
                var v1rt = v1.rectTransform;
                v1rt.anchorMin = new Vector2(0.05f, 0.53f); v1rt.anchorMax = new Vector2(0.95f, 0.60f);
                v1rt.offsetMin = Vector2.zero; v1rt.offsetMax = Vector2.zero;

                var v2 = MakeText(panel.transform, "Verdict2", _leftPanel.Value<string>("verdict_line2") ?? "", 22, TextAnchor.UpperCenter);
                v2.fontStyle = FontStyle.Bold; v2.color = new Color(0.95f, 0.95f, 0.9f, 1f); v2.raycastTarget = false;
                var v2rt = v2.rectTransform;
                v2rt.anchorMin = new Vector2(0.05f, 0.47f); v2rt.anchorMax = new Vector2(0.95f, 0.53f);
                v2rt.offsetMin = Vector2.zero; v2rt.offsetMax = Vector2.zero;

                var div = new GameObject("Divider");
                div.transform.SetParent(panel.transform, false);
                var dImg = div.AddComponent<Image>();
                dImg.color = new Color(0.5f, 0.5f, 0.45f, 0.6f);
                dImg.raycastTarget = false;
                var drt = dImg.rectTransform;
                drt.anchorMin = new Vector2(0.15f, 0.45f); drt.anchorMax = new Vector2(0.85f, 0.455f);
                drt.offsetMin = Vector2.zero; drt.offsetMax = Vector2.zero;

                var body = MakeText(panel.transform, "Body", _leftPanel.Value<string>("body") ?? "", 15, TextAnchor.UpperCenter);
                body.color = new Color(0.85f, 0.85f, 0.8f, 1f); body.raycastTarget = false;
                var brt = body.rectTransform;
                brt.anchorMin = new Vector2(0.06f, 0.10f); brt.anchorMax = new Vector2(0.94f, 0.44f);
                brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;

                var foot = MakeText(panel.transform, "Foot", _leftPanel.Value<string>("footer") ?? "", 14, TextAnchor.LowerCenter);
                foot.color = new Color(0.75f, 0.75f, 0.7f, 1f); foot.raycastTarget = false;
                var frt = foot.rectTransform;
                frt.anchorMin = new Vector2(0.05f, 0.02f); frt.anchorMax = new Vector2(0.95f, 0.08f);
                frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;
            }

            // narrative slides run the typewriter — track it here so a NEXT
            // click can skip-complete the animation rather than jumping slides
            private Coroutine _typewriterCo;
            private Text _typewriterTarget;
            private string _typewriterFullText;
            private bool _typewriterDone;

            private void ShowSlide(int ix)
            {
                _slideIx = Mathf.Clamp(ix, 0, _slides.Count - 1);

                // stop any typewriter from the previous slide
                if (_typewriterCo != null) { StopCoroutine(_typewriterCo); _typewriterCo = null; }
                _typewriterDone = true;
                _typewriterTarget = null;

                // tear down the layout tree — full rebuild per slide so the
                // three layouts (narrative_full / medal_center / side) don't
                // leak state into each other. borrowed KillList container is
                // restored FIRST so it doesn't get destroyed along with the
                // layout children (previous bug: killfeed rows lingered on
                // the thanks slide + main menu after CloseAllScreensForced).
                RestoreBorrowedContainer();
                foreach (Transform t in _layoutRoot) UnityEngine.Object.Destroy(t.gameObject);
                _rightRoot = null;
                // every slide fades in as a whole; individual staggered fades
                // (stat rows, achv cells, reward cards) run inside that
                AttachFadeIn(_layoutRoot.gameObject, 0f, 0.35f);

                var s = _slides[_slideIx] as JObject;
                if (s == null) { CloseToMenu(); return; }
                var kind = s.Value<string>("kind") ?? "narrative_full";

                switch (kind)
                {
                    case "narrative_full":
                        SetDim(0.35f);
                        BuildNarrativeFull(s);
                        break;
                    case "medal_center":
                        SetDim(0.55f);
                        BuildHeader(s);
                        BuildMedalCenter();
                        break;
                    case "thanks":
                        // full-screen background + centered boxed body + OK button.
                        // no left panel, no header/left widgets — matches the
                        // retail 1.0 "final trial" thank-you slide reference.
                        SetDim(0.65f);
                        BuildHeader(s);
                        BuildThanks(s);
                        break;
                    default:
                        // side layouts (stats/achievements/rewards/killfeed)
                        SetDim(0.55f);
                        BuildHeader(s);
                        BuildLeftPanel();
                        BuildRightRoot();
                        switch (kind)
                        {
                            case "stats":        BuildStatsSlide();        break;
                            case "achievements": BuildAchievementsSlide(); break;
                            case "rewards":      BuildRewardsSlide();      break;
                            case "killfeed":     BuildKillfeedSlide();     break;
                        }
                        break;
                }

                // NEXT button hidden while typewriter is animating (narrative
                // slides); shown immediately for every other layout. MAIN MENU
                // (repurposed as BACK) hidden on slide 0 and on the thanks
                // slide — final NEXT is the exit path there, not BACK.
                UpdateButtonVisibility(kind);
            }

            private void UpdateButtonVisibility(string kind)
            {
                bool showNext = _typewriterDone;
                bool showBack = _slideIx > 0 && kind != "thanks";
                if (_nextBtnClone is Component nextComp) nextComp.gameObject.SetActive(showNext);
                if (_menuBtnClone is Component menuComp) menuComp.gameObject.SetActive(showBack);

                // relabel NEXT → OK on the thanks slide (last-page exit path)
                try
                {
                    if (_nextBtnClone is Component nc)
                    {
                        var setText = AccessTools.Method(nc.GetType(), "SetHeaderText", new[] { typeof(string) });
                        setText?.Invoke(nc, new object[] { kind == "thanks" ? "OK" : "NEXT" });
                    }
                }
                catch { }
            }

            private void SetDim(float a)
            {
                if (_dim == null) return;
                var img = _dim.GetComponent<Image>();
                if (img != null) img.color = new Color(0f, 0f, 0f, a);
            }

            private void BuildRightRoot()
            {
                var rightGo = new GameObject("RightRoot");
                rightGo.transform.SetParent(_layoutRoot, false);
                _rightRoot = rightGo.AddComponent<RectTransform>();
                _rightRoot.anchorMin = new Vector2(0.32f, 0.15f);
                _rightRoot.anchorMax = new Vector2(0.98f, 0.87f);
                _rightRoot.offsetMin = Vector2.zero; _rightRoot.offsetMax = Vector2.zero;
            }

            // slides 0-1: full-screen background, text tucked into the bottom
            // middle inside a narrow column with a thin horizontal divider
            // above it (matches the retail bottom-caption layout). typewriter
            // characters roll in one at a time; NEXT stays hidden until the
            // animation completes (or a NEXT click skip-completes it).
            private void BuildNarrativeFull(JObject s)
            {
                var text = s.Value<string>("text") ?? "";

                // thin horizontal divider line above the text column
                var div = new GameObject("NarrativeDivider");
                div.transform.SetParent(_layoutRoot, false);
                var dImg = div.AddComponent<Image>();
                dImg.color = new Color(0.85f, 0.85f, 0.80f, 0.60f);
                dImg.raycastTarget = false;
                var drt = dImg.rectTransform;
                drt.anchorMin = new Vector2(0.32f, 0.235f); drt.anchorMax = new Vector2(0.68f, 0.238f);
                drt.offsetMin = Vector2.zero; drt.offsetMax = Vector2.zero;
                AttachFadeIn(div, 0f, 0.35f);

                var t = MakeText(_layoutRoot, "NarrativeBody", "", 20, TextAnchor.UpperCenter);
                t.color = new Color(0.92f, 0.92f, 0.9f, 1f);
                t.horizontalOverflow = HorizontalWrapMode.Wrap;
                var rt = t.rectTransform;
                rt.anchorMin = new Vector2(0.30f, 0.05f); rt.anchorMax = new Vector2(0.70f, 0.230f);
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

                _typewriterTarget = t;
                _typewriterFullText = text;
                _typewriterDone = false;
                _typewriterCo = StartCoroutine(TypewriterCoroutine(text, t));
            }

            // slide 2: retail's "medal moves to the middle" transition slide.
            // header at top, medal centered upper-half, verdict lines, boxed
            // narrative body, endings-achieved footer. no left panel — the
            // medal HERE is what slides 3+ show in the left panel.
            private void BuildMedalCenter()
            {
                if (_medal != null)
                {
                    var mGo = new GameObject("Medal");
                    mGo.transform.SetParent(_layoutRoot, false);
                    var img = mGo.AddComponent<Image>();
                    img.sprite = _medal;
                    img.preserveAspect = true;
                    img.raycastTarget = false;
                    var mrt = img.rectTransform;
                    mrt.anchorMin = new Vector2(0.5f, 0.5f);
                    mrt.anchorMax = new Vector2(0.5f, 0.5f);
                    mrt.pivot = new Vector2(0.5f, 0.5f);
                    mrt.sizeDelta = new Vector2(360f, 360f);
                    mrt.anchoredPosition = new Vector2(0f, 90f);
                    AttachFadeIn(mGo, 0.05f, 0.50f);
                }

                var v1 = MakeText(_layoutRoot, "Verdict1", _leftPanel.Value<string>("verdict_line1") ?? "", 30, TextAnchor.UpperCenter);
                v1.fontStyle = FontStyle.Bold; v1.color = new Color(0.95f, 0.95f, 0.9f, 1f); v1.raycastTarget = false;
                var v1rt = v1.rectTransform;
                v1rt.anchorMin = new Vector2(0.2f, 0.32f); v1rt.anchorMax = new Vector2(0.8f, 0.38f);
                v1rt.offsetMin = Vector2.zero; v1rt.offsetMax = Vector2.zero;
                AttachFadeIn(v1.gameObject, 0.30f, 0.35f);

                var v2 = MakeText(_layoutRoot, "Verdict2", _leftPanel.Value<string>("verdict_line2") ?? "", 30, TextAnchor.UpperCenter);
                v2.fontStyle = FontStyle.Bold; v2.color = new Color(0.95f, 0.95f, 0.9f, 1f); v2.raycastTarget = false;
                var v2rt = v2.rectTransform;
                v2rt.anchorMin = new Vector2(0.2f, 0.26f); v2rt.anchorMax = new Vector2(0.8f, 0.32f);
                v2rt.offsetMin = Vector2.zero; v2rt.offsetMax = Vector2.zero;
                AttachFadeIn(v2.gameObject, 0.35f, 0.35f);

                var box = new GameObject("BodyBox");
                box.transform.SetParent(_layoutRoot, false);
                var boxImg = box.AddComponent<Image>();
                boxImg.color = new Color(0.12f, 0.12f, 0.12f, 0.65f);
                boxImg.raycastTarget = false;
                var bxrt = boxImg.rectTransform;
                bxrt.anchorMin = new Vector2(0.28f, 0.14f); bxrt.anchorMax = new Vector2(0.72f, 0.24f);
                bxrt.offsetMin = Vector2.zero; bxrt.offsetMax = Vector2.zero;
                AttachFadeIn(box, 0.55f, 0.35f);

                var body = MakeText(box.transform, "Body", _leftPanel.Value<string>("body") ?? "", 15, TextAnchor.MiddleCenter);
                body.color = new Color(0.88f, 0.88f, 0.85f, 1f); body.raycastTarget = false;
                var brt = body.rectTransform;
                brt.anchorMin = new Vector2(0.03f, 0.05f); brt.anchorMax = new Vector2(0.97f, 0.95f);
                brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;

                var foot = MakeText(_layoutRoot, "Foot", _leftPanel.Value<string>("footer") ?? "", 15, TextAnchor.UpperCenter);
                foot.color = new Color(0.75f, 0.75f, 0.7f, 1f); foot.raycastTarget = false;
                var frt = foot.rectTransform;
                frt.anchorMin = new Vector2(0.28f, 0.10f); frt.anchorMax = new Vector2(0.72f, 0.13f);
                frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;
                AttachFadeIn(foot.gameObject, 0.80f, 0.35f);
            }

            // final slide: centered bordered box holding the thank-you copy.
            // background stays visible around it; header sits above; OK button
            // (relabeled by UpdateButtonVisibility) sits below at bottom-center.
            private void BuildThanks(JObject s)
            {
                var box = new GameObject("ThanksBox");
                box.transform.SetParent(_layoutRoot, false);
                var boxImg = box.AddComponent<Image>();
                boxImg.color = new Color(0.10f, 0.10f, 0.10f, 0.80f);
                boxImg.raycastTarget = false;
                var boxRt = boxImg.rectTransform;
                boxRt.anchorMin = new Vector2(0.25f, 0.20f);
                boxRt.anchorMax = new Vector2(0.75f, 0.55f);
                boxRt.offsetMin = Vector2.zero; boxRt.offsetMax = Vector2.zero;

                // subtle 1px border stroke on top of the fill
                var stroke = new GameObject("Stroke");
                stroke.transform.SetParent(box.transform, false);
                var strokeImg = stroke.AddComponent<Image>();
                strokeImg.color = new Color(0.35f, 0.33f, 0.30f, 0.55f);
                strokeImg.raycastTarget = false;
                strokeImg.type = Image.Type.Sliced;
                var srt = strokeImg.rectTransform;
                srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one;
                srt.offsetMin = Vector2.zero; srt.offsetMax = Vector2.zero;
                // hack: make the stroke a hollow rect by giving it a slight transparent center —
                // simpler approach is to just use the outer fill and rely on the darker interior;
                // remove the child if it looks too heavy
                strokeImg.color = new Color(1f, 1f, 1f, 0f);

                var body = MakeText(box.transform, "Body", "", 20, TextAnchor.MiddleCenter);
                body.color = new Color(0.92f, 0.92f, 0.88f, 1f);
                body.raycastTarget = false;
                body.horizontalOverflow = HorizontalWrapMode.Wrap;
                var brt = body.rectTransform;
                brt.anchorMin = new Vector2(0.05f, 0.06f); brt.anchorMax = new Vector2(0.95f, 0.94f);
                brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;

                // typewriter the thanks body — same skip-on-click behaviour as
                // the narrative slides. NEXT/OK stays hidden until it finishes.
                var full = s.Value<string>("body") ?? "";
                _typewriterTarget = body;
                _typewriterFullText = full;
                _typewriterDone = false;
                _typewriterCo = StartCoroutine(TypewriterCoroutine(full, body));
            }

            private IEnumerator TypewriterCoroutine(string full, Text target)
            {
                float cps = _cfg.Value<float?>("typewriter_chars_per_second") ?? 45f;
                if (cps < 1f) cps = 45f;
                float delay = 1f / cps;
                for (int i = 1; i <= full.Length; i++)
                {
                    if (target == null) yield break;
                    target.text = full.Substring(0, i);
                    yield return new WaitForSecondsRealtime(delay);
                }
                _typewriterDone = true;
                UpdateButtonVisibility(_slides[_slideIx].Value<string>("kind") ?? "");
            }

            // faction sprites lifted from InventoryPlayerModelWithStatsWindow —
            // it has [SerializeField] _usecSprite/_bearSprite fields the char
            // screen uses. we cache them on first lookup and reuse across slides.
            private static Sprite _usecSprite, _bearSprite;
            private static void EnsureFactionSprites()
            {
                if (_usecSprite != null && _bearSprite != null) return;
                try
                {
                    var t = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                        .FirstOrDefault(x => x.Name == "InventoryPlayerModelWithStatsWindow");
                    if (t == null) return;
                    // FindObjectsOfTypeAll includes inactive prefab instances
                    var comps = Resources.FindObjectsOfTypeAll(t);
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        _usecSprite ??= AccessTools.Field(t, "_usecSprite")?.GetValue(c) as Sprite;
                        _bearSprite ??= AccessTools.Field(t, "_bearSprite")?.GetValue(c) as Sprite;
                        if (_usecSprite != null && _bearSprite != null) break;
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] faction sprite lookup failed: {e.Message}"); }
            }

            // retail-style profile header (matches the character screen strip):
            // real USEC/BEAR sprite from the game's own atlas + nickname in
            // large cyan bold. sits along the top of the panel.
            private void BuildProfileHeader(Transform parent, string side, string nickname)
            {
                EnsureFactionSprites();

                var strip = new GameObject("ProfileHeader");
                strip.transform.SetParent(parent, false);
                var stripImg = strip.AddComponent<Image>();
                stripImg.color = new Color(0.12f, 0.12f, 0.12f, 0.60f);
                stripImg.raycastTarget = false;
                var srt = stripImg.rectTransform;
                srt.anchorMin = new Vector2(0.02f, 0.87f);
                srt.anchorMax = new Vector2(0.98f, 0.99f);
                srt.offsetMin = Vector2.zero; srt.offsetMax = Vector2.zero;
                AttachFadeIn(strip, 0.05f, 0.30f);

                // faction icon (real sprite when we have it; nothing if scav
                // since there IS no scav logo per user)
                var factionText = (side ?? "").ToLowerInvariant();
                Sprite factionSprite = null;
                if (factionText.Contains("usec")) factionSprite = _usecSprite;
                else if (factionText.Contains("bear")) factionSprite = _bearSprite;

                if (factionSprite != null)
                {
                    var badge = new GameObject("FactionIcon");
                    badge.transform.SetParent(strip.transform, false);
                    var badgeImg = badge.AddComponent<Image>();
                    badgeImg.sprite = factionSprite;
                    badgeImg.preserveAspect = true;
                    badgeImg.raycastTarget = false;
                    var brt = badgeImg.rectTransform;
                    brt.anchorMin = new Vector2(0.01f, 0.10f);
                    brt.anchorMax = new Vector2(0.09f, 0.90f);
                    brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
                    AttachFadeIn(badge, 0.10f, 0.35f);
                }

                // nickname — large cyan bold
                var name = MakeText(strip.transform, "Nickname", nickname ?? "—", 32, TextAnchor.MiddleLeft);
                name.fontStyle = FontStyle.Bold;
                name.color = new Color(0.55f, 0.85f, 1f, 1f);
                name.raycastTarget = false;
                var nrt = name.rectTransform;
                nrt.anchorMin = new Vector2(0.11f, 0.10f);
                nrt.anchorMax = new Vector2(0.99f, 0.90f);
                nrt.offsetMin = Vector2.zero; nrt.offsetMax = Vector2.zero;
                AttachFadeIn(name.gameObject, 0.15f, 0.35f);
            }

            // add a CanvasGroup to a GO and fade its alpha from 0→1 over `dur`
            // seconds, after `delay` seconds. no-op if the GO already has a
            // CanvasGroup driven elsewhere (e.g. StaggerFadeIn cells).
            private void AttachFadeIn(GameObject go, float delay, float dur)
            {
                if (go == null) return;
                var cg = go.GetComponent<CanvasGroup>();
                if (cg == null) cg = go.AddComponent<CanvasGroup>();
                cg.alpha = 0f;
                StartCoroutine(FadeInCoroutine(cg, delay, dur));
            }
            private static IEnumerator FadeInCoroutine(CanvasGroup cg, float delay, float dur)
            {
                if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
                float t = 0f;
                while (t < dur)
                {
                    if (cg == null) yield break;
                    t += Time.unscaledDeltaTime;
                    cg.alpha = Mathf.Clamp01(t / dur);
                    yield return null;
                }
                if (cg != null) cg.alpha = 1f;
            }

            // wrap the achievements/rewards content in a ScrollRect so grids
            // that overflow the viewport get a vertical scrollbar. returns the
            // content RectTransform where cells should be placed — content
            // sizeDelta.y is set by the caller once cells are laid out
            private RectTransform MakeScrollableContent(GameObject parent, float viewportTopFrac, float viewportBottomFrac)
            {
                // narrow band down the right edge reserved for the scrollbar
                // (matches the shell's kill-list scrollbar placement)
                const float scrollbarWidth = 0.015f; // fraction of parent width

                // viewport uses RectMask2D + a near-invisible Image so wheel
                // events raycast to the ScrollRect (Mask alone provides no
                // Graphic; wheel would fall through to whatever's beneath)
                var viewportGo = new GameObject("Viewport");
                viewportGo.transform.SetParent(parent.transform, false);
                var vpRt = viewportGo.AddComponent<RectTransform>();
                vpRt.anchorMin = new Vector2(0.02f, viewportBottomFrac);
                vpRt.anchorMax = new Vector2(0.98f - scrollbarWidth, viewportTopFrac);
                vpRt.offsetMin = Vector2.zero; vpRt.offsetMax = Vector2.zero;
                var vpImg = viewportGo.AddComponent<Image>();
                vpImg.color = new Color(0f, 0f, 0f, 0.001f);
                vpImg.raycastTarget = true;
                viewportGo.AddComponent<UnityEngine.UI.RectMask2D>();

                // content anchored top-stretch, pivot top-center, sizeDelta.y
                // set by the caller once cells are placed
                var contentGo = new GameObject("Content");
                contentGo.transform.SetParent(viewportGo.transform, false);
                var content = contentGo.AddComponent<RectTransform>();
                content.anchorMin = new Vector2(0f, 1f);
                content.anchorMax = new Vector2(1f, 1f);
                content.pivot = new Vector2(0.5f, 1f);
                content.anchoredPosition = Vector2.zero;
                content.sizeDelta = new Vector2(0f, 0f);

                // vertical scrollbar — dark track + lighter thumb, wired to
                // the ScrollRect below. matches the kill-list default style.
                var barGo = new GameObject("Scrollbar");
                barGo.transform.SetParent(parent.transform, false);
                var barRt = barGo.AddComponent<RectTransform>();
                barRt.anchorMin = new Vector2(0.98f - scrollbarWidth, viewportBottomFrac);
                barRt.anchorMax = new Vector2(0.98f, viewportTopFrac);
                barRt.offsetMin = Vector2.zero; barRt.offsetMax = Vector2.zero;
                var barBgImg = barGo.AddComponent<Image>();
                barBgImg.color = new Color(0.06f, 0.06f, 0.06f, 0.80f);
                var scrollbar = barGo.AddComponent<UnityEngine.UI.Scrollbar>();
                scrollbar.direction = UnityEngine.UI.Scrollbar.Direction.BottomToTop;

                // sliding area holds the handle rect
                var slidingGo = new GameObject("SlidingArea");
                slidingGo.transform.SetParent(barGo.transform, false);
                var slidingRt = slidingGo.AddComponent<RectTransform>();
                slidingRt.anchorMin = Vector2.zero; slidingRt.anchorMax = Vector2.one;
                slidingRt.offsetMin = new Vector2(2f, 2f); slidingRt.offsetMax = new Vector2(-2f, -2f);

                var handleGo = new GameObject("Handle");
                handleGo.transform.SetParent(slidingGo.transform, false);
                var handleRt = handleGo.AddComponent<RectTransform>();
                handleRt.anchorMin = Vector2.zero; handleRt.anchorMax = Vector2.one;
                handleRt.offsetMin = Vector2.zero; handleRt.offsetMax = Vector2.zero;
                var handleImg = handleGo.AddComponent<Image>();
                handleImg.color = new Color(0.45f, 0.45f, 0.42f, 0.90f);
                scrollbar.targetGraphic = handleImg;
                scrollbar.handleRect = handleRt;

                var scroll = parent.AddComponent<UnityEngine.UI.ScrollRect>();
                scroll.viewport = vpRt;
                scroll.content = content;
                scroll.verticalScrollbar = scrollbar;
                // Permanent, not AutoHideAndExpandViewport — expand mode resizes
                // the viewport rect when the bar hides, which can NOT-clip
                // content near the anchor edge (rows visibly bled up above the
                // panel's own header on the first frame, 2026-08-21)
                scroll.verticalScrollbarVisibility = UnityEngine.UI.ScrollRect.ScrollbarVisibility.Permanent;
                scroll.verticalScrollbarSpacing = 4f;
                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 30f;
                // fresh ScrollRects default to verticalNormalizedPosition=0
                // (scrolled to bottom) — with pivot-top content that puts the
                // content TOP above the viewport top and top rows bleed out
                // over any label above the panel. force top-scroll AFTER the
                // caller has set content.sizeDelta (deferred by two frames).
                StartCoroutine(ForceScrollTop(scroll));

                return content;
            }

            private static IEnumerator ForceScrollTop(UnityEngine.UI.ScrollRect s)
            {
                yield return null;
                yield return null;
                if (s != null) s.verticalNormalizedPosition = 1f;
            }

            // stagger a bunch of CanvasGroups to fade in one at a time (in the
            // order given). used for stats rows, achievement cells, reward cards.
            private IEnumerator StaggerFadeIn(List<CanvasGroup> groups, float perItem, float fade)
            {
                foreach (var g in groups) if (g != null) g.alpha = 0f;
                foreach (var g in groups)
                {
                    if (g == null) continue;
                    float t = 0f;
                    while (t < fade)
                    {
                        if (g == null) yield break;
                        t += Time.unscaledDeltaTime;
                        g.alpha = Mathf.Clamp01(t / fade);
                        yield return null;
                    }
                    if (g != null) g.alpha = 1f;
                    yield return new WaitForSecondsRealtime(perItem);
                }
            }

            private void BuildStatsSlide()
            {
                var stats = ProfileData.ReadStats(_profile);
                var rows = new (string label, string value)[]
                {
                    ("Character Level",     stats.Level.ToString()),
                    ("Favorite Weapon",     stats.FavoriteWeapon),
                    ("Prestige Level",      stats.PrestigeLevel.ToString()),
                    ("K/D",                 stats.Kd),
                    ("Total Played Time",   stats.PlayedTime),
                    ("Game Style",          stats.GameStyle),
                    ("Game Edition",        stats.GameEdition),
                    ("PMC Eliminated",      stats.PmcKills.ToString()),
                    ("Game Mode",           stats.GameMode),
                    ("Scavs Eliminated",    stats.ScavKills.ToString()),
                    ("Total Raid Count",    stats.TotalRaids.ToString()),
                    ("Bosses Neutralized",  stats.BossKills.ToString()),
                    ("Longest Win Streak",  stats.WinStreak.ToString()),
                    ("Survival Rate",       stats.SurvivalRate),
                };

                var panelGo = new GameObject("StatsPanel");
                panelGo.transform.SetParent(_rightRoot, false);
                var panelImg = panelGo.AddComponent<Image>();
                panelImg.color = new Color(0f, 0f, 0f, 0.72f);
                var prt = panelImg.rectTransform;
                prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
                prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;

                BuildProfileHeader(panelGo.transform, stats.Side, stats.Nickname);

                // build order matches retail's stagger: down the left column
                // first (0..6), then down the right column (7..13). rows are
                // sorted so index i in the list corresponds to the position
                // above/below its neighbour, giving a smooth in-order fade.
                var groups = new List<CanvasGroup>();
                var valueTexts = new List<(Text txt, string full)>();
                for (int i = 0; i < rows.Length; i++)
                {
                    int col = i / 7;                // 0 = left, 1 = right
                    int row = i % 7;                // 0..6 top to bottom
                    float rowH = 0.10f;
                    float top = 0.85f - row * (rowH + 0.005f);
                    float bottom = top - rowH;
                    float xLeft  = col == 0 ? 0.04f : 0.52f;
                    float xRight = col == 0 ? 0.48f : 0.96f;

                    var rowGo = new GameObject($"Row{i}");
                    rowGo.transform.SetParent(panelGo.transform, false);
                    var rowImg = rowGo.AddComponent<Image>();
                    rowImg.color = new Color(0.12f, 0.12f, 0.12f, 0.6f);
                    rowImg.raycastTarget = false;
                    var rrt = rowImg.rectTransform;
                    rrt.anchorMin = new Vector2(xLeft, bottom); rrt.anchorMax = new Vector2(xRight, top);
                    rrt.offsetMin = Vector2.zero; rrt.offsetMax = Vector2.zero;
                    groups.Add(rowGo.AddComponent<CanvasGroup>());

                    var lbl = MakeText(rowGo.transform, "Lbl", rows[i].label, 15, TextAnchor.MiddleLeft);
                    lbl.color = new Color(0.85f, 0.85f, 0.8f, 1f);
                    lbl.raycastTarget = false;
                    var lrt = lbl.rectTransform;
                    lrt.anchorMin = new Vector2(0.05f, 0f); lrt.anchorMax = new Vector2(0.65f, 1f);
                    lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;

                    // start values empty — a coroutine types each in
                    // sequence AFTER the row has faded in
                    var val = MakeText(rowGo.transform, "Val", "", 15, TextAnchor.MiddleRight);
                    val.color = new Color(0.95f, 0.95f, 0.9f, 1f);
                    val.raycastTarget = false;
                    var vrt = val.rectTransform;
                    vrt.anchorMin = new Vector2(0.35f, 0f); vrt.anchorMax = new Vector2(0.95f, 1f);
                    vrt.offsetMin = Vector2.zero; vrt.offsetMax = Vector2.zero;
                    valueTexts.Add((val, rows[i].value));
                }
                StartCoroutine(StaggerFadeIn(groups, 0.06f, 0.18f));
                StartCoroutine(TypewriteStatValues(valueTexts));
            }

            // types each stat value into its Text one at a time, starting
            // immediately with the first row and marching down the list. very
            // fast (~200 cps) so all 14 values finish in about a second.
            private IEnumerator TypewriteStatValues(List<(Text txt, string full)> targets)
            {
                // small settle so the first row's fade-in has begun
                yield return new WaitForSecondsRealtime(0.05f);
                const float perChar = 1f / 200f;
                foreach (var (txt, full) in targets)
                {
                    if (txt == null) continue;
                    var s = full ?? "";
                    if (s.Length == 0) continue;
                    for (int i = 1; i <= s.Length; i++)
                    {
                        if (txt == null) yield break;
                        txt.text = s.Substring(0, i);
                        yield return new WaitForSecondsRealtime(perChar);
                    }
                    yield return new WaitForSecondsRealtime(0.05f);
                }
            }

            // achievements grid — same lookup path AchievementIconView uses:
            //   GClass4014.Instance.GetAllAchievementTemplates() gives every
            //   template, we filter to what the player has in AchievementsData,
            //   then read template.Sprite (loading it via LoadIconSprite(session)
            //   if not cached yet). retail cell = square dark border with the
            //   hexagonal icon centered inside; no text, no labels.
            private void BuildAchievementsSlide()
            {
                var panelGo = new GameObject("AchvPanel");
                panelGo.transform.SetParent(_rightRoot, false);
                var panelImg = panelGo.AddComponent<Image>();
                panelImg.color = new Color(0f, 0f, 0f, 0.72f);
                var prt = panelImg.rectTransform;
                prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
                prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;

                var stats = ProfileData.ReadStats(_profile);
                BuildProfileHeader(panelGo.transform, stats.Side, stats.Nickname);

                var unlockedTemplates = ResolveUnlockedTemplates();
                int total = unlockedTemplates.Count;
                Plugin.Log.LogInfo($"[Epilogue] achievements slide: {total} unlocked template(s) resolved");

                // scrollable content — pixel-based grid: 6 cols, 150px cells
                // with 12px spacing. content height auto-sized to fit total rows.
                // viewport top well below the profile header (0.87-0.99) so
                // the first row of cells doesn't collide with the nickname
                // strip. gap ≈ 5% of the panel = clean visual break.
                var content = MakeScrollableContent(panelGo, 0.82f, 0.02f);
                const int cols = 6;
                const float cellSize = 150f;
                const float spacing = 12f;
                int rows = (total + cols - 1) / cols;
                float contentHeight = rows * (cellSize + spacing) + spacing;
                content.sizeDelta = new Vector2(0f, contentHeight);

                var groups = new List<CanvasGroup>();
                for (int i = 0; i < total; i++)
                {
                    int col = i % cols;
                    int row = i / cols;

                    var cell = new GameObject($"Achv{i}");
                    cell.transform.SetParent(content, false);
                    var borderImg = cell.AddComponent<Image>();
                    borderImg.color = new Color(0.20f, 0.20f, 0.20f, 0.90f);
                    borderImg.raycastTarget = false;
                    var brt = borderImg.rectTransform;
                    brt.anchorMin = new Vector2(0f, 1f);
                    brt.anchorMax = new Vector2(0f, 1f);
                    brt.pivot = new Vector2(0f, 1f);
                    brt.sizeDelta = new Vector2(cellSize, cellSize);
                    brt.anchoredPosition = new Vector2(
                        spacing + col * (cellSize + spacing),
                        -(spacing + row * (cellSize + spacing)));
                    groups.Add(cell.AddComponent<CanvasGroup>());

                    var iconGo = new GameObject("Icon");
                    iconGo.transform.SetParent(cell.transform, false);
                    var iconImg = iconGo.AddComponent<Image>();
                    iconImg.raycastTarget = false;
                    iconImg.preserveAspect = true;
                    var irt = iconImg.rectTransform;
                    irt.anchorMin = new Vector2(0.10f, 0.10f); irt.anchorMax = new Vector2(0.90f, 0.90f);
                    irt.offsetMin = Vector2.zero; irt.offsetMax = Vector2.zero;

                    StartCoroutine(LoadAndAssignAchievementIcon(iconImg, unlockedTemplates[i]));
                }
                StartCoroutine(StaggerFadeIn(groups, 0.03f, 0.15f));
            }

            // GClass4014.Instance.GetAllAchievementTemplates() + filter to
            // profile.AchievementsData keys. templates come back as opaque
            // objects (GClass4061) — caller reads .Sprite / LoadIconSprite via
            // reflection so we don't take a hard type dep on obfuscated names.
            // ids from both sides are lowercased for the match — MongoID's
            // ToString may not match GClass4061.Id verbatim across casings.
            private List<object> ResolveUnlockedTemplates()
            {
                var list = new List<object>();
                try
                {
                    var unlocked = AccessTools.Property(_profile.GetType(), "AchievementsData")?.GetValue(_profile)
                                ?? AccessTools.Field(_profile.GetType(), "AchievementsData")?.GetValue(_profile);
                    if (!(unlocked is System.Collections.IDictionary unlockedDict))
                    {
                        Plugin.Log.LogWarning("[Epilogue] AchievementsData not a Dictionary — path likely wrong");
                        return list;
                    }
                    Plugin.Log.LogInfo($"[Epilogue] AchievementsData has {unlockedDict.Count} unlocked id(s)");

                    var g4014 = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                        .FirstOrDefault(t => t.Name == "GClass4014");
                    if (g4014 == null) { Plugin.Log.LogWarning("[Epilogue] GClass4014 type not found"); return list; }
                    var instProp = g4014.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    var instance = instProp?.GetValue(null);
                    if (instance == null) { Plugin.Log.LogWarning("[Epilogue] GClass4014.Instance null"); return list; }
                    var getAll = g4014.GetMethod("GetAllAchievementTemplates", BindingFlags.Public | BindingFlags.Instance);
                    var all = getAll?.Invoke(instance, null) as System.Collections.IEnumerable;
                    if (all == null) { Plugin.Log.LogWarning("[Epilogue] GetAllAchievementTemplates returned null"); return list; }

                    var byId = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    int templateCount = 0;
                    foreach (var tpl in all)
                    {
                        if (tpl == null) continue;
                        var id = AccessTools.Property(tpl.GetType(), "Id")?.GetValue(tpl)?.ToString()
                              ?? AccessTools.Field(tpl.GetType(), "Id")?.GetValue(tpl)?.ToString();
                        if (!string.IsNullOrEmpty(id)) { byId[id] = tpl; templateCount++; }
                    }
                    Plugin.Log.LogInfo($"[Epilogue] {templateCount} global achievement template(s) indexed");

                    int matched = 0;
                    foreach (System.Collections.DictionaryEntry e in unlockedDict)
                    {
                        var id = e.Key?.ToString();
                        if (string.IsNullOrEmpty(id)) continue;
                        if (byId.TryGetValue(id, out var tpl)) { list.Add(tpl); matched++; }
                        else Plugin.Log.LogDebug($"[Epilogue] unlocked id '{id}' has no matching template");
                    }
                    Plugin.Log.LogInfo($"[Epilogue] matched {matched}/{unlockedDict.Count} unlocked achievements to templates");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] achievement templates resolve failed: {e.Message}"); }
                return list;
            }

            // template.Sprite may be null on first access — call LoadIconSprite(session)
            // (same async fetch AchievementIconView.method_1 uses) and poll the
            // task from a coroutine. deadline 5s so we don't hang on a bad url
            private IEnumerator LoadAndAssignAchievementIcon(Image target, object template)
            {
                if (target == null || template == null) yield break;
                var spriteProp = AccessTools.Property(template.GetType(), "Sprite");
                var sprite = spriteProp?.GetValue(template) as Sprite;
                if (sprite != null) { target.sprite = sprite; yield break; }

                var load = AccessTools.Method(template.GetType(), "LoadIconSprite");
                if (load == null || _session == null) yield break;

                System.Threading.Tasks.Task task = null;
                try { task = load.Invoke(template, new object[] { _session }) as System.Threading.Tasks.Task; }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] LoadIconSprite invoke failed: {e.Message}"); yield break; }

                if (task != null)
                {
                    float deadline = Time.unscaledTime + 5f;
                    while (!task.IsCompleted && Time.unscaledTime < deadline) yield return null;
                }

                sprite = spriteProp?.GetValue(template) as Sprite;
                if (target != null && sprite != null) target.sprite = sprite;
            }

            // rewards grid — matches retail PrestigeRewardView layout: each
            // card has a dark rectangular border, the reward's image fills the
            // card, and the kind + name labels sit in the top-right corner. a
            // card marked "big": true (main menu backgrounds like Bleak Horizon)
            // spans 2 grid columns. images live in plugin-data/epilogue/rewards/
            // per the JSON's `sprite` field.
            private void BuildRewardsSlide()
            {
                var panelGo = new GameObject("RwdPanel");
                panelGo.transform.SetParent(_rightRoot, false);
                var panelImg = panelGo.AddComponent<Image>();
                panelImg.color = new Color(0f, 0f, 0f, 0.72f);
                var prt = panelImg.rectTransform;
                prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
                prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;

                var stats = ProfileData.ReadStats(_profile);
                BuildProfileHeader(panelGo.transform, stats.Side, stats.Nickname);

                var rewards = _cfg["rewards"]?["items"] as JArray;
                Plugin.Log.LogInfo($"[Epilogue] rewards slide: {(rewards?.Count ?? 0)} item(s) in JSON");
                if (rewards == null || rewards.Count == 0) return;

                // 5-column pixel grid inside a ScrollRect. "big" = 2 cols wide
                // (matches retail's prestige-reward panel — walls/ceiling/floor
                // + main-menu-bg cards span 2 tiles each). fixed cell height in
                // pixels so scaling with resolution doesn't stretch cards weirdly.
                // viewport top well below the profile header (0.87-0.99)
                var content = MakeScrollableContent(panelGo, 0.82f, 0.02f);
                const int cols = 5;
                const float cellW = 200f;
                const float rowH = 220f;
                const float spacing = 12f;
                var dir = AssetsDir();
                var rewardsDir = Path.Combine(dir, "rewards");

                int cursorCol = 0;
                int cursorRow = 0;
                var rwdGroups = new List<CanvasGroup>();

                for (int i = 0; i < rewards.Count; i++)
                {
                    var r = rewards[i] as JObject;
                    if (r == null) continue;
                    bool big = r.Value<bool?>("big") ?? false;
                    int span = big ? 2 : 1;
                    if (cursorCol + span > cols) { cursorCol = 0; cursorRow++; }

                    float x = spacing + cursorCol * (cellW + spacing);
                    float y = -(spacing + cursorRow * (rowH + spacing));
                    float cardW = span * cellW + (span - 1) * spacing;

                    Sprite iconSprite = null;
                    var spriteFile = r.Value<string>("sprite");
                    if (!string.IsNullOrEmpty(spriteFile))
                        iconSprite = LoadSprite(Path.Combine(rewardsDir, spriteFile));

                    string kindTxt = r.Value<string>("kind") ?? "";
                    string labelTxt = r.Value<string>("label") ?? "";
                    bool itemKind = kindTxt.Equals("Item", StringComparison.OrdinalIgnoreCase)
                                 || kindTxt.Equals("Dogtag", StringComparison.OrdinalIgnoreCase);

                    // try to clone the game's own PrestigeRewardView prefab so
                    // we inherit its native card background (rounded stroke +
                    // gradient) rather than a flat gray rect. falls back to
                    // custom construction if the prefab isn't reachable.
                    var cell = TryClonePrestigeRewardCard(content, iconSprite, kindTxt, labelTxt, itemKind, cardW, rowH)
                               ?? BuildFallbackRewardCard(content, iconSprite, kindTxt, labelTxt);
                    cell.name = $"Rwd{i}";
                    var brt = cell.GetComponent<RectTransform>();
                    if (brt == null) brt = cell.AddComponent<RectTransform>();
                    brt.anchorMin = new Vector2(0f, 1f);
                    brt.anchorMax = new Vector2(0f, 1f);
                    brt.pivot = new Vector2(0f, 1f);
                    brt.sizeDelta = new Vector2(cardW, rowH);
                    brt.anchoredPosition = new Vector2(x, y);
                    rwdGroups.Add(cell.GetComponent<CanvasGroup>() ?? cell.AddComponent<CanvasGroup>());

                    cursorCol += span;
                    if (cursorCol >= cols) { cursorCol = 0; cursorRow++; }
                }

                // finalize content height so the last row is reachable
                int totalRows = cursorRow + (cursorCol > 0 ? 1 : 0);
                content.sizeDelta = new Vector2(0f, totalRows * (rowH + spacing) + spacing);

                StartCoroutine(StaggerFadeIn(rwdGroups, 0.10f, 0.20f));
            }

            // clone the vanilla PrestigeRewardView prefab (the same UI the
            // prestige menu shows) — includes the native card background,
            // stroke, gradient, and Bender text styling. we override the icon,
            // rewardType, rewardName, and background_1/2 visibility via
            // reflection. cached template lookup so we don't rescan each cell.
            private static object _prestigeTemplate;
            private static bool _prestigeTemplateChecked;
            private GameObject TryClonePrestigeRewardCard(Transform parent, Sprite iconSprite, string kind, string label, bool itemKind, float cardW, float rowH)
            {
                try
                {
                    if (!_prestigeTemplateChecked)
                    {
                        _prestigeTemplateChecked = true;
                        var t = AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                            .FirstOrDefault(x => x.Name == "PrestigeRewardView");
                        if (t != null)
                        {
                            var comps = Resources.FindObjectsOfTypeAll(t);
                            if (comps != null && comps.Length > 0) _prestigeTemplate = comps[0];
                        }
                    }
                    if (_prestigeTemplate == null) return null;

                    var srcComp = _prestigeTemplate as Component;
                    if (srcComp == null || srcComp.gameObject == null) return null;

                    var clone = UnityEngine.Object.Instantiate(srcComp.gameObject, parent, false);
                    clone.SetActive(true);
                    var cloneComp = clone.GetComponent(srcComp.GetType());

                    // the prefab's visual box is driven by its INTERNAL
                    // `_sizeContainer` RectTransform, not the root — retail's
                    // Show() sizes it based on IsBigImage. bypassing Show()
                    // left every card at the prefab's default (small) size, so
                    // big cards visually collapsed to small ones. push our
                    // cardW/rowH down to _sizeContainer to make big cards big.
                    var sizeContainer = AccessTools.Field(srcComp.GetType(), "_sizeContainer")?.GetValue(cloneComp) as RectTransform;
                    if (sizeContainer != null) sizeContainer.sizeDelta = new Vector2(cardW, rowH);

                    // set icon sprite
                    var iconField = AccessTools.Field(srcComp.GetType(), "_icon");
                    if (iconField != null && iconSprite != null)
                    {
                        var img = iconField.GetValue(cloneComp) as Image;
                        if (img != null) { img.sprite = iconSprite; img.preserveAspect = true; }
                    }
                    // labels
                    SetTmpText(cloneComp, "_rewardType", kind);
                    SetTmpText(cloneComp, "_rewardName", label);
                    // background variants: _background_1 for non-Item, _background_2 for Item
                    var bg1 = AccessTools.Field(srcComp.GetType(), "_background_1")?.GetValue(cloneComp) as GameObject;
                    var bg2 = AccessTools.Field(srcComp.GetType(), "_background_2")?.GetValue(cloneComp) as GameObject;
                    if (bg1 != null) bg1.SetActive(!itemKind);
                    if (bg2 != null) bg2.SetActive(itemKind);
                    // highlight off
                    var hl = AccessTools.Field(srcComp.GetType(), "_highlight")?.GetValue(cloneComp) as GameObject;
                    if (hl != null) hl.SetActive(false);

                    return clone;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] prestige clone failed, using fallback: {e.Message}"); return null; }
            }

            private static void SetTmpText(object comp, string fieldName, string text)
            {
                try
                {
                    var f = AccessTools.Field(comp.GetType(), fieldName);
                    var tgt = f?.GetValue(comp);
                    if (tgt == null) return;
                    var textProp = AccessTools.Property(tgt.GetType(), "text");
                    textProp?.SetValue(tgt, text);
                }
                catch { }
            }

            // custom card when the prestige prefab isn't reachable — dark
            // rectangle with icon fill + top-right labels. matches the
            // structure of the vanilla card so switching between the two
            // doesn't shift layout
            private GameObject BuildFallbackRewardCard(Transform parent, Sprite iconSprite, string kind, string label)
            {
                var cell = new GameObject("Rwd");
                cell.transform.SetParent(parent, false);
                var borderImg = cell.AddComponent<Image>();
                borderImg.color = new Color(0.18f, 0.18f, 0.18f, 0.90f);
                borderImg.raycastTarget = false;

                if (iconSprite != null)
                {
                    var iconGo = new GameObject("Image");
                    iconGo.transform.SetParent(cell.transform, false);
                    var iconImg = iconGo.AddComponent<Image>();
                    iconImg.sprite = iconSprite;
                    iconImg.preserveAspect = true;
                    iconImg.raycastTarget = false;
                    var irt = iconImg.rectTransform;
                    irt.anchorMin = new Vector2(0.02f, 0.02f); irt.anchorMax = new Vector2(0.98f, 0.98f);
                    irt.offsetMin = Vector2.zero; irt.offsetMax = Vector2.zero;
                }

                var kindLbl = MakeText(cell.transform, "Kind", kind, 12, TextAnchor.UpperRight);
                kindLbl.color = new Color(0.65f, 0.65f, 0.60f, 1f);
                kindLbl.raycastTarget = false;
                var krt = kindLbl.rectTransform;
                krt.anchorMin = new Vector2(0.02f, 0.88f); krt.anchorMax = new Vector2(0.98f, 0.98f);
                krt.offsetMin = Vector2.zero; krt.offsetMax = Vector2.zero;

                var nameLbl = MakeText(cell.transform, "Name", label, 14, TextAnchor.UpperRight);
                nameLbl.color = new Color(0.95f, 0.95f, 0.90f, 1f);
                nameLbl.raycastTarget = false;
                var nrt = nameLbl.rectTransform;
                nrt.anchorMin = new Vector2(0.02f, 0.80f); nrt.anchorMax = new Vector2(0.98f, 0.90f);
                nrt.offsetMin = Vector2.zero; nrt.offsetMax = Vector2.zero;

                return cell;
            }

            // reuse SessionResultKillList — the shell already has the real
            // Bender-formatted table with weapon icons, distance, body-part
            // callouts. we reparent its GameObject into our right-side slot,
            // stretch it to fill, then call Show(victims, tags) to populate.
            private void BuildKillfeedSlide()
            {
                var panelGo = new GameObject("KillPanelWrap");
                panelGo.transform.SetParent(_rightRoot, false);
                var panelImg = panelGo.AddComponent<Image>();
                panelImg.color = new Color(0f, 0f, 0f, 0.72f);
                var prt = panelImg.rectTransform;
                prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
                prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;

                try
                {
                    var ui = MonoBehaviourSingleton<SessionEndUI>.Instance;
                    var killList = ui != null ? ui.SessionResultKillList : null;
                    if (killList == null)
                    {
                        Plugin.Log.LogWarning("[Epilogue] SessionResultKillList not available");
                        var t = MakeText(panelGo.transform, "Empty", "kill list unavailable", 18, TextAnchor.MiddleCenter);
                        t.color = new Color(0.7f, 0.7f, 0.65f, 1f);
                        return;
                    }

                    // build the shell's KillList content INSIDE its own GO (populates
                    // its _container with victim rows via _victimTemplate), then
                    // reparent ONLY the container into our panel — leaves KillList's
                    // own NEXT/BACK buttons + backdrop offscreen where they belong
                    var eftStats = AccessTools.Property(_profile.GetType(), "EftStats")?.GetValue(_profile)
                                ?? AccessTools.Field(_profile.GetType(), "EftStats")?.GetValue(_profile);
                    var victims = AccessTools.Field(eftStats?.GetType(), "Victims")?.GetValue(eftStats)
                               ?? AccessTools.Property(eftStats?.GetType(), "Victims")?.GetValue(eftStats);
                    var tags = new EFT.InventoryLogic.DogtagComponent[0];

                    // ensure KillList's GO is active so Show() actually builds rows
                    var klGo = killList.gameObject;
                    _borrowedKillListGo = klGo;
                    if (!klGo.activeSelf) klGo.SetActive(true);

                    var showMethod = killList.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "Show" && m.GetParameters().Length == 2);
                    if (showMethod != null && victims != null)
                        showMethod.Invoke(killList, new[] { victims, tags });
                    else Plugin.Log.LogWarning($"[Epilogue] KillList Show(victims,tags) not resolved");

                    // reparent the CONTAINER (the RectTransform holding the
                    // KillListVictim rows) into a ScrollRect so long raids scroll
                    // instead of overflowing off-screen (2026-08-20 raid: 40+
                    // kills bled below the viewport and off the bottom of the
                    // slide). content anchors + pivot top so rows stack down
                    // and the ScrollRect drives their y-position via anchored
                    // position, clamped to the container's own height.
                    var container = AccessTools.Field(typeof(SessionResultKillList), "_container")?.GetValue(killList) as RectTransform;
                    if (container != null)
                    {
                        _borrowedContainer = container;
                        _borrowedContainerOrigParent = container.parent;
                        _borrowedContainerOrigAnchors = (container.anchorMin, container.anchorMax, container.offsetMin, container.offsetMax);
                        _borrowedContainerOrigSize = (container.sizeDelta, container.anchoredPosition, container.pivot);

                        // viewport top well below the "RAID KILL LIST" header
                        // (0.92-0.99) so row 1 doesn't overlap the label
                        var scrollContent = MakeScrollableContent(panelGo, 0.87f, 0.02f);
                        container.SetParent(scrollContent, false);
                        container.anchorMin = new Vector2(0f, 1f);
                        container.anchorMax = new Vector2(1f, 1f);
                        container.pivot = new Vector2(0.5f, 1f);
                        container.anchoredPosition = Vector2.zero;
                        // Unity hasn't laid the container out at this instant —
                        // its sizeDelta / rect.height read as 0 or a stale
                        // default. defer height measurement to the next frame
                        // via a coroutine, then push it to Content so the
                        // ScrollRect knows the full scrollable extent.
                        StartCoroutine(SizeKillContentAfterLayout(scrollContent, container));
                    }
                    else Plugin.Log.LogWarning("[Epilogue] KillList _container not reachable — killfeed slide will be empty");

                    // deactivate the KillList's own GameObject NOW — the rows
                    // live under our panel via the reparented container, so we
                    // don't need the KillList's shell (its own NEXT/BACK buttons,
                    // backdrop, header) rendering onscreen. keeping it active is
                    // what leaked the fake buttons + made the killfeed persist
                    // onto the thanks slide + trapped the user on the shell's
                    // kill screen after CloseAllScreensForced (2026-08-20 raid).
                    klGo.SetActive(false);

                    var header = MakeText(panelGo.transform, "Head", "RAID KILL LIST", 20, TextAnchor.MiddleCenter);
                    header.color = new Color(0.9f, 0.9f, 0.85f, 1f);
                    header.raycastTarget = false;
                    header.fontStyle = FontStyle.Bold;
                    var hrt = header.rectTransform;
                    hrt.anchorMin = new Vector2(0.04f, 0.92f); hrt.anchorMax = new Vector2(0.96f, 0.99f);
                    hrt.offsetMin = Vector2.zero; hrt.offsetMax = Vector2.zero;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] killfeed reuse failed: {e.Message}"); }
            }

            private RectTransform _borrowedContainer;
            private Transform _borrowedContainerOrigParent;
            private (Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax) _borrowedContainerOrigAnchors;
            private (Vector2 sizeDelta, Vector2 anchoredPos, Vector2 pivot) _borrowedContainerOrigSize;
            private GameObject _borrowedKillListGo;

            // wait for Unity to finalize the borrowed container's layout (its
            // rows are populated by GClass3823, a coroutine that yields), then
            // push the measured height into the scroll Content sizeDelta so
            // the ScrollRect knows the actual scroll extent. we poll for a
            // few frames — Unity's layout pass may take 1-2 frames after
            // reparent + Show, and Content stays sized to something reasonable
            // in the meantime.
            private IEnumerator SizeKillContentAfterLayout(RectTransform scrollContent, RectTransform container)
            {
                for (int i = 0; i < 8; i++)
                {
                    yield return null;
                    if (scrollContent == null || container == null) yield break;
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(container);
                    float h = Mathf.Max(container.rect.height, container.sizeDelta.y);
                    if (h > 1f)
                    {
                        scrollContent.sizeDelta = new Vector2(0f, h + 20f);
                        // ScrollRect might have drifted after our size push —
                        // re-force top so row 1 sits at viewport top, not
                        // above it (matches ForceScrollTop in MakeScrollableContent)
                        var sr = scrollContent.parent != null ? scrollContent.parent.parent?.GetComponent<UnityEngine.UI.ScrollRect>() : null;
                        if (sr != null) sr.verticalNormalizedPosition = 1f;
                        yield break;
                    }
                }
                // fallback: give Content some scrollable extent regardless
                if (scrollContent != null) scrollContent.sizeDelta = new Vector2(0f, 3000f);
            }

            // EFT's Bender is the standard menu font — matches the rest of the
            // results-screen chrome + our subtitles. falls back to Arial if the
            // Bender pair wasn't loaded (headless test, cold cache).
            private static Text MakeText(Transform parent, string name, string content, int size, TextAnchor align)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                var t = go.AddComponent<Text>();
                t.text = content;
                t.font = _benderRegular != null
                    ? _benderRegular
                    : Resources.GetBuiltinResource<Font>("Arial.ttf");
                t.fontSize = size;
                t.alignment = align;
                t.horizontalOverflow = HorizontalWrapMode.Wrap;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                var rt = t.rectTransform;
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                return t;
            }

            private void Update()
            {
                if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    Advance();
                else if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Escape))
                    Regress();
            }

            private void Advance()
            {
                // if the typewriter is still animating, first click completes
                // the text (skip-on-click) and reveals NEXT; a second click
                // moves to the next slide
                if (!_typewriterDone && _typewriterTarget != null && _typewriterFullText != null)
                {
                    if (_typewriterCo != null) StopCoroutine(_typewriterCo);
                    _typewriterCo = null;
                    _typewriterTarget.text = _typewriterFullText;
                    _typewriterDone = true;
                    UpdateButtonVisibility(_slides[_slideIx].Value<string>("kind") ?? "");
                    return;
                }
                if (_slideIx + 1 >= _slides.Count) { CloseToMenu(); return; }
                ShowSlide(_slideIx + 1);
            }

            private void Regress()
            {
                if (_slideIx == 0) return;
                ShowSlide(_slideIx - 1);
            }

            // final NEXT: restore the borrowed KillList container to its
            // original parent + anchors, then force-close the whole SessionEnd
            // stack. CurrentScreenSingletonClass.CloseAllScreensForced tears
            // down every registered screen (ExitStatus / KillList / Stats /
            // XP) and returns to the main menu — verified path used by
            // MainMenuControllerClass:792.
            // final NEXT: invoke the ScreenController's GoToMainMenu (fires
            // OnGoToMainMenu event that PostRaidHealthScreenClass.method_14
            // is subscribed to → method_9 → method_10 does the actual close +
            // preloader + main-menu transition). previous CloseAllScreensForced
            // approach only closed screens but left the UI in a null state
            // (empty menu, stuck — 2026-08-20 raid).
            //
            // ScreenController is a PROTECTED FIELD on BaseScreen<T,U>, not a
            // property — AccessTools.Field walks base types so it resolves.
            private void CloseToMenu()
            {
                if (_audio != null) { try { _audio.Stop(); } catch { } }
                RestoreBorrowedContainer();
                UnityEngine.Object.Destroy(gameObject);
                try
                {
                    var controller = AccessTools.Field(_host.GetType(), "ScreenController")?.GetValue(_host);
                    if (controller != null)
                    {
                        var goMenu = AccessTools.Method(controller.GetType(), "GoToMainMenu");
                        if (goMenu != null)
                        {
                            goMenu.Invoke(controller, null);
                            Plugin.Log.LogInfo("[Epilogue] ScreenController.GoToMainMenu → main menu");
                        }
                        else Plugin.Log.LogWarning("[Epilogue] GoToMainMenu method not found on controller");
                    }
                    else Plugin.Log.LogWarning("[Epilogue] ScreenController field not reachable — main menu handoff missed");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] menu handoff failed: {e.Message}"); }
            }

            private void RestoreBorrowedContainer()
            {
                if (_borrowedContainer == null || _borrowedContainerOrigParent == null) return;
                try
                {
                    _borrowedContainer.SetParent(_borrowedContainerOrigParent, false);
                    _borrowedContainer.anchorMin = _borrowedContainerOrigAnchors.aMin;
                    _borrowedContainer.anchorMax = _borrowedContainerOrigAnchors.aMax;
                    _borrowedContainer.offsetMin = _borrowedContainerOrigAnchors.offMin;
                    _borrowedContainer.offsetMax = _borrowedContainerOrigAnchors.offMax;
                    _borrowedContainer.pivot = _borrowedContainerOrigSize.pivot;
                    _borrowedContainer.sizeDelta = _borrowedContainerOrigSize.sizeDelta;
                    _borrowedContainer.anchoredPosition = _borrowedContainerOrigSize.anchoredPos;
                    // KillList GO stays inactive — we already handed off; the
                    // shell will re-activate it via CurrentScreenSingletonClass
                    // if it's ever shown again
                }
                catch { }
                _borrowedContainer = null;
                _borrowedContainerOrigParent = null;
                _borrowedKillListGo = null;
            }

            private void OnDestroy()
            {
                RestoreBorrowedContainer();
            }
        }

        // reflection-safe reader over EFT.Profile — paths verified against
        // D:\SPT400_assembly\ (Profile.cs, InfoClass.cs, ProfileStats.cs,
        // SessionCountersClass.cs, SessionCounterTypesAbstractClass.cs).
        // reads directly off the Profile passed in (guaranteed valid at
        // SessionResultExitStatus.Show time), so no null-GameWorld hazards.
        internal static class ProfileData
        {
            internal struct Stats
            {
                public string Nickname, GameEdition, GameMode, GameStyle, PlayedTime, Kd, SurvivalRate, FavoriteWeapon;
                public string Side; // "Usec" / "Bear" / "Savage" — from Info.Side
                public int Level, PrestigeLevel;
                public long TotalRaids, PmcKills, ScavKills, BossKills, WinStreak;
            }
            internal struct AchvRow { public string Id, Title; public bool Unlocked; }
            internal struct KillRow { public string Name, Location, Time, Faction, Status; public int Level; }

            internal static Stats ReadStats(Profile prof)
            {
                var s = new Stats
                {
                    Nickname = "—", GameEdition = "—", GameMode = "—", GameStyle = "—",
                    PlayedTime = "—", Kd = "—", SurvivalRate = "—", FavoriteWeapon = "—",
                };
                try
                {
                    if (prof == null) return s;
                    var info = AccessTools.Property(prof.GetType(), "Info")?.GetValue(prof)
                            ?? AccessTools.Field(prof.GetType(), "Info")?.GetValue(prof);
                    if (info != null)
                    {
                        s.Nickname = GetStr(info, "Nickname") ?? "—";
                        s.Level = GetInt(info, "Level");
                        s.PrestigeLevel = GetInt(info, "PrestigeLevel");
                        s.GameEdition = MapEdition(GetStr(info, "GameVersion"));
                        s.Side = GetStr(info, "Side") ?? "";
                        var pve = GetBool(info, "HasPveGame");
                        s.GameMode = pve.HasValue ? (pve.Value ? "PVE" : "PVP") : "—";
                    }

                    var eftStats = AccessTools.Property(prof.GetType(), "EftStats")?.GetValue(prof)
                                ?? AccessTools.Field(prof.GetType(), "EftStats")?.GetValue(prof);
                    if (eftStats != null)
                    {
                        var seconds = GetLongVal(eftStats, "TotalInGameTime");
                        if (seconds.HasValue) s.PlayedTime = FormatTime(TimeSpan.FromSeconds(seconds.Value));
                    }

                    var overall = AccessTools.Field(eftStats?.GetType(), "OverallCounters")?.GetValue(eftStats)
                               ?? AccessTools.Property(eftStats?.GetType(), "OverallCounters")?.GetValue(eftStats);
                    if (overall != null)
                    {
                        s.TotalRaids = CounterLong(overall, "Sessions");
                        s.WinStreak  = CounterLong(overall, "LongestWinStreak");
                        s.PmcKills   = CounterLong(overall, "KilledPmc");
                        s.ScavKills  = CounterLong(overall, "KilledSavage");
                        s.BossKills  = CounterLong(overall, "KilledBoss");
                        long kills   = CounterLong(overall, "Kills");
                        long deaths  = CounterLong(overall, "Deaths");
                        s.Kd = deaths > 0 ? (kills / (float)deaths).ToString("0.0") : kills.ToString("0.0");
                        if (s.TotalRaids > 0)
                        {
                            long survived = Math.Max(0, s.TotalRaids - deaths);
                            s.SurvivalRate = ((survived * 100L) / s.TotalRaids).ToString() + " %";
                        }
                    }

                    var surv = AccessTools.Property(prof.GetType(), "SurvivorClass")?.GetValue(prof)
                            ?? AccessTools.Field(prof.GetType(), "SurvivorClass")?.GetValue(prof);
                    if (surv != null) s.GameStyle = surv.ToString();
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] stats read failed: {e.Message}"); }
                return s;
            }

            internal static List<AchvRow> ReadAchievements(Profile prof)
            {
                var list = new List<AchvRow>();
                try
                {
                    if (prof == null) return list;
                    var achv = AccessTools.Property(prof.GetType(), "AchievementsData")?.GetValue(prof)
                            ?? AccessTools.Field(prof.GetType(), "AchievementsData")?.GetValue(prof)
                            ?? AccessTools.Property(prof.GetType(), "Achievements")?.GetValue(prof);
                    if (achv is System.Collections.IDictionary dict)
                        foreach (System.Collections.DictionaryEntry e in dict)
                        {
                            var id = e.Key?.ToString() ?? "";
                            var t = 0;
                            try { t = Convert.ToInt32(e.Value); } catch { }
                            list.Add(new AchvRow { Id = id, Title = id.Length > 6 ? id.Substring(0, 6) : id, Unlocked = t > 0 });
                        }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] achievements read failed: {e.Message}"); }
                return list;
            }

            internal static List<KillRow> ReadVictims(Profile prof)
            {
                var list = new List<KillRow>();
                try
                {
                    if (prof == null) return list;
                    var eftStats = AccessTools.Property(prof.GetType(), "EftStats")?.GetValue(prof)
                                ?? AccessTools.Field(prof.GetType(), "EftStats")?.GetValue(prof);
                    if (eftStats == null) return list;
                    var victims = AccessTools.Field(eftStats.GetType(), "Victims")?.GetValue(eftStats)
                               ?? AccessTools.Property(eftStats.GetType(), "Victims")?.GetValue(eftStats);
                    if (victims is System.Collections.IEnumerable seq)
                        foreach (var v in seq) list.Add(VictimToRow(v));
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Epilogue] victims read failed: {e.Message}"); }
                return list;
            }

            private static KillRow VictimToRow(object v)
            {
                var r = new KillRow { Name = "—", Location = "Terminal", Time = "—", Faction = "—", Status = "—" };
                if (v == null) return r;
                r.Name = GetStr(v, "Name") ?? "—";
                r.Location = GetStr(v, "Location") ?? "Terminal";
                r.Level = GetInt(v, "Level");
                var t = AccessTools.Property(v.GetType(), "Time")?.GetValue(v)
                     ?? AccessTools.Field(v.GetType(), "Time")?.GetValue(v);
                if (t is TimeSpan ts) r.Time = string.Format("{0:00}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
                var side = AccessTools.Property(v.GetType(), "Side")?.GetValue(v)
                        ?? AccessTools.Field(v.GetType(), "Side")?.GetValue(v);
                var role = AccessTools.Property(v.GetType(), "Role")?.GetValue(v)
                        ?? AccessTools.Field(v.GetType(), "Role")?.GetValue(v);
                r.Faction = MapFaction(side?.ToString(), role?.ToString());
                var weapon = GetStr(v, "Weapon") ?? "";
                var body = (AccessTools.Property(v.GetType(), "BodyPart")?.GetValue(v)
                         ?? AccessTools.Field(v.GetType(), "BodyPart")?.GetValue(v))?.ToString() ?? "";
                float dist = 0f;
                try { dist = Convert.ToSingle(AccessTools.Property(v.GetType(), "Distance")?.GetValue(v)
                                       ?? AccessTools.Field(v.GetType(), "Distance")?.GetValue(v) ?? 0f); } catch { }
                string bodyLabel = body == "Head" ? "Headshot" : body;
                r.Status = weapon.Length > 0 ? $"{bodyLabel} ({ShortWeapon(weapon)}, {dist:0.0}m)" : bodyLabel;
                return r;
            }

            private static long CounterLong(object overall, string tagName)
            {
                try
                {
                    // preferred: pass the SessionCounterIdentifierValueClass directly
                    // to the typed GetLong(SessionCounterIdentifierValueClass) overload —
                    // avoids GetLong(params object[])'s enum-hashing path which won't
                    // match a pre-built identifier
                    var identType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                        .FirstOrDefault(t => t.Name == "SessionCounterTypesAbstractClass");
                    if (identType == null) return 0;

                    var field = identType.GetField(tagName, BindingFlags.Public | BindingFlags.Static);
                    var key = field?.GetValue(null);
                    if (key == null) return 0;

                    var m = overall.GetType().GetMethod("GetLong", new[] { key.GetType() });
                    if (m != null) return Convert.ToInt64(m.Invoke(overall, new[] { key }));
                }
                catch (Exception e) { Plugin.Log.LogDebug($"[Epilogue] counter '{tagName}' read failed: {e.Message}"); }
                return 0;
            }

            private static string GetStr(object obj, string name)
            {
                if (obj == null) return null;
                var p = AccessTools.Property(obj.GetType(), name);
                if (p != null) return p.GetValue(obj)?.ToString();
                var f = AccessTools.Field(obj.GetType(), name);
                return f?.GetValue(obj)?.ToString();
            }
            private static int GetInt(object obj, string name)
            {
                try { return Convert.ToInt32(AccessTools.Property(obj.GetType(), name)?.GetValue(obj) ?? AccessTools.Field(obj.GetType(), name)?.GetValue(obj) ?? 0); }
                catch { return 0; }
            }
            private static bool? GetBool(object obj, string name)
            {
                try { var v = AccessTools.Property(obj.GetType(), name)?.GetValue(obj) ?? AccessTools.Field(obj.GetType(), name)?.GetValue(obj); return v is bool b ? b : (bool?)null; }
                catch { return null; }
            }
            private static long? GetLongVal(object obj, string name)
            {
                try { var v = AccessTools.Property(obj.GetType(), name)?.GetValue(obj) ?? AccessTools.Field(obj.GetType(), name)?.GetValue(obj); return v == null ? (long?)null : Convert.ToInt64(v); }
                catch { return null; }
            }

            private static string FormatTime(TimeSpan ts)
            {
                int days = (int)ts.TotalDays;
                return $"{days}.{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
            }

            private static string MapEdition(string key)
            {
                if (string.IsNullOrEmpty(key)) return "—";
                switch (key.ToLowerInvariant())
                {
                    case "standard":           return "Standard Edition";
                    case "left_behind":        return "Left Behind Edition";
                    case "prepare_for_escape": return "Prepare for Escape Edition";
                    case "edge_of_darkness":   return "Edge of Darkness";
                    case "unheard_edition":    return "The Unheard Edition";
                    default: return key;
                }
            }

            private static string MapFaction(string side, string role)
            {
                if (!string.IsNullOrEmpty(role))
                {
                    var r = role.ToLowerInvariant();
                    if (r.StartsWith("boss") || r.Contains("boss")) return "BOSS";
                    if (r == "assault" || r == "marksman" || r == "cursedassault") return "SCAV";
                    if (r == "sptbear" || r == "sptusec" || r == "pmcbear" || r == "pmcusec") return "PMC";
                }
                if (!string.IsNullOrEmpty(side))
                {
                    var s = side.ToLowerInvariant();
                    if (s == "bear" || s == "usec") return "PMC";
                    if (s == "savage") return "SCAV";
                }
                return "—";
            }

            private static string ShortWeapon(string weapon)
            {
                if (string.IsNullOrEmpty(weapon)) return "";
                var space = weapon.IndexOf(' ');
                if (space > 0 && weapon.Length - space > 24) return weapon.Substring(0, space);
                return weapon.Length > 20 ? weapon.Substring(0, 20) : weapon;
            }
        }
    }
}
