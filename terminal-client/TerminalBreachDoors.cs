using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // RANDOM BREACHABLE DOOR (user design 2026-08-21).
    //
    // the OilStorage courtyard ships a set of doors authored Locked with both
    // breach flags off — no key, no breach, no way through. that's a dead end for
    // the player wherever the route needs one of them. rather than unlock the lot
    // (which erases the pressure) or hand-pick one (same route every raid), roll
    // ONE of them at raid start and let that one be breached.
    //
    // the door stays Locked — breaching is the point. we only flip the two
    // authored breach flags the inspector calls "Can Be Breached" / "Can Interact
    // With Breach"; those identifiers are confirmed present in 4.0's
    // Assembly-CSharp, and we resolve field-or-property and log which shape we
    // found rather than assuming.
    internal static class TerminalBreachDoors
    {
        // these are door IDs (WorldInteractiveObject.Id), not GameObject names —
        // every one of these objects is called 'Outside_door_fence_01_R_240-110'
        // in the scene, with (1)/(2) suffixes, so only the Id distinguishes them.
        // explicit list, NOT a numeric range: the set skips 00007-00011 but
        // includes 00012.
        private static readonly string[] Candidates =
        {
            "door_Terminal_Area_07_OilStorage_Courtyard_00001",
            "door_Terminal_Area_07_OilStorage_Courtyard_00002",
            "door_Terminal_Area_07_OilStorage_Courtyard_00003",
            "door_Terminal_Area_07_OilStorage_Courtyard_00004",
            "door_Terminal_Area_07_OilStorage_Courtyard_00005",
            "door_Terminal_Area_07_OilStorage_Courtyard_00006",
            "door_Terminal_Area_07_OilStorage_Courtyard_00012",
        };

        private static MemberSetter _canBeBreached;
        private static MemberSetter _canInteractWithBreach;
        private static bool _resolved;

        // tiny field-or-property wrapper — BSG moves things between the two across
        // versions and a hard assumption here fails silently (flag never set, door
        // still unbreachable, no error)
        private class MemberSetter
        {
            private readonly FieldInfo _f;
            private readonly PropertyInfo _p;
            internal readonly string Shape;

            internal MemberSetter(Type t, string name)
            {
                const BindingFlags B = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _f = t.GetField(name, B);
                if (_f == null) _p = t.GetProperty(name, B);
                Shape = _f != null ? "field" : _p != null ? "property" : "MISSING";
            }

            internal bool Ok => _f != null || (_p != null && _p.CanWrite);

            internal void Set(object target, bool v)
            {
                if (_f != null) _f.SetValue(target, v);
                else _p?.SetValue(target, v, null);
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            // the flags live on Door or its WorldInteractiveObject base — try the
            // concrete type first, fall back to the base
            foreach (var t in new[] { typeof(Door), typeof(WorldInteractiveObject) })
            {
                var a = new MemberSetter(t, "CanBeBreached");
                var b = new MemberSetter(t, "CanInteractWithBreach");
                if (a.Ok)
                {
                    _canBeBreached = a;
                    _canInteractWithBreach = b.Ok ? b : null;
                    Plugin.Log.LogInfo($"[BreachDoor] flags resolved on {t.Name}: CanBeBreached={a.Shape}, "
                        + $"CanInteractWithBreach={(b.Ok ? b.Shape : "MISSING")}");
                    return;
                }
            }
            Plugin.Log.LogWarning("[BreachDoor] could not resolve CanBeBreached on Door or WorldInteractiveObject — "
                + "breach roll disabled this session (BSG renamed the member?)");
        }

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_RollBreachableDoor
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On) return;
                int want = Plugin.BreachableDoors.Value;
                if (want <= 0) return;
                try { Roll(want); }
                catch (Exception e) { Plugin.Log.LogWarning($"[BreachDoor] roll failed: {e.Message}"); }
            }
        }

        // Id / DoorState read defensively — these live at different levels of the
        // WorldInteractiveObject hierarchy across subclasses, and a throw here would
        // kill the whole roll
        private static PropertyInfo _idProp; private static FieldInfo _idField; private static bool _idResolved;
        internal static string SafeId(Component c)
        {
            try
            {
                if (!_idResolved)
                {
                    _idResolved = true;
                    const BindingFlags B = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    _idProp = typeof(WorldInteractiveObject).GetProperty("Id", B);
                    if (_idProp == null) _idField = typeof(WorldInteractiveObject).GetField("Id", B);
                }
                return (_idProp?.GetValue(c, null) ?? _idField?.GetValue(c))?.ToString();
            }
            catch { return null; }
        }

        private static string SafeState(Component c)
        {
            try
            {
                var p = c.GetType().GetProperty("DoorState",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return p?.GetValue(c, null)?.ToString() ?? "?";
            }
            catch { return "?"; }
        }

        private static void Roll(int want)
        {
            Resolve();
            if (_canBeBreached == null) return;

            // match on the door's Id, NOT the GameObject name — the authored list is
            // door IDs (door_Terminal_Area_07_...), while the scene objects are all
            // named 'Outside_door_fence_01_R_240-110' with (1)/(2) dupe suffixes, so
            // a name match finds nothing. scan WorldInteractiveObject rather than
            // Door too, in case any of these are a subclass.
            var wanted = new HashSet<string>(Candidates, StringComparer.OrdinalIgnoreCase);
            var found = new List<WorldInteractiveObject>();
            var nearMiss = new System.Text.StringBuilder();
            int nearMissCount = 0;

            foreach (var d in UnityEngine.Object.FindObjectsOfType<WorldInteractiveObject>(true))
            {
                if (d == null) continue;
                var id = SafeId(d);
                if (!string.IsNullOrEmpty(id) && wanted.Contains(id)) { found.Add(d); continue; }
                // same-area doors we didn't match — log their real IDs so the list
                // can be corrected from data rather than another guess
                if (!string.IsNullOrEmpty(id)
                    && id.IndexOf("OilStorage", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    nearMissCount++;
                    if (nearMissCount <= 30)
                        nearMiss.Append($"\n    [{d.GetType().Name}] id='{id}' go='{d.gameObject.name}' state={SafeState(d)}");
                }
            }

            if (found.Count == 0)
            {
                Plugin.Log.LogWarning($"[BreachDoor] none of the {Candidates.Length} candidate ID(s) matched. "
                    + $"{nearMissCount} OilStorage door ID(s) in scene:{nearMiss}");
                return;
            }
            if (found.Count < Candidates.Length)
                Plugin.Log.LogInfo($"[BreachDoor] {found.Count}/{Candidates.Length} candidate doors present in scene");

            // The rebuilt scene authored these grate doors with the correct
            // door_grate_hit impact clip, but their independent BreachSound field
            // fell back to door_kick_break1 (the wooden-door break).  Reuse the
            // door's own grate impact for the breach: it is already bundled, routed
            // through Tarkov's InteractiveObjects mixer, and is the correct material
            // sound for this chain-fence prefab.  Repair every candidate, not merely
            // this raid's random winner, so increasing BreachableDoors later cannot
            // expose the bad fallback again.
            int audioFixed = 0;
            foreach (var interactive in found)
            {
                if (!(interactive is Door door) || door.HitClip == null) continue;
                if (door.HitClip.name.IndexOf("grate", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (door.BreachSound == door.HitClip) continue;

                string oldName = door.BreachSound != null ? door.BreachSound.name : "<null>";
                door.BreachSound = door.HitClip;
                audioFixed++;
                Plugin.Log.LogInfo($"[BreachDoor] grate breach audio repaired on id='{SafeId(door)}': "
                    + $"{oldName} -> {door.BreachSound.name}");
            }
            if (audioFixed > 0)
                Plugin.Log.LogWarning($"[BreachDoor] repaired chain-fence breach audio on {audioFixed} candidate door(s)");

            // partial Fisher-Yates so multiple picks are distinct
            int take = Mathf.Min(want, found.Count);
            for (int i = 0; i < take; i++)
            {
                int j = UnityEngine.Random.Range(i, found.Count);
                var tmp = found[i]; found[i] = found[j]; found[j] = tmp;
            }

            for (int i = 0; i < take; i++)
            {
                var d = found[i];
                _canBeBreached.Set(d, true);
                _canInteractWithBreach?.Set(d, true);
                Plugin.Log.LogWarning($"[BreachDoor] id='{SafeId(d)}' [{d.GetType().Name}] go='{d.gameObject.name}' "
                    + $"is BREACHABLE this raid (state={SafeState(d)}, {found.Count} candidate(s) in the pool)");
                DumpBreachState(d, "post-roll");
            }
        }

        // full breach-related state of a door. setting CanBeBreached made the
        // BREACH action APPEAR but greyed out, so a second condition is gating it —
        // the assembly carries IsBreachAngle / BreachSuccessRoll /
        // BreachRolledForCurrentDoor, any of which could be it. dump every
        // breach-ish member by reflection rather than guessing which.
        internal static void DumpBreachState(Component door, string tag)
        {
            try
            {
                const BindingFlags B = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var sb = new System.Text.StringBuilder();
                for (var t = door.GetType(); t != null && t != typeof(Component); t = t.BaseType)
                {
                    foreach (var f in t.GetFields(B))
                    {
                        var n = f.Name.ToLowerInvariant();
                        if (!n.Contains("breach") && !n.Contains("operat") && !n.Contains("broken")
                            && !n.Contains("lock") && !n.Contains("keyid") && !n.Contains("doorstate")) continue;
                        object v = null;
                        try { v = f.GetValue(door); } catch { }
                        sb.Append($"\n    [f] {t.Name}.{f.Name} = {v}");
                    }
                    foreach (var p in t.GetProperties(B))
                    {
                        var n = p.Name.ToLowerInvariant();
                        if (!n.Contains("breach") && !n.Contains("operat") && !n.Contains("broken")
                            && !n.Contains("lock") && !n.Contains("keyid") && !n.Contains("doorstate")) continue;
                        if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                        object v = null;
                        try { v = p.GetValue(door, null); } catch (Exception e) { v = "<throw: " + e.GetBaseException().Message + ">"; }
                        sb.Append($"\n    [p] {t.Name}.{p.Name} = {v}");
                    }
                }
                Plugin.Log.LogWarning($"[BreachDoor][{tag}] '{door.gameObject.name}' breach state:{sb}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[BreachDoor] state dump failed: {e.Message}"); }
        }
    }

    // WHY IS BREACH GREYED OUT — postfix whatever EFT.InteractionContextHelper method builds a
    // door's action list, and log each action's Disabled flag alongside the door's
    // breach state. resolved by SIGNATURE (a static method taking a Door) rather
    // than by smethod_N: the obfuscated numbers shift between SPT builds, and a
    // name-only AccessTools lookup would happily bind the wrong overload.
    [HarmonyPatch]
    internal static class Patch_BreachActionProbe
    {
        private static float _lastLog;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var t = AccessTools.TypeByName("EFT.InteractionContextHelper");
            if (t == null) yield break;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                var ps = m.GetParameters();
                bool takesDoor = false;
                foreach (var p in ps)
                    if (typeof(Door).IsAssignableFrom(p.ParameterType)) { takesDoor = true; break; }
                if (takesDoor) yield return m;
            }
        }

        [HarmonyPostfix]
        private static void Postfix(object __result, MethodBase __originalMethod, object[] __args)
        {
            try
            {
                if (!TerminalGate.On || !Plugin.BreachDoorProbe.Value) return;
                if (Time.unscaledTime - _lastLog < 2f) return;   // it fires every frame you look at a door

                Door door = null;
                foreach (var a in __args) if (a is Door d) { door = d; break; }
                if (door == null) return;
                _lastLog = Time.unscaledTime;

                var sb = new System.Text.StringBuilder();
                var actions = AccessTools.Field(__result?.GetType(), "Actions")?.GetValue(__result)
                              as System.Collections.IEnumerable;
                if (actions != null)
                {
                    foreach (var a in actions)
                    {
                        if (a == null) continue;
                        var at = a.GetType();
                        string name = AccessTools.Field(at, "Name")?.GetValue(a)?.ToString() ?? "?";
                        object dis = AccessTools.Field(at, "Disabled")?.GetValue(a);
                        sb.Append($"\n    action '{name}' Disabled={dis}");
                        // some builds carry a reason string alongside Disabled
                        foreach (var f in at.GetFields(BindingFlags.Public | BindingFlags.Instance))
                            if (f.FieldType == typeof(string) && f.Name != "Name")
                                sb.Append($"  {f.Name}='{f.GetValue(a)}'");
                    }
                }
                else sb.Append("\n    <no Actions list on the result>");

                Plugin.Log.LogWarning($"[BreachDoor][probe] {__originalMethod.Name} on '{door.gameObject.name}':{sb}");
                TerminalBreachDoors.DumpBreachState(door, "probe");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[BreachDoor] probe threw: {e.Message}"); }
        }
    }
}
