using System;
using System.Collections.Generic;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Option A: the C2 tactical console. Unit roster, plotting map (KerbNet/SCANsat imagery), track table and tasking panel in one window.
    /// </summary>
    public static class C2ConsoleWindow
    {
        public const int WindowID = 71730310;
        const float RosterW = 210, PanelW = 262, TableH = 150, BarH = 26;

        // Map view state
        static double lat0, lon0;
        static bool follow = true;
        static bool panning;
        static Vector2 panStart;
        static double panLat, panLon;
        static bool resizing;
        static Vector2 rosterScroll, tableScroll, panelScroll;

        static readonly C2MapTile tile = new();
        static bool tileBuilding;

        public static void Draw(int id)
        {
            var rect = C4ISRSettings.ConsoleRect;
            var net = DataLinkNetwork.PlayerNetwork;
            float w = rect.width, h = rect.height;

            if (GUI.Button(new Rect(w - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { C4ISRSettings.ShowConsole = false; return; }

            // Status bar
            var body = FlightGlobals.currentMainBody;
            string status = net == null ? "No weapon manager on the active vessel"
                : $"TEAM {net.team.Name}   NET: {(C4ISRSettings.GlobalC2 ? "GLOBAL C2" : C4ISRSettings.LinkModel.ToString().ToUpper())} · {net.ConnectedCount}/{net.nodes.Count} linked · {net.tracks.Count} tracks";
            AvionicsDraw.ColorLabel(new Rect(8, 22, w - 300, 16), status, AvionicsDraw.Label, C2UI.Accent);
            AvionicsDraw.ColorLabel(new Rect(w - 290, 22, 280, 16), KSPUtil.PrintDateCompact(Planetarium.GetUniversalTime(), true, true), AvionicsDraw.LabelRight, AvionicsDraw.ScopeText);

            float top = 22 + BarH;
            var roster = new Rect(6, top, RosterW, h - top - 8);
            var panel = new Rect(w - PanelW - 6, top, PanelW, h - top - 8);
            float cx = roster.xMax + 6, cw = panel.x - 6 - cx;
            var toolbar = new Rect(cx, top, cw, 22);
            var map = new Rect(cx, top + 26, cw, h - top - 26 - TableH - 10);
            var table = new Rect(cx, map.yMax + 4, cw, TableH);

            DrawRoster(roster, net);
            DrawToolbar(toolbar, net);
            DrawMap(map, net, body);
            DrawTable(table, net);

            panelScroll = GUI.BeginScrollView(panel, panelScroll, new Rect(0, 0, panel.width - 16, 760));
            C2UI.DrawSelectionPanel(new Rect(0, 0, panel.width - 18, 760), net);
            GUI.EndScrollView();

            // Resize handle
            var grip = new Rect(w - 16, h - 16, 16, 16);
            GUI.Label(grip, "◢", AvionicsDraw.LabelSmall);
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition)) { resizing = true; e.Use(); }
            if (resizing && e.type == EventType.MouseDrag)
            {
                C4ISRSettings.ConsoleRect.width = Mathf.Clamp(e.mousePosition.x + 8, 900, Screen.width);
                C4ISRSettings.ConsoleRect.height = Mathf.Clamp(e.mousePosition.y + 8, 560, Screen.height);
                e.Use();
            }
            if (e.rawType == EventType.MouseUp) { resizing = false; panning = false; }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && C2UI.Placing != PlaceMode.None) { C2UI.Placing = PlaceMode.None; e.Use(); }

            if (!resizing && !panning) GUI.DragWindow(new Rect(0, 0, w - 24, 20));
        }

        // ---- Roster ----

        static readonly NodeRole[] RosterOrder = [NodeRole.HQ, NodeRole.Fighter, NodeRole.AEW, NodeRole.SAM, NodeRole.EWR, NodeRole.Ship, NodeRole.Ground];

        static void DrawRoster(Rect r, TeamNetwork net)
        {
            if (AvionicsDraw.Begin()) { AvionicsDraw.FilledRect(r, C2UI.Panel); AvionicsDraw.End(); }
            if (net == null) return;
            int rows = net.nodes.Count + RosterOrder.Length;
            rosterScroll = GUI.BeginScrollView(r, rosterScroll, new Rect(0, 0, r.width - 16, rows * 20 + 8));
            float y = 4;
            foreach (var role in RosterOrder)
            {
                int count = 0;
                foreach (var n in net.nodes) if (n.role == role) count++;
                if (count == 0) continue;
                AvionicsDraw.ColorLabel(new Rect(6, y, r.width - 20, 16), $"{role.ToString().ToUpper()} · {count}", AvionicsDraw.LabelSmall, C2UI.Accent);
                y += 18;
                foreach (var n in net.nodes)
                {
                    if (n.role != role) continue;
                    bool sel = C2UI.SelectedUnit == n.vessel;
                    string link = !n.connected ? "×" : n.path == LinkPath.Relay ? "≈" : "●";
                    string task = n.role == NodeRole.SAM && IADSController.For(net.team)?.SiteFor(n.vessel) is IADSController.Site s ? s.State : C2Tasking.TaskString(n.vessel);
                    if (GUI.Button(new Rect(2, y, r.width - 20, 19), $"{link} {Trim(n.Name, 16)}  {task}", sel ? BDArmorySetup.SelectedButtonStyle : BDArmorySetup.ButtonStyle))
                        C2UI.Select(n.vessel);
                    y += 20;
                }
            }
            GUI.EndScrollView();
        }

        static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        // ---- Map toolbar ----

        static void DrawToolbar(Rect r, TeamNetwork net)
        {
            float x = r.x, bw = 78;
            var btn = BDArmorySetup.ButtonStyle;
            var sel = BDArmorySetup.SelectedButtonStyle;
            var source = net != null ? C2Map.Effective(net.team) : MapSource.Grid;
            if (GUI.Button(new Rect(x, r.y, bw, 20), "Terrain", C4ISRSettings.MapLayer == C2MapLayer.Terrain ? sel : btn)) C4ISRSettings.MapLayer = C2MapLayer.Terrain; x += bw + 2;
            if (GUI.Button(new Rect(x, r.y, bw, 20), "Biome", C4ISRSettings.MapLayer == C2MapLayer.Biome ? sel : btn)) C4ISRSettings.MapLayer = C2MapLayer.Biome; x += bw + 2;
            AvionicsDraw.ColorLabel(new Rect(x + 4, r.y + 3, 110, 16), source == MapSource.SCANsat ? "SCANsat" : source == MapSource.KerbNet ? "KerbNet" : "Grid only", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); x += 100;
            if (GUI.Button(new Rect(x, r.y, 56, 20), "Rings", C4ISRSettings.MapRings ? sel : btn)) C4ISRSettings.MapRings = !C4ISRSettings.MapRings; x += 58;
            if (GUI.Button(new Rect(x, r.y, 60, 20), "Tracks", C4ISRSettings.MapTracks ? sel : btn)) C4ISRSettings.MapTracks = !C4ISRSettings.MapTracks; x += 62;
            if (GUI.Button(new Rect(x, r.y, 60, 20), "Cover", showCover ? sel : btn)) showCover = !showCover; x += 62;
            if (GUI.Button(new Rect(x, r.y, 60, 20), "Follow", follow ? sel : btn)) follow = !follow; x += 62;
            if (GUI.Button(new Rect(x, r.y, 60, 20), "GPS", C2UI.Placing == PlaceMode.DropGPS ? sel : btn)) C2UI.Placing = C2UI.Placing == PlaceMode.DropGPS ? PlaceMode.None : PlaceMode.DropGPS;
            float zx = r.xMax - 110;
            if (GUI.Button(new Rect(zx, r.y, 24, 20), "−", btn)) C4ISRSettings.MapZoomIndex = Math.Min(C4ISRSettings.MapZoomIndex + 1, C4ISRSettings.MapSpansKm.Length - 1);
            AvionicsDraw.ColorLabel(new Rect(zx + 26, r.y + 3, 58, 16), $"{C4ISRSettings.MapSpansKm[C4ISRSettings.MapZoomIndex]:F0} km", AvionicsDraw.LabelCenter, AvionicsDraw.HudText);
            if (GUI.Button(new Rect(zx + 86, r.y, 24, 20), "+", btn)) C4ISRSettings.MapZoomIndex = Math.Max(C4ISRSettings.MapZoomIndex - 1, 0);
        }

        // ---- Map ----

        static double KmPerDeg(CelestialBody b) => 2 * Math.PI * b.Radius / 360000.0;

        static Vector2 ToMap(Rect m, CelestialBody b, double lat, double lon)
        {
            double s = m.width / C4ISRSettings.MapSpansKm[C4ISRSettings.MapZoomIndex]; // px per km
            double dLon = lon - lon0;
            while (dLon > 180) dLon -= 360;
            while (dLon < -180) dLon += 360;
            double k = KmPerDeg(b);
            return m.center + new Vector2((float)(dLon * Math.Cos(lat0 * Math.PI / 180) * k * s), (float)(-(lat - lat0) * k * s));
        }

        static Vector2d FromMap(Rect m, CelestialBody b, Vector2 p)
        {
            double s = m.width / C4ISRSettings.MapSpansKm[C4ISRSettings.MapZoomIndex];
            double k = KmPerDeg(b);
            double lat = lat0 - (p.y - m.center.y) / s / k;
            double lon = lon0 + (p.x - m.center.x) / s / k / Math.Max(0.01, Math.Cos(lat0 * Math.PI / 180));
            return new Vector2d(Math.Max(-89.9, Math.Min(89.9, lat)), lon);
        }

        static Vector2 ToMap(Rect m, CelestialBody b, Vector3 world) => ToMap(m, b, b.GetLatitude(world), b.GetLongitude(world));

        static void DrawMap(Rect m, TeamNetwork net, CelestialBody body)
        {
            if (body == null) return;
            var e = Event.current;

            // Centre on the selection (or the active vessel) unless the player has panned away.
            if (follow && !panning)
            {
                Vessel c = C2UI.SelectedUnit != null ? C2UI.SelectedUnit : FlightGlobals.ActiveVessel;
                if (C2UI.SelectedTrack != null && C2UI.SelectedUnit == null) { lat0 = body.GetLatitude(C2UI.SelectedTrack.position); lon0 = body.GetLongitude(C2UI.SelectedTrack.position); }
                else if (c != null) { lat0 = c.latitude; lon0 = c.longitude; }
            }

            // Imagery
            EnsureTile(m, body, net);
            if (AvionicsDraw.Begin()) { AvionicsDraw.FilledRect(m, new Color(0.05f, 0.07f, 0.08f, 1)); AvionicsDraw.End(); }
            GUI.BeginGroup(m);
            if (tile.texture != null && tile.done && tile.body == body)
            {
                var local = new Rect(0, 0, m.width, m.height);
                Vector2 a = ToMap(local, body, tile.latMax, tile.lonMin), b = ToMap(local, body, tile.latMin, tile.lonMax);
                GUI.DrawTexture(new Rect(a.x, a.y, b.x - a.x, b.y - a.y), tile.texture, ScaleMode.StretchToFill, false);
            }
            GUI.EndGroup();
            if (tileBuilding) AvionicsDraw.ColorLabel(new Rect(m.x + 6, m.y + 4, 200, 14), $"Loading map {tile.progress * 100:F0}%", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);

            var labels = new List<(Vector2 p, string text, Color c)>();
            if (net != null && AvionicsDraw.Begin())
            {
                // 10 km grid
                double k = KmPerDeg(body), stepDeg = GridStepKm() / k;
                for (double la = Math.Floor((lat0 - 10) / stepDeg) * stepDeg; la < lat0 + 10; la += stepDeg)
                {
                    float y = ToMap(m, body, la, lon0).y;
                    if (y > m.y && y < m.yMax) AvionicsDraw.Line(new Vector2(m.x, y), new Vector2(m.xMax, y), 1, new Color(0.3f, 0.45f, 0.5f, 0.25f));
                }
                double lonStep = stepDeg / Math.Max(0.05, Math.Cos(lat0 * Math.PI / 180));
                for (double lo = Math.Floor((lon0 - 20) / lonStep) * lonStep; lo < lon0 + 20; lo += lonStep)
                {
                    float x = ToMap(m, body, lat0, lo).x;
                    if (x > m.x && x < m.xMax) AvionicsDraw.Line(new Vector2(x, m.y), new Vector2(x, m.yMax), 1, new Color(0.3f, 0.45f, 0.5f, 0.25f));
                }

                float pxPerM = m.width / (C4ISRSettings.MapSpansKm[C4ISRSettings.MapZoomIndex] * 1000f);
                var iads = IADSController.For(net.team);

                // Radar coverage with terrain shadows (friendly radars, targets 300 m above the ground) and burn-through rings
                var se = SensorEngine.Latest;
                if (showCover)
                {
                    foreach (var cv in se.coverage.Values)
                    {
                        Vector2 prev = Vector2.zero;
                        for (int a = 0; a <= 72; a++)
                        {
                            double brg = (a % 72) * 5 * Math.PI / 180, dist = cv.reach[a % 72], ang = dist / body.Radius;
                            double la1 = cv.lat * Math.PI / 180, lo1 = cv.lon * Math.PI / 180;
                            double la2 = Math.Asin(Math.Sin(la1) * Math.Cos(ang) + Math.Cos(la1) * Math.Sin(ang) * Math.Cos(brg));
                            double lo2 = lo1 + Math.Atan2(Math.Sin(brg) * Math.Sin(ang) * Math.Cos(la1), Math.Cos(ang) - Math.Sin(la1) * Math.Sin(la2));
                            var p = ToMap(m, body, la2 * 180 / Math.PI, lo2 * 180 / Math.PI);
                            if (a > 0) ClipLine(m, prev, p, 1.5f, new Color(0.3f, 0.8f, 1f, 0.7f), dashed: false);
                            prev = p;
                        }
                    }
                    foreach (var bt in se.burnThrough)
                        ClipCircle(m, ToMap(m, body, bt.lat, bt.lon), bt.burnM * pxPerM, new Color(1f, 0.6f, 0.2f, 0.8f));
                }

                // Engagement rings
                if (C4ISRSettings.MapRings)
                    foreach (var n in net.nodes)
                        if (n.role == NodeRole.SAM && n.maxMissileRange > 0)
                            ClipCircle(m, ToMap(m, body, n.vessel.latitude, n.vessel.longitude), n.maxMissileRange * pxPerM, n.radarOn ? AvionicsDraw.Hostile * 0.8f : AvionicsDraw.Friendly * 0.6f);

                // Routes and assignments
                foreach (var kv in C2Tasking.Orders)
                {
                    if (kv.Key == null || kv.Value.route.Count == 0 || net.Node(kv.Key) == null) continue;
                    Vector2 prev = ToMap(m, body, kv.Key.latitude, kv.Key.longitude);
                    for (int i = 0; i < kv.Value.route.Count; i++)
                    {
                        var g = kv.Value.route[(kv.Value.routeIndex + i) % kv.Value.route.Count];
                        var p = ToMap(m, body, g.x, g.y);
                        ClipLine(m, prev, p, 1.5f, AvionicsDraw.Friendly * 0.8f, dashed: true);
                        if (In(m, p)) AvionicsDraw.Box(p, 3, 1.5f, AvionicsDraw.Friendly);
                        prev = p;
                    }
                }
                if (iads != null)
                    foreach (var s in iads.sites.Values)
                        if (s.assigned != null)
                            ClipLine(m, ToMap(m, body, s.node.vessel.latitude, s.node.vessel.longitude), ToMap(m, body, s.assigned.position), 1.5f, C2UI.Assignment, dashed: false);

                // Units
                foreach (var n in net.nodes)
                {
                    var p = ToMap(m, body, n.vessel.latitude, n.vessel.longitude);
                    if (!In(m, p)) continue;
                    C2UI.NodeSymbol(p, n, C2UI.SelectedUnit == n.vessel);
                    labels.Add((p + new Vector2(9, -7), n.Name, n.connected ? AvionicsDraw.HudText : AvionicsDraw.Neutral));
                }

                // Faction bases
                if (FactionsScenario.Instance != null)
                    foreach (var fb in FactionsScenario.Instance.bases)
                    {
                        var bp = ToMap(m, body, fb.lat, fb.lon);
                        if (!In(m, bp)) continue;
                        var fac = FactionsScenario.FactionByName(fb.faction);
                        var bc = fac != null ? fac.colour : AvionicsDraw.Neutral;
                        AvionicsDraw.Box(bp, 9, 2.5f, bc);
                        AvionicsDraw.Box(bp, 4, 2f, bc);
                        if (fb.hasRunway) AvionicsDraw.Line(ToMap(m, body, fb.latA, fb.lonA), ToMap(m, body, fb.latB, fb.lonB), 3f, bc * 0.8f);
                        labels.Add((bp + new Vector2(12, 2), fb.site == "Runway" ? "KSC" : fb.site, bc));
                    }

                // Downed pilots of our side
                foreach (var sv in CSARScenario.ActiveFor(net.team))
                {
                    if (sv.status != SurvivorStatus.Down) continue;
                    var sp = ToMap(m, body, sv.lat, sv.lon);
                    if (!In(m, sp)) continue;
                    AvionicsDraw.Circle(sp, 8, 2f, AvionicsDraw.Hostile, 16);
                    AvionicsDraw.Line(sp + new Vector2(-5, 0), sp + new Vector2(5, 0), 2f, AvionicsDraw.Hostile);
                    AvionicsDraw.Line(sp + new Vector2(0, -5), sp + new Vector2(0, 5), 2f, AvionicsDraw.Hostile);
                    labels.Add((sp + new Vector2(10, -7), $"CSAR {sv.kerbal.Split(' ')[0]}", AvionicsDraw.Hostile));
                }

                // AI sorties (virtual) the player's side can see
                if (FactionsScenario.Instance != null)
                    foreach (var fl in FactionsScenario.Instance.flights)
                    {
                        if (fl.state == FlightState.Materialized || fl.state == FlightState.Done || !AICommander.Detected(fl)) continue;
                        var fp = ToMap(m, body, fl.lat, fl.lon);
                        if (!In(m, fp)) continue;
                        var fac = FactionsScenario.FactionByName(fl.faction);
                        var fc = fac != null ? fac.colour : AvionicsDraw.Hostile;
                        AvionicsDraw.Diamond(fp, 8, 2f, fc);
                        AvionicsDraw.Diamond(fp, 3, 2f, fc);
                        labels.Add((fp + new Vector2(10, -7), $"{fl.Callsign} x{fl.alive}", fc));
                    }

                // Loitering munitions: loiter ring, line to target, symbol
                foreach (var lm in LoiteringMunitions.Flying(net.team))
                {
                    var p = ToMap(m, body, lm.vessel.latitude, lm.vessel.longitude);
                    var lp = ToMap(m, body, lm.loiterLat, lm.loiterLon);
                    if (lm.state != LMState.Terminal) ClipCircle(m, lp, lm.loiterRadius * pxPerM, C2UI.MunitionColor * 0.7f);
                    if (lm.CanStrike) ClipLine(m, p, ToMap(m, body, lm.TargetPos), 1.5f, lm.state == LMState.Terminal ? AvionicsDraw.Missile : AvionicsDraw.Unknown, dashed: lm.state != LMState.Terminal);
                    if (!In(m, p)) continue;
                    C2UI.MunitionSymbol(p, lm, C2UI.SelectedMunition == lm, C2UI.Heading(lm.vessel));
                    labels.Add((p + new Vector2(9, -7), lm.Label, C2UI.MunitionColor));
                }

                // Tracks with a 60 s velocity leader
                if (C4ISRSettings.MapTracks)
                    foreach (var t in net.tracks.Values)
                    {
                        var p = ToMap(m, body, t.position);
                        if (!In(m, p)) continue;
                        var lead = ToMap(m, body, t.position + t.velocity * 60f);
                        C2UI.TrackSymbol(p, t, C2UI.SelectedTrack == t, In(m, lead) ? lead : p);
                        labels.Add((p + new Vector2(9, -7), t.Label, C2UI.TrackColor(t)));
                    }
                AvionicsDraw.End();
            }
            foreach (var l in labels) AvionicsDraw.ShadowLabel(new Rect(l.p.x, l.p.y, 140, 14), l.text, AvionicsDraw.LabelSmall, l.c);

            // Cursor readout and placing hint
            if (m.Contains(e.mousePosition))
            {
                var g = FromMap(m, body, e.mousePosition);
                string biome = "";
                try { biome = ScienceUtil.GetExperimentBiome(body, g.x, g.y); } catch { }
                AvionicsDraw.ShadowLabel(new Rect(m.x + 6, m.yMax - 18, m.width - 12, 14),
                    $"{g.x:F3}°, {g.y:F3}°   ground {AvionicsDraw.FormatAlt(body.TerrainAltitude(g.x, g.y, true))}   {biome}", AvionicsDraw.LabelSmall, AvionicsDraw.HudText);
            }
            if (C2UI.Placing != PlaceMode.None)
                AvionicsDraw.ShadowLabel(new Rect(m.x + 6, m.y + 18, m.width - 12, 14), C2UI.PlacingHint, AvionicsDraw.LabelSmall, AvionicsDraw.LockBox);

            HandleMapInput(m, net, body, e);
        }

        static float GridStepKm()
        {
            float span = C4ISRSettings.MapSpansKm[C4ISRSettings.MapZoomIndex];
            return span <= 20 ? 2 : span <= 80 ? 10 : span <= 320 ? 50 : 200;
        }

        static void HandleMapInput(Rect m, TeamNetwork net, CelestialBody body, Event e)
        {
            if (e.type == EventType.ScrollWheel && m.Contains(e.mousePosition))
            {
                C4ISRSettings.MapZoomIndex = Mathf.Clamp(C4ISRSettings.MapZoomIndex + (e.delta.y > 0 ? 1 : -1), 0, C4ISRSettings.MapSpansKm.Length - 1);
                e.Use();
                return;
            }
            if (e.type == EventType.MouseDown && m.Contains(e.mousePosition))
            {
                var g = FromMap(m, body, e.mousePosition);
                var geo = new Vector3d(g.x, g.y, Math.Max(0, body.TerrainAltitude(g.x, g.y, true)));
                if (e.button == 1) { AvionicsContacts.AddGPS(geo, "C2 mark"); e.Use(); return; }
                if (e.button != 0) return;
                if (C2UI.ApplyPlacement(geo)) { e.Use(); return; }
                if (net != null && Pick(m, net, body, e.mousePosition)) { e.Use(); return; }
                panning = true;
                follow = false;
                panStart = e.mousePosition;
                panLat = lat0; panLon = lon0;
                e.Use();
            }
            if (panning && e.type == EventType.MouseDrag)
            {
                double s = m.width / C4ISRSettings.MapSpansKm[C4ISRSettings.MapZoomIndex];
                double k = KmPerDeg(body);
                var d = e.mousePosition - panStart;
                lat0 = Math.Max(-85, Math.Min(85, panLat + d.y / s / k));
                lon0 = panLon - d.x / s / k / Math.Max(0.05, Math.Cos(lat0 * Math.PI / 180));
                e.Use();
            }
        }

        static bool Pick(Rect m, TeamNetwork net, CelestialBody body, Vector2 mouse)
        {
            const float r2 = 12 * 12;
            float best = r2;
            Vessel unit = null;
            NetTrack track = null;
            foreach (var n in net.nodes)
            {
                float d = (ToMap(m, body, n.vessel.latitude, n.vessel.longitude) - mouse).sqrMagnitude;
                if (d < best) { best = d; unit = n.vessel; track = null; }
            }
            foreach (var t in net.tracks.Values)
            {
                float d = (ToMap(m, body, t.position) - mouse).sqrMagnitude;
                if (d < best) { best = d; track = t; unit = null; }
            }
            ModuleLoiteringMunition lmPick = null;
            foreach (var lm in LoiteringMunitions.Flying(net.team))
            {
                float d = (ToMap(m, body, lm.vessel.latitude, lm.vessel.longitude) - mouse).sqrMagnitude;
                if (d < best) { best = d; lmPick = lm; unit = null; track = null; }
            }
            if (lmPick != null) { C2UI.Select(lmPick); return true; }
            if (unit != null) { C2UI.Select(unit); follow = false; return true; }
            if (track != null) { C2UI.Select(track); return true; }
            return false;
        }

        /// <summary>Rebuild the imagery when the view moves well away from the current tile, the zoom changes or the layer changes.</summary>
        static void EnsureTile(Rect m, CelestialBody body, TeamNetwork net)
        {
            if (tileBuilding || C4ISRCore.Instance == null || m.width < 10) return;
            var source = net != null ? C2Map.Effective(net.team) : MapSource.Grid;
            double k = KmPerDeg(body);
            double halfLat = C4ISRSettings.MapSpansKm[C4ISRSettings.MapZoomIndex] * (m.height / m.width) / k * 0.75; // 1.5x the view
            double halfLon = C4ISRSettings.MapSpansKm[C4ISRSettings.MapZoomIndex] / k / Math.Max(0.05, Math.Cos(lat0 * Math.PI / 180)) * 0.75;
            bool covers = tile.texture != null && tile.body == body && tile.layer == C4ISRSettings.MapLayer && tile.source == source
                && lat0 - halfLat / 1.5 >= tile.latMin && lat0 + halfLat / 1.5 <= tile.latMax
                && lon0 - halfLon / 1.5 >= tile.lonMin && lon0 + halfLon / 1.5 <= tile.lonMax
                && Math.Abs((tile.latMax - tile.latMin) - 2 * halfLat) < halfLat * 0.2; // same zoom
            if (covers) return;
            tile.body = body;
            tile.layer = C4ISRSettings.MapLayer;
            tile.source = source;
            tile.latMin = Math.Max(-90, lat0 - halfLat); tile.latMax = Math.Min(90, lat0 + halfLat);
            tile.lonMin = lon0 - halfLon; tile.lonMax = lon0 + halfLon;
            int tw = 256, th = Mathf.Clamp((int)(256 * m.height / m.width), 64, 256);
            tileBuilding = true;
            C4ISRCore.Instance.StartCoroutine(BuildTile(tw, th));
        }

        static System.Collections.IEnumerator BuildTile(int w, int h)
        {
            yield return C2Map.Build(tile, w, h);
            tileBuilding = false;
        }

        static bool showCover = true;

        /// <summary>Threat score 0-100: hostile tracks closing fast on a friendly unit or base score highest (missiles above aircraft).</summary>
        public static float Threat(NetTrack t, TeamNetwork net)
        {
            if (!t.hostile) return 0;
            float best = 0;
            foreach (var n in net.nodes)
            {
                if (n.vessel == null) continue;
                Vector3 rel = (Vector3)n.vessel.CoM - t.position;
                float d = Mathf.Max(200f, rel.magnitude);
                float closing = Vector3.Dot(t.velocity - (Vector3)n.vessel.Velocity(), rel.normalized);
                float tti = closing > 1 ? d / closing : 9999f;
                best = Mathf.Max(best, Mathf.Clamp01(1f - tti / 300f) * 70f + Mathf.Clamp01(1f - d / 60000f) * 30f);
            }
            if (t.isMissile) best = Mathf.Min(100f, best * 1.5f + 20f);
            return best;
        }

        // ---- Track table ----

        static void DrawTable(Rect r, TeamNetwork net)
        {
            if (AvionicsDraw.Begin()) { AvionicsDraw.FilledRect(r, C2UI.Panel); AvionicsDraw.End(); }
            if (net == null) return;
            float[] cols = [0, 40, 130, 190, 236, 270, 324, 360, 400, 500];
            string[] heads = ["TRK", "CLASS", "ALT", "SPD", "HDG", "RANGE", "Q", "THR", "SOURCE", "ASSIGNED"];
            for (int i = 0; i < heads.Length; i++) AvionicsDraw.ColorLabel(new Rect(r.x + 6 + cols[i], r.y + 2, 120, 14), heads[i], AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
            var list = new List<NetTrack>(net.tracks.Values);
            var refPos = C2UI.SelectedUnit != null ? C2UI.SelectedUnit.CoM : FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.CoM : Vector3.zero;
            list.Sort((a, b) => Threat(b, net).CompareTo(Threat(a, net))); // most dangerous first
            var body = FlightGlobals.currentMainBody;
            var iads = IADSController.For(net.team);
            var view = new Rect(r.x, r.y + 18, r.width, r.height - 18);
            tableScroll = GUI.BeginScrollView(view, tableScroll, new Rect(0, 0, r.width - 16, list.Count * 16));
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                float y = i * 16;
                var row = new Rect(0, y, r.width - 16, 16);
                if (C2UI.SelectedTrack == t && AvionicsDraw.Begin()) { AvionicsDraw.FilledRect(row, new Color(0.3f, 0.25f, 0.1f, 0.6f)); AvionicsDraw.End(); }
                if (GUI.Button(row, "", GUIStyle.none)) C2UI.Select(t);
                double alt = body != null ? FlightGlobals.getAltitudeAtPos(t.position, body) : 0;
                var v = t.velocity;
                Vector3 up = body != null ? ((Vector3)(t.position - (Vector3)body.position)).normalized : Vector3.up;
                var north = body != null ? Vector3.ProjectOnPlane(body.transform.up, up).normalized : Vector3.forward;
                var hv = Vector3.ProjectOnPlane(v, up);
                float hdg = hv.sqrMagnitude > 1 ? (Vector3.SignedAngle(north, hv, up) + 360) % 360 : 0;
                var site = iads != null ? IADSController.AssignedTo(iads, t) : null;
                string assigned = site != null ? site.node.Name : "";
                foreach (var o in C2Tasking.Orders) if (o.Value.track == t && o.Key != null) assigned += (assigned.Length > 0 ? ", " : "") + o.Key.GetName();
                float q = SensorEngine.Quality(net.team.Name, t.vessel);
                float thr = Threat(t, net);
                string[] cells = [t.Label, C2UI.TrackClass(t), AvionicsDraw.FormatAlt(alt), $"{v.magnitude:F0}", $"{hdg:000}", AvionicsDraw.FormatRange((t.position - refPos).magnitude), q < 0 ? "-" : $"{q * 100:F0}", t.hostile ? $"{thr:F0}" : "", t.SourceString, assigned];
                for (int c = 0; c < cells.Length; c++)
                    AvionicsDraw.ColorLabel(new Rect(6 + cols[c], y, (c + 1 < cols.Length ? cols[c + 1] - cols[c] : 200) - 4, 16), cells[c], AvionicsDraw.LabelSmall, c == 1 ? C2UI.TrackColor(t) : AvionicsDraw.HudText);
            }
            GUI.EndScrollView();
        }

        // ---- Clipped drawing ----

        static bool In(Rect m, Vector2 p) => p.x >= m.x && p.x <= m.xMax && p.y >= m.y && p.y <= m.yMax;

        /// <summary>Liang–Barsky clip, then draw.</summary>
        public static void ClipLine(Rect m, Vector2 a, Vector2 b, float width, Color c, bool dashed)
        {
            float t0 = 0, t1 = 1;
            Vector2 d = b - a;
            float[] p = [-d.x, d.x, -d.y, d.y];
            float[] q = [a.x - m.x, m.xMax - a.x, a.y - m.y, m.yMax - a.y];
            for (int i = 0; i < 4; i++)
            {
                if (Mathf.Approximately(p[i], 0)) { if (q[i] < 0) return; continue; }
                float t = q[i] / p[i];
                if (p[i] < 0) { if (t > t1) return; if (t > t0) t0 = t; }
                else { if (t < t0) return; if (t < t1) t1 = t; }
            }
            var a2 = a + d * t0; var b2 = a + d * t1;
            if (dashed) AvionicsDraw.DashedLine(a2, b2, width, c, 6, 4);
            else AvionicsDraw.Line(a2, b2, width, c);
        }

        public static void ClipCircle(Rect m, Vector2 c, float r, Color col)
        {
            if (r < 2 || r > 20000) return;
            int seg = Mathf.Clamp((int)(r / 4), 24, 180);
            Vector2 prev = c + new Vector2(r, 0);
            for (int i = 1; i <= seg; i++)
            {
                float a = i * 2 * Mathf.PI / seg;
                var p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                ClipLine(m, prev, p, 1.2f, col, dashed: false);
                prev = p;
            }
        }
    }
}
