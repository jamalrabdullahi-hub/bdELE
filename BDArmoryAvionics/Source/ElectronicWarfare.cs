using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.CounterMeasure;
using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Specular stealth shaping for BDArmory's rendered RCS.
    /// Stock shades every pixel with cos(2θ) (θ = angle between the surface normal and the radar), so a facet tilted
    /// 30° still returns half as much as one facing the radar: angled stealth facets barely help.
    /// Real flat panels reflect in a narrow lobe. Here each pixel's return becomes C·s^k + d·s (s = the stock value):
    /// surfaces facing the radar reflect more, angled facets far less, and a sphere keeps exactly the same RCS
    /// (C = (1-d)(k+1)/2), so BDArmory's 1 m² calibration holds. RAM coatings (part reflectivity) still apply.
    /// </summary>
    public static class StealthRCS
    {
        public static readonly string[] ModeNames = ["Stock", "Low", "Medium", "High"];
        static readonly float[] K = [1f, 4f, 8f, 16f];   // lobe sharpness
        static readonly float[] D = [1f, 0.2f, 0.1f, 0.05f]; // diffuse / edge-diffraction share that never goes away
        static readonly float[] lut = new float[256];
        static int lutMode = -1;
        static readonly FieldInfo pixelsField = AccessTools.Field(typeof(RadarUtils), "pixels");
        internal static bool transpiled;

        public static bool On => AvionicsSettings.StealthModel > 0 && transpiled;

        public static string ModeHelp => AvionicsSettings.StealthModel switch
        {
            0 => "Stock: any surface within ~45° of the radar reflects.",
            1 => "Low: facets 25°/30° off the radar return ~25%/55% less; panels facing it up to 2x more.",
            2 => "Medium: facets 20°/30° off return ~30%/85% less; panels facing it up to 4x more.",
            _ => "High: facets 20° off return ~80% less; panels facing it up to 8x (sharp spikes).",
        };

        public static void SetMode(int mode)
        {
            AvionicsSettings.StealthModel = Mathf.Clamp(mode, 0, 3);
            AvionicsSettings.Save();
            if (HighLogic.LoadedSceneIsFlight)
                try { RadarUtils.ForceUpdateRadarCrossSections(); } catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: RCS refresh failed: {e.Message}"); }
            Debug.Log($"[BDArmoryAvionics]: stealth model {ModeNames[AvionicsSettings.StealthModel]}");
        }

        /// <summary>Replaces Material.SetColor in BDArmory's RCS pass: reflectivity goes in red only, so green carries the pure geometric return.</summary>
        public static void SetRcsColor(Material m, string name, Color c)
        {
            if (On && name == "_RCSCOLOR") c = new Color(c.r, 1f, 1f, 1f);
            m.SetColor(name, c);
        }

        /// <summary>Called with BDArmory's pixel sum just before it is normalised; returns the shaped sum instead.</summary>
        public static float Rescore(float stockSum)
        {
            if (!On) return stockSum;
            if (pixelsField?.GetValue(null) is not Color32[] px || px.Length == 0) return stockSum;
            int mode = AvionicsSettings.StealthModel;
            if (lutMode != mode)
            {
                float k = K[mode], d = D[mode], c = (1f - d) * (k + 1f) / 2f;
                for (int i = 0; i < 256; i++) { float s = i / 255f; lut[i] = c * Mathf.Pow(s, k) + d * s; }
                lutMode = mode;
            }
            double sum = 0;
            for (int i = 0; i < px.Length; i++)
            {
                var p = px[i];
                if (p.g == 0) continue;
                sum += lut[p.g] * (p.r / (float)p.g); // shaped geometric return x material reflectivity
            }
            return (float)sum;
        }
    }

    [HarmonyPatch]
    static class Patch_RCSColor
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(RadarUtils), "RenderSinglePass");

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var setColor = AccessTools.Method(typeof(Material), nameof(Material.SetColor), [typeof(string), typeof(Color)]);
            var mine = AccessTools.Method(typeof(StealthRCS), nameof(StealthRCS.SetRcsColor));
            int n = 0;
            foreach (var ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo mi && mi == setColor)
                {
                    n++;
                    yield return new CodeInstruction(OpCodes.Call, mine).WithLabels(ci.labels).WithBlocks(ci.blocks);
                }
                else yield return ci;
            }
            Debug.Log($"[BDArmoryAvionics]: RCS colour hook: {n} calls");
        }
    }

    [HarmonyPatch(typeof(RadarUtils), nameof(RadarUtils.RenderVesselRadarSnapshot))]
    static class Patch_RCSRescore
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var list = new List<CodeInstruction>(code);
            var rescore = AccessTools.Method(typeof(StealthRCS), nameof(StealthRCS.Rescore));
            for (int i = 0; i < list.Count - 1; i++)
            {
                // rcsVariable / RCS_NORMALIZATION_FACTOR (3.04, inlined)
                if (list[i].opcode == OpCodes.Ldc_R4 && list[i].operand is float f && Mathf.Abs(f - 3.04f) < 1e-4f && list[i + 1].opcode == OpCodes.Div)
                {
                    var call = new CodeInstruction(OpCodes.Call, rescore);
                    call.labels.AddRange(list[i].labels); list[i].labels.Clear();
                    list.Insert(i, call);
                    StealthRCS.transpiled = true;
                    Debug.Log("[BDArmoryAvionics]: stealth shaping hook installed");
                    break;
                }
            }
            if (!StealthRCS.transpiled) Debug.LogWarning("[BDArmoryAvionics]: stealth shaping hook NOT installed (RCS normalisation not found); stock RCS stays");
            return list;
        }
    }

    /// <summary>
    /// Electronic warfare on top of BDArmory's jammers:
    /// - enemy jammers degrade and cut data links (J/S falls off with distance², needs line of sight; EW skill resists),
    /// - ESM: RWR-equipped linked units locate enemy radars and jammers passively and share them as tracks
    ///   (rough position from one sensor, exact when two or more see it),
    /// - jammer modes per craft: Auto (BDArmory guard mode), Barrage (always on), Silent (never).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ElectronicWarfare : MonoBehaviour
    {
        public class Jammer { public Vessel vessel; public BDTeam team; public float strength; public float Radius => strength * 10f; }
        public class Emitter { public Vessel vessel; public BDTeam team; public float radarRange; public float jamRadius; }

        public static readonly List<Jammer> Jammers = new();
        public static readonly List<Emitter> Emitters = new();
        public const int WindowID = 71730333;
        public static Rect rect = new(Screen.width - 760, 120, 460, 500);
        static float nextEnforce;
        Vector2 scroll;

        public static string JammerMode(Vessel v)
        {
            var t = v != null ? ModuleC4ISRTerminal.For(v) : null;
            return t != null ? t.jammer : "Auto";
        }

        static BDTeam TeamOf(Vessel v) { var wm = v.ActiveController().WM; return wm != null ? wm.Team : null; }

        /// <summary>Active jammers and radar emitters among loaded craft (once per data link update).</summary>
        public static void Survey()
        {
            Jammers.Clear(); Emitters.Clear();
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed || VesselModuleRegistry.IgnoredVesselTypes.Contains(v.vesselType)) continue;
                var team = TeamOf(v);
                if (team == null) continue;
                float jam = 0;
                var info = v.gameObject.GetComponent<VesselECMJInfo>();
                if (info != null && info.jammerEnabled && info.jammerStrength > 0) { jam = info.jammerStrength; Jammers.Add(new Jammer { vessel = v, team = team, strength = jam }); }
                float rr = 0;
                foreach (var r in VesselModuleRegistry.GetModules<ModuleRadar>(v)) if (r != null && r.radarEnabled) rr = Mathf.Max(rr, r.radarMaxDistanceDetect * 1000f);
                if (rr > 0 || jam > 0) Emitters.Add(new Emitter { vessel = v, team = team, radarRange = rr, jamRadius = jam * 10f });
            }
        }

        /// <summary>Jamming received by a vessel from a team's enemies (J/S; 1 at the edge of a jammer's zone).</summary>
        public static float JammingAt(Vessel v, BDTeam team)
        {
            float j = 0;
            foreach (var jm in Jammers)
            {
                if (jm.vessel == null || jm.vessel == v || team == null || !team.IsEnemy(jm.team)) continue;
                float d = Mathf.Max(50f, (jm.vessel.CoM - v.CoM).magnitude);
                if (d > jm.Radius * 4f || !DataLinkNetwork.LineOfSight(jm.vessel, v, jm.Radius * 4f)) continue;
                float jamSkill = AvionicsSettings.CombatOperators ? 1f + (OperatorEffects.Skill(jm.vessel, Discipline.Sensors) - Operator.Baseline) * 0.10f : 1f;
                j += jamSkill * (jm.Radius / d) * (jm.Radius / d);
            }
            if (j > 0 && AvionicsSettings.CombatOperators)
                j *= Mathf.Max(0.3f, 1f - (OperatorEffects.Skill(v, Discipline.Sensors) - Operator.Baseline) * 0.10f); // anti-jam discipline
            return j;
        }

        public static void ApplyLinkJamming(TeamNetwork net)
        {
            foreach (var n in net.nodes)
            {
                n.jam = 0; n.jammed = false;
                if (!AvionicsSettings.EWLinkJamming || !n.connected) continue;
                n.jam = JammingAt(n.vessel, net.team);
                if (n.jam <= 0.01f) continue;
                float q = n.quality / (1f + n.jam);
                if (q < 0.3f)
                {
                    n.jammed = true;
                    DataLinkNetwork.SetLink(n, false, LinkPath.None, 0, 0, 0);
                }
                else n.quality = q;
            }
        }

        public static void ReportEmitters(TeamNetwork net, float now)
        {
            if (!AvionicsSettings.EWESM) return;
            foreach (var e in Emitters)
            {
                if (e.vessel == null || !net.team.IsEnemy(e.team)) continue;
                NetNode first = null; int count = 0; bool byJam = false;
                foreach (var n in net.nodes)
                {
                    if (!n.connected || !n.hasRWR || n.vessel == null) continue;
                    // Emissions are heard well beyond the emitter's own detection range; jammers are loud.
                    float range = Mathf.Max(e.radarRange * 1.5f, e.jamRadius * 3f);
                    if (range <= 0 || !DataLinkNetwork.LineOfSight(n.vessel, e.vessel, range)) continue;
                    if (first == null) first = n;
                    count++;
                    byJam |= e.jamRadius > 0 && (n.vessel.CoM - e.vessel.CoM).magnitude < e.jamRadius * 3f && e.radarRange * 1.5f < (n.vessel.CoM - e.vessel.CoM).magnitude;
                }
                if (first == null) continue;
                Vector3 pos = e.vessel.CoM;
                if (count < 2)
                {
                    // Bearing-only from one sensor: range uncertain by ~10%, stable per emitter/sensor pair so the track doesn't jitter.
                    var rng = new System.Random((int)(e.vessel.persistentId ^ first.vessel.persistentId));
                    float err = (float)(rng.NextDouble() * 0.2 - 0.1);
                    pos += (e.vessel.CoM - first.vessel.CoM) * err;
                }
                var t = DataLinkNetwork.Report(net, e.vessel, pos, e.vessel.Velocity(), null, first, now);
                if (t == null) continue;
                t.byESM = true;
                t.emitterJammer = byJam || e.radarRange <= 0;
            }
        }

        // ---- Jammer modes ----

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || Time.time < nextEnforce) return;
            nextEnforce = Time.time + 1f;
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed) continue;
                string mode = JammerMode(v);
                if (mode == "Auto") continue;
                foreach (var j in VesselModuleRegistry.GetModules<ModuleECMJammer>(v))
                {
                    if (j == null || j.isMissileECM || j.alwaysOn) continue;
                    if ((mode == "Barrage" || mode == "Deception") && !j.jammerEnabled) j.EnableJammer();
                    else if (mode == "Silent" && j.jammerEnabled) j.DisableJammer();
                }
            }
        }

        [HarmonyPatch(typeof(MissileFire), nameof(MissileFire.FireECM))]
        static class Patch_FireECM
        {
            static bool Prefix(MissileFire __instance) => JammerMode(__instance.vessel) != "Silent";
        }

        [HarmonyPatch(typeof(MissileFire), nameof(MissileFire.StopECM))]
        static class Patch_StopECM
        {
            static bool Prefix(MissileFire __instance) { var m = JammerMode(__instance.vessel); return m != "Barrage" && m != "Deception"; }
        }

        // ---- EW window ----

        void OnGUI()
        {
            if (!HighLogic.LoadedSceneIsFlight || !BDArmorySetup.GAME_UI_ENABLED || !AvionicsSettings.ShowEW) return;
            rect = AvionicsCore.DrawWindow(WindowID, rect, Draw, "Electronic Warfare", BDArmorySetup.BDGuiSkin);
        }

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { AvionicsSettings.ShowEW = false; return; }
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            float w = rect.width - 16, y = 24;
            var small = AvionicsDraw.LabelSmall;

            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), "Stealth model (radar shaping)", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
            float sw = (w - 12) / 4;
            for (int i = 0; i < StealthRCS.ModeNames.Length; i++)
                if (GUI.Button(new Rect(8 + i * (sw + 4), y, sw, 20), StealthRCS.ModeNames[i], AvionicsSettings.StealthModel == i ? sel : btn)) StealthRCS.SetMode(i);
            y += 22;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), StealthRCS.transpiled ? StealthRCS.ModeHelp : "Stealth hook not installed - see KSP.log", small, AvionicsDraw.ScopeText); y += 14;
            var av = FlightGlobals.ActiveVessel;
            var ti = av != null ? av.gameObject.GetComponent<TargetInfo>() : null;
            if (ti != null) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), $"{av.GetName()}: RCS {ti.radarBaseSignature:F1} m², with ECM/RAM {ti.radarModifiedSignature:F1} m²", small, AvionicsDraw.ScopeText); }
            y += 18;
            AvionicsSettings.RadarTerrainMask = GUI.Toggle(new Rect(8, y, 150, 20), AvionicsSettings.RadarTerrainMask, AvionicsSettings.RadarTerrainMask ? "Terrain masking ON" : "Terrain masking off", AvionicsSettings.RadarTerrainMask ? sel : btn);
            var se = SensorEngine.Latest;
            AvionicsDraw.ColorLabel(new Rect(164, y + 3, w - 156, 16), $"Engine: {SensorEngine.Threads} threads, {se.ms:F1} ms/cycle, {se.pairs} pairs, {se.masked.Count} masked, {se.undetected.Count} faded, {TerrainGrid.TileCount} tiles", small, AvionicsDraw.ScopeText);
            y += 24;
            if (se.burnThrough.Count > 0)
            {
                float minBt = float.MaxValue; foreach (var b in se.burnThrough) minBt = Mathf.Min(minBt, b.burnM);
                AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), $"Your jammer: enemy radars burn through inside {AvionicsDraw.FormatRange(minBt)} ({se.burnThrough.Count} radars, rings on the C2 map)", small, AvionicsDraw.Unknown);
                y += 18;
            }

            AvionicsSettings.EWLinkJamming = GUI.Toggle(new Rect(8, y, (w - 4) / 2, 20), AvionicsSettings.EWLinkJamming, "Jamming cuts links", AvionicsSettings.EWLinkJamming ? sel : btn);
            AvionicsSettings.EWESM = GUI.Toggle(new Rect(12 + (w - 4) / 2, y, (w - 4) / 2, 20), AvionicsSettings.EWESM, "ESM emitter tracks", AvionicsSettings.EWESM ? sel : btn); y += 26;

            var net = DataLinkNetwork.PlayerNetwork;
            if (net == null) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), "No weapon manager on the active craft.", AvionicsDraw.Label, AvionicsDraw.ScopeText); GUI.DragWindow(); return; }

            scroll = GUI.BeginScrollView(new Rect(8, y, w, rect.height - y - 6), scroll, new Rect(0, 0, w - 18, 40 + (net.nodes.Count + Jammers.Count + net.tracks.Count) * 18 + 80));
            float yy = 0, cw = w - 20;
            AvionicsDraw.ColorLabel(new Rect(0, yy, cw, 14), "OUR JAMMERS / LINKS", AvionicsDraw.Header, AvionicsDraw.HudText); yy += 16;
            foreach (var n in net.nodes)
            {
                if (n.vessel == null) continue;
                var jm = Jammers.Find(j => j.vessel == n.vessel);
                bool hasJammer = VesselModuleRegistry.GetModules<ModuleECMJammer>(n.vessel).Exists(j => j != null && !j.isMissileECM);
                string link = n.jammed ? "LINK JAMMED" : !n.connected ? "no link" : n.jam > 0.05f ? $"link {n.quality:P0} (jam {n.jam:F1})" : $"link {n.quality:P0}";
                var col = n.jammed ? AvionicsDraw.Hostile : n.jam > 0.05f ? AvionicsDraw.Unknown : AvionicsDraw.Friendly;
                AvionicsDraw.ColorLabel(new Rect(0, yy + 2, cw - 120, 14), $"{n.Name}  {link}{(jm != null ? $"  JAMMING {jm.Radius / 1000f:F1} km" : "")}{(n.hasRWR ? "  ESM" : "")}", small, col);
                if (hasJammer)
                {
                    var term = n.terminal;
                    string mode = term != null ? term.jammer : "Auto";
                    if (GUI.Button(new Rect(cw - 116, yy, 116, 17), $"Jam: {mode}", mode == "Auto" ? btn : sel))
                    {
                        if (term == null) ScreenMessages.PostScreenMessage("This craft has no C4ISR terminal (add a weapon manager / command part)", 3f, ScreenMessageStyle.UPPER_CENTER);
                        else term.jammer = ModuleC4ISRTerminal.JammerModes[(Array.IndexOf(ModuleC4ISRTerminal.JammerModes, mode) + 1) % ModuleC4ISRTerminal.JammerModes.Length];
                    }
                }
                yy += 18;
            }
            yy += 6;
            AvionicsDraw.ColorLabel(new Rect(0, yy, cw, 14), "ENEMY JAMMERS", AvionicsDraw.Header, AvionicsDraw.HudText); yy += 16;
            int shown = 0;
            foreach (var j in Jammers)
            {
                if (j.vessel == null || !net.team.IsEnemy(j.team)) continue;
                var tr = net.Track(j.vessel);
                string where = tr == null ? "not located" : av != null ? $"{(tr.position - av.CoM).magnitude / 1000f:F1} km ({tr.SourceString.Trim()})" : "";
                AvionicsDraw.ColorLabel(new Rect(0, yy, cw, 14), $"{(tr != null ? tr.Label + " " + j.vessel.GetName() : "Unknown jammer")}  zone {j.Radius / 1000f:F1} km  {where}", small, AvionicsDraw.Hostile);
                yy += 16; shown++;
            }
            if (shown == 0) { AvionicsDraw.ColorLabel(new Rect(0, yy, cw, 14), "none active", small, AvionicsDraw.ScopeText); yy += 16; }
            yy += 6;
            AvionicsDraw.ColorLabel(new Rect(0, yy, cw, 14), "ESM EMITTER TRACKS", AvionicsDraw.Header, AvionicsDraw.HudText); yy += 16;
            shown = 0;
            foreach (var t in net.tracks.Values)
            {
                if (!t.byESM) continue;
                string acc = t.byRadar || t.byIRST ? "fixed by sensors" : t.reporters.Count >= 2 ? "triangulated" : "bearing (±10% range)";
                AvionicsDraw.ColorLabel(new Rect(0, yy, cw, 14), $"{t.Label} {t.Name}  {(t.emitterJammer ? "jammer" : "radar")}  {acc}", small, AvionicsDraw.Unknown);
                yy += 16; shown++;
            }
            if (shown == 0) AvionicsDraw.ColorLabel(new Rect(0, yy, cw, 14), AvionicsSettings.EWESM ? "none (needs an RWR switched on, on a linked craft)" : "ESM off", small, AvionicsDraw.ScopeText);
            GUI.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, rect.width - 22, 20));
        }
    }
}
