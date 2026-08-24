using System.Collections.Generic;
using UnityEngine;

namespace Manimal.Terminal
{
    // SPAWN-TRIGGER VISUALIZER (2026-08-21, user request while hunting the chop).
    //
    // draws every AIPlaceInfo trigger box as a see-through wireframe with a label,
    // so the spawn choreography is physically visible in-raid. purely visual — we
    // never touch the colliders, so you still walk straight through them.
    //
    // GL line drawing rather than mesh cubes on purpose: 'Hidden/Internal-Colored'
    // is a built-in shader that always exists in a player build, whereas
    // Shader.Find on a transparent surface shader is a coin flip in a shipped
    // game. ZTest=Always so the boxes draw THROUGH geometry — the whole point is
    // finding a trigger you can't see.
    internal class TerminalTriggerViz : MonoBehaviour
    {
        internal static TerminalTriggerViz Instance;

        // driven straight off the config — flip it live in F12 and the overlay
        // follows on the next frame; no hotkey, no state to get out of sync
        private static bool Visible => Plugin.ShowSpawnTriggers.Value && TerminalGate.On;
        private bool _wasVisible;

        private struct Box
        {
            public Transform T;
            public Vector3 Center, Size;
            public Color C;
            public string Label;
        }

        private readonly List<Box> _boxes = new List<Box>();
        private static Material _mat;

        internal static void Ensure()
        {
            if (Instance != null) return;
            var go = new GameObject("Manimal_TerminalTriggerViz");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<TerminalTriggerViz>();
        }

        // rebuild on the rising edge — the boxes only exist once AIPlaces has run,
        // and the user can enable this mid-raid
        private void Update()
        {
            bool v = Visible;
            if (v == _wasVisible) return;
            _wasVisible = v;
            if (v) Rebuild();
            Plugin.Log.LogWarning($"[TriggerViz] spawn-trigger overlay {(v ? "ON" : "OFF")}"
                + (v ? $" — {_boxes.Count} trigger box(es)" : ""));
        }

        private static Material Mat()
        {
            if (_mat != null) return _mat;
            var sh = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
            if (sh == null) { Plugin.Log.LogWarning("[TriggerViz] no usable shader — overlay unavailable"); return null; }
            _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _mat.SetInt("_ZWrite", 0);
                // draw through walls — you're looking for boxes you can't see
                _mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            }
            catch { }
            return _mat;
        }

        // T4 is the one under investigation, so it gets its own loud colour
        private static Color ColorFor(string ev)
        {
            if (string.IsNullOrEmpty(ev)) return new Color(0.5f, 0.5f, 0.5f, 0.7f);   // no event logic
            if (ev.Equals("T4", System.StringComparison.OrdinalIgnoreCase)) return new Color(1f, 0.1f, 0.1f, 1f);
            if (ev.StartsWith("TB", System.StringComparison.OrdinalIgnoreCase)) return new Color(0.2f, 0.9f, 1f, 0.9f);
            if (ev.StartsWith("T", System.StringComparison.OrdinalIgnoreCase)) return new Color(1f, 0.65f, 0.1f, 0.9f);
            return new Color(0.6f, 1f, 0.4f, 0.9f);
        }

        internal void Rebuild()
        {
            _boxes.Clear();
            var holder = GameObject.Find("AIPlaceInfoHolder");
            if (holder == null) { Plugin.Log.LogWarning("[TriggerViz] AIPlaceInfoHolder not in scene"); return; }

            var sb = new System.Text.StringBuilder();
            foreach (var col in holder.GetComponentsInChildren<BoxCollider>(true))
            {
                if (col == null) continue;
                var logic = col.GetComponent<TerminalTierEventLogic>();
                string ev = logic != null ? logic.EventName : null;
                // the logic component often sits on a CHILD of the collider's GO
                if (ev == null)
                {
                    var childLogic = col.GetComponentInChildren<TerminalTierEventLogic>(true);
                    if (childLogic != null) ev = childLogic.EventName;
                }

                _boxes.Add(new Box
                {
                    T = col.transform,
                    Center = col.center,
                    Size = col.size,
                    C = ColorFor(ev),
                    Label = string.IsNullOrEmpty(ev) ? col.gameObject.name : $"{ev}  ({col.gameObject.name})",
                });

                var world = col.transform.TransformPoint(col.center);
                var size = Vector3.Scale(col.size, col.transform.lossyScale);
                sb.Append($"\n    {(string.IsNullOrEmpty(ev) ? "(no event)" : ev),-10} '{col.gameObject.name}' "
                    + $"center={world} size={size} isTrigger={col.isTrigger} enabled={col.enabled} "
                    + $"active={col.gameObject.activeInHierarchy}");
            }
            Plugin.Log.LogWarning($"[TriggerViz] {_boxes.Count} trigger box(es):{sb}");
        }

        private void OnRenderObject()
        {
            if (!Visible || _boxes.Count == 0) return;
            // only draw into the player's camera — not reflection/optic passes
            var fps = TerminalCullingDriver.CameraRef;
            if (fps != null && Camera.current != fps) return;

            var m = Mat();
            if (m == null) return;
            m.SetPass(0);

            for (int i = 0; i < _boxes.Count; i++)
            {
                var b = _boxes[i];
                if (b.T == null) continue;
                GL.PushMatrix();
                GL.MultMatrix(b.T.localToWorldMatrix);   // local space: honours rotation + scale
                GL.Begin(GL.LINES);
                GL.Color(b.C);

                Vector3 h = b.Size * 0.5f, c = b.Center;
                // 8 corners
                var p000 = c + new Vector3(-h.x, -h.y, -h.z);
                var p100 = c + new Vector3(h.x, -h.y, -h.z);
                var p010 = c + new Vector3(-h.x, h.y, -h.z);
                var p110 = c + new Vector3(h.x, h.y, -h.z);
                var p001 = c + new Vector3(-h.x, -h.y, h.z);
                var p101 = c + new Vector3(h.x, -h.y, h.z);
                var p011 = c + new Vector3(-h.x, h.y, h.z);
                var p111 = c + new Vector3(h.x, h.y, h.z);

                void Line(Vector3 a, Vector3 bb) { GL.Vertex(a); GL.Vertex(bb); }
                // bottom, top, verticals
                Line(p000, p100); Line(p100, p101); Line(p101, p001); Line(p001, p000);
                Line(p010, p110); Line(p110, p111); Line(p111, p011); Line(p011, p010);
                Line(p000, p010); Line(p100, p110); Line(p101, p111); Line(p001, p011);

                GL.End();
                GL.PopMatrix();
            }
        }

        private void OnGUI()
        {
            if (!Visible || _boxes.Count == 0) return;
            var cam = TerminalCullingDriver.CameraRef != null ? TerminalCullingDriver.CameraRef : Camera.main;
            if (cam == null) return;

            var style = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            for (int i = 0; i < _boxes.Count; i++)
            {
                var b = _boxes[i];
                if (b.T == null) continue;
                var world = b.T.TransformPoint(b.Center);
                var sp = cam.WorldToScreenPoint(world);
                if (sp.z <= 0f) continue;                      // behind the camera
                if (sp.z > 250f) continue;                     // too far to be useful
                style.normal.textColor = b.C;
                var r = new Rect(sp.x - 90f, Screen.height - sp.y - 9f, 180f, 18f);
                GUI.Label(r, $"{b.Label}  [{sp.z:F0}m]", style);
            }
        }
    }
}
