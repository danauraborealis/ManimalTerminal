using System.Diagnostics;
using UnityEngine;

namespace Manimal.Terminal
{
    // FRAME PHASE BREAKDOWN (2026-08-21).
    //
    // the chop has now survived every subsystem toggle, and the numbers say the
    // cost is in NONE of the places we can already see: our Update tick reads
    // 0.0ms, camera submit across ALL cameras reads 2-3ms, and the frame is 35ms.
    // ~30ms is unaccounted for. FrameTimingManager returns zeros on this driver,
    // so we can't ask the engine directly.
    //
    // so bracket the frame ourselves. two probes with extreme execution orders
    // sandwich every other MonoBehaviour, and the camera callbacks mark the
    // render window. the gaps between the marks localize the cost to a phase:
    //
    //   upd   = the whole Update phase (every MonoBehaviour Update in the game)
    //   late  = the LateUpdate phase (animators, IK, camera follow)
    //   cull  = last LateUpdate -> camera starts rendering (Unity's culling)
    //   rend  = camera render submit
    //   tail  = render end -> next frame's first Update
    //           (present, vsync wait, GPU wait, FixedUpdate/physics)
    //
    // reading it: `tail` dominating with low everything else = we're waiting on
    // the GPU or vsync, i.e. genuinely GPU-bound. `upd` dominating = some other
    // MonoBehaviour's Update. `cull` dominating = Unity culling against too many
    // renderers. that's the three-way split we've never been able to make.
    internal static class TerminalFramePhase
    {
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        private static long _tUpdStart, _tUpdEnd, _tLateEnd, _tPreCull, _tPreRender, _tPostRender;
        private static bool _havePrevPost;

        internal static double UpdMs, LateMs, PostLateMs, CullMs, RendMs, TailMs;
        internal static double AvgUpd, AvgLate, AvgPostLate, AvgCull, AvgRend, AvgTail;
        internal static double PeakUpd, PeakLate, PeakPostLate, PeakCull, PeakRend, PeakTail;

        private static bool _spawned;

        internal static void EnsureSpawned()
        {
            if (_spawned) return;
            _spawned = true;
            var go = new GameObject("Manimal_TerminalFramePhase");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<EarlyProbe>();
            go.AddComponent<LateProbe>();
            Camera.onPreCull += OnPreCull;
            Camera.onPreRender += OnPreRender;
            Camera.onPostRender += OnPostRender;
            Plugin.Log.LogInfo("[Phase] frame-phase profiler armed (upd/late/cull/rend/tail breakdown)");
        }

        internal static void ResetForRaid()
        {
            UpdMs = LateMs = PostLateMs = CullMs = RendMs = TailMs = 0;
            AvgUpd = AvgLate = AvgPostLate = AvgCull = AvgRend = AvgTail = 0;
            PeakUpd = PeakLate = PeakPostLate = PeakCull = PeakRend = PeakTail = 0;
            _havePrevPost = false;
        }

        private static double Ms(long a, long b) => (b - a) * TickMs;

        internal static void MarkUpdateStart()
        {
            long now = Stopwatch.GetTimestamp();
            // tail of the PREVIOUS frame closes here — everything between the last
            // camera finishing and this frame's first Update: present, vsync wait,
            // GPU wait, plus FixedUpdate/physics
            if (_havePrevPost)
            {
                TailMs = Ms(_tPostRender, now);
                AvgTail = AvgTail * 0.95 + TailMs * 0.05;
                if (TailMs > PeakTail) PeakTail = TailMs;
            }
            _tUpdStart = now;
        }

        internal static void MarkUpdateEnd()
        {
            _tUpdEnd = Stopwatch.GetTimestamp();
            UpdMs = Ms(_tUpdStart, _tUpdEnd);
            AvgUpd = AvgUpd * 0.95 + UpdMs * 0.05;
            if (UpdMs > PeakUpd) PeakUpd = UpdMs;
        }

        internal static void MarkLateEnd()
        {
            _tLateEnd = Stopwatch.GetTimestamp();
            LateMs = Ms(_tUpdEnd, _tLateEnd);
            AvgLate = AvgLate * 0.95 + LateMs * 0.05;
            if (LateMs > PeakLate) PeakLate = LateMs;
        }

        // Unity's order is onPreCull -> [culling] -> onPreRender, so splitting here
        // separates the PostLateUpdate systems (UpdateAllRenderers /
        // UpdateAllSkinnedMeshes / UpdateLightProbeProxyVolumes) from Unity's actual
        // culling work. lumping them was hiding which of the two is the 36ms.
        private static void OnPreCull(Camera cam)
        {
            if (!TerminalGate.On) return;
            var fps = TerminalCullingDriver.CameraRef;
            if (fps == null || cam != fps) return;
            _tPreCull = Stopwatch.GetTimestamp();
            PostLateMs = Ms(_tLateEnd, _tPreCull);
            AvgPostLate = AvgPostLate * 0.95 + PostLateMs * 0.05;
            if (PostLateMs > PeakPostLate) PeakPostLate = PostLateMs;
        }

        private static void OnPreRender(Camera cam)
        {
            if (!TerminalGate.On) return;
            var fps = TerminalCullingDriver.CameraRef;
            if (fps == null || cam != fps) return;   // only bracket the main camera
            _tPreRender = Stopwatch.GetTimestamp();
            CullMs = Ms(_tPreCull, _tPreRender);      // the culling pass proper
            AvgCull = AvgCull * 0.95 + CullMs * 0.05;
            if (CullMs > PeakCull) PeakCull = CullMs;
        }

        private static void OnPostRender(Camera cam)
        {
            if (!TerminalGate.On) return;
            var fps = TerminalCullingDriver.CameraRef;
            if (fps == null || cam != fps) return;
            _tPostRender = Stopwatch.GetTimestamp();
            _havePrevPost = true;
            RendMs = Ms(_tPreRender, _tPostRender);
            AvgRend = AvgRend * 0.95 + RendMs * 0.05;
            if (RendMs > PeakRend) PeakRend = RendMs;
        }

        internal static string Summary()
            => $"upd={AvgUpd:F1}/{PeakUpd:F0}p late={AvgLate:F1}/{PeakLate:F0}p "
             + $"postLate={AvgPostLate:F1}/{PeakPostLate:F0}p cull={AvgCull:F1}/{PeakCull:F0}p "
             + $"rend={AvgRend:F1}/{PeakRend:F0}p tail={AvgTail:F1}/{PeakTail:F0}p";

        // exposed so the trace can measure onPostRender -> end of
        // FinishFrameRendering: image effects + present/GPU wait
        internal static long PostRenderStamp => _tPostRender;

        internal static string Instant()
            => $"upd={UpdMs:F1} late={LateMs:F1} postLate={PostLateMs:F1} cull={CullMs:F1} "
             + $"rend={RendMs:F1} tail={TailMs:F1}";

        // -32000 puts this before every other script's Update in the frame
        [DefaultExecutionOrder(-32000)]
        internal class EarlyProbe : MonoBehaviour
        {
            private void Update() => MarkUpdateStart();
        }

        // +32000 puts these after every other script's Update / LateUpdate
        [DefaultExecutionOrder(32000)]
        internal class LateProbe : MonoBehaviour
        {
            private void Update() => MarkUpdateEnd();
            private void LateUpdate() => MarkLateEnd();
        }
    }
}
