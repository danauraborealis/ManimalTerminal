using System;
using HarmonyLib;

namespace Manimal.Terminal
{
    // LOCKABLE DOORS, OFF ON THIS MAP — icebreaker's shim, ported (user 2026-08-11:
    // "its cause of lockable doors lmao. i forgot we made a patch that disables it for
    // icebreaker but not for terminal yet").
    //
    // the failure mode here was nastier than icebreaker's. Jehree.LockableDoors hooks
    // EFT.InteractionContextHelper.GetAvailableActions with a POSTFIX, and on terminal it THREW:
    //
    //   LockableDoors.Models.CustomInteraction.get_ActionsTypesClass
    //   LockableDoors.Components.DoorLock.AddUninitializedLockInteractionsToActionList
    //   LockableDoors.Patches.GetAvailableActionsPatch.PatchPostfix
    //   -> GamePlayerOwner.InteractionsChangedHandler
    //
    // an exception there takes the WHOLE action list down before it reaches the UI, so
    // the door offers nothing at all — while the door itself probes perfectly healthy
    // (Shut, unlocked, Interactive layer, Operatable, engine holding it as the current
    // InteractableObject, all five FindInteractable gates green). that is exactly the
    // "fake door" symptom, and it costs a raid every time it's misread as our bug.
    //
    // its raid-start sweep is equally wrong for us: it force-shuts every operatable door
    // and bolts a DoorLock on from a server list, but terminal's doors ARE progression —
    // the cutscene gate, the armory key door and the keycard rooms own their own state.
    //
    // suppression, not exception-swallowing, and gated on TerminalGate so the mod stays
    // fully intact on every other map. all reflection: it's optional and absent from
    // most installs, so nothing here may hard-reference it.
    internal static class TerminalLockableDoorsOff
    {
        private const string PluginGuid = "Jehree.LockableDoors";
        private static bool _tried;

        private static readonly (string type, string method)[] Targets =
        {
            ("LockableDoors.Patches.GameStartedPatch", "PatchPrefix"),          // raid-start shut+lock sweep
            ("LockableDoors.Patches.GetAvailableActionsPatch", "PatchPostfix"), // the thrower above
            // 2026-08-15: skipping GameStartedPatch means LDSession never initializes on
            // terminal — so its raid-END hook NREs on LDSession.Instance inside
            // LocalRaidEnded and the death screen becomes an empty error popup. gate the
            // pair together.
            ("LockableDoors.Patches.GameEndedPatch", "Postfix"),
        };

        internal static void TryPatch(Harmony h)
        {
            if (_tried) return;
            _tried = true;
            try
            {
                // DON'T gate on the plugin GUID (2026-08-11: guessed "Jehree.LockableDoors",
                // BepInEx actually loads it as "LockableDoors 2.0.0", so the shim silently
                // never attached and the doors stayed dead). the TYPES are the honest test —
                // if they resolve, the mod is here, whatever it calls itself.
                var skip = new HarmonyMethod(AccessTools.Method(typeof(TerminalLockableDoorsOff), nameof(SkipOnTerminal)));
                var swallow = new HarmonyMethod(AccessTools.Method(typeof(TerminalLockableDoorsOff), nameof(SwallowOnTerminal)));
                int done = 0;
                foreach (var (typeName, methodName) in Targets)
                {
                    try
                    {
                        var t = AccessTools.TypeByName(typeName);
                        var m = t != null ? AccessTools.Method(t, methodName) : null;
                        if (m == null)
                        {
                            Plugin.Log.LogWarning($"[LockableDoors] {typeName}.{methodName} not found — mod updated? "
                                + "terminal's doors may lose their prompts until this shim is refreshed");
                            continue;
                        }
                        // prefix skips it on terminal; finalizer is the backstop — its
                        // KeyNotFoundException('terminal') must never escape into
                        // InteractionsChangedHandler again, or EVERY prompt on the map dies
                        h.Patch(m, prefix: skip, finalizer: swallow);
                        done++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"[LockableDoors] couldn't neutralise {typeName}.{methodName}: {e.Message}");
                    }
                }
                if (done > 0)
                    Plugin.Log.LogWarning($"[LockableDoors] detected — suppressed on terminal ({done} hook(s) gated); "
                        + "the mod stays fully active on every other map.");
                else
                    Plugin.Log.LogDebug("[LockableDoors] not installed (no patch types resolved) — nothing to gate");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[LockableDoors] shim failed: {e.Message}"); }
        }

        // false = skip the original. TerminalGate is the ONLY condition. the scene check
        // rides along because the raid-start sweep fires before the location id lands.
        private static bool SkipOnTerminal() => !(TerminalGate.On || TerminalLoaded.Check());

        // the mod keys its settings by MAP NAME and 'terminal' isn't in that dictionary,
        // so it throws KeyNotFoundException from GetLockedDoorLimit. swallow it here
        // rather than let it take the whole action list — and say so once, since a
        // silently-eaten exception is how this cost several raids.
        private static bool _swallowLogged;

        private static Exception SwallowOnTerminal(Exception __exception)
        {
            if (__exception == null) return null;
            if (!(TerminalGate.On || TerminalLoaded.Check())) return __exception; // other maps keep their errors
            if (!_swallowLogged)
            {
                _swallowLogged = true;
                Plugin.Log.LogWarning("[LockableDoors] swallowed its exception on terminal "
                    + $"({__exception.GetType().Name}: {__exception.Message}) — it keys settings by map name and has no "
                    + "'terminal' entry. left unhandled this empties EVERY interaction prompt on the map.");
            }
            return null;
        }
    }
}
