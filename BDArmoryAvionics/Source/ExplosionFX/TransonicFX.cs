using System.Collections.Generic;
using UnityEngine;

using BDArmory.Extensions;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Transonic vapor cones: a condensation collar forms around aircraft near Mach 1 in the lower, moister air
    /// (strongest at about Mach 0.98-1.02, gone by 15 km), and breaking the barrier sends out a refracting shock ring.
    /// Up to 4 cones at once, only within 5 km of the camera, using the volumetric shader (nothing without it).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class TransonicFX : MonoBehaviour
    {
        class Cone { public Transform t; public Material m; }
        readonly Dictionary<Vessel, Cone> cones = new();
        readonly Dictionary<Vessel, double> lastMach = new();
        readonly HashSet<Vessel> seen = new();
        readonly List<Vessel> drop = new();
        const int MaxCones = 4;
        const float MaxDist = 5000f;

        void LateUpdate()
        {
            if (!VolumetricFX.Enabled || !AvionicsSettings.VaporCones) { ClearAll(); return; }
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null) return;
            seen.Clear();
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed || v.isEVA || v.vesselType == VesselType.Debris || v.LandedOrSplashed) continue;
                double mach = v.mach;
                float dist = (cam.transform.position - v.CoM).magnitude;
                // Breaking the barrier: a shock ring.
                if (lastMach.TryGetValue(v, out var lm) && lm < 1.0 && mach >= 1.0 && v.altitude < 20000 && dist < MaxDist * 2)
                {
                    float r = Mathf.Clamp(v.GetRadius(), 3f, 30f);
                    VolumetricFX.Shock(v.CoM, r * 10f);
                    Debug.Log($"[BDArmoryAvionics]: {v.GetName()} went supersonic at {v.altitude:F0} m");
                }
                lastMach[v] = mach;
                float humid = Mathf.Clamp01(1f - (float)v.altitude / 15000f);
                float intensity = Mathf.Exp(-Mathf.Pow(((float)mach - 1.0f) / 0.045f, 2)) * humid;
                if (intensity < 0.04f || dist > MaxDist || v.srf_velocity.sqrMagnitude < 100) continue;
                if (!cones.TryGetValue(v, out var c))
                {
                    if (cones.Count >= MaxCones || !VolumetricFX.AcquireCone(out var t, out var m)) continue;
                    cones[v] = c = new Cone { t = t, m = m };
                }
                seen.Add(v);
                Vector3 dir = ((Vector3)v.srf_velocity).normalized;
                float rad = Mathf.Clamp(v.GetRadius(), 2f, 40f);
                c.t.rotation = Quaternion.FromToRotation(Vector3.up, dir);
                c.t.position = (Vector3)v.CoM - dir * rad * 0.25f;
                c.t.localScale = Vector3.one * rad * 2.6f;
                c.m.SetFloat("_Fade", Mathf.Clamp01(intensity * 1.3f));
                VolumetricFX.SetSun(c.t, c.m);
            }
            // Release cones whose aircraft left the transonic band, flew off, or were destroyed.
            drop.Clear();
            foreach (var kv in cones) if (kv.Key == null || !seen.Contains(kv.Key)) drop.Add(kv.Key);
            foreach (var v in drop) { VolumetricFX.ReleaseCone(cones[v].t); cones.Remove(v); }
            if (lastMach.Count > 64) lastMach.Clear();
        }

        void ClearAll() { foreach (var c in cones.Values) VolumetricFX.ReleaseCone(c.t); cones.Clear(); }
        void OnDestroy() => ClearAll();
    }
}
