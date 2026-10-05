using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// IVA prop clicks go to the front-most camera whose ray hits a collider. In IVA the flight cameras still render the
    /// world (for the windows), and in the Probe Control Room the eye sits on the drone's probe core, so their rays hit
    /// the drone's own parts first and every button click is swallowed. While in IVA, flight and scaled-space cameras
    /// stop receiving mouse events; they are restored as soon as you leave IVA.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class IvaClickFix : MonoBehaviour
    {
        bool muted;

        void LateUpdate()
        {
            var cm = CameraManager.Instance;
            bool iva = cm != null && (cm.currentCameraMode == CameraManager.CameraMode.IVA || cm.currentCameraMode == CameraManager.CameraMode.Internal);
            if (iva == muted) return;
            muted = iva;
            if (FlightCamera.fetch != null && FlightCamera.fetch.cameras != null)
                foreach (var c in FlightCamera.fetch.cameras) if (c != null) c.eventMask = iva ? 0 : ~0;
            if (ScaledCamera.Instance != null && ScaledCamera.Instance.cam != null) ScaledCamera.Instance.cam.eventMask = iva ? 0 : ~0;
            if (GalaxyCameraControl.Instance != null) foreach (var c in GalaxyCameraControl.Instance.GetComponentsInChildren<Camera>()) c.eventMask = iva ? 0 : ~0;
        }

        void OnDestroy()
        {
            if (!muted || FlightCamera.fetch == null || FlightCamera.fetch.cameras == null) return;
            foreach (var c in FlightCamera.fetch.cameras) if (c != null) c.eventMask = ~0;
        }
    }
}
