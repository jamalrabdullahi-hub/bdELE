using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

using BDArmory.Radar;
using BDArmory.Targeting;

namespace BDArmory.Avionics
{
    /// <summary>
    /// FLIR modes for the targeting camera: white-hot, black-hot and green, with a contrast control,
    /// plus heat glows over running engines and hot parts.
    /// The image is post-processed with Unity's built-in "Hidden/Internal-Colored" shader using blend modes,
    /// so no new shader or asset bundle is needed. The base image is still BDArmory's grayscale render,
    /// so this is a stylised thermal view rather than a true infrared simulation.
    /// </summary>
    public static class FLIRModes
    {
        public static bool Active => AvionicsSettings.FLIRMode != 0;
        public static string ModeName => AvionicsSettings.FLIRMode switch { 1 => "WHOT", 2 => "BHOT", 3 => "GRN", _ => "OFF" };

        static Material multiply2x, add, invert, tint;
        static bool materialsFailed = false;

        public static Material Make(BlendMode src, BlendMode dst)
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return null;
            var m = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            m.SetInt("_SrcBlend", (int)src);
            m.SetInt("_DstBlend", (int)dst);
            m.SetInt("_Cull", (int)CullMode.Off);
            m.SetInt("_ZWrite", 0);
            m.SetInt("_ZTest", (int)CompareFunction.Always);
            return m;
        }

        static bool EnsureMaterials()
        {
            if (materialsFailed) return false;
            if (multiply2x != null) return true;
            multiply2x = Make(BlendMode.DstColor, BlendMode.SrcColor);   // result = 2 * src * dst
            add = Make(BlendMode.One, BlendMode.One);                     // result = src + dst
            invert = Make(BlendMode.OneMinusDstColor, BlendMode.Zero);    // result = 1 - dst (with white src)
            tint = Make(BlendMode.DstColor, BlendMode.Zero);              // result = src * dst
            if (multiply2x == null || add == null || invert == null || tint == null)
            {
                materialsFailed = true;
                Debug.LogWarning("[BDArmory.Avionics]: Hidden/Internal-Colored shader not found; FLIR image modes are disabled.");
                return false;
            }
            return true;
        }

        static void Pass(Material m, Color c)
        {
            m.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(c);
            GL.Vertex3(0, 0, 0); GL.Vertex3(0, 1, 0); GL.Vertex3(1, 1, 0); GL.Vertex3(1, 0, 0);
            GL.End();
        }

        /// <summary>Called by TargetingCamera after the cameras have rendered into the render texture.</summary>
        public static void PostProcess(RenderTexture rt)
        {
            if (!Active || rt == null || !EnsureMaterials()) return;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.PushMatrix();
            GL.LoadOrtho();

            // Contrast around mid-grey: out = c * x - 0.5 * (c - 1)
            float c = Mathf.Clamp(AvionicsSettings.FLIRContrast, 1f, 2.5f);
            if (c > 1.001f)
            {
                float g = Mathf.Sqrt(c) * 0.5f; // two passes of 2*g each
                Pass(multiply2x, new Color(g, g, g, 1));
                Pass(multiply2x, new Color(g, g, g, 1));
                float k = 0.5f * (c - 1);
                Pass(invert, Color.white);
                Pass(add, new Color(k, k, k, 0));
                Pass(invert, Color.white);
            }

            if (AvionicsSettings.FLIRMode == 2) Pass(invert, Color.white);
            else if (AvionicsSettings.FLIRMode == 3) Pass(tint, new Color(0.35f, 1f, 0.45f, 1));

            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        // ---- Heat sources ----

        struct HeatSource { public Transform t; public float intensity; public float size; }
        static readonly List<HeatSource> sources = new();
        static float nextScan = 0;

        /// <summary>Rescan loaded vessels for hot parts a few times a second.</summary>
        public static void UpdateHeatSources(ModuleTargetingCamera cam)
        {
            if (Time.time < nextScan) return;
            nextScan = Time.time + 0.25f;
            sources.Clear();
            if (cam == null || !Active || !AvionicsSettings.FLIRHeatGlow || cam.cameraParentTransform == null) return;
            Vector3 camPos = cam.cameraParentTransform.position;
            const float maxDist = 25000f;
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v == cam.vessel || v.packed) continue;
                if ((v.CoM - camPos).sqrMagnitude > maxDist * maxDist) continue;
                foreach (var p in v.parts)
                {
                    if (p == null) continue;
                    bool engine = false;
                    foreach (var m in p.Modules)
                    {
                        if (m is ModuleEngines e && e.EngineIgnited && e.finalThrust > 0.01f)
                        {
                            float intensity = Mathf.Clamp01(0.55f + 0.45f * e.currentThrottle);
                            Transform t = e.thrustTransforms != null && e.thrustTransforms.Count > 0 && e.thrustTransforms[0] != null ? e.thrustTransforms[0] : p.transform;
                            sources.Add(new HeatSource { t = t, intensity = intensity, size = 1.4f });
                            engine = true;
                            break;
                        }
                    }
                    if (engine) continue;
                    float heat = (float)((p.temperature - 400) / 1200);
                    if (heat > 0.05f) sources.Add(new HeatSource { t = p.transform, intensity = Mathf.Clamp01(heat) * 0.8f, size = 1f });
                }
            }
        }

        /// <summary>Draw heat glows over the camera image. Must be called inside the TGP window.</summary>
        public static void DrawHeatGlows(Rect imageRect)
        {
            if (!Active || !AvionicsSettings.FLIRHeatGlow || sources.Count == 0 || Event.current.type != EventType.Repaint) return;
            var tc = TargetingCamera.Instance;
            var cam = tc != null ? tc.NearCamera() : null;
            if (cam == null) return;
            Vector3 camPos = cam.transform.position;
            float tanHalf = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            Color baseColor = AvionicsSettings.FLIRMode switch { 2 => Color.black, 3 => new Color(0.75f, 1f, 0.75f), _ => Color.white };

            var oldColor = GUI.color;
            GUI.BeginGroup(imageRect);
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s.t == null) continue;
                Vector3 pos = s.t.position;
                Vector3 vp = cam.WorldToViewportPoint(pos);
                if (vp.z <= 0 || vp.x < -0.05f || vp.x > 1.05f || vp.y < -0.05f || vp.y > 1.05f) continue;
                if (RadarUtils.TerrainCheck(camPos, pos)) continue; // hidden behind terrain
                float pxPerM = imageRect.height / (2 * vp.z * tanHalf);
                float r = Mathf.Clamp(s.size * pxPerM * (1 + 2 * s.intensity), 3, imageRect.height * 0.25f);
                float x = vp.x * imageRect.width, y = (1 - vp.y) * imageRect.height;
                GUI.color = new Color(baseColor.r, baseColor.g, baseColor.b, Mathf.Clamp01(0.35f + 0.65f * s.intensity));
                GUI.DrawTexture(new Rect(x - r, y - r, 2 * r, 2 * r), AvionicsDraw.GlowTexture, ScaleMode.StretchToFill, true);
                // Hot core
                float rc = Mathf.Max(1.5f, r * 0.3f);
                GUI.DrawTexture(new Rect(x - rc, y - rc, 2 * rc, 2 * rc), AvionicsDraw.GlowTexture, ScaleMode.StretchToFill, true);
            }
            GUI.EndGroup();
            GUI.color = oldColor;
        }
    }
}
