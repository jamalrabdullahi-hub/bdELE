using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Per-axis model learned in flight by recursive least squares:
    ///   angular acceleration = (bq * q + b0) * u_eff + a * rate + c
    /// q = dynamic pressure (kPa), u_eff = control input after the surfaces' lag, rate = body rate (rad/s).
    /// (bq*q + b0) is the control power: aerodynamic surfaces scale with q, reaction wheels / gimbals don't.
    /// </summary>
    public class AxisModel
    {
        public readonly double[] theta = [0.3, 0.5, -1.0, 0.0];
        readonly double[,] P = new double[4, 4];
        public int samples;
        const double Lambda = 0.995; // forgetting: ~200 samples (4 s) memory

        public AxisModel() { ResetCovariance(10); }

        void ResetCovariance(double v) { for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) P[i, j] = i == j ? v : 0; }

        public double Power(double q) => theta[0] * q + theta[1];

        public void Update(double[] phi, double y)
        {
            var Pphi = new double[4];
            double denom = Lambda;
            for (int i = 0; i < 4; i++) { double s = 0; for (int j = 0; j < 4; j++) s += P[i, j] * phi[j]; Pphi[i] = s; denom += phi[i] * s; }
            double err = y;
            for (int i = 0; i < 4; i++) err -= theta[i] * phi[i];
            for (int i = 0; i < 4; i++) theta[i] += Pphi[i] / denom * err;
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) P[i, j] = (P[i, j] - Pphi[i] * Pphi[j] / denom) / Lambda;
            double trace = P[0, 0] + P[1, 1] + P[2, 2] + P[3, 3];
            if (trace > 1e5 || double.IsNaN(trace)) ResetCovariance(10); // covariance wind-up guard
            samples++;
        }

        public string Serialize()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return string.Join(",", Array.ConvertAll(theta, t => t.ToString("G6", ci))) + "," + samples;
        }

        public void Deserialize(string s)
        {
            var p = (s ?? "").Split(',');
            if (p.Length < 5) return;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < 4; i++) double.TryParse(p[i], System.Globalization.NumberStyles.Float, ci, out theta[i]);
            int.TryParse(p[4], out samples);
            ResetCovariance(1); // trusted prior: adapt, don't relearn from scratch
        }
    }

    /// <summary>
    /// ELE flight control system for BDArmory's pilot AI (per craft, "ELE flight control" in the AI part's menu).
    ///
    /// Replaces the last step of the AI (angle error -> stick, a fixed-gain PID) with:
    ///  1. Outer loop: angle error -> desired body rate along a braking curve (rate falls off as sqrt(2 * accel * error)),
    ///     so the craft eases into its target instead of overshooting; the rate command is slew-limited (smooth commands).
    ///  2. Inner loop: desired rate -> stick by inverting the learned model (nonlinear dynamic inversion), including a lead
    ///     for control-surface lag. Every aircraft gets its own model, learned continuously and saved per craft.
    ///  3. Coordinated turns: outside gun-aiming, yaw zeroes sideslip instead of chasing a yaw angle.
    ///  4. Energy autothrottle: throttle from the acceleration actually needed (incl. climb and learned drag), slew-limited.
    ///  5. Formation / refuelling: in the wingman zone, pitch and roll follow the leader's rates plus a position law,
    ///     and the throttle follows the leader's acceleration (feed-forward) instead of reacting late.
    /// Until each axis has learned enough, output blends from BDArmory's stock control, so takeover is seamless.
    /// </summary>
    public class ModuleELEFlightControl : PartModule
    {
        const string Group = "eleFCS";

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "ELE flight control", groupName = Group, groupDisplayName = "ELE flight control", groupStartCollapsed = true),
         UI_Toggle(enabledText = "On", disabledText = "Off (BDArmory stock)", scene = UI_Scene.All)]
        public bool fcsEnabled = false;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Feel (smooth - aggressive)", groupName = Group),
         UI_FloatRange(minValue = 0f, maxValue = 1f, stepIncrement = 0.05f, scene = UI_Scene.All)]
        public float feel = 0.5f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Energy autothrottle", groupName = Group),
         UI_Toggle(enabledText = "On", disabledText = "Off", scene = UI_Scene.All)]
        public bool autothrottle = true;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Coordinated turns", groupName = Group),
         UI_Toggle(enabledText = "On", disabledText = "Off", scene = UI_Scene.All)]
        public bool coordinated = true;

        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "Learned", groupName = Group)]
        public string learned = "-";

        [KSPField(isPersistant = true)] public string modelPitch = "", modelYaw = "", modelRoll = "";

        public readonly AxisModel[] models = [new(), new(), new()]; // pitch, yaw, roll
        public bool calibrating;
        public int calibAxis = -1; public float calibValue;
        int savedSamples;

        string CraftFile => Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmoryAvionics/PluginData/FCS", SafeName(vessel != null ? vessel.GetName() : part.partInfo.name) + ".cfg");
        static string SafeName(string n) { foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_'); return n; }

        public override void OnStart(StartState state)
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (string.IsNullOrEmpty(modelPitch)) LoadCraftFile();
            models[0].Deserialize(modelPitch); models[1].Deserialize(modelYaw); models[2].Deserialize(modelRoll);
            savedSamples = models[0].samples + models[1].samples + models[2].samples;
        }

        public override void OnSave(ConfigNode node)
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            modelPitch = models[0].Serialize(); modelYaw = models[1].Serialize(); modelRoll = models[2].Serialize();
            node.SetValue("modelPitch", modelPitch, true); node.SetValue("modelYaw", modelYaw, true); node.SetValue("modelRoll", modelRoll, true);
            SaveCraftFile();
        }

        void LoadCraftFile()
        {
            try
            {
                if (!File.Exists(CraftFile)) return;
                var n = ConfigNode.Load(CraftFile)?.GetNode("FCS");
                if (n == null) return;
                modelPitch = n.GetValue("pitch"); modelYaw = n.GetValue("yaw"); modelRoll = n.GetValue("roll");
                Debug.Log($"[BDArmoryAvionics]: FCS: loaded learned model for {vessel.GetName()}");
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: FCS model load failed: {e.Message}"); }
        }

        public void SaveCraftFile()
        {
            int s = models[0].samples + models[1].samples + models[2].samples;
            if (s <= savedSamples) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CraftFile));
                var root = new ConfigNode(); var n = root.AddNode("FCS");
                n.AddValue("pitch", models[0].Serialize()); n.AddValue("yaw", models[1].Serialize()); n.AddValue("roll", models[2].Serialize());
                root.Save(CraftFile);
                savedSamples = s;
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: FCS model save failed: {e.Message}"); }
        }

        void OnDestroy() { if (HighLogic.LoadedSceneIsFlight && fcsEnabled) SaveCraftFile(); }

        [KSPEvent(guiActive = true, guiName = "Calibrate (doublets)", groupName = Group)]
        public void CalibrateEvent()
        {
            if (vessel == null || vessel.LandedOrSplashed || vessel.radarAltitude < 400) { ScreenMessages.PostScreenMessage("FCS calibration: be airborne above 400 m", 3f, ScreenMessageStyle.UPPER_CENTER); return; }
            StartCoroutine(Calibrate());
        }

        [KSPEvent(guiActive = true, guiName = "Forget learned model", groupName = Group)]
        public void ResetEvent()
        {
            for (int i = 0; i < 3; i++) models[i] = new AxisModel();
            modelPitch = modelYaw = modelRoll = ""; savedSamples = 0;
            try { if (File.Exists(CraftFile)) File.Delete(CraftFile); } catch { }
            ScreenMessages.PostScreenMessage("FCS: learned model cleared", 2f, ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>Short pitch, roll and yaw doublets so every axis gets well identified quickly.</summary>
        IEnumerator Calibrate()
        {
            calibrating = true;
            ScreenMessages.PostScreenMessage("FCS calibration: doublets on pitch, roll, yaw (9 s)", 3f, ScreenMessageStyle.UPPER_CENTER);
            foreach (int axis in new[] { 0, 2, 1 })
            {
                calibAxis = axis;
                calibValue = 0.35f; yield return new WaitForSeconds(0.6f);
                calibValue = -0.35f; yield return new WaitForSeconds(0.6f);
                calibAxis = -1; yield return new WaitForSeconds(1.8f);
            }
            calibrating = false; calibAxis = -1;
            Debug.Log($"[BDArmoryAvionics]: FCS calibration done on {vessel.GetName()}: {learned}");
        }
    }

    public static class FlightControl
    {
        class State
        {
            public ModuleELEFlightControl m;
            public readonly float[] uEff = new float[3], uCmd = new float[3], rateFilt = new float[3], ratePrev = new float[3], rDes = new float[3], uDesPrev = new float[3];
            public float uEffPrev0, uEffPrev1, uEffPrev2;
            public double qPrev;
            public bool havePrev;
            public float thr = -1, dragEst, accFilt;
            public float nextLog;
            public double rmsErr;
            public bool seeded; // starting model predicted from the airframe (AeroPredict)
        }

        static readonly Dictionary<BDModulePilotAI, State> states = new();
        static readonly AccessTools.FieldRef<BDModulePilotAI, float> finalMaxSteer = AccessTools.FieldRefAccess<BDModulePilotAI, float>("finalMaxSteer");
        static readonly FieldInfo steerModeField = AccessTools.Field(typeof(BDModulePilotAI), "steerMode");
        static readonly Action<BDGenericAIBase, FlightCtrlState, float, float, float> setState =
            AccessTools.MethodDelegate<Action<BDGenericAIBase, FlightCtrlState, float, float, float>>(AccessTools.Method(typeof(BDGenericAIBase), "SetFlightControlState"));
        static readonly Dictionary<Vessel, (float accel, int frame)> accelDemand = new();

        /// <summary>Formation / refuelling: request this along-track acceleration for the autothrottle this frame.</summary>
        public static void DemandAcceleration(Vessel v, float accel) { if (v != null) accelDemand[v] = (accel, Time.frameCount); }

        static State For(BDModulePilotAI ai)
        {
            if (ai == null) return null;
            if (!states.TryGetValue(ai, out var st))
            {
                var m = ai.part != null ? ai.part.FindModuleImplementing<ModuleELEFlightControl>() : null;
                st = new State { m = m };
                states[ai] = st;
            }
            return st.m != null && st.m.fcsEnabled ? st : null;
        }

        public static bool Enabled(Vessel v)
        {
            var ai = v != null ? v.ActiveController().PilotAI : null;
            return ai != null && For(ai) != null;
        }

        /// <summary>Replaces BDArmory's SetFlightControlState(s, pitch, yaw, roll) at the end of FlyToPosition (errors in degrees).</summary>
        public static void Steer(BDGenericAIBase baseAi, FlightCtrlState s, float pitch, float yaw, float roll, float pitchErr, float yawErr, float rollErr)
        {
            var ai = baseAi as BDModulePilotAI;
            State st = null;
            try { st = For(ai); } catch { }
            if (st == null) { setState(baseAi, s, pitch, yaw, roll); return; }
            try { Control(ai, st, s, pitch, yaw, roll, pitchErr, yawErr, rollErr); }
            catch (Exception e)
            {
                Debug.LogWarning($"[BDArmoryAvionics]: FCS error, falling back to stock: {e}");
                st.m.fcsEnabled = false;
                setState(baseAi, s, pitch, yaw, roll);
            }
        }

        static void Control(BDModulePilotAI ai, State st, FlightCtrlState s, float pitch, float yaw, float roll, float pitchErr, float yawErr, float rollErr)
        {
            var m = st.m; var v = ai.vessel;
            float dt = Time.fixedDeltaTime, f = Mathf.Clamp01(m.feel);
            double q = Math.Max(0.0, v.dynamicPressurekPa);
            float V = (float)v.srfSpeed;
            Vector3 w = v.angularVelocity;
            float[] rate = [-w.x, -w.z, -w.y]; // pitch up, yaw right, roll right (rad/s)
            float[] stock = [pitch, yaw, roll];

            // ---- Starting model from the airframe (KSP's own lift maths), for axes not learned yet ----
            if (!st.seeded && !v.packed)
            {
                st.seeded = true;
                var (perK, cst) = AeroPredict.ControlPower(v);
                for (int i = 0; i < 3; i++)
                {
                    var mod = m.models[i];
                    if (mod.samples >= 100) continue;
                    mod.theta[0] = perK[i]; mod.theta[1] = cst[i]; mod.theta[2] = -1.5; mod.theta[3] = 0;
                }
                Debug.Log($"[BDArmoryAvionics]: FCS {v.GetName()}: predicted control power per kPa p/y/r {perK[0]:F2}/{perK[1]:F2}/{perK[2]:F2}, wheels {cst[0]:F2}/{cst[1]:F2}/{cst[2]:F2} rad/s²");
            }

            // ---- Identification (what last frame's input did) ----
            bool learn = !v.LandedOrSplashed && q > 0.2 && st.havePrev && !v.packed;
            for (int i = 0; i < 3; i++)
            {
                st.rateFilt[i] = Mathf.Lerp(st.rateFilt[i], rate[i], 0.6f);
                if (learn)
                {
                    float alpha = (st.rateFilt[i] - st.ratePrev[i]) / dt;
                    float uE = i == 0 ? st.uEffPrev0 : i == 1 ? st.uEffPrev1 : st.uEffPrev2;
                    bool excited = Mathf.Abs(uE) > 0.02f || Mathf.Abs(st.ratePrev[i]) > 0.02f;
                    if (excited && Mathf.Abs(alpha) < 40f)
                        m.models[i].Update([uE * st.qPrev, uE, st.ratePrev[i], 1.0], alpha);
                }
            }

            // ---- Actuator lag model: the surfaces reach the command with ~0.1 s lag ----
            const float Tau = 0.1f;
            float lagK = Mathf.Min(1f, dt / Tau);
            for (int i = 0; i < 3; i++) st.uEff[i] += (st.uCmd[i] - st.uEff[i]) * lagK;

            // ---- Errors (radians); coordinated yaw outside gun aiming ----
            float[] err = [pitchErr * Mathf.Deg2Rad, yawErr * Mathf.Deg2Rad, rollErr * Mathf.Deg2Rad];
            bool aiming = steerModeField != null && steerModeField.GetValue(ai)?.ToString() == "Aiming";
            if (m.coordinated && !aiming && V > 40)
            {
                var rt = v.ReferenceTransform;
                Vector3 vel = v.srf_velocity;
                float beta = Mathf.Atan2(Vector3.Dot(vel, rt.right), Vector3.Dot(vel, rt.up)); // + = sliding right
                err[1] = Mathf.Lerp(err[1], beta, 0.8f);
            }

            // ---- Formation / refuelling precision law (wingman zone) ----
            float[] rFF = new float[3]; bool[] overrideRate = new bool[3];
            if (FormationAssist.Wingman(ai, out Vector3 slot, out Vessel leader, out float dist) && dist < FormationAssist.WingmanRange)
            {
                var lw = leader.angularVelocity;
                Vector3 lVel = leader.Velocity();
                Vector3 lFwd = lVel.sqrMagnitude > 1 ? lVel.normalized : leader.ReferenceTransform.up;
                Vector3 lUp = Vector3.ProjectOnPlane(-leader.ReferenceTransform.forward, lFwd).normalized;
                float ev = Vector3.Dot(slot - (Vector3)v.CoM, lUp);
                float vRel = Vector3.Dot(lVel - (Vector3)v.Velocity(), lUp);
                float an = Mathf.Clamp(0.35f * ev + 1.1f * vRel, -25f, 25f);           // normal accel to fix height
                float blend = Mathf.Clamp01((FormationAssist.WingmanRange - dist) / (FormationAssist.WingmanRange * 0.5f));
                rFF[0] = (-lw.x + an / Mathf.Max(V, 40f)) * blend; overrideRate[0] = blend > 0.5f; // follow the leader's pitch rate
                rFF[2] = -lw.y * blend;                                                              // and its roll rate
            }

            // ---- Outer loop: braking curve + slew-limited rate command; inner loop: dynamic inversion ----
            float[] kp = [1.5f + 2f * f, 1.0f + 1f * f, 2.0f + 3f * f];
            float[] rMax = [Mathf.Clamp(ai.maxAllowedGForce * 9.81f / Mathf.Max(V, 30f), 0.2f, 2.5f), 0.6f, 1.5f + 2.5f * f];
            float kr = 4f + 6f * f;
            float slew = (3f + 7f * f) * dt;
            float maxSteer = Mathf.Clamp(finalMaxSteer(ai), 0.1f, 1f);
            float[] outU = new float[3];
            double rms = 0;
            for (int i = 0; i < 3; i++)
            {
                var mod = m.models[i];
                double B = mod.Power(q);
                float conf = mod.samples < 100 || B < 0.02 ? 0f : Mathf.Clamp01((mod.samples - 100) / 300f);
                if (st.seeded && B >= 0.02) conf = Mathf.Max(conf, 0.6f); // predicted model: fly on it from the start, learning refines it
                if (m.calibrating || v.LandedOrSplashed || q < 0.2) conf = 0; // on the ground / no air: stock steering
                float aMax = (float)Math.Max(0.05, Math.Abs(B) * (0.5 + 0.3 * f));

                float rCmd;
                if (overrideRate[i]) rCmd = Mathf.Clamp(rFF[i], -rMax[i], rMax[i]);
                else
                {
                    float e = err[i];
                    float shaped = Mathf.Min(kp[i] * Mathf.Abs(e), Mathf.Sqrt(2f * aMax * 0.8f * Mathf.Abs(e)));
                    rCmd = Mathf.Clamp(Mathf.Sign(e) * shaped + rFF[i], -rMax[i], rMax[i]);
                }
                // Smooth commands: the rate command itself can't jump faster than the craft can accelerate.
                st.rDes[i] = Mathf.MoveTowards(st.rDes[i], rCmd, aMax * dt * 1.5f);

                float aDes = kr * (st.rDes[i] - rate[i]);
                float uDes = B > 0.02 ? (float)((aDes - mod.theta[2] * rate[i] - mod.theta[3]) / B) : stock[i];
                uDes = Mathf.Clamp(uDes, -1f, 1f);
                // Lead for surface lag: drive the commanded deflection so the lagged one reaches uDes.
                float u = st.uEff[i] + (uDes - st.uEff[i]) * Mathf.Min(1.6f, 1f + 0.6f * (1f - lagK));
                float lim = i == 2 ? 1f : maxSteer;
                u = Mathf.Clamp(u, -lim, lim);
                u = Mathf.Lerp(stock[i], u, conf);
                if (m.calibrating && m.calibAxis == i) u = m.calibValue;
                u = Mathf.MoveTowards(st.uCmd[i], u, slew);
                outU[i] = u;
                rms += (st.rDes[i] - rate[i]) * (st.rDes[i] - rate[i]);
            }
            st.rmsErr = st.rmsErr * 0.98 + Math.Sqrt(rms) * 0.02;

            setState(ai, s, outU[0], outU[1], outU[2]);

            // ---- Remember for next frame's identification ----
            for (int i = 0; i < 3; i++) { st.uCmd[i] = outU[i]; st.ratePrev[i] = st.rateFilt[i]; }
            st.uEffPrev0 = st.uEff[0]; st.uEffPrev1 = st.uEff[1]; st.uEffPrev2 = st.uEff[2];
            st.qPrev = q; st.havePrev = true;

            if (Time.time >= st.nextLog)
            {
                st.nextLog = Time.time + 2f;
                int c(int i) => Mathf.RoundToInt(100 * Mathf.Clamp01((m.models[i].samples - 100) / 300f));
                m.learned = $"P {m.models[0].Power(q):F1} Y {m.models[1].Power(q):F1} R {m.models[2].Power(q):F1} rad/s² ({c(0)}/{c(1)}/{c(2)}%)";
                if (AvionicsSettings.AirOpsTelemetry)
                    Debug.Log($"[BDArmoryAvionics]: FCS {v.GetName()}: q {q:F1} kPa V {V:F0}, power p/y/r {m.models[0].Power(q):F2}/{m.models[1].Power(q):F2}/{m.models[2].Power(q):F2}, damping {m.models[0].theta[2]:F2}/{m.models[1].theta[2]:F2}/{m.models[2].theta[2]:F2}, " +
                              $"confidence {c(0)}/{c(1)}/{c(2)}%, rate err rms {st.rmsErr:F3} rad/s, stick {outU[0]:F2}/{outU[1]:F2}/{outU[2]:F2}{(aiming ? " AIMING" : "")}");
            }
        }

        /// <summary>Energy autothrottle (after BDArmory's airspeed control, which keeps afterburner and airbrake decisions).</summary>
        public static void Throttle(BDAirspeedControl sc, FlightCtrlState s)
        {
            var v = sc.vessel;
            var ai = v != null ? v.ActiveController().PilotAI : null;
            var st = For(ai);
            if (st == null || !st.m.autothrottle || sc.throttleOverride >= 0 || sc.targetSpeed <= 0) { if (st != null) st.thr = -1; return; }
            float f = Mathf.Clamp01(st.m.feel), dt = Time.fixedDeltaTime;
            Vector3 vel = v.srf_velocity; float V = vel.magnitude;
            if (V < 20) { st.thr = s.mainThrottle; return; }
            Vector3 dir = vel / V;
            float g = (float)FlightGlobals.getGeeForceAtPosition(v.CoM).magnitude;
            Vector3 up = (v.CoM - v.mainBody.position).normalized;
            float sinGamma = Vector3.Dot(dir, up);
            st.accFilt = Mathf.Lerp(st.accFilt, Vector3.Dot(v.acceleration_immediate, dir), 0.08f);

            float maxThrust = 0, thrust = 0;
            foreach (var e in VesselModuleRegistry.GetModuleEngines(v))
            {
                if (e == null || !e.EngineIgnited) continue;
                float t = e.maxThrust * (e.atmChangeFlow ? e.flowMultiplier : 1f) * e.thrustPercentage / 100f;
                maxThrust += Mathf.Max(0, t); thrust += e.finalThrust;
            }
            if (maxThrust <= 0) return;
            float mass = v.GetTotalMass();
            // Learned drag: what the engines push minus what we actually gain (along the path, net of gravity).
            float dragNow = thrust - mass * (st.accFilt + g * sinGamma);
            st.dragEst = Mathf.Lerp(st.dragEst, Mathf.Max(0, dragNow), 0.02f);

            float aCmd = accelDemand.TryGetValue(v, out var d) && d.frame == Time.frameCount
                ? d.accel
                : Mathf.Clamp((sc.targetSpeed - V) * (0.3f + 0.4f * f), -15f, 15f);
            float want = (mass * (aCmd + g * sinGamma) + st.dragEst) / maxThrust;
            if (st.thr < 0) st.thr = s.mainThrottle;
            st.thr = Mathf.MoveTowards(st.thr, Mathf.Clamp01(want), (0.8f + 2.2f * f) * dt);
            sc.SetThrottle(s, st.thr);
        }

        public static void Clear() => states.Clear();
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class FlightControlLifecycle : MonoBehaviour
    {
        void Awake() => FlightControl.Clear();
    }

    /// <summary>Route FlyToPosition's final SetFlightControlState through the ELE FCS, passing BDArmory's pitch/yaw/roll errors along.</summary>
    [HarmonyPatch(typeof(BDModulePilotAI), "FlyToPosition")]
    static class Patch_FCS_FlyToPosition
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var setFCS = AccessTools.Method(typeof(BDGenericAIBase), "SetFlightControlState");
            var tuneType = AccessTools.TypeByName("BDArmory.Control.PIDAutoTuning") ?? AccessTools.TypeByName("PIDAutoTuning");
            var tuneUpdate = tuneType != null ? AccessTools.Method(tuneType, "Update") : null;
            var steer = AccessTools.Method(typeof(FlightControl), nameof(FlightControl.Steer));
            int call = list.FindIndex(i => (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo mi && mi == setFCS);
            int tune = tuneUpdate == null ? -1 : list.FindIndex(i => (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo mi && mi == tuneUpdate);
            // pidAutoTuning.Update(pitchError, rollError, yawError): the three loads before the call are those locals.
            if (call < 0 || tune < 3 || !list[tune - 1].IsLdloc() || !list[tune - 2].IsLdloc() || !list[tune - 3].IsLdloc())
            {
                Debug.LogWarning("[BDArmoryAvionics]: FCS hook not installed (pilot AI steering not recognised); ELE flight control unavailable");
                return list;
            }
            var pitchL = list[tune - 3]; var rollL = list[tune - 2]; var yawL = list[tune - 1];
            list[call] = new CodeInstruction(OpCodes.Call, steer).WithLabels(list[call].labels).WithBlocks(list[call].blocks);
            list.InsertRange(call, new[] { new CodeInstruction(pitchL.opcode, pitchL.operand), new CodeInstruction(yawL.opcode, yawL.operand), new CodeInstruction(rollL.opcode, rollL.operand) });
            Debug.Log("[BDArmoryAvionics]: FCS hook installed");
            return list;
        }
    }

    [HarmonyPatch(typeof(BDAirspeedControl), "AirspeedControl")]
    static class Patch_FCS_Throttle
    {
        static void Postfix(BDAirspeedControl __instance, FlightCtrlState s)
        {
            try { FlightControl.Throttle(__instance, s); } catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: FCS throttle: {e.Message}"); }
        }
    }
}
