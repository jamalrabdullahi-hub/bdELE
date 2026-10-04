using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

using BDArmory.Radar;
using BDArmory.Targeting;
using Debug = UnityEngine.Debug;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Terrain heights cached in tiles (lat/lon grid, ~210 m cells). Filled on the main thread (KSP's terrain is main-thread only),
    /// a tile or two per frame, around where sensors need them; read freely by worker threads.
    /// </summary>
    public static class TerrainGrid
    {
        public const double Cell = 0.02;           // degrees (~210 m on Kerbin)
        public const int TileCells = 32;           // 32 x 32 cells per tile (~6.7 km)
        const double TileDeg = Cell * TileCells;
        static readonly ConcurrentDictionary<long, float[]> tiles = new();
        static readonly ConcurrentQueue<long> requests = new();
        static readonly ConcurrentDictionary<long, byte> requested = new();
        static string bodyName;
        public static int TileCount => tiles.Count;

        static long Key(int ti, int tj) => ((long)ti << 32) ^ (uint)tj;

        /// <summary>Worker-safe: terrain height (m, sea floor clamped to 0) or NaN if the tile isn't loaded yet (it gets requested).</summary>
        public static float Height(double lat, double lon)
        {
            lon = ((lon + 180) % 360 + 360) % 360 - 180;
            int ti = (int)Math.Floor((lat + 90) / TileDeg), tj = (int)Math.Floor((lon + 180) / TileDeg);
            long k = Key(ti, tj);
            if (!tiles.TryGetValue(k, out var t))
            {
                if (requested.TryAdd(k, 0)) requests.Enqueue(k);
                return float.NaN;
            }
            double fi = (lat + 90) / Cell - ti * TileCells, fj = (lon + 180) / Cell - tj * TileCells;
            int i = Math.Min(TileCells - 1, Math.Max(0, (int)fi)), j = Math.Min(TileCells - 1, Math.Max(0, (int)fj));
            double ai = fi - i, aj = fj - j;
            int i1 = i + 1, j1 = j + 1;
            float h00 = t[i * (TileCells + 1) + j], h01 = t[i * (TileCells + 1) + j1], h10 = t[i1 * (TileCells + 1) + j], h11 = t[i1 * (TileCells + 1) + j1];
            return (float)((h00 * (1 - aj) + h01 * aj) * (1 - ai) + (h10 * (1 - aj) + h11 * aj) * ai);
        }

        /// <summary>Main thread: fill up to n requested tiles.</summary>
        public static void Fill(CelestialBody body, int n)
        {
            if (body == null) return;
            if (body.bodyName != bodyName) { tiles.Clear(); requested.Clear(); while (requests.TryDequeue(out _)) { } bodyName = body.bodyName; }
            while (n-- > 0 && requests.TryDequeue(out long k))
            {
                int ti = (int)(k >> 32), tj = (int)(uint)k;
                var t = new float[(TileCells + 1) * (TileCells + 1)];
                double lat0 = ti * TileDeg - 90, lon0 = tj * TileDeg - 180;
                for (int i = 0; i <= TileCells; i++)
                    for (int j = 0; j <= TileCells; j++)
                    {
                        double h = body.TerrainAltitude(lat0 + i * Cell, lon0 + j * Cell, true);
                        t[i * (TileCells + 1) + j] = (float)(body.ocean ? Math.Max(0, h) : h);
                    }
                tiles[k] = t;
            }
        }

        public static int Pending => requests.Count;
    }

    /// <summary>
    /// ELE sensor engine. Every 0.2 s the main thread copies a small snapshot (no Unity objects) of radars and targets;
    /// a worker pool (all cores but two) computes terrain line of sight for every radar/target pair against the terrain grid,
    /// and publishes the result with a single reference swap. The game never waits for it; radar code reads the latest result.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class SensorEngine : MonoBehaviour
    {
        public struct Node { public uint id; public double lat, lon, alt; public bool radar; public float radarRange; }

        public sealed class Result
        {
            public HashSet<ulong> masked = new();   // (radar id, target id) pairs with terrain in the way
            public int pairs; public double ms; public int cycle;
        }

        public static volatile Result Latest = new();
        public static int Threads { get; private set; }
        public static double LastCycleMs => Latest.ms;
        public static int Cycle;
        static double bodyRadius;
        static volatile bool busy;
        float nextSnap;

        public static bool Masked(Vessel radar, Vessel target)
        {
            if (radar == null || target == null) return false;
            return Latest.masked.Contains(((ulong)radar.persistentId << 32) | target.persistentId);
        }

        void Start()
        {
            Threads = Math.Max(1, Environment.ProcessorCount - 2);
            Debug.Log($"[BDArmoryAvionics]: sensor engine: {Threads} worker threads");
        }

        void Update()
        {
            var body = FlightGlobals.currentMainBody;
            TerrainGrid.Fill(body, 2); // ~2 x 1089 terrain samples per frame at most
            if (Time.time < nextSnap || busy || body == null) return;
            nextSnap = Time.time + 0.2f;
            bodyRadius = body.Radius;
            var nodes = new List<Node>();
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed || v.vesselType == VesselType.Debris) continue;
                bool radar = false; float range = 0;
                foreach (var r in v.FindPartModulesImplementing<ModuleRadar>()) if (r != null && r.radarEnabled) { radar = true; range = Mathf.Max(range, r.radarMaxDistanceDetect); }
                nodes.Add(new Node { id = v.persistentId, lat = v.latitude, lon = v.longitude, alt = v.altitude, radar = radar, radarRange = range });
            }
            busy = true;
            var snap = nodes.ToArray();
            double R = bodyRadius;
            Task.Run(() =>
            {
                try { Latest = Compute(snap, R); }
                catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: sensor engine: {e.Message}"); }
                finally { busy = false; }
            });
        }

        static Vector3d ToXYZ(double lat, double lon, double alt, double R)
        {
            double la = lat * Math.PI / 180, lo = lon * Math.PI / 180, r = R + alt;
            return new Vector3d(r * Math.Cos(la) * Math.Cos(lo), r * Math.Sin(la), r * Math.Cos(la) * Math.Sin(lo));
        }

        /// <summary>Worker: line of sight for each radar/target pair, in parallel.</summary>
        static Result Compute(Node[] n, double R)
        {
            var sw = Stopwatch.StartNew();
            var res = new Result();
            var pairs = new List<(int a, int b)>();
            for (int a = 0; a < n.Length; a++)
            {
                if (!n[a].radar) continue;
                for (int b = 0; b < n.Length; b++) if (a != b) pairs.Add((a, b));
            }
            var masked = new ConcurrentBag<ulong>();
            Parallel.For(0, pairs.Count, new ParallelOptions { MaxDegreeOfParallelism = Threads }, k =>
            {
                var (a, b) = pairs[k];
                if (Blocked(n[a], n[b], R)) masked.Add(((ulong)n[a].id << 32) | n[b].id);
            });
            foreach (var m in masked) res.masked.Add(m);
            res.pairs = pairs.Count; res.ms = sw.Elapsed.TotalMilliseconds; res.cycle = Interlocked.Increment(ref Cycle);
            return res;
        }

        /// <summary>Terrain between two points? Samples the straight line (with planet curvature) against the terrain grid.</summary>
        static bool Blocked(Node a, Node b, double R)
        {
            Vector3d pa = ToXYZ(a.lat, a.lon, a.alt, R), pb = ToXYZ(b.lat, b.lon, b.alt, R);
            double dist = (pb - pa).magnitude;
            if (dist < 300) return false;
            int steps = (int)Math.Min(400, Math.Max(16, dist / 200)); // one sample per ~200 m, up to 400
            for (int i = 1; i < steps; i++)
            {
                Vector3d p = pa + (pb - pa) * ((double)i / steps);
                double r = p.magnitude;
                double lat = Math.Asin(p.y / r) * 180 / Math.PI, lon = Math.Atan2(p.z, p.x) * 180 / Math.PI;
                float h = TerrainGrid.Height(lat, lon);
                if (float.IsNaN(h)) continue; // tile still loading: assume clear
                if (r - R < h + 5) return true;
            }
            return false;
        }
    }

    /// <summary>Radars can't see through hills at any range (BDArmory's own check only reaches the nearby terrain colliders).</summary>
    [HarmonyPatch(typeof(RadarUtils), nameof(RadarUtils.RadarUpdateScanLock))]
    static class Patch_RadarScan_TerrainMask
    {
        static int logged;
        static void Postfix(ModuleRadar radar, ref TargetSignatureData[] dataArray)
        {
            if (radar == null || radar.vessel == null || dataArray == null || !AvionicsSettings.RadarTerrainMask) return;
            for (int i = 0; i < dataArray.Length; i++)
            {
                var d = dataArray[i];
                if (!d.exists || d.vessel == null) continue;
                if (!SensorEngine.Masked(radar.vessel, d.vessel)) continue;
                if (logged++ < 20) Debug.Log($"[BDArmoryAvionics]: radar masked: {radar.vessel.GetName()} can't see {d.vessel.GetName()} (terrain)");
                dataArray[i] = TargetSignatureData.noTarget;
            }
        }
    }

    /// <summary>A locked target that goes behind terrain is lost after a short grace period.</summary>
    [HarmonyPatch(typeof(RadarUtils), nameof(RadarUtils.RadarUpdateLockTrack))]
    static class Patch_RadarLock_TerrainMask
    {
        static readonly Dictionary<(uint, uint), float> since = new();
        static void Postfix(ModuleRadar radar, Vessel lockedVessel, ref bool __result)
        {
            if (!__result || radar == null || radar.vessel == null || lockedVessel == null || !AvionicsSettings.RadarTerrainMask) return;
            var key = (radar.vessel.persistentId, lockedVessel.persistentId);
            if (!SensorEngine.Masked(radar.vessel, lockedVessel)) { since.Remove(key); return; }
            if (!since.TryGetValue(key, out float t0)) { since[key] = Time.time; return; }
            if (Time.time - t0 > 1.5f) { __result = false; since.Remove(key); Debug.Log($"[BDArmoryAvionics]: radar lock lost: {lockedVessel.GetName()} went behind terrain"); }
        }
    }
}
