using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Manimal.Terminal
{
    // CAMERA DONOR — 1:1 port of icebreaker's IcebreakerCameraDonor, lessons intact.
    // steal a real 0.16.9 map's camera at runtime instead of shipping one.
    //
    // the bundle route died twice on icebreaker, for reasons measured: bundle binding
    // needs stubs and exact compiled layouts, and the rip's serialized DATA is dead on
    // arrival — shaders/materials null, curves empty — so shipped components crashed
    // Awake/Start, and the un-shippable SSAA broke EFT.CameraControl.CameraManager.SetSSR inside
    // PlayerCameraController.Create: error screen, no spawn.
    //
    // instead: run any VANILLA raid once with DevMode on — the dumper walks the real
    // player camera (live object, every ref VALID) and writes camera_donor.json next
    // to the dll. Cam2 stays the chassis (valid core data, boots reliably) and the
    // graft ADDS the donor's missing components at runtime — AddComponent of the
    // game's own classes needs no bundle binding, which dissolves the SSAA problem —
    // then fills fields from the dump, resolving asset refs BY NAME against the
    // game's loaded assets. icebreaker's donor json is reusable here as-is (it was
    // dumped from a vanilla map on this same client build).
    internal static class TerminalCameraDonor
    {
        private static string DonorPath => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
            "plugin-data", "camera", "camera_donor.json");

        // never dump, never graft: core objects the chassis already owns, and transforms
        private static readonly HashSet<string> SkipTypes = new HashSet<string>
        {
            "Transform", "Camera", "AudioListener", "FlareLayer",
        };

        // ALLOWLIST — the graft model flipped after icebreaker's fresh-install test:
        // "refs happened to resolve" is a name-resolution lottery (three separate
        // strikes: RainScreenDrops' cross-raid pool, ScreenWater's rip assets,
        // UltimateBloom's name-matching texture from an unrelated mod pack blitting
        // garbage). ONLY components proven stable across installs graft at all —
        // these four resolve exclusively against game-global assets (Shader.Find)
        // and rendered clean on every machine tested. additions require proving.
        private static readonly HashSet<string> AllowGraft = new HashSet<string>
        {
            "DesaturateEffect", "Antialiasing", "Tonemapping", "PerfectCullingCamera",
        };

        // POISON — never grafted, each one measured (black screen with HUD burn-in,
        // zero exceptions — an OnRenderImage owner with null refs blits nothing
        // SILENTLY):
        //   MBOIT_Scattering                 needs WindowsManager; dead on backported maps
        //   PerfectCullingCrossSceneSampler  Start() NREs (Koenigz.PerfectCulling.EFT.CullingGridVisibilitySampler)
        //   StreamingController              factory's streaming manager, not an effect
        //   ContactShadows                   NoiseTextureSet ScriptableObject can't ride the dump
        //   ScreenWater / InfectionEffect    resolving a ripped asset by name is not the
        //                                    same as the asset WORKING — both went black
        //   RainScreenDrops                  the TRANSIT special: self-withhold passes
        //                                    against the ORIGIN raid's still-loaded rain
        //                                    assets, then they unload and it blits black
        //                                    off dead native objects
        private static readonly HashSet<string> NeverGraft = new HashSet<string>
        {
            "MBOIT_Scattering", "ContactShadows",
            "PerfectCullingCrossSceneSampler", "StreamingController",
            "ScreenWater", "InfectionEffect",
            "RainScreenDrops",
        };

        // ------------------------------------------------------------------ dump side

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_DumpDonorCamera
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                try
                {
                    // late graft: on a menu load SetCamera ran too early (bare camera) and
                    // deferred — by OnGameStarted the stack is built, so try once more
                    if (TerminalGate.On)
                    {
                        var live = EFT.CameraControl.CameraManager.Instance?.Camera;
                        if (live != null) TryGraft(live.gameObject, "OnGameStarted");
                    }
                    // vanilla maps only — dumping our own grafted camera would feed the
                    // graft its own output next raid
                    if (TerminalGate.On || !Plugin.DevMode.Value) return;
                    if (System.IO.File.Exists(DonorPath)) return; // one blessed donor, dump once
                    var cc = EFT.CameraControl.CameraManager.Instance;
                    var cam = cc != null ? cc.Camera : null;
                    if (cam == null) { Plugin.Log.LogWarning("[CamDonor] no EFT.CameraControl.CameraManager.Camera to dump"); return; }
                    Dump(cam.gameObject);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[CamDonor] dump failed: {e.Message}"); }
            }
        }

        private static void Dump(GameObject go)
        {
            var comps = new JArray();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var t = c.GetType();
                if (SkipTypes.Contains(t.Name)) continue;
                var fields = new JObject();
                int skipped = 0;
                foreach (var f in SerializedFields(t))
                {
                    try
                    {
                        var tok = Encode(f.GetValue(c));
                        if (tok != null) fields[f.Name] = tok; else skipped++;
                    }
                    catch { skipped++; }
                }
                comps.Add(new JObject
                {
                    ["type"] = t.FullName,
                    ["enabled"] = !(c is Behaviour b) || b.enabled,
                    ["fields"] = fields,
                    ["unencodable"] = skipped,
                });
            }
            var root = new JObject
            {
                ["map"] = Singleton<GameWorld>.Instance?.LocationId ?? "?",
                ["go"] = go.name,
                ["components"] = comps,
            };
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DonorPath));
            System.IO.File.WriteAllText(DonorPath, root.ToString());
            Plugin.Log.LogWarning($"[CamDonor] DUMPED '{go.name}' ({comps.Count} components) from "
                + $"'{root["map"]}' -> {DonorPath} — this is now the terminal camera donor");
        }

        // ------------------------------------------------------------------ graft side

        [HarmonyPatch(typeof(EFT.CameraControl.CameraManager), "SetCamera", typeof(Camera))]
        internal static class Patch_GraftDonorCamera
        {
            [HarmonyPrefix]
            private static void Prefix(Camera camera)
            {
                if (!TerminalGate.On || camera == null) return;
                TryGraft(camera.gameObject, "SetCamera");
            }
        }

        // the camera GO we already grafted — per-GO, because EFT.CameraControl.CameraManager persists across
        // raids but its camera object does not
        private static GameObject _graftedGo;

        // the UltimateBloom we grafted FOR HollywoodGraphics — donor-serialized and
        // HG-configured, not a raw corpse
        internal static Behaviour ProvisionedBloom;

        // BARE-CAMERA TRAP: SetCamera fires at two very different moments. transiting
        // in, the camera is FULLY BUILT and our effects graft onto the end of a live
        // render chain. loading straight from the MENU it fires on a BARE camera — the
        // graft's OnRenderImage owners end up FIRST in the chain and the first (black)
        // fade frame never gets overwritten. so: only graft a camera the game has
        // finished furnishing (EffectsController is that marker), and retry from
        // OnGameStarted, by which point it always is. if it never furnishes, we simply
        // dont graft — plain Cam2 renders, better absent than hollow.
        internal static void TryGraft(GameObject go, string site)
        {
            if (go == null || _graftedGo == go) return;
            if (go.GetComponent<EffectsController>() == null)
            {
                Plugin.Log.LogDebug($"[CamDonor] {site}: camera not furnished yet (no EffectsController) — deferring graft");
                return;
            }
            try
            {
                if (!System.IO.File.Exists(DonorPath))
                {
                    Plugin.Log.LogWarning("[CamDonor] no plugin-data/camera/camera_donor.json — run one vanilla raid "
                        + "(any map) with DevMode on to record a donor. staying on plain Cam2.");
                    return;
                }
                Graft(go, JObject.Parse(System.IO.File.ReadAllText(DonorPath)));
                _graftedGo = go;
            }
            catch (Exception e) { Plugin.Log.LogError($"[CamDonor] graft failed — plain Cam2 stands: {e}"); }
        }

        private static void Graft(GameObject go, JObject donor)
        {
            var comps = donor["components"] as JArray;
            if (comps == null) return;

            // ADD-ONLY, hard-learned on icebreaker's first grafted boot: writing the
            // donor's fields over the CHASSIS components produced a black screen with
            // UI burn-in and ZERO exceptions — on these obfuscated classes many public
            // fields are live runtime wiring, not tuning. Cam2's own components already
            // work; the graft's whole job is only what Cam2 LACKS.
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in (Plugin.CamDonorSkip?.Value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                skip.Add(name.Trim());

            int filled = 0, missingType = 0, refMiss = 0, existing = 0, skipped = 0;
            var addedNames = new List<string>();
            var refMissDetail = new List<string>();
            var restoreEnabled = new List<(Behaviour, bool)>();

            // inactive while we add+populate: AddComponent fires Awake IMMEDIATELY on an
            // active GO, and a donor component awakening on default fields is exactly
            // the NightVision empty-curve crash from the bundle attempt
            bool wasActive = go.activeSelf;
            go.SetActive(false);
            try
            {
                foreach (var token in comps)
                {
                    if (token is not JObject row) continue;
                    var typeName = (string)row["type"];
                    var type = AccessTools.TypeByName(typeName);
                    if (type == null || !typeof(Component).IsAssignableFrom(type)) { missingType++; continue; }
                    if (SkipTypes.Contains(type.Name)) continue;
                    // HG PROVISION: HollywoodGraphics' Bloom wrapper AddComponents a raw
                    // UltimateBloom when the camera has none — a raw one has NULL
                    // serialized arrays so its ctor NREs, GraphicsController.Start dies,
                    // and the half-built bloom eats the frame. HFX-without-HG variant:
                    // ConcussionController does camera.GetComponent<UltimateBloom>() with
                    // NO null check — permanent battle blur on a camera without one.
                    // retail cameras all ship an UltimateBloom; our Cam2 fallback must
                    // provide one. parked-but-present satisfies both.
                    bool hgBloom = type.Name == "UltimateBloom"
                        && (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.janky.hollywoodgraphics")
                         || BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.janky.hollywoodfx"));
                    if (!AllowGraft.Contains(type.Name) && !hgBloom) { skipped++; continue; } // allowlist: proven-stable only
                    if (skip.Contains(type.Name)) { skipped++; continue; }        // bisect knob
                    if (go.GetComponent(type) != null) { existing++; continue; }  // chassis owns it

                    var c = go.AddComponent(type);
                    if (c == null) { missingType++; continue; }

                    int compRefMiss = 0;
                    var compMissed = new List<string>();
                    var fields = row["fields"] as JObject;
                    if (fields != null)
                        foreach (var kv in fields)
                        {
                            // the dust texture never resolves here and HG supplies its own
                            if (hgBloom && kv.Key == "m_DustTexture") continue;
                            var f = FindSerializedField(type, kv.Key);
                            if (f == null) continue;
                            try
                            {
                                int before = compRefMiss;
                                object v = Decode(kv.Value, f.FieldType, go, ref compRefMiss);
                                if (compRefMiss > before) compMissed.Add(kv.Key);
                                if (v != null || !f.FieldType.IsValueType) { f.SetValue(c, v); filled++; }
                            }
                            catch { }
                        }

                    // no dust texture until HG assigns one — lens dust off so nothing
                    // samples a null; HG's UpdateSettings sets it from its own config
                    if (hgBloom && compRefMiss == 0)
                    {
                        try
                        {
                            var lensDust = FindSerializedField(type, "m_UseLensDust");
                            if (lensDust != null) lensDust.SetValue(c, false);
                        }
                        catch { }
                        ProvisionedBloom = c as Behaviour;
                        Plugin.Log.LogWarning("[CamDonor] UltimateBloom provisioned for HollywoodGraphics (keeps its init alive)");
                    }

                    // SELF-WITHHOLD: a camera effect with an unresolved shader/material/
                    // texture is exactly the silent black-blit class. better absent than
                    // hollow — and the log names what's owed.
                    if (compRefMiss > 0)
                    {
                        refMiss += compRefMiss;
                        refMissDetail.Add($"{type.Name}({string.Join("+", compMissed)})");
                        UnityEngine.Object.DestroyImmediate(c);
                        continue;
                    }
                    addedNames.Add(type.Name);

                    if (c is Behaviour beh)
                    {
                        bool want = row["enabled"] == null || (bool)row["enabled"];
                        restoreEnabled.Add((beh, want));
                        beh.enabled = false; // OnEnable waits until everything is populated
                    }
                }
            }
            finally
            {
                go.SetActive(wasActive);
            }
            foreach (var (beh, want) in restoreEnabled)
                if (beh != null) beh.enabled = want;

            Plugin.Log.LogWarning($"[CamDonor] GRAFTED (add-only) onto '{go.name}': "
                + $"added [{string.Join(", ", addedNames)}] ({filled} fields), "
                + $"{existing} chassis component(s) left untouched, {skipped} skipped by config, "
                + $"{missingType} type(s) unresolved"
                + (refMissDetail.Count > 0 ? $"; WITHHELD (refs owed): {string.Join(", ", refMissDetail)}" : ""));
        }

        // ------------------------------------------------------------- (de)serialization

        private static FieldInfo FindSerializedField(Type type, string name)
        {
            foreach (var field in SerializedFields(type))
                if (field.Name == name) return field;
            return null;
        }

        private static IEnumerable<FieldInfo> SerializedFields(Type t)
        {
            for (var cur = t; cur != null && cur != typeof(MonoBehaviour) && cur != typeof(Behaviour) && cur != typeof(Component); cur = cur.BaseType)
                foreach (var f in cur.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (f.IsInitOnly || f.IsLiteral) continue;
                    bool serialized = f.IsPublic || f.GetCustomAttribute<SerializeField>() != null;
                    if (!serialized) continue;
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    yield return f;
                }
        }

        // json-encodes what a camera component plausibly carries; null = not encodable
        private static JToken Encode(object v)
        {
            switch (v)
            {
                case null: return JValue.CreateNull();
                case bool _: case string _: return new JValue(v);
                case int _: case long _: case short _: case byte _: case sbyte _:
                case uint _: case ushort _: case ulong _:
                case float _: case double _: return new JValue(v);
                case Enum e: return new JValue(Convert.ToInt64(e));
                case Vector2 v2: return new JObject { ["x"] = v2.x, ["y"] = v2.y };
                case Vector3 v3: return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
                case Vector4 v4: return new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
                case Color c: return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
                case LayerMask lm: return new JObject { ["mask"] = lm.value };
                case AnimationCurve ac:
                    return TerminalSerializedCurves.ToDonor(ac);
                case UnityEngine.Object uo:
                    // by NAME + type — the whole point: the ref is re-resolved against
                    // the game's own loaded assets on the destination map
                    return new JObject { ["ref"] = uo.GetType().Name, ["name"] = uo.name };
                default:
                    return null;
            }
        }

        private static object Decode(JToken tok, Type want, GameObject host, ref int refMiss)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;
            if (want.IsEnum) return Enum.ToObject(want, (long)tok);
            if (want == typeof(bool)) return (bool)tok;
            if (want == typeof(string)) return (string)tok;
            if (want == typeof(int)) return (int)tok;
            if (want == typeof(long)) return (long)tok;
            if (want == typeof(float)) return (float)tok;
            if (want == typeof(double)) return (double)tok;
            if (want == typeof(byte)) return (byte)tok;
            if (want == typeof(Vector2)) return new Vector2((float)tok["x"], (float)tok["y"]);
            if (want == typeof(Vector3)) return new Vector3((float)tok["x"], (float)tok["y"], (float)tok["z"]);
            if (want == typeof(Vector4)) return new Vector4((float)tok["x"], (float)tok["y"], (float)tok["z"], (float)tok["w"]);
            if (want == typeof(Color)) return new Color((float)tok["r"], (float)tok["g"], (float)tok["b"], (float)tok["a"]);
            if (want == typeof(LayerMask)) return (LayerMask)(int)tok["mask"];
            if (want == typeof(AnimationCurve))
            {
                return TerminalSerializedCurves.FromDonor(tok);
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(want) && tok["ref"] != null)
            {
                var name = (string)tok["name"];
                if (string.IsNullOrEmpty(name)) return null;
                // sibling components first (PrismEffects-style same-camera refs), then
                // shaders by Find, then anything the game has loaded, by exact name
                if (typeof(Component).IsAssignableFrom(want))
                {
                    var sib = host.GetComponent(want);
                    if (sib != null) return sib;
                }
                if (want == typeof(Shader))
                {
                    var sh = Shader.Find(name);
                    if (sh != null) return sh;
                }
                var found = Array.Find(Resources.FindObjectsOfTypeAll(want), o => o.name == name);
                if (found == null) { refMiss++; return null; }
                // carried materials come from the rip, whose shader is decompiled
                // garbage — swap in the game's own same-name shader (the RebindShaders
                // lesson)
                if (found is Material mat && mat.shader != null)
                {
                    try
                    {
                        var live = Shader.Find(mat.shader.name);
                        if (live != null && live != mat.shader) mat.shader = live;
                    }
                    catch { }
                }
                return found;
            }
            return null;
        }
    }
}
