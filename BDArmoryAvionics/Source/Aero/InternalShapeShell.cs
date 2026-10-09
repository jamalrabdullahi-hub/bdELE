using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// IVA for the ELE Shape cockpit, built from the part's own shape: the cabin walls are the inside of the skin (open where
    /// the canopy glass is, so you see out), a floor, an instrument panel in front of each seat, and the seats with their
    /// instruments moved to where you put them in the shape editor (seat position, height, tandem / side-by-side spacing).
    /// The internal's template model only supplies the seat transforms; its own meshes are hidden.
    /// </summary>
    public class InternalShapeShell : InternalModule
    {
        [KSPField] public float inset = 0.03f;
        bool built; int wait;
        GameObject shell;

        public override void OnUpdate()
        {
            if (built || internalModel == null || part == null) return;
            if (++wait < 3) return;   // let KSP place the internal model first
            built = true;
            try { Build(); } catch (System.Exception e) { Debug.LogWarning($"[ELE Shape IVA] {e}"); }
        }

        void Build()
        {
            var shape = part.FindModuleImplementing<ModuleELEShape>();
            if (shape == null) return;
            var root = internalModel.transform;
            var ptr = part.transform;
            Vector3 ToIva(Vector3 partLocal) => root.InverseTransformPoint(InternalSpace.WorldToInternal(ptr.TransformPoint(partLocal)));

            // hide the template's own meshes (not the props)
            var propRoots = new HashSet<Transform>(internalModel.props.Where(p => p != null).Select(p => p.transform));
            bool InProp(Transform t) { for (var x = t; x != null && x != root; x = x.parent) if (propRoots.Contains(x)) return true; return false; }
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true)) if (!InProp(r.transform)) r.enabled = false;

            // ---- cabin walls: the skin from inside, without the glass ----
            var src = part.FindModelTransform(shape.meshName)?.GetComponent<MeshFilter>()?.sharedMesh;
            if (src == null) return;
            var sv = src.vertices; var tri = src.GetTriangles(0);
            var v = new List<Vector3>(); var t = new List<int>();
            foreach (var p in sv)
            {
                var sec = shape.SectionAt(p.y);
                var centre = sec != null ? new Vector3(sec.offset.x, p.y, -sec.offset.y) : new Vector3(0, p.y, 0);
                v.Add(ToIva(p + (centre - p).normalized * inset));
            }
            for (int i = 0; i < tri.Length; i += 3) { t.Add(tri[i]); t.Add(tri[i + 2]); t.Add(tri[i + 1]); }   // facing in
            shell = Make("ele_shape_cabin", v, t, src.uv, StockSkin.Mat("dark", new Color(0.55f, 0.56f, 0.58f)));

            // ---- seats ----
            var seats = internalModel.seats.Where(s => s?.seatTransform != null).ToList();
            var origSeat = seats.Select(s => s.seatTransform.localPosition).ToList();
            var want = new List<Vector3>();
            float along = shape.seatAlong, up = shape.seatUp, sp = shape.seatSpacing;
            want.Add(new Vector3(sp < -0.05f ? sp / 2 : 0, along, -up));
            if (sp > 0.05f) want.Add(new Vector3(0, along - sp, -up));          // tandem
            else if (sp < -0.05f) want.Add(new Vector3(-sp / 2, along, -up));   // side by side
            for (int i = 0; i < seats.Count; i++)
            {
                var target = i < want.Count ? ToIva(want[i]) : origSeat[i];
                var delta = target - origSeat[i];
                // instruments that belonged to this seat move with it
                foreach (var pr in internalModel.props)
                {
                    if (pr == null) continue;
                    var lp = pr.transform.localPosition;
                    int nearest = 0; float best = float.MaxValue;
                    for (int k = 0; k < origSeat.Count; k++) { float d = (lp - origSeat[k]).sqrMagnitude; if (d < best) { best = d; nearest = k; } }
                    if (nearest == i) pr.transform.localPosition = lp + delta;
                }
                seats[i].seatTransform.localPosition = target;
                var cam = string.IsNullOrEmpty(seats[i].portraitCameraName) ? null : root.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == seats[i].portraitCameraName);
                if (cam != null) cam.localPosition += delta;   // crew portrait follows its seat
                if (i >= want.Count) seats[i].taken = true;   // no room for this seat in this shape
            }

            // ---- floor and panels (simple boxes in IVA space, under / in front of each seat) ----
            var mat = StockSkin.Mat("accent", new Color(0.3f, 0.31f, 0.33f));
            var sec0 = shape.SectionAt(along);
            float halfW = sec0 != null ? (sec0.pts.Max(q => q.x) - sec0.pts.Min(q => q.x)) * 0.42f : 0.4f;
            float floorUp = up - 0.45f;
            float back = (sp > 0.05f ? along - sp : along) - 0.6f, front = along + 0.9f;
            Box("ele_shape_floor", ToIva(new Vector3(0, (back + front) / 2, -floorUp)), new Vector3(halfW * 2, 0.04f, front - back), mat);
            foreach (var w in want)
            {
                var panelCentre = new Vector3(w.x, w.y + 0.75f, -(up + 0.35f));
                Box("ele_shape_panel", ToIva(panelCentre), new Vector3(Mathf.Min(0.7f, halfW * 2), 0.32f, 0.12f), mat);
            }
            Debug.Log($"[ELE Shape IVA] cabin built for {part.partInfo.title}: {want.Count} seat(s), {t.Count / 3} wall triangles");
        }

        GameObject Make(string name, List<Vector3> v, List<int> t, Vector2[] uv, Material m)
        {
            var mesh = new Mesh { name = name }; mesh.SetVertices(v); if (uv != null && uv.Length == v.Count) mesh.uv = uv; mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            var go = new GameObject(name) { layer = 16 };
            go.transform.SetParent(internalModel.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = m;
            return go;
        }

        void Box(string name, Vector3 centre, Vector3 size, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(go.GetComponent<Collider>());
            go.name = name; go.layer = 16;
            go.transform.SetParent(internalModel.transform, false);
            go.transform.localPosition = centre;
            // IVA axes: up +Y, nose -Z; boxes are given as (width, height, depth)
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = m;
        }
    }
}
