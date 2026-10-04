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

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Finish"),
         UI_ChooseOption(scene = UI_Scene.Editor, options = new[] { "Grey", "White", "Dark", "Bare metal" })]
        public string finish = "Grey";

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
            foreach (var f in new[] { nameof(diameter), nameof(bypass), nameof(blades), nameof(stages), nameof(tech), nameof(nozzle), nameof(afterburner), nameof(finish) })
                if (Fields[f].uiControlEditor is UI_Control c) c.onFieldChanged = OnChanged;
            Apply(false);
        }

        void OnChanged(BaseField f, object o)
        {
            Apply(true);
            foreach (var p in part.symmetryCounterparts)
                if (p.FindModuleImplementing<ModuleDynamicJet>() is ModuleDynamicJet m)
                {
                    m.diameter = diameter; m.bypass = bypass; m.blades = blades; m.stages = stages; m.tech = tech; m.nozzle = nozzle; m.afterburner = afterburner; m.finish = finish;
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
        static Material darkMat, bladeMat, spinnerMat, blurMat, innerMat;
        static Texture2D panelTex, nozzleTex, spiralTex, blurTex;
        static readonly Dictionary<string, Material> finishMats = new();
        Transform petals; float petalOpen;

        static readonly Dictionary<string, Color> Finishes = new()
        {
            ["Grey"] = new Color(0.62f, 0.64f, 0.66f), ["White"] = new Color(0.9f, 0.9f, 0.88f),
            ["Dark"] = new Color(0.22f, 0.23f, 0.25f), ["Bare metal"] = new Color(0.78f, 0.78f, 0.8f),
        };

        static void Textures()
        {
            if (panelTex != null) return;
            // Panel lines + rivets on a light base (tinted by the material colour)
            panelTex = new Texture2D(256, 256, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    int row = y / 64, xo = (x + (row % 2) * 32) % 128;
                    bool line = y % 64 < 2 || xo < 2;
                    bool rivet = (y % 64 == 5 || y % 64 == 59) && x % 8 == 0 || (xo == 5 || xo == 123) && y % 8 == 0;
                    float n = 0.93f + 0.07f * Mathf.PerlinNoise(x * 0.05f, y * 0.05f);
                    float v = line ? 0.55f : rivet ? 0.75f : n;
                    panelTex.SetPixel(x, y, new Color(v, v, v, 1));
                }
            panelTex.Apply(true);
            // Nozzle: heat tint along the length (steel -> straw -> blue -> dark), petal seams around
            nozzleTex = new Texture2D(256, 64, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
            Color[] heat = { new(0.62f, 0.62f, 0.6f), new(0.72f, 0.62f, 0.4f), new(0.45f, 0.38f, 0.55f), new(0.28f, 0.32f, 0.45f), new(0.15f, 0.14f, 0.14f) };
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 256; x++)
                {
                    float t = y / 63f * (heat.Length - 1); int i = Mathf.Min((int)t, heat.Length - 2);
                    var c = Color.Lerp(heat[i], heat[i + 1], t - i) * (0.9f + 0.1f * Mathf.PerlinNoise(x * 0.1f, y * 0.3f));
                    if (x % 16 == 0) c *= 0.6f;
                    c.a = 1; nozzleTex.SetPixel(x, y, c);
                }
            nozzleTex.Apply(true);
            // Spinner spiral
            spiralTex = new Texture2D(128, 128, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
            for (int y = 0; y < 128; y++)
                for (int x = 0; x < 128; x++)
                {
                    float f = Mathf.Repeat(x / 128f + y / 128f * 0.9f, 1f);
                    float v = f < 0.12f ? 0.95f : 0.12f;
                    spiralTex.SetPixel(x, y, new Color(v, v, v, 1));
                }
            spiralTex.Apply(true);
            // Fan blur disc: soft radial alpha
            blurTex = new Texture2D(64, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    float d = new Vector2(x - 31.5f, y - 31.5f).magnitude / 32f;
                    float a = d > 1 || d < 0.28f ? 0 : 0.5f * (1 - Mathf.Pow(d, 6));
                    blurTex.SetPixel(x, y, new Color(0.75f, 0.76f, 0.78f, a));
                }
            blurTex.Apply();
        }

        Material Finish()
        {
            if (!finishMats.TryGetValue(finish, out var m) || m == null)
            {
                m = new Material(Shader.Find("KSP/Specular")) { mainTexture = panelTex, color = Finishes.TryGetValue(finish, out var c) ? c : Finishes["Grey"] };
                m.SetFloat("_Shininess", finish == "Bare metal" ? 0.9f : 0.35f);
                m.SetColor("_SpecColor", finish == "Bare metal" ? new Color(0.8f, 0.8f, 0.8f) : new Color(0.35f, 0.35f, 0.35f));
                finishMats[finish] = m;
            }
            return m;
        }

        void Build(bool moveChildren)
        {
            if (jetRoot == null) return;
            foreach (var g in built) if (g) Destroy(g);
            built.Clear();
            Textures();
            if (darkMat == null)
            {
                var spec = Shader.Find("KSP/Specular");
                darkMat = new Material(spec) { color = new Color(0.1f, 0.1f, 0.11f) };
                innerMat = new Material(spec) { mainTexture = panelTex, color = new Color(0.3f, 0.31f, 0.33f) };
                bladeMat = new Material(spec) { color = new Color(0.68f, 0.7f, 0.74f) }; bladeMat.SetFloat("_Shininess", 0.95f); bladeMat.SetColor("_SpecColor", new Color(0.9f, 0.9f, 0.9f));
                spinnerMat = new Material(spec) { mainTexture = spiralTex, color = Color.white }; spinnerMat.SetFloat("_Shininess", 0.6f);
                blurMat = new Material(Shader.Find("KSP/Alpha/Translucent") ?? Shader.Find("KSP/Particles/Alpha Blended")) { mainTexture = blurTex, color = new Color(1, 1, 1, 0) };
            }
            if (nozzleMat == null) { nozzleMat = new Material(Shader.Find("KSP/Emissive/Specular") ?? Shader.Find("KSP/Specular")) { mainTexture = nozzleTex, color = Color.white }; nozzleMat.SetFloat("_Shininess", 0.5f); }
            var skin = Finish();

            float D = diameter, R = D / 2, by = Mathf.Min(bypass, 10f);
            float Rc = R * Mathf.Clamp(1f / Mathf.Sqrt(1 + bypass), 0.35f, 1f);
            float L = Mathf.Max(0.8f * D, D * (1.8f - 0.06f * by)) + (afterburner ? 0.5f * D : 0);
            bool fan = bypass > 1f;
            float yTop = L / 2, yDuctEnd = fan ? yTop - Mathf.Lerp(0.75f, 0.5f, Mathf.Clamp01(bypass / 8f)) * L : -L / 2;
            float t = Mathf.Max(0.02f, 0.035f * D);           // nacelle wall
            float Ri = R - t, lipR = t / 2;
            float Rend = fan ? R * 0.93f : R * 0.86f;

            // Nacelle: one closed profile (inner duct -> rounded lip -> outer skin with a slight bulge -> boat tail -> trailing edge)
            var nac = new List<Vector2> { new(Rend - t, yDuctEnd), new(Ri, yTop - 0.3f * D), new(Ri, yTop - lipR) };
            for (int i = 1; i < 8; i++) { float a = Mathf.PI - i * Mathf.PI / 8; nac.Add(new Vector2(R - lipR + Mathf.Cos(a) * lipR, yTop - lipR + Mathf.Sin(a) * lipR)); }
            float len = yTop - yDuctEnd;
            nac.Add(new Vector2(R, yTop - lipR)); nac.Add(new Vector2(R * 1.015f, yTop - 0.18f * len)); nac.Add(new Vector2(R * 1.02f, yTop - 0.4f * len));
            nac.Add(new Vector2(R * 0.995f, yTop - 0.7f * len)); nac.Add(new Vector2(Rend, yDuctEnd)); nac.Add(new Vector2(Rend - t, yDuctEnd));
            var nacGo = Lathe("nacelle", nac, skin, 48, 2f * Mathf.PI * R / 1.5f);
            var mc = nacGo.AddComponent<MeshCollider>(); mc.convex = true; mc.sharedMesh = nacGo.GetComponent<MeshFilter>().sharedMesh;

            float fanY = yTop - 0.14f * D;
            float rNoz0, yN = -L / 2;
            if (fan)
            {
                // Core cowl leaving the fan duct, curving into the core nozzle
                var core = new List<Vector2> { new(Rc * 1.02f, yDuctEnd + 0.25f * D), new(Rc, yDuctEnd) };
                for (int i = 1; i <= 5; i++) { float f = i / 5f; core.Add(new Vector2(Mathf.Lerp(Rc, Rc * 0.74f, f * f), Mathf.Lerp(yDuctEnd, yN, f))); }
                Lathe("core", core, skin, 40, 2f * Mathf.PI * Rc / 1.5f);
                rNoz0 = Rc * 0.74f;
                // Outlet guide vanes in the bypass duct
                Vanes("bypassVanes", 28, Rc * 1.02f, Ri, fanY - 0.22f * D, 0.08f * D, 12f, darkMat);
            }
            else
            {
                rNoz0 = Rend - t * 0.5f;
                Vanes("inletVanes", 14, 0.3f * R, Ri, fanY - 0.12f * D, 0.06f * D, 8f, darkMat);
            }
            float nozLen = (afterburner && !fan ? 0.35f : 0.22f) * D;
            float rNoz1 = rNoz0 * (CD ? 1.06f : 0.84f);
            // Nozzle: smooth shell, or afterburner petals ("turkey feathers") that open in wet mode
            if (afterburner && !fan)
            {
                var pg = new GameObject("petals"); pg.transform.SetParent(jetRoot, false); pg.transform.localPosition = new Vector3(0, yN, 0); built.Add(pg); petals = pg.transform;
                pg.AddComponent<MeshFilter>().sharedMesh = Petals(18, rNoz0, rNoz1, nozLen, t);
                pg.AddComponent<MeshRenderer>().sharedMaterial = nozzleMat;
            }
            else
            {
                petals = null;
                Lathe("nozzle", new List<Vector2> { new(rNoz0, yN), new(rNoz1, yN - nozLen), new(rNoz1 - t * 0.6f, yN - nozLen), new(rNoz0 - t * 0.6f, yN) }, nozzleMat, 40, 4);
            }
            // Exhaust plug / turbine face
            var plug = new List<Vector2>();
            for (int i = 0; i <= 6; i++) { float f = i / 6f; plug.Add(new Vector2(Mathf.Max(0.005f, rNoz1 * 0.5f * Mathf.Sqrt(1 - f * f)), yN - nozLen * 0.3f - f * (0.45f * rNoz1 + 0.2f * nozLen))); }
            Lathe("plug", plug, innerMat, 28, 2);
            Lathe("turbineFace", new List<Vector2> { new(rNoz0 * 0.98f, yN + 0.05f * D), new(0.005f, yN + 0.05f * D) }, darkMat, 28, 1);
            // Compressor face behind the fan (so the intake isn't see-through)
            Lathe("compressorFace", new List<Vector2> { new(0.005f, fanY - 0.3f * D), new(Ri * 0.99f, fanY - 0.3f * D) }, innerMat, 40, 2);

            // Fan: spinner + curved blades on fanSpin, plus a blur disc for high rpm
            var spinGo = new GameObject("fanSpin"); spinGo.transform.SetParent(jetRoot, false); spinGo.transform.localPosition = new Vector3(0, fanY, 0);
            built.Add(spinGo); fanSpin = spinGo.transform;
            float hubR = fan ? 0.26f * R : 0.32f * R;
            var sp = new List<Vector2>();
            for (int i = 0; i <= 8; i++) { float f = i / 8f; float ang = f * Mathf.PI / 2; sp.Add(new Vector2(Mathf.Max(0.003f, Mathf.Sin(ang) * hubR), Mathf.Cos(ang) * (fan ? 0.32f : 0.22f) * D)); }
            sp.Add(new Vector2(hubR, -0.1f * D));
            Lathe("spinner", sp, spinnerMat, 32, 1, fanSpin);
            var bg = new GameObject("blades"); bg.transform.SetParent(fanSpin, false);
            bg.AddComponent<MeshFilter>().sharedMesh = Blades((int)blades, hubR * 0.95f, Ri * 0.99f, fan);
            bg.AddComponent<MeshRenderer>().sharedMaterial = bladeMat;
            var blur = new GameObject("fanBlur"); blur.transform.SetParent(jetRoot, false); blur.transform.localPosition = new Vector3(0, fanY + 0.01f, 0);
            blur.transform.localRotation = Quaternion.Euler(-90, 0, 0); blur.transform.localScale = Vector3.one * Ri * 2f; built.Add(blur);
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad); Destroy(q.GetComponent<Collider>()); q.transform.SetParent(blur.transform, false);
            blurQuad = q.GetComponent<Renderer>(); blurQuad.material = new Material(blurMat); blurQuad.enabled = false;

            // thrust / FX points at the nozzle exit; nodes on the intake face and the nozzle, sized to the diameter
            float yExit = yN - nozLen;
            if (thrustT != null) { thrustT.localPosition = new Vector3(0, yExit, 0); thrustT.localRotation = Quaternion.Euler(90, 0, 0); }
            if (smokeT != null) { smokeT.localPosition = new Vector3(0, yExit, 0); smokeT.localRotation = Quaternion.Euler(90, 0, 0); }
            int size = Mathf.Clamp(Mathf.RoundToInt(D / 1.25f), 0, 4);
            SetNode("top", new Vector3(0, yTop, 0), Vector3.up, moveChildren, size);
            SetNode("bottom", new Vector3(0, yExit, 0), Vector3.down, moveChildren, size);
            if (part.srfAttachNode != null) { part.srfAttachNode.position = part.srfAttachNode.originalPosition = new Vector3(R * 1.02f, yTop - 0.4f * len, 0); part.srfAttachNode.orientation = part.srfAttachNode.originalOrientation = Vector3.right; }
            if (HighLogic.LoadedSceneIsFlight && part.DragCubes != null) { part.DragCubes.Procedural = true; part.DragCubes.ForceUpdate(true, true, true); }
        }

        Renderer blurQuad;

        /// <summary>Surface of revolution about Y. The profile's direction sets which side faces out (left of travel when going -Y).</summary>
        GameObject Lathe(string name, List<Vector2> prof, Material mat, int seg, float uRepeat, Transform parent = null)
        {
            var go = new GameObject(name); go.transform.SetParent(parent ?? jetRoot, false); built.Add(go);
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            float total = 0; var acc = new float[prof.Count];
            for (int i = 1; i < prof.Count; i++) { total += (prof[i] - prof[i - 1]).magnitude; acc[i] = total; }
            float circ = 2 * Mathf.PI * Mathf.Max(0.05f, prof[0].x);
            for (int i = 0; i < prof.Count; i++)
                for (int s = 0; s <= seg; s++)
                {
                    float a = s * Mathf.PI * 2 / seg;
                    v.Add(new Vector3(Mathf.Cos(a) * prof[i].x, prof[i].y, Mathf.Sin(a) * prof[i].x));
                    uv.Add(new Vector2((float)s / seg * uRepeat, acc[i] / circ * uRepeat));
                }
            for (int i = 0; i < prof.Count - 1; i++)
                for (int s = 0; s < seg; s++)
                {
                    int a = i * (seg + 1) + s, b = a + seg + 1;
                    tri.AddRange(new[] { a, a + 1, b, a + 1, b + 1, b });
                }
            var m = new Mesh { name = name }; m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tri, 0); m.RecalculateNormals(); m.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = m; go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>Fan blades: spanwise sections with twist, taper and (on fans) scimitar sweep; double-sided.</summary>
        static Mesh Blades(int n, float r0, float r1, bool fan)
        {
            const int S = 5;
            var v = new List<Vector3>(); var tri = new List<int>();
            float pitch = 2 * Mathf.PI * (r0 + r1) / 2 / n;
            float cRoot = Mathf.Min(pitch * 0.9f, (r1 - r0) * 0.45f), cTip = cRoot * (fan ? 1.35f : 0.75f);
            for (int b = 0; b < n; b++)
            {
                var q = Quaternion.Euler(0, b * 360f / n, 0);
                int k0 = v.Count;
                for (int j = 0; j <= S; j++)
                {
                    float f = j / (float)S, r = Mathf.Lerp(r0, r1, f), c = Mathf.Lerp(cRoot, cTip, f);
                    var twist = Quaternion.AngleAxis(Mathf.Lerp(58f, fan ? 22f : 32f, f), Vector3.right);
                    var sweep = Quaternion.Euler(0, (fan ? 14f : 4f) * f * f, 0);
                    Vector3 mid = new(r, 0, 0);
                    v.Add(q * sweep * (mid + twist * new Vector3(0, 0, -c / 2)));
                    v.Add(q * sweep * (mid + twist * new Vector3(0, 0, c / 2)));
                }
                for (int j = 0; j < S; j++)
                {
                    int a = k0 + j * 2;
                    tri.AddRange(new[] { a, a + 1, a + 3, a, a + 3, a + 2, a, a + 3, a + 1, a, a + 2, a + 3 });
                }
            }
            var m = new Mesh { name = "fanBlades" }; m.SetVertices(v); m.SetTriangles(tri, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        /// <summary>Ring of thin fixed vanes (stators / guide vanes).</summary>
        void Vanes(string name, int n, float r0, float r1, float y, float chord, float angle, Material mat)
        {
            var v = new List<Vector3>(); var tri = new List<int>();
            for (int b = 0; b < n; b++)
            {
                var q = Quaternion.Euler(0, b * 360f / n, 0); var tw = Quaternion.AngleAxis(angle, Vector3.right);
                int k = v.Count;
                v.Add(q * new Vector3(r0, 0, 0) + q * (tw * new Vector3(0, chord / 2, 0)));
                v.Add(q * new Vector3(r1, 0, 0) + q * (tw * new Vector3(0, chord / 2, 0)));
                v.Add(q * new Vector3(r1, 0, 0) + q * (tw * new Vector3(0, -chord / 2, 0)));
                v.Add(q * new Vector3(r0, 0, 0) + q * (tw * new Vector3(0, -chord / 2, 0)));
                tri.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3, k, k + 2, k + 1, k, k + 3, k + 2 });
            }
            var go = new GameObject(name); go.transform.SetParent(jetRoot, false); go.transform.localPosition = new Vector3(0, y, 0); built.Add(go);
            var m = new Mesh { name = name }; m.SetVertices(v); m.SetTriangles(tri, 0); m.RecalculateNormals(); m.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = m; go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        /// <summary>Afterburner nozzle petals: overlapping curved plates, double-sided, opened by scaling X/Z.</summary>
        static Mesh Petals(int n, float r0, float r1, float len, float thick)
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            float w = 2 * Mathf.PI / n * 0.62f;
            for (int b = 0; b < n; b++)
            {
                float a0 = b * 2 * Mathf.PI / n - w, a1 = b * 2 * Mathf.PI / n + w;
                int k = v.Count;
                const int S = 3;
                for (int j = 0; j <= S; j++)
                {
                    float f = j / (float)S, r = Mathf.Lerp(r0, r1, f) + (b % 2) * thick * 0.4f, y = -f * len;
                    v.Add(new Vector3(Mathf.Cos(a0) * r, y, Mathf.Sin(a0) * r)); uv.Add(new Vector2(0, f));
                    v.Add(new Vector3(Mathf.Cos((a0 + a1) / 2) * r * 1.01f, y, Mathf.Sin((a0 + a1) / 2) * r * 1.01f)); uv.Add(new Vector2(0.5f, f));
                    v.Add(new Vector3(Mathf.Cos(a1) * r, y, Mathf.Sin(a1) * r)); uv.Add(new Vector2(1, f));
                }
                for (int j = 0; j < S; j++)
                    for (int c = 0; c < 2; c++)
                    {
                        int a = k + j * 3 + c, d = a + 3;
                        tri.AddRange(new[] { a, a + 1, d, a + 1, d + 1, d, a, d, a + 1, a + 1, d, d + 1 });
                    }
            }
            var m = new Mesh { name = "petals" }; m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tri, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        void SetNode(string id, Vector3 pos, Vector3 dir, bool moveChildren, int size)
        {
            var n = part.FindAttachNode(id); if (n == null) return;
            n.size = size;
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
            if (blurQuad != null)
            {
                float a = Mathf.Clamp01((rpm - 900f) / 1200f) * 0.55f;
                blurQuad.enabled = a > 0.01f;
                if (blurQuad.enabled) blurQuad.material.color = new Color(1, 1, 1, a);
            }
            if (petals != null)
            {
                bool open = mm != null && !mm.runningPrimary && thr > 0.05f;
                petalOpen = Mathf.MoveTowards(petalOpen, open ? 1f : 0f, Time.deltaTime * 1.5f);
                float sc = 1f + petalOpen * 0.16f;
                petals.localScale = new Vector3(sc, 1, sc);
            }
            if (nozzleMat != null)
            {
                bool wetOn = mm != null && !mm.runningPrimary && afterburner;
                nozzleMat.SetColor("_EmissiveColor", Color.Lerp(Color.black, wetOn ? new Color(1f, 0.55f, 0.2f) : new Color(0.6f, 0.25f, 0.1f), thr * (wetOn ? 1f : 0.5f)));
            }
        }

        void OnDestroy() { foreach (var g in built) if (g) Destroy(g); if (nozzleMat != null) Destroy(nozzleMat); }
    }
}
