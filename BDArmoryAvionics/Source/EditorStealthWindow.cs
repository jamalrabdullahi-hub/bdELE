using System.Linq;
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

using BDArmory.CounterMeasure;
using BDArmory.Radar;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// VAB/SPH: renders the craft's radar cross-section under every stealth model (Stock / Low / Medium / High),
    /// plots it around the craft, shows how far a chosen radar detects and locks it, and what its jammers, RWR (ESM)
    /// and radars mean for electronic warfare.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorStealthWindow : MonoBehaviour
    {
        public static bool Show;
        const int WindowID = 71730321;
        Rect rect = new(Screen.width / 2 - 340, 70, 680, 660);

        class Result { public float total, front, side, rear, top, bottom; public float[,] matrix; }
        readonly Result[] results = new Result[4];
        bool analysed;
        string note = "";
        float rcsReduction = 1f, rcsOverride = -1f;
        readonly List<ModuleRadar> radars = new();
        readonly List<string> radarNames = new();
        int radarIdx;
        float polarEl;
        static readonly FieldInfo rcsTotalField = AccessTools.Field(typeof(RadarUtils), "rcsTotal");

        // EW fit
        float jam, lockBreak, ecmReduction = 1f, ownRadarKm;
        int jammers;
        bool rwr;
        string jamMode = "Auto";

        static readonly Color StockCol = new(0.6f, 0.6f, 0.6f), ModelCol = AvionicsDraw.Friendly;

        void Start()
        {
            foreach (var ap in PartLoader.LoadedPartsList)
            {
                var r = ap.partPrefab != null ? ap.partPrefab.FindModuleImplementing<ModuleRadar>() : null;
                if (r == null || !r.canScan) continue;
                radars.Add(r); radarNames.Add(ap.title);
            }
        }

        void OnDestroy() => EditorLogic.fetch?.Unlock("BDAStealthEditor");

        void OnGUI()
        {
            if (!Show || !HighLogic.LoadedSceneIsEditor) { return; }
            rect = AvionicsCore.DrawWindow(WindowID, rect, Draw, "Stealth & EW analysis", BDArmorySetup.BDGuiSkin);
            EditorLogic.fetch?.Unlock("BDAStealthEditor");
        }

        void Analyse()
        {
            analysed = false;
            if (EditorLogic.RootPart == null || EditorLogic.fetch.ship == null) { note = "No craft."; return; }
            int saved = AvionicsSettings.StealthModel;
            try
            {
                for (int m = 0; m < 4; m++)
                {
                    AvionicsSettings.StealthModel = m;
                    var v = new Vessel { parts = EditorLogic.fetch.ship.Parts, vesselType = VesselType.Plane };
                    RadarUtils.RenderVesselRadarSnapshot(v, EditorLogic.RootPart.transform, null, true);
                    var r = new Result { total = rcsTotalField != null ? (float)rcsTotalField.GetValue(null) : 0f };
                    r.matrix = RadarUtils.RCSMatrix != null ? (float[,])RadarUtils.RCSMatrix.Clone() : null;
                    r.front = At(r, 0, 0); r.side = At(r, 90, 0); r.rear = At(r, 180, 0); r.top = At(r, 0, 90); r.bottom = At(r, 0, -90);
                    results[m] = r;
                }
                analysed = true;
                note = StealthRCS.transpiled ? "" : "Stealth hook not installed: all models show stock values (see KSP.log).";
                Debug.Log($"[BDArmoryAvionics]: editor RCS: stock {results[0].total:F2}, low {results[1].total:F2}, medium {results[2].total:F2}, high {results[3].total:F2} m²");
            }
            catch (Exception e) { note = $"RCS render failed: {e.Message}"; Debug.LogWarning($"[BDArmoryAvionics]: editor RCS analysis failed: {e}"); }
            finally { AvionicsSettings.StealthModel = saved; }
            SurveyEW();
        }

        static float At(Result r, float az, float el)
        {
            if (r.matrix == null) return r.total;
            try { return RadarUtils.RCSMatrixEval(r.matrix, r.total, az, el); } catch { return r.total; }
        }

        void SurveyEW()
        {
            jam = 0; lockBreak = 0; ecmReduction = 1f; rcsOverride = -1f; jammers = 0; rwr = false; ownRadarKm = 0; jamMode = "Auto";
            float spam = 1, lbf = 1, red = 1; int redCount = 0;
            foreach (var p in EditorLogic.fetch.ship.Parts)
            {
                foreach (var j in p.FindModulesImplementing<ModuleECMJammer>())
                {
                    if (j.isMissileECM) continue;
                    jammers++;
                    if (j.signalSpam) { jam += spam * j.jammerStrength; spam *= 0.75f; }
                    if (j.lockBreaker) { lockBreak += lbf * j.lockBreakerStrength; lbf *= 0.65f; }
                    if (j.rcsReduction) { red *= j.rcsReductionFactor; redCount++; if (rcsOverride < j.rcsOverride) rcsOverride = j.rcsOverride; }
                }
                if (p.FindModuleImplementing<RadarWarningReceiver>() != null) rwr = true;
                foreach (var r in p.FindModulesImplementing<ModuleRadar>()) ownRadarKm = Mathf.Max(ownRadarKm, r.radarMaxDistanceDetect);
                var t = p.FindModuleImplementing<ModuleC4ISRTerminal>();
                if (t != null) jamMode = t.jammer;
            }
            ecmReduction = redCount > 0 ? Mathf.Max(red * redCount, 0f) : 1f; // same formula as BDArmory's VesselECMJInfo
            rcsReduction = ecmReduction;
        }

        float Effective(float rcs) => rcsOverride > 0 ? rcsOverride : rcs * rcsReduction;

        static float DetectKm(ModuleRadar r, float rcs)
        {
            if (r == null || !r.canScan) return -1;
            for (float d = r.radarMaxDistanceDetect; d >= 0; d -= 0.5f) if (r.radarDetectionCurve.Evaluate(d) <= rcs) return d;
            return 0;
        }

        static float LockKm(ModuleRadar r, float rcs)
        {
            if (r == null || !r.canLock) return -1;
            for (float d = r.radarMaxDistanceLockTrack; d >= 0; d -= 0.5f) if (r.radarLockTrackCurve.Evaluate(d) <= rcs) return d;
            return 0;
        }

        static string Km(float k) => k < 0 ? "n/a" : k == 0 ? "never" : $"{k:F1} km";
        static string M2(float v) => v >= 100 ? $"{v:F0}" : v >= 1 ? $"{v:F1}" : $"{v:F2}";

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; EditorLogic.fetch?.Unlock("BDAStealthEditor"); return; }
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            var small = AvionicsDraw.LabelSmall;
            float w = rect.width - 16, y = 24;

            if (GUI.Button(new Rect(8, y, 130, 22), analysed ? "Re-analyse craft" : "Analyse craft", btn)) Analyse();
            if (GUI.Button(new Rect(142, y, 70, 22), "Export 3D", btn)) note = CraftExport.ExportShip();
            AvionicsDraw.ColorLabel(new Rect(216, y + 3, w - 210, 16), "Active model (used in flight):", small, AvionicsDraw.ScopeText);
            float mw = 64;
            for (int i = 0; i < 4; i++)
                if (GUI.Button(new Rect(rect.width - 8 - (4 - i) * (mw + 3), y, mw, 22), StealthRCS.ModeNames[i], AvionicsSettings.StealthModel == i ? sel : btn)) StealthRCS.SetMode(i);
            y += 28;
            if (!string.IsNullOrEmpty(note)) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), note, small, AvionicsDraw.Unknown); y += 16; }
            if (!analysed) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), "Press Analyse: renders the craft's radar return from every angle under each model (a few seconds).", AvionicsDraw.Label, AvionicsDraw.ScopeText); GUI.DragWindow(); return; }

            // Radar picker
            var radar = radars.Count > 0 ? radars[Mathf.Clamp(radarIdx, 0, radars.Count - 1)] : null;
            if (GUI.Button(new Rect(8, y, 24, 20), "<", btn) && radars.Count > 0) radarIdx = (radarIdx + radars.Count - 1) % radars.Count;
            AvionicsDraw.ColorLabel(new Rect(38, y + 2, 360, 16), radar != null ? $"Against: {radarNames[radarIdx]} (max {radar.radarMaxDistanceDetect:F0} km)" : "No scanning radars installed", AvionicsDraw.Label, AvionicsDraw.HudText);
            if (GUI.Button(new Rect(400, y, 24, 20), ">", btn) && radars.Count > 0) radarIdx = (radarIdx + 1) % radars.Count;
            y += 26;

            // Table
            float[] cx = [8, 80, 150, 210, 270, 330, 390, 450, 560];
            string[] head = ["Model", "Overall m²", "Front", "Side", "Rear", "Top", "Bottom", "Detected at", "Locked at"];
            for (int i = 0; i < head.Length; i++) AvionicsDraw.ColorLabel(new Rect(cx[i], y, 110, 14), head[i], AvionicsDraw.Header, AvionicsDraw.ScopeText);
            y += 16;
            for (int m = 0; m < 4; m++)
            {
                var r = results[m];
                var col = m == AvionicsSettings.StealthModel ? ModelCol : m == 0 ? StockCol : AvionicsDraw.HudText;
                float eff = Effective(r.total);
                string pct = m == 0 || results[0].total <= 0 ? "" : $" ({(r.total / results[0].total - 1f) * 100f:+0;-0}%)";
                AvionicsDraw.ColorLabel(new Rect(cx[0], y, 70, 14), StealthRCS.ModeNames[m] + (m == AvionicsSettings.StealthModel ? " *" : ""), AvionicsDraw.Label, col);
                AvionicsDraw.ColorLabel(new Rect(cx[1], y, 80, 14), M2(r.total) + pct, small, col);
                AvionicsDraw.ColorLabel(new Rect(cx[2], y, 60, 14), M2(r.front), small, col);
                AvionicsDraw.ColorLabel(new Rect(cx[3], y, 60, 14), M2(r.side), small, col);
                AvionicsDraw.ColorLabel(new Rect(cx[4], y, 60, 14), M2(r.rear), small, col);
                AvionicsDraw.ColorLabel(new Rect(cx[5], y, 60, 14), M2(r.top), small, col);
                AvionicsDraw.ColorLabel(new Rect(cx[6], y, 60, 14), M2(r.bottom), small, col);
                AvionicsDraw.ColorLabel(new Rect(cx[7], y, 110, 14), $"{Km(DetectKm(radar, eff))} (nose {Km(DetectKm(radar, Effective(r.front)))})", small, col);
                AvionicsDraw.ColorLabel(new Rect(cx[8], y, 110, 14), $"{Km(LockKm(radar, eff))}", small, col);
                y += 15;
            }
            // One number to compare designs: frontal sector (±45° azimuth, ±15° elevation) under the active model.
            {
                var ar = results[AvionicsSettings.StealthModel];
                float sum = 0; int n = 0;
                for (float az = -45; az <= 45; az += 5) for (float el = -15; el <= 15; el += 15) { sum += Mathf.Max(1e-4f, Effective(At(ar, az, el))); n++; }
                float avg = sum / n, rating = 20f - 10f * Mathf.Log10(avg);
                string grade = rating >= 35 ? "very low observable" : rating >= 28 ? "low observable" : rating >= 22 ? "reduced signature" : rating >= 16 ? "conventional" : "large target";
                AvionicsDraw.ColorLabel(new Rect(8, y + 2, w, 18), $"Stealth rating: {rating:F1}   ({grade}; frontal average {M2(avg)} m², {StealthRCS.ModeNames[AvionicsSettings.StealthModel]} model)", AvionicsDraw.Header, rating >= 28 ? AvionicsDraw.Friendly : AvionicsDraw.HudText);
                y += 22;
            }
            if (rcsReduction != 1f || rcsOverride > 0) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), rcsOverride > 0 ? $"RCS override part: {rcsOverride:F2} m² regardless of shape" : $"RCS-reduction parts: x{rcsReduction:F2} applied to detection ranges", small, AvionicsDraw.Unknown); y += 15; }
            y += 6;

            if (GUI.Button(new Rect(8, y, 220, 20), allRadars ? "Back to plot / EW" : $"Detection by every radar ({radars.Count})", allRadars ? sel : btn)) allRadars = !allRadars;
            y += 24;
            if (allRadars) { DrawRadarTable(y, w); GUI.DragWindow(new Rect(0, 0, rect.width - 22, 20)); return; }

            // Polar plot (left) + EW (right)
            float size = 250;
            var plot = new Rect(8, y, size, size);
            DrawPolar(plot);
            AvionicsDraw.ColorLabel(new Rect(8, y + size + 2, size, 14), $"Grey stock, cyan {StealthRCS.ModeNames[Mathf.Max(1, AvionicsSettings.StealthModel)]}. Nose up. Rings 10 dB.", small, AvionicsDraw.ScopeText);
            if (GUI.Button(new Rect(8, y + size + 18, 60, 18), "Level", Mathf.Approximately(polarEl, 0) ? sel : btn)) polarEl = 0;
            if (GUI.Button(new Rect(72, y + size + 18, 60, 18), "From +30°", Mathf.Approximately(polarEl, 30) ? sel : btn)) polarEl = 30;
            if (GUI.Button(new Rect(136, y + size + 18, 60, 18), "From -30°", Mathf.Approximately(polarEl, -30) ? sel : btn)) polarEl = -30;

            float ex = 8 + size + 14, ew = rect.width - ex - 8, ey = y;
            DrawEW(ex, ref ey, ew, radar);
            GUI.DragWindow(new Rect(0, 0, rect.width - 22, 20));
        }

        bool allRadars; Vector2 radarScroll;

        /// <summary>Every scanning radar in the game against this craft (active model, with its ECM): detect / lock range by aspect.</summary>
        void DrawRadarTable(float y, float w)
        {
            var r = results[AvionicsSettings.StealthModel];
            float nose = Effective(r.front), side = Effective(r.side), tail = Effective(r.rear), all = Effective(r.total);
            float[] cx = [8, 250, 320, 390, 460, 530, 600];
            string[] head = ["Radar (detect at: nose / side / tail / overall)", "Nose", "Side", "Tail", "Overall", "Lock nose", "Max"];
            for (int i = 0; i < head.Length; i++) AvionicsDraw.ColorLabel(new Rect(cx[i], y, i == 0 ? 240 : 70, 14), head[i], AvionicsDraw.Header, AvionicsDraw.ScopeText);
            y += 16;
            var order = Enumerable.Range(0, radars.Count).OrderByDescending(i => DetectKm(radars[i], nose)).ToList();
            var view = new Rect(8, y, w, rect.height - y - 10);
            radarScroll = GUI.BeginScrollView(view, radarScroll, new Rect(0, 0, w - 18, order.Count * 15 + 4));
            float ry = 0;
            foreach (int i in order)
            {
                var rd = radars[i];
                float dn = DetectKm(rd, nose);
                var col = dn <= 0 ? AvionicsDraw.Friendly : dn < rd.radarMaxDistanceDetect * 0.5f ? AvionicsDraw.Unknown : AvionicsDraw.Hostile;
                AvionicsDraw.ColorLabel(new Rect(0, ry, 240, 14), radarNames[i], AvionicsDraw.LabelSmall, col);
                AvionicsDraw.ColorLabel(new Rect(cx[1] - 8, ry, 70, 14), Km(dn), AvionicsDraw.LabelSmall, col);
                AvionicsDraw.ColorLabel(new Rect(cx[2] - 8, ry, 70, 14), Km(DetectKm(rd, side)), AvionicsDraw.LabelSmall, col);
                AvionicsDraw.ColorLabel(new Rect(cx[3] - 8, ry, 70, 14), Km(DetectKm(rd, tail)), AvionicsDraw.LabelSmall, col);
                AvionicsDraw.ColorLabel(new Rect(cx[4] - 8, ry, 70, 14), Km(DetectKm(rd, all)), AvionicsDraw.LabelSmall, col);
                AvionicsDraw.ColorLabel(new Rect(cx[5] - 8, ry, 70, 14), Km(LockKm(rd, nose)), AvionicsDraw.LabelSmall, col);
                AvionicsDraw.ColorLabel(new Rect(cx[6] - 8, ry, 70, 14), $"{rd.radarMaxDistanceDetect:F0} km", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
                ry += 15;
            }
            GUI.EndScrollView();
        }

        void DrawPolar(Rect r)
        {
            var stock = results[0];
            var cur = results[Mathf.Max(1, AvionicsSettings.StealthModel)];
            float maxRcs = 0.01f;
            for (int a = 0; a <= 180; a += 5) maxRcs = Mathf.Max(maxRcs, At(stock, a, polarEl), At(cur, a, polarEl));
            float topDb = Mathf.Ceil(10f * Mathf.Log10(maxRcs) / 10f) * 10f, span = 40f;
            Vector2 c = r.center; float R = r.width / 2 - 6;
            AvionicsDraw.FilledRect(r, AvionicsDraw.ScopeBackground);
            if (!AvionicsDraw.Begin()) return;
            for (int ring = 1; ring <= 4; ring++) AvionicsDraw.Circle(c, R * ring / 4f, 1f, AvionicsDraw.GridFaint, 48);
            AvionicsDraw.Line(new Vector2(c.x, c.y - R), new Vector2(c.x, c.y + R), 1f, AvionicsDraw.GridFaint);
            AvionicsDraw.Line(new Vector2(c.x - R, c.y), new Vector2(c.x + R, c.y), 1f, AvionicsDraw.GridFaint);
            Trace(stock, c, R, topDb, span, StockCol);
            Trace(cur, c, R, topDb, span, ModelCol);
            AvionicsDraw.Triangle(new Vector2(c.x, c.y - 10), 6, 0, 1.5f, AvionicsDraw.HudText);
            AvionicsDraw.End();
            AvionicsDraw.ColorLabel(new Rect(r.x + 4, r.y + 2, 120, 14), $"{topDb:F0} dBsm outer", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
        }

        void Trace(Result res, Vector2 c, float R, float topDb, float span, Color col)
        {
            Vector2 prev = default; bool first = true;
            for (int a = -180; a <= 180; a += 5)
            {
                float rcs = At(res, Mathf.Abs(a), polarEl);
                float db = 10f * Mathf.Log10(Mathf.Max(rcs, 1e-4f));
                float rr = Mathf.Clamp01((db - (topDb - span)) / span) * R;
                float ang = a * Mathf.Deg2Rad;
                var p = new Vector2(c.x + Mathf.Sin(ang) * rr, c.y - Mathf.Cos(ang) * rr);
                if (!first) AvionicsDraw.Line(prev, p, 1.5f, col);
                prev = p; first = false;
            }
        }

        void DrawEW(float x, ref float y, float w, ModuleRadar radar)
        {
            var small = AvionicsDraw.LabelSmall;
            void H(ref float yy, string t) { AvionicsDraw.ColorLabel(new Rect(x, yy, w, 14), t, AvionicsDraw.Header, AvionicsDraw.HudText); yy += 16; }
            void L(ref float yy, string t, Color c) { AvionicsDraw.ColorLabel(new Rect(x, yy, w, 14), t, small, c); yy += 14; }
            var dim = AvionicsDraw.ScopeText; var hi = AvionicsDraw.HudText;
            float rcs = Effective(results[AvionicsSettings.StealthModel].total);

            H(ref y, "JAMMERS");
            if (jammers == 0) L(ref y, "None (add a BDArmory ECM jammer part).", dim);
            else
            {
                float zone = jam * 10f;
                float modified = rcs * (jam / Mathf.Max(rcs, 0.01f) / 100f + 1f);
                float garble = (jam / Mathf.Max(rcs, 0.01f) / 100f + 1f) * jam;
                float lbf = rcs <= 0 ? 0 : Mathf.Max(Mathf.Clamp01(rcs / modified) * (1f - lockBreak / rcs / 100f), 0f);
                L(ref y, $"{jammers} jammer(s): noise {jam:F0}, lock-break {lockBreak:F0}. Mode: {jamMode}", hi);
                L(ref y, $"Jamming on, radars see {M2(modified)} m² (x{modified / Mathf.Max(rcs, 0.01f):F1}):", hi);
                if (radar != null) L(ref y, $"  detected at {Km(DetectKm(radar, modified))} instead of {Km(DetectKm(radar, rcs))}", AvionicsDraw.Unknown);
                L(ref y, $"Radar picture garbled within {garble / 1000f:F1} km of you", dim);
                L(ref y, $"Radar locks on you hold at {lbf:P0} strength", dim);
                H(ref y, "DATA LINK JAMMING (enemy links)");
                L(ref y, $"Cuts links within {zone * 0.655f / 1000f:F1} km", AvionicsDraw.Hostile);
                L(ref y, $"Halves link quality at {zone / 1000f:F1} km, -20% at {zone * 2f / 1000f:F1} km", hi);
                L(ref y, $"Enemy RWRs warn within {zone / 1000f:F1} km; enemy ESM locates you", dim);
                L(ref y, $"  out to {zone * 3f / 1000f:F1} km while jamming", dim);
                L(ref y, "Crew Sensors/EW skill: +/-10% per level from 2.", dim);
            }
            y += 4;
            H(ref y, "ESM / EMISSIONS");
            L(ref y, rwr ? "RWR fitted: locates enemy radars at 1.5x their range, jammers at 3x their zone, and shares them on the data link." : "No RWR: can't locate emitters passively.", rwr ? hi : dim);
            if (ownRadarKm > 0) L(ref y, $"Own radar ({ownRadarKm:F0} km) is heard by enemy ESM out to {ownRadarKm * 1.5f:F0} km while on.", AvionicsDraw.Unknown);
            else L(ref y, "No radar: emits nothing unless jamming.", dim);
            y += 4;
            H(ref y, "SHAPING TIPS");
            L(ref y, "Lower is better. Big side/front numbers = flat panels", dim);
            L(ref y, "facing that way: angle them 20-30° (more with Medium/High).", dim);
            L(ref y, "Parts with smoothed normals behave like curved surfaces.", dim);
        }
    }
}
