using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// ELE main rotor. The part is a ModuleEnginesFX (thrust up through the rotor disc, ModuleGimbal for cyclic) that
    /// this module drives:
    ///   - lift is limited by the turboshaft power on the vessel (lose an engine, lose lift);
    ///   - the rotor spins visibly with throttle;
    ///   - without a working tail rotor its torque spins the airframe.
    /// The VTOL flight computer treats it as a helicopter rotor.
    /// </summary>
    public class ModuleHeliRotor : PartModule
    {
        [KSPField] public string spinTransformName = "rotorSpin";
        [KSPField] public float requiredKW = 2600f;     // turboshaft power for full lift
        [KSPField] public float maxRPM = 290f;
        [KSPField] public float idleRPM = 80f;
        [KSPField] public float torqueArm = 0.25f;      // reaction torque per kN of thrust (kN·m)
        [KSPField(guiActive = true, guiName = "Rotor")] public string status = "";

        Transform spin; ModuleEngines engine; float baseThrust, baseFlow, rpm, applied = -1;

        public override void OnStart(StartState state)
        {
            spin = part.FindModelTransform(spinTransformName);
            engine = part.FindModuleImplementing<ModuleEngines>();
            if (engine != null) { baseThrust = engine.maxThrust; baseFlow = engine.maxFuelFlow; }
        }

        public override string GetInfo() => $"Needs {requiredKW:F0} kW of turboshaft power for full lift.\nWithout a working tail rotor the airframe spins.";

        public float PowerKW()
        {
            float p = 0;
            foreach (var t in vessel.FindPartModulesImplementing<ModuleTurboshaft>()) p += t.Available();
            return p;
        }

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || engine == null) return;
            float ratio = Mathf.Clamp01(PowerKW() / requiredKW);
            if (Mathf.Abs(ratio - applied) > 0.01f)
            {
                applied = ratio;
                engine.maxThrust = Mathf.Max(0.001f, baseThrust * ratio);
                engine.maxFuelFlow = Mathf.Max(1e-7f, baseFlow * ratio);
            }
            bool on = engine.EngineIgnited && !engine.flameout && ratio > 0;
            float target = on ? Mathf.Lerp(idleRPM, maxRPM, engine.currentThrottle) : 0;
            rpm = Mathf.MoveTowards(rpm, target, TimeWarp.fixedDeltaTime * (on ? 40f : 15f));
            float thrust = engine.finalThrust;
            if (thrust > 0.1f && part.Rigidbody != null && !TailRotorWorking())
                part.Rigidbody.AddTorque(part.transform.up * thrust * torqueArm, ForceMode.Force);
            float bend = DynamicDamage.Bend(part); // bent blades: once-per-rev shake that grows with rpm
            if (bend > 0 && rpm > 30 && part.Rigidbody != null)
            {
                float a = Time.time * rpm / 60f * Mathf.PI * 2;
                part.Rigidbody.AddForce((part.transform.right * Mathf.Cos(a) + part.transform.forward * Mathf.Sin(a)) * thrust * 0.15f * bend, ForceMode.Force);
            }
            status = on ? $"{rpm:F0} rpm, power {ratio * 100:F0}%{(TailRotorWorking() ? "" : ", NO TAIL ROTOR")}" : ratio <= 0 ? "no turboshaft power" : "stopped";
        }

        bool TailRotorWorking()
        {
            foreach (var t in vessel.FindPartModulesImplementing<ModuleTailRotor>()) if (t.isEnabled) return true;
            return false;
        }

        void Update() { if (spin != null && rpm > 0) spin.Rotate(Vector3.up, rpm * 6f * Time.deltaTime, Space.Self); }
    }

    /// <summary>Turboshaft engine pod: supplies power to the rotor while it has fuel and air; runs hot with rotor load.</summary>
    public class ModuleTurboshaft : PartModule
    {
        [KSPField] public float powerKW = 1300f;
        [KSPField] public float heatKW = 30f;
        [KSPField] public float minAtm = 0.15f;         // air density ratio below which it chokes
        [KSPField(guiActive = true, guiName = "Turboshaft")] public string status = "";

        public float Available()
        {
            if (vessel == null) return 0;
            float air = (float)(vessel.atmDensity / 1.225);
            return air < minAtm ? 0 : powerKW * Mathf.Clamp01(air * 1.2f);
        }

        public override string GetInfo() => $"Rotor power: {powerKW:F0} kW (less in thin air).";

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            float load = 0;
            foreach (var r in vessel.FindPartModulesImplementing<ModuleEngines>())
                if (r.part.FindModuleImplementing<ModuleHeliRotor>() != null && r.EngineIgnited) load = Mathf.Max(load, r.currentThrottle);
            if (load > 0) part.AddThermalFlux(heatKW * (0.3f + 0.7f * load));
            status = load > 0 ? $"{Available():F0} kW, load {load * 100:F0}%" : "idle";
        }
    }

    /// <summary>Tail rotor: spins with the main rotor and (via its reaction wheel) gives yaw control and anti-torque.</summary>
    public class ModuleTailRotor : PartModule
    {
        [KSPField] public string spinTransformName = "tailSpin";
        [KSPField] public float rpm = 1300f;
        Transform spin; float speed;

        public override void OnStart(StartState state) => spin = part.FindModelTransform(spinTransformName);

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight || spin == null) return;
            bool on = false;
            foreach (var r in vessel.FindPartModulesImplementing<ModuleHeliRotor>())
            {
                var e = r.part.FindModuleImplementing<ModuleEngines>();
                if (e != null && e.EngineIgnited) on = true;
            }
            speed = Mathf.MoveTowards(speed, on ? rpm : 0, Time.deltaTime * 300f);
            if (speed > 0) spin.Rotate(Vector3.up, speed * 6f * Time.deltaTime, Space.Self);
        }
    }
}
