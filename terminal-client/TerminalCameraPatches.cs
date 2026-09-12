using System;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // the Cam2-era fix set, ported 1:1 from icebreaker's RaidFixPatches. our scenes
    // ship no usable camera prefab, so the game falls back to its built-in "Cam2"
    // from InGameResources — an OLD prefab predating several effects every retail
    // map's own camera carries. each patch below is one measured gap.

    // ALWAYS discard the scene camera prefab. shipping a camera through the bundle
    // was tried and measured dead on icebreaker: the rip's serialized DATA does not
    // survive (null shaders/materials, empty curves crashed NightVision/Thermal/
    // DistortCameraFX in Awake), and the un-shippable SSAA left EFT.CameraControl.CameraManager.SetSSR
    // to NRE inside PlayerCameraController.Create — error screen, no spawn. the
    // camera story is: Cam2 as the CHASSIS + the donor graft (TerminalCameraDonor).
    [HarmonyPatch(typeof(EFT.CameraControl.CameraManager), "SetCameraFromSettings")]
    internal static class Patch_RejectShellCameraPrefab
    {
        private static void Prefix(ref EFT.CameraControl.CameraManager.ISettings settings)
        {
            if (!TerminalGate.On) return; // vanilla maps: not even a log line
            var prefab = settings != null && settings.CameraPrefab != null ? settings.CameraPrefab.name : "<null>";
            Plugin.Log.LogDebug($"[RaidFix] SetCameraFromSettings on terminal: prefab={prefab}");
            if (settings == null || settings.CameraPrefab == null)
                return; // already headed for the Cam2 fallback
            Plugin.Log.LogDebug("[RaidFix] discarding scene camera prefab — Cam2 chassis + donor graft owns the camera");
            settings = null;
        }
    }

    // GRENADE FLASH HEAL: Awake derefs a serialized same-prefab component ref
    // (PrismEffects.toneValues) that can arrive null — the component itself sits on
    // the same GameObject, one GetComponent away. re-point before Awake reads it.
    // flashbang blindness is gameplay, which is why this is healed rather than
    // dropped. OnEnable re-calls Awake; the null check makes later passes free.
    [HarmonyPatch(typeof(GrenadeFlashScreenEffect), "Awake")]
    internal static class Patch_GrenadeFlashPrismRef
    {
        [HarmonyPrefix]
        private static void Prefix(GrenadeFlashScreenEffect __instance)
        {
            if (!TerminalGate.On) return; // real maps ship a real ref — never touch it
            try
            {
                if (__instance.PrismEffects == null)
                {
                    __instance.PrismEffects = __instance.GetComponent<PrismEffects>();
                    if (__instance.PrismEffects != null)
                        Plugin.Log.LogDebug("[RaidFix] GrenadeFlash PrismEffects ref healed from sibling component");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RaidFix] grenade flash heal failed: {e.Message}"); }
        }
    }

    // THE actual Cam2 camera bug: EffectsController.method_3 has exactly ONE
    // unguarded GetComponent — FrostbiteEffect (newer than Cam2). every retail map
    // ships a modern camera prefab so the fallback path never runs; backported maps
    // are the only ones that hit it. give the camera the missing component before
    // EffectsController.Awake needs it; method_3 immediately disables it anyway.
    //
    // NOTE the icebreaker original also adds TOD_Camera + TOD_Scattering here (sky
    // dome follow + fog scattering) — those blocks are gated on its weather system
    // and come over with the weather port, not before.
    [HarmonyPatch(typeof(EffectsController), "Awake")]
    internal static class Patch_EffectsControllerFrostbite
    {
        private static void Prefix(EffectsController __instance)
        {
            if (!TerminalGate.On) return; // vanilla camera prefabs ship the component
            if (__instance.GetComponent<FrostbiteEffect>() == null)
            {
                __instance.gameObject.AddComponent<FrostbiteEffect>();
                Plugin.Log.LogWarning("[RaidFix] added missing FrostbiteEffect to fallback camera (Cam2 predates it)");
            }

            // Cam2 gap #5 — the DLSS/menu-load black screen: Cam2's PostProcessLayer
            // carries no PostProcessResources, cosmetic-only... until a DLSS mod
            // routes the FINAL UPSCALE through the layer — dead layer = quarter-res
            // frame never reaches the backbuffer, black screen + HUD burn-in, zero
            // exceptions. heal: hand the layer the game's own live resources.
            HealPostProcessLayer(__instance.gameObject);
        }

        // reflection-only: the client project doesn't reference
        // Unity.Postprocessing.Runtime, and TarkovDLSS45 IL-patches that assembly
        // anyway — resolve at runtime, touch nothing when absent or already fed
        private static void HealPostProcessLayer(GameObject go)
        {
            try
            {
                var ppType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessLayer");
                if (ppType == null) return;
                var layer = go.GetComponent(ppType) as Behaviour;
                if (layer == null) { Plugin.Log.LogDebug("[RaidFix] no PostProcessLayer on fallback camera"); return; }
                var resField = AccessTools.Field(ppType, "m_Resources");
                if (resField == null) { Plugin.Log.LogWarning("[RaidFix] PostProcessLayer.m_Resources not found — PP version drift?"); return; }
                var res = resField.GetValue(layer) as UnityEngine.Object;
                if (res != null) { Plugin.Log.LogDebug("[RaidFix] PostProcessLayer resources already present — no heal needed"); return; }

                var resType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessResources");
                UnityEngine.Object found = null;
                if (resType != null)
                    foreach (var o in Resources.FindObjectsOfTypeAll(resType)) { found = o; break; }
                if (found == null)
                {
                    Plugin.Log.LogWarning("[RaidFix] PostProcessLayer has NO resources and none found in memory — layer stays dead (DLSS/FSR upscale will not run)");
                    return;
                }
                // disable around Init so OnEnable re-runs bundle init with valid resources
                bool wasEnabled = layer.enabled;
                layer.enabled = false;
                var init = AccessTools.Method(ppType, "Init", new[] { resType });
                if (init != null) init.Invoke(layer, new object[] { found });
                else resField.SetValue(layer, found); // older PP: field only, OnEnable does the rest
                layer.enabled = wasEnabled;
                Plugin.Log.LogWarning($"[RaidFix] HEALED PostProcessLayer: fed '{found.name}' resources (was null) — DLSS/FSR final pass can run");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RaidFix] PostProcessLayer heal failed: {e.Message}"); }
        }
    }

    // Cam2 ships neither RainScreenDrops nor ScreenWater and RainController's camera
    // hookup NREs on them, aborting the whole raid init through
    // PlayerCameraController.Create. the swallow saves the raid — but the rain
    // never re-hooks and the raid runs rainless with broken half-init droplets
    // (2026-08-19, post-icebreaker session: the fallback cam path fired on the
    // MAIN raid camera). so: swallow AND retry the hookup once the real camera
    // with the rain components exists.
    [HarmonyPatch(typeof(RainController), "CameraChanged")]
    internal static class Patch_RainScreenOnCam2
    {
        private static Exception Finalizer(RainController __instance, Exception __exception)
        {
            if (__exception == null) return null;
            if (!TerminalGate.On) return __exception;
            Plugin.Log.LogWarning($"[RaidFix] RainController camera hookup failed (visor drops off): {__exception.Message} — retry armed");
            try
            {
                var go = new GameObject("Terminal_RainRehook");
                go.AddComponent<RainRehookHost>().Rain = __instance;
            }
            catch { }
            return null;
        }
    }

    internal class RainRehookHost : MonoBehaviour
    {
        public RainController Rain;
        private float _deadline;

        private void Start() => _deadline = Time.realtimeSinceStartup + 30f;

        private void Update()
        {
            if (!Rain || Time.realtimeSinceStartup > _deadline) { Done(false); return; }
            var cam = EFT.CameraControl.CameraManager.Instance?.Camera;
            if (cam == null) return;
            // the real FPS camera ships the rain screen components — retry only
            // once they exist so method_0 has something to hook
            if (cam.GetComponent<RainScreenDrops>() == null) return;
            try
            {
                HarmonyLib.AccessTools.Method(typeof(RainController), "CameraChanged")?.Invoke(Rain, null);
                Plugin.Log.LogInfo("[RaidFix] RainController re-hooked to the real camera — rain restored");
                Done(true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RaidFix] rain re-hook failed: {e.InnerException?.Message ?? e.Message}");
                Done(false);
            }
        }

        private void Done(bool ok)
        {
            if (!ok && Rain) Plugin.Log.LogWarning("[RaidFix] rain re-hook gave up — raid stays rainless");
            Destroy(gameObject);
        }
    }

    // NVG on the Cam2-era camera: OnPreCull throws per-frame, goggles show the mask
    // overlay but no effect, and the overlay STICKS after toggling off (the disabled
    // component can't run its off-transition). guard kills the TextureMask overlay
    // too, and logs the FULL stack once so the actual null site can be healed.
    [HarmonyPatch(typeof(BSG.CameraEffects.NightVision), "OnPreCull")]
    internal static class Patch_NightVisionNeverSpams
    {
        private static bool _logged;
        private static Exception Finalizer(Exception __exception, BSG.CameraEffects.NightVision __instance)
        {
            if (__exception != null && !TerminalGate.On) return __exception; // Cam2-era gap only
            if (__exception != null && __instance != null)
            {
                __instance.enabled = false;
                // don't strand the player behind the goggle vignette
                try
                {
                    if (__instance.TextureMask != null)
                    {
                        __instance.TextureMask.Mask = null;
                        __instance.TextureMask.enabled = false;
                    }
                }
                catch { }
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Log.LogWarning($"[RaidFix] NightVision.OnPreCull threw — component disabled, mask cleared. FULL STACK (once): {__exception}");
                }
            }
            return null;
        }
    }

    // safety net: if method_3 trips on yet another Cam2-era gap, don't let it kill
    // Awake — the rest of Awake still runs and the camera lives. a partially-
    // initialized effects stack just means some per-frame effect NREs (non-fatal).
    [HarmonyPatch(typeof(EffectsController), "Init")]
    internal static class Patch_EffectsControllerInit
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!TerminalGate.On) return __exception;
            Plugin.Log.LogWarning($"[RaidFix] swallowed EffectsController.method_3: {__exception.Message}\n{__exception.StackTrace}");
            return null;
        }
    }
}
