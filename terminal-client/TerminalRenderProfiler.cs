using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace Manimal.Terminal
{
    // RENDER-SIDE ACCOUNTING, v2 (2026-08-21).
    //
    // v1 only timed the FPS camera (onPreCull -> onPostRender on CameraRef) and
    // read a flat 3-4ms right through the chop windows. that measurement was
    // structurally blind: anything rendering OUTSIDE that one camera — reflection
    // probes re-capturing their cubemaps, BSG's DepthPhotograper, shadow-map
    // extractors, optic cameras — burns GPU without ever entering the window.
    //
    // so v2 hooks EVERY camera and reports per-frame: how many cameras rendered,
    // total submit time across all of them, and which one was worst. plus a
    // one-shot scene inventory of cameras and reflection probes, because a
    // ripped map is exactly where a probe lands on Realtime/EveryFrame after its
    // baked cubemap failed to survive the rip — which would cost GPU on a cycle
    // AND swing the weapon's specular, matching both symptoms at once.
    //
    // NOTE none of this proves GPU execution time — onPostRender fires at command
    // submission, not GPU completion. it does tell us how much work is being
    // submitted and by whom, which is the part we're currently blind to.
    internal static class TerminalRenderProfiler
    {
        private static readonly Stopwatch _sw = new Stopwatch();
        private static bool _subscribed;

        // per-frame accumulation
        private static int _frame = -1;
        private static int _camsThisFrame;
        private static double _msThisFrame;
        private static string _worstNameThisFrame;
        private static double _worstMsThisFrame;

        // published (read by PerfWatch)
        internal static double LastRenderMs;      // FPS cam only — kept for the old log field
        internal static double AvgRenderMs;
        internal static double PeakRenderMs;
        internal static int LastCamCount;         // cameras that rendered last frame
        internal static int PeakCamCount;
        internal static double LastTotalMs;       // submit time across ALL cameras
        internal static double AvgTotalMs;
        internal static double PeakTotalMs;
        internal static string WorstCamName = "";
        internal static double WorstCamMs;

        internal static void EnsureSubscribed()
        {
            if (_subscribed) return;
            _subscribed = true;
            Camera.onPreRender += OnPreRender;
            Camera.onPostRender += OnPostRender;
            Plugin.Log.LogInfo("[Render] all-camera profiler armed (per-frame camera count + total submit time)");
        }

        internal static void ResetForRaid()
        {
            LastRenderMs = AvgRenderMs = PeakRenderMs = 0;
            LastCamCount = PeakCamCount = 0;
            LastTotalMs = AvgTotalMs = PeakTotalMs = 0;
            WorstCamName = ""; WorstCamMs = 0;
            _inventoryDone = false;
            _inventoryAt = -1f;
            GpuMs = CpuMs = MainThreadMs = RenderThreadMs = 0;
            AvgGpuMs = AvgMainMs = AvgRenderThreadMs = 0;
            PeakGpuMs = 0;
            _timingWarned = false;
        }

        // ------------------------------------------------- real GPU frame timing
        //
        // FrameTimingManager is the one runtime API that reports ACTUAL GPU time
        // per frame in a release player (Unity's Frame Debugger needs a
        // development build, which EFT is not). it also splits CPU main thread vs
        // CPU render thread, which is exactly the three-way test we need:
        //
        //   gpu high, main low        -> GPU-bound (probe re-renders, overdraw, shaders)
        //   renderThread high         -> draw-call/batch submission bound
        //   main high, gpu low        -> game logic on the main thread
        //
        // every measurement so far has been CPU submit time, which can't tell
        // these apart — that's why "GPU% is up but render submit is 4ms" has been
        // unresolvable. gpuFrameTime can read 0 if the driver/platform declines to
        // report it; we log that once rather than silently showing zeros.
        internal static double GpuMs, CpuMs, MainThreadMs, RenderThreadMs;
        internal static double AvgGpuMs, AvgMainMs, AvgRenderThreadMs, PeakGpuMs;
        private static FrameTiming[] _timings = new FrameTiming[1];
        private static bool _timingWarned;
        private static int _timingFrames;

        internal static void TickFrameTimings()
        {
            if (!TerminalGate.On) return;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                uint got = FrameTimingManager.GetLatestTimings(1, _timings);
                if (got == 0) return;
                var t = _timings[0];
                CpuMs = t.cpuFrameTime;
                GpuMs = t.gpuFrameTime;
                MainThreadMs = t.cpuMainThreadFrameTime;
                RenderThreadMs = t.cpuRenderThreadFrameTime;

                AvgGpuMs = AvgGpuMs * 0.95 + GpuMs * 0.05;
                AvgMainMs = AvgMainMs * 0.95 + MainThreadMs * 0.05;
                AvgRenderThreadMs = AvgRenderThreadMs * 0.95 + RenderThreadMs * 0.05;
                if (GpuMs > PeakGpuMs) PeakGpuMs = GpuMs;

                if (!_timingWarned && ++_timingFrames > 300 && AvgGpuMs <= 0.01)
                {
                    _timingWarned = true;
                    Plugin.Log.LogWarning("[Render] FrameTimingManager reports gpuFrameTime=0 — this GPU/driver isn't "
                        + "returning GPU timestamps, so the gpu= field below is meaningless. main/renderThread are still valid.");
                }
            }
            catch (System.Exception e)
            {
                if (!_timingWarned)
                {
                    _timingWarned = true;
                    Plugin.Log.LogWarning($"[Render] FrameTimingManager unavailable: {e.Message}");
                }
            }
        }

        private static void OnPreRender(Camera cam)
        {
            if (!TerminalGate.On || cam == null) return;
            // new frame — publish what the previous frame accumulated
            if (Time.frameCount != _frame)
            {
                if (_frame >= 0)
                {
                    LastCamCount = _camsThisFrame;
                    LastTotalMs = _msThisFrame;
                    AvgTotalMs = AvgTotalMs * 0.95 + _msThisFrame * 0.05;
                    if (_camsThisFrame > PeakCamCount) PeakCamCount = _camsThisFrame;
                    if (_msThisFrame > PeakTotalMs) PeakTotalMs = _msThisFrame;
                    if (_worstMsThisFrame > WorstCamMs)
                    {
                        WorstCamMs = _worstMsThisFrame;
                        WorstCamName = _worstNameThisFrame ?? "";
                    }
                }
                _frame = Time.frameCount;
                _camsThisFrame = 0;
                _msThisFrame = 0;
                _worstMsThisFrame = 0;
                _worstNameThisFrame = null;
            }
            // cameras render sequentially on the main thread, so one shared
            // stopwatch is enough — no nesting to worry about
            _sw.Restart();
        }

        private static void OnPostRender(Camera cam)
        {
            if (!TerminalGate.On || cam == null || !_sw.IsRunning) return;
            _sw.Stop();
            double ms = _sw.Elapsed.TotalMilliseconds;
            _camsThisFrame++;
            _msThisFrame += ms;
            if (ms > _worstMsThisFrame) { _worstMsThisFrame = ms; _worstNameThisFrame = cam.name; }

            var fps = TerminalCullingDriver.CameraRef;
            if (fps != null && cam == fps)
            {
                LastRenderMs = ms;
                AvgRenderMs = AvgRenderMs * 0.95 + ms * 0.05;
                if (ms > PeakRenderMs) PeakRenderMs = ms;
            }
        }

        // ------------------------------------------------------------ inventory

        private static bool _inventoryDone;
        private static float _inventoryAt = -1f;

        // dumped ~20s in, once the scene has settled and probes have had a chance
        // to do whatever they're going to do
        internal static void TickInventory()
        {
            if (_inventoryDone || !TerminalGate.On) return;
            // the v1 dump fired during LOAD and inventoried the menu cameras
            // (MainMenuCamera / Camera_timehascome0) — worthless. wait for a live
            // MainPlayer so we're looking at the raid scene, THEN start the clock.
            var gw = Comfort.Common.Singleton<EFT.GameWorld>.Instantiated
                ? Comfort.Common.Singleton<EFT.GameWorld>.Instance : null;
            if (gw == null || gw.MainPlayer == null) return;
            if (_inventoryAt < 0f) { _inventoryAt = Time.realtimeSinceStartup + 20f; return; }
            if (Time.realtimeSinceStartup < _inventoryAt) return;
            _inventoryDone = true;
            try { DumpInventory(); }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[Render] inventory dump failed: {e.Message}"); }
        }

        private static void DumpInventory()
        {
            // --- reflection probes: the prime suspect for a cyclic GPU cost that
            // also swings weapon specular
            var probes = Object.FindObjectsOfType<ReflectionProbe>();
            int realtime = 0, everyFrame = 0, viaScripting = 0, onAwake = 0, baked = 0, custom = 0, noTex = 0;
            var loud = new StringBuilder();
            int loudShown = 0;
            foreach (var p in probes)
            {
                if (p == null) continue;
                if (p.mode == UnityEngine.Rendering.ReflectionProbeMode.Realtime) realtime++;
                else if (p.mode == UnityEngine.Rendering.ReflectionProbeMode.Baked) baked++;
                else custom++;

                if (p.mode == UnityEngine.Rendering.ReflectionProbeMode.Realtime)
                {
                    switch (p.refreshMode)
                    {
                        case UnityEngine.Rendering.ReflectionProbeRefreshMode.EveryFrame: everyFrame++; break;
                        case UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting: viaScripting++; break;
                        default: onAwake++; break;
                    }
                    // an EveryFrame probe is a whole-scene re-render x6 per refresh
                    if (p.refreshMode == UnityEngine.Rendering.ReflectionProbeRefreshMode.EveryFrame && loudShown < 12)
                    {
                        loudShown++;
                        loud.Append($"\n    '{p.name}' res={p.resolution} timeSlice={p.timeSlicingMode} "
                            + $"importance={p.importance} box={p.size} pos={p.transform.position} enabled={p.enabled}");
                    }
                }
                if (p.mode == UnityEngine.Rendering.ReflectionProbeMode.Baked && p.bakedTexture == null) noTex++;
            }

            Plugin.Log.LogWarning($"[Render][probes] {probes.Length} reflection probe(s): "
                + $"{realtime} realtime ({everyFrame} EVERY-FRAME, {viaScripting} via-scripting, {onAwake} on-awake), "
                + $"{baked} baked ({noTex} with NO baked cubemap — rip casualties), {custom} custom. "
                + $"QualitySettings.realtimeReflectionProbes={QualitySettings.realtimeReflectionProbes} "
                + $"defaultReflectionMode={RenderSettings.defaultReflectionMode}"
                + (loud.Length > 0 ? $"\n  EVERY-FRAME probes:{loud}" : ""));

            // --- cameras: anything beyond the FPS cam that renders every frame
            var cams = Object.FindObjectsOfType<Camera>();
            var sb = new StringBuilder();
            foreach (var c in cams)
            {
                if (c == null) continue;
                sb.Append($"\n    '{c.name}' enabled={c.isActiveAndEnabled} depth={c.depth} "
                    + $"rt={(c.targetTexture != null ? c.targetTexture.width + "x" + c.targetTexture.height : "screen")} "
                    + $"mask=0x{c.cullingMask:X} path={c.renderingPath} hdr={c.allowHDR}");
            }
            Plugin.Log.LogWarning($"[Render][cams] {cams.Length} camera(s) in scene:{sb}");
        }
    }
}
