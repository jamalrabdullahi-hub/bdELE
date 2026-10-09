using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Dynamic boat hull on top of ELE Shape. Floats by its real shape: every ring of the skin is cut at the water surface,
    /// the submerged area x length gives displaced volume and the buoyancy acts at the true centre of buoyancy (so the hull
    /// rights itself, trims and heels like a boat). Water resistance from the wetted shape: low along the hull, high sideways
    /// (the hull is its own keel) and against heave. Hull generator: V / deep-V / flat / round-bilge / catamaran sections,
    /// raked or plumb bow, transom or cruiser stern. Editor readout: draft and freeboard for the craft's mass, waterline drawn.
    /// </summary>
    public class ModuleELEHull : PartModule
    {
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Length", guiUnits = " m", groupName = "elehull", groupDisplayName = "ELE Hull"),
         UI_FloatRange(minValue = 2, maxValue = 120, stepIncrement = 0.5f, scene = UI_Scene.Editor)]
        public float hullLength = 12f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Beam", guiUnits = " m", groupName = "elehull"),
         UI_FloatRange(minValue = 0.8f, maxValue = 30, stepIncrement = 0.1f, scene = UI_Scene.Editor)]
        public float hullBeam = 3.5f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Depth (keel to deck)", guiUnits = " m", groupName = "elehull"),
         UI_FloatRange(minValue = 0.4f, maxValue = 20, stepIncrement = 0.1f, scene = UI_Scene.Editor)]
        public float hullDepth = 2f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Hull form", groupName = "elehull"),
         UI_ChooseOption(options = new[] { "V", "DeepV", "Flat", "Round", "Catamaran" }, display = new[] { "V hull", "Deep-V (fast)", "Flat bottom", "Round bilge (ship)", "Catamaran" }, scene = UI_Scene.Editor)]
        public string hullForm = "V";

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Bow", groupName = "elehull"),
         UI_ChooseOption(options = new[] { "Raked", "Plumb", "Bulbous" }, scene = UI_Scene.Editor)]
        public string bow = "Raked";

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Stern", groupName = "elehull"),
         UI_ChooseOption(options = new[] { "Transom", "Cruiser" }, scene = UI_Scene.Editor)]
        public string stern = "Transom";

        [KSPField(guiActiveEditor = true, guiActive = true, guiName = "Water", groupName = "elehull")]
        public string waterInfo = "";

        [KSPField] public float hullDrag = 1f;   // scales the water resistance

        ModuleELEShape shape;
        List<Vector3[]> rings; string ringsFor;
        float editorWaterline = float.NaN;   // part-local "up" of the waterline for the craft's mass (editor)

        [KSPEvent(guiActiveEditor = true, guiName = "Generate hull (replaces the shape)", groupName = "elehull")]
        public void Generate()
        {
            if (shape == null) return;
            BuildHull();
            shape.Encode(); shape.Rebuild(true); shape.Propagate();
            ringsFor = null;
        }

        public override void OnLoad(ConfigNode node)
        {
            // the part in the parts list starts as a generated hull, not the shape part's default tube
            if (HighLogic.LoadedScene != GameScenes.LOADING) return;
            shape = part.FindModuleImplementing<ModuleELEShape>();
            if (shape != null) { BuildHull(); shape.Encode(); shape.Rebuild(false); }
        }

        public override void OnStart(StartState state)
        {
            shape = part.FindModuleImplementing<ModuleELEShape>();
            if (HighLogic.LoadedSceneIsFlight) part.buoyancy = 0f;   // this module does the floating
        }

        // ---------------- hull generator ----------------
        /// <summary>Right half of a section, from the deck centre clockwise (seen from the bow) to the bottom centre; x ≥ 0, y up.</summary>
        static List<(Vector2 p, bool sharp)> HalfOutline(string form, float b, float top, float bottom)
        {
            float h = top - bottom, hb = b / 2;
            var l = new List<(Vector2, bool)> { (new Vector2(0, top), false), (new Vector2(hb, top), true) };
            switch (form)
            {
                case "DeepV": l.Add((new Vector2(hb * 0.97f, bottom + h * 0.55f), true)); l.Add((new Vector2(0, bottom), true)); break;
                case "Flat": l.Add((new Vector2(hb, bottom + h * 0.08f), true)); l.Add((new Vector2(hb * 0.9f, bottom), true)); l.Add((new Vector2(0, bottom), false)); break;
                case "Round":
                    for (int i = 0; i <= 4; i++) { float a = i / 4f * Mathf.PI / 2; l.Add((new Vector2(hb * Mathf.Cos(a) * (i == 4 ? 0.0f : 1f), bottom + h * 0.45f * (1 - Mathf.Sin(a))), false)); }
                    l[l.Count - 1] = (new Vector2(0, bottom), false);
                    break;
                case "Catamaran":
                    float tunnel = bottom + h * 0.55f, keelX = hb * 0.72f;
                    l.Add((new Vector2(hb, bottom + h * 0.35f), true)); l.Add((new Vector2(keelX, bottom), true)); l.Add((new Vector2(keelX - hb * 0.22f, bottom + h * 0.35f), true));
                    l.Add((new Vector2(hb * 0.3f, tunnel), true)); l.Add((new Vector2(0, tunnel), false));
                    break;
                default: l.Add((new Vector2(hb * 0.98f, bottom + h * 0.4f), true)); l.Add((new Vector2(0, bottom), true)); break;   // V
            }
            return l;
        }

        /// <summary>Resample a half outline to k+1 points (even spacing, its corners kept sharp) and mirror to a full ring of 2k points.</summary>
        static void FillSection(ModuleELEShape.Section s, List<(Vector2 p, bool sharp)> half, int k)
        {
            var cum = new List<float> { 0 };
            for (int i = 1; i < half.Count; i++) cum.Add(cum[i - 1] + (half[i].p - half[i - 1].p).magnitude);
            float L = Mathf.Max(1e-4f, cum[cum.Count - 1]);
            var right = new Vector2[k + 1]; var sh = new bool[k + 1];
            for (int j = 0; j <= k; j++)
            {
                float t = L * j / k; int i = 1; while (i < half.Count - 1 && cum[i] < t) i++;
                float u = (t - cum[i - 1]) / Mathf.Max(1e-5f, cum[i] - cum[i - 1]);
                right[j] = Vector2.Lerp(half[i - 1].p, half[i].p, Mathf.Clamp01(u));
            }
            for (int i = 1; i < half.Count - 1; i++) if (half[i].sharp) { int j = Mathf.Clamp(Mathf.RoundToInt(cum[i] / L * k), 1, k - 1); right[j] = half[i].p; sh[j] = true; }
            s.pts.Clear(); s.sharp.Clear();
            for (int j = 0; j <= k; j++) { s.pts.Add(right[j]); s.sharp.Add(sh[j]); }
            for (int j = k - 1; j >= 1; j--) { s.pts.Add(new Vector2(-right[j].x, right[j].y)); s.sharp.Add(sh[j]); }
        }

        void BuildHull()
        {
            float L = hullLength, B = hullBeam, D = hullDepth;
            // stations from stern (0) to bow (1): width, keel rise (fraction of depth), deck sheer
            var st = new List<(float f, float w, float keel, float sheer)>();
            bool cat = hullForm == "Catamaran";
            if (stern == "Transom") st.Add((0f, 0.82f, 0.18f, 0f)); else { st.Add((0f, 0.08f, 0.45f, 0f)); st.Add((0.05f, 0.55f, 0.2f, 0f)); }
            st.Add((0.15f, 0.93f, 0.08f, 0f)); st.Add((0.4f, 1f, 0f, 0f)); st.Add((0.62f, 0.98f, 0f, 0.02f)); st.Add((0.8f, 0.82f, 0.05f, 0.05f));
            if (bow == "Plumb") { st.Add((0.93f, 0.45f, 0.1f, 0.07f)); st.Add((1f, cat ? 0.6f : 0.04f, 0.15f, 0.08f)); }
            else if (bow == "Bulbous") { st.Add((0.93f, 0.4f, 0.05f, 0.07f)); st.Add((0.985f, 0.18f, 0.02f, 0.08f)); st.Add((1f, 0.03f, 0.35f, 0.09f)); }
            else { st.Add((0.92f, 0.5f, 0.25f, 0.08f)); st.Add((1f, cat ? 0.55f : 0.03f, 0.75f, 0.12f)); }   // raked: forefoot cut away

            var secs = shape.sections; secs.Clear();
            foreach (var (f, w, keel, sheer) in st)
            {
                var s = new ModuleELEShape.Section { y = (f - 0.5f) * L, hard = f == 0f && stern == "Transom" };
                float top = D / 2 + sheer * D, bottom = -D / 2 + keel * D;
                FillSection(s, HalfOutline(hullForm, Mathf.Max(0.05f, B * w), top, Mathf.Min(bottom, top - 0.05f)), 8);
                secs.Add(s);
            }
            shape.canopy = false;
            shape.smoothing = 1;
            shape.spanSegments = Mathf.Clamp(Mathf.RoundToInt(L / 3f), 2, 8);
        }

        // ---------------- submerged geometry ----------------
        List<Vector3[]> HullRings()
        {
            if (shape == null) return null;
            if (rings == null || ringsFor != shape.shape) { rings = shape.Rings(true); ringsFor = shape.shape; }
            return rings;
        }

        /// <summary>Polygon of a ring below the water: depth(p) &gt; 0 is submerged (linear between points).</summary>
        static List<Vector3> Clip(Vector3[] ring, System.Func<Vector3, float> depth)
        {
            var o = new List<Vector3>();
            int n = ring.Length;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = ring[i], b = ring[(i + 1) % n];
                float da = depth(a), db = depth(b);
                if (da > 0) o.Add(a);
                if ((da > 0) != (db > 0)) o.Add(Vector3.Lerp(a, b, da / (da - db)));
            }
            return o;
        }

        /// <summary>Area (along the axis) and centroid of a planar polygon (fan from its first point).</summary>
        static (float area, Vector3 c, float perim, float width, float height) Props(List<Vector3> poly, Vector3 axis, Vector3 up)
        {
            if (poly.Count < 3) return (0, Vector3.zero, 0, 0, 0);
            float A = 0; Vector3 c = Vector3.zero; float per = 0;
            for (int i = 1; i < poly.Count - 1; i++)
            {
                float a = Vector3.Dot(Vector3.Cross(poly[i] - poly[0], poly[i + 1] - poly[0]), axis) * 0.5f;
                A += a; c += a * (poly[0] + poly[i] + poly[i + 1]) / 3f;
            }
            for (int i = 0; i < poly.Count; i++) per += (poly[(i + 1) % poly.Count] - poly[i]).magnitude;
            var right = Vector3.Cross(up, axis).normalized;
            float w = poly.Max(p => Vector3.Dot(p, right)) - poly.Min(p => Vector3.Dot(p, right));
            float h = poly.Max(p => Vector3.Dot(p, up)) - poly.Min(p => Vector3.Dot(p, up));
            if (Mathf.Abs(A) < 1e-6f) return (0, poly[0], per, w, h);
            return (Mathf.Abs(A), c / A, per, w, h);
        }

        // ---------------- flight: buoyancy + water resistance ----------------
        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || part.rb == null || vessel == null || vessel.packed || shape == null) return;
            var body = vessel.mainBody; if (!body.ocean) return;
            if (vessel.altitude > hullDepth + hullLength) { waterInfo = "flying"; return; }
            var rs = HullRings(); if (rs == null || rs.Count < 2) return;
            var tr = part.transform;
            Vector3d bc = body.position; double R = body.Radius;
            float Depth(Vector3 w) => (float)(R - ((Vector3d)w - bc).magnitude);
            Vector3 g = FlightGlobals.getGeeForceAtPosition(part.transform.position);
            Vector3 upW = -g.normalized; float gm = g.magnitude;
            float rho = (float)body.oceanDensity;   // t/m³: forces come out in kN
            Vector3 axis = tr.up;
            var world = new List<(float area, Vector3 c, float per, float w, float h)>(rs.Count);
            foreach (var ring in rs)
            {
                var wr = new Vector3[ring.Length];
                for (int i = 0; i < ring.Length; i++) wr[i] = tr.TransformPoint(ring[i]);
                world.Add(Props(Clip(wr, Depth), axis, upW));
            }
            float vol = 0;
            Vector3 vframe = (Vector3)vessel.srf_velocity - vessel.rb_velocity;   // water is still in the surface frame
            for (int i = 0; i < world.Count - 1; i++)
            {
                var a = world[i]; var b = world[i + 1];
                if (a.area <= 0 && b.area <= 0) continue;
                float dy = Mathf.Abs(Vector3.Dot(tr.TransformPoint(rs[i + 1][0]) - tr.TransformPoint(rs[i][0]), axis));
                if (dy < 1e-4f) continue;
                float v = (a.area + b.area) * 0.5f * dy; vol += v;
                Vector3 c = a.area > 0 && b.area > 0 ? (a.c * a.area + b.c * b.area) / (a.area + b.area) : (a.area > 0 ? a.c : b.c);
                part.rb.AddForceAtPosition(upW * rho * gm * v, c, ForceMode.Force);

                // resistance on this slice
                Vector3 rel = part.rb.GetPointVelocity(c) + vframe;
                float vf = Vector3.Dot(rel, axis);
                Vector3 side = Vector3.Cross(upW, axis).normalized;
                float vs = Vector3.Dot(rel, side), vu = Vector3.Dot(rel, upW);
                float wet = (a.per + b.per) * 0.5f * dy, hs = (a.h + b.h) * 0.5f, ws = (a.w + b.w) * 0.5f;
                float q = 0.5f * rho * hullDrag;
                Vector3 F = -axis * q * vf * Mathf.Abs(vf) * (0.006f * wet + 0.02f * Mathf.Abs(b.area - a.area))   // skin friction + form (where the section changes)
                          - side * q * vs * Mathf.Abs(vs) * 1.0f * hs * dy                                     // the hull as keel
                          - upW * q * vu * Mathf.Abs(vu) * 1.2f * ws * dy;                                     // heave / pitch damping
                part.rb.AddForceAtPosition(F, c, ForceMode.Force);
            }
            waterInfo = vol > 0 ? $"displacing {vol * rho:F1} t of {vessel.GetTotalMass():F1} t" : "out of the water";
        }

        // ---------------- editor: draft for the craft's mass ----------------
        float nextEditorCalc;
        void Update()
        {
            if (!HighLogic.LoadedSceneIsEditor || shape == null || Time.time < nextEditorCalc) return;
            nextEditorCalc = Time.time + 0.5f;
            var ship = EditorLogic.fetch?.ship; if (ship == null) return;
            float mass = ship.GetTotalMass();
            var rs = HullRings(); if (rs == null || rs.Count < 2) return;
            // part-local: up is -Z. Volume below a level waterline at height u, by bisection on u.
            float lo = rs.Min(r => r.Min(p => -p.z)), hi = rs.Max(r => r.Max(p => -p.z));
            float Vol(float u)
            {
                float v = 0; var prev = (0f, Vector3.zero, 0f, 0f, 0f);
                for (int i = 0; i < rs.Count; i++)
                {
                    var pr = Props(Clip(rs[i], p => u - (-p.z)), Vector3.up, Vector3.back);
                    if (i > 0) v += (prev.Item1 + pr.area) * 0.5f * Mathf.Abs(rs[i][0].y - rs[i - 1][0].y);
                    prev = (pr.area, pr.c, pr.perim, pr.width, pr.height);
                }
                return v;
            }
            float full = Vol(hi), need = mass / 1.0f;   // Kerbin sea water ~1 t/m³
            if (need >= full) { editorWaterline = hi; waterInfo = $"SINKS: craft {mass:F1} t > hull {full:F1} t"; return; }
            float a = lo, b = hi;
            for (int k = 0; k < 24; k++) { float m = (a + b) / 2; if (Vol(m) < need) a = m; else b = m; }
            editorWaterline = (a + b) / 2;
            waterInfo = $"draft {editorWaterline - lo:F2} m, freeboard {hi - editorWaterline:F2} m ({mass:F1} t, reserve {(full - need) / full * 100:F0}%)";
        }

        void OnGUI()
        {
            if (!HighLogic.LoadedSceneIsEditor || float.IsNaN(editorWaterline) || ShapeEditor.Target != shape || Event.current.type != EventType.Repaint) return;
            var cam = EditorLogic.fetch?.editorCamera; var rs = HullRings(); if (cam == null || rs == null) return;
            if (!AvionicsDraw.Begin()) return;
            var tr = part.transform; var col = new Color(0.3f, 0.7f, 1f);
            foreach (var ring in rs)
            {
                // the waterline crossing points on this ring
                var pts = new List<Vector3>();
                for (int i = 0; i < ring.Length; i++)
                {
                    Vector3 a = ring[i], b = ring[(i + 1) % ring.Length];
                    float da = editorWaterline + a.z, db = editorWaterline + b.z;
                    if ((da > 0) != (db > 0)) pts.Add(Vector3.Lerp(a, b, da / (da - db)));
                }
                for (int i = 0; i + 1 < pts.Count; i += 2)
                {
                    var s0 = cam.WorldToScreenPoint(tr.TransformPoint(pts[i])); var s1 = cam.WorldToScreenPoint(tr.TransformPoint(pts[i + 1]));
                    if (s0.z > 0 && s1.z > 0) AvionicsDraw.Line(new Vector2(s0.x, Screen.height - s0.y), new Vector2(s1.x, Screen.height - s1.y), 2, col);
                }
            }
            AvionicsDraw.End();
        }
    }
}
