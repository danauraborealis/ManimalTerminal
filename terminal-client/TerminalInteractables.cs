using System;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // interactable state heals, from the decompile (2026-08-10):
    //
    // CONTAINERS: EFT.InteractionContextHelper.smethod_16 offers "Search" ONLY when
    // DoorState == Shut — the rip left every LootableContainer at None, which
    // renders as no prompt at all (empty actions list). icebreaker's Author 11
    // rebakes _doorState=2 for exactly this reason; terminal heals it at raid
    // start instead so it works with any bundle.
    //
    // DOORS: the visible swing runs through the player-animation-driven
    // interaction; forcing InteractWithoutAnimation routes through the direct
    // SetDoorState/SmoothDoorOpenCoroutine path. doubles as the diagnostic: if
    // doors swing with this on, the animation path is what's broken on the
    // backported map; if they stay frozen, the meshes are static-batched in the
    // bundle and the build needs the unstatic pass.
    internal static class TerminalInteractables
    {
        // re-run the door heal after anything that hands the map back (cutscenes hide
        // and restore props, load scenes, and run the door beat). cheap, idempotent,
        // and it reports what it had to fix so a silent prompt loss names its cause.
        internal static void ReassertDoors(string when)
        {
            if (!TerminalGate.On) return;
            try
            {
                int interactive = UnityEngine.LayerMask.NameToLayer("Interactive");
                int relayered = 0, reactivated = 0, unstuck = 0;
                foreach (var d in UnityEngine.Object.FindObjectsOfType<Door>(true))
                {
                    if (d == null) continue;
                    if (interactive >= 0 && d.gameObject.layer != interactive
                        && d.gameObject.layer != UnityEngine.LayerMask.NameToLayer("Default"))
                    { d.gameObject.layer = interactive; relayered++; }
                    var col = d.GetComponent<Collider>();
                    if (col != null && !col.enabled) { col.enabled = true; reactivated++; }
                    // a door left mid-interaction offers nothing at all
                    if (d.DoorState == EDoorState.Interacting) { d.DoorState = EDoorState.Shut; unstuck++; }
                }
                if (relayered + reactivated + unstuck > 0)
                    Plugin.Log.LogWarning($"[Interactables] doors re-asserted {when}: {relayered} relayered, "
                        + $"{reactivated} collider(s) re-enabled, {unstuck} unstuck from Interacting");
                else
                    Plugin.Log.LogDebug($"[Interactables] doors checked {when} — all healthy");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Interactables] door re-assert failed: {e.Message}"); }
        }

        // THE WHAT-IS-WRONG-WITH-THIS-DOOR BUTTON (F9). dumps every interactive object
        // within 6m with the full set of things that can silence a prompt: layer, active
        // state, collider, door state, key, operability and the interaction flags. one
        // press beats another round of theories.
        internal class ProbeHost : MonoBehaviour
        {
            private void Update()
            {
                if (!Plugin.InteractProbeKey.Value.IsDown()) return;
                try
                {
                    var mp = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
                    if (mp == null) { Plugin.Log.LogWarning("[InteractProbe] no player"); return; }
                    int interactive = LayerMask.NameToLayer("Interactive");
                    int found = 0;
                    foreach (var w in UnityEngine.Object.FindObjectsOfType<WorldInteractiveObject>(true))
                    {
                        if (w == null) continue;
                        float d = (w.transform.position - mp.Position).magnitude;
                        if (d > 6f) continue;
                        found++;
                        var col = w.GetComponent<Collider>();
                        string path = w.name;
                        for (var t = w.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                        Plugin.Log.LogWarning($"[InteractProbe] '{path}' ({w.GetType().Name}) d={d:0.0}m "
                            + $"layer={LayerMask.LayerToName(w.gameObject.layer)}({w.gameObject.layer}"
                            + $"{(w.gameObject.layer == interactive ? "=Interactive OK" : " NOT Interactive")}) "
                            + $"activeInHierarchy={w.gameObject.activeInHierarchy} "
                            + $"collider={(col == null ? "NONE" : col.enabled ? "on" : "OFF")} "
                            + $"state={w.DoorState} snap={w.Snap} keyId='{w.KeyId}' "
                            + $"OPERATABLE={w.Operatable} "   // false = the prompt exists but greys out
                            + $"noInteractions={w.NoInteractionsAllowed} id='{w.Id}'");
                    }
                    Plugin.Log.LogWarning($"[InteractProbe] {found} interactive object(s) within 6m");

                    // REPLAY THE ENGINE'S OWN DECISION, GATE BY GATE. Player.InteractionRaycast
                    // -> GameWorld.FindInteractable is a chain of five separate "no"s, and
                    // from outside you can't tell which one fired — the door looks perfect
                    // either way. masks are BSG's own (GameWorld line 1413/1415).
                    try
                    {
                        var pl = mp;
                        int maskFind = LayerMask.GetMask("Interactive", "Deadbody", "Player", "Loot");
                        int maskBlock = LayerMask.GetMask("HighPolyCollider", "TransparentCollider");
                        Plugin.Log.LogWarning($"[InteractProbe] GATE 1 CanInteract={pl.CurrentState?.CanInteract} "
                            + $"handsController={(pl.HandsController == null ? "NULL" : pl.HandsController.CanInteract().ToString())}"
                            + "  <- both must be true or NOTHING is interactable");
                        var camT = Camera.main;
                        if (camT != null)
                        {
                            var ray = new Ray(camT.transform.position, camT.transform.forward);
                            if (Physics.Raycast(ray, out var fh, 3f, maskFind, QueryTriggerInteraction.Ignore))
                            {
                                var io = fh.collider.gameObject.GetComponentInParent<InteractableObject>();
                                bool blocked = Physics.Linecast(ray.origin, fh.point, maskBlock);
                                Plugin.Log.LogWarning($"[InteractProbe] GATE 2 raycast hit '{fh.collider.name}' at {fh.distance:0.00}m "
                                    + $"layer={LayerMask.LayerToName(fh.collider.gameObject.layer)}");
                                Plugin.Log.LogWarning($"[InteractProbe] GATE 3 occlusion linecast blocked={blocked}"
                                    + (blocked ? "  <- THIS kills the prompt (something on HighPoly/TransparentCollider is in the way)" : ""));
                                Plugin.Log.LogWarning($"[InteractProbe] GATE 4 InteractableObject={(io == null ? "NONE FOUND" : io.GetType().Name)}");
                                if (io != null)
                                    Plugin.Log.LogWarning($"[InteractProbe] GATE 5 InteractsFromAppropriateDirection="
                                        + $"{io.InteractsFromAppropriateDirection(pl.LookDirection)}"
                                        + "  <- false = authored interaction direction/dot rejects your angle");
                            }
                            else Plugin.Log.LogWarning("[InteractProbe] GATE 2 raycast (Interactive/Deadbody/Player/Loot) hit NOTHING within 3m");
                        }
                        Plugin.Log.LogWarning($"[InteractProbe] engine currently holds InteractableObject="
                            + $"{(pl.InteractableObject == null ? "NULL (no prompt)" : pl.InteractableObject.GetType().Name)}");
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"[InteractProbe] gate replay failed: {e.Message}"); }

                    var cam = Camera.main;
                    if (cam != null)
                    {
                        var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, 6f,
                            ~0, QueryTriggerInteraction.Collide);
                        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                        if (hits.Length == 0) Plugin.Log.LogWarning("[InteractProbe] crosshair ray hit NOTHING within 6m");
                        for (int i = 0; i < hits.Length && i < 6; i++)
                        {
                            var h = hits[i];
                            var owner = h.collider.GetComponentInParent<WorldInteractiveObject>();
                            Plugin.Log.LogWarning($"[InteractProbe]   ray[{i}] {h.distance:0.00}m '{h.collider.name}' "
                                + $"layer={LayerMask.LayerToName(h.collider.gameObject.layer)} "
                                + $"trigger={h.collider.isTrigger} "
                                + $"owner={(owner == null ? "none" : owner.GetType().Name + " '" + owner.Id + "'")}");
                        }
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[InteractProbe] failed: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_HealInteractables
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On) return;
                if (UnityEngine.Object.FindObjectOfType<ProbeHost>() == null)
                    new GameObject("Terminal_InteractProbe").AddComponent<ProbeHost>();
                try
                {
                    int healed = 0, remapped = 0;
                    foreach (var lc in UnityEngine.Object.FindObjectsOfType<LootableContainer>(true))
                    {
                        if (lc == null) continue;
                        if (TerminalContainerTemplates.Apply(lc)) remapped++;
                        if (lc.DoorState != EDoorState.None) continue;
                        try { lc.DoorState = EDoorState.Shut; healed++; }
                        catch { }
                    }
                    if (healed > 0 || remapped > 0)
                        Plugin.Log.LogInfo($"[Interactables] {healed} container(s) healed None -> Shut (Search prompt requires Shut), {remapped} container template(s) restored from server authoring");

                    // DOOR TRANSFORM STOMPERS (2026-08-10: container lids swing through
                    // the exact same coroutine/CurrentAngle path, doors don't move and
                    // throw nothing) — an enabled Animator on the door hierarchy rewrites
                    // the transform every frame AFTER the door code sets it (AssetRipper
                    // habitually adds Animators to prefab roots; retail doors drive the
                    // leaf transform directly, so nothing legit is lost by disabling)
                    int stripped = 0;
                    foreach (var d in UnityEngine.Object.FindObjectsOfType<Door>(true))
                    {
                        if (d == null) continue;
                        foreach (var an in d.GetComponentsInChildren<Animator>(true))
                            if (an != null && an.enabled)
                            {
                                an.enabled = false;
                                stripped++;
                                Plugin.Log.LogInfo($"[Interactables] door '{d.name}': enabled Animator on '{an.gameObject.name}' DISABLED (transform stomper)");
                            }
                    }
                    if (stripped > 0)
                        Plugin.Log.LogInfo($"[Interactables] {stripped} door animator(s) stripped");

                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Interactables] heal failed: {e.Message}"); }
            }
        }

        // the door no-motion verdict machine: on every state change toward Open/Shut,
        // sample CurrentAngle now and 1.5s later. angle unchanged = the swing never ran
        // (state machine problem); angle advanced but the door looks frozen = something
        // rewrites the transform after us (animator/batching). one raid of pressing
        // Open answers which world we're in.
        [HarmonyPatch(typeof(WorldInteractiveObject), nameof(WorldInteractiveObject.SetDoorState))]
        internal static class Patch_DoorProbe
        {
            [HarmonyPostfix]
            private static void Postfix(WorldInteractiveObject __instance, EDoorState state)
            {
                if (!TerminalGate.On || !(__instance is Door)) return;
                if (state != EDoorState.Open && state != EDoorState.Shut) return;
                try
                {
                    var host = new GameObject("Terminal_DoorProbe").AddComponent<DoorProbeHost>();
                    host.Door = __instance;
                    host.Angle0 = __instance.CurrentAngle;
                    host.Target = state;
                    host.Rot0 = __instance.transform.localRotation;
                }
                catch { }
            }
        }

        internal class DoorProbeHost : MonoBehaviour
        {
            internal WorldInteractiveObject Door;
            internal float Angle0;
            internal Quaternion Rot0;
            internal EDoorState Target;
            private float _t;

            private void Update()
            {
                _t += Time.deltaTime;
                if (_t < 1.5f) return;
                try
                {
                    if (Door != null)
                    {
                        float a1 = Door.CurrentAngle;
                        float rotDelta = Quaternion.Angle(Rot0, Door.transform.localRotation);
                        Plugin.Log.LogWarning($"[DoorProbe] '{Door.name}' -> {Target}: angle {Angle0:0.#} -> {a1:0.#} "
                            + $"(open={Door.OpenAngle:0.#}) transformDelta={rotDelta:0.#}deg state={Door.DoorState} "
                            + (Mathf.Approximately(a1, Angle0)
                                ? "VERDICT: swing never ran (state machine)"
                                : (rotDelta < 1f ? "VERDICT: angle moved, transform stomped" : "moved OK")));
                    }
                }
                catch { }
                Destroy(gameObject);
            }
        }
    }
}
