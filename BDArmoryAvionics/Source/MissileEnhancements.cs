using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.FX;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Procedural missile motor boost: x2 thrust and x2 propellant (configurable), applied when the missile reaches flight.
    /// Thrust and fuel flow are doubled every load (the engine resets them); the propellant only once (it's saved).
    /// The Missile Lab and flight estimator count the boost before launch too.
    /// </summary>
    public class ModuleMissileMotorBoost : PartModule
    {
        [KSPField] public float thrustMult = 2f;
        [KSPField] public float fuelMult = 2f;
        [KSPField(isPersistant = true)] public bool fuelBoosted;
        [KSPField(guiActive = false, guiActiveEditor = true, guiName = "ELE motor")] public string info = "";
        public bool thrustApplied;

        public override void OnStart(StartState state)
        {
            info = $"x{thrustMult:0.#} thrust, x{fuelMult:0.#} propellant (at launch)";
            if (HighLogic.LoadedSceneIsFlight) StartCoroutine(Apply());
        }

        IEnumerator Apply()
        {
            yield return null; yield return null; // after ProceduralParts has set the engine up
            if (!fuelBoosted)
            {
                foreach (PartResource r in part.Resources)
                    if (r.info != null && r.info.density > 0 && r.resourceName != "ElectricCharge") { r.maxAmount *= fuelMult; r.amount *= fuelMult; }
                fuelBoosted = true;
            }
            foreach (var e in part.FindModulesImplementing<ModuleEngines>())
            {
                e.maxThrust *= thrustMult; e.minThrust *= thrustMult;
                e.maxFuelFlow *= thrustMult; e.minFuelFlow *= thrustMult;
            }
            thrustApplied = true;
        }
    }

    /// <summary>Fragmentation (shrapnel) warhead: the normal blast, plus fragments out to rangeMult x the blast radius.</summary>
    public class ModuleFragmentationWarhead : PartModule
    {
        [KSPField] public int fragments = 100;       // rays, and the cap on bullet-hole effects
        [KSPField] public float rangeMult = 2f;      // fragment reach in blast radii
        [KSPField] public float fragMass = 0.03f;    // kg per fragment
        [KSPField] public float caliber = 12f;       // mm (damage model / hole size)
        [KSPField] public float fragSpeed = 1800f;   // m/s at the warhead, slows with distance
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Fragments")] public string info = "";

        public override void OnStart(StartState state) => info = $"{fragments} out to {rangeMult:0.#}x blast radius";
    }

    public static class Fragmentation
    {
        static readonly MethodInfo applyScore = AccessTools.Method("BDArmory.Utils.ProjectileUtils:ApplyScore");

        /// <summary>Called as BDArmory creates the explosion (part still exists).</summary>
        public static void Detonate(Part warhead, Vector3 pos, float tnt, string sourceVessel, string team)
        {
            var mod = warhead != null ? warhead.FindModuleImplementing<ModuleFragmentationWarhead>() : null;
            if (mod == null) return;
            var exp = warhead.FindModuleImplementing<BDExplosivePart>();
            float blast = exp != null && exp.blastRadius > 0 ? exp.blastRadius : 10f * Mathf.Pow(Mathf.Max(tnt, 0.01f), 1f / 3f);
            float range = blast * mod.rangeMult;
            int n = Mathf.Clamp(mod.fragments, 1, 400), holes = 0, hits = 0;
            float damage = 0;
            var own = warhead.vessel;
            int mask = (int)(LayerMasks.Parts | LayerMasks.Scenery | LayerMasks.EVA | LayerMasks.Wheels);
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            var hitBuf = new RaycastHit[16];
            for (int i = 0; i < n; i++)
            {
                // Even spread over a sphere (Fibonacci), slightly jittered so repeated blasts don't pattern.
                float y = 1f - (i + 0.5f) * 2f / n, r = Mathf.Sqrt(1f - y * y), th = golden * i + UnityEngine.Random.Range(-0.05f, 0.05f);
                var dir = new Vector3(Mathf.Cos(th) * r, y, Mathf.Sin(th) * r);
                int count = Physics.RaycastNonAlloc(pos, dir, hitBuf, range, mask);
                RaycastHit best = default; float bestD = float.MaxValue; Part bestPart = null;
                for (int k = 0; k < count; k++)
                {
                    var h = hitBuf[k];
                    var p = h.collider.GetComponentInParent<Part>();
                    if (p != null && (p == warhead || p.vessel == own)) continue; // own missile body
                    if (h.distance < bestD) { bestD = h.distance; best = h; bestPart = p; }
                }
                if (bestD == float.MaxValue || bestPart == null || bestPart.vessel == null) continue; // missed, or stopped by terrain
                float v = mod.fragSpeed * Mathf.Exp(-bestD / (range * 1.2f));
                float dmg = bestPart.AddBallisticDamage(mod.fragMass, mod.caliber, 1f, 1.2f, 1f, v, ExplosionSourceType.Missile, true);
                damage += dmg; hits++;
                try { applyScore?.Invoke(null, new object[] { bestPart, sourceVessel, (double)bestD, dmg, "Fragmentation", ExplosionSourceType.Missile, true }); } catch { }
                if (holes < 100)
                {
                    try { BulletHitFX.CreateBulletHit(bestPart, best.point, best, best.normal, false, mod.caliber, 1f, team); holes++; } catch { }
                }
            }
            Debug.Log($"[BDArmoryAvionics]: fragmentation warhead ({sourceVessel}): blast {blast:F0} m, fragments to {range:F0} m: {hits}/{n} hits, {damage:F0} damage, {holes} hole effects");
        }
    }

    /// <summary>
    /// Terminal attack modes for modular missiles (part menu "Terminal mode"):
    ///   Top attack - aim high over the target, dive steeply in the last stretch.
    ///   Sea skim   - stay a few metres above the sea / ground, pop up near the target and dive.
    /// Works by moving the aim point BDArmory's guidance steers to (its own guidance law still flies the missile).
    /// </summary>
    public class ModuleMissileTerminal : PartModule
    {
        public static readonly string[] Modes = ["Off", "Top attack", "Sea skim"];

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Terminal mode", groupName = "missileProfile"),
         UI_ChooseOption(options = ["Off", "Top attack", "Sea skim"], scene = UI_Scene.All)]
        public string mode = "Off";

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Attack height", guiUnits = " m", groupName = "missileProfile"),
         UI_FloatRange(minValue = 50f, maxValue = 3000f, stepIncrement = 50f, scene = UI_Scene.All)]
        public float attackHeight = 600f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Dive / pop-up range", guiUnits = " m", groupName = "missileProfile"),
         UI_FloatRange(minValue = 300f, maxValue = 8000f, stepIncrement = 100f, scene = UI_Scene.All)]
        public float diveRange = 1500f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Skim height", guiUnits = " m", groupName = "missileProfile"),
         UI_FloatRange(minValue = 5f, maxValue = 100f, stepIncrement = 5f, scene = UI_Scene.All)]
        public float skimHeight = 15f;

        /// <summary>The aim point to give BDArmory's guidance this frame.</summary>
        public Vector3 Aim(Vector3 missilePos, Vector3 target)
        {
            if (mode == "Off" || vessel == null) return target;
            var body = vessel.mainBody;
            Vector3 up = (target - (Vector3)body.position).normalized;
            Vector3 rel = target - missilePos;
            float horiz = Vector3.ProjectOnPlane(rel, up).magnitude;
            if (mode == "Top attack")
            {
                if (horiz < diveRange) return target;                        // dive
                return target + up * attackHeight + Vector3.ProjectOnPlane(-rel, up).normalized * diveRange * 0.8f; // fly to a point above and short of it
            }
            // Sea skim: hold skimHeight over the surface under the missile until the pop-up range, then pop up and dive.
            if (horiz < diveRange * 0.5f) return target;
            if (horiz < diveRange) return target + up * Mathf.Max(attackHeight * 0.3f, 100f);
            double ground = Math.Max(0, body.TerrainAltitude(body.GetLatitude(missilePos), body.GetLongitude(missilePos)));
            Vector3 ahead = missilePos + Vector3.ProjectOnPlane(rel, up).normalized * Mathf.Min(horiz, 2000f);
            double aheadAlt = body.GetAltitude(ahead);
            return ahead + up * (float)(ground + skimHeight - aheadAlt);
        }
    }

    [HarmonyPatch(typeof(BDModularGuidance), nameof(BDModularGuidance.GuidanceSteer))]
    static class Patch_MissileTerminal
    {
        static void Prefix(BDModularGuidance __instance, out Vector3 __state)
        {
            __state = default;
            try
            {
                var t = __instance.part != null ? __instance.part.FindModuleImplementing<ModuleMissileTerminal>() : null;
                if (t == null || t.mode == "Off" || !__instance.HasFired || __instance.TargetPosition == Vector3.zero) return;
                var prof = ModuleMissileProfile.For(__instance);
                if (prof != null && prof.IsICBM) return; // the ICBM profile steers itself
                __state = __instance.TargetPosition;
                __instance.TargetPosition = t.Aim(__instance.vessel.CoM, __state);
            }
            catch { __state = default; }
        }

        static void Finalizer(BDModularGuidance __instance, Vector3 __state)
        {
            if (__state != default) __instance.TargetPosition = __state;
        }
    }

    /// <summary>
    /// Dynamic launch zones: every 2 s, each armed craft's unfired modular missiles get their max engagement range
    /// from the flight estimator against the current target (its aspect, speed, and our altitude / speed),
    /// so the AI fires them when they can actually reach and holds when they can't.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MissileLaunchZones : MonoBehaviour
    {
        static readonly Dictionary<MissileBase, (float engage, float stat)> originals = new();
        float next;

        void Awake() => originals.Clear();

        void Update()
        {
            if (!FlightGlobals.ready || Time.time < next) return;
            next = Time.time + 2f;
            if (!AvionicsSettings.MissileDLZ) return;
            try
            {
                foreach (var v in FlightGlobals.VesselsLoaded)
                {
                    if (v == null || v.packed) continue;
                    var wm = v.ActiveController().WM;
                    var tgt = wm != null ? wm.currentTarget?.Vessel : null;
                    foreach (var mmg in VesselModuleRegistry.GetModules<BDModularGuidance>(v))
                    {
                        if (mmg == null || mmg.HasFired) continue;
                        if (!originals.ContainsKey(mmg)) originals[mmg] = (mmg.engageRangeMax, mmg.maxStaticLaunchRange);
                        var o = originals[mmg];
                        if (tgt == null) { mmg.engageRangeMax = o.engage; mmg.maxStaticLaunchRange = o.stat; continue; }
                        var est = MissileEstimator.Estimate(mmg, v.CoM, v.Velocity(), tgt.CoM, tgt.Velocity(), true, tgt.persistentId ^ mmg.part.persistentId);
                        if (!est.valid || est.maxRange <= 0) continue;
                        float r = Mathf.Clamp(est.maxRange * 0.85f, mmg.engageRangeMin + 200f, 500000f);
                        mmg.engageRangeMax = r; mmg.maxStaticLaunchRange = r;
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: launch zones: {e.Message}"); }
        }
    }

    /// <summary>In-flight modular missiles of a team, for the operator (watch through a nose camera, retarget).</summary>
    public static class MissileOperator
    {
        public static List<BDModularGuidance> InFlight(Competition.BDTeam team)
        {
            var list = new List<BDModularGuidance>();
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null) continue;
                foreach (var m in VesselModuleRegistry.GetModules<BDModularGuidance>(v))
                    if (m != null && m.HasFired && m.vessel != null && (team == null || m.Team == null || m.Team == team)) list.Add(m);
            }
            return list;
        }

        /// <summary>Retarget to a craft (radar / heat / laser modes keep their seeker, GPS gets the craft's position).</summary>
        public static void Retarget(BDModularGuidance m, Vessel v)
        {
            if (m == null || v == null) return;
            var ti = v.gameObject.GetComponent<TargetInfo>();
            if (ti != null) m.targetVessel = ti;
            m.targetGPSCoords = VectorUtils.WorldPositionToGeoCoords(v.CoM, v.mainBody);
            m.TargetPosition = v.CoM;
            Debug.Log($"[BDArmoryAvionics]: missile {m.vessel.GetName()} retargeted to {v.GetName()}");
            ScreenMessages.PostScreenMessage($"{m.vessel.GetName()}: retargeted to {v.GetName()}", 2f, ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>Retarget to a point (switches the missile to GPS guidance).</summary>
        public static void RetargetPoint(BDModularGuidance m, Vector3 world)
        {
            if (m == null || m.vessel == null) return;
            m.targetGPSCoords = VectorUtils.WorldPositionToGeoCoords(world, m.vessel.mainBody);
            m.TargetingMode = MissileBase.TargetingModes.Gps;
            m.targetVessel = null;
            m.TargetPosition = world;
            Debug.Log($"[BDArmoryAvionics]: missile {m.vessel.GetName()} retargeted to a point");
            ScreenMessages.PostScreenMessage($"{m.vessel.GetName()}: new aim point", 2f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
