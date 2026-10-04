using System.Collections.Generic;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Dynamic Frame: a resizable hull / frame / armour block for cars, APCs and tanks (procedural parts for vehicles).
    /// Length runs along the part's Y axis (forward in the SPH), width along X, height along -Z (up in the SPH).
    /// Front, rear and side slopes cut the top face back, so one part makes boxes, wedges, glacis plates and sloped hulls.
    /// BDArmory armour works on it as a procedural part (the part name contains "procedural"): set thickness and
    /// material in the usual BDArmory fields; the frame shows the line-of-sight thickness of its sloped faces.
    /// </summary>
    public class ModuleDynamicFrame : PartModule, IPartMassModifier, IPartCostModifier
    {
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Length", guiUnits = " m"),
         UI_FloatEdit(scene = UI_Scene.Editor, minValue = 0.1f, maxValue = 16f, incrementLarge = 1f, incrementSmall = 0.1f, incrementSlide = 0.01f, sigFigs = 2)]
        public float length = 3f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Width", guiUnits = " m"),
         UI_FloatEdit(scene = UI_Scene.Editor, minValue = 0.1f, maxValue = 8f, incrementLarge = 1f, incrementSmall = 0.1f, incrementSlide = 0.01f, sigFigs = 2)]
        public float width = 2f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Height", guiUnits = " m"),
         UI_FloatEdit(scene = UI_Scene.Editor, minValue = 0.02f, maxValue = 4f, incrementLarge = 0.5f, incrementSmall = 0.05f, incrementSlide = 0.01f, sigFigs = 2)]
        public float height = 0.8f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Front slope", guiUnits = "°"),
         UI_FloatRange(scene = UI_Scene.Editor, minValue = 0f, maxValue = 80f, stepIncrement = 1f)]
        public float frontSlope = 0f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Rear slope", guiUnits = "°"),
         UI_FloatRange(scene = UI_Scene.Editor, minValue = 0f, maxValue = 80f, stepIncrement = 1f)]
        public float rearSlope = 0f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Side slope", guiUnits = "°"),
         UI_FloatRange(scene = UI_Scene.Editor, minValue = 0f, maxValue = 70f, stepIncrement = 1f)]
        public float sideSlope = 0f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Colour"),
         UI_ChooseOption(scene = UI_Scene.Editor, options = new[] { "Olive", "Sand", "Grey", "Dark", "Green", "White" })]
        public string colour = "Olive";

        [KSPField(guiActiveEditor = true, guiName = "LOS armour")] public string losInfo = "";
        [KSPField(guiActiveEditor = true, guiName = "Frame")] public string sizeInfo = "";

        [KSPField] public string meshName = "frameMesh";
        [KSPField] public float skinDensity = 0.01f;   // structure t per m² of surface (armour mass is BDArmory's)
        [KSPField] public float costPerM2 = 30f;

        static readonly Dictionary<string, Color> Colours = new()
        {
            ["Olive"] = new Color(0.33f, 0.36f, 0.22f), ["Sand"] = new Color(0.72f, 0.62f, 0.44f), ["Grey"] = new Color(0.45f, 0.47f, 0.47f),
            ["Dark"] = new Color(0.16f, 0.17f, 0.17f), ["Green"] = new Color(0.20f, 0.30f, 0.20f), ["White"] = new Color(0.85f, 0.86f, 0.84f),
        };

        Transform meshT; Mesh mesh; float area;
        readonly Dictionary<string, Vector3> nodePos = new();

        public override void OnStart(StartState state)
        {
            meshT = part.FindModelTransform(meshName);
            foreach (var f in new[] { nameof(length), nameof(width), nameof(height), nameof(frontSlope), nameof(rearSlope), nameof(sideSlope), nameof(colour) })
            {
                var c = Fields[f].uiControlEditor; if (c != null) c.onFieldChanged = OnChanged;
            }
            Rebuild(false);
        }

        public override void OnLoad(ConfigNode node) { if (meshT != null) Rebuild(false); }

        void OnChanged(BaseField field, object old)
        {
            Rebuild(true);
            foreach (var p in part.symmetryCounterparts)
            {
                var m = p.FindModuleImplementing<ModuleDynamicFrame>(); if (m == null) continue;
                m.length = length; m.width = width; m.height = height; m.frontSlope = frontSlope; m.rearSlope = rearSlope; m.sideSlope = sideSlope; m.colour = colour;
                m.Rebuild(true);
            }
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch?.ship != null) GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
        }

        // Top-face inset from each edge, clamped so the top face never inverts.
        void Insets(out float f, out float r, out float s)
        {
            f = height * Mathf.Tan(frontSlope * Mathf.Deg2Rad); r = height * Mathf.Tan(rearSlope * Mathf.Deg2Rad); s = height * Mathf.Tan(sideSlope * Mathf.Deg2Rad);
            float maxL = length - 0.01f; if (f + r > maxL) { float k = maxL / (f + r); f *= k; r *= k; }
            s = Mathf.Min(s, width / 2 - 0.005f);
        }

        public void Rebuild(bool moveChildren)
        {
            if (meshT == null) meshT = part.FindModelTransform(meshName);
            if (meshT == null) return;
            Insets(out float f, out float r, out float s);
            float L = length / 2, W = width / 2, H = height / 2;
            // bottom (z = +H) and top (z = -H, "up" in the SPH)
            Vector3 b0 = new(-W, -L, H), b1 = new(W, -L, H), b2 = new(W, L, H), b3 = new(-W, L, H);
            Vector3 t0 = new(-W + s, -L + r, -H), t1 = new(W - s, -L + r, -H), t2 = new(W - s, L - f, -H), t3 = new(-W + s, L - f, -H);
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>(); area = 0;
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                var n = Vector3.Cross(b - a, c - a);
                if (Vector3.Dot(n, (a + b + c + d) / 4) < 0) { (b, d) = (d, b); n = -n; } // face outward
                area += Vector3.Cross(c - a, d - b).magnitude / 2;
                var u = (b - a).normalized; var w = Vector3.Cross(n.normalized, u);
                int i = v.Count;
                foreach (var p in new[] { a, b, c, d }) { v.Add(p); uv.Add(new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, w))); }
                tri.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
            Quad(b0, b1, b2, b3); Quad(t0, t1, t2, t3);            // bottom, top
            Quad(b3, b2, t2, t3); Quad(b0, b1, t1, t0);            // front, rear
            Quad(b1, b2, t2, t1); Quad(b0, b3, t3, t0);            // right, left

            if (mesh == null) { mesh = new Mesh { name = "DynamicFrame" }; }
            mesh.Clear(); mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(tri, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); mesh.RecalculateTangents();
            meshT.localScale = Vector3.one; meshT.localPosition = Vector3.zero; meshT.localRotation = Quaternion.identity;
            var mf = meshT.GetComponent<MeshFilter>(); if (mf != null) mf.sharedMesh = mesh;
            foreach (var bc in meshT.GetComponents<BoxCollider>()) Destroy(bc);
            var mc = meshT.GetComponent<MeshCollider>() ?? meshT.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = null; mc.convex = true; mc.sharedMesh = mesh;
            var mr = meshT.GetComponent<MeshRenderer>();
            if (mr != null && Colours.TryGetValue(colour, out var col)) mr.material.SetColor("_Color", col);

            // Attach nodes on the face centres
            SetNode("top", new Vector3(0, (r - f) / 2, -H), Vector3.back, moveChildren);
            SetNode("bottom", new Vector3(0, 0, H), Vector3.forward, moveChildren);
            SetNode("front", new Vector3(0, L - f / 2, 0), Vector3.up, moveChildren);
            SetNode("back", new Vector3(0, -L + r / 2, 0), Vector3.down, moveChildren);
            SetNode("right", new Vector3(W - s / 2, 0, 0), Vector3.right, moveChildren);
            SetNode("left", new Vector3(-W + s / 2, 0, 0), Vector3.left, moveChildren);

            if (part.DragCubes != null) { part.DragCubes.Procedural = true; if (HighLogic.LoadedSceneIsFlight) part.DragCubes.ForceUpdate(true, true, true); }
            UpdateInfo();
        }

        void SetNode(string id, Vector3 pos, Vector3 dir, bool moveChildren)
        {
            var n = part.FindAttachNode(id); if (n == null) return;
            var old = n.position;
            n.position = n.originalPosition = pos; n.orientation = n.originalOrientation = dir;
            if (!moveChildren || n.attachedPart == null || n.attachedPart.parent != part) return;
            var child = n.attachedPart;
            child.transform.position += part.transform.TransformVector(pos - old); // the child stays on the face
            child.attPos0 += part.transform.TransformVector(pos - old);
        }

        void UpdateInfo()
        {
            sizeInfo = $"{length:F2} x {width:F2} x {height:F2} m, {area:F1} m², {area * skinDensity:F2} t";
            var hp = part.Modules.GetModule("HitpointTracker");
            float t = hp != null ? (float)(hp.Fields.GetValue("Armor") ?? 0f) : 0f;
            if (t <= 0) { losInfo = "set BDArmory armour"; return; }
            float Los(float deg) => t / Mathf.Max(0.1f, Mathf.Cos(deg * Mathf.Deg2Rad)); // slope measured from vertical
            losInfo = $"front {Los(frontSlope):F0} / side {Los(sideSlope):F0} / rear {Los(rearSlope):F0} / top {t:F0} mm";
        }

        void Update() { if (HighLogic.LoadedSceneIsEditor && Time.frameCount % 30 == 0) UpdateInfo(); }

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) => Mathf.Max(0.005f, area * skinDensity) - defaultMass;
        public ModifierChangeWhen GetModuleMassChangeWhen() => ModifierChangeWhen.FIXED;
        public float GetModuleCost(float defaultCost, ModifierStagingSituation sit) => area * costPerM2;
        public ModifierChangeWhen GetModuleCostChangeWhen() => ModifierChangeWhen.FIXED;

        void OnDestroy() { if (mesh != null) Destroy(mesh); }
    }
}
