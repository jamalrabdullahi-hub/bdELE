using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Targeting;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>A runway: threshold A, far end B (lat/lon), elevation from the terrain.</summary>
    public class Runway
    {
        public string name;
        public CelestialBody body;
        public double latA, lonA, latB, lonB;

        public Vector3d A => body.GetWorldSurfacePosition(latA, lonA, body.TerrainAltitude(latA, lonA, true));
        public Vector3d B => body.GetWorldSurfacePosition(latB, lonB, body.TerrainAltitude(latB, lonB, true));
        public double Elevation => body.TerrainAltitude(latA, lonA, true);
        public Runway Reversed => new() { name = name + " (rev)", body = body, latA = latB, lonA = lonB, latB = latA, lonB = lonA };
    }

    public static class Runways
    {
        /// <summary>Built-in KSC runway (09 threshold to 27 threshold), plus any pair of GPS points named "&lt;name&gt; RWY A" and "&lt;name&gt; RWY B".</summary>
        public static List<Runway> All(CelestialBody body, BDArmory.Competition.BDTeam team)
        {
            var list = new List<Runway>();
            if (body == FlightGlobals.GetHomeBody())
                list.Add(new Runway { name = "KSC", body = body, latA = -0.0486, lonA = -74.7247, latB = -0.0502, lonB = -74.4910 });
            list.AddRange(FactionsScenario.RunwaysFor(body, team)); // faction bases (Kerbal Konstructs runways)
            if (team != null)
            {
                var gps = BDATargetManager.GPSTargetList(team);
                foreach (var a in gps)
                {
                    if (!a.name.EndsWith(" RWY A")) continue;
                    string n = a.name.Substring(0, a.name.Length - 6);
                    foreach (var b in gps)
                        if (b.name == n + " RWY B")
                            list.Add(new Runway { name = n, body = body, latA = a.gpsCoordinates.x, lonA = a.gpsCoordinates.y, latB = b.gpsCoordinates.x, lonB = b.gpsCoordinates.y });
                }
            }
            return list;
        }

        /// <summary>Nearest runway, oriented so the approach needs the least turning from where the aircraft is.</summary>
        public static Runway Best(Vessel v, BDArmory.Competition.BDTeam team)
        {
            Runway best = null; double bestD = double.MaxValue;
            foreach (var r in All(v.mainBody, team))
                foreach (var rr in new[] { r, r.Reversed })
                {
                    Vector3d dir = (rr.B - rr.A).normalized;
                    Vector3d faf = rr.A - dir * 8000;
                    double d = (faf - v.CoMD).magnitude;
                    if (d < bestD) { bestD = d; best = rr; }
                }
            return best;
        }
    }

    public enum AirOpKind { Landing, Refuel, Tanker }

    /// <summary>
    /// Air operations for BDArmory's pilot AI: takeoff assist (always on when enabled), and landing / air-to-air refuelling / tanker
    /// tasks that take over the AI while they run. Flying still goes through BDArmory's own FlyToPosition, so each aircraft's PID tuning applies.
    /// </summary>
    public static class AirOps
    {
        // ---- BDArmory internals ----
        static readonly AccessTools.FieldRef<BDModulePilotAI, Vector3> upDirection = AccessTools.FieldRefAccess<BDModulePilotAI, Vector3>("upDirection");
        static readonly AccessTools.FieldRef<BDModulePilotAI, float> finalMaxSteer = AccessTools.FieldRefAccess<BDModulePilotAI, float>("finalMaxSteer");
        static readonly AccessTools.FieldRef<BDModulePilotAI, float> userSteerLimit = AccessTools.FieldRefAccess<BDModulePilotAI, float>("userSteerLimit");
        static readonly AccessTools.FieldRef<BDModulePilotAI, bool> belowMinAltitude = AccessTools.FieldRefAccess<BDModulePilotAI, bool>("belowMinAltitude");
        static readonly AccessTools.FieldRef<BDModulePilotAI, bool> useVelRollTarget = AccessTools.FieldRefAccess<BDModulePilotAI, bool>("useVelRollTarget");
        static readonly FieldInfo steerMode = AccessTools.Field(typeof(BDModulePilotAI), "steerMode");
        static readonly MethodInfo flyToPosition = AccessTools.Method(typeof(BDModulePilotAI), "FlyToPosition");
        static readonly MethodInfo adjustThrottle = AccessTools.Method(typeof(BDModulePilotAI), "AdjustThrottle");
        static readonly MethodInfo getUserSteerLimit = AccessTools.Method(typeof(BDModulePilotAI), "GetUserDefinedSteerLimit");
        static readonly MethodInfo updateVelDirs = AccessTools.Method(typeof(BDModulePilotAI), "UpdateVelocityRelativeDirections");
        static readonly MethodInfo updateGLimits = AccessTools.Method(typeof(BDModulePilotAI), "UpdateGAndAoALimits");
        static readonly MethodInfo adjustPitchG = AccessTools.Method(typeof(BDModulePilotAI), "AdjustPitchForGAndAoALimits");
        static readonly MethodInfo setStatus = AccessTools.Method(typeof(BDModulePilotAI), "SetStatus");

        static void Fly(BDModulePilotAI ai, FlightCtrlState s, Vector3 target, bool corrections) => flyToPosition.Invoke(ai, new object[] { s, target, true, corrections });
        static void Throttle(BDModulePilotAI ai, float speed, bool brakes, bool ab, float overrideThrottle = -1f) => adjustThrottle.Invoke(ai, new object[] { speed, brakes, ab, false, overrideThrottle });
        static void Status(BDModulePilotAI ai, string text) => setStatus.Invoke(ai, new object[] { text });

        /// <summary>The same per-frame setup BDArmory's AutoPilot does before steering.</summary>
        static void Prepare(BDModulePilotAI ai, bool lowFlying)
        {
            upDirection(ai) = ai.vessel.up;
            finalMaxSteer(ai) = 1f;
            userSteerLimit(ai) = (float)getUserSteerLimit.Invoke(ai, null);
            steerMode.SetValue(ai, Enum.ToObject(steerMode.FieldType, 0)); // NormalFlight
            useVelRollTarget(ai) = false;
            // Below min altitude BDArmory forces the wings level, which stops a landing aircraft banking back onto the centreline.
            // Landing: only treat it as "low" in the last 30 m.
            belowMinAltitude(ai) = ai.vessel.radarAltitude < (lowFlying ? 30f : ai.minAltitude);
            updateVelDirs.Invoke(ai, null);
            ai.vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
        }

        static void Finish(BDModulePilotAI ai, FlightCtrlState s)
        {
            updateGLimits.Invoke(ai, new object[] { s });
            adjustPitchG.Invoke(ai, new object[] { s });
        }

        // ---- Operations ----

        public class Op
        {
            public AirOpKind kind;
            public string phase = "start";
            public float phaseStart;
            public Runway runway;
            public int attempts;
            public Vessel tanker;
            public PartModule drogue;          // AARS FuelDrogue on the tanker
            public PartModule probe;           // AARS FuelProbe on this aircraft
            public float stableSince = -1, contactLostSince = -1;
            public Vector3 tankerHeading;
            public double tankerAlt;
            public float tankerSpeed;
            public float smoothedSpeed = -1;
            public float lastLog;
        }

        public static readonly Dictionary<BDModulePilotAI, Op> Ops = new();

        public static BDModulePilotAI PilotOf(Vessel v) => v != null ? v.ActiveController().PilotAI : null;

        public static Op OpFor(Vessel v) { var ai = PilotOf(v); return ai != null && Ops.TryGetValue(ai, out var o) ? o : null; }

        public static string Describe(Vessel v)
        {
            var o = OpFor(v);
            if (o == null) return null;
            return o.kind switch
            {
                AirOpKind.Landing => $"Land {o.runway?.name} ({o.phase})",
                AirOpKind.Refuel => $"Refuel from {(o.tanker != null ? o.tanker.GetName() : "?")} ({o.phase})",
                _ => $"Tanker track ({o.phase})",
            };
        }

        public static bool StartLanding(Vessel v)
        {
            var ai = PilotOf(v);
            if (ai == null) { Msg("No pilot AI on this craft"); return false; }
            var team = v.ActiveController().WM?.Team;
            var rw = Runways.Best(v, team);
            if (rw == null) { Msg("No runway: drop GPS points named \"<name> RWY A\" and \"<name> RWY B\" on the runway ends"); return false; }
            Ops[ai] = new Op { kind = AirOpKind.Landing, runway = rw, phase = "approach", phaseStart = Time.time };
            ai.ActivatePilot();
            Log(v, $"landing at {rw.name}");
            return true;
        }

        public static bool StartRefuel(Vessel v, Vessel tanker)
        {
            var ai = PilotOf(v);
            if (ai == null || tanker == null) return false;
            var probe = FindModule(v, "FuelProbe");
            if (probe == null) { Msg($"{v.GetName()} has no AARS refuelling probe"); return false; }
            var drogue = FindModule(tanker, "FuelDrogue");
            if (drogue == null) { Msg($"{tanker.GetName()} has no AARS drogue"); return false; }
            DeployTanker(tanker);
            Invoke(probe, "Activate");
            TargetDrogue(v, probe, drogue, tanker);
            Ops[ai] = new Op { kind = AirOpKind.Refuel, tanker = tanker, drogue = drogue, probe = probe, phase = "join", phaseStart = Time.time };
            ai.ActivatePilot();
            Log(v, $"refuelling from {tanker.GetName()}: probe tip '{ProbeTransform(probe).name}' on {probe.part.partInfo.title}, basket '{DrogueTransform(drogue).name}' on {drogue.part.partInfo.title}");
            return true;
        }

        public static bool StartTanker(Vessel v)
        {
            var ai = PilotOf(v);
            if (ai == null) return false;
            if (FindModule(v, "FuelDrogue") == null) { Msg($"{v.GetName()} has no AARS drogue"); return false; }
            DeployTanker(v);
            Vector3 h = Vector3.ProjectOnPlane(v.srf_velocity.sqrMagnitude > 25 ? (Vector3)v.srf_velocity : v.ReferenceTransform.up, v.up).normalized;
            Ops[ai] = new Op { kind = AirOpKind.Tanker, phase = "track", tankerHeading = h, tankerAlt = Math.Max(v.altitude, 1500), tankerSpeed = Mathf.Clamp((float)v.srfSpeed, ai.minSpeed + 30, ai.idleSpeed) };
            ai.ActivatePilot();
            Log(v, "tanker track");
            return true;
        }

        public static void Cancel(Vessel v)
        {
            var ai = PilotOf(v);
            if (ai == null || !Ops.TryGetValue(ai, out var o)) return;
            if (o.probe != null) Invoke(o.probe, "Deactivate");
            Ops.Remove(ai);
            v.ActionGroups.SetGroup(KSPActionGroup.Brakes, false);
        }

        /// <summary>Called instead of BDArmory's AutoPilot while an operation runs.</summary>
        public static bool Run(BDModulePilotAI ai, FlightCtrlState s)
        {
            if (!Ops.TryGetValue(ai, out var op)) return false;
            if (ai.vessel == null || !ai.vessel.loaded) { Ops.Remove(ai); return false; }
            Prepare(ai, lowFlying: op.kind == AirOpKind.Landing);
            bool keep = op.kind switch
            {
                AirOpKind.Landing => Landing(ai, s, op),
                AirOpKind.Refuel => Refuel(ai, s, op),
                _ => Tanker(ai, s, op),
            };
            Finish(ai, s);
            if (!keep) Ops.Remove(ai);
            return true;
        }

        static void SetPhase(BDModulePilotAI ai, Op op, string phase)
        {
            if (op.phase == phase) return;
            op.phase = phase;
            op.phaseStart = Time.time;
            Log(ai.vessel, $"{op.kind}: {phase}");
        }

        // ---- Landing ----

        const float GlideSlopeDeg = 3.5f, TouchdownFromThreshold = 300f, FinalFixDistance = 8000f;

        static bool Landing(BDModulePilotAI ai, FlightCtrlState s, Op op)
        {
            var v = ai.vessel;
            var rw = op.runway;
            Vector3 up = v.up;
            Vector3 d = Vector3.ProjectOnPlane((Vector3)(rw.B - rw.A), up).normalized;
            Vector3 right = Vector3.Cross(up, d).normalized;
            Vector3 td = (Vector3)rw.A + d * TouchdownFromThreshold;
            Vector3 rel = (Vector3)v.CoM - td;
            float along = Vector3.Dot(rel, d);       // negative before the touchdown point
            float lateral = Vector3.Dot(rel, right);  // positive = right of centreline
            float height = (float)(v.altitude - rw.Elevation);
            float approachSpeed = Mathf.Max(ai.takeOffSpeed * 1.25f, ai.minSpeed * 1.1f);
            float tanG = Mathf.Tan(GlideSlopeDeg * Mathf.Deg2Rad);
            float glideHeight = Mathf.Max(0, -along) * tanG;
            Telemetry(ai, op, $"along {along:F0} m, lateral {lateral:F1} m, height {height:F0} m (glide {glideHeight:F0}), {v.srfSpeed:F0} m/s");

            switch (op.phase)
            {
                case "approach":
                    {
                        Status(ai, $"Landing: approach {rw.name}");
                        Vector3 faf = td - d * FinalFixDistance + up * (FinalFixDistance * tanG);
                        Fly(ai, s, faf, corrections: true);
                        Throttle(ai, approachSpeed * 1.4f, true, false);
                        bool aligned = Vector3.Angle(Vector3.ProjectOnPlane((Vector3)v.srf_velocity, up), d) < 35f;
                        if ((faf - (Vector3)v.CoM).magnitude < 1500f || (along > -FinalFixDistance - 2000 && along < -2500 && Mathf.Abs(lateral) < 500 && aligned))
                            SetPhase(ai, op, "final");
                        return true;
                    }
                case "final":
                    {
                        Status(ai, $"Landing: final {-along / 1000f:F1} km");
                        // Aim at the glide path ahead on the centreline (closer in = tighter tracking), leading out sideways drift.
                        float lookAhead = Mathf.Clamp(-along * 0.25f, 400f, 1000f);
                        float aheadAlong = Mathf.Min(along + lookAhead, 0f);
                        float lateralRate = Vector3.Dot((Vector3)v.srf_velocity, right);
                        Vector3 aim = td + d * aheadAlong + up * (Mathf.Max(0, -aheadAlong) * tanG + 2f)
                                      - right * Mathf.Clamp(lateralRate * 6f, -150f, 150f);
                        Fly(ai, s, aim, corrections: false);
                        Throttle(ai, approachSpeed, true, false);
                        if (along > -4500f) v.ActionGroups.SetGroup(KSPActionGroup.Gear, true);
                        // Go around if badly off the path close in, or floating long.
                        if ((along > -900f && (Mathf.Abs(lateral) > 45f || height - glideHeight > 60f || height - glideHeight < -30f)) || (along > 400f && height > 25f))
                        { SetPhase(ai, op, "go-around"); return true; }
                        if (v.radarAltitude < 10f + 0.05f * (float)v.srfSpeed) SetPhase(ai, op, "flare");
                        return true;
                    }
                case "flare":
                    {
                        Status(ai, "Landing: flare");
                        // Ease the sink rate to ~1.5 m/s over the runway, idle power.
                        float speed = Mathf.Max((float)v.srfSpeed, 10f);
                        float gamma = Mathf.Atan2(-1.5f, speed);
                        Vector3 aim = (Vector3)v.CoM + d * 300f + up * (300f * Mathf.Tan(gamma)) - right * Mathf.Clamp(lateral, -20, 20) * 0.5f;
                        Fly(ai, s, aim, corrections: false);
                        Throttle(ai, 0, false, false, 0f);
                        if (v.Landed) SetPhase(ai, op, "rollout");
                        else if (along > 1200f) SetPhase(ai, op, "go-around");
                        return true;
                    }
                case "rollout":
                    {
                        Status(ai, "Landing: rollout");
                        Throttle(ai, 0, true, false, 0f);
                        v.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
                        GroundSteer(ai, s, d, td, float.NaN); // neutral elevator, track the centreline
                        if (v.srfSpeed < 2f)
                        {
                            ai.standbyMode = true;
                            ai.ReleaseCommand();
                            Status(ai, "Landed");
                            Log(v, $"landed at {rw.name}");
                            return false;
                        }
                        return true;
                    }
                default: // go-around
                    {
                        Status(ai, "Landing: go-around");
                        v.ActionGroups.SetGroup(KSPActionGroup.Brakes, false);
                        Throttle(ai, ai.maxSpeed, false, true);
                        Fly(ai, s, (Vector3)v.CoM + d * 1500f + up * 400f, corrections: true);
                        if (v.radarAltitude > 300f && Time.time - op.phaseStart > 8f)
                        {
                            if (++op.attempts >= 3) { Msg($"{v.GetName()}: landing abandoned after 3 attempts"); return false; }
                            SetPhase(ai, op, "approach");
                        }
                        return true;
                    }
            }
        }

        // ---- Takeoff assist (runs after BDArmory's TakeOff) ----

        class Roll { public Vector3 heading; public Vector3d origin; public float rotateT0 = -1, att0; }
        static readonly Dictionary<BDModulePilotAI, Roll> rolls = new();

        public static void TakeoffAssist(BDModulePilotAI ai, FlightCtrlState s)
        {
            var v = ai.vessel;
            if (!AvionicsSettings.TakeoffAssist || v == null || v.Splashed) { rolls.Remove(ai); return; }
            Vector3 up = v.up;
            if (v.Landed)
            {
                if (!rolls.TryGetValue(ai, out var r) || (v.srfSpeed < 3 && r.rotateT0 < 0 && (v.CoMD - r.origin).magnitude > 50))
                {
                    r = new Roll { heading = Vector3.ProjectOnPlane(v.ReferenceTransform.up, up).normalized, origin = v.CoMD };
                    rolls[ai] = r;
                }
                v.ActionGroups.SetGroup(KSPActionGroup.Brakes, false);
                float pitch = 0f;
                if (v.srfSpeed >= ai.takeOffSpeed)
                {
                    // Rotate at a limited rate to a capped attitude: no yanking, no tail strike.
                    float att = 90f - Vector3.Angle(v.ReferenceTransform.up, up);
                    if (r.rotateT0 < 0) { r.rotateT0 = Time.time; r.att0 = att; }
                    pitch = Mathf.Min(r.att0 + AvionicsSettings.RotateRate * (Time.time - r.rotateT0), AvionicsSettings.MaxRotateAttitude);
                }
                GroundSteer(ai, s, r.heading, (Vector3)r.origin, v.srfSpeed >= ai.takeOffSpeed ? pitch : float.NaN);
                Status(ai, v.srfSpeed >= ai.takeOffSpeed ? "Taking off: rotate" : "Taking off: roll");
                return;
            }
            rolls.Remove(ai);
            // Just airborne: keep the wings level and don't over-rotate until clear of the ground.
            if (ai.TakingOff && v.radarAltitude < 25f)
            {
                var rt = v.ReferenceTransform;
                Vector3 top = -rt.forward;
                float bank = Mathf.Atan2(Vector3.Dot(up, rt.right), Vector3.Dot(up, top)) * Mathf.Rad2Deg;
                if (Mathf.Abs(bank) > 8f) s.roll = Mathf.Clamp(0.02f * bank - 0.2f * -v.angularVelocity.y, -1, 1);
                float att = 90f - Vector3.Angle(rt.up, up);
                if (att > 15f) s.pitch = Mathf.Min(s.pitch, Mathf.Clamp(0.06f * (15f - att) - 0.25f * -v.angularVelocity.x, -1, 1));
            }
        }

        /// <summary>
        /// Ground steering: hold a heading and centreline with wheel steering and rudder, wings level.
        /// pitchAttitude = NaN: neutral elevator; otherwise hold that nose attitude (degrees above the horizon).
        /// Signs follow BDArmory: +pitch nose up, +yaw right, +roll right, +wheelSteer LEFT.
        /// </summary>
        static void GroundSteer(BDModulePilotAI ai, FlightCtrlState s, Vector3 heading, Vector3 linePoint, float pitchAttitude)
        {
            var v = ai.vessel;
            var rt = v.ReferenceTransform;
            Vector3 up = v.up, nose = rt.up, top = -rt.forward;
            Vector3 lineRight = Vector3.Cross(up, heading).normalized;
            float psi = Vector3.SignedAngle(heading, Vector3.ProjectOnPlane(nose, up), up); // + = nose right of the line
            float lateral = Vector3.Dot((Vector3)v.CoM - linePoint, lineRight);           // + = right of the line
            float yawRate = -v.angularVelocity.z;                                          // + = yawing right
            float speedScale = Mathf.Clamp(30f / Mathf.Max((float)v.srfSpeed, 1f), 0.25f, 1f); // less steering authority needed when fast
            float yaw = Mathf.Clamp((-0.05f * psi - 0.01f * Mathf.Clamp(lateral, -30, 30) - 0.4f * yawRate) * speedScale, -1, 1);
            s.yaw = yaw;
            s.wheelSteer = -yaw;
            float bank = Mathf.Atan2(Vector3.Dot(up, rt.right), Vector3.Dot(up, top)) * Mathf.Rad2Deg; // + = left wing low
            s.roll = Mathf.Clamp(0.02f * bank - 0.2f * -v.angularVelocity.y, -1, 1);
            if (float.IsNaN(pitchAttitude)) s.pitch = 0f;
            else
            {
                float att = 90f - Vector3.Angle(nose, up);
                s.pitch = Mathf.Clamp(0.06f * (pitchAttitude - att) - 0.25f * -v.angularVelocity.x, -1, 1);
            }
        }

        // ---- Air-to-air refuelling (AARS) ----

        static bool Refuel(BDModulePilotAI ai, FlightCtrlState s, Op op)
        {
            var v = ai.vessel;
            if (op.tanker == null || !op.tanker.loaded || op.drogue == null || op.probe == null) { Msg($"{v.GetName()}: refuelling aborted (tanker lost)"); return false; }
            TargetDrogue(v, op.probe, op.drogue, op.tanker); // keep the probe pointed at our drogue (AARS needs the tanker targeted)
            var drogueT = DrogueTransform(op.drogue);
            var probeT = ProbeTransform(op.probe);
            if (drogueT == null || probeT == null) { Msg($"AARS {(drogueT == null ? "drogue" : "probe")} transform not found"); return false; }

            Vector3 tVel = op.tanker.Velocity();
            float tSpeed = tVel.magnitude;
            Vector3 fwd = tSpeed > 1 ? tVel / tSpeed : op.tanker.ReferenceTransform.up;
            Vector3 probeOffset = probeT.position - (Vector3)v.CoM;
            Vector3 contactCoM = drogueT.position - probeOffset;   // where our CoM must be for probe-in-basket
            Vector3 precontact = contactCoM - fwd * 25f;
            Vector3 relVel = (Vector3)v.Velocity() - tVel;
            bool contact = Field<bool>(op.probe, "IsContact");
            Telemetry(ai, op, $"probe->basket {(drogueT.position - probeT.position).magnitude:F1} m, rel vel {relVel.magnitude:F1} m/s, contact {contact}");

            Vector3 slot; float maxClosure;
            switch (op.phase)
            {
                case "join":
                    slot = precontact; maxClosure = 60f;
                    if ((precontact - (Vector3)v.CoM).magnitude < 60f) SetPhase(ai, op, "precontact");
                    break;
                case "precontact":
                    slot = precontact; maxClosure = 8f;
                    if ((precontact - (Vector3)v.CoM).magnitude < 5f && relVel.magnitude < 1.5f)
                    { if (op.stableSince < 0) op.stableSince = Time.time; }
                    else op.stableSince = -1;
                    if (op.stableSince > 0 && Time.time - op.stableSince > 3f) { op.stableSince = -1; SetPhase(ai, op, "contact"); }
                    break;
                case "contact":
                    slot = contactCoM + fwd * 0.5f; maxClosure = 1.5f; // push ~0.5 m into the basket
                    if (contact) SetPhase(ai, op, "refuel");
                    else
                    {
                        // Missed: probe level with or past the basket but off to the side.
                        Vector3 miss = Vector3.ProjectOnPlane(drogueT.position - probeT.position, fwd);
                        if (Vector3.Dot(probeT.position - drogueT.position, fwd) > -1f && miss.magnitude > 2.5f) SetPhase(ai, op, "precontact");
                    }
                    break;
                case "refuel":
                    slot = contactCoM + fwd * 0.3f; maxClosure = 1f;
                    if (!contact) { if (op.contactLostSince < 0) op.contactLostSince = Time.time; } else op.contactLostSince = -1;
                    if (op.contactLostSince > 0 && Time.time - op.contactLostSince > 2f) { op.contactLostSince = -1; SetPhase(ai, op, "precontact"); }
                    if (FuelFraction(v, Field<string>(op.probe, "fuelType")) > 0.98f || Time.time - op.phaseStart > 180f) SetPhase(ai, op, "disconnect");
                    break;
                default: // disconnect: drop back, then hand the aircraft back to its AI
                    slot = contactCoM - fwd * 60f; maxClosure = 4f;
                    if (Time.time - op.phaseStart > 8f)
                    {
                        Invoke(op.probe, "Deactivate");
                        ai.ReleaseCommand();
                        Log(v, "refuelling complete");
                        return false;
                    }
                    break;
            }
            Status(ai, $"Refuel: {op.phase}");
            HoldSlot(ai, s, op, slot, tVel, fwd, maxClosure);
            return true;
        }

        /// <summary>Formation-style slot keeping behind a moving reference (tanker), with a capped closure rate.</summary>
        static void HoldSlot(BDModulePilotAI ai, FlightCtrlState s, Op op, Vector3 slot, Vector3 refVel, Vector3 fwd, float maxClosure)
        {
            var v = ai.vessel;
            Vector3 toSlot = slot - (Vector3)v.CoM;
            Vector3 cross = Vector3.ProjectOnPlane(toSlot, fwd);
            // Aim through the slot well ahead so small sideways/vertical errors are flown out gently.
            Vector3 aim = slot + fwd * Mathf.Clamp(toSlot.magnitude * 2f, 300f, 1000f) + cross * 0.5f;
            FormationAssist.SetSlot(ai, slot, refVel, fwd, op.tanker);
            Fly(ai, s, aim, corrections: false);
            float alongErr = Vector3.Dot(toSlot, fwd);
            float closure = Vector3.Dot(refVel - (Vector3)v.Velocity(), fwd);
            float target = refVel.magnitude + Mathf.Clamp(0.15f * alongErr + 0.9f * closure, -maxClosure * 2f, maxClosure);
            if (op.smoothedSpeed > 0) target = Mathf.Lerp(op.smoothedSpeed, target, 0.15f);
            op.smoothedSpeed = target;
            Throttle(ai, Mathf.Clamp(target, 0, ai.maxSpeed), true, true);
        }

        // ---- Tanker: straight and level at constant speed with the drogues out ----

        static bool Tanker(BDModulePilotAI ai, FlightCtrlState s, Op op)
        {
            var v = ai.vessel;
            Vector3 up = v.up;
            Vector3 h = Vector3.ProjectOnPlane(op.tankerHeading, up).normalized;
            // Altitude hold: height error minus a vertical-speed damping term, as a target ~2 km ahead (up to ±4°).
            float climb = Mathf.Clamp((float)(op.tankerAlt - v.altitude) * 0.5f - (float)v.verticalSpeed * 4f, -150f, 150f);
            Fly(ai, s, (Vector3)v.CoM + h * 2000f + up * climb, corrections: true);
            Throttle(ai, op.tankerSpeed, true, false);
            Status(ai, $"Tanker {op.tankerSpeed:F0} m/s, {op.tankerAlt / 1000:F1} km");
            Telemetry(ai, op, $"speed {v.srfSpeed:F0}/{op.tankerSpeed:F0} m/s, alt {v.altitude:F0}/{op.tankerAlt:F0} m");
            return true;
        }

        // ---- AARS helpers ----

        static void DeployTanker(Vessel tanker)
        {
            foreach (var name in new[] { "DroguePod", "FuelBoom", "FuelBoomAndPod" })
                foreach (var p in tanker.parts)
                {
                    var m = p.Modules.GetModule(name);
                    if (m == null) continue;
                    bool deployed = Field<bool>(m, "IsDeployed");
                    if (!deployed) Invoke(m, "Activate");
                }
        }

        /// <summary>The basket: AARS's trigger if it has set it, else the "Trigger" / "Drogue" model transforms, else the pod's moving drogue.</summary>
        static Transform DrogueTransform(PartModule drogue)
        {
            var t = Field<Transform>(drogue, "TirggerTransform");
            if (t == null) t = drogue.part.FindModelTransform("Trigger");
            if (t == null) t = drogue.part.FindModelTransform("Drogue");
            if (t == null) { var pod = drogue.part.Modules.GetModule("DroguePod"); if (pod != null) t = Field<Transform>(pod, "DrogueTransform"); }
            return t != null ? t : drogue.part.transform;
        }

        /// <summary>The probe tip: AARS's trigger, else the "Trigger" / "ProbeTransform" model transforms, else the part.</summary>
        static Transform ProbeTransform(PartModule probe)
        {
            var t = Field<Transform>(probe, "TriggerTransform");
            if (t == null) t = probe.part.FindModelTransform("Trigger");
            if (t == null) t = Field<Transform>(probe, "ProbeTransform");
            if (t == null) t = probe.part.FindModelTransform("ProbeTransform");
            return t != null ? t : probe.part.transform;
        }

        /// <summary>What targeting the tanker by hand does: the receiver targets the tanker, and the probe is told which drogue it's going for.</summary>
        static void TargetDrogue(Vessel receiver, PartModule probe, PartModule drogue, Vessel tanker)
        {
            try
            {
                if (receiver == FlightGlobals.ActiveVessel)
                {
                    if (!ReferenceEquals(FlightGlobals.fetch.VesselTarget, tanker)) FlightGlobals.fetch.SetVesselTarget(tanker);
                }
                else AccessTools.Property(typeof(Vessel), "targetObject")?.SetValue(receiver, tanker);
                SetField(probe, "target", tanker);
                SetField(probe, "targetvessel", tanker);
                SetField(probe, "TDrogue", drogue.part);
                SetField(probe, "Drogue", drogue);
                SetField(probe, "IsTarget", true);
                SetField(drogue, "IsTarget", true);
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: AARS targeting failed: {e.Message}"); }
        }

        static void SetField(object o, string name, object value)
        {
            var f = AccessTools.Field(o.GetType(), name);
            if (f != null && (value == null || f.FieldType.IsInstanceOfType(value))) f.SetValue(o, value);
        }

        static PartModule FindModule(Vessel v, string name)
        {
            foreach (var p in v.parts) { var m = p.Modules.GetModule(name); if (m != null) return m; }
            return null;
        }

        static T Field<T>(object o, string name)
        {
            if (o == null) return default;
            var f = AccessTools.Field(o.GetType(), name);
            return f != null && f.GetValue(o) is T t ? t : default;
        }

        static void Invoke(object o, string method)
        {
            try { AccessTools.Method(o.GetType(), method, Type.EmptyTypes)?.Invoke(o, null); }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: AARS {method} failed: {e.Message}"); }
        }

        static float FuelFraction(Vessel v, string resource)
        {
            if (string.IsNullOrEmpty(resource)) resource = "LiquidFuel";
            double amount = 0, max = 0;
            foreach (var p in v.parts)
                foreach (PartResource r in p.Resources)
                    if (r.resourceName == resource) { amount += r.amount; max += r.maxAmount; }
            return max > 0 ? (float)(amount / max) : 1f;
        }

        // ---- Logging ----

        static void Msg(string text) { ScreenMessages.PostScreenMessage(text, 4f, ScreenMessageStyle.UPPER_CENTER); Debug.Log($"[BDArmoryAvionics]: {text}"); }
        static void Log(Vessel v, string text) => Debug.Log($"[BDArmoryAvionics]: AirOps {v.GetName()}: {text}");

        static void Telemetry(BDModulePilotAI ai, Op op, string text)
        {
            if (!AvionicsSettings.AirOpsTelemetry || Time.time - op.lastLog < 2f) return;
            op.lastLog = Time.time;
            Debug.Log($"[BDArmoryAvionics]: AirOps {ai.vessel.GetName()} {op.kind}/{op.phase}: {text}");
        }
    }

    [HarmonyPatch(typeof(BDModulePilotAI), "AutoPilot")]
    static class Patch_PilotAI_AutoPilot
    {
        static bool Prefix(BDModulePilotAI __instance, FlightCtrlState s) => !AirOps.Run(__instance, s);
    }

    [HarmonyPatch(typeof(BDModulePilotAI), "TakeOff")]
    static class Patch_PilotAI_TakeOff
    {
        static void Postfix(BDModulePilotAI __instance, FlightCtrlState s) => AirOps.TakeoffAssist(__instance, s);
    }
}
