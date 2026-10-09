using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

using BDArmory.Radar;
using BDArmory.Settings;
using BDArmory.Targeting;

// KSP checks these names/versions at load time. BDArmory registers itself as "BDArmory" V1.0.0 (whatever its release number),
// and the 000_Harmony package registers as "HarmonyKSP" V1.0.0.
[assembly: KSPAssembly("BDArmoryAvionics", 1, 0)]
[assembly: KSPAssemblyDependency("BDArmory", 1, 0)]
[assembly: KSPAssemblyDependency("HarmonyKSP", 1, 0)]

namespace BDArmory.Avionics
{
    /// <summary>
    /// Everything this mod needs from BDArmory that isn't public. BDArmory.dll itself is never modified:
    /// private members are reached with reflection and a few methods get Harmony patches at runtime.
    /// </summary>
    public static class BDAHooks
    {
        static readonly FieldInfo displayedTargets = AccessTools.Field(typeof(VesselRadarData), "displayedTargets");
        static readonly FieldInfo displayedIRTargets = AccessTools.Field(typeof(VesselRadarData), "displayedIRTargets");
        static readonly FieldInfo externalVRDs = AccessTools.Field(typeof(VesselRadarData), "externalVRDs");
        static readonly MethodInfo unlinkVRD = AccessTools.Method(typeof(VesselRadarData), "UnlinkVRD");
        static readonly FieldInfo cameras = AccessTools.Field(typeof(TargetingCamera), "cameras");
        static readonly FieldInfo slewingMouseCam = AccessTools.Field(typeof(ModuleTargetingCamera), "SlewingMouseCam");

        public static IReadOnlyList<RadarDisplayData> DisplayedTargets(this VesselRadarData vrd) =>
            vrd != null && displayedTargets != null ? displayedTargets.GetValue(vrd) as List<RadarDisplayData> : null;

        public static IReadOnlyList<IRSTDisplayData> DisplayedIRTargets(this VesselRadarData vrd) =>
            vrd != null && displayedIRTargets != null ? displayedIRTargets.GetValue(vrd) as List<IRSTDisplayData> : null;

        /// <summary>IADS launch-on-remote: is this radar data already linked to the other vessel's?</summary>
        public static bool IsLinkedVRD(this VesselRadarData vrd, VesselRadarData other) =>
            vrd != null && externalVRDs != null && externalVRDs.GetValue(vrd) is List<VesselRadarData> l && l.Contains(other);

        /// <summary>IADS launch-on-remote: drop a radar link we made (BDArmory's own UnlinkVRD is private).</summary>
        public static void UnlinkExternalVRD(this VesselRadarData vrd, VesselRadarData other) => unlinkVRD?.Invoke(vrd, new object[] { other });

        public static Camera NearCamera(this TargetingCamera tc)
        {
            if (tc == null || cameras == null) return null;
            var c = cameras.GetValue(tc) as Camera[];
            return c != null && c.Length > 0 ? c[0] : null;
        }

        /// <summary>Stop the targeting camera treating a click on our overlay buttons as the start of a mouse slew.</summary>
        public static void CancelCameraSlew() => slewingMouseCam?.SetValue(null, false);

        /// <summary>Same job as BDArmory's (internal) GUIUtils.RepositionWindow: keep a window on screen.</summary>
        public static void KeepOnScreen(ref Rect r)
        {
            float s = BDArmorySettings.UI_SCALE_ACTUAL;
            r.x = Mathf.Clamp(r.x, 0, Mathf.Max(0, Screen.width - r.width * s));
            r.y = Mathf.Clamp(r.y, 0, Mathf.Max(0, Screen.height - r.height * s));
        }
    }

    /// <summary>Applies the Harmony patches once, when KSP starts.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class HarmonyLoader : MonoBehaviour
    {
        void Awake()
        {
            // Patch class by class: one patch that can't apply (a game or mod update changed its target) must not take the others down.
            var harmony = new Harmony("BDArmoryAvionics");
            int ok = 0, failed = 0;
            foreach (var t in AccessTools.GetTypesFromAssembly(typeof(HarmonyLoader).Assembly))
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                try { harmony.CreateClassProcessor(t).Patch(); ok++; }
                catch (System.Exception e) { failed++; Debug.LogError($"[BDArmoryAvionics]: Harmony patch {t.Name} failed (only that feature is affected): {e.InnerException?.Message ?? e.Message}"); }
            }
            Debug.Log($"[BDArmoryAvionics]: Harmony patches applied: {ok} ok, {failed} failed.");
        }
    }

    /// <summary>EMCON: IADS can stop a radar from switching on.</summary>
    [HarmonyPatch(typeof(ModuleRadar), nameof(ModuleRadar.EnableRadar))]
    static class Patch_ModuleRadar_EnableRadar
    {
        static bool Prefix(ModuleRadar __instance) => !(IADSController.EmconBlocked != null && IADSController.EmconBlocked(__instance));
    }

    /// <summary>
    /// FLIR: render with night lighting and BDArmory's grayscale filter (both follow TargetingCamera.nvMode),
    /// then recolour the image after BDArmory has rendered it.
    /// </summary>
    [HarmonyPatch(typeof(TargetingCamera), "RenderCameras")]
    static class Patch_TargetingCamera_RenderCameras
    {
        static void Prefix(TargetingCamera __instance, out bool __state)
        {
            __state = __instance.nvMode;
            if (FLIRModes.Active) __instance.nvMode = true;
        }

        static void Postfix(TargetingCamera __instance)
        {
            FLIRModes.PostProcess(__instance.targetCamRenderTexture);
        }

        static void Finalizer(TargetingCamera __instance, bool __state)
        {
            __instance.nvMode = __state; // always restore, even if rendering threw
        }
    }

    /// <summary>Draw the FLIR controls, heat glows and pod HUD inside BDArmory's Target Camera window.</summary>
    [HarmonyPatch(typeof(ModuleTargetingCamera), "WindowTargetCam")]
    static class Patch_ModuleTargetingCamera_WindowTargetCam
    {
        static void Postfix(ModuleTargetingCamera __instance)
        {
            if (__instance == null || !__instance.cameraEnabled || TargetingCamera.Instance == null) return;
            float size = 360f * BDArmorySettings.TARGET_WINDOW_SCALE; // same as BDArmory's image rect
            TGPOverlay.Draw(__instance, new Rect(2, 20, size, size));
        }
    }
}
