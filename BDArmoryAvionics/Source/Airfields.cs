using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>A published approach to one end of a drawn runway: fixes flown in order, then the 3.5° final.</summary>
    public class Approach
    {
        public string name = "Approach";
        public bool towardB = true;                     // lands from threshold A toward B (false: lands from B toward A)
        public readonly List<Vector3d> fixes = new();   // lat, lon, altitude ASL

        public void Save(ConfigNode n)
        {
            n.AddValue("name", name); n.AddValue("towardB", towardB);
            foreach (var f in fixes) n.AddValue("fix", $"{f.x.ToString("R", CultureInfo.InvariantCulture)},{f.y.ToString("R", CultureInfo.InvariantCulture)},{f.z.ToString("F0", CultureInfo.InvariantCulture)}");
        }

        public static Approach Load(ConfigNode n)
        {
            var a = new Approach { name = n.GetValue("name") ?? "Approach" };
            bool.TryParse(n.GetValue("towardB") ?? "true", out a.towardB);
            foreach (var s in n.GetValues("fix"))
            {
                var p = s.Split(',');
                if (p.Length == 3 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var la) && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var al))
                    a.fixes.Add(new Vector3d(la, lo, al));
            }
            return a;
        }
    }

    /// <summary>A runway drawn by the player: two thresholds, an owning team ("" = everyone) and its approaches.</summary>
    public class DrawnRunway
    {
        public string name = "Strip";
        public string body = "Kerbin";
        public string team = "";
        public bool open = true;
        public double latA, lonA, latB, lonB;
        public bool hasA, hasB;
        public readonly List<Approach> approaches = new();

        public CelestialBody Body => FlightGlobals.GetBodyByName(body) ?? FlightGlobals.GetHomeBody();
        public bool Complete => hasA && hasB;
        public Runway ToRunway() => new() { name = name, body = Body, latA = latA, lonA = lonA, latB = latB, lonB = lonB };

        /// <summary>Runway numbers from the magnetic-free bearing, e.g. "09/27".</summary>
        public string Numbers
        {
            get
            {
                if (!Complete) return "--/--";
                var r = ToRunway();
                Vector3d a = r.A, b = r.B;
                var bd = Body;
                Vector3 up = (a - bd.position).normalized;
                Vector3 north = VectorUtils.GetNorthVector(a, bd);
                float brg = Mathf.Repeat(Vector3.SignedAngle(north, Vector3.ProjectOnPlane((Vector3)(b - a), up), up), 360);
                int n1 = Mathf.RoundToInt(brg / 10f); if (n1 == 0) n1 = 36;
                int n2 = (n1 + 18 - 1) % 36 + 1;
                return $"{n1:00}/{n2:00}";
            }
        }

        public void Save(ConfigNode n)
        {
            n.AddValue("name", name); n.AddValue("body", body); n.AddValue("team", team); n.AddValue("open", open);
            n.AddValue("hasA", hasA); n.AddValue("hasB", hasB);
            n.AddValue("latA", latA.ToString("R", CultureInfo.InvariantCulture)); n.AddValue("lonA", lonA.ToString("R", CultureInfo.InvariantCulture));
            n.AddValue("latB", latB.ToString("R", CultureInfo.InvariantCulture)); n.AddValue("lonB", lonB.ToString("R", CultureInfo.InvariantCulture));
            foreach (var a in approaches) a.Save(n.AddNode("APPROACH"));
        }

        public static DrawnRunway Load(ConfigNode n)
        {
            var r = new DrawnRunway { name = n.GetValue("name") ?? "Strip", body = n.GetValue("body") ?? "Kerbin", team = n.GetValue("team") ?? "" };
            bool.TryParse(n.GetValue("open") ?? "true", out r.open);
            bool.TryParse(n.GetValue("hasA"), out r.hasA); bool.TryParse(n.GetValue("hasB"), out r.hasB);
            double.TryParse(n.GetValue("latA"), NumberStyles.Float, CultureInfo.InvariantCulture, out r.latA);
            double.TryParse(n.GetValue("lonA"), NumberStyles.Float, CultureInfo.InvariantCulture, out r.lonA);
            double.TryParse(n.GetValue("latB"), NumberStyles.Float, CultureInfo.InvariantCulture, out r.latB);
            double.TryParse(n.GetValue("lonB"), NumberStyles.Float, CultureInfo.InvariantCulture, out r.lonB);
            foreach (var a in n.GetNodes("APPROACH")) r.approaches.Add(Approach.Load(a));
            return r;
        }
    }

    /// <summary>Drawn runways and approaches, kept with the save.</summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
    public class AirfieldsScenario : ScenarioModule
    {
        public static AirfieldsScenario Instance;
        public readonly List<DrawnRunway> runways = new();
        public static bool ATCEnabled = true;

        public override void OnAwake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        public override void OnSave(ConfigNode node)
        {
            node.AddValue("ATCEnabled", ATCEnabled);
            foreach (var r in runways) r.Save(node.AddNode("RUNWAY"));
        }

        public override void OnLoad(ConfigNode node)
        {
            runways.Clear();
            if (bool.TryParse(node.GetValue("ATCEnabled") ?? "true", out var atc)) ATCEnabled = atc;
            foreach (var n in node.GetNodes("RUNWAY")) runways.Add(DrawnRunway.Load(n));
        }

        public static IEnumerable<Runway> RunwaysFor(CelestialBody body, BDArmory.Competition.BDTeam team)
        {
            if (Instance == null) yield break;
            foreach (var r in Instance.runways)
            {
                if (!r.Complete || !r.open || r.Body != body) continue;
                if (team != null && r.team != "" && r.team != team.Name) continue;
                yield return r.ToRunway();
            }
        }

        public static DrawnRunway Find(string runwayName)
        {
            if (Instance == null || runwayName == null) return null;
            string n = Runways.BaseName(runwayName);
            foreach (var r in Instance.runways) if (r.name == n) return r;
            return null;
        }

        /// <summary>The first published approach to this runway end (lat/lon/alt fixes), or null.</summary>
        public static Approach ApproachFor(Runway rw)
        {
            var d = Find(rw?.name);
            if (d == null) return null;
            bool towardB = !Runways.IsReversed(rw.name);
            foreach (var a in d.approaches) if (a.towardB == towardB && a.fixes.Count > 0) return a;
            return null;
        }
    }

    /// <summary>
    /// Airfields window: draw runways and approaches by clicking on the ground in the flight view.
    /// Drawn runways are used by AI landing, RTB and ATC. Everything is drawn in the world while the window is open.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class AirfieldsWindow : MonoBehaviour
    {
        public static bool Show;
        const int WindowID = 71730360;
        static Rect rect = new(Screen.width - 1000, 140, 400, 480);
        enum Mode { None, A, B, Fix }
        Mode mode;
        int selRunway = -1, selApproach = -1, selFix = -1;
        Vector2 scroll;
        string nameEdit = "";
        readonly List<LineRenderer> lines = new();
        int lineUsed;
        Material lineMat;

        static List<DrawnRunway> List => AirfieldsScenario.Instance?.runways;
        DrawnRunway Sel => List != null && selRunway >= 0 && selRunway < List.Count ? List[selRunway] : null;
        Approach SelApp => Sel != null && selApproach >= 0 && selApproach < Sel.approaches.Count ? Sel.approaches[selApproach] : null;

        void OnDestroy() { foreach (var l in lines) if (l != null) Destroy(l.gameObject); }

        // ---- Placing points by clicking the ground ----
        void Update()
        {
            if (!Show || mode == Mode.None || Sel == null) return;
            if (Input.GetKeyDown(KeyCode.Escape)) { mode = Mode.None; return; }
            if (!Input.GetMouseButtonDown(0)) return;
            Vector2 gm = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (rect.Contains(gm)) return;
            if (!GroundUnderMouse(out double lat, out double lon)) { ScreenMessages.PostScreenMessage("No ground under the cursor", 2f, ScreenMessageStyle.UPPER_CENTER); return; }
            var r = Sel;
            r.body = FlightGlobals.currentMainBody.bodyName;
            switch (mode)
            {
                case Mode.A: r.latA = lat; r.lonA = lon; r.hasA = true; mode = r.hasB ? Mode.None : Mode.B; break;
                case Mode.B: r.latB = lat; r.lonB = lon; r.hasB = true; mode = Mode.None; break;
                case Mode.Fix:
                    var app = SelApp;
                    if (app == null) { mode = Mode.None; break; }
                    app.fixes.Add(new Vector3d(lat, lon, AutoAltitude(r, app, lat, lon)));
                    selFix = app.fixes.Count - 1;
                    break; // keep placing until the button is clicked again or Escape
            }
        }

        /// <summary>Terrain under the mouse; beyond the loaded terrain, where the view ray meets the planet's surface.</summary>
        static bool GroundUnderMouse(out double lat, out double lon)
        {
            lat = lon = 0;
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;
            if (cam == null) return false;
            var body = FlightGlobals.currentMainBody;
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            Vector3 hitPos;
            if (Physics.Raycast(ray, out var hit, 300000f, (1 << 15) | (1 << 28))) hitPos = hit.point;
            else
            {
                // Sphere at the terrain height under the active vessel
                double r = body.Radius + Math.Max(0, FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.terrainAltitude : 0);
                Vector3d o = ray.origin - (Vector3)body.position, d = ray.direction;
                double b = Vector3d.Dot(o, d), c = o.sqrMagnitude - r * r, disc = b * b - c;
                if (disc < 0) return false;
                double t = -b - Math.Sqrt(disc);
                if (t < 0) return false;
                hitPos = (Vector3)(body.position + o + d * t);
            }
            lat = body.GetLatitude(hitPos); lon = body.GetLongitude(hitPos);
            return true;
        }

        /// <summary>A sensible altitude for a new fix: on a 3° slope from the threshold, at least 450 m above it, at most 3000 m.</summary>
        static double AutoAltitude(DrawnRunway r, Approach app, double lat, double lon)
        {
            var rw = r.ToRunway();
            var bd = r.Body;
            Vector3d thr = app.towardB ? rw.A : rw.B;
            double elev = app.towardB ? bd.TerrainAltitude(r.latA, r.lonA, true) : bd.TerrainAltitude(r.latB, r.lonB, true);
            Vector3d p = bd.GetWorldSurfacePosition(lat, lon, elev);
            double dist = (p - thr).magnitude;
            return Math.Round(elev + Math.Min(3000, Math.Max(450, dist * Math.Tan(3.0 * Math.PI / 180))) / 50) * 50;
        }

        /// <summary>Standard procedures generated from the runway geometry.</summary>
        static void Generate(DrawnRunway r, Approach app, string kind)
        {
            var rw = r.ToRunway(); var bd = r.Body;
            Vector3d thr = app.towardB ? rw.A : rw.B, far = app.towardB ? rw.B : rw.A;
            Vector3d up = (thr - bd.position).normalized;
            Vector3d d = Vector3d.Exclude(up, far - thr).normalized;     // landing direction
            Vector3d right = Vector3d.Cross(up, d).normalized;
            double elev = bd.GetAltitude(thr);
            app.fixes.Clear();
            void Add(Vector3d w, double agl) { app.fixes.Add(new Vector3d(bd.GetLatitude(w), bd.GetLongitude(w), Math.Round((elev + agl) / 50) * 50)); }
            switch (kind)
            {
                case "straight": // IAF 25 km, IF 15 km on the extended centreline
                    Add(thr - d * 25000, 2000); Add(thr - d * 15000, 1000); app.name = "Straight-in"; break;
                case "left": case "right": // downwind 3 km abeam, base, onto a 6 km final
                    double side = kind == "left" ? -1 : 1;
                    Add(thr + d * 4000 + right * side * 3000, 600); Add(thr + right * side * 3000, 500); Add(thr - d * 3500 + right * side * 3000, 450); Add(thr - d * 6000 + right * side * 1500, 400);
                    app.name = kind == "left" ? "Left pattern" : "Right pattern"; break;
                case "teardrop": // overhead, outbound 30° off, turn back onto a 10 km final
                    Add(thr + d * 1000, 1200); Add(thr - d * 10000 + right * 4000, 900); app.name = "Teardrop"; break;
            }
        }

        // ---- Window ----
        void OnGUI()
        {
            if (!Show || !BDArmorySetup.GAME_UI_ENABLED) return;
            rect = AvionicsCore.DrawWindow(WindowID, rect, Draw, "Airfields: runways and approaches", BDArmorySetup.BDGuiSkin);
            DrawLabels();
        }

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; mode = Mode.None; return; }
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle; var small = AvionicsDraw.LabelSmall;
            float w = rect.width - 16, y = 24;
            if (List == null) { GUI.Label(new Rect(8, y, w, 20), "No save loaded"); GUI.DragWindow(); return; }

            // Runway list
            if (GUI.Button(new Rect(8, y, 120, 20), "+ New runway", btn))
            {
                List.Add(new DrawnRunway { name = $"Strip {List.Count + 1}", body = FlightGlobals.currentMainBody.bodyName });
                selRunway = List.Count - 1; selApproach = -1; nameEdit = Sel.name; mode = Mode.A;
            }
            AirfieldsScenario.ATCEnabled = GUI.Toggle(new Rect(132, y, 70, 20), AirfieldsScenario.ATCEnabled, "ATC", AirfieldsScenario.ATCEnabled ? sel : btn);
            y += 24;
            scroll = GUI.BeginScrollView(new Rect(8, y, w, 92), scroll, new Rect(0, 0, w - 18, List.Count * 22));
            for (int i = 0; i < List.Count; i++)
            {
                var r = List[i];
                string t = $"{r.name}  {r.Numbers}  {(r.team == "" ? "all" : r.team)}{(r.open ? "" : "  CLOSED")}  {r.approaches.Count} app";
                if (GUI.Button(new Rect(0, i * 22, w - 18, 20), t, i == selRunway ? sel : btn)) { selRunway = i; selApproach = -1; selFix = -1; nameEdit = r.name; mode = Mode.None; }
            }
            GUI.EndScrollView();
            y += 96;

            var rwy = Sel;
            if (rwy == null) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 40), "Make a runway, then click its two ends on the ground.\nIt's drawn in the world while this window is open.", small, AvionicsDraw.ScopeText); GUI.DragWindow(); return; }

            // Runway details
            nameEdit = GUI.TextField(new Rect(8, y, 140, 20), nameEdit);
            if (GUI.Button(new Rect(152, y, 50, 20), "Name", btn) && nameEdit.Trim() != "") rwy.name = nameEdit.Trim();
            if (GUI.Button(new Rect(206, y, 90, 20), rwy.team == "" ? "Team: all" : $"Team: {rwy.team}", btn)) rwy.team = NextTeam(rwy.team);
            rwy.open = GUI.Toggle(new Rect(300, y, 60, 20), rwy.open, rwy.open ? "Open" : "Closed", rwy.open ? sel : btn);
            if (GUI.Button(new Rect(w - 20, y, 28, 20), "Del", btn)) { List.RemoveAt(selRunway); selRunway = -1; mode = Mode.None; GUI.DragWindow(); return; }
            y += 24;
            if (GUI.Button(new Rect(8, y, 120, 20), mode == Mode.A ? "Click threshold A…" : rwy.hasA ? "Re-place end A" : "Place end A", mode == Mode.A ? sel : btn)) mode = mode == Mode.A ? Mode.None : Mode.A;
            if (GUI.Button(new Rect(132, y, 120, 20), mode == Mode.B ? "Click threshold B…" : rwy.hasB ? "Re-place end B" : "Place end B", mode == Mode.B ? sel : btn)) mode = mode == Mode.B ? Mode.None : Mode.B;
            var av = FlightGlobals.ActiveVessel;
            if (GUI.Button(new Rect(256, y, w - 248, 20), "A/B = my craft", btn) && av != null && av.LandedOrSplashed)
            {
                // Sitting on the runway: set A here (if unset) else B here, along the craft's heading.
                if (!rwy.hasA) { rwy.latA = av.latitude; rwy.lonA = av.longitude; rwy.hasA = true; }
                else { rwy.latB = av.latitude; rwy.lonB = av.longitude; rwy.hasB = true; }
                rwy.body = av.mainBody.bodyName;
            }
            y += 24;
            if (rwy.Complete)
            {
                var rr = rwy.ToRunway();
                AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), $"Runway {rwy.Numbers}: {AvionicsDraw.FormatRange((float)(rr.B - rr.A).magnitude)} long, elevation {rr.Elevation:F0} m", small, AvionicsDraw.HudText);
            }
            else AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), "Click both runway ends on the ground (Esc cancels).", small, AvionicsDraw.Unknown);
            y += 20;
            if (!rwy.Complete) { GUI.DragWindow(); return; }

            // Approaches
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), "Approaches", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
            string[] ends = rwy.Numbers.Split('/');
            if (GUI.Button(new Rect(8, y, 110, 20), $"+ to rwy {ends[0]}", btn)) { rwy.approaches.Add(new Approach { towardB = true, name = $"RWY {ends[0]}" }); selApproach = rwy.approaches.Count - 1; selFix = -1; }
            if (GUI.Button(new Rect(122, y, 110, 20), $"+ to rwy {(ends.Length > 1 ? ends[1] : "?")}", btn)) { rwy.approaches.Add(new Approach { towardB = false, name = $"RWY {(ends.Length > 1 ? ends[1] : "?")}" }); selApproach = rwy.approaches.Count - 1; selFix = -1; }
            y += 22;
            for (int i = 0; i < rwy.approaches.Count; i++)
            {
                var a = rwy.approaches[i];
                if (GUI.Button(new Rect(8 + (i % 3) * 128, y + (i / 3) * 22, 124, 20), $"{a.name} ({a.fixes.Count})", i == selApproach ? sel : btn)) { selApproach = i; selFix = -1; mode = Mode.None; }
            }
            y += Mathf.Max(1, (rwy.approaches.Count + 2) / 3) * 22 + 2;

            var app = SelApp;
            if (app == null) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 30), "Without an approach the AI flies straight in from 8 km.", small, AvionicsDraw.ScopeText); GUI.DragWindow(); return; }
            if (GUI.Button(new Rect(8, y, 120, 20), mode == Mode.Fix ? "Clicking fixes… (stop)" : "Click to add fixes", mode == Mode.Fix ? sel : btn)) mode = mode == Mode.Fix ? Mode.None : Mode.Fix;
            if (GUI.Button(new Rect(132, y, 60, 20), "Clear", btn)) { app.fixes.Clear(); selFix = -1; }
            if (GUI.Button(new Rect(196, y, 60, 20), "Delete", btn)) { rwy.approaches.RemoveAt(selApproach); selApproach = -1; GUI.DragWindow(); return; }
            y += 22;
            AvionicsDraw.ColorLabel(new Rect(8, y + 3, 60, 16), "Generate:", small, AvionicsDraw.ScopeText);
            if (GUI.Button(new Rect(66, y, 74, 20), "Straight-in", btn)) Generate(rwy, app, "straight");
            if (GUI.Button(new Rect(142, y, 70, 20), "Left patt.", btn)) Generate(rwy, app, "left");
            if (GUI.Button(new Rect(214, y, 70, 20), "Right patt.", btn)) Generate(rwy, app, "right");
            if (GUI.Button(new Rect(286, y, 70, 20), "Teardrop", btn)) Generate(rwy, app, "teardrop");
            y += 24;
            for (int i = 0; i < app.fixes.Count && y < rect.height - 24; i++)
            {
                var f = app.fixes[i];
                bool s = i == selFix;
                if (GUI.Button(new Rect(8, y, 150, 20), $"{(i == 0 ? "IAF" : $"Fix {i + 1}")}  {f.z:F0} m", s ? sel : btn)) selFix = i;
                if (GUI.Button(new Rect(162, y, 30, 20), "-", btn)) app.fixes[i] = new Vector3d(f.x, f.y, Math.Max(100, f.z - 100));
                if (GUI.Button(new Rect(194, y, 30, 20), "+", btn)) app.fixes[i] = new Vector3d(f.x, f.y, f.z + 100);
                if (i > 0 && GUI.Button(new Rect(226, y, 26, 20), "↑", btn)) { (app.fixes[i - 1], app.fixes[i]) = (app.fixes[i], app.fixes[i - 1]); }
                if (GUI.Button(new Rect(254, y, 26, 20), "x", btn)) { app.fixes.RemoveAt(i); break; }
                y += 22;
            }
            AvionicsDraw.ColorLabel(new Rect(8, rect.height - 18, w, 14), "Fixes are flown in order, then the 3.5° final from 8 km.", small, AvionicsDraw.ScopeText);
            GUI.DragWindow(new Rect(0, 0, rect.width - 22, 20));
        }

        static string NextTeam(string current)
        {
            var teams = new List<string> { "" };
            if (FactionsScenario.Instance != null) foreach (var f in FactionsScenario.Instance.factions) if (!teams.Contains(f.team)) teams.Add(f.team);
            foreach (var v in FlightGlobals.VesselsLoaded) { var t = v.ActiveController().WM?.Team?.Name; if (t != null && !teams.Contains(t)) teams.Add(t); }
            int i = teams.IndexOf(current);
            return teams[(i + 1) % teams.Count];
        }

        // ---- World drawing (while the window is open) ----
        void LateUpdate()
        {
            lineUsed = 0;
            if (Show && List != null && FlightGlobals.currentMainBody != null && !MapView.MapIsEnabled)
            {
                var bd = FlightGlobals.currentMainBody;
                for (int i = 0; i < List.Count; i++)
                {
                    var r = List[i];
                    if (r.Body != bd) continue;
                    bool s = i == selRunway;
                    Color rc = !r.open ? new Color(1f, 0.3f, 0.2f) : s ? Color.white : new Color(0.8f, 0.8f, 0.8f, 0.7f);
                    if (r.hasA) Marker(Surf(bd, r.latA, r.lonA, 2), rc);
                    if (r.hasB) Marker(Surf(bd, r.latB, r.lonB, 2), rc);
                    if (!r.Complete) continue;
                    Vector3 a = Surf(bd, r.latA, r.lonA, 2), b = Surf(bd, r.latB, r.lonB, 2);
                    Line(new[] { a, b }, rc, 3f);
                    foreach (var app in r.approaches)
                    {
                        if (app.fixes.Count == 0) continue;
                        bool sa = s && app == SelApp;
                        Color ac = sa ? new Color(0.3f, 0.95f, 1f) : new Color(0.3f, 0.75f, 1f, 0.5f);
                        var rw = r.ToRunway(); if (!app.towardB) rw = rw.Reversed;
                        Vector3 thr = (Vector3)rw.A;
                        Vector3 up = (thr - (Vector3)bd.position).normalized;
                        Vector3 dir = Vector3.ProjectOnPlane((Vector3)(rw.B - rw.A), up).normalized;
                        var pts = new List<Vector3>();
                        foreach (var f in app.fixes) pts.Add(bd.GetWorldSurfacePosition(f.x, f.y, f.z));
                        Vector3 faf = thr - dir * 8000f + up * (8000f * Mathf.Tan(3.5f * Mathf.Deg2Rad));
                        pts.Add(faf); pts.Add(thr + dir * 300f);
                        Line(pts.ToArray(), ac, sa ? 2f : 1.2f);
                        foreach (var f in app.fixes) { Vector3 p = bd.GetWorldSurfacePosition(f.x, f.y, f.z); Line(new[] { p, Surf(bd, f.x, f.y, 0) }, new Color(ac.r, ac.g, ac.b, 0.3f), 0.8f); }
                    }
                }
            }
            for (int i = lineUsed; i < lines.Count; i++) if (lines[i] != null && lines[i].enabled) lines[i].enabled = false;
        }

        static Vector3 Surf(CelestialBody bd, double lat, double lon, double above) => bd.GetWorldSurfacePosition(lat, lon, bd.TerrainAltitude(lat, lon, true) + above);

        void Marker(Vector3 p, Color c)
        {
            Vector3 up = (p - (Vector3)FlightGlobals.currentMainBody.position).normalized;
            Line(new[] { p, p + up * 150f }, c, 2.5f);
        }

        void Line(Vector3[] pts, Color c, float width)
        {
            if (lineMat == null) lineMat = new Material(Shader.Find("KSP/Particles/Alpha Blended"));
            if (lineUsed >= lines.Count)
            {
                var go = new GameObject("ELE airfield line");
                var lr = go.AddComponent<LineRenderer>();
                lr.material = lineMat; lr.useWorldSpace = true;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; lr.receiveShadows = false;
                lines.Add(lr);
            }
            var l = lines[lineUsed++];
            l.enabled = true;
            l.positionCount = pts.Length;
            l.SetPositions(pts);
            l.startColor = l.endColor = c;
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            float dist = cam != null ? Vector3.Distance(cam.transform.position, pts[0]) : 1000f;
            l.widthMultiplier = Mathf.Max(0.5f, dist * 0.0015f * width);
        }

        void DrawLabels()
        {
            if (List == null || MapView.MapIsEnabled || Event.current.type != EventType.Repaint) return;
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null) return;
            var bd = FlightGlobals.currentMainBody;
            void Label(Vector3 world, string text, Color c)
            {
                Vector3 sp = cam.WorldToScreenPoint(world);
                if (sp.z <= 0) return;
                AvionicsDraw.ShadowLabel(new Rect(sp.x - 70, Screen.height - sp.y - 18, 140, 14), text, AvionicsDraw.LabelCenter, c);
            }
            foreach (var r in List)
            {
                if (r.Body != bd) continue;
                string[] n = r.Numbers.Split('/');
                if (r.hasA) Label(Surf(bd, r.latA, r.lonA, 160), $"{r.name} {n[0]}", Color.white);
                if (r.hasB) Label(Surf(bd, r.latB, r.lonB, 160), $"{r.name} {(n.Length > 1 ? n[1] : "")}", Color.white);
                foreach (var a in r.approaches)
                    for (int i = 0; i < a.fixes.Count; i++)
                        Label(bd.GetWorldSurfacePosition(a.fixes[i].x, a.fixes[i].y, a.fixes[i].z), $"{a.name} {(i == 0 ? "IAF" : (i + 1).ToString())} {a.fixes[i].z:F0}m", new Color(0.4f, 0.9f, 1f));
            }
        }
    }
}
