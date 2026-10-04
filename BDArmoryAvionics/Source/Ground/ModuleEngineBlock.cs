using System.Collections.Generic;
using UnityEngine;
using ModuleWheels;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Liquid-fuel engine block for ground vehicles. While running it burns LiquidFuel to keep the vessel's
    /// ElectricCharge topped up (idle burn when nothing is drawing power) and drives every wheel motor on the
    /// vessel harder: wheel torque is multiplied by up to torqueBoost depending on engine power per wheel.
    /// Running engines also put out heat, so vehicles show up hot on thermal sights.
    /// </summary>
    public class ModuleEngineBlock : PartModule
    {
        [KSPField] public float maxPowerKW = 400f;       // shaft power
        [KSPField] public float ecPerKW = 0.1f;          // electric charge per second per kW
        [KSPField] public float fuelPerKWh = 0.15f;      // LiquidFuel units per kWh
        [KSPField] public float idleFraction = 0.08f;    // idle burn as a share of full power
        [KSPField] public float torqueBoost = 2f;        // wheel torque multiplier with enough power
        [KSPField] public float kwPerWheel = 60f;        // power one wheel needs for the full boost
        [KSPField] public float heatKW = 25f;            // heat at full load (thermal signature)

        [KSPField(isPersistant = true)] public bool running;
        [KSPField(guiActive = true, guiName = "Engine")] public string status = "Off";
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Power", guiUnits = " kW", guiFormat = "F0")] public float powerInfo;

        float load;
        int lfId, ecId;
        static readonly Dictionary<ModuleWheelMotor, FloatCurve> stockCurves = new();
        static readonly Dictionary<ModuleWheelMotor, float> applied = new();

        [KSPEvent(guiActive = true, guiName = "Start engine")]
        public void StartEngine() => SetRunning(true);

        [KSPEvent(guiActive = true, guiName = "Stop engine")]
        public void StopEngine() => SetRunning(false);

        [KSPAction("Start engine")] public void StartAction(KSPActionParam p) => SetRunning(true);
        [KSPAction("Stop engine")] public void StopAction(KSPActionParam p) => SetRunning(false);
        [KSPAction("Toggle engine")] public void ToggleAction(KSPActionParam p) => SetRunning(!running);

        void SetRunning(bool on)
        {
            running = on;
            Events[nameof(StartEngine)].active = !on; Events[nameof(StopEngine)].active = on;
            if (!on) { load = 0; status = "Off"; ApplyWheels(); }
        }

        public override void OnStart(StartState state)
        {
            lfId = PartResourceLibrary.Instance.GetDefinition("LiquidFuel").id;
            ecId = PartResourceLibrary.Instance.GetDefinition("ElectricCharge").id;
            powerInfo = maxPowerKW;
            SetRunning(running);
        }

        public override string GetInfo() =>
            $"Shaft power: {maxPowerKW:F0} kW\nElectric output: {maxPowerKW * ecPerKW:F0} EC/s\nFuel: {maxPowerKW * fuelPerKWh / 3600 * 60:F2} LF/min at full load\nWheel torque: up to x{torqueBoost:F1} ({kwPerWheel:F0} kW per wheel)";

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null) return;
            if (!running) return;
            float dt = TimeWarp.fixedDeltaTime;
            double maxEc = maxPowerKW * ecPerKW * dt;
            part.GetConnectedResourceTotals(ecId, out double amt, out double cap);
            double want = System.Math.Min(System.Math.Max(0, cap - amt), maxEc);
            load = Mathf.Max(idleFraction, (float)(want / maxEc));
            double fuel = load * maxPowerKW * fuelPerKWh / 3600 * dt;
            double got = part.RequestResource(lfId, fuel);
            if (fuel > 0 && got < fuel * 0.5)
            {
                ScreenMessages.PostScreenMessage($"{part.partInfo.title}: out of fuel", 4f, ScreenMessageStyle.UPPER_CENTER);
                SetRunning(false); return;
            }
            part.RequestResource(ecId, -want * (got / System.Math.Max(fuel, 1e-9)));
            part.AddThermalFlux(heatKW * load);
            status = $"Running {load * 100:F0}%";
            ApplyWheels();
        }

        /// <summary>The first running engine on the vessel sets the wheel torque for all of them.</summary>
        void ApplyWheels()
        {
            if (vessel == null) return;
            float power = 0; ModuleEngineBlock lead = null;
            foreach (var e in vessel.FindPartModulesImplementing<ModuleEngineBlock>())
                if (e.running) { power += e.maxPowerKW; if (lead == null) lead = e; }
            if (lead != null && lead != this) return;
            var motors = vessel.FindPartModulesImplementing<ModuleWheelMotor>();
            float mult = motors.Count == 0 || lead == null ? 1f : 1f + (torqueBoost - 1f) * Mathf.Clamp01(power / (motors.Count * kwPerWheel));
            foreach (var m in motors)
            {
                if (m.torqueCurve == null) continue;
                if (!stockCurves.TryGetValue(m, out var stock)) stockCurves[m] = stock = m.torqueCurve;
                if (applied.TryGetValue(m, out var was) && Mathf.Abs(was - mult) < 0.02f) continue;
                applied[m] = mult;
                if (mult <= 1.001f) { m.torqueCurve = stock; continue; }
                var c = new FloatCurve();
                foreach (var k in stock.Curve.keys) c.Add(k.time, k.value * mult, k.inTangent * mult, k.outTangent * mult);
                m.torqueCurve = c;
            }
        }

        void OnDestroy()
        {
            if (vessel == null) return;
            foreach (var m in vessel.FindPartModulesImplementing<ModuleWheelMotor>())
                if (stockCurves.TryGetValue(m, out var stock)) { m.torqueCurve = stock; stockCurves.Remove(m); applied.Remove(m); }
        }
    }
}
