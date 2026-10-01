using System.Collections.Generic;
using UnityEngine;

using BDArmory.Weapons.Missiles;

namespace BDArmory.Avionics
{
    /// <summary>Predicted performance of one missile against one target from one launch state.</summary>
    public struct MissileEstimate
    {
        public bool valid;          // a model could be built for this missile
        public bool canReach;       // reaches the target with enough speed left to manoeuvre
        public float timeOfFlight;  // seconds to intercept (or until it gave up)
        public float arrivalSpeed;  // m/s at intercept
        public float maxRange;      // metres, against this target's current aspect and speed (0 if not computed)
        public bool approximate;    // modular missile: thrust and drag are estimated from its parts
    }

    /// <summary>
    /// Missile flight estimator. Simulates a missile flying a lead-pursuit line to the target:
    /// thrust phases and fuel burn from the missile's config, BDArmory's own drag model, air density
    /// along the way and gravity along the flight path. Used for launch zones, IADS time-to-intercept and the ICBM profile.
    /// </summary>
    public static class MissileEstimator
    {
        struct Phase { public float duration, thrust, massFlow; } // seconds, kN, t/s

        class Model
        {
            public readonly List<Phase> phases = new();
            public float mass;           // t at launch
            public float dragK;          // drag accel = dragK * rho * v^2
            public float dropTime;       // unpowered fall after release
            public float minSpeed = 150; // below this (after burnout) it can't catch anything
            public float maxTime = 240;
            public bool approximate;
        }

        const float Dt = 0.1f;
        static readonly Dictionary<(int, uint), (float time, MissileEstimate est)> cache = new();

        /// <summary>Time of flight and reachability from the launcher's current state; cached for a second.</summary>
        public static MissileEstimate Estimate(MissileBase m, Vector3 launchPos, Vector3 launchVel, Vector3 targetPos, Vector3 targetVel, bool withMaxRange = false, uint targetId = 0)
        {
            if (m == null) return default;
            var key = (m.GetInstanceID(), targetId);
            if (targetId != 0 && cache.TryGetValue(key, out var c) && Time.time - c.time < 1f && (!withMaxRange || c.est.maxRange > 0)) return c.est;
            var model = BuildModel(m);
            if (model == null) return default;
            var body = FlightGlobals.currentMainBody;
            var est = Simulate(model, body, launchPos, launchVel, targetPos, targetVel);
            if (withMaxRange) est.maxRange = MaxRange(model, body, launchPos, launchVel, targetPos, targetVel);
            if (targetId != 0) cache[key] = (Time.time, est);
            return est;
        }

        /// <summary>The ready missile on a vessel with the longest engagement range (what a SAM site will shoot).</summary>
        public static MissileBase BestMissile(Vessel v)
        {
            MissileBase best = null;
            foreach (var m in Utils.VesselModuleRegistry.GetModules<MissileBase>(v))
                if (m != null && !m.HasFired && !m.launched && (best == null || m.GetEngageRange() > best.GetEngageRange())) best = m;
            return best;
        }

        // ---- Model ----

        static Model BuildModel(MissileBase m)
        {
            if (m is MissileLauncher ml) return FromLauncher(ml);
            if (m is BDModularGuidance mmg) return FromModular(mmg);
            return null;
        }

        static Model FromLauncher(MissileLauncher ml)
        {
            var model = new Model
            {
                mass = Mathf.Max(0.001f, ml.part.mass),
                dragK = ml.simpleDrag * 0.004f, // BDArmory: simpleDrag * (0.008 * mass) * 0.5 * rho * v^2 / mass
                dropTime = ml.dropTime,
            };
            bool fuel = ml.useFuel;
            float boostFlow = fuel && ml.boostTime > 0 ? ml.boosterFuelMass / ml.boostTime : 0;
            float cruiseFlow = fuel && ml.cruiseTime > 0 ? ml.cruiseFuelMass / ml.cruiseTime : 0;
            if (ml.boostTime > 0) model.phases.Add(new Phase { duration = ml.boostTime, thrust = ml.thrust, massFlow = boostFlow });
            if (ml.cruiseDelay > 0) model.phases.Add(new Phase { duration = ml.cruiseDelay });
            if (ml.cruiseTime > 0) model.phases.Add(new Phase { duration = ml.cruiseTime, thrust = ml.cruiseThrust, massFlow = cruiseFlow });
            model.maxTime = Mathf.Clamp(ml.dropTime + ml.boostTime + ml.cruiseDelay + ml.cruiseTime + 90f, 30f, 400f);
            return model;
        }

        /// <summary>Modular missile: the parts below its launch decoupler, their engines and propellant.</summary>
        static Model FromModular(BDModularGuidance mmg)
        {
            var parts = MissileParts(mmg);
            if (parts.Count == 0) return null;
            float mass = 0, thrust = 0, isp = 0, prop = 0;
            foreach (var p in parts)
            {
                mass += p.mass + p.GetResourceMass();
                foreach (var e in p.FindModulesImplementing<ModuleEngines>())
                {
                    float t = e.maxThrust * (e.thrustPercentage / 100f);
                    if (t <= 0) continue;
                    float i = e.atmosphereCurve.Evaluate(0.5f); // mid-atmosphere Isp
                    isp = (isp * thrust + i * t) / (thrust + t);
                    thrust += t;
                }
                foreach (PartResource r in p.Resources)
                    if (r.info != null && r.info.density > 0 && r.resourceName != "ElectricCharge" && r.resourceName != "MonoPropellant")
                        prop += (float)(r.amount * r.info.density);
            }
            if (mass <= 0) return null;
            var model = new Model { mass = mass, approximate = true, dropTime = mmg.dropTime, dragK = 0.0008f / Mathf.Max(0.05f, Mathf.Pow(mass, 1f / 3f)) };
            if (thrust > 0 && isp > 0 && prop > 0)
            {
                float flow = thrust / (isp * 9.80665f); // t/s
                model.phases.Add(new Phase { duration = prop / flow, thrust = thrust, massFlow = flow });
            }
            model.maxTime = 400f;
            return model;
        }

        public static List<Part> MissileParts(BDModularGuidance mmg)
        {
            var list = new List<Part>();
            var dec = BDModularGuidance.FindFirstDecoupler(mmg.part.parent, null);
            if (dec == null || mmg.HasFired) Collect(mmg.vessel != null ? mmg.vessel.rootPart : mmg.part, list);
            else foreach (var c in dec.part.children) Collect(c, list);
            return list;
        }

        static void Collect(Part p, List<Part> list)
        {
            if (p == null) return;
            list.Add(p);
            foreach (var c in p.children) Collect(c, list);
        }

        // ---- Simulation ----

        static MissileEstimate Simulate(Model m, CelestialBody body, Vector3 launchPos, Vector3 launchVel, Vector3 targetPos, Vector3 targetVel)
        {
            var est = new MissileEstimate { valid = true, approximate = m.approximate };
            Vector3 pos = launchPos, vel = launchVel;
            float mass = m.mass, t = 0, phaseT = 0;
            int phase = 0;
            float closest = float.MaxValue;
            Vector3 tpos = targetPos;
            while (t < m.maxTime)
            {
                tpos = targetPos + targetVel * t;
                Vector3 los = tpos - pos;
                float dist = los.magnitude;
                float speed = vel.magnitude;
                if (dist < Mathf.Max(15f, speed * Dt * 1.5f))
                {
                    est.canReach = speed >= m.minSpeed || t < 3;
                    est.timeOfFlight = t;
                    est.arrivalSpeed = speed;
                    return est;
                }
                closest = Mathf.Min(closest, dist);

                // Thrust along a lead-pursuit heading, after the drop.
                float thrust = 0, flow = 0;
                if (t >= m.dropTime && phase < m.phases.Count)
                {
                    thrust = m.phases[phase].thrust; flow = m.phases[phase].massFlow;
                    phaseT += Dt;
                    if (phaseT >= m.phases[phase].duration) { phase++; phaseT = 0; }
                }
                Vector3 dir = LeadDirection(pos, vel, tpos, targetVel);
                Vector3 up = body != null ? (pos - (Vector3)body.position).normalized : Vector3.up;
                double alt = body != null ? FlightGlobals.getAltitudeAtPos(pos, body) : 0;
                float rho = body != null && body.atmosphere && alt < body.atmosphereDepth
                    ? (float)FlightGlobals.getAtmDensity(FlightGlobals.getStaticPressure(alt, body), FlightGlobals.getExternalTemperature(alt, body), body) : 0f;
                float g = body != null ? (float)(body.gravParameter / ((pos - (Vector3)body.position).sqrMagnitude)) : 9.81f;

                // Guided missiles turn their velocity toward the lead point; model that as velocity following the heading.
                if (t >= m.dropTime && speed > 1) vel = Vector3.RotateTowards(vel, dir * speed, 25f * Mathf.Deg2Rad * Dt, 0);
                Vector3 acc = dir * (thrust / mass) - (speed > 0 ? vel / speed : Vector3.zero) * (m.dragK * rho * speed * speed) - up * g;
                vel += acc * Dt;
                pos += vel * Dt;
                mass = Mathf.Max(m.mass * 0.2f, mass - flow * Dt);
                t += Dt;

                bool burnedOut = phase >= m.phases.Count;
                if (burnedOut && vel.magnitude < m.minSpeed && t > m.dropTime + 2) break;
                if (body != null && alt < -50) break; // hit the ground
                if (burnedOut && dist > closest + 500) break; // flying away
            }
            est.canReach = false;
            est.timeOfFlight = t;
            est.arrivalSpeed = vel.magnitude;
            return est;
        }

        static Vector3 LeadDirection(Vector3 pos, Vector3 vel, Vector3 tpos, Vector3 tvel)
        {
            Vector3 los = tpos - pos;
            float speed = Mathf.Max(vel.magnitude, 200f);
            float tgo = los.magnitude / speed;
            Vector3 aim = tpos + tvel * tgo - pos;
            return aim.sqrMagnitude > 0 ? aim.normalized : vel.normalized;
        }

        /// <summary>Longest launch range against this target's aspect and speed (binary search along the line of sight).</summary>
        static float MaxRange(Model m, CelestialBody body, Vector3 launchPos, Vector3 launchVel, Vector3 targetPos, Vector3 targetVel)
        {
            Vector3 los = targetPos - launchPos;
            float r0 = los.magnitude;
            if (r0 < 1) return 0;
            Vector3 dir = los / r0;
            float lo = 0, hi = 400000f;
            for (int i = 0; i < 14; i++)
            {
                float mid = (lo + hi) * 0.5f;
                var e = Simulate(m, body, launchPos, launchVel, launchPos + dir * mid, targetVel);
                if (e.canReach) lo = mid; else hi = mid;
            }
            return lo;
        }
    }
}
