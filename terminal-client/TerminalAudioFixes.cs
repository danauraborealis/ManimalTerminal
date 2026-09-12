using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Audio.SpatialSystem;
using HarmonyLib;

namespace Manimal.Terminal
{
    // SPATIAL AUDIO ARMOR — icebreaker's RaidFixPatches recipe, finally ported. terminal
    // stages no acoustics (audiobakedata/rooms deferred), so SpatialAudioSystem never
    // initializes and every occlusion call NREs on the dead internals. THE DOOR KILLER
    // (2026-08-10, user log): SmoothDoorOpenCoroutine plays its sound FIRST — PlaySound ->
    // ProcessOcclusion -> ProcessInteractiveObjectOcclusion NRE killed the coroutine on
    // its opening step, before the angle loop — sound, then a door frozen forever, zero
    // door-side errors. containers dodged it (their open path doesnt occlude), which is
    // why lids swung while doors sat still.
    internal static class TerminalAudioFixes
    {
        // icebreaker's staged model: when the acoustics sidecar + bake stage cleanly,
        // let BSG's REAL Initialize run (full room/portal occlusion). staging
        // unavailable -> skip init, Initialized stays false, and the airbags below own
        // every occlusion call. real maps untouched either way.
        [HarmonyPatch(typeof(SpatialAudioSystem), "Initialize")]
        internal static class Patch_SpatialAudioInitSkip
        {
            [HarmonyPrefix]
            private static bool Prefix(SpatialAudioSystem __instance, ref Task __result)
            {
                // MAP TEST MUST WORK DURING LOAD. TerminalGate.On reads the raid's
                // location id, which isn't set yet when TarkovApplication.method_43
                // calls Initialize — so gating on it alone let the LOADER's premature
                // call through, and method_0 NRE'd and faulted the whole raid-load task
                // ("couldnt even load in", 2026-08-11). the scene-name check is the one
                // that answers correctly that early; keep both.
                if (!TerminalGate.On && !TerminalLoaded.Check()) return true; // real maps: untouched
                if (Plugin.SpatialAudio.Value && TerminalAcoustics.TryPrepareSpatialAudio(__instance))
                    return true; // staged — run the real init

                // NOT staged yet (the loader beats our scenes/sidecar): skipping here is
                // what keeps raid start alive. our own TickSpatialInit drives Initialize
                // again in-raid once staging can actually succeed.
                Plugin.Log.LogInfo("[AudioFix] SpatialAudioSystem.Initialize skipped (staging not ready yet) — "
                    + "occlusion airbags active until we drive it in-raid");
                __result = Task.CompletedTask;
                return false;
            }
        }

        // interactive objects (doors, containers, switches): void method, plain skip.
        // gated on Initialized, not the map — on real maps the system is initialized
        // and the original runs untouched. silent: fires per door sound.
        [HarmonyPatch(typeof(SpatialAudioSystem), nameof(SpatialAudioSystem.ProcessInteractiveObjectOcclusion))]
        internal static class Patch_InteractiveOcclusionUninit
        {
            [HarmonyPrefix]
            private static bool Prefix() => SpatialAudioSystem.Initialized;
        }

        // DOOR FOLEY VOLUME — every door interaction plays open + squeak clips, and
        // with 30-40 bots working doors the creak chorus gets busy (2026-08-11 probe:
        // the "rat squeak" = door_wood_old_squeak, the "chain jingle" = metal-door
        // handle foley). retail has no knob for this; we do.
        [HarmonyPatch(typeof(EFT.Interactive.WorldInteractiveObject), nameof(EFT.Interactive.WorldInteractiveObject.PlaySound))]
        internal static class Patch_DoorFoleyVolume
        {
            [HarmonyPrefix]
            private static void Prefix(ref float volume)
            {
                if (TerminalGate.On) volume *= Plugin.DoorFoleyVolume.Value;
            }
        }

        // Terminal's ripped BarbedWire components have a null _soundBank.  Damage and
        // movement penalties work, but every contact calls BetterAudio.PlayAtPoint
        // with that null bank and throws twice per tick.  Skip only the missing sound;
        // wires with a valid bank and every other map retain normal audio.
        [HarmonyPatch(typeof(EFT.Interactive.BarbedWire), nameof(EFT.Interactive.BarbedWire.PlaySound))]
        internal static class Patch_NullBarbedWireSound
        {
            [HarmonyPrefix]
            private static bool Prefix(EFT.SoundBank ____soundBank)
            {
                return !TerminalGate.On || ____soundBank;
            }
        }

        // impacts/gunshots: BetterAudio routes them through ProcessSourceOcclusion —
        // icebreaker measured thousands of NREs per mag dump on the uninitialized
        // system. -1 = BSG's own EOcclusionTest.None "no occlusion" result; the sound
        // already Play()ed, it just stays unoccluded.
        [HarmonyPatch]
        internal static class Patch_SourceOcclusionUninit
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var method in typeof(SpatialAudioSystem).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (method.Name == "ProcessSourceOcclusion") yield return method;
            }

            [HarmonyPrefix]
            private static bool Prefix(ref int __result)
            {
                if (SpatialAudioSystem.Initialized) return true;
                __result = -1;
                return false;
            }
        }
    }
}
