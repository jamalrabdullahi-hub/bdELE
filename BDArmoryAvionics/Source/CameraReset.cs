using System.Reflection;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Panic key for the camera: Ctrl+Shift+C puts the view back to KSP's normal flight camera on the active craft,
    /// whatever HullCam, the EO/IR view, FPV or the camera pan left it in.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class CameraReset : MonoBehaviour
    {
        static MethodInfo leaveCamera;

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
