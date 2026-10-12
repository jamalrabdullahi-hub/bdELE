using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Live stealth in flight: for every hostile radar near you, your radar cross-section from where that radar actually sees
    /// you (BDArmory's own aspected RCS, with your jammers and the active stealth model), how far it can detect / lock you,
    /// how far you are, and which aspect to give it to be least visible. Window + HUD markers on the radars.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class LiveStealth : MonoBehaviour
    {
        public static bool Show;
        const int WindowID = 71730393;
        Rect rect = new(Screen.width - 520, 300, 460, 200);

        public class Threat
        {
            public Vessel v; public ModuleRadar radar; public string name;
            public float dist, rcs, detectKm, lockKm, bestAz, bestRcs, az; public bool active, detected, locked;
        }
        public static readonly List<Threat> Threats = new();
        public static float FrontRating = float.NaN;
        float nextScan, nextEval;
        readonly List<(Vessel v, ModuleRadar r)> radars = new();

        static string TeamOf(Vessel v) { var wm = v != null ? v.ActiveController().WM : null; return wm != null && wm.Team != null ? wm.Team.Name : null; }

        void Update()
        {
            var me = FlightGlobals.ActiveVessel;
            if (me == null || me.packed) { Threats.Clear(); return; }
            if (!Show && !AvionicsSettings.StealthHud) return;
            if (Time.time >= nextScan)
            {
                nextScan = Time.time + 2f;
                radars.Clear();
                string mine = TeamOf(me);
                foreach (var v in FlightGlobals.VesselsLoaded)
                {
                    if (v == null || v == me) continue;
                    string t = TeamOf(v);
                    if (mine != null && t == mine) continue;   // friendly
                    foreach (var r in VesselModuleRegistry.GetModules<ModuleRadar>(v)) if (r != null && r.canScan) radars.Add((v, r));
                }
            }
            if (Time.time < nextEval) return;
            nextEval = Time.time + 0.25f;
            Threats.Clear();
            TargetInfo ti;
            // read BDArmory's own record of this craft (with its ECM); only ask for a render the first time
            ti = me.gameObject.GetComponent<TargetInfo>();
            if (ti == null || ti.radarBaseSignature <= 0) { try { ti = RadarUtils.GetVesselRadarSignature(me, false); } catch { return; } }
            if (ti == null) return;
            var rt = me.ReferenceTransform;
            foreach (var (v, r) in radars)
            {
                if (v == null || r == null) continue;
                Vector3 rp = r.transform.position; float d = (rp - me.CoM).magnitude;
                float rcs;
                try { rcs = RadarUtils.GetVesselRadarSignatureAtAspect(ti, rp, d); } catch { rcs = ti.radarModifiedSignature; }
                float km = d / 1000f;
                var th = new Threat
                {
                    v = v, radar = r, name = $"{v.GetDisplayName()} ({r.part.partInfo.title})", dist = km, rcs = rcs, active = r.radarEnabled,
                    detectKm = RangeFor(r.radarDetectionCurve, r.radarMaxDistanceDetect, rcs),
                    lockKm = r.canLock ? RangeFor(r.radarLockTrackCurve, r.radarMaxDistanceLockTrack, rcs) : -1,
                };
                th.detected = km <= th.detectKm; th.locked = th.lockKm > 0 && km <= th.lockKm;
                // which side to show it: same elevation, best azimuth (craft are left/right symmetric, so 0..180)
                Vector3 dir = rp - rt.position;
                th.az = VectorUtils.GetAngleOnPlane(dir, rt.up, rt.right);
                float el = -VectorUtils.GetElevation(dir, rt.forward, d);
                th.bestRcs = float.MaxValue;
                if (ti.radarSignatureMatrix != null)
                    for (float az = 0; az <= 180; az += 5)
                    {
                        float s = RadarUtils.RCSMatrixEval(ti.radarSignatureMatrix, ti.radarBaseSignature, az, el) * ti.radarModifiedSignature / Mathf.Max(1e-4f, ti.radarBaseSignature);
                        if (s < th.bestRcs) { th.bestRcs = s; th.bestAz = az; }
                    }
                Threats.Add(th);
            }
            Threats.Sort((a, b) => (a.dist - a.detectKm).CompareTo(b.dist - b.detectKm));   // most exposed first
            // live rating: the frontal sector as in the editor
            if (ti.radarSignatureMatrix != null)
            {
                float sum = 0; int n = 0;
                for (float az = 0; az <= 45; az += 5) for (float el = -15; el <= 15; el += 15) { sum += Mathf.Max(1e-4f, RadarUtils.RCSMatrixEval(ti.radarSignatureMatrix, ti.radarBaseSignature, az, el)); n++; }
                FrontRating = 20f - 10f * Mathf.Log10(sum / n * ti.radarModifiedSignature / Mathf.Max(1e-4f, ti.radarBaseSignature));
            }
        }

        /// <summary>Farthest range (km) where the curve's minimum detectable RCS is at most this RCS.</summary>
        public static float RangeFor(FloatCurve c, float maxKm, float rcs)
        {
            if (c == null) return 0;
            for (float d = maxKm; d >= 0; d -= 0.5f) if (c.Evaluate(d) <= rcs) return d;
            return 0;
        }

        static Color Col(Threat t) => t.locked ? AvionicsDraw.Hostile : t.detected ? new Color(1f, 0.6f, 0.2f) : t.dist < t.detectKm * 1.25f ? AvionicsDraw.Unknown : AvionicsDraw.Friendly;

        void OnGUI()
        {
            if (!BDArmorySetup.GAME_UI_ENABLED || KillCam.Playing) return;
            if (AvionicsSettings.StealthHud && !MapView.MapIsEnabled && Event.current.type == EventType.Repaint) DrawHud();
            if (Show) rect = AvionicsCore.DrawWindow(WindowID, rect, Window, "Live stealth", BDArmorySetup.BDGuiSkin);
        }

        void DrawHud()
        {
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null; if (cam == null) return;
            var st = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.UpperCenter };
            foreach (var t in Threats)
            {
                if (t.v == null || t.radar == null) continue;
                var sp = cam.WorldToScreenPoint(t.radar.transform.position);
                if (sp.z <= 0) continue;
                var c = Col(t);
                var p = new Vector2(sp.x, Screen.height - sp.y);
                GUI.color = c;
                GUI.Label(new Rect(p.x - 90, p.y - 34, 180, 30), $"{(t.locked ? "LOCK" : t.detected ? "SEES YOU" : "radar")}{(t.active ? "" : " (off)")}\n{t.dist:F1} km · det {t.detectKm:F1} · {t.rcs:F2} m²", st);
                GUI.Label(new Rect(p.x - 6, p.y - 8, 12, 16), "◇", st);
            }
            GUI.color = Color.white;
        }

        void Window(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; return; }
            var small = AvionicsDraw.LabelSmall; float w = rect.width - 16, y = 24;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), float.IsNaN(FrontRating) ? "Stealth rating: (aspected RCS off in BDArmory settings)" : $"Stealth rating now: {FrontRating:F1}   model {StealthRCS.ModeNames[AvionicsSettings.StealthModel]}", AvionicsDraw.Header, AvionicsDraw.HudText);
            y += 18;
            AvionicsSettings.StealthHud = GUI.Toggle(new Rect(8, y, w, 18), AvionicsSettings.StealthHud, AvionicsSettings.StealthHud ? "HUD markers on radars: on" : "HUD markers on radars: off", AvionicsSettings.StealthHud ? BDArmorySetup.SelectedButtonStyle : BDArmorySetup.ButtonStyle);
            y += 22;
            if (Threats.Count == 0) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), "No hostile radars loaded nearby.", small, AvionicsDraw.ScopeText); y += 16; }
            foreach (var t in Threats.Take(10))
            {
                var c = Col(t);
                string status = t.locked ? "LOCKED" : t.detected ? "DETECTED" : $"clear by {t.dist - t.detectKm:F1} km";
                AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), $"{t.name}{(t.active ? "" : "  (radar off)")}", AvionicsDraw.Label, c); y += 14;
                AvionicsDraw.ColorLabel(new Rect(16, y, w, 14), $"{status} · you {t.dist:F1} km · detects you at {t.detectKm:F1} km{(t.lockKm >= 0 ? $", locks at {t.lockKm:F1}" : "")} · RCS {t.rcs:F2} m²", small, c); y += 13;
                if (t.bestRcs < float.MaxValue && t.bestRcs < t.rcs * 0.8f)
                {
                    float turn = Mathf.Abs(t.az) - t.bestAz;   // positive: bring the radar closer to the nose
                    string how = Mathf.Abs(turn) < 5 ? "hold this aspect" : turn > 0 ? $"turn {turn:F0}° toward it" : $"turn {-turn:F0}° away from it";
                    AvionicsDraw.ColorLabel(new Rect(16, y, w, 14), $"best: radar {t.bestAz:F0}° off your nose ({t.bestRcs:F2} m², detect {RangeFor(t.radar.radarDetectionCurve, t.radar.radarMaxDistanceDetect, t.bestRcs):F1} km): {how}", small, AvionicsDraw.ScopeText); y += 13;
                }
                y += 3;
            }
            rect.height = y + 6;
            GUI.DragWindow();
        }
    }
}
