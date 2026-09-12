using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.Terminal
{
    // The 2026-09-06 raid spent 45-67ms/frame in GoToPosition(IAICorePointLink).
    // Observe its results and constituent nav queries without changing routing,
    // bot cadence, path status or movement. At most four reports per ten seconds.
    internal static class TerminalPathDiagnostics
    {
        internal struct Call
        {
            internal long Started;
            internal Vector3 From;
            internal Vector3 To;
        }
        private sealed class Repeat
        {
            internal Vector3 From, To;
            internal int Calls, Failures;
            internal float NextLog;
        }
        private static readonly Dictionary<BotPathFinderCorePoints, Repeat> Repeats = new Dictionary<BotPathFinderCorePoints, Repeat>();
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
        private static float _window;
        private static int _reports;
        [ThreadStatic] private static int _scopeDepth;
        [ThreadStatic] private static int _queries;
        [ThreadStatic] private static int _failedQueries;
        [ThreadStatic] private static long _queryTicks;

        internal static void ResetForRaid()
        {
            Repeats.Clear();
            _window = 0f;
            _reports = 0;
            _scopeDepth = _queries = _failedQueries = 0;
            _queryTicks = 0;
        }

        [HarmonyPatch(typeof(BotPathFinderCorePoints), nameof(BotPathFinderCorePoints.GoToPosition),
            new[] { typeof(IAICorePointLink), typeof(bool), typeof(float), typeof(bool) })]
        internal static class Patch_CoverRoute
        {
            [HarmonyPrefix]
            private static void Prefix(BotPathFinderCorePoints __instance, IAICorePointLink target, out Call __state)
            {
                __state = default;
                if (!TerminalGate.On || target == null || __instance._owner == null) return;
                try
                {
                    __state.From = __instance._owner.Position;
                    __state.To = target.Position;
                    __state.Started = Stopwatch.GetTimestamp();
                    if (_scopeDepth++ == 0) { _queries = _failedQueries = 0; _queryTicks = 0; }
                }
                catch { __state = default; }
            }

            [HarmonyFinalizer]
            private static void Finalizer(BotPathFinderCorePoints __instance, IAICorePointLink target,
                NavMeshPathStatus __result, Exception __exception, Call __state)
            {
                if (__state.Started == 0) return;
                double ms = (Stopwatch.GetTimestamp() - __state.Started) * TickMs;
                _scopeDepth = Math.Max(0, _scopeDepth - 1);
                if (ms < 5.0 || _scopeDepth != 0) return;
                try
                {
                    if (!Repeats.TryGetValue(__instance, out var repeat))
                        Repeats[__instance] = repeat = new Repeat();
                    if ((__state.From - repeat.From).sqrMagnitude > 0.01f || (__state.To - repeat.To).sqrMagnitude > 0.01f)
                    {
                        repeat.Calls = repeat.Failures = 0;
                        repeat.From = __state.From;
                        repeat.To = __state.To;
                    }
                    repeat.Calls++;
                    if (__exception != null || __result != NavMeshPathStatus.PathComplete) repeat.Failures++;
                    float now = Time.realtimeSinceStartup;
                    if (now >= _window) { _window = now + 10f; _reports = 0; }
                    if (now < repeat.NextLog || _reports >= 4) return;
                    repeat.NextLog = now + 10f;
                    _reports++;
                    // Small local samples distinguish off-mesh authoring from an
                    // unreachable connection; never add another full path search.
                    bool fromOnMesh = NavMesh.SamplePosition(__state.From, out var fromHit, 1.5f, NavMesh.AllAreas);
                    bool toOnMesh = NavMesh.SamplePosition(__state.To, out var toHit, 1.5f, NavMesh.AllAreas);
                    var bot = __instance._owner;
                    var point = target as CustomNavigationPoint;
                    Plugin.Log.LogWarning($"[PathPerf] bot={bot.Id} role={bot.Profile?.Info?.Settings?.Role} "
                        + $"result={(__exception == null ? __result.ToString() : __exception.GetType().Name)} cost={ms:F1}ms "
                        + $"navQueries={_queries} navFailed={_failedQueries} navTime={_queryTicks * TickMs:F1}ms "
                        + $"repeatedSlowCalls={repeat.Calls} failedCalls={repeat.Failures} "
                        + $"from={__state.From:F2} to={__state.To:F2} coverId={(point != null ? point.Id : -1)} "
                        + $"fromMeshDelta={(fromOnMesh ? Vector3.Distance(__state.From, fromHit.position).ToString("F3") : "missing")} "
                        + $"toMeshDelta={(toOnMesh ? Vector3.Distance(__state.To, toHit.position).ToString("F3") : "missing")}");
                }
                catch { /* Diagnostic failure must not affect the bot or hide its original exception. */ }
            }
        }

        [HarmonyPatch(typeof(BotPathFinderCorePoints), nameof(BotPathFinderCorePoints.FindPath))]
        internal static class Patch_NavQuery
        {
            [HarmonyPrefix]
            private static void Prefix(out long __state) => __state = _scopeDepth > 0 ? Stopwatch.GetTimestamp() : 0;

            [HarmonyFinalizer]
            private static void Finalizer(bool __result, Exception __exception, long __state)
            {
                if (__state == 0) return;
                _queries++;
                if (__exception != null || !__result) _failedQueries++;
                _queryTicks += Stopwatch.GetTimestamp() - __state;
            }
        }
    }
}
