using System;
using System.Collections;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    public enum VTOLMode { Off, Hover, GoTo, Land }

    /// <summary>
    /// VTOL flight assist for tiltrotors / helicopters / lift-jet craft. Works whenever the engines' thrust points mostly up.
    /// Hover: holds altitude and position, levels the craft; your keys become velocity commands
    /// (W/S forward/back, A/D yaw, Q/E sideways, Shift/Ctrl climb/descend), let go and it stops and holds.
    /// GoTo: hovers to a point (C2 / CSAR), optionally landing there. Land: descends gently and cuts the throttle on touchdown.
    /// An optional action group converts the nacelles to hover for automatic transitions (CSAR pickup).
    /// </summary>
    public class ModuleVTOLAssist : PartModule
    {
        const string Group = "vtolAssist";
        public static readonly string[] AGs = ["None", "Custom01", "Custom02", "Custom03", "Custom04", "Custom05", "Custom06", "Custom07", "Custom08", "Custom09", "Custom10", "Gear", "Brakes"];

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "VTOL assist", groupName = Group, groupDisplayName = "VTOL assist", groupStartCollapsed = true),
         UI_Toggle(enabledText = "Available", disabledText = "Off", scene = UI_Scene.All)]
        public bool available = false;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Max hover speed", guiUnits = " m/s", groupName = Group),
         UI_FloatRange(minValue = 3f, maxValue = 40f, stepIncrement = 1f, scene = UI_Scene.All)]
        public float maxSpeed = 15f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Max tilt", guiUnits = "°", groupName = Group),
         UI_FloatRange(minValue = 3f, maxValue = 30f, stepIncrement = 1f, scene = UI_Scene.All)]
        public float maxTilt = 15f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Attitude response", groupName = Group),
         UI_FloatRange(minValue = 0.2f, maxValue = 3f, stepIncrement = 0.1f, scene = UI_Scene.All)]
        public float attitudeGain = 1f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Damping", groupName = Group),
         UI_FloatRange(minValue = 0.2f, maxValue = 3f, stepIncrement = 0.1f, scene = UI_Scene.All)]
        public float rateGain = 1f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Hover mode action group", groupName = Group),
         UI_ChooseOption(options = ["None", "Custom01", "Custom02", "Custom03", "Custom04", "Custom05", "Custom06", "Custom07", "Custom08", "Custom09", "Custom10", "Gear", "Brakes"], scene = UI_Scene.All)]
        public string hoverAG = "None";

        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "VTOL", groupName = Group)]
        public string status = "off";

        public VTOLMode mode = VTOLMode.Off;
        public double holdAlt;            // ASL
        public Vector3d holdPoint;        // world, refreshed from lat/lon
        double holdLat, holdLon, gotoLat, gotoLon, gotoAGL;
        bool gotoLand;
        float heading, integral;
        bool aiWasOn;
        Vessel registered;
        float tilt, vsNow; Vector3 gs;
        public float ThrustUp { get; private set; }

        public static ModuleVTOLAssist For(Vessel v)
        {
            if (v == null) return null;
            foreach (var p in v.parts) { var m = p.FindModuleImplementing<ModuleVTOLAssist>(); if (m != null && m.available) return m; }
            return null;
        }

        [KSPEvent(guiActive = true, guiName = "Hover hold", groupName = Group)] public void HoverEvent() => Engage(VTOLMode.Hover);
        [KSPEvent(guiActive = true, guiName = "Auto-land", groupName = Group)] public void LandEvent() => Engage(VTOLMode.Land);
        [KSPEvent(guiActive = true, guiName = "Assist off", groupName = Group)] public void OffEvent() => Disengage("off");
        [KSPAction("VTOL hover hold toggle")] public void HoverAction(KSPActionParam p) { if (mode == VTOLMode.Off) Engage(VTOLMode.Hover); else Disengage("off"); }
        [KSPAction("VTOL auto-land")] public void LandAction(KSPActionParam p) => Engage(VTOLMode.Land);

        Vector3 Up => (vessel.CoM - vessel.mainBody.position).normalized;

        /// <summary>Thrust direction (unit) and max thrust of the running engines.</summary>
        (Vector3 dir, float max) Thrust()
        {
            Vector3 sum = Vector3.zero; float max = 0;
            foreach (var e in VesselModuleRegistry.GetModules<ModuleEngines>(vessel))
            {
                if (e == null || !e.EngineIgnited || e.flameout) continue;
                float t = e.GetMaxThrust() * e.thrustPercentage / 100f;
                if (t <= 0 || e.thrustTransforms.Count == 0) continue;
                Vector3 d = Vector3.zero;
                foreach (var tt in e.thrustTransforms) d -= tt.forward;
                sum += d.normalized * t; max += t;
            }
            return (sum.sqrMagnitude > 0 ? sum.normalized : -vessel.ReferenceTransform.forward, max);
        }

        public void Engage(VTOLMode m)
        {
            if (vessel == null) return;
            var up = Up;
            if (mode == VTOLMode.Off)
            {
                var ai = vessel.ActiveController().PilotAI;
                aiWasOn = ai != null && ai.pilotEnabled;
                if (aiWasOn) ai.DeactivatePilot(); // the assist flies; BDArmory's AI comes back when it's switched off
                holdAlt = vessel.altitude;
                SetHold(vessel.CoM);
                heading = Heading();
                integral = 0;
            }
            mode = m;
            if (vessel.OnFlyByWire != null) vessel.OnFlyByWire -= Fly;
            vessel.OnFlyByWire += Fly; // added last, so it acts after any other autopilot
            registered = vessel;
            ScreenMessages.PostScreenMessage($"VTOL assist: {m}", 2f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log($"[BDArmoryAvionics]: VTOL {vessel.GetName()}: {m} at {vessel.altitude:F0} m (AI was {(aiWasOn ? "on" : "off")})");
        }

        public void Disengage(string why)
        {
            if (registered != null) registered.OnFlyByWire -= Fly;
            registered = null;
            if (mode != VTOLMode.Off)
            {
                Debug.Log($"[BDArmoryAvionics]: VTOL {vessel?.GetName()}: off ({why})");
                if (aiWasOn && vessel != null && !vessel.LandedOrSplashed) vessel.ActiveController().PilotAI?.ActivatePilot();
            }
            mode = VTOLMode.Off; status = why;
        }

        /// <summary>Fly (in hover) to a point; optionally land there. Converts to hover first via the hover action group.</summary>
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
            Debug.Log($"[BDArmoryAvionics]: VTOL {vessel.GetName()}: action group {hoverAG} for hover conversion");
            return true;
        }

        void SetHold(Vector3d world) { holdLat = vessel.mainBody.GetLatitude(world); holdLon = vessel.mainBody.GetLongitude(world); }

        float Heading()
        {
            var up = Up; var north = Vector3.ProjectOnPlane(vessel.mainBody.transform.up, up).normalized;
            var nose = Vector3.ProjectOnPlane(vessel.ReferenceTransform.up, up);
            return nose.sqrMagnitude < 1e-4f ? 0 : Vector3.SignedAngle(north, nose, up);
        }

        void Fly(FlightCtrlState s)
        {
            if (mode == VTOLMode.Off || vessel == null || vessel.packed) return;
            var rt = vessel.ReferenceTransform;
            Vector3 up = Up, nose = rt.up, right = rt.right, fwdAxis = rt.forward;
            var (tdir, tmax) = Thrust();
            ThrustUp = Vector3.Dot(tdir, up);
            if (ThrustUp < 0.5f) { status = "thrust not vertical (convert to hover)"; return; } // let the pilot fly: not in hover configuration

            // Player input (already in s) becomes velocity / heading / climb commands.
            bool player = vessel == FlightGlobals.ActiveVessel && mode == VTOLMode.Hover;
            float inFwd = player ? -s.pitch : 0, inSide = player ? s.roll : 0, inYaw = player ? s.yaw : 0; // W/S, Q/E, A/D
            if (player && Mathf.Abs(inSide) < 0.05f) inSide = 0;
            float climb = 0;
            if (player) { if (GameSettings.THROTTLE_UP.GetKey()) climb = 1; if (GameSettings.THROTTLE_DOWN.GetKey()) climb = -1; }

            // Horizontal frame
            var north = Vector3.ProjectOnPlane(vessel.mainBody.transform.up, up).normalized;
            heading += inYaw * 40f * Time.fixedDeltaTime;
            var hFwd = Quaternion.AngleAxis(heading, up) * north;
            var hRight = Vector3.Cross(up, hFwd).normalized;
            Vector3 v = vessel.srf_velocity;
            vsNow = Vector3.Dot(v, up);
            gs = v - vsNow * up;

            // Velocity command
            Vector3 vCmd;
            if (mode == VTOLMode.GoTo)
            {
                var target = vessel.mainBody.GetWorldSurfacePosition(gotoLat, gotoLon, vessel.mainBody.TerrainAltitude(gotoLat, gotoLon, true));
                var d = Vector3.ProjectOnPlane((Vector3)(target - vessel.CoMD), up);
                vCmd = Vector3.ClampMagnitude(d * 0.08f, maxSpeed * (d.magnitude > 200 ? 2f : 1f));
                if (d.magnitude > 30) heading = Vector3.SignedAngle(north, d, up);
                double ground = Math.Max(0, vessel.mainBody.TerrainAltitude(gotoLat, gotoLon, true));
                holdAlt = ground + (gotoLand && d.magnitude < 25 ? 0 : gotoAGL);
                if (gotoLand && d.magnitude < 25) { mode = VTOLMode.Land; SetHold(target); }
            }
            else if (Mathf.Abs(inFwd) > 0.05f || Mathf.Abs(inSide) > 0.05f)
            {
                vCmd = (hFwd * inFwd + hRight * inSide) * maxSpeed;
                SetHold(vessel.CoM); // re-anchor where you let go
            }
            else
            {
                var hold = vessel.mainBody.GetWorldSurfacePosition(holdLat, holdLon, vessel.altitude);
                vCmd = Vector3.ClampMagnitude(Vector3.ProjectOnPlane((Vector3)(hold - vessel.CoMD), up) * 0.3f, maxSpeed * 0.5f);
            }

            // Desired thrust axis: lean toward the needed acceleration (limited tilt)
            var aH = Vector3.ClampMagnitude((vCmd - gs) * 0.6f, 9.81f * Mathf.Tan(maxTilt * Mathf.Deg2Rad));
            var desired = (up * 9.81f + aH).normalized;

            // Attitude: rotate the thrust axis onto the desired one, and the nose onto the heading.
            Vector3 omegaDes = Vector3.Cross(tdir, desired) * 2.0f * attitudeGain;
            var noseH = Vector3.ProjectOnPlane(nose, up);
            if (noseH.sqrMagnitude > 0.01f) omegaDes += Vector3.Cross(noseH.normalized, hFwd) * 1.2f * attitudeGain; // turn the nose onto the heading
            Vector3 w = vessel.angularVelocity;                 // vessel frame: x right, y nose, z forward(down)
            float pitchRate = -w.x, rollRate = -w.y, yawRate = -w.z;
            float pitchDes = -Vector3.Dot(omegaDes, right), rollDes = -Vector3.Dot(omegaDes, nose), yawDes = -Vector3.Dot(omegaDes, fwdAxis);
            float k = 0.9f * rateGain;
            s.pitch = Mathf.Clamp(k * (pitchDes - pitchRate) * 2f, -1f, 1f);
            s.roll = Mathf.Clamp(k * (rollDes - rollRate) * 2f, -1f, 1f);
            s.yaw = Mathf.Clamp(k * (yawDes - yawRate) * 2f, -1f, 1f);
            s.X = 0;

            // Vertical: altitude -> climb rate -> throttle (hover feed-forward + PI)
            if (climb != 0) holdAlt = vessel.altitude + climb * 3f;
            float vsCmd;
            if (mode == VTOLMode.Land)
            {
                float agl = (float)Math.Max(0, vessel.radarAltitude);
                vsCmd = -Mathf.Clamp(agl * 0.25f, 0.6f, 4f);
                if (vessel.LandedOrSplashed && Mathf.Abs(vsNow) < 0.5f) { s.mainThrottle = 0; Disengage("landed"); ScreenMessages.PostScreenMessage("VTOL: landed", 2f, ScreenMessageStyle.UPPER_CENTER); return; }
            }
            else
            {
                double floor = vessel.altitude - vessel.radarAltitude + 3; // never command into the ground
                vsCmd = Mathf.Clamp((float)(Math.Max(holdAlt, floor) - vessel.altitude) * 0.5f, -5f, 5f);
            }
            float g = (float)FlightGlobals.getGeeForceAtPosition(vessel.CoM).magnitude;
            float hoverT = tmax > 0 ? (float)(vessel.totalMass * g / (tmax * Mathf.Max(0.3f, ThrustUp))) : 1f;
            float err = vsCmd - vsNow;
            integral = Mathf.Clamp(integral + err * Time.fixedDeltaTime * 0.05f, -0.3f, 0.3f);
            s.mainThrottle = Mathf.Clamp01(hoverT + err * 0.12f + integral);
            tilt = Vector3.Angle(tdir, up);
            status = $"{mode} {vessel.radarAltitude:F0} m AGL";
        }

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight || !available || vessel != FlightGlobals.ActiveVessel || GUIUtility.keyboardControl != 0) return;
            if (Input.GetKeyDown(AvionicsSettings.KeyVTOLHover)) { if (mode == VTOLMode.Off) Engage(VTOLMode.Hover); else Disengage("off"); }
            if (Input.GetKeyDown(AvionicsSettings.KeyVTOLLand)) Engage(VTOLMode.Land);
        }

        void OnDestroy() { if (registered != null) registered.OnFlyByWire -= Fly; }

        void OnGUI()
        {
            if (mode == VTOLMode.Off || vessel != FlightGlobals.ActiveVessel || !BDArmorySetup.GAME_UI_ENABLED) return;
            string line = $"VTOL {mode}   AGL {vessel.radarAltitude:F0} m (hold {holdAlt - (vessel.altitude - vessel.radarAltitude):F0})   V/S {vsNow:+0.0;-0.0}   GS {gs.magnitude:F1} m/s   tilt {tilt:F0}°   [{AvionicsSettings.KeyVTOLHover}] off  [{AvionicsSettings.KeyVTOLLand}] land";
            AvionicsDraw.ShadowLabel(new Rect(Screen.width / 2f - 400, Screen.height - 190, 800, 16), line, AvionicsDraw.LabelCenter, ThrustUp < 0.5f ? AvionicsDraw.Unknown : AvionicsDraw.Friendly);
        }
    }
}
