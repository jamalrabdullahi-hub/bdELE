using HarmonyLib;
using UnityEngine;

using BDArmory.Targeting;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Long-range targeting pods. BDArmory finds the pod's aim point with a physics ray, but KSP only builds terrain colliders
    /// a few km around your craft, so beyond ~5 km over land the ray hits nothing and the pod can't lock or lase.
    /// When the physics ray misses, this marches the ray against the planet's terrain height instead (works at any range,
    /// out to the pod's max range, 1500 km via the ELE patch), so the pod locks and lases far targets and ground points.
    /// </summary>
    public static class FLIRLongRange
    {
        const int Mask = (int)(LayerMasks.Parts | LayerMasks.Scenery | LayerMasks.EVA | LayerMasks.Unknown19 | LayerMasks.Unknown23 | LayerMasks.Wheels);

        /// <summary>Where the ray meets the terrain (or sea), by stepping along it against the planet's height map.</summary>
        public static bool TerrainHit(Vessel v, Ray ray, float maxDist, out Vector3 hit)
        {
            hit = default;
            var body = v.mainBody;
            if (body == null) return false;
            Vector3d center = body.position;
            float d = 50f, prev = 0f;
            for (int i = 0; i < 400 && d < maxDist; i++)
            {
                Vector3 p = ray.GetPoint(d);
                double lat = body.GetLatitude(p), lon = body.GetLongitude(p);
                double alt = (p - center).magnitude - body.Radius;
                double ground = body.TerrainAltitude(lat, lon, true);
                if (body.ocean) ground = System.Math.Max(ground, 0);
                if (alt <= ground)
                {
                    // refine between prev and d
                    float a = prev, b = d;
                    for (int k = 0; k < 14; k++)
                    {
                        float m = (a + b) * 0.5f; Vector3 q = ray.GetPoint(m);
                        double g = body.TerrainAltitude(body.GetLatitude(q), body.GetLongitude(q), true); if (body.ocean) g = System.Math.Max(g, 0);
                        if ((q - (Vector3)center).magnitude - body.Radius <= g) b = m; else a = m;
                    }
                    hit = ray.GetPoint(b);
                    return true;
                }
                // Pointing up and already well above any terrain: it won't come down.
                if (alt > 12000 && Vector3.Dot(ray.direction, (p - (Vector3)center).normalized) > 0) return false;
                prev = d;
                d += Mathf.Max(25f, d * 0.025f, (float)(alt - ground) * 0.5f); // big steps when high above the ground
            }
            return false;
        }

        static void SetGround(ModuleTargetingCamera cam, Vector3 point)
        {
            cam.groundStabilized = true;
            cam.groundTargetPosition = point;
            var geo = VectorUtils.WorldPositionToGeoCoords(point, cam.vessel.mainBody);
            if (geo != Vector3d.zero) cam.bodyRelativeGTP = geo;
            cam.lockedVessel = null;
        }

        [HarmonyPatch(typeof(ModuleTargetingCamera), "GroundStabilize")]
        static class Patch_GroundStabilize
        {
            static bool Prefix(ModuleTargetingCamera __instance)
            {
                try
                {
                    var cam = __instance;
                    if (cam.vessel == null || cam.vessel.packed || cam.cameraParentTransform == null) return true;
                    var ray = new Ray(cam.cameraParentTransform.position, cam.cameraParentTransform.forward);
                    if (Physics.Raycast(ray, cam.maxRayDistance, Mask)) return true; // close in: BDArmory's own lock (can lock vessels)
                    if (!TerrainHit(cam.vessel, ray, cam.maxRayDistance, out var p)) return true; // sky / sea: BDArmory handles it
                    SetGround(cam, p);
                    return false;
                }
                catch { return true; }
            }
        }

        static int frame;

        [HarmonyPatch(typeof(ModuleTargetingCamera), "GetHitPoint")]
        static class Patch_GetHitPoint
        {
            static bool Prefix(ModuleTargetingCamera __instance)
            {
                try
                {
                    var cam = __instance;
                    if (cam.vessel == null || cam.vessel.packed || cam.cameraParentTransform == null) return true;
                    var ray = new Ray(cam.cameraParentTransform.position, cam.cameraParentTransform.forward);
                    if (Physics.Raycast(ray, cam.maxRayDistance, Mask)) return true;
                    // Far over land: keep the point (and the lock) using the terrain. Recomputed every other frame to save CPU.
                    if ((++frame & 1) == 0 && cam.surfaceDetected) return false;
                    if (!TerrainHit(cam.vessel, ray, cam.maxRayDistance, out var p)) return true;
                    cam.targetPointPosition = p;
                    cam.surfaceDetected = true;
                    return false; // ground-stabilised points stay where they are (BDArmory keeps them from the geo position)
                }
                catch { return true; }
            }
        }
    }
}
