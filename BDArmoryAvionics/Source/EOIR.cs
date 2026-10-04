using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Shaders;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>HullCam VDS access by reflection.</summary>
    public static class HullCam
    {
        static bool resolved;
        static Type type;
        static FieldInfo current, transformName, camForward, camPosition, camFoV;

        static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            foreach (var a in AssemblyLoader.loadedAssemblies)
                if (a.name == "HullcamVDSContinued") type = a.assembly.GetType("HullcamVDS.MuMechModuleHullCamera");
            if (type == null) return;
            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic;
            current = type.GetField("sCurrentCamera", bf | BindingFlags.Static);
            transformName = type.GetField("cameraTransformName", bf | BindingFlags.Instance);
            camForward = type.GetField("cameraForward", bf | BindingFlags.Instance);
            camPosition = type.GetField("cameraPosition", bf | BindingFlags.Instance);
            camFoV = type.GetField("cameraFoV", bf | BindingFlags.Instance);
        }

        public static bool Available { get { Resolve(); return type != null; } }
        public static PartModule Find(Part p) { Resolve(); if (type == null || p == null) return null; foreach (PartModule m in p.Modules) if (type.IsInstanceOfType(m)) return m; return null; }
        /// <summary>The HullCam currently being looked through, or null (normal flight camera).</summary>
        public static PartModule Current { get { Resolve(); return current?.GetValue(null) as PartModule; } }
        public static float FoV(PartModule cam) => camFoV != null ? (float)camFoV.GetValue(cam) : 60f;

        public static void Ray(PartModule cam, out Vector3 origin, out Vector3 fwd)
        {
            if (cam == Current && FlightCamera.fetch != null) { var t = FlightCamera.fetch.mainCamera.transform; origin = t.position; fwd = t.forward; return; }
            string tn = transformName?.GetValue(cam) as string;
            Transform tr = !string.IsNullOrEmpty(tn) ? cam.part.FindModelTransform(tn) : null;
            if (tr == null) tr = cam.part.transform;
            Vector3 lf = camForward != null ? (Vector3)camForward.GetValue(cam) : Vector3.forward;
            Vector3 lp = camPosition != null ? (Vector3)camPosition.GetValue(cam) : Vector3.zero;
            origin = tr.TransformPoint(lp);
            fwd = tr.TransformDirection(lf == Vector3.zero ? Vector3.forward : lf).normalized;
        }
    }

    /// <summary>
    /// EO/IR sensor view for HullCam: while you look through a HullCam, a thermal image (white-hot or black-hot: grey scene,
    /// darkened, with engines, hot parts and aircraft glowing) and point track: click a craft or the ground to track it.
    /// The track drives IR turrets on the craft (so a camera or gun on servos follows it) and can be sent as a GPS point or selected target.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class EOIRView : MonoBehaviour
    {
        public static int Mode = 1; // 0 EO (colour), 1 WHOT, 2 BHOT
        static readonly string[] ModeNames = { "EO", "WHOT", "BHOT" };
        static Vessel trackVessel;
        static Vector3d trackGeo;   // lat, lon, alt
        static bool trackGround;
        static string trackName = "";

        Camera attached;
        CommandBuffer cb;
        int cbMode = -1;
        Material gray, darken, invert;
        Mesh quad;

        public static bool HasTrack => trackVessel != null || trackGround;

        public static bool TryTrack(out Vector3 pos, out Vector3 vel)
        {
            pos = default; vel = default;
            if (trackVessel != null)
            {
                if (!trackVessel.loaded) { ClearTrack(); return false; }
                pos = trackVessel.CoM; vel = trackVessel.Velocity(); return true;
            }
            if (trackGround && FlightGlobals.currentMainBody != null)
            { pos = FlightGlobals.currentMainBody.GetWorldSurfacePosition(trackGeo.x, trackGeo.y, trackGeo.z); return true; }
            return false;
        }

        public static void ClearTrack() { trackVessel = null; trackGround = false; trackName = ""; }

        void OnDestroy() => Detach();

        // ---- Thermal image: command buffer on the near flight camera (renders last, so it sees the whole scene) ----

        void LateUpdate()
        {
            bool want = HullCam.Current != null && Mode != 0 && BDAShaderLoader.GrayscaleEffectShader != null;
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (!want || cam == null) { Detach(); return; }
            if (attached != cam || cbMode != Mode) { Detach(); Attach(cam); }
        }

        void Attach(Camera cam)
        {
            if (gray == null)
            {
                gray = new Material(BDAShaderLoader.GrayscaleEffectShader);
                gray.SetFloat("_RedPower", 1.5f); gray.SetFloat("_RedDelta", 1.5f); // never keep red
                darken = FLIRModes.Make(BlendMode.DstColor, BlendMode.Zero);
                invert = FLIRModes.Make(BlendMode.OneMinusDstColor, BlendMode.Zero);
                darken.SetColor("_Color", new Color(0.55f, 0.55f, 0.55f, 1));
                invert.SetColor("_Color", Color.white);
                quad = new Mesh
                {
                    vertices = new[] { new Vector3(-1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0), new Vector3(1, -1, 0) },
                    colors = new[] { Color.white, Color.white, Color.white, Color.white },
                    triangles = new[] { 0, 1, 2, 0, 2, 3 }
                };
            }
            cb = new CommandBuffer { name = "ELE EO/IR" };
            int tmp = Shader.PropertyToID("_ELE_EOIR_Tmp");
            cb.GetTemporaryRT(tmp, -1, -1, 0);
            cb.Blit(BuiltinRenderTextureType.CameraTarget, tmp, gray);
            cb.Blit(tmp, BuiltinRenderTextureType.CameraTarget);
            cb.ReleaseTemporaryRT(tmp);
            cb.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cb.DrawMesh(quad, Matrix4x4.identity, darken); // cool, dark background so heat stands out
            if (Mode == 2) cb.DrawMesh(quad, Matrix4x4.identity, invert);
            cam.AddCommandBuffer(CameraEvent.AfterEverything, cb);
            attached = cam; cbMode = Mode;
        }

        void Detach()
        {
            if (attached != null && cb != null) attached.RemoveCommandBuffer(CameraEvent.AfterEverything, cb);
            attached = null; cb = null; cbMode = -1;
        }

        // ---- Point track: click in the camera view ----

        void Update()
        {
            if (HullCam.Current == null) return;
            if (Input.GetKeyDown(KeyCode.Delete)) ClearTrack();
            if (!Input.GetMouseButtonDown(0)) return;
            Vector2 gm = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (HudRect.Contains(gm) || AvionicsCore.MouseOverAnyWindow()) return;
            var cam = FlightCamera.fetch.mainCamera;
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            var own = FlightGlobals.ActiveVessel;
            // Skip our own craft: the camera sits on it.
            var hits = Physics.RaycastAll(ray, 200000f, (1 << 0) | (1 << 15) | (1 << 28));
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                var p = h.collider.GetComponentInParent<Part>();
                if (p != null && p.vessel == own) continue;
                if (p != null && p.vessel != null) { trackVessel = p.vessel; trackGround = false; trackName = p.vessel.GetName(); Msg($"Tracking {trackName}"); return; }
                var body = FlightGlobals.currentMainBody;
                trackVessel = null; trackGround = true;
                trackGeo = new Vector3d(body.GetLatitude(h.point), body.GetLongitude(h.point), body.GetAltitude(h.point));
                trackName = "point"; Msg("Point track"); return;
            }
        }

        static void Msg(string s) => ScreenMessages.PostScreenMessage($"EO/IR: {s}", 2f, ScreenMessageStyle.UPPER_CENTER);

        // ---- Sensor HUD ----

        static Rect HudRect => new(Screen.width * 0.5f - 230, 8, 460, 26);

        void OnGUI()
        {
            var hc = HullCam.Current;
            if (hc == null || !BDArmorySetup.GAME_UI_ENABLED) return;
            var cam = FlightCamera.fetch.mainCamera;
            if (Event.current.type == EventType.Repaint && Mode != 0) DrawHeat(cam, hc.vessel);

            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            Rect r = HudRect; float x = r.x;
            GUI.Box(r, "", BDArmorySetup.BDGuiSkin.box);
            for (int i = 0; i < 3; i++) { if (GUI.Button(new Rect(x + 3, r.y + 3, 50, 20), ModeNames[i], Mode == i ? sel : btn)) Mode = i; x += 52; }
            x += 6;
            if (GUI.Button(new Rect(x + 3, r.y + 3, 60, 20), "Clear", btn)) ClearTrack(); x += 62;
            if (GUI.Button(new Rect(x + 3, r.y + 3, 60, 20), "To GPS", btn) && TryTrack(out var tp, out _))
                AvionicsContacts.AddGPS(VectorUtils.WorldPositionToGeoCoords(tp, FlightGlobals.currentMainBody), trackGround ? "EOIR pt" : trackName);
            x += 62;
            if (GUI.Button(new Rect(x + 3, r.y + 3, 60, 20), "Select", btn) && trackVessel != null)
                foreach (var c in AvionicsContacts.Contacts) if (c.vessel == trackVessel) { AvionicsContacts.Select(c); break; }
            x += 62;
            AvionicsDraw.ColorLabel(new Rect(x + 4, r.y + 6, r.xMax - x - 6, 16), $"FOV {cam.fieldOfView:F1}°", AvionicsDraw.LabelSmall, AvionicsDraw.HudText);

            if (Event.current.type != EventType.Repaint) return;
            Color hud = Mode == 2 ? new Color(0.1f, 0.1f, 0.1f) : new Color(0.85f, 1f, 0.85f);
            Vector2 c0 = new(Screen.width * 0.5f, Screen.height * 0.5f);
            if (!AvionicsDraw.Begin()) return;
            // Reticle
            float g = 14, l = 40;
            AvionicsDraw.Line(c0 + new Vector2(-g - l, 0), c0 + new Vector2(-g, 0), 1.5f, hud);
            AvionicsDraw.Line(c0 + new Vector2(g, 0), c0 + new Vector2(g + l, 0), 1.5f, hud);
            AvionicsDraw.Line(c0 + new Vector2(0, -g - l), c0 + new Vector2(0, -g), 1.5f, hud);
            AvionicsDraw.Line(c0 + new Vector2(0, g), c0 + new Vector2(0, g + l), 1.5f, hud);
            string info = "";
            if (TryTrack(out var pos, out var vel))
            {
                Vector3 sp = cam.WorldToScreenPoint(pos);
                if (sp.z > 0)
                {
                    Vector2 p = new(sp.x, Screen.height - sp.y);
                    float s = trackVessel != null ? 18 : 10;
                    Color tc = new(1f, 0.85f, 0.2f);
                    AvionicsDraw.Box(p, s, 2, tc);
                    AvionicsDraw.Line(p + new Vector2(-s - 8, 0), p + new Vector2(-s, 0), 2, tc);
                    AvionicsDraw.Line(p + new Vector2(s, 0), p + new Vector2(s + 8, 0), 2, tc);
                    AvionicsDraw.Line(p + new Vector2(0, -s - 8), p + new Vector2(0, -s), 2, tc);
                    AvionicsDraw.Line(p + new Vector2(0, s), p + new Vector2(0, s + 8), 2, tc);
                }
                else AvionicsDraw.Triangle(new Vector2(c0.x, 60), 8, 0, 2, new Color(1f, 0.85f, 0.2f)); // behind: cue at the top
                var body = FlightGlobals.currentMainBody;
                float range = (pos - cam.transform.position).magnitude;
                bool los = !RadarUtils.TerrainCheck(cam.transform.position, pos);
                info = $"TRK {trackName}  RNG {AvionicsDraw.FormatRange(range)}  {body.GetLatitude(pos):F4} {body.GetLongitude(pos):F4}  {AvionicsDraw.FormatAlt(body.GetAltitude(pos))}" +
                       (trackVessel != null ? $"  {vel.magnitude:F0} m/s" : "") + (los ? "" : "  MASKED");
            }
            AvionicsDraw.End();
            AvionicsDraw.ShadowLabel(new Rect(c0.x - 300, Screen.height - 70, 600, 16), $"{ModeNames[Mode]}  {hc.part.partInfo.title}", AvionicsDraw.LabelCenter, hud);
            AvionicsDraw.ShadowLabel(new Rect(c0.x - 300, Screen.height - 54, 600, 16), info != "" ? info : "click a craft or the ground to track · Del clears", AvionicsDraw.LabelCenter, hud);
        }

        /// <summary>Heat: engines very hot, hot parts warm, aircraft and kerbals faintly warm (bigger and hotter when fast).</summary>
        static void DrawHeat(Camera cam, Vessel own)
        {
            Color baseColor = Mode == 2 ? Color.black : Color.white;
            var old = GUI.color;
            float tanHalf = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            Vector3 camPos = cam.transform.position;
            void Glow(Vector3 w, float sizeM, float intensity)
            {
                Vector3 vp = cam.WorldToViewportPoint(w);
                if (vp.z <= 0 || vp.x < -0.1f || vp.x > 1.1f || vp.y < -0.1f || vp.y > 1.1f) return;
                float pxPerM = Screen.height / (2 * vp.z * tanHalf);
                float r = Mathf.Clamp(sizeM * pxPerM, 2.5f, Screen.height * 0.3f);
                float x = vp.x * Screen.width, y = (1 - vp.y) * Screen.height;
                GUI.color = new Color(baseColor.r, baseColor.g, baseColor.b, Mathf.Clamp01(intensity));
                GUI.DrawTexture(new Rect(x - r, y - r, 2 * r, 2 * r), AvionicsDraw.GlowTexture, ScaleMode.StretchToFill, true);
            }
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed || v.vesselType == VesselType.Debris && v.parts.Count < 2) continue;
                if ((v.CoM - camPos).sqrMagnitude > 40000f * 40000f) continue;
                if (v != own && RadarUtils.TerrainCheck(camPos, v.CoM)) continue;
                // Body heat: skin warmed by speed (and kerbals are warm).
                float size = Mathf.Clamp(v.GetRadius(), 0.5f, 30f);
                float warm = v.isEVA ? 0.6f : Mathf.Clamp01(0.25f + (float)v.srfSpeed / 900f);
                if (v != own) Glow(v.CoM, size * 0.9f, warm * 0.6f);
                foreach (var p in v.parts)
                {
                    if (p == null) continue;
                    foreach (var m in p.Modules)
                        if (m is ModuleEngines e && e.EngineIgnited && e.finalThrust > 0.01f)
                        {
                            Transform t = e.thrustTransforms != null && e.thrustTransforms.Count > 0 && e.thrustTransforms[0] != null ? e.thrustTransforms[0] : p.transform;
                            float k = Mathf.Clamp01(0.6f + 0.4f * e.currentThrottle);
                            Glow(t.position, 1.5f + 2f * k, 1f);
                            Glow(t.position + t.forward * 4f, 3f + 4f * k, 0.45f * k); // plume behind the nozzle
                            break;
                        }
                    float heat = (float)((p.temperature - 350) / 1000);
                    if (heat > 0.05f) Glow(p.transform.position, 1.2f, Mathf.Clamp01(heat));
                }
            }
            foreach (var (hp, hi, hs) in Persistent.Heat)
                if (!RadarUtils.TerrainCheck(camPos, hp + (Vector3)FlightGlobals.getUpAxis(hp) * 2f)) Glow(hp, hs, hi);
            GUI.color = old;
        }
    }
}
