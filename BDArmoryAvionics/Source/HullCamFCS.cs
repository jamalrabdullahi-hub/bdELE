using System.Collections.Generic;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Avionics
{
    /// <summary>
    /// HullCam fire-control: one target shared by radar / data link / IRST contacts, the camera track and the laser.
    /// Ctrl+arrows pan (accelerates while held), Ctrl+Home recentre, mouse wheel zoom to 120x, Tab next target (camera slaves to it),
    /// L lase: the craft's targeting pod is slaved to the camera's track (laser spot / range / GPS). Turrets follow the same track.
    /// </summary>
    [DefaultExecutionOrder(10001)] // after HullCam has placed the camera
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class HullCamFCS : MonoBehaviour
    {
        public static float Yaw, Pitch;      // pan offsets in the camera's own frame (degrees)
        public static bool Slave = true;     // camera follows the track
        public static bool Lase;             // targeting pod slaved to the track
        static PartModule cam;
        static Quaternion baseLocal, lastSetLocal;
        static int settleFrames;
        static float holdT;
        static float nextLase;
        const float MinFov = 40f / 120f;     // 120x

        public static ModuleEOIRCamera Settings => cam != null ? cam.part.FindModuleImplementing<ModuleEOIRCamera>() : null;

        /// <summary>The camera's orientation within the HullCam rig (cameras can be turned inside it, even backwards).</summary>
        static Quaternion CamLocal(FlightCamera fc) => fc.mainCamera != null && fc.mainCamera.transform.parent == fc.transform ? fc.mainCamera.transform.localRotation : Quaternion.identity;

        void LateUpdate()
        {
            var hc = HullCam.Current;
            var fc = FlightCamera.fetch;
            if (hc == null || fc == null) { cam = null; Lase = false; return; }
            var t = fc.transform;
            if (hc != cam) { cam = hc; Yaw = Pitch = 0; settleFrames = 3; HullCam.SetMinFoV(hc, MinFov); }
            // Base = HullCam's own placement. Let it settle after a switch; re-read it whenever HullCam re-places the camera.
            if (settleFrames > 0) { settleFrames--; baseLocal = t.localRotation; lastSetLocal = t.localRotation; return; }
            if (Quaternion.Angle(t.localRotation, lastSetLocal) > 0.01f) baseLocal = t.localRotation;

            var s = Settings;
            float limit = s != null ? s.panLimit : 60f;
            if (GUIUtility.keyboardControl == 0)
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                bool any = ctrl && (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow));
                holdT = any ? holdT + Time.unscaledDeltaTime : 0f;
                // fast: 90 deg/s wide, doubling after half a second held; finer when zoomed in
                float rate = 90f * Mathf.Clamp(HullCam.FoV(hc) / 40f, 0.08f, 1f) * (holdT > 0.5f ? 2f : 1f) * Time.unscaledDeltaTime;
                if (ctrl)
                {
                    if (Input.GetKey(KeyCode.LeftArrow)) { Yaw -= rate; Slave = false; }
                    if (Input.GetKey(KeyCode.RightArrow)) { Yaw += rate; Slave = false; }
                    if (Input.GetKey(KeyCode.UpArrow)) { Pitch += rate; Slave = false; }
                    if (Input.GetKey(KeyCode.DownArrow)) { Pitch -= rate; Slave = false; }
                    if (Input.GetKeyDown(KeyCode.Home)) { Yaw = Pitch = 0; Slave = false; }
                }
                if (Input.GetKeyDown(KeyCode.Tab)) NextTarget();
                if (Input.GetKeyDown(KeyCode.L)) { Lase = !Lase; ScreenMessages.PostScreenMessage(Lase ? "FCS: LASER ON (pod slaved to track)" : "FCS: laser off", 1.5f, ScreenMessageStyle.UPPER_CENTER); }
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(wheel) > 0.001f && !AvionicsCore.MouseOverAnyWindow())
                    HullCam.SetFoV(hc, Mathf.Clamp(HullCam.FoV(hc) * (wheel > 0 ? 0.8f : 1.25f), MinFov, 90f));
            }
            Quaternion camLocal = CamLocal(fc);
            // Slaved: point at the track, measured in the camera's own frame
            if (Slave && EOIRView.TryTrack(out var tp, out _))
            {
                var parentRot = t.parent != null ? t.parent.rotation : Quaternion.identity;
                Vector3 dir = Quaternion.Inverse(parentRot * baseLocal * camLocal) * (tp - fc.mainCamera.transform.position).normalized;
                Yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                Pitch = Mathf.Asin(Mathf.Clamp(dir.y, -1, 1)) * Mathf.Rad2Deg;
            }
            Yaw = Mathf.Clamp(Yaw, -limit, limit); Pitch = Mathf.Clamp(Pitch, -limit, limit);
            // rotate about the camera's own axes
            t.localRotation = baseLocal * camLocal * Quaternion.Euler(-Pitch, Yaw, 0) * Quaternion.Inverse(camLocal);
            lastSetLocal = t.localRotation;
            if (fc.mainCamera != null) fc.mainCamera.fieldOfView = HullCam.FoV(hc);
            if (Lase) SlavePod(hc.vessel);
        }

        /// <summary>Keep the craft's targeting pod pointed at the camera track (it lases / ranges / gives GPS like normal).</summary>
        static void SlavePod(Vessel v)
        {
            if (Time.time < nextLase || !EOIRView.TryTrack(out var tp, out _)) return;
            nextLase = Time.time + 0.5f;
            foreach (var p in v.parts)
            {
                var pod = p.FindModuleImplementing<ModuleTargetingCamera>();
                if (pod == null) continue;
                if (!pod.cameraEnabled) pod.EnableCamera();
                if (pod.groundStabilized && (pod.groundTargetPosition - tp).sqrMagnitude < 4f) return; // already on it
                pod.StartCoroutine(pod.PointToPositionRoutine(tp, -1, EOIRView.TrackedVessel));
                return;
            }
        }

        public static ModuleTargetingCamera Pod(Vessel v) { foreach (var p in v.parts) { var pod = p.FindModuleImplementing<ModuleTargetingCamera>(); if (pod != null) return pod; } return null; }

        /// <summary>Tab: the next contact (nearest first) becomes the track; the camera and turrets slew to it.</summary>
        public static void NextTarget()
        {
            var list = new List<Contact>();
            foreach (var c in AvionicsContacts.Contacts) if (c.vessel != null && !c.IsGPS) list.Add(c);
            if (list.Count == 0) { ScreenMessages.PostScreenMessage("FCS: no tracks", 1.5f, ScreenMessageStyle.UPPER_CENTER); return; }
            list.Sort((a, b) => a.range.CompareTo(b.range));
            int i = list.FindIndex(c => c.vessel == EOIRView.TrackedVessel);
            var next = list[(i + 1) % list.Count];
            EOIRView.TrackVessel(next.vessel);
            AvionicsContacts.Select(next);
            Slave = true;
        }
    }

    /// <summary>HUD symbology for HullCam views.</summary>
    public static class HullCamHUD
    {
        public static readonly string[] Types = { "Minimal", "Ground", "SHORAD", "ISR", "Seeker" };
        public static readonly string[] Colors = { "Green", "White", "Amber" };

        static Color HudColor(ModuleEOIRCamera s, int mode) =>
            mode == 2 ? new Color(0.08f, 0.08f, 0.08f) :
            s == null || s.hudColor == "Green" ? new Color(0.45f, 1f, 0.5f) : s.hudColor == "Amber" ? new Color(1f, 0.75f, 0.25f) : new Color(0.95f, 0.95f, 0.95f);

        static Color A(Color c, float a) => new(c.r, c.g, c.b, a);
        static readonly Color Panel = new(0f, 0f, 0f, 0.38f);

        enum FcsState { Search, Acquire, Track, Solution }

        /// <summary>Draws the HUD; returns false for "Minimal" (the plain reticle is drawn instead).</summary>
        public static bool Draw(PartModule hc, Camera cam, int mode)
        {
            var s = hc.part.FindModuleImplementing<ModuleEOIRCamera>();
            string type = s != null ? s.hudType : "Minimal";
            if (type == "Minimal") return false;
            Color c = HudColor(s, mode);
            var v = hc.vessel;
            var wm = v.ActiveController().WM;
            Vector2 c0 = new(Screen.width * 0.5f, Screen.height * 0.5f);
            bool track = EOIRView.TryTrack(out var tp, out var tv);
            float range = track ? (tp - cam.transform.position).magnitude : -1f;

            // Fire-control solution
            var gun = wm?.selectedWeapon?.GetWeaponModule();
            var missile = gun == null ? wm?.CurrentMissile : null;
            Vector3 lead = tp; float tof = 0; bool inRange = false; string weaponLine = "NO WEAPON";
            if (gun != null)
            {
                lead = track ? GunLead(gun, v, tp, tv, out tof) : tp;
                inRange = track && range <= gun.maxEffectiveDistance;
                weaponLine = $"{gun.GetShortName()}  {(gun.ammoCount > 0 ? $"AMMO {gun.ammoCount:F0}" : "")}  MAX {gun.maxEffectiveDistance / 1000f:F1} km";
            }
            else if (missile != null)
            {
                weaponLine = missile.GetShortName();
                if (track && EOIRView.TrackedVessel != null)
                {
                    var est = MissileEstimator.Estimate(missile, v.CoM, v.Velocity(), tp, tv, withMaxRange: true, targetId: EOIRView.TrackedVessel.persistentId);
                    if (est.valid) { inRange = est.canReach; tof = est.timeOfFlight; }
                }
            }
            var state = !track ? FcsState.Search : EOIRView.TrackedVessel == null ? FcsState.Acquire : inRange ? FcsState.Solution : FcsState.Track;

            if (!AvionicsDraw.Begin()) return true;
            Compass(cam, v, c);
            ElevationScale(cam, v, c);
            PanBox(c);
            Cross(c0, 14, 40, c, state == FcsState.Solution);
            if (track) TrackBox(cam, tp, c, type == "SHORAD" ? 22 : 16, state);
            if (track && (gun != null || missile != null) && state != FcsState.Acquire) LeadPipper(cam, tp, lead, c, tof, inRange);
            ThreatArrows(cam, c);
            switch (type)
            {
                case "Ground": GroundDial(cam, v, c); break;
                case "SHORAD": MiniRadar(cam, v, c); InViewContacts(cam, c); break;
                case "ISR": NorthArrow(cam, v, c); break;
                case "Seeker": SeekerSymbology(cam, c, c0, track, tp); break;
            }
            AvionicsDraw.End();

            // ---- text panels ----
            var small = AvionicsDraw.LabelSmall;
            string stateText = state switch { FcsState.Search => "SEARCH", FcsState.Acquire => "ACQ (POINT)", FcsState.Track => "TRACK", _ => "FIRE SOLUTION" };
            bool blink = Mathf.Repeat(Time.time, 0.6f) < 0.35f;
            var pod = HullCamFCS.Pod(v);
            string laser = !HullCamFCS.Lase ? "LSR OFF" : pod == null ? "LSR: NO POD" : pod.groundStabilized ? "LASING" : "LSR SLEW";
            // top-left status
            Box(new Rect(14, 70, 300, 64));
            Label(new Rect(20, 74, 290, 16), $"{type.ToUpper()} FCS   {ModuleEOIRCamera.Modes[Mathf.Clamp(mode, 0, 3)]}   {40f / Mathf.Max(0.01f, cam.fieldOfView):F0}x", c);
            Label(new Rect(20, 92, 290, 16), $"{stateText}   {(HullCamFCS.Slave ? "SLAVE" : "MAN")}   {laser}", state == FcsState.Solution && blink ? Color.white : c);
            Label(new Rect(20, 110, 290, 16), weaponLine, c);
            if (state == FcsState.Solution && blink) AvionicsDraw.ShadowLabel(new Rect(c0.x - 60, c0.y + 60, 120, 20), gun != null ? "FIRE" : "SHOOT", AvionicsDraw.LabelCenter, c);

            // right: target data
            if (track)
            {
                var tvsl = EOIRView.TrackedVessel;
                var body = v.mainBody;
                float alt = (float)body.GetAltitude(tp);
                Vector3 up = v.upAxis;
                float tHdg = tv.sqrMagnitude > 1 ? Mathf.Repeat(Vector3.SignedAngle(Vector3.ProjectOnPlane(body.transform.up, up), Vector3.ProjectOnPlane(tv, up), up), 360) : 0;
                float closure = Vector3.Dot((Vector3)v.Velocity() - tv, (tp - (Vector3)v.CoM).normalized);
                string iff = "UNK"; Color ic = c;
                foreach (var ct in AvionicsContacts.Contacts) if (ct.vessel != null && ct.vessel == tvsl) { iff = ct.iff.ToString().ToUpper(); ic = ct.IFFColor; break; }
                Rect r = new(Screen.width - 250, 150, 236, 128);
                Box(r);
                Label(new Rect(r.x + 6, r.y + 4, r.width - 12, 16), tvsl != null ? $"{tvsl.GetName()}" : "POINT TRACK", ic);
                Label(new Rect(r.x + 6, r.y + 22, r.width - 12, 16), $"IFF {iff}", ic);
                Label(new Rect(r.x + 6, r.y + 40, r.width - 12, 16), $"RNG {range / 1000f:F2} km   ALT {AvionicsDraw.FormatAlt(alt)}", c);
                Label(new Rect(r.x + 6, r.y + 58, r.width - 12, 16), $"SPD {tv.magnitude:F0} m/s   HDG {tHdg:000}", c);
                Label(new Rect(r.x + 6, r.y + 76, r.width - 12, 16), $"Vc {closure:F0} m/s   {(tof > 0 ? $"TOF {tof:F1} s" : "")}", c);
                Label(new Rect(r.x + 6, r.y + 94, r.width - 12, 16), $"{body.GetLatitude(tp):F4} {body.GetLongitude(tp):F4}", A(c, 0.8f));
            }

            // SHORAD track table
            if (type == "SHORAD")
            {
                var list = new List<Contact>();
                foreach (var ct in AvionicsContacts.Contacts) if (ct.vessel != null && !ct.IsGPS) list.Add(ct);
                list.Sort((a, b) => a.range.CompareTo(b.range));
                int n = Mathf.Min(6, list.Count);
                Rect r = new(Screen.width - 330, Screen.height - 150 - n * 16, 316, 22 + n * 16);
                Box(r);
                Label(new Rect(r.x + 6, r.y + 3, r.width, 16), "#  TRACK            RNG     ALT    Vc", A(c, 0.8f));
                for (int i = 0; i < n; i++)
                {
                    var ct = list[i]; bool on = ct.vessel == EOIRView.TrackedVessel;
                    string nm = ct.name.Length > 14 ? ct.name.Substring(0, 14) : ct.name;
                    Label(new Rect(r.x + 6, r.y + 19 + i * 16, r.width, 16), $"{(on ? ">" : " ")}{i + 1} {nm,-14} {ct.range / 1000f,5:F1}k {AvionicsDraw.FormatAlt(ct.altitude),7} {ct.closure,5:F0}", on ? Color.white : ct.IFFColor);
                }
            }
            // ground: own state
            if (type == "Ground" || type == "Seeker")
                Label(new Rect(20, 140, 400, 16), $"OWN SPD {v.srfSpeed * 3.6:F0} km/h  ALT {v.radarAltitude:F0} m", c);

            Label(new Rect(20, Screen.height - 60, 900, 16), "Ctrl+arrows pan · Ctrl+Home centre · wheel zoom · Tab next target · L laser · click to track", A(c, 0.6f));
            return true;
        }

        static void Box(Rect r) { var o = GUI.color; GUI.color = Panel; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o; }
        static void Label(Rect r, string t, Color c) => AvionicsDraw.ShadowLabel(r, t, AvionicsDraw.LabelSmall, c);

        // ---- symbology ----
        static void Cross(Vector2 c0, float gap, float len, Color c, bool solution)
        {
            float w = solution ? 2.5f : 1.5f;
            AvionicsDraw.Line(c0 + new Vector2(-gap - len, 0), c0 + new Vector2(-gap, 0), w, c);
            AvionicsDraw.Line(c0 + new Vector2(gap, 0), c0 + new Vector2(gap + len, 0), w, c);
            AvionicsDraw.Line(c0 + new Vector2(0, -gap - len), c0 + new Vector2(0, -gap), w, c);
            AvionicsDraw.Line(c0 + new Vector2(0, gap), c0 + new Vector2(0, gap + len), w, c);
            AvionicsDraw.Circle(c0, 2, 2, c, 8);
        }

        static void Compass(Camera cam, Vessel v, Color c)
        {
            Vector3 up = v.upAxis, north = Vector3.ProjectOnPlane(v.mainBody.transform.up, up).normalized;
            Vector3 look = Vector3.ProjectOnPlane(cam.transform.forward, up);
            float hdg = look.sqrMagnitude < 1e-4f ? 0 : Mathf.Repeat(Vector3.SignedAngle(north, look, up), 360);
            float cx = Screen.width * 0.5f, y = 44, pxPerDeg = 6f, half = 240;
            AvionicsDraw.FilledRect(new Rect(cx - half, y - 4, half * 2, 26), Panel);
            for (int d = Mathf.FloorToInt((hdg - 40) / 5) * 5; d <= hdg + 40; d += 5)
            {
                float x = cx + Mathf.DeltaAngle(hdg, d) * pxPerDeg;
                if (x < cx - half || x > cx + half) continue;
                AvionicsDraw.Line(new Vector2(x, y), new Vector2(x, y + (d % 10 == 0 ? 9 : 5)), 1, c);
            }
            AvionicsDraw.Triangle(new Vector2(cx, y + 18), 6, 180, 2, c);
            // text labels drawn after End() would be nicer; use GL-only ticks plus centre readout
            AvionicsDraw.End();
            for (int d = Mathf.FloorToInt((hdg - 40) / 30) * 30; d <= hdg + 40; d += 30)
            {
                float x = cx + Mathf.DeltaAngle(hdg, d) * pxPerDeg;
                if (x < cx - half + 10 || x > cx + half - 10) continue;
                int dd = (int)Mathf.Repeat(d, 360);
                string lbl = dd == 0 ? "N" : dd == 90 ? "E" : dd == 180 ? "S" : dd == 270 ? "W" : (dd / 10).ToString("00");
                AvionicsDraw.ShadowLabel(new Rect(x - 12, y + 9, 24, 14), lbl, AvionicsDraw.LabelCenter, c);
            }
            AvionicsDraw.ShadowLabel(new Rect(cx - 20, y - 20, 40, 16), $"{hdg:000}", AvionicsDraw.LabelCenter, c);
            AvionicsDraw.Begin();
        }

        static void ElevationScale(Camera cam, Vessel v, Color c)
        {
            float el = 90f - Vector3.Angle(v.upAxis, cam.transform.forward);
            float x = 70, cy = Screen.height * 0.5f, pxPerDeg = 4f;
            AvionicsDraw.Line(new Vector2(x, cy - 160), new Vector2(x, cy + 160), 1, A(c, 0.5f));
            for (int d = -90; d <= 90; d += 10)
            {
                float y = cy - (d - el) * pxPerDeg;
                if (y < cy - 160 || y > cy + 160) continue;
                AvionicsDraw.Line(new Vector2(x, y), new Vector2(x + (d % 30 == 0 ? 14 : 8), y), 1, c);
            }
            AvionicsDraw.Triangle(new Vector2(x - 8, cy), 6, 90, 2, c);
            AvionicsDraw.End();
            AvionicsDraw.ShadowLabel(new Rect(x - 60, cy - 8, 50, 16), $"{el:+0;-0}°", AvionicsDraw.LabelSmall, c);
            AvionicsDraw.Begin();
        }

        static void PanBox(Color c)
        {
            var s = HullCamFCS.Settings; float lim = s != null ? Mathf.Max(1f, s.panLimit) : 60f;
            Rect r = new(Screen.width - 130, 70, 100, 70);
            AvionicsDraw.FilledRect(r, Panel);
            AvionicsDraw.Line(new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), 1, c); AvionicsDraw.Line(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), 1, c);
            AvionicsDraw.Line(new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax), 1, c); AvionicsDraw.Line(new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMin), 1, c);
            AvionicsDraw.Line(new Vector2(r.center.x, r.yMin), new Vector2(r.center.x, r.yMax), 1, A(c, 0.3f));
            AvionicsDraw.Line(new Vector2(r.xMin, r.center.y), new Vector2(r.xMax, r.center.y), 1, A(c, 0.3f));
            Vector2 p = new(r.center.x + HullCamFCS.Yaw / lim * r.width * 0.5f, r.center.y - HullCamFCS.Pitch / lim * r.height * 0.5f);
            AvionicsDraw.Circle(p, 4, 1.5f, c, 10);
        }

        static bool ToScreen(Camera cam, Vector3 w, out Vector2 p) { var sp = cam.WorldToScreenPoint(w); p = new Vector2(sp.x, Screen.height - sp.y); return sp.z > 0; }

        static void TrackBox(Camera cam, Vector3 tp, Color c, float s, FcsState st)
        {
            if (!ToScreen(cam, tp, out var p)) return;
            float w = st == FcsState.Solution ? 2.5f : 2f;
            // corner brackets
            float k = s * 0.45f;
            foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                Vector2 corner = p + new Vector2(sx * s, sy * s);
                AvionicsDraw.Line(corner, corner - new Vector2(sx * k, 0), w, c);
                AvionicsDraw.Line(corner, corner - new Vector2(0, sy * k), w, c);
            }
            AvionicsDraw.Circle(p, 2, 2, c, 8);
        }

        static void LeadPipper(Camera cam, Vector3 tp, Vector3 lead, Color c, float tof, bool inRange)
        {
            if (!ToScreen(cam, lead, out var lp) || !ToScreen(cam, tp, out var p)) return;
            AvionicsDraw.DashedLine(p, lp, 1, A(c, 0.7f), 4, 4); // lead line
            AvionicsDraw.Circle(lp, 10, inRange ? 2.5f : 1.5f, c, 20);
            AvionicsDraw.Circle(lp, 1.5f, 2, c, 8);
            // range arc: fraction of time of flight (unwinds as the target closes)
            if (tof > 0) { float frac = Mathf.Clamp01(tof / 6f); for (int i = 0; i < 12 * frac; i++) { float a = -Mathf.PI / 2 + i * Mathf.PI * 2 / 12; AvionicsDraw.Line(lp + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 13, lp + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 16, 2, c); } }
        }

        static Vector3 GunLead(ModuleWeapon g, Vessel own, Vector3 tp, Vector3 tv, out float t)
        {
            Vector3 o = g.fireTransforms != null && g.fireTransforms.Length > 0 ? g.fireTransforms[0].position : own.CoM;
            Vector3 rel = tv - (Vector3)own.Velocity();
            float spd = Mathf.Max(g.bulletVelocity, 50f);
            t = (tp - o).magnitude / spd;
            for (int i = 0; i < 3; i++) t = (tp + rel * t - o).magnitude / spd;
            float gAcc = (float)FlightGlobals.getGeeForceAtPosition(o).magnitude;
            return tp + rel * t + (Vector3)own.upAxis * 0.5f * gAcc * t * t;
        }

        static void ThreatArrows(Camera cam, Color c)
        {
            Vector2 c0 = new(Screen.width * 0.5f, Screen.height * 0.5f);
            foreach (var th in ThreatWarning.Threats)
            {
                Vector3 local = cam.transform.InverseTransformDirection(th.position - cam.transform.position);
                Vector2 d = new(local.x, -local.y); if (d.sqrMagnitude < 1e-4f) continue;
                d.Normalize();
                Vector2 p = c0 + d * Mathf.Min(Screen.width, Screen.height) * 0.42f;
                bool blink = th.isMissile && Mathf.Repeat(Time.time, 0.4f) < 0.2f;
                AvionicsDraw.Triangle(p, blink ? 12 : 9, Vector2.SignedAngle(Vector2.down, d), 2, th.isMissile ? AvionicsDraw.Missile : th.Color);
            }
        }

        static void GroundDial(Camera cam, Vessel v, Color c)
        {
            Vector3 up = v.upAxis;
            Vector3 hull = Vector3.ProjectOnPlane(v.ReferenceTransform.up, up), look = Vector3.ProjectOnPlane(cam.transform.forward, up);
            float rel = Vector3.SignedAngle(hull, look, up);
            Vector2 hb = new(Screen.width - 80, Screen.height - 220);
            AvionicsDraw.FilledRect(new Rect(hb.x - 40, hb.y - 40, 80, 80), Panel);
            AvionicsDraw.Circle(hb, 30, 1, c, 24);
            // hull outline (pointing up = hull forward)
            AvionicsDraw.Box(hb, 12, 1.5f, A(c, 0.7f));
            float a = rel * Mathf.Deg2Rad;
            AvionicsDraw.Line(hb, hb + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * 34, 2.5f, AvionicsDraw.Friendly); // turret / camera
        }

        static void MiniRadar(Camera cam, Vessel v, Color c)
        {
            Vector2 rc = new(140, Screen.height - 300); float rpx = 100, rng = 20000f;
            AvionicsDraw.FilledRect(new Rect(rc.x - rpx - 8, rc.y - rpx - 8, rpx * 2 + 16, rpx * 2 + 16), Panel);
            for (int i = 1; i <= 4; i++) AvionicsDraw.Circle(rc, rpx * i / 4f, 1, A(c, i == 4 ? 0.9f : 0.35f), 40);
            AvionicsDraw.Line(rc + new Vector2(-rpx, 0), rc + new Vector2(rpx, 0), 1, A(c, 0.25f));
            AvionicsDraw.Line(rc + new Vector2(0, -rpx), rc + new Vector2(0, rpx), 1, A(c, 0.25f));
            // sweep
            float sw = Mathf.Repeat(Time.time * 180f, 360f) * Mathf.Deg2Rad;
            AvionicsDraw.Line(rc, rc + new Vector2(Mathf.Sin(sw), -Mathf.Cos(sw)) * rpx, 1.5f, A(c, 0.6f));
            Vector3 up = v.upAxis, fwd = Vector3.ProjectOnPlane(cam.transform.forward, up).normalized, right = Vector3.Cross(up, fwd);
            // camera field of view wedge
            float half = cam.fieldOfView * 0.5f * Mathf.Deg2Rad * cam.aspect;
            AvionicsDraw.Line(rc, rc + new Vector2(Mathf.Sin(-half), -Mathf.Cos(-half)) * rpx, 1, A(c, 0.5f));
            AvionicsDraw.Line(rc, rc + new Vector2(Mathf.Sin(half), -Mathf.Cos(half)) * rpx, 1, A(c, 0.5f));
            foreach (var ct in AvionicsContacts.Contacts)
            {
                if (ct.IsGPS) continue;
                Vector3 d = ct.position - (Vector3)v.CoM;
                Vector2 p = rc + new Vector2(Vector3.Dot(d, right), -Vector3.Dot(d, fwd)) / rng * rpx;
                if ((p - rc).magnitude > rpx) continue;
                Color ic = ct.IFFColor;
                if (ct.isMissile) AvionicsDraw.Triangle(p, 5, 0, 1.5f, ic); else AvionicsDraw.Box(p, 3.5f, 1.5f, ic);
                if (ct.velocity.sqrMagnitude > 4) { Vector2 vd = new Vector2(Vector3.Dot(ct.velocity, right), -Vector3.Dot(ct.velocity, fwd)).normalized; AvionicsDraw.Line(p, p + vd * 10, 1, ic); }
                if (ct.vessel != null && ct.vessel == EOIRView.TrackedVessel) AvionicsDraw.Circle(p, 8, 1.5f, Color.white, 14);
            }
            AvionicsDraw.End();
            AvionicsDraw.ShadowLabel(new Rect(rc.x - rpx, rc.y + rpx - 4, 120, 14), $"{rng / 1000:F0} km", AvionicsDraw.LabelSmall, c);
            AvionicsDraw.Begin();
        }

        static void InViewContacts(Camera cam, Color c)
        {
            foreach (var ct in AvionicsContacts.Contacts)
            {
                if (ct.vessel == null || ct.IsGPS || ct.vessel == EOIRView.TrackedVessel) continue;
                if (ToScreen(cam, ct.position, out var p) && p.x > 0 && p.x < Screen.width && p.y > 0 && p.y < Screen.height) AvionicsDraw.Diamond(p, 6, 1.5f, ct.IFFColor);
            }
        }

        /// <summary>
        /// Seeker / target painting: every contact in view gets a small IFF-coloured gate; the locked target gets a
        /// rotating lock diamond, closing chevrons and a pulsing ring, plus a gate box that shrinks as the seeker settles.
        /// </summary>
        static void SeekerSymbology(Camera cam, Color c, Vector2 c0, bool track, Vector3 tp)
        {
            float field = Mathf.Min(Screen.width, Screen.height) * 0.3f;
            // seeker field-of-regard box with corner marks
            foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                Vector2 k = c0 + new Vector2(sx * field, sy * field);
                AvionicsDraw.Line(k, k - new Vector2(sx * 30, 0), 2, A(c, 0.7f));
                AvionicsDraw.Line(k, k - new Vector2(0, sy * 30), 2, A(c, 0.7f));
            }
            // candidate targets in view
            foreach (var ct in AvionicsContacts.Contacts)
            {
                if (ct.vessel == null || ct.IsGPS || ct.vessel == EOIRView.TrackedVessel) continue;
                if (!ToScreen(cam, ct.position, out var p) || p.x < 0 || p.x > Screen.width || p.y < 0 || p.y > Screen.height) continue;
                Color ic = ct.IFFColor;
                AvionicsDraw.Box(p, 9, 1.5f, A(ic, 0.85f));
                AvionicsDraw.Line(p + new Vector2(-4, 0), p + new Vector2(4, 0), 1, ic);
            }
            if (!track || !ToScreen(cam, tp, out var lp)) return;
            float t = Time.time;
            // gate shrinking onto the target over ~1.5 s after a new lock
            if (lockedOn != EOIRView.TrackedVessel) { lockedOn = EOIRView.TrackedVessel; lockT0 = t; }
            float settle = Mathf.Clamp01((t - lockT0) / 1.5f);
            float gate = Mathf.Lerp(70f, 26f, settle);
            Color lc = settle >= 1f ? Color.white : c;
            // rotating lock diamond
            float rot = t * 90f * Mathf.Deg2Rad;
            for (int i = 0; i < 4; i++)
            {
                float a0 = rot + i * Mathf.PI / 2, a1 = rot + (i + 1) * Mathf.PI / 2;
                AvionicsDraw.Line(lp + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * gate * 0.7f, lp + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * gate * 0.7f, 2, lc);
            }
            // closing chevrons
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI / 2;
                Vector2 d = new(Mathf.Cos(a), Mathf.Sin(a)), n = new(-d.y, d.x);
                Vector2 tip = lp + d * (gate + 6);
                AvionicsDraw.Line(tip, tip + d * 12 + n * 7, 2, lc);
                AvionicsDraw.Line(tip, tip + d * 12 - n * 7, 2, lc);
            }
            // pulsing ring and centre dot
            float pulse = 0.5f + 0.5f * Mathf.Sin(t * 8f);
            AvionicsDraw.Circle(lp, gate + 20 + pulse * 6, 1, A(lc, 0.4f + 0.4f * pulse), 28);
            AvionicsDraw.Circle(lp, 2.5f, 2.5f, lc, 8);
            // line from boresight to the lock
            AvionicsDraw.DashedLine(c0, lp, 1, A(c, 0.5f), 6, 6);
            AvionicsDraw.End();
            AvionicsDraw.ShadowLabel(new Rect(lp.x - 50, lp.y + gate + 30, 100, 16), settle >= 1f ? "LOCKED" : "ACQUIRING", AvionicsDraw.LabelCenter, lc);
            AvionicsDraw.Begin();
        }
        static Vessel lockedOn; static float lockT0;

        static void NorthArrow(Camera cam, Vessel v, Color c)
        {
            Vector3 up = v.upAxis, north = Vector3.ProjectOnPlane(v.mainBody.transform.up, up).normalized;
            Vector3 nl = cam.transform.InverseTransformDirection(north);
            Vector2 nc = new(Screen.width - 80, Screen.height - 220), nd = new(nl.x, -nl.y);
            AvionicsDraw.FilledRect(new Rect(nc.x - 40, nc.y - 40, 80, 80), Panel);
            if (nd.sqrMagnitude > 1e-4f) { nd.Normalize(); AvionicsDraw.Line(nc, nc + nd * 30, 2, c); AvionicsDraw.Triangle(nc + nd * 30, 6, Vector2.SignedAngle(Vector2.down, nd), 2, c); }
        }
    }
}
