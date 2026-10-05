using System.Collections.Generic;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Stock look for generated meshes: regions of Squad's jet engine texture sheet (white panelled skin, dark panels, metal),
    /// tiled per quad at a fixed real-world size so panels look like stock panels on any shape.
    /// </summary>
    public static class StockSkin
    {
        public static readonly Rect Skin = new(0.195f, 0.004f, 0.43f, 0.32f);
        public static readonly Rect Dark = new(0.122f, 0.6f, 0.258f, 0.18f);
        public static readonly Rect Metal = new(0.77f, 0.595f, 0.216f, 0.155f);
        public static readonly Rect Nozzle = new(0.63f, 0.595f, 0.137f, 0.155f);
        static Texture atlas; static bool tried;
        static readonly Dictionary<string, Material> mats = new();

        public static Texture Atlas
        {
            get { if (!tried) { tried = true; atlas = GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture("Squad/Parts/Engine/jetEngines/Jet Engines", false) : null; } return atlas; }
        }

        public static Rect RectFor(string role) => role == "dark" ? Dark : role == "metal" ? Metal : role == "nozzle" ? Nozzle : Skin;

        /// <summary>Material for a role: skin / dark / metal / nozzle (atlas), glass (dark tinted, glossy), accent (painted colour).</summary>
        public static Material Mat(string role, Color tint)
        {
            string key = role + "|" + ColorUtility.ToHtmlStringRGB(tint);
            if (mats.TryGetValue(key, out var m) && m != null) return m;
            var spec = Shader.Find("KSP/Specular");
            if (role == "glass")
            {
                m = new Material(Shader.Find("KSP/Alpha/Translucent Specular") ?? spec) { color = new Color(0.12f, 0.18f, 0.24f, 0.65f) };
                m.SetFloat("_Shininess", 0.95f); m.SetColor("_SpecColor", new Color(0.9f, 0.9f, 0.9f));
            }
            else if (role == "accent")
            {
                m = new Material(spec) { color = tint }; m.SetFloat("_Shininess", 0.3f);
            }
            else
            {
                m = new Material(spec) { mainTexture = Atlas, color = tint };
                m.SetFloat("_Shininess", role == "metal" ? 0.7f : 0.3f); m.SetColor("_SpecColor", new Color(0.4f, 0.4f, 0.4f));
            }
            mats[key] = m;
            return m;
        }
    }

    /// <summary>
    /// Builds smooth parts from cross-sections in the part config, skinned with the stock texture sheet. Replaces the part's
    /// boxy model (renderers and colliders hidden) with lofted surfaces:
    ///   LOFT { name, role = skin|dark|metal|glass|accent, color, axis = X|Y|Z, segments, capStart, capEnd, collider, parent
    ///          SECTION { pos = x,y,z  w = width  h = height  n = squareness (2 = ellipse, 4+ = rounded box)  [tw = top width] } ... }
    /// Sections run along the loft axis; w/h span the other two axes (Y-axis loft: w = X, h = Z; X-axis: w = Y, h = Z; Z-axis: w = X, h = Y).
    /// Built on the part prefab at load too, so the parts-list icon shows the real shape.
    /// </summary>
    public class ModuleLoftShape : PartModule
    {
        [KSPField] public bool hideOriginal = true;
        [KSPField] public string keepRenderers = "";     // comma list of transform names whose renderers stay visible

        const string Prefix = "loft_";

        public override void OnLoad(ConfigNode node)
        {
            if (HighLogic.LoadedScene == GameScenes.LOADING && node.HasNode("LOFT")) Build(node);
        }

        public override void OnStart(StartState state)
        {
            ConfigNode cfg = null;
            if (part.partInfo?.partConfig != null)
                foreach (var m in part.partInfo.partConfig.GetNodes("MODULE")) if (m.GetValue("name") == nameof(ModuleLoftShape)) { cfg = m; break; }
            if (cfg != null) Build(cfg);
        }

        void Build(ConfigNode cfg)
        {
            var model = part.transform.Find("model") ?? part.transform;
            // already built (cloned from the prefab)? keep it, just make sure the originals are hidden
            bool have = false;
            foreach (Transform t in model.GetComponentsInChildren<Transform>(true)) if (t.name.StartsWith(Prefix)) { have = true; break; }
            if (hideOriginal) HideOriginal(model);
            if (have) return;
            foreach (var lofts in cfg.GetNodes("LOFT"))
            {
                try { LoftBuilder.Build(lofts, model, part.gameObject.layer, part.FindModelTransform); }
                catch (System.Exception e) { Debug.LogWarning($"[ELE Loft] {part.name} {lofts.GetValue("name")}: {e.Message}"); }
            }
        }

        void HideOriginal(Transform model)
        {
            var keep = new HashSet<string>(keepRenderers.Split(new[] { ',', ' ' }, System.StringSplitOptions.RemoveEmptyEntries));
            foreach (var r in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.name.StartsWith(Prefix) || Kept(r.transform, keep)) continue;
                r.enabled = false;
            }
            foreach (var c in model.GetComponentsInChildren<Collider>(true))
            {
                if (c.name.StartsWith(Prefix) || Kept(c.transform, keep)) continue;
                if (HighLogic.LoadedScene == GameScenes.LOADING) c.enabled = false; else Destroy(c);
            }
        }

        static bool Kept(Transform t, HashSet<string> keep)
        {
            for (var x = t; x != null; x = x.parent) if (keep.Contains(x.name)) return true;
            return false;
        }
    }

    /// <summary>Lofted surfaces from LOFT/SECTION config nodes (shared by part exteriors and cockpit interiors).</summary>
    public static class LoftBuilder
    {
        const string Prefix = "loft_";
        static Vector3 Axis(string a, int i)
        {
            // basis per loft axis: (along, w, h)
            switch (a)
            {
                case "X": return i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;
                case "Z": return i == 0 ? Vector3.forward : i == 1 ? Vector3.right : Vector3.up;
                default: return i == 0 ? Vector3.up : i == 1 ? Vector3.right : Vector3.forward;
            }
        }

        public static GameObject Build(ConfigNode n, Transform model, int layer, System.Func<string, Transform> find)
        {
            string name = n.GetValue("name") ?? "shape", role = n.GetValue("role") ?? "skin", axis = (n.GetValue("axis") ?? "Y").ToUpper();
            int seg = int.TryParse(n.GetValue("segments"), out var sg) ? Mathf.Clamp(sg, 6, 64) : 24;
            bool capS = n.GetValue("capStart") != "false", capE = n.GetValue("capEnd") != "false", coll = n.GetValue("collider") == "true";
            Color tint = Color.white; if (n.HasValue("color")) tint = ConfigNode.ParseColor(n.GetValue("color"));
            Transform parent = model;
            if (n.HasValue("parent")) { var p = find(n.GetValue("parent")); if (p != null) parent = p; }
            bool inside = n.GetValue("inside") == "true";                     // interior surfaces (cockpit tubs): faces point in
            float arc0 = n.HasValue("arc0") ? float.Parse(n.GetValue("arc0"), System.Globalization.CultureInfo.InvariantCulture) : 0f;
            float arc1 = n.HasValue("arc1") ? float.Parse(n.GetValue("arc1"), System.Globalization.CultureInfo.InvariantCulture) : 360f;
            bool open = arc1 - arc0 < 359.9f; if (open) capS = capE = false;

            var secs = new List<(Vector3 pos, float w, float h, float sq, float tw)>();
            foreach (var s in n.GetNodes("SECTION"))
            {
                var pos = ConfigNode.ParseVector3(s.GetValue("pos") ?? "0,0,0");
                float w = float.Parse(s.GetValue("w") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
                float h = float.Parse(s.GetValue("h") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
                float sq = float.Parse(s.GetValue("n") ?? "2", System.Globalization.CultureInfo.InvariantCulture);
                float tw = s.HasValue("tw") ? float.Parse(s.GetValue("tw"), System.Globalization.CultureInfo.InvariantCulture) : w;
                secs.Add((pos, w, h, sq, tw));
            }
            if (secs.Count < 2) return null;
            Vector3 aw = Axis(axis, 1), ah = Axis(axis, 2);

            // ring points (superellipse; top/bottom width can differ for tapered tops)
            var rings = new List<Vector3[]>(); var normals2 = new List<Vector2[]>();
            foreach (var (pos, w, h, sq, tw) in secs)
            {
                var ring = new Vector3[seg + 1];
                for (int i = 0; i <= seg; i++)
                {
                    float a = (arc0 + (arc1 - arc0) * i / seg) * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
                    float e = 2f / Mathf.Max(1.2f, sq);
                    float x = Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), e), y = Mathf.Sign(s) * Mathf.Pow(Mathf.Abs(s), e);
                    float halfW = Mathf.Lerp(w, tw, Mathf.Clamp01(-y)) / 2;              // -y side = "top" for h along -Z
                    ring[i] = pos + aw * (x * halfW) + ah * (y * h / 2);
                }
                rings.Add(ring);
            }

            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            bool atlas = role != "glass" && role != "accent";
            var rect = StockSkin.RectFor(role);
            float tile = role == "skin" ? 1.4f : 0.7f, tileH = tile * rect.height / rect.width;
            // cumulative length along the loft (centre line) and around (per ring, for tiling)
            var along = new float[rings.Count];
            for (int r = 1; r < rings.Count; r++) along[r] = along[r - 1] + (secs[r].pos - secs[r - 1].pos).magnitude;
            for (int r = 0; r < rings.Count - 1; r++)
            {
                var A = rings[r]; var B = rings[r + 1];
                float aroundLen = 0; var arc = new float[seg + 1];
                for (int i = 1; i <= seg; i++) { aroundLen += (A[i] - A[i - 1]).magnitude; arc[i] = aroundLen; }
                float va = along[r] / tileH, vb = along[r + 1] / tileH, vf = Mathf.Floor(va);
                for (int i = 0; i < seg; i++)
                {
                    int k = v.Count;
                    v.Add(A[i]); v.Add(A[i + 1]); v.Add(B[i]); v.Add(B[i + 1]);
                    if (atlas)
                    {
                        float ua = arc[i] / tile, ub = arc[i + 1] / tile, uf = Mathf.Floor(ua);
                        float u0 = Mathf.Clamp01(ua - uf), u1 = Mathf.Clamp01(ub - uf), v0 = Mathf.Clamp01(va - vf), v1 = Mathf.Clamp01(vb - vf);
                        uv.Add(new Vector2(rect.x + u0 * rect.width, rect.y + v0 * rect.height)); uv.Add(new Vector2(rect.x + u1 * rect.width, rect.y + v0 * rect.height));
                        uv.Add(new Vector2(rect.x + u0 * rect.width, rect.y + v1 * rect.height)); uv.Add(new Vector2(rect.x + u1 * rect.width, rect.y + v1 * rect.height));
                    }
                    else { uv.Add(Vector2.zero); uv.Add(Vector2.right); uv.Add(Vector2.up); uv.Add(Vector2.one); }
                    tri.AddRange(new[] { k, k + 2, k + 1, k + 1, k + 2, k + 3 });
                }
            }
            // end caps (fans)
            void Cap(Vector3[] ring, Vector3 centre, bool flip)
            {
                int c = v.Count; v.Add(centre); uv.Add(new Vector2(rect.x + rect.width / 2, rect.y + rect.height / 2));
                for (int i = 0; i <= seg; i++) { v.Add(ring[i]); uv.Add(new Vector2(rect.x + rect.width / 2, rect.y + rect.height / 2)); }
                for (int i = 0; i < seg; i++) { if (flip) tri.AddRange(new[] { c, c + 1 + i, c + 2 + i }); else tri.AddRange(new[] { c, c + 2 + i, c + 1 + i }); }
            }
            if (capS) Cap(rings[0], secs[0].pos, true);
            if (capE) Cap(rings[rings.Count - 1], secs[secs.Count - 1].pos, false);

            // Make sure triangles face outward: test the middle quad against the loft centre line
            var mesh = new Mesh { name = Prefix + name };
            mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(tri, 0);
            mesh.RecalculateNormals();
            {
                int mid = (rings.Count - 1) / 2 * seg * 4 + seg / 4 * 4;
                var nrm = mesh.normals[mid]; var ctr = (secs[(rings.Count - 1) / 2].pos + secs[(rings.Count - 1) / 2 + 1].pos) / 2;
                if ((Vector3.Dot(nrm, v[mid] - ctr) < 0) != inside) { for (int i = 0; i < tri.Count; i += 3) (tri[i + 1], tri[i + 2]) = (tri[i + 2], tri[i + 1]); mesh.SetTriangles(tri, 0); }
            }
            SmoothNormals(mesh);
            mesh.RecalculateBounds(); mesh.RecalculateTangents();

            var go = new GameObject(Prefix + name); go.layer = layer;
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = StockSkin.Mat(role, tint);
            if (coll) { var mc = go.AddComponent<MeshCollider>(); mc.convex = true; mc.sharedMesh = mesh; }
            return go;
        }


        static void SmoothNormals(Mesh m)
        {
            var v = m.vertices; var n = m.normals;
            var map = new Dictionary<Vector3Int, Vector3>();
            Vector3Int K(Vector3 p) => new(Mathf.RoundToInt(p.x * 1000), Mathf.RoundToInt(p.y * 1000), Mathf.RoundToInt(p.z * 1000));
            for (int i = 0; i < v.Length; i++) { var k = K(v[i]); map[k] = (map.TryGetValue(k, out var s) ? s : Vector3.zero) + n[i]; }
            for (int i = 0; i < v.Length; i++) { var s = map[K(v[i])]; if (Vector3.Angle(s, n[i]) < 60f) n[i] = s.normalized; }
            m.normals = n;
        }
    }

    /// <summary>
    /// Cockpit interior built from LOFT nodes in the INTERNAL config (tub, consoles, seats), on the IVA layer.
    /// Built on the internal prefab at load, so every cockpit instance just clones it.
    /// </summary>
    public class InternalLoftShell : InternalModule
    {
        public override void OnLoad(ConfigNode node)
        {
            if (!node.HasNode("LOFT")) return;
            var root = internalModel != null ? internalModel.transform : transform;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) if (t.name.StartsWith("loft_")) return;
            foreach (var l in node.GetNodes("LOFT"))
            {
                try { LoftBuilder.Build(l, root, 16, n => FindIn(root, n)); }
                catch (System.Exception e) { Debug.LogWarning($"[ELE IVA] {l.GetValue("name")}: {e.Message}"); }
            }
        }

        static Transform FindIn(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            return null;
        }
    }
}
