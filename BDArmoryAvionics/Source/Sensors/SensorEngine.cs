using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

using BDArmory.CounterMeasure;
using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Targeting;
using BDArmory.UI;
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
        public static int Pending => requests.Count;

        static long Key(int ti, int tj) => ((long)ti << 32) ^ (uint)tj;

        /// <summary>Worker-safe: terrain height (m, sea clamped to 0) or NaN if the tile isn't loaded yet (it gets requested).</summary>
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
            int w = TileCells + 1;
            float h00 = t[i * w + j], h01 = t[i * w + j + 1], h10 = t[(i + 1) * w + j], h11 = t[(i + 1) * w + j + 1];
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
                int w = TileCells + 1;
                var t = new float[w * w];
                double lat0 = ti * TileDeg - 90, lon0 = tj * TileDeg - 180;
                for (int i = 0; i < w; i++)
                    for (int j = 0; j < w; j++)
                    {
                        double h = body.TerrainAltitude(lat0 + i * Cell, lon0 + j * Cell, true);
                        t[i * w + j] = (float)(body.ocean ? Math.Max(0, h) : h);
                    }
                tiles[k] = t;
            }
        }
    }

    /// <summary>
    /// ELE sensor engine. Every 0.2 s the main thread copies a small snapshot (no Unity objects) of radars, targets and jammers;
    /// a worker pool (all cores but two) computes, for every radar/target pair: terrain line of sight, detection probability
    /// (radar curve vs radar cross-section, jamming and burn-through), deception, per-team track quality, and every 3 s the
    /// terrain-shadowed coverage of friendly radars. Results are published with one reference swap; the game never waits.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class SensorEngine : MonoBehaviour
    {
        public struct Node
        {
            public uint id; public string team;
            public double lat, lon, alt;
            public float rcs, jam; public bool deception;
            public bool radar; public float rangeM; public float[] minSig; // min detectable RCS at 16 ranges, 0..range
        }

        public sealed class Result
        {
            public HashSet<ulong> masked = new();      // terrain in the way
            public HashSet<ulong> undetected = new();  // not detected this cycle (weak return or jammed)
            public HashSet<ulong> deceived = new();    // deception jammer beating the radar
            public Dictionary<ulong, float> pd = new();
            public Dictionary<(string, uint), float> quality = new();
            public List<(uint radar, double lat, double lon, float burnM)> burnThrough = new(); // vs the player's craft
            public Dictionary<uint, (double lat, double lon, float[] reach)> coverage = new(); // friendly radars, 72 azimuths
            public int pairs; public double ms; public int cycle;
        }

        public static volatile Result Latest = new();
        public static int Threads { get; private set; }
        public static int Cycle;
        static double bodyRadius;
        static volatile bool busy;
        float nextSnap, nextCover;
        static readonly Dictionary<(string, uint), float> qualityState = new(); // worker-owned (one cycle at a time)
        static Dictionary<uint, (double, double, float[])> coverState = new();
        const double JamK = 1.5e-10; // J/S = JamK * jam * R^2 / RCS for a jammer on the target: burn-through R = sqrt(RCS / (JamK * jam))

        static ulong Pair(uint a, uint b) => ((ulong)a << 32) | b;
        public static bool Masked(Vessel radar, Vessel target) => radar != null && target != null && Latest.masked.Contains(Pair(radar.persistentId, target.persistentId));
        public static bool Undetected(Vessel radar, Vessel target) => radar != null && target != null && Latest.undetected.Contains(Pair(radar.persistentId, target.persistentId));
        public static bool Deceived(Vessel radar, Vessel target) => radar != null && target != null && Latest.deceived.Contains(Pair(radar.persistentId, target.persistentId));
        public static float Quality(string team, Vessel v) => team != null && v != null && Latest.quality.TryGetValue((team, v.persistentId), out var q) ? q : -1f;

        void Start()
        {
            Threads = Math.Max(1, Environment.ProcessorCount - 2);
            qualityState.Clear(); coverState = new();
            Debug.Log($"[BDArmoryAvionics]: sensor engine: {Threads} worker threads");
        }

        void Update()
        {
            var body = FlightGlobals.currentMainBody;
            TerrainGrid.Fill(body, 2); // at most ~2 x 1089 terrain samples per frame
            if (Time.time < nextSnap || busy || body == null) return;
            nextSnap = Time.time + 0.2f;
            bodyRadius = body.Radius;
            string myTeam = BDArmorySetup.Instance?.ActiveWeaponManager?.Team?.Name;
            uint myId = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0;
            var nodes = new List<Node>();
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed || v.vesselType == VesselType.Debris) continue;
                var n = new Node { id = v.persistentId, lat = v.latitude, lon = v.longitude, alt = v.altitude, team = v.ActiveController().WM?.Team?.Name ?? "" };
                var ti = v.gameObject.GetComponent<TargetInfo>();
                n.rcs = ti != null && ti.radarModifiedSignature > 0 ? ti.radarModifiedSignature : 10f;
                var ecm = v.gameObject.GetComponent<VesselECMJInfo>();
                if (ecm != null && ecm.jammerEnabled) { n.jam = ecm.jammerStrength; n.deception = ElectronicWarfare.JammerMode(v) == "Deception"; }
                foreach (var r in v.FindPartModulesImplementing<ModuleRadar>())
                {
                    if (r == null || !r.radarEnabled || r.radarMaxDistanceDetect * 1000f <= n.rangeM) continue;
                    n.radar = true; n.rangeM = r.radarMaxDistanceDetect * 1000f;
                    n.minSig = new float[16];
                    for (int i = 0; i < 16; i++) n.minSig[i] = Mathf.Max(0.001f, r.radarDetectionCurve.Evaluate(r.radarMaxDistanceDetect * i / 15f));
                }
                nodes.Add(n);
            }
            bool cover = Time.time >= nextCover;
            if (cover) nextCover = Time.time + 3f;
            busy = true;
            var snap = nodes.ToArray();
            double R = bodyRadius;
            Task.Run(() =>
            {
                try { Latest = Compute(snap, R, myTeam, myId, cover); }
                catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: sensor engine: {e}"); }
                finally { busy = false; }
            });
        }

        static Vector3d ToXYZ(double lat, double lon, double alt, double R)
        {
            double la = lat * Math.PI / 180, lo = lon * Math.PI / 180, r = R + alt;
            return new Vector3d(r * Math.Cos(la) * Math.Cos(lo), r * Math.Sin(la), r * Math.Cos(la) * Math.Sin(lo));
        }

        static float MinSig(in Node r, double d)
        {
            double f = Math.Max(0, Math.Min(15, d / Math.Max(1, r.rangeM) * 15));
            int i = (int)f; int j = Math.Min(15, i + 1); double a = f - i;
            return (float)(r.minSig[i] * (1 - a) + r.minSig[j] * a);
        }

        [ThreadStatic] static System.Random rng;

        /// <summary>Worker: everything for every radar/target pair, in parallel.</summary>
        static Result Compute(Node[] n, double R, string myTeam, uint myId, bool cover)
        {
            var sw = Stopwatch.StartNew();
            var res = new Result();
            var xyz = new Vector3d[n.Length];
            for (int i = 0; i < n.Length; i++) xyz[i] = ToXYZ(n[i].lat, n[i].lon, n[i].alt, R);
            var pairs = new List<(int a, int b)>();
            for (int a = 0; a < n.Length; a++)
            {
                if (!n[a].radar) continue;
                for (int b = 0; b < n.Length; b++) if (a != b && (xyz[a] - xyz[b]).magnitude < n[a].rangeM * 1.2) pairs.Add((a, b));
            }
            var masked = new ConcurrentBag<ulong>(); var undetected = new ConcurrentBag<ulong>(); var deceived = new ConcurrentBag<ulong>();
            var pd = new ConcurrentDictionary<ulong, float>();
            var seen = new ConcurrentDictionary<(string, uint), byte>();
            Parallel.For(0, pairs.Count, new ParallelOptions { MaxDegreeOfParallelism = Threads }, k =>
            {
                rng ??= new System.Random(Thread.CurrentThread.ManagedThreadId * 7919 + Environment.TickCount);
                var (a, b) = pairs[k];
                ulong key = Pair(n[a].id, n[b].id);
                if (Blocked(xyz[a], xyz[b], R)) { masked.Add(key); return; }
                double d = (xyz[a] - xyz[b]).magnitude;
                double margin = n[b].rcs / MinSig(n[a], d);
                // Jamming: every enemy jammer adds J/S (self-protection and stand-off), falling with its distance from the radar.
                double js = 0; bool dec = false;
                for (int j = 0; j < n.Length; j++)
                {
                    if (n[j].jam <= 0 || n[j].team == n[a].team) continue;
                    double rj = Math.Max(100, (xyz[j] - xyz[a]).magnitude);
                    if (rj > n[a].rangeM * 2) continue;
                    double s = JamK * n[j].jam * d * d * d * d / (n[b].rcs * rj * rj);
                    js += s;
                    if (j == b && n[j].deception && s > 1) dec = true; // deception jammer on the target, not yet burned through
                }
                double eff = margin / (1 + js);
                float p = (float)(1 / (1 + Math.Exp(-5 * Math.Log(Math.Max(1e-6, eff)))));
                pd[key] = p;
                if (dec) deceived.Add(key);
                if (rng.NextDouble() > p) undetected.Add(key);
                else seen.TryAdd((n[a].team, n[b].id), 0);
            });
            foreach (var m in masked) res.masked.Add(m);
            foreach (var m in undetected) res.undetected.Add(m);
            foreach (var m in deceived) res.deceived.Add(m);
            foreach (var kv in pd) res.pd[kv.Key] = kv.Value;

            // Track quality per team: builds with each detecting scan cycle, fades without
            var teams = new HashSet<string>(); foreach (var x in n) if (x.radar) teams.Add(x.team);
            foreach (var t in teams)
                foreach (var x in n)
                {
                    if (x.team == t) continue;
                    var qk = (t, x.id);
                    qualityState.TryGetValue(qk, out float q);
                    q = seen.ContainsKey(qk) ? Math.Min(1f, q + 0.2f) : Math.Max(0f, q - 0.05f);
                    qualityState[qk] = q;
                    if (q > 0) res.quality[qk] = q;
                }

            // Burn-through range of each enemy radar against the player's craft (with its own jammer)
            int me = Array.FindIndex(n, x => x.id == myId);
            if (me >= 0 && n[me].jam > 0)
                for (int a = 0; a < n.Length; a++)
                    if (n[a].radar && n[a].team != n[me].team)
                        res.burnThrough.Add((n[a].id, n[a].lat, n[a].lon, (float)Math.Sqrt(n[me].rcs / (JamK * n[me].jam))));

            // Coverage with terrain shadows: friendly radars, 72 azimuths, targets 300 m above the ground
            if (cover)
            {
                var cov = new ConcurrentDictionary<uint, (double, double, float[])>();
                var radars = new List<int>(); for (int a = 0; a < n.Length; a++) if (n[a].radar && n[a].team == myTeam) radars.Add(a);
                Parallel.ForEach(radars, new ParallelOptions { MaxDegreeOfParallelism = Threads }, a =>
                {
                    var reach = new float[72];
                    for (int az = 0; az < 72; az++)
                    {
                        double brg = az * 5 * Math.PI / 180;
                        float far = 0;
                        for (int s = 1; s <= 24; s++)
                        {
                            double dist = n[a].rangeM * s / 24.0, ang = dist / R;
                            double la1 = n[a].lat * Math.PI / 180, lo1 = n[a].lon * Math.PI / 180;
                            double la2 = Math.Asin(Math.Sin(la1) * Math.Cos(ang) + Math.Cos(la1) * Math.Sin(ang) * Math.Cos(brg));
                            double lo2 = lo1 + Math.Atan2(Math.Sin(brg) * Math.Sin(ang) * Math.Cos(la1), Math.Cos(ang) - Math.Sin(la1) * Math.Sin(la2));
                            double lat2 = la2 * 180 / Math.PI, lon2 = lo2 * 180 / Math.PI;
                            float h = TerrainGrid.Height(lat2, lon2); if (float.IsNaN(h)) h = 0;
                            if (!Blocked(xyz[a], ToXYZ(lat2, lon2, h + 300, R), R)) far = (float)dist;
                        }
                        reach[az] = far;
                    }
                    cov[n[a].id] = (n[a].lat, n[a].lon, reach);
                });
                coverState = new Dictionary<uint, (double, double, float[])>(cov);
            }
            foreach (var kv in coverState) res.coverage[kv.Key] = kv.Value;

            res.pairs = pairs.Count; res.ms = sw.Elapsed.TotalMilliseconds; res.cycle = Interlocked.Increment(ref Cycle);
            return res;
        }

        /// <summary>Terrain between two points? Samples the straight line (with planet curvature) against the terrain grid.</summary>
        static bool Blocked(Vector3d pa, Vector3d pb, double R)
        {
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

    /// <summary>Radar scans: terrain hides targets at any range; weak or jammed returns drop out (edge contacts flicker before going solid).</summary>
    [HarmonyPatch(typeof(RadarUtils), nameof(RadarUtils.RadarUpdateScanLock))]
    static class Patch_RadarScan_Model
    {
        static int logged;
        static void Postfix(ModuleRadar radar, ref TargetSignatureData[] dataArray)
        {
            if (radar == null || radar.vessel == null || dataArray == null || !AvionicsSettings.RadarTerrainMask) return;
            for (int i = 0; i < dataArray.Length; i++)
            {
                var d = dataArray[i];
                if (!d.exists || d.vessel == null) continue;
                bool masked = SensorEngine.Masked(radar.vessel, d.vessel);
                if (!masked && !SensorEngine.Undetected(radar.vessel, d.vessel)) continue;
                if (masked && logged++ < 20) Debug.Log($"[BDArmoryAvionics]: radar masked: {radar.vessel.GetName()} can't see {d.vessel.GetName()} (terrain)");
                dataArray[i] = TargetSignatureData.noTarget;
            }
        }
    }

    /// <summary>Locks: lost 1.5 s after the target goes behind terrain or into the noise; a deception jammer walks the lock off in 4 s.</summary>
    [HarmonyPatch(typeof(RadarUtils), nameof(RadarUtils.RadarUpdateLockTrack))]
    static class Patch_RadarLock_Model
    {
        static readonly Dictionary<(uint, uint), float> lostSince = new(), decSince = new();
        static void Postfix(ModuleRadar radar, Vessel lockedVessel, ref bool __result)
        {
            if (!__result || radar == null || radar.vessel == null || lockedVessel == null || !AvionicsSettings.RadarTerrainMask) return;
            var key = (radar.vessel.persistentId, lockedVessel.persistentId);
            bool lost = SensorEngine.Masked(radar.vessel, lockedVessel) || SensorEngine.Latest.pd.TryGetValue(((ulong)key.Item1 << 32) | key.Item2, out var p) && p < 0.15f;
            if (!lost) lostSince.Remove(key);
            else if (!lostSince.TryGetValue(key, out float t0)) lostSince[key] = Time.time;
            else if (Time.time - t0 > 1.5f) { __result = false; lostSince.Remove(key); Debug.Log($"[BDArmoryAvionics]: radar lock lost: {lockedVessel.GetName()} ({(SensorEngine.Masked(radar.vessel, lockedVessel) ? "behind terrain" : "in the noise / jammed")})"); return; }
            if (!SensorEngine.Deceived(radar.vessel, lockedVessel)) decSince.Remove(key);
            else if (!decSince.TryGetValue(key, out float d0)) decSince[key] = Time.time;
            else if (Time.time - d0 > 4f) { __result = false; decSince.Remove(key); Debug.Log($"[BDArmoryAvionics]: radar lock lost: {lockedVessel.GetName()} (deception jamming, range-gate pull-off)"); }
        }
    }
}
