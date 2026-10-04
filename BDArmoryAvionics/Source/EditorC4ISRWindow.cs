using System.Collections;
using System.Collections.Generic;
using KSP.UI.Screens;
using UnityEngine;

using BDArmory.Control;
using BDArmory.CounterMeasure;
using BDArmory.Radar;
using BDArmory.UI;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Avionics
{
    /// <summary>
    /// VAB/SPH window: checks the craft's C4ISR fit while it's being built and sets its data link terminal
    /// (role, data link, IADS membership, EMCON) for every terminal on the craft at once.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorC4ISRWindow : MonoBehaviour
    {
        const int WindowID = 71730320;
        ApplicationLauncherButton button;
        bool show;
        Rect rect = new(Screen.width - 420, 120, 360, 560);
        Vector2 scroll;
        float nextScan;
        Fit fit = new();

        class Fit
        {
            public int parts, terminals, wms, missiles, radars, irsts, guns, crewParts, crew, antennas, relays, kerbNet, probes;
            public float missileRange, radarRange;
            public double antennaPower;
            public readonly List<ModuleC4ISRTerminal> terms = new();
            public readonly List<string> warnings = new();
            public int flares, chaff;
            public readonly List<ModuleCMProgram> cmPrograms = new();
            public readonly List<BDModularGuidance> modular = new();
            public BDModulePilotAI pilot;
        }

        void Start()
        {
            AvionicsSettings.Load();
            StartCoroutine(AddButton());
        }

        IEnumerator AddButton()
        {
            yield return new WaitUntil(() => ApplicationLauncher.Ready);
            button = ApplicationLauncher.Instance.AddModApplication(() => show = true, () => show = false, null, null, null, null,
                ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPH, AvionicsDraw.IconTexture);
        }

        void OnDestroy()
        {
            if (button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(button);
        }

        void Update()
        {
            if (!show || Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 0.5f;
            Scan();
        }

        void Scan()
        {
            fit = new Fit();
            var ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            if (ship == null) return;
            foreach (var p in ship.parts)
            {
                if (p == null) continue;
                fit.parts++;
                if (p.CrewCapacity > 0) { fit.crewParts++; fit.crew += p.protoModuleCrew.Count; }
                foreach (var m in p.Modules)
                {
                    switch (m)
                    {
                        case ModuleC4ISRTerminal t: fit.terminals++; fit.terms.Add(t); break;
                        case ModuleCMProgram cp: fit.cmPrograms.Add(cp); break;
                        case BDModulePilotAI pa: if (fit.pilot == null) fit.pilot = pa; break;
                        case CMDropper cm: if (cm.cmType == CMDropper.CountermeasureTypes.Flare) fit.flares++; else if (cm.cmType == CMDropper.CountermeasureTypes.Chaff) fit.chaff++; break;
                        case MissileFire: fit.wms++; break;
                        case BDModularGuidance mmg: fit.modular.Add(mmg); fit.missiles++; fit.missileRange = Mathf.Max(fit.missileRange, mmg.GetEngageRange()); break;
                        case MissileBase mb: fit.missiles++; fit.missileRange = Mathf.Max(fit.missileRange, mb.GetEngageRange()); break;
                        case ModuleRadar r: fit.radars++; fit.radarRange = Mathf.Max(fit.radarRange, r.radarMaxDistanceDetect * 1000f); break;
                        case ModuleIRST: fit.irsts++; break;
                        case ModuleWeapon: fit.guns++; break;
                        case ModuleDataTransmitter dt:
                            fit.antennas++;
                            if (dt.antennaType == AntennaType.RELAY) fit.relays++;
                            fit.antennaPower = System.Math.Max(fit.antennaPower, dt.antennaPower);
                            break;
                        case ModuleKerbNetAccess: fit.kerbNet++; break;
                        case ModuleCommand mc: if (mc.minimumCrew == 0) fit.probes++; break;
                    }
                }
            }
            if (fit.parts == 0) return;
            if (fit.wms == 0) fit.warnings.Add("No BDArmory weapon manager: the craft won't join the data link.");
            if (fit.wms > 0 && fit.terminals == 0) fit.warnings.Add("No C4ISR terminal: automatic settings (is ModuleManager installed?).");
            if (fit.antennas == 0) fit.warnings.Add("No antenna: cut off when the link model is CommNet.");
            if (fit.kerbNet == 0) fit.warnings.Add("No KerbNet-capable part (probe core): no map imagery from this craft.");
            if (fit.missiles > 0 && fit.radars == 0 && fit.irsts == 0) fit.warnings.Add("Missiles but no sensors: needs another unit's track (launch on remote).");
            if (fit.crewParts > 0 && fit.crew == 0) fit.warnings.Add("Crewed parts are empty: nobody to eject.");
            if (fit.cmPrograms.Count > 0 && fit.cmPrograms[0].Enabled && fit.flares + fit.chaff == 0) fit.warnings.Add("Countermeasure program is on but the craft has no flare or chaff dispensers.");
            foreach (var mmg in fit.modular) CheckModular(mmg);
        }

        /// <summary>Modular missile checks: release decoupler, staging, and what the ICBM profile needs.</summary>
        void CheckModular(BDModularGuidance mmg)
        {
            string name = mmg.part.partInfo != null ? mmg.part.partInfo.title : mmg.part.name;
            var dec = BDModularGuidance.FindFirstDecoupler(mmg.part.parent, null);
            if (dec == null) { fit.warnings.Add($"{name}: no decoupler above the guidance part, so the missile can't be released."); return; }
            var prof = ModuleMissileProfile.For(mmg);
            if (prof == null || !prof.IsICBM) return;
            bool throttleable = false; // solid boosters are fine for early stages; the last stage must be able to cut off
            foreach (var p in MissileEstimator.MissileParts(mmg))
                foreach (var e in p.FindModulesImplementing<ModuleEngines>())
                    if (!e.throttleLocked) throttleable = true;
            if (!throttleable) fit.warnings.Add($"{name}: ICBM profile but no throttleable engine. Solid motors can't be cut off, so range control will be poor.");
            if (mmg.TargetingMode != MissileBase.TargetingModes.Gps && mmg.TargetingMode != MissileBase.TargetingModes.Inertial)
                fit.warnings.Add($"{name}: ICBM profile works best with GPS targeting.");
        }

        void OnGUI()
        {
            if (!show || !HighLogic.LoadedSceneIsEditor) return;
            rect = GUI.Window(WindowID, rect, Draw, "C4ISR craft setup", BDArmorySetup.BDGuiSkin.window);
            if (rect.Contains(Event.current.mousePosition)) EditorLogic.fetch?.Lock(true, true, true, "BDAC4ISREditor");
            else EditorLogic.fetch?.Unlock("BDAC4ISREditor");
        }

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { show = false; button?.SetFalse(false); EditorLogic.fetch?.Unlock("BDAC4ISREditor"); return; }
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            float w = rect.width - 34;
            scroll = GUI.BeginScrollView(new Rect(6, 22, rect.width - 12, rect.height - 28), scroll, new Rect(0, 0, w, 1200));
            float y = 0;

            if (fit.parts == 0) { AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), "No craft loaded.", AvionicsDraw.Label, AvionicsDraw.ScopeText); GUI.EndScrollView(); GUI.DragWindow(); return; }

            Head(ref y, w, "CRAFT");
            Line(ref y, w, $"Weapon managers {fit.wms} · terminals {fit.terminals}");
            Line(ref y, w, $"Missiles {fit.missiles}{(fit.missiles > 0 ? $" (max {fit.missileRange / 1000f:F0} km)" : "")} · guns {fit.guns}");
            Line(ref y, w, $"Radars {fit.radars}{(fit.radars > 0 ? $" ({fit.radarRange / 1000f:F0} km)" : "")} · IRST {fit.irsts}");
            Line(ref y, w, $"Antennas {fit.antennas} (relays {fit.relays}, best power {FormatPower(fit.antennaPower)})");
            Line(ref y, w, $"KerbNet parts {fit.kerbNet} · probe cores {fit.probes}");
            Line(ref y, w, $"Ejection seats: {fit.crewParts} crewed parts, {fit.crew} crew");

            Head(ref y, w, "PREDICTED ROLE (Auto)");
            var onGround = DataLinkNetwork.PredictRole(true, false, fit.missiles, fit.radars > 0, fit.guns > 0);
            var flying = DataLinkNetwork.PredictRole(false, false, fit.missiles, fit.radars > 0, fit.guns > 0);
            Line(ref y, w, $"On the ground: {onGround}   ·   Flying: {flying}");

            if (fit.terms.Count > 0)
            {
                var t0 = fit.terms[0];
                Head(ref y, w, "TERMINAL SETTINGS (applies to every terminal)");
                AvionicsDraw.ColorLabel(new Rect(0, y, w, 14), "Role", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 16;
                float rw = (w - 12) / 4;
                for (int i = 0; i < ModuleC4ISRTerminal.Roles.Length; i++)
                {
                    var role = ModuleC4ISRTerminal.Roles[i];
                    if (GUI.Button(new Rect((i % 4) * (rw + 4), y + (i / 4) * 22, rw, 20), role, t0.role == role ? sel : btn)) foreach (var t in fit.terms) t.role = role;
                }
                y += 48;
                if (GUI.Button(new Rect(0, y, w / 2 - 2, 20), t0.dataLink ? "Data link ON" : "Data link OFF", t0.dataLink ? sel : btn)) { bool v = !t0.dataLink; foreach (var t in fit.terms) t.dataLink = v; }
                if (GUI.Button(new Rect(w / 2 + 2, y, w / 2 - 2, 20), t0.iadsMember ? "IADS member" : "Not in IADS", t0.iadsMember ? sel : btn)) { bool v = !t0.iadsMember; foreach (var t in fit.terms) t.iadsMember = v; }
                y += 24;
                AvionicsDraw.ColorLabel(new Rect(0, y, w, 14), "Radar EMCON", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 16;
                float ew = (w - 8) / 3;
                for (int i = 0; i < ModuleC4ISRTerminal.EmconModes.Length; i++)
                {
                    var mode = ModuleC4ISRTerminal.EmconModes[i];
                    if (GUI.Button(new Rect(i * (ew + 4), y, ew, 20), mode, t0.emcon == mode ? sel : btn)) foreach (var t in fit.terms) t.emcon = mode;
                }
                y += 24;
                AvionicsDraw.ColorLabel(new Rect(0, y, w, 14), "Jammers (Barrage = EW aircraft, jams continuously)", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 16;
                for (int i = 0; i < ModuleC4ISRTerminal.JammerModes.Length; i++)
                {
                    var mode = ModuleC4ISRTerminal.JammerModes[i];
                    if (GUI.Button(new Rect(i * (ew + 4), y, ew, 20), mode, t0.jammer == mode ? sel : btn)) foreach (var t in fit.terms) t.jammer = mode;
                }
                y += 26;
            }

            Head(ref y, w, "STEALTH (radar shaping model)");
            float sw = (w - 12) / 4;
            for (int i = 0; i < StealthRCS.ModeNames.Length; i++)
                if (GUI.Button(new Rect(i * (sw + 4), y, sw, 20), StealthRCS.ModeNames[i], AvionicsSettings.StealthModel == i ? sel : btn)) StealthRCS.SetMode(i);
            y += 22;
            Line(ref y, w, StealthRCS.ModeHelp);
            if (GUI.Button(new Rect(0, y, w, 22), EditorStealthWindow.Show ? "Stealth & EW analysis (open)" : "Open stealth & EW analysis", EditorStealthWindow.Show ? sel : btn)) EditorStealthWindow.Show = !EditorStealthWindow.Show;
            y += 26;
            if (GUI.Button(new Rect(0, y, w, 22), EditorTurretWindow.Show ? "IR turret tool (open)" : "Open IR turret tool", EditorTurretWindow.Show ? sel : btn)) EditorTurretWindow.Show = !EditorTurretWindow.Show;
            y += 26;
            if (GUI.Button(new Rect(0, y, w, 22), EditorVTOLWindow.Show ? "VTOL designer (open)" : "Open VTOL designer", EditorVTOLWindow.Show ? sel : btn)) EditorVTOLWindow.Show = !EditorVTOLWindow.Show;
            y += 26;
            if (GUI.Button(new Rect(0, y, w, 22), EditorRangeWindow.Show ? "Aircraft range (open)" : "Open aircraft range", EditorRangeWindow.Show ? sel : btn)) EditorRangeWindow.Show = !EditorRangeWindow.Show;
            y += 26;

            if (fit.pilot != null)
            {
                var ai = fit.pilot;
                Head(ref y, w, "AI FLIGHT ENVELOPE");
                Line(ref y, w, $"Unclamp tuning: {(ai.UpToEleven ? "ON (limits up to 10 km/s, 400 km)" : "off (stock limits)")}");
                Line(ref y, w, $"Altitude: default {ai.defaultAltitude:F0} m, min {ai.minAltitude:F0}, max {ai.maxAltitude:F0}");
                Line(ref y, w, $"Speed: idle {ai.idleSpeed:F0}, corner {ai.cornerSpeed:F0}, max {ai.maxSpeed:F0} m/s");
                Line(ref y, w, $"Max G {ai.maxAllowedGForce:F0} · takeoff speed {ai.takeOffSpeed:F0} m/s");
                float pw = (w - 4) / 2;
                for (int i = 0; i < AIEnvelope.Presets.Length; i++)
                {
                    var p = AIEnvelope.Presets[i];
                    if (GUI.Button(new Rect((i % 2) * (pw + 4), y + (i / 2) * 22, pw, 20), p.name, btn)) StartCoroutine(AIEnvelope.Apply(ai, p));
                }
                y += ((AIEnvelope.Presets.Length + 1) / 2) * 22 + 2;
                if (GUI.Button(new Rect(0, y, w, 20), ai.UpToEleven ? "Unclamp tuning: ON" : "Unclamp tuning: off", ai.UpToEleven ? sel : btn)) ai.UpToEleven = !ai.UpToEleven;
                y += 22;
                AvionicsDraw.ColorLabel(new Rect(0, y, w, 28), "Fine-tune in the Pilot AI part menu (all settings shown,\nincluding Unclamp tuning).", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
                y += 30;
            }

            if (fit.cmPrograms.Count > 0)
            {
                var p0 = fit.cmPrograms[0];
                Head(ref y, w, "COUNTERMEASURE PROGRAM");
                Line(ref y, w, $"Dispensers: {fit.flares} flare, {fit.chaff} chaff");
                float mw = (w - 8) / 3;
                for (int i = 0; i < ModuleCMProgram.Modes.Length; i++)
                {
                    var mode = ModuleCMProgram.Modes[i];
                    if (GUI.Button(new Rect(i * (mw + 4), y, mw, 20), mode, p0.mode == mode ? sel : btn)) foreach (var p in fit.cmPrograms) p.mode = mode;
                }
                y += 24;
                Line(ref y, w, $"{p0.salvos:F0} salvos × {p0.bursts:F0} bursts ({p0.flaresPerBurst:F0} flares / {p0.chaffPerBurst:F0} chaff), at {p0.triggerTime:F1} s to impact");
                AvionicsDraw.ColorLabel(new Rect(0, y, w, 14), "Fine-tune in the weapon manager's part menu.", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 16;
            }

            if (fit.modular.Count > 0)
            {
                Head(ref y, w, "MODULAR MISSILES");
                foreach (var mmg in fit.modular)
                {
                    var prof = ModuleMissileProfile.For(mmg);
                    string name = mmg.part.partInfo != null ? mmg.part.partInfo.title : mmg.part.name;
                    if (name.Length > 22) name = name.Substring(0, 22);
                    Line(ref y, w, $"{name}: {mmg.StagesNumber:F0} stages, {mmg.GuidanceLabel}");
                    if (prof != null)
                    {
                        if (GUI.Button(new Rect(0, y, w / 2 - 2, 20), "Standard", !prof.IsICBM ? sel : btn)) prof.profile = "Standard";
                        if (GUI.Button(new Rect(w / 2 + 2, y, w / 2 - 2, 20), "ICBM", prof.IsICBM ? sel : btn)) prof.profile = "ICBM";
                        y += 24;
                    }
                }
                AvionicsDraw.ColorLabel(new Rect(0, y, w, 28), "Mount the missile by any part (e.g. its booster);\nguidance follows the stage that carries it.", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
                y += 30;
            }

            if (fit.warnings.Count > 0)
            {
                Head(ref y, w, "CHECKS");
                foreach (var s in fit.warnings)
                {
                    AvionicsDraw.ColorLabel(new Rect(0, y, w, 30), s, AvionicsDraw.LabelSmall, C2UI.LinkWeak);
                    y += 28;
                }
            }
            else { Head(ref y, w, "CHECKS"); Line(ref y, w, "All good."); }

            GUI.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, rect.width - 24, 20));
        }

        static string FormatPower(double p) => p <= 0 ? "-" : p >= 1e9 ? $"{p / 1e9:F1}G" : p >= 1e6 ? $"{p / 1e6:F1}M" : p >= 1e3 ? $"{p / 1e3:F0}k" : p.ToString("F0");

        static void Head(ref float y, float w, string s)
        {
            y += 6;
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 14), s, AvionicsDraw.LabelSmall, C2UI.Accent);
            y += 16;
        }

        static void Line(ref float y, float w, string s)
        {
            AvionicsDraw.ColorLabel(new Rect(0, y, w, 16), s, AvionicsDraw.Label, AvionicsDraw.HudText);
            y += 18;
        }
    }
}
