using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using EFT;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // the SOUND rig orchestrator, icebreaker AmbientAudio model: the ambient
    // player/spline stack (OneShotAmbientSoundPlayer, AmbientSoundPlayer, the whole
    // BezierSpline mover chain) is ALIVE in 4.0's Assembly-CSharp, so we re-attach
    // BSG's own components from the sound_rig_components.json sidecar and pour the
    // authored fields back — BSG code then does the playing, including the roaming
    // spline emitters (footstep/shout squads crawling their beziers) and the
    // per-trigger chances. EFT.SoundBank's 1.0 layout drifted, so bank instances are
    // rebuilt via reflection from sound_rig_banks.json + the Author 26 holder clips.
    //
    // what stayed OURS (classes 1.0-only, verified dead in the 4.0 DLL):
    //   - phase gating (retail used CutsceneStart/EndEventFilter): the between/after
    //     group objects activate off our cutscene drivers' FinishedAt anchors
    //   - the firefight director (FirefightArea/AmbientFirefightDirector dead) —
    //     simulated from the parsed FirefightAreaAudioPreset
    //   - the sirens (SyncLoopSoundPlayer is live but server-sync-driven)
    //   - mixer fades during cutscenes: subsumed by group deactivation — the attack
    //     timeline's own mixed audio owns those seconds
    internal class TerminalSoundRig : MonoBehaviour
    {
        private const string HolderName = "Terminal_SoundRig_ClipHolder";
        private const string SoundRootName = "SOUND";
        private const string BetweenGroup = "AmbBetween1And2Cutscenes";
        private const string AfterGroup = "AmbAfter2Cutscene";

        private static TerminalSoundRig _instance;
        internal static void ResetForNewRaid()
        {
            if (_instance != null) Destroy(_instance.gameObject);
            _instance = null;
        }

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_AttachAtRaidStart
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On || !Plugin.SoundRig.Value || _instance != null) return;
                var go = new GameObject("Terminal_SoundRig");
                _instance = go.AddComponent<TerminalSoundRig>();
            }
        }

        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, UnityEngine.Object> _banks = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();
        private Dictionary<string, List<Transform>> _byPath;
        private readonly List<Component> _restored = new List<Component>();
        private readonly List<AudioSource> _alarmSrcs = new List<AudioSource>();
        private GameObject _betweenGo, _afterGo;
        private FirefightRunner _firefight;
        private bool _initialized;
        private float _startedAt;

        private enum Phase { Waiting, Between, Attack, After }
        private Phase _phase = Phase.Waiting;
        private bool _betweenVoiced, _afterVoiced;

        private void Start()
        {
            _startedAt = Time.realtimeSinceStartup;
            try { Init(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[SoundRig] init failed: {e} — rig disabled");
                enabled = false;
            }
        }

        // ---- init: component restore ---------------------------------------------

        private void Init()
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var compFile = Path.Combine(dir, "plugin-data", "sound_rig_components.json");
            var bankFile = Path.Combine(dir, "plugin-data", "sound_rig_banks.json");
            if (!File.Exists(compFile))
            {
                Plugin.Log.LogWarning($"[SoundRig] sidecar missing: {compFile}");
                enabled = false;
                return;
            }

            var soundRoot = FindSoundRoot();
            if (soundRoot == null)
            {
                Plugin.Log.LogWarning("[SoundRig] no SOUND object in any Terminal scene — rig not restored");
                enabled = false;
                return;
            }

            IndexClips();
            if (_clips.Count == 0)
                Plugin.Log.LogWarning("[SoundRig] clip holder not found — bank players will be silent (Author 26 + bundle rebuild needed)");
            if (File.Exists(bankFile)) BuildBanks(JObject.Parse(File.ReadAllText(bankFile)));

            // groups stay dark until their phase — retail gated them on cutscene
            // event filters that no longer exist
            _betweenGo = ChildByName(soundRoot.transform, BetweenGroup);
            _afterGo = ChildByName(soundRoot.transform, AfterGroup);
            if (_betweenGo != null) _betweenGo.SetActive(false);
            if (_afterGo != null) _afterGo.SetActive(false);

            // ORDER (icebreaker lesson): the whole hierarchy is rebuilt while the root
            // is INACTIVE — components attached, fields poured — then reactivated so
            // the Awakes cascade with their data already present
            var records = JArray.Parse(File.ReadAllText(compFile));
            bool wasActive = soundRoot.activeSelf;
            soundRoot.SetActive(false);
            try
            {
                IndexHierarchy(soundRoot.transform);

                // players FIRST: the spline helpers declare [RequireComponent] on the
                // abstract BaseAmbientSoundPlayer, and unity refuses to auto-add an
                // abstract class — the concrete player must already sit on the object
                var ordered = new List<JToken>(records.Count);
                foreach (var t in records) if (IsPlayerRecord(t)) ordered.Add(t);
                foreach (var t in records) if (!IsPlayerRecord(t)) ordered.Add(t);

                int placed = 0, noObject = 0, noType = 0;
                var built = new List<KeyValuePair<JObject, Component>>(records.Count);
                var refused = new HashSet<string>();
                foreach (var tok in ordered)
                {
                    var rec = (JObject)tok;
                    var type = ResolveType(rec.Value<string>("cls"));
                    if (type == null || !typeof(Component).IsAssignableFrom(type))
                    { noType++; refused.Add(rec.Value<string>("cls") ?? "?"); continue; }
                    var target = TakeTransform(rec.Value<string>("path"), type);
                    if (target == null) { noObject++; continue; }
                    var comp = target.gameObject.GetComponent(type) ?? target.gameObject.AddComponent(type);
                    if (comp == null) { refused.Add(type.Name); continue; }
                    built.Add(new KeyValuePair<JObject, Component>(rec, comp));
                    placed++;
                }

                int fields = 0, fieldFails = 0;
                foreach (var kv in built)
                {
                    var f = kv.Key["fields"] as JObject;
                    if (f == null || kv.Value == null) continue;
                    foreach (var prop in f)
                    {
                        try { if (SetField(kv.Value, prop.Key, prop.Value)) fields++; }
                        catch { fieldFails++; }
                    }
                    _restored.Add(kv.Value);
                }
                Plugin.Log.LogInfo($"[SoundRig] rebuilt {placed}/{records.Count} components "
                                 + $"({fields} fields, {fieldFails} field errors, {noObject} missing objects, {noType} dead types)"
                                 + (refused.Count > 0 ? $"; refused: {string.Join(", ", new List<string>(refused).ToArray())}" : ""));
                if (_missingClips.Count > 0)
                    Plugin.Log.LogWarning($"[SoundRig] clips not found: {string.Join(", ", new List<string>(_missingClips).ToArray())}");
            }
            finally
            {
                soundRoot.SetActive(wasActive);
            }

            int mixerRouted = TerminalAudioRouting.RouteTree(soundRoot);
            SetupAlarmAndFirefight(dir);
            _initialized = true;
            Plugin.Log.LogInfo($"[SoundRig] up: {_restored.Count} live components, {_banks.Count} banks, "
                             + $"{_clips.Count} clips, {_alarmSrcs.Count} sirens, firefight {(_firefight != null ? "armed" : "off")}, "
                             + $"{mixerRouted} source(s) -> Tarkov AmbientOut mixer");
        }

        private static GameObject FindSoundRoot()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded || sc.name == null || !sc.name.StartsWith("Terminal")) continue;
                foreach (var go in sc.GetRootGameObjects())
                    if (go.name == SoundRootName) return go;
            }
            return null;
        }

        private static GameObject ChildByName(Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++)
                if (root.GetChild(i).name == name) return root.GetChild(i).gameObject;
            return null;
        }

        private void IndexClips()
        {
            // the Author 26 holder carries the bank clips; scene-authored clips (the
            // sirens, door foley) ride along via the full sweep
            foreach (var src in Resources.FindObjectsOfTypeAll<AudioSource>())
            {
                if (src == null || src.clip == null) continue;
                var sc = src.gameObject.scene;
                if (!sc.IsValid() || sc.name == null || !sc.name.StartsWith("Terminal")) continue;
                if (!_clips.ContainsKey(src.clip.name)) _clips[src.clip.name] = src.clip;
            }
        }

        // EFT.SoundBank rebuild — only PickSingleClip(0) -> Environments[0][0] matters
        // to the ambient players, so a bank carrying the right clips in slot zero
        // behaves exactly like the retail asset (icebreaker-proven)
        private void BuildBanks(JObject wanted)
        {
            var bankType = ResolveType("EFT.SoundBank");
            var envType = ResolveType("EFT.EnvironmentVariety");
            var distType = ResolveType("EFT.DistanceVarity");
            if (bankType == null || envType == null || distType == null)
            {
                Plugin.Log.LogWarning("[SoundRig] SoundBank/EnvironmentVariety/DistanceVarity missing — bank players stay silent");
                return;
            }
            int builtN = 0;
            var thin = new List<string>();
            foreach (var kv in wanted)
            {
                var names = kv.Value as JArray;
                if (names == null) continue;
                var clips = new List<AudioClip>();
                foreach (var n in names)
                {
                    var cn = n.Value<string>();
                    if (cn != null && _clips.TryGetValue(cn, out var clip) && clip != null) clips.Add(clip);
                    else if (cn != null) _missingClips.Add(cn);
                }
                if (clips.Count == 0) { thin.Add(kv.Key); continue; }

                var bank = ScriptableObject.CreateInstance(bankType);
                bank.name = kv.Key;
                var dist = Activator.CreateInstance(distType);
                AccessTools.Field(distType, "Clips").SetValue(dist, clips.ToArray());
                var variety = Activator.CreateInstance(envType);
                var vClips = Array.CreateInstance(distType, 1);
                vClips.SetValue(dist, 0);
                AccessTools.Field(envType, "Clips").SetValue(variety, vClips);
                var envs = Array.CreateInstance(envType, 1);
                envs.SetValue(variety, 0);
                AccessTools.Field(bankType, "Environments").SetValue(bank, envs);
                AccessTools.Field(bankType, "HasEnvironment")?.SetValue(bank, false);
                _banks[kv.Key] = bank;
                builtN++;
            }
            Plugin.Log.LogDebug($"[SoundRig] rebuilt {builtN} sound bank(s)"
                              + (thin.Count > 0 ? $"; no clips for: {string.Join(", ", thin.ToArray())}" : ""));
        }

        private void SetupAlarmAndFirefight(string dir)
        {
            try
            {
                var file = Path.Combine(dir, "plugin-data", "sound_rig.json");
                if (!File.Exists(file)) return;
                var spec = JObject.Parse(File.ReadAllText(file));

                foreach (var a in spec["alarm"] as JArray ?? new JArray())
                {
                    var path = a.Value<string>("path");
                    if (path == null || !_byPath.TryGetValue(path, out var list) || list.Count == 0) continue;
                    var src = list[0].GetComponent<AudioSource>();
                    if (src == null || src.clip == null) continue;
                    src.loop = a.Value<int?>("loop") != 0;
                    src.playOnAwake = false;
                    _alarmSrcs.Add(src);
                }

                var ff = spec["firefight"] as JObject;
                if (ff != null && ff["areas"] is JArray areas && areas.Count > 0 && Plugin.SoundRigFirefight.Value)
                    _firefight = new FirefightRunner(this, ff, _clips, _byPath);
                else if (!Plugin.SoundRigFirefight.Value)
                    Plugin.Log.LogInfo("[SoundRig] firefight OFF (config A/B)");

                _doorOpen = spec["doorOpen"] as JObject;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[SoundRig] alarm/firefight setup failed: {e.Message}"); }
        }

        private JObject _doorOpen;

        // retail's post-attack beat (ClientInteractiveTriggerEventFilter, dead in 4.0):
        // the checkpoint cutscene door unlocks and the AdditionalSounds tableau plays —
        // cardlock release, door-push foley, VSRF voice. only the voice's clip survived
        // on its scene source (2026-08-18 report); the other two now resolve from the
        // fx bundle.
        private System.Collections.IEnumerator DoorOpenBeat()
        {
            if (_doorOpen == null) yield break;
            var doorId = _doorOpen.Value<string>("doorId");
            EFT.Interactive.Door door = null;
            if (!string.IsNullOrEmpty(doorId))
            {
                foreach (var d in UnityEngine.Object.FindObjectsOfType<EFT.Interactive.Door>(true))
                {
                    if (d == null || d.Id != doorId) continue;
                    door = d;
                    try
                    {
                        // retail authors this door Locked|Shut ONLY — players never open
                        // it, the EVENT does (the vsrf foley IS the door opening). widen
                        // the state mask so it stays usable afterward, then unlock.
                        d.Snap = EFT.Interactive.EDoorState.Locked | EFT.Interactive.EDoorState.Shut | EFT.Interactive.EDoorState.Open;
                        if (d.DoorState == EFT.Interactive.EDoorState.Locked)
                            d.DoorState = EFT.Interactive.EDoorState.Shut;
                        d.KeyId = string.Empty;
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"[SoundRig] door unlock failed: {e.Message}"); }
                    break;
                }
                Plugin.Log.LogInfo($"[SoundRig] cutscene door '{doorId}': {(door != null ? "unlocked" : "NOT FOUND")}");
            }

            float t0 = Time.realtimeSinceStartup;
            foreach (var sTok in _doorOpen["sounds"] as JArray ?? new JArray())
            {
                var s = (JObject)sTok;
                float delay = s.Value<float?>("delay") ?? 0f;
                while (Time.realtimeSinceStartup - t0 < delay) yield return null;
                var path = s.Value<string>("path");
                if (path == null || !_byPath.TryGetValue(path, out var list) || list.Count == 0) continue;
                var src = list[0].GetComponent<AudioSource>();
                if (src == null) continue;
                var clipName = s.Value<string>("clip");
                if (src.clip == null && clipName != null && _clips.TryGetValue(clipName, out var clip))
                    src.clip = clip;
                if (src.clip == null && clipName != null)
                    src.clip = TerminalFxBundle.FindClip(clipName);
                if (src.clip == null)
                    Plugin.Log.LogWarning($"[SoundRig] doorOpen sound '{clipName}' has no clip anywhere — silent");
                if (src.clip != null)
                {
                    src.loop = false;
                    src.volume = (s.Value<float?>("volume") ?? 1f) * Plugin.SoundRigVolume.Value;
                    src.Play();
                }
            }
        }

        // ---- phase machine --------------------------------------------------------

        private void Update()
        {
            if (!_initialized) return;
            float now = Time.realtimeSinceStartup;

            if (TerminalAttackCutscene.PlayingNow)
            {
                if (_phase != Phase.Attack) EnterAttack();
            }
            else if (TerminalAttackCutscene.FinishedAt >= 0f)
            {
                if (_phase != Phase.After) EnterAfter();
            }
            else if (_phase == Phase.Waiting)
            {
                // between-phase anchor mirrors the attack timer: intro end, with a
                // raid-start fallback if the intro never reports (disabled/bailed)
                float anchor = TerminalIntroCutscene.FinishedAt;
                if (anchor >= 0f || now - _startedAt > 180f) EnterBetween();
            }

            _firefight?.Tick(now, _phase == Phase.After);
        }

        private void EnterBetween()
        {
            _phase = Phase.Between;
            if (_betweenGo != null)
            {
                _betweenGo.SetActive(true);
                if (!_betweenVoiced) { _betweenVoiced = true; VoiceGroup(_betweenGo); }
            }
            Plugin.Log.LogInfo("[SoundRig] phase BETWEEN — pre-attack ambience up");
        }

        private void EnterAttack()
        {
            _phase = Phase.Attack;
            // deactivation stops the players and their coroutines — the timeline's own
            // mixed audio owns the attack (this is the mixer-fade handlers' job in retail)
            if (_betweenGo != null) _betweenGo.SetActive(false);
            if (_afterGo != null) _afterGo.SetActive(false);
            foreach (var s in _alarmSrcs) if (s != null && s.isPlaying) s.Stop();
            _firefight?.StopAll();
            Plugin.Log.LogInfo("[SoundRig] attack cutscene — rig silenced");
        }

        private void EnterAfter()
        {
            _phase = Phase.After;
            if (_betweenGo != null) _betweenGo.SetActive(false);
            if (_afterGo != null)
            {
                _afterGo.SetActive(true);
                if (!_afterVoiced) { _afterVoiced = true; VoiceGroup(_afterGo); }
            }
            if (Plugin.SoundRigAlarm.Value)
                foreach (var s in _alarmSrcs)
                    if (s != null) s.Play();
            StartCoroutine(DoorOpenBeat());
            Plugin.Log.LogInfo($"[SoundRig] phase AFTER — post-attack ambience up"
                             + (Plugin.SoundRigAlarm.Value ? $", {_alarmSrcs.Count} sirens on" : ""));
        }

        // spatialise the sources and hand playback to BSG's own components. runs AFTER
        // the group went active — Play() on a disabled hierarchy is a silent no-op
        private void VoiceGroup(GameObject group)
        {
            int voiced = 0, started = 0, dead = 0;
            foreach (var comp in _restored)
            {
                if (comp == null || !comp.transform.IsChildOf(group.transform)) continue;
                if (!IsPlayer(comp)) continue;
                if (ApplyToSource(comp)) voiced++;
                if (StartPlayer(comp)) started++;
                else dead++;
            }
            Plugin.Log.LogDebug($"[SoundRig] group '{group.name}': {voiced} spatialised, {started} started, {dead} not started");
        }

        private bool ApplyToSource(Component player)
        {
            var src = player.GetComponent<AudioSource>();
            if (src == null) return false;
            TerminalAudioRouting.Route(src);
            var t = player.GetType();
            src.playOnAwake = false;
            src.mute = false;
            src.spatialBlend = Get<float>(player, t, "_spatialBlend", 1f);
            src.minDistance = Get<float>(player, t, "_minDistance", 1f);
            src.maxDistance = Get<float>(player, t, "_maxDistance", 20f);
            // spread is INVERTED 0-1 in BSG data — SetSpread does Lerp(180, 0, value)
            float spreadVal = Mathf.Clamp01(Get<float>(player, t, "_spread", 0f));
            src.spread = Mathf.Lerp(180f, 0f, spreadVal);
            var curve = Get<AnimationCurve>(player, t, "_rolloffCurve", null);
            if (curve != null && curve.length > 0)
            {
                src.rolloffMode = AudioRolloffMode.Custom;
                src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
            }
            src.volume = Mathf.Clamp01(Get<float>(player, t, "_volume", 1f) * Plugin.SoundRigVolume.Value);
            return true;
        }

        // BSG's Play() is what knows how to pick from the bank, honor the start delay,
        // and re-arm on _randomTimeRange — hand playback to it. fallback: play one bank
        // clip straight off the source so a renamed method doesn't mean silence.
        private bool StartPlayer(Component player)
        {
            var t = player.GetType();
            try
            {
                var bank = Get<UnityEngine.Object>(player, t, "_soundBank", null)
                        ?? Get<UnityEngine.Object>(player, t, "_ambientBank", null);
                if (bank == null) return false;
                var play = AccessTools.Method(t, "Play");
                if (play != null && play.GetParameters().Length == 0)
                {
                    play.Invoke(player, null);
                    return true;
                }
                var src = player.GetComponent<AudioSource>();
                if (src == null) return false;
                var clips = BankClips(bank);
                if (clips == null || clips.Count == 0) return false;
                src.clip = clips[UnityEngine.Random.Range(0, clips.Count)];
                src.Play();
                Plugin.Log.LogWarning($"[SoundRig] {t.Name}.Play() not found — played one bank clip directly");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[SoundRig] Play failed on {t.Name}: {e.Message}");
                return false;
            }
        }

        private static List<AudioClip> BankClips(UnityEngine.Object bank)
        {
            try
            {
                var envs = AccessTools.Field(bank.GetType(), "Environments").GetValue(bank) as Array;
                if (envs == null || envs.Length == 0) return null;
                var variety = envs.GetValue(0);
                var dists = AccessTools.Field(variety.GetType(), "Clips").GetValue(variety) as Array;
                if (dists == null || dists.Length == 0) return null;
                var clips = AccessTools.Field(dists.GetValue(0).GetType(), "Clips").GetValue(dists.GetValue(0)) as AudioClip[];
                return clips != null ? new List<AudioClip>(clips) : null;
            }
            catch { return null; }
        }

        private void OnDestroy()
        {
            foreach (var s in _alarmSrcs) if (s != null && s.isPlaying) s.Stop();
            _firefight?.StopAll();
            if (_instance == this) _instance = null;
        }

        // ---- restore plumbing (icebreaker ports) ----------------------------------

        private static bool IsPlayerRecord(JToken rec)
        {
            var cls = (rec as JObject)?.Value<string>("cls");
            return cls != null && cls.EndsWith("AmbientSoundPlayer", StringComparison.Ordinal);
        }

        private static bool IsPlayer(Component c)
        {
            if (c == null) return false;
            for (var t = c.GetType(); t != null; t = t.BaseType)
                if (t.Name == "BaseAmbientSoundPlayer") return true;
            return false;
        }

        private void IndexHierarchy(Transform root)
        {
            _byPath = new Dictionary<string, List<Transform>>(512);
            var sc = root.gameObject.scene;
            if (sc.IsValid() && sc.isLoaded)
                foreach (var go in sc.GetRootGameObjects()) WalkTf(go.transform, go.name);
            else
                WalkTf(root, root.name);
        }

        private void WalkTf(Transform t, string path)
        {
            if (!_byPath.TryGetValue(path, out var list)) _byPath[path] = list = new List<Transform>(1);
            list.Add(t);
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                WalkTf(c, path + "/" + c.name);
            }
        }

        private Transform TakeTransform(string path, Type type)
        {
            if (string.IsNullOrEmpty(path) || !_byPath.TryGetValue(path, out var list)) return null;
            foreach (var t in list)
                if (t != null && t.gameObject.GetComponent(type) == null) return t;
            return list.Count > 0 ? list[0] : null;
        }

        private static Type ResolveType(string name)
        {
            if (name == null) return null;
            if (_types.TryGetValue(name, out var t)) return t;
            t = AccessTools.TypeByName(name);
            _types[name] = t;
            return t;
        }

        private readonly HashSet<string> _missingClips = new HashSet<string>();

        private static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public |
                                         BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private static T Get<T>(object target, Type type, string field, T fallback)
        {
            var f = FindField(type, field);
            if (f == null) return fallback;
            var v = f.GetValue(target);
            return v is T typed ? typed : fallback;
        }

        private bool SetField(object target, string name, JToken value)
        {
            var f = FindField(target.GetType(), name);
            if (f == null) return false;
            var converted = ConvertTok(value, f.FieldType);
            if (converted == null && f.FieldType.IsValueType) return false;
            f.SetValue(target, converted);
            return true;
        }

        private object ConvertTok(JToken tok, Type type)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;
            if (type == typeof(float)) return tok.Value<float>();
            if (type == typeof(double)) return tok.Value<double>();
            if (type == typeof(int)) return tok.Value<int>();
            if (type == typeof(long)) return tok.Value<long>();
            if (type == typeof(byte)) return (byte)tok.Value<int>();
            if (type == typeof(bool)) return tok.Type == JTokenType.Boolean ? tok.Value<bool>() : tok.Value<int>() != 0;
            if (type == typeof(string)) return tok.Value<string>();
            if (type.IsEnum) return Enum.ToObject(type, tok.Value<int>());

            var obj = tok as JObject;
            if (type == typeof(Vector2) && obj != null) return new Vector2(F(obj, "x"), F(obj, "y"));
            if (type == typeof(Vector3) && obj != null) return new Vector3(F(obj, "x"), F(obj, "y"), F(obj, "z"));
            if (type == typeof(Vector4) && obj != null) return new Vector4(F(obj, "x"), F(obj, "y"), F(obj, "z"), F(obj, "w"));
            if (type == typeof(Quaternion) && obj != null) return new Quaternion(F(obj, "x"), F(obj, "y"), F(obj, "z"), F(obj, "w"));
            if (type == typeof(AnimationCurve) && obj != null) return BuildCurve(obj);
            if (typeof(UnityEngine.Object).IsAssignableFrom(type) && obj != null) return ResolveReference(obj, type);

            if (tok is JArray arr)
            {
                if (type.IsArray)
                {
                    var elem = type.GetElementType();
                    var a = Array.CreateInstance(elem, arr.Count);
                    for (int i = 0; i < arr.Count; i++) a.SetValue(ConvertTok(arr[i], elem), i);
                    return a;
                }
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var elem = type.GetGenericArguments()[0];
                    var list = (System.Collections.IList)Activator.CreateInstance(type);
                    foreach (var item in arr) list.Add(ConvertTok(item, elem));
                    return list;
                }
                return null;
            }

            // nested serializable struct/class (the spline emitter configs) — recurse
            if (obj != null && !type.IsPrimitive)
            {
                object inst;
                try { inst = Activator.CreateInstance(type); }
                catch { return null; }
                foreach (var p in obj)
                {
                    try { SetField(inst, p.Key, p.Value); } catch { }
                }
                return inst;
            }
            return null;
        }

        private static float F(JObject o, string k)
        {
            var t = o[k];
            return t == null || t.Type == JTokenType.Null ? 0f : t.Value<float>();
        }

        private static AnimationCurve BuildCurve(JObject obj)
        {
            var keysTok = obj["m_Curve"] as JArray;
            if (keysTok == null) return new AnimationCurve();
            var keys = new Keyframe[keysTok.Count];
            for (int i = 0; i < keysTok.Count; i++)
            {
                var k = (JObject)keysTok[i];
                var kf = new Keyframe(F(k, "time"), F(k, "value"), F(k, "inSlope"), F(k, "outSlope"));
                kf.weightedMode = (WeightedMode)(k["weightedMode"]?.Value<int>() ?? 0);
                kf.inWeight = F(k, "inWeight");
                kf.outWeight = F(k, "outWeight");
                keys[i] = kf;
            }
            var curve = new AnimationCurve(keys);
            // NEVER restore wrap modes on a rolloff — serialized 2 is WrapMode.Loop and
            // a looping falloff makes every distant sound audible map-wide (icebreaker)
            curve.preWrapMode = WrapMode.ClampForever;
            curve.postWrapMode = WrapMode.ClampForever;
            return curve;
        }

        private UnityEngine.Object ResolveReference(JObject obj, Type wanted)
        {
            var clip = obj["$AudioClip"]?.Value<string>();
            if (clip != null)
            {
                if (_clips.TryGetValue(clip, out var c)) return c;
                _missingClips.Add(clip);
                return null;
            }
            var bank = obj["$asset:SoundBank"]?.Value<string>();
            if (bank != null)
                return _banks.TryGetValue(bank, out var b) && wanted.IsInstanceOfType(b) ? b : null;
            var refPath = obj["$ref"]?.Value<string>();
            if (refPath != null)
            {
                if (!_byPath.TryGetValue(refPath, out var list)) return null;
                foreach (var t in list)
                {
                    var c = t.GetComponent(wanted);
                    if (c != null) return c;
                }
                return null;
            }
            return null;
        }

        internal static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        // ---- firefight simulation (director classes are 1.0-only) -----------------
        // distant volleys per FirefightAreaAudioPreset: density curve, cooldowns,
        // call-and-response, pooled roaming sources on the rig's own mixer group
        private class FirefightRunner
        {
            private const int PoolSize = 6;

            private class Area
            {
                public Transform tf;
                public float radius, localDensity, aperture;
                public bool useSector;
                public Vector2 fwdOffset;
                public float nextAt;
                public float responseAt = -1f;
                public Vector3 lastShotPos;
                public int responseShots;
            }

            private readonly List<Area> _areas = new List<Area>();
            private readonly List<AudioSource> _pool = new List<AudioSource>();
            private readonly List<AudioClip> _shots = new List<AudioClip>();
            private readonly List<AudioClip> _booms = new List<AudioClip>();
            private int _poolIdx;
            private readonly float _period, _baseDensity, _cdZero, _cdMax, _jitMin, _jitMax;
            private readonly float _respProb, _respDelayMin, _respDelayMax, _grenadePerMin;
            private readonly AnimationCurve _density;

            public FirefightRunner(TerminalSoundRig host, JObject ff,
                Dictionary<string, AudioClip> clips, Dictionary<string, List<Transform>> byPath)
            {
                void Fill(string key, List<AudioClip> into)
                {
                    foreach (var n in ff[key] as JArray ?? new JArray())
                        if (clips.TryGetValue(n.Value<string>() ?? "", out var c) && c != null) into.Add(c);
                }
                Fill("shotClips", _shots);
                Fill("debrisClips", _booms);
                Fill("explosionClips", _booms);

                var pr = ff["preset"] as JObject ?? new JObject();
                _period = pr.Value<float?>("DensityPeriodSec") ?? 10f;
                _baseDensity = pr.Value<float?>("BaseDensity") ?? 0.5f;
                _cdZero = pr.Value<float?>("BaseCooldownAtZeroDensitySec") ?? 1f;
                _cdMax = pr.Value<float?>("BaseCooldownAtMaxDensitySec") ?? 0.1f;
                _respProb = pr.Value<float?>("ResponseProbability") ?? 0.65f;
                _grenadePerMin = pr.Value<float?>("GrenadeEventsPerShooterPerMinuteAtMaxDensity") ?? 0.15f;
                _jitMin = pr["CooldownJitterRangeSec"]?.Value<float?>("x") ?? 0f;
                _jitMax = pr["CooldownJitterRangeSec"]?.Value<float?>("y") ?? 0.2f;
                _respDelayMin = pr["ResponseDelayRange"]?.Value<float?>("x") ?? 1f;
                _respDelayMax = pr["ResponseDelayRange"]?.Value<float?>("y") ?? 3f;
                _density = pr["DensityCurve"] is JObject dc ? BuildCurve(dc) : AnimationCurve.Constant(0f, 1f, 1f);

                foreach (var aTok in ff["areas"] as JArray ?? new JArray())
                {
                    var a = (JObject)aTok;
                    var path = a.Value<string>("path");
                    if (path == null || !byPath.TryGetValue(path, out var list) || list.Count == 0) continue;
                    _areas.Add(new Area
                    {
                        tf = list[0],
                        radius = a.Value<float?>("radius") ?? 450f,
                        localDensity = a.Value<float?>("localDensity") ?? 1f,
                        useSector = (a.Value<int?>("useSector") ?? 0) != 0,
                        aperture = a.Value<float?>("sectorApertureDeg") ?? 360f,
                        fwdOffset = new Vector2(
                            (a["forwardOffsetPercentRange"] as JArray)?[0]?.Value<float>() ?? 0f,
                            (a["forwardOffsetPercentRange"] as JArray)?[1]?.Value<float>() ?? 0.8f),
                        nextAt = Time.realtimeSinceStartup + UnityEngine.Random.Range(2f, 10f),
                    });
                }

                AudioMixerGroup mixer = TerminalAudioRouting.AmbientEffects();
                foreach (var kv in byPath)
                    if (kv.Key.EndsWith("/ShootPlayer") && kv.Value.Count > 0)
                    {
                        var s = kv.Value[0].GetComponent<AudioSource>();
                        if (mixer == null && s != null && s.outputAudioMixerGroup != null) { mixer = s.outputAudioMixerGroup; break; }
                    }
                var root = new GameObject("Terminal_SoundRig_FirefightPool");
                root.transform.SetParent(host.transform, false);
                for (int i = 0; i < PoolSize; i++)
                {
                    var go = new GameObject($"ff_src_{i}");
                    go.transform.SetParent(root.transform, false);
                    var src = go.AddComponent<AudioSource>();
                    src.playOnAwake = false;
                    src.spatialBlend = 1f;
                    src.minDistance = 20f;
                    src.maxDistance = 500f;
                    src.rolloffMode = AudioRolloffMode.Linear;
                    if (mixer != null) src.outputAudioMixerGroup = mixer;
                    _pool.Add(src);
                }
            }

            public void Tick(float now, bool active)
            {
                if (!active || _shots.Count == 0) return;
                foreach (var a in _areas)
                {
                    float density = Mathf.Clamp01(_baseDensity
                        * _density.Evaluate(Mathf.PingPong(now / _period, 1f))
                        * a.localDensity);

                    if (now >= a.nextAt)
                    {
                        a.lastShotPos = ShooterPoint(a);
                        PlayAt(_shots[UnityEngine.Random.Range(0, _shots.Count)], a.lastShotPos, 0.6f);
                        a.nextAt = now + Mathf.Lerp(_cdZero, _cdMax, density) * UnityEngine.Random.Range(2f, 6f)
                                 + UnityEngine.Random.Range(_jitMin, _jitMax);
                        if (a.responseAt < 0f && UnityEngine.Random.value < _respProb)
                        {
                            a.responseAt = now + UnityEngine.Random.Range(_respDelayMin, _respDelayMax);
                            a.responseShots = UnityEngine.Random.Range(1, 6);
                        }
                        if (_booms.Count > 0 && UnityEngine.Random.value < _grenadePerMin * density / 60f * 5f)
                            PlayAt(_booms[UnityEngine.Random.Range(0, _booms.Count)], ShooterPoint(a), 0.7f);
                    }

                    if (a.responseAt >= 0f && now >= a.responseAt)
                    {
                        var pos = a.lastShotPos + new Vector3(UnityEngine.Random.Range(-40f, 40f), 0f, UnityEngine.Random.Range(-40f, 40f));
                        PlayAt(_shots[UnityEngine.Random.Range(0, _shots.Count)], pos, 0.55f);
                        if (--a.responseShots > 0)
                            a.responseAt = now + UnityEngine.Random.Range(0.15f, 0.6f);
                        else
                            a.responseAt = -1f;
                    }
                }
            }

            private Vector3 ShooterPoint(Area a)
            {
                float half = a.useSector ? a.aperture * 0.5f : 180f;
                float ang = UnityEngine.Random.Range(-half, half);
                var dir = Quaternion.AngleAxis(ang, Vector3.up) * a.tf.forward;
                float pct = UnityEngine.Random.Range(a.fwdOffset.x, a.fwdOffset.y);
                return a.tf.position + dir * (a.radius * pct);
            }

            private void PlayAt(AudioClip clip, Vector3 pos, float vol)
            {
                var src = _pool[_poolIdx = (_poolIdx + 1) % _pool.Count];
                if (src == null) return;
                src.transform.position = pos;
                src.pitch = UnityEngine.Random.Range(0.92f, 1.08f);
                src.volume = vol * Plugin.SoundRigVolume.Value;
                src.clip = clip;
                src.Play();
            }

            public void StopAll()
            {
                foreach (var s in _pool) if (s != null && s.isPlaying) s.Stop();
            }
        }
    }
}
