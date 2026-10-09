using System.Collections.Generic;
using UnityEngine;

using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// ELE engine smoke for every engine on every nearby craft, no part configs needed (works next to Waterfall plumes,
    /// which draw the flame): solid boosters leave thick billowing columns, liquid rockets light exhaust near the ground,
    /// jets a dark haze at full power low down, and white contrails high up in cold air. Smoke stays in the air where it
    /// was made (it doesn't follow the craft), drifts, spreads and fades. One shared particle system, capped.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class EngineSmoke : MonoBehaviour
    {
        enum Kind { None, Solid, Liquid, Jet, Mono }
        ParticleSystem ps;
        readonly Dictionary<ModuleEngines, (Kind kind, float size)> cache = new();
        float nextScan;
        readonly List<ModuleEngines> engines = new();
        const float RangeM = 6000f;

        void Start()
        {
            ps = MakeSystem();
        }

        void OnDestroy() { if (ps != null) Destroy(ps.gameObject); }

        ParticleSystem MakeSystem()
        {
            var go = new GameObject("ELE engine smoke");
            go.transform.SetParent(transform, false);
            var p = go.AddComponent<ParticleSystem>();
            p.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = p.main;
            main.loop = true; main.playOnAwake = false; main.simulationSpace = ParticleSystemSimulationSpace.Local; // root follows the floating origin
            main.maxParticles = 2500; main.startSpeed = 0; main.gravityModifier = 0;
            var em = p.emission; em.enabled = false;
            var shape = p.shape; shape.enabled = false;
            var col = p.colorOverLifetime; col.enabled = true;
            col.color = ExplosionDirector.Gradient(new[] { (Color.white, 0f), (Color.white, 1f) }, new[] { (0f, 0f), (1f, 0.05f), (0.6f, 0.4f), (0f, 1f) });
            var size = p.sizeOverLifetime; size.enabled = true; size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 0.35f), new Keyframe(0.3f, 1f), new Keyframe(1, 2.2f)));
            var rot = p.rotationOverLifetime; rot.enabled = true; rot.z = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);
            var noise = p.noise; noise.enabled = true; noise.strength = 1.5f; noise.frequency = 0.08f; noise.scrollSpeed = 0.2f; noise.damping = true; noise.quality = ParticleSystemNoiseQuality.Low;
            var drag = p.limitVelocityOverLifetime; drag.enabled = true; drag.limit = 4f; drag.dampen = 0.12f;   // exhaust slows to the air's speed quickly
            var r = p.GetComponent<ParticleSystemRenderer>();
            r.material = new Material(Shader.Find("KSP/Particles/Alpha Blended")) { mainTexture = ExplosionDirector.PuffTexture() };
            r.renderMode = ParticleSystemRenderMode.Billboard; r.maxParticleSize = 8f;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            p.Play();
            return p;
        }

        static (Kind, float) Classify(ModuleEngines e)
        {
            bool air = false, solid = false, lf = false, mono = false;
            foreach (var pr in e.propellants)
            {
                if (pr.name == "IntakeAir" || pr.name == "IntakeAtm") air = true;
                else if (pr.name == "SolidFuel") solid = true;
                else if (pr.name == "LiquidFuel" || pr.name == "Oxidizer") lf = true;
                else if (pr.name == "MonoPropellant") mono = true;
            }
            var k = solid ? Kind.Solid : air ? Kind.Jet : lf ? Kind.Liquid : mono ? Kind.Mono : Kind.None;
            // nozzle size from thrust: roughly 0.6 m for 50 kN, 2.5 m for 1000 kN
            float size = Mathf.Clamp(Mathf.Sqrt(Mathf.Max(1f, e.maxThrust)) * 0.08f, 0.25f, 4f);
            return (k, size);
        }

        void LateUpdate()
        {
            Vector3d off = BDKrakensbane.FloatingOriginOffset;
            if (off != Vector3d.zero) transform.position -= (Vector3)off;
            if (ps == null || !AvionicsSettings.EngineSmoke || KillCam.Playing || FlightGlobals.ActiveVessel == null) return;

            if (Time.time >= nextScan)
            {
                nextScan = Time.time + 1f;
                engines.Clear();
                var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera.transform.position : FlightGlobals.ActiveVessel.transform.position;
                foreach (var v in FlightGlobals.VesselsLoaded)
                {
                    if (v == null || v.packed || (v.transform.position - cam).sqrMagnitude > RangeM * RangeM) continue;
                    foreach (var e in VesselModuleRegistry.GetModules<ModuleEngines>(v)) if (e != null) engines.Add(e);
                }
            }
            float dt = Time.deltaTime; if (dt <= 0) return;
            var ep = new ParticleSystem.EmitParams();
            foreach (var e in engines)
            {
                if (e == null || e.vessel == null || !e.EngineIgnited || e.flameout || e.finalThrust <= 0.01f) continue;
                if (!cache.TryGetValue(e, out var info)) cache[e] = info = Classify(e);
                if (info.kind == Kind.None) continue;
                var v = e.vessel;
                float rho = (float)v.atmDensity;
                if (rho < 0.003f) continue;
                float thr = e.finalThrust / Mathf.Max(1f, e.maxThrust);
                float alt = (float)v.altitude;
                // what this engine leaves behind here
                float rate, life, size, alpha; Color tint;
                switch (info.kind)
                {
                    case Kind.Solid:
                        rate = 30f * thr; life = 14f; size = info.size * 1.6f; alpha = 0.85f; tint = new Color(0.86f, 0.85f, 0.83f); break;
                    case Kind.Liquid:
                        if (rho < 0.25f) continue;
                        rate = 14f * thr * Mathf.Clamp01(rho); life = 5f; size = info.size * 1.2f; alpha = 0.45f; tint = new Color(0.92f, 0.92f, 0.92f); break;
                    case Kind.Mono:
                        rate = 6f * thr; life = 1.5f; size = info.size * 0.6f; alpha = 0.3f; tint = new Color(0.95f, 0.95f, 0.95f); break;
                    default: // jet
                        bool contrail = alt > 7500f && alt < 14000f;
                        if (contrail) { rate = 22f * Mathf.Clamp01(thr * 2f); life = 22f; size = info.size * 0.7f; alpha = 0.5f; tint = Color.white; }
                        else if (thr > 0.75f && rho > 0.6f) { rate = 8f * (thr - 0.75f) * 4f; life = 3f; size = info.size * 0.9f; alpha = 0.22f; tint = new Color(0.25f, 0.24f, 0.23f); }
                        else continue;
                        break;
                }
                rate *= AvionicsSettings.EngineSmokeAmount;
                foreach (var t in e.thrustTransforms)
                {
                    if (t == null) continue;
                    float n = rate * dt + Random.value;   // fractional rates still emit, on average right
                    int count = (int)n; if (count <= 0) continue;
                    Vector3 exhaust = t.forward;            // KSP pushes the craft along -forward: exhaust leaves along +forward
                    for (int i = 0; i < count; i++)
                    {
                        // spread the puffs along the frame's travel so fast craft leave an even trail, not beads
                        float back = Random.value;
                        Vector3 pos = t.position + exhaust * size * 0.8f - (Vector3)v.srf_velocity * dt * back + Random.insideUnitSphere * size * 0.25f;
                        ep.position = transform.InverseTransformPoint(pos);
                        ep.velocity = exhaust * Random.Range(4f, 10f) + Random.insideUnitSphere * 1.5f;   // in still air; the drag module stops it
                        ep.startSize = size * Random.Range(0.8f, 1.25f);
                        ep.startLifetime = life * Random.Range(0.75f, 1.2f);
                        ep.rotation = Random.Range(0f, 360f);
                        ep.startColor = new Color(tint.r, tint.g, tint.b, alpha);
                        ps.Emit(ep, 1);
                    }
                }
            }
        }
    }
}
