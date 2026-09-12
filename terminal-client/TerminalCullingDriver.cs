using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using UnityEngine;
using SysIoPath = System.IO.Path;

namespace Manimal.Terminal
{
    // Perfect Culling sidecar driver — port of icebreaker's RenderEnvProbe culling
    // subsystem. volumes are built ENTIRELY from .pcbake sidecars (next to the dll,
    // exported by the SDK's Terminal/Culling tools), so rebakes are sidecar-export +
    // client restart, never a bundle rebuild. the vanilla PerfectCullingRuntime.dll
    // ships next to the plugin (BSG's Assembly-CSharp fork has no vanilla
    // PerfectCullingVolume class — both coexist).
    //
    // the hard-won shape (each rule cost icebreaker a measured hitch):
    //  - NO PerfectCullingCamera: its OnPreCull re-executes ALL volumes on any cell
    //    crossing (90-105ms). one volume per frame, round-robin, per-volume cell gate.
    //  - SET DIFF on cell change, not full reapply — adjacent cells differ by a
    //    handful of groups.
    //  - budgeted drains: 200 groups/frame, 350 hides/frame, 600 shows/frame
    //    (shows keep priority — a delayed hide is invisible, a delayed show is the
    //    turn-a-corner-and-the-wall-is-missing pop).
    //  - BakeGroup.Toggle rerouted to renderer.enabled (forceRenderingOff does not
    //    affect statically-batched renderers).
    //  - never-cull whitelist: loot containers + doorway-sliver wall deco the bake's
    //    sampling chronically misses.
    //  - cross-cull: an interior volume only culls for cameras INSIDE it — from
    //    outside, far interior groups go dark wholesale.
    internal class TerminalCullingDriver : MonoBehaviour
    {
        internal static TerminalCullingDriver Instance;
        internal static Camera CameraRef;

        private int _frames;

        internal static void ResetForNewRaid()
        {
            _rendererPosMap = null;
            _pcPending.Clear();
            _pcGroupPending.Clear();
            _pcWhitelistCache.Clear();
            _pcLcRoots = null;
            _pcVols.Clear();
            _xvols.Clear();
            _crossForced.Clear();
            _amCachedProfile = null;
            _amEnabledParam = null;
            if (Instance != null)
            {
                Instance._frames = 0;
                Instance._amandsStateLogged = false;
            }
        }

        // probe host rides the camera lifecycle — SetCamera fires on every raid
        [HarmonyPatch(typeof(EFT.CameraControl.CameraManager), "SetCamera", typeof(Camera))]
        internal static class Patch_CaptureCamera
        {
            [HarmonyPostfix]
            private static void Postfix(Camera camera)
            {
                if (!TerminalGate.On)
                {
                    CameraRef = null;
                    TerminalRenderProfiler.Disable();
                    TerminalFramePhase.Disable();
                    return;
                }
                if (Instance == null)
                {
                    var go = new GameObject("Manimal_TerminalCullingDriver");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    Instance = go.AddComponent<TerminalCullingDriver>();
                }
                CameraRef = camera;
                // camera ref is now valid — arm the render-time profiler so it
                // measures Unity's per-frame render pass on this camera
                TerminalRenderProfiler.EnsureSubscribed();
                TerminalFramePhase.EnsureSpawned();
            }
        }

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_AttachAtRaidStart
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On || Instance == null) return;
                Instance.StartCoroutine(Instance.AttachSoon());
            }
        }

        private IEnumerator AttachSoon()
        {
            // let the raid-start frame pass — the attach does a world sweep
            yield return null;
            AttachCullingCamera();
            // loot tracking is per-raid state — a stale list from the last raid would hold
            // destroyed renderers and a stale hidden flag
            TerminalLootBounds.ResetForRaid();
            // LOD cull-floor RESTORED 2026-08-22. it was pulled on 08-20 blamed for
            // the periodic GPU spikes; the real cause turned out to be weapon-light
            // shadow maps accumulating on bots and corpses (TerminalShadowGuard).
            // the re-tier sweep was never it.
            yield return Instance.StartCoroutine(TerminalLodCullFloor.Apply());
        }

        // QualitySettings is GLOBAL engine state — it survives scene unload, and vanilla
        // only rewrites lodBias at boot or on a settings apply. leaving the clamp set
        // after a terminal raid gives every OTHER map our bias and its pop-ins until the
        // player restarts (icebreaker shipped exactly this bug, field-reported 08-18).
        // called from the !TerminalGate.On branch of Update.
        private static float _biasOrig = -1f;
        private static float _lastBiasWritten = float.NaN;

        private static void RestoreLodBias()
        {
            if (_biasOrig < 0f) return;
            QualitySettings.lodBias = _biasOrig;
            Plugin.Log.LogInfo($"[LOD] left the terminal — lodBias restored to the game's {_biasOrig:F2}");
            _biasOrig = -1f;
            _lastBiasWritten = float.NaN;
        }

        private void TickLootBounds()
        {
            try
            {
                float want = Plugin.LodBiasClamp.Value;
                if (want > 0f)
                {
                    if (_biasOrig < 0f) _biasOrig = QualitySettings.lodBias; // capture BEFORE the first write
                    // only write when it actually differs — this runs every frame
                    if (!Mathf.Approximately(QualitySettings.lodBias, want) || !Mathf.Approximately(_lastBiasWritten, want))
                    {
                        QualitySettings.lodBias = want;
                        _lastBiasWritten = want;
                    }
                }
                else RestoreLodBias(); // config flipped to hands-off mid-raid
                if (CameraRef != null) TerminalLodCullFloor.Tick(CameraRef.transform.position);
                TerminalLootBounds.Tick();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[LodCullFloor] tick failed: {e.Message}"); }
        }

        private void Update()
        {
            // F8 env dump works on ANY map — the whole point is diffing a working
            // map's block against terminal's
            if (UnityEngine.Input.GetKeyDown(KeyCode.F8))
                TerminalEnvDump.Dump(TerminalGate.On ? "terminal" : "vanilla");

            // the bias is global engine state — restore it the moment we're off
            // terminal, or every other map inherits our clamp until a restart
            if (!TerminalGate.On) { RestoreLodBias(); return; }
            _frames++;
            long t0;
            t0 = System.Diagnostics.Stopwatch.GetTimestamp(); TickLootBounds();       TerminalTickProfiler.Add("LootBnds",  System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            t0 = System.Diagnostics.Stopwatch.GetTimestamp(); TickPcDriver();         TerminalTickProfiler.Add("PcDrv",     System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            t0 = System.Diagnostics.Stopwatch.GetTimestamp(); DrainPcGroupToggles();  TerminalTickProfiler.Add("PcGrpDrn",  System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            t0 = System.Diagnostics.Stopwatch.GetTimestamp(); DrainPcToggles();       TerminalTickProfiler.Add("PcTogDrn",  System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            t0 = System.Diagnostics.Stopwatch.GetTimestamp(); TickCrossCull();        TerminalTickProfiler.Add("XCull",     System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            t0 = System.Diagnostics.Stopwatch.GetTimestamp(); TickDeadEffectGuard();  TerminalTickProfiler.Add("DeadFxGd",  System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            t0 = System.Diagnostics.Stopwatch.GetTimestamp(); TickAmandsReapply();     TerminalTickProfiler.Add("Amands",    System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        }

        // ------------------------------------------------------------------- attach

        private static void AttachCullingCamera()
        {
            try
            {
                // native stand-down backstop: terminal's culling scene ships stubless
                // (nothing binds), but if a future bundle ever carries a live BSG grid
                // with valid packed data, running our driver on top would double-toggle
                var nativeGridT = Type.GetType("Koenigz.PerfectCulling.EFT.PerfectCullingAdaptiveGrid, Assembly-CSharp");
                var nativeGrid = nativeGridT?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (nativeGrid != null && !nativeGrid.Equals(null))
                {
                    bool packedOk;
                    try
                    {
                        var packed = nativeGridT.GetProperty("PackedData")?.GetValue(nativeGrid);
                        packedOk = packed != null && Equals(packed.GetType().GetProperty("IsValid")?.GetValue(packed), true);
                    }
                    catch { packedOk = true; }
                    if (packedOk)
                    {
                        Plugin.Log.LogDebug("[Culling] NATIVE BSG culling grid alive with valid packed data — sidecar driver standing down");
                        return;
                    }
                    Plugin.Log.LogWarning("[Culling] native grid present but packed bake NOT loaded — falling back to sidecar driver");
                }

                var camType = Type.GetType("Koenigz.PerfectCulling.PerfectCullingCamera, PerfectCullingRuntime");
                if (camType == null) { Plugin.Log.LogDebug("[Culling] PerfectCullingRuntime not loaded — no culling"); return; }
                var volType = Type.GetType("Koenigz.PerfectCulling.PerfectCullingVolume, PerfectCullingRuntime");
                if (volType == null) { Plugin.Log.LogWarning("[Culling] volume type missing"); return; }

                var dir = SysIoPath.Combine(SysIoPath.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "culling");
                if (System.IO.Directory.Exists(dir))
                    foreach (var f in System.IO.Directory.GetFiles(dir, "*.pcbake"))
                        RehydrateVolumeFromSidecar(f, volType);

                var vols = UnityEngine.Object.FindObjectsOfType(volType);
                if (vols == null || vols.Length == 0)
                {
                    Plugin.Log.LogDebug("[Culling] no live volumes (no sidecars yet?) — no occlusion culling this raid");
                    return;
                }

                NeuterEditorOnlyCallbacks(camType);
                try
                {
                    BuildPcDriver(vols);
                    Plugin.Log.LogInfo($"[Culling] sliced PC driver armed — {vols.Length} volume(s), 1 volume/frame, per-volume cell gating");
                }
                catch (Exception de) { Plugin.Log.LogWarning($"[Culling] driver build failed: {de.Message}"); }

                BuildCrossCull(volType);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Culling] attach failed: {e.Message}"); }
        }

        // ---------------------------------------------------------------- rehydrate

        private static Dictionary<string, List<Renderer>> _rendererPosMap;

        // quantize to 5cm so tiny float drift between editor and runtime doesn't miss
        private static string PosKey(string name, Vector3 p)
            => $"{name}|{Mathf.RoundToInt(p.x * 20)}|{Mathf.RoundToInt(p.y * 20)}|{Mathf.RoundToInt(p.z * 20)}";

        private static void DisableInvalidVolume(string volName, Type volType)
        {
            foreach (var existing in UnityEngine.Object.FindObjectsOfType(volType))
            {
                var comp = existing as Component;
                if (comp != null && comp.name == volName)
                    comp.gameObject.SetActive(false);
            }
        }

        private static void RehydrateVolumeFromSidecar(string file, Type volType)
        {
            var volName = SysIoPath.GetFileNameWithoutExtension(file);
            try
            {
                var asm = volType.Assembly;
                var bdType = asm.GetType("Koenigz.PerfectCulling.PerfectCullingVolumeBakeData");
                var visType = bdType.GetNestedType("VisibilitySet");
                var groupType = asm.GetType("Koenigz.PerfectCulling.PerfectCullingBakeGroup");

                using var r = new System.IO.BinaryReader(System.IO.File.OpenRead(file));
                if (r.ReadString() != "PCBK3") { Plugin.Log.LogWarning($"[Culling] {volName}: old/bad sidecar format — re-export from the editor"); return; }
                r.ReadString(); // volume name (== file name)
                var volPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var volRot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var volSize = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var bakeCell = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var cellCount = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var cellSize = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var orientation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                int numberOfGroups = r.ReadInt32();
                int cells = r.ReadInt32();

                // Validate before creating a volume or allocating a potentially huge
                // array. Perfect Culling allocates every cell at bake start and only
                // sets bakeCompleted after the final merge. An editor crash therefore
                // leaves a convincing-sized array containing nothing but zero cells;
                // the old exporter and loader both accepted it as a real bake.
                if (cells <= 0) throw new System.IO.InvalidDataException($"invalid cell count {cells}");
                long cellDataStart = r.BaseStream.Position;
                int usefulCells = 0;
                for (int i = 0; i < cells; i++)
                {
                    if (r.BaseStream.Length - r.BaseStream.Position < 6)
                        throw new System.IO.EndOfStreamException($"cell {i} header is truncated");
                    ushort len = r.ReadUInt16();
                    int byteCount = r.ReadInt32();
                    if (byteCount < 0 || byteCount > r.BaseStream.Length - r.BaseStream.Position)
                        throw new System.IO.InvalidDataException($"cell {i} has invalid payload length {byteCount}");
                    if (len > 0 && byteCount > 0) usefulCells++;
                    r.BaseStream.Position += byteCount;
                }
                if (usefulCells == 0)
                {
                    DisableInvalidVolume(volName, volType);
                    Plugin.Log.LogWarning($"[Culling] REJECTED {volName}: {cells} allocated cells but zero contain visibility data (interrupted/corrupt bake); volume disabled until rebaked");
                    return;
                }
                r.BaseStream.Position = cellDataStart;

                // find-or-create: the bundle may carry an (empty) volume by this name;
                // otherwise build fresh — disabled until populated so OnEnable's
                // registration check runs against real data
                Component vol = null;
                foreach (var existing in UnityEngine.Object.FindObjectsOfType(volType))
                    if ((existing as Component)?.name == volName) { vol = existing as Component; break; }
                if (vol == null)
                {
                    var go = new GameObject(volName);
                    go.SetActive(false);
                    vol = go.AddComponent(volType) as Component;
                }
                vol.transform.SetPositionAndRotation(volPos, volRot);
                volType.GetField("volumeSize").SetValue(vol, volSize);
                volType.GetField("bakeCellSize").SetValue(vol, bakeCell);
                var dataArr = Array.CreateInstance(visType, cells);
                var fCompressed = visType.GetField("compressed");
                var fLen = visType.GetField("len");
                for (int i = 0; i < cells; i++)
                {
                    ushort len = r.ReadUInt16();
                    var bytes = r.ReadBytes(r.ReadInt32());
                    var cell = Activator.CreateInstance(visType);
                    fLen.SetValue(cell, len);
                    fCompressed.SetValue(cell, bytes);
                    dataArr.SetValue(cell, i);
                }

                // groups: renderers resolved by NAME + quantized WORLD POSITION —
                // geometry doesn't move, so this survives the hierarchy churn that broke
                // path identity across bundle rebuilds (twice, on icebreaker)
                if (_rendererPosMap == null)
                {
                    _rendererPosMap = new Dictionary<string, List<Renderer>>();
                    foreach (var rend in Resources.FindObjectsOfTypeAll<Renderer>())
                    {
                        if (!rend.gameObject.scene.isLoaded) continue;
                        var k = PosKey(rend.name, rend.transform.position);
                        if (!_rendererPosMap.TryGetValue(k, out var lst)) _rendererPosMap[k] = lst = new List<Renderer>(1);
                        lst.Add(rend);
                    }
                }
                int groups = r.ReadInt32(), missing = 0;
                var groupArr = Array.CreateInstance(groupType, groups);
                var fRenderers = groupType.GetField("renderers");
                var fGroupType = groupType.GetField("groupType");
                var userEnum = Enum.Parse(fGroupType.FieldType, "User");
                for (int gi = 0; gi < groups; gi++)
                {
                    int rc = r.ReadInt32();
                    var list = new List<Renderer>(rc);
                    for (int ri = 0; ri < rc; ri++)
                    {
                        var rname = r.ReadString();
                        var rpos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        if (_rendererPosMap.TryGetValue(PosKey(rname, rpos), out var candidates) && candidates.Count > 0)
                        {
                            // pop so identical duplicates each bind a distinct instance
                            var rr = candidates[candidates.Count - 1];
                            candidates.RemoveAt(candidates.Count - 1);
                            if (rr != null) list.Add(rr); else missing++;
                        }
                        else missing++;
                    }
                    var g = Activator.CreateInstance(groupType);
                    fRenderers.SetValue(g, list.ToArray());
                    fGroupType.SetValue(g, userEnum);
                    groupArr.SetValue(g, gi);
                }

                var bd = ScriptableObject.CreateInstance(bdType);
                bdType.GetField("cellCount").SetValue(bd, cellCount);
                bdType.GetField("cellSize").SetValue(bd, cellSize);
                bdType.GetField("orientation").SetValue(bd, orientation);
                bdType.GetField("numberOfGroups").SetValue(bd, numberOfGroups);
                bdType.GetField("data").SetValue(bd, dataArr);
                volType.GetField("volumeBakeData").SetValue(vol, bd);
                volType.GetField("bakeGroups", BindingFlags.Public | BindingFlags.Instance)?.SetValue(vol, groupArr);

                // re-run Start (rebuilds internal renderer-state from the new groups),
                // then make sure OnEnable runs against populated data so the volume
                // registers into AllVolumes
                volType.GetMethod("Start", BindingFlags.Public | BindingFlags.Instance)?.Invoke(vol, null);
                if (!vol.gameObject.activeSelf) vol.gameObject.SetActive(true);
                else { var beh = vol as Behaviour; if (beh != null) { beh.enabled = false; beh.enabled = true; } }

                Plugin.Log.LogInfo($"[Culling] rehydrated {volName}: {cells} cells ({usefulCells} populated), {groups} groups ({missing} renderers unresolved)");
            }
            catch (Exception e)
            {
                DisableInvalidVolume(volName, volType);
                Plugin.Log.LogWarning($"[Culling] rehydrate {volName} failed; volume disabled: {e}");
            }
        }

        // ---------------------------------------------------- toggle path + budgets

        // we ship the EDITOR-compiled PerfectCullingRuntime.dll, so its #if UNITY_EDITOR
        // code is baked in — LateUpdate/OnGUI are editor visualization and NRE every
        // frame in the player. the real culling path (OnPreCull) is clean runtime code.
        private static bool _cullingNeutered;
        private static FieldInfo _groupRenderersField;

        private static void NeuterEditorOnlyCallbacks(Type camType)
        {
            if (_cullingNeutered) return;
            _cullingNeutered = true;
            var h = new Harmony("com.manimal.terminal.culling");
            foreach (var name in new[] { "LateUpdate", "OnGUI" })
            {
                var m = AccessTools.Method(camType, name);
                if (m != null)
                    h.Patch(m, prefix: new HarmonyMethod(typeof(TerminalCullingDriver), nameof(SkipOriginal)));
            }

            // PC hides culled renderers via forceRenderingOff — which does NOT affect
            // statically-batched renderers. patch BakeGroup.Toggle ITSELF (the tiny
            // static helper inside it gets JIT-inlined) to flip renderer.enabled.
            var groupT = camType.Assembly.GetType("Koenigz.PerfectCulling.PerfectCullingBakeGroup");
            var toggle = groupT != null ? AccessTools.Method(groupT, "Toggle") : null;
            if (toggle != null)
            {
                _groupRenderersField = groupT.GetField("renderers");
                h.Patch(toggle, prefix: new HarmonyMethod(typeof(TerminalCullingDriver), nameof(ToggleGroupViaEnabled)));
                Plugin.Log.LogDebug("[Culling] BakeGroup.Toggle replaced with renderer.enabled path (static-batching compatible)");
            }
            Plugin.Log.LogDebug("[Culling] editor-only callbacks neutered on PerfectCullingCamera");
        }

        private static bool SkipOriginal() => false;

        // budgets — icebreaker's measured line: ~350 smooth where 1500 froze; shows get
        // priority (a delayed hide is invisible, a delayed show is visible pop)
        private const int PcApplyBudget = 350;
        private const int PcShowBudget = 600;
        private const int PcGroupBudget = 200;
        private static readonly Dictionary<Renderer, bool> _pcPending = new Dictionary<Renderer, bool>();
        private static readonly List<Renderer> _pcDrainScratch = new List<Renderer>(PcShowBudget);
        private static readonly Dictionary<object, bool> _pcGroupPending = new Dictionary<object, bool>();
        private static readonly List<object> _pcGroupScratch = new List<object>(1024);

        // doorway-sliver wall deco the bake's sampling chronically misses at any
        // resolution — pops in plain view. seeded with icebreaker's proven patterns
        // (harmless when absent); extend from terminal playtesting. loot containers are
        // protected structurally below.
        private static readonly string[] PcNeverCull = { "curtains_", "combination_lock", "cpu_panel" };
        private static readonly Dictionary<object, bool> _pcWhitelistCache = new Dictionary<object, bool>();
        private static HashSet<Transform> _pcLcRoots; // lazy per raid

        private static HashSet<Transform> BuildLootContainerRoots()
        {
            var lcRoots = new HashSet<Transform>();
            foreach (var lc in UnityEngine.Object.FindObjectsOfType<EFT.Interactive.LootableContainer>(true))
            {
                var root = lc.transform.parent != null ? lc.transform.parent : lc.transform;
                var walk = lc.transform;
                for (int hop = 0; walk != null && hop < 4; hop++, walk = walk.parent)
                    if (walk.GetComponent<LODGroup>() != null) { root = walk; break; }
                lcRoots.Add(root);
            }
            return lcRoots;
        }

        private static bool IsNeverCullGroup(object group, Renderer[] rs)
        {
            if (_pcWhitelistCache.TryGetValue(group, out var hit)) return hit;
            hit = false;
            if (rs != null)
                foreach (var r in rs)
                {
                    if (r == null) continue;
                    var n = r.name.ToLowerInvariant();
                    foreach (var pat in PcNeverCull)
                        if (n.Contains(pat)) { hit = true; break; }
                    // loot containers go PERMANENTLY invisible on stale sightline data —
                    // never occlusion-cull them. sibling-aware root check: the
                    // LootableContainer is a SIBLING of the meshes, not a parent.
                    if (!hit)
                    {
                        if (_pcLcRoots == null) _pcLcRoots = BuildLootContainerRoots();
                        for (var w = r.transform; w != null && !hit; w = w.parent)
                            if (_pcLcRoots.Contains(w)) hit = true;
                    }
                    if (hit) break;
                }
            _pcWhitelistCache[group] = hit;
            return hit;
        }

        private static bool ToggleGroupViaEnabled(object __instance, bool isVisible)
        {
            // groups force-culled by the cross-cull stay dark no matter what the volume decides
            if (isVisible && _crossForced.Contains(__instance)) isVisible = false;
            var rs = _groupRenderersField?.GetValue(__instance) as Renderer[];
            if (!isVisible && IsNeverCullGroup(__instance, rs)) isVisible = true;
            if (rs != null)
                foreach (var r in rs)
                    if (r != null && r.enabled != isVisible)
                        _pcPending[r] = isVisible;
                    else if (r != null)
                        _pcPending.Remove(r); // re-decided back to current state — drop stale queue entry
            return false; // skip original entirely
        }

        // ------------------------------------------------------------ sliced driver

        private class PcVol
        {
            public UnityEngine.Object Vol;
            public int LastCell = int.MinValue;
            public MethodInfo GetIndex, GetIndices;
            public MethodInfo QueueAll, Execute;
            public object[] Groups;
            public HashSet<int> VisSet;
        }
        private static readonly List<PcVol> _pcVols = new List<PcVol>();
        private static readonly List<ushort> _pcIndices = new List<ushort>(2048);
        private static int _pcvCursor;

        private static void BuildPcDriver(UnityEngine.Object[] vols)
        {
            _pcVols.Clear();
            _pcGroupPending.Clear();
            _pcWhitelistCache.Clear();
            _pcLcRoots = null;
            foreach (var v in vols)
            {
                var t = v.GetType();
                var fGroups = t.GetField("bakeGroups", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                var groupsArr = fGroups?.GetValue(v) as Array;
                var groups = new object[groupsArr?.Length ?? 0];
                if (groupsArr != null) groupsArr.CopyTo(groups, 0);
                _pcVols.Add(new PcVol
                {
                    Vol = v,
                    // explicit overload binds — the bare name is AmbiguousMatch, which
                    // once aborted icebreaker's whole culling attach
                    GetIndex = AccessTools.Method(t, "GetIndexForWorldPos", new[] { typeof(Vector3), typeof(bool).MakeByRefType() }),
                    GetIndices = AccessTools.Method(t, "GetIndicesForWorldPos"),
                    QueueAll = AccessTools.Method(t, "QueueToggleAllRenderers"),
                    Execute = AccessTools.Method(t, "ExecuteQueue"),
                    Groups = groups,
                });
            }
        }

        internal static int _cullNothingFrames;
        private static float _lastCullNothingLog;
        private static bool _pcDriverWasOff;
        private static void TickPcDriver()
        {
            // live kill switch — the pop-isolation tool: flip PcDriverEnabled off,
            // everything occlusion-culled restores; pops stop = the BAKE's data is stale
            if (!Plugin.PcDriverEnabled.Value)
            {
                if (!_pcDriverWasOff)
                {
                    _pcDriverWasOff = true;
                    foreach (var vol in _pcVols)
                    {
                        if (vol.Vol == null || vol.QueueAll == null || vol.Execute == null) continue;
                        try
                        {
                            vol.QueueAll.Invoke(vol.Vol, new object[] { true });
                            vol.Execute.Invoke(vol.Vol, new object[] { true });
                            vol.LastCell = int.MinValue;
                            vol.VisSet = null;
                        }
                        catch { }
                    }
                    Plugin.Log.LogDebug("[Culling] PC driver DISABLED (live) — occlusion-culled renderers restored");
                }
                return;
            }
            if (_pcDriverWasOff) { _pcDriverWasOff = false; Plugin.Log.LogDebug("[Culling] PC driver re-enabled (live)"); }

            if (_pcVols.Count == 0 || CameraRef == null) return;
            _pcvCursor = (_pcvCursor + 1) % _pcVols.Count;
            var pv = _pcVols[_pcvCursor];
            if (pv.Vol == null || pv.GetIndex == null) return;
            try
            {
                var camPos = CameraRef.transform.position;
                var args = new object[] { camPos, false };
                int cell = (int)pv.GetIndex.Invoke(pv.Vol, args);
                if (cell == pv.LastCell) return; // camera still in this volume's same cell
                pv.LastCell = cell;

                // SET DIFF, not full reapply — adjacent cells differ by a handful of
                // groups; a full sweep per crossing was THE engine-room-entry hitch
                _pcIndices.Clear();
                pv.GetIndices.Invoke(pv.Vol, new object[] { camPos, _pcIndices });
                var newSet = new HashSet<int>();
                foreach (var idx in _pcIndices) newSet.Add(idx);
                bool cullNothing = newSet.Count == 0; // empty/unbaked cell: show all

                // AN UNBAKED CELL TURNS CULLING OFF FOR THE WHOLE VOLUME. that means
                // every group it owns renders — ContainerPort_B_Courtyard alone is
                // 2450 — and walking in and out of such cells would read exactly like
                // the chop: GPU-bound, one region, cyclic, indifferent to bots and
                // view direction. count it so we know whether that's what's happening.
                if (cullNothing)
                {
                    _cullNothingFrames++;
                    if (Time.realtimeSinceStartup - _lastCullNothingLog > 5f)
                    {
                        _lastCullNothingLog = Time.realtimeSinceStartup;
                        var vc = pv.Vol as Component;
                        Plugin.Log.LogWarning($"[Culling] UNBAKED CELL on '{(vc != null ? vc.name : "?")}' cell={cell} — "
                            + $"culling OFF for this volume, all {pv.Groups.Length} group(s) rendering "
                            + $"({_cullNothingFrames} such cell-entries this raid)");
                    }
                }

                if (pv.VisSet == null)
                {
                    // first apply for this volume: full pass, sliced by the group drain
                    for (int i = 0; i < pv.Groups.Length; i++)
                        _pcGroupPending[pv.Groups[i]] = cullNothing || newSet.Contains(i);
                }
                else
                {
                    for (int i = 0; i < pv.Groups.Length; i++)
                    {
                        bool was = pv.VisSet.Count == 0 || pv.VisSet.Contains(i);
                        bool now = cullNothing || newSet.Contains(i);
                        if (was != now) _pcGroupPending[pv.Groups[i]] = now;
                    }
                }
                pv.VisSet = newSet;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Culling] driver failed on volume: {e.Message}");
                pv.Vol = null; // don't retry a broken volume every cycle
            }
        }

        private static void DrainPcGroupToggles()
        {
            if (_pcGroupPending.Count == 0) return;
            _pcGroupScratch.Clear();
            int budget = PcGroupBudget;
            foreach (var kv in _pcGroupPending)
            {
                if (budget-- <= 0) break;
                ToggleGroupViaEnabled(kv.Key, kv.Value);
                _pcGroupScratch.Add(kv.Key);
            }
            foreach (var g in _pcGroupScratch) _pcGroupPending.Remove(g);
        }

        private static void DrainPcToggles()
        {
            if (_pcPending.Count == 0) return;
            _pcDrainScratch.Clear();
            int showBudget = PcShowBudget;
            foreach (var kv in _pcPending)
            {
                if (!kv.Value) continue;
                if (showBudget-- <= 0) break;
                var r = kv.Key;
                if (r != null && !r.enabled) r.enabled = true;
                _pcDrainScratch.Add(r);
            }
            int budget = PcApplyBudget;
            foreach (var kv in _pcPending)
            {
                if (kv.Value) continue; // shows handled above
                if (budget-- <= 0) break;
                var r = kv.Key;
                if (r != null && r.enabled) r.enabled = false;
                _pcDrainScratch.Add(r);
            }
            foreach (var r in _pcDrainScratch) _pcPending.Remove(r);
        }

        // ------------------------------------------------ cross-volume interior cull

        private class XVol
        {
            public string Name;
            public Bounds B;
            public object[] Groups;
            public Bounds[] GB;
            public bool[] Forced;
            public bool[] OpenSightline;
        }
        private static readonly List<XVol> _xvols = new List<XVol>();
        private static readonly HashSet<object> _crossForced = new HashSet<object>();
        private static int _xcullTick;

        private static void BuildCrossCull(Type volType)
        {
            _xvols.Clear();
            _crossForced.Clear();
            if (_groupRenderersField == null) return;
            var fGroups = volType.GetField("bakeGroups", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var v in UnityEngine.Object.FindObjectsOfType(volType))
            {
                var comp = v as Component;
                if (comp == null || !comp.name.Contains("Indoor")) continue;
                var groups = fGroups?.GetValue(v) as Array;
                if (groups == null || groups.Length == 0) continue;
                var xv = new XVol { Name = comp.name, Groups = new object[groups.Length], GB = new Bounds[groups.Length], Forced = new bool[groups.Length], OpenSightline = new bool[groups.Length] };
                bool haveB = false;
                int openSightline = 0;
                for (int i = 0; i < groups.Length; i++)
                {
                    var g = groups.GetValue(i);
                    xv.Groups[i] = g;
                    var rs = _groupRenderersField.GetValue(g) as Renderer[];
                    if (comp.name == "CullingVolume_Terminal_Area_04_MarineStation_Indoor" && IsMarineStationInteriorShell(rs))
                    {
                        xv.OpenSightline[i] = true;
                        openSightline++;
                    }
                    Bounds gb = default;
                    bool haveG = false;
                    if (rs != null)
                        foreach (var r in rs)
                            if (r != null)
                            {
                                if (!haveG) { gb = r.bounds; haveG = true; }
                                else gb.Encapsulate(r.bounds);
                            }
                    xv.GB[i] = gb;
                    if (haveG)
                    {
                        if (!haveB) { xv.B = gb; haveB = true; }
                        else xv.B.Encapsulate(gb);
                    }
                }
                xv.B.Expand(3f); // doorway grace at the seams
                _xvols.Add(xv);
                Plugin.Log.LogDebug($"[XCull] interior volume '{xv.Name}': {xv.Groups.Length} groups, bounds {xv.B.size}"
                    + (openSightline > 0 ? $", {openSightline} open-hangar shell group(s) exempt" : ""));
            }
        }

        private static bool IsMarineStationInteriorShell(Renderer[] renderers)
        {
            if (renderers == null) return false;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                if (r.name.StartsWith("Marine_Station_01_INDOOR") || r.name.StartsWith("Marine_Station_01_Fakeindoor"))
                    return true;
            }
            return false;
        }

        // every ~15 frames: camera outside an interior volume -> its far groups go dark.
        // near groups stay (stairwell/doorway/window sightlines). all toggles flow
        // through the pending queue so big flips settle under the same frame budget.
        private static void TickCrossCull()
        {
            if (!Plugin.InteriorCrossCull.Value || _xvols.Count == 0 || CameraRef == null) return;
            if ((++_xcullTick % 15) != 0) return;
            var cp = CameraRef.transform.position;
            float d2 = Plugin.CrossCullDistance.Value * Plugin.CrossCullDistance.Value;
            foreach (var xv in _xvols)
            {
                bool inside = xv.B.Contains(cp);
                for (int i = 0; i < xv.Groups.Length; i++)
                {
                    bool wantForce = !xv.OpenSightline[i] && !inside && xv.GB[i].SqrDistance(cp) > d2;
                    if (wantForce == xv.Forced[i]) continue;
                    xv.Forced[i] = wantForce;
                    if (wantForce) _crossForced.Add(xv.Groups[i]);
                    else _crossForced.Remove(xv.Groups[i]);
                    ToggleGroupViaEnabled(xv.Groups[i], !wantForce);
                }
            }
        }

        // ---------------------------------------------------------- dead-effect guard

        // the Cam2 fallback cannot host UltimateBloom: every RETAIL camera ships one,
        // configured by BSG — a graphics mod that expects one and constructs it here
        // leaves an unconfigured renderer LAST in the effect chain, which owns the
        // final blit and swallows the whole frame (black screen + HUD burn-in).
        // GlobalFog: Cam2-chassis legacy fog effect no retail map runs (ground zero's
        // camera doesn't even carry it) — unconfigured it paints a white distance wash
        // over the frame and auto-exposure then crushes everything else black (the
        // first-raid "blinding fog + pitch black world", UE-peeler-confirmed 08-09).
        // continuous, once a second — the FPS camera SURVIVES across raids and mods
        // re-enable their effects every raid start.
        private static readonly HashSet<string> CannotHostOnCam2 = new HashSet<string> { "UltimateBloom", "GlobalFog" };
        private static readonly HashSet<Behaviour> _strippedEffects = new HashSet<Behaviour>();
        private static readonly List<Component> _guardScratch = new List<Component>(80);

        private void TickDeadEffectGuard()
        {
            if (_frames < 120 || _frames % 60 != 0) return;
            try
            {
                var cam = CameraRef != null ? CameraRef : Camera.main;
                if (cam == null) return;
                cam.GetComponents(_guardScratch);
                foreach (var c in _guardScratch)
                {
                    if (c == null || !(c is Behaviour b) || !b.enabled) continue;
                    if (!CannotHostOnCam2.Contains(c.GetType().Name)) continue;
                    // the HG provision is NOT exempt — its job is only to keep HG's init
                    // alive; the renderer itself gets parked and HG writes its settings
                    // into a disabled component, harmlessly
                    b.enabled = false;
                    if (_strippedEffects.Add(b))
                        Plugin.Log.LogWarning($"[RaidFix] disabled {c.GetType().Name} on the terminal camera — the Cam2 "
                            + "fallback has no retail configuration for it; unconfigured at the END of the effect chain "
                            + "it swallows the whole frame (black screen + HUD burn-in). that mod's bloom is off on this map only.");
                    else
                        Plugin.Log.LogInfo($"[RaidFix] re-parked {c.GetType().Name} — its owner re-enabled it (new raid on the persistent camera)");
                }
            }
            catch (Exception e) { Plugin.Log.LogDebug($"[RaidFix] dead-effect guard: {e.Message}"); }
        }

        // ---------------------------------------------------- Amands Graphics reconcile

        // Amands injects MotionBlur into the camera's PostProcessVolume and disables
        // the profile instance it captured during camera activation. Terminal then
        // finishes repairing/grafting the Cam2 fallback, which can replace the live
        // profile while Amands still holds the old instance. The replacement may carry
        // enabled default motion blur even when the user's Amands config says Off.
        // Reconcile the LIVE profile continuously, as Icebreaker does; this also covers
        // the persistent FPS camera being reused across consecutive raids.
        private bool _amandsStateLogged;
        private static bool _amResolved;
        private static bool _amPresent;
        private static BepInEx.Configuration.ConfigEntryBase _amMbCfg;
        private static Type _amVolType;
        private static PropertyInfo _amProfileProp;
        private static object _amCachedProfile;
        private static object _amEnabledParam;
        private static FieldInfo _amValueField;
        private static MethodInfo _amOverride;

        private void TickAmandsReapply()
        {
            // Reflection/type discovery is cached session-wide. The steady-state call
            // is two field reads every ~2 seconds, avoiding the rhythmic stutter caused
            // by resolving types on every sweep in Icebreaker's early implementation.
            if (_frames < 300 || _frames % 120 != 0) return;
            try
            {
                if (!_amResolved)
                {
                    _amResolved = true;
                    _amPresent = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.Amanda.Graphics");
                    if (_amPresent)
                    {
                        var pluginType = Type.GetType("AmandsGraphics.AmandsGraphicsPlugin, AmandsGraphics");
                        _amMbCfg = pluginType?.GetProperty("MotionBlur",
                            BindingFlags.Public | BindingFlags.Static)
                            ?.GetValue(null) as BepInEx.Configuration.ConfigEntryBase;
                        _amVolType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessVolume");
                        if (_amVolType != null) _amProfileProp = AccessTools.Property(_amVolType, "profile");
                        _amPresent = _amMbCfg != null && _amProfileProp != null;
                    }
                }
                if (!_amPresent) return;

                bool wantOn = _amMbCfg.BoxedValue?.ToString() == "On";
                var cam = CameraRef != null ? CameraRef : Camera.main;
                if (cam == null) return;
                var volume = cam.GetComponent(_amVolType);
                if (volume == null) return;
                var profile = _amProfileProp.GetValue(volume);
                if (profile == null) return;

                if (!ReferenceEquals(profile, _amCachedProfile))
                {
                    _amCachedProfile = profile;
                    _amEnabledParam = null;
                    _amValueField = null;
                    _amOverride = null;
                    var settings = AccessTools.Field(profile.GetType(), "settings")
                        ?.GetValue(profile) as System.Collections.IEnumerable;
                    if (settings != null)
                        foreach (var setting in settings)
                            if (setting != null && setting.GetType().Name == "MotionBlur")
                            {
                                _amEnabledParam = AccessTools.Field(setting.GetType(), "enabled")?.GetValue(setting);
                                if (_amEnabledParam != null)
                                {
                                    _amValueField = AccessTools.Field(_amEnabledParam.GetType(), "value");
                                    _amOverride = AccessTools.Method(_amEnabledParam.GetType(), "Override");
                                }
                                break;
                            }
                }

                if (!_amandsStateLogged)
                {
                    _amandsStateLogged = true;
                    bool liveNow = _amEnabledParam != null && _amValueField != null
                        && (bool)_amValueField.GetValue(_amEnabledParam);
                    Plugin.Log.LogInfo($"[Amands] reconcile armed — config MotionBlur={_amMbCfg.BoxedValue}, live profile MotionBlur "
                        + $"{(_amEnabledParam == null ? "ABSENT" : liveNow ? "enabled" : "disabled")}");
                }

                if (_amEnabledParam == null || _amValueField == null) return;
                if ((bool)_amValueField.GetValue(_amEnabledParam) && !wantOn)
                {
                    _amOverride?.Invoke(_amEnabledParam, new object[] { false });
                    Plugin.Log.LogWarning("[Amands] live profile MotionBlur was ON with config Off — overridden off "
                        + "(Amands disabled a stale profile while Terminal's camera repair installed the live one)");
                }
            }
            catch (Exception e)
            {
                if (!_amandsStateLogged)
                {
                    _amandsStateLogged = true;
                    Plugin.Log.LogWarning($"[Amands] reconcile failed (cosmetic only): {e.Message}");
                }
            }
        }
    }
}
