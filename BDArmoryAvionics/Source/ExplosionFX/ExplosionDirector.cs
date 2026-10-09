using System.Collections.Generic;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    public enum ExplosionKind { Small, AirBurst, GroundHE, Water, FuelKill, Thermobaric, ShapedCharge, Nuke }

    /// <summary>
    /// Extra explosion layers on top of BDArmory's fireball, chosen by the kind of explosion:
    /// flash light, rolling fire and smoke (fake volumetric puffs), ground dust ring, water column, shockwave shell,
    /// mushroom for very large blasts, and camera shake.
    /// Built to be cheap: three shared particle systems (fire, smoke, dust) instead of objects per puff, hard caps on
    /// particles, lights and shockwave shells, and a quality setting (Low by default).
    /// </summary>
    [DefaultExecutionOrder(10000)] // after the flight camera, so the shake isn't overwritten
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ExplosionDirector : MonoBehaviour
    {
        public static ExplosionDirector Instance;
        public static readonly string[] QualityNames = { "Off", "Low", "Medium", "High" };

        ParticleSystem fire, smoke, dust;
        readonly List<Light> lights = new();
        readonly List<(Light l, float t0, float life, float i0)> liveLights = new();
        readonly List<(Transform t, Material m, float t0, float life, float r)> shells = new();
        readonly Stack<(Transform t, Material m)> shellPool = new();
        Material shellMat;
        float shake; Vector3 shakeApplied;

        static float Q => AvionicsSettings.ExplosionQuality switch { 1 => 0.35f, 2 => 0.7f, 3 => 1f, _ => 0f };
        static int MaxLights => AvionicsSettings.ExplosionQuality switch { 1 => 3, 2 => 6, 3 => 8, _ => 0 };
        static int MaxShells => AvionicsSettings.ExplosionQuality switch { 1 => 2, 2 => 4, 3 => 6, _ => 0 };

        void Awake() => Instance = this;

        void Start()
        {
            fire = MakeSystem("ELE fire", "KSP/Particles/Additive", AvionicsDraw.GlowTexture, 250,
                Gradient(new[] { (new Color(1f, 0.95f, 0.8f), 0f), (new Color(1f, 0.55f, 0.15f), 0.3f), (new Color(0.5f, 0.15f, 0.05f), 1f) }, new[] { (1f, 0f), (0.8f, 0.3f), (0f, 1f) }),
                new AnimationCurve(new Keyframe(0, 0.4f), new Keyframe(0.4f, 1f), new Keyframe(1, 1.2f)));
            smoke = MakeSystem("ELE smoke", "KSP/Particles/Alpha Blended", PuffTexture(), 450,
                Gradient(new[] { (new Color(0.25f, 0.22f, 0.2f), 0f), (new Color(0.4f, 0.4f, 0.4f), 1f) }, new[] { (0f, 0f), (0.75f, 0.08f), (0.5f, 0.5f), (0f, 1f) }),
                new AnimationCurve(new Keyframe(0, 0.5f), new Keyframe(1, 2.2f)));
            dust = MakeSystem("ELE dust", "KSP/Particles/Alpha Blended", PuffTexture(), 300,
                Gradient(new[] { (new Color(0.55f, 0.47f, 0.36f), 0f), (new Color(0.6f, 0.55f, 0.48f), 1f) }, new[] { (0f, 0f), (0.7f, 0.1f), (0f, 1f) }),
                new AnimationCurve(new Keyframe(0, 0.6f), new Keyframe(1, 2.5f)));
            shellMat = new Material(Shader.Find("KSP/Particles/Alpha Blended")) { mainTexture = AvionicsDraw.GlowTexture };
        }

        void OnDestroy() { if (Instance == this) Instance = null; Persistent.Clear(); VolumetricFX.Clear(); }

        /// <summary>Smoke (and fire, when there's no Destruction Effects flame) rising from a lingering fire.</summary>
        public void FireSmoke(Vector3 local, float size, bool ownFlames)
        {
            if (smoke == null) return;
            Vector3 up = FlightGlobals.getUpAxis(transform.TransformPoint(local));
            Vector3 drift = Vector3.ProjectOnPlane(new Vector3(0.6f, 0, 0.8f), up).normalized * 3f;
            Smoke(local + up * size * 0.5f, up, drift, size * 0.4f, 1f, 1, 18f, 4f, new Color(0.15f, 0.13f, 0.12f));
            if (ownFlames) Fire(local, up, size * 0.3f, 0.6f, 2, 1.2f);
        }

        static float nextLog;
        static void LogFx(ExplosionKind kind, float tnt, float k)
        {
            if (Time.time < nextLog) return;
            nextLog = Time.time + 0.5f; // a salvo shouldn't flood the log
            Debug.Log($"[BDArmoryAvionics]: explosion FX {kind}, {tnt:F1} kg, x{k:F2}");
        }

        /// <summary>Water impact from KSP's splash: a column and spray ring (bullets only at High quality).</summary>
        public static void Splash(Vector3 pos, float size)
        {
            var d = Instance;
            if (d == null || d.smoke == null || AvionicsSettings.ExplosionQuality == 0) return;
            if (size < 8f && AvionicsSettings.ExplosionQuality < 3) return;
            float R = Mathf.Clamp(Mathf.Sqrt(size) * 1.2f, 2f, 120f), q = Q, k = Mathf.Clamp(R / 10f, 0.4f, 6f);
            Vector3 up = FlightGlobals.getUpAxis(pos);
            Vector3 local = d.transform.InverseTransformPoint(pos);
            d.Column(local, up, R, k, Mathf.Max(2, Mathf.RoundToInt(16 * q * k)), d.smoke, 3.5f, new Color(0.9f, 0.95f, 1f));
            d.Ring(local, up, R * 0.8f, k, Mathf.Max(4, Mathf.RoundToInt(18 * q * Mathf.Sqrt(k))), new Color(0.95f, 0.97f, 1f));
            if (size > 200f) d.Shake(pos, R, k * 0.5f);
            LogFx(ExplosionKind.Water, size, k);
        }

        // ---- Setup helpers ----

        ParticleSystem MakeSystem(string name, string shader, Texture tex, int max, Gradient col, AnimationCurve size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true; main.playOnAwake = false; main.simulationSpace = ParticleSystemSimulationSpace.Local; // the root moves with the floating origin
            main.maxParticles = max; main.startSpeed = 0; main.gravityModifier = 0;
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.enabled = false;
            var c = ps.colorOverLifetime; c.enabled = true; c.color = col;
            var s = ps.sizeOverLifetime; s.enabled = true; s.size = new ParticleSystem.MinMaxCurve(1f, size);
            // billow: slow random spin and gentle turbulence so puffs roll instead of sliding as flat cards
            var rot = ps.rotationOverLifetime; rot.enabled = true; rot.z = new ParticleSystem.MinMaxCurve(-0.6f, 0.6f);
            var noise = ps.noise; noise.enabled = true; noise.strength = 1.2f; noise.frequency = 0.15f; noise.scrollSpeed = 0.3f; noise.damping = true; noise.quality = ParticleSystemNoiseQuality.Low;
            var drag = ps.limitVelocityOverLifetime; drag.enabled = true; drag.limit = 30f; drag.dampen = 0.05f;
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.material = new Material(Shader.Find(shader)) { mainTexture = tex };
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            r.maxParticleSize = 5f;
            ps.Play();
            return ps;
        }

        static Gradient Gradient((Color c, float t)[] cols, (float a, float t)[] alphas)
        {
            var g = new Gradient();
            g.SetKeys(System.Array.ConvertAll(cols, x => new GradientColorKey(x.c, x.t)), System.Array.ConvertAll(alphas, x => new GradientAlphaKey(x.a, x.t)));
            return g;
        }

        /// <summary>Soft, lumpy smoke puff (generated once, 64x64).</summary>
        static Texture2D PuffTexture()
        {
            var t = new Texture2D(64, 64, TextureFormat.ARGB32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    float dx = (x - 31.5f) / 32f, dy = (y - 31.5f) / 32f, d = Mathf.Sqrt(dx * dx + dy * dy);
                    float n = Mathf.PerlinNoise(x * 0.12f, y * 0.12f) * 0.6f + Mathf.PerlinNoise(x * 0.3f + 7, y * 0.3f + 3) * 0.4f;
                    float a = Mathf.Clamp01((1f - d) * 1.6f) * Mathf.Lerp(0.55f, 1f, n);
                    t.SetPixel(x, y, new Color(1, 1, 1, a * a));
                }
            t.Apply();
            return t;
        }

        // ---- Per frame: floating origin, lights, shells, camera shake ----

        void LateUpdate()
        {
            Vector3d off = BDKrakensbane.FloatingOriginOffset;
            if (off != Vector3d.zero) transform.position -= (Vector3)off;
            Persistent.Tick();
            VolumetricFX.Tick();
            float now = Time.time;
            for (int i = liveLights.Count - 1; i >= 0; i--)
            {
                var (l, t0, life, i0) = liveLights[i];
                float k = (now - t0) / life;
                if (k >= 1 || l == null) { if (l != null) l.enabled = false; liveLights.RemoveAt(i); continue; }
                l.intensity = i0 * (1 - k) * (1 - k);
            }
            for (int i = shells.Count - 1; i >= 0; i--)
            {
                var (t, m, t0, life, r) = shells[i];
                float k = (now - t0) / life;
                if (k >= 1) { t.gameObject.SetActive(false); shellPool.Push((t, m)); shells.RemoveAt(i); continue; }
                float e = 1 - (1 - k) * (1 - k); // fast then slowing
                t.localScale = Vector3.one * Mathf.Max(0.1f, r * 2f * e);
                var sc = new Color(0.5f, 0.5f, 0.5f, 0.18f * (1 - k)); // _TintColor is doubled by KSP's particle shaders
                m.color = sc; if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", sc);
            }
            // Camera shake: remove last frame's offset, add a new decaying one.
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.transform : null;
            if (cam != null)
            {
                cam.position -= shakeApplied; shakeApplied = Vector3.zero;
                if (shake > 0.001f)
                {
                    shakeApplied = Random.insideUnitSphere * shake;
                    cam.position += shakeApplied;
                    shake *= Mathf.Exp(-Time.deltaTime * 6f);
                }
            }
        }

        // ---- Classification ----

        public static ExplosionKind Classify(Vector3 pos, float tnt, Part part, string weapon, bool shaped, out float groundDist, out Vector3 up)
        {
            up = FlightGlobals.getUpAxis(pos);
            groundDist = float.MaxValue;
            float R = Shrapnel.BlastRadius(part, tnt);
            if (tnt < 0.5f) return ExplosionKind.Small;
            if (tnt > 5000f) return ExplosionKind.Nuke;
            if (shaped) return ExplosionKind.ShapedCharge;
            var body = FlightGlobals.currentMainBody;
            double alt = FlightGlobals.getAltitudeAtPos(pos, body);
            if (body.ocean && alt < 3 && body.TerrainAltitude(body.GetLatitude(pos), body.GetLongitude(pos)) < -2) return ExplosionKind.Water;
            if (Physics.Raycast(pos + up * 0.5f, -up, out var hit, Mathf.Max(5f, R * 0.6f), (int)LayerMasks.Scenery)) groundDist = hit.distance;
            if (!string.IsNullOrEmpty(weapon) && (weapon.ToLower().Contains("thermo") || weapon.ToLower().Contains("fae"))) return ExplosionKind.Thermobaric;
            if (part != null && string.IsNullOrEmpty(weapon) && part.Resources.Contains("LiquidFuel") && part.Resources["LiquidFuel"].amount > 20) return ExplosionKind.FuelKill;
            return groundDist < float.MaxValue ? ExplosionKind.GroundHE : ExplosionKind.AirBurst;
        }

        // ---- Spawning ----

        public static ExplosionKind? ForceKind; // test panel
        public static void Spawn(Vector3 pos, float tnt, Part part, string weapon, bool shaped)
        {
            var d = Instance;
            if (d == null || d.fire == null || AvionicsSettings.ExplosionQuality == 0) return;
            var kind = Classify(pos, tnt, part, weapon, shaped, out float groundDist, out Vector3 up);
            if (ForceKind.HasValue) kind = ForceKind.Value;
            if (kind == ExplosionKind.Small && AvionicsSettings.ExplosionQuality < 3) return; // cannon shells: BDArmory's own effect is enough
            float k = ExplosionScaling.Factor(tnt), R = Shrapnel.BlastRadius(part, tnt), q = Q;
            Vector3 local = d.transform.InverseTransformPoint(pos);
            Vector3 drift = Vector3.ProjectOnPlane(new Vector3(0.6f, 0, 0.8f), up).normalized * 3f; // a light breeze
            LogFx(kind, tnt, k);
            // True volumetrics (when the shader bundle is installed) replace most of the puffs for big blasts.
            if (kind != ExplosionKind.Small && kind != ExplosionKind.ShapedCharge && kind != ExplosionKind.Water && tnt >= 5f
                && VolumetricFX.Explosion(pos, up, R, k, kind, groundDist < R * 0.6f)) q *= 0.4f;
            if (k > 1.6f || kind == ExplosionKind.Thermobaric || kind == ExplosionKind.Nuke) VolumetricFX.Shock(pos, R * (kind == ExplosionKind.Nuke ? 6f : 2.5f));
            // Marks that stay: scorch / crater where it touched the ground, and fires from big ground hits and fuel kills.
            if (groundDist < R * 0.6f && kind != ExplosionKind.Water && kind != ExplosionKind.Small
                && Physics.Raycast(pos + up * 1f, -up, out var gh, R + 2f, (int)LayerMasks.Scenery))
            {
                Persistent.AddScorch(gh.point, gh.normal, Mathf.Clamp(R * 0.35f, 1.5f, 80f), kind == ExplosionKind.Nuke ? 1800f : 300f);
                if (kind == ExplosionKind.FuelKill || tnt >= 100f || kind == ExplosionKind.Thermobaric)
                    Persistent.AddFire(gh.point, Mathf.Clamp(R * 0.25f, 2f, 30f), kind == ExplosionKind.FuelKill ? 90f : Mathf.Clamp(tnt / 5f, 30f, 120f));
            }
            switch (kind)
            {
                case ExplosionKind.AirBurst:
                    d.Light(pos, R, 0.25f, 6f);
                    d.Fire(local, up, R * 0.5f, k, Mathf.RoundToInt(10 * q * k), 0.9f);
                    d.Smoke(local, up, drift, R * 0.5f, k, Mathf.RoundToInt(8 * q * k), 8f, 0.6f);
                    if (k > 1.6f) d.Shell(pos, R * 1.6f, 0.35f);
                    d.Shake(pos, R, k);
                    break;
                case ExplosionKind.GroundHE:
                    d.Light(pos, R, 0.3f, 6f);
                    d.Fire(local, up, R * 0.4f, k, Mathf.RoundToInt(10 * q * k), 1.1f);
                    d.Smoke(local, up, drift, R * 0.4f, k, Mathf.RoundToInt(12 * q * k), 18f, 2.5f);
                    d.Ring(local - up * Mathf.Min(groundDist, R) * 0.9f, up, R, k, Mathf.RoundToInt(24 * q * Mathf.Sqrt(k)));
                    d.Column(local, up, R, k, Mathf.RoundToInt(8 * q * k), d.dust, 1.8f);
                    d.Shake(pos, R, k * 1.2f);
                    break;
                case ExplosionKind.Water:
                    d.Light(pos, R, 0.15f, 3f);
                    d.Column(local, up, R, k, Mathf.RoundToInt(16 * q * k), d.smoke, 3.5f, new Color(0.9f, 0.95f, 1f));
                    d.Ring(local, up, R * 0.8f, k, Mathf.RoundToInt(18 * q * Mathf.Sqrt(k)), new Color(0.95f, 0.97f, 1f));
                    d.Shake(pos, R, k * 0.6f);
                    break;
                case ExplosionKind.FuelKill:
                    d.Light(pos, R * 1.5f, 0.6f, 8f);
                    d.Fire(local, up, R * 0.8f, k * 1.4f, Mathf.RoundToInt(18 * q * k), 1.8f);
                    d.Smoke(local, up, drift, R * 0.6f, k * 1.3f, Mathf.RoundToInt(16 * q * k), 25f, 4f, new Color(0.08f, 0.07f, 0.07f));
                    d.Shake(pos, R, k);
                    break;
                case ExplosionKind.Thermobaric:
                    d.Ring(local, up, R * 0.6f, k, Mathf.RoundToInt(18 * q * k)); // the dispersal cloud first
                    d.Light(pos, R * 3f, 0.8f, 10f);
                    d.Fire(local, up, R * 1.5f, k * 2f, Mathf.RoundToInt(26 * q * k), 2.2f);
                    d.Shell(pos, R * 3f, 0.5f);
                    d.Smoke(local, up, drift, R, k * 1.5f, Mathf.RoundToInt(14 * q * k), 20f, 2f);
                    d.Shake(pos, R * 2f, k * 2f);
                    break;
                case ExplosionKind.ShapedCharge:
                    d.Light(pos, R * 0.6f, 0.12f, 5f);
                    d.Fire(local, up, R * 0.25f, k * 0.6f, Mathf.RoundToInt(5 * q * k), 0.5f);
                    d.Smoke(local, up, drift, R * 0.2f, k * 0.6f, Mathf.RoundToInt(4 * q * k), 6f, 0.5f);
                    break;
                case ExplosionKind.Nuke:
                    d.Light(pos, R * 6f, 2f, 16f);
                    d.Shell(pos, R * 6f, 1.2f);
                    d.Fire(local, up, R * 0.6f, k, Mathf.RoundToInt(30 * q), 4f);
                    d.Mushroom(local, up, R, k, q);
                    d.Ring(local - up * Mathf.Min(groundDist, R), up, R * 3f, k, Mathf.RoundToInt(40 * q));
                    d.Shake(pos, R * 6f, 6f);
                    break;
                default:
                    d.Light(pos, R, 0.12f, 3f);
                    break;
            }
        }

        // ---- Layers ----

        void Light(Vector3 pos, float range, float life, float intensity)
        {
            if (liveLights.Count >= MaxLights) { var o = liveLights[0]; if (o.l != null) o.l.enabled = false; liveLights.RemoveAt(0); }
            Light l = lights.Find(x => x != null && !x.enabled);
            if (l == null)
            {
                if (lights.Count >= 8) return;
                var go = new GameObject("ELE flash"); go.transform.SetParent(transform, false);
                l = go.AddComponent<Light>(); l.type = LightType.Point; l.shadows = LightShadows.None; l.color = new Color(1f, 0.78f, 0.5f);
                lights.Add(l);
            }
            l.transform.position = pos; l.range = Mathf.Clamp(range * 4f, 20f, 4000f); l.intensity = intensity; l.enabled = true;
            liveLights.Add((l, Time.time, life, intensity));
        }

        void Fire(Vector3 local, Vector3 up, float radius, float k, int n, float life)
        {
            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < n; i++)
            {
                Vector3 r = Random.insideUnitSphere;
                ep.position = local + r * radius;
                ep.velocity = (r * 6f + up * 3f) * Mathf.Sqrt(k);
                ep.startSize = radius * Random.Range(0.7f, 1.3f) + 2f * k;
                ep.startLifetime = life * Random.Range(0.7f, 1.2f) * Mathf.Sqrt(k);
                ep.rotation = Random.Range(0f, 360f);
                ep.startColor = Color.white;
                fire.Emit(ep, 1);
            }
        }

        void Smoke(Vector3 local, Vector3 up, Vector3 drift, float radius, float k, int n, float life, float rise, Color? tint = null)
        {
            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < n; i++)
            {
                Vector3 r = Random.insideUnitSphere;
                ep.position = local + r * radius;
                ep.velocity = drift + up * rise * Random.Range(0.6f, 1.4f) + r * 2f;
                ep.startSize = radius * 1.2f * Random.Range(0.8f, 1.3f) + 3f * k;
                ep.startLifetime = life * Random.Range(0.7f, 1.2f);
                ep.rotation = Random.Range(0f, 360f);
                ep.startColor = tint ?? Color.white;
                smoke.Emit(ep, 1);
            }
        }

        internal void Ring(Vector3 localGround, Vector3 up, float R, float k, int n, Color? tint = null)
        {
            var ep = new ParticleSystem.EmitParams();
            Vector3 a = Vector3.Cross(up, Vector3.forward); if (a.sqrMagnitude < 0.01f) a = Vector3.Cross(up, Vector3.right);
            a.Normalize(); Vector3 b = Vector3.Cross(up, a);
            for (int i = 0; i < n; i++)
            {
                float th = i * Mathf.PI * 2f / Mathf.Max(1, n) + Random.Range(-0.1f, 0.1f);
                Vector3 dir = a * Mathf.Cos(th) + b * Mathf.Sin(th);
                ep.position = localGround + dir * R * 0.3f + up * 1f;
                ep.velocity = dir * R * 1.6f + up * 2f;
                ep.startSize = R * 0.35f + 2f;
                ep.startLifetime = Random.Range(1.5f, 3f) * Mathf.Sqrt(k);
                ep.rotation = Random.Range(0f, 360f);
                ep.startColor = tint ?? Color.white;
                dust.Emit(ep, 1);
            }
        }

        internal void Column(Vector3 local, Vector3 up, float R, float k, int n, ParticleSystem ps, float life, Color? tint = null)
        {
            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < n; i++)
            {
                ep.position = local + Random.insideUnitSphere * R * 0.15f;
                ep.velocity = up * Random.Range(R * 0.8f, R * 2f) + Random.insideUnitSphere * R * 0.2f;
                ep.startSize = R * 0.25f + 1.5f;
                ep.startLifetime = life * Random.Range(0.7f, 1.2f) * Mathf.Sqrt(k);
                ep.rotation = Random.Range(0f, 360f);
                ep.startColor = tint ?? Color.white;
                ps.Emit(ep, 1);
            }
        }

        void Mushroom(Vector3 local, Vector3 up, float R, float k, float q)
        {
            // stem then cap: slow-rising puffs whose speed grows with height so the cap spreads
            var ep = new ParticleSystem.EmitParams();
            int n = Mathf.RoundToInt(40 * q);
            for (int i = 0; i < n; i++)
            {
                bool cap = i > n / 3;
                Vector3 r = Random.insideUnitSphere;
                ep.position = local + (cap ? up * R * 2f : Vector3.zero) + new Vector3(r.x, 0, r.z) * R * (cap ? 1.2f : 0.3f);
                ep.velocity = up * (cap ? R * 0.25f : R * 0.4f) + Vector3.ProjectOnPlane(r, up) * (cap ? R * 0.15f : 1f);
                ep.startSize = R * (cap ? 1.4f : 0.7f);
                ep.startLifetime = Random.Range(25f, 45f);
                ep.rotation = Random.Range(0f, 360f);
                ep.startColor = cap ? new Color(0.85f, 0.75f, 0.7f) : new Color(0.6f, 0.5f, 0.45f);
                smoke.Emit(ep, 1);
            }
        }

        void Shell(Vector3 pos, float radius, float life)
        {
            if (shells.Count >= MaxShells) return;
            (Transform t, Material m) s;
            if (shellPool.Count > 0) s = shellPool.Pop();
            else
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(transform, false);
                var m = new Material(shellMat);
                var mr = go.GetComponent<MeshRenderer>(); mr.sharedMaterial = m; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
                s = (go.transform, m);
            }
            s.t.gameObject.SetActive(true);
            s.t.position = pos; s.t.localScale = Vector3.one * 0.1f;
            shells.Add((s.t, s.m, Time.time, life, radius));
        }

        internal void Shake(Vector3 pos, float R, float k)
        {
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null || !AvionicsSettings.ExplosionShake) return;
            float dist = (cam.transform.position - pos).magnitude;
            if (dist > R * 25f) return;
            shake = Mathf.Min(1.5f, Mathf.Max(shake, 0.6f * k * R * R / Mathf.Max(R * R, dist * dist)));
        }
    }
}
