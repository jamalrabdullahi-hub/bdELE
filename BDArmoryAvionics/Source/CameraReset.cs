using System.Reflection;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Panic key for the camera: Ctrl+Shift+C puts the view back to KSP's normal flight camera on the active craft,
    /// whatever HullCam, the EO/IR view, FPV or the camera pan left it in.
    /// Also a watchdog: after every vessel switch the camera must follow the new active craft. A HullCam view left on the
    /// old craft is closed, a flight camera still aimed at (or parented to) the old craft is put back on the new one.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class CameraReset : MonoBehaviour
    {
        static MethodInfo leaveCamera;

        float checkAt = -1;

        void Start() => GameEvents.onVesselChange.Add(OnVesselChange);
        void OnDestroy() => GameEvents.onVesselChange.Remove(OnVesselChange);
        void OnVesselChange(Vessel v) { Follow(); checkAt = Time.unscaledTime + 0.5f; }   // and once more after other mods have reacted

        void LateUpdate()
        {
            if (checkAt > 0 && Time.unscaledTime >= checkAt) { checkAt = -1; Follow(); }
            else if (Time.frameCount % 60 == 0) Follow();
        }

        /// <summary>Quietly make the camera follow the active craft (no-op when it already does).</summary>
        public static void Follow()
        {
            if (KillCam.Playing || FPVDrone.Active) return;
            var fc = FlightCamera.fetch; var av = FlightGlobals.ActiveVessel;
            if (fc == null || av == null || av.packed) return;
            try
            {
                var hc = HullCam.Current;
                if (hc != null && hc.vessel != av)
                {
                    // looking through a camera on another craft while flying this one: leave it
                    leaveCamera ??= hc.GetType().GetMethod("LeaveCamera", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    leaveCamera?.Invoke(null, null);
                    Debug.Log($"[BDArmoryAvionics]: camera follow: left the HullCam on {hc.vessel?.GetName()} (active craft is {av.GetName()})");
                    hc = null;
                }
                if (hc != null) return;   // a camera on the active craft: HullCam owns the view
                var mode = CameraManager.Instance != null ? CameraManager.Instance.currentCameraMode : CameraManager.CameraMode.Flight;
                if (mode != CameraManager.CameraMode.Flight) return;
                bool fix = false;
                if (fc.transform.parent != fc.GetPivot()) { fc.transform.parent = fc.GetPivot(); fix = true; }
                if (!fc.updateActive) { fc.ActivateUpdate(); fix = true; }
                if (fc.vesselTarget != av) { fc.SetTargetVessel(av); fix = true; }
                if (fix) Debug.Log($"[BDArmoryAvionics]: camera follow: flight camera put back on {av.GetName()}");
            }
            catch (System.Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: camera follow: {e.Message}"); }
        }

        void Update()
        {
            if (GUIUtility.keyboardControl != 0) return;
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (ctrl && shift && Input.GetKeyDown(KeyCode.C)) Reset();
        }

        public static void Reset()
        {
            // Our own camera modes off
            FPVDrone.Active = false;
            HullCamFCS.Yaw = HullCamFCS.Pitch = 0; HullCamFCS.Slave = false; HullCamFCS.Lase = false;
            EOIRView.Mode = 0;
            // HullCam: its own "leave camera" restores the main camera
            try
            {
                var hc = HullCam.Current;
                if (hc != null)
                {
                    leaveCamera ??= hc.GetType().GetMethod("LeaveCamera", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    leaveCamera?.Invoke(null, null);
                }
            }
            catch (System.Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: camera reset: HullCam leave failed: {e.Message}"); }
            // KSP's flight camera back on the active vessel, updating normally
            try
            {
                var fc = FlightCamera.fetch;
                var av = FlightGlobals.ActiveVessel;
                if (fc != null && av != null)
                {
                    fc.transform.parent = fc.GetPivot(); // back under KSP's camera pivot (HullCam re-parents it to a part)
                    fc.ActivateUpdate();
                    fc.SetTargetVessel(av);
                    fc.SetFoV(fc.fovDefault);
                    if (CameraManager.Instance != null) CameraManager.Instance.SetCameraFlight();
                }
            }
            catch (System.Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: camera reset: {e.Message}"); }
            ScreenMessages.PostScreenMessage("Camera reset (Ctrl+Shift+C)", 2f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log("[BDArmoryAvionics]: camera reset to the flight camera");
        }
    }
}
