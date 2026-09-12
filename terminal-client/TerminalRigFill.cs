using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // shared machinery for resurrecting retail trigger-network rigs from sidecars
    // (gates_explosion, crane_falling): find the rig root in the Terminal scenes,
    // add-or-get native handler components per sidecar row, reflective-fill their
    // fields from the authored json ($ref for local refs, $ext-with-name for
    // audio clips/mixer groups, AnimationCurve rebuild, nested structs/lists)
    internal static class TerminalRigFill
    {
        internal static Transform FindRootNamed(string name)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scn = SceneManager.GetSceneAt(i);
                if (!scn.isLoaded || scn.name == null || !scn.name.StartsWith("Terminal")) continue;
                foreach (var r in scn.GetRootGameObjects())
                {
                    var hit = FindChildNamed(r.transform, name);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        internal static Transform FindChildNamed(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var hit = FindChildNamed(root.GetChild(i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        internal static void IndexTree(Transform t, string path, Dictionary<string, Transform> index)
        {
            index[path] = t;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                IndexTree(c, path + "/" + c.name, index);
            }
        }

        internal static Type TypeFor(string cls)
        {
            return AccessTools.TypeByName("EFT.GameTriggers." + cls)
                ?? AccessTools.TypeByName("CommonAssets.Scripts.Game.GameTriggers.Handlers." + cls)
                ?? AccessTools.TypeByName(cls);
        }

        // the sidecar row loop shared by every rig: add-or-get + fill + enable.
        // returns filled component count; rows whose class is not in nativeSet are
        // skipped (sync shims, 1.0-only classes the caller replaces or drops)
        internal static int StageRows(JArray rows, HashSet<string> nativeSet,
            Dictionary<string, Transform> index, string tag, Action<Component, JObject> postFill = null)
        {
            int added = 0, filled = 0;
            foreach (var rowTok in rows ?? new JArray())
            {
                var row = rowTok as JObject;
                if (row == null) continue;
                var cls = row.Value<string>("cls") ?? "";
                if (!nativeSet.Contains(cls)) continue;
                var rp = row.Value<string>("path") ?? "";
                if (!index.TryGetValue(rp, out var t)) { Plugin.Log.LogWarning($"[{tag}] '{rp}' not in scene — {cls} skipped"); continue; }
                var type = TypeFor(cls);
                if (type == null) { Plugin.Log.LogWarning($"[{tag}] type '{cls}' missing from 4.0 — skipped"); continue; }

                // retail authored these GOs active — the rip may disagree, and an
                // inactive handler never Starts (never subscribes)
                if ((row.Value<bool?>("active") ?? true) && !t.gameObject.activeSelf)
                    t.gameObject.SetActive(true);

                int cocc = row.Value<int?>("cocc") ?? 0;
                var comps = t.gameObject.GetComponents(type);
                Component comp = cocc < comps.Length ? comps[cocc] : null;
                if (!comp) { comp = t.gameObject.AddComponent(type); added++; }
                if (!comp) continue;
                if (row["fields"] is JObject f) FillObject(comp, f, index);
                var b = comp as Behaviour;
                if (b) b.enabled = true;
                if (postFill != null) postFill(comp, row);
                filled++;
            }
            Plugin.Log.LogInfo($"[{tag}] {filled} native handler(s) live ({added} added)");
            return filled;
        }

        internal static void FillObject(object target, JObject fields, Dictionary<string, Transform> index)
        {
            var type = target.GetType();
            foreach (var prop in fields.Properties())
            {
                if (prop.Name == "m_Enabled") continue;
                var fi = AccessTools.Field(type, prop.Name);
                if (fi == null) continue;
                try
                {
                    var v = Convert(prop.Value, fi.FieldType, index);
                    if (v != null || !fi.FieldType.IsValueType) fi.SetValue(target, v);
                }
                catch (Exception e) { Plugin.Log.LogDebug($"[RigFill] {type.Name}.{prop.Name} failed: {e.Message}"); }
            }
        }

        internal static object Convert(JToken tok, Type want, Dictionary<string, Transform> index)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;
            if (tok is JObject o && o["$ref"] != null)
                return ResolveRef(o["$ref"], want, index);
            if (tok is JObject oe && oe["$ext"] != null)
                return ResolveExt(oe["$ext"], want);

            if (want == typeof(string)) return tok.Type == JTokenType.String ? tok.Value<string>() : tok.ToString();
            if (want == typeof(float)) return tok.Value<float>();
            if (want == typeof(int)) return tok.Value<int>();
            if (want == typeof(long)) return tok.Value<long>();
            if (want == typeof(bool)) return tok.Type == JTokenType.Boolean ? tok.Value<bool>() : tok.Value<int>() != 0;
            if (want.IsEnum) return Enum.ToObject(want, tok.Value<long>());
            if (want == typeof(Vector3) && tok is JObject v3)
                return new Vector3(v3.Value<float>("x"), v3.Value<float>("y"), v3.Value<float>("z"));
            if (want == typeof(Vector2) && tok is JObject v2)
                return new Vector2(v2.Value<float>("x"), v2.Value<float>("y"));
            if (want == typeof(Color) && tok is JObject c)
                return new Color(c.Value<float>("r"), c.Value<float>("g"), c.Value<float>("b"), c.Value<float>("a"));
            if (want == typeof(AnimationCurve) && tok is JObject cv)
                return BuildCurve(cv);

            // List<T>
            if (tok is JArray arr && want.IsGenericType && want.GetGenericTypeDefinition() == typeof(List<>))
            {
                var el = want.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(want);
                foreach (var item in arr) list.Add(Convert(item, el, index));
                return list;
            }
            // T[]
            if (tok is JArray arr2 && want.IsArray)
            {
                var el = want.GetElementType();
                var a = Array.CreateInstance(el, arr2.Count);
                for (int i = 0; i < arr2.Count; i++) a.SetValue(Convert(arr2[i], el, index), i);
                return a;
            }
            // nested plain class/struct
            if (tok is JObject nested && !typeof(UnityEngine.Object).IsAssignableFrom(want))
            {
                var inst = Activator.CreateInstance(want);
                FillObject(inst, nested, index);
                return inst;
            }
            return null;
        }

        private static AnimationCurve BuildCurve(JObject cv)
        {
            var curve = new AnimationCurve();
            if (cv["m_Curve"] is JArray keys)
            {
                foreach (var kt in keys)
                {
                    var k = kt as JObject;
                    if (k == null) continue;
                    var kf = new Keyframe(k.Value<float>("time"), k.Value<float>("value"),
                        k.Value<float>("inSlope"), k.Value<float>("outSlope"),
                        k.Value<float>("inWeight"), k.Value<float>("outWeight"))
                    { weightedMode = (WeightedMode)(k.Value<int?>("weightedMode") ?? 0) };
                    curve.AddKey(kf);
                }
            }
            curve.preWrapMode = WrapMode.ClampForever;
            curve.postWrapMode = WrapMode.ClampForever;
            return curve;
        }

        private static object ResolveExt(JToken ext, Type want)
        {
            var name = (ext as JObject)?.Value<string>("name");
            if (string.IsNullOrEmpty(name)) return null;
            if (want == typeof(AudioClip)) return TerminalFxBundle.FindClip(name);
            if (want.Name == "AudioMixerGroup")
            {
                foreach (var g in Resources.FindObjectsOfTypeAll(want))
                    if (g && g.name == name) return g;
                Plugin.Log.LogDebug($"[RigFill] mixer group '{name}' not found — sound keeps default routing");
            }
            return null;
        }

        // -------------------------------------------------------------- delay shims
        // SPT 4.0's HandlerDelay is GUTTED: the live coroutine is `yield return
        // new WaitForSeconds(_secondsDelay);` and NOTHING ELSE — the output emit
        // was stripped from the build (proved 2026-08-18: handlers subscribed,
        // received, waited, and the whole graph died there). so authored delays
        // run through OUR shim instead: subscribe input -> wait -> emit output.
        internal static void StageDelayShims(JArray rows, string tag)
        {
            int n = 0;
            var host = new GameObject($"Terminal_DelayShims_{tag}").AddComponent<DelayShimHost>();
            foreach (var rowTok in rows ?? new JArray())
            {
                var row = rowTok as JObject;
                if (row == null || row.Value<string>("cls") != "HandlerDelay") continue;
                var f = row["fields"] as JObject;
                if (f == null) continue;
                var input = f.Value<string>("_inputTrigger");
                var output = f.Value<string>("_outputTrigger");
                float secs = f.Value<float?>("_secondsDelay") ?? 0f;
                if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) continue;
                try
                {
                    var gw = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
                    gw.TriggersEmitter.Subscribe(input, new Action(() => host.Run(input, output, secs, tag)));
                    n++;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[{tag}] delay shim '{input}' subscribe failed: {e.Message}"); }
            }
            if (n > 0) Plugin.Log.LogInfo($"[{tag}] {n} delay shim(s) armed (native HandlerDelay is gutted in this build)");
            else UnityEngine.Object.Destroy(host.gameObject);
        }

        internal class DelayShimHost : MonoBehaviour
        {
            public void Run(string input, string output, float secs, string tag)
            {
                // In coop the authority emits each delayed transition once. Clients
                // receive those transitions; replay never starts another timer.
                if (TerminalCoop.Active && (!TerminalCoop.Authority || TerminalCoop.Replaying)) return;
                StartCoroutine(Fire(input, output, secs, tag));
            }

            private System.Collections.IEnumerator Fire(string input, string output, float secs, string tag)
            {
                yield return new WaitForSeconds(secs);
                Plugin.Log.LogInfo($"[{tag}] delay shim: '{input}' + {secs:0.##}s -> '{output}'");
                TerminalGatesExplosion.Emit(output);
            }
        }

        // ---------------------------------------------------- trigger diagnostics
        // mirror of EFT's GetStableHashCode — the trigger dictionary's key
        internal static int StableHash(string s)
        {
            int n = 23;
            foreach (var c in s) n = n * 31 + c;
            return n;
        }

        // logs how many listeners each trigger id actually has in the live emitter —
        // the decisive test for 'did the resurrected handlers' Start() ever subscribe'
        internal static void DumpSubscribers(string tag, string[] ids)
        {
            try
            {
                var gw = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
                var em = gw != null ? gw.TriggersEmitter : null;
                if (em == null) { Plugin.Log.LogWarning($"[{tag}] no emitter to dump"); return; }
                var parts = new List<string>();
                foreach (var id in ids)
                {
                    em._actionsMap.TryGetValue(StableHash(id), out var list);
                    parts.Add($"{id}={list?.Count ?? 0}");
                }
                Plugin.Log.LogInfo($"[{tag}] SUBSCRIBERS: {string.Join(", ", parts)}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[{tag}] subscriber dump failed: {e.Message}"); }
        }

        // waits a few seconds (handler Starts run a frame after staging) then dumps
        internal static void ScheduleSubscriberDump(string tag, params string[] ids)
        {
            var go = new GameObject($"Terminal_TriggerDiag_{tag}");
            var d = go.AddComponent<TriggerDiagHost>();
            d.Tag = tag;
            d.Ids = ids;
        }

        internal class TriggerDiagHost : MonoBehaviour
        {
            public string Tag;
            public string[] Ids;
            private float _at;
            private void Start() => _at = Time.time + 3f;
            private void Update()
            {
                if (Time.time < _at) return;
                DumpSubscribers(Tag, Ids ?? new string[0]);
                Destroy(gameObject);
            }
        }

        internal static object ResolveRef(JToken r, Type want, Dictionary<string, Transform> index)
        {
            if (r == null || r.Type == JTokenType.Null) return null;
            var path = r.Value<string>("path");
            if (path == null || !index.TryGetValue(path, out var t)) return null;
            var kind = r.Value<string>("kind");
            if (kind == "go") return t.gameObject;
            if (kind == "transform") return t;
            var cls = r.Value<string>("cls");
            var ct = TypeFor(cls) ?? AccessTools.TypeByName("UnityEngine." + cls + ", UnityEngine.CoreModule");
            if (ct == null && cls == "Animator") ct = typeof(Animator);
            if (ct == null) return null;
            int cocc = r.Value<int?>("cocc") ?? 0;
            var comps = t.gameObject.GetComponents(ct);
            var comp = cocc < comps.Length ? comps[cocc] : (comps.Length > 0 ? comps[0] : null);
            return comp;
        }
    }
}
