using HarmonyLib;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Turns any stock-style parachute into a landing brake chute: out of the staging list, it pops by itself on touchdown
    /// (throttle at idle, below its safe speed), cuts away when you've slowed down, and repacks itself for the next flight.
    /// Off by default per part; switch it on in the VAB/SPH. Also action groups: deploy / cut / repack.
    /// </summary>
    public class ModuleELELandingChute : PartModule
    {
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Landing chute"),
         UI_Toggle(enabledText = "On (auto, not staged)", disabledText = "Off (stock staging)", scene = UI_Scene.All)]
        public bool landingChute = false;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Deploy below", guiUnits = " m/s"),
         UI_FloatRange(minValue = 30, maxValue = 150, stepIncrement = 5, scene = UI_Scene.All)]
        public float deployBelow = 110;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Cut away at", guiUnits = " m/s"),
         UI_FloatRange(minValue = 0, maxValue = 30, stepIncrement = 1, scene = UI_Scene.All)]
        public float cutBelow = 8;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Auto repack"),
         UI_Toggle(enabledText = "Yes", disabledText = "No", scene = UI_Scene.All)]
        public bool autoRepack = true;

        ModuleParachute chute;
        float airborneUntil;   // touchdown counts only shortly after being airborne
        float cutAt = -1;
        bool applied;
        float stockAlt, stockPressure;

        public override void OnStart(StartState state)
        {
            chute = part.FindModuleImplementing<ModuleParachute>();
            if (chute == null) { enabled = false; return; }
            stockAlt = chute.deployAltitude; stockPressure = chute.minAirPressureToOpen;
            ApplyStaging();
        }

        void ApplyStaging()
        {
            if (chute == null || applied == landingChute && chute.stagingEnabled == !landingChute) return;
            applied = landingChute;
            if (chute.stagingEnabled == landingChute) { chute.stagingEnabled = !landingChute; part.UpdateStageability(true, true); }
            if (landingChute) { chute.deployAltitude = 50000; chute.minAirPressureToOpen = 0.01f; }   // full canopy straight away on the ground
            else { chute.deployAltitude = stockAlt; chute.minAirPressureToOpen = stockPressure; }
        }

        void Update()
        {
            if (HighLogic.LoadedSceneIsEditor || HighLogic.LoadedSceneIsFlight) ApplyStaging();
        }

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || chute == null || !landingChute || vessel == null || vessel.packed) return;
            if (vessel.situation == Vessel.Situations.FLYING) airborneUntil = Time.time + 8f;

            var st = chute.deploymentState;
            bool landed = vessel.LandedOrSplashed;
            if (st == ModuleParachute.deploymentStates.STOWED && landed && Time.time < airborneUntil
                && vessel.srfSpeed > cutBelow + 5 && vessel.srfSpeed < deployBelow && vessel.ctrlState.mainThrottle < 0.2f)
            {
                chute.Deploy();
                ScreenMessages.PostScreenMessage("Brake chute", 1.5f, ScreenMessageStyle.UPPER_CENTER);
            }
            else if ((st == ModuleParachute.deploymentStates.DEPLOYED || st == ModuleParachute.deploymentStates.SEMIDEPLOYED || st == ModuleParachute.deploymentStates.ACTIVE)
                     && landed && vessel.srfSpeed < cutBelow)
            {
                chute.CutParachute();
                cutAt = Time.time;
            }
            else if (st == ModuleParachute.deploymentStates.CUT && autoRepack && cutAt > 0 && Time.time - cutAt > 3f)
            {
                Repack(); cutAt = -1;
            }
        }

        [KSPAction("Brake chute: deploy")] public void ActDeploy(KSPActionParam p) { if (chute != null) chute.Deploy(); }
        [KSPAction("Brake chute: cut")] public void ActCut(KSPActionParam p) { if (chute != null) { chute.CutParachute(); cutAt = Time.time; } }
        [KSPAction("Brake chute: repack")] public void ActRepack(KSPActionParam p) => Repack();

        /// <summary>Stock repack without the engineer on EVA (it's a ground-crew job here).</summary>
        void Repack()
        {
            if (chute == null || chute.deploymentState != ModuleParachute.deploymentStates.CUT) return;
            var cap = Traverse.Create(chute).Field("cap").GetValue<Transform>();
            var canopy = Traverse.Create(chute).Field("canopy").GetValue<Transform>();
            float baseDrag = Traverse.Create(chute).Field("baseDrag").GetValue<float>();
            if (cap != null) cap.gameObject.SetActive(true);
            if (canopy != null) canopy.gameObject.SetActive(false);
            chute.deploymentState = ModuleParachute.deploymentStates.STOWED;
            chute.persistentState = "STOWED";
            part.maximum_drag = baseDrag + chute.stowedDrag;
            Traverse.Create(chute).Method("SetDragCubes_Stowed").GetValue();
            part.stackIcon?.SetIconColor(Color.white);
            chute.Events["Deploy"].active = true;
            chute.Events["CutParachute"].active = false;
            chute.Events["Repack"].active = false;
            chute.Events["Disarm"].active = false;
        }

        public override string GetInfo() => "ELE: can be set as a landing brake chute (auto deploy on touchdown, not staged, auto repack).";
    }
}
