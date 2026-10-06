using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;

namespace BDArmory.Avionics
{
    /// <summary>
    /// ELE Physics LOD: every craft stays alive at any range, but only craft near you (or in a fight) run part physics.
    ///   FULL   within unpack range (default 8 km) or engaged (targeted / missiles inbound, out to 25 km): normal KSP + BDArmory.
    ///   RIGID  loaded but packed (on rails) out to load range (default 30 km): one rigid body, no joints, flown by ELE.
    ///   GHOST  beyond that: unloaded (no parts in memory), still flown by ELE in double precision, drawn as a marker.
    /// ELE flies RIGID and GHOST aircraft straight and level on their last heading, speed and altitude (terrain-aware),
    /// through the orbit (double precision, so no floating-point jitter far away). They hand back to full physics already
    /// moving, through the Kraken Guard's gentle unpack, and loads are staggered so a crowded sky never hitches.
    /// KSP's "destroyed on rails in atmosphere" rule is skipped for craft ELE is flying.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class PhysicsLOD : MonoBehaviour
    {
        public static PhysicsLOD Instance;
        public static int Full, Rigid, Ghost;

        // mode: 0 straight, 1 fly to a point (then orbit it), 2 orbit (CAP / patrol)
        class Track { public double lat, lon, alt, agl, tLat, tLon; public float heading, speed, radius = 6000f; public int mode; public bool lowLevel; public double lastUT; public bool init; }
        readonly Dictionary<Guid, Track> tracks = new();
        readonly HashSet<Guid> loadGranted = new(), unpackGranted = new();
        float nextRanges, nextGhost, grantWindow; int grantsThisWindow, unpacksThisWindow;
        const int GrantsPerSecond = 2, UnpacksPerSecond = 3;

        void Awake() { Instance = this; }
        void OnDestroy() { if (Instance == this) Instance = null; }

        // any eligible aircraft on rails or unloaded is ours (also in the first frames after a load, before it is tracked)
        public static bool Drives(Vessel v) => Instance != null && AvionicsSettings.PhysicsLOD && Eligible(v) && (!v.loaded || v.packed);

        static bool Eligible(Vessel v) =>
            v != null && v != FlightGlobals.ActiveVessel && v.vesselType != VesselType.Debris && v.vesselType != VesselType.SpaceObject && !v.isEVA
            && v.mainBody == FlightGlobals.currentMainBody && v.mainBody.atmosphere
            && (v.situation == Vessel.Situations.FLYING || v.situation == Vessel.Situations.SUB_ORBITAL) && v.altitude < v.mainBody.atmosphereDepth;

        // ---------------- ranges: who gets full physics, who stays rigid, who stays a ghost ----------------
        void Update()
        {
            if (!AvionicsSettings.PhysicsLOD || !FlightGlobals.ready || FlightGlobals.ActiveVessel == null) return;
            if (Time.time < nextRanges) return;
            nextRanges = Time.time + 0.5f;
            ApplyRanges();
        }

        /// <summary>Physics Range Extender (and KSP) set ranges on load / off-rails / vessel switch: put ours back at once.</summary>
        public static void ReapplyNow() { if (Instance != null && AvionicsSettings.PhysicsLOD && FlightGlobals.ready && FlightGlobals.ActiveVessel != null) Instance.ApplyRanges(); }

        void ApplyRanges()
        {
            if (Time.time > grantWindow) { grantWindow = Time.time + 1f; grantsThisWindow = 0; unpacksThisWindow = 0; }
            var me = FlightGlobals.ActiveVessel.GetWorldPos3D();
            float unpack = AvionicsSettings.LODUnpackKm * 1000f, load = AvionicsSettings.LODLoadKm * 1000f, threat = 25000f;
            var engaged = Engaged();
            // closest first, so staggered loading serves the nearest craft first
            var list = new List<(Vessel v, double d)>();
            foreach (var v in FlightGlobals.Vessels) if (Eligible(v)) list.Add((v, (v.GetWorldPos3D() - me).magnitude));
            list.Sort((a, b) => a.d.CompareTo(b.d));
            Full = Rigid = Ghost = 0;
            foreach (var (v, d) in list)
            {
                float up = engaged.Contains(v) ? Mathf.Max(unpack, threat) : unpack;
                float ld = Mathf.Max(load, up + 2000f);
                if (!v.loaded && d < ld && !loadGranted.Contains(v.id))
                {
                    if (grantsThisWindow < GrantsPerSecond) { loadGranted.Add(v.id); grantsThisWindow++; }
                    else ld = (float)Math.Max(1000, d - 500);                          // wait for a load slot
                }
                if (d > ld + 3000) loadGranted.Remove(v.id);
                // stagger unpacking too: a formation crossing the line together unpacks over a second or two, not in one frame
                if (v.loaded && v.packed && d < up && !unpackGranted.Contains(v.id))
                {
                    if (unpacksThisWindow < UnpacksPerSecond) { unpackGranted.Add(v.id); unpacksThisWindow++; }
                    else up = (float)Math.Max(1000, d - 300);
                }
                if (!v.loaded || (v.packed && d > up + 1500)) unpackGranted.Remove(v.id);
                SetRanges(v, ld, up);
                if (!v.loaded) Ghost++; else if (v.packed) Rigid++; else Full++;
            }
        }

        static void SetRanges(Vessel v, float load, float unpack)
        {
            var r = new VesselRanges.Situation(load, load + 2500f, unpack + 1000f, unpack);
            var vr = v.vesselRanges;
            vr.flying = r; vr.subOrbital = r; vr.escaping = r;
        }

        HashSet<Vessel> Engaged()
        {
            var set = new HashSet<Vessel>();
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed) continue;
                var wm = v.ActiveController().WM;
                var tgt = wm != null ? wm.currentTarget?.Vessel : null;
                if (tgt != null) set.Add(tgt);
                if (wm != null && wm.guardMode) set.Add(v);                             // a craft in a fight keeps full physics
            }
            return set;
        }

        // ---------------- flying RIGID and GHOST craft ----------------
        void FixedUpdate()
        {
            if (!AvionicsSettings.PhysicsLOD || !FlightGlobals.ready) return;
            double ut = Planetarium.GetUniversalTime();
            bool ghostTick = Time.time > nextGhost; if (ghostTick) nextGhost = Time.time + 0.2f;
            foreach (var v in FlightGlobals.Vessels)
            {
                if (!Eligible(v)) { tracks.Remove(v != null ? v.id : Guid.Empty); continue; }
                bool rigid = v.loaded && v.packed, ghost = !v.loaded;
                if (!rigid && !ghost) { tracks.Remove(v.id); continue; }                 // full physics: KSP has it
                if (ghost && !ghostTick) continue;
                if (!tracks.TryGetValue(v.id, out var t)) tracks[v.id] = t = Capture(v, ut);
                Step(v, t, ut, rigid);
            }
        }

        static Track Capture(Vessel v, double ut)
        {
            var b = v.mainBody; var pos = v.GetWorldPos3D();
            Vector3d up = (pos - b.position).normalized;
            Vector3d surf = v.loaded ? (Vector3d)v.srf_velocity : v.obt_velocity - b.getRFrmVel(pos);
            Vector3d hor = surf - Vector3d.Dot(surf, up) * up;
            Vector3d north = Vector3d.Exclude(up, b.transform.up).normalized, east = Vector3d.Cross(up, north);
            float heading = (float)(Math.Atan2(Vector3d.Dot(hor, east), Vector3d.Dot(hor, north)) * 180 / Math.PI);
            double ter = Math.Max(0, b.TerrainAltitude(v.latitude, v.longitude));
            var t = new Track
            {
                lat = v.latitude, lon = v.longitude, alt = v.altitude, agl = v.altitude - ter,
                heading = heading, speed = (float)Math.Max(hor.magnitude, 60), lastUT = ut, init = true,
            };
            t.lowLevel = t.agl < 600;
            // follow the AI's orders: fly-to / attack a point, or hold the area (CAP) when it has none
            t.tLat = t.lat; t.tLon = t.lon;
            try
            {
                if (v.loaded)
                {
                    var ai = v.ActiveController().AI;
                    if (ai != null && ai.pilotEnabled)
                    {
                        if (ai.currentCommand == PilotCommands.FlyTo || ai.currentCommand == PilotCommands.Attack) { t.mode = 1; t.tLat = ai.commandGPS.x; t.tLon = ai.commandGPS.y; }
                        else t.mode = 2;
                    }
                }
                else if (v.protoVessel != null)
                    foreach (var pp in v.protoVessel.protoPartSnapshots) foreach (var m in pp.modules) if (m.moduleName == "BDModulePilotAI") { t.mode = 2; break; }
            }
            catch { }
            return t;
        }

        static double Bearing(double la1, double lo1, double la2, double lo2)
        {
            double f1 = la1 * Math.PI / 180, f2 = la2 * Math.PI / 180, dl = (lo2 - lo1) * Math.PI / 180;
            return Math.Atan2(Math.Sin(dl) * Math.Cos(f2), Math.Cos(f1) * Math.Sin(f2) - Math.Sin(f1) * Math.Cos(f2) * Math.Cos(dl)) * 180 / Math.PI;
        }

        static double Distance(CelestialBody b, double la1, double lo1, double la2, double lo2)
        {
            double f1 = la1 * Math.PI / 180, f2 = la2 * Math.PI / 180, dl = (lo2 - lo1) * Math.PI / 180;
            return b.Radius * Math.Acos(Math.Max(-1, Math.Min(1, Math.Sin(f1) * Math.Sin(f2) + Math.Cos(f1) * Math.Cos(f2) * Math.Cos(dl))));
        }

        /// <summary>Heading for the mode: toward the target, or round the patrol circle (turning at most 3 deg/s).</summary>
        static void Steer(CelestialBody b, Track t, float dt)
        {
            if (t.mode == 0) return;
            double d = Distance(b, t.lat, t.lon, t.tLat, t.tLon);
            if (t.mode == 1 && d < 3000) t.mode = 2;                                   // arrived: hold the area
            double want;
            if (t.mode == 1) want = Bearing(t.lat, t.lon, t.tLat, t.tLon);
            else
            {
                double fromCentre = Bearing(t.tLat, t.tLon, t.lat, t.lon);
                double correction = Math.Max(-60, Math.Min(60, (d - t.radius) / t.radius * 60));
                want = d < 100 ? t.heading : fromCentre + 90 + correction;           // clockwise, pulled back onto the circle
            }
            float err = Mathf.DeltaAngle(t.heading, (float)want);
            t.heading = Mathf.Repeat(t.heading + Mathf.Clamp(err, -3f * dt, 3f * dt), 360f);
        }

        static void Step(Vessel v, Track t, double ut, bool rigid)
        {
            var b = v.mainBody;
            double dt = Math.Min(ut - t.lastUT, 5); t.lastUT = ut;
            if (dt <= 0) return;
            Steer(b, t, (float)dt);
            double R = b.Radius + t.alt, h = t.heading * Math.PI / 180;
            t.lat += Math.Cos(h) * t.speed * dt / R * 180 / Math.PI;
            t.lon += Math.Sin(h) * t.speed * dt / (R * Math.Max(0.05, Math.Cos(t.lat * Math.PI / 180))) * 180 / Math.PI;
            if (t.lat > 89) t.lat = 89; if (t.lat < -89) t.lat = -89;
            // terrain-aware altitude: low flyers hug the ground, the rest hold altitude but never go below 300 m AGL
            double ter = Math.Max(0, b.TerrainAltitude(t.lat, t.lon));
            double want = t.lowLevel ? ter + t.agl : Math.Max(t.alt, ter + 300);
            t.alt += Math.Max(-30 * dt, Math.Min(30 * dt, want - t.alt));
            // position and inertial velocity -> orbit (double precision; KSP moves packed craft along it)
            Vector3d pos = b.GetWorldSurfacePosition(t.lat, t.lon, t.alt);
            Vector3d up = (pos - b.position).normalized;
            Vector3d north = Vector3d.Exclude(up, b.transform.up).normalized, east = Vector3d.Cross(up, north);
            Vector3d surfVel = (north * Math.Cos(h) + east * Math.Sin(h)) * t.speed;
            Vector3d obtVel = surfVel + b.getRFrmVel(pos);
            try
            {
                v.orbit.UpdateFromStateVectors((pos - b.position).xzy, obtVel.xzy, b, ut);
                if (rigid && Vector3.Dot(v.transform.up, (Vector3)surfVel.normalized) > 0.3f)
                    v.SetRotation(Quaternion.LookRotation(-(Vector3)up, (Vector3)surfVel.normalized)); // planes: nose along track, belly down
            }
            catch (Exception e) { Debug.LogWarning($"[ELE LOD] {v.vesselName}: {e.Message}"); }
        }

        // ---------------- markers for GHOST craft (visible from far away, e.g. from orbit) ----------------
        static Texture2D dot;
        void OnGUI()
        {
            if (!AvionicsSettings.PhysicsLOD || !AvionicsSettings.LODMarkers || Event.current.type != EventType.Repaint || MapView.MapIsEnabled) return;
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null || FlightGlobals.ActiveVessel == null) return;
            if (dot == null) { dot = new Texture2D(1, 1); dot.SetPixel(0, 0, Color.white); dot.Apply(); }
            var camPos = (Vector3d)cam.transform.position; float maxD = AvionicsSettings.LODMarkerKm * 1000f;
            foreach (var v in FlightGlobals.Vessels)
            {
                if (v == null || v.loaded || !tracks.ContainsKey(v.id)) continue;
                var p = v.GetWorldPos3D(); var d = (p - camPos).magnitude;
                if (d > maxD || Occluded(camPos, p, v.mainBody)) continue;
                var sp = cam.WorldToScreenPoint(p); if (sp.z <= 0) continue;
                float s = Mathf.Clamp(6f - (float)d / 60000f, 3f, 6f);
                var col = TeamColor(v);
                GUI.color = new Color(0, 0, 0, 0.6f); GUI.DrawTexture(new Rect(sp.x - s / 2 - 1, Screen.height - sp.y - s / 2 - 1, s + 2, s + 2), dot);
                GUI.color = col; GUI.DrawTexture(new Rect(sp.x - s / 2, Screen.height - sp.y - s / 2, s, s), dot);
            }
            GUI.color = Color.white;
        }

        static bool Occluded(Vector3d from, Vector3d to, CelestialBody b)
        {
            Vector3d d = to - from; double len = d.magnitude; d /= len;
            double tc = Vector3d.Dot(b.position - from, d); if (tc < 0 || tc > len) return false;
            return (from + d * tc - b.position).magnitude < b.Radius * 0.999;
        }

        static Color TeamColor(Vessel v)
        {
            try
            {
                var own = FlightGlobals.ActiveVessel.ActiveController().WM;
                if (own != null && v.loaded) { var wm = v.ActiveController().WM; if (wm != null) return wm.Team == own.Team ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.35f, 0.25f); }
            }
            catch { }
            return new Color(1f, 0.85f, 0.3f);
        }
    }

    /// <summary>KSP destroys craft that are on rails inside an atmosphere; craft ELE is flying are exempt.</summary>
    [HarmonyPatch(typeof(Vessel), nameof(Vessel.CheckKill))]
    static class Patch_LODCheckKill
    {
        static bool Prefix(Vessel __instance) => !PhysicsLOD.Drives(__instance);
    }

    /// <summary>Physics Range Extender re-applies its (200 km) ranges on load, off-rails, switching and situation changes; ELE re-applies its own right after.</summary>
    [HarmonyPatch]
    static class Patch_LODAfterPRE
    {
        static System.Type T => AccessTools.TypeByName("PhysicsRangeExtender.PhysicsRangeExtender");
        static bool Prepare() => T != null;
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(T))
                if (m.Name == "ApplyPhysRange" || m.Name == "ApplyPhysRangeOnLoad" || m.Name == "UpdateRanges" || m.Name == "SituationChangeFixes") yield return m;
        }
        static void Postfix() { try { PhysicsLOD.ReapplyNow(); } catch { } }
    }
}
