using System;
using System.Collections.Generic;
using System.Text;
using EFT;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // Automatic onset recorder. It makes no assumption about which map event is
    // responsible: keep recent timings/events, maintain a state-aware baseline
    // while smooth, then dump the evidence when the postLate sawtooth appears.
    internal static class TerminalWorldDiff
    {
        private sealed class Snapshot
        {
            internal readonly Dictionary<string, int> Total = new Dictionary<string, int>(768);
            internal readonly Dictionary<string, int> Active = new Dictionary<string, int>(768);
            internal readonly Dictionary<string, string> State = new Dictionary<string, string>(4096);
            internal long AllocatedMb, ReservedMb, UnusedMb, MonoUsedMb, MonoHeapMb;
            internal int Alive, LiveParticles, EmittingParticles, RenderTextures, CreatedRenderTextures;
            internal long RenderTexturePixels;
            internal long VramUsedMb, VramBudgetMb, VramTotalMb;
        }

        private struct Sample
        {
            internal float At, Frame, AvgFrame;
            internal double Post, AvgPost, Finish, Upd, Late, Cull, Rend, Tail;
            internal long ManagedMb;
            internal long VramUsedMb, VramBudgetMb;
            internal int Alive;
            internal Vector3 Pos;
        }

        private struct EventMark { internal float At; internal string Text; }

        private static Snapshot _baseline;
        private static float _baseAt = -1f, _nextSnap, _chopSince = -1f, _nextSample;
        private static float _gameplaySince = -1f, _ignoreUntil = -1f;
        private static bool _armed;
        private static bool _fired;
        private const float GameplayWarmupSeconds = 30f;
        private const float LoadingHitchIgnoreSeconds = 10f;
        private const int HistorySize = 180; // 45 seconds at 4 Hz
        private static readonly Sample[] History = new Sample[HistorySize];
        private static int _historyNext, _historyCount;
        private static readonly Queue<EventMark> Events = new Queue<EventMark>(64);
        private static readonly List<Component> ComponentBuffer = new List<Component>(24);

        internal static void ResetForRaid()
        {
            _baseline = null;
            _baseAt = -1f;
            _nextSnap = _nextSample = 0f;
            _chopSince = -1f;
            _gameplaySince = _ignoreUntil = -1f;
            _armed = false;
            _fired = false;
            _historyNext = _historyCount = 0;
            Events.Clear();
        }

        internal static void RecordEvent(string category, string detail)
        {
            try
            {
                while (Events.Count >= 64) Events.Dequeue();
                Events.Enqueue(new EventMark { At = Time.realtimeSinceStartup, Text = category + ": " + detail });
            }
            catch { }
        }

        internal static void Tick(float avgFrameMs, Player player)
        {
            try
            {
                if (!TerminalGate.On || Plugin.WorldDiff == null || !Plugin.WorldDiff.Value) return;
                CaptureTimingSample(avgFrameMs, player);
                if (_fired) return;

                float now = Time.realtimeSinceStartup;
                if (!GameplayCameraReady())
                {
                    _gameplaySince = -1f;
                    _chopSince = -1f;
                    return;
                }

                // Raid construction produces multi-second frames whose EMA remains
                // elevated after the frame itself. That looked like sustained chop
                // and consumed the one-shot recorder before gameplay. Do not let a
                // catastrophic load/scene-build frame arm or trip the detector.
                if (Time.unscaledDeltaTime >= 0.25f)
                {
                    _ignoreUntil = now + LoadingHitchIgnoreSeconds;
                    if (!_armed) _gameplaySince = now;
                    _chopSince = -1f;
                    return;
                }
                if (now < _ignoreUntil)
                {
                    _chopSince = -1f;
                    return;
                }

                if (_gameplaySince < 0f) _gameplaySince = now;
                if (!_armed)
                {
                    if (now - _gameplaySince < GameplayWarmupSeconds) return;
                    _armed = true;
                    _nextSnap = 0f;
                    Plugin.Log.LogWarning($"[Onset] detector armed after {GameplayWarmupSeconds:F0}s of hitch-free FPS-camera gameplay");
                }

                float post = (float)TerminalFramePhase.AvgPostLate;
                if (post < 4f)
                {
                    _chopSince = -1f;
                    if (now < _nextSnap) return;
                    _nextSnap = now + 20f;
                    _baseline = TakeSnapshot();
                    _baseAt = now;
                    Plugin.Log.LogWarning($"[Onset] smooth baseline t={_baseAt:F0}s frame={avgFrameMs:F1}ms "
                        + $"postLate={post:F1}ms activeTypes={_baseline.Active.Count} stateful={_baseline.State.Count} "
                        + Metrics(_baseline));
                    return;
                }

                if (post <= 10f || _baseline == null)
                {
                    _chopSince = -1f;
                    return;
                }
                if (_chopSince < 0f)
                {
                    _chopSince = now;
                    RecordEvent("detector", $"postLate EMA crossed 10ms ({post:F1}ms)");
                    return;
                }
                if (now - _chopSince < 3f) return;

                _fired = true;
                Report(TakeSnapshot(), avgFrameMs, post);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Onset] recorder failed: {e}"); }
        }

        private static bool GameplayCameraReady()
        {
            try
            {
                var camera = TerminalCullingDriver.CameraRef;
                return camera != null && camera.enabled && camera.gameObject.activeInHierarchy
                    && String.Equals(camera.name, "FPS Camera", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static void CaptureTimingSample(float avgFrameMs, Player player)
        {
            if (Time.realtimeSinceStartup < _nextSample) return;
            _nextSample = Time.realtimeSinceStartup + 0.25f;
            int alive = -1;
            try
            {
                var gw = Comfort.Common.Singleton<GameWorld>.Instantiated
                    ? Comfort.Common.Singleton<GameWorld>.Instance : null;
                alive = gw != null && gw.AllAlivePlayersList != null ? gw.AllAlivePlayersList.Count : -1;
            }
            catch { }
            TerminalVramProbe.Read(out _, out long vramBudget, out long vramUsed);
            History[_historyNext] = new Sample
            {
                At = Time.realtimeSinceStartup,
                Frame = Time.unscaledDeltaTime * 1000f,
                AvgFrame = avgFrameMs,
                Post = TerminalFramePhase.PostLateMs,
                AvgPost = TerminalFramePhase.AvgPostLate,
                Finish = TerminalLoopProbe.InstantOf("FinishFrameRendering"),
                Upd = TerminalFramePhase.UpdMs,
                Late = TerminalFramePhase.LateMs,
                Cull = TerminalFramePhase.CullMs,
                Rend = TerminalFramePhase.RendMs,
                Tail = TerminalFramePhase.TailMs,
                ManagedMb = GC.GetTotalMemory(false) / (1024 * 1024),
                VramUsedMb = vramUsed,
                VramBudgetMb = vramBudget,
                Alive = alive,
                Pos = player != null ? player.Position : Vector3.zero
            };
            _historyNext = (_historyNext + 1) % HistorySize;
            if (_historyCount < HistorySize) _historyCount++;
        }

        private static void Report(Snapshot now, float avgFrameMs, float post)
        {
            var sb = new StringBuilder(32768);
            sb.Append($"[Onset] SAWTOOTH ONSET t={Time.realtimeSinceStartup:F2}s frame={avgFrameMs:F1}ms postLate={post:F1}ms\n")
              .Append($"  baseline t={_baseAt:F2}s ({Time.realtimeSinceStartup - _baseAt:F1}s earlier): {Metrics(_baseline)}\n")
              .Append($"  onset: {Metrics(now)}\n");
            AppendTypeDiff(sb, now);
            AppendStateDiff(sb, now);
            AppendEvents(sb);
            AppendHistory(sb);
            Plugin.Log.LogWarning(sb.ToString());
        }

        private static void AppendTypeDiff(StringBuilder sb, Snapshot now)
        {
            var deltas = new List<KeyValuePair<string, int>>();
            foreach (var kv in now.Active)
            {
                _baseline.Active.TryGetValue(kv.Key, out int was);
                int d = kv.Value - was;
                if (d != 0) deltas.Add(new KeyValuePair<string, int>(kv.Key, d));
            }
            foreach (var kv in _baseline.Active)
                if (!now.Active.ContainsKey(kv.Key)) deltas.Add(new KeyValuePair<string, int>(kv.Key, -kv.Value));
            deltas.Sort((a, b) => Math.Abs(b.Value).CompareTo(Math.Abs(a.Value)));

            sb.Append("  ACTIVE COMPONENT TYPE DELTAS (top 50):\n");
            if (deltas.Count == 0) sb.Append("    none\n");
            for (int i = 0; i < deltas.Count && i < 50; i++)
            {
                var kv = deltas[i];
                _baseline.Active.TryGetValue(kv.Key, out int was);
                now.Total.TryGetValue(kv.Key, out int total);
                sb.Append($"    {(kv.Value > 0 ? "+" : "")}{kv.Value,-7} {kv.Key,-42} active {was} -> {was + kv.Value} (total {total})\n");
            }
        }

        private static void AppendStateDiff(StringBuilder sb, Snapshot now)
        {
            var changes = new List<string>();
            foreach (var kv in now.State)
            {
                if (!_baseline.State.TryGetValue(kv.Key, out string was)) changes.Add("ADDED   " + kv.Value);
                else if (!String.Equals(was, kv.Value, StringComparison.Ordinal))
                    changes.Add("CHANGED " + was + "  =>  " + kv.Value);
            }
            foreach (var kv in _baseline.State)
                if (!now.State.ContainsKey(kv.Key)) changes.Add("REMOVED " + kv.Value);

            sb.Append($"  STATEFUL INSTANCE DELTAS ({changes.Count} total, first 100):\n");
            if (changes.Count == 0) sb.Append("    none\n");
            for (int i = 0; i < changes.Count && i < 100; i++) sb.Append("    ").Append(changes[i]).Append('\n');
        }

        private static void AppendEvents(StringBuilder sb)
        {
            sb.Append("  RECENT MAP EVENTS:\n");
            bool any = false;
            foreach (var e in Events)
            {
                if (Time.realtimeSinceStartup - e.At > 120f) continue;
                any = true;
                sb.Append($"    t={e.At:F2} {e.Text}\n");
            }
            if (!any) sb.Append("    none recorded in the preceding 120s\n");
        }

        private static void AppendHistory(StringBuilder sb)
        {
            sb.Append($"  ROLLING FRAME HISTORY ({_historyCount} samples, oldest -> onset):\n")
              .Append("    t frame avg post postAvg finish upd late cull rend tail mem vram/budget alive pos\n");
            int first = (_historyNext - _historyCount + HistorySize) % HistorySize;
            for (int i = 0; i < _historyCount; i++)
            {
                Sample s = History[(first + i) % HistorySize];
                sb.Append($"    {s.At:F2} {s.Frame:F1} {s.AvgFrame:F1} {s.Post:F1} {s.AvgPost:F1} {s.Finish:F1} "
                    + $"{s.Upd:F1} {s.Late:F1} {s.Cull:F1} {s.Rend:F1} {s.Tail:F1} {s.ManagedMb} "
                    + $"{s.VramUsedMb}/{s.VramBudgetMb} {s.Alive} "
                    + $"{s.Pos.x:F0},{s.Pos.y:F0},{s.Pos.z:F0}\n");
            }
        }

        private static string Metrics(Snapshot s)
            => $"alloc={s.AllocatedMb}MB reserved={s.ReservedMb}MB unused={s.UnusedMb}MB "
             + $"mono={s.MonoUsedMb}/{s.MonoHeapMb}MB alive={s.Alive} "
             + $"particles={s.LiveParticles} emitting={s.EmittingParticles} "
             + $"RT={s.CreatedRenderTextures}/{s.RenderTextures} ({s.RenderTexturePixels / 1000000f:F1}MP) "
             + $"vram={s.VramUsedMb}/{s.VramBudgetMb}/{s.VramTotalMb}MB";

        private static Snapshot TakeSnapshot()
        {
            var snap = new Snapshot
            {
                AllocatedMb = Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024),
                ReservedMb = Profiler.GetTotalReservedMemoryLong() / (1024 * 1024),
                UnusedMb = Profiler.GetTotalUnusedReservedMemoryLong() / (1024 * 1024),
                MonoUsedMb = Profiler.GetMonoUsedSizeLong() / (1024 * 1024),
                MonoHeapMb = Profiler.GetMonoHeapSizeLong() / (1024 * 1024)
            };
            try
            {
                var gw = Comfort.Common.Singleton<GameWorld>.Instantiated
                    ? Comfort.Common.Singleton<GameWorld>.Instance : null;
                snap.Alive = gw != null && gw.AllAlivePlayersList != null ? gw.AllAlivePlayersList.Count : -1;
            }
            catch { snap.Alive = -1; }
            TerminalVramProbe.Read(out snap.VramTotalMb, out snap.VramBudgetMb, out snap.VramUsedMb);

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) Walk(root.transform, snap);
            }
            GameObject probe = null;
            try
            {
                probe = new GameObject("Manimal_DdolProbe");
                UnityEngine.Object.DontDestroyOnLoad(probe);
                foreach (var root in probe.scene.GetRootGameObjects()) Walk(root.transform, snap);
            }
            catch { }
            finally { if (probe != null) UnityEngine.Object.Destroy(probe); }

            try
            {
                foreach (var rt in Resources.FindObjectsOfTypeAll<RenderTexture>())
                {
                    if (rt == null) continue;
                    snap.RenderTextures++;
                    if (!rt.IsCreated()) continue;
                    snap.CreatedRenderTextures++;
                    snap.RenderTexturePixels += (long)rt.width * rt.height;
                }
            }
            catch { }
            return snap;
        }

        private static void Walk(Transform t, Snapshot snap)
        {
            t.GetComponents(ComponentBuffer);
            bool active = t.gameObject.activeInHierarchy;
            for (int i = 0; i < ComponentBuffer.Count; i++)
            {
                var c = ComponentBuffer[i];
                if (c == null) continue;
                string type = c.GetType().Name;
                Bump(snap.Total, type);
                var behaviour = c as Behaviour;
                if (active && (behaviour == null || behaviour.enabled)) Bump(snap.Active, type);
                string state = StatefulSignature(c, active, snap);
                if (state != null) snap.State[type + "#" + c.GetInstanceID()] = type + " '" + ObjectPath(t) + "' " + state;
            }
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), snap);
        }

        private static string StatefulSignature(Component c, bool active, Snapshot snap)
        {
            try
            {
                if (c is ParticleSystem ps)
                {
                    snap.LiveParticles += ps.particleCount;
                    if (ps.isEmitting) snap.EmittingParticles++;
                    var main = ps.main;
                    return $"active={active} play={ps.isPlaying} emit={ps.isEmitting} pause={ps.isPaused} loop={main.loop}";
                }
                if (c is AudioSource audio)
                    return $"active={active} enabled={audio.enabled} playing={audio.isPlaying} loop={audio.loop} mute={audio.mute} clip='{(audio.clip != null ? audio.clip.name : "null")}'";
                if (c is Animator animator)
                {
                    int state = animator.isInitialized && animator.layerCount > 0
                        ? animator.GetCurrentAnimatorStateInfo(0).fullPathHash : 0;
                    return $"active={active} enabled={animator.enabled} init={animator.isInitialized} speed={animator.speed:F2} cull={animator.cullingMode} state0={state}";
                }
                if (c is PlayableDirector director)
                    return $"active={active} enabled={director.enabled} state={director.state} wrap={director.extrapolationMode} asset='{(director.playableAsset != null ? director.playableAsset.name : "null")}'";
                if (c is ReflectionProbe probe)
                    return $"active={active} enabled={probe.enabled} mode={probe.mode} refresh={probe.refreshMode} slice={probe.timeSlicingMode} res={probe.resolution}";
                if (c is Camera camera)
                    return $"active={active} enabled={camera.enabled} depth={camera.depth:F1} target='{(camera.targetTexture != null ? camera.targetTexture.name : "screen")}' px={camera.pixelWidth}x{camera.pixelHeight}";
                if (c is Light light)
                    return $"active={active} enabled={light.enabled} type={light.type} shadows={light.shadows} range={light.range:F1} intensity={light.intensity:F2}";
            }
            catch { }
            return null;
        }

        private static string ObjectPath(Transform t)
        {
            var names = new Stack<string>();
            for (Transform p = t; p != null; p = p.parent) names.Push(p.name);
            return String.Join("/", names.ToArray());
        }

        private static void Bump(Dictionary<string, int> d, string k)
        {
            d.TryGetValue(k, out int v);
            d[k] = v + 1;
        }
    }
}
