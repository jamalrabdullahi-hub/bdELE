using System.Collections.Generic;
using UnityEngine;

using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// VAB/SPH VTOL designer: switch the flight computer on, tell it which way is up and forward, check the hover balance
    /// (thrust-to-weight, centre of thrust vs centre of mass, one-click balancing), set up Infernal Robotics tilt servos
    /// (hover / cruise positions, jogged live), and choose how it flies (presets and sliders). Saved with the craft.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorVTOLWindow : MonoBehaviour
    {
        public static bool Show;
        const int WindowID = 71730363;
        Rect rect = new(Screen.width / 2 - 80, 80, 460, 760);
        Vector2 scroll;
        string error;
        bool openTilt = true, openFly = true, openEngines;

        // analysis
        float mass, twr, cotOffset, vertThrust;
        Vector3 com, cot;
        readonly List<(ModuleEngines e, float thrust, bool lift)> engines = new();
        float nextAnalyse;

        const string LockId = "ELE_VTOLDesigner";
        bool locked;

        void LockEditor()
        {
            Vector2 m = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool over = Show && rect.Contains(m);
            if (over == locked || EditorLogic.fetch == null) return;
            if (over) EditorLogic.fetch.Lock(true, true, true, LockId); else EditorLogic.fetch.Unlock(LockId);
            locked = over;
        }

        void OnDestroy() { if (locked && EditorLogic.fetch != null) EditorLogic.fetch.Unlock(LockId); }

        static List<Part> Parts => EditorLogic.fetch != null && EditorLogic.fetch.ship != null ? EditorLogic.fetch.ship.parts : new List<Part>();

        static ModuleVTOLAssist FC()
        {
            foreach (var p in Parts) { var m = p.FindModuleImplementing<ModuleVTOLAssist>(); if (m != null && m.available) return m; }
            return null;
        }

        static ModuleVTOLAssist AnyFC()
        {
            if (EditorLogic.RootPart != null && EditorLogic.RootPart.FindModuleImplementing<ModuleVTOLAssist>() is ModuleVTOLAssist r) return r;
            foreach (var p in Parts) { var m = p.FindModuleImplementing<ModuleVTOLAssist>(); if (m != null) return m; }
            return null;
        }

        void Update()
        {
            LockEditor();
            if (!Show || Time.time < nextAnalyse) return;
            nextAnalyse = Time.time + 0.5f;
            Analyse();
        }

        /// <summary>Hover balance with the craft as it sits (world up = up at launch).</summary>
        void Analyse()
        {
            engines.Clear();
            mass = 0; com = Vector3.zero;
            foreach (var p in Parts)
            {
                float m = p.mass + p.GetResourceMass();
                mass += m; com += (p.transform.position + p.transform.rotation * p.CoMOffset) * m;
            }
            if (mass > 0) com /= mass;
            Vector3 sum = Vector3.zero, moment = Vector3.zero; float vert = 0, total = 0;
            foreach (var p in Parts)
                foreach (var e in p.FindModulesImplementing<ModuleEngines>())
                {
                    if (e.thrustTransforms == null || e.thrustTransforms.Count == 0) continue;
                    if (p.FindModuleImplementing<BDArmory.Weapons.Missiles.MissileBase>() != null) continue;
                    float t = e.maxThrust * e.thrustPercentage / 100f;
                    Vector3 d = Vector3.zero, pos = Vector3.zero;
                    foreach (var tt in e.thrustTransforms) { d -= tt.forward; pos += tt.position; }
                    d.Normalize(); pos /= e.thrustTransforms.Count;
                    bool lift = Vector3.Dot(d, Vector3.up) > 0.5f;
                    engines.Add((e, t, lift));
                    if (!lift) continue;
                    vert += t * Vector3.Dot(d, Vector3.up); total += t;
                    moment += pos * t; sum += d * t;
                }
            vertThrust = vert;
            twr = mass > 0 ? vert / (mass * 9.81f) : 0;
            cot = total > 0 ? moment / total : com;
            cotOffset = Vector3.ProjectOnPlane(cot - com, Vector3.up).magnitude;
        }

        /// <summary>Back off engines on the far side until the centre of thrust sits over the centre of mass.</summary>
        void Balance()
        {
            for (int it = 0; it < 200; it++)
            {
                Analyse();
                Vector3 off = Vector3.ProjectOnPlane(cot - com, Vector3.up);
                if (off.magnitude < 0.02f) break;
                ModuleEngines worst = null; float best = 0;
                foreach (var (e, t, lift) in engines)
                {
                    if (!lift || e.thrustPercentage <= 10) continue;
                    float along = Vector3.Dot(e.thrustTransforms[0].position - com, off.normalized) * t;
                    if (along > best) { best = along; worst = e; }
                }
                if (worst == null) break;
                worst.thrustPercentage = Mathf.Max(10, worst.thrustPercentage - 1);
                foreach (var c in worst.part.symmetryCounterparts) { var ce = c.FindModuleImplementing<ModuleEngines>(); if (ce != null && Vector3.Dot(ce.thrustTransforms[0].position - com, off) > 0) ce.thrustPercentage = worst.thrustPercentage; }
            }
            Analyse();
            GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
        }

        void OnGUI()
        {
            if (!Show) return;
            rect = AvionicsCore.DrawWindow(WindowID, rect, SafeDraw, "VTOL designer", BDArmorySetup.BDGuiSkin);
            if (Event.current.type == EventType.Repaint && error == null) try { Draw3D(); } catch (System.Exception e) { Fail(e); }
        }

        void SafeDraw(int id)
        {
            if (error != null)
            {
                if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; error = null; return; }
                AvionicsDraw.ColorLabel(new Rect(8, 24, rect.width - 16, 60), "The VTOL designer hit an error (details in KSP.log):\n" + error, AvionicsDraw.LabelSmall, AvionicsDraw.Hostile);
                if (GUI.Button(new Rect(8, 90, 100, 20), "Retry", BDArmorySetup.ButtonStyle)) error = null;
                GUI.DragWindow(); return;
            }
            try { Draw(); } catch (System.Exception e) { Fail(e); try { GUI.EndScrollView(); } catch { } }
        }

        void Fail(System.Exception e)
        {
            if (e is ExitGUIException) throw e;
            error = e.InnerException?.Message ?? e.Message;
            Debug.LogError($"[BDArmoryAvionics]: VTOL designer: {e}");
        }

        void Draw()
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; return; }
            var btn = BDArmorySetup.ButtonStyle; var on = BDArmorySetup.SelectedButtonStyle; var small = AvionicsDraw.LabelSmall;
            float W = rect.width - 16, y = 24;
            var fc = FC();
            if (fc == null)
            {
                var any = AnyFC();
                AvionicsDraw.ColorLabel(new Rect(8, y, W, 30), any == null ? "Add a command part (cockpit or probe core) first." : "The VTOL flight computer is off on this craft.", small, AvionicsDraw.ScopeText);
                y += 32;
                if (any != null && GUI.Button(new Rect(8, y, 220, 24), "Switch on VTOL flight computer", on))
                {
                    any.available = true;
                    any.upLocal = any.part.transform.InverseTransformDirection(Vector3.up); // up as it sits = up at launch
                    var nose = EditorDriver.editorFacility == EditorFacility.SPH ? Vector3.forward : Vector3.ProjectOnPlane(any.part.transform.forward, Vector3.up);
                    if (nose.sqrMagnitude < 0.01f) nose = Vector3.forward;
                    any.noseLocal = any.part.transform.InverseTransformDirection(nose.normalized);
                }
                GUI.DragWindow(); return;
            }

            scroll = GUI.BeginScrollView(new Rect(8, y, W, rect.height - y - 6), scroll, new Rect(0, 0, W - 18, 1500));
            float w = W - 18; y = 0;

            // 1. Type and orientation
            Head(ref y, w, "1. CRAFT");
            for (int i = 0; i < ModuleVTOLAssist.Types.Length; i++)
            {
                string t = ModuleVTOLAssist.Types[i];
                if (GUI.Button(new Rect(i * (w / 4), y, w / 4 - 3, 20), t, fc.craftType == t ? on : btn)) fc.craftType = t;
            }
            y += 22;
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), $"Flies as: {fc.Type}" + (fc.craftType == "Auto" ? " (detected)" : ""), small, AvionicsDraw.HudText); y += 18;
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), "Up = as the craft sits here (blue line). Nose = green arrow:", small, AvionicsDraw.ScopeText); y += 18;
            if (GUI.Button(new Rect(0, y, w / 2 - 3, 20), "Turn nose 90° left", btn)) RotateNose(fc, -90);
            if (GUI.Button(new Rect(w / 2, y, w / 2, 20), "Turn nose 90° right", btn)) RotateNose(fc, 90);
            y += 22;
            if (GUI.Button(new Rect(0, y, w, 20), "Reset up to how it sits now", btn)) fc.upLocal = fc.part.transform.InverseTransformDirection(Vector3.up);
            y += 26;

            // 2. Hover check
            Head(ref y, w, "2. HOVER CHECK (as posed now)");
            Color tc = twr >= 1.3f ? AvionicsDraw.Friendly : twr >= 1.05f ? AvionicsDraw.Unknown : AvionicsDraw.Hostile;
            bool rotor = VTOLParts.HasRotors(Parts);
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), rotor && vertThrust < 1 ? $"Rotorcraft: {mass:F1} t. Lift comes from the rotors (bind blade pitch to the main throttle)." :
                $"Mass {mass:F1} t · lift thrust {vertThrust:F0} kN · hover TWR {twr:F2} {(twr < 1.05f ? "(can't hover)" : twr < 1.3f ? "(marginal: aim for 1.3+)" : "(good)")}", small, rotor && vertThrust < 1 ? AvionicsDraw.HudText : tc);
            y += 18;
            if (vertThrust > 0)
            {
                Color oc = cotOffset < 0.1f ? AvionicsDraw.Friendly : cotOffset < 0.4f ? AvionicsDraw.Unknown : AvionicsDraw.Hostile;
                AvionicsDraw.ColorLabel(new Rect(0, y, w - 120, 16), $"Centre of thrust {cotOffset:F2} m from centre of mass {(cotOffset < 0.1f ? "(balanced)" : "(it will tip)")}", small, oc);
                if (GUI.Button(new Rect(w - 116, y - 2, 116, 20), "Balance thrust", btn)) Balance();
                y += 20;
            }
            if (Fold(ref y, w, $"Engines ({engines.Count})", ref openEngines))
                foreach (var (e, t, lift) in engines)
                {
                    AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), $"{e.part.partInfo.title}: {t:F0} kN at {e.thrustPercentage:F0}% {(lift ? "· LIFT" : "· cruise")}", small, lift ? AvionicsDraw.HudText : AvionicsDraw.ScopeText);
                    y += 16;
                }
            y += 4;

            // 3. Tilt mechanism
            if (Fold(ref y, w, "3. TILT MECHANISM (Infernal Robotics)", ref openTilt))
            {
                if (!IRServo.Available) { AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), "Infernal Robotics Next isn't installed.", small, AvionicsDraw.Unknown); y += 20; }
                else
                {
                    var servos = new List<(Part p, PartModule s, ModuleIRTurret m)>();
                    foreach (var p in Parts) { var m = p.FindModuleImplementing<ModuleIRTurret>(); var s = IRServo.Find(p); if (m != null && s != null && IRServo.Rotational(s)) servos.Add((p, s, m)); }
                    if (servos.Count == 0) { AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), "No rotating IR servos on this craft.", small, AvionicsDraw.ScopeText); y += 20; }
                    else
                    {
                        if (GUI.Button(new Rect(0, y, w / 2 - 3, 20), "Show hover pose", btn)) Pose(servos, false);
                        if (GUI.Button(new Rect(w / 2, y, w / 2, 20), "Show cruise pose", btn)) Pose(servos, true);
                        y += 24;
                        string[] roles = { "Not tilt", "Tilt", "Tilt + yaw" };
                        foreach (var (p, s, m) in servos)
                        {
                            float pos = IRServo.EditorPosition(s);
                            AvionicsDraw.ColorLabel(new Rect(0, y, w - 90, 16), $"{p.partInfo.title}  at {pos:F0}", small, m.tiltRole != 0 ? AvionicsDraw.Friendly : AvionicsDraw.HudText);
                            if (GUI.Button(new Rect(w - 86, y - 2, 86, 18), roles[m.tiltRole], m.tiltRole != 0 ? on : btn)) ForSym(m, x => x.tiltRole = (x.tiltRole + 1) % 3);
                            y += 18;
                            if (m.tiltRole == 0) { y += 4; continue; }
                            float mn = IRServo.Min(s), mx = IRServo.Max(s);
                            if (mx <= mn || mx - mn >= 359f) { mn = -180; mx = 180; }
                            float np = GUI.HorizontalSlider(new Rect(0, y + 2, w - 210, 14), pos, mn, mx);
                            if (Mathf.Abs(np - pos) > 0.2f) { float r = Mathf.Round(np); IRServo.EditorSetTo(s, r); foreach (var c in p.symmetryCounterparts) { var cs = IRServo.Find(c); if (cs != null) IRServo.EditorSetTo(cs, r); } }
                            if (GUI.Button(new Rect(w - 206, y, 102, 18), $"Hover = {m.tiltHover:F0}", btn)) { float v = pos; ForSym(m, x => x.tiltHover = v); }
                            if (GUI.Button(new Rect(w - 102, y, 102, 18), $"Cruise = {m.tiltCruise:F0}", btn)) { float v = pos; ForSym(m, x => x.tiltCruise = v); }
                            y += 20;
                            if (m.tiltRole == 2) { m.tiltYaw = Mathf.Round(Slider(ref y, w, $"Yaw tilt ±{m.tiltYaw:F0}° (left/right nacelles opposite)", m.tiltYaw, 0, 30)); }
                            y += 4;
                        }
                        AvionicsDraw.ColorLabel(new Rect(0, y, w, 30), "Drag a slider to tilt, then click Hover = or Cruise = to store that position.\nThe flight computer tilts between them with airspeed.", small, AvionicsDraw.ScopeText);
                        y += 34;
                    }
                }
            }

            // 4. How it flies
            if (Fold(ref y, w, "4. HOW IT FLIES", ref openFly))
            {
                string[] presets = { "Gentle heli", "Agile heli", "Jump jet", "Tiltrotor", "Drone" };
                for (int i = 0; i < presets.Length; i++) if (GUI.Button(new Rect(i * (w / 5), y, w / 5 - 3, 20), presets[i], btn)) Preset(fc, i);
                y += 26;
                fc.maxSpeed = Mathf.Round(Slider(ref y, w, $"Hover manoeuvring speed {fc.maxSpeed:F0} m/s", fc.maxSpeed, 3, 60));
                fc.maxTilt = Mathf.Round(Slider(ref y, w, $"Max body tilt {fc.maxTilt:F0}°", fc.maxTilt, 3, 35));
                fc.maxClimb = Mathf.Round(Slider(ref y, w, $"Max climb / descent {fc.maxClimb:F0} m/s", fc.maxClimb, 1, 25));
                fc.responsiveness = Mathf.Round(Slider(ref y, w, $"Responsiveness {fc.responsiveness:P0} (gentle - sharp)", fc.responsiveness, 0, 1) * 20) / 20;
                fc.smoothness = Mathf.Round(Slider(ref y, w, $"Smoothness {fc.smoothness:P0} (crisp - soft)", fc.smoothness, 0, 1) * 20) / 20;
                fc.takeoffAGL = Mathf.Round(Slider(ref y, w, $"Takeoff hover height {fc.takeoffAGL:F0} m", fc.takeoffAGL, 5, 200));
                if (fc.Type != VTOLType.Helicopter)
                {
                    fc.transitionStart = Mathf.Round(Slider(ref y, w, $"Start converting at {fc.transitionStart:F0} m/s", fc.transitionStart, 5, 120));
                    fc.cruiseSpeed = Mathf.Max(fc.transitionStart + 10, Mathf.Round(Slider(ref y, w, $"Fully converted at {fc.cruiseSpeed:F0} m/s", fc.cruiseSpeed, 20, 400)));
                }
                fc.gyro = Mathf.Round(Slider(ref y, w, fc.gyro <= 0 ? "Stability gyro: off" : $"Stability gyro {fc.gyro:P0}", fc.gyro, 0, 1) * 20) / 20;
                fc.fastEngines = GUI.Toggle(new Rect(0, y, w, 20), fc.fastEngines, fc.fastEngines ? "Engines: instant throttle response (no spool lag) ON" : "Engines: normal spool", fc.fastEngines ? on : btn); y += 22;
                fc.diffThrust = GUI.Toggle(new Rect(0, y, w, 20), fc.diffThrust, fc.diffThrust ? "Differential thrust for pitch/roll ON" : "Differential thrust off", fc.diffThrust ? on : btn); y += 22;
                fc.liftOffInCruise = GUI.Toggle(new Rect(0, y, w, 20), fc.liftOffInCruise, fc.liftOffInCruise ? "Lift-only engines off in cruise ON" : "Lift engines stay on in cruise", fc.liftOffInCruise ? on : btn); y += 22;
                int agi = System.Array.IndexOf(ModuleVTOLAssist.AGs, fc.hoverAG); if (agi < 0) agi = 0;
                if (GUI.Button(new Rect(0, y, w, 20), $"Hover-conversion action group: {fc.hoverAG}", btn)) fc.hoverAG = ModuleVTOLAssist.AGs[(agi + 1) % ModuleVTOLAssist.AGs.Length];
                y += 26;
            }

            // 5. Learning
            Head(ref y, w, "5. LEARNED IN FLIGHT");
            var tmp = new[] { new AxisLearner(), new AxisLearner(), new AxisLearner() };
            tmp[0].Load(fc.learnPitch); tmp[1].Load(fc.learnRoll); tmp[2].Load(fc.learnYaw);
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 30), $"Pitch {tmp[0].Confidence:P0} · roll {tmp[1].Confidence:P0} · yaw {tmp[2].Confidence:P0} · hover throttle {(fc.hoverThrottle > 0 ? fc.hoverThrottle.ToString("P0") : "-")}\nIt learns whenever it flies; press Train in flight (while hovering) for a quick session.", small, AvionicsDraw.HudText);
            y += 32;
            if (GUI.Button(new Rect(0, y, 160, 20), "Forget learning", btn)) { fc.learnPitch = fc.learnRoll = fc.learnYaw = ""; fc.hoverThrottle = -1; }
            y += 26;

            GUI.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, rect.width - 22, 20));
        }

        static void ForSym(ModuleIRTurret m, System.Action<ModuleIRTurret> set)
        {
            set(m);
            foreach (var c in m.part.symmetryCounterparts) { var o = c.FindModuleImplementing<ModuleIRTurret>(); if (o != null) set(o); }
        }

        static void Pose(List<(Part p, PartModule s, ModuleIRTurret m)> servos, bool cruise)
        {
            foreach (var (_, s, m) in servos) if (m.tiltRole != 0) IRServo.EditorSetTo(s, cruise ? m.tiltCruise : m.tiltHover);
        }

        static void RotateNose(ModuleVTOLAssist fc, float deg)
        {
            fc.BodyAxesEditor(out var up, out var nose);
            nose = Quaternion.AngleAxis(deg, up) * nose;
            fc.noseLocal = fc.part.transform.InverseTransformDirection(nose);
        }

        static void Preset(ModuleVTOLAssist fc, int i)
        {
            switch (i)
            {
                case 0: fc.craftType = "Helicopter"; fc.maxSpeed = 12; fc.maxTilt = 12; fc.maxClimb = 4; fc.responsiveness = 0.35f; fc.smoothness = 0.7f; fc.gyro = 0.2f; break;
                case 1: fc.craftType = "Helicopter"; fc.maxSpeed = 30; fc.maxTilt = 25; fc.maxClimb = 10; fc.responsiveness = 0.75f; fc.smoothness = 0.4f; fc.gyro = 0.1f; break;
                case 2: fc.craftType = "Jump jet"; fc.maxSpeed = 20; fc.maxTilt = 12; fc.maxClimb = 8; fc.responsiveness = 0.55f; fc.smoothness = 0.5f; fc.fastEngines = true; fc.diffThrust = true; fc.gyro = 0.3f; fc.transitionStart = 30; fc.cruiseSpeed = 120; break;
                case 3: fc.craftType = "Tiltrotor"; fc.maxSpeed = 15; fc.maxTilt = 10; fc.maxClimb = 6; fc.responsiveness = 0.5f; fc.smoothness = 0.6f; fc.gyro = 0.2f; fc.transitionStart = 20; fc.cruiseSpeed = 90; break;
                case 4: fc.craftType = "Auto"; fc.maxSpeed = 25; fc.maxTilt = 30; fc.maxClimb = 12; fc.responsiveness = 0.9f; fc.smoothness = 0.3f; fc.diffThrust = true; fc.fastEngines = true; fc.gyro = 0; break;
            }
        }

        static void Head(ref float y, float w, string text) { AvionicsDraw.ColorLabel(new Rect(0, y, w, 14), text, AvionicsDraw.Header, AvionicsDraw.HudText); y += 18; }
        static bool Fold(ref float y, float w, string text, ref bool open)
        {
            if (GUI.Button(new Rect(0, y, w, 20), (open ? "▼ " : "► ") + text, BDArmorySetup.ButtonStyle)) open = !open;
            y += 24; return open;
        }
        static float Slider(ref float y, float w, string label, float value, float min, float max)
        {
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 14), label, AvionicsDraw.LabelSmall, AvionicsDraw.HudText);
            float v = GUI.HorizontalSlider(new Rect(0, y + 15, w, 14), value, min, max);
            y += 32; return v;
        }

        void Draw3D()
        {
            var fc = FC();
            var cam = EditorCamera.Instance != null ? EditorCamera.Instance.cam : Camera.main;
            if (fc == null || cam == null || !AvionicsDraw.Begin()) return;
            bool S(Vector3 wp, out Vector2 p) { var sp = cam.WorldToScreenPoint(wp); p = new Vector2(sp.x, Screen.height - sp.y); return sp.z > 0; }
            void L(Vector3 a, Vector3 b, float wd, Color c) { if (S(a, out var pa) && S(b, out var pb)) AvionicsDraw.Line(pa, pb, wd, c); }
            fc.BodyAxesEditor(out var up, out var nose);
            L(com, com + up * 4f, 2.5f, new Color(0.4f, 0.6f, 1f));
            L(com, com + nose * 5f, 3f, new Color(0.3f, 1f, 0.4f));
            L(com + nose * 5f, com + nose * 4.2f + Vector3.Cross(up, nose) * 0.6f, 3f, new Color(0.3f, 1f, 0.4f));
            L(com + nose * 5f, com + nose * 4.2f - Vector3.Cross(up, nose) * 0.6f, 3f, new Color(0.3f, 1f, 0.4f));
            if (S(com, out var pc)) AvionicsDraw.Circle(pc, 7, 2, Color.yellow, 16);
            if (vertThrust > 0 && S(cot, out var pt)) { AvionicsDraw.Circle(pt, 5, 2, new Color(1f, 0.5f, 0.2f), 12); L(com, cot, 1.5f, new Color(1f, 0.5f, 0.2f)); }
            foreach (var (e, t, lift) in engines)
                if (lift && e.thrustTransforms.Count > 0) { var tt = e.thrustTransforms[0]; L(tt.position, tt.position - tt.forward * Mathf.Clamp(t / 50f, 0.5f, 4f), 2, new Color(1f, 0.6f, 0.2f, 0.8f)); }
            AvionicsDraw.End();
        }
    }
}
