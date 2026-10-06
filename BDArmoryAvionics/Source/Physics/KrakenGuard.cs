using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Kraken Guard: catches the main kraken failure modes before they wreck a craft.
    ///   Gentle unpack: for 3 s after a craft loads / comes off rails, overlapping colliders push apart slowly
    ///                  (low max depenetration velocity) and G-force breakage is off, so load-time overlaps don't explode.
    ///   Stiffer solver: more PhysX solver iterations for normal-sized craft (springy joint chains wobble less).
    ///   Watchdog: every physics frame each part's motion is compared with its craft's robust (median) motion; a part
    ///             moving or spinning far beyond it, with no explosion nearby to explain it, is put back in line and logged.
    ///             Rotors, servos and wheels (which legitimately spin) are left alone.
    /// Everything logs to KSP.log as [ELE Kraken] with the vessel, part and what was happening.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class KrakenGuard : MonoBehaviour
    {
        public static int Fixes, Unpacks;
        public static string LastEvent = "";
        const float SettleTime = 3f, LinearLimit = 60f, AngularLimit = 50f, BlastMemory = 1.5f, BlastRadius = 60f;

        static readonly List<(float t, Vector3 pos)> blasts = new();
        readonly Dictionary<uint, float> settling = new();                 // vessel persistentId -> settle end time
        readonly Dictionary<Rigidbody, float> depen = new();               // original max depenetration velocity
        readonly Dictionary<Part, bool> exempt = new();                    // spins legitimately (rotor / servo / its children)
        readonly Dictionary<Part, float> lastLog = new();
        readonly Dictionary<Part, bool> wheel = new();
        bool IsWheel(Part p) { if (!wheel.TryGetValue(p, out var w)) wheel[p] = w = p.Modules.Contains("ModuleWheelBase"); return w; }
        readonly List<Vector3> vbuf = new(), wbuf = new();

        void Awake()
        {
            GameEvents.onVesselGoOffRails.Add(OnOffRails);
            GameEvents.onVesselWasModified.Add(OnModified);
            GameEvents.onCrash.Add(OnCrash); GameEvents.onCollision.Add(OnCrash); GameEvents.onCrashSplashdown.Add(OnCrash);
        }

        // crashes and collisions are real violence: stand down for that craft for a moment
        readonly Dictionary<uint, float> contact = new();
        void OnCrash(EventReport r) { var v = r?.origin?.vessel; if (v != null) contact[v.persistentId] = Time.time + 2f; }

        void OnDestroy()
        {
            GameEvents.onVesselGoOffRails.Remove(OnOffRails);
            GameEvents.onVesselWasModified.Remove(OnModified);
            GameEvents.onCrash.Remove(OnCrash); GameEvents.onCollision.Remove(OnCrash); GameEvents.onCrashSplashdown.Remove(OnCrash);
            foreach (var kv in depen) if (kv.Key != null) kv.Key.maxDepenetrationVelocity = kv.Value;
        }

        public static void Blast(Vector3 pos) { blasts.Add((Time.time, pos)); if (blasts.Count > 64) blasts.RemoveAt(0); }

        void OnModified(Vessel v) { if (v != null) foreach (var p in v.parts) exempt.Remove(p); }

        void OnOffRails(Vessel v)
        {
            if (!AvionicsSettings.KrakenGuard || v == null) return;
            Unpacks++;
            settling[v.persistentId] = Time.time + SettleTime;
            int iters = v.parts.Count <= 150 ? 12 : v.parts.Count <= 400 ? 8 : 0;
            foreach (var p in v.parts)
            {
                var rb = p.Rigidbody; if (rb == null) continue;
                if (!depen.ContainsKey(rb)) depen[rb] = rb.maxDepenetrationVelocity;
                rb.maxDepenetrationVelocity = 1.5f;                       // overlaps separate at 1.5 m/s, not explosively
                if (iters > 0) { rb.solverIterations = Mathf.Max(rb.solverIterations, iters); rb.solverVelocityIterations = Mathf.Max(rb.solverVelocityIterations, 4); }
            }
            v.IgnoreGForces(10);
        }

        void FixedUpdate()
        {
            if (!AvionicsSettings.KrakenGuard || !FlightGlobals.ready) return;
            float now = Time.time;
            // settle: keep G-forces off, then restore normal depenetration
            if (settling.Count > 0)
            {
                var done = new List<uint>();
                foreach (var kv in settling)
                {
                    var v = FindById(kv.Key);
                    if (v == null || now > kv.Value) { done.Add(kv.Key); if (v != null) Restore(v); continue; }
                    if (!v.packed) v.IgnoreGForces(2);
                }
                foreach (var id in done) settling.Remove(id);
            }
            if (AvionicsSettings.KrakenWatchdog) foreach (var v in FlightGlobals.VesselsLoaded) if (v != null && !v.packed && v.parts.Count > 1) Watch(v, now);
        }

        static Vessel FindById(uint id) { foreach (var v in FlightGlobals.VesselsLoaded) if (v != null && v.persistentId == id) return v; return null; }

        void Restore(Vessel v)
        {
            foreach (var p in v.parts)
            {
                var rb = p.Rigidbody; if (rb == null) continue;
                if (depen.TryGetValue(rb, out var d)) { rb.maxDepenetrationVelocity = d; depen.Remove(rb); }
            }
        }

        void Watch(Vessel v, float now)
        {
            if (contact.TryGetValue(v.persistentId, out var until) && now < until) return;
            foreach (var p in v.parts) if (p.currentCollisions != null && p.currentCollisions.Count > 0) { contact[v.persistentId] = now + 1f; return; } // touching something
            // robust craft motion: component-wise median of part velocities / spins (a krakened part can't drag it along)
            vbuf.Clear(); wbuf.Clear();
            foreach (var p in v.parts) { var rb = p.Rigidbody; if (rb == null || IsExempt(p)) continue; vbuf.Add(rb.velocity); wbuf.Add(rb.angularVelocity); }
            if (vbuf.Count < 2) return;
            Vector3 vMed = Median(vbuf), wMed = Median(wbuf), com = v.CoM;
            foreach (var p in v.parts)
            {
                var rb = p.Rigidbody; if (rb == null || IsExempt(p)) continue;
                Vector3 expect = vMed + Vector3.Cross(wMed, rb.worldCenterOfMass - com);
                float dv = (rb.velocity - expect).magnitude, dw = IsWheel(p) ? 0 : (rb.angularVelocity - wMed).magnitude;
                if (dv < LinearLimit && dw < AngularLimit) continue;
                if (NearBlast(rb.worldCenterOfMass, now)) continue;                               // real explosion: let it fly
                rb.velocity = expect; rb.angularVelocity = wMed;
                Fixes++;
                if (!lastLog.TryGetValue(p, out var t) || now - t > 2f)
                {
                    lastLog[p] = now;
                    bool settle = settling.ContainsKey(v.persistentId);
                    LastEvent = $"{v.vesselName}: {p.partInfo?.title} ({(dv >= LinearLimit ? $"{dv:F0} m/s" : "")}{(dw >= AngularLimit ? $" {dw:F0} rad/s" : "")})";
                    Debug.Log($"[ELE Kraken] runaway part caught: {v.vesselName} / {p.partInfo?.title}: relative speed {dv:F0} m/s, spin {dw:F0} rad/s, " +
                              $"{(settle ? "during unpack" : "in flight")}, craft {v.srfSpeed:F0} m/s, {v.parts.Count} parts");
                }
            }
        }

        bool IsExempt(Part p)
        {
            if (exempt.TryGetValue(p, out var e)) return e;
            e = false;
            for (var x = p; x != null && !e; x = x.parent)
                foreach (PartModule m in x.Modules)
                {
                    string n = m.moduleName;
                    if (n.Contains("Robotic") || n.Contains("Servo") || n.Contains("Rotor") || n == "ModuleHeliRotor" || n == "FSswitchEngineThrustTransform") { e = true; break; }
                }
            exempt[p] = e;
            return e;
        }

        static bool NearBlast(Vector3 pos, float now)
        {
            for (int i = blasts.Count - 1; i >= 0; i--)
            {
                if (now - blasts[i].t > BlastMemory) break;
                if ((blasts[i].pos - pos).sqrMagnitude < BlastRadius * BlastRadius) return true;
            }
            return false;
        }

        static Vector3 Median(List<Vector3> l)
        {
            int n = l.Count; var xs = new float[n]; var ys = new float[n]; var zs = new float[n];
            for (int i = 0; i < n; i++) { xs[i] = l[i].x; ys[i] = l[i].y; zs[i] = l[i].z; }
            System.Array.Sort(xs); System.Array.Sort(ys); System.Array.Sort(zs);
            return new Vector3(xs[n / 2], ys[n / 2], zs[n / 2]);
        }
    }

    [HarmonyPatch]
    static class Patch_KrakenBlast
    {
        static System.Reflection.MethodBase TargetMethod() => AccessTools.Method("BDArmory.FX.ExplosionFx:CreateExplosion");
        static void Postfix(Vector3 __0) { try { KrakenGuard.Blast(__0); } catch { } }
    }
}
