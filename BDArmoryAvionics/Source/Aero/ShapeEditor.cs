using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// VAB/SPH handles for ELE Shape parts ("Edit shape" in the part menu). Every section shows its ring of points:
    ///   drag a point            reshape the section (mirror keeps left and right the same; "all sections" moves it everywhere)
    ///   drag a section's square slide it along the part      Ctrl+drag: shift it sideways / up and down
    ///   wheel over a square     size the section (Shift: width only, Alt: height only)
    ///   S sharp corner · Insert / A add a point after · Delete remove point · Ctrl+Z / Ctrl+Y undo / redo
    /// The window has sections, presets, sizes and the options.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class ShapeEditor : MonoBehaviour
    {
        public static ModuleELEShape Target;
        static ShapeEditor instance;
        const int WindowID = 71730392;
        const string LockId = "ELE_ShapeEditor";

        Rect rect = new(320, 120, 320, 520);
        int sel, selPt = -1;
        bool mirror = true, allSections, snap = true;
        float snapStep = 0.05f;
        int preset;
        readonly List<string> undo = new(), redo = new();

        // drag state
        enum Grab { None, Point, Centre }
        Grab grab; int grabSec, grabPt; bool grabSide; Vector2 grabStart; Vector3 grabAnchor; float grabY0; Vector2 grabOff0;
        List<Vector2>[] grabPts0;
        bool locked;

        public static void Open(ModuleELEShape m) { Target = m; if (instance != null) { instance.sel = Mathf.Clamp(instance.sel, 0, m.sections.Count - 1); instance.selPt = -1; instance.undo.Clear(); instance.redo.Clear(); } }
        public static void Close() { Target = null; }

        void Awake() => instance = this;
        void OnDestroy() { if (locked && EditorLogic.fetch != null) EditorLogic.fetch.Unlock(LockId); Target = null; }

        Camera Cam => EditorLogic.fetch != null && EditorLogic.fetch.editorCamera != null ? EditorLogic.fetch.editorCamera : Camera.main;
        ModuleELEShape.Section Sec(int i) => Target.sections[Mathf.Clamp(i, 0, Target.sections.Count - 1)];
        int N => Target.sections[0].pts.Count;
        static int Mirror(int i, int n) => (n - i) % n;

        // ---------------- edits ----------------
        void Push() { Target.Encode(); undo.Add(Target.shape); if (undo.Count > 60) undo.RemoveAt(0); redo.Clear(); }
        void Commit() { Target.Rebuild(true); Target.Propagate(); }
        void Restore(string s) { Target.shape = s; Target.Decode(); sel = Mathf.Clamp(sel, 0, Target.sections.Count - 1); if (selPt >= N) selPt = -1; Commit(); }
        void Undo() { if (undo.Count == 0) return; Target.Encode(); redo.Add(Target.shape); var s = undo[undo.Count - 1]; undo.RemoveAt(undo.Count - 1); Restore(s); }
        void Redo() { if (redo.Count == 0) return; Target.Encode(); undo.Add(Target.shape); var s = redo[redo.Count - 1]; redo.RemoveAt(redo.Count - 1); Restore(s); }

        Vector2 Snap(Vector2 p) => snap ? new Vector2(Mathf.Round(p.x / snapStep) * snapStep, Mathf.Round(p.y / snapStep) * snapStep) : p;

        /// <summary>Make a section mirror-symmetric from its right half (x ≥ 0 side: points 1..n/2-1), top and bottom on the centre line.</summary>
        static void Symmetrize(ModuleELEShape.Section s)
        {
            int n = s.pts.Count; if (n % 2 != 0) return;
            s.pts[0] = new Vector2(0, s.pts[0].y); s.pts[n / 2] = new Vector2(0, s.pts[n / 2].y);
            for (int i = 1; i < n / 2; i++) { s.pts[n - i] = new Vector2(-s.pts[i].x, s.pts[i].y); s.sharp[n - i] = s.sharp[i]; }
        }

        void SetPoint(ModuleELEShape.Section s, int i, Vector2 p)
        {
            int n = s.pts.Count;
            if (mirror && n % 2 == 0)
            {
                int m = Mirror(i, n);
                if (m == i) p.x = 0;                       // top / bottom stay on the centre line
                else s.pts[m] = new Vector2(-p.x, p.y);
            }
            s.pts[i] = p;
        }

        void InsertAfter(int i)
        {
            int n = N; if (n >= 64) return;
            Push();
            void Ins(int k) { foreach (var s in Target.sections) { int nn = s.pts.Count; s.pts.Insert(k + 1, (s.pts[k] + s.pts[(k + 1) % nn]) / 2); s.sharp.Insert(k + 1, false); } }
            int j = Mirror((i + 1) % n, n);                 // the mirror edge starts here
            Ins(i);
            if (mirror && n % 2 == 0 && j != i) { if (j > i) j++; Ins(j); foreach (var s in Target.sections) Symmetrize(s); }
            selPt = i + 1; Commit();
        }

        void DeletePoint(int i)
        {
            int n = N;
            if (mirror && n % 2 == 0)
            {
                int m = Mirror(i, n);
                if (m == i) { ScreenMessages.PostScreenMessage("Top and bottom points stay while Mirror is on", 2f, ScreenMessageStyle.UPPER_CENTER); return; }
                if (n - 2 < 4) return;
                Push();
                foreach (var s in Target.sections) { foreach (var k in new[] { Mathf.Max(i, m), Mathf.Min(i, m) }) { s.pts.RemoveAt(k); s.sharp.RemoveAt(k); } Symmetrize(s); }
            }
            else
            {
                if (n - 1 < 3) return;
                Push();
                foreach (var s in Target.sections) { s.pts.RemoveAt(i); s.sharp.RemoveAt(i); }
            }
            selPt = -1; Commit();
        }

        void ToggleSharp(int i)
        {
            Push();
            var targets = allSections ? Target.sections : new List<ModuleELEShape.Section> { Sec(sel) };
            bool v = !Sec(sel).sharp[i];
            foreach (var s in targets) { s.sharp[i] = v; if (mirror && N % 2 == 0) s.sharp[Mirror(i, N)] = v; }
            Commit();
        }

        void ScaleSection(ModuleELEShape.Section s, float kx, float ky)
        {
            for (int i = 0; i < s.pts.Count; i++) s.pts[i] = new Vector2(s.pts[i].x * kx, s.pts[i].y * ky);
        }

        static (float w, float h) Size(ModuleELEShape.Section s) => (s.pts.Max(p => p.x) - s.pts.Min(p => p.x), s.pts.Max(p => p.y) - s.pts.Min(p => p.y));

        // ---------------- input ----------------
        void Update()
        {
            if (Target == null || Target.part == null || EditorLogic.fetch == null) { SetLock(false); grab = Grab.None; return; }
            var cam = Cam; if (cam == null) return;
            Vector2 mouse = new(Input.mousePosition.x, UnityEngine.Screen.height - Input.mousePosition.y);
            bool overWindow = rect.Contains(mouse);
            var tr = Target.part.transform;

            // keys (not while typing)
            if (GUIUtility.keyboardControl == 0)
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                if (ctrl && Input.GetKeyDown(KeyCode.Z)) Undo();
                if (ctrl && Input.GetKeyDown(KeyCode.Y)) Redo();
                if (selPt >= 0 && selPt < N)
                {
                    if (Input.GetKeyDown(KeyCode.S) && !ctrl) ToggleSharp(selPt);
                    if (Input.GetKeyDown(KeyCode.Insert) || (Input.GetKeyDown(KeyCode.A) && !ctrl)) InsertAfter(selPt);
                    if (Input.GetKeyDown(KeyCode.Delete)) DeletePoint(selPt);
                }
            }

            // hover
            Grab hov = Grab.None; int hs = -1, hp = -1; float best = 11f;
            if (!overWindow && grab == Grab.None)
                for (int s = 0; s < Target.sections.Count; s++)
                {
                    var sec = Target.sections[s];
                    var c = Screen(cam, tr.TransformPoint(ModuleELEShape.Local(sec, Vector2.zero)));
                    if (c.z > 0 && (c.xy() - mouse).magnitude < best) { best = (c.xy() - mouse).magnitude; hov = Grab.Centre; hs = s; hp = -1; }
                    for (int i = 0; i < sec.pts.Count; i++)
                    {
                        var p = Screen(cam, tr.TransformPoint(ModuleELEShape.Local(sec, sec.pts[i])));
                        float d = (p.xy() - mouse).magnitude;
                        if (p.z > 0 && d < best - (s == sel ? 2 : 0)) { best = d; hov = Grab.Point; hs = s; hp = i; }
                    }
                }
            SetLock(overWindow || hov != Grab.None || grab != Grab.None);

            // wheel over a section square: size it
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (hov == Grab.Centre && Mathf.Abs(wheel) > 0.001f)
            {
                Push();
                float k = 1 + Mathf.Sign(wheel) * 0.05f;
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift), alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                ScaleSection(Target.sections[hs], alt ? 1 : k, shift ? 1 : k);
                sel = hs; Commit();
            }

            if (Input.GetMouseButtonDown(0) && hov != Grab.None)
            {
                Push();
                grab = hov; grabSec = hs; grabPt = hp; sel = hs; if (hov == Grab.Point) selPt = hp;
                var sec = Target.sections[hs];
                grabSide = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                grabStart = mouse; grabY0 = sec.y; grabOff0 = sec.offset;
                grabAnchor = tr.TransformPoint(ModuleELEShape.Local(sec, hov == Grab.Point ? sec.pts[hp] : Vector2.zero));
                grabPts0 = Target.sections.Select(s => new List<Vector2>(s.pts)).ToArray();
            }
            if (grab != Grab.None && Input.GetMouseButton(0)) Drag(cam, tr);
            if (grab != Grab.None && !Input.GetMouseButton(0))
            {
                grab = Grab.None;
                if ((mouse - grabStart).magnitude < 3 && undo.Count > 0) undo.RemoveAt(undo.Count - 1);   // a click, not a drag
                else Commit();
            }
        }

        static Vector3 Screen(Camera cam, Vector3 world) { var s = cam.WorldToScreenPoint(world); return new Vector3(s.x, UnityEngine.Screen.height - s.y, s.z); }

        void Drag(Camera cam, Transform tr)
        {
            var ray = cam.ScreenPointToRay(Input.mousePosition);
            var sec = Target.sections[grabSec];
            if (grab == Grab.Centre && !grabSide)
            {
                // slide along the part's length: closest point between the mouse ray and the part's axis
                Vector3 a = tr.position, u = tr.up;
                Vector3 w0 = a - ray.origin; float b = Vector3.Dot(u, ray.direction), d = Vector3.Dot(u, w0), e = Vector3.Dot(ray.direction, w0);
                float den = 1 - b * b; if (den < 1e-4f) return;
                float t = (b * e - d) / den;
                float y = snap ? Mathf.Round(t / snapStep) * snapStep : t;
                // keep the order: a section can't pass its neighbours
                float lo = grabSec > 0 ? Target.sections[grabSec - 1].y + 0.02f : float.MinValue, hi = grabSec < Target.sections.Count - 1 ? Target.sections[grabSec + 1].y - 0.02f : float.MaxValue;
                sec.y = Mathf.Clamp(y, lo, hi);
            }
            else
            {
                // in the section's plane
                var plane = new Plane(tr.up, tr.TransformPoint(new Vector3(0, sec.y, 0)));
                if (!plane.Raycast(ray, out float dist)) return;
                var l = tr.InverseTransformPoint(ray.GetPoint(dist));
                var q = new Vector2(l.x, -l.z);
                if (grab == Grab.Centre) { var off = Snap(q); if (mirror) off.x = 0; sec.offset = off; }
                else
                {
                    var p = Snap(q - sec.offset);
                    var delta = p - grabPts0[grabSec][grabPt];
                    if (allSections)
                        for (int s = 0; s < Target.sections.Count; s++) SetPoint(Target.sections[s], grabPt, grabPts0[s][grabPt] + delta);
                    else SetPoint(sec, grabPt, p);
                }
            }
            Target.Rebuild(true);
        }

        void SetLock(bool on)
        {
            if (on == locked || EditorLogic.fetch == null) return;
            if (on) EditorLogic.fetch.Lock(true, true, true, LockId); else EditorLogic.fetch.Unlock(LockId);
            locked = on;
        }

        // ---------------- drawing ----------------
        void OnGUI()
        {
            if (Target == null || Target.part == null) return;
            if (Event.current.type == EventType.Repaint) DrawHandles();
            rect = AvionicsCore.DrawWindow(WindowID, rect, Window, "ELE Shape editor", BDArmorySetup.BDGuiSkin);
        }

        static readonly Color Sel = new(1f, 0.75f, 0.2f), Other = new(0.6f, 0.85f, 1f, 0.6f), Sharp = new(1f, 0.35f, 0.3f), Axis = new(1f, 1f, 1f, 0.35f);

        void DrawHandles()
        {
            var cam = Cam; if (cam == null || !AvionicsDraw.Begin()) return;
            var tr = Target.part.transform;
            // spine through the section centres
            for (int s = 0; s < Target.sections.Count - 1; s++)
            {
                var a = Screen(cam, tr.TransformPoint(ModuleELEShape.Local(Target.sections[s], Vector2.zero)));
                var b = Screen(cam, tr.TransformPoint(ModuleELEShape.Local(Target.sections[s + 1], Vector2.zero)));
                if (a.z > 0 && b.z > 0) AvionicsDraw.Line(a.xy(), b.xy(), 1, Axis);
            }
            for (int s = 0; s < Target.sections.Count; s++)
            {
                var sec = Target.sections[s]; bool on = s == sel;
                var col = on ? Sel : Other;
                int n = sec.pts.Count;
                var sp = sec.pts.Select(p => Screen(cam, tr.TransformPoint(ModuleELEShape.Local(sec, p)))).ToArray();
                for (int i = 0; i < n; i++) { var a = sp[i]; var b = sp[(i + 1) % n]; if (a.z > 0 && b.z > 0) AvionicsDraw.Line(a.xy(), b.xy(), on ? 2 : 1, col); }
                for (int i = 0; i < n; i++)
                {
                    if (sp[i].z <= 0) continue;
                    float r = on ? (i == selPt ? 6 : 4) : 3;
                    var c = sec.sharp[i] ? Sharp : col;
                    if (on && i == selPt) c = Color.white;
                    AvionicsDraw.Box(sp[i].xy(), r, sec.sharp[i] ? 3 : 2, c);
                }
                var ctr = Screen(cam, tr.TransformPoint(ModuleELEShape.Local(sec, Vector2.zero)));
                if (ctr.z > 0) { AvionicsDraw.Box(ctr.xy(), on ? 8 : 6, 2, on ? Sel : Axis); if (sec.hard) AvionicsDraw.Box(ctr.xy(), 3, 3, Sharp); }
            }
            AvionicsDraw.End();
        }

        void Window(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Close(); return; }
            var btn = BDArmorySetup.ButtonStyle; var on = BDArmorySetup.SelectedButtonStyle; var small = AvionicsDraw.LabelSmall;
            float x = 8, w = rect.width - 16, y = 24, hw = (w - 4) / 2, tw = (w - 8) / 3;
            var secs = Target.sections; sel = Mathf.Clamp(sel, 0, secs.Count - 1); var s = secs[sel];

            // options
            mirror = GUI.Toggle(new Rect(x, y, tw, 20), mirror, "Mirror L/R", mirror ? on : btn);
            allSections = GUI.Toggle(new Rect(x + tw + 4, y, tw, 20), allSections, "All sections", allSections ? on : btn);
            if (GUI.Button(new Rect(x + 2 * (tw + 4), y, tw, 20), snap ? $"Snap {snapStep * 100:0} cm" : "Snap off", snap ? on : btn)) { if (!snap) { snap = true; snapStep = 0.01f; } else if (snapStep < 0.05f) snapStep = 0.05f; else if (snapStep < 0.25f) snapStep = 0.25f; else snap = false; }
            y += 24;
            if (mirror && N % 2 == 1) { AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "Odd point count: add a point to mirror exactly", small, AvionicsDraw.Unknown); y += 14; }
            if (GUI.Button(new Rect(x, y, hw, 20), $"Undo ({undo.Count})", btn)) Undo();
            if (GUI.Button(new Rect(x + hw + 4, y, hw, 20), $"Redo ({redo.Count})", btn)) Redo();
            y += 26;

            // sections
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"SECTIONS ({secs.Count})  front = top of list", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
            for (int i = secs.Count - 1; i >= 0; i--)
            {
                var (sw, sh) = Size(secs[i]);
                if (GUI.Toggle(new Rect(x, y, w, 18), sel == i, $"{(i == secs.Count - 1 ? "front" : i == 0 ? "rear " : $"#{i}   ")}  at {secs[i].y:+0.00;-0.00} m   {sw:0.00} × {sh:0.00} m{(secs[i].hard ? "  hard" : "")}", sel == i ? on : btn)) sel = i;
                y += 19;
            }
            y += 3;
            if (GUI.Button(new Rect(x, y, tw, 20), "+ Section", btn))
            {
                Push();
                var a = s; var b = sel < secs.Count - 1 ? secs[sel + 1] : null;
                var ns = a.Clone();
                if (b != null) { ns.y = (a.y + b.y) / 2; ns.offset = (a.offset + b.offset) / 2; for (int i = 0; i < ns.pts.Count; i++) ns.pts[i] = (a.pts[i] + b.pts[i]) / 2; }
                else ns.y = a.y + 1f;
                secs.Insert(sel + 1, ns); sel++; Commit();
            }
            if (GUI.Button(new Rect(x + tw + 4, y, tw, 20), "Delete", btn) && secs.Count > 2) { Push(); secs.RemoveAt(sel); sel = Mathf.Clamp(sel - 1, 0, secs.Count - 1); Commit(); }
            if (GUI.Button(new Rect(x + 2 * (tw + 4), y, tw, 20), s.hard ? "Hard join" : "Smooth join", s.hard ? on : btn)) { Push(); s.hard = !s.hard; Commit(); }
            y += 24;

            // size of the selected section
            var (cw, ch) = Size(s);
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"SIZE  {cw:0.00} wide × {ch:0.00} high", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
            float qw = (w - 12) / 4;
            if (GUI.Button(new Rect(x, y, qw, 20), "W −", btn)) { Push(); ScaleSection(s, 0.9f, 1); Commit(); }
            if (GUI.Button(new Rect(x + qw + 4, y, qw, 20), "W +", btn)) { Push(); ScaleSection(s, 1.1f, 1); Commit(); }
            if (GUI.Button(new Rect(x + 2 * (qw + 4), y, qw, 20), "H −", btn)) { Push(); ScaleSection(s, 1, 0.9f); Commit(); }
            if (GUI.Button(new Rect(x + 3 * (qw + 4), y, qw, 20), "H +", btn)) { Push(); ScaleSection(s, 1, 1.1f); Commit(); }
            y += 22;
            if (GUI.Button(new Rect(x, y, hw, 20), "All smaller", btn)) { Push(); ScaleSection(s, 0.9f, 0.9f); Commit(); }
            if (GUI.Button(new Rect(x + hw + 4, y, hw, 20), "All bigger", btn)) { Push(); ScaleSection(s, 1.1f, 1.1f); Commit(); }
            y += 22;
            if (GUI.Button(new Rect(x, y, hw, 20), "Copy to all sections", btn)) { Push(); foreach (var o in secs) if (o != s) { o.pts = new List<Vector2>(s.pts); o.sharp = new List<bool>(s.sharp); } Commit(); }
            if (GUI.Button(new Rect(x + hw + 4, y, hw, 20), "Centre it", btn)) { Push(); s.offset = Vector2.zero; Commit(); }
            y += 26;

            // presets
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "PRESET", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
            if (GUI.Button(new Rect(x, y, w, 20), $"◀  {Presets.Names[preset]}  ▶", btn)) preset = (preset + 1) % Presets.Names.Length;
            y += 22;
            if (GUI.Button(new Rect(x, y, hw, 20), "Apply to section", btn)) { Push(); var (pw, ph) = Size(s); Presets.Apply(s, Presets.Names[preset], Mathf.Max(0.1f, pw), Mathf.Max(0.1f, ph)); Commit(); }
            if (GUI.Button(new Rect(x + hw + 4, y, hw, 20), "Apply to all", btn)) { Push(); foreach (var o in secs) { var (pw, ph) = Size(o); Presets.Apply(o, Presets.Names[preset], Mathf.Max(0.02f, pw), Mathf.Max(0.02f, ph)); } Commit(); }
            y += 26;

            // points
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"POINTS ({N} per section){(selPt >= 0 ? $"  · point {selPt}" : "")}", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
            GUI.enabled = selPt >= 0 && selPt < N;
            if (GUI.Button(new Rect(x, y, tw, 20), "Add after (A)", btn)) InsertAfter(selPt);
            if (GUI.Button(new Rect(x + tw + 4, y, tw, 20), "Delete (Del)", btn)) DeletePoint(selPt);
            if (GUI.Button(new Rect(x + 2 * (tw + 4), y, tw, 20), selPt >= 0 && selPt < N && s.sharp[selPt] ? "Sharp (S)" : "Round (S)", selPt >= 0 && selPt < N && s.sharp[selPt] ? on : btn)) ToggleSharp(selPt);
            GUI.enabled = true;
            y += 26;

            AvionicsDraw.ColorLabel(new Rect(x, y, w, 64),
                "Drag points to shape · drag a square to slide a section\nCtrl+drag a square to shift it · wheel on a square to size it\n(Shift width, Alt height) · Ctrl+Z / Ctrl+Y undo/redo\nLook, paint, smoothness and tank: part right-click menu", small, AvionicsDraw.ScopeText);
            y += 66;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), Target.info, small, AvionicsDraw.HudText); y += 16;
            rect.height = y + 6;
            GUI.DragWindow();
        }
    }

    static class V3Ext { public static Vector2 xy(this Vector3 v) => new(v.x, v.y); }
}
