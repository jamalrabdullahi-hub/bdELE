using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// VAB/SPH IR turret tool. Each servo is measured by moving it a little and watching the mounted part turn, so the axis,
    /// direction and gearing are real, not guessed. Then: jog servos by hand, aim the turret at test directions (the parts move),
    /// see a coverage map, and set what it aims at and how it moves. Everything is saved with the craft and used in flight.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorTurretWindow : MonoBehaviour
    {
        public static bool Show;
        const int WindowID = 71730362;
        Rect rect = new(Screen.width / 2 - 520, 80, 440, 720);
        Vector2 scroll;
        int sel;
        List<TurretLogic.Def> defs = new();
        float nextDetect;
        string error;

        // Pose: what we commanded each servo to (IR's editor position isn't always reported back), and where it started.
        readonly Dictionary<PartModule, float> pose = new();
        readonly Dictionary<PartModule, float> original = new();
        bool measuring;
        string measureNote = "";

        // Aim test
        float testAz = 0, testEl = 10, testRange = 500;
        bool moveParts = true;
        string lastApplied = "";
        float pvError = -1;
        int refine;
        string curKey = "";
        readonly List<(float need, bool limit)> pvJoints = new();
        Vector3 pvTarget, pvOrigin, pvFwd;

        // Sections
        bool openAimAt, openMotion, openCoverage;
        float[,] coverage;
        string coverageKey = "";

        // ---- Input lock: dragging a slider mustn't orbit the editor camera ----
        const string LockId = "ELE_TurretTool";
        bool locked;
        void LockEditor()
        {
            Vector2 m = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool over = Show && rect.Contains(m);
            if (over == locked || EditorLogic.fetch == null) return;
            if (over) EditorLogic.fetch.Lock(true, true, true, LockId); else EditorLogic.fetch.Unlock(LockId);
            locked = over;
        }

        void OnDestroy() { ResetPose(); if (locked && EditorLogic.fetch != null) EditorLogic.fetch.Unlock(LockId); }

        bool wasShown;
        void Update()
        {
            LockEditor();
            if (wasShown && !Show) ResetPose(); // closing the tool puts the turret back as built
            wasShown = Show;
            if (!Show || EditorLogic.fetch == null || EditorLogic.fetch.ship == null) return;
            if (Time.time < nextDetect) return;
            nextDetect = Time.time + 1f;
            defs = IRServo.Available ? TurretLogic.Detect(EditorLogic.fetch.ship.parts) : new List<TurretLogic.Def>();
            if (sel >= defs.Count) sel = 0;
        }

        void OnGUI()
        {
            if (!Show) return;
            rect = AvionicsCore.DrawWindow(WindowID, rect, SafeDraw, "IR turret tool", BDArmorySetup.BDGuiSkin);
            if (Event.current.type == EventType.Repaint && sel < defs.Count && error == null)
                try { Draw3D(defs[sel]); } catch (System.Exception e) { Fail(e); }
        }

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

        // ---- Pose helpers ----

        /// <summary>The servo's real editor position (read back every time: IR may clamp what we ask for).</summary>
        float Pos(PartModule s)
        {
            float v = 0; try { v = IRServo.EditorPosition(s); } catch { if (pose.TryGetValue(s, out var c)) v = c; }
            if (!original.ContainsKey(s)) original[s] = v;
            return v;
        }

        void SetPos(PartModule s, float v)
        {
            Pos(s); // records the original
            if (IRServo.Continuous(s)) v = Mathf.Repeat(v, 360f); // full rotation: 0..360, 361 = 1
            else v = Mathf.Clamp(v, IRServo.Min(s), IRServo.Max(s));
            IRServo.EditorSetTo(s, v);
            pose[s] = v;
            coverage = null;
        }

        void ResetPose()
        {
            foreach (var kv in original) if (kv.Key != null) try { IRServo.EditorSetTo(kv.Key, kv.Value); } catch { }
            original.Clear(); pose.Clear(); lastApplied = "";
        }

        // ---- Measuring (moves each servo 10 units and back) ----

        IEnumerator Measure(TurretLogic.Def t)
        {
            measuring = true; measureNote = "";
            var reference = EditorLogic.RootPart != null ? EditorLogic.RootPart.transform : t.chain[0].part.transform;
            int ok = 0;
            foreach (var (part, servo, module) in t.chain)
            {
                float p0 = Pos(servo), max = IRServo.Max(servo), min = IRServo.Min(servo);
                float p1 = p0 + ((max > min && p0 + 10f > max) ? -10f : 10f);
                var a = ServoKin.Take(servo, t.aimPart, reference); a.pos = p0;
                IRServo.EditorSetTo(servo, p1);
                yield return null; yield return null;
                var b = ServoKin.Take(servo, t.aimPart, reference); b.pos = p1;
                IRServo.EditorSetTo(servo, p0);
                yield return null;
                if (ServoKin.Solve(module, servo, t.aimPart, reference, a, b, out string why)) ok++;
                else measureNote += $"{part.partInfo.title}: {why}. ";
            }
            measureNote = ok == t.chain.Count ? $"All {ok} servos measured." : $"Measured {ok}/{t.chain.Count}. " + measureNote;
            coverage = null; lastApplied = ""; measuring = false;
            if (EditorLogic.fetch != null) GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
        }

        static void ForSym(ModuleIRTurret m, System.Action<ModuleIRTurret> set)
        {
            set(m);
            foreach (var c in m.part.symmetryCounterparts) { var o = c.FindModuleImplementing<ModuleIRTurret>(); if (o != null) set(o); }
        }

        // ---- Window ----

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; return; }
            var btn = BDArmorySetup.ButtonStyle; var on = BDArmorySetup.SelectedButtonStyle; var small = AvionicsDraw.LabelSmall;
            float W = rect.width - 16, y = 24;
            if (!IRServo.Available) { AvionicsDraw.ColorLabel(new Rect(8, y, W, 40), "Infernal Robotics Next isn't installed.", AvionicsDraw.Label, AvionicsDraw.Unknown); GUI.DragWindow(); return; }
            if (defs.Count == 0)
            {
                AvionicsDraw.ColorLabel(new Rect(8, y, W, 60), "No turrets on this craft.\nA turret = rotating IR servos with a gun, missile launcher,\ntargeting pod or HullCam mounted beyond them.", small, AvionicsDraw.ScopeText);
                GUI.DragWindow(); return;
            }
            for (int i = 0; i < defs.Count; i++)
                if (GUI.Button(new Rect(8 + (i % 4) * 106, y + (i / 4) * 22, 102, 20), $"{i + 1}: {defs[i].KindName} ({defs[i].chain.Count})", i == sel ? on : btn)) { ResetPose(); sel = i; coverage = null; measureNote = ""; }
            y += ((defs.Count + 3) / 4) * 22 + 4;
            var t = defs[sel];
            var s = t.Settings;

            scroll = GUI.BeginScrollView(new Rect(8, y, W, rect.height - y - 6), scroll, new Rect(0, 0, W - 18, 1100));
            float w = W - 18; y = 0;

            // 1. Measure
            int measured = t.chain.FindAll(c => c.module.Measured).Count;
            Head(ref y, w, $"1. SERVOS  ({measured}/{t.chain.Count} measured)");
            if (GUI.Button(new Rect(0, y, 150, 22), measuring ? "Measuring…" : measured == t.chain.Count ? "Re-measure" : "Measure servos", measured < t.chain.Count ? on : btn) && !measuring)
                StartCoroutine(Measure(t));
            if (GUI.Button(new Rect(154, y, 110, 22), "Reset pose", btn)) ResetPose();
            y += 24;
            if (measureNote != "") { AvionicsDraw.ColorLabel(new Rect(0, y, w, 30), measureNote, small, measureNote.StartsWith("All") ? AvionicsDraw.Friendly : AvionicsDraw.Unknown); y += 30; }
            else if (measured < t.chain.Count) { AvionicsDraw.ColorLabel(new Rect(0, y, w, 30), "Measure first: each servo moves a little and back, and the tool\nlearns its real axis and direction (no guessing).", small, AvionicsDraw.ScopeText); y += 30; }

            foreach (var (part, servo, module) in t.chain)
            {
                float min = IRServo.Min(servo), max = IRServo.Max(servo), p = Pos(servo);
                bool limited = !IRServo.Continuous(servo);
                if (!limited) p = Mathf.Repeat(p, 360f);
                string kin = module.Measured ? $"{module.calGain:F2}°/unit" : "not measured";
                string range = limited ? $"[{min:F0}..{max:F0}]" : "[0..360 wrap]";
                AvionicsDraw.ColorLabel(new Rect(0, y, w - 70, 16), $"{part.partInfo.title}  {p:F0} {range}  {kin}", small, module.Measured ? AvionicsDraw.HudText : AvionicsDraw.Unknown);
                if (GUI.Button(new Rect(w - 66, y - 2, 66, 18), "Exclude", btn)) { ForSym(module, m => m.aiming = false); nextDetect = 0; }
                y += 16;
                float lo = limited ? min : 0, hi = limited ? max : 360;
                float np = GUI.HorizontalSlider(new Rect(0, y + 2, w - 70, 14), p, lo, hi);
                if (GUI.Button(new Rect(w - 66, y, 32, 18), "-", btn)) np = p - 5;
                if (GUI.Button(new Rect(w - 32, y, 32, 18), "+", btn)) np = p + 5;
                if (Mathf.Abs(np - p) > 0.05f) { SetPos(servo, Mathf.Round(np)); lastApplied = curKey; refine = 99; } // keep the hand pose until the aim test changes
                y += 24;
            }
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), "Drag a slider to move that servo (the parts move). Closing the tool resets the pose.", small, AvionicsDraw.ScopeText);
            y += 22;

            // 2. Aim test
            Head(ref y, w, "2. AIM TEST (the turret points the parts at it)");
            string[] presets = { "Nose", "Left", "Right", "Tail", "Up", "Down" };
            float[,] pv = { { 0, 5 }, { -90, 5 }, { 90, 5 }, { 180, 5 }, { 0, 85 }, { 0, -30 } };
            for (int i = 0; i < presets.Length; i++)
                if (GUI.Button(new Rect(i * (w / 6), y, w / 6 - 3, 20), presets[i], btn)) { testAz = pv[i, 0]; testEl = pv[i, 1]; }
            y += 24;
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), DirsNote(s), small, s.refNose == Vector3.zero ? AvionicsDraw.Unknown : AvionicsDraw.Friendly); y += 18;
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), "Point the turret with the sliders above, then:", small, AvionicsDraw.ScopeText); y += 18;
            if (GUI.Button(new Rect(0, y, w / 3 - 3, 20), "This is NOSE", btn)) DefineDir(t, true);
            if (GUI.Button(new Rect(w / 3, y, w / 3 - 3, 20), "This is UP", btn)) DefineDir(t, false);
            if (GUI.Button(new Rect(2 * w / 3, y, w / 3, 20), "Reset directions", btn)) { ForSym(s, m => { m.refNose = Vector3.zero; m.refUp = Vector3.zero; }); lastApplied = ""; coverage = null; }
            y += 26;
            testAz = Mathf.Round(Slider(ref y, w, $"Bearing {testAz:F0}° (0 nose, + right)", testAz, -180, 180));
            testEl = Mathf.Round(Slider(ref y, w, $"Elevation {testEl:F0}°", testEl, -60, 90));
            testRange = Mathf.Round(Slider(ref y, w, $"Range {testRange:F0} m", testRange, 50, 3000) / 10) * 10;
            moveParts = GUI.Toggle(new Rect(0, y, w, 20), moveParts, moveParts ? "Move the parts to the solution: ON" : "Move the parts to the solution: OFF (lines only)", moveParts ? on : btn);
            y += 24;
            if (measured < t.chain.Count) { AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), "Measure the servos first.", small, AvionicsDraw.Unknown); y += 20; }
            else
            {
                Preview(t);
                for (int i = 0; i < pvJoints.Count; i++)
                {
                    AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), $"{t.chain[i].part.partInfo.title}: {pvJoints[i].need:F0}{(pvJoints[i].limit ? "  AT LIMIT" : "")}", small, pvJoints[i].limit ? AvionicsDraw.Unknown : AvionicsDraw.HudText);
                    y += 16;
                }
                AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), pvError < 2f ? $"ON TARGET (error {pvError:F1}°)" : $"CAN'T REACH: {pvError:F0}° off (servo limits)", AvionicsDraw.Label, pvError < 2f ? AvionicsDraw.Friendly : AvionicsDraw.Hostile);
                y += 22;
            }

            // 3. Coverage
            if (Fold(ref y, w, "3. COVERAGE MAP", ref openCoverage))
            {
                if (measured < t.chain.Count) { AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), "Measure the servos first.", small, AvionicsDraw.Unknown); y += 20; }
                else { DrawCoverage(t, new Rect(0, y, w, 140)); y += 162; }
            }

            // 4. Aimed by / aim at
            if (Fold(ref y, w, "4. WHAT IT AIMS AT", ref openAimAt))
            {
                AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), $"Aimed by: {t.aimPart.partInfo.title} ({t.KindName})", small, AvionicsDraw.HudText); y += 18;
                for (int i = 0; i < TurretLogic.AimWithOptions.Length; i++)
                {
                    string o = TurretLogic.AimWithOptions[i];
                    if (GUI.Button(new Rect(i * (w / 4), y, w / 4 - 4, 20), o, s.aimWith == o ? on : btn)) { ForSym(s, m => m.aimWith = o); nextDetect = 0; }
                }
                y += 26;
                var pr = TurretLogic.ParsePriority(s.priority);
                for (int i = 0; i < pr.Count; i++)
                {
                    var (pid, pon) = pr[i];
                    bool nowOn = GUI.Toggle(new Rect(0, y, w - 60, 20), pon, $"{i + 1}. {TurretLogic.SourceNames[pid]}", pon ? on : btn);
                    bool changed = false;
                    if (nowOn != pon) { pr[i] = (pid, nowOn); changed = true; }
                    if (i > 0 && GUI.Button(new Rect(w - 56, y, 26, 20), "↑", btn)) { (pr[i - 1], pr[i]) = (pr[i], pr[i - 1]); changed = true; }
                    if (i < pr.Count - 1 && GUI.Button(new Rect(w - 28, y, 26, 20), "↓", btn)) { (pr[i + 1], pr[i]) = (pr[i], pr[i + 1]); changed = true; }
                    if (changed) { string ps = TurretLogic.FormatPriority(pr); ForSym(s, m => m.priority = ps); }
                    y += 22;
                }
                if (GUI.Button(new Rect(0, y, 110, 20), "Reset order", btn)) ForSym(s, m => m.priority = TurretLogic.DefaultPriority);
                y += 26;
            }

            // 5. Motion
            if (Fold(ref y, w, "5. MOTION", ref openMotion))
            {
                float v;
                v = Slider(ref y, w, $"Slew speed {s.slewLimit:F0}% of servo max", s.slewLimit, 10, 100); if (Mathf.Abs(v - s.slewLimit) > 0.5f) { float vv = Mathf.Round(v / 5) * 5; ForSym(s, m => m.slewLimit = vv); }
                v = Slider(ref y, w, $"Start slowing {s.slowAngle:F0}° out (higher = smoother)", s.slowAngle, 2, 60); if (Mathf.Abs(v - s.slowAngle) > 0.5f) { float vv = Mathf.Round(v); ForSym(s, m => m.slowAngle = vv); }
                v = Slider(ref y, w, $"Deadband {s.deadband:F2}° (higher = less twitch)", s.deadband, 0.05f, 2f); if (Mathf.Abs(v - s.deadband) > 0.02f) { float vv = Mathf.Round(v * 20) / 20; ForSym(s, m => m.deadband = vv); }
                v = Slider(ref y, w, s.maxRangeKm <= 0 ? "Max target range: no limit" : $"Max target range {s.maxRangeKm:F1} km", s.maxRangeKm, 0, 20); if (Mathf.Abs(v - s.maxRangeKm) > 0.2f) { float vv = Mathf.Round(v * 2) / 2; ForSym(s, m => m.maxRangeKm = vv); }
                bool rest = GUI.Toggle(new Rect(0, y, w, 20), s.restWhenIdle, s.restWhenIdle ? "No target: return to rest" : "No target: hold position", s.restWhenIdle ? on : btn);
                if (rest != s.restWhenIdle) ForSym(s, m => m.restWhenIdle = rest);
                y += 26;
            }
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 30), "In the editor: yellow = servo axes (measured), cyan = where it points,\ngreen/red circle = test target.", small, AvionicsDraw.ScopeText);

            GUI.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, rect.width - 22, 20));
        }

        static void Head(ref float y, float w, string text) { AvionicsDraw.ColorLabel(new Rect(0, y, w, 14), text, AvionicsDraw.Header, AvionicsDraw.HudText); y += 18; }

        static bool Fold(ref float y, float w, string text, ref bool open)
        {
            if (GUI.Button(new Rect(0, y, w, 20), (open ? "▼ " : "► ") + text, BDArmorySetup.ButtonStyle)) open = !open;
            y += 24;
            return open;
        }

        static float Slider(ref float y, float w, string label, float value, float min, float max)
        {
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 14), label, AvionicsDraw.LabelSmall, AvionicsDraw.HudText);
            float v = GUI.HorizontalSlider(new Rect(0, y + 15, w, 14), value, min, max);
            y += 32;
            return v;
        }

        // ---- Solving with measured kinematics ----

        static Transform Root => EditorLogic.RootPart != null ? EditorLogic.RootPart.transform : null;

        /// <summary>The craft's nose and up as defined in the tool (or the root part's own).</summary>
        static void Dirs(ModuleIRTurret s, out Vector3 nose, out Vector3 up)
        {
            var root = Root;
            if (root == null) { nose = Vector3.forward; up = Vector3.up; return; }
            nose = s.refNose != Vector3.zero ? root.TransformDirection(s.refNose) : root.up;
            up = s.refUp != Vector3.zero ? root.TransformDirection(s.refUp) : -root.forward;
            up = Vector3.ProjectOnPlane(up, nose);
            if (up.sqrMagnitude < 1e-4f) up = Vector3.ProjectOnPlane(-root.forward, nose);
            if (up.sqrMagnitude < 1e-4f) up = Vector3.ProjectOnPlane(root.right, nose);
            nose.Normalize(); up.Normalize();
        }

        static Vector3 CraftDir(ModuleIRTurret s, float az, float el)
        {
            Dirs(s, out Vector3 nose, out Vector3 up);
            Vector3 right = Vector3.Cross(up, nose).normalized;
            return (Quaternion.AngleAxis(az, up) * Quaternion.AngleAxis(-el, right) * nose).normalized;
        }

        static string DirsNote(ModuleIRTurret s) =>
            s.refNose == Vector3.zero ? "Directions: from the root part (set them below if Nose/Up look wrong)" :
            s.refUp == Vector3.zero ? "Directions: NOSE set, UP from the root part" : "Directions: NOSE and UP set by you";

        void DefineDir(TurretLogic.Def t, bool nose)
        {
            var root = Root;
            if (root == null) return;
            TurretLogic.AimRay(t.aimPart, out _, out Vector3 f);
            Vector3 local = root.InverseTransformDirection(f).normalized;
            ForSym(t.Settings, m => { if (nose) m.refNose = local; else m.refUp = local; });
            lastApplied = ""; coverage = null;
        }

        /// <summary>Servo positions that point the aim part at the target, inside the limits (per-joint iterative solve, as in flight).</summary>
        float Solve(TurretLogic.Def t, Vector3 target, List<(float need, bool limit)> result)
        {
            int n = t.chain.Count;
            var piv = new Vector3[n]; var ax = new Vector3[n]; var gain = new float[n]; var th = new float[n];
            var pos0 = new float[n]; var lo = new float[n]; var hi = new float[n];
            for (int i = 0; i < n; i++)
            {
                var (_, servo, module) = t.chain[i];
                ServoKin.Get(servo, module, out ax[i], out piv[i], out gain[i]);
                pos0[i] = Pos(servo); lo[i] = IRServo.Min(servo); hi[i] = IRServo.Max(servo);
                if (hi[i] - lo[i] >= 359f || hi[i] <= lo[i]) { lo[i] = float.NegativeInfinity; hi[i] = float.PositiveInfinity; }
            }
            TurretLogic.AimRay(t.aimPart, out Vector3 o0, out Vector3 f0);
            var P = new Vector3[n]; var A = new Vector3[n];
            void FK(out Vector3 o, out Vector3 f)
            {
                for (int i = 0; i < n; i++) { P[i] = piv[i]; A[i] = ax[i]; }
                o = o0; f = f0;
                for (int k = 0; k < n; k++)
                {
                    var R = Quaternion.AngleAxis(th[k], A[k]);
                    for (int i = k + 1; i < n; i++) { P[i] = P[k] + R * (P[i] - P[k]); A[i] = R * A[i]; }
                    o = P[k] + R * (o - P[k]); f = R * f;
                }
            }
            Vector3 oo, ff;
            for (int it = 0; it < 40; it++)
                for (int j = 0; j < n; j++)
                {
                    FK(out oo, out ff);
                    Vector3 fp = Vector3.ProjectOnPlane(ff, A[j]), dp = Vector3.ProjectOnPlane(target - oo, A[j]);
                    if (fp.sqrMagnitude < 1e-6f || dp.sqrMagnitude < 1e-6f) continue;
                    th[j] += Vector3.SignedAngle(fp, dp, A[j]);
                    float p = Mathf.Clamp(pos0[j] + th[j] / gain[j], lo[j], hi[j]);
                    th[j] = (p - pos0[j]) * gain[j];
                }
            FK(out oo, out ff);
            result?.Clear();
            for (int j = 0; j < n; j++)
            {
                float need = pos0[j] + th[j] / gain[j];
                result?.Add((need, !float.IsInfinity(lo[j]) && (need <= lo[j] + 0.5f || need >= hi[j] - 0.5f)));
            }
            return Vector3.Angle(ff, target - oo);
        }

        void Preview(TurretLogic.Def t)
        {
            TurretLogic.AimRay(t.aimPart, out pvOrigin, out pvFwd);
            pvTarget = pvOrigin + CraftDir(t.Settings, testAz, testEl) * testRange;
            string key = $"{sel}|{testAz}|{testEl}|{testRange}|{moveParts}|{t.Settings.refNose}|{t.Settings.refUp}";
            curKey = key;
            if (key != lastApplied) { refine = 0; }
            else
            {
                pvError = Vector3.Angle(pvFwd, pvTarget - pvOrigin); // what the real parts give
                if (!moveParts || pvError < 0.3f || refine >= 8) return;
            }
            // Solve from where the servos really are; repeat on the moved parts until they're on target (corrects any model error).
            float predicted = Solve(t, pvTarget, pvJoints);
            if (!moveParts) { pvError = predicted; lastApplied = key; return; }
            if (Event.current.type == EventType.Repaint)
            {
                for (int i = 0; i < t.chain.Count; i++) SetPos(t.chain[i].servo, pvJoints[i].need);
                lastApplied = key; refine++;
            }
        }

        void DrawCoverage(TurretLogic.Def t, Rect r)
        {
            const int nx = 24, ny = 10; // 15° cells: bearing -180..180, elevation -60..90
            string key = t.Key + string.Join(",", t.chain.ConvertAll(c => c.module.calGain.ToString("F2") + c.module.calFrame));
            if (coverage == null || coverageKey != key)
            {
                coverage = new float[nx, ny];
                TurretLogic.AimRay(t.aimPart, out Vector3 o, out _);
                for (int i = 0; i < nx; i++)
                    for (int j = 0; j < ny; j++)
                        coverage[i, j] = Solve(t, o + CraftDir(t.Settings, -180 + 15 * i + 7.5f, -60 + 15 * j + 7.5f) * 500f, null);
                coverageKey = key;
            }
            float cw = r.width / nx, ch = r.height / ny;
            var old = GUI.color;
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    float e = coverage[i, j];
                    GUI.color = e < 2 ? new Color(0.3f, 0.85f, 0.4f, 0.85f) : e < 10 ? new Color(0.95f, 0.8f, 0.3f, 0.85f) : new Color(0.85f, 0.3f, 0.25f, 0.85f);
                    GUI.DrawTexture(new Rect(r.x + i * cw + 1, r.y + (ny - 1 - j) * ch + 1, cw - 2, ch - 2), Texture2D.whiteTexture);
                }
            GUI.color = old;
            float mx = r.x + (testAz + 180) / 360f * r.width, my = r.y + (1 - (testEl + 60) / 150f) * r.height;
            GUI.Label(new Rect(mx - 5, my - 9, 12, 18), "+");
            AvionicsDraw.ColorLabel(new Rect(r.x, r.yMax + 2, r.width, 14), "← tail · left · NOSE · right · tail →   bottom -60° … top 90°   green = reachable", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
        }

        void Draw3D(TurretLogic.Def t)
        {
            var cam = EditorCamera.Instance != null ? EditorCamera.Instance.cam : Camera.main;
            if (cam == null || !AvionicsDraw.Begin()) return;
            bool S(Vector3 wp, out Vector2 p) { var sp = cam.WorldToScreenPoint(wp); p = new Vector2(sp.x, Screen.height - sp.y); return sp.z > 0; }
            void L(Vector3 a, Vector3 b, float wd, Color c) { if (S(a, out var pa) && S(b, out var pb)) AvionicsDraw.Line(pa, pb, wd, c); }
            foreach (var (part, servo, module) in t.chain)
            {
                if (!ServoKin.Get(servo, module, out Vector3 a, out Vector3 p, out _)) continue;
                L(p - a * 1.5f, p + a * 1.5f, 3, new Color(1f, 0.85f, 0.2f));
                L(p + a * 1.5f, p + a * 1.2f + Vector3.Cross(a, cam.transform.forward).normalized * 0.2f, 3, new Color(1f, 0.85f, 0.2f)); // arrow: + direction
            }
            TurretLogic.AimRay(t.aimPart, out Vector3 o, out Vector3 f);
            L(o, o + f * 15f, 2, Color.cyan);
            if (pvError >= 0)
            {
                Color c = pvError < 2f ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.3f, 0.25f);
                L(o + f * 15f, pvTarget, 1, new Color(c.r, c.g, c.b, 0.35f));
                if (S(pvTarget, out var tp)) AvionicsDraw.Circle(tp, 8, 2, c, 16);
            }
            AvionicsDraw.End();
        }
    }
}
