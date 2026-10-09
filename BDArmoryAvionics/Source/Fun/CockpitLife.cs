using System.Collections.Generic;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// ELE cockpit life, for the craft you fly:
    ///  * G effects: sustained positive G greys out then tunnels your vision and can knock the pilot out (G-LOC: stick goes
    ///    limp for a few seconds); negative G reds out. Tolerance from the crew: courage, badass and veteran help.
    ///  * Camera shake from G onset, hits, stall buffet and transonic buffet (flight and IVA cameras, never the craft).
    ///  * Warning callouts with tones: PULL UP, STALL, OVER-G, BINGO FUEL, GEAR. Alt+N silences the current one.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class CockpitLife : MonoBehaviour
    {
        // ---- G state ----
        public static float Gz;              // smoothed G along the pilot's spine (+ = pushed into the seat)
        float grey, red, glocTimer, recover; // 0..1 grey-out, 0..1 red-out, seconds unconscious, seconds recovering
        float lastGz;
        Vessel hooked;
        static Texture2D vignette;

        // ---- shake ----
        float shake;                         // 0..1 impulse, decays
        float buffet;                        // 0..1 continuous
        readonly Dictionary<Camera, Quaternion> restore = new();

        // ---- callouts ----
        enum Warn { None, PullUp, OverG, Stall, Gear, Bingo }
        static readonly string[] WarnText = { "", "PULL UP", "OVER-G", "STALL", "GEAR", "BINGO FUEL" };
        Warn current, silenced;
        bool bingoLatched;
        float warnSince;
        AudioSource audio;
        AudioClip beepHi, beepLo, warble, chime;
        float nextTone;

        void Start()
        {
            if (vignette == null) vignette = MakeVignette(256);
            audio = gameObject.AddComponent<AudioSource>();
            audio.spatialBlend = 0; audio.playOnAwake = false; audio.ignoreListenerPause = false;
            beepHi = Tone("ele_hi", 1250, 0.11f); beepLo = Tone("ele_lo", 420, 0.16f); chime = Tone("ele_chime", 880, 0.35f, true);
            warble = Warble("ele_warble", 650, 900, 0.5f);
            Camera.onPreCull += PreCull;
            Camera.onPostRender += PostRender;
            CombatLog.OnHit += Hit;
        }

        void OnDestroy()
        {
            Camera.onPreCull -= PreCull;
            Camera.onPostRender -= PostRender;
            CombatLog.OnHit -= Hit;
            Unhook();
        }

        void Unhook() { if (hooked != null) { hooked.OnFlyByWire -= Limp; hooked = null; } }

        // ---------------- simulation ----------------
        void FixedUpdate()
        {
            var v = FlightGlobals.ActiveVessel;
            if (v != hooked) { Unhook(); if (v != null) { v.OnFlyByWire += Limp; hooked = v; } grey = red = glocTimer = recover = 0; bingoLatched = false; }
            if (v == null || v.packed || KillCam.Playing) return;
            float dt = Time.fixedDeltaTime;

            // specific force along the pilot's spine (canopy direction = -forward of the control transform)
            Vector3d sf = v.acceleration_immediate - FlightGlobals.getGeeForceAtPosition(v.CoMD);
            float g = (float)Vector3d.Dot(sf, -(Vector3d)v.ReferenceTransform.forward) / 9.81f;
            if (float.IsNaN(g) || Mathf.Abs(g) > 60) g = Gz;                // collisions / kraken spikes
            Gz = Mathf.Lerp(Gz, g, 1 - Mathf.Exp(-dt * 6));
            float onsetRate = Mathf.Abs(Gz - lastGz) / dt; lastGz = Gz;

            bool crewed = v.GetCrewCount() > 0;
            if (AvionicsSettings.GEffects && crewed && !v.LandedOrSplashed)
            {
                Tolerance(v, out float pos, out float neg);
                // grey-out builds above the onset, faster the harder you pull; recovers at about 0.35 per second
                if (Gz > pos) grey += (Gz - pos) / 3.5f * dt; else grey -= 0.35f * dt;
                if (Gz < -neg) red += (-Gz - neg) / 2f * dt; else red -= 0.5f * dt;
                grey = Mathf.Clamp(grey, 0, 1.6f); red = Mathf.Clamp01(red);
                if (grey >= 1.6f && glocTimer <= 0 && recover <= 0)
                {
                    glocTimer = 4f + Random.value * 3f;
                    ScreenMessages.PostScreenMessage("G-LOC", 2f, ScreenMessageStyle.UPPER_CENTER);
                }
            }
            else { grey = Mathf.Max(0, grey - dt); red = Mathf.Max(0, red - dt); }
            if (glocTimer > 0) { glocTimer -= dt; if (glocTimer <= 0) { recover = 3f; grey = 1f; } }
            else if (recover > 0) recover -= dt;

            // shake sources
            if (AvionicsSettings.CamShake)
            {
                if (onsetRate > 4) shake = Mathf.Max(shake, Mathf.Clamp01((onsetRate - 4) / 25));
                float aoa = v.srfSpeed > 20 ? Vector3.Angle(v.ReferenceTransform.up, v.srf_velocity) : 0;
                float q = (float)v.dynamicPressurekPa;
                float stallBuffet = Mathf.Clamp01((aoa - 14) / 12) * Mathf.Clamp01(q / 15);
                float transonic = Mathf.Clamp01(1 - Mathf.Abs((float)v.mach - 1) / 0.06f) * Mathf.Clamp01(q / 20) * 0.6f;
                buffet = Mathf.Max(stallBuffet, transonic);
            }
            else buffet = 0;
            shake = Mathf.Max(0, shake - dt * 1.6f);

            Callouts(v);
        }

        /// <summary>G at which grey-out starts (+) and red-out starts (−). Braver, tougher crews hold more.</summary>
        static void Tolerance(Vessel v, out float pos, out float neg)
        {
            pos = 5f; neg = 2.5f;
            var crew = v.GetVesselCrew();
            if (crew.Count == 0) return;
            var k = crew[0];
            pos += k.courage * 0.6f + (k.isBadass ? 0.6f : 0) + (k.veteran ? 0.4f : 0) + k.experienceLevel * 0.15f;
            neg += k.courage * 0.3f + (k.isBadass ? 0.3f : 0);
        }

        /// <summary>Unconscious pilot: hands off the stick (throttle stays). Only when a person is flying, not BDArmory's AI.</summary>
        void Limp(FlightCtrlState s)
        {
            if (glocTimer <= 0 && recover <= 0) return;
            var ai = hooked != null ? hooked.ActiveController().AI : null;
            if (ai != null && ai.pilotEnabled) return;
            float k = glocTimer > 0 ? 0 : 1 - recover / 3f;   // control comes back gradually
            s.pitch *= k; s.roll *= k; s.yaw *= k;
        }

        void Hit(Vessel v, WeaponKind kind, float damage)
        {
            if (!AvionicsSettings.CamShake || v != FlightGlobals.ActiveVessel) return;
            float size = kind == WeaponKind.Gun ? 0.25f : 0.8f;
            shake = Mathf.Max(shake, Mathf.Clamp01(size + damage / 2000f));
        }

        // ---------------- callouts ----------------
        void Callouts(Vessel v)
        {
            Warn w = Warn.None;
            if (AvionicsSettings.Callouts && !v.LandedOrSplashed && v.situation == Vessel.Situations.FLYING)
            {
                double vs = v.verticalSpeed, ra = v.radarAltitude;
                float aoa = v.srfSpeed > 20 ? Vector3.Angle(v.ReferenceTransform.up, v.srf_velocity) : 0;
                double fuel = 0, max = 0;
                foreach (var p in v.parts) foreach (PartResource r in p.Resources)
                        if (r.resourceName == "LiquidFuel") { fuel += r.amount; max += r.maxAmount; }
                if (max > 0 && fuel / max > 0.3) bingoLatched = false;

                if (vs < -10 && ra < 2500 && ra / -vs < 6) w = Warn.PullUp;
                else if (Gz > 9.5f || Gz < -3.5f) w = Warn.OverG;
                else if (aoa > 20 && v.srfSpeed < 160 && ra > 30) w = Warn.Stall;
                else if (ra < 150 && vs < -1 && v.srfSpeed < 110 && !v.ActionGroups[KSPActionGroup.Gear] && HasGear(v)) w = Warn.Gear;
                else if (max > 0 && fuel / max < 0.2 && !bingoLatched) { w = Warn.Bingo; if (current == Warn.Bingo && Time.time - warnSince > 4) { bingoLatched = true; w = Warn.None; } }
            }
            if (w != current) { current = w; warnSince = Time.time; nextTone = 0; if (w != silenced) silenced = Warn.None; }
        }

        static bool HasGear(Vessel v)
        {
            foreach (var p in v.parts) if (p.Modules.Contains("ModuleWheelDeployment")) return true;
            return false;
        }

        void Update()
        {
            if (GUIUtility.keyboardControl == 0 && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) && Input.GetKeyDown(KeyCode.N)) silenced = current;
            if (current == Warn.None || current == silenced || KillCam.Playing || Time.unscaledTime < nextTone || FlightDriver.Pause) return;
            audio.volume = GameSettings.UI_VOLUME * 0.7f;
            switch (current)
            {
                case Warn.PullUp: audio.PlayOneShot(beepHi); nextTone = Time.unscaledTime + 0.22f; break;
                case Warn.OverG: audio.PlayOneShot(beepLo); nextTone = Time.unscaledTime + 0.3f; break;
                case Warn.Stall: audio.PlayOneShot(warble); nextTone = Time.unscaledTime + 0.55f; break;
                case Warn.Gear: audio.PlayOneShot(beepLo); nextTone = Time.unscaledTime + 0.8f; break;
                case Warn.Bingo: audio.PlayOneShot(chime); nextTone = Time.unscaledTime + 1.2f; break;
            }
        }

        // ---------------- camera shake (applied per render, removed after: the cameras' own logic never sees it) ----------------
        void PreCull(Camera c)
        {
            float amp = Mathf.Max(shake, buffet * 0.35f);
            if (amp < 0.01f || KillCam.Playing || !IsShakeCamera(c)) return;
            float t = Time.unscaledTime * (buffet > shake ? 23f : 31f);
            float deg = amp * (CameraManager.Instance != null && CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA ? 1.6f : 0.6f);
            var tr = c.transform;
            restore[c] = tr.localRotation;
            tr.localRotation *= Quaternion.Euler((Mathf.PerlinNoise(t, 0.3f) - 0.5f) * 2 * deg, (Mathf.PerlinNoise(0.7f, t) - 0.5f) * 2 * deg, (Mathf.PerlinNoise(t, t * 0.5f) - 0.5f) * deg);
        }

        void PostRender(Camera c)
        {
            if (restore.TryGetValue(c, out var r)) { c.transform.localRotation = r; restore.Remove(c); }
        }

        static bool IsShakeCamera(Camera c)
        {
            if (FlightCamera.fetch != null && c == FlightCamera.fetch.mainCamera) return true;
            return InternalCamera.Instance != null && c.transform == InternalCamera.Instance.transform;
        }

        // ---------------- overlay ----------------
        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || KillCam.Playing) return;
            var full = new Rect(0, 0, Screen.width, Screen.height);
            float g = Mathf.Clamp01(grey);
            if (glocTimer > 0) { GUI.color = Color.black; GUI.DrawTexture(full, Texture2D.whiteTexture); }
            else
            {
                if (g > 0.02f)
                {
                    GUI.color = new Color(0.35f, 0.35f, 0.37f, g * 0.55f); GUI.DrawTexture(full, Texture2D.whiteTexture);
                    // tunnel vision: the vignette closes in as grey-out deepens
                    float s = Mathf.Lerp(2.2f, 0.75f, g);
                    var r = new Rect(Screen.width / 2f - Screen.width * s / 2, Screen.height / 2f - Screen.width * s / 2, Screen.width * s, Screen.width * s);
                    GUI.color = new Color(0, 0, 0, Mathf.Clamp01(g * 1.3f)); GUI.DrawTexture(r, vignette);
                    if (r.y > 0) { GUI.DrawTexture(new Rect(0, 0, Screen.width, r.y), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(0, r.yMax, Screen.width, Screen.height - r.yMax), Texture2D.whiteTexture); }
                    if (r.x > 0) { GUI.DrawTexture(new Rect(0, 0, r.x, Screen.height), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(r.xMax, 0, Screen.width - r.xMax, Screen.height), Texture2D.whiteTexture); }
                }
                if (red > 0.02f) { GUI.color = new Color(0.6f, 0.02f, 0.02f, red * 0.6f); GUI.DrawTexture(full, Texture2D.whiteTexture); }
            }

            if (current != Warn.None && AvionicsSettings.Callouts && (Time.unscaledTime * 3 % 1 < 0.6f || current == silenced))
            {
                var style = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                var r = new Rect(Screen.width / 2f - 130, Screen.height * 0.3f, 260, 44);
                GUI.color = new Color(0, 0, 0, 0.5f); GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = current == Warn.Bingo || current == Warn.Gear ? new Color(1f, 0.8f, 0.2f) : new Color(1f, 0.25f, 0.2f);
                GUI.Label(r, WarnText[(int)current], style);
            }
            GUI.color = Color.white;
        }

        // ---------------- generated assets ----------------
        static Texture2D MakeVignette(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    px[y * n + x] = new Color(1, 1, 1, Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.35f, 1f, d)));
                }
            t.SetPixels(px); t.Apply();
            return t;
        }

        static AudioClip Tone(string name, float hz, float len, bool decay = false)
        {
            int rate = 44100, n = (int)(rate * len);
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate, env = Mathf.Min(1, Mathf.Min(t, len - t) * 80);
                if (decay) env *= Mathf.Exp(-t * 6);
                d[i] = Mathf.Sin(2 * Mathf.PI * hz * t) * env * 0.5f;
            }
            var c = AudioClip.Create(name, n, 1, rate, false); c.SetData(d, 0); return c;
        }

        static AudioClip Warble(string name, float a, float b, float len)
        {
            int rate = 44100, n = (int)(rate * len);
            var d = new float[n]; float ph = 0;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate, hz = (t * 8 % 1) < 0.5f ? a : b, env = Mathf.Min(1, Mathf.Min(t, len - t) * 80);
                ph += 2 * Mathf.PI * hz / rate; d[i] = Mathf.Sin(ph) * env * 0.45f;
            }
            var c = AudioClip.Create(name, n, 1, rate, false); c.SetData(d, 0); return c;
        }
    }
}
