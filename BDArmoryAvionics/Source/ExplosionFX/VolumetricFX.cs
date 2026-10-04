using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// True volumetric explosions, heat haze and refracting shockwaves, from ELE's shader bundle
    /// (GameData/BDArmoryAvionics/Shaders/eleshaders.ksp, built from Documents/KSP-Mods/ELEShaders with Unity 2019.4.18f1).
    /// Without the bundle nothing here runs and the particle effects carry on as before.
    /// Budget: only a few volumes at once (by quality), fewer ray steps with distance, none beyond 8 km.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class VolumetricShaders : MonoBehaviour
    {
        public static Shader Volume, Distort;
        public static bool Loaded => Volume != null;

        void Awake()
        {
            DontDestroyOnLoad(this);
            string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmoryAvionics/Shaders/eleshaders.ksp");
            if (!File.Exists(path)) { Debug.Log("[BDArmoryAvionics]: volumetric shaders not installed (particle effects only)"); return; }
            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) { Debug.LogWarning("[BDArmoryAvionics]: couldn't load the volumetric shader bundle"); return; }
            foreach (var s in bundle.LoadAllAssets<Shader>())
            {
                if (s.name == "ELE/Volumetric") Volume = s;
                if (s.name == "ELE/Distort") Distort = s;
            }
            bundle.Unload(false);
            Debug.Log($"[BDArmoryAvionics]: volumetric shaders {(Volume != null && Volume.isSupported ? "loaded" : "not supported on this GPU")}, distortion {(Distort != null && Distort.isSupported ? "loaded" : "unavailable")}");
            if (Volume != null && !Volume.isSupported) Volume = null;
            if (Distort != null && !Distort.isSupported) Distort = null;
        }
    }

    public static class VolumetricFX
    {
        class Vol
        {
            public Transform t; public Material m; public Vector3 rise;
            public float t0, life, r0, r1, heat0; public bool active; public int kind; // 0 volume, 1 shock, 2 haze
        }
        static readonly List<Vol> pool = new();

        public static bool Enabled => VolumetricShaders.Loaded && AvionicsSettings.Volumetric && AvionicsSettings.ExplosionQuality > 0;
        static int MaxVolumes => AvionicsSettings.ExplosionQuality switch { 1 => 2, 2 => 4, 3 => 6, _ => 0 };
        static float BaseSteps => AvionicsSettings.ExplosionQuality switch { 1 => 12, 2 => 18, 3 => 26, _ => 0 };

        static int Live(int kind) { int n = 0; foreach (var v in pool) if (v.active && v.kind == kind) n++; return n; }

        static Vol Get(int kind, Shader shader, Transform root)
        {
            foreach (var v in pool) if (!v.active && v.kind == kind) return v;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = "ELE volume";
            go.transform.SetParent(root, false);
            var mr = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            var m = new Material(shader);
            mr.sharedMaterial = m;
            var nv = new Vol { t = go.transform, m = m, kind = kind };
            pool.Add(nv);
            return nv;
        }

        static bool TooFar(Vector3 pos, float r)
        {
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            return cam == null || (cam.transform.position - pos).magnitude - r > 8000f;
        }

        /// <summary>A billowing fireball / smoke volume. Returns false when it wasn't made (budget, distance, no shaders).</summary>
        public static bool Explosion(Vector3 pos, Vector3 up, float R, float k, ExplosionKind kind, bool onGround)
        {
            var root = ExplosionDirector.Instance != null ? ExplosionDirector.Instance.transform : null;
            if (!Enabled || root == null || TooFar(pos, R * 2f) || Live(0) >= MaxVolumes) return false;
            var v = Get(0, VolumetricShaders.Volume, root);
            v.active = true; v.t.gameObject.SetActive(true);
            v.t.position = pos; v.t.rotation = Quaternion.FromToRotation(Vector3.up, up);
            v.r0 = Mathf.Max(2f, R * 0.35f * k); v.r1 = Mathf.Max(4f, R * 0.9f * k);
            v.t0 = Time.time;
            v.life = kind == ExplosionKind.FuelKill ? 22f : kind == ExplosionKind.Nuke ? 60f : kind == ExplosionKind.GroundHE ? 16f : 10f;
            v.rise = up * Mathf.Clamp(R * 0.08f, 1f, 12f);
            v.heat0 = kind == ExplosionKind.FuelKill || kind == ExplosionKind.Thermobaric || kind == ExplosionKind.Nuke ? 1.3f : 1f;
            var smoke = kind == ExplosionKind.FuelKill ? new Color(0.08f, 0.07f, 0.07f) : kind == ExplosionKind.GroundHE ? new Color(0.42f, 0.36f, 0.3f) : new Color(0.3f, 0.29f, 0.28f);
            v.m.SetColor("_SmokeColor", smoke);
            v.m.SetFloat("_Seed", Random.Range(0f, 100f));
            v.m.SetFloat("_Flatten", onGround ? 0.35f : 0f);
            v.m.SetFloat("_Density", kind == ExplosionKind.FuelKill ? 2.2f : 1.6f);
            return true;
        }

        /// <summary>A refracting shockwave shell racing outward.</summary>
        public static void Shock(Vector3 pos, float radius)
        {
            var root = ExplosionDirector.Instance != null ? ExplosionDirector.Instance.transform : null;
            if (!Enabled || VolumetricShaders.Distort == null || root == null || TooFar(pos, radius) || Live(1) >= 2) return;
            var v = Get(1, VolumetricShaders.Distort, root);
            v.active = true; v.t.gameObject.SetActive(true);
            v.t.position = pos; v.r0 = 0.5f; v.r1 = radius; v.t0 = Time.time; v.life = 0.45f;
            v.m.SetFloat("_Mode", 1); v.m.SetFloat("_Strength", 0.035f); v.m.SetFloat("_RingWidth", 0.2f);
        }

        /// <summary>Heat haze over a fire (follows it until it burns out).</summary>
        public static void Haze(Vector3 pos, float size, float life)
        {
            var root = ExplosionDirector.Instance != null ? ExplosionDirector.Instance.transform : null;
            if (!Enabled || VolumetricShaders.Distort == null || root == null || TooFar(pos, size) || Live(2) >= 3) return;
            var v = Get(2, VolumetricShaders.Distort, root);
            v.active = true; v.t.gameObject.SetActive(true);
            v.t.position = pos + FlightGlobals.getUpAxis(pos) * size * 1.2f;
            v.r0 = v.r1 = size * 1.6f; v.t0 = Time.time; v.life = life;
            v.m.SetFloat("_Mode", 0); v.m.SetFloat("_Strength", 0.012f); v.m.SetFloat("_Speed", 1.5f);
        }

        /// <summary>Animate live volumes. Called by the director every frame.</summary>
        public static void Tick()
        {
            if (pool.Count == 0) return;
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            Vector3 sun = Planetarium.fetch != null && Planetarium.fetch.Sun != null ? (Vector3)Planetarium.fetch.Sun.position : Vector3.up * 1e9f;
            float now = Time.time;
            foreach (var v in pool)
            {
                if (!v.active || v.t == null) continue;
                float a = (now - v.t0) / v.life;
                if (a >= 1f) { v.active = false; v.t.gameObject.SetActive(false); continue; }
                switch (v.kind)
                {
                    case 0:
                        {
                            float grow = 1f - Mathf.Pow(1f - Mathf.Min(1f, a * 6f), 3f); // fast expansion, then slow billowing growth
                            float r = Mathf.Lerp(v.r0, v.r1, grow) * (1f + a * 0.5f);
                            v.t.localScale = Vector3.one * r * 2f;
                            v.t.position += v.rise * Time.deltaTime * (1f + a);
                            v.m.SetFloat("_Heat", v.heat0 * Mathf.Clamp01(1f - a * 5f));
                            v.m.SetFloat("_Fade", Mathf.Clamp01(Mathf.Min(a * 30f, (1f - a) * 2.5f)));
                            v.m.SetVector("_SunDir", v.t.InverseTransformDirection((sun - v.t.position).normalized));
                            float dist = cam != null ? (cam.transform.position - v.t.position).magnitude : 1000f;
                            v.m.SetFloat("_Steps", Mathf.Max(6f, BaseSteps * Mathf.Clamp(1.5f - dist / 4000f, 0.35f, 1f))); // fewer steps far away
                            break;
                        }
                    case 1:
                        {
                            float e = 1f - (1f - a) * (1f - a);
                            v.t.localScale = Vector3.one * Mathf.Lerp(v.r0, v.r1, e) * 2f;
                            v.m.SetFloat("_Fade", 1f - a);
                            break;
                        }
                    case 2:
                        v.t.localScale = Vector3.one * v.r0 * 2f;
                        v.m.SetFloat("_Fade", Mathf.Clamp01(Mathf.Min(a * 10f, (1f - a) * 5f)));
                        break;
                }
            }
        }

        public static void Clear() { foreach (var v in pool) if (v.t != null) Object.Destroy(v.t.gameObject); pool.Clear(); }
    }
}
