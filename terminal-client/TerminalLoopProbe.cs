using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Manimal.Terminal
{
    // POSTLATEUPDATE SUBSYSTEM PROFILER (2026-08-21).
    //
    // the chop lives in PostLateUpdate: 39-42ms there while every other frame
    // phase stays at 1-6ms. that phase is a stack of native Unity systems —
    // UpdateAllRenderers, UpdateAllSkinnedMeshes, PlayerUpdateCanvases,
    // ParticleSystemBeginUpdateAll, PhysicsSkinnedClothBeginUpdate,
    // EnlightenRuntimeUpdate — and no amount of config bisecting can tell them
    // apart because none of them is ours.
    //
    // Unity's PlayerLoop API lets us interleave managed marker systems between
    // those native children. each marker stamps a timestamp; the deltas are the
    // per-subsystem cost. that names the exact engine system burning the frame.
    //
    // opt-in via config and fully restored on teardown — this rewrites the
    // engine's update loop, which is not something to leave on by default.
    internal static class TerminalLoopProbe
    {
        private static bool _installed;
        private static PlayerLoopSystem _original;
        private static long[] _stamps;
        private static double[] _cost;     // EMA ms per child
        private static double[] _peak;
        private static string[] _names;
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        // distinct closed generic types give each marker its own identity without
        // hand-writing 30 empty structs
        private struct Mark<T> { }
        private static readonly Type[] MarkTypes =
        {
            typeof(Mark<byte>), typeof(Mark<sbyte>), typeof(Mark<short>), typeof(Mark<ushort>),
            typeof(Mark<int>), typeof(Mark<uint>), typeof(Mark<long>), typeof(Mark<ulong>),
            typeof(Mark<float>), typeof(Mark<double>), typeof(Mark<decimal>), typeof(Mark<char>),
            typeof(Mark<bool>), typeof(Mark<string>), typeof(Mark<object>), typeof(Mark<Vector2>),
            typeof(Mark<Vector3>), typeof(Mark<Vector4>), typeof(Mark<Quaternion>), typeof(Mark<Color>),
            typeof(Mark<Rect>), typeof(Mark<Bounds>), typeof(Mark<Matrix4x4>), typeof(Mark<Ray>),
            typeof(Mark<Plane>), typeof(Mark<Color32>), typeof(Mark<Vector2Int>), typeof(Mark<Vector3Int>),
            typeof(Mark<RectInt>), typeof(Mark<BoundsInt>), typeof(Mark<LayerMask>), typeof(Mark<Keyframe>),
        };

        private static float _nextVerify;
        private static int _reinstalls;

        internal static void Tick()
        {
            // This rewrites Unity's global player loop and exists only for profiler
            // raids. Older configs persisted the original test-build default=true;
            // require DevMode as an additional explicit opt-in so normal players do
            // not carry the instrumentation after updating.
            bool want = TerminalGate.On && Plugin.DevMode.Value && Plugin.ProfilePlayerLoop.Value;
            if (want && !_installed) { Install(); return; }
            if (!want && _installed) { Restore(); return; }
            if (!want || !_installed) return;

            // OUR MARKERS GET EVICTED. the first run came back with frozen numbers —
            // identical to the decimal every heartbeat, summing to 2.4ms against a
            // 24-34ms postLate — which is what a dead delegate looks like: the EMA
            // keeps its last value forever. something re-sets the player loop after
            // we install (scene load / another mod), so verify and re-arm.
            if (Time.realtimeSinceStartup < _nextVerify) return;
            _nextVerify = Time.realtimeSinceStartup + 2f;
            if (MarkersPresent()) return;
            _reinstalls++;
            _installed = false;
            Install();
            if (_reinstalls <= 5)
                Plugin.Log.LogWarning($"[LoopProbe] markers had been evicted from the player loop — re-armed (#{_reinstalls}). "
                    + "readings before this point were stale.");
        }

        private static bool MarkersPresent()
        {
            try
            {
                var root = PlayerLoop.GetCurrentPlayerLoop();
                var top = root.subSystemList;
                if (top == null) return false;
                for (int i = 0; i < top.Length; i++)
                {
                    if (top[i].type != typeof(PostLateUpdate)) continue;
                    var kids = top[i].subSystemList;
                    if (kids == null) return false;
                    for (int k = 0; k < kids.Length; k++)
                        if (kids[k].type == MarkTypes[0]) return true;
                    return false;
                }
            }
            catch { }
            return false;
        }

        // measured total, so the heartbeat can show whether the instrumentation
        // actually accounts for the phase it claims to be measuring
        // instantaneous ms for one named subsystem this frame — the EMA hides the
        // waveform, and the waveform is the thing we've never actually looked at
        internal static double InstantOf(string name)
        {
            if (!_installed || _names == null) return 0;
            for (int i = 0; i < _names.Length; i++)
                if (_names[i] == name)
                {
                    double ms = (_stamps[i + 1] - _stamps[i]) * TickMs;
                    // same sanity gate FoldFrame uses: if the loop was evicted
                    // mid-frame one of these stamps is stale, and the subtraction
                    // is meaningless rather than merely wrong
                    return (ms < 0 || ms > 5000) ? 0 : ms;
                }
            return 0;
        }

        // raw end-stamp of a named subsystem, so callers can measure a window that
        // starts elsewhere. used to split FinishFrameRendering into "camera work"
        // vs "everything after the camera finished" — image effects (OnRenderImage
        // runs AFTER onPostRender) and the GPU present-wait, which is where ~40ms
        // is currently hiding.
        internal static long EndStampOf(string name)
        {
            if (!_installed || _names == null) return 0;
            for (int i = 0; i < _names.Length; i++)
                if (_names[i] == name) return _stamps[i + 1];
            return 0;
        }

        internal static double MsBetween(long a, long b)
        {
            if (a == 0 || b == 0) return 0;
            double ms = (b - a) * TickMs;
            return (ms < 0 || ms > 5000) ? 0 : ms;
        }

        internal static double MeasuredSum()
        {
            if (!_installed || _cost == null) return 0;
            double s = 0;
            for (int i = 0; i < _cost.Length; i++) s += _cost[i];
            return s;
        }

        private static void Install()
        {
            try
            {
                _original = PlayerLoop.GetCurrentPlayerLoop();
                var root = PlayerLoop.GetCurrentPlayerLoop();
                var top = root.subSystemList;
                if (top == null) { Plugin.Log.LogWarning("[LoopProbe] player loop has no subsystems"); return; }

                for (int i = 0; i < top.Length; i++)
                {
                    if (top[i].type != typeof(PostLateUpdate)) continue;
                    var kids = top[i].subSystemList;
                    if (kids == null || kids.Length == 0) break;

                    int n = Mathf.Min(kids.Length, MarkTypes.Length - 1);
                    _stamps = new long[n + 1];
                    _cost = new double[n];
                    _peak = new double[n];
                    _names = new string[n];

                    var woven = new List<PlayerLoopSystem>(kids.Length + n + 1);
                    for (int k = 0; k < n; k++)
                    {
                        _names[k] = kids[k].type != null ? kids[k].type.Name : "native";
                        int idx = k;                     // capture
                        woven.Add(new PlayerLoopSystem
                        {
                            type = MarkTypes[k],
                            updateDelegate = () => _stamps[idx] = Stopwatch.GetTimestamp(),
                        });
                        woven.Add(kids[k]);
                    }
                    // trailing marker closes the last child and folds the frame
                    woven.Add(new PlayerLoopSystem
                    {
                        type = MarkTypes[n],
                        updateDelegate = FoldFrame,
                    });
                    // anything past our marker budget rides along untouched
                    for (int k = n; k < kids.Length; k++) woven.Add(kids[k]);

                    top[i].subSystemList = woven.ToArray();
                    root.subSystemList = top;
                    PlayerLoop.SetPlayerLoop(root);
                    _installed = true;
                    if (_reinstalls == 0)
                    {
                        // log what we're actually measuring — if the expensive system
                        // isn't in this list, the instrument can't see it and no
                        // amount of staring at the numbers will help
                        var list = new System.Text.StringBuilder();
                        for (int k = 0; k < n; k++) list.Append("\n    ").Append(k).Append(". ").Append(_names[k]);
                        if (kids.Length > n)
                            list.Append($"\n    (+{kids.Length - n} more beyond the marker budget — NOT measured)");
                        Plugin.Log.LogWarning($"[LoopProbe] instrumented {n}/{kids.Length} PostLateUpdate subsystem(s):{list}");
                    }
                    return;
                }
                Plugin.Log.LogWarning("[LoopProbe] PostLateUpdate not found in the player loop");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LoopProbe] install failed: {e.Message}");
                try { PlayerLoop.SetPlayerLoop(_original); } catch { }
                _installed = false;
            }
        }

        private static void FoldFrame()
        {
            _stamps[_stamps.Length - 1] = Stopwatch.GetTimestamp();
            for (int i = 0; i < _cost.Length; i++)
            {
                double ms = (_stamps[i + 1] - _stamps[i]) * TickMs;
                if (ms < 0 || ms > 5000) continue;      // stamp not written this frame
                _cost[i] = _cost[i] * 0.95 + ms * 0.05;
                if (ms > _peak[i]) _peak[i] = ms;
            }
        }

        internal static void Restore()
        {
            if (!_installed) return;
            try { PlayerLoop.SetPlayerLoop(_original); } catch { }
            _installed = false;
            Plugin.Log.LogWarning("[LoopProbe] player loop restored");
        }

        internal static void ResetForRaid()
        {
            if (_cost != null) Array.Clear(_cost, 0, _cost.Length);
            if (_peak != null) Array.Clear(_peak, 0, _peak.Length);
        }

        // top N PostLateUpdate subsystems by EMA cost
        internal static string TopN(int n)
        {
            if (!_installed || _cost == null) return "";
            var order = new List<int>(_cost.Length);
            for (int i = 0; i < _cost.Length; i++) order.Add(i);
            order.Sort((a, b) => _cost[b].CompareTo(_cost[a]));
            var sb = new System.Text.StringBuilder();
            int take = Mathf.Min(n, order.Count);
            for (int i = 0; i < take; i++)
            {
                int k = order[i];
                if (_cost[k] < 0.05) break;
                if (i > 0) sb.Append(' ');
                sb.Append(_names[k]).Append('=').Append(_cost[k].ToString("F1"))
                  .Append('/').Append(_peak[k].ToString("F0")).Append('p');
            }
            return sb.ToString();
        }
    }
}
