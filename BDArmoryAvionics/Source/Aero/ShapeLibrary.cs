using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Stock profiles for section ends (so an ELE Shape meets Mk1 / Mk2 / Mk3 / round stock parts flush), and the shape library:
    /// whole shapes and single cross-sections saved by name in GameData/BDArmoryAvionics/PluginData/Shapes.
    /// </summary>
    public static class ShapeLibrary
    {
        // ---------------- stock profiles ----------------
        public static readonly (string label, string part, float round, int nodeSize)[] Stock =
        {
            ("0.625 m (size 0)", null, 0.625f, 0), ("Mk1 / 1.25 m", null, 1.25f, 1), ("1.875 m", null, 1.875f, 1), ("2.5 m (size 2)", null, 2.5f, 2),
            ("3.75 m (size 3)", null, 3.75f, 3), ("Mk2", "mk2Fuselage", 0, 1), ("Mk3", "mk3FuselageLF_50", 0, 2),
        };

        static readonly Dictionary<string, List<Vector2>> sampled = new();

        /// <summary>Give a section a stock profile (keeping its point count). Mk2 / Mk3 are measured from the stock parts themselves.</summary>
        public static bool ApplyStock(ModuleELEShape.Section s, int index, out int nodeSize)
        {
            var (label, partName, round, size) = Stock[index];
            nodeSize = size;
            int n = s.pts.Count;
            if (partName == null) { Presets.Circle(s, n, round / 2, round / 2); return true; }
            var prof = Sample(partName, n);
            if (prof == null) { if (label == "Mk2") Presets.Apply(s, "Mk2 lifting", 1.5f, 0.75f); else Presets.Super(s, n, 1.875f, 1.7f, 3f); ScreenMessages.PostScreenMessage($"{label}: stock part not readable, using an approximation", 3f, ScreenMessageStyle.UPPER_CENTER); return true; }
            s.pts = new List<Vector2>(prof);
            s.sharp = Enumerable.Repeat(false, n).ToList();
            // corners: where the outline turns sharply
            for (int i = 0; i < n; i++) { Vector2 a = s.pts[(i + n - 1) % n], p = s.pts[i], b = s.pts[(i + 1) % n]; s.sharp[i] = Vector2.Angle(p - a, b - p) > 40f; }
            return true;
        }

        /// <summary>The cross-section of a stock fuselage: for n directions from the centre, the farthest mesh point that way (x right, y up).</summary>
        static List<Vector2> Sample(string partName, int n)
        {
            string key = partName + "#" + n;
            if (sampled.TryGetValue(key, out var cached)) return cached;
            var info = PartLoader.getPartInfoByName(partName);
            if (info?.partPrefab == null) return null;
            var root = info.partPrefab.transform;
            var pts = new List<Vector2>();
            foreach (var mf in info.partPrefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                var r = mf.GetComponent<Renderer>(); if (r == null) continue;
                var m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices) { var l = m.MultiplyPoint3x4(v); pts.Add(new Vector2(l.x, -l.z)); }
            }
            if (pts.Count < 8) return null;
            var c = new Vector2((pts.Max(p => p.x) + pts.Min(p => p.x)) / 2, (pts.Max(p => p.y) + pts.Min(p => p.y)) / 2);
            var outl = new List<Vector2>();
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.PI / 2 - 2 * Mathf.PI * i / n;
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                // farthest support along d among points within a narrow wedge around d
                float best = 0;
                foreach (var p in pts)
                {
                    var q = p - c; float along = Vector2.Dot(q, d);
                    if (along <= 0) continue;
                    float side = Mathf.Abs(q.x * d.y - q.y * d.x);
                    if (side < along * Mathf.Tan(Mathf.PI / n) + 0.02f && along > best) best = along;
                }
                outl.Add(d * best);
            }
            // symmetric left / right (stock fuselages are)
            for (int i = 1; i < n / 2; i++) { var r = outl[i]; var l = outl[n - i]; float x = (r.x - l.x) / 2, y = (r.y + l.y) / 2; outl[i] = new Vector2(x, y); outl[n - i] = new Vector2(-x, y); }
            sampled[key] = outl;
            return outl;
        }

        // ---------------- library ----------------
        public static string Folder => Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "BDArmoryAvionics", "PluginData", "Shapes");

        public static IEnumerable<string> List(string kind)
        {
            if (!Directory.Exists(Folder)) return new string[0];
            return Directory.GetFiles(Folder, "*." + kind + ".cfg").Select(f => Path.GetFileName(f).Replace("." + kind + ".cfg", "")).OrderBy(x => x);
        }

        static string Safe(string name) { foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_'); return name.Trim(); }

        /// <summary>Save the whole shape (sections, look, nodes, canopy, seats).</summary>
        public static void SaveShape(ModuleELEShape m, string name)
        {
            if (string.IsNullOrEmpty(Safe(name))) return;
            Directory.CreateDirectory(Folder);
            m.Encode();
            var n = new ConfigNode("ELE_SHAPE");
            n.AddValue("name", name);
            foreach (var f in new[] { "shape", "role", "paint", "smoothing", "spanSegments", "tank", "bodyLift", "nodeTopOffset", "nodeBottomOffset", "nodeTopSize", "nodeBottomSize", "surfaceNode", "canopy", "canopyFrom", "canopyTo", "canopyAbove", "seatAlong", "seatUp", "seatSpacing" })
            {
                var v = m.Fields.GetValue(f);
                n.AddValue(f, v is Vector3 vv ? $"{vv.x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{vv.y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{vv.z.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : System.Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
            }
            var w = new ConfigNode(); w.AddNode(n); w.Save(Path.Combine(Folder, Safe(name) + ".shape.cfg"));
            ScreenMessages.PostScreenMessage($"Shape saved: {name}", 2f, ScreenMessageStyle.UPPER_CENTER);
        }

        public static bool LoadShape(ModuleELEShape m, string name)
        {
            var n = ConfigNode.Load(Path.Combine(Folder, Safe(name) + ".shape.cfg"))?.GetNode("ELE_SHAPE");
            if (n == null) return false;
            foreach (ConfigNode.Value v in n.values)
            {
                if (v.name == "name") continue;
                var f = m.Fields[v.name]; if (f == null) continue;
                try { f.Read(v.value, m); } catch { }
            }
            m.Decode(); m.Rebuild(true); m.Propagate();
            return true;
        }

        /// <summary>Save one cross-section (its outline and creases) to reuse on any section.</summary>
        public static void SaveSection(ModuleELEShape.Section s, string name)
        {
            if (string.IsNullOrEmpty(Safe(name))) return;
            Directory.CreateDirectory(Folder);
            var n = new ConfigNode("ELE_SECTION");
            n.AddValue("name", name);
            n.AddValue("points", string.Join(";", s.pts.Select((p, i) => $"{p.x.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)},{p.y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)},{(s.sharp[i] ? 1 : 0)}")));
            var w = new ConfigNode(); w.AddNode(n); w.Save(Path.Combine(Folder, Safe(name) + ".section.cfg"));
            ScreenMessages.PostScreenMessage($"Section saved: {name}", 2f, ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>Put a saved section's outline on a section, resampled to its point count, at its current size.</summary>
        public static bool LoadSection(ModuleELEShape.Section s, string name, bool keepSize)
        {
            var n = ConfigNode.Load(Path.Combine(Folder, Safe(name) + ".section.cfg"))?.GetNode("ELE_SECTION");
            if (n == null) return false;
            var src = new List<Vector2>(); var sh = new List<bool>();
            foreach (var t in (n.GetValue("points") ?? "").Split(';'))
            {
                var q = t.Split(','); if (q.Length < 2) continue;
                src.Add(new Vector2(float.Parse(q[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(q[1], System.Globalization.CultureInfo.InvariantCulture)));
                sh.Add(q.Length > 2 && q[2] == "1");
            }
            if (src.Count < 3) return false;
            float ow = s.pts.Max(p => p.x) - s.pts.Min(p => p.x), oh = s.pts.Max(p => p.y) - s.pts.Min(p => p.y);
            int count = s.pts.Count;
            var res = Resample(src, sh, count, out var rsh);
            if (keepSize)
            {
                float w = res.Max(p => p.x) - res.Min(p => p.x), h = res.Max(p => p.y) - res.Min(p => p.y);
                for (int i = 0; i < res.Count; i++) res[i] = new Vector2(res[i].x * (w > 0.01f ? ow / w : 1), res[i].y * (h > 0.01f ? oh / h : 1));
            }
            s.pts = res; s.sharp = rsh;
            return true;
        }

        /// <summary>Resample a closed outline to n points evenly around its length (sharp corners snap to the nearest new point).</summary>
        static List<Vector2> Resample(List<Vector2> src, List<bool> sh, int n, out List<bool> outSharp)
        {
            if (src.Count == n) { outSharp = new List<bool>(sh); return new List<Vector2>(src); }
            int m = src.Count; var cum = new float[m + 1];
            for (int i = 0; i < m; i++) cum[i + 1] = cum[i] + (src[(i + 1) % m] - src[i]).magnitude;
            float L = cum[m]; var res = new List<Vector2>(); outSharp = Enumerable.Repeat(false, n).ToList();
            for (int k = 0; k < n; k++)
            {
                float t = L * k / n; int i = 0; while (i < m - 1 && cum[i + 1] < t) i++;
                float u = (t - cum[i]) / Mathf.Max(1e-5f, cum[i + 1] - cum[i]);
                res.Add(Vector2.Lerp(src[i], src[(i + 1) % m], u));
            }
            for (int i = 0; i < m; i++) if (sh[i]) { int k = Mathf.RoundToInt(cum[i] / L * n) % n; res[k] = src[i]; outSharp[k] = true; }
            return res;
        }
    }
}
