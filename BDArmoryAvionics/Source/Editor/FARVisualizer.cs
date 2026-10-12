using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// VAB/SPH: FAR's own editor aerodynamics drawn as charts, with what they mean for flying.
    ///  * Lift / drag / pitching moment / L/D against angle of attack at a chosen Mach and flap setting.
    ///  * What it means: maximum lift and stall angle, stall and take-off speed for the craft's mass, best L/D, trim angle,
    ///    pitch stability (Cm slope), and at what angle of attack the craft cruises at a chosen speed and height.
    ///  * Drag against Mach (the transonic drag rise), FAR's critical Mach and wave-drag area.
    ///  * Area ruling: the craft's cross-section area along its length against the ideal (Sears-Haack) shape of the same
    ///    length and volume, with the bumps that cost transonic drag marked.
    /// Uses FAR's simulator through reflection (only when FAR is installed); nothing in FAR is changed.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class FARVisualizer : MonoBehaviour
    {
        public static bool Show;
        const int WindowID = 71730394;
        Rect rect = new(Screen.width / 2 - 360, 60, 720, 720);

        // FAR by reflection
        static Type editorGUI, simInputT; static PropertyInfo instanceP; static FieldInfo instantSimF, vehicleAeroF; static MethodInfo getSteady; static PropertyInfo readyP;
        static MethodInfo areasM; static PropertyInfo lengthP, critMachP, sonicAreaP, maxAreaP;
        public static bool Available { get { Resolve(); return getSteady != null; } }

        static void Resolve()
        {
            if (editorGUI != null) return;
            var a = AssemblyLoader.loadedAssemblies.FirstOrDefault(x => x.name == "FerramAerospaceResearch")?.assembly;
            if (a == null) { editorGUI = typeof(object); return; }
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            editorGUI = a.GetType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI");
            var sim = a.GetType("FerramAerospaceResearch.FARGUI.FAREditorGUI.Simulation.InstantConditionSim");
            simInputT = a.GetType("FerramAerospaceResearch.FARGUI.FAREditorGUI.Simulation.InstantConditionSimInput");
            var va = a.GetType("FerramAerospaceResearch.FARAeroComponents.VehicleAerodynamics");
            if (editorGUI == null || sim == null || simInputT == null) return;
            instanceP = editorGUI.GetProperty("Instance", all);
            instantSimF = editorGUI.GetField("_instantSim", all);
            vehicleAeroF = editorGUI.GetField("_vehicleAero", all);
            getSteady = sim.GetMethods(all).FirstOrDefault(m => m.Name == "GetClCdCmSteady" && m.GetParameters().Length == 4);
            readyP = sim.GetProperty("Ready", all);
            if (va != null)
            {
                areasM = va.GetMethods(all).FirstOrDefault(m => m.Name == "GetCrossSectionAreas" && m.GetParameters().Length == 0);
                lengthP = va.GetProperty("Length", all); critMachP = va.GetProperty("CriticalMach", all); sonicAreaP = va.GetProperty("SonicDragArea", all); maxAreaP = va.GetProperty("MaxCrossSectionArea", all);
            }
        }

        object Sim => instanceP?.GetValue(null) is object gui ? instantSimF?.GetValue(gui) : null;
        object VAero => instanceP?.GetValue(null) is object gui ? vehicleAeroF?.GetValue(gui) : null;

        bool Steady(object sim, double alpha, double mach, int flaps, out double cl, out double cd, out double cm)
        {
            cl = cd = cm = 0;
            var input = Activator.CreateInstance(simInputT, alpha, 0.0, 0.0, 0.0, 0.0, 0.0, mach, 0.0, flaps, false);
            var args = new object[] { input, null, true, false };
            getSteady.Invoke(sim, args);
            var o = args[1]; if (o == null) return false;
            var t = o.GetType();
            cl = (double)t.GetField("Cl").GetValue(o); cd = (double)t.GetField("Cd").GetValue(o); cm = (double)t.GetField("Cm").GetValue(o);
            return true;
        }

        // results
        class Sweep { public float[] a, cl, cd, cm; }
        Sweep aoa; float[] machs, machCd; double[] areas; float length, critMach, sonicArea, maxArea, wingS, massT;
        string status = "Press Analyse (FAR must have finished its own voxel pass).";
        float mach = 0.6f; int flaps; float cruiseKmh = 600, cruiseAlt = 3000;
        bool busy;

        static float WingArea()
        {
            float s = 0;
            foreach (var p in EditorLogic.fetch.ship.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (m.moduleName != "FARWingAerodynamicModel" && m.moduleName != "FARControllableSurface") continue;
                    var t = m.GetType();
                    var v = t.GetProperty("S")?.GetValue(m) ?? t.GetField("S")?.GetValue(m);
                    if (v is double d) s += (float)d; else if (v is float f) s += f;
                }
            return s;
        }

        IEnumerator Analyse()
        {
            busy = true; status = "Waiting for FAR...";
            var gui = instanceP?.GetValue(null);
            if (gui == null) { status = "FAR's editor isn't running (open the FAR window once?)."; busy = false; yield break; }
            editorGUI.GetMethod("RequestUpdateVoxel", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
            float until = Time.realtimeSinceStartup + 20;
            yield return new WaitForSecondsRealtime(1f);
            object sim = null;
            while (Time.realtimeSinceStartup < until) { sim = Sim; if (sim != null && readyP != null && (bool)readyP.GetValue(sim)) break; yield return new WaitForSecondsRealtime(0.25f); }
            if (sim == null || !(bool)readyP.GetValue(sim)) { status = "FAR didn't finish (is there a wing on the craft?)."; busy = false; yield break; }
            try
            {
                massT = EditorLogic.fetch.ship.GetTotalMass(); wingS = WingArea();
                var s = new Sweep { a = new float[46], cl = new float[46], cd = new float[46], cm = new float[46] };
                for (int i = 0; i < 46; i++) { double al = -10 + i; Steady(sim, al, mach, flaps, out var cl, out var cd, out var cm); s.a[i] = (float)al; s.cl[i] = (float)cl; s.cd[i] = (float)cd; s.cm[i] = (float)cm; }
                aoa = s;
                machs = new float[30]; machCd = new float[30];
                for (int i = 0; i < 30; i++) { machs[i] = 0.1f + i * 0.1f; Steady(sim, 3.0, machs[i], 0, out _, out var cd, out _); machCd[i] = (float)cd; }
                var va = VAero;
                if (va != null && areasM != null)
                {
                    areas = areasM.Invoke(va, null) as double[];
                    length = (float)(double)lengthP.GetValue(va); critMach = (float)(double)critMachP.GetValue(va); sonicArea = (float)(double)sonicAreaP.GetValue(va); maxArea = (float)(double)maxAreaP.GetValue(va);
                }
                status = "";
            }
            catch (Exception e) { status = "FAR analysis failed: " + (e.InnerException?.Message ?? e.Message); Debug.LogWarning($"[BDArmoryAvionics]: FAR visualizer: {e}"); }
            busy = false;
        }

        // ---------------- drawing ----------------
        void OnGUI()
        {
            if (!Show || !HighLogic.LoadedSceneIsEditor) return;
            rect = AvionicsCore.DrawWindow(WindowID, rect, Draw, "FAR aerodynamics", BDArmorySetup.BDGuiSkin);

        }

        void OnDestroy() => EditorLogic.fetch?.Unlock("ELE_FARVis");

        static readonly Color ClC = new(0.35f, 0.7f, 1f), CdC = new(1f, 0.4f, 0.3f), CmC = new(1f, 0.85f, 0.3f), LdC = new(0.4f, 1f, 0.5f), Grid = new(1, 1, 1, 0.12f), IdealC = new(1, 1, 1, 0.5f);

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; return; }
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle; var small = AvionicsDraw.LabelSmall;
            float w = rect.width - 16, y = 24;
            Resolve();
            if (!Available) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 40), "Ferram Aerospace Research isn't installed (or its editor API changed).", AvionicsDraw.Label, AvionicsDraw.Unknown); GUI.DragWindow(); return; }

            if (GUI.Button(new Rect(8, y, 110, 22), busy ? "Working..." : "Analyse", aoa != null ? btn : sel) && !busy) StartCoroutine(Analyse());
            GUI.Label(new Rect(124, y + 2, 40, 20), "Mach", small);
            float[] ms = { 0.3f, 0.6f, 0.9f, 1.2f, 2f };
            for (int i = 0; i < ms.Length; i++) if (GUI.Button(new Rect(160 + i * 46, y, 44, 22), ms[i].ToString("0.0"), Mathf.Approximately(mach, ms[i]) ? sel : btn)) mach = ms[i];
            GUI.Label(new Rect(395, y + 2, 40, 20), "Flaps", small);
            for (int i = 0; i < 4; i++) if (GUI.Button(new Rect(435 + i * 28, y, 26, 22), i.ToString(), flaps == i ? sel : btn)) flaps = i;
            y += 26;
            if (!string.IsNullOrEmpty(status)) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), status, small, AvionicsDraw.Unknown); y += 18; }
            if (aoa == null) { GUI.DragWindow(); return; }

            // ---- chart 1: vs angle of attack ----
            var c1 = new Rect(8, y, w * 0.58f, 220);
            Panel(c1);
            float clMax = aoa.cl.Max(), clMin = aoa.cl.Min(), cdMax = aoa.cd.Max();
            float lo = Mathf.Min(clMin, aoa.cm.Min(), -0.2f), hi = Mathf.Max(clMax, aoa.cm.Max(), 0.2f);
            Vector2 P(Rect r, float x, float v, float x0, float x1, float v0, float v1) => new(r.x + (x - x0) / (x1 - x0) * r.width, r.yMax - (v - v0) / (v1 - v0) * r.height);
            if (AvionicsDraw.Begin())
            {
                for (float a = -10; a <= 35; a += 5) AvionicsDraw.Line(P(c1, a, lo, -10, 35, lo, hi), P(c1, a, hi, -10, 35, lo, hi), 1, Grid);
                AvionicsDraw.Line(P(c1, -10, 0, -10, 35, lo, hi), P(c1, 35, 0, -10, 35, lo, hi), 1, new Color(1, 1, 1, 0.3f));
                float ldMax = 0; for (int i = 0; i < 46; i++) if (aoa.cd[i] > 1e-4f) ldMax = Mathf.Max(ldMax, aoa.cl[i] / aoa.cd[i]);
                for (int i = 0; i < 45; i++)
                {
                    AvionicsDraw.Line(P(c1, aoa.a[i], aoa.cl[i], -10, 35, lo, hi), P(c1, aoa.a[i + 1], aoa.cl[i + 1], -10, 35, lo, hi), 2, ClC);
                    AvionicsDraw.Line(P(c1, aoa.a[i], aoa.cd[i] / Mathf.Max(1e-4f, cdMax) * hi, -10, 35, lo, hi), P(c1, aoa.a[i + 1], aoa.cd[i + 1] / Mathf.Max(1e-4f, cdMax) * hi, -10, 35, lo, hi), 2, CdC);
                    AvionicsDraw.Line(P(c1, aoa.a[i], aoa.cm[i], -10, 35, lo, hi), P(c1, aoa.a[i + 1], aoa.cm[i + 1], -10, 35, lo, hi), 2, CmC);
                    if (aoa.cd[i] > 1e-4f && aoa.cd[i + 1] > 1e-4f && ldMax > 0)
                        AvionicsDraw.Line(P(c1, aoa.a[i], aoa.cl[i] / aoa.cd[i] / ldMax * hi, -10, 35, lo, hi), P(c1, aoa.a[i + 1], aoa.cl[i + 1] / aoa.cd[i + 1] / ldMax * hi, -10, 35, lo, hi), 1, LdC);
                }
                AvionicsDraw.End();
            }
            AvionicsDraw.ColorLabel(new Rect(c1.x + 4, c1.y + 2, c1.width, 14), $"vs angle of attack, Mach {mach:0.0}, flaps {flaps}   (-10° … 35°)", small, AvionicsDraw.HudText);
            AvionicsDraw.ColorLabel(new Rect(c1.x + 4, c1.yMax - 16, c1.width, 14), "lift Cl", small, ClC);
            AvionicsDraw.ColorLabel(new Rect(c1.x + 54, c1.yMax - 16, c1.width, 14), "drag Cd (scaled)", small, CdC);
            AvionicsDraw.ColorLabel(new Rect(c1.x + 154, c1.yMax - 16, c1.width, 14), "pitch Cm", small, CmC);
            AvionicsDraw.ColorLabel(new Rect(c1.x + 224, c1.yMax - 16, c1.width, 14), "L/D (scaled)", small, LdC);

            // ---- what it means ----
            float ex = c1.xMax + 10, ew = rect.width - ex - 8, ey = y;
            void Line(string label, string val, Color c) { AvionicsDraw.ColorLabel(new Rect(ex, ey, ew, 14), $"{label}: {val}", small, c); ey += 15; }
            int iMax = Array.IndexOf(aoa.cl, clMax);
            float stallA = aoa.a[iMax];
            float W = massT * 1000f * 9.81f, rho0 = 1.225f, rho5 = 0.736f;
            float S = wingS > 0.01f ? wingS : 1f;
            float vs0 = clMax > 0.05f ? Mathf.Sqrt(2 * W / (rho0 * S * clMax)) : float.NaN, vs5 = clMax > 0.05f ? Mathf.Sqrt(2 * W / (rho5 * S * clMax)) : float.NaN;
            int iLd = 0; float ld = 0; for (int i = 0; i < 46; i++) if (aoa.cd[i] > 1e-4f && aoa.cl[i] / aoa.cd[i] > ld) { ld = aoa.cl[i] / aoa.cd[i]; iLd = i; }
            float trim = float.NaN; for (int i = 0; i < 45; i++) if (aoa.cm[i] * aoa.cm[i + 1] <= 0 && aoa.cm[i] != aoa.cm[i + 1]) { trim = aoa.a[i] + aoa.cm[i] / (aoa.cm[i] - aoa.cm[i + 1]); break; }
            int i0 = 10, i5 = 15; float cmSlope = (aoa.cm[i5] - aoa.cm[i0]) / 5f;
            AvionicsDraw.ColorLabel(new Rect(ex, ey, ew, 16), "WHAT IT MEANS", AvionicsDraw.Header, AvionicsDraw.HudText); ey += 18;
            Line("Mass / wing area", $"{massT:F1} t / {S:F1} m²{(wingS <= 0.01f ? " (no FAR wings!)" : "")}", AvionicsDraw.HudText);
            Line("Max lift", $"Cl {clMax:F2} at {stallA:F0}° (stall)", ClC);
            Line("Stall speed", $"{vs0 * 3.6f:F0} km/h at sea level, {vs5 * 3.6f:F0} at 5 km", vs0 * 3.6f > 300 ? AvionicsDraw.Unknown : AvionicsDraw.HudText);
            Line("Take-off speed", $"~{vs0 * 1.2f * 3.6f:F0} km/h", AvionicsDraw.HudText);
            Line("Best L/D", $"{ld:F1} at {aoa.a[iLd]:F0}°", LdC);
            Line("Trim (Cm = 0)", float.IsNaN(trim) ? "none in range: needs elevator" : $"{trim:F1}°", CmC);
            Line("Pitch stability", cmSlope < -0.001f ? $"stable (dCm/dα {cmSlope:F3})" : cmSlope > 0.001f ? $"UNSTABLE (dCm/dα {cmSlope:F3})" : "neutral", cmSlope < -0.001f ? AvionicsDraw.Friendly : AvionicsDraw.Hostile);
            ey += 6;
            // cruise: angle of attack needed at a speed / height
            GUI.Label(new Rect(ex, ey, 70, 18), "Cruise km/h", small); cruiseKmh = GUI.HorizontalSlider(new Rect(ex + 72, ey + 4, ew - 120, 12), cruiseKmh, 150, 2500); GUI.Label(new Rect(ex + ew - 44, ey, 46, 18), $"{cruiseKmh:F0}", small); ey += 18;
            GUI.Label(new Rect(ex, ey, 70, 18), "at m", small); cruiseAlt = GUI.HorizontalSlider(new Rect(ex + 72, ey + 4, ew - 120, 12), cruiseAlt, 0, 15000); GUI.Label(new Rect(ex + ew - 44, ey, 46, 18), $"{cruiseAlt:F0}", small); ey += 20;
            float rho = 1.225f * Mathf.Exp(-cruiseAlt / 5600f), v = cruiseKmh / 3.6f;
            float clNeed = W / (0.5f * rho * v * v * S);
            float aNeed = float.NaN; for (int i = 0; i < 45; i++) if (aoa.cl[i] <= clNeed && aoa.cl[i + 1] >= clNeed) { aNeed = aoa.a[i] + (clNeed - aoa.cl[i]) / Mathf.Max(1e-4f, aoa.cl[i + 1] - aoa.cl[i]); break; }
            float cdAt = float.IsNaN(aNeed) ? float.NaN : Mathf.Lerp(aoa.cd[Mathf.Clamp(Mathf.FloorToInt(aNeed + 10), 0, 45)], aoa.cd[Mathf.Clamp(Mathf.CeilToInt(aNeed + 10), 0, 45)], aNeed - Mathf.Floor(aNeed));
            float dragKN = float.IsNaN(cdAt) ? float.NaN : 0.5f * rho * v * v * S * cdAt / 1000f;
            Line("Needs", float.IsNaN(aNeed) ? $"Cl {clNeed:F2}: more than the wing gives (too slow / heavy)" : $"{aNeed:F1}° AoA, Cl {clNeed:F2}, drag ~{dragKN:F1} kN (thrust to hold it)", float.IsNaN(aNeed) ? AvionicsDraw.Hostile : AvionicsDraw.HudText);
            y = Mathf.Max(c1.yMax, ey) + 8;

            // ---- chart 2: drag vs Mach ----
            var c2 = new Rect(8, y, w * 0.48f, 170);
            Panel(c2);
            if (machCd != null)
            {
                float cdm = Mathf.Max(1e-4f, machCd.Max());
                if (AvionicsDraw.Begin())
                {
                    AvionicsDraw.Line(P(c2, 1, 0, 0, 3.1f, 0, cdm * 1.1f), P(c2, 1, cdm * 1.1f, 0, 3.1f, 0, cdm * 1.1f), 1, new Color(1, 1, 1, 0.3f));
                    if (critMach > 0) AvionicsDraw.Line(P(c2, critMach, 0, 0, 3.1f, 0, cdm * 1.1f), P(c2, critMach, cdm * 1.1f, 0, 3.1f, 0, cdm * 1.1f), 1, AvionicsDraw.Unknown);
                    for (int i = 0; i < machs.Length - 1; i++) AvionicsDraw.Line(P(c2, machs[i], machCd[i], 0, 3.1f, 0, cdm * 1.1f), P(c2, machs[i + 1], machCd[i + 1], 0, 3.1f, 0, cdm * 1.1f), 2, CdC);
                    AvionicsDraw.End();
                }
                float sub = machCd[5], peak = machCd.Skip(7).Take(8).Max();
                AvionicsDraw.ColorLabel(new Rect(c2.x + 4, c2.y + 2, c2.width, 14), "drag vs Mach (3° AoA)   yellow: critical Mach", small, AvionicsDraw.HudText);
                AvionicsDraw.ColorLabel(new Rect(c2.x + 4, c2.yMax - 30, c2.width, 14), $"transonic drag rise x{peak / Mathf.Max(1e-4f, sub):F1}   critical Mach {critMach:F2}", small, peak / Mathf.Max(1e-4f, sub) > 3 ? AvionicsDraw.Hostile : AvionicsDraw.HudText);
                AvionicsDraw.ColorLabel(new Rect(c2.x + 4, c2.yMax - 16, c2.width, 14), $"wave-drag area {sonicArea:F2} m²", small, AvionicsDraw.ScopeText);
            }

            // ---- chart 3: area ruling ----
            var c3 = new Rect(c2.xMax + 8, y, rect.width - c2.xMax - 16, 170);
            Panel(c3);
            if (areas != null && areas.Length > 2)
            {
                int n = areas.Length; float amax = (float)areas.Max();
                // Sears-Haack body of the same length and volume: A(x) = A_max * (4x(1-x))^1.5, volume = 3π/16 * A_max * L... scaled to equal volume
                double vol = areas.Sum(); double shSum = 0; var sh = new double[n];
                for (int i = 0; i < n; i++) { double x = (i + 0.5) / n; sh[i] = Math.Pow(4 * x * (1 - x), 1.5); shSum += sh[i]; }
                double k = vol / Math.Max(1e-6, shSum);
                float top = (float)Math.Max(amax, sh.Max() * k) * 1.1f;
                double dev = 0;
                if (AvionicsDraw.Begin())
                {
                    for (int i = 0; i < n - 1; i++)
                    {
                        var a0 = P(c3, i, (float)areas[i], 0, n - 1, 0, top); var a1 = P(c3, i + 1, (float)areas[i + 1], 0, n - 1, 0, top);
                        var i0p = P(c3, i, (float)(sh[i] * k), 0, n - 1, 0, top); var i1p = P(c3, i + 1, (float)(sh[i + 1] * k), 0, n - 1, 0, top);
                        AvionicsDraw.Line(i0p, i1p, 1, IdealC);
                        float over = (float)(areas[i] - sh[i] * k) / Mathf.Max(1e-4f, top);
                        AvionicsDraw.Line(a0, a1, 2, over > 0.08f ? AvionicsDraw.Hostile : over < -0.08f ? AvionicsDraw.Unknown : ClC);
                        dev += Math.Abs(areas[i] - sh[i] * k);
                    }
                    AvionicsDraw.End();
                }
                float score = (float)(1 - dev / Math.Max(1e-6, vol));
                AvionicsDraw.ColorLabel(new Rect(c3.x + 4, c3.y + 2, c3.width, 14), "area ruling: nose ← cross-section area → tail   white: ideal", small, AvionicsDraw.HudText);
                AvionicsDraw.ColorLabel(new Rect(c3.x + 4, c3.yMax - 30, c3.width, 14), $"match to ideal {Mathf.Clamp01(score) * 100:F0}%   length {length:F1} m, max area {maxArea:F2} m²", small, score > 0.8f ? AvionicsDraw.Friendly : score > 0.6f ? AvionicsDraw.HudText : AvionicsDraw.Unknown);
                AvionicsDraw.ColorLabel(new Rect(c3.x + 4, c3.yMax - 16, c3.width, 14), "red: too fat there (slim it / move parts)  yellow: too thin", small, AvionicsDraw.ScopeText);
            }
            y = c2.yMax + 6;
            rect.height = y + 8;
            GUI.DragWindow(new Rect(0, 0, rect.width - 44, 20));
        }

        static void Panel(Rect r) { GUI.color = new Color(0, 0, 0, 0.35f); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.white; }
    }
}
