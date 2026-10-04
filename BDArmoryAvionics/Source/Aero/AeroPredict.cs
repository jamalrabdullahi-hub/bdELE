using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Control power predicted from the craft itself, using KSP's own lift model (read from the game's ModuleLiftingSurface /
    /// ModuleControlSurface): surface lift = Q[Pa] x 0.036 (PhysicsGlobals.liftMultiplier) x liftCurve(sin deflection)
    /// x deflectionLiftCoeff x ctrlSurfaceArea, along the surface's forward axis. Torque about the centre of mass over the
    /// moment of inertia gives angular acceleration per unit stick: per kPa from surfaces, plus a constant from reaction wheels.
    /// Used to seed the ELE flight control model so a new aircraft flies well from its first second.
    /// </summary>
    public static class AeroPredict
    {
        const float LiftMultiplier = 0.036f, LiftSlope = 1.965f; // stock liftCurve slope near zero

        /// <summary>Per axis (pitch, yaw, roll): angular acceleration per unit stick per kPa (surfaces) and constant (reaction wheels), rad/s².</summary>
        public static (float[] perKPa, float[] constant) ControlPower(Vessel v)
        {
            var perKPa = new float[3]; var constant = new float[3];
            if (v == null) return (perKPa, constant);
            var rt = v.ReferenceTransform;
            Vector3[] axes = { rt.right, rt.forward, rt.up }; // pitch, yaw, roll axes
            Vector3 com = v.CoM;
            // Moment of inertia about each axis (point masses at part centres; small floor for compact craft)
            var I = new float[3];
            foreach (var p in v.parts)
            {
                float m = p.mass + p.GetResourceMass();
                Vector3 r = p.transform.position - com;
                for (int k = 0; k < 3; k++) { Vector3 rp = Vector3.ProjectOnPlane(r, axes[k]); I[k] += m * rp.sqrMagnitude; }
            }
            for (int k = 0; k < 3; k++) I[k] = Mathf.Max(I[k], (float)v.totalMass * 0.5f);

            foreach (var p in v.parts)
            {
                foreach (var cs in p.FindModulesImplementing<ModuleControlSurface>())
                {
                    if (cs == null) continue;
                    float defl = cs.ctrlSurfaceRange * cs.authorityLimiter * 0.01f * Mathf.Deg2Rad;
                    float force = 1000f * LiftMultiplier * LiftSlope * Mathf.Sin(defl) * cs.deflectionLiftCoeff * cs.ctrlSurfaceArea; // kN per kPa at full stick
                    Vector3 r = p.transform.position - com, n = p.transform.forward;
                    Vector3 torque = Vector3.Cross(r, n) * force;
                    bool[] used = { !cs.ignorePitch, !cs.ignoreYaw, !cs.ignoreRoll };
                    for (int k = 0; k < 3; k++) if (used[k]) perKPa[k] += Mathf.Abs(Vector3.Dot(torque, axes[k])) / I[k];
                }
                foreach (var rw in p.FindModulesImplementing<ModuleReactionWheel>())
                {
                    if (rw == null || !rw.moduleIsEnabled || rw.wheelState != ModuleReactionWheel.WheelState.Active) continue;
                    constant[0] += rw.PitchTorque / I[0]; constant[1] += rw.YawTorque / I[1]; constant[2] += rw.RollTorque / I[2];
                }
            }
            return (perKPa, constant);
        }
    }
}
