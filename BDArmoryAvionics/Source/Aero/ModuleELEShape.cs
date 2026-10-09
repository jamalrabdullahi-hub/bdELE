using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// ELE Shape: a fuselage / pod / fairing / canopy built from cross-sections you edit with handles in the VAB/SPH
    /// (ShapeEditor). Each section is a ring of points; every section has the same number of points, so they loft into one
    /// smooth skin (rounded corners, or sharp creases per point) with the stock panelled texture. Length runs along the
    /// part's Y axis (SPH: nose), the section plane is X (right) and U (up, part -Z).
    /// Mass from skin area, optional fuel from the enclosed volume, nodes on the two end sections, convex collider, drag cubes.
    /// </summary>
    public class ModuleELEShape : PartModule, IPartMassModifier, IPartCostModifier
    {
        public class Section
        {
            public float y; public Vector2 offset; public bool hard;          // hard: straight join here, no curve through it
            public List<Vector2> pts = new(); public List<bool> sharp = new();
            public Section Clone() => new() { y = y, offset = offset, hard = hard, pts = new List<Vector2>(pts), sharp = new List<bool>(sharp) };
        }

        [KSPField(isPersistant = true)] public string shape = "";
        [KSPField] public string meshName = "frameMesh";
        [KSPField] public float skinDensity = 0.006f;   // t per m² (skin + frames)
        [KSPField] public float costPerM2 = 12f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Look", groupName = "eleshape", groupDisplayName = "ELE Shape"),
         UI_ChooseOption(options = new[] { "skin", "dark", "metal", "accent", "glass" }, display = new[] { "Stock white", "Stock dark", "Metal", "Painted", "Glass" }, scene = UI_Scene.Editor)]
        public string role = "skin";

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Paint", groupName = "eleshape"),
         UI_ChooseOption(options = new[] { "White", "Grey", "Dark", "Olive", "Tan", "Navy", "Red", "Orange" }, scene = UI_Scene.Editor)]
        public string paint = "White";

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Smoothness", groupName = "eleshape"),
         UI_FloatRange(minValue = 0, maxValue = 3, stepIncrement = 1, scene = UI_Scene.Editor)]
        public float smoothing = 2;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Lengthwise detail", groupName = "eleshape"),
         UI_FloatRange(minValue = 1, maxValue = 10, stepIncrement = 1, scene = UI_Scene.Editor)]
        public float spanSegments = 5;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Tank", groupName = "eleshape"),
         UI_ChooseOption(options = new[] { "None", "LiquidFuel", "LFO", "MonoPropellant" }, display = new[] { "Empty", "Liquid fuel", "Fuel + oxidiser", "Monoprop" }, scene = UI_Scene.Editor)]
        public string tank = "None";

        [KSPField(guiActiveEditor = true, guiActive = true, guiName = "Shape", groupName = "eleshape")]
        public string info = "";

        public static readonly Dictionary<string, Color> Paints = new()
        {
            { "White", Color.white }, { "Grey", new Color(0.62f, 0.64f, 0.66f) }, { "Dark", new Color(0.25f, 0.26f, 0.28f) }, { "Olive", new Color(0.42f, 0.45f, 0.3f) },
            { "Tan", new Color(0.72f, 0.64f, 0.48f) }, { "Navy", new Color(0.2f, 0.26f, 0.4f) }, { "Red", new Color(0.7f, 0.15f, 0.12f) }, { "Orange", new Color(0.95f, 0.5f, 0.12f) },
        };

        public readonly List<Section> sections = new();
        Transform meshT; Mesh mesh, hull;
        float area, volume;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [KSPEvent(guiActiveEditor = true, guiName = "Edit shape (handles)", groupName = "eleshape")]
        public void EditShape() => ShapeEditor.Open(this);

        // ---------------- lifecycle ----------------
        public override void OnStart(StartState state)
        {
            meshT = part.FindModelTransform(meshName);
            foreach (var f in new[] { nameof(role), nameof(paint), nameof(smoothing), nameof(spanSegments), nameof(tank) })
            {
                var c = Fields[f].uiControlEditor; if (c != null) c.onFieldChanged = (a, b) => { Rebuild(false); Propagate(); };
            }
            Decode();
            Rebuild(false);
        }

        public override void OnLoad(ConfigNode node) { if (meshT == null) meshT = part.FindModelTransform(meshName); if (meshT != null) { Decode(); Rebuild(false); } }   // also on the prefab: the parts list shows the real shape

        public void Decode()
        {
            sections.Clear();
            if (!string.IsNullOrEmpty(shape))
            {
                try
                {
                    foreach (var s in shape.Split('|'))
                    {
                        var parts = s.Split(':');
                        var head = parts[0].Split(',');
                        var sec = new Section { y = F(head[0]), offset = new Vector2(F(head[1]), F(head[2])), hard = head.Length > 3 && head[3] == "1" };
                        foreach (var p in parts[1].Split(';'))
                        {
                            var q = p.Split(',');
                            sec.pts.Add(new Vector2(F(q[0]), F(q[1]))); sec.sharp.Add(q.Length > 2 && q[2] == "1");
                        }
                        sections.Add(sec);
                    }
                }
                catch (Exception e) { Debug.LogWarning($"[ELE Shape] bad shape data, using default: {e.Message}"); sections.Clear(); }
            }
            if (sections.Count < 2 || sections.Any(s => s.pts.Count != sections[0].pts.Count || s.pts.Count < 3)) Default();
        }

        public void Encode()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                if (i > 0) sb.Append('|');
                sb.Append($"{S(s.y)},{S(s.offset.x)},{S(s.offset.y)},{(s.hard ? 1 : 0)}:");
                for (int k = 0; k < s.pts.Count; k++) { if (k > 0) sb.Append(';'); sb.Append($"{S(s.pts[k].x)},{S(s.pts[k].y)},{(s.sharp[k] ? 1 : 0)}"); }
            }
            shape = sb.ToString();
        }

        static float F(string s) => float.Parse(s, NumberStyles.Float, Inv);
        static string S(float f) => f.ToString("0.####", Inv);

        void Default()
        {
            sections.Clear();
            float[] ys = { -1.25f, -0.4f, 0.4f, 1.25f };
            foreach (var y in ys) { var s = new Section { y = y }; Presets.Circle(s, 12, 0.625f, 0.625f); sections.Add(s); }
            Encode();
        }

        // ---------------- geometry ----------------
        /// <summary>One smoothing pass: each point becomes three (corner cut), sharp points stay put. Point count always triples.</summary>
        static void Smooth(List<Vector2> p, List<bool> sh, out List<Vector2> o, out List<bool> osh)
        {
            int n = p.Count; o = new List<Vector2>(n * 3); osh = new List<bool>(n * 3);
            for (int i = 0; i < n; i++)
            {
                Vector2 v = p[i], prev = p[(i + n - 1) % n], next = p[(i + 1) % n];
                if (sh[i]) { o.Add(v); o.Add(v); o.Add(v); osh.Add(false); osh.Add(true); osh.Add(false); continue; }
                Vector2 a = Vector2.Lerp(v, prev, 0.25f), c = Vector2.Lerp(v, next, 0.25f);
                o.Add(a); o.Add(v * 0.5f + (a + c) * 0.25f); o.Add(c);
                osh.Add(false); osh.Add(false); osh.Add(false);
            }
        }

        public List<Vector2> SmoothRing(Section s)
        {
            List<Vector2> p = s.pts; List<bool> sh = s.sharp;
            for (int i = 0; i < (int)smoothing; i++) { Smooth(p, sh, out var o, out var osh); p = o; sh = osh; }
            return p;
        }

        /// <summary>Section point (x, up) at position y to part-local 3D (up is part -Z, as in the SPH).</summary>
        public static Vector3 Local(Section s, Vector2 p) => new(p.x + s.offset.x, s.y, -(p.y + s.offset.y));

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
        }

        /// <summary>All rings of the skin: section rings smoothed, with curved in-between rings (straight next to "hard" sections).</summary>
        List<Vector3[]> Rings(bool detailed)
        {
            var secRings = sections.Select(s => (detailed ? SmoothRing(s) : s.pts).Select(p => Local(s, p)).ToArray()).ToList();
            var rings = new List<Vector3[]>();
            int segs = detailed ? Mathf.Max(1, (int)spanSegments) : 1;
            for (int r = 0; r < secRings.Count - 1; r++)
            {
                var A = secRings[r]; var B = secRings[r + 1];
                var P = r > 0 && !sections[r].hard ? secRings[r - 1] : null;
                var Q = r + 2 < secRings.Count && !sections[r + 1].hard ? secRings[r + 2] : null;
                for (int k = 0; k < segs; k++)
                {
                    float t = (float)k / segs;
                    var ring = new Vector3[A.Length];
                    for (int i = 0; i < A.Length; i++)
                    {
                        Vector3 p0 = P != null ? P[i] : A[i] * 2 - B[i], p3 = Q != null ? Q[i] : B[i] * 2 - A[i];
                        ring[i] = CatmullRom(p0, A[i], B[i], p3, t);
                    }
                    rings.Add(ring);
                }
            }
            rings.Add(secRings[secRings.Count - 1]);
            return rings;
        }

        public void Rebuild(bool moveChildren)
        {
            if (meshT == null) meshT = part.FindModelTransform(meshName);
            if (meshT == null || sections.Count < 2) return;
            sections.Sort((a, b) => a.y.CompareTo(b.y));

            var rings = Rings(true);
            int n = rings[0].Length;
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            bool atlas = role != "glass" && role != "accent";
            var rect = StockSkin.RectFor(role);
            float tile = role == "skin" ? 1.4f : 0.7f, tileH = tile * rect.height / rect.width;
            area = 0;
            float along = 0;
            for (int r = 0; r < rings.Count - 1; r++)
            {
                var A = rings[r]; var B = rings[r + 1];
                float step = ((A.Aggregate(Vector3.zero, (s, p) => s + p) - B.Aggregate(Vector3.zero, (s, p) => s + p)) / n).magnitude;
                float va = along / tileH, vb = (along + step) / tileH, vf = Mathf.Floor(va);
                along += step;
                float arc = 0;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    float seg = (A[j] - A[i]).magnitude;
                    float ua = arc / tile, ub = (arc + seg) / tile, uf = Mathf.Floor(ua); arc += seg;
                    int k = v.Count;
                    v.Add(A[i]); v.Add(A[j]); v.Add(B[i]); v.Add(B[j]);
                    if (atlas)
                    {
                        float u0 = Mathf.Clamp01(ua - uf), u1 = Mathf.Clamp01(ub - uf), v0 = Mathf.Clamp01(va - vf), v1 = Mathf.Clamp01(vb - vf);
                        uv.Add(new Vector2(rect.x + u0 * rect.width, rect.y + v0 * rect.height)); uv.Add(new Vector2(rect.x + u1 * rect.width, rect.y + v0 * rect.height));
                        uv.Add(new Vector2(rect.x + u0 * rect.width, rect.y + v1 * rect.height)); uv.Add(new Vector2(rect.x + u1 * rect.width, rect.y + v1 * rect.height));
                    }
                    else { uv.Add(Vector2.zero); uv.Add(Vector2.right); uv.Add(Vector2.up); uv.Add(Vector2.one); }
                    tri.AddRange(new[] { k, k + 2, k + 1, k + 1, k + 2, k + 3 });
                    area += Vector3.Cross(A[j] - A[i], B[i] - A[i]).magnitude * 0.5f + Vector3.Cross(B[j] - A[j], B[i] - A[j]).magnitude * 0.5f;
                }
            }
            // end caps (flat fans) when the end section has size
            void Cap(Vector3[] ring, bool start)
            {
                var c = ring.Aggregate(Vector3.zero, (s, p) => s + p) / ring.Length;
                if (ring.Max(p => (p - c).magnitude) < 0.01f) return;
                int ci = v.Count; v.Add(c); uv.Add(new Vector2(rect.x + rect.width / 2, rect.y + rect.height / 2));
                foreach (var p in ring) { v.Add(p); uv.Add(new Vector2(rect.x + rect.width / 2, rect.y + rect.height / 2)); }
                for (int i = 0; i < n; i++) { int a = ci + 1 + i, b = ci + 1 + (i + 1) % n; if (start) tri.AddRange(new[] { ci, a, b }); else tri.AddRange(new[] { ci, b, a }); area += Vector3.Cross(v[a] - c, v[b] - c).magnitude * 0.5f; }
            }
            Cap(rings[0], true); Cap(rings[rings.Count - 1], false);

            // outward faces: signed volume of the closed skin must be positive
            volume = 0;
            for (int i = 0; i < tri.Count; i += 3) volume += Vector3.Dot(v[tri[i]], Vector3.Cross(v[tri[i + 1]], v[tri[i + 2]])) / 6f;
            if (volume < 0) { for (int i = 0; i < tri.Count; i += 3) (tri[i + 1], tri[i + 2]) = (tri[i + 2], tri[i + 1]); volume = -volume; }

            if (mesh == null) mesh = new Mesh { name = "ELEShape" };
            mesh.Clear(); mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(tri, 0);
            mesh.RecalculateNormals(); SmoothNormals(mesh); mesh.RecalculateBounds(); mesh.RecalculateTangents();
            meshT.localScale = Vector3.one; meshT.localPosition = Vector3.zero; meshT.localRotation = Quaternion.identity;
            var mf = meshT.GetComponent<MeshFilter>(); if (mf != null) mf.sharedMesh = mesh;
            var mr = meshT.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = StockSkin.Mat(role, Paints.TryGetValue(paint, out var col) ? col : Color.white);

            // collider: the unsmoothed hull (few points, cheap and convex)
            var hr = Rings(false);
            var hv = new List<Vector3>(); foreach (var r in hr) hv.AddRange(r);
            var ht = new List<int>();
            int hn = hr[0].Length;
            for (int r = 0; r < hr.Count - 1; r++) for (int i = 0; i < hn; i++) { int a = r * hn + i, b = r * hn + (i + 1) % hn, c = a + hn, d = b + hn; ht.AddRange(new[] { a, c, b, b, c, d }); }
            if (hull == null) hull = new Mesh { name = "ELEShapeHull" };
            hull.Clear(); hull.SetVertices(hv); hull.SetTriangles(ht, 0); hull.RecalculateBounds();
            foreach (var bc in meshT.GetComponents<BoxCollider>()) Destroy(bc);
            var mc = meshT.GetComponent<MeshCollider>() ?? meshT.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = null; mc.convex = true; mc.sharedMesh = hull;

            // nodes on the end sections
            var s0 = sections[0]; var s1 = sections[sections.Count - 1];
            SetNode("bottom", new Vector3(s0.offset.x, s0.y, -s0.offset.y), Vector3.down, moveChildren);
            SetNode("top", new Vector3(s1.offset.x, s1.y, -s1.offset.y), Vector3.up, moveChildren);

            UpdateTank();
            if (part.DragCubes != null) { part.DragCubes.Procedural = true; if (HighLogic.LoadedSceneIsFlight) part.DragCubes.ForceUpdate(true, true, true); }
            float len = s1.y - s0.y;
            info = $"{len:F2} m long, {sections.Count} sections × {s0.pts.Count} pts, {area:F1} m², {volume:F2} m³, {Mathf.Max(0.005f, area * skinDensity):F2} t";
        }

        static void SmoothNormals(Mesh m)
        {
            var v = m.vertices; var n = m.normals;
            var map = new Dictionary<Vector3Int, Vector3>();
            Vector3Int K(Vector3 p) => new(Mathf.RoundToInt(p.x * 1000), Mathf.RoundToInt(p.y * 1000), Mathf.RoundToInt(p.z * 1000));
            for (int i = 0; i < v.Length; i++) { var k = K(v[i]); map[k] = (map.TryGetValue(k, out var s) ? s : Vector3.zero) + n[i]; }
            for (int i = 0; i < v.Length; i++) { var s = map[K(v[i])]; if (Vector3.Angle(s, n[i]) < 40f) n[i] = s.normalized; }   // creases (sharp points) keep their own normals
            m.normals = n;
        }

        void SetNode(string id, Vector3 pos, Vector3 dir, bool moveChildren)
        {
            var nd = part.FindAttachNode(id); if (nd == null) return;
            var old = nd.position;
            nd.position = nd.originalPosition = pos; nd.orientation = nd.originalOrientation = dir;
            if (!moveChildren || nd.attachedPart == null || nd.attachedPart.parent != part) return;
            var child = nd.attachedPart; var d = part.transform.TransformVector(pos - old);
            child.transform.position += d; child.attPos0 += d;
        }

        void UpdateTank()
        {
            if (!HighLogic.LoadedSceneIsEditor) return;
            float units = volume * 1000f / 5f * 0.85f;   // 5 L per unit, 85 % usable
            var want = new Dictionary<string, double>();
            if (tank == "LiquidFuel") want["LiquidFuel"] = units;
            else if (tank == "LFO") { want["LiquidFuel"] = units * 0.45f; want["Oxidizer"] = units * 0.55f; }
            else if (tank == "MonoPropellant") want["MonoPropellant"] = units * 0.8f;
            foreach (var r in part.Resources.ToList()) if (!want.ContainsKey(r.resourceName)) part.Resources.Remove(r);
            foreach (var kv in want)
            {
                var r = part.Resources[kv.Key];
                if (r == null) part.Resources.Add(kv.Key, kv.Value, kv.Value, true, true, false, true, PartResource.FlowMode.Both);
                else { bool full = r.amount >= r.maxAmount - 0.01; r.maxAmount = kv.Value; r.amount = full ? kv.Value : Math.Min(r.amount, kv.Value); }
            }
            GameEvents.onPartResourceListChange.Fire(part);
        }

        /// <summary>Copy this shape to the part's symmetry twins and tell the editor.</summary>
        public void Propagate()
        {
            Encode();
            foreach (var p in part.symmetryCounterparts)
            {
                var m = p.FindModuleImplementing<ModuleELEShape>(); if (m == null) continue;
                m.role = role; m.paint = paint; m.smoothing = smoothing; m.spanSegments = spanSegments; m.tank = tank;
                m.sections.Clear();
                foreach (var s in sections) m.sections.Add(s.Clone());
                m.Encode(); m.Rebuild(true);
            }
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch?.ship != null) GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
        }

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) => Mathf.Max(0.005f, area * skinDensity) - defaultMass;
        public ModifierChangeWhen GetModuleMassChangeWhen() => ModifierChangeWhen.FIXED;
        public float GetModuleCost(float defaultCost, ModifierStagingSituation sit) => area * costPerM2;
        public ModifierChangeWhen GetModuleCostChangeWhen() => ModifierChangeWhen.FIXED;

        void OnDestroy() { if (mesh != null) Destroy(mesh); if (hull != null) Destroy(hull); if (ShapeEditor.Target == this) ShapeEditor.Close(); }
    }

    /// <summary>Cross-section presets (keep the point count; mirror-symmetric: point 0 on top, points run clockwise seen from the nose).</summary>
    public static class Presets
    {
        public static readonly string[] Names = { "Circle", "Rounded box", "Box", "Mk2 lifting", "Teardrop", "Wedge (flat top)", "Diamond", "Point" };

        public static void Apply(ModuleELEShape.Section s, string name, float w, float h)
        {
            int n = s.pts.Count;
            switch (name)
            {
                case "Circle": Circle(s, n, w / 2, h / 2); break;
                case "Rounded box": Super(s, n, w / 2, h / 2, 4f); break;
                case "Box": Super(s, n, w / 2, h / 2, 12f); MarkCorners(s); break;
                case "Mk2 lifting": Super(s, n, w / 2, h / 2, 3f); for (int i = 0; i < n; i++) { var p = s.pts[i]; float k = Mathf.Clamp01(-p.y / (h / 2)); s.pts[i] = new Vector2(p.x * (1 + 0.35f * (1 - k)), p.y * (p.y > 0 ? 0.6f : 1f)); } break;
                case "Teardrop": Circle(s, n, w / 2, h / 2); for (int i = 0; i < n; i++) { var p = s.pts[i]; if (p.y > 0) s.pts[i] = new Vector2(p.x * (1 - p.y / (h / 2) * 0.85f), p.y * 1.4f); } s.sharp[0] = true; break;
                case "Wedge (flat top)": Super(s, n, w / 2, h / 2, 12f); for (int i = 0; i < n; i++) { var p = s.pts[i]; if (p.y < 0) s.pts[i] = new Vector2(p.x * (1 + p.y / (h / 2) * 0.9f), p.y); } MarkCorners(s); break;
                case "Diamond": Super(s, n, w / 2, h / 2, 1f); for (int i = 0; i < n; i++) s.sharp[i] = false; s.sharp[0] = s.sharp[n / 2] = true; if (n % 4 == 0) { s.sharp[n / 4] = s.sharp[3 * n / 4] = true; } break;
                case "Point": Circle(s, n, 0.01f, 0.01f); break;
            }
        }

        public static void Circle(ModuleELEShape.Section s, int n, float rx, float ry) => Super(s, n, rx, ry, 2f);

        /// <summary>Superellipse with n points from the top, clockwise seen from the nose (x right, y up).</summary>
        public static void Super(ModuleELEShape.Section s, int n, float rx, float ry, float e)
        {
            s.pts.Clear(); s.sharp.Clear();
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.PI / 2 - 2 * Mathf.PI * i / n, c = Mathf.Cos(a), sn = Mathf.Sin(a), k = 2f / e;
                s.pts.Add(new Vector2(Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), k) * rx, Mathf.Sign(sn) * Mathf.Pow(Mathf.Abs(sn), k) * ry));
                s.sharp.Add(false);
            }
        }

        static void MarkCorners(ModuleELEShape.Section s)
        {
            int n = s.pts.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 p = s.pts[i], a = s.pts[(i + n - 1) % n], b = s.pts[(i + 1) % n];
                s.sharp[i] = Vector2.Angle(p - a, b - p) > 35f;
            }
        }
    }
}
