using System.Collections.Generic;
using UnityEngine;

using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Option B: C2 in KSP's map view. Draws the team's units, tracks, data links (including CommNet relay paths)
    /// and SAM rings over the planet, with a docked drawer for selection and tasking.
    /// </summary>
    public static class C2MapOverlay
    {
        public const int DrawerID = 71730311;
        static readonly List<(Vector2 p, NetNode n)> nodeScreen = new();
        static readonly List<(Vector2 p, NetTrack t)> trackScreen = new();
        static Vector2 drawerScroll;

        public static Rect Drawer
        {
            get
            {
                var r = C4ISRSettings.DrawerRect;
                if (r.x < 0) r.x = Screen.width - r.width - 10; // docked right
                r.height = Mathf.Min(r.height, Screen.height - r.y - 60);
                return r;
            }
        }

        static bool ToScreen(Vector3d world, out Vector2 p)
        {
            p = default;
            var cam = PlanetariumCamera.Camera;
            var body = FlightGlobals.currentMainBody;
            if (cam == null || body == null || body.scaledBody == null) return false;
            Vector3 scaled = ScaledSpace.LocalToScaledSpace(world);
            Vector3 centre = body.scaledBody.transform.position;
            // Hidden behind the planet?
            Vector3 toCam = cam.transform.position - scaled;
            if (Vector3.Dot(scaled - centre, toCam) < 0)
            {
                float r = (float)(body.Radius * ScaledSpace.InverseScaleFactor);
                Vector3 d = -toCam.normalized;
                Vector3 oc = cam.transform.position - centre;
                float b = Vector3.Dot(oc, d), c = oc.sqrMagnitude - r * r * 0.995f;
                if (b * b - c > 0 && -b - Mathf.Sqrt(b * b - c) < toCam.magnitude) return false;
            }
            Vector3 sp = cam.WorldToScreenPoint(scaled);
            if (sp.z < 0) return false;
            p = new Vector2(sp.x, Screen.height - sp.y);
            return true;
        }

        /// <summary>Planet surface point under the mouse, as lat/lon/ground altitude.</summary>
        public static bool MouseGeo(out Vector3d geo)
        {
            geo = default;
            var cam = PlanetariumCamera.Camera;
            var body = FlightGlobals.currentMainBody;
            if (cam == null || body == null || body.scaledBody == null) return false;
            var ray = cam.ScreenPointToRay(Input.mousePosition);
            Vector3 centre = body.scaledBody.transform.position;
            float r = (float)(body.Radius * ScaledSpace.InverseScaleFactor);
            Vector3 oc = ray.origin - centre;
            float b = Vector3.Dot(oc, ray.direction), c = oc.sqrMagnitude - r * r, disc = b * b - c;
            if (disc < 0) return false;
            float t = -b - Mathf.Sqrt(disc);
            if (t < 0) return false;
            Vector3d world = ScaledSpace.ScaledToLocalSpace(ray.origin + ray.direction * t);
            double lat = body.GetLatitude(world), lon = body.GetLongitude(world);
            geo = new Vector3d(lat, lon, System.Math.Max(0, body.TerrainAltitude(lat, lon, true)));
            return true;
        }

        public static void DrawOverlay()
        {
            var net = DataLinkNetwork.PlayerNetwork;
            var body = FlightGlobals.currentMainBody;
            if (net == null || body == null) return;
            var e = Event.current;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;

            nodeScreen.Clear();
            trackScreen.Clear();
            foreach (var n in net.nodes) if (ToScreen(n.vessel.CoMD, out var p)) nodeScreen.Add((p, n));
            if (C4ISRSettings.OverlayTracks) foreach (var t in net.tracks.Values) if (ToScreen(t.position, out var p)) trackScreen.Add((p, t));

            if (e.type == EventType.Repaint && AvionicsDraw.Begin())
            {
                if (C4ISRSettings.OverlayLinks) DrawLinks(net);
                if (C4ISRSettings.OverlayRings)
                    foreach (var n in net.nodes)
                        if (n.role == NodeRole.SAM && n.maxMissileRange > 0) SurfaceRing(body, n.vessel.latitude, n.vessel.longitude, n.maxMissileRange, n.radarOn ? AvionicsDraw.Hostile : AvionicsDraw.Friendly * 0.7f);
                var iads = IADSController.For(net.team);
                if (iads != null)
                    foreach (var s in iads.sites.Values)
                        if (s.assigned != null && ToScreen(s.node.vessel.CoMD, out var a) && ToScreen(s.assigned.position, out var b))
                            AvionicsDraw.Line(a, b, 1.5f, C2UI.Assignment);
                foreach (var kv in C2Tasking.Orders)
                {
                    if (kv.Key == null || kv.Value.route.Count == 0 || net.Node(kv.Key) == null || !ToScreen(kv.Key.CoMD, out var prev)) continue;
                    foreach (var g in kv.Value.route)
                    {
                        if (!ToScreen(body.GetWorldSurfacePosition(g.x, g.y, g.z), out var p)) break;
                        AvionicsDraw.DashedLine(prev, p, 1.2f, AvionicsDraw.Friendly * 0.8f, 6, 4);
                        AvionicsDraw.Box(p, 3, 1.5f, AvionicsDraw.Friendly);
                        prev = p;
                    }
                }
                foreach (var (p, n) in nodeScreen) C2UI.NodeSymbol(p, n, C2UI.SelectedUnit == n.vessel);
                foreach (var (p, t) in trackScreen)
                {
                    ToScreen(t.position + t.velocity * 60f, out var lead);
                    C2UI.TrackSymbol(p, t, C2UI.SelectedTrack == t, lead == default ? p : lead);
                }
                AvionicsDraw.End();
            }
            if (C4ISRSettings.OverlayLabels && e.type == EventType.Repaint)
            {
                foreach (var (p, n) in nodeScreen) AvionicsDraw.ShadowLabel(new Rect(p.x + 10, p.y - 7, 160, 14), n.Name, AvionicsDraw.LabelSmall, n.connected ? AvionicsDraw.HudText : AvionicsDraw.Neutral);
                foreach (var (p, t) in trackScreen) AvionicsDraw.ShadowLabel(new Rect(p.x + 10, p.y - 7, 80, 14), t.Label, AvionicsDraw.LabelSmall, C2UI.TrackColor(t));
            }

            // Legend
            if (e.type == EventType.Repaint)
            {
                string mode = C4ISRSettings.GlobalC2 ? "GLOBAL C2 · ON" : $"{C4ISRSettings.LinkModel.ToString().ToUpper()} LINKS";
                AvionicsDraw.ShadowLabel(new Rect(16, Screen.height - 120, 400, 14), mode, AvionicsDraw.Label, C2UI.Accent);
                AvionicsDraw.ShadowLabel(new Rect(16, Screen.height - 104, 400, 14), "solid = direct   dashed = relay   red = lost", AvionicsDraw.LabelSmall, AvionicsDraw.HudText);
                AvionicsDraw.ShadowLabel(new Rect(16, Screen.height - 90, 400, 14), $"{net.ConnectedCount}/{net.nodes.Count} linked · {net.tracks.Count} tracks", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
                if (C2UI.Placing != PlaceMode.None)
                    AvionicsDraw.ShadowLabel(new Rect(Input.mousePosition.x + 16, Screen.height - Input.mousePosition.y + 10, 320, 14), C2UI.PlacingHint, AvionicsDraw.LabelSmall, AvionicsDraw.LockBox);
            }

            GUI.matrix = old;
        }

        /// <summary>Clicks on the map view itself (not on a window): place orders or select symbols.</summary>
        public static void HandleClick()
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0) return;
            Vector2 mouse = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (C4ISRCore.MouseOverC4ISRWindow(mouse)) return;
            if (C2UI.Placing != PlaceMode.None)
            {
                if (MouseGeo(out var geo)) { C2UI.ApplyPlacement(geo); e.Use(); }
                return;
            }
            float best = 14 * 14;
            Vessel unit = null; NetTrack track = null;
            foreach (var (p, n) in nodeScreen) { float d = (p - mouse).sqrMagnitude; if (d < best) { best = d; unit = n.vessel; track = null; } }
            foreach (var (p, t) in trackScreen) { float d = (p - mouse).sqrMagnitude; if (d < best) { best = d; track = t; unit = null; } }
            if (unit != null) { C2UI.Select(unit); e.Use(); }
            else if (track != null) { C2UI.Select(track); e.Use(); }
        }

        static void DrawLinks(TeamNetwork net)
        {
            if (C4ISRSettings.GlobalC2)
            {
                // Star from the C2 hub: an HQ if there is one, else the active vessel.
                NetNode hub = net.nodes.Find(n => n.role == NodeRole.HQ) ?? net.Node(FlightGlobals.ActiveVessel);
                if (hub == null || !ToScreen(hub.vessel.CoMD, out var h)) return;
                foreach (var n in net.nodes)
                    if (n != hub && ToScreen(n.vessel.CoMD, out var p))
                        AvionicsDraw.DashedLine(h, p, 1, n.connected ? C2UI.LinkGood * 0.7f : C2UI.LinkLost, 8, 6);
                return;
            }
            if (C4ISRSettings.LinkModel == LinkModel.CommNet && CommNet.CommNetScenario.CommNetEnabled)
            {
                foreach (var n in net.nodes)
                {
                    var path = n.vessel.Connection?.ControlPath;
                    if (path == null || path.Count == 0)
                    {
                        if (ToScreen(n.vessel.CoMD, out var p)) AvionicsDraw.Circle(p, 10, 1.5f, C2UI.LinkLost, 12);
                        continue;
                    }
                    foreach (var link in path)
                        if (link?.start != null && link.end != null && ToScreen(link.start.precisePosition, out var a) && ToScreen(link.end.precisePosition, out var b))
                            AvionicsDraw.DashedLine(a, b, 1.2f, link.signalStrength > 0.5 ? C2UI.LinkGood : C2UI.LinkWeak, 8, 5);
                }
                return;
            }
            float range = C4ISRSettings.LOSLinkRangeKm * 1000f;
            for (int i = 0; i < net.nodes.Count; i++)
                for (int j = i + 1; j < net.nodes.Count; j++)
                {
                    var a = net.nodes[i]; var b = net.nodes[j];
                    if (!a.connected || !b.connected || !DataLinkNetwork.LineOfSight(a.vessel, b.vessel, range)) continue;
                    if (ToScreen(a.vessel.CoMD, out var pa) && ToScreen(b.vessel.CoMD, out var pb)) AvionicsDraw.Line(pa, pb, 1.2f, C2UI.LinkGood);
                }
        }

        static void SurfaceRing(CelestialBody body, double lat, double lon, float radiusM, Color c)
        {
            const int seg = 48;
            double angDist = radiusM / body.Radius; // radians
            double la = lat * Mathf.Deg2Rad, lo = lon * Mathf.Deg2Rad;
            Vector2 prev = default; bool havePrev = false;
            for (int i = 0; i <= seg; i++)
            {
                double brg = i * 2 * Mathf.PI / seg;
                double la2 = System.Math.Asin(System.Math.Sin(la) * System.Math.Cos(angDist) + System.Math.Cos(la) * System.Math.Sin(angDist) * System.Math.Cos(brg));
                double lo2 = lo + System.Math.Atan2(System.Math.Sin(brg) * System.Math.Sin(angDist) * System.Math.Cos(la), System.Math.Cos(angDist) - System.Math.Sin(la) * System.Math.Sin(la2));
                bool ok = ToScreen(body.GetWorldSurfacePosition(la2 * Mathf.Rad2Deg, lo2 * Mathf.Rad2Deg, 0), out var p);
                if (ok && havePrev) AvionicsDraw.Line(prev, p, 1.2f, c);
                prev = p; havePrev = ok;
            }
        }

        // ---- Drawer ----

        public static void DrawDrawer(int id)
        {
            var r = Drawer;
            var net = DataLinkNetwork.PlayerNetwork;
            if (GUI.Button(new Rect(r.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { C4ISRSettings.ShowMapOverlay = false; return; }
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            float y = 22, w = r.width - 12, q = (w - 6) / 4;
            if (GUI.Button(new Rect(6, y, q, 20), "Tracks", C4ISRSettings.OverlayTracks ? sel : btn)) C4ISRSettings.OverlayTracks = !C4ISRSettings.OverlayTracks;
            if (GUI.Button(new Rect(8 + q, y, q, 20), "Links", C4ISRSettings.OverlayLinks ? sel : btn)) C4ISRSettings.OverlayLinks = !C4ISRSettings.OverlayLinks;
            if (GUI.Button(new Rect(10 + 2 * q, y, q, 20), "Rings", C4ISRSettings.OverlayRings ? sel : btn)) C4ISRSettings.OverlayRings = !C4ISRSettings.OverlayRings;
            if (GUI.Button(new Rect(12 + 3 * q, y, q, 20), "Labels", C4ISRSettings.OverlayLabels ? sel : btn)) C4ISRSettings.OverlayLabels = !C4ISRSettings.OverlayLabels;
            y += 22;
            float h3 = (w - 4) / 3;
            if (GUI.Button(new Rect(6, y, h3, 20), "Console", C4ISRSettings.ShowConsole ? sel : btn)) C4ISRSettings.ShowConsole = !C4ISRSettings.ShowConsole;
            if (GUI.Button(new Rect(8 + h3, y, h3, 20), "IADS", C4ISRSettings.ShowIADS ? sel : btn)) C4ISRSettings.ShowIADS = !C4ISRSettings.ShowIADS;
            if (GUI.Button(new Rect(10 + 2 * h3, y, h3, 20), "GPS mark", C2UI.Placing == PlaceMode.DropGPS ? sel : btn)) C2UI.Placing = C2UI.Placing == PlaceMode.DropGPS ? PlaceMode.None : PlaceMode.DropGPS;
            y += 26;

            float alertsH = 90;
            var view = new Rect(6, y, w, r.height - y - alertsH - 8);
            drawerScroll = GUI.BeginScrollView(view, drawerScroll, new Rect(0, 0, w - 16, 700));
            C2UI.DrawSelectionPanel(new Rect(0, 0, w - 18, 700), net);
            GUI.EndScrollView();

            y = r.height - alertsH - 4;
            AvionicsDraw.ColorLabel(new Rect(6, y, w, 14), "ALERTS", AvionicsDraw.LabelSmall, C2UI.Accent); y += 14;
            var iads = net != null ? IADSController.For(net.team) : null;
            if (net != null)
                foreach (var n in net.nodes)
                    if (!n.connected && n.LinkEnabled && y < r.height - 18)
                    {
                        AvionicsDraw.ColorLabel(new Rect(6, y, w, 14), $"{n.Name}: no link ({Time.time - n.lastConnectedTime:F0} s)", AvionicsDraw.LabelSmall, C2UI.LinkLost);
                        y += 13;
                    }
            if (iads != null)
                for (int i = 0; i < iads.log.Count && y < r.height - 18; i++, y += 13)
                    AvionicsDraw.ColorLabel(new Rect(6, y, w, 14), iads.log[i], AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
            GUI.DragWindow(new Rect(0, 0, r.width - 24, 20));
        }
    }
}
