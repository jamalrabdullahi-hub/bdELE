using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

using BDArmory.FX;

namespace BDArmory.Avionics
{
    /// <summary>
    /// BDArmory shows every weapon's explosion model at the same size. This scales its particles by warhead size:
    /// k = (TNT / 20 kg)^(1/3) (the same cube-root law as the blast range), so an AMRAAM stays as it is and an Iskander's
    /// fireball is about three times wider. Pooled explosions are reset to their original values before each use.
    /// </summary>
    public static class ExplosionScaling
    {
        public const float RefTnt = 20f;

        struct Orig { public float minSize, maxSize; public Vector3 localVel, rndVel; public int minEm, maxEm; }
        static readonly Dictionary<KSPParticleEmitter, Orig> originals = new();

        public static float Factor(float tnt)
        {
            float k = Mathf.Pow(Mathf.Max(tnt, 0.001f) / RefTnt, 1f / 3f) * AvionicsSettings.ExplosionSizeCurve;
            return Mathf.Clamp(k, 0.4f, AvionicsSettings.ExplosionSizeCap);
        }

        public static void Apply(ExplosionFx fx)
        {
            if (fx == null || fx.pEmitters == null) return;
            float k = AvionicsSettings.ExplosionScale ? Factor(fx.Power) : 1f;
            float em = Mathf.Clamp(k, 1f, 2.5f); // more particles for big blasts, but not k^3 of them
            foreach (var pe in fx.pEmitters)
            {
                if (pe == null) continue;
                if (!originals.TryGetValue(pe, out var o))
                    originals[pe] = o = new Orig { minSize = pe.minSize, maxSize = pe.maxSize, localVel = pe.localVelocity, rndVel = pe.rndVelocity, minEm = pe.minEmission, maxEm = pe.maxEmission };
                pe.minSize = o.minSize * k; pe.maxSize = o.maxSize * k;
                pe.localVelocity = o.localVel * k; pe.rndVelocity = o.rndVel * k;
                pe.minEmission = Mathf.RoundToInt(o.minEm * em); pe.maxEmission = Mathf.RoundToInt(o.maxEm * em);
            }
            if (k > 1.5f || k < 0.7f) Debug.Log($"[BDArmoryAvionics]: explosion {fx.SourceWeaponName ?? "?"} tnt {fx.Power:F1} kg, fireball x{k:F2}");
        }
    }

    [HarmonyPatch(typeof(ExplosionFx), "OnEnable")]
    static class Patch_ExplosionFx_OnEnable
    {
        static void Postfix(ExplosionFx __instance)
        {
            try { ExplosionScaling.Apply(__instance); } catch (System.Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: explosion scaling: {e.Message}"); }
        }
    }
}
