using UnityEngine;
using UnityEngine.Rendering;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Line drawing, generated textures, colours and label styles shared by the avionics windows.
    /// Lines are drawn with GL during the Repaint event, using coordinates local to the current GUI window.
    /// </summary>
    public static class AvionicsDraw
    {
        public static readonly Color Hostile = new(0.88f, 0.44f, 0.17f);
        public static readonly Color Friendly = new(0.36f, 0.79f, 0.88f);
        public static readonly Color Unknown = new(0.95f, 0.85f, 0.35f);
        public static readonly Color Neutral = new(0.75f, 0.75f, 0.75f);
        public static readonly Color Missile = new(1f, 0.25f, 0.2f);
        public static readonly Color LockBox = new(1f, 0.82f, 0.29f);
        public static readonly Color Grid = new(0.18f, 0.42f, 0.27f, 0.9f);
        public static readonly Color GridFaint = new(0.12f, 0.29f, 0.19f, 0.9f);
        public static readonly Color ScopeText = new(0.5f, 0.64f, 0.54f);
        public static readonly Color HudText = new(0.91f, 0.96f, 0.93f);
        public static readonly Color ScopeBackground = new(0.04f, 0.05f, 0.05f, 0.94f);

        static Material lineMaterial;
        public static Material LineMaterial
        {
            get
            {
                if (lineMaterial == null)
                {
                    var shader = Shader.Find("Hidden/Internal-Colored");
                    if (shader == null) return null;
                    lineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    lineMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                    lineMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    lineMaterial.SetInt("_Cull", (int)CullMode.Off);
                    lineMaterial.SetInt("_ZWrite", 0);
                    lineMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
                }
                return lineMaterial;
            }
        }

        // Mapping from window-local GUI coordinates to GL pixel coordinates, valid between Begin() and End().
        static Vector2 origin;
        static float scale = 1;
        static bool drawing = false;

        /// <summary>
        /// Start a batch of lines. Only draws during the Repaint event; returns false otherwise.
        /// </summary>
        public static bool Begin()
        {
            if (Event.current.type != EventType.Repaint) return false;
            var mat = LineMaterial;
            if (mat == null) return false;
            origin = GUIUtility.GUIToScreenPoint(Vector2.zero);
            scale = GUIUtility.GUIToScreenPoint(Vector2.right).x - origin.x;
            GL.PushMatrix();
            mat.SetPass(0);
            GL.LoadPixelMatrix();
            GL.Begin(GL.QUADS);
            drawing = true;
            return true;
        }

        public static void End()
        {
            if (!drawing) return;
            GL.End();
            GL.PopMatrix();
            drawing = false;
        }

        static Vector3 ToGL(Vector2 p)
        {
            var s = origin + p * scale;
            return new Vector3(s.x, Screen.height - s.y, 0);
        }

        public static void Line(Vector2 a, Vector2 b, float width, Color color)
        {
            if (!drawing) return;
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) return;
            Vector2 n = new Vector2(-d.y, d.x) / len * (width * 0.5f);
            GL.Color(color);
            GL.Vertex(ToGL(a - n));
            GL.Vertex(ToGL(a + n));
            GL.Vertex(ToGL(b + n));
            GL.Vertex(ToGL(b - n));
        }

        public static void DashedLine(Vector2 a, Vector2 b, float width, Color color, float dash = 4, float gap = 4)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) return;
            Vector2 dir = d / len;
            for (float t = 0; t < len; t += dash + gap)
                Line(a + dir * t, a + dir * Mathf.Min(t + dash, len), width, color);
        }

        public static void Polyline(Vector2[] points, int count, float width, Color color, bool closed)
        {
            for (int i = 0; i < count - 1; i++) Line(points[i], points[i + 1], width, color);
            if (closed && count > 2) Line(points[count - 1], points[0], width, color);
        }

        public static void Box(Vector2 c, float half, float width, Color color)
        {
            Vector2 a = c + new Vector2(-half, -half), b = c + new Vector2(half, -half), d = c + new Vector2(half, half), e = c + new Vector2(-half, half);
            Line(a, b, width, color); Line(b, d, width, color); Line(d, e, width, color); Line(e, a, width, color);
        }

        public static void Diamond(Vector2 c, float r, float width, Color color)
        {
            Vector2 t = c + new Vector2(0, -r), rt = c + new Vector2(r, 0), bt = c + new Vector2(0, r), l = c + new Vector2(-r, 0);
            Line(t, rt, width, color); Line(rt, bt, width, color); Line(bt, l, width, color); Line(l, t, width, color);
        }

        public static void Triangle(Vector2 c, float r, float angleDeg, float width, Color color)
        {
            float a = angleDeg * Mathf.Deg2Rad;
            Vector2 P(float off) => c + new Vector2(Mathf.Sin(a + off), -Mathf.Cos(a + off)) * r;
            Vector2 p0 = P(0), p1 = P(2.5f), p2 = P(-2.5f);
            Line(p0, p1, width, color); Line(p1, p2, width, color); Line(p2, p0, width, color);
        }

        public static void Circle(Vector2 c, float r, float width, Color color, int segments = 32)
        {
            Vector2 prev = c + new Vector2(r, 0);
            for (int i = 1; i <= segments; i++)
            {
                float a = i * 2 * Mathf.PI / segments;
                Vector2 p = c + new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
                Line(prev, p, width, color);
                prev = p;
            }
        }

        public static void FilledRect(Rect r, Color color)
        {
            if (!drawing) return;
            GL.Color(color);
            GL.Vertex(ToGL(new Vector2(r.xMin, r.yMin)));
            GL.Vertex(ToGL(new Vector2(r.xMax, r.yMin)));
            GL.Vertex(ToGL(new Vector2(r.xMax, r.yMax)));
            GL.Vertex(ToGL(new Vector2(r.xMin, r.yMax)));
        }

        // ---- Textures ----
        static Texture2D glowTexture;
        /// <summary>Soft radial blob used for FLIR heat glows.</summary>
        public static Texture2D GlowTexture
        {
            get
            {
                if (glowTexture == null)
                {
                    const int size = 64;
                    glowTexture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            float dx = (x + 0.5f) / size * 2 - 1, dy = (y + 0.5f) / size * 2 - 1;
                            float d = Mathf.Sqrt(dx * dx + dy * dy);
                            float a = Mathf.Clamp01(1 - d);
                            a = a * a * (3 - 2 * a); // smoothstep falloff
                            glowTexture.SetPixel(x, y, new Color(1, 1, 1, a));
                        }
                    glowTexture.Apply();
                }
                return glowTexture;
            }
        }

        static Texture2D iconTexture;
        /// <summary>Toolbar icon: a radar scope with a sweep line, drawn at runtime so no texture file is needed.</summary>
        public static Texture2D IconTexture
        {
            get
            {
                if (iconTexture == null)
                {
                    const int size = 38;
                    iconTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                    Vector2 c = new(size / 2f - 0.5f, size / 2f - 0.5f);
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            Vector2 p = new(x, y);
                            float d = Vector2.Distance(p, c);
                            float a = 0;
                            if (Mathf.Abs(d - 16) < 1.1f || Mathf.Abs(d - 10.5f) < 0.8f || Mathf.Abs(d - 5) < 0.8f) a = 1;
                            Vector2 sweep = new Vector2(1, 1).normalized;
                            Vector2 rel = p - c;
                            float along = Vector2.Dot(rel, sweep);
                            float across = Mathf.Abs(rel.x * sweep.y - rel.y * sweep.x);
                            if (along > 0 && along < 16 && across < 1.2f) a = 1;
                            if (Vector2.Distance(p, c + new Vector2(-7, 6)) < 2.2f) a = 1;
                            iconTexture.SetPixel(x, y, new Color(1, 1, 1, a));
                        }
                    iconTexture.Apply();
                }
                return iconTexture;
            }
        }

        // ---- Styles ----
        static GUIStyle label, labelRight, labelCenter, labelSmall, header;
        public static GUIStyle Label => label ??= MakeLabel(11, TextAnchor.UpperLeft);
        public static GUIStyle LabelRight => labelRight ??= MakeLabel(11, TextAnchor.UpperRight);
        public static GUIStyle LabelCenter => labelCenter ??= MakeLabel(11, TextAnchor.UpperCenter);
        public static GUIStyle LabelSmall => labelSmall ??= MakeLabel(10, TextAnchor.UpperLeft);
        public static GUIStyle Header => header ??= MakeHeader();

        static GUIStyle MakeLabel(int size, TextAnchor anchor)
        {
            var s = new GUIStyle(BDArmory.UI.BDArmorySetup.BDGuiSkin.label)
            {
                fontSize = size,
                alignment = anchor,
                wordWrap = false,
                clipping = TextClipping.Overflow,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                richText = false,
            };
            s.normal.textColor = Color.white;
            return s;
        }

        static GUIStyle MakeHeader()
        {
            var s = MakeLabel(11, TextAnchor.MiddleLeft);
            s.fontStyle = FontStyle.Bold;
            s.normal.textColor = new Color(0.6f, 0.63f, 0.6f);
            return s;
        }

        /// <summary>Draw text with a 1px shadow so it stays readable over camera imagery.</summary>
        public static void ShadowLabel(Rect r, string text, GUIStyle style, Color color)
        {
            var old = style.normal.textColor;
            style.normal.textColor = new Color(0, 0, 0, 0.85f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, style);
            style.normal.textColor = color;
            GUI.Label(r, text, style);
            style.normal.textColor = old;
        }

        public static void ColorLabel(Rect r, string text, GUIStyle style, Color color)
        {
            var old = style.normal.textColor;
            style.normal.textColor = color;
            GUI.Label(r, text, style);
            style.normal.textColor = old;
        }

        public static string FormatRange(float meters)
        {
            if (meters < 1000) return $"{meters:0} m";
            if (meters < 100000) return $"{meters / 1000f:0.0} km";
            return $"{meters / 1000f:0} km";
        }

        public static string FormatAlt(double meters)
        {
            if (System.Math.Abs(meters) < 1000) return $"{meters:0} m";
            return $"{meters / 1000:0.0}k";
        }
    }
}
