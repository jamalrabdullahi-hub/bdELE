using System.Collections.Generic;
using UnityEngine;

using BDArmory.UI;

namespace BDArmory.Avionics
{
    public enum PlaceMode { None, MoveTo, Strike, Waypoint, DropGPS, LMLaunch, LMLoiter, LMStrike }

    /// <summary>
    /// Selection, symbols and the tasking panels shared by the C2 console (option A) and the map view overlay (option B).
    /// </summary>
    public static class C2UI
    {
        public static Vessel SelectedUnit;
        public static NetTrack SelectedTrack;
        public static PlaceMode Placing = PlaceMode.None;
        public static ModuleLoiteringMunition SelectedMunition;
        static Vector2 routeScroll;

        public static readonly Color Panel = new(0.06f, 0.08f, 0.09f, 0.94f);
        public static readonly Color Accent = new(0.50f, 0.82f, 0.55f);
        public static readonly Color LinkGood = new(0.50f, 0.82f, 0.55f, 0.9f);
        public static readonly Color LinkWeak = new(0.89f, 0.76f, 0.35f, 0.9f);
        public static readonly Color LinkLost = new(0.75f, 0.34f, 0.29f, 0.9f);
        public static readonly Color Assignment = new(0.50f, 0.82f, 0.55f, 0.95f);

        public static void Select(Vessel unit) { SelectedUnit = unit; SelectedTrack = null; SelectedMunition = null; Placing = PlaceMode.None; }
        public static void Select(ModuleLoiteringMunition m) { SelectedMunition = m; SelectedUnit = null; Placing = PlaceMode.None; }

        public static readonly Color MunitionColor = new(1f, 0.62f, 0.25f);

        /// <summary>Loitering munition symbol: small triangle in its heading, ring when manual, box when selected.</summary>
        public static void MunitionSymbol(Vector2 p, ModuleLoiteringMunition m, bool selected, float headingDeg)
        {
            var c = m.state == LMState.Terminal ? AvionicsDraw.Missile : m.state == LMState.Pending ? AvionicsDraw.Unknown : MunitionColor;
            AvionicsDraw.Triangle(p, 6, headingDeg, 2, c);
            if (m.manual) AvionicsDraw.Circle(p, 9, 1.2f, AvionicsDraw.LockBox, 12);
            if (selected) AvionicsDraw.Box(p, 12, 1.5f, AvionicsDraw.LockBox);
        }

        public static float Heading(Vessel v)
        {
            var up = (v.CoM - v.mainBody.position).normalized;
            var north = Vector3.ProjectOnPlane(v.mainBody.transform.up, up).normalized;
            var vel = Vector3.ProjectOnPlane(v.srf_velocity, up);
            if (vel.sqrMagnitude < 1) return 0;
            return Vector3.SignedAngle(north, vel, up);
        }
        public static void Select(NetTrack track) { SelectedTrack = track; Placing = PlaceMode.None; }

        public static Color TrackColor(NetTrack t) =>
            t.isMissile ? AvionicsDraw.Missile : t.hostile ? AvionicsDraw.Hostile : t.team == null ? AvionicsDraw.Unknown : AvionicsDraw.Neutral;

        public static string TrackClass(NetTrack t) =>
            (t.hostile ? "HOSTILE" : t.team == null ? "UNKNOWN" : "NEUTRAL") + (t.isMissile ? " MSL" : t.vessel != null && t.vessel.LandedOrSplashed ? " SFC" : " AIR");

        public static string RoleShort(NodeRole r) => r switch
        {
            NodeRole.Fighter => "FTR", NodeRole.AEW => "AEW", NodeRole.SAM => "SAM", NodeRole.EWR => "EWR",
            NodeRole.HQ => "HQ", NodeRole.Ship => "SHIP", _ => "GND",
        };

        // ---- Symbols (call between AvionicsDraw.Begin and End) ----

        public static void NodeSymbol(Vector2 p, NetNode n, bool selected)
        {
            Color c = n.connected ? AvionicsDraw.Friendly : AvionicsDraw.Neutral;
            switch (n.role)
            {
                case NodeRole.Fighter:
                case NodeRole.AEW: AvionicsDraw.Circle(p, 6, 2, c, 16); if (n.role == NodeRole.AEW) AvionicsDraw.Circle(p, 10, 1, c, 16); break;
                case NodeRole.SAM: AvionicsDraw.Box(p, 6, 2, c); AvionicsDraw.Line(p + new Vector2(-6, 6), p + new Vector2(6, -6), 1.5f, c); break;
                case NodeRole.EWR: AvionicsDraw.Box(p, 6, 2, c); AvionicsDraw.Circle(p, 3, 1.5f, c, 10); break;
                case NodeRole.HQ: AvionicsDraw.Box(p, 7, 2, c); AvionicsDraw.Box(p, 3, 2, c); break;
                default: AvionicsDraw.Diamond(p, 7, 2, c); break;
            }
            if (selected) AvionicsDraw.Box(p, 12, 1.5f, AvionicsDraw.LockBox);
        }

        public static void TrackSymbol(Vector2 p, NetTrack t, bool selected, Vector2 leaderEnd)
        {
            Color c = TrackColor(t);
            if (t.isMissile) AvionicsDraw.Triangle(p, 6, 0, 2, c);
            else if (t.hostile) AvionicsDraw.Diamond(p, 7, 2, c);
            else AvionicsDraw.Box(p, 5, 2, c);
            if ((leaderEnd - p).sqrMagnitude > 4) AvionicsDraw.Line(p, leaderEnd, 1, c);
            if (selected) AvionicsDraw.Box(p, 12, 1.5f, AvionicsDraw.LockBox);
        }

        // ---- Panels ----

        static GUIStyle Btn => BDArmorySetup.ButtonStyle;
        static GUIStyle Sel => BDArmorySetup.SelectedButtonStyle;

        static bool Button(Rect r, string text, bool on = false) => GUI.Button(r, text, on ? Sel : Btn);

        /// <summary>Details and tasking for the selected unit or track. Returns the height used.</summary>
        public static float DrawSelectionPanel(Rect r, TeamNetwork net)
        {
            if (net == null) { AvionicsDraw.ColorLabel(r, "No data link (no weapon manager)", AvionicsDraw.Label, AvionicsDraw.ScopeText); return 20; }
            if (SelectedTrack != null && (SelectedTrack.vessel == null || !net.tracks.ContainsValue(SelectedTrack))) SelectedTrack = null;
            var unit = SelectedUnit != null ? net.Node(SelectedUnit) : null;
            if (unit == null) SelectedUnit = null;

            float y = r.y, w = r.width, x = r.x;
            y = DrawApprovals(new Rect(x, y, w, r.height), net);
            if (SelectedMunition != null && (SelectedMunition.vessel == null || SelectedMunition.state == LMState.Done)) SelectedMunition = null;
            if (SelectedMunition != null)
            {
                y = DrawMunition(new Rect(x, y, w, r.height), net, SelectedMunition) + 8;
                if (SelectedTrack != null) y = DrawTrack(new Rect(x, y, w, r.yMax - y), net, SelectedTrack, null);
                return y - r.y;
            }
            if (unit == null && SelectedTrack == null)
            {
                AvionicsDraw.ColorLabel(new Rect(x, y, w, 40), "Click a unit or track on the map,\nor a row in the lists.", AvionicsDraw.Label, AvionicsDraw.ScopeText);
                return 40;
            }

            if (unit != null)
            {
                y = DrawUnit(new Rect(x, y, w, r.height), net, unit);
                y += 8;
            }
            if (SelectedTrack != null)
                y = DrawTrack(new Rect(x, y, w, r.yMax - y), net, SelectedTrack, unit);
            return y - r.y;
        }

        static float DrawUnit(Rect r, TeamNetwork net, NetNode n)
        {
            float x = r.x, y = r.y, w = r.width, half = (w - 4) / 2, third = (w - 8) / 3;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "SELECTED UNIT", AvionicsDraw.LabelSmall, Accent); y += 14;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 18), n.Name, AvionicsDraw.Header, AvionicsDraw.HudText); y += 18;
            string link = !n.LinkEnabled ? "link off" : !n.connected ? "NO LINK" : n.path == LinkPath.Global ? "global C2" : n.path == LinkPath.Relay ? $"relay, {n.hops} hops" : "direct";
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"{n.role} · {link} · {AvionicsDraw.FormatAlt(n.vessel.altitude)}", AvionicsDraw.LabelSmall, n.connected ? AvionicsDraw.ScopeText : LinkLost); y += 14;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"Missiles {n.readyMissiles}  ·  {(n.hasRadar ? (n.radarOn ? "radar ON" : "radar off") : "no radar")}", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 14;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"Task: {C2Tasking.TaskString(n.vessel)}", AvionicsDraw.LabelSmall, AvionicsDraw.Friendly); y += 14;
            if (AvionicsSettings.CombatOperators)
            { AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"Crew: {OperatorEffects.CrewSummary(n.vessel)}", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 14; }
            y += 4;

            var v = n.vessel;
            if (n.IsAir || n.role == NodeRole.Ship || n.role == NodeRole.Ground)
            {
                if (Button(new Rect(x, y, half, 20), "Patrol (add WP)", Placing == PlaceMode.Waypoint)) Placing = Placing == PlaceMode.Waypoint ? PlaceMode.None : PlaceMode.Waypoint;
                if (Button(new Rect(x + half + 4, y, half, 20), "Move to", Placing == PlaceMode.MoveTo)) Placing = Placing == PlaceMode.MoveTo ? PlaceMode.None : PlaceMode.MoveTo;
                y += 22;
                if (Button(new Rect(x, y, half, 20), "Strike GPS", Placing == PlaceMode.Strike)) Placing = Placing == PlaceMode.Strike ? PlaceMode.None : PlaceMode.Strike;
                GUI.enabled = SelectedTrack != null;
                if (Button(new Rect(x + half + 4, y, half, 20), SelectedTrack != null ? $"Intercept {SelectedTrack.Label}" : "Intercept (pick)")) C2Tasking.Intercept(v, SelectedTrack);
                GUI.enabled = true;
                y += 22;
                if (Button(new Rect(x, y, half, 20), "Escort active")) C2Tasking.Escort(v, FlightGlobals.ActiveVessel);
                if (Button(new Rect(x + half + 4, y, half, 20), "RTB")) C2Tasking.RTB(v);
                y += 22;
                if (Button(new Rect(x, y, w, 20), "Release (AI free)")) C2Tasking.Release(v);
                y += 24;
            }

            var rack = LoiteringMunitions.OnRack(v);
            if (rack.Count > 0)
            {
                AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"LOITERING MUNITIONS: {rack.Count} on board", AvionicsDraw.LabelSmall, Accent); y += 14;
                if (Button(new Rect(x, y, w, 20), "Launch one: loiter at (click map)", Placing == PlaceMode.LMLaunch)) Placing = Placing == PlaceMode.LMLaunch ? PlaceMode.None : PlaceMode.LMLaunch;
                y += 24;
            }

            if (n.IsAir || AirOps.OpFor(v) != null)
            {
                AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "AIR OPS", AvionicsDraw.LabelSmall, Accent); y += 14;
                var op = AirOps.OpFor(v);
                if (Button(new Rect(x, y, third, 20), "Land", op?.kind == AirOpKind.Landing)) { C2Tasking.Release(v); AirOps.StartLanding(v); }
                var tanker = NearestTanker(net, v);
                GUI.enabled = tanker != null;
                if (Button(new Rect(x + third + 4, y, third, 20), "Refuel", op?.kind == AirOpKind.Refuel)) { C2Tasking.Release(v); AirOps.StartRefuel(v, tanker); }
                GUI.enabled = true;
                if (Button(new Rect(x + 2 * third + 8, y, third, 20), "Tanker", op?.kind == AirOpKind.Tanker)) { C2Tasking.Release(v); AirOps.StartTanker(v); }
                y += 22;
                if (tanker != null && op == null) { AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"Nearest tanker: {tanker.GetName()}", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 14; }
                y += 4;
            }

            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "ROE", AvionicsDraw.LabelSmall, Accent); y += 14;
            bool free = C2Tasking.WeaponsFree(v);
            if (Button(new Rect(x, y, half, 20), "HOLD FIRE", !free)) C2Tasking.SetWeaponsFree(v, false);
            if (Button(new Rect(x + half + 4, y, half, 20), "WEAPONS FREE", free)) C2Tasking.SetWeaponsFree(v, true);
            y += 24;

            var o = C2Tasking.OrderFor(v);
            if (o != null && o.route.Count > 0 && (o.task == C2Task.Patrol || o.task == C2Task.MoveTo || o.task == C2Task.Strike))
            {
                AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "ROUTE", AvionicsDraw.LabelSmall, Accent); y += 14;
                float h = Mathf.Min(o.route.Count * 14, 70);
                routeScroll = GUI.BeginScrollView(new Rect(x, y, w, h), routeScroll, new Rect(0, 0, w - 16, o.route.Count * 14));
                for (int i = 0; i < o.route.Count; i++)
                {
                    var g = o.route[i];
                    AvionicsDraw.ColorLabel(new Rect(0, i * 14, w - 16, 14), $"{(i == o.routeIndex ? ">" : " ")}WP{i + 1} {g.x:F3}, {g.y:F3}  {AvionicsDraw.FormatAlt(g.z)}", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
                }
                GUI.EndScrollView();
                y += h + 4;
            }

            if (n.role == NodeRole.SAM && IADSController.For(net.team) is IADSController.TeamIADS iads && iads.SiteFor(v) is IADSController.Site site)
            {
                AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"IADS: {site.State}{(site.manual ? " (manual)" : "")}", AvionicsDraw.LabelSmall, AvionicsDraw.Friendly); y += 16;
                GUI.enabled = SelectedTrack != null;
                if (Button(new Rect(x, y, half, 20), SelectedTrack != null ? $"Engage {SelectedTrack.Label}" : "Engage (pick)")) IADSController.Assign(net.team, v, SelectedTrack);
                GUI.enabled = site.assigned != null;
                if (Button(new Rect(x + half + 4, y, half, 20), "Clear")) IADSController.Assign(net.team, v, null);
                GUI.enabled = true;
                y += 24;
            }
            if (Button(new Rect(x, y, third, 20), "Focus")) { if (v.loaded) FlightGlobals.ForceSetActiveVessel(v); }
            if (Button(new Rect(x + third + 4, y, third, 20), "GPS here")) AvionicsContacts.AddGPS(new Vector3d(v.latitude, v.longitude, v.altitude), n.Name);
            if (Button(new Rect(x + 2 * third + 8, y, third, 20), "Deselect")) SelectedUnit = null;
            return y + 22;
        }

        /// <summary>Downed pilots (CSAR) and munitions waiting for strike approval, at the top of the panel.</summary>
        static float DrawApprovals(Rect r, TeamNetwork net)
        {
            float x = r.x, y = r.y, w = r.width;
            foreach (var s in CSARScenario.ActiveFor(net.team))
            {
                AvionicsDraw.FilledRect(new Rect(x, y, w, 40), new Color(0.35f, 0.08f, 0.08f, 0.85f));
                AvionicsDraw.ColorLabel(new Rect(x + 4, y + 2, w - 8, 14), $"CSAR {CSARScenario.Summary(s)}", AvionicsDraw.LabelSmall, s.status == SurvivorStatus.PickedUp ? AvionicsDraw.Friendly : AvionicsDraw.Hostile);
                float b = (w - 12) / 2;
                var wmA = BDArmorySetup.Instance?.ActiveWeaponManager;
                if (Button(new Rect(x + 4, y + 18, b, 19), "GPS target"))
                    if (wmA != null) foreach (var g in BDArmory.UI.BDATargetManager.GPSTargetList(wmA.Team)) if (g.name == s.gpsName) { wmA.designatedGPSInfo = g; break; }
                GUI.enabled = SelectedUnit != null && s.status == SurvivorStatus.Down;
                if (Button(new Rect(x + 8 + b, y + 18, b, 19), SelectedUnit != null ? $"Send {SelectedUnit.GetName()}" : "Send (select a unit)")) CSARScenario.Send(SelectedUnit, s);
                GUI.enabled = true;
                y += 44;
            }
            foreach (var m in LoiteringMunitions.Flying(net.team))
            {
                if (m.state != LMState.Pending || m.target == null) continue;
                AvionicsDraw.FilledRect(new Rect(x, y, w, 40), new Color(0.35f, 0.28f, 0.05f, 0.85f));
                float d = (m.target.CoM - m.vessel.CoM).magnitude;
                AvionicsDraw.ColorLabel(new Rect(x + 4, y + 2, w - 8, 14), $"{m.Label} requests strike: {m.targetLabel} ({d / 1000f:F1} km)", AvionicsDraw.LabelSmall, AvionicsDraw.Unknown);
                float b = (w - 16) / 3;
                if (Button(new Rect(x + 4, y + 18, b, 19), "APPROVE")) m.Approve();
                if (Button(new Rect(x + 8 + b, y + 18, b, 19), "Wave off")) m.WaveOff();
                if (Button(new Rect(x + 12 + 2 * b, y + 18, b, 19), "Watch")) LoiteringMunitions.WatchStatic(m, false);
                y += 44;
            }
            return y;
        }

        static float DrawMunition(Rect r, TeamNetwork net, ModuleLoiteringMunition m)
        {
            float x = r.x, y = r.y, w = r.width, half = (w - 4) / 2, third = (w - 8) / 3;
            var v = m.vessel;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "LOITERING MUNITION", AvionicsDraw.LabelSmall, Accent); y += 14;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 18), m.Label, AvionicsDraw.Header, MunitionColor); y += 18;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"{(m.manual ? "MANUAL - " : "")}{m.state}{(m.CanStrike ? $" > {m.targetLabel}" : "")}", AvionicsDraw.LabelSmall, m.state == LMState.Terminal ? AvionicsDraw.Missile : AvionicsDraw.HudText); y += 14;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"{AvionicsDraw.FormatAlt(v.altitude)} · {v.srfSpeed:F0} m/s · {(v.CoM - (Vector3)m.LoiterWorld).magnitude / 1000f:F1} km from loiter pt", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 14;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"Home: {(string.IsNullOrEmpty(m.homeName) ? "-" : m.homeName)}", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 18;

            int mi = System.Array.IndexOf(ModuleLoiteringMunition.StrikeModes, m.strikeMode);
            if (Button(new Rect(x, y, half, 20), m.strikeMode)) m.strikeMode = ModuleLoiteringMunition.StrikeModes[(mi + 1) % ModuleLoiteringMunition.StrikeModes.Length];
            int ti = System.Array.IndexOf(ModuleLoiteringMunition.TargetTypes, m.targetType);
            if (Button(new Rect(x + half + 4, y, half, 20), m.targetType)) m.targetType = ModuleLoiteringMunition.TargetTypes[(ti + 1) % ModuleLoiteringMunition.TargetTypes.Length];
            y += 22;
            GUI.enabled = m.state == LMState.Pending;
            if (Button(new Rect(x, y, half, 20), "APPROVE", m.state == LMState.Pending)) m.Approve();
            GUI.enabled = m.state == LMState.Pending || m.state == LMState.Terminal;
            if (Button(new Rect(x + half + 4, y, half, 20), "Wave off")) m.WaveOff();
            GUI.enabled = true;
            y += 22;
            if (Button(new Rect(x, y, half, 20), "Loiter at (map)", Placing == PlaceMode.LMLoiter)) Placing = Placing == PlaceMode.LMLoiter ? PlaceMode.None : PlaceMode.LMLoiter;
            if (Button(new Rect(x + half + 4, y, half, 20), "Strike point (map)", Placing == PlaceMode.LMStrike)) Placing = Placing == PlaceMode.LMStrike ? PlaceMode.None : PlaceMode.LMStrike;
            y += 22;
            GUI.enabled = SelectedTrack != null && SelectedTrack.vessel != null;
            if (Button(new Rect(x, y, w, 20), SelectedTrack != null ? $"Strike track {SelectedTrack.Label}" : "Strike track (select one)")) m.Strike(SelectedTrack.vessel, null, "C2 track");
            GUI.enabled = true;
            y += 22;
            if (Button(new Rect(x, y, third, 20), "Watch")) LoiteringMunitions.WatchStatic(m, false);
            if (Button(new Rect(x + third + 4, y, third, 20), m.manual ? "MANUAL" : "Control", m.manual)) { if (m.manual) m.manual = false; else LoiteringMunitions.WatchStatic(m, true); }
            if (Button(new Rect(x + 2 * third + 8, y, third, 20), "DETONATE")) m.Detonate("C2");
            y += 22;
            if (Button(new Rect(x, y, w, 20), "Deselect")) SelectedMunition = null;
            return y + 22;
        }

        /// <summary>Nearest other team aircraft carrying an AARS drogue.</summary>
        static Vessel NearestTanker(TeamNetwork net, Vessel self)
        {
            Vessel best = null; float bestD = float.MaxValue;
            foreach (var n in net.nodes)
            {
                if (n.vessel == self || !n.IsAir) continue;
                bool hasDrogue = false;
                foreach (var p in n.vessel.parts) if (p.Modules.Contains("FuelDrogue")) { hasDrogue = true; break; }
                if (!hasDrogue) continue;
                float d = (n.vessel.CoM - self.CoM).sqrMagnitude;
                if (d < bestD) { bestD = d; best = n.vessel; }
            }
            return best;
        }

        static float DrawTrack(Rect r, TeamNetwork net, NetTrack t, NetNode unit)
        {
            float x = r.x, y = r.y, w = r.width, half = (w - 4) / 2;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "SELECTED TRACK", AvionicsDraw.LabelSmall, Accent); y += 14;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 18), $"{t.Label}  {t.Name}", AvionicsDraw.Header, TrackColor(t)); y += 18;
            var body = FlightGlobals.currentMainBody;
            double alt = body != null ? FlightGlobals.getAltitudeAtPos(t.position, body) : 0;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"{TrackClass(t)} · {AvionicsDraw.FormatAlt(alt)} · {t.velocity.magnitude:F0} m/s", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 14;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"Source: {t.SourceString} ({t.reporters.Count} sensors)", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 16;
            var iads = IADSController.For(net.team);
            var site = iads != null ? IADSController.AssignedTo(iads, t) : null;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), site != null ? $"IADS: {site.node.Name}" : "IADS: unassigned", AvionicsDraw.LabelSmall, site != null ? AvionicsDraw.Friendly : AvionicsDraw.ScopeText); y += 16;
            if (iads != null)
            {
                // Quick assign to any SAM site that can reach it.
                foreach (var s in iads.sites.Values)
                {
                    if (y > r.yMax - 46) break;
                    float range = (t.position - s.node.vessel.CoM).magnitude;
                    bool canReach = s.node.IsShooter && IADSController.CanReach(s, t);
                    GUI.enabled = canReach && s.assigned != t;
                    if (Button(new Rect(x, y, w, 20), $"Engage with {s.node.Name} ({range / 1000f:F0} km)", s.assigned == t)) IADSController.Assign(net.team, s.node.vessel, t);
                    GUI.enabled = true;
                    y += 22;
                }
            }
            var lms = LoiteringMunitions.Flying(net.team);
            if (lms.Count > 0 && t.hostile && !t.isMissile)
            {
                AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "LOITERING MUNITIONS", AvionicsDraw.LabelSmall, Accent); y += 14;
                foreach (var m in lms)
                {
                    if (y > r.yMax - 46) break;
                    float d = (t.position - m.vessel.CoM).magnitude;
                    bool on = m.target == t.vessel && m.state == LMState.Terminal;
                    if (Button(new Rect(x, y, w, 20), on ? $"{m.Label} striking ({d / 1000f:F1} km)" : $"Strike with {m.Label} ({d / 1000f:F1} km)", on)) m.Strike(t.vessel, null, "C2 tasking");
                    y += 22;
                }
            }
            if (Button(new Rect(x, y, half, 20), "Drop GPS on it")) AvionicsContacts.AddGPS(new Vector3d(body.GetLatitude(t.position), body.GetLongitude(t.position), alt), t.Label);
            if (Button(new Rect(x + half + 4, y, half, 20), "Deselect")) SelectedTrack = null;
            return y + 22;
        }

        /// <summary>Apply the armed map action at a lat/lon/alt. Returns true if it consumed the click.</summary>
        public static bool ApplyPlacement(Vector3d geo)
        {
            if (Placing == PlaceMode.None) return false;
            bool keep = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            var v = SelectedUnit;
            switch (Placing)
            {
                case PlaceMode.DropGPS: AvionicsContacts.AddGPS(geo, "C2 mark"); break;
                case PlaceMode.MoveTo: if (v != null) C2Tasking.MoveTo(v, WithCruiseAlt(v, geo)); break;
                case PlaceMode.Strike: if (v != null) C2Tasking.Strike(v, geo); break;
                case PlaceMode.Waypoint: if (v != null) C2Tasking.AddWaypoint(v, WithCruiseAlt(v, geo)); keep = true; break;
                case PlaceMode.LMLaunch:
                    if (v != null)
                    {
                        var err = LoiteringMunitions.Launch(v, v.mainBody.GetWorldSurfacePosition(geo.x, geo.y, geo.z));
                        ScreenMessages.PostScreenMessage(err ?? $"{v.GetName()}: loitering munition away", 3f, ScreenMessageStyle.UPPER_CENTER);
                    }
                    break;
                case PlaceMode.LMLoiter:
                    if (SelectedMunition != null) SelectedMunition.SetLoiter(SelectedMunition.vessel.mainBody.GetWorldSurfacePosition(geo.x, geo.y, geo.z));
                    break;
                case PlaceMode.LMStrike:
                    if (SelectedMunition != null) SelectedMunition.Strike(null, SelectedMunition.vessel.mainBody.GetWorldSurfacePosition(geo.x, geo.y, geo.z + 1), "C2 map point");
                    break;
            }
            if (!keep) Placing = PlaceMode.None;
            return true;
        }

        /// <summary>Aircraft keep their current altitude (min 1 km above the ground) when sent to a map point.</summary>
        static Vector3d WithCruiseAlt(Vessel v, Vector3d geo)
        {
            if (v == null || v.LandedOrSplashed) return geo;
            return new Vector3d(geo.x, geo.y, Mathf.Max((float)v.altitude, (float)geo.z + 1000f));
        }

        public static string PlacingHint => Placing switch
        {
            PlaceMode.MoveTo => "Click the map: move here",
            PlaceMode.Strike => "Click the map: strike here",
            PlaceMode.Waypoint => "Click the map: add patrol waypoint (Esc to finish)",
            PlaceMode.DropGPS => "Click the map: drop GPS point (Shift: keep dropping)",
            PlaceMode.LMLaunch => "Click the map: launch a loitering munition to loiter here",
            PlaceMode.LMLoiter => "Click the map: move the munition's loiter point here",
            PlaceMode.LMStrike => "Click the map: munition strikes this point",
            _ => "",
        };
    }
}
