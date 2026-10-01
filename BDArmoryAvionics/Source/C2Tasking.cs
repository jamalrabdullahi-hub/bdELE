using System.Collections.Generic;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    public enum C2Task { None, Patrol, Intercept, Strike, Escort, MoveTo, RTB }

    /// <summary>
    /// Orders from the C2 console and map overlay, carried out through BDArmory's own AI commands
    /// (fly to, attack GPS point, release) and the weapon manager (guard mode, override target).
    /// Works for pilot, VTOL and surface AIs alike.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class C2Tasking : MonoBehaviour
    {
        public class Order
        {
            public C2Task task;
            public readonly List<Vector3d> route = new(); // lat, lon, alt
            public int routeIndex;
            public NetTrack track;
            public Vessel escortOf;
            public float issued;
        }

        public static C2Tasking Instance;
        public static readonly Dictionary<Vessel, Order> Orders = new();
        static readonly Dictionary<Vessel, Vector3d> home = new(); // where each unit was last seen on the ground
        const float WaypointReachedM = 1500f;
        float nextTick;

        void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
            Orders.Clear();
            home.Clear();
        }

        void OnDestroy()
        {
            Orders.Clear();
            if (Instance == this) Instance = null;
        }

        public static Order OrderFor(Vessel v) => v != null && Orders.TryGetValue(v, out var o) ? o : null;

        public static string TaskString(Vessel v)
        {
            var air = AirOps.Describe(v);
            if (air != null) return air;
            var o = OrderFor(v);
            if (o == null) return "Free";
            return o.task switch
            {
                C2Task.Intercept => $"Intercept {o.track?.Label}",
                C2Task.Patrol => $"Patrol WP{o.routeIndex + 1}/{o.route.Count}",
                C2Task.Strike => "Strike GPS",
                C2Task.Escort => $"Escort {(o.escortOf != null ? o.escortOf.GetName() : "?")}",
                C2Task.MoveTo => "Move",
                C2Task.RTB => "RTB",
                _ => "Free",
            };
        }

        static IBDAIControl AI(Vessel v) => v != null ? v.ActiveController().AI : null;
        static MissileFire WM(Vessel v) => v != null ? v.ActiveController().WM : null;

        // ---- Orders ----

        public static void Patrol(Vessel v, List<Vector3d> route)
        {
            if (route == null || route.Count == 0) return;
            var o = New(v, C2Task.Patrol);
            o.route.AddRange(route);
            AI(v)?.CommandFlyTo(o.route[0]);
        }

        public static void AddWaypoint(Vessel v, Vector3d geo)
        {
            var o = OrderFor(v);
            if (o == null || o.task != C2Task.Patrol) { Patrol(v, new List<Vector3d> { geo }); return; }
            o.route.Add(geo);
        }

        public static void MoveTo(Vessel v, Vector3d geo)
        {
            var o = New(v, C2Task.MoveTo);
            o.route.Add(geo);
            AI(v)?.CommandFlyTo(geo);
        }

        public static void Strike(Vessel v, Vector3d geo)
        {
            var o = New(v, C2Task.Strike);
            o.route.Add(geo);
            SetWeaponsFree(v, true);
            AI(v)?.CommandAttack(geo);
        }

        public static void Intercept(Vessel v, NetTrack t)
        {
            if (t == null) return;
            var o = New(v, C2Task.Intercept);
            o.track = t;
            SetWeaponsFree(v, true);
            AI(v)?.ReleaseCommand();
            if (t.targetInfo != null) WM(v)?.SetOverrideTarget(t.targetInfo);
        }

        public static void Escort(Vessel v, Vessel leader)
        {
            if (leader == null || leader == v) return;
            var o = New(v, C2Task.Escort);
            o.escortOf = leader;
        }

        public static void RTB(Vessel v)
        {
            var o = New(v, C2Task.RTB);
            o.route.Add(HomeOf(v));
            AI(v)?.CommandFlyTo(o.route[0]);
        }

        public static void Release(Vessel v)
        {
            AirOps.Cancel(v);
            Orders.Remove(v);
            AI(v)?.ReleaseCommand();
        }

        public static bool WeaponsFree(Vessel v) { var wm = WM(v); return wm != null && wm.guardMode; }

        public static void SetWeaponsFree(Vessel v, bool free)
        {
            var wm = WM(v);
            if (wm != null && wm.guardMode != free) wm.ToggleGuardMode();
        }

        static Order New(Vessel v, C2Task task)
        {
            AirOps.Cancel(v); // a new order replaces any landing / refuelling in progress
            var o = new Order { task = task, issued = Time.time };
            Orders[v] = o;
            return o;
        }

        /// <summary>Where a unit returns to: where it was last on the ground, else the space centre.</summary>
        public static Vector3d HomeOf(Vessel v)
        {
            if (v != null && home.TryGetValue(v, out var h)) return h;
            var body = v != null ? v.mainBody : FlightGlobals.GetHomeBody();
            if (SpaceCenter.Instance != null && body == FlightGlobals.GetHomeBody())
                return new Vector3d(SpaceCenter.Instance.Latitude, SpaceCenter.Instance.Longitude, 1500);
            return new Vector3d(0, 0, 1500);
        }

        // ---- Keeping orders going ----

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready || Time.time < nextTick) return;
            nextTick = Time.time + 1f;

            foreach (var v in FlightGlobals.VesselsLoaded)
                if (v != null && v.LandedOrSplashed && !v.isEVA && v.srfSpeed < 5)
                    home[v] = new Vector3d(v.latitude, v.longitude, v.altitude + 1500);

            var done = new List<Vessel>();
            foreach (var kv in Orders)
            {
                var v = kv.Key;
                var o = kv.Value;
                if (v == null || !v.loaded) { done.Add(v); continue; }
                var ai = AI(v);
                switch (o.task)
                {
                    case C2Task.Patrol:
                        if (o.route.Count == 0) { done.Add(v); break; }
                        if (Distance(v, o.route[o.routeIndex]) < WaypointReachedM)
                        {
                            o.routeIndex = (o.routeIndex + 1) % o.route.Count;
                            ai?.CommandFlyTo(o.route[o.routeIndex]);
                        }
                        break;
                    case C2Task.Intercept:
                        if (o.track == null || o.track.vessel == null) { done.Add(v); ai?.ReleaseCommand(); break; }
                        if (o.track.targetInfo != null) WM(v)?.SetOverrideTarget(o.track.targetInfo); // keep it on top of the target list
                        break;
                    case C2Task.Escort:
                        if (o.escortOf == null || !o.escortOf.loaded) { done.Add(v); ai?.ReleaseCommand(); break; }
                        var lead = o.escortOf;
                        // Sit about 1 km behind and 200 m above the leader.
                        Vector3 back = lead.srf_velocity.sqrMagnitude > 1 ? -lead.srf_velocity.normalized : -lead.transform.up;
                        Vector3 pos = lead.CoM + back * 1000f;
                        var body = lead.mainBody;
                        ai?.CommandFlyTo(new Vector3d(body.GetLatitude(pos), body.GetLongitude(pos), lead.altitude + 200));
                        break;
                    case C2Task.MoveTo:
                    case C2Task.RTB:
                        if (o.route.Count > 0 && Distance(v, o.route[0]) < WaypointReachedM && !v.LandedOrSplashed)
                            ai?.CommandFlyTo(o.route[0]); // arrived: keep orbiting the point
                        break;
                }
            }
            foreach (var v in done) Orders.Remove(v);
        }

        static double Distance(Vessel v, Vector3d geo)
        {
            var p = v.mainBody.GetWorldSurfacePosition(geo.x, geo.y, v.altitude);
            return (p - v.CoMD).magnitude;
        }
    }
}
