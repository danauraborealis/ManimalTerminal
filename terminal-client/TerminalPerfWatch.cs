using System;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.Terminal
{
    // spike + ramp forensics (2026-08-19: the chop is a monotonic RAMP — avg frame
    // cost climbs 16->38ms across a raid regardless of artillery; something
    // accumulates). three instruments, all near-free between events:
    //  - spike lines: >50ms frames with position/bots/water/artillery context and
    //    OURS attribution (Plugin.Update tick cost measured by stopwatch), ported
    //    from icebreaker's stutter probe — UNTRACKED means "not this component"
    //  - last-8-frame ring + gc deltas on each spike: one giant frame vs creep
    //  - opt-in 60s census: object counts that grow help investigate retention (renderers,
    //    audio sources, particle systems, gameobject total via scene roots)
    internal static class TerminalPerfWatch
    {
        private static float _lastLog;
        private static int _spikes;
        private static System.Collections.Generic.List<Renderer> _water;

        // OURS attribution: Plugin.Update wraps its tick block with Begin/End
        private static readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        private static double _oursMs;
        internal static void OursBegin() => _sw.Restart();
        internal static void OursEnd() => _oursMs = _sw.Elapsed.TotalMilliseconds;

        // last-8 frame ring
        private static readonly float[] _ring = new float[8];
        private static int _ringIdx;
        private static int _gc0Prev;
        private static float _avg = 1f / 60f;

        // census
        private static float _nextCensusAt;
        private static int _censusNum;

        // profiler heartbeat
        private static float _nextHeartAt;

        internal static void ResetForRaid()
        {
            _spikes = 0;
            _lastLog = 0f;
            _water = null;
            _nextCensusAt = 0f;
            _censusNum = 0;
            _nextHeartAt = 0f;
            _avg = 1f / 60f;
            _ringIdx = 0;
            Array.Clear(_ring, 0, _ring.Length);
            _gc0Prev = GC.CollectionCount(0);
            _nextTrace = _traceMaxDt = 0f;
            _traceFrames = 0;
            _psCache = null;
            _nextPsRefresh = 0f;
            TerminalTickProfiler.Reset();
            TerminalPathDiagnostics.ResetForRaid();
            TerminalRenderProfiler.ResetForRaid();
            TerminalFramePhase.ResetForRaid();
            TerminalLoopProbe.ResetForRaid();
            TerminalVramProbe.ResetForRaid();
            TerminalWorldDiff.ResetForRaid();
        }

        internal static void WatchWater(System.Collections.Generic.List<Renderer> planes) => _water = planes;

        // WAVEFORM TRACE. every number we've produced so far is a 30s average, which
        // cannot show a ~4-5s cycle at all — we've been reasoning about a shape we
        // never measured. this samples 4x/sec so the period, duty cycle and phase
        // are visible directly, and includes the camera position so a stationary
        // run is provable rather than assumed.
        private static float _nextTrace;
        private static float _traceMaxDt;      // worst frame BETWEEN samples
        private static int _traceFrames;

        // the trace showed a clean SAWTOOTH: post/finish ramp linearly for ~3.75s
        // then reset instantly, while stationary. that's accumulation + flush, and
        // the classic GPU-side version is particles — emission piling up until a
        // burst cycle ends and the whole population dies at once. count live
        // particles per sample so the correlation is measured, not assumed.
        private static ParticleSystem[] _psCache;
        private static float _nextPsRefresh;

        private static int LiveParticles(out int emitting)
        {
            emitting = 0;
            try
            {
                if (_psCache == null || Time.realtimeSinceStartup >= _nextPsRefresh)
                {
                    _nextPsRefresh = Time.realtimeSinceStartup + 20f;
                    _psCache = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
                }
                int total = 0;
                for (int i = 0; i < _psCache.Length; i++)
                {
                    var ps = _psCache[i];
                    if (ps == null) continue;
                    int c = ps.particleCount;
                    if (c > 0) { total += c; if (ps.isEmitting) emitting++; }
                }
                return total;
            }
            catch { return -1; }
        }

        private static void TickTrace(GameWorld world, Player p)
        {
            try
            {
                if (Plugin.TraceFrameCycle == null || !Plugin.TraceFrameCycle.Value) return;

                // sampling 4x/sec only inspects 4 of ~60-120 frames, so a short
                // spike between samples would be invisible. carry the worst frame
                // seen since the last line so the trace can't alias past the thing
                // it exists to measure.
                float dtms = Time.unscaledDeltaTime * 1000f;
                if (dtms > _traceMaxDt) _traceMaxDt = dtms;
                _traceFrames++;

                if (Time.realtimeSinceStartup < _nextTrace) return;
                _nextTrace = Time.realtimeSinceStartup + 0.25f;

                var pos = p != null ? p.Position : Vector3.zero;
                int psEmitting;
                int psLive = LiveParticles(out psEmitting);

                // particles came back FLAT through a full sawtooth, so the ramp is
                // something else accumulating per-frame and being released in one
                // go. three candidates, three cheap probes:
                //   cmdBufs  — a command buffer added each frame and never removed
                //              grows GPU work linearly; a RemoveAllCommandBuffers
                //              would drop it instantly. exactly this waveform.
                //   memMB    — managed heap climbing (adam's "RAM spike" report)
                //   gc0      — if the reset lands on a collection, it's GC-driven
                int cmdBufs = -1;
                try
                {
                    var cam = TerminalCullingDriver.CameraRef;
                    if (cam != null) cmdBufs = cam.commandBufferCount;
                }
                catch { }
                long memMB = GC.GetTotalMemory(false) / (1024 * 1024);
                int gen0 = GC.CollectionCount(0);
                TerminalVramProbe.Read(out long vramTotal, out long vramBudget, out long vramUsed);
                // AllAlivePlayersList can retain lootable dead players. Count health
                // states in the existing registry so a corpse-only cleanup can be
                // distinguished from removing living AI without a scene search.
                int livingAi = 0, deadAi = 0;
                var players = world != null ? world.RegisteredPlayers : null;
                if (players != null)
                {
                    foreach (var player in players)
                    {
                        if (player == null || !player.IsAI || player.HealthController == null) continue;
                        if (player.HealthController.IsAlive) livingAi++;
                        else deadAi++;
                    }
                }

                // FinishFrameRendering ramps to ~45ms while the camera's own cull
                // (1.5) and render (1.7) stay flat — so ~40ms sits AFTER the camera
                // finished. that window is image effects (OnRenderImage fires after
                // onPostRender) plus the present/GPU wait. split it.
                double postFx = TerminalLoopProbe.MsBetween(
                    TerminalFramePhase.PostRenderStamp,
                    TerminalLoopProbe.EndStampOf("FinishFrameRendering"));

                // DYNAMIC RESOLUTION. everything object-side is flat — draw submit,
                // cameras, particles, shadows, command buffers — yet GPU time ramps
                // smoothly and resets. same commands costing more means more PIXELS,
                // not more objects. and FrameTimingManager reads ZERO on this rig,
                // which is precisely the signal a resolution scaler uses to decide
                // how far to push: fed nothing, it ramps until it clamps, then snaps
                // back. log the scale factors and the camera's actual pixel size.
                float wScale = 1f, hScale = 1f;
                int camW = 0, camH = 0;
                try
                {
                    wScale = ScalableBufferManager.widthScaleFactor;
                    hScale = ScalableBufferManager.heightScaleFactor;
                    var cam = TerminalCullingDriver.CameraRef;
                    if (cam != null) { camW = cam.pixelWidth; camH = cam.pixelHeight; }
                }
                catch { }
                // NOTE every phase value here is the PREVIOUS frame's — Update runs
                // before PostLateUpdate — and so is unscaledDeltaTime, so they're
                // consistent with each other.
                Plugin.Log.LogWarning($"[Trace] t={Time.realtimeSinceStartup:F2} frame={dtms:F1} worst={_traceMaxDt:F1}"
                    + $" n={_traceFrames}"
                    + $" post={TerminalFramePhase.PostLateMs:F1} finish={TerminalLoopProbe.InstantOf("FinishFrameRendering"):F1}"
                    + $" upd={TerminalFramePhase.UpdMs:F1} late={TerminalFramePhase.LateMs:F1}"
                    + $" cull={TerminalFramePhase.CullMs:F1} rend={TerminalFramePhase.RendMs:F1} tail={TerminalFramePhase.TailMs:F1}"
                    + $" parts={psLive} emit={psEmitting}"
                    + $" postFx={postFx:F1} resScale={wScale:F3}x{hScale:F3} camPx={camW}x{camH}"
                    + $" cmdBufs={cmdBufs} memMB={memMB} gc0={gen0}"
                    + $" livingAi={livingAi} deadAi={deadAi}"
                    + $" vramMB={vramUsed}/{vramBudget}/{vramTotal}"
                    + $" pos={pos.x:F0},{pos.y:F0},{pos.z:F0}");
                _traceMaxDt = 0f;
                _traceFrames = 0;
            }
            // a throw here would take out the rest of PerfWatch.Tick AND the calls
            // after it in Plugin.Update — a diagnostic must never do that
            catch (Exception e) { Plugin.Log.LogWarning($"[Trace] failed: {e.Message}"); }
        }

        private static string LoopBit()
        {
            var s = TerminalLoopProbe.TopN(6);
            if (string.IsNullOrEmpty(s)) return "";
            // measured-vs-actual: if these diverge the instrument isn't seeing the
            // cost and its ranking is worthless (first run read 2.4 vs 24-34)
            return $" POSTLATE[sum={TerminalLoopProbe.MeasuredSum():F1} vs {TerminalFramePhase.AvgPostLate:F1}] {s} ||";
        }

        internal static void Tick()
        {
            if (!TerminalGate.On) return;
            float dt = Time.unscaledDeltaTime;
            int gc0 = System.GC.CollectionCount(0);
            TerminalRenderProfiler.TickInventory();
            TerminalRenderProfiler.TickFrameTimings();
            TerminalLoopProbe.Tick();

            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            var p = gw != null ? gw.MainPlayer : null;
            TickTrace(gw, p);
            if (p != null) TerminalWorldDiff.Tick(_avg * 1000f, p);

            // Whole-scene discovery is intrusive on this map; keep it opt-in so
            // the diagnostic itself cannot introduce a periodic gameplay hitch.
            if (p != null && Plugin.SceneCensus != null && Plugin.SceneCensus.Value
                && Time.realtimeSinceStartup >= _nextCensusAt)
            {
                _nextCensusAt = Time.realtimeSinceStartup + 60f;
                _censusNum++;
                try
                {
                    int renderers = UnityEngine.Object.FindObjectsOfType<Renderer>().Length;
                    int audio = UnityEngine.Object.FindObjectsOfType<AudioSource>().Length;
                    int particles = UnityEngine.Object.FindObjectsOfType<ParticleSystem>().Length;
                    int alive = 0;
                    try { alive = gw.AllAlivePlayersList?.Count ?? 0; } catch { }

                    // LIGHTS (2026-08-21): the chop is the cull phase, which is where
                    // per-object light culling runs. we drive 1854 lights and — the
                    // part that matters — CullingLightObject-owned ones never go
                    // through DriveLamp's enable/disable path, so LampIntensity=0 did
                    // NOT switch them off. count what's actually enabled and how much
                    // of it is near the camera, so light load can be correlated with
                    // cull cost instead of assumed.
                    int litTotal = 0, litNear = 0, litShadow = 0;
                    try
                    {
                        var cam = TerminalCullingDriver.CameraRef != null
                            ? TerminalCullingDriver.CameraRef.transform.position
                            : (p != null ? p.Position : Vector3.zero);
                        foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                        {
                            if (l == null || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                            if (l.intensity <= 0.01f) continue;
                            litTotal++;
                            if (l.shadows != LightShadows.None) litShadow++;
                            if ((l.transform.position - cam).sqrMagnitude < 60f * 60f) litNear++;
                        }
                    }
                    catch { }

                    // CAMERA CENSUS, every 60s. the one-shot inventory in
                    // TerminalRenderProfiler kept firing during LOAD (its
                    // MainPlayer guard is satisfied while the menu is still up), so
                    // every camera list we have is menu cameras — we have never
                    // seen the real in-raid set. a stray enabled cutscene camera
                    // would render the whole scene a second time and never show up
                    // in any measurement we've taken, so list them repeatedly.
                    var camSb = new System.Text.StringBuilder();
                    int camsOn = 0;
                    try
                    {
                        foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>())
                        {
                            if (c == null) continue;
                            bool on = c.isActiveAndEnabled;
                            if (on) camsOn++;
                            camSb.Append($"\n    '{c.name}' on={on} depth={c.depth} "
                                + $"rt={(c.targetTexture != null ? c.targetTexture.width + "x" + c.targetTexture.height : "screen")} "
                                + $"px={c.pixelWidth}x{c.pixelHeight} mask=0x{c.cullingMask:X} path={c.renderingPath}");
                        }
                    }
                    catch { }

                    Plugin.Log.LogWarning($"[Perf][cams #{_censusNum}] {camsOn} camera(s) ENABLED:{camSb}");

                    Plugin.Log.LogWarning($"[Perf][census #{_censusNum}] avgFrame={_avg * 1000f:F1}ms renderers={renderers}"
                        + $" audioSources={audio} particles={particles} alive={alive}"
                        + $" | LIGHTS lit={litTotal} within60m={litNear} shadowCasting={litShadow}"
                        + $" | cull={TerminalFramePhase.AvgCull:F1}ms");
                }
                catch { }
            }

            // 30s profiler heartbeat — dumps top-6 subsystems by avg cost
            // regardless of spikes, so a sit-still (no-spike) raid still tells
            // us where our tick time is going. compare successive heartbeats
            // to spot cost creep over the raid.
            if (p != null && Time.realtimeSinceStartup >= _nextHeartAt)
            {
                _nextHeartAt = Time.realtimeSinceStartup + 30f;
                string top = TerminalTickProfiler.TopN(6);
                if (!string.IsNullOrEmpty(top))
                    Plugin.Log.LogWarning($"[Perf][heartbeat] t={Time.realtimeSinceStartup:F0}s avg={_avg * 1000f:F1}ms"
                        + $" || PHASE {TerminalFramePhase.Summary()} ||"
                        + LoopBit()
                        + $" fpsCam={TerminalRenderProfiler.AvgRenderMs:F1}avg"
                        + $" allCams={TerminalRenderProfiler.LastCamCount}(peak {TerminalRenderProfiler.PeakCamCount})"
                        + $" allCamMs={TerminalRenderProfiler.LastTotalMs:F1}/{TerminalRenderProfiler.AvgTotalMs:F1}avg/{TerminalRenderProfiler.PeakTotalMs:F1}peak"
                        + $" worstCam='{TerminalRenderProfiler.WorstCamName}'@{TerminalRenderProfiler.WorstCamMs:F1}ms"
                        + $" | top: {top}");
            }

            if (dt >= 0.05f)
            {
                _spikes++;
                if (Time.realtimeSinceStartup - _lastLog >= 2f)
                {
                    _lastLog = Time.realtimeSinceStartup;
                    int alive = -1;
                    try { alive = gw != null && gw.AllAlivePlayersList != null ? gw.AllAlivePlayersList.Count : -1; } catch { }
                    int waterVis = 0;
                    if (_water != null)
                        foreach (var r in _water)
                            if (r && r.isVisible) waterVis++;
                    int artyZones = -1;
                    try { artyZones = TerminalArtillery.ActiveZonesDict()?.Count ?? -1; } catch { }
                    var ring = new System.Text.StringBuilder();
                    for (int i = 1; i <= _ring.Length; i++)
                        ring.Append((_ring[(_ringIdx + i) % _ring.Length] * 1000f).ToString("F0")).Append(' ');
                    string top = TerminalTickProfiler.TopN(4);
                    Plugin.Log.LogWarning($"[Perf] {dt * 1000f:F0}ms frame (spike #{_spikes})"
                        + $" at {(p != null ? p.Position.ToString() : "?")}, {alive} alive"
                        + $", water visible: {waterVis}, artyZones={artyZones} pump={(TerminalArtillery.Pumping ? "on" : "off")}"
                        + $" | PHASE {TerminalFramePhase.Instant()}"
                        + $" OURS={_oursMs:F1}ms fpsCam={TerminalRenderProfiler.LastRenderMs:F1}ms"
                        + $" allCams={TerminalRenderProfiler.LastCamCount}@{TerminalRenderProfiler.LastTotalMs:F1}ms"
                        + $" gc0Δ={gc0 - _gc0Prev} avg={_avg * 1000f:F1}ms prev8: {ring.ToString().TrimEnd()}"
                        + (string.IsNullOrEmpty(top) ? "" : $" | top: {top}"));
                }
            }

            _ring[_ringIdx] = dt;
            _ringIdx = (_ringIdx + 1) % _ring.Length;
            _gc0Prev = gc0;
            _avg = Mathf.Lerp(_avg, dt, 0.05f);
        }
    }
}
