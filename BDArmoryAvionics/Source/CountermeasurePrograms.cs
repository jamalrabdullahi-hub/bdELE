using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using BDArmory.Control;
using BDArmory.CounterMeasure;
using BDArmory.Extensions;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Countermeasure program for a craft, added by ModuleManager to BDArmory weapon managers and set up in the VAB/SPH.
    /// A program is salvos of bursts: flares against heat seekers, chaff against radar missiles.
    /// Auto: runs when a missile is inbound inside the trigger time. Manual: runs on the CM program key.
    /// </summary>
    public class ModuleCMProgram : PartModule
    {
        public static readonly string[] Modes = ["Off", "Manual", "Auto"];

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "CM program", groupName = "cmprog", groupDisplayName = "Countermeasure program", groupStartCollapsed = true),
         UI_ChooseOption(options = ["Off", "Manual", "Auto"], scene = UI_Scene.All)]
        public string mode = "Manual"; // Auto has to be chosen per craft: it makes targets very hard to hit with radar/IR missiles

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Trigger time to impact", guiUnits = " s", groupName = "cmprog"),
         UI_FloatRange(minValue = 1f, maxValue = 15f, stepIncrement = 0.5f, scene = UI_Scene.All)]
        public float triggerTime = 6f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Flares per burst", groupName = "cmprog"),
         UI_FloatRange(minValue = 0f, maxValue = 8f, stepIncrement = 1f, scene = UI_Scene.All)]
        public float flaresPerBurst = 2;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Chaff per burst", groupName = "cmprog"),
         UI_FloatRange(minValue = 0f, maxValue = 8f, stepIncrement = 1f, scene = UI_Scene.All)]
        public float chaffPerBurst = 2;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Bursts per salvo", groupName = "cmprog"),
         UI_FloatRange(minValue = 1f, maxValue = 10f, stepIncrement = 1f, scene = UI_Scene.All)]
        public float bursts = 4;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Burst interval", guiUnits = " s", groupName = "cmprog"),
         UI_FloatRange(minValue = 0.1f, maxValue = 2f, stepIncrement = 0.05f, scene = UI_Scene.All)]
        public float burstInterval = 0.3f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Salvos", groupName = "cmprog"),
         UI_FloatRange(minValue = 1f, maxValue = 5f, stepIncrement = 1f, scene = UI_Scene.All)]
        public float salvos = 2;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Salvo interval", guiUnits = " s", groupName = "cmprog"),
         UI_FloatRange(minValue = 0.2f, maxValue = 5f, stepIncrement = 0.1f, scene = UI_Scene.All)]
        public float salvoInterval = 1f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Also when AI flies", groupName = "cmprog"),
         UI_Toggle(enabledText = "Yes", disabledText = "No (BDA AI handles it)", scene = UI_Scene.All)]
        public bool runUnderAI = false;

        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "Program", groupName = "cmprog")]
        public string status = "idle";

        public bool Auto => mode == "Auto";
        public bool Enabled => mode != "Off";

        [KSPEvent(guiActive = true, guiActiveEditor = false, guiName = "Run CM program", groupName = "cmprog")]
        public void RunNow() => CMProgramRunner.Run(vessel, threatHeat: null);

        public override string GetInfo() =>
            "Countermeasure program: salvos of flare/chaff bursts.\nAuto runs when a missile is inbound inside the trigger time; Manual runs on the CM program key (default Keypad 0).";

        public override string GetModuleDisplayName() => "Countermeasure Program";

        public static ModuleCMProgram For(Vessel v, Part wmPart = null)
        {
            if (v == null) return null;
            if (wmPart != null && wmPart.FindModuleImplementing<ModuleCMProgram>() is ModuleCMProgram p) return p;
            foreach (var part in v.parts) if (part != null && part.FindModuleImplementing<ModuleCMProgram>() is ModuleCMProgram q) return q;
            return null;
        }
    }

    /// <summary>Runs countermeasure programs for every loaded craft that has one.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class CMProgramRunner : MonoBehaviour
    {
        static CMProgramRunner instance;
        static readonly Dictionary<Vessel, float> busyUntil = new();
        float nextCheck;

        void Awake() { instance = this; busyUntil.Clear(); }
        void OnDestroy() { if (instance == this) instance = null; busyUntil.Clear(); }

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            var key = AvionicsSettings.KeyCMProgram;
            if (key != KeyCode.None && Input.GetKeyDown(key) && GUIUtility.keyboardControl == 0)
            {
                var v = FlightGlobals.ActiveVessel;
                var prog = ModuleCMProgram.For(v);
                if (prog != null && prog.Enabled) Run(v, threatHeat: null);
                else ScreenMessages.PostScreenMessage(prog == null ? "No countermeasure program on this craft" : "Countermeasure program is off", 2f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || Time.time < nextCheck) return;
            nextCheck = Time.time + 0.1f;
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed) continue;
                var wm = v.ActiveController().WM;
                if (wm == null || !wm.missileIsIncoming || wm.incomingMissileVessel == null) continue;
                var prog = ModuleCMProgram.For(v, wm.part);
                if (prog == null || !prog.Auto) continue;
                if (wm.guardMode && !prog.runUnderAI) continue; // BDArmory's AI dispenses its own countermeasures
                if (wm.incomingMissileTime > prog.triggerTime) continue;
                var mb = wm.incomingMissileVessel.FindPartModuleImplementing<MissileBase>();
                bool? heat = mb == null ? null : mb.TargetingMode == MissileBase.TargetingModes.Heat;
                Run(v, heat);
            }
        }

        /// <summary>Start the vessel's program unless it's already running. threatHeat: true = IR missile (flares), false = radar (chaff), null = both.</summary>
        public static void Run(Vessel v, bool? threatHeat)
        {
            if (instance == null || v == null) return;
            if (busyUntil.TryGetValue(v, out float until) && Time.time < until) return;
            var prog = ModuleCMProgram.For(v);
            if (prog == null || !prog.Enabled) return;
            float duration = prog.salvos * (prog.bursts * prog.burstInterval + prog.salvoInterval);
            busyUntil[v] = Time.time + duration + 0.5f;
            Debug.Log($"[BDArmoryAvionics]: CM program on {v.GetName()} ({prog.mode}): {(threatHeat == true ? "flares (IR missile)" : threatHeat == false ? "chaff (radar missile)" : "flares + chaff")}");
            instance.StartCoroutine(instance.Program(v, prog, threatHeat));
        }

        IEnumerator Program(Vessel v, ModuleCMProgram prog, bool? heat)
        {
            int flares = heat == false ? 0 : (int)prog.flaresPerBurst;
            int chaff = heat == true ? 0 : (int)prog.chaffPerBurst;
            for (int s = 0; s < (int)prog.salvos; s++)
            {
                for (int b = 0; b < (int)prog.bursts; b++)
                {
                    if (v == null || !v.loaded) yield break;
                    prog.status = $"salvo {s + 1}/{(int)prog.salvos} burst {b + 1}";
                    Drop(v, CMDropper.CountermeasureTypes.Flare, flares);
                    Drop(v, CMDropper.CountermeasureTypes.Chaff, chaff);
                    yield return new WaitForSeconds(prog.burstInterval);
                }
                yield return new WaitForSeconds(prog.salvoInterval);
            }
            if (prog != null) prog.status = "idle";
        }

        /// <summary>Drop count countermeasures of a type, spread across that type's dispensers.</summary>
        static void Drop(Vessel v, CMDropper.CountermeasureTypes type, int count)
        {
            if (count <= 0) return;
            var droppers = new List<CMDropper>();
            foreach (var d in VesselModuleRegistry.GetModules<CMDropper>(v)) if (d != null && d.cmType == type) droppers.Add(d);
            if (droppers.Count == 0) return;
            for (int i = 0; i < count; i++) droppers[i % droppers.Count].DropCM();
        }
    }
}
