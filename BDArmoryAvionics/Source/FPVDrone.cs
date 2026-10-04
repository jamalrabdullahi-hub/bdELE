using System;
using System.Reflection;
using UnityEngine;

using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// FPV drone: arcade flying for any craft with the VTOL flight computer, seen through its HullCam.
    ///   W / S   faster / slower (speed is held when you let go)      A / D  turn
    ///   Q / E   slide left / right                                   Shift / Ctrl  up / down
    ///   F       STRIKE: full-speed dive into whatever is under the crosshair (or the tracked target)
    ///   B       stop and hover (Space would stage, Backspace would abort)
    /// The VTOL computer does the hard part (attitude, thrust, staying in the air); any BDArmory warhead on it does the rest.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class FPVDrone : MonoBehaviour
    {
        public static bool Active;
        static Vessel drone;
        static ModuleVTOLAssist fc;
        float speed, heading, slide;
        double alt;
        bool strike; Vector3 strikePoint; Vessel strikeVessel;
        const float MaxSpeed = 60f, StrikeSpeed = 90f;

        static MethodInfo activateCam;

        public static bool Available(Vessel v) => v != null && ModuleVTOLAssist.For(v) != null;

        public void Engage(Vessel v)
        {
            fc = ModuleVTOLAssist.For(v);
            if (fc == null) { ScreenMessages.PostScreenMessage("FPV needs the VTOL flight computer on this craft (VTOL designer)", 4f, ScreenMessageStyle.UPPER_CENTER); return; }
            drone = v; Active = true; strike = false;
            speed = 0; slide = 0;
            fc.BodyAxes(out _, out var nose);
            var up = (Vector3)v.upAxis; var north = Vector3.ProjectOnPlane(v.mainBody.transform.up, up).normalized;
            heading = Vector3.SignedAngle(north, Vector3.ProjectOnPlane(nose, up), up);
            alt = v.altitude;
            if (v.LandedOrSplashed) alt = v.altitude + 10;
            fc.Engage(VTOLMode.Direct);
            // look through the drone's camera
            foreach (var p in v.parts)
            {
                var cam = HullCam.Find(p);
                if (cam == null) continue;
                activateCam ??= cam.GetType().GetMethod("ActivateCamera", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (HullCam.Current != cam) try { activateCam?.Invoke(cam, null); } catch { }
                break;
            }
            ScreenMessages.PostScreenMessage("FPV: W/S speed · A/D turn · Q/E slide · Shift/Ctrl up/down · F STRIKE · B stop and hover", 5f, ScreenMessageStyle.UPPER_CENTER);
        }

        public void Disengage(string why)
        {
            if (fc != null && fc.mode == VTOLMode.Direct) { if (drone != null && !drone.LandedOrSplashed) fc.Engage(VTOLMode.Hover); else fc.Disengage(why); }
            Active = false; drone = null; fc = null;
        }

        void Update()
        {
            if (!Active) return;
            if (drone == null || !drone.loaded || fc == null || fc.vessel != drone) { Active = false; return; }
            if (fc.mode != VTOLMode.Direct) { Active = false; return; } // something else took over
            if (GUIUtility.keyboardControl != 0) return;
            float dt = Time.deltaTime;
            if (Input.GetKey(KeyCode.W)) speed = Mathf.Min(MaxSpeed, speed + 20f * dt);
            if (Input.GetKey(KeyCode.S)) speed = Mathf.Max(-10f, speed - 25f * dt);
            if (Input.GetKey(KeyCode.A)) heading -= 70f * dt;
            if (Input.GetKey(KeyCode.D)) heading += 70f * dt;
            slide = Input.GetKey(KeyCode.Q) ? -12f : Input.GetKey(KeyCode.E) ? 12f : 0f;
            if (Input.GetKey(KeyCode.LeftShift)) alt += 12f * dt;
            if (Input.GetKey(KeyCode.LeftControl)) alt -= 12f * dt;
            if (Input.GetKeyDown(KeyCode.B)) { speed = 0; strike = false; alt = drone.altitude; }
            if (Input.GetKeyDown(KeyCode.F)) BeginStrike();
        }

        void BeginStrike()
        {
            strikeVessel = null;
            if (EOIRView.TryTrack(out var tp, out _)) { strikePoint = tp; strikeVessel = EOIRView.TrackedVessel; }
            else
            {
                var cam = FlightCamera.fetch.mainCamera;
                var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, 5000f, (1 << 0) | (1 << 15) | (1 << 28));
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                bool found = false;
                foreach (var h in hits)
                {
                    var p = h.collider.GetComponentInParent<Part>();
                    if (p != null && p.vessel == drone) continue;
                    strikePoint = h.point; strikeVessel = p != null ? p.vessel : null; found = true; break;
                }
                if (!found) { ScreenMessages.PostScreenMessage("FPV: nothing under the crosshair", 2f, ScreenMessageStyle.UPPER_CENTER); return; }
            }
            strike = true;
            Debug.Log($"[BDArmoryAvionics]: FPV {drone.GetName()} strike on {(strikeVessel != null ? strikeVessel.GetName() : "ground point")}");
        }

        void FixedUpdate()
        {
            if (!Active || fc == null || drone == null) return;
            var up = (Vector3)drone.upAxis; var north = Vector3.ProjectOnPlane(drone.mainBody.transform.up, up).normalized;
            if (strike)
            {
                Vector3 tp = strikeVessel != null && strikeVessel.loaded ? (Vector3)strikeVessel.CoM : strikePoint;
                Vector3 d = tp - (Vector3)drone.CoM;
                Vector3 h = Vector3.ProjectOnPlane(d, up);
                fc.directVel = h.normalized * StrikeSpeed;
                if (h.magnitude > 2f) fc.directHeading = Vector3.SignedAngle(north, h, up);
                fc.directAlt = drone.altitude + Vector3.Dot(d, up) * 1.1f; // aim slightly below: commit to the hit
                fc.directTilt = 60f; fc.directClimbMax = 60f; fc.directGroundHug = true;
                return;
            }
            var fwd = Quaternion.AngleAxis(heading, up) * north;
            var right = Vector3.Cross(up, fwd).normalized;
            fc.directVel = fwd * speed + right * slide;
            fc.directHeading = heading;
            double floor = drone.altitude - drone.radarAltitude + 2;
            if (alt < floor) alt = floor;
            fc.directAlt = alt;
            fc.directTilt = 45f; fc.directClimbMax = 20f; fc.directGroundHug = false;
        }

        // ---- panel + OSD ----
        static Rect PanelRect => new(Screen.width / 2f - 300, Screen.height - 330, 600, 26);

        void OnGUI()
        {
            var v = FlightGlobals.ActiveVessel;
            if (!BDArmorySetup.GAME_UI_ENABLED || MapView.MapIsEnabled || v == null || !Available(v)) return;
            var r = PanelRect;
            GUI.Box(r, "", BDArmorySetup.BDGuiSkin.box);
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            if (GUI.Button(new Rect(r.x + 4, r.y + 3, 90, 20), Active ? "FPV ON" : "FPV", Active ? sel : btn)) { if (Active) Disengage("off"); else Engage(v); }
            if (Active && GUI.Button(new Rect(r.x + 98, r.y + 3, 80, 20), strike ? "STRIKING" : "STRIKE", strike ? sel : btn)) { if (strike) strike = false; else BeginStrike(); }
            if (!Active || drone == null) return;
            string osd = $"SPD {drone.srfSpeed:F0} m/s (set {speed:F0})   ALT {drone.radarAltitude:F0} m   {(strike ? $"STRIKE {((strikeVessel != null && strikeVessel.loaded ? (Vector3)strikeVessel.CoM : strikePoint) - (Vector3)drone.CoM).magnitude:F0} m" : "")}";
            AvionicsDraw.ShadowLabel(new Rect(r.x + 184, r.y + 5, r.width - 190, 16), osd, AvionicsDraw.LabelSmall, strike ? AvionicsDraw.Hostile : AvionicsDraw.Friendly);
            if (Event.current.type == EventType.Repaint && AvionicsDraw.Begin())
            {
                Vector2 c = new(Screen.width / 2f, Screen.height / 2f);
                Color col = strike ? AvionicsDraw.Hostile : new Color(1f, 1f, 1f, 0.8f);
                AvionicsDraw.Circle(c, 10, 1.5f, col, 16);
                AvionicsDraw.Line(c + new Vector2(-30, 0), c + new Vector2(-14, 0), 1.5f, col);
                AvionicsDraw.Line(c + new Vector2(14, 0), c + new Vector2(30, 0), 1.5f, col);
                AvionicsDraw.End();
            }
        }
    }
}
