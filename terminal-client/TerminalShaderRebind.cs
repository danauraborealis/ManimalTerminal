using System;
using System.Collections;
using System.Collections.Generic;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // the white-model fix, ported from icebreaker's RenderEnvProbe rebind subsystem.
    // our scene bundle carries AssetRipper's compiled shader blobs whose FORWARD pass
    // works but DEFERRED is broken (variants stripped at bundle build) — fine in the
    // editor scene view, white in raid. the GAME has the real copies loaded, and
    // property names are identical, so rebinding every material to Shader.Find(name)
    // fixes lighting while keeping textures. terminal's shader audit: 45/52 ripped
    // shaders exist in 4.0.13 by name, so this pass covers nearly everything.
    internal static class TerminalShaderRebind
    {
        // shaders MISSING from 4.0.13 (terminal_shader_audit) aliased onto the closest
        // game particle shader — the attack cutscene's 413 particle systems rendered
        // NOTHING because their ripped blobs are dead and no same-name rebind target
        // exists ("stand-in decisions when seen broken" — seen broken 08-09, muzzle
        // lights fired but no explosion/smoke particles). additive stand-ins read
        // bright-ish for smoke; refine per-shader if it looks off.
        private static readonly Dictionary<string, string> ShaderAliases = new Dictionary<string, string>
        {
            { "Particles/Explosion", "Particles/SuperAdditive" },
            { "Particles/Emissive", "Particles/AdditiveSimple" },
            { "Effects/Explosions/Particles/Alpha Blended", "Particles/AdditiveSimple" },
        };

        // NEVER rebind these — keep the bundle's own stand-in (icebreaker lesson: the
        // game ships a compiled Cloth pair in its own sharedassets5, older than the
        // 1.0 materials we author for; the name sweep silently swapped cloth onto it
        // and four successive stand-in fixes rendered zero frames)
        private static readonly HashSet<string> RebindExclude = new HashSet<string>
        {
            "Cloth/ClothShader",
            "Cloth/ClothShader_backface",
        };

        // OWNERSHIP GATE for the global sweep. it walks every Renderer in the world —
        // ParticleSystemRenderer included — which on icebreaker grabbed OTHER MODS'
        // materials (HollywoodFX particles became opaque squares). the discriminator
        // is ORIGIN: materials embedded in OUR scenes are captured at scene load,
        // before any mod instantiates anything, and the global pass touches only
        // those. our own runtime spawns use the scoped RebindShadersUnder instead.
        private static readonly HashSet<int> _ourMaterials = new HashSet<int>();
        // MUST clear per raid (2026-08-19: second raid in one session rendered
        // white map-wide) — the bundle materials SURVIVE between raids with the
        // same instance ids, but the native shaders they were rebound to die in
        // raid teardown. a stale done-set then skips every dead-shader material.
        // _ourMaterials stays: same-instance persistence makes its dedup correct,
        // and clearing it would wipe transit-preloaded captures (transit loads
        // scenes before raid creation — the transit-gate-blindness trap).
        private static readonly HashSet<int> _rebindDone = new HashSet<int>();

        internal static void ResetForRaid()
        {
            _rebindDone.Clear();
            _missingShaders.Clear();
        }

        // called from Plugin's sceneLoaded hook for every Terminal* scene. gate by
        // scene NAME only, never by TerminalGate.On — see the transit note above.
        internal static void CaptureSceneMaterials(UnityEngine.SceneManagement.Scene scene)
        {
            try
            {
                int added = 0;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                        foreach (var m in r.sharedMaterials)
                            if (m != null && _ourMaterials.Add(m.GetInstanceID())) added++;
                Plugin.Log.LogDebug($"[RebindShaders] captured {added} scene-owned material(s) from '{scene.name}'");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RebindShaders] material capture failed for '{scene.name}': {e.Message}"); }
        }

        // late loaders (the intro cutscene scene arrives after raid start) call this
        // after their content exists — _rebindDone dedupes, so re-runs only touch new
        // materials. sync sweep: acceptable under a fade, never on the load-in frame.
        internal static void RebindNow()
        {
            int rebound = 0, skipped = 0;
            var seen = new HashSet<Material>();
            // include INACTIVE (true): armed-later objects that sit disabled through
            // the raid-start pass keep the broken blob otherwise
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
                Rebind(r, seen, ref rebound, ref skipped);
            Plugin.Log.LogDebug($"[RebindShaders] sync pass: {seen.Count} materials, {rebound} rebound, {skipped} skipped{MissingShaderReport()}");
        }

        // SCOPED rebind for props WE spawn, usable on any map — no ownership gate
        // needed, the root makes it explicit
        internal static int RebindShadersUnder(Transform root)
        {
            if (root == null) return 0;
            int rebound = 0;
            var seen = new HashSet<Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null || !seen.Add(m)) continue;
                    var name = m.shader.name;
                    if (RebindExclude.Contains(name)) continue;
                    if (ShaderAliases.TryGetValue(name, out var alias)) name = alias;
                    var gameShader = FindGameShader(name);
                    if (gameShader != null && gameShader != m.shader) { m.shader = gameShader; rebound++; }
                }
            }
            if (rebound > 0)
                Plugin.Log.LogInfo($"[RebindShaders] scoped: {rebound} material(s) under '{root.name}' bound to game shaders");
            return rebound;
        }

        // scene-owned shaders with NO same-name game counterpart — these materials
        // keep the bundle's compiled copy, whose DEFERRED pass may be stripped
        // (geometry that ignores lights: black under lamps). the histogram names
        // what still needs a stand-in/alias decision.
        private static readonly Dictionary<string, int> _missingShaders = new Dictionary<string, int>();

        private static void Rebind(Renderer r, HashSet<Material> seen, ref int rebound, ref int skipped)
        {
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || m.shader == null || !seen.Add(m)) continue;
                if (!_ourMaterials.Contains(m.GetInstanceID())) { skipped++; continue; } // another mod's material — never ours to touch
                if (!_rebindDone.Add(m.GetInstanceID())) continue; // handled in a prior pass
                var name = m.shader.name;
                if (RebindExclude.Contains(name)) { skipped++; continue; }
                if (ShaderAliases.TryGetValue(name, out var alias)) name = alias;
                var gameShader = FindGameShader(name);
                if (gameShader != null && gameShader != m.shader) { m.shader = gameShader; rebound++; }
                else
                {
                    skipped++;
                    if (gameShader == null)
                    {
                        _missingShaders.TryGetValue(name, out var c);
                        _missingShaders[name] = c + 1;
                    }
                }
            }
        }

        private static string MissingShaderReport()
        {
            if (_missingShaders.Count == 0) return "";
            var top = new List<KeyValuePair<string, int>>(_missingShaders);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < top.Count && i < 10; i++) parts.Add($"{top[i].Value}x {top[i].Key}");
            return $" | NO game counterpart (bundle copy stays, deferred may be dead): {string.Join(", ", parts)}";
        }

        // Shader.Find covers everything the client has loaded; ShadersFinder is the game's
        // bundle-shader registry (4.0.13 name — re-verify on any client update).
        // icebreaker's alias + retry machinery (for stand-ins whose real shader loads
        // late from the global shaders bundle) is NOT ported — terminal has no aliases
        // yet; see RaidFixPatches.RetryAliasPending if one is ever needed.
        private static Shader FindGameShader(string name)
        {
            var s = Shader.Find(name);
            if (s == null) { try { s = ShadersFinder.Find(name); } catch { } }
            return s;
        }

        // the raid-start pass: sliced walk (icebreaker's sync sweep stacked with other
        // builders was a 14.3s load-in frame). host GO destroys itself when done.
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_RebindAtRaidStart
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On) return;
                var go = new GameObject("Terminal_ShaderRebind");
                go.AddComponent<Host>();
            }
        }

        internal class Host : MonoBehaviour
        {
            private void Start() => StartCoroutine(Run());

            private IEnumerator Run()
            {
                var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
                int slice = 3000, rebound = 0, skipped = 0;
                var seen = new HashSet<Material>();
                foreach (var r in renderers)
                {
                    if (r != null) Rebind(r, seen, ref rebound, ref skipped);
                    if (--slice <= 0) { slice = 3000; yield return null; }
                }
                Plugin.Log.LogInfo($"[RebindShaders] raid-start pass: {seen.Count} materials, {rebound} rebound, {skipped} skipped{MissingShaderReport()}");
                Destroy(gameObject);
            }
        }
    }
}
