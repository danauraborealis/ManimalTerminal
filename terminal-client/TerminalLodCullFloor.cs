using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Manimal.Terminal
{
    // LOD CULL FLOOR, ported from icebreaker 08-13 (user call). a LODGroup's LAST
    // threshold is not a mesh swap — below it unity stops rendering the object
    // (crossfaded, hence the fade in/out). BSG authored those heights assuming lod
    // bias >= 2 (their slider clamps there, verified in GraphicsSettingsClass), so a
    // sub-2 LodBiasClamp makes aggressive cullers vanish in plain sight. tiering by
    // distance resolves the fps-vs-pop tradeoff instead of splitting it: groups NEAR
    // the camera get a tiny protective floor (nothing dithers in your face), and
    // everything past the radius gets the aggressive far floor (the fps lever).
    //
    // TERMINAL IS NOT ICEBREAKER. the ship is tight CQB where a 27m outdoor bubble and
    // 15m cells were right; terminal is a large open map, so LodCellSize and both radii
    // default much higher here and are exposed for tuning (user call: "will just need to
    // be tuned up higher"). the indoor/outdoor split rides retail's own EnvironmentManager,
    // which TerminalAcoustics rebuilds along with its 64 IndoorTriggers — on a map this
    // size the difference between a hangar interior and the open apron is the whole point.
    internal static class TerminalLodCullFloor
    {
        private class Cell
        {
            public readonly List<int> Members = new List<int>();
            public float Applied = float.NaN;
        }

        private static LODGroup[] _g;
        private static float[] _orig;
        private static bool[] _neverCull;
        private static Dictionary<Vector3Int, Cell> _cells;
        private static readonly Queue<Cell> _dirty = new Queue<Cell>();
        private static readonly Queue<float> _dirtyWant = new Queue<float>();
        private static int _n;
        private static bool _built;
        private static float _cellSize = 30f;   // snapshotted at build — cells are baked around it
        private static Vector3Int _camCell = new Vector3Int(int.MinValue, 0, 0);
        private static float _lastNear = float.NaN, _lastFar = float.NaN;
        private static float _lastRadius = float.NaN;
        private static bool _wasIndoor;
        private static Cell _draining;
        private static float _drainWant;
        private static int _drainIdx;
        private static readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();

        private static Vector3Int Key(Vector3 p) => new Vector3Int(
            Mathf.RoundToInt(p.x / _cellSize), Mathf.RoundToInt(p.y / _cellSize), Mathf.RoundToInt(p.z / _cellSize));

        internal static IEnumerator Apply()
        {
            _built = false; _n = 0;
            // cell size is read ONCE per raid: the buckets are quantized around it, so
            // changing it live would orphan every existing cell. the knob takes effect on
            // the next raid, which the config description says out loud.
            _cellSize = Mathf.Max(5f, Plugin.LodCellSize.Value);
            _cells = new Dictionary<Vector3Int, Cell>(8192);
            _dirty.Clear(); _dirtyWant.Clear(); _draining = null;
            _camCell = new Vector3Int(int.MinValue, 0, 0);
            _lastNear = _lastFar = _lastRadius = float.NaN;

            var groups = UnityEngine.Object.FindObjectsOfType<LODGroup>();
            _g = new LODGroup[groups.Length];
            _orig = new float[groups.Length];
            _neverCull = new bool[groups.Length];
            var histo = new int[5];
            int skippedLoot = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var g in groups)
            {
                if (g == null) continue;
                // map geometry only — bot/weapon rigs manage their own lods
                var sc = g.gameObject.scene.name;
                if (sc == null || !sc.StartsWith("Terminal")) continue;
                var lods = g.GetLODs();
                if (lods == null || lods.Length == 0) continue;
                // LOOT IS GAMEPLAY, NOT DECORATION. these floors are tuned for scenery —
                // a small item drops under them within a couple of metres, so anything
                // lootable captured here would effectively stop rendering.
                if (g.GetComponentInParent<EFT.Interactive.LootItem>() != null
                    || g.GetComponentInParent<EFT.Interactive.LootableContainer>() != null)
                { skippedLoot++; continue; }

                histo[Math.Min(lods.Length, histo.Length - 1)]++;
                _g[_n] = g;
                _orig[_n] = lods[lods.Length - 1].screenRelativeTransitionHeight;
                // Marine Station is an open hangar with long exterior-to-interior
                // sightlines. Its exterior, real-interior and fake-interior shells are
                // each a single-LOD group authored with a 0.03 terminal threshold.
                // That is a hard object cutoff, not a mesh swap: the whole building
                // disappears until the distance-tier sweep happens to put it in the
                // near bucket. Keep these three structural shells alive and let the
                // Perfect Culling visibility bake decide whether they are occluded.
                _neverCull[_n] = IsMarineStationOpenShell(g);
                // bucket by RENDERER bounds, not the group's pivot — ripped prefab roots
                // share a handful of parent origins, and pivot-bucketing collapsed 81k
                // groups into 79 cells on icebreaker's first live run
                Vector3 where = g.transform.position;
                var rs = lods[0].renderers;
                if (rs != null)
                    for (int ri = 0; ri < rs.Length; ri++)
                        if (rs[ri] != null) { where = rs[ri].bounds.center; break; }
                var key = Key(where);
                Cell cell;
                if (!_cells.TryGetValue(key, out cell)) _cells[key] = cell = new Cell();
                cell.Members.Add(_n);
                _n++;
                if (sw.ElapsedMilliseconds > 4) { yield return null; sw.Restart(); }
            }
            _built = true;
            int neverCull = 0;
            for (int i = 0; i < _n; i++) if (_neverCull[i]) neverCull++;
            Plugin.Log.LogInfo($"[LodCullFloor] {_n} map LODGroups in {_cells.Count} cells of {_cellSize:0}m (cell-tiered culling live) | "
                + $"lod counts: 1-lod={histo[1]} 2-lod={histo[2]} 3-lod={histo[3]} 4+lod={histo[4]}"
                + (skippedLoot > 0 ? $" | {skippedLoot} loot group(s) left alone" : "")
                + (neverCull > 0 ? $" | {neverCull} Marine Station shell group(s) protected" : ""));
        }

        private static bool IsMarineStationOpenShell(LODGroup g)
        {
            string scene = g.gameObject.scene.name;
            string name = g.name;
            if (scene == "Terminal_Area_04_MarineStation")
                return name == "Marine_Station_01";
            if (scene == "Terminal_Area_04_MarineStation_Indoor")
                return name == "Marine_Station_01_INDOOR" || name == "Marine_Station_01_FAKEINDOOR";
            return false;
        }

        internal static void Tick(Vector3 cam)
        {
            if (!_built) return;
            float near = Plugin.LodCullNearFloor.Value;
            float far = Plugin.LodCullFloor.Value;

            // retail's own indoor/outdoor state. terminal has no interior-volume fallback
            // of its own, so a missing EnvironmentManager simply reads as outdoor — the
            // wider of the two bubbles, which fails safe (less culling, not more).
            bool indoor = false;
            try
            {
                var em = EFT.EnvironmentEffect.EnvironmentManager.Instance;
                if (em != null) indoor = em.Environment == global::EnvironmentType.Indoor;
            }
            catch { }

            float radiusM = indoor ? Plugin.LodCullNearRadiusIndoor.Value : Plugin.LodCullNearRadius.Value;
            // which radius is live is invisible without this — on icebreaker the user
            // tuned the OUTDOOR slider while standing indoors and concluded the system
            // was broken. on a map this size that mistake is even easier to make.
            if (indoor != _wasIndoor)
            {
                _wasIndoor = indoor;
                Plugin.Log.LogInfo($"[LodCullFloor] {(indoor ? "INDOOR" : "OUTDOOR")} — active near radius {radiusM:0}m "
                    + $"({(indoor ? "LodCullNearRadiusIndoor" : "LodCullNearRadius")})");
            }
            var camCell = Key(cam);

            if (camCell != _camCell || near != _lastNear || far != _lastFar || radiusM != _lastRadius)
            {
                _camCell = camCell; _lastNear = near; _lastFar = far; _lastRadius = radiusM;
                // true-meters tiering: distance from the camera to each cell's BOUNDS.
                // whole-cell steps made the radius slider only bite every CellSize metres
                // and unable to shrink below ~1.5 cells, which read as a dead knob.
                float r2 = radiusM * radiusM;
                float half = _cellSize * 0.5f;
                foreach (var kv in _cells)
                {
                    float dx = Mathf.Max(0f, Mathf.Abs(kv.Key.x * _cellSize - cam.x) - half);
                    float dy = Mathf.Max(0f, Mathf.Abs(kv.Key.y * _cellSize - cam.y) - half);
                    float dz = Mathf.Max(0f, Mathf.Abs(kv.Key.z * _cellSize - cam.z) - half);
                    float want = (dx * dx + dy * dy + dz * dz) <= r2 ? near : far;
                    if (!Mathf.Approximately(want, kv.Value.Applied))
                    {
                        kv.Value.Applied = want;
                        _dirty.Enqueue(kv.Value);
                        _dirtyWant.Enqueue(want);
                    }
                }
            }

            // budgeted drain, MEMBER-granular: budgeting between whole cells but applying
            // each cell's members in one gulp gave a triple-digit-ms hitch when a dense
            // cell re-tiered mid-movement. the cursor lets a fat cell span frames. a cell
            // re-tiered mid-drain gets stale writes for its tail, but its fresh queue entry
            // re-applies everything — eventual consistency, no hitch.
            _sw.Restart();
            while (_sw.Elapsed.TotalMilliseconds < 1.0)
            {
                if (_draining == null)
                {
                    if (_dirty.Count == 0) break;
                    _draining = _dirty.Dequeue();
                    _drainWant = _dirtyWant.Dequeue();
                    _drainIdx = 0;
                    if (!Mathf.Approximately(_drainWant, _draining.Applied)) { _draining = null; continue; } // superseded
                }
                var members = _draining.Members;
                while (_drainIdx < members.Count && _sw.Elapsed.TotalMilliseconds < 1.0)
                {
                    int i = members[_drainIdx++];
                    var g = _g[i];
                    if (g == null) continue;
                    float want = _neverCull[i] ? 0f : (_drainWant < 0f ? _orig[i] : Mathf.Min(_orig[i], _drainWant));
                    var lods = g.GetLODs();
                    if (lods == null || lods.Length == 0) continue;
                    int last = lods.Length - 1;
                    // same SetLODs ceiling clamp as LootBounds — equal-or-greater
                    // than the previous tier throws per call
                    float ceil = last > 0 ? lods[last - 1].screenRelativeTransitionHeight - 0.001f : 1f;
                    if (want > ceil) want = Mathf.Max(0f, ceil);
                    if (Mathf.Abs(lods[last].screenRelativeTransitionHeight - want) > 0.0005f)
                    {
                        lods[last].screenRelativeTransitionHeight = want;
                        g.SetLODs(lods);
                    }
                }
                if (_drainIdx >= members.Count) _draining = null;
            }
        }
    }
}
