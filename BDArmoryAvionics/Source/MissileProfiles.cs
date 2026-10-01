using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

using BDArmory.Weapons.Missiles;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Flight profile for a BDArmory modular missile, added by ModuleManager next to BDModularGuidance and set up in the VAB/SPH.
    /// - Per-stage guidance: switch guidance mode when stage 2/3/4 fires.
    /// - ICBM: boost on a ballistic arc, cut the engine when the predicted impact reaches the target,
    ///   coast, then hand over to the missile's own guidance for the terminal dive.
    /// </summary>
    public class ModuleMissileProfile : PartModule
    {
        public static readonly string[] StageGuidance = ["Inherit", "AAM", "AGM/STS", "Cruise", "Ballistic", "PN", "APN", "Orbital", "AAM Loft"];

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Flight profile", groupName = "mslprof", groupDisplayName = "Missile flight profile", groupStartCollapsed = true),
         UI_ChooseOption(options = ["Standard", "ICBM"], scene = UI_Scene.Editor)]
        public string profile = "Standard";

        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = true, guiName = "Stage 2 guidance", groupName = "mslprof"),
         UI_ChooseOption(options = ["Inherit", "AAM", "AGM/STS", "Cruise", "Ballistic", "PN", "APN", "Orbital", "AAM Loft"], scene = UI_Scene.Editor)]
        public string stage2Guidance = "Inherit";

        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = true, guiName = "Stage 3 guidance", groupName = "mslprof"),
         UI_ChooseOption(options = ["Inherit", "AAM", "AGM/STS", "Cruise", "Ballistic", "PN", "APN", "Orbital", "AAM Loft"], scene = UI_Scene.Editor)]
        public string stage3Guidance = "Inherit";

        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = true, guiName = "Stage 4 guidance", groupName = "mslprof"),
         UI_ChooseOption(options = ["Inherit", "AAM", "AGM/STS", "Cruise", "Ballistic", "PN", "APN", "Orbital", "AAM Loft"], scene = UI_Scene.Editor)]
        public string stage4Guidance = "Inherit";

        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = true, guiName = "ICBM trajectory", groupName = "mslprof"),
         UI_ChooseOption(options = ["Minimum energy", "Lofted", "Depressed"], scene = UI_Scene.Editor)]
        public string loft = "Minimum energy";

        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = true, guiName = "ICBM terminal altitude", guiUnits = " km", groupName = "mslprof"),
         UI_FloatRange(minValue = 2f, maxValue = 70f, stepIncrement = 1f, scene = UI_Scene.Editor)]
        public float terminalAltKm = 15f;

        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = true, guiName = "ICBM drag allowance", guiUnits = " %", groupName = "mslprof"),
         UI_FloatRange(minValue = -10f, maxValue = 20f, stepIncrement = 0.5f, scene = UI_Scene.Editor)]
        public float dragAllowancePct = 3f;

        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "Profile phase", groupName = "mslprof")]
        public string phase = "-";

        public bool IsICBM => profile == "ICBM";

        public string GuidanceForStage(int stage) => stage switch { 2 => stage2Guidance, 3 => stage3Guidance, 4 => stage4Guidance, _ => "Inherit" };

        public override string GetInfo() =>
            "Flight profile for modular missiles:\n- per-stage guidance switching\n- ICBM profile (boost, cutoff on predicted impact, coast, terminal)\nFor the ICBM profile the last stage needs a throttleable (liquid) engine for an accurate cutoff.";

        public override string GetModuleDisplayName() => "Missile Flight Profile";

        public static ModuleMissileProfile For(BDModularGuidance mmg) => mmg != null ? mmg.part.FindModuleImplementing<ModuleMissileProfile>() : null;
    }

    /// <summary>Harmony patches on BDModularGuidance: vessel-split fix, per-stage guidance and the ICBM profile.</summary>
    static class MissileProfilePatches
    {
        static readonly FieldInfo velocityTransform = AccessTools.Field(typeof(BDModularGuidance), "_velocityTransform");
        static readonly FieldInfo nextStage = AccessTools.Field(typeof(BDModularGuidance), "_nextStage");
        static readonly MethodInfo refreshGuidance = AccessTools.Method(typeof(BDModularGuidance), "RefreshGuidanceMode");

        /// <summary>
        /// Staging fix. When a stage holding the vessel's root part separates, KSP makes the other half (the one with the guidance)
        /// a new vessel, but BDArmory keeps its velocity reference and steering on the old one. Move them to the guidance part's
        /// current vessel, so a missile can be mounted by any part (e.g. its booster) instead of hanging from the guidance.
        /// </summary>
        [HarmonyPatch(typeof(BDModularGuidance), nameof(BDModularGuidance.OnFixedUpdate))]
        static class Patch_OnFixedUpdate
        {
            static void Postfix(BDModularGuidance __instance)
            {
                var mmg = __instance;
                if (!mmg.HasFired || mmg.vessel == null || velocityTransform == null) return;
                if (velocityTransform.GetValue(mmg) is not Transform vt || vt == null) return;
                if (vt.parent == mmg.vessel.transform) return;
                var oldVessel = vt.parent != null ? vt.parent.GetComponent<Vessel>() : null;
                if (oldVessel != null) oldVessel.OnFlyByWire -= mmg.GuidanceSteer;
                vt.position = mmg.vessel.transform.position;
                vt.SetParent(mmg.vessel.transform, true);
                mmg.vessel.OnFlyByWire -= mmg.GuidanceSteer;
                mmg.vessel.OnFlyByWire += mmg.GuidanceSteer;
                if (oldVessel != null)
                {
                    mmg.vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, oldVessel.ActionGroups[KSPActionGroup.SAS]);
                    mmg.vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, oldVessel.ActionGroups[KSPActionGroup.RCS]);
                }
                mmg.vessel.vesselName = mmg.GetShortName();
                Debug.Log($"[BDArmoryAvionics]: {mmg.GetShortName()}: guidance moved to the separated stage ({(oldVessel != null ? oldVessel.vesselName : "?")} left behind)");
            }
        }

        /// <summary>Per-stage guidance: after stage N fires, switch to that stage's guidance mode.</summary>
        [HarmonyPatch(typeof(BDModularGuidance), nameof(BDModularGuidance.ExecuteNextStage))]
        static class Patch_ExecuteNextStage
        {
            static void Postfix(BDModularGuidance __instance)
            {
                var prof = ModuleMissileProfile.For(__instance);
                if (prof == null || nextStage == null) return;
                int fired = (int)nextStage.GetValue(__instance) - 1;
                string g = prof.GuidanceForStage(fired);
                int idx = Array.IndexOf(ModuleMissileProfile.StageGuidance, g);
                if (idx <= 0) return;
                __instance.GuidanceIndex = idx;
                refreshGuidance?.Invoke(__instance, null);
                Debug.Log($"[BDArmoryAvionics]: {__instance.GetShortName()}: stage {fired} guidance -> {g}");
            }
        }

        /// <summary>ICBM profile: take over steering until the terminal phase.</summary>
        [HarmonyPatch(typeof(BDModularGuidance), nameof(BDModularGuidance.GuidanceSteer))]
        static class Patch_GuidanceSteer
        {
            static bool Prefix(BDModularGuidance __instance, FlightCtrlState s)
            {
                var prof = ModuleMissileProfile.For(__instance);
                if (prof == null || !prof.IsICBM) return true;
                return !BallisticController.Steer(__instance, prof, s);
            }
        }
    }

    /// <summary>ICBM profile controller. Steer returns true while it is flying the missile.</summary>
    public static class BallisticController
    {
        enum Phase { Rise, Boost, Coast, Terminal }

        class State
        {
            public Phase phase = Phase.Rise;
            public double lat, lon, alt;
            public bool haveTarget;
            public Vector3 lastTargetWorld;
        }

        static readonly Dictionary<BDModularGuidance, State> states = new();
        static Transform frame;

        public static bool Steer(BDModularGuidance m, ModuleMissileProfile prof, FlightCtrlState s)
        {
            var v = m.vessel;
            if (v == null || !v.loaded || v.packed || !m.HasFired || !m.guidanceActive) return false;
            if (!states.TryGetValue(m, out var st)) states[m] = st = new State();
            if (st.phase == Phase.Terminal) return false;
            var body = v.mainBody;

            // Keep the target as lat/lon/alt so the planet's rotation during a long flight is accounted for.
            Vector3 tp = m.TargetPosition;
            if (tp == Vector3.zero) return false;
            if (!st.haveTarget || (tp - st.lastTargetWorld).sqrMagnitude > 100f * 100f)
            {
                st.lat = body.GetLatitude(tp); st.lon = body.GetLongitude(tp); st.alt = body.GetAltitude(tp);
                st.haveTarget = true;
            }
            st.lastTargetWorld = tp;
            Vector3d target = body.GetWorldSurfacePosition(st.lat, st.lon, st.alt);
            Vector3d pos = v.CoMD;
            Vector3 up = (pos - body.position).normalized;
            double psi = CentralAngle(body, pos, target); // radians to go

            // Terminal: descending below the terminal altitude, or close in.
            if (st.phase == Phase.Coast && v.verticalSpeed < 0 && (v.altitude < prof.terminalAltKm * 1000 || psi * body.Radius < 5000))
            {
                st.phase = Phase.Terminal;
                prof.phase = "terminal";
                Debug.Log($"[BDArmoryAvionics]: {m.GetShortName()}: ICBM terminal phase, {psi * body.Radius / 1000:F1} km to go");
                return false; // the missile's own guidance mode flies the dive
            }

            Vector3 dir;
            float throttle;
            Vector3 toTargetHoriz = Vector3.ProjectOnPlane((Vector3)(target - pos), up).normalized;

            switch (st.phase)
            {
                case Phase.Rise:
                    dir = up;
                    throttle = 1;
                    prof.phase = "rise";
                    if (m.TimeIndex > m.dropTime + 2f && v.heightFromTerrain > 300) st.phase = Phase.Boost;
                    break;

                case Phase.Boost:
                    {
                        // Minimum-energy flight-path angle is 45° minus a quarter of the range angle; loft or depress from there.
                        float gamma = 45f - (float)(psi * Mathf.Rad2Deg) / 4f;
                        if (prof.loft == "Lofted") gamma += 15f; else if (prof.loft == "Depressed") gamma -= 15f;
                        gamma = Mathf.Clamp(gamma, 5f, 85f);
                        // Ease over from near-vertical through the lower atmosphere (gravity turn).
                        gamma = Mathf.Lerp(85f, gamma, Mathf.Clamp01((float)v.altitude / 15000f));

                        // Steer out cross-range error of the predicted impact point.
                        Vector3 heading = toTargetHoriz;
                        bool impact = PredictImpact(v, st.alt, out Vector3d impactPos);
                        if (impact)
                        {
                            Vector3 toImpact = Vector3.ProjectOnPlane((Vector3)(impactPos - pos), up);
                            float err = Vector3.SignedAngle(toImpact, heading, up);
                            heading = Quaternion.AngleAxis(Mathf.Clamp(err * 0.5f, -10f, 10f), up) * heading;
                        }
                        dir = Mathf.Cos(gamma * Mathf.Deg2Rad) * heading + Mathf.Sin(gamma * Mathf.Deg2Rad) * up;
                        throttle = 1;
                        prof.phase = $"boost {gamma:F0}°";

                        // Cut off when the free-fall impact point reaches the target (plus drag allowance), or if it would go into orbit.
                        double psiImpact = impact ? CentralAngle(body, pos, impactPos) : double.MaxValue;
                        if (m.TimeIndex > m.dropTime + 4f && psiImpact >= psi * (1 + prof.dragAllowancePct / 100.0))
                        {
                            st.phase = Phase.Coast;
                            throttle = 0;
                            Debug.Log($"[BDArmoryAvionics]: {m.GetShortName()}: ICBM cutoff at {v.altitude / 1000:F1} km, {v.obt_speed:F0} m/s, {psi * body.Radius / 1000:F0} km to go");
                        }
                        break;
                    }

                default: // Coast: nose along the velocity, engines off
                    dir = (v.altitude > body.atmosphereDepth ? (Vector3)v.obt_velocity : (Vector3)v.srf_velocity).normalized;
                    throttle = 0;
                    prof.phase = $"coast {psi * body.Radius / 1000:F0} km";
                    break;
            }

            AttitudeSteer(m, s, dir);
            s.mainThrottle = throttle;
            return true;
        }

        /// <summary>BDModularGuidance's own steering law and gains, but measured from the missile's nose rather than its velocity.</summary>
        static void AttitudeSteer(BDModularGuidance m, FlightCtrlState s, Vector3 dir)
        {
            if (frame == null) frame = new GameObject("BDAAvionicsProfileFrame").transform;
            var v = m.vessel;
            frame.position = v.CoM;
            frame.rotation = Quaternion.LookRotation(m.GetForwardTransform(), -v.transform.forward);
            Vector3 td = frame.InverseTransformDirection(dir).normalized;
            td = Vector3.RotateTowards(Vector3.forward, td, 15 * Mathf.Deg2Rad, 0);
            Vector3 av = v.angularVelocity;
            s.yaw = Mathf.Clamp(m.SteerMult * td.x - m.SteerDamping * -av.z, -m.MaxSteer, m.MaxSteer);
            s.pitch = Mathf.Clamp(m.SteerMult * td.y - m.SteerDamping * -av.x, -m.MaxSteer, m.MaxSteer);
            s.roll = 0;
        }

        static double CentralAngle(CelestialBody body, Vector3d a, Vector3d b) =>
            Vector3d.Angle(a - body.position, b - body.position) * Mathf.Deg2Rad;

        /// <summary>Where the current free-fall orbit (no drag) crosses the target's altitude, with the planet's rotation taken out.</summary>
        static bool PredictImpact(Vessel v, double targetAlt, out Vector3d impact)
        {
            impact = default;
            var o = v.orbit;
            var body = v.mainBody;
            if (o == null || body == null) return false;
            double r = body.Radius + targetAlt;
            if (o.PeR > r || o.eccentricity >= 1) return false; // won't come down
            double ta = o.TrueAnomalyAtRadius(r);
            if (double.IsNaN(ta)) return false;
            double now = Planetarium.GetUniversalTime();
            double ut = o.GetUTforTrueAnomaly(-ta, o.period); // descending crossing
            if (ut < now) ut += o.period;
            Vector3d p = o.getPositionAtUT(ut);
            double lat = body.GetLatitude(p), lon = body.GetLongitude(p);
            if (body.rotates && body.rotationPeriod > 0) lon -= (ut - now) * 360.0 / body.rotationPeriod;
            impact = body.GetWorldSurfacePosition(lat, lon, targetAlt);
            return true;
        }
    }

    /// <summary>
    /// Data link mid-course updates: if a radar missile's launcher no longer tracks the target but another networked
    /// sensor does, radar-link the launcher to that sensor so BDArmory keeps feeding the missile target updates.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class DataLinkMidcourse : MonoBehaviour
    {
        readonly Dictionary<(Radar.VesselRadarData from, Radar.VesselRadarData to), float> links = new();
        float next;

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || Time.time < next) return;
            next = Time.time + 0.5f;
            float now = Time.time;
            if (C4ISRSettings.DataLinkMidcourse)
                foreach (var v in FlightGlobals.VesselsLoaded)
                {
                    if (v == null || v.packed) continue;
                    var m = v.FindPartModuleImplementing<MissileBase>();
                    if (m == null || !m.HasFired || m.TargetingMode != MissileBase.TargetingModes.Radar || m.ActiveRadar) continue;
                    var shooter = m.SourceVessel;
                    var tv = m.targetVessel != null ? m.targetVessel.Vessel : null;
                    if (shooter == null || tv == null || m.Team == null) continue;
                    var net = DataLinkNetwork.For(m.Team);
                    var node = net?.Node(shooter);
                    var track = net?.Track(tv);
                    if (node == null || !node.connected || track == null || node.vrd == null) continue;
                    if (IADSController.For(m.Team)?.SiteFor(shooter) != null) continue; // IADS manages its own sites' links
                    var src = track.bestRadarReporter;
                    if (src == null || src == node || src.vrd == null || !src.connected) continue;
                    if (!node.vrd.IsLinkedVRD(src.vrd))
                    {
                        node.vrd.LinkVRD(src.vrd);
                        Debug.Log($"[BDArmoryAvionics]: Mid-course: {shooter.GetName()} linked to {src.Name} for {m.GetShortName()} -> {tv.GetName()}");
                    }
                    links[(node.vrd, src.vrd)] = now;
                }

            // Drop links nobody has needed for 10 s
            var stale = new List<(Radar.VesselRadarData, Radar.VesselRadarData)>();
            foreach (var kv in links) if (now - kv.Value > 10f || kv.Key.from == null || kv.Key.to == null) stale.Add(kv.Key);
            foreach (var k in stale)
            {
                if (k.Item1 != null && k.Item2 != null && k.Item1.IsLinkedVRD(k.Item2)) k.Item1.UnlinkExternalVRD(k.Item2);
                links.Remove(k);
            }
        }
    }
}
