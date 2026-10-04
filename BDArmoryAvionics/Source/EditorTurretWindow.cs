using System.Collections.Generic;
using UnityEngine;

using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// VAB/SPH IR turret tool: shows every turret the flight controller will find on this craft, lets you edit its logic
    /// (what it aims at and in what order, which part aims it, servo directions, speed and smoothing), and previews the aim:
    /// pick a test direction and see the servo positions it needs, whether the limits allow it, and a coverage map.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorTurretWindow : MonoBehaviour
    {
        public static bool Show;
        const int WindowID = 71730362;
        Rect rect = new(Screen.width / 2 - 520, 80, 440, 700);
        Vector2 scroll;
        int sel;
        float testAz = 0, testEl = 10, testRange = 500;
        bool showCoverage;
        float[,] coverage; // error in degrees per az/el cell, -1 = not computed
        string coverageKey = "";
        List<TurretLogic.Def> defs = new();
        float nextDetect;

        // Preview result
        Vector3 pvOrigin, pvFwd, pvTarget, pvFinalOrigin, pvFinalFwd;
        float pvError;
        readonly List<(float need, bool clamped)> pvJoints = new();

        void Update()
        {
            if (!Show || EditorLogic.fetch == null || EditorLogic.fetch.ship == null) return;
            if (Time.time < nextDetect) return;
            nextDetect = Time.time + 1f;
            defs = IRServo.Available ? TurretLogic.Detect(EditorLogic.fetch.ship.parts) : new List<TurretLogic.Def>();
            if (sel >= defs.Count) sel = 0;
        }

        string error;

        void OnGUI()
        {
            if (!Show) return;
            rect = AvionicsCore.DrawWindow(WindowID, rect, SafeDraw, "IR turret tool", BDArmorySetup.BDGuiSkin);
            if (Event.current.type == EventType.Repaint && sel < defs.Count && error == null)
                try { Draw3D(defs[sel]); } catch (System.Exception e) { Fail(e); }
        }

        /// <summary>Never let an exception repeat every frame (that's what froze the editor): log once, show it, stop the preview.</summary>
        void SafeDraw(int id)
        {
            if (error != null)
            {
                if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; error = null; return; }
                AvionicsDraw.ColorLabel(new Rect(8, 24, rect.width - 16, 60), "The turret tool hit an error (details in KSP.log):\n" + error, AvionicsDraw.LabelSmall, AvionicsDraw.Hostile);
                if (GUI.Button(new Rect(8, 90, 100, 20), "Retry", BDArmorySetup.ButtonStyle)) error = null;
                GUI.DragWindow(); return;
            }
            try { Draw(id); }
            catch (System.Exception e) { Fail(e); try { GUI.EndScrollView(); } catch { } }
        }

        void Fail(System.Exception e)
        {
            if (e is ExitGUIException) throw e;
            error = e.InnerException?.Message ?? e.Message;
            Debug.LogError($"[BDArmoryAvionics]: IR turret tool: {e}");
        }

        // ---- Settings helpers (copied to symmetry counterparts) ----

        static void ForSym(ModuleIRTurret m, System.Action<ModuleIRTurret> set)
        {
            set(m);
            foreach (var c in m.part.symmetryCounterparts) { var o = c.FindModuleImplementing<ModuleIRTurret>(); if (o != null) set(o); }
            if (EditorLogic.fetch != null) GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
        }

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; return; }
            var btn = BDArmorySetup.ButtonStyle; var on = BDArmorySetup.SelectedButtonStyle; var small = AvionicsDraw.LabelSmall;
            float w = rect.width - 16, y = 24;
            if (!IRServo.Available) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 40), "Infernal Robotics Next isn't installed.", AvionicsDraw.Label, AvionicsDraw.Unknown); GUI.DragWindow(); return; }
            if (defs.Count == 0)
            {
                AvionicsDraw.ColorLabel(new Rect(8, y, w, 60), "No turrets on this craft.\nA turret is one or more rotating IR servos with a gun, missile launcher,\ntargeting pod or HullCam mounted beyond them.", small, AvionicsDraw.ScopeText);
                GUI.DragWindow(); return;
            }

            // Turret tabs
            for (int i = 0; i < defs.Count; i++)
            {
                var d = defs[i];
                if (GUI.Button(new Rect(8 + (i % 4) * 106, y + (i / 4) * 22, 102, 20), $"{i + 1}: {d.KindName} ({d.chain.Count} servo)", i == sel ? on : btn)) { sel = i; coverage = null; }
            }
            y += ((defs.Count + 3) / 4) * 22 + 4;
            var t = defs[sel];
            var s = t.Settings;
            scroll = GUI.BeginScrollView(new Rect(8, y, w, rect.height - y - 6), scroll, new Rect(0, 0, w - 18, 1000));
            w -= 18; y = 0;

            // Aim part
            Head(ref y, w, "AIMED BY");
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), $"{t.aimPart.partInfo.title} ({t.KindName})", small, AvionicsDraw.HudText); y += 18;
            for (int i = 0; i < TurretLogic.AimWithOptions.Length; i++)
            {
                string o = TurretLogic.AimWithOptions[i];
                if (GUI.Button(new Rect(i * (w / 4), y, w / 4 - 4, 20), o, s.aimWith == o ? on : btn)) { ForSym(s, m => m.aimWith = o); nextDetect = 0; }
            }
            y += 24;

            // Servos
            Head(ref y, w, "SERVOS (base first)");
            foreach (var (part, servo, module) in t.chain)
            {
                float min = 0, max = 0, pos = 0;
                try { min = IRServo.Min(servo); max = IRServo.Max(servo); pos = IRServo.Position(servo); } catch { }
                AvionicsDraw.ColorLabel(new Rect(0, y + 3, w - 150, 16), $"{part.partInfo.title}  [{min:F0}..{max:F0}] at {pos:F0}", small, AvionicsDraw.HudText);
                string dirName = module.direction == 0 ? "Dir: auto" : module.direction > 0 ? "Dir: +" : "Dir: -";
                if (GUI.Button(new Rect(w - 146, y, 72, 20), dirName, module.direction != 0 ? on : btn)) ForSym(module, m => m.direction = m.direction == 0 ? 1 : m.direction == 1 ? -1 : 0);
                if (GUI.Button(new Rect(w - 72, y, 72, 20), "Exclude", btn)) { ForSym(module, m => m.aiming = false); nextDetect = 0; }
                y += 22;
            }
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 28), "Dir auto: tested in flight with a 6° nudge. Set + or - to skip the test\n(if the preview below turns the wrong way, flip it).", small, AvionicsDraw.ScopeText); y += 32;

            // Priority
            Head(ref y, w, "AIM AT (first available wins)");
            var pr = TurretLogic.ParsePriority(s.priority);
            for (int i = 0; i < pr.Count; i++)
            {
                var (pid, pon) = pr[i];
                bool nowOn = GUI.Toggle(new Rect(0, y, w - 60, 20), pon, $"{i + 1}. {TurretLogic.SourceNames[pid]}", pon ? on : btn);
                if (nowOn != pon) { pr[i] = (pid, nowOn); string ps = TurretLogic.FormatPriority(pr); ForSym(s, m => m.priority = ps); }
                if (i > 0 && GUI.Button(new Rect(w - 56, y, 26, 20), "↑", btn)) { (pr[i - 1], pr[i]) = (pr[i], pr[i - 1]); string ps = TurretLogic.FormatPriority(pr); ForSym(s, m => m.priority = ps); }
                if (i < pr.Count - 1 && GUI.Button(new Rect(w - 28, y, 26, 20), "↓", btn)) { (pr[i + 1], pr[i]) = (pr[i], pr[i + 1]); string ps = TurretLogic.FormatPriority(pr); ForSym(s, m => m.priority = ps); }
                y += 22;
            }
            if (GUI.Button(new Rect(0, y, 120, 20), "Reset order", btn)) ForSym(s, m => m.priority = TurretLogic.DefaultPriority);
            y += 26;

            // Motion
            Head(ref y, w, "MOTION");
            float v;
            v = Slider(ref y, w, $"Slew speed {s.slewLimit:F0}% of servo max", s.slewLimit, 10, 100); if (v != s.slewLimit) { float vv = Mathf.Round(v / 5) * 5; ForSym(s, m => m.slewLimit = vv); }
            v = Slider(ref y, w, $"Start slowing {s.slowAngle:F0}° from target (higher = smoother)", s.slowAngle, 2, 60); if (v != s.slowAngle) { float vv = Mathf.Round(v); ForSym(s, m => m.slowAngle = vv); }
            v = Slider(ref y, w, $"Deadband {s.deadband:F2}° (higher = less twitch)", s.deadband, 0.05f, 2f); if (v != s.deadband) { float vv = Mathf.Round(v * 20) / 20; ForSym(s, m => m.deadband = vv); }
            v = Slider(ref y, w, s.maxRangeKm <= 0 ? "Max target range: no limit" : $"Max target range {s.maxRangeKm:F1} km", s.maxRangeKm, 0, 20); if (v != s.maxRangeKm) { float vv = Mathf.Round(v * 2) / 2; ForSym(s, m => m.maxRangeKm = vv); }
            bool rest = GUI.Toggle(new Rect(0, y, w, 20), s.restWhenIdle, s.restWhenIdle ? "No target: return to rest position" : "No target: hold where it is", s.restWhenIdle ? on : btn);
            if (rest != s.restWhenIdle) ForSym(s, m => m.restWhenIdle = rest);
            y += 26;

            // Preview
            Head(ref y, w, "AIM PREVIEW (from the craft's nose, in the editor)");
            testAz = Slider(ref y, w, $"Test bearing {testAz:F0}° (0 = nose, 90 = right)", testAz, -180, 180);
            testEl = Slider(ref y, w, $"Test elevation {testEl:F0}°", testEl, -60, 90);
            testRange = Slider(ref y, w, $"Test range {testRange:F0} m", testRange, 50, 3000);
            Preview(t);
            for (int i = 0; i < t.chain.Count && i < pvJoints.Count; i++)
            {
                var (need, clamped) = pvJoints[i];
                AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), $"{t.chain[i].part.partInfo.title}: needs {need:F0}{(clamped ? "  LIMIT" : "")}", small, clamped ? AvionicsDraw.Unknown : AvionicsDraw.HudText);
                y += 16;
            }
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), pvError < 2f ? $"On target (error {pvError:F1}°)" : $"Can't reach: {pvError:F0}° off (servo limits or geometry)", small, pvError < 2f ? AvionicsDraw.Friendly : AvionicsDraw.Hostile);
            y += 18;
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), "In the editor: yellow = servo axes, cyan = aim now, green/red = aim needed.", small, AvionicsDraw.ScopeText); y += 20;

            if (GUI.Button(new Rect(0, y, 160, 20), showCoverage ? "Hide coverage" : "Show coverage map", showCoverage ? on : btn)) { showCoverage = !showCoverage; coverage = null; }
            y += 24;
            if (showCoverage) { DrawCoverage(t, new Rect(0, y, w, 150)); y += 170; }

            GUI.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, rect.width - 22, 20));
        }

        static void Head(ref float y, float w, string text) { AvionicsDraw.ColorLabel(new Rect(0, y, w, 14), text, AvionicsDraw.Header, AvionicsDraw.HudText); y += 16; }

        static float Slider(ref float y, float w, string label, float value, float min, float max)
        {
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 14), label, AvionicsDraw.LabelSmall, AvionicsDraw.HudText);
            float v = GUI.HorizontalSlider(new Rect(0, y + 15, w, 14), value, min, max);
            y += 32;
            return v;
        }

        // ---- Virtual aiming: rotate the geometry (not the parts) to see what the servos would do ----

        static Vector3 CraftDir(float az, float el)
        {
            var root = EditorLogic.RootPart != null ? EditorLogic.RootPart.transform : null;
            if (root == null) return Vector3.forward;
            Vector3 nose = root.up, top = -root.forward, right = root.right;
            Quaternion q = Quaternion.AngleAxis(az, top) * Quaternion.AngleAxis(-el, right);
            return (q * nose).normalized;
        }

        /// <summary>Iterative per-joint solve (as in flight), within each servo's limits. Returns error in degrees.</summary>
        static float Solve(TurretLogic.Def t, Vector3 target, List<(float need, bool clamped)> outJoints, out Vector3 o, out Vector3 f)
        {
            int n = t.chain.Count;
            var piv = new Vector3[n]; var ax = new Vector3[n]; var th = new float[n];
            var pos0 = new float[n]; var lo = new float[n]; var hi = new float[n]; var sign = new float[n];
            for (int i = 0; i < n; i++)
            {
                var (part, servo, module) = t.chain[i];
                piv[i] = part.transform.position; ax[i] = IRServo.WorldAxis(servo);
                sign[i] = module.direction == 0 ? 1 : module.direction;
                try { pos0[i] = IRServo.Position(servo); lo[i] = IRServo.Min(servo); hi[i] = IRServo.Max(servo); } catch { lo[i] = -180; hi[i] = 180; }
                if (hi[i] - lo[i] >= 359f || hi[i] <= lo[i]) { lo[i] = float.NegativeInfinity; hi[i] = float.PositiveInfinity; }
            }
            TurretLogic.AimRay(t.aimPart, out Vector3 o0, out Vector3 f0);
            void FK(out Vector3 oo, out Vector3 ff, Vector3[] p, Vector3[] a)
            {
                for (int i = 0; i < n; i++) { p[i] = piv[i]; a[i] = ax[i]; }
                oo = o0; ff = f0;
                for (int k = 0; k < n; k++)
                {
                    var R = Quaternion.AngleAxis(th[k], a[k]);
                    for (int i = k + 1; i < n; i++) { p[i] = p[k] + R * (p[i] - p[k]); a[i] = R * a[i]; }
                    oo = p[k] + R * (oo - p[k]); ff = R * ff;
                }
            }
            var P = new Vector3[n]; var A = new Vector3[n];
            o = o0; f = f0;
            for (int it = 0; it < 40; it++)
                for (int j = 0; j < n; j++)
                {
                    FK(out o, out f, P, A);
                    Vector3 d = target - o;
                    Vector3 fp = Vector3.ProjectOnPlane(f, A[j]), dp = Vector3.ProjectOnPlane(d, A[j]);
                    if (fp.sqrMagnitude < 1e-6f || dp.sqrMagnitude < 1e-6f) continue;
                    th[j] += Vector3.SignedAngle(fp, dp, A[j]);
                    // servo position = pos0 + sign * angle, kept inside its limits
                    float posNew = Mathf.Clamp(pos0[j] + sign[j] * th[j], lo[j], hi[j]);
                    th[j] = (posNew - pos0[j]) * sign[j];
                }
            FK(out o, out f, P, A);
            outJoints?.Clear();
            for (int j = 0; j < n; j++)
            {
                float need = pos0[j] + sign[j] * th[j];
                outJoints?.Add((need, !float.IsInfinity(lo[j]) && (need <= lo[j] + 0.01f || need >= hi[j] - 0.01f)));
            }
            return Vector3.Angle(f, target - o);
        }

        void Preview(TurretLogic.Def t)
        {
            TurretLogic.AimRay(t.aimPart, out pvOrigin, out pvFwd);
            pvTarget = pvOrigin + CraftDir(testAz, testEl) * testRange;
            pvError = Solve(t, pvTarget, pvJoints, out pvFinalOrigin, out pvFinalFwd);
        }

        void DrawCoverage(TurretLogic.Def t, Rect r)
        {
            const int nx = 24, ny = 10; // 15° cells, az -180..180, el -60..90
            if (coverage == null || coverageKey != t.Key + string.Join(",", t.chain.ConvertAll(c => c.module.direction)))
            {
                coverage = new float[nx, ny];
                TurretLogic.AimRay(t.aimPart, out Vector3 o, out _);
                for (int i = 0; i < nx; i++)
                    for (int j = 0; j < ny; j++)
                        coverage[i, j] = Solve(t, o + CraftDir(-180 + 15 * i + 7.5f, -60 + 15 * j + 7.5f) * 500f, null, out _, out _);
                coverageKey = t.Key + string.Join(",", t.chain.ConvertAll(c => c.module.direction));
            }
            float cw = r.width / nx, ch = r.height / ny;
            var old = GUI.color;
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    float e = coverage[i, j];
                    GUI.color = e < 2 ? new Color(0.3f, 0.85f, 0.4f, 0.8f) : e < 10 ? new Color(0.95f, 0.8f, 0.3f, 0.8f) : new Color(0.85f, 0.3f, 0.25f, 0.8f);
                    GUI.DrawTexture(new Rect(r.x + i * cw + 1, r.y + (ny - 1 - j) * ch + 1, cw - 2, ch - 2), Texture2D.whiteTexture);
                }
            GUI.color = old;
            // test direction marker
            float mx = r.x + (testAz + 180) / 360f * r.width, my = r.y + (1 - (testEl + 60) / 150f) * r.height;
            GUI.Label(new Rect(mx - 5, my - 9, 12, 18), "+");
            AvionicsDraw.ColorLabel(new Rect(r.x, r.yMax + 2, r.width, 14), "bearing -180 … 0 (nose) … 180 → · elevation -60 (bottom) … 90 (top) · green = reachable", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
        }

        // ---- 3D lines in the editor ----

        void Draw3D(TurretLogic.Def t)
        {
            var cam = EditorCamera.Instance != null ? EditorCamera.Instance.cam : Camera.main;
            if (cam == null || !AvionicsDraw.Begin()) return;
            bool S(Vector3 wp, out Vector2 p) { var s = cam.WorldToScreenPoint(wp); p = new Vector2(s.x, Screen.height - s.y); return s.z > 0; }
            void L(Vector3 a, Vector3 b, float wdt, Color c) { if (S(a, out var pa) && S(b, out var pb)) AvionicsDraw.Line(pa, pb, wdt, c); }
            foreach (var (part, servo, _) in t.chain)
            {
                Vector3 p = part.transform.position, a = IRServo.WorldAxis(servo);
                L(p - a * 1.2f, p + a * 1.2f, 3, new Color(1f, 0.85f, 0.2f));
            }
            L(pvOrigin, pvOrigin + pvFwd * 12f, 2, Color.cyan);
            Color need = pvError < 2f ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.3f, 0.25f);
            L(pvFinalOrigin, pvFinalOrigin + pvFinalFwd * 12f, 2.5f, need);
            L(pvFinalOrigin + pvFinalFwd * 12f, pvTarget, 1, new Color(need.r, need.g, need.b, 0.35f));
            if (S(pvTarget, out var tp)) AvionicsDraw.Circle(tp, 8, 2, need, 16);
            AvionicsDraw.End();
        }
    }
}
