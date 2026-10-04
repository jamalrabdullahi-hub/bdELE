using System;
using System.Collections.Generic;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Dynamic Jet: one jet engine part you design in the editor. Diameter, bypass ratio, fan blades, compressor stages,
    /// tech level, nozzle and afterburner set the real engine numbers (thrust, Isp, Mach range, spool, heat, mass, cost)
    /// on the part's two ModuleEnginesFX (Dry / Wet, switched by MultiModeEngine) and build the engine's shape:
    /// nacelle, fan with the chosen blade count (spins with throttle), core cowl and plug for turbofans, glowing nozzle.
    ///   Low bypass: slim, fast, thirsty, hot.  High bypass: fat, efficient, slow-spooling, runs out of puff above Mach ~1.
    /// Engine axis is the part's Y: intake at +Y, exhaust at -Y (like stock jets).
    /// </summary>
    public class ModuleDynamicJet : PartModule, IPartMassModifier, IPartCostModifier
    {
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Diameter", guiUnits = " m"),
         UI_FloatEdit(scene = UI_Scene.Editor, minValue = 0.4f, maxValue = 5f, incrementLarge = 0.625f, incrementSmall = 0.05f, incrementSlide = 0.01f, sigFigs = 3)]
        public float diameter = 1.25f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Bypass ratio"),
         UI_FloatRange(scene = UI_Scene.Editor, minValue = 0f, maxValue = 12f, stepIncrement = 0.1f)]
        public float bypass = 0.8f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Fan blades"),
         UI_FloatRange(scene = UI_Scene.Editor, minValue = 8f, maxValue = 48f, stepIncrement = 1f)]
        public float blades = 22f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Compressor stages"),
         UI_FloatRange(scene = UI_Scene.Editor, minValue = 3f, maxValue = 20f, stepIncrement = 1f)]
        public float stages = 12f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Tech"),
         UI_ChooseOption(scene = UI_Scene.Editor, options = new[] { "Early", "Modern", "Advanced" })]
        public string tech = "Modern";

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Nozzle"),
         UI_ChooseOption(scene = UI_Scene.Editor, options = new[] { "Convergent", "Con-Di" })]
        public string nozzle = "Convergent";

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Afterburner"),
         UI_Toggle(scene = UI_Scene.Editor, enabledText = "Yes", disabledText = "No")]
        public bool afterburner = true;

        [KSPField(guiActiveEditor = true, guiActive = true, guiName = "Thrust (dry/wet)")] public string thrustInfo = "";
        [KSPField(guiActiveEditor = true, guiActive = true, guiName = "Isp (dry/wet)")] public string ispInfo = "";
        [KSPField(guiActiveEditor = true, guiName = "Top speed")] public string machInfo = "";
        [KSPField(guiActiveEditor = true, guiName = "Mass / spool")] public string massInfo = "";

        float mass = 1f, cost = 1000f, rpm;
        Transform jetRoot, fanSpin, thrustT, smokeT;
        Material nozzleMat;
        ModuleEnginesFX dry, wet;
        MultiModeEngine mm;
        readonly List<GameObject> built = new();

        float Tech => tech == "Early" ? 0.85f : tech == "Advanced" ? 1.15f : 1f;
        bool CD => nozzle == "Con-Di";

        public override void OnStart(StartState state)
        {
            jetRoot = part.FindModelTransform("jetRoot");
            thrustT = part.FindModelTransform("thrustTransform");
            smokeT = part.FindModelTransform("smokePoint");
            var ph = part.FindModelTransform("placeholder");
            if (ph != null) { foreach (var c in ph.GetComponents<Collider>()) Destroy(c); ph.gameObject.SetActive(false); }
            foreach (var e in part.FindModulesImplementing<ModuleEnginesFX>()) { if (e.engineID == "Wet") wet = e; else dry = e; }
            mm = part.FindModuleImplementing<MultiModeEngine>();
            foreach (var f in new[] { nameof(diameter), nameof(bypass), nameof(blades), nameof(stages), nameof(tech), nameof(nozzle), nameof(afterburner) })
                if (Fields[f].uiControlEditor is UI_Control c) c.onFieldChanged = OnChanged;
            Apply(false);
        }

        void OnChanged(BaseField f, object o)
        {
            Apply(true);
            foreach (var p in part.symmetryCounterparts)
                if (p.FindModuleImplementing<ModuleDynamicJet>() is ModuleDynamicJet m)
                {
                    m.diameter = diameter; m.bypass = bypass; m.blades = blades; m.stages = stages; m.tech = tech; m.nozzle = nozzle; m.afterburner = afterburner;
                    m.Apply(true);
                }
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch?.ship != null) GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
        }

        // ---------------- engine numbers ----------------
        void Apply(bool moveChildren)
        {
            float b = Mathf.Min(bypass, 8f) / 8f;              // 0 turbojet .. 1 big fan
            float stageF = stages / 12f;
            float bladeF = Mathf.Clamp((blades - 20f) * 0.002f, -0.04f, 0.05f);
            float k = Tech;

            float thrustDry = 75f * Mathf.Pow(diameter, 1.6f) * (1 + 0.06f * Mathf.Min(bypass, 8f)) * k * Mathf.Pow(stageF, 0.25f);
            float ispDry = Mathf.Min(14500f, (8000f + 600f * Mathf.Min(bypass, 8f)) * (0.85f + 0.15f * Mathf.Min(stageF, 1.5f)) * Mathf.Sqrt(k) * (1 + bladeF));
            float machMax = Mathf.Lerp(2.6f, 1.3f, b) + (CD && bypass < 1.5f ? 0.6f : 0f);
            float peak = Mathf.Lerp(1.5f, 0.95f, b) + (CD && bypass < 1.5f ? 0.15f : 0f);
            float accel = Mathf.Clamp(0.6f / (1 + 0.25f * bypass) / Mathf.Sqrt(Mathf.Max(0.5f, diameter / 1.25f)) * (1 - (blades - 20) * 0.005f), 0.05f, 0.9f);

            float twr = Mathf.Lerp(8f, 7f, b) * k;
            mass = Mathf.Max(0.05f, thrustDry / (9.81f * twr) * (1 + Mathf.Max(0, stages - 12) * 0.03f) + (afterburner ? 0.12f * diameter * diameter : 0));
            cost = thrustDry * 25f * k * k + (afterburner ? 400f * diameter : 0);

            float thrustWet = afterburner ? thrustDry * (bypass > 2 ? 1.4f : 1.55f) : thrustDry;
            float ispWet = afterburner ? ispDry * 0.42f : ispDry;
            float machWet = afterburner ? machMax + 0.8f + (CD ? 0.4f : 0) : machMax;
            float peakWet = afterburner ? (CD ? 3.0f : 2.2f) : peak;

            Configure(dry, thrustDry, ispDry, VelCurve(machMax, peak, b), machMax * 0.7f, accel, 40f / (1 + 0.3f * bypass));
            Configure(wet, thrustWet, ispWet, VelCurve(machWet, peakWet, b), machWet * 0.75f, afterburner ? Mathf.Max(accel, 0.6f) : accel, afterburner ? 120f : 40f / (1 + 0.3f * bypass));
            if (mm != null)
            {
                mm.Events["ModeEvent"].guiActive = mm.Events["ModeEvent"].guiActiveEditor = afterburner;
                if (!afterburner && !mm.runningPrimary && HighLogic.LoadedSceneIsFlight) mm.ModeEvent();
            }

            thrustInfo = $"{thrustDry:F0} / {thrustWet:F0} kN";
            ispInfo = $"{ispDry:F0} / {ispWet:F0} s";
            machInfo = $"Mach {machMax * 0.95f:F1} dry, {machWet * 0.95f:F1} wet";
            massInfo = $"{mass:F2} t, spool ~{1f / accel:F1} s";
            Build(moveChildren);
        }

        static FloatCurve VelCurve(float machMax, float peak, float b)
        {
            var c = new FloatCurve();
            float mp = machMax * 0.7f;
            c.Add(0, 1); c.Add(mp * 0.4f, Mathf.Lerp(0.95f, 0.85f, b)); c.Add(mp, peak); c.Add(machMax, 0);
            return c;
        }

        static void Configure(ModuleEnginesFX e, float thrust, float isp, FloatCurve vel, float machLimit, float accel, float heat)
        {
            if (e == null) return;
            e.maxThrust = thrust; e.minThrust = 0;
            var a = new FloatCurve(); a.Add(0, isp); a.Add(1, isp); e.atmosphereCurve = a;
            e.velCurve = vel; e.useVelCurve = true;
            e.machLimit = machLimit;
            e.engineAccelerationSpeed = accel; e.engineDecelerationSpeed = accel * 1.4f;
            e.heatProduction = heat;
            e.maxFuelFlow = thrust / (isp * (float)e.g);
            e.minFuelFlow = 0;
        }

        public override string GetInfo() => "Design it in the editor: diameter, bypass ratio, fan blades, compressor stages, tech, nozzle and afterburner set thrust, Isp, top speed, spool, heat, mass and cost.";

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) => mass - defaultMass;
        public ModifierChangeWhen GetModuleMassChangeWhen() => ModifierChangeWhen.FIXED;
        public float GetModuleCost(float defaultCost, ModifierStagingSituation sit) => cost - defaultCost;
        public ModifierChangeWhen GetModuleCostChangeWhen() => ModifierChangeWhen.FIXED;

        // ---------------- shape ----------------
        static Material nacelleMat, darkMat, bladeMat;

        void Build(bool moveChildren)
        {
            if (jetRoot == null) return;
            foreach (var g in built) if (g) Destroy(g);
            built.Clear();
            if (nacelleMat == null)
            {
                var spec = Shader.Find("KSP/Specular");
                nacelleMat = new Material(spec) { color = new Color(0.55f, 0.57f, 0.58f) }; nacelleMat.SetFloat("_Shininess", 0.4f);
                darkMat = new Material(spec) { color = new Color(0.12f, 0.12f, 0.13f) };
                bladeMat = new Material(spec) { color = new Color(0.7f, 0.72f, 0.75f) }; bladeMat.SetFloat("_Shininess", 0.8f);
            }
            if (nozzleMat == null) { nozzleMat = new Material(Shader.Find("KSP/Emissive/Specular") ?? Shader.Find("KSP/Specular")) { color = new Color(0.25f, 0.22f, 0.2f) }; }

            float D = diameter, R = D / 2, by = Mathf.Min(bypass, 10f);
            float Rc = R * Mathf.Clamp(1f / Mathf.Sqrt(1 + bypass), 0.35f, 1f);
            float L = Mathf.Max(0.8f * D, D * (1.8f - 0.06f * by)) + (afterburner ? 0.5f * D : 0);
            bool fan = bypass > 1f;
            float yTop = L / 2, yDuctEnd = fan ? yTop - 0.55f * L : -L / 2;

            // nacelle: lip, body, aft taper (double-sided so you can see into the intake)
            var nac = new List<Vector2> { new(R * 0.88f, yTop), new(R, yTop - 0.06f * D), new(R, yTop - 0.35f * (yTop - yDuctEnd)), new(fan ? R * 0.95f : R * 0.85f, yDuctEnd) };
            var nacGo = Lathe("nacelle", nac, nacelleMat, true, 28);
            var mc = nacGo.AddComponent<MeshCollider>(); mc.convex = true; mc.sharedMesh = nacGo.GetComponent<MeshFilter>().sharedMesh;

            float rNoz0;
            if (fan)
            {
                // core cowl sticking out of the fan duct
                Lathe("core", new List<Vector2> { new(Rc, yDuctEnd + 0.15f * D), new(Rc, yDuctEnd), new(Rc * 0.75f, -L / 2) }, darkMat, false, 24);
                rNoz0 = Rc * 0.75f;
            }
            else rNoz0 = R * 0.85f;
            float rNoz1 = rNoz0 * (CD ? 1.05f : 0.82f), nozLen = 0.25f * D, yN = -L / 2;
            Lathe("nozzle", new List<Vector2> { new(rNoz0, yN), new(rNoz1, yN - nozLen) }, nozzleMat, true, 24);
            Lathe("plug", new List<Vector2> { new(rNoz1 * 0.5f, yN - nozLen * 0.6f), new(0.01f, yN - nozLen - 0.3f * rNoz1) }, darkMat, false, 16);
            // inner face so the intake isn't see-through
            Lathe("innerFace", new List<Vector2> { new(R * 0.86f, yTop - 0.25f * D), new(0.01f, yTop - 0.25f * D) }, darkMat, true, 24);

            // fan: hub dome + blades on fanSpin
            var spinGo = new GameObject("fanSpin"); spinGo.transform.SetParent(jetRoot, false); spinGo.transform.localPosition = new Vector3(0, yTop - 0.12f * D, 0);
            built.Add(spinGo); fanSpin = spinGo.transform;
            Lathe("hub", new List<Vector2> { new(0.01f, 0.1f * D), new(0.28f * R, 0), new(0.28f * R, -0.08f * D) }, darkMat, false, 20, fanSpin);
            var bg = new GameObject("blades"); bg.transform.SetParent(fanSpin, false);
            bg.AddComponent<MeshFilter>().sharedMesh = Blades((int)blades, 0.27f * R, 0.95f * R, 0.04f * D);
            bg.AddComponent<MeshRenderer>().sharedMaterial = bladeMat;

            // thrust / FX points at the nozzle exit; nodes on the intake face and the nozzle
            if (thrustT != null) { thrustT.localPosition = new Vector3(0, yN - nozLen, 0); thrustT.localRotation = Quaternion.Euler(90, 0, 0); }
            if (smokeT != null) { smokeT.localPosition = new Vector3(0, yN - nozLen, 0); smokeT.localRotation = Quaternion.Euler(90, 0, 0); }
            SetNode("top", new Vector3(0, yTop, 0), Vector3.up, moveChildren);
            SetNode("bottom", new Vector3(0, yN - nozLen, 0), Vector3.down, moveChildren);
            if (HighLogic.LoadedSceneIsFlight && part.DragCubes != null) { part.DragCubes.Procedural = true; part.DragCubes.ForceUpdate(true, true, true); }
        }

        GameObject Lathe(string name, List<Vector2> prof, Material mat, bool doubleSided, int seg, Transform parent = null)
        {
            var go = new GameObject(name); go.transform.SetParent(parent ?? jetRoot, false); built.Add(go);
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            for (int i = 0; i < prof.Count; i++)
                for (int s = 0; s <= seg; s++)
                {
                    float a = s * Mathf.PI * 2 / seg;
                    v.Add(new Vector3(Mathf.Cos(a) * prof[i].x, prof[i].y, Mathf.Sin(a) * prof[i].x)); uv.Add(new Vector2((float)s / seg, (float)i / (prof.Count - 1)));
                }
            for (int i = 0; i < prof.Count - 1; i++)
                for (int s = 0; s < seg; s++)
                {
                    int a = i * (seg + 1) + s, b = a + seg + 1;
                    t.AddRange(new[] { a, a + 1, b, a + 1, b + 1, b });
                    if (doubleSided) t.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
                }
            var m = new Mesh { name = name }; m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = m; go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static Mesh Blades(int n, float r0, float r1, float chordDepth)
        {
            var v = new List<Vector3>(); var t = new List<int>();
            float chord = Mathf.Min(2 * Mathf.PI * (r0 + r1) / 2 / n * 0.85f, (r1 - r0) * 0.5f);
            for (int i = 0; i < n; i++)
            {
                var q = Quaternion.Euler(0, i * 360f / n, 0);
                var twistRoot = Quaternion.AngleAxis(50f, Vector3.right); var twistTip = Quaternion.AngleAxis(25f, Vector3.right);
                Vector3 c = new(0, 0, chord / 2);
                Vector3[] p =
                {
                    q * (new Vector3(r0, 0, 0) + twistRoot * -c), q * (new Vector3(r0, 0, 0) + twistRoot * c),
                    q * (new Vector3(r1, 0, 0) + twistTip * c), q * (new Vector3(r1, 0, 0) + twistTip * -c),
                };
                int k = v.Count; v.AddRange(p);
                t.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3, k, k + 2, k + 1, k, k + 3, k + 2 });
            }
            var m = new Mesh { name = "fanBlades" }; m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        void SetNode(string id, Vector3 pos, Vector3 dir, bool moveChildren)
        {
            var n = part.FindAttachNode(id); if (n == null) return;
            var old = n.position;
            n.position = n.originalPosition = pos; n.orientation = n.originalOrientation = dir;
            if (!moveChildren || n.attachedPart == null || n.attachedPart.parent != part) return;
            var d = part.transform.TransformVector(pos - old);
            n.attachedPart.transform.position += d; n.attachedPart.attPos0 += d;
        }

        // ---------------- running look ----------------
        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            var e = mm != null && !mm.runningPrimary ? wet : dry;
            float thr = e != null && e.EngineIgnited ? e.currentThrottle : 0;
            rpm = Mathf.MoveTowards(rpm, e != null && e.EngineIgnited ? Mathf.Lerp(300f, 2400f / Mathf.Max(0.5f, diameter), thr) : 0, Time.deltaTime * 800f);
            if (fanSpin != null && rpm > 0) fanSpin.Rotate(Vector3.up, rpm * 6f * Time.deltaTime, Space.Self);
            if (nozzleMat != null)
            {
                bool wetOn = mm != null && !mm.runningPrimary && afterburner;
                nozzleMat.SetColor("_EmissiveColor", Color.Lerp(Color.black, wetOn ? new Color(1f, 0.55f, 0.2f) : new Color(0.6f, 0.25f, 0.1f), thr * (wetOn ? 1f : 0.5f)));
            }
        }

        void OnDestroy() { foreach (var g in built) if (g) Destroy(g); if (nozzleMat != null) Destroy(nozzleMat); }
    }
}
