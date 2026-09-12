using System;
using System.Reflection;
using HarmonyLib;

namespace Manimal.Terminal
{
    // reflection-based finalizer guard for the BSG core method observed on the
    // 2026-08-20 Terminal raid-start crash (NullReferenceException, confirmed in the
    // client log: Backend.method_3[T] throws, EFT.EftClientBackendSession.LocalRaidStarted propagates it
    // unhandled up into TarkovApplication's async raid-start chain — no matching
    // server-side error at the same timestamp):
    //   EFT.EftClientBackendSession.LocalRaidStarted
    // Backend.method_3 is NOT patched here on purpose: it's an open generic method
    // (`method_3<T>`) and Harmony cannot patch an open generic method definition — a
    // real build attempt to do so failed loud (HarmonyException / IL Compile Error,
    // "Specified method is not supported"), which is expected and not a bug to chase.
    // Patching LocalRaidStarted alone is sufficient: a Harmony finalizer catches
    // exceptions from anywhere in the patched method's call graph, including from
    // method_3 underneath it.
    //
    // Both type names are obfuscated and WILL drift across game/SPT builds — resolved
    // by simple type/method name via reflection rather than a compile-time reference.
    // If a name can't be resolved, TryPatch logs it loudly at plugin startup instead of
    // silently never patching (see CLAUDE.md: unlisted/unresolved patches must not fail
    // silently) and Terminal stays unguarded until the name is fixed.
    internal static class TerminalCrashGuard
    {
        private const string TargetTypeName = "EFT.EftClientBackendSession";
        private const string TargetMethodName = "LocalRaidStarted";

        internal static void TryPatch(Harmony h)
        {
            var type = AccessTools.TypeByName(TargetTypeName);
            if (type == null)
            {
                Plugin.Log.LogError(
                    $"[CrashGuard] could not resolve type '{TargetTypeName}' — obfuscated name likely " +
                    "drifted on this game build. This crash site is UNGUARDED until fixed.");
                return;
            }

            var method = AccessTools.Method(type, TargetMethodName);
            if (method == null)
            {
                Plugin.Log.LogError(
                    $"[CrashGuard] resolved type '{TargetTypeName}' but method '{TargetMethodName}' not " +
                    "found on it (overload/rename?). This crash site is UNGUARDED until fixed.");
                return;
            }

            h.Patch(method, finalizer: new HarmonyMethod(typeof(TerminalCrashGuard), nameof(Finalizer)));
            Plugin.Log.LogInfo($"[CrashGuard] guarding {TargetTypeName}.{TargetMethodName} on Terminal");
        }

        // gated by TerminalGate.PendingLocationId, NOT TerminalGate.On's live GameWorld
        // fallback: LocalRaidStarted fires DURING MATCHING, before GameWorld exists, so
        // a live lookup is always false at the moment this fires (the
        // transit-gate-blindness trap already recorded in this repo's notes). Reuses
        // TerminalGate's own capture (TerminalGate.Patch_CaptureLocationId, armed
        // earlier in Plugin.cs's patch list) rather than a second smethod_6 prefix —
        // one gate, one source of truth.
        private static Exception Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null)
                return null;

            if (!TerminalGate.On)
                return __exception;

            Plugin.Log.LogError(
                $"[CrashGuard] swallowed exception in {__originalMethod?.DeclaringType?.FullName}." +
                $"{__originalMethod?.Name} during Terminal raid start: {__exception}");
            return null;
        }
    }
}
