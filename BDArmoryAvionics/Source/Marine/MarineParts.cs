using System.Collections.Generic;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Rudder / skeg / hydrofoil plate you size in the editor. Works in water: lift from its angle to the flow, turned by the
    /// yaw input (it also steadies the boat as a skeg at zero). Part axes: span hangs down (part -Z... built along the
    /// part's down = +Z in the SPH), chord runs aft from the pivot (-Y), it turns about the span axis.
    /// </summary>
    public class ModuleELERudder : PartModule
    {
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Span", guiUnits = " m", groupName = "elerudder", groupDisplayName = "ELE Rudder"),
         UI_FloatRange(minValue = 0.2f, maxValue = 8f, stepIncrement = 0.05f, scene = UI_Scene.Editor)]
        public float span = 1.2f;
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Chord", guiUnits = " m", groupName = "elerudder"),
         UI_FloatRange(minValue = 0.2f, maxValue = 6f, stepIncrement = 0.05f, scene = UI_Scene.Editor)]
        public float chord = 0.8f;
        [KSPField(isPersistant = true, guiActiveEditor = true, guiActive = true, guiName = "Max angle", guiUnits = "°", groupName = "elerudder"),
         UI_FloatRange(minValue = 0f, maxValue = 40f, stepIncrement = 1f, scene = UI_Scene.All)]
        public float maxAngle = 30f;
        [KSPField(isPersistant = true, guiActiveEditor = true, guiActive = true, guiName = "Steers with", groupName = "elerudder"),
         UI_ChooseOption(options = new[] { "Yaw", "Pitch", "Roll", "None" }, scene = UI_Scene.All)]
        public string axisInput = "Yaw";
        [KSPField(isPersistant = true, guiActiveEditor = true, guiActive = true, guiName = "Invert", groupName = "elerudder"), UI_Toggle(scene = UI_Scene.All)]
        public bool invert = false;
        [KSPField] public string meshName = "frameMesh";

        Transform meshT, blade; Mesh mesh; float angle;

        public override void OnStart(StartState state)
        {
            meshT = part.FindModelTransform(meshName);
            foreach (var f in new[] { nameof(span), nameof(chord) }) { var c = Fields[f].uiControlEditor; if (c != null) c.onFieldChanged = (a, b) => Build(); }
            Build();
        }

        void Build()
        {
            if (meshT == null) return;
            if (blade == null)
            {
                var go = new GameObject("rudderBlade"); go.layer = part.gameObject.layer;
                blade = go.transform; blade.SetParent(meshT.parent, false);
                go.AddComponent<MeshFilter>(); go.AddComponent<MeshRenderer>().sharedMaterial = StockSkin.Mat("dark", Color.white);
                var mr = meshT.GetComponent<MeshRenderer>(); if (mr != null) mr.enabled = false;
            }
            // thin symmetric foil: span along +Z (down in the SPH), chord along -Y from the pivot
            float t = chord * 0.1f;
            var v = new List<Vector3>(); var tri = new List<int>();
            int n = 8;
            var prof = new List<Vector2>();
            for (int i = 0; i <= n; i++) { float x = (float)i / n; prof.Add(new Vector2(-x * chord, 0.5f * t * 5 * (0.2969f * Mathf.Sqrt(x) - 0.126f * x - 0.3516f * x * x + 0.2843f * x * x * x - 0.1015f * x * x * x * x))); }
            var ring = new List<Vector2>(prof); for (int i = n - 1; i >= 1; i--) ring.Add(new Vector2(prof[i].x, -prof[i].y));
            int m = ring.Count;
            foreach (var z in new[] { 0f, span }) foreach (var p in ring) v.Add(new Vector3(p.y, p.x + chord * 0.25f, z));
            for (int i = 0; i < m; i++) { int a = i, b = (i + 1) % m; tri.AddRange(new[] { a, m + a, b, b, m + a, m + b }); }
            for (int i = 1; i < m - 1; i++) { tri.AddRange(new[] { 0, i + 1, i }); tri.AddRange(new[] { m, m + i, m + i + 1 }); }
            if (mesh == null) mesh = new Mesh { name = "rudder" };
            mesh.Clear(); mesh.SetVertices(v); mesh.SetTriangles(tri, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            blade.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mc = meshT.GetComponent<MeshCollider>() ?? meshT.gameObject.AddComponent<MeshCollider>();
            foreach (var bc in meshT.GetComponents<BoxCollider>()) Destroy(bc);
            mc.sharedMesh = null; mc.convex = true; mc.sharedMesh = mesh;
        }

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || part.rb == null || blade == null) return;
            var cs = vessel.ctrlState;
            float input = axisInput == "Yaw" ? cs.yaw : axisInput == "Pitch" ? cs.pitch : axisInput == "Roll" ? cs.roll : 0;
            if (invert) input = -input;
            angle = Mathf.MoveTowards(angle, input * maxAngle, 60f * Time.fixedDeltaTime);
            blade.localRotation = Quaternion.AngleAxis(angle, Vector3.forward);

            // submerged part of the span
            var body = vessel.mainBody; if (!body.ocean) return;
            Vector3 top = blade.TransformPoint(new Vector3(0, 0, 0)), bot = blade.TransformPoint(new Vector3(0, 0, span));
            float dTop = (float)(body.Radius - ((Vector3d)top - body.position).magnitude), dBot = (float)(body.Radius - ((Vector3d)bot - body.position).magnitude);
            float wet = dTop > 0 && dBot > 0 ? 1 : dTop <= 0 && dBot <= 0 ? 0 : Mathf.Clamp01(Mathf.Max(dTop, dBot) / Mathf.Abs(dTop - dBot));
            if (wet <= 0) return;
            Vector3 c = blade.TransformPoint(new Vector3(0, 0, span * (dBot > dTop ? 1 - wet * 0.5f : wet * 0.5f)));
            Vector3 flow = -(part.rb.GetPointVelocity(c) + ((Vector3)vessel.srf_velocity - vessel.rb_velocity));   // water past the blade
            Vector3 spanDir = blade.forward, chordDir = -blade.up;   // chord points aft
            Vector3 f = Vector3.ProjectOnPlane(flow, spanDir);
            float sp = f.magnitude; if (sp < 0.2f) return;
            Vector3 fd = f / sp;
            float alpha = Vector3.SignedAngle(chordDir, fd, spanDir) * Mathf.Deg2Rad;
            float cl = Mathf.Clamp(2 * Mathf.PI * Mathf.Sin(alpha), -1.2f, 1.2f) * (Mathf.Abs(alpha) > 0.35f ? 0.6f : 1f);   // stalls past ~20°
            float area = span * wet * chord, ar = span / chord;
            float q = 0.5f * (float)body.oceanDensity * sp * sp;
            Vector3 liftDir = Vector3.Cross(spanDir, fd).normalized;
            Vector3 F = liftDir * q * area * cl + fd * q * area * (0.012f + cl * cl / (Mathf.PI * Mathf.Max(0.5f, ar)));
            part.rb.AddForceAtPosition(F, c, ForceMode.Force);
        }
    }

    /// <summary>
    /// Boat drive: propeller (or waterjet) of a set diameter and power, driven by the throttle, burning liquid fuel, pushing
    /// only while it's under water. Thrust from momentum theory (more diameter = more grip), falling off with speed.
    /// The waterjet also steers with yaw (vectoring). Pushes along the part's forward (the SPH nose direction).
    /// </summary>
    public class ModuleELEMarineDrive : PartModule
    {
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Type", groupName = "eledrive", groupDisplayName = "ELE Marine drive"),
         UI_ChooseOption(options = new[] { "Propeller", "Waterjet" }, scene = UI_Scene.Editor)]
        public string driveType = "Propeller";
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Diameter", guiUnits = " m", groupName = "eledrive"),
         UI_FloatRange(minValue = 0.2f, maxValue = 6f, stepIncrement = 0.05f, scene = UI_Scene.Editor)]
        public float diameter = 0.8f;
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Power", guiUnits = " kW", groupName = "eledrive"),
         UI_FloatRange(minValue = 20f, maxValue = 40000f, stepIncrement = 20f, scene = UI_Scene.Editor)]
        public float powerKW = 600f;
        [KSPField(isPersistant = true, guiActive = true, guiName = "Reverse", groupName = "eledrive"), UI_Toggle(scene = UI_Scene.Flight)]
        public bool reverse = false;
        [KSPField(guiActive = true, guiName = "Thrust", guiUnits = " kN", guiFormat = "F1", groupName = "eledrive")]
        public float thrustNow;
        [KSPField] public string meshName = "frameMesh";
        [KSPField] public float sfc = 0.25f;   // kg of fuel per kWh

        Transform meshT, rotor; Mesh mesh; float rpmAngle; PartResourceDefinition lf;

        [KSPAction("Toggle reverse")] public void ActReverse(KSPActionParam p) => reverse = !reverse;

        public override void OnStart(StartState state)
        {
            meshT = part.FindModelTransform(meshName);
            lf = PartResourceLibrary.Instance.GetDefinition("LiquidFuel");
            foreach (var f in new[] { nameof(diameter), nameof(driveType) }) { var c = Fields[f].uiControlEditor; if (c != null) c.onFieldChanged = (a, b) => Build(); }
            Build();
        }

        void Build()
        {
            if (meshT == null) return;
            if (rotor == null)
            {
                var go = new GameObject("driveRotor"); go.layer = part.gameObject.layer;
                rotor = go.transform; rotor.SetParent(meshT.parent, false);
                go.AddComponent<MeshFilter>(); go.AddComponent<MeshRenderer>().sharedMaterial = StockSkin.Mat("metal", new Color(0.85f, 0.75f, 0.5f));
                var mr = meshT.GetComponent<MeshRenderer>(); if (mr != null) mr.enabled = false;
            }
            float r = diameter / 2;
            var v = new List<Vector3>(); var tri = new List<int>();
            void Box(Vector3 c, Vector3 a, Vector3 b, Vector3 d)
            {
                int k = v.Count;
                foreach (var sx in new[] { -1, 1 }) foreach (var sy in new[] { -1, 1 }) foreach (var sz in new[] { -1, 1 }) v.Add(c + a * sx + b * sy + d * sz);
                int[] f = { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
                foreach (var i in f) tri.Add(k + i);
            }
            if (driveType == "Waterjet")
            {
                // a short nozzle duct (8 sided)
                int s = 8;
                for (int i = 0; i < s; i++)
                {
                    float a0 = i * Mathf.PI * 2 / s, a1 = (i + 1) * Mathf.PI * 2 / s;
                    int k = v.Count;
                    v.Add(new Vector3(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r)); v.Add(new Vector3(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r));
                    v.Add(new Vector3(Mathf.Cos(a0) * r * 0.7f, -diameter, Mathf.Sin(a0) * r * 0.7f)); v.Add(new Vector3(Mathf.Cos(a1) * r * 0.7f, -diameter, Mathf.Sin(a1) * r * 0.7f));
                    tri.AddRange(new[] { k, k + 2, k + 1, k + 1, k + 2, k + 3, k, k + 1, k + 2, k + 1, k + 3, k + 2 });
                }
            }
            else
            {
                Box(Vector3.zero, new Vector3(r * 0.18f, 0, 0), new Vector3(0, r * 0.3f, 0), new Vector3(0, 0, r * 0.18f));   // hub
                int blades = diameter > 2f ? 5 : 4;
                for (int i = 0; i < blades; i++)
                {
                    var q = Quaternion.AngleAxis(360f / blades * i, Vector3.up) * Quaternion.AngleAxis(25f, Vector3.right);
                    Box(q * new Vector3(0, 0, r * 0.55f), q * new Vector3(r * 0.22f, 0, 0), q * new Vector3(0, r * 0.03f, 0), q * new Vector3(0, 0, r * 0.42f));
                }
            }
            if (mesh == null) mesh = new Mesh { name = "drive" };
            mesh.Clear(); mesh.SetVertices(v); mesh.SetTriangles(tri, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            rotor.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mc = meshT.GetComponent<MeshCollider>() ?? meshT.gameObject.AddComponent<MeshCollider>();
            foreach (var bc in meshT.GetComponents<BoxCollider>()) Destroy(bc);
            mc.sharedMesh = null; mc.convex = true; mc.sharedMesh = mesh;
        }

        void FixedUpdate()
        {
            thrustNow = 0;
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || part.rb == null || rotor == null) return;
            float thr = vessel.ctrlState.mainThrottle;
            var body = vessel.mainBody;
            Vector3 c = rotor.position;
            bool wet = body.ocean && (float)(body.Radius - ((Vector3d)c - body.position).magnitude) > diameter * 0.3f;
            if (thr > 0.001f)
            {
                // fuel for this much power
                double need = powerKW * thr * sfc / 3600.0 * TimeWarp.fixedDeltaTime / 5.0;   // units (5 kg each)
                double got = lf != null ? part.RequestResource(lf.id, need) : need;
                float frac = need > 0 ? (float)(got / need) : 0;
                float P = powerKW * thr * frac;   // kW
                rpmAngle += (wet ? 900f : 1500f) * thr * frac * Time.fixedDeltaTime;
                if (wet && P > 0)
                {
                    float rho = (float)body.oceanDensity * 1000f, A = Mathf.PI * diameter * diameter / 4f;
                    float v = Mathf.Max(0f, Vector3.Dot((Vector3)vessel.srf_velocity, part.transform.up));
                    float staticT = Mathf.Pow(2f * rho * A * (P * 1000f) * (P * 1000f), 1f / 3f) / 1000f;   // kN, momentum theory
                    float runT = 0.6f * P / Mathf.Max(0.5f, v);                                                     // kN = kW / (m/s), 60 % efficient
                    float T = Mathf.Min(staticT, runT) * (reverse ? -0.6f : 1f);
                    Vector3 dir = part.transform.up;
                    if (driveType == "Waterjet") dir = Quaternion.AngleAxis(-vessel.ctrlState.yaw * 25f, part.transform.forward) * dir;
                    part.rb.AddForceAtPosition(dir * T, c, ForceMode.Force);
                    thrustNow = T;
                }
            }
            rotor.localRotation = Quaternion.AngleAxis(rpmAngle, Vector3.up);
        }
    }
}
