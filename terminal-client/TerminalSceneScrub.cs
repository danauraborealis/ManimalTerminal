using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // kills the LIVE-BUT-BROKEN components the guid rebind resurrected. icebreaker
    // never had this problem: its rip left these as unbound husks and the strip pass
    // removed the shells. terminal's SDK import REPAIRED their script refs (the
    // icebreaker-era stubs match by name), so they ship as real components whose
    // ripped internals NRE — first raid: ~904 frames each of TOD_Sky/WeatherController/
    // EnvironmentManager/Rain/Ripple tick spam + 906 AmbientLight null-shader ctors.
    //
    // scrub at sceneLoaded (fires after scene Awakes — those NREs are one-shot and
    // unavoidable client-side; the per-frame spam is what this stops). the weather
    // stack gets resurrected properly by the weather-system port later; until then a
    // static sky + our ambient fill carry the look. scene-name gated, never
    // TerminalGate (transit-gate-blindness).
    internal static class TerminalSceneScrub
    {
        // class name -> why it dies (ripped refs). destroyed as COMPONENTS — their
        // GOs may carry meshes (the sky dome stays, its driver goes)
        private static readonly string[] BrokenTypes =
        {
            // TOD_Sky/TOD_Time/TOD_Components NO LONGER scrubbed: terminal ships the
            // full retail sky dome rig (Sun/Moon/Atmosphere children all survived) and
            // killing the driver left an undriven blown-white dome + no sun. their
            // Awake NREs come from rip-nulled serialized PARAMETER classes — healed +
            // re-initialized by HealSky below instead.
            "EFT.Weather.WeatherController",            // LateUpdate NRE; LocalGame's weather block
                                                        // is gated on Instance != null, so its absence
                                                        // skips cleanly. resurrection = the weather
                                                        // phase (drives TOD hour/clouds; static sky
                                                        // until then)
            "EFT.Weather.ToDController",
            "RainController",
            "RippleController",
            "EFT.EnvironmentEffect.EnvironmentManager", // ripped scene instance NREs in Update —
                                                        // a bare replacement is rebuilt at Player.Init
                                                        // (TerminalRaidFixes.EnsureEnvironmentManager)
            "AmbientLight",                             // Material ctor on a null ripped shader,
                                                        // our flat ambient fill replaces it

            // the AI data holders are NO LONGER scrubbed (2026-08-10): the AI bake port
            // landed — TerminalAIBake.TryFill fills the scene holders with the retail
            // bake in the AICoversData.RestoreData prefix, and TerminalRaidFixes'
            // null-collection heal (BotsController.Init prefix) covers whatever the
            // fill misses, so RestoreData runs clean on the REAL holders.
        };

        private static Type[] _resolved;

        // LEVEL-BORDER BLOCKERS (user-diagnosed 2026-08-18, UnityExplorer): the
        // 'BLOCKERS/Cube (N)' boundary volumes ship with default-material cube
        // RENDERERS the rip failed to strip — giant white boxes in raid. kill the
        // renderers, keep the colliders (blocking is their job). idempotent.
        // forceRenderingOff, NOT enabled=false: culling-managed courtyards
        // (PerfectCulling, 885 groups in Area_02) re-ENABLE their group renderers
        // on visibility flips, silently undoing the scrub — and at scrub time the
        // same system holds them disabled, hiding them from an enabled-only walk.
        private static int HideBlockers(Scene scene)
        {
            int blockers = 0, cubes = 0, sampleLayer = -1;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!r || r.forceRenderingOff) continue;
                    bool border = LayerMask.LayerToName(r.gameObject.layer) == "LevelBorder";
                    if (!border)
                        for (var up = r.transform; up != null; up = up.parent)
                            if (up.name == "BLOCKERS" || up.name == "Blockers") { border = true; break; }
                    // a Cube on unity's builtin Default-Material is rip damage, never
                    // content (user 2026-08-18: Area_02's cubes wear Default-Material,
                    // LevelBorders' wear an eft material — both are blockers)
                    if (!border && r.name.StartsWith("Cube"))
                    {
                        var m = r.sharedMaterial;
                        if (m && m.name.StartsWith("Default-Material")) border = true;
                    }
                    if (!border && r.name.StartsWith("Cube"))
                    {
                        cubes++;
                        if (sampleLayer < 0) sampleLayer = r.gameObject.layer;
                    }
                    if (!border) continue;
                    r.forceRenderingOff = true;
                    blockers++;
                }
            if (blockers > 0)
                Plugin.Log.LogInfo($"[Scrub] '{scene.name}': {blockers} level-border blocker renderer(s) hidden");
            else if (cubes > 0)
                // the blockers matched NOTHING two raids running — name the escapees
                Plugin.Log.LogWarning($"[Scrub] '{scene.name}': 0 blockers matched but {cubes} Cube renderer(s) present "
                    + $"(sample layer {sampleLayer} '{LayerMask.LayerToName(sampleLayer)}')");
            return blockers;
        }

        // belt-and-braces re-pass once the raid is up: catches scenes whose
        // sceneLoaded processing died mid-hook and blockers activated after load
        // (2026-08-18: Area_02's cubes survived the load-time pass with no log)
        private static bool _lateSwept;

        internal static void ResetForRaid() => _lateSwept = false;

        internal static void TickLateSweep()
        {
            if (_lateSwept || !TerminalGate.On) return;
            var gw = Comfort.Common.Singleton<EFT.GameWorld>.Instantiated
                ? Comfort.Common.Singleton<EFT.GameWorld>.Instance : null;
            if (gw == null || gw.MainPlayer == null) return;
            _lateSwept = true;
            int total = 0, scenes = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded || sc.name == null || !sc.name.StartsWith("Terminal")) continue;
                try { total += HideBlockers(sc); scenes++; }
                catch (Exception e) { Plugin.Log.LogWarning($"[Scrub] late sweep '{sc.name}': {e.Message}"); }
            }
            Plugin.Log.LogInfo($"[Scrub] late blocker sweep over {scenes} scene(s): {total} straggler renderer(s) hidden");
        }

        internal static void Scrub(Scene scene)
        {
            try
            {
                HideBlockers(scene);

                if (_resolved == null)
                {
                    var list = new List<Type>();
                    foreach (var n in BrokenTypes)
                    {
                        var t = AccessTools.TypeByName(n);
                        if (t != null && typeof(Component).IsAssignableFrom(t)) list.Add(t);
                        else Plugin.Log.LogDebug($"[Scrub] type '{n}' not found — skipped");
                    }
                    _resolved = list.ToArray();
                }

                int killed = 0;
                var counts = new Dictionary<string, int>();
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var t in _resolved)
                        foreach (var c in root.GetComponentsInChildren(t, true))
                        {
                            UnityEngine.Object.DestroyImmediate(c);
                            killed++;
                            counts.TryGetValue(t.Name, out var n);
                            counts[t.Name] = n + 1;
                        }
                if (killed > 0)
                {
                    var parts = new List<string>();
                    foreach (var kv in counts) parts.Add($"{kv.Value}x {kv.Key}");
                    Plugin.Log.LogWarning($"[Scrub] '{scene.name}': destroyed {killed} broken ripped component(s) — {string.Join(", ", parts)}");
                }

                HealSky(scene);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Scrub] '{scene.name}' failed: {e.Message}"); }
        }

        // the retail TOD sky rig survived the rip structurally (dome + all transform
        // children) but its serialized PARAMETER classes (World/Cycle/Light/...) can
        // arrive null — TOD_Sky.Awake -> Initialize -> LateUpdate -> method_18 NREs
        // and the sky sits undriven (blown-white dome, no sun). default-construct the
        // nulled classes on the TOD components, then re-run Initialize. static sky at
        // the authored hour; the weather phase later drives it live.
        private static void HealSky(Scene scene)
        {
            try
            {
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var sky in root.GetComponentsInChildren<TOD_Sky>(true))
                    {
                        int healed = 0, rewired = 0;
                        foreach (var c in sky.GetComponents<Component>())
                        {
                            if (c == null) continue;
                            var tn = c.GetType().Name;
                            if (!tn.StartsWith("TOD_")) continue;
                            healed += HealNullFields(c);
                            rewired += RewireChildRefs(c, sky.transform);
                        }
                        int assets = FillSkyResources(sky);
                        int rebound = RebindSkyShaders(sky);
                        ApplyTodAuthoring(sky); // retail lat/long/UTC + atmosphere + day length
                        HealTodComponents(sky); // MUST precede Initialize — LightTransform or no sky at all
                        Plugin.Log.LogWarning($"[Sky] '{scene.name}': TOD rig on '{sky.name}' — {healed} null field(s) defaulted, {rewired} child ref(s) rewired, {assets} resource asset(s) reverse-filled, {rebound} shader(s) rebound to native, re-initializing");
                        try
                        {
                            sky.Initialize();
                            Plugin.Log.LogWarning("[Sky] TOD_Sky re-initialized OK — sun/sky driven at the authored hour");
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError($"[Sky] TOD_Sky.Initialize still fails — sky stays undriven. FULL: {e}");
                        }
                    }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sky] heal failed: {e.Message}"); }
        }

        // THE DAYLIGHT ROOT CAUSE (2026-08-11, found via the LateUpdate NRE that had been
        // in every log all along): TOD_Sky.method_18 — the sun/moon solver — opens with
        //     this.Components.LightTransform.rotation = ...
        // and TOD_Components.Init only sets LightTransform when its `Light` GameObject
        // ref survived ("Light reference not set." otherwise). the rip dropped that ref,
        // so method_18 threw on its FIRST LINE, every frame, forever. the sky therefore
        // never recomputed anything after Initialize and sat frozen at the scene's
        // authored Cycle.Hour = 13.0 — broad daylight — no matter how correct the clock,
        // the hour sync or the world params were. every previous "the sky ignores our
        // time" symptom traces here.
        //
        // heal: give it a real directional light (an existing one under the dome, the
        // scene's own sun, or a fresh child), then set the derived refs Init would have.
        private static void HealTodComponents(TOD_Sky sky)
        {
            try
            {
                var comps = sky.GetComponent<TOD_Components>();
                if (!comps) return;

                if (!comps.Light)
                {
                    GameObject lightGo = null;
                    foreach (var l in sky.GetComponentsInChildren<Light>(true))
                        if (l && l.type == LightType.Directional) { lightGo = l.gameObject; break; }
                    if (!lightGo)
                    {
                        var sun = RenderSettings.sun;
                        if (sun && sun.gameObject.scene.IsValid()) lightGo = sun.gameObject;
                    }
                    if (!lightGo)
                    {
                        lightGo = new GameObject("TOD Light");
                        lightGo.transform.SetParent(sky.transform, false);
                        var nl = lightGo.AddComponent<Light>();
                        nl.type = LightType.Directional;
                        nl.shadows = LightShadows.Soft;
                        nl.intensity = 0f; // TOD drives this from the Day/Night curves
                        Plugin.Log.LogWarning("[Sky] TOD had no light reference and the scene had no directional light — created one");
                    }
                    comps.Light = lightGo;
                }
                comps.LightTransform = comps.Light.transform;
                comps.LightSource = comps.Light.GetComponent<Light>();
                if (!comps.LightSource) comps.LightSource = comps.Light.AddComponent<Light>();
                if (comps.LightSource.type != LightType.Directional) comps.LightSource.type = LightType.Directional;
                if (!comps.DomeTransform) comps.DomeTransform = sky.transform;

                // GEOMETRY SANITY (2026-08-15): the solver's SunDirection comes from
                // SunTransform.LookAt(DomeTransform.position) — if the dome's scale
                // collapsed in the rip (or the sun isn't parented under it), sun and
                // dome share a point and LookAt degenerates to a CONSTANT direction, no
                // matter what the orbital math computes. that is the observed symptom:
                // hour advances, LerpValue changes between raids (solver runs), sunDir.y
                // frozen at +0.115.
                var dome = comps.DomeTransform;
                var ds = dome.localScale;
                if (ds.x < 0.01f || ds.y < 0.01f || ds.z < 0.01f)
                {
                    dome.localScale = Vector3.one * Mathf.Max(ds.x, ds.y, ds.z, 1f);
                    Plugin.Log.LogWarning($"[Sky] dome scale was degenerate {ds} — reset to {dome.localScale} (frozen-sun fix)");
                }
                if (comps.SunTransform && comps.SunTransform.parent != dome)
                {
                    Plugin.Log.LogWarning($"[Sky] Sun parented under '{(comps.SunTransform.parent ? comps.SunTransform.parent.name : "NULL")}' "
                        + "not the dome — reparenting (orbital positions are dome-local)");
                    comps.SunTransform.SetParent(dome, false);
                }
                if (comps.MoonTransform && comps.MoonTransform.parent != dome)
                    comps.MoonTransform.SetParent(dome, false);
                if (!comps.SunTransform && comps.Sun) comps.SunTransform = comps.Sun.transform;
                if (!comps.MoonTransform && comps.Moon) comps.MoonTransform = comps.Moon.transform;
                if (!comps.SpaceTransform && comps.Space) comps.SpaceTransform = comps.Space.transform;

                Plugin.Log.LogWarning($"[Sky] TOD component refs healed — light='{comps.Light.name}' "
                    + $"lightTransform={(bool)comps.LightTransform} sun={(bool)comps.SunTransform} "
                    + $"moon={(bool)comps.MoonTransform} dome={(bool)comps.DomeTransform}"
                    + "  <- a null LightTransform is what froze the sky at its authored 13:00");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sky] TOD component heal failed: {e.Message}"); }
        }

        // AUTHORED TOD PARAMETERS (2026-08-11: the map renders broad daylight at a
        // verifiably-synced 22:01). the sun comes from `Cycle.Hour - World.UTC` with
        // World.Latitude/Longitude (TOD_Sky:978-986), and the sky's look comes from the
        // Atmosphere/Day/Night blocks — all serialized SUB-OBJECTS, which is exactly what
        // the rip nulls and our scrub can only default-CONSTRUCT. defaults put the sun
        // somewhere else entirely and leave daytime atmosphere constants in place, so the
        // hour sync lands on a sky that ignores it.
        //
        // retail's authored values (extract_terminal_tod.py, level600): World lat 46 /
        // long 84 / UTC 7.24, Atmosphere brightness 0.299, and TOD_Time DayLength 3000min.
        // NOTE the scene ships Cycle.Hour = 13.0 — the editor's daytime state; retail
        // overrides it at runtime because the map only opens 21:00-06:00, which is why
        // the authored hour must never be restored (TickSkyTime owns the clock).
        private static JObject _todSidecar;
        private static bool _todTried;

        private static JObject TodSidecar()
        {
            if (_todTried) return _todSidecar;
            _todTried = true;
            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_tod_sky.json");
                if (System.IO.File.Exists(path)) _todSidecar = JObject.Parse(System.IO.File.ReadAllText(path));
                else Plugin.Log.LogDebug($"[Sky] no TOD sidecar at {path}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sky] TOD sidecar parse failed: {e.Message}"); }
            return _todSidecar;
        }

        private static int ApplyTodAuthoring(TOD_Sky sky)
        {
            var sc = TodSidecar();
            var comps = sc?["components"] as JObject;
            if (comps == null) return 0;
            int applied = 0, missed = 0;

            void ApplyRows(string cls, Component target)
            {
                if (target == null) return;
                var row = (comps[cls] as JArray)?.FirstOrDefault()?["fields"] as JObject;
                if (row == null) return;
                foreach (var prop in row.Properties())
                {
                    var clean = prop.Name.StartsWith("<")
                        ? prop.Name.Substring(1, prop.Name.IndexOf('>') - 1)
                        : prop.Name;
                    // the clock is ours — never restore the editor's 13:00
                    if (clean == "Cycle") continue;
                    ApplyTodMember(target, clean, prop.Value, ref applied, ref missed);
                }
            }

            ApplyRows("TOD_Sky", sky);
            ApplyRows("TOD_Time", sky.GetComponent<TOD_Time>());

            try
            {
                Plugin.Log.LogWarning($"[Sky] retail TOD authoring restored ({applied} value(s)): "
                    + $"lat={sky.World.Latitude:0.0} long={sky.World.Longitude:0.0} UTC={sky.World.UTC:0.00} "
                    + $"atmoBrightness={sky.Atmosphere.Brightness:0.000} "
                    + $"dayLenMin={(sky.GetComponent<TOD_Time>() != null ? sky.GetComponent<TOD_Time>().DayLengthInMinutes : -1f):0}"
                    + $" gradients/colors/curves included, {missed} member(s) missed"
                    + "  <- the old shallow loader left the retail sky gradients at default black");
            }
            catch { }
            return applied;
        }

        // Full TOD value converter, ported from Icebreaker's proven retail-sky
        // restore. The old Terminal loader descended only one level and accepted
        // primitives, leaving Day/Night/Sun/Moon gradients at default black.
        private static void ApplyTodMember(object target, string name, JToken token,
            ref int applied, ref int missed)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var type = target.GetType();
            var field = type.GetField(name, flags) ?? type.GetField($"<{name}>k__BackingField", flags);
            var property = field == null ? type.GetProperty(name, flags) : null;
            if (field == null && property == null) { missed++; return; }

            var memberType = field != null ? field.FieldType : property.PropertyType;
            object existing = null;
            try { existing = field != null ? field.GetValue(target) : property.GetValue(target, null); }
            catch { }

            var converted = ConvertTodValue(token, memberType, existing, ref applied, ref missed);
            if (converted == null) return;
            try
            {
                if (field != null) field.SetValue(target, converted);
                else if (property.CanWrite) property.SetValue(target, converted, null);
                else { missed++; return; }
                applied++;
            }
            catch { missed++; }
        }

        private static object ConvertTodValue(JToken token, Type memberType, object existing,
            ref int applied, ref int missed)
        {
            try
            {
                if (memberType == typeof(float)) return token.Value<float>();
                if (memberType == typeof(double)) return token.Value<double>();
                if (memberType == typeof(int)) return token.Value<int>();
                if (memberType == typeof(bool))
                    return token.Type == JTokenType.Boolean ? token.Value<bool>() : token.Value<int>() != 0;
                if (memberType.IsEnum) return Enum.ToObject(memberType, token.Value<int>());

                var obj = token as JObject;
                if (obj == null) { missed++; return null; }
                float F(string key, float fallback = 0f) => obj.Value<float?>(key) ?? fallback;

                if (memberType == typeof(Color))
                    return new Color(F("r"), F("g"), F("b"), F("a", 1f));
                if (memberType == typeof(Vector2)) return new Vector2(F("x"), F("y"));
                if (memberType == typeof(Vector3)) return new Vector3(F("x"), F("y"), F("z"));
                if (memberType == typeof(Vector4)) return new Vector4(F("x"), F("y"), F("z"), F("w"));
                if (memberType == typeof(AnimationCurve))
                {
                    var rows = obj["m_Curve"] as JArray;
                    if (rows == null) { missed++; return null; }
                    var keys = new Keyframe[rows.Count];
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var key = rows[i] as JObject;
                        if (key == null) continue;
                        float KF(string n, float d = 0f) => key.Value<float?>(n) ?? d;
                        keys[i] = new Keyframe(KF("time"), KF("value"), KF("inSlope"), KF("outSlope"),
                            KF("inWeight", 1f / 3f), KF("outWeight", 1f / 3f));
                        keys[i].weightedMode = (WeightedMode)(key.Value<int?>("weightedMode") ?? 0);
                    }
                    return new AnimationCurve(keys)
                    {
                        preWrapMode = (WrapMode)(obj.Value<int?>("m_PreInfinity") ?? (int)WrapMode.ClampForever),
                        postWrapMode = (WrapMode)(obj.Value<int?>("m_PostInfinity") ?? (int)WrapMode.ClampForever),
                    };
                }
                if (memberType == typeof(Gradient))
                {
                    int colorCount = Mathf.Clamp(obj.Value<int?>("m_NumColorKeys") ?? 2, 1, 8);
                    int alphaCount = Mathf.Clamp(obj.Value<int?>("m_NumAlphaKeys") ?? 2, 1, 8);
                    var colors = new GradientColorKey[colorCount];
                    for (int i = 0; i < colorCount; i++)
                    {
                        var key = obj[$"key{i}"] as JObject;
                        if (key == null) { missed++; return null; }
                        colors[i] = new GradientColorKey(new Color(
                            key.Value<float?>("r") ?? 0f, key.Value<float?>("g") ?? 0f,
                            key.Value<float?>("b") ?? 0f),
                            (obj.Value<int?>($"ctime{i}") ?? 0) / 65535f);
                    }
                    var alphas = new GradientAlphaKey[alphaCount];
                    for (int i = 0; i < alphaCount; i++)
                    {
                        var key = obj[$"key{i}"] as JObject;
                        if (key == null) { missed++; return null; }
                        alphas[i] = new GradientAlphaKey(key.Value<float?>("a") ?? 1f,
                            (obj.Value<int?>($"atime{i}") ?? 0) / 65535f);
                    }
                    var gradient = new Gradient();
                    gradient.SetKeys(colors, alphas);
                    return gradient;
                }

                var nested = existing;
                if (nested == null && memberType.IsClass && !memberType.IsAbstract
                    && memberType.GetConstructor(Type.EmptyTypes) != null)
                    nested = Activator.CreateInstance(memberType);
                if (nested != null && memberType.IsClass)
                {
                    foreach (var child in obj.Properties())
                    {
                        var childName = child.Name.StartsWith("<")
                            ? child.Name.Substring(1, child.Name.IndexOf('>') - 1)
                            : child.Name;
                        ApplyTodMember(nested, childName, child.Value, ref applied, ref missed);
                    }
                    return existing == null ? nested : null;
                }
            }
            catch { missed++; return null; }

            missed++;
            return null;
        }

        // the reverse-filled materials carry the BUNDLE's recompiled TOD shaders — the
        // heli disease: they compile (isSupported lies) but render garbage. atmosphere
        // draws blown-bright whatever the sun does ("still daytime" at a synced 22:00)
        // and the sun quad loses its additive blend (opaque black-backed plane that
        // FOLLOWS the player, because TOD parents the dome to the camera). EFT ships
        // TOD natively, so the real shaders live in the game registry — force every
        // sky material onto the registry instance by name, heli-heal style.
        private static int RebindSkyShaders(TOD_Sky sky)
        {
            int rebound = 0;
            var seen = new HashSet<Material>();
            void Rebind(Material m)
            {
                if (m == null || !seen.Add(m) || m.shader == null) return;
                Shader native = null;
                try { native = GClass872.Find(m.shader.name); } catch { }
                if (native == null || !native.isSupported)
                {
                    Plugin.Log.LogWarning($"[Sky] no native shader for '{m.name}' ('{m.shader.name}') — stays on the bundle copy");
                    return;
                }
                int queueBefore = m.renderQueue;
                if (!ReferenceEquals(native, m.shader))
                {
                    m.shader = native;
                    rebound++;
                }
                // THE BLACK-BACKED SUN: a material carries its own renderQueue override,
                // and the rip's value survives a shader swap. the TOD sun/moon/space
                // materials are additive-transparent (queue 3000+); stuck at Geometry
                // they draw as opaque quads with a black background — exactly what the
                // sky has been doing. -1 = "use the shader's own queue".
                m.renderQueue = -1;
                Plugin.Log.LogDebug($"[Sky] '{m.name}' -> '{native.name}' queue {queueBefore} -> {m.renderQueue}"
                    + $" (blend src={(m.HasProperty("_SrcBlend") ? m.GetFloat("_SrcBlend").ToString() : "n/a")}"
                    + $" dst={(m.HasProperty("_DstBlend") ? m.GetFloat("_DstBlend").ToString() : "n/a")}"
                    + $" zwrite={(m.HasProperty("_ZWrite") ? m.GetFloat("_ZWrite").ToString() : "n/a")})");
            }

            var res = sky.GetComponent<TOD_Resources>();
            if (res != null)
            {
                Rebind(res.SpaceMaterial);
                Rebind(res.AtmosphereMaterial);
                Rebind(res.ClearMaterial);
                Rebind(res.SunMaterial);
                Rebind(res.MoonMaterial);
                Rebind(res.SkyboxMaterial);
            }
            foreach (var r in sky.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    Rebind(m);
            return rebound;
        }

        // TOD_Resources' serialized ASSETS (dome meshes + sky materials) are rip-nulled
        // and its Initialize only computes shader ids — method_0 then NREs on the null
        // Quad mesh. reverse-fill from the dome children: they still carry the authored
        // meshes/materials, and method_0's own job is assigning these exact objects
        // back onto these exact children — sourcing from them makes that a no-op.
        private static int FillSkyResources(TOD_Sky sky)
        {
            var res = sky.GetComponent<TOD_Resources>();
            if (res == null) return 0;
            Transform Child(string name)
            {
                foreach (var t in sky.transform.GetComponentsInChildren<Transform>(true))
                    if (t.name == name) return t;
                return null;
            }
            Material Mat(string name)
            {
                var t = Child(name);
                var r = t != null ? t.GetComponent<Renderer>() : null;
                return r != null ? r.sharedMaterial : null;
            }
            Mesh MeshOf(string name)
            {
                var t = Child(name);
                var mf = t != null ? t.GetComponent<MeshFilter>() : null;
                return mf != null ? mf.sharedMesh : null;
            }

            int n = 0;
            void SetMat(ref Material field, Material v) { if (field == null && v != null) { field = v; n++; } }
            void SetMesh(ref Mesh field, Mesh v) { if (field == null && v != null) { field = v; n++; } }

            SetMat(ref res.SpaceMaterial, Mat("Space"));
            SetMat(ref res.AtmosphereMaterial, Mat("Atmosphere"));
            SetMat(ref res.ClearMaterial, Mat("Clear"));
            SetMat(ref res.SunMaterial, Mat("Sun"));
            SetMat(ref res.MoonMaterial, Mat("Moon"));
            SetMat(ref res.SkyboxMaterial, Mat("Atmosphere"));

            var ico = MeshOf("Atmosphere") ?? MeshOf("Space");
            var half = MeshOf("Clear") ?? ico;
            var sph = MeshOf("Moon") ?? ico;
            var quad = MeshOf("Sun") ?? sph;
            SetMesh(ref res.IcosphereHigh, ico);
            SetMesh(ref res.IcosphereMedium, ico);
            SetMesh(ref res.IcosphereLow, ico);
            SetMesh(ref res.HalfIcosphereHigh, half);
            SetMesh(ref res.HalfIcosphereMedium, half);
            SetMesh(ref res.HalfIcosphereLow, half);
            SetMesh(ref res.SphereHigh, sph);
            SetMesh(ref res.SphereMedium, sph);
            SetMesh(ref res.SphereLow, sph);
            SetMesh(ref res.Quad, quad);
            return n;
        }

        // rip-nulled serialized GameObject/Transform refs re-pointed by NAME against
        // the rig's children (TOD_Components.Sun -> the dome's 'Sun' child, etc. —
        // Initialize null-checks these and silently skips wiring, leaving
        // SunTransform/LightTransform null for method_18 to NRE on)
        private static int RewireChildRefs(Component c, Transform rigRoot)
        {
            int n = 0;
            foreach (var f in c.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                var ft = f.FieldType;
                if (ft != typeof(GameObject) && ft != typeof(Transform)) continue;
                try
                {
                    if (f.GetValue(c) is UnityEngine.Object existing && existing != null) continue;
                    Transform match = null;
                    foreach (var t in rigRoot.GetComponentsInChildren<Transform>(true))
                        if (t.name == f.Name) { match = t; break; }
                    if (match == null) continue;
                    f.SetValue(c, ft == typeof(GameObject) ? (UnityEngine.Object)match.gameObject : match);
                    n++;
                }
                catch { }
            }
            return n;
        }

        // null plain-class fields (parameterless ctor) -> defaults; null arrays/lists
        // -> empty. never touches UnityEngine.Object refs (a defaulted material/
        // transform would hide real breakage).
        private static int HealNullFields(Component c)
        {
            int n = 0;
            foreach (var f in c.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                var ft = f.FieldType;
                try
                {
                    if (f.GetValue(c) != null) continue;
                    if (typeof(UnityEngine.Object).IsAssignableFrom(ft)) continue;
                    if (ft.IsArray && ft.GetArrayRank() == 1)
                    { f.SetValue(c, Array.CreateInstance(ft.GetElementType(), 0)); n++; }
                    else if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>))
                    { f.SetValue(c, Activator.CreateInstance(ft)); n++; }
                    else if (ft.IsClass && !ft.IsAbstract && ft.GetConstructor(Type.EmptyTypes) != null)
                    { f.SetValue(c, Activator.CreateInstance(ft)); n++; }
                }
                catch { }
            }
            return n;
        }
    }
}
