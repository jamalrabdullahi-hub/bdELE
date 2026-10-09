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
        int preset, tab, stockIdx;
        string libName = "My shape", copyPart = "modernsuperfront";
        Vector2 libScroll;
        static readonly string[] Tabs = { "Shape", "Ends & nodes", "Canopy & seats", "Library" };
        readonly List<string> undo = new(), redo = new();

        // drag state
        enum Grab { None, Point, Centre, Node, Seat }
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

        void OtherTab(ref float yRef, float x, float w, float hw, float tw, ModuleELEShape.Section s)
        {
            float y = yRef;
            try { OtherTabBody(ref y, x, w, hw, tw, s); } finally { yRef = y; }
        }

        void OtherTabBody(ref float yOut, float x, float w, float hw, float tw, ModuleELEShape.Section s)
        {
            float y = yOut;
            var btn = BDArmorySetup.ButtonStyle; var on = BDArmorySetup.SelectedButtonStyle; var small = AvionicsDraw.LabelSmall;
            var secs = Target.sections;
            void Head(string t) { AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), t, AvionicsDraw.Header, AvionicsDraw.HudText); y += 16; }
            void Note(string t) { AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), t, small, AvionicsDraw.ScopeText); y += 14; }
            float Slide(string label, float v, float min, float max) { GUI.Label(new Rect(x, y, w, 16), label, small); y += 15; v = GUI.HorizontalSlider(new Rect(x, y, w, 14), v, min, max); y += 18; return v; }
            if (tab == 1)
            {
                Head("STOCK PROFILE FOR AN END");
                if (GUI.Button(new Rect(x, y, w, 20), $"<  {ShapeLibrary.Stock[stockIdx].label}  >", btn)) stockIdx = (stockIdx + 1) % ShapeLibrary.Stock.Length;
                y += 22;
                if (GUI.Button(new Rect(x, y, tw, 20), "Front end", btn)) StockEnd(secs.Count - 1);
                if (GUI.Button(new Rect(x + tw + 4, y, tw, 20), "Rear end", btn)) StockEnd(0);
                if (GUI.Button(new Rect(x + 2 * (tw + 4), y, tw, 20), "Selected", btn)) StockEnd(sel);
                y += 24;
                Note("Measured from the stock parts, so they meet flush.");
                y += 4;
                Head("SNAP POINTS (green: drag them in the scene)");
                for (int e = 1; e >= 0; e--)
                {
                    string nm = e == 1 ? "Front" : "Rear";
                    int size = e == 1 ? Target.nodeTopSize : Target.nodeBottomSize;
                    var off = e == 1 ? Target.nodeTopOffset : Target.nodeBottomOffset;
                    GUI.Label(new Rect(x, y, 140, 20), $"{nm}: size {size}, out {off.z:0.00} m", small);
                    if (GUI.Button(new Rect(x + w - 150, y, 36, 18), "−", btn)) { Push(); SetNodeSize(e, Mathf.Max(0, size - 1)); }
                    if (GUI.Button(new Rect(x + w - 112, y, 36, 18), "+", btn)) { Push(); SetNodeSize(e, Mathf.Min(4, size + 1)); }
                    if (GUI.Button(new Rect(x + w - 74, y, 36, 18), "in", btn)) { Push(); Shift(e, -0.05f); }
                    if (GUI.Button(new Rect(x + w - 36, y, 36, 18), "out", btn)) { Push(); Shift(e, 0.05f); }
                    y += 22;
                }
                if (GUI.Button(new Rect(x, y, w, 20), "Centre both snap points", btn)) { Push(); Target.nodeTopOffset = Target.nodeBottomOffset = Vector3.zero; Commit(); }
                y += 24;
                Head("SURFACE ATTACH POINT");
                var sn = Target.surfaceNode;
                float a = Slide($"Along {sn.y:0.00} m", sn.y, secs[0].y, secs[secs.Count - 1].y);
                float u = Slide($"Up from the underside {sn.z:0.00} m", sn.z, 0, 2f);
                if (Mathf.Abs(a - sn.y) > 0.005f || Mathf.Abs(u - sn.z) > 0.005f) { Target.surfaceNode = new Vector3(0, Mathf.Round(a * 100) / 100, Mathf.Round(u * 100) / 100); Commit(); }
            }
            else if (tab == 2)
            {
                Head("CANOPY GLASS BAND");
                bool c = GUI.Toggle(new Rect(x, y, w, 20), Target.canopy, Target.canopy ? "Canopy glass ON" : "Canopy glass off", Target.canopy ? on : btn);
                if (c != Target.canopy) { Push(); Target.canopy = c; Commit(); }
                y += 24;
                if (Target.canopy)
                {
                    float lo = secs[0].y, hi = secs[secs.Count - 1].y;
                    float f = Slide($"From {Target.canopyFrom:0.00} m", Target.canopyFrom, lo, hi);
                    float t = Slide($"To {Target.canopyTo:0.00} m", Target.canopyTo, lo, hi);
                    float ab = Slide($"Above {Target.canopyAbove:0.00} m from the centre line", Target.canopyAbove, -1.5f, 1.5f);
                    if (Mathf.Abs(f - Target.canopyFrom) > 0.004f || Mathf.Abs(t - Target.canopyTo) > 0.004f || Mathf.Abs(ab - Target.canopyAbove) > 0.004f)
                    { Target.canopyFrom = Mathf.Min(f, t - 0.05f); Target.canopyTo = Mathf.Max(t, f + 0.05f); Target.canopyAbove = ab; Commit(); }
                    Note("Tip: raise the sections over the canopy for a bubble.");
                }
                y += 4;
                Head("CREW SEATS (pink: drag the heads in the scene)");
                if (Target.part.CrewCapacity <= 0) Note("This is the plain ELE Shape: use ELE Shape Cockpit for crew.");
                else
                {
                    float al = Slide($"Pilot along {Target.seatAlong:0.00} m", Target.seatAlong, secs[0].y, secs[secs.Count - 1].y);
                    float up = Slide($"Seat height {Target.seatUp:0.00} m", Target.seatUp, -1.5f, 1f);
                    if (Mathf.Abs(al - Target.seatAlong) > 0.004f || Mathf.Abs(up - Target.seatUp) > 0.004f) { Target.seatAlong = al; Target.seatUp = up; Target.Propagate(); }
                    string mode = Target.seatSpacing > 0.05f ? "Tandem" : Target.seatSpacing < -0.05f ? "Side by side" : "Single seat";
                    if (GUI.Button(new Rect(x, y, w, 20), $"Layout: {mode}", btn)) { Target.seatSpacing = Target.seatSpacing > 0.05f ? -0.8f : Target.seatSpacing < -0.05f ? 0 : 1.1f; Target.Propagate(); }
                    y += 22;
                    if (Mathf.Abs(Target.seatSpacing) > 0.05f)
                    {
                        float spv = Slide($"Spacing {Mathf.Abs(Target.seatSpacing):0.00} m", Mathf.Abs(Target.seatSpacing), 0.5f, 2.5f);
                        if (Mathf.Abs(spv - Mathf.Abs(Target.seatSpacing)) > 0.004f) { Target.seatSpacing = Mathf.Sign(Target.seatSpacing) * spv; Target.Propagate(); }
                    }
                    Note("Head must stay inside the skin / under the glass.");
                }
            }
            else
            {
                Head("SAVE");
                libName = GUI.TextField(new Rect(x, y, w, 20), libName); y += 22;
                if (GUI.Button(new Rect(x, y, hw, 20), "Save whole shape", btn)) ShapeLibrary.SaveShape(Target, libName);
                if (GUI.Button(new Rect(x + hw + 4, y, hw, 20), "Save this section", btn)) ShapeLibrary.SaveSection(s, libName);
                y += 24;
                Head("LOAD SHAPE (onto this part)");
                var shapes = ShapeLibrary.List("shape").ToList();
                var secsL = ShapeLibrary.List("section").ToList();
                float lh = Mathf.Min(150, (shapes.Count + secsL.Count + 1) * 21 + 20);
                var view = new Rect(x, y, w, lh);
                libScroll = GUI.BeginScrollView(view, libScroll, new Rect(0, 0, w - 16, (shapes.Count + secsL.Count + 1) * 21 + 20));
                float ly = 0;
                if (shapes.Count == 0) { GUI.Label(new Rect(0, ly, w - 18, 20), "(no saved shapes yet)", small); ly += 21; }
                foreach (var n in shapes)
                {
                    if (GUI.Button(new Rect(0, ly, w - 80, 20), n, btn)) { Push(); ShapeLibrary.LoadShape(Target, n); sel = 0; selPt = -1; }
                    if (GUI.Button(new Rect(w - 76, ly, 56, 20), "name", btn)) libName = n;
                    ly += 21;
                }
                GUI.Label(new Rect(0, ly, w - 18, 20), "Sections (onto the selected section, same size):", small); ly += 20;
                foreach (var n in secsL)
                {
                    if (GUI.Button(new Rect(0, ly, w - 80, 20), n, btn)) { Push(); if (ShapeLibrary.LoadSection(s, n, true)) { MatchCount(s); Commit(); } }
                    if (GUI.Button(new Rect(w - 76, ly, 56, 20), "all", btn)) { Push(); foreach (var o in secs) ShapeLibrary.LoadSection(o, n, true); Commit(); }
                    ly += 21;
                }
                GUI.EndScrollView();
                y += lh + 4;
                if (GUI.Button(new Rect(x, y, w, 20), "Open shapes folder", btn)) { System.IO.Directory.CreateDirectory(ShapeLibrary.Folder); Application.OpenURL("file:///" + ShapeLibrary.Folder.Replace('\\', '/')); }
                y += 24;
                Note("Saved shapes load onto any ELE Shape or Shape Cockpit.");
                y += 6;
                Head("COPY ANY PART'S SHAPE");
                copyPart = GUI.TextField(new Rect(x, y, w - 84, 20), copyPart);
                if (GUI.Button(new Rect(x + w - 80, y, 80, 20), "Copy", btn))
                {
                    Push();
                    if (!HullLines.CopyPart(Target, copyPart.Trim(), 6)) { undo.RemoveAt(undo.Count - 1); ScreenMessages.PostScreenMessage($"No part named {copyPart}", 2f, ScreenMessageStyle.UPPER_CENTER); }
                    else { sel = 0; selPt = -1; }
                }
                y += 22;
                Note("Part name as in its cfg (e.g. modernsuperfront, mk1pod).");
            }
            yOut = y;
        }

        void StockEnd(int i)
        {
            Push();
            var sc = Target.sections[i];
            if (!ShapeLibrary.ApplyStock(sc, stockIdx, out int size)) { undo.RemoveAt(undo.Count - 1); return; }
            if (i == 0) Target.nodeBottomSize = size; if (i == Target.sections.Count - 1) Target.nodeTopSize = size;
            Commit();
        }

        void SetNodeSize(int e, int size) { if (e == 1) Target.nodeTopSize = size; else Target.nodeBottomSize = size; Commit(); }
        void Shift(int e, float d) { if (e == 1) Target.nodeTopOffset += new Vector3(0, 0, d); else Target.nodeBottomOffset += new Vector3(0, 0, d); Commit(); }

        /// <summary>A loaded section may come with another point count: give every section the same count (resample the others).</summary>
        void MatchCount(ModuleELEShape.Section s)
        {
            int n = s.pts.Count;
            foreach (var o in Target.sections)
            {
                if (o == s || o.pts.Count == n) continue;
                var (ow, oh) = Size(o);
                var c = s.Clone(); c.y = o.y; c.offset = o.offset; c.hard = o.hard;
                var (cw, ch) = Size(c);
                for (int i = 0; i < n; i++) c.pts[i] = new Vector2(c.pts[i].x * (cw > 0.01f ? ow / cw : 1), c.pts[i].y * (ch > 0.01f ? oh / ch : 1));
                o.pts = c.pts; o.sharp = c.sharp;
            }
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
            if (!overWindow && grab == Grab.None && tab != 0)
            {
                for (int e = 0; e < 2; e++)
                {
                    var np = Screen(cam, tr.TransformPoint(NodePos(e)));
                    float d = (np.xy() - mouse).magnitude; if (np.z > 0 && d < best + 3) { best = d; hov = Grab.Node; hs = e; hp = -1; }
                }
                var seatsW = SeatPositions();
                for (int i = 0; i < seatsW.Count; i++)
                {
                    var sp2 = Screen(cam, tr.TransformPoint(seatsW[i] + new Vector3(0, 0, -0.75f)));   // drag by the head
                    float d = (sp2.xy() - mouse).magnitude; if (sp2.z > 0 && d < best + 4) { best = d; hov = Grab.Seat; hs = i; hp = -1; }
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
                grab = hov; grabSec = hs; grabPt = hp;
                if (hov == Grab.Node || hov == Grab.Seat) { grabStart = mouse; grabPts0 = new List<Vector2>[0]; }
                else
                {
                sel = hs; if (hov == Grab.Point) selPt = hp;
                var sec = Target.sections[hs];
                grabSide = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                grabStart = mouse; grabY0 = sec.y; grabOff0 = sec.offset;
                grabAnchor = tr.TransformPoint(ModuleELEShape.Local(sec, hov == Grab.Point ? sec.pts[hp] : Vector2.zero));
                grabPts0 = Target.sections.Select(s => new List<Vector2>(s.pts)).ToArray();
                }
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
            if (grab == Grab.Node)
            {
                var end = grabSec == 0 ? Target.sections[0] : Target.sections[Target.sections.Count - 1];
                var plane = new Plane(tr.up, tr.TransformPoint(new Vector3(0, end.y, 0)));
                if (!plane.Raycast(ray, out float nd)) return;
                var l = tr.InverseTransformPoint(ray.GetPoint(nd));
                var q = Snap(new Vector2(l.x, -l.z) - end.offset); if (mirror) q.x = 0;
                if (grabSec == 0) Target.nodeBottomOffset = new Vector3(q.x, q.y, Target.nodeBottomOffset.z); else Target.nodeTopOffset = new Vector3(q.x, q.y, Target.nodeTopOffset.z);
                Target.Rebuild(true); return;
            }
            if (grab == Grab.Seat)
            {
                // in the side plane through the centre line
                var plane = new Plane(tr.right, tr.position);
                if (!plane.Raycast(ray, out float sd)) return;
                var l = tr.InverseTransformPoint(ray.GetPoint(sd));
                float along = snap ? Mathf.Round(l.y / snapStep) * snapStep : l.y, headUp = -l.z;
                if (grabSec == 0) { Target.seatAlong = along; Target.seatUp = (snap ? Mathf.Round(headUp / snapStep) * snapStep : headUp) - 0.75f; }
                else if (Target.seatSpacing > 0.05f) Target.seatSpacing = Mathf.Max(0.6f, Target.seatAlong - along);
                return;
            }
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

        /// <summary>Attach node position (part-local): 0 rear, 1 front.</summary>
        Vector3 NodePos(int e)
        {
            var end = e == 0 ? Target.sections[0] : Target.sections[Target.sections.Count - 1];
            var o = e == 0 ? Target.nodeBottomOffset : Target.nodeTopOffset;
            return new Vector3(end.offset.x + o.x, end.y + (e == 0 ? -o.z : o.z), -(end.offset.y + o.y));
        }

        /// <summary>Seat pans (part-local) as the cockpit IVA will place them.</summary>
        List<Vector3> SeatPositions()
        {
            var l = new List<Vector3>();
            if (Target.part.CrewCapacity <= 0) return l;
            float a = Target.seatAlong, u = Target.seatUp, sp = Target.seatSpacing;
            l.Add(new Vector3(sp < -0.05f ? sp / 2 : 0, a, -u));
            if (Target.part.CrewCapacity > 1) { if (sp > 0.05f) l.Add(new Vector3(0, a - sp, -u)); else if (sp < -0.05f) l.Add(new Vector3(-sp / 2, a, -u)); }
            return l;
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
            if (tab != 0)
            {
                var green = new Color(0.4f, 1f, 0.45f);
                for (int e = 0; e < 2; e++)
                {
                    var np = Screen(cam, tr.TransformPoint(NodePos(e)));
                    var tip = Screen(cam, tr.TransformPoint(NodePos(e) + (e == 0 ? Vector3.down : Vector3.up) * 0.3f));
                    if (np.z > 0) { AvionicsDraw.Diamond(np.xy(), 9, 2, green); if (tip.z > 0) AvionicsDraw.Line(np.xy(), tip.xy(), 2, green); }
                }
                if (Target.part.srfAttachNode != null)
                {
                    var sp3 = Screen(cam, tr.TransformPoint(Target.part.srfAttachNode.position));
                    if (sp3.z > 0) AvionicsDraw.Diamond(sp3.xy(), 6, 2, new Color(0.4f, 1f, 0.45f, 0.6f));
                }
                // seated crew: pan, back, head (a kerbal sits about 0.9 m tall)
                foreach (var seat in SeatPositions())
                {
                    var pan = Screen(cam, tr.TransformPoint(seat));
                    var head = Screen(cam, tr.TransformPoint(seat + new Vector3(0, 0, -0.75f)));
                    var knee = Screen(cam, tr.TransformPoint(seat + new Vector3(0, 0.45f, 0)));
                    var foot = Screen(cam, tr.TransformPoint(seat + new Vector3(0, 0.55f, 0.4f)));
                    if (pan.z <= 0 || head.z <= 0) continue;
                    var c = new Color(1f, 0.6f, 0.9f);
                    AvionicsDraw.Line(pan.xy(), head.xy(), 3, c); AvionicsDraw.Line(pan.xy(), knee.xy(), 3, c); if (foot.z > 0) AvionicsDraw.Line(knee.xy(), foot.xy(), 3, c);
                    AvionicsDraw.Box(head.xy(), 9, 2, c);
                }
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
            y += 24;
            float tbw = (w - 6) / 2;
            for (int i = 0; i < Tabs.Length; i++) if (GUI.Toggle(new Rect(x + (i % 2) * (tbw + 6), y + (i / 2) * 22, tbw, 20), tab == i, Tabs[i], tab == i ? on : btn)) tab = i;
            y += 48;
            if (tab != 0) { OtherTab(ref y, x, w, hw, tw, s); rect.height = y + 6; GUI.DragWindow(); return; }

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
