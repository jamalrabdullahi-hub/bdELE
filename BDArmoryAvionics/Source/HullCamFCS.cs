using System.Collections.Generic;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.UI;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Avionics
{
    /// <summary>
    /// HullCam fire-control: one target shared by radar / data link / IRST contacts, the camera track and the laser range.
    /// Camera: Ctrl+arrows pan, Ctrl+Home recentre, mouse wheel zoom to 120x, Tab next target (the camera slews to it and
    /// stays on it), the turrets follow the same track. The HUD on top depends on the camera's HUD type (set in the VAB).
    /// </summary>
    [DefaultExecutionOrder(10001)] // after HullCam has placed the camera
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class HullCamFCS : MonoBehaviour
    {
        public static float Yaw, Pitch;      // pan offsets (degrees)
        public static bool Slave = true;     // camera follows the track
        static PartModule cam;
        static Quaternion baseLocal;
        const float MinFov = 40f / 120f;     // 120x

        public static ModuleEOIRCamera Settings => cam != null ? cam.part.FindModuleImplementing<ModuleEOIRCamera>() : null;

        void LateUpdate()
        {
            var hc = HullCam.Current;
            var fc = FlightCamera.fetch;
            if (hc == null || fc == null) { cam = null; return; }
            var t = fc.transform;
            if (hc != cam) { cam = hc; baseLocal = t.localRotation; Yaw = Pitch = 0; HullCam.SetMinFoV(hc, MinFov); }
            var s = Settings;
            float limit = s != null ? s.panLimit : 60f;

            if (GUIUtility.keyboardControl == 0)
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                float rate = 40f * Mathf.Clamp(HullCam.FoV(hc) / 40f, 0.05f, 1f) * Time.deltaTime; // slower when zoomed in
                if (ctrl)
                {
                    if (Input.GetKey(KeyCode.LeftArrow)) { Yaw -= rate; Slave = false; }
                    if (Input.GetKey(KeyCode.RightArrow)) { Yaw += rate; Slave = false; }
                    if (Input.GetKey(KeyCode.UpArrow)) { Pitch += rate; Slave = false; }
                    if (Input.GetKey(KeyCode.DownArrow)) { Pitch -= rate; Slave = false; }
                    if (Input.GetKeyDown(KeyCode.Home)) { Yaw = Pitch = 0; Slave = false; }
                }
                if (Input.GetKeyDown(KeyCode.Tab)) NextTarget();
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(wheel) > 0.001f && !AvionicsCore.MouseOverAnyWindow())
                    HullCam.SetFoV(hc, Mathf.Clamp(HullCam.FoV(hc) * (wheel > 0 ? 0.8f : 1.25f), MinFov, 90f));
            }
            // Slaved: point at the track (within the pan limit)
            if (Slave && EOIRView.TryTrack(out var tp, out _))
            {
                var parentRot = t.parent != null ? t.parent.rotation : Quaternion.identity;
                Vector3 dirLocal = Quaternion.Inverse(parentRot * baseLocal) * (tp - t.position).normalized;
                Yaw = Mathf.Atan2(dirLocal.x, dirLocal.z) * Mathf.Rad2Deg;
                Pitch = Mathf.Asin(Mathf.Clamp(dirLocal.y, -1, 1)) * Mathf.Rad2Deg;
            }
            Yaw = Mathf.Clamp(Yaw, -limit, limit); Pitch = Mathf.Clamp(Pitch, -limit, limit);
            t.localRotation = baseLocal * Quaternion.Euler(-Pitch, Yaw, 0);
            if (fc.mainCamera != null) fc.mainCamera.fieldOfView = HullCam.FoV(hc);
        }

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

        /// <summary>Draws the HUD; returns false for "Minimal" (the plain reticle is drawn instead).</summary>
        public static bool Draw(PartModule hc, Camera cam, int mode)
        {
            var s = hc.part.FindModuleImplementing<ModuleEOIRCamera>();
            string type = s != null ? s.hudType : "Minimal";
            if (type == "Minimal") return false;
            Color c = HudColor(s, mode);
            var v = hc.vessel;
            var small = AvionicsDraw.LabelSmall;
            Vector2 c0 = new(Screen.width * 0.5f, Screen.height * 0.5f);
            bool track = EOIRView.TryTrack(out var tp, out var tv);
            float range = track ? (tp - cam.transform.position).magnitude : -1;
            var wm = v.ActiveController().WM;
            var lines = new List<string>();

            if (!AvionicsDraw.Begin()) return true;
            // Common: boresight cross, pan indicator, heading tape, track box
            Cross(c0, 16, 50, c);
            PanBox(c);
            if (track) TrackBox(cam, tp, c, type == "SHORAD" ? 22 : 16);
            switch (type)
            {
                case "Ground": Ground(cam, v, wm, c, c0, track, tp, tv, lines); break;
                case "SHORAD": Shorad(cam, v, wm, c, c0, track, tp, tv, lines); break;
                case "ISR": Isr(cam, v, c, track, tp, lines); break;
                case "Seeker": Seeker(v, c, c0, track, tp, tv, lines); break;
            }
            AvionicsDraw.End();

            // Text block, bottom left, and header
            string head = $"{type.ToUpper()}  {ModuleEOIRCamera.Modes[Mathf.Clamp(mode, 0, 3)]}  FOV {cam.fieldOfView:F2}° ({40f / Mathf.Max(0.01f, cam.fieldOfView):F0}x)  {(HullCamFCS.Slave ? "SLAVE" : "MAN")}";
            AvionicsDraw.ShadowLabel(new Rect(20, Screen.height - 220, 600, 16), head, small, c);
            float y = Screen.height - 200;
            foreach (var l in lines) { AvionicsDraw.ShadowLabel(new Rect(20, y, 600, 16), l, small, c); y += 16; }
            AvionicsDraw.ShadowLabel(new Rect(20, y + 4, 700, 16), "Ctrl+arrows pan · Ctrl+Home centre · wheel zoom · Tab next target · click to track", small, new Color(c.r, c.g, c.b, 0.6f));
            return true;
        }

        // ---- shared pieces ----
        static void Cross(Vector2 c0, float gap, float len, Color c)
        {
            AvionicsDraw.Line(c0 + new Vector2(-gap - len, 0), c0 + new Vector2(-gap, 0), 1.5f, c);
            AvionicsDraw.Line(c0 + new Vector2(gap, 0), c0 + new Vector2(gap + len, 0), 1.5f, c);
            AvionicsDraw.Line(c0 + new Vector2(0, -gap - len), c0 + new Vector2(0, -gap), 1.5f, c);
            AvionicsDraw.Line(c0 + new Vector2(0, gap), c0 + new Vector2(0, gap + len), 1.5f, c);
        }

        static void PanBox(Color c)
        {
            // small box top-right showing where the camera points within its pan limits
            var s = HullCamFCS.Settings; float lim = s != null ? s.panLimit : 60f;
            Rect r = new(Screen.width - 130, 60, 100, 70);
            AvionicsDraw.Line(new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), 1, c); AvionicsDraw.Line(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), 1, c);
            AvionicsDraw.Line(new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax), 1, c); AvionicsDraw.Line(new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMin), 1, c);
            Vector2 p = new(r.center.x + HullCamFCS.Yaw / lim * r.width * 0.5f, r.center.y - HullCamFCS.Pitch / lim * r.height * 0.5f);
            AvionicsDraw.Circle(p, 4, 1.5f, c, 10);
        }

        static void TrackBox(Camera cam, Vector3 tp, Color c, float s)
        {
            Vector3 sp = cam.WorldToScreenPoint(tp);
            if (sp.z <= 0) return;
            Vector2 p = new(sp.x, Screen.height - sp.y);
            AvionicsDraw.Box(p, s, 2, c);
        }

        static bool Screen2(Camera cam, Vector3 w, out Vector2 p) { var sp = cam.WorldToScreenPoint(w); p = new Vector2(sp.x, Screen.height - sp.y); return sp.z > 0; }

        static Vector3 GunLead(ModuleWeapon g, Vessel own, Vector3 tp, Vector3 tv)
        {
            Vector3 o = g.fireTransforms != null && g.fireTransforms.Length > 0 ? g.fireTransforms[0].position : own.CoM;
            Vector3 rel = tv - (Vector3)own.Velocity();
            float spd = Mathf.Max(g.bulletVelocity, 50f), t = (tp - o).magnitude / spd;
            for (int i = 0; i < 2; i++) t = (tp + rel * t - o).magnitude / spd;
            float gAcc = (float)FlightGlobals.getGeeForceAtPosition(o).magnitude;
            return tp + rel * t + (Vector3)own.upAxis * 0.5f * gAcc * t * t;
        }

        static void ThreatArrows(Camera cam, Color c)
        {
            Vector2 c0 = new(Screen.width * 0.5f, Screen.height * 0.5f);
            foreach (var th in ThreatWarning.Threats)
            {
                Vector3 local = cam.transform.InverseTransformDirection(th.position - cam.transform.position);
                Vector2 d = new Vector2(local.x, -local.y); if (d.sqrMagnitude < 1e-4f) continue;
                d.Normalize();
                Vector2 p = c0 + d * Mathf.Min(Screen.width, Screen.height) * 0.42f;
                AvionicsDraw.Triangle(p, 9, Vector2.SignedAngle(Vector2.down, d), 2, th.isMissile ? AvionicsDraw.Missile : th.Color);
            }
        }

        // ---- 1. Ground combat drone ----
        static void Ground(Camera cam, Vessel v, MissileFire wm, Color c, Vector2 c0, bool track, Vector3 tp, Vector3 tv, List<string> lines)
        {
            // range ticks under the cross
            for (int i = 1; i <= 4; i++) AvionicsDraw.Line(c0 + new Vector2(-6, 18 + i * 10), c0 + new Vector2(6, 18 + i * 10), 1, c);
            var gun = wm?.selectedWeapon?.GetWeaponModule();
            if (gun != null && track && Screen2(cam, GunLead(gun, v, tp, tv), out var lp)) { AvionicsDraw.Circle(lp, 6, 2, c, 12); AvionicsDraw.Line(lp + new Vector2(0, -10), lp + new Vector2(0, 10), 1, c); }
            ThreatArrows(cam, c);
            // hull vs camera heading
            Vector3 up = v.upAxis;
            Vector3 hull = Vector3.ProjectOnPlane(v.ReferenceTransform.up, up), look = Vector3.ProjectOnPlane(cam.transform.forward, up);
            float rel = Vector3.SignedAngle(hull, look, up);
            Vector2 hb = new(Screen.width - 80, Screen.height - 120);
            AvionicsDraw.Circle(hb, 26, 1, c, 20);
            AvionicsDraw.Line(hb, hb + new Vector2(0, -24), 2, c); // hull forward
            float a = rel * Mathf.Deg2Rad; AvionicsDraw.Line(hb, hb + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * 34, 2.5f, AvionicsDraw.Friendly); // turret/camera
            lines.Add($"SPD {v.srfSpeed * 3.6:F0} km/h  HDG {Heading(v):000}  CAM {rel:+0;-0}°");
            if (gun != null) lines.Add($"{gun.GetShortName()}  AMMO {(gun.ammoCount > 0 ? gun.ammoCount.ToString("F0") : "-")}");
            if (track) lines.Add($"TGP RNG {(tp - cam.transform.position).magnitude:F0} m  {(wm != null && wm.guardMode ? "AUTO TRACK / AUTO FIRE" : "TRACK")}");
            else lines.Add(wm != null && wm.guardMode ? "AUTO SEARCH" : "NO TRACK");
        }

        // ---- 2. SHORAD with merged FCS and mini radar ----
        static void Shorad(Camera cam, Vessel v, MissileFire wm, Color c, Vector2 c0, bool track, Vector3 tp, Vector3 tv, List<string> lines)
        {
            AvionicsDraw.Circle(c0, Screen.height * 0.18f, 1, new Color(c.r, c.g, c.b, 0.6f), 48); // search circle
            ThreatArrows(cam, c);
            var gun = wm?.selectedWeapon?.GetWeaponModule();
            if (gun != null && track && Screen2(cam, GunLead(gun, v, tp, tv), out var lp)) { AvionicsDraw.Circle(lp, 8, 2, c, 14); AvionicsDraw.Line(lp, lp + new Vector2(0, 14), 1, c); }
            // contacts in view: small boxes
            foreach (var ct in AvionicsContacts.Contacts)
            {
                if (ct.vessel == null || ct.IsGPS) continue;
                if (Screen2(cam, ct.position, out var p) && p.x > 0 && p.x < Screen.width && p.y > 0 && p.y < Screen.height) AvionicsDraw.Diamond(p, 5, 1.5f, ct.IFFColor);
            }
            // mini radar (plan view, heading up)
            Vector2 rc = new(140, Screen.height - 330); float rpx = 90, rng = 20000f;
            AvionicsDraw.FilledRect(new Rect(rc.x - rpx - 6, rc.y - rpx - 6, rpx * 2 + 12, rpx * 2 + 12), new Color(0, 0, 0, 0.45f));
            AvionicsDraw.Circle(rc, rpx, 1, c, 40); AvionicsDraw.Circle(rc, rpx * 0.5f, 1, new Color(c.r, c.g, c.b, 0.4f), 32);
            Vector3 up = v.upAxis, fwd = Vector3.ProjectOnPlane(cam.transform.forward, up).normalized, right = Vector3.Cross(up, fwd);
            AvionicsDraw.Line(rc, rc + new Vector2(0, -rpx), 1, new Color(c.r, c.g, c.b, 0.5f)); // look direction
            foreach (var ct in AvionicsContacts.Contacts)
            {
                if (ct.IsGPS) continue;
                Vector3 d = ct.position - (Vector3)v.CoM;
                Vector2 p = rc + new Vector2(Vector3.Dot(d, right), -Vector3.Dot(d, fwd)) / rng * rpx;
                if ((p - rc).magnitude > rpx) continue;
                Color ic = ct.IFFColor;
                if (ct.isMissile) AvionicsDraw.Triangle(p, 5, 0, 1.5f, ic); else AvionicsDraw.Box(p, 3, 1.5f, ic);
                if (ct.vessel != null && ct.vessel == EOIRView.TrackedVessel) AvionicsDraw.Circle(p, 7, 1.5f, Color.white, 12);
            }
            // track list (nearest 5)
            var list = new List<Contact>();
            foreach (var ct in AvionicsContacts.Contacts) if (ct.vessel != null && !ct.IsGPS) list.Add(ct);
            list.Sort((a, b) => a.range.CompareTo(b.range));
            for (int i = 0; i < list.Count && i < 5; i++)
            {
                var ct = list[i];
                bool on = ct.vessel == EOIRView.TrackedVessel;
                lines.Add($"{(on ? ">" : " ")}{i + 1} {ct.name,-14} {ct.range / 1000f:F1} km  {AvionicsDraw.FormatAlt(ct.altitude)}  Vc {ct.closure:F0}");
            }
            // missile in-range cue
            var m = wm?.CurrentMissile;
            if (m != null && track && EOIRView.TrackedVessel != null)
            {
                var est = MissileEstimator.Estimate(m, v.CoM, v.Velocity(), tp, tv, withMaxRange: true, targetId: EOIRView.TrackedVessel.persistentId);
                if (est.valid)
                {
                    bool shoot = est.canReach;
                    lines.Add($"{m.GetShortName()}  {(shoot ? $"SHOOT  TTI {est.timeOfFlight:F0}s" : "OUT OF RANGE")}");
                    if (shoot && Mathf.Repeat(Time.time, 0.6f) < 0.35f) AvionicsDraw.ShadowLabel(new Rect(c0.x - 60, c0.y + 70, 120, 20), "SHOOT", AvionicsDraw.LabelCenter, c);
                }
            }
            if (track) lines.Add($"TRACK {(tp - cam.transform.position).magnitude / 1000f:F2} km  {tv.magnitude:F0} m/s");
        }

        // ---- 3. ISR / targeting pod ----
        static void Isr(Camera cam, Vessel v, Color c, bool track, Vector3 tp, List<string> lines)
        {
            var body = v.mainBody;
            Vector3 pt = track ? tp : (Physics.Raycast(cam.transform.position, cam.transform.forward, out var hit, 200000f, (1 << 15) | (1 << 28) | 1) ? hit.point : Vector3.zero);
            if (pt != Vector3.zero)
            {
                lines.Add($"POINT {body.GetLatitude(pt):F5} {body.GetLongitude(pt):F5}  ELEV {body.GetAltitude(pt):F0} m");
                lines.Add($"LRF {(pt - cam.transform.position).magnitude:F0} m  {(track ? "TRACK" : "BORESIGHT")}");
            }
            // north arrow
            Vector3 up = v.upAxis, north = Vector3.ProjectOnPlane(body.transform.up, up).normalized;
            Vector3 nl = cam.transform.InverseTransformDirection(north);
            Vector2 nc = new(Screen.width - 80, Screen.height - 120), nd = new Vector2(nl.x, -nl.y);
            if (nd.sqrMagnitude > 1e-4f) { nd.Normalize(); AvionicsDraw.Line(nc, nc + nd * 30, 2, c); AvionicsDraw.ShadowLabel(new Rect(nc.x + nd.x * 40 - 6, nc.y + nd.y * 40 - 8, 14, 16), "N", AvionicsDraw.LabelSmall, c); }
            lines.Add($"OWN ALT {v.altitude:F0} m  HDG {Heading(v):000}");
        }

        // ---- 4. Seeker / loitering munition ----
        static void Seeker(Vessel v, Color c, Vector2 c0, bool track, Vector3 tp, Vector3 tv, List<string> lines)
        {
            float box = Mathf.Min(Screen.width, Screen.height) * 0.3f;
            AvionicsDraw.Box(c0, box, 1, new Color(c.r, c.g, c.b, 0.5f));
            if (track)
            {
                Vector3 rel = tp - (Vector3)v.CoM;
                lines.Add($"LOCKED  RNG {rel.magnitude:F0} m  Vc {Vector3.Dot((Vector3)v.Velocity() - tv, rel.normalized):F0} m/s  TTI {(Vector3.Dot((Vector3)v.Velocity() - tv, rel.normalized) > 1 ? rel.magnitude / Vector3.Dot((Vector3)v.Velocity() - tv, rel.normalized) : 0):F0}s");
            }
            else lines.Add(Mathf.Repeat(Time.time, 1f) < 0.5f ? "SEARCH" : "");
            double fuel = 0, cap = 0;
            foreach (var p in v.parts) foreach (PartResource r in p.Resources) if (r.resourceName != "ElectricCharge") { fuel += r.amount; cap += r.maxAmount; }
            lines.Add($"FUEL {(cap > 0 ? fuel / cap : 0):P0}  ALT {v.radarAltitude:F0} m  SPD {v.srfSpeed:F0} m/s");
        }

        static float Heading(Vessel v)
        {
            Vector3 up = v.upAxis, north = Vector3.ProjectOnPlane(v.mainBody.transform.up, up).normalized;
            return Mathf.Repeat(Vector3.SignedAngle(north, Vector3.ProjectOnPlane(v.ReferenceTransform.up, up), up), 360);
        }
    }
}
