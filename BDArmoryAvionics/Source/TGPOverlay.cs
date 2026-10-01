using System.Collections.Generic;
using UnityEngine;

using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Targeting pod HUD drawn over BDArmory's targeting camera image, plus the FLIR mode controls.
    /// Called from ModuleTargetingCamera.WindowTargetCam.
    /// </summary>
    public static class TGPOverlay
    {
        static readonly Dictionary<string, float[]> fovCache = new();

        public static void Draw(ModuleTargetingCamera cam, Rect img)
        {
            if (cam == null) return;
            FLIRModes.UpdateHeatSources(cam);
            FLIRModes.DrawHeatGlows(img);
            DrawControls(img);
            if (AvionicsSettings.TGPHud) DrawHud(cam, img);
        }

        static void DrawControls(Rect img)
        {
            var btn = BDArmorySetup.ButtonStyle;
            float y = img.yMax - 22;
            float x = img.x + img.width * 0.30f;
            // BDArmory starts a mouse slew on any click inside the image (before this overlay runs), so undo that for clicks on these buttons.
            var e = Event.current;
            if (e.type == EventType.MouseDown && new Rect(x, y, 240, 18).Contains(e.mousePosition))
                BDAHooks.CancelCameraSlew();
            if (GUI.Button(new Rect(x, y, 78, 18), $"FLIR {FLIRModes.ModeName}", FLIRModes.Active ? BDArmorySetup.SelectedButtonStyle : btn))
                AvionicsSettings.FLIRMode = (AvionicsSettings.FLIRMode + 1) % 4;
            x += 80;
            if (FLIRModes.Active)
            {
                if (GUI.Button(new Rect(x, y, 18, 18), "-", btn)) AvionicsSettings.FLIRContrast = Mathf.Max(1f, AvionicsSettings.FLIRContrast - 0.1f);
                x += 19;
                if (GUI.Button(new Rect(x, y, 18, 18), "+", btn)) AvionicsSettings.FLIRContrast = Mathf.Min(2.5f, AvionicsSettings.FLIRContrast + 0.1f);
                x += 20;
                AvionicsSettings.FLIRHeatGlow = GUI.Toggle(new Rect(x, y, 38, 18), AvionicsSettings.FLIRHeatGlow, "HEAT", AvionicsSettings.FLIRHeatGlow ? BDArmorySetup.SelectedButtonStyle : btn);
                x += 40;
            }
            AvionicsSettings.TGPHud = GUI.Toggle(new Rect(x, y, 34, 18), AvionicsSettings.TGPHud, "HUD", AvionicsSettings.TGPHud ? BDArmorySetup.SelectedButtonStyle : btn);
        }

        static float CurrentFOV(ModuleTargetingCamera cam)
        {
            if (!fovCache.TryGetValue(cam.zoomFOVs, out var fovs))
            {
                fovs = OtherUtils.ParseToFloatArray(cam.zoomFOVs);
                fovCache[cam.zoomFOVs] = fovs;
            }
            if (fovs == null || fovs.Length == 0) return 40;
            return fovs[Mathf.Clamp(cam.currentFovIndex, 0, fovs.Length - 1)];
        }

        static void DrawHud(ModuleTargetingCamera cam, Rect img)
        {
            bool dark = AvionicsSettings.FLIRMode == 2; // black-hot image is light, so use dark text
            Color text = dark ? new Color(0.05f, 0.05f, 0.05f) : AvionicsDraw.HudText;
            Color shadow = dark ? new Color(1, 1, 1, 0.7f) : new Color(0, 0, 0, 0.85f);
            var st = AvionicsDraw.Label;
            var stR = AvionicsDraw.LabelRight;
            var stC = AvionicsDraw.LabelCenter;

            // Mode and zoom, top centre
            string mode = cam.radarLock ? "RDR SLAVE" : cam.groundStabilized ? (cam.lockedVessel != null ? "PTRK" : "INR") : "SLEW";
            float fov = CurrentFOV(cam);
            var fovs = fovCache[cam.zoomFOVs];
            float mag = fovs != null && fovs.Length > 0 && fov > 0 ? fovs[0] / fov : 1;
            string top = $"{mode}   ×{mag:0.#}   {fov:0.#}°";
            if (cam.slaveTurrets) top += "   SLV";
            Label(new Rect(img.x + img.width * 0.25f, img.y + 4, img.width * 0.5f, 16), top, stC, text, shadow);

            // Target point
            var vessel = cam.vessel;
            Vector3 camPos = cam.cameraParentTransform != null ? cam.cameraParentTransform.position : cam.transform.position;
            bool havePoint = cam.groundStabilized || cam.surfaceDetected;
            Vector3 point = cam.groundStabilized ? cam.groundTargetPosition : cam.targetPointPosition;
            float colX = img.xMax - 118;
            float y = img.y + img.height * 0.06f + 50;
            if (havePoint && vessel != null)
            {
                Vector3 up = (Vector3)vessel.upAxis;
                Vector3 north = VectorUtils.GetNorthVector(vessel.CoM, vessel.mainBody);
                Vector3 east = Vector3.Cross(up, north);
                Vector3 d = point - vessel.CoM;
                float brg = Mathf.Repeat(Mathf.Atan2(Vector3.Dot(d, east), Vector3.Dot(d, north)) * Mathf.Rad2Deg, 360);
                double elv = FlightGlobals.getAltitudeAtPos(point, vessel.mainBody);
                Label(new Rect(colX, y, 110, 14), $"RNG {AvionicsDraw.FormatRange(Vector3.Distance(camPos, point))}", stR, text, shadow); y += 14;
                Label(new Rect(colX, y, 110, 14), $"BRG {brg:000}°", stR, text, shadow); y += 14;
                Label(new Rect(colX, y, 110, 14), $"ELV {elv:#,0} m", stR, text, shadow); y += 14;
            }
            else
            {
                Label(new Rect(colX, y, 110, 14), "RNG ---", stR, text, shadow); y += 14;
            }
            if (cam.lockedVessel != null)
            {
                y += 4;
                string n = cam.lockedVessel.GetName();
                if (n.Length > 16) n = n.Substring(0, 16);
                Label(new Rect(colX - 20, y, 130, 14), n, stR, AvionicsDraw.LockBox, shadow); y += 14;
                Label(new Rect(colX, y, 110, 14), $"{cam.lockedVessel.srfSpeed:0} m/s", stR, text, shadow);
            }

            // Laser and time to impact, bottom right above the gimbal indicator
            bool designating = cam.groundStabilized && cam.surfaceDetected && !cam.gimbalLimitReached;
            Label(new Rect(img.xMax - 118, img.yMax - 104, 110, 14), designating ? "LASER DESIG" : "LASER ---", stR, designating ? AvionicsDraw.LockBox : text, shadow);
            var wm = cam.WeaponManager;
            var msl = wm != null ? wm.PreviousMissile : null;
            if (msl != null && msl.HasFired && msl.vessel != null && msl.TimeToImpact > 0 && msl.TimeToImpact < 600)
            {
                int t = Mathf.CeilToInt(msl.TimeToImpact);
                Label(new Rect(img.xMax - 118, img.yMax - 90, 110, 14), $"TTI {t / 60:00}:{t % 60:00}", stR, text, shadow);
            }

            // Gimbal indicator
            if (cam.cameraParentTransform != null && cam.cameraParentTransform.parent != null)
            {
                Vector2 c = new(img.xMax - 30, img.yMax - 52);
                const float r = 17;
                Vector3 local = cam.cameraParentTransform.parent.InverseTransformDirection(cam.cameraParentTransform.forward);
                float off = Vector3.Angle(Vector3.forward, local);
                float frac = Mathf.Clamp01(off / Mathf.Max(1, cam.gimbalLimit));
                Vector2 dir = new(local.x, -local.y);
                if (dir.sqrMagnitude > 1e-6f) dir.Normalize();
                Color gc = cam.gimbalLimitReached ? AvionicsDraw.Missile : frac > 0.8f ? AvionicsDraw.LockBox : text;
                if (AvionicsDraw.Begin())
                {
                    AvionicsDraw.Circle(c, r, 1.2f, new Color(gc.r, gc.g, gc.b, 0.6f), 28);
                    AvionicsDraw.Circle(c + dir * frac * r, 2.5f, 2.5f, gc, 8);
                    AvionicsDraw.End();
                }
                Label(new Rect(c.x - 40, c.y + r + 1, 80, 12), cam.gimbalLimitReached ? "GIMBAL LIMIT" : "GIMBAL", AvionicsDraw.LabelCenter, gc, shadow);
            }
        }

        static void Label(Rect r, string s, GUIStyle style, Color col, Color shadow)
        {
            var old = style.normal.textColor;
            style.normal.textColor = shadow;
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), s, style);
            style.normal.textColor = col;
            GUI.Label(r, s, style);
            style.normal.textColor = old;
        }
    }
}
