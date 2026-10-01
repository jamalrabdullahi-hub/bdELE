using System;
using System.Collections.Generic;
using UnityEngine;

using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>IADS sector controller: SAM sites, engagement matrix, doctrine, EMCON and the event log.</summary>
    public static class IADSWindow
    {
        public const int WindowID = 71730312;
        static Vector2 siteScroll, matrixScroll, logScroll;

        public static void Draw(int id)
        {
            var r = C4ISRSettings.IADSRect;
            if (GUI.Button(new Rect(r.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { C4ISRSettings.ShowIADS = false; return; }
            var net = DataLinkNetwork.PlayerNetwork;
            var iads = net != null ? IADSController.For(net.team) : null;
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            float w = r.width, h = r.height;

            int hostiles = 0;
            if (net != null) foreach (var t in net.tracks.Values) if (t.hostile) hostiles++;
            AvionicsDraw.ColorLabel(new Rect(8, 22, w - 200, 16),
                !C4ISRSettings.IADSEnabled ? "IADS OFF" : iads == null ? "No IADS for this team" : $"{iads.sites.Count} SAM sites · {hostiles} hostile tracks · EMCON {C4ISRSettings.Emcon.ToString()[0]}",
                AvionicsDraw.Label, hostiles > 0 ? AvionicsDraw.Hostile : C2UI.Accent);
            if (GUI.Button(new Rect(w - 190, 22, 80, 20), "AUTO", C4ISRSettings.IADSAutoAssign ? sel : btn)) C4ISRSettings.IADSAutoAssign = true;
            if (GUI.Button(new Rect(w - 106, 22, 80, 20), "MANUAL", !C4ISRSettings.IADSAutoAssign ? sel : btn)) C4ISRSettings.IADSAutoAssign = false;

            float top = 48, leftW = 250, rightW = 240;
            var sites = new Rect(6, top, leftW, h - top - 8);
            var doctrine = new Rect(w - rightW - 6, top, rightW, h - top - 8);
            var mid = new Rect(sites.xMax + 6, top, doctrine.x - sites.xMax - 12, h - top - 8);

            // Sites
            if (iads != null)
            {
                siteScroll = GUI.BeginScrollView(sites, siteScroll, new Rect(0, 0, leftW - 16, iads.sites.Count * 52 + 80));
                float y = 0;
                AvionicsDraw.ColorLabel(new Rect(0, y, leftW, 14), "SAM SITES", AvionicsDraw.LabelSmall, C2UI.Accent); y += 16;
                foreach (var s in iads.sites.Values)
                {
                    var box = new Rect(0, y, leftW - 18, 48);
                    if (AvionicsDraw.Begin()) { AvionicsDraw.FilledRect(box, s.assigned != null ? new Color(0.2f, 0.15f, 0.08f, 0.9f) : C2UI.Panel); AvionicsDraw.End(); }
                    if (GUI.Button(box, "", GUIStyle.none)) C2UI.Select(s.node.vessel);
                    AvionicsDraw.ColorLabel(new Rect(6, y + 3, box.width - 12, 16), s.node.Name, AvionicsDraw.Label, AvionicsDraw.HudText);
                    AvionicsDraw.ColorLabel(new Rect(6, y + 3, box.width - 12, 16), s.State, AvionicsDraw.LabelRight, s.assigned != null ? AvionicsDraw.Hostile : AvionicsDraw.ScopeText);
                    string guard = s.node.wm != null && s.node.wm.guardMode ? "" : "  GUARD OFF";
                    AvionicsDraw.ColorLabel(new Rect(6, y + 19, box.width - 12, 14), $"Radar {(s.node.radarOn ? "ON" : "off")} · {s.node.readyMissiles} ready · {s.node.maxMissileRange / 1000f:F0} km{guard}{(s.node.connected ? "" : " · NO LINK")}", AvionicsDraw.LabelSmall, s.node.connected && guard.Length == 0 ? AvionicsDraw.ScopeText : C2UI.LinkLost);
                    y += 52;
                }
                y += 4;
                AvionicsDraw.ColorLabel(new Rect(0, y, leftW, 14), "SENSORS", AvionicsDraw.LabelSmall, C2UI.Accent); y += 16;
                foreach (var n in net.nodes)
                    if (n.IsSensor)
                    {
                        AvionicsDraw.ColorLabel(new Rect(0, y, leftW - 18, 14), $"{n.Name} · {n.role} · {n.radarRange / 1000f:F0} km · {(n.radarOn ? "ON" : "off")}", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
                        y += 14;
                    }
                GUI.EndScrollView();
            }

            // Engagement matrix: threats (rows) x sites (columns), time to intercept in seconds.
            if (iads != null)
            {
                AvionicsDraw.ColorLabel(new Rect(mid.x, mid.y, mid.width, 14), "ENGAGEMENT MATRIX · threat score / time to intercept (s) · click a cell to assign", AvionicsDraw.LabelSmall, C2UI.Accent);
                var siteList = new List<IADSController.Site>(iads.sites.Values);
                var threats = new List<NetTrack>(iads.threat.Keys);
                threats.Sort((a, b) => iads.threat[b].CompareTo(iads.threat[a]));
                float colW = 76, rowH = 20, labelW = 90;
                var matrixRect = new Rect(mid.x, mid.y + 18, mid.width, mid.height * 0.55f);
                matrixScroll = GUI.BeginScrollView(matrixRect, matrixScroll, new Rect(0, 0, labelW + 50 + siteList.Count * colW, (threats.Count + 1) * rowH));
                AvionicsDraw.ColorLabel(new Rect(0, 0, labelW, rowH), "TRACK", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
                AvionicsDraw.ColorLabel(new Rect(labelW, 0, 50, rowH), "SCORE", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
                for (int c = 0; c < siteList.Count; c++)
                    AvionicsDraw.ColorLabel(new Rect(labelW + 50 + c * colW, 0, colW - 4, rowH), Short(siteList[c].node.Name), AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
                for (int i = 0; i < threats.Count; i++)
                {
                    var t = threats[i];
                    float y = (i + 1) * rowH;
                    if (GUI.Button(new Rect(0, y, labelW - 4, rowH - 2), $"{t.Label} {(t.isMissile ? "MSL" : "")}", C2UI.SelectedTrack == t ? sel : btn)) C2UI.Select(t);
                    AvionicsDraw.ColorLabel(new Rect(labelW, y + 2, 50, rowH), $"{iads.threat[t]:F1}", AvionicsDraw.LabelSmall, AvionicsDraw.Hostile);
                    for (int c = 0; c < siteList.Count; c++)
                    {
                        var s = siteList[c];
                        bool inRange = IADSController.CanReach(s, t);
                        string cell = inRange ? $"{IADSController.TimeToIntercept(s, t):F0}{(s.assigned == t ? (s.LaunchingOnRemote ? " LOR" : " ◆") : "")}" : "—";
                        GUI.enabled = inRange && s.node.IsShooter;
                        if (GUI.Button(new Rect(labelW + 50 + c * colW, y, colW - 4, rowH - 2), cell, s.assigned == t ? sel : btn))
                            IADSController.Assign(net.team, s.node.vessel, s.assigned == t ? null : t);
                        GUI.enabled = true;
                    }
                }
                GUI.EndScrollView();

                // Log
                var logRect = new Rect(mid.x, matrixRect.yMax + 8, mid.width, mid.yMax - matrixRect.yMax - 8);
                if (AvionicsDraw.Begin()) { AvionicsDraw.FilledRect(logRect, C2UI.Panel); AvionicsDraw.End(); }
                AvionicsDraw.ColorLabel(new Rect(logRect.x + 6, logRect.y + 2, 200, 14), "EVENT LOG", AvionicsDraw.LabelSmall, C2UI.Accent);
                logScroll = GUI.BeginScrollView(new Rect(logRect.x, logRect.y + 18, logRect.width, logRect.height - 20), logScroll, new Rect(0, 0, logRect.width - 16, iads.log.Count * 14));
                for (int i = 0; i < iads.log.Count; i++) AvionicsDraw.ColorLabel(new Rect(6, i * 14, logRect.width - 24, 14), iads.log[i], AvionicsDraw.LabelSmall, AvionicsDraw.HudText);
                GUI.EndScrollView();
            }

            // Doctrine
            {
                float x = doctrine.x, y = doctrine.y, dw = doctrine.width;
                AvionicsDraw.ColorLabel(new Rect(x, y, dw, 14), "DOCTRINE", AvionicsDraw.LabelSmall, C2UI.Accent); y += 16;
                C4ISRSettings.IADSEnabled = Toggle(x, ref y, dw, C4ISRSettings.IADSEnabled, "IADS controller on");
                C4ISRSettings.LaunchOnRemote = Toggle(x, ref y, dw, C4ISRSettings.LaunchOnRemote, "Launch on remote track");
                C4ISRSettings.ShootLookShoot = Toggle(x, ref y, dw, C4ISRSettings.ShootLookShoot, "Shoot-look-shoot");
                C4ISRSettings.EngageMissiles = Toggle(x, ref y, dw, C4ISRSettings.EngageMissiles, "Engage missiles (point defence)");
                C4ISRSettings.IADSForAITeams = Toggle(x, ref y, dw, C4ISRSettings.IADSForAITeams, "Run IADS for every team");
                AvionicsDraw.ColorLabel(new Rect(x, y + 2, 80, 16), "Salvo", AvionicsDraw.Label, AvionicsDraw.HudText);
                for (int i = 1; i <= 4; i++) if (GUI.Button(new Rect(x + 60 + (i - 1) * 44, y, 40, 20), i.ToString(), C4ISRSettings.Salvo == i ? sel : btn)) C4ISRSettings.Salvo = i;
                y += 28;
                AvionicsDraw.ColorLabel(new Rect(x, y, dw, 14), "EMCON", AvionicsDraw.LabelSmall, C2UI.Accent); y += 16;
                foreach (EmconLevel lvl in Enum.GetValues(typeof(EmconLevel)))
                {
                    if (GUI.Button(new Rect(x, y, dw, 20), C4ISRSettings.EmconName(lvl), C4ISRSettings.Emcon == lvl ? sel : btn)) C4ISRSettings.Emcon = lvl;
                    y += 22;
                }
                y += 4;
                AvionicsDraw.ColorLabel(new Rect(x, y, dw, 16), $"Wake-up ring: {C4ISRSettings.WakeRingKm:F0} km", AvionicsDraw.Label, AvionicsDraw.HudText); y += 18;
                C4ISRSettings.WakeRingKm = Mathf.Round(GUI.HorizontalSlider(new Rect(x, y, dw, 16), C4ISRSettings.WakeRingKm, 5, 150)); y += 22;
                AvionicsDraw.ColorLabel(new Rect(x, y, dw, 60), "SAM sites must be in guard mode.\nIADS picks their targets; BDArmory's\nweapon manager still fires.", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
            }
            GUI.DragWindow(new Rect(0, 0, w - 24, 20));
        }

        static string Short(string s) => s.Length <= 10 ? s : s.Substring(0, 9) + "…";

        public static bool Toggle(float x, ref float y, float w, bool v, string text)
        {
            v = GUI.Toggle(new Rect(x, y, w, 20), v, text, v ? BDArmorySetup.SelectedButtonStyle : BDArmorySetup.ButtonStyle);
            y += 22;
            return v;
        }
    }

    /// <summary>Data link network status: link model, every node's path and quality, shared picture totals.</summary>
    public static class DataLinkWindow
    {
        public const int WindowID = 71730313;
        static Vector2 scroll;

        public static void Draw(int id)
        {
            var r = C4ISRSettings.LinkRect;
            if (GUI.Button(new Rect(r.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { C4ISRSettings.ShowLink = false; return; }
            var net = DataLinkNetwork.PlayerNetwork;
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            float w = r.width, y = 22, bw = (w - 20) / 3;
            if (GUI.Button(new Rect(6, y, bw, 20), "Global C2", C4ISRSettings.GlobalC2 ? sel : btn)) C4ISRSettings.GlobalC2 = true;
            if (GUI.Button(new Rect(10 + bw, y, bw, 20), "CommNet", !C4ISRSettings.GlobalC2 && C4ISRSettings.LinkModel == LinkModel.CommNet ? sel : btn)) { C4ISRSettings.GlobalC2 = false; C4ISRSettings.LinkModel = LinkModel.CommNet; }
            if (GUI.Button(new Rect(14 + 2 * bw, y, bw, 20), "Line of sight", !C4ISRSettings.GlobalC2 && C4ISRSettings.LinkModel == LinkModel.LineOfSight ? sel : btn)) { C4ISRSettings.GlobalC2 = false; C4ISRSettings.LinkModel = LinkModel.LineOfSight; }
            y += 26;
            if (!C4ISRSettings.GlobalC2 && C4ISRSettings.LinkModel == LinkModel.CommNet && !CommNet.CommNetScenario.CommNetEnabled)
            {
                AvionicsDraw.ColorLabel(new Rect(8, y, w - 16, 14), "CommNet is off in this save's difficulty settings, so everyone stays linked.", AvionicsDraw.LabelSmall, C2UI.LinkWeak);
                y += 16;
            }
            if (net == null) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), "No weapon manager on the active vessel.", AvionicsDraw.Label, AvionicsDraw.ScopeText); GUI.DragWindow(); return; }

            int sensors = 0; float oldest = 0;
            foreach (var n in net.nodes) if (n.trackCount > 0) sensors++;
            foreach (var t in net.tracks.Values) oldest = Mathf.Max(oldest, Time.time - t.lastSeen);
            AvionicsDraw.ColorLabel(new Rect(8, y, w - 16, 16), $"{net.ConnectedCount}/{net.nodes.Count} linked · {net.tracks.Count} tracks · {sensors} reporting sensors · oldest track {oldest:F1} s", AvionicsDraw.Label, C2UI.Accent);
            y += 20;

            float[] cols = [0, 170, 230, 330, 380, 430, 490];
            string[] heads = ["NODE", "ROLE", "PATH", "HOPS", "QUAL", "LAG", "TRK"];
            for (int i = 0; i < heads.Length; i++) AvionicsDraw.ColorLabel(new Rect(8 + cols[i], y, 120, 14), heads[i], AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
            y += 16;
            scroll = GUI.BeginScrollView(new Rect(6, y, w - 12, r.height - y - 8), scroll, new Rect(0, 0, w - 30, net.nodes.Count * 18));
            for (int i = 0; i < net.nodes.Count; i++)
            {
                var n = net.nodes[i];
                float ry = i * 18;
                if (GUI.Button(new Rect(0, ry, w - 30, 17), "", C2UI.SelectedUnit == n.vessel ? sel : GUIStyle.none)) C2UI.Select(n.vessel);
                Color c = !n.LinkEnabled ? AvionicsDraw.Neutral : !n.connected ? C2UI.LinkLost : n.quality < 0.5f ? C2UI.LinkWeak : AvionicsDraw.HudText;
                string[] cells = [n.Name, C2UI.RoleShort(n.role), !n.LinkEnabled ? "off" : !n.connected ? "LOST" : n.path.ToString(), n.connected ? n.hops.ToString() : "—",
                    n.connected ? $"{n.quality * 100:F0}%" : "0%", n.connected && n.delay > 0 ? $"{n.delay:F2} s" : "—", n.trackCount.ToString()];
                for (int k = 0; k < cells.Length; k++)
                    AvionicsDraw.ColorLabel(new Rect(2 + cols[k], ry + 1, (k + 1 < cols.Length ? cols[k + 1] - cols[k] : 60) - 4, 16), cells[k], AvionicsDraw.LabelSmall, c);
            }
            GUI.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, w - 24, 20));
        }
    }

    /// <summary>All C4ISR settings in one place.</summary>
    public static class C4ISRSettingsWindow
    {
        public const int WindowID = 71730314;
        static Vector2 scroll;

        public static void Draw(int id)
        {
            var r = C4ISRSettings.SettingsRect;
            if (GUI.Button(new Rect(r.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { C4ISRSettings.ShowSettings = false; AvionicsSettings.Save(); return; }
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            float w = r.width - 30;
            scroll = GUI.BeginScrollView(new Rect(6, 22, r.width - 12, r.height - 30), scroll, new Rect(0, 0, w, 900));
            float x = 0, y = 0;

            Head(x, ref y, w, "COMMAND & CONTROL");
            C4ISRSettings.GlobalC2 = IADSWindow.Toggle(x, ref y, w, C4ISRSettings.GlobalC2, "Global C2 (whole team linked, no range limit)");
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "Link model when Global C2 is off:", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 16;
            if (GUI.Button(new Rect(x, y, w / 2 - 2, 20), "CommNet", C4ISRSettings.LinkModel == LinkModel.CommNet ? sel : btn)) C4ISRSettings.LinkModel = LinkModel.CommNet;
            if (GUI.Button(new Rect(x + w / 2 + 2, y, w / 2 - 2, 20), "Line of sight", C4ISRSettings.LinkModel == LinkModel.LineOfSight ? sel : btn)) C4ISRSettings.LinkModel = LinkModel.LineOfSight;
            y += 24;
            Slider(x, ref y, w, ref C4ISRSettings.LOSLinkRangeKm, 10, 1000, "Line-of-sight link range", "km");
            C4ISRSettings.RequireAntenna = IADSWindow.Toggle(x, ref y, w, C4ISRSettings.RequireAntenna, "CommNet: craft without a connection are cut off");
            C4ISRSettings.DataLinkMidcourse = IADSWindow.Toggle(x, ref y, w, C4ISRSettings.DataLinkMidcourse, "Mid-course updates for radar missiles");

            Head(x, ref y, w, "TRACKS");
            Slider(x, ref y, w, ref C4ISRSettings.NetUpdateHz, 1, 10, "Network update rate", "Hz");
            Slider(x, ref y, w, ref C4ISRSettings.TrackTimeout, 2, 30, "Drop tracks not seen for", "s");

            Head(x, ref y, w, "MAP");
            bool scan = C2Map.SCANsatInstalled;
            if (GUI.Button(new Rect(x, y, w / 3 - 2, 20), "KerbNet", C4ISRSettings.MapSource == MapSource.KerbNet ? sel : btn)) C4ISRSettings.MapSource = MapSource.KerbNet;
            GUI.enabled = scan;
            if (GUI.Button(new Rect(x + w / 3, y, w / 3 - 2, 20), "SCANsat", C4ISRSettings.MapSource == MapSource.SCANsat ? sel : btn)) C4ISRSettings.MapSource = MapSource.SCANsat;
            GUI.enabled = true;
            if (GUI.Button(new Rect(x + 2 * w / 3, y, w / 3, 20), "Grid only", C4ISRSettings.MapSource == MapSource.Grid ? sel : btn)) C4ISRSettings.MapSource = MapSource.Grid;
            y += 22;
            if (!scan) { AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "SCANsat not installed", AvionicsDraw.LabelSmall, C2UI.LinkWeak); y += 16; }
            C4ISRSettings.MapRequiresCoverage = IADSWindow.Toggle(x, ref y, w, C4ISRSettings.MapRequiresCoverage, "KerbNet map needs a probe core on the team");

            Head(x, ref y, w, "OVERLAYS");
            C4ISRSettings.ShowMapOverlay = IADSWindow.Toggle(x, ref y, w, C4ISRSettings.ShowMapOverlay, "C2 overlay in map view");
            C4ISRSettings.OverlayLinks = IADSWindow.Toggle(x, ref y, w, C4ISRSettings.OverlayLinks, "Draw data links in map view");
            C4ISRSettings.OverlayRings = IADSWindow.Toggle(x, ref y, w, C4ISRSettings.OverlayRings, "Draw SAM rings in map view");

            Head(x, ref y, w, "THREAT WARNING");
            AvionicsSettings.ThreatBanner = IADSWindow.Toggle(x, ref y, w, AvionicsSettings.ThreatBanner, "Missile / lock warning banner");
            AvionicsSettings.Radar3DShowThreats = IADSWindow.Toggle(x, ref y, w, AvionicsSettings.Radar3DShowThreats, "Threat strobes on the 3D radar");

            Head(x, ref y, w, "KEYS (with Right Alt)");
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 60), $"Console: {C4ISRSettings.KeyC2Window}   IADS: {C4ISRSettings.KeyIADSWindow}   Data link: {C4ISRSettings.KeyLinkWindow}\nEject: {AvionicsSettings.KeyEject} twice\nChange keys in PluginData/Avionics.cfg", AvionicsDraw.LabelSmall, AvionicsDraw.HudText);
            GUI.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, r.width - 24, 20));
        }

        static void Head(float x, ref float y, float w, string s)
        {
            y += 6;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), s, AvionicsDraw.LabelSmall, C2UI.Accent);
            y += 16;
        }

        static void Slider(float x, ref float y, float w, ref float v, float min, float max, string label, string unit)
        {
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 16), $"{label}: {v:0.#} {unit}", AvionicsDraw.Label, AvionicsDraw.HudText);
            y += 18;
            v = GUI.HorizontalSlider(new Rect(x, y, w, 16), v, min, max);
            v = max - min > 20 ? Mathf.Round(v) : Mathf.Round(v * 2) / 2;
            y += 20;
        }
    }
}
