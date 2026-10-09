using HarmonyLib;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// While the AI commander is spawning, block anything that tries to make a just-spawned craft the active vessel
    /// (that is what yanks the camera away from you). Logs who tried, so the culprit shows in KSP.log.
    /// Switching between your own craft still works.
    /// </summary>
    static class SpawnCameraGuard
    {
        public static bool Block(Vessel v)
        {
            var guarded = Materializer.Guarded;
            if (guarded == null || v == null || guarded.Contains(v.persistentId) || v == FlightGlobals.ActiveVessel) return false;
            Debug.Log($"[BDArmoryAvionics]: blocked a camera switch to {v.GetName()} during AI spawn\n{System.Environment.StackTrace}");
            return true;
        }
    }

    [HarmonyPatch(typeof(FlightGlobals), nameof(FlightGlobals.ForceSetActiveVessel))]
    static class Patch_ForceSetActiveVessel
    {
        static bool Prefix(Vessel v) => !SpawnCameraGuard.Block(v);
    }

    [HarmonyPatch(typeof(FlightGlobals), nameof(FlightGlobals.SetActiveVessel), new[] { typeof(Vessel) })]   // there is also an internal (Vessel, bool) overload
    static class Patch_SetActiveVessel
    {
        static bool Prefix(Vessel v, ref bool __result)
        {
            if (!SpawnCameraGuard.Block(v)) return true;
            __result = false; return false;
        }
    }
}
