using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Battlefield marks that stay: scorch marks / craters on the ground, lingering fires (Destruction Effects' flame model
    /// when it's installed, so they match its burning wrecks) with smoke columns, and a heat-source list the EO/IR and FLIR
    /// views use (our fires, fresh scorch marks, and Destruction Effects' own burning parts). Capped by the quality setting.
    /// </summary>
    public static class Persistent
    {
        class Scorch { public Transform t; public float t0, life; }
        class Fire { public GameObject go; public Vector3 local; public float t0, life, nextSmoke, size; }

        static readonly List<Scorch> scorches = new();
        static readonly List<Fire> fires = new();
        static Material scorchMat;
        static Mesh quad;
        static Type deFlameType;
        static bool deResolved;
        static readonly List<(Vector3 pos, float intensity, float size)> heat = new();
        static float nextDeScan;
        static readonly List<Component> deFlames = new();

        static int MaxScorch => AvionicsSettings.ExplosionQuality switch { 1 => 24, 2 => 48, 3 => 64, _ => 0 };
        static int MaxFires => AvionicsSettings.ExplosionQuality switch { 1 => 4, 2 => 8, 3 => 12, _ => 0 };

        public static bool DEInstalled { get { ResolveDE(); return deFlameType != null; } }

        static void ResolveDE()
        {
            if (deResolved) return; deResolved = true;
            foreach (var a in AssemblyLoader.loadedAssemblies)
                if (a.name == "DestructionEffects") deFlameType = a.assembly.GetType("DestructionEffects.FlamingJointScript");
        }

        static Transform Root => ExplosionDirector.Instance != null ? ExplosionDirector.Instance.transform : null;

        /// <summary>A dark mark on the ground (a crater when it's big), facing the ground.</summary>
        public static void AddScorch(Vector3 groundPos, Vector3 normal, float radius, float life = 300f)
        {
            var root = Root;
            if (root == null || MaxScorch == 0) return;
            if (scorchMat == null)
            {
                // KSP's particle shaders colour by _TintColor (x2), not .color: dark, semi-opaque soot.
                scorchMat = new Material(Shader.Find("KSP/Particles/Alpha Blended")) { mainTexture = AvionicsDraw.GlowTexture };
                var soot = new Color(0.02f, 0.017f, 0.015f, 0.42f);
                scorchMat.color = soot;
                if (scorchMat.HasProperty("_TintColor")) scorchMat.SetColor("_TintColor", soot);
                quad = new Mesh
                {
                    vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(0.5f, 0, -0.5f) },
                    uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) },
                    triangles = new[] { 0, 1, 2, 0, 2, 3 },
                    colors = new[] { Color.black, Color.black, Color.black, Color.black }
                };
                quad.RecalculateNormals();
            }
            Scorch s;
            if (scorches.Count >= MaxScorch) { s = scorches[0]; scorches.RemoveAt(0); }
            else
            {
                var go = new GameObject("ELE scorch");
                go.AddComponent<MeshFilter>().sharedMesh = quad;
                var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = scorchMat; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
                go.transform.SetParent(root, false);
                s = new Scorch { t = go.transform };
            }
            s.t.gameObject.SetActive(true);
            s.t.position = groundPos + normal * 0.15f;
            s.t.rotation = Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.AngleAxis(UnityEngine.Random.Range(0f, 360f), Vector3.up);
            s.t.localScale = new Vector3(radius * 2f, 1, radius * 2f);
            s.t0 = Time.time; s.life = life;
            scorches.Add(s);
        }

        /// <summary>A fire that keeps burning (and smoking) for a while.</summary>
        public static void AddFire(Vector3 pos, float size, float life)
        {
            var root = Root;
            if (root == null || MaxFires == 0) return;
            if (fires.Count >= MaxFires) { var o = fires[0]; if (o.go != null) UnityEngine.Object.Destroy(o.go); fires.RemoveAt(0); }
            GameObject go = null;
            ResolveDE();
            var model = deFlameType != null ? GameDatabase.Instance.GetModel("DestructionEffects/Models/FlameEffect2/model") : null;
            if (model != null)
            {
                go = UnityEngine.Object.Instantiate(model);
                go.SetActive(true);
                go.transform.SetParent(root, true);
                go.transform.position = pos;
                go.transform.rotation = Quaternion.LookRotation(FlightGlobals.getUpAxis(pos));
                go.transform.localScale = Vector3.one * Mathf.Clamp(size / 4f, 0.5f, 6f);
                foreach (var pe in go.GetComponentsInChildren<KSPParticleEmitter>()) { pe.emit = true; EffectBehaviour.AddParticleEmitter(pe); }
            }
            fires.Add(new Fire { go = go, local = root.InverseTransformPoint(pos), t0 = Time.time, life = life, size = size });
        }

        /// <summary>Ages marks and fires, puffs smoke from fires, refreshes the heat list. Called by the director every frame.</summary>
        public static void Tick()
        {
            float now = Time.time;
            for (int i = scorches.Count - 1; i >= 0; i--)
            {
                var s = scorches[i];
                if (s.t == null) { scorches.RemoveAt(i); continue; }
                if (now - s.t0 > s.life) { s.t.gameObject.SetActive(false); UnityEngine.Object.Destroy(s.t.gameObject); scorches.RemoveAt(i); }
            }
            var root = Root;
            for (int i = fires.Count - 1; i >= 0; i--)
            {
                var f = fires[i];
                if (now - f.t0 > f.life)
                {
                    if (f.go != null) { foreach (var pe in f.go.GetComponentsInChildren<KSPParticleEmitter>()) { pe.emit = false; EffectBehaviour.RemoveParticleEmitter(pe); } UnityEngine.Object.Destroy(f.go, 3f); }
                    fires.RemoveAt(i); continue;
                }
                if (now >= f.nextSmoke && root != null)
                {
                    f.nextSmoke = now + 0.7f;
                    ExplosionDirector.Instance?.FireSmoke(f.local, f.size, f.go == null);
                }
            }
            // Heat sources: our fires, fresh scorch marks, Destruction Effects' burning parts (scanned once a second).
            heat.Clear();
            if (root != null)
            {
                foreach (var f in fires) heat.Add((root.TransformPoint(f.local), 1f, f.size));
                foreach (var s in scorches) { float age = now - s.t0; if (age < 60f) heat.Add((s.t.position, 0.6f * (1 - age / 60f), s.t.localScale.x * 0.3f)); }
            }
            if (deFlameType != null)
            {
                if (now >= nextDeScan)
                {
                    nextDeScan = now + 1f;
                    deFlames.Clear();
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(deFlameType)) if (o is Component c && c != null) deFlames.Add(c);
                }
                foreach (var c in deFlames) if (c != null && c.gameObject.activeInHierarchy) heat.Add((c.transform.position, 0.9f, 2f));
            }
        }

        /// <summary>Heat sources for thermal views: position, intensity 0..1, size in metres.</summary>
        public static IReadOnlyList<(Vector3 pos, float intensity, float size)> Heat => heat;

        public static void Clear() { scorches.Clear(); fires.Clear(); heat.Clear(); deFlames.Clear(); }
    }

    /// <summary>Water impacts (KSP's splash): a water column and spray ring, sized by the splash.</summary>
    [HarmonyPatch(typeof(FXMonger), nameof(FXMonger.Splash))]
    static class Patch_FXMonger_Splash
    {
        static void Postfix(Vector3 __0, float __1)
        {
            try { ExplosionDirector.Splash(__0, __1); } catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: splash FX: {e.Message}"); }
        }
    }
}
