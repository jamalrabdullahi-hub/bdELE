using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Copy hull lines from existing parts: slice a part's mesh along its length and turn each slice into an ELE Shape
    /// section (outline by rays from the slice centre, left/right averaged). Ship classes chain stern + hull + bow parts of
    /// Large Boat Parts Pack Modern (CC BY-SA 4.0, Laythe and SpannerMonkey) when it's installed.
    /// </summary>
    public static class HullLines
    {
        public static readonly (string label, string[] parts, int[] slices)[] Classes =
        {
            ("LBP Corvette", new[] { "smallstern", "smallhull", "smallbow" }, new[] { 3, 2, 4 }),
            ("LBP Destroyer (medium)", new[] { "mediumstern", "mediumhull", "mediumbow" }, new[] { 3, 3, 4 }),
            ("LBP Carrier", new[] { "carriermhull", "carriermbow" }, new[] { 4, 5 }),
            ("LBP Trimaran", new[] { "trihull" }, new[] { 7 }),
            ("LBP Submarine", new[] { "subhullstern", "subhull", "subbow" }, new[] { 3, 2, 3 }),
        };

        public static bool Available(int cls) => Classes[cls].parts.All(p => PartLoader.getPartInfoByName(p) != null);

        /// <summary>Mesh points of a part prefab in ELE Shape coordinates: x right, y along (part Y), z = up (part -Z).</summary>
        static List<Vector3> Points(AvailablePart ap)
        {
            var root = ap.partPrefab.transform; var pts = new List<Vector3>();
            foreach (var mf in ap.partPrefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || !mf.sharedMesh.isReadable || mf.GetComponent<Renderer>() == null) continue;
                var m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices) { var l = m.MultiplyPoint3x4(v); pts.Add(new Vector3(l.x, l.y, -l.z)); }
            }
            return pts;
        }

        /// <summary>Slice one part into sections (stern first), shifted yOffset along the chain.</summary>
        public static List<ModuleELEShape.Section> Slice(string partName, int slices, int n, float yOffset, out float length)
        {
            length = 0;
            var ap = PartLoader.getPartInfoByName(partName);
            if (ap?.partPrefab == null) return null;
            var pts = Points(ap); if (pts.Count < 20) return null;
            float y0 = pts.Min(p => p.y), y1 = pts.Max(p => p.y); length = y1 - y0;
            // upside down? the deck is the wide side
            float uMin = pts.Min(p => p.z), uMax = pts.Max(p => p.z), band = (uMax - uMin) * 0.12f;
            float wTop = Width(pts.Where(p => p.z > uMax - band)), wBot = Width(pts.Where(p => p.z < uMin + band));
            if (wBot > wTop * 1.3f) for (int i = 0; i < pts.Count; i++) pts[i] = new Vector3(pts[i].x, pts[i].y, -pts[i].z);
            var secs = new List<ModuleELEShape.Section>();
            float step = length / slices;
            for (int s = 0; s <= slices; s++)
            {
                float yc = y0 + Mathf.Clamp(s * step, length * 0.015f, length * 0.985f);
                float half = Mathf.Max(0.15f, step * 0.35f);
                var b = pts.Where(p => Mathf.Abs(p.y - yc) < half).Select(p => new Vector2(p.x, p.z)).ToList();
                if (b.Count < 6) continue;
                var sec = new ModuleELEShape.Section { y = yc - y0 + yOffset };
                Outline(sec, b, n);
                secs.Add(sec);
            }
            return secs;
        }

        static float Width(IEnumerable<Vector3> p) { var l = p.ToList(); return l.Count == 0 ? 0 : l.Max(q => q.x) - l.Min(q => q.x); }

        /// <summary>Outline of a slice: farthest point along n rays from the centre (top first, clockwise), left/right averaged.</summary>
        static void Outline(ModuleELEShape.Section s, List<Vector2> b, int n)
        {
            var c = new Vector2(0, (b.Max(p => p.y) + b.Min(p => p.y)) / 2);
            var r = new float[n];
            float wedge = Mathf.Tan(Mathf.PI / n) * 1.2f;
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.PI / 2 - 2 * Mathf.PI * i / n; var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                float best = 0;
                foreach (var p in b) { var q = p - c; float al = Vector2.Dot(q, d); if (al <= 0) continue; if (Mathf.Abs(q.x * d.y - q.y * d.x) < al * wedge + 0.05f && al > best) best = al; }
                r[i] = best;
            }
            for (int i = 0; i < n; i++) if (r[i] <= 0) r[i] = Mathf.Max(r[(i + n - 1) % n], r[(i + 1) % n]);
            for (int i = 1; i < n / 2; i++) { float m = (r[i] + r[n - i]) / 2; r[i] = r[n - i] = m; }
            s.pts.Clear(); s.sharp.Clear();
            for (int i = 0; i < n; i++) { float a = Mathf.PI / 2 - 2 * Mathf.PI * i / n; s.pts.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r[i]); s.sharp.Add(false); }
            for (int i = 0; i < n; i++) { Vector2 a = s.pts[(i + n - 1) % n], p = s.pts[i], q = s.pts[(i + 1) % n]; s.sharp[i] = Vector2.Angle(p - a, q - p) > 40f; }
        }

        /// <summary>A whole ship class as sections, scaled to the given length (0 = its own size).</summary>
        public static List<ModuleELEShape.Section> Build(int cls, int n, float targetLength, out float scale)
        {
            scale = 1;
            var (_, parts, slices) = Classes[cls];
            var all = new List<ModuleELEShape.Section>(); float y = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                var secs = Slice(parts[i], slices[i], n, y, out float len);
                if (secs == null) return null;
                if (all.Count > 0 && secs.Count > 0) secs.RemoveAt(0);   // the joint is already there
                all.AddRange(secs); y += len;
            }
            if (all.Count < 2) return null;
            float total = y;
            scale = targetLength > 0 ? targetLength / total : 1;
            foreach (var s in all)
            {
                s.y = (s.y - total / 2) * scale;
                for (int k = 0; k < s.pts.Count; k++) s.pts[k] *= scale;
            }
            return all;
        }

        /// <summary>Copy any part's shape onto an ELE Shape (k slices along its length).</summary>
        public static bool CopyPart(ModuleELEShape m, string partName, int slices)
        {
            var secs = Slice(partName, slices, Mathf.Max(8, m.sections.Count > 0 ? m.sections[0].pts.Count : 16), 0, out float len);
            if (secs == null || secs.Count < 2) return false;
            foreach (var s in secs) s.y -= len / 2;
            m.sections.Clear(); m.sections.AddRange(secs);
            m.Encode(); m.Rebuild(true); m.Propagate();
            return true;
        }
    }
}
