using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

using BDArmory.Damage;
using BDArmory.FX;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Dynamic damage: hits leave a mark on the craft.
    ///   Dents:   shell hits and blasts push the part's mesh in around the impact (bigger round / closer blast = deeper).
    ///   Scorch:  dark burn decals at hit points, stuck to the part.
    ///   Bending: parts that drop below 50% / 25% HP bend a few degrees (the part's model and everything on it: a bent
    ///            barrel shoots off, a bent rotor shakes), and their joint gets weaker.
    /// Budgets: a few dents per frame from a queue, vertex cap per mesh, decal caps, nothing for vessels far away.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class DynamicDamage : MonoBehaviour
    {
        public static DynamicDamage Instance;
        const int DentsPerFrame = 4, MaxVerts = 8000, DecalsPerPart = 10, DecalsTotal = 300;
        const float MaxRange = 3000f;

        struct Hit { public Part part; public Vector3 point, dir; public float radius, depth; public bool decal; }
        readonly Queue<Hit> queue = new();
        readonly LinkedList<GameObject> decals = new();
        Material decalMat;

        void Awake() { Instance = this; }
        void OnDestroy() { if (Instance == this) Instance = null; }

        public static bool On => AvionicsSettings.DynamicDamage && Instance != null;

        /// <summary>Shell hit: caliber in mm, impact speed in m/s, mass in kg.</summary>
        public static void Bullet(Part p, Vector3 point, Vector3 dir, float caliber, float speed, float mass, bool explosive)
        {
            if (!On || p == null || !Near(p)) return;
            float energy = 0.5f * Mathf.Max(0.01f, mass) * speed * speed / 1e6f; // MJ
            float k = AvionicsSettings.DamageIntensity;
            float radius = Mathf.Clamp(caliber / 1000f * 4f + (explosive ? 0.25f : 0), 0.05f, 1.5f) * k;
            float depth = Mathf.Clamp(0.01f + Mathf.Sqrt(energy) * 0.04f + (explosive ? 0.05f : 0), 0.005f, 0.35f) * k;
            Instance.Enqueue(new Hit { part = p, point = point, dir = dir, radius = radius, depth = depth, decal = caliber >= 20 || explosive });
        }

        /// <summary>Blast on a part: power in kg TNT-ish, distance in m.</summary>
        public static void Blast(Part p, Vector3 center, Vector3 hitPoint, float power, float distance)
        {
            if (!On || p == null || !Near(p)) return;
            float k = AvionicsSettings.DamageIntensity;
            float strength = Mathf.Clamp01(Mathf.Pow(Mathf.Max(power, 0.01f), 1f / 3f) * 2f / Mathf.Max(1f, distance));
            if (strength < 0.03f) return;
            var dir = (hitPoint - center).sqrMagnitude > 0.01f ? (hitPoint - center).normalized : (p.transform.position - center).normalized;
            Instance.Enqueue(new Hit { part = p, point = hitPoint, dir = dir, radius = Mathf.Lerp(0.4f, 2.5f, strength) * k, depth = Mathf.Lerp(0.02f, 0.3f, strength) * k, decal = true });
        }

        static bool Near(Part p) => FlightGlobals.ActiveVessel != null && (p.transform.position - FlightGlobals.ActiveVessel.CoM).sqrMagnitude < MaxRange * MaxRange
            || FlightCamera.fetch != null && (p.transform.position - FlightCamera.fetch.transform.position).sqrMagnitude < MaxRange * MaxRange;

        void Enqueue(Hit h) { if (queue.Count < 200) queue.Enqueue(h); }

        void Update()
        {
            for (int i = 0; i < DentsPerFrame && queue.Count > 0; i++)
            {
                var h = queue.Dequeue();
                if (h.part == null || h.part.transform == null) continue;
                var s = h.part.GetComponent<PartDamageState>() ?? h.part.gameObject.AddComponent<PartDamageState>();
                s.Dent(h.point, h.dir, h.radius, h.depth);
                if (h.decal) Decal(h.part, h.point, -h.dir, h.radius);
            }
        }

        void Decal(Part p, Vector3 point, Vector3 normal, float radius)
        {
            if (decalMat == null)
            {
                var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                {
                    float d = Mathf.Clamp01(new Vector2(x - 31.5f, y - 31.5f).magnitude / 32f);
                    float n = Mathf.PerlinNoise(x * 0.15f, y * 0.15f);
                    tex.SetPixel(x, y, new Color(0.04f, 0.035f, 0.03f, Mathf.Clamp01((1 - d * d) * (0.6f + 0.5f * n)) * 0.9f));
                }
                tex.Apply();
                decalMat = new Material(Shader.Find("KSP/Particles/Alpha Blended") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended")) { mainTexture = tex };
                decalMat.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            }
            var s = p.GetComponent<PartDamageState>();
            if (s == null) return;
            if (s.decals.Count >= DecalsPerPart) { var old = s.decals[0]; s.decals.RemoveAt(0); decals.Remove(old); if (old) Destroy(old); }
            if (decals.Count >= DecalsTotal) { var old = decals.First.Value; decals.RemoveFirst(); if (old) Destroy(old); }
            // Snap to the surface along the shot line
            if (Physics.Raycast(point + normal * 0.5f, -normal, out var hit, 1.5f, 1 << 0) && hit.collider != null && hit.collider.GetComponentInParent<Part>() == p) { point = hit.point; normal = hit.normal; }
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad); Destroy(q.GetComponent<Collider>());
            q.name = "ELE scorch"; q.GetComponent<Renderer>().sharedMaterial = decalMat;
            q.transform.SetParent(p.transform, true);
            q.transform.position = point + normal * 0.01f;
            q.transform.rotation = Quaternion.LookRotation(-normal) * Quaternion.Euler(0, 0, Random.Range(0f, 360f));
            float size = Mathf.Clamp(radius * 1.6f, 0.15f, 3f);
            q.transform.localScale = Vector3.one * size / Mathf.Max(0.01f, p.transform.lossyScale.x);
            s.decals.Add(q); decals.AddLast(q);
        }

        /// <summary>How bent a part is, 0..1 (used for rotor vibration).</summary>
        public static float Bend(Part p) => p != null && p.GetComponent<PartDamageState>() is PartDamageState s ? s.bend : 0;
    }

    /// <summary>Per-part damage state: deformed mesh copies, decals, and the bend applied at HP thresholds.</summary>
    public class PartDamageState : MonoBehaviour
    {
        class MeshDent { public MeshFilter mf; public Mesh mesh; public Vector3[] verts; }
        readonly List<MeshDent> meshes = new();
        public readonly List<GameObject> decals = new();
        public float bend;
        Part part; HitpointTracker hp; bool scanned; int stage; float nextCheck;

        void Awake() { part = GetComponent<Part>(); }

        void Scan()
        {
            scanned = true;
            foreach (var mf in part.FindModelComponents<MeshFilter>())
            {
                var shared = mf.sharedMesh;
                if (shared == null || !shared.isReadable || shared.vertexCount > 8000 || mf.GetComponent<MeshRenderer>() == null || !mf.GetComponent<MeshRenderer>().enabled) continue;
                var m = mf.mesh; // per-part copy
                meshes.Add(new MeshDent { mf = mf, mesh = m, verts = m.vertices });
            }
        }

        public void Dent(Vector3 worldPoint, Vector3 worldDir, float radius, float depth)
        {
            if (!scanned) Scan();
            foreach (var d in meshes)
            {
                if (d.mf == null) continue;
                var t = d.mf.transform;
                Vector3 lp = t.InverseTransformPoint(worldPoint), ldir = t.InverseTransformDirection(worldDir).normalized;
                float scale = Mathf.Max(0.001f, t.lossyScale.magnitude / 1.732f);
                float r = radius / scale, dep = depth / scale, r2 = r * r;
                if (!d.mesh.bounds.Contains(lp) && d.mesh.bounds.SqrDistance(lp) > r2) continue;
                bool changed = false;
                for (int i = 0; i < d.verts.Length; i++)
                {
                    float s2 = (d.verts[i] - lp).sqrMagnitude;
                    if (s2 >= r2) continue;
                    float f = 1 - Mathf.Sqrt(s2) / r; f *= f;
                    d.verts[i] += ldir * (dep * f * Random.Range(0.7f, 1.1f));
                    changed = true;
                }
                if (!changed) continue;
                d.mesh.vertices = d.verts; d.mesh.RecalculateNormals(); d.mesh.RecalculateBounds();
            }
        }

        void FixedUpdate()
        {
            if (Time.time < nextCheck || part == null) return;
            nextCheck = Time.time + 0.5f;
            if (hp == null) hp = part.FindModuleImplementing<HitpointTracker>();
            if (hp == null || hp.GetMaxHitpoints() <= 0) return;
            float f = hp.Hitpoints / hp.GetMaxHitpoints();
            int want = f < 0.25f ? 2 : f < 0.5f ? 1 : 0;
            if (want <= stage || !AvionicsSettings.DynamicDamage) return;
            stage = want;
            var model = part.transform.Find("model");
            if (model == null) return;
            float deg = Random.Range(2f, 5f) * AvionicsSettings.DamageIntensity;
            var axis = Vector3.Cross(Random.onUnitSphere, Vector3.up).normalized; if (axis.sqrMagnitude < 0.1f) axis = Vector3.right;
            model.localRotation = Quaternion.AngleAxis(deg, axis) * model.localRotation;
            bend += deg / 10f;
            part.breakingForce *= 0.7f; part.breakingTorque *= 0.7f;
            if (part.attachJoint != null) part.attachJoint.SetBreakingForces(part.breakingForce, part.breakingTorque);
        }
    }

    // ---- hooks ----

    [HarmonyPatch]
    static class Patch_DamageBullet
    {
        static System.Reflection.MethodBase TargetMethod() => AccessTools.Method("BDArmory.Utils.ProjectileUtils:ApplyDamage");
        static void Postfix(Part hitPart, RaycastHit hit, float caliber, float projmass, float impactVelocity, bool explosive)
        {
            try { DynamicDamage.Bullet(hitPart, hit.point, hit.normal.sqrMagnitude > 0 ? -hit.normal : Vector3.forward, caliber, impactVelocity, projmass, explosive); } catch { }
        }
    }

    [HarmonyPatch]
    static class Patch_DamageBlast
    {
        static System.Reflection.MethodBase TargetMethod() => AccessTools.Method("BDArmory.FX.ExplosionFx:ExecutePartBlastEvent");
        static readonly Dictionary<string, System.Reflection.PropertyInfo> props = new();
        static object Get(object o, string n)
        {
            if (!props.TryGetValue(n, out var p)) props[n] = p = AccessTools.Property(o.GetType(), n);
            return p?.GetValue(o);
        }
        static void Prefix(ExplosionFx __instance, object __0)
        {
            try
            {
                if (!DynamicDamage.On || __0 == null) return;
                var part = Get(__0, "Part") as Part;
                if (part == null) return;
                var hitPoint = Get(__0, "HitPoint") is Vector3 h ? h : part.transform.position;
                float dist = Get(__0, "Distance") is float d ? d : (hitPoint - __instance.Position).magnitude;
                DynamicDamage.Blast(part, __instance.Position, hitPoint, __instance.Power, dist);
            }
            catch { }
        }
    }
}
