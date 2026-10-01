using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

using BDArmory.Control;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Altitude reference for BDArmory's pilot AI. Stock BDArmory measures its altitude settings (default / min / max altitude,
    /// extend, orbit, evasion, energy regain) from the ground directly below. With "sea level" they're measured from sea level,
    /// so an AI holds a true altitude over mountains and formations, tankers and receivers all share one reference.
    /// Ground-safety logic keeps using height above the ground: terrain avoidance, the "room to loop" check, landing gear,
    /// the takeoff run and autoland.
    /// </summary>
    public static class AltitudeReference
    {
        /// <summary>The AI's altitude in the chosen reference.</summary>
        public static double Height(Vessel v) => v == null ? 0 : AvionicsSettings.AltitudeFromSeaLevel ? v.altitude : v.radarAltitude;

        /// <summary>Methods whose radar-altitude reads are altitude targets (not ground safety).</summary>
        static readonly string[] TargetMethodNames =
        [
            "AutoPilot", "UpdateAI", "FlightPosition", "FlyOrbit", "CheckExtend", "FlyExtend",
            "Evasive", "RegainEnergy", "FlyToTargetVessel",
        ];

        [HarmonyPatch]
        static class Patch_PilotAI_AltitudeReads
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var name in TargetMethodNames)
                {
                    var m = AccessTools.Method(typeof(BDModulePilotAI), name);
                    if (m != null && m.DeclaringType == typeof(BDModulePilotAI)) yield return m;
                    else Debug.LogWarning($"[BDArmoryAvionics]: Altitude reference: BDModulePilotAI.{name} not found, left on ground altitude");
                }
            }

            /// <summary>Swap "ldfld Vessel::radarAltitude" for "call AltitudeReference.Height(Vessel)" (same stack shape: Vessel in, double out).</summary>
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
            {
                var radarAlt = AccessTools.Field(typeof(Vessel), nameof(Vessel.radarAltitude));
                var height = AccessTools.Method(typeof(AltitudeReference), nameof(Height));
                int n = 0;
                foreach (var ins in instructions)
                {
                    if (ins.opcode == OpCodes.Ldfld && ins.operand is FieldInfo f && f == radarAlt)
                    {
                        var call = new CodeInstruction(OpCodes.Call, height);
                        call.MoveLabelsFrom(ins);
                        call.MoveBlocksFrom(ins);
                        n++;
                        yield return call;
                    }
                    else yield return ins;
                }
                if (n == 0) Debug.LogWarning($"[BDArmoryAvionics]: Altitude reference: no radar-altitude reads in BDModulePilotAI.{__originalMethod.Name}");
            }
        }
    }
}
