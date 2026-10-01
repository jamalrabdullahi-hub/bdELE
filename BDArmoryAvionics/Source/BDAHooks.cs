using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

using BDArmory.Radar;
using BDArmory.Targeting;

[assembly: KSPAssembly("BDArmoryAvionics", 0, 3)]
[assembly: KSPAssemblyDependency("BDArmory", 1, 0)]   // BDArmory registers itself as V1.0.0
[assembly: KSPAssemblyDependency("HarmonyKSP", 1, 0)] // the 000_Harmony package registers as HarmonyKSP

namespace BDArmory.Avionics
{
    /// <summary>
    /// Everything this mod needs from inside stock BDArmory, so BDArmory.dll itself stays unmodified:
    /// read access to a few private fields and methods, and Harmony patches for the radar EMCON gate and the targeting camera.
    /// </summary>
    public static class BDAAccess
    {
        static readonly AccessTools.FieldRef<VesselRadarData, List<RadarDisplayData>> displayedTargets =
            AccessTools.FieldRefAccess<VesselRadarData, List<RadarDisplayData>>("displayedTargets");
        static readonly AccessTools.FieldRef<VesselRadarData, List<IRSTDisplayData>> displayedIRTargets =
            AccessTools.FieldRefAccess<VesselRadarData, List<IRSTDisplayData>>("displayedIRTargets");
        static readonly AccessTools.FieldRef<VesselRadarData, List<VesselRadarData>> externalVRDs =
            AccessTools.FieldRefAccess<VesselRadarData, List<VesselRadarData>>("externalVRDs");
        static readonly AccessTools.FieldRef<TargetingCamera, Camera[]> cameras =
            AccessTools.FieldRefAccess<TargetingCamera, Camera[]>("cameras");
        static readonly MethodInfo unlinkVRD = AccessTools.Method(typeof(VesselRadarData), "UnlinkVRD");
        static readonly FieldInfo camImageSize = AccessTools.Field(typeof(ModuleTargetingCamera), "adjCamImageSize");

        public static IReadOnlyList<RadarDisplayData> DisplayedTargets(this VesselRadarData vrd) => vrd != null ? displayedTargets(vrd) : null;
        public static IReadOnlyList<IRSTDisplayData> DisplayedIRTargets(this VesselRadarData vrd) => vrd != null ? displayedIRTargets(vrd) : null;
        public static bool IsLinkedVRD(this VesselRadarData vrd, VesselRadarData other) { var l = externalVRDs(vrd); return l != null && l.Contains(other); }
        public static void UnlinkExternalVRD(this VesselRadarData vrd, VesselRadarData other) => unlinkVRD.Invoke(vrd, new object[] { other });
        public static Camera NearCamera(this TargetingCamera tc) { var c = tc != null ? cameras(tc) : null; return c != null && c.Length > 0 ? c[0] : null; }
        public static float CamImageSize => camImageSize != null ? (float)camImageSize.GetValue(null) : 360f;
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class BDAHooks : MonoBehaviour
    {
        void Awake()
        {
            try
            {
                new Harmony("BDArmoryAvionics").PatchAll(typeof(BDAHooks).Assembly);
                Debug.Log("[BDArmoryAvionics]: Harmony patches applied");
            }
            catch (Exception e)
            {
                Debug.LogError($"[BDArmoryAvionics]: Harmony patching failed, EMCON and FLIR will not work: {e}");
            }
        }
    }

    /// <summary>EMCON: IADS can stop a radar from switching on.</summary>
    [HarmonyPatch(typeof(ModuleRadar), nameof(ModuleRadar.EnableRadar))]
    static class Patch_ModuleRadar_EnableRadar
    {
        static bool Prefix(ModuleRadar __instance) => !(IADSController.EmconBlocked != null && IADSController.EmconBlocked(__instance));
    }

    /// <summary>FLIR uses the grayscale pass even in colour mode.</summary>
    [HarmonyPatch(typeof(TGPCameraEffects), "OnRenderImage")]
    static class Patch_TGPCameraEffects_OnRenderImage
    {
        static bool Prefix(RenderTexture source, RenderTexture destination)
        {
            if (!FLIRModes.Active) return true;
            Graphics.Blit(source, destination, TGPCameraEffects.grayscaleMaterial);
            return false;
        }
    }

    /// <summary>FLIR sees at night (borrows the night-vision lighting), then post-processes the image.</summary>
    [HarmonyPatch(typeof(TargetingCamera), "RenderCameras")]
    static class Patch_TargetingCamera_RenderCameras
    {
        static void Prefix(TargetingCamera __instance, out bool __state)
        {
            __state = FLIRModes.Active && !__instance.nvMode;
            if (__state) __instance.nvMode = true;
        }

        static void Postfix(TargetingCamera __instance, bool __state)
        {
            if (__state) __instance.nvMode = false;
            FLIRModes.PostProcess(__instance.targetCamRenderTexture);
        }
    }

    /// <summary>Draw the FLIR controls and pod HUD right after the camera image and reticle, before BDArmory's own buttons.</summary>
    [HarmonyPatch(typeof(ModuleTargetingCamera), "WindowTargetCam")]
    static class Patch_ModuleTargetingCamera_WindowTargetCam
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getReticle = AccessTools.PropertyGetter(typeof(TargetingCamera), nameof(TargetingCamera.ReticleTexture));
            var hook = AccessTools.Method(typeof(Patch_ModuleTargetingCamera_WindowTargetCam), nameof(DrawOverlay));
            bool sawReticle = false, done = false;
            foreach (var ins in instructions)
            {
                yield return ins;
                if (done) continue;
                if (ins.Calls(getReticle)) sawReticle = true;
                else if (sawReticle && ins.opcode == OpCodes.Call && ins.operand is MethodInfo m && m.Name == "DrawTexture" && m.DeclaringType == typeof(GUI))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, hook);
                    done = true;
                }
            }
            if (!done) Debug.LogWarning("[BDArmoryAvionics]: Targeting camera window not recognised; pod HUD and FLIR controls disabled");
        }

        static void DrawOverlay(ModuleTargetingCamera cam)
        {
            float s = BDAAccess.CamImageSize;
            TGPOverlay.Draw(cam, new Rect(2, 20, s, s));
        }
    }
}
