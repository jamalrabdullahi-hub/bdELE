using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    public enum VTOLMode { Off, Hover, GoTo, Land, Takeoff, Cruise, Train }

    /// <summary>
    /// Learned response of one rotation axis: angular acceleration = a * rate + b * stick + c.
    /// b is how hard the stick turns the craft (control power), a its natural damping, c a trim bias.
    /// Recursive least squares with forgetting, so it keeps adapting (fuel burn, tilt, speed).
    /// </summary>
    public class AxisLearner
    {
        public double a = -1.0, b = 2.0, c = 0;
        readonly double[,] P = new double[3, 3];
        public int samples;
        const double Lambda = 0.997;

        public AxisLearner() { Reset(10); }
        void Reset(double v) { for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) P[i, j] = i == j ? v : 0; }

        public void Update(double rate, double u, double accel)
        {
            double[] phi = { rate, u, 1 };
            double[] theta = { a, b, c };
            var Pp = new double[3]; double den = Lambda;
            for (int i = 0; i < 3; i++) { double s = 0; for (int j = 0; j < 3; j++) s += P[i, j] * phi[j]; Pp[i] = s; den += phi[i] * s; }
            double err = accel - (theta[0] * phi[0] + theta[1] * phi[1] + theta[2]);
            for (int i = 0; i < 3; i++) theta[i] += Pp[i] / den * err;
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) P[i, j] = (P[i, j] - Pp[i] * Pp[j] / den) / Lambda;
            if (P[0, 0] + P[1, 1] + P[2, 2] > 1e5 || double.IsNaN(theta[1])) { Reset(10); return; }
            a = Math.Max(-20, Math.Min(2, theta[0])); b = theta[1]; c = Math.Max(-5, Math.Min(5, theta[2]));
            samples++;
        }

        /// <summary>0..1: how far learning has got (samples and a sane, positive control power).</summary>
        public float Confidence => b > 0.05 ? Mathf.Clamp01(samples / 600f) : 0;

        public string Save() => string.Join(",", new[] { a, b, c }.ConvertAll(x => x.ToString("G6", CultureInfo.InvariantCulture))) + "," + samples;
        public void Load(string s)
        {
            var p = (s ?? "").Split(',');
            if (p.Length < 4) return;
            double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a);
            double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b);
            double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c);
            int.TryParse(p[3], out samples);
            Reset(1); // trusted: adapt, don't relearn
        }
    }

    static class ArrayExt { public static TOut[] ConvertAll<TIn, TOut>(this TIn[] a, Converter<TIn, TOut> f) => Array.ConvertAll(a, f); }

    /// <summary>
    /// ELE VTOL flight computer for helicopters, jump jets and tiltrotors (one per craft, on a command part).
    ///
    /// Cascade: where to be -> velocity -> acceleration -> thrust direction / body attitude -> body rates -> stick, with the
    /// last step inverting a model of each axis learned in flight (and saved with the craft), so every design gets its own tuning.
    /// Tilt mechanisms on Infernal Robotics servos follow a hover-to-cruise schedule by airspeed; tiltrotors can also yaw with
    /// differential nacelle tilt. Engine aids: no spool lag, differential thrust for attitude, lift engines off in cruise, gyro.
    /// Directs: keys as velocity commands (W/S, Q/E, A/D, Shift/Ctrl), click the ground to go or land, C2/CSAR GoTo.
    /// Set up and tune in the VAB/SPH with ELE's VTOL designer.
    /// </summary>
    public class ModuleVTOLAssist : PartModule
    {
        const string Group = "vtolAssist";
        public static readonly string[] AGs = ["None", "Custom01", "Custom02", "Custom03", "Custom04", "Custom05", "Custom06", "Custom07", "Custom08", "Custom09", "Custom10", "Gear", "Brakes"];
        public static readonly string[] Types = { "Auto", "Helicopter", "Jump jet", "Tiltrotor" };

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "VTOL flight computer", groupName = Group, groupDisplayName = "ELE VTOL", groupStartCollapsed = true),
         UI_Toggle(enabledText = "Available", disabledText = "Off", scene = UI_Scene.All)]
        public bool available = false;

        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "VTOL", groupName = Group)]
        public string status = "off";

        // ---- Designer settings (VAB/SPH VTOL designer) ----
        [KSPField(isPersistant = true)] public string craftType = "Auto";
        [KSPField(isPersistant = true)] public float maxSpeed = 15f;          // hover manoeuvring speed
        [KSPField(isPersistant = true)] public float maxTilt = 15f;           // body tilt for manoeuvring
        [KSPField(isPersistant = true)] public float maxClimb = 6f;
        [KSPField(isPersistant = true)] public float responsiveness = 0.5f;   // 0 gentle .. 1 sharp
        [KSPField(isPersistant = true)] public float smoothness = 0.5f;       // 0 crisp .. 1 soft (rate damping / command smoothing)
        [KSPField(isPersistant = true)] public float takeoffAGL = 30f;
        [KSPField(isPersistant = true)] public float transitionStart = 25f;   // tilt starts here (m/s)
        [KSPField(isPersistant = true)] public float cruiseSpeed = 80f;       // fully converted here
        [KSPField(isPersistant = true)] public bool fastEngines = true;
        [KSPField(isPersistant = true)] public bool diffThrust = true;
        [KSPField(isPersistant = true)] public bool liftOffInCruise = true;
        [KSPField(isPersistant = true)] public float gyro = 0f;               // 0..1 stability gyro
        [KSPField(isPersistant = true)] public string hoverAG = "None";
        [KSPField(isPersistant = true)] public Vector3 upLocal = Vector3.zero;   // craft "up" in this part's frame (set in the editor; zero = guess)
        [KSPField(isPersistant = true)] public Vector3 noseLocal = Vector3.zero; // craft "nose" in this part's frame
        // ---- Learned (flight) ----
        [KSPField(isPersistant = true)] public string learnPitch = "", learnRoll = "", learnYaw = "";
        [KSPField(isPersistant = true)] public float hoverThrottle = -1f;
        // compatibility with older craft
        [KSPField(isPersistant = true)] public float attitudeGain = 1f, rateGain = 1f;

        public readonly AxisLearner[] learn = { new(), new(), new() }; // pitch, roll, yaw

        public VTOLMode mode = VTOLMode.Off;
        public double holdAlt;
        double holdLat, holdLon, gotoLat, gotoLon, gotoAGL;
        bool gotoLand;
        float heading, integral, tau, lastVs;
        bool aiWasOn;
        Vessel registered;
        float tilt, vsNow; Vector3 gs;
        public float ThrustUp { get; private set; }
        float cruiseAlt, cruiseSpeedCmd;
        Vector3 lastRate; float[] lastU = new float[3]; bool haveLast;

        public static ModuleVTOLAssist For(Vessel v)
        {
            if (v == null) return null;
            foreach (var p in v.parts) { var m = p.FindModuleImplementing<ModuleVTOLAssist>(); if (m != null && m.available) return m; }
            return null;
        }

        [KSPEvent(guiActive = true, guiName = "Hover hold", groupName = Group)] public void HoverEvent() => Engage(VTOLMode.Hover);
        [KSPEvent(guiActive = true, guiName = "Auto-land", groupName = Group)] public void LandEvent() => Engage(VTOLMode.Land);
        [KSPEvent(guiActive = true, guiName = "Flight computer off", groupName = Group)] public void OffEvent() => Disengage("off");
        [KSPAction("VTOL hover hold toggle")] public void HoverAction(KSPActionParam p) { if (mode == VTOLMode.Off) Engage(VTOLMode.Hover); else Disengage("off"); }
        [KSPAction("VTOL auto-land")] public void LandAction(KSPActionParam p) => Engage(VTOLMode.Land);
        [KSPAction("VTOL takeoff")] public void TakeoffAction(KSPActionParam p) => Engage(VTOLMode.Takeoff);
        [KSPAction("VTOL cruise")] public void CruiseAction(KSPActionParam p) => Engage(VTOLMode.Cruise);

        public override void OnStart(StartState state)
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            learn[0].Load(learnPitch); learn[1].Load(learnRoll); learn[2].Load(learnYaw);
        }

        void SaveLearning() { learnPitch = learn[0].Save(); learnRoll = learn[1].Save(); learnYaw = learn[2].Save(); }

        // ---- Craft geometry ----

        Vector3 Up => (vessel.CoM - vessel.mainBody.position).normalized;

        /// <summary>The craft's "up" (thrust side in hover) and "nose" in world space, from the designer (or a guess).</summary>
        public void BodyAxes(out Vector3 bodyUp, out Vector3 bodyNose)
        {
            var t = part.transform;
            if (upLocal != Vector3.zero) bodyUp = t.TransformDirection(upLocal).normalized;
            else
            {
                var (d, max) = Thrust(false);
                bodyUp = max > 0 ? d : -vessel.ReferenceTransform.forward;
            }
            if (noseLocal != Vector3.zero) bodyNose = t.TransformDirection(noseLocal).normalized;
            else
            {
                var rt = vessel.ReferenceTransform;
                bodyNose = Mathf.Abs(Vector3.Dot(rt.up, bodyUp)) < 0.7f ? rt.up : -rt.forward;
            }
            bodyNose = Vector3.ProjectOnPlane(bodyNose, bodyUp).normalized;
        }

        /// <summary>Editor: up and nose in world space from the stored settings (up defaults to world up).</summary>
        public void BodyAxesEditor(out Vector3 bodyUp, out Vector3 bodyNose)
        {
            var t = part.transform;
            bodyUp = upLocal != Vector3.zero ? t.TransformDirection(upLocal).normalized : Vector3.up;
            bodyNose = noseLocal != Vector3.zero ? t.TransformDirection(noseLocal) : Vector3.forward;
            bodyNose = Vector3.ProjectOnPlane(bodyNose, bodyUp);
            if (bodyNose.sqrMagnitude < 1e-4f) bodyNose = Vector3.ProjectOnPlane(Vector3.forward, bodyUp);
            bodyNose.Normalize();
        }

        public VTOLType Type
        {
            get
            {
                if (craftType == "Helicopter") return VTOLType.Helicopter;
                if (craftType == "Jump jet") return VTOLType.JumpJet;
                if (craftType == "Tiltrotor") return VTOLType.Tiltrotor;
                if (VTOLParts.TiltServos(vessel != null ? vessel.parts : part.ship?.parts).Count > 0) return VTOLType.Tiltrotor;
                return VTOLParts.HasRotors(vessel != null ? vessel.parts : part.ship?.parts) ? VTOLType.Helicopter : VTOLType.JumpJet;
            }
        }

        /// <summary>Thrust direction (unit) and max thrust of running engines (or of all engines, for the designer).</summary>
        (Vector3 dir, float max) Thrust(bool runningOnly = true)
        {
            Vector3 sum = Vector3.zero; float max = 0;
            foreach (var e in VesselModuleRegistry.GetModules<ModuleEngines>(vessel))
            {
                if (e == null || (runningOnly && (!e.EngineIgnited || e.flameout))) continue;
                if (e.part.FindModuleImplementing<BDArmory.Weapons.Missiles.MissileBase>() != null) continue;
                float t = e.GetMaxThrust() * e.thrustPercentage / 100f;
                if (t <= 0 || e.thrustTransforms.Count == 0) continue;
                Vector3 d = Vector3.zero;
                foreach (var tt in e.thrustTransforms) d -= tt.forward;
                sum += d.normalized * t; max += t;
            }
            return (sum.sqrMagnitude > 0 ? sum.normalized : Vector3.zero, max);
        }

        // ---- Engage / modes ----

        public void Engage(VTOLMode m)
        {
            if (vessel == null) return;
            if (mode == VTOLMode.Off)
            {
                var ai = vessel.ActiveController().PilotAI;
                aiWasOn = ai != null && ai.pilotEnabled;
                if (aiWasOn) ai.DeactivatePilot();
                holdAlt = vessel.altitude;
                SetHold(vessel.CoM);
                heading = Heading();
                integral = 0; haveLast = false;
                VTOLParts.ApplyEngineAids(this, true);
            }
            if (m == VTOLMode.Takeoff) holdAlt = vessel.altitude - vessel.radarAltitude + takeoffAGL;
            if (m == VTOLMode.Cruise) { cruiseAlt = (float)Math.Max(vessel.altitude, vessel.altitude - vessel.radarAltitude + 150); cruiseSpeedCmd = Mathf.Max(cruiseSpeed, (float)vessel.srfSpeed); }
            if (m == VTOLMode.Train) trainT0 = Time.time;
            mode = m;
            if (vessel.OnFlyByWire != null) vessel.OnFlyByWire -= Fly;
            vessel.OnFlyByWire += Fly;
            registered = vessel;
            ScreenMessages.PostScreenMessage($"VTOL: {m}", 2f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log($"[BDArmoryAvionics]: VTOL {vessel.GetName()} ({Type}): {m} at {vessel.altitude:F0} m");
        }

        public void Disengage(string why)
        {
            if (registered != null) registered.OnFlyByWire -= Fly;
            registered = null;
            if (mode != VTOLMode.Off)
            {
                SaveLearning();
                VTOLParts.ApplyEngineAids(this, false);
                Debug.Log($"[BDArmoryAvionics]: VTOL {vessel?.GetName()}: off ({why}); learned {LearnedText}");
                if (aiWasOn && vessel != null && !vessel.LandedOrSplashed) vessel.ActiveController().PilotAI?.ActivatePilot();
            }
            mode = VTOLMode.Off; status = why;
        }

        public string LearnedText => $"pitch {learn[0].Confidence:P0}, roll {learn[1].Confidence:P0}, yaw {learn[2].Confidence:P0}, hover throttle {(hoverThrottle > 0 ? hoverThrottle.ToString("P0") : "-")}";

        /// <summary>Fly (in hover) to a point; optionally land there.</summary>
        public void GoTo(double lat, double lon, double agl, bool land)
        {
            gotoLat = lat; gotoLon = lon; gotoAGL = agl; gotoLand = land;
            if (FireHoverAG()) StartCoroutine(GoToWhenHovering()); else Engage(VTOLMode.GoTo);
        }

        IEnumerator GoToWhenHovering()
        {
            status = "converting to hover";
            float t0 = Time.time;
            while (Time.time - t0 < 20f && Vector3.Dot(Thrust().dir, Up) < 0.8f) yield return new WaitForSeconds(0.25f);
            Engage(VTOLMode.GoTo);
        }

        bool FireHoverAG()
        {
            if (hoverAG == "None" || Vector3.Dot(Thrust().dir, Up) > 0.8f) return false;
            if (!Enum.TryParse(hoverAG, out KSPActionGroup g)) return false;
            vessel.ActionGroups.ToggleGroup(g);
            return true;
        }

        void SetHold(Vector3d world) { holdLat = vessel.mainBody.GetLatitude(world); holdLon = vessel.mainBody.GetLongitude(world); }

        float Heading()
        {
            BodyAxes(out _, out var nose);
            var up = Up; var north = Vector3.ProjectOnPlane(vessel.mainBody.transform.up, up).normalized;
            var n = Vector3.ProjectOnPlane(nose, up);
            return n.sqrMagnitude < 1e-4f ? 0 : Vector3.SignedAngle(north, n, up);
        }

        // ---- Training: automatic doublets on each axis while hovering ----
        float trainT0;
        string trainStep = "";
        float TrainSignal(out int axis)
        {
            float t = Time.time - trainT0;
            axis = -1;
            if (t < 3) { trainStep = "settling"; return 0; }
            t -= 3;
            int cycle = (int)(t / 4f); float ph = t % 4f;
            if (cycle >= 9) { trainStep = "done"; return 0; }
            axis = cycle % 3;
            trainStep = $"{(axis == 0 ? "pitch" : axis == 1 ? "roll" : "yaw")} {cycle / 3 + 1}/3";
            return ph < 0.5f ? 0.35f : ph < 1.0f ? -0.35f : 0; // a doublet, then let it settle
        }

        // ---- The control law ----

        void Fly(FlightCtrlState s)
        {
            if (mode == VTOLMode.Off || vessel == null || vessel.packed) return;
            float dt = Time.fixedDeltaTime;
            var rt = vessel.ReferenceTransform;
            Vector3 up = Up;
            BodyAxes(out Vector3 bodyUp, out Vector3 bodyNose);
            var (tdir, tmax) = Thrust();
            if (tmax <= 0) tdir = bodyUp; // rotorcraft: rotor thrust along the body's up
            ThrustUp = Vector3.Dot(tdir, up);
            Vector3 v = vessel.srf_velocity;
            vsNow = Vector3.Dot(v, up);
            gs = v - vsNow * up;
            var north = Vector3.ProjectOnPlane(vessel.mainBody.transform.up, up).normalized;
            var type = Type;
            bool canTilt = VTOLParts.TiltServos(vessel.parts).Count > 0;
            if (mode != VTOLMode.Cruise && !canTilt && tmax > 0 && Vector3.Dot(tdir, bodyUp) < 0.5f)
            { status = "thrust isn't pointing up: convert to hover (action group) first"; return; }

            // Learning: measure angular acceleration per axis against last frame's stick.
            Vector3 w = vessel.angularVelocity;
            Vector3 rate = new(-w.x, -w.y, -w.z); // pitch, roll, yaw rates (BDArmory sign convention)
            if (haveLast && !vessel.LandedOrSplashed)
            {
                Vector3 acc = (rate - lastRate) / dt;
                for (int i = 0; i < 3; i++) if (Mathf.Abs(acc[i]) < 50f) learn[i].Update(rate[i], lastU[i], acc[i]);
            }

            // Player directs
            bool player = vessel == FlightGlobals.ActiveVessel && (mode == VTOLMode.Hover || mode == VTOLMode.Cruise);
            float inFwd = player ? -s.pitch : 0, inSide = player ? s.roll : 0, inYaw = player ? s.yaw : 0;
            float climb = 0;
            if (player) { if (GameSettings.THROTTLE_UP.GetKey()) climb = 1; if (GameSettings.THROTTLE_DOWN.GetKey()) climb = -1; }

            float resp = Mathf.Lerp(0.5f, 1.6f, responsiveness);
            float soft = Mathf.Lerp(1.4f, 0.6f, smoothness); // rate-loop gain (higher = crisper)

            // ---- Cruise (converted): fly like an aeroplane, keys change speed / heading / altitude ----
            if (mode == VTOLMode.Cruise)
            {
                heading += inYaw * 25f * dt + inSide * 25f * dt;
                cruiseSpeedCmd = Mathf.Clamp(cruiseSpeedCmd + inFwd * 20f * dt, transitionStart, 2000f);
                if (climb != 0) cruiseAlt += climb * 20f * dt;
                SetTiltSchedule(1f, 0);
                VTOLParts.LiftEngines(this, liftOffInCruise && vessel.srfSpeed > transitionStart * 1.5f);
                var hdg = Quaternion.AngleAxis(heading, up) * north;
                float hErr = Vector3.SignedAngle(Vector3.ProjectOnPlane(v, up), hdg, up);
                float bank = Mathf.Clamp(hErr * 1.2f, -35f, 35f);
                float fpa = Mathf.Clamp((cruiseAlt - (float)vessel.altitude) * 0.05f, -12f, 12f);
                float pitchUp = fpa + 3f; // a little angle of attack
                Vector3 cNose = Quaternion.AngleAxis(-pitchUp, Vector3.Cross(up, hdg).normalized) * hdg;
                Vector3 topDes = Quaternion.AngleAxis(-bank, cNose) * Vector3.ProjectOnPlane(up, cNose).normalized;
                Attitude(s, cNose, topDes, bodyNose, bodyUp, resp, soft, rt, rate);
                float spdErr = cruiseSpeedCmd - (float)vessel.srfSpeed;
                integral = Mathf.Clamp(integral + spdErr * dt * 0.01f, -0.4f, 0.4f);
                s.mainThrottle = Mathf.Clamp01(0.6f + spdErr * 0.05f + integral);
                status = $"Cruise {vessel.srfSpeed:F0}/{cruiseSpeedCmd:F0} m/s";
                if (!player && vessel.srfSpeed < transitionStart) Engage(VTOLMode.Hover);
                Remember(rate, s);
                return;
            }

            // ---- Hover / transition ----
            var hFwd = Quaternion.AngleAxis(heading, up) * north;
            var hRight = Vector3.Cross(up, hFwd).normalized;
            float speedLimit = maxSpeed;
            Vector3 vCmd;
            if (mode == VTOLMode.GoTo)
            {
                var target = vessel.mainBody.GetWorldSurfacePosition(gotoLat, gotoLon, vessel.mainBody.TerrainAltitude(gotoLat, gotoLon, true));
                var d = Vector3.ProjectOnPlane((Vector3)(target - vessel.CoMD), up);
                float far = d.magnitude;
                speedLimit = far > 600 && type != VTOLType.Helicopter ? Mathf.Max(maxSpeed, cruiseSpeed * 0.8f) : far > 200 ? maxSpeed * 2f : maxSpeed;
                // brake in time: v = sqrt(2 * a * d)
                float vMax = Mathf.Sqrt(2f * 9.81f * Mathf.Tan(maxTilt * Mathf.Deg2Rad) * 0.5f * far);
                vCmd = d.normalized * Mathf.Min(speedLimit, vMax, far * 0.5f);
                if (far > 30) heading = Vector3.SignedAngle(north, d, up);
                double ground = Math.Max(0, vessel.mainBody.TerrainAltitude(gotoLat, gotoLon, true));
                holdAlt = ground + (gotoLand && far < 25 ? 0 : gotoAGL);
                if (gotoLand && far < 25 && gs.magnitude < 3) { mode = VTOLMode.Land; SetHold(target); }
            }
            else if (mode == VTOLMode.Train)
            {
                vCmd = Vector3.ClampMagnitude(Vector3.ProjectOnPlane((Vector3)(vessel.mainBody.GetWorldSurfacePosition(holdLat, holdLon, vessel.altitude) - vessel.CoMD), up) * 0.3f, maxSpeed * 0.5f);
            }
            else if (Mathf.Abs(inFwd) > 0.05f || Mathf.Abs(inSide) > 0.05f)
            {
                // Full forward stick on a tiltrotor / jump jet keeps accelerating through the transition.
                float fwdLimit = inFwd > 0.9f && type != VTOLType.Helicopter ? cruiseSpeed : maxSpeed;
                vCmd = hFwd * inFwd * fwdLimit + hRight * inSide * maxSpeed;
                SetHold(vessel.CoM);
            }
            else
            {
                var hold = vessel.mainBody.GetWorldSurfacePosition(holdLat, holdLon, vessel.altitude);
                vCmd = Vector3.ClampMagnitude(Vector3.ProjectOnPlane((Vector3)(hold - vessel.CoMD), up) * 0.3f * resp, maxSpeed * 0.5f);
            }
            heading += inYaw * 40f * dt;

            // Tilt schedule by airspeed along the nose: hover below transitionStart, cruise at cruiseSpeed.
            float fwdSpeed = Vector3.Dot(gs, Vector3.ProjectOnPlane(bodyNose, up).normalized);
            float wantTau = Mathf.Clamp01((Mathf.Max(fwdSpeed, Vector3.Dot(vCmd, hFwd) * 0.5f) - transitionStart) / Mathf.Max(5f, cruiseSpeed - transitionStart));
            tau = Mathf.MoveTowards(tau, Mathf.Min(wantTau, Mathf.Clamp01((fwdSpeed - transitionStart * 0.5f) / Mathf.Max(5f, cruiseSpeed - transitionStart)) + 0.2f), dt * 0.15f);
            float yawCmd = lastU[2];
            SetTiltSchedule(tau, yawCmd);
            VTOLParts.LiftEngines(this, false);
            if (tau > 0.95f && fwdSpeed > cruiseSpeed * 0.9f && mode != VTOLMode.GoTo) { Engage(VTOLMode.Cruise); return; }

            // Desired thrust: lean toward the needed horizontal acceleration (limited tilt); wings carry more as tau grows.
            float aMax = 9.81f * Mathf.Tan(Mathf.Lerp(maxTilt, maxTilt * 0.5f, tau) * Mathf.Deg2Rad);
            var aH = Vector3.ClampMagnitude((vCmd - gs) * 0.6f * resp, aMax);
            var desiredThrust = (up * 9.81f + aH).normalized;
            // Body attitude that puts the current thrust axis onto the desired one (nacelle tilt included), nose on the heading.
            Quaternion bodyFromThrust = Quaternion.FromToRotation(tdir, bodyUp);
            Vector3 upDes = bodyFromThrust * desiredThrust;
            if (tau > 0.2f) upDes = Vector3.Slerp(upDes, up, tau); // transition: keep the body near level, the nacelles do the pushing
            Vector3 noseDes = Vector3.ProjectOnPlane(hFwd, upDes).normalized;

            float sig = 0; int tAxis = -1;
            if (mode == VTOLMode.Train) sig = TrainSignal(out tAxis);
            Attitude(s, noseDes, upDes, bodyNose, bodyUp, resp, soft, rt, rate, tAxis, sig);
            if (mode == VTOLMode.Train && trainStep == "done") { SaveLearning(); ScreenMessages.PostScreenMessage($"VTOL training done: {LearnedText}", 6f, ScreenMessageStyle.UPPER_CENTER); mode = VTOLMode.Hover; }

            // Vertical: altitude -> climb rate -> throttle, hover feed-forward learned in flight.
            if (climb != 0) holdAlt = vessel.altitude + climb * maxClimb * 0.6f;
            float vsCmd;
            if (mode == VTOLMode.Land)
            {
                float agl = (float)Math.Max(0, vessel.radarAltitude);
                vsCmd = -Mathf.Clamp(agl * 0.25f, 0.6f, maxClimb);
                if (vessel.LandedOrSplashed && Mathf.Abs(vsNow) < 0.6f) { s.mainThrottle = 0; Disengage("landed"); ScreenMessages.PostScreenMessage("VTOL: landed", 2f, ScreenMessageStyle.UPPER_CENTER); return; }
            }
            else
            {
                double floor = vessel.altitude - vessel.radarAltitude + 3;
                vsCmd = Mathf.Clamp((float)(Math.Max(holdAlt, floor) - vessel.altitude) * 0.5f * resp, -maxClimb, maxClimb);
                if (mode == VTOLMode.Takeoff && vessel.radarAltitude > takeoffAGL * 0.9f) { mode = VTOLMode.Hover; SetHold(vessel.CoM); }
            }
            float g = (float)FlightGlobals.getGeeForceAtPosition(vessel.CoM).magnitude;
            float hoverT = hoverThrottle > 0 ? hoverThrottle : tmax > 0 ? (float)(vessel.totalMass * g / tmax) : 0.5f;
            hoverT /= Mathf.Max(0.35f, ThrustUp); // tilted thrust needs more throttle to hold height
            float err = vsCmd - vsNow;
            float vsAcc = (vsNow - lastVs) / dt; lastVs = vsNow;
            integral = Mathf.Clamp(integral + err * dt * 0.06f, -0.3f, 0.3f);
            s.mainThrottle = Mathf.Clamp01(hoverT + err * 0.12f * resp + integral);
            // learn the hover throttle while steady
            if (!vessel.LandedOrSplashed && Mathf.Abs(vsNow) < 1f && Mathf.Abs(vsAcc) < 0.5f && ThrustUp > 0.85f && tau < 0.1f)
                hoverThrottle = hoverThrottle < 0 ? s.mainThrottle * ThrustUp : Mathf.Lerp(hoverThrottle, s.mainThrottle * ThrustUp, dt * 0.2f);

            VTOLParts.DiffThrust(this, s, bodyUp, diffThrust && tau < 0.5f);
            tilt = Vector3.Angle(tdir, up);
            status = mode == VTOLMode.Train ? $"Training: {trainStep}" : $"{mode} {vessel.radarAltitude:F0} m AGL{(tau > 0.02f ? $", tilt {tau:P0}" : "")}";
            Remember(rate, s);
        }

        void Remember(Vector3 rate, FlightCtrlState s) { lastRate = rate; lastU[0] = s.pitch; lastU[1] = s.roll; lastU[2] = s.yaw; haveLast = true; }

        /// <summary>
        /// Attitude -> rate -> stick. Desired body rates come from the rotation that takes the body's (nose, up) onto (noseDes, upDes),
        /// shaped by a braking curve; the stick is the learned model inverted (blended with a fixed law until it has learned).
        /// </summary>
        void Attitude(FlightCtrlState s, Vector3 noseDes, Vector3 upDes, Vector3 bodyNose, Vector3 bodyUp, float resp, float soft, Transform rt, Vector3 rate, int trainAxis = -1, float trainSig = 0)
        {
            Quaternion cur = Quaternion.LookRotation(bodyNose, bodyUp), des = Quaternion.LookRotation(noseDes, upDes);
            (des * Quaternion.Inverse(cur)).ToAngleAxis(out float ang, out Vector3 ax);
            if (ang > 180) { ang -= 360; }
            if (float.IsInfinity(ax.x) || float.IsNaN(ax.x)) ax = Vector3.zero;
            Vector3 errVec = ax * (ang * Mathf.Deg2Rad); // world, radians
            // BDArmory axes: pitch about rt.right (+ = nose up => -right), roll about rt.up (nose), yaw about rt.forward.
            Vector3 eAxes = new(-Vector3.Dot(errVec, rt.right), -Vector3.Dot(errVec, rt.up), -Vector3.Dot(errVec, rt.forward));
            float gust = 1f + gyro * 0.5f;
            for (int i = 0; i < 3; i++)
            {
                var L = learn[i];
                float e = eAxes[i];
                float accMax = Mathf.Max(0.3f, (float)Math.Abs(L.b) * 0.6f * (L.Confidence > 0.3f ? 1 : 0.5f));
                float rDes = Mathf.Sign(e) * Mathf.Min(Mathf.Abs(e) * 2.0f * resp, Mathf.Sqrt(2f * accMax * Mathf.Abs(e)));
                float kr = 4f * soft * gust;
                float accDes = kr * (rDes - rate[i]);
                // Learned inversion vs fixed law, blended by confidence.
                float uModel = L.b > 0.05 ? (float)((accDes - L.a * rate[i] - L.c) / L.b) : 0;
                float uFixed = 0.9f * (rDes - rate[i]) * 2f * soft;
                float c = L.Confidence;
                float u = Mathf.Lerp(uFixed, uModel, c);
                if (i == trainAxis) u += trainSig;
                if (i == 0) s.pitch = Mathf.Clamp(u, -1, 1); else if (i == 1) s.roll = Mathf.Clamp(u, -1, 1); else s.yaw = Mathf.Clamp(u, -1, 1);
            }
            s.X = 0;
            if (gyro > 0) VTOLParts.Gyro(this, rate, gyro);
        }

        void SetTiltSchedule(float t, float yaw) { tau = t; VTOLParts.DriveTilt(this, t, yaw); }

        // ---- Click to go / land ----
        public static int clickMode; // 0 none, 1 go, 2 land

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight || !available || vessel != FlightGlobals.ActiveVessel || GUIUtility.keyboardControl != 0) return;
            if (Input.GetKeyDown(AvionicsSettings.KeyVTOLHover)) { if (mode == VTOLMode.Off) Engage(VTOLMode.Hover); else Disengage("off"); }
            if (Input.GetKeyDown(AvionicsSettings.KeyVTOLLand)) Engage(VTOLMode.Land);
            if (clickMode != 0 && Input.GetMouseButtonDown(0) && !PanelRect.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y)))
            {
                var cam = FlightCamera.fetch.mainCamera;
                if (Physics.Raycast(cam.ScreenPointToRay(Input.mousePosition), out var hit, 100000f, (1 << 15) | (1 << 28)))
                {
                    var b = vessel.mainBody;
                    GoTo(b.GetLatitude(hit.point), b.GetLongitude(hit.point), Math.Max(20, vessel.radarAltitude), clickMode == 2);
                }
                clickMode = 0;
            }
        }

        void OnDestroy() { if (registered != null) registered.OnFlyByWire -= Fly; }

        // ---- Flight panel ----
        static Rect PanelRect => new(Screen.width / 2f - 300, Screen.height - 230, 600, 46);

        void OnGUI()
        {
            if (!available || vessel != FlightGlobals.ActiveVessel || !BDArmorySetup.GAME_UI_ENABLED || MapView.MapIsEnabled) return;
            var r = PanelRect;
            GUI.Box(r, "", BDArmorySetup.BDGuiSkin.box);
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            string[] names = { "Hover", "Takeoff", "Land", "Go ▸", "Land ▸", "Cruise", "Train", "Off" };
            float bw = (r.width - 8) / names.Length;
            for (int i = 0; i < names.Length; i++)
            {
                bool active = (i == 0 && mode == VTOLMode.Hover) || (i == 1 && mode == VTOLMode.Takeoff) || (i == 2 && mode == VTOLMode.Land) || (i == 3 && (clickMode == 1 || mode == VTOLMode.GoTo)) || (i == 4 && clickMode == 2) || (i == 5 && mode == VTOLMode.Cruise) || (i == 6 && mode == VTOLMode.Train);
                if (!GUI.Button(new Rect(r.x + 4 + i * bw, r.y + 4, bw - 3, 20), names[i], active ? sel : btn)) continue;
                switch (i)
                {
                    case 0: Engage(VTOLMode.Hover); break;
                    case 1: Engage(VTOLMode.Takeoff); break;
                    case 2: Engage(VTOLMode.Land); break;
                    case 3: clickMode = clickMode == 1 ? 0 : 1; break;
                    case 4: clickMode = clickMode == 2 ? 0 : 2; break;
                    case 5: Engage(VTOLMode.Cruise); break;
                    case 6: if (mode == VTOLMode.Off) Engage(VTOLMode.Hover); SetHold(vessel.CoM); Engage(VTOLMode.Train); break;
                    case 7: Disengage("off"); break;
                }
            }
            string line = mode == VTOLMode.Off ? $"{Type} · flight computer off · learned: {LearnedText}" + (clickMode != 0 ? " · click the ground" : "")
                : $"{status} · V/S {vsNow:+0.0;-0.0} · GS {gs.magnitude:F1} · tilt {tilt:F0}°{(clickMode != 0 ? " · click the ground" : "")}";
            AvionicsDraw.ShadowLabel(new Rect(r.x, r.y + 27, r.width, 16), line, AvionicsDraw.LabelCenter, mode != VTOLMode.Off ? AvionicsDraw.Friendly : AvionicsDraw.HudText);
        }
    }

    public enum VTOLType { Helicopter, JumpJet, Tiltrotor }

    /// <summary>Part helpers: tilt servos, rotors, lift engines, engine aids, differential thrust, gyro.</summary>
    public static class VTOLParts
    {
        public static List<(Part part, PartModule servo, ModuleIRTurret m)> TiltServos(IEnumerable<Part> parts)
        {
            var list = new List<(Part, PartModule, ModuleIRTurret)>();
            if (parts == null || !IRServo.Available) return list;
            foreach (var p in parts)
            {
                var m = p.FindModuleImplementing<ModuleIRTurret>();
                if (m == null || m.tiltRole == 0) continue;
                var s = IRServo.Find(p);
                if (s != null) list.Add((p, s, m));
            }
            return list;
        }

        public static bool HasRotors(IEnumerable<Part> parts)
        {
            if (parts == null) return false;
            foreach (var p in parts) foreach (PartModule m in p.Modules) if (m.moduleName == "ModuleRoboticServoRotor") return true;
            return false;
        }

        /// <summary>Tilt servos to the schedule (0 hover .. 1 cruise), plus differential tilt for yaw on "tilt + yaw" servos.</summary>
        public static void DriveTilt(ModuleVTOLAssist fc, float tau, float yaw)
        {
            var v = fc.vessel;
            fc.BodyAxes(out var bodyUp, out var bodyNose);
            Vector3 right = Vector3.Cross(bodyUp, bodyNose).normalized;
            foreach (var (part, servo, m) in TiltServos(v.parts))
            {
                float pos = Mathf.Lerp(m.tiltHover, m.tiltCruise, tau);
                if (m.tiltRole == 2 && tau < 0.5f)
                {
                    float side = Mathf.Sign(Vector3.Dot(part.transform.position - v.CoM, right));
                    pos += side * yaw * m.tiltYaw * (1 - tau * 2);
                }
                if (Mathf.Abs(IRServo.Position(servo) - pos) > 0.3f) IRServo.MoveTo(servo, pos, IRServo.MaxSpeed(servo));
            }
        }

        static readonly Dictionary<ModuleEngines, (bool resp, float pct)> saved = new();

        /// <summary>Engine aids on (no spool lag) / off (restore).</summary>
        public static void ApplyEngineAids(ModuleVTOLAssist fc, bool on)
        {
            foreach (var e in VesselModuleRegistry.GetModules<ModuleEngines>(fc.vessel))
            {
                if (e == null) continue;
                if (on)
                {
                    if (!saved.ContainsKey(e)) saved[e] = (e.useEngineResponseTime, e.thrustPercentage);
                    if (fc.fastEngines) e.useEngineResponseTime = false;
                }
                else if (saved.TryGetValue(e, out var o)) { e.useEngineResponseTime = o.resp; e.thrustPercentage = o.pct; if (!e.EngineIgnited && o.pct > 0 && liftOff.Contains(e)) e.Activate(); saved.Remove(e); liftOff.Remove(e); }
            }
        }

        static readonly HashSet<ModuleEngines> liftOff = new();

        /// <summary>Lift-only engines (thrust along the body's up while the craft is converted) off in cruise, back on otherwise.</summary>
        public static void LiftEngines(ModuleVTOLAssist fc, bool off)
        {
            fc.BodyAxes(out var bodyUp, out var bodyNose);
            foreach (var e in VesselModuleRegistry.GetModules<ModuleEngines>(fc.vessel))
            {
                if (e == null || e.thrustTransforms.Count == 0) continue;
                Vector3 d = -e.thrustTransforms[0].forward;
                bool lift = Vector3.Dot(d, bodyUp) > 0.8f && Vector3.Dot(d, bodyNose) < 0.3f;
                if (off && lift && e.EngineIgnited) { liftOff.Add(e); e.Shutdown(); }
                else if (!off && liftOff.Contains(e) && !e.EngineIgnited) { e.Activate(); liftOff.Remove(e); }
            }
        }

        /// <summary>Differential thrust: trim each lift engine's thrust limiter to add the pitch/roll the stick asks for.</summary>
        public static void DiffThrust(ModuleVTOLAssist fc, FlightCtrlState s, Vector3 bodyUp, bool on)
        {
            var v = fc.vessel;
            var rt = v.ReferenceTransform;
            var lift = new List<(ModuleEngines e, float pitch, float roll)>();
            foreach (var e in VesselModuleRegistry.GetModules<ModuleEngines>(v))
            {
                if (e == null || !e.EngineIgnited || e.thrustTransforms.Count == 0 || !saved.ContainsKey(e)) continue;
                Vector3 d = -e.thrustTransforms[0].forward;
                if (Vector3.Dot(d, bodyUp) < 0.7f) continue;
                Vector3 torque = Vector3.Cross(e.thrustTransforms[0].position - v.CoM, d); // per unit thrust
                lift.Add((e, -Vector3.Dot(torque, rt.right), -Vector3.Dot(torque, rt.up)));
            }
            if (lift.Count < 2) return;
            float maxArm = 0.01f; foreach (var l in lift) maxArm = Mathf.Max(maxArm, Mathf.Abs(l.pitch), Mathf.Abs(l.roll));
            foreach (var (e, pArm, rArm) in lift)
            {
                float basePct = saved[e].pct;
                float delta = on ? (pArm * s.pitch + rArm * s.roll) / maxArm * 25f : 0; // up to +-25 % per engine
                // Engines can only give less than full: the side that should push harder stays, the other side backs off.
                e.thrustPercentage = delta < 0 ? Mathf.Clamp(basePct + delta, 5f, 100f) : basePct;
            }
        }

        /// <summary>Stability gyro: damps body rates with a torque scaled to the craft's mass (a stability augmentation aid).</summary>
        public static void Gyro(ModuleVTOLAssist fc, Vector3 rate, float strength)
        {
            var v = fc.vessel;
            var rb = v.rootPart != null ? v.rootPart.Rigidbody : null;
            if (rb == null) return;
            var rt = v.ReferenceTransform;
            Vector3 wWorld = -(rt.right * rate.x + rt.up * rate.y + rt.forward * rate.z);
            float k = (float)v.totalMass * strength * 4f; // kN·m per rad/s
            rb.AddTorque(-wWorld * k, ForceMode.Force);
        }
    }
}
