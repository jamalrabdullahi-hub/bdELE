using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

using BDArmory.Bullets;
using BDArmory.Extensions;
using BDArmory.FX;
using BDArmory.Utils;
using BDArmory.Weapons;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Shrapnel on every explosion: on top of BDArmory's blast, fragments fly out to twice the blast radius.
    /// 200 to 1000 fragments (by explosive mass; small shells get a few dozen), each hitting like a .50 cal round that slows with distance,
    /// with bullet-hole effects and visible streaks. Pattern: sphere in the air, half sphere on the ground, cone for directional charges.
    /// Fragmentation warheads (proceduralFragWarhead) keep their own heavier pattern.
    /// </summary>
    public static class Shrapnel
    {
        public static readonly string[] Patterns = { "Auto", "Sphere", "Half sphere", "Cone" };
        static readonly MethodInfo applyScore = AccessTools.Method("BDArmory.Utils.ProjectileUtils:ApplyScore");
        static readonly MethodInfo blastRange = AccessTools.Method("BDArmory.Utils.BlastPhysicsUtils:CalculateBlastRange");

        // .50 cal (12.7 mm) fragment
        const float FragMass = 0.046f, Caliber = 12.7f, FragSpeed = 900f;
        const int RayBudgetPerFrame = 2500, MaxHoles = 60, MaxStreaks = 40; // kept low so salvos stay smooth
        static int frame = -1, raysThisFrame;
        static readonly RaycastHit[] firstHits = new RaycastHit[RayBudgetPerFrame];

        public static float BlastRadius(Part explosive, float tnt)
        {
            var exp = explosive != null ? explosive.FindModuleImplementing<BDExplosivePart>() : null;
            if (exp != null && exp.blastRadius > 0) return exp.blastRadius;
            try { if (blastRange != null) return (float)blastRange.Invoke(null, new object[] { tnt }); } catch { }
            return 10f * Mathf.Pow(Mathf.Max(tnt, 0.001f), 1f / 3f);
        }

        public static void Detonate(Vector3 pos, float tnt, Part explosive, string sourceVessel, string team, Vector3 direction, float angle, ExplosionSourceType type)
        {
            if (!AvionicsSettings.Shrapnel || tnt <= 0.001f) return;
            if (Time.frameCount != frame) { frame = Time.frameCount; raysThisFrame = 0; }
            int n = tnt >= 1f ? Mathf.Clamp(Mathf.RoundToInt(200 + 40 * tnt), 200, 600) : Mathf.Clamp(Mathf.RoundToInt(20 + 180 * tnt), 10, 200);
            n = Mathf.Min(n, RayBudgetPerFrame - raysThisFrame);
            if (n <= 0) return;
            raysThisFrame += n;
            float range = BlastRadius(explosive, tnt) * AvionicsSettings.ShrapnelRangeMult;

            // Pattern
            string pattern = Patterns[Mathf.Clamp(AvionicsSettings.ShrapnelPattern, 0, Patterns.Length - 1)];
            Vector3 up = FlightGlobals.getUpAxis(pos);
            bool nearGround = false;
            if (Physics.Raycast(pos + up * 0.5f, -up, out var g, 3f, (int)LayerMasks.Scenery)) nearGround = true;
            float coneHalf = 180f; Vector3 axis = up;
            if (pattern == "Auto")
            {
                if (direction != Vector3.zero && angle < 90f) { coneHalf = Mathf.Max(15f, angle); axis = direction.normalized; pattern = "Cone"; }
                else if (nearGround) { coneHalf = 90f; pattern = "Half sphere"; }
                else pattern = "Sphere";
            }
            else if (pattern == "Half sphere") coneHalf = 90f;
            else if (pattern == "Cone") { coneHalf = Mathf.Clamp(angle < 90f ? angle : 45f, 15f, 89f); axis = direction != Vector3.zero ? direction.normalized : up; }

            var own = explosive != null ? explosive.vessel : null;
            int mask = (int)(LayerMasks.Parts | LayerMasks.Scenery | LayerMasks.EVA | LayerMasks.Wheels);
            var hitBuf = new RaycastHit[12];
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            float cosMin = Mathf.Cos(coneHalf * Mathf.Deg2Rad);
            Quaternion toAxis = Quaternion.FromToRotation(Vector3.up, axis);
            int hits = 0, holes = 0, streaks = 0; float damage = 0;
            var srcType = type == ExplosionSourceType.Other ? ExplosionSourceType.Missile : type;
            // All fragment rays go to Unity's physics job system in one batch (worker threads), first hit per ray.
            var dirs = new Vector3[n]; var reaches = new float[n];
            var cmds = new Unity.Collections.NativeArray<RaycastCommand>(n, Unity.Collections.Allocator.TempJob);
            var results = new Unity.Collections.NativeArray<RaycastHit>(n, Unity.Collections.Allocator.TempJob);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    // Even spread over the cap (Fibonacci over y in [cosMin, 1]), jittered.
                    float y = 1f - (i + 0.5f) / n * (1f - cosMin), r = Mathf.Sqrt(Mathf.Max(0, 1f - y * y)), th = golden * i + Random.Range(-0.05f, 0.05f);
                    dirs[i] = toAxis * new Vector3(Mathf.Cos(th) * r, y, Mathf.Sin(th) * r);
                    reaches[i] = range * Random.Range(0.7f, 1f);
                    cmds[i] = new RaycastCommand(pos, dirs[i], reaches[i], mask, 1);
                }
                RaycastCommand.ScheduleBatch(cmds, results, 32).Complete();
                for (int i = 0; i < n; i++) firstHits[i] = results[i];
            }
            finally { cmds.Dispose(); results.Dispose(); }

            for (int i = 0; i < n; i++)
            {
                Vector3 dir = dirs[i]; float reach = reaches[i];
                RaycastHit best = default; float bestD = float.MaxValue; Part bestPart = null;
                var fh = firstHits[i];
                if (fh.collider != null)
                {
                    var p0 = fh.collider.GetComponentInParent<Part>();
                    if (p0 != null && (p0 == explosive || (own != null && p0.vessel == own)))
                    {
                        // first hit is our own vessel: look past it the old way (rare)
                        int count = Physics.RaycastNonAlloc(pos, dir, hitBuf, reach, mask);
                        for (int k = 0; k < count; k++)
                        {
                            var h = hitBuf[k];
                            var p = h.collider.GetComponentInParent<Part>();
                            if (p != null && (p == explosive || (own != null && p.vessel == own))) continue;
                            if (h.distance < bestD) { bestD = h.distance; best = h; bestPart = p; }
                        }
                    }
                    else { best = fh; bestD = fh.distance; bestPart = p0; }
                }
                bool hit = bestD < float.MaxValue;
                if (streaks < MaxStreaks && (i % Mathf.Max(1, n / MaxStreaks)) == 0) { ShrapnelStreaks.Add(pos, pos + dir * (hit ? bestD : reach)); streaks++; }
                if (!hit || bestPart == null || bestPart.vessel == null) continue; // missed, or stopped by terrain
                float v = FragSpeed * Mathf.Exp(-bestD / (range * 0.8f));
                float dmg = bestPart.AddBallisticDamage(FragMass, Caliber, 1f, 1.1f, 1f, v, srcType, true);
                damage += dmg; hits++;
                try { applyScore?.Invoke(null, new object[] { bestPart, sourceVessel, (double)bestD, dmg, "Shrapnel", srcType, true }); } catch { }
                if (holes < MaxHoles) { try { BulletHitFX.CreateBulletHit(bestPart, best.point, best, best.normal, false, Caliber, 1f, team); holes++; } catch { } }
            }
            if (hits > 0 || tnt >= 1f)
                Debug.Log($"[BDArmoryAvionics]: shrapnel ({sourceVessel}, {tnt:F2} kg): {pattern}, {n} fragments to {range:F0} m: {hits} hits, {damage:F0} damage");
        }
    }

    /// <summary>Short bright streaks along some fragment paths (they fade in a quarter second).</summary>
    public class ShrapnelStreaks : MonoBehaviour
    {
        static ShrapnelStreaks inst;
        static Material mat;
        readonly List<(LineRenderer lr, float t0)> live = new();
        readonly Stack<LineRenderer> pool = new();
        const float Life = 0.25f;

        public static void Add(Vector3 a, Vector3 b)
        {
            if (inst == null) { var go = new GameObject("ELE shrapnel streaks"); inst = go.AddComponent<ShrapnelStreaks>(); }
            if (mat == null) mat = new Material(Shader.Find("KSP/Particles/Additive"));
            var lr = inst.pool.Count > 0 ? inst.pool.Pop() : inst.NewLine();
            lr.enabled = true;
            lr.positionCount = 2;
            // A streak runs part of the way, as if caught mid-flight.
            float f0 = Random.Range(0f, 0.4f), f1 = f0 + Random.Range(0.25f, 0.6f);
            lr.SetPosition(0, Vector3.Lerp(a, b, f0)); lr.SetPosition(1, Vector3.Lerp(a, b, Mathf.Min(1, f1)));
            inst.live.Add((lr, Time.time));
        }

        LineRenderer NewLine()
        {
            var go = new GameObject("streak");
            go.transform.SetParent(transform);
            var lr = go.AddComponent<LineRenderer>();
            lr.material = mat; lr.useWorldSpace = true;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; lr.receiveShadows = false;
            lr.startWidth = 0.12f; lr.endWidth = 0.03f;
            return lr;
        }

        void Update()
        {
            for (int i = live.Count - 1; i >= 0; i--)
            {
                var (lr, t0) = live[i];
                float k = (Time.time - t0) / Life;
                if (lr == null) { live.RemoveAt(i); continue; }
                if (k >= 1) { lr.enabled = false; pool.Push(lr); live.RemoveAt(i); continue; }
                var c = new Color(1f, 0.8f, 0.45f, 1f - k);
                lr.startColor = c; lr.endColor = new Color(1f, 0.5f, 0.2f, (1f - k) * 0.5f);
            }
        }

        void OnDestroy() { if (inst == this) inst = null; }
    }
}
