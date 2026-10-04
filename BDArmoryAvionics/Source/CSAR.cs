using System;
using System.Collections.Generic;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Targeting;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    public enum SurvivorStatus { Down, PickedUp, Rescued, Captured, Freed, KIA }

    /// <summary>A pilot who ejected (P-P or auto-eject). Reported to C2 as a GPS point on their team.</summary>
    public class Survivor
    {
        public string kerbal = "", team = "", aircraft = "", gpsName = "", rescuer = "", heldAt = "";
        public SurvivorStatus status = SurvivorStatus.Down;
        public double lat, lon, alt, downUT, captureUT;
        public uint evaId;

        public bool Active => status == SurvivorStatus.Down || status == SurvivorStatus.PickedUp;

        public void Save(ConfigNode n)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            n.AddValue("kerbal", kerbal); n.AddValue("team", team); n.AddValue("aircraft", aircraft); n.AddValue("gpsName", gpsName);
            n.AddValue("rescuer", rescuer); n.AddValue("heldAt", heldAt); n.AddValue("status", status); n.AddValue("evaId", evaId);
            n.AddValue("pos", string.Join(",", Array.ConvertAll(new[] { lat, lon, alt, downUT, captureUT }, d => d.ToString("R", ci))));
        }

        public static Survivor Load(ConfigNode n)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture; var st = System.Globalization.NumberStyles.Float;
            var s = new Survivor
            {
                kerbal = n.GetValue("kerbal") ?? "", team = n.GetValue("team") ?? "", aircraft = n.GetValue("aircraft") ?? "", gpsName = n.GetValue("gpsName") ?? "",
                rescuer = n.GetValue("rescuer") ?? "", heldAt = n.GetValue("heldAt") ?? "",
            };
            Enum.TryParse(n.GetValue("status"), out s.status); uint.TryParse(n.GetValue("evaId"), out s.evaId);
            var p = (n.GetValue("pos") ?? "").Split(',');
            if (p.Length == 5) { double.TryParse(p[0], st, ci, out s.lat); double.TryParse(p[1], st, ci, out s.lon); double.TryParse(p[2], st, ci, out s.alt); double.TryParse(p[3], st, ci, out s.downUT); double.TryParse(p[4], st, ci, out s.captureUT); }
            return s;
        }
    }

    /// <summary>
    /// Combat search and rescue.
    /// - An ejected pilot reports their position to C2: a GPS point "CSAR &lt;rank&gt; &lt;name&gt; (&lt;aircraft&gt;)" on their team,
    ///   moved to where they land.
    /// - Deep in enemy territory an enemy search team closes in on game time (sooner the nearer an enemy base); armed
    ///   enemy ground units within 150 m for 15 s capture them on the spot. Friendly aircraft overhead hold the search off.
    /// - Pick up: board any friendly craft (walk to the hatch, winch, or have the craft land / hover within 60 m: the
    ///   survivor climbs in). Rescued when that craft is recovered or stops at a friendly base.
    /// - Records: rescued / POW / KIA, rescues for the rescuing crew (Rescue Medal); POWs are freed when their base is taken.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
    public class CSARScenario : ScenarioModule
    {
        public static CSARScenario Instance;
        public readonly List<Survivor> survivors = new();
        float nextTick;

        public override void OnAwake()
        {
            Instance = this;
            GameEvents.onCrewBoardVessel.Add(OnBoard);
            GameEvents.onVesselRecovered.Add(OnRecovered);
            GameEvents.onKerbalStatusChange.Add(OnKerbalStatus);
        }

        void OnDestroy()
        {
            GameEvents.onCrewBoardVessel.Remove(OnBoard);
            GameEvents.onVesselRecovered.Remove(OnRecovered);
            GameEvents.onKerbalStatusChange.Remove(OnKerbalStatus);
            if (Instance == this) Instance = null;
        }

        public override void OnSave(ConfigNode node) { foreach (var s in survivors) s.Save(node.AddNode("SURVIVOR")); }
        public override void OnLoad(ConfigNode node) { survivors.Clear(); foreach (var n in node.GetNodes("SURVIVOR")) survivors.Add(Survivor.Load(n)); }

        public static Survivor Find(string kerbal) => Instance?.survivors.Find(s => s.kerbal == kerbal && s.Active);

        // ---- Ejection report ----

        public static void ReportEjection(Vessel eva, ProtoCrewMember crew, BDTeam team, string aircraft)
        {
            if (Instance == null || eva == null || crew == null) return;
            if (team == null) return; // not a combat craft
            Instance.survivors.RemoveAll(x => x.kerbal == crew.name && !x.Active);
            var old = Find(crew.name); if (old != null) RemoveGPS(old);
            var o = CombatOperators.Get(crew.name);
            var s = new Survivor
            {
                kerbal = crew.name, team = team.Name, aircraft = aircraft ?? "", evaId = eva.persistentId,
                lat = eva.latitude, lon = eva.longitude, alt = eva.altitude, downUT = Planetarium.GetUniversalTime(),
            };
            s.gpsName = SanitizeName($"CSAR {(o != null ? Ranks.Short(o) + " " : "")}{crew.name} ({s.aircraft})");
            s.captureUT = s.downUT + CaptureHours(s) * 3600;
            Instance.survivors.Add(s);
            SetGPS(s);
            o?.Count("ejections");
            string where = DescribeTerritory(s);
            CombatLog.Note($"EJECTION: {crew.name} out of {s.aircraft} - {where}", true, 2);
            AICommander.Log($"Pilot down: {crew.name} ({s.aircraft}), {where}. GPS \"{s.gpsName}\" sent to C2");
            Debug.Log($"[BDArmoryAvionics]: CSAR: {crew.name} ejected from {s.aircraft} at {s.lat:F4},{s.lon:F4}, team {s.team}, capture in {(double.IsInfinity(s.captureUT) ? "never" : KSPUtil.PrintTimeCompact(s.captureUT - s.downUT, false))}");
        }

        static string SanitizeName(string n) => n.Replace(";", "").Replace(":", "").Replace(",", "");

        static void SetGPS(Survivor s)
        {
            var team = BDTeam.Get(s.team);
            var list = BDATargetManager.GPSTargetList(team);
            list.RemoveAll(g => g.name == s.gpsName);
            list.Add(new GPSTargetInfo(new Vector3d(s.lat, s.lon, Math.Max(s.alt, 0)), s.gpsName));
            try { BDATargetManager.Instance?.SaveGPSTargets(); } catch { }
        }

        static void RemoveGPS(Survivor s)
        {
            var list = BDATargetManager.GPSTargetList(BDTeam.Get(s.team));
            if (list.RemoveAll(g => g.name == s.gpsName) > 0) try { BDATargetManager.Instance?.SaveGPSTargets(); } catch { }
        }

        // ---- Territory and capture clock ----

        static (Base b, double km) NearestBase(Survivor s, bool enemy)
        {
            var sc = FactionsScenario.Instance; var body = FlightGlobals.GetHomeBody();
            if (sc == null || body == null) return (null, double.MaxValue);
            var p = body.GetWorldSurfacePosition(s.lat, s.lon, 0);
            Base best = null; double bd = double.MaxValue;
            foreach (var b in sc.bases)
            {
                var f = FactionsScenario.FactionByName(b.faction);
                if (f == null) continue;
                bool isEnemy = f.team != s.team && BDTeam.Get(s.team).IsEnemy(f.BDTeam);
                if (isEnemy != enemy) continue;
                double d = (body.GetWorldSurfacePosition(b.lat, b.lon, 0) - p).magnitude / 1000;
                if (d < bd) { bd = d; best = b; }
            }
            return (best, bd);
        }

        /// <summary>Hours until an enemy search team reaches them: 1 h + 1 h per 100 km from the nearest enemy base; never in friendly territory.</summary>
        static double CaptureHours(Survivor s)
        {
            var enemy = NearestBase(s, true); var friend = NearestBase(s, false);
            if (enemy.b == null || friend.km < enemy.km) return double.PositiveInfinity;
            return 1 + enemy.km / 100;
        }

        static string DescribeTerritory(Survivor s)
        {
            var enemy = NearestBase(s, true); var friend = NearestBase(s, false);
            if (enemy.b == null || friend.km < enemy.km) return friend.b != null ? $"friendly territory, {friend.km:F0} km from {BaseLogistics.Name(friend.b)}" : "no enemy nearby";
            return $"ENEMY territory, {enemy.km:F0} km from {BaseLogistics.Name(enemy.b)}";
        }

        void Update()
        {
            if (Time.time < nextTick || HighLogic.CurrentGame == null) return;
            nextTick = Time.time + 2f;
            try { Tick(); } catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: CSAR: {e}"); }
        }

        readonly Dictionary<string, float> captureContact = new();
        public readonly Dictionary<uint, string> pickups = new(); // unit -> survivor, VTOL handover pending

        void HandOver()
        {
            foreach (var kv in new List<KeyValuePair<uint, string>>(pickups))
            {
                var s = Find(kv.Value);
                var v = FindVessel(kv.Key);
                if (s == null || s.status != SurvivorStatus.Down || v == null || !v.loaded) { pickups.Remove(kv.Key); continue; }
                if ((v.CoMD - v.mainBody.GetWorldSurfacePosition(s.lat, s.lon, s.alt)).magnitude > 3000) continue;
                var vtol = ModuleVTOLAssist.For(v);
                pickups.Remove(kv.Key);
                if (vtol == null) continue;
                C2Tasking.Release(v);
                var evaV = FindVessel(s.evaId);
                if (!(KASOps.CanHoist(v) && evaV != null && evaV.loaded && KASOps.Rescue(v, evaV, s.kerbal)))
                    vtol.GoTo(s.lat, s.lon, 30, land: true);
                try { Ocisly.OpenFirst(v, "CSAR pickup"); } catch { }
                AICommander.Log($"{v.GetName()} on scene: VTOL pickup of {s.kerbal}");
            }
        }

        void Tick()
        {
            double ut = Planetarium.GetUniversalTime();
            if (HighLogic.LoadedSceneIsFlight) HandOver();
            foreach (var s in survivors.ToArray())
            {
                if (s.status != SurvivorStatus.Down) { if (s.status == SurvivorStatus.PickedUp) CheckDelivered(s); continue; }
                var eva = FindVessel(s.evaId);
                if (eva != null)
                {
                    double moved = (eva.mainBody.GetWorldSurfacePosition(s.lat, s.lon, s.alt) - eva.CoMD).magnitude;
                    s.lat = eva.latitude; s.lon = eva.longitude; s.alt = eva.altitude;
                    if (moved > 50 && eva.LandedOrSplashed) SetGPS(s);
                    if (HighLogic.LoadedSceneIsFlight && eva.loaded) { AutoBoard(s, eva); if (s.status != SurvivorStatus.Down) continue; PhysicalCapture(s, eva); if (s.status != SurvivorStatus.Down) continue; }
                }
                else if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && !KerbalStillOnEVA(s)) { ResolveMissing(s); continue; }

                if (!double.IsInfinity(s.captureUT) && ut >= s.captureUT)
                {
                    if (HighLogic.LoadedSceneIsFlight && FriendlyOverhead(s)) { s.captureUT = ut + 1800; AICommander.Log($"Friendly aircraft over {s.kerbal}: the enemy search team is held off (30 min)"); }
                    else Capture(s, "the enemy search team");
                }
            }
        }

        static Vessel FindVessel(uint id) { foreach (var v in FlightGlobals.Vessels) if (v != null && v.persistentId == id) return v; return null; }

        static bool KerbalStillOnEVA(Survivor s)
        {
            foreach (var v in FlightGlobals.Vessels) if (v != null && v.isEVA && v.GetVesselCrew().Exists(c => c.name == s.kerbal)) { s.evaId = v.persistentId; return true; }
            return false;
        }

        void ResolveMissing(Survivor s)
        {
            var pcm = HighLogic.CurrentGame.CrewRoster[s.kerbal];
            if (pcm == null) return;
            if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead) { Finish(s, SurvivorStatus.KIA, ""); return; }
            foreach (var v in FlightGlobals.Vessels)
                if (v != null && !v.isEVA && v.GetVesselCrew().Exists(c => c.name == s.kerbal)) { PickedUp(s, v); return; }
            if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Available) Finish(s, SurvivorStatus.Rescued, "recovered");
        }

        static bool FriendlyOverhead(Survivor s)
        {
            var body = FlightGlobals.GetHomeBody(); var p = body.GetWorldSurfacePosition(s.lat, s.lon, s.alt);
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                var wm = v != null ? v.ActiveController().WM : null;
                if (wm != null && wm.Team != null && wm.Team.Name == s.team && (v.CoMD - p).magnitude < 3000) return true;
            }
            return false;
        }

        void PhysicalCapture(Survivor s, Vessel eva)
        {
            Vessel captor = null;
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.isEVA || !v.LandedOrSplashed) continue;
                var wm = v.ActiveController().WM;
                if (wm == null || wm.Team == null || !BDTeam.Get(s.team).IsEnemy(wm.Team)) continue;
                if ((v.CoMD - eva.CoMD).magnitude < 150) { captor = v; break; }
            }
            if (captor == null) { captureContact.Remove(s.kerbal); return; }
            if (!captureContact.TryGetValue(s.kerbal, out float t0)) { captureContact[s.kerbal] = Time.time; ScreenMessages.PostScreenMessage($"{s.kerbal}: enemy troops closing in!", 3f, ScreenMessageStyle.UPPER_CENTER); return; }
            if (Time.time - t0 >= 15f) Capture(s, captor.GetName());
        }

        /// <summary>A friendly craft landed or hovering low within 60 m with a free seat: the survivor climbs aboard.</summary>
        void AutoBoard(Survivor s, Vessel eva)
        {
            if (!eva.LandedOrSplashed) return;
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.isEVA || v.packed || KASOps.Ops.ContainsKey(v)) continue; // mid-hoist: the winch brings them up
                var wm = v.ActiveController().WM;
                if (wm == null || wm.Team == null || wm.Team.Name != s.team) continue;
                if ((v.CoMD - eva.CoMD).magnitude > 60 || v.srfSpeed > 8 || (!v.LandedOrSplashed && v.radarAltitude > 20)) continue;
                Part seat = null;
                foreach (var p in v.parts) if (p.CrewCapacity > p.protoModuleCrew.Count && p.airlock != null) { seat = p; break; }
                if (seat == null) continue;
                var k = eva.GetComponent<KerbalEVA>() ?? eva.rootPart?.FindModuleImplementing<KerbalEVA>();
                if (k == null) return;
                try { k.BoardPart(seat); Debug.Log($"[BDArmoryAvionics]: CSAR: {s.kerbal} boarded {v.GetName()} ({seat.partInfo.title})"); }
                catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: CSAR board failed: {e.Message}"); }
                return;
            }
        }

        void OnBoard(GameEvents.FromToAction<Part, Part> a)
        {
            if (a.to == null || a.to.vessel == null) return;
            foreach (var s in survivors)
                if (s.status == SurvivorStatus.Down && a.to.vessel.GetVesselCrew().Exists(c => c.name == s.kerbal)) { PickedUp(s, a.to.vessel); return; }
        }

        void PickedUp(Survivor s, Vessel v)
        {
            var wm = v.ActiveController().WM;
            if (wm != null && wm.Team != null && wm.Team.Name != s.team && BDTeam.Get(s.team).IsEnemy(wm.Team)) { Capture(s, v.GetName()); return; }
            s.status = SurvivorStatus.PickedUp; s.rescuer = v.GetName();
            RemoveGPS(s);
            ScreenMessages.PostScreenMessage($"{s.kerbal} PICKED UP by {v.GetName()} - get them home!", 5f, ScreenMessageStyle.UPPER_CENTER);
            CombatLog.Note($"CSAR: {v.GetName()} picked up {s.kerbal}", true, 1);
            AICommander.Log($"{s.kerbal} picked up by {v.GetName()}");
        }

        void CheckDelivered(Survivor s)
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || !v.GetVesselCrew().Exists(c => c.name == s.kerbal)) continue;
                if (!v.LandedOrSplashed || v.srfSpeed > 1) return;
                var sc = FactionsScenario.Instance; if (sc == null) return;
                foreach (var b in sc.bases)
                {
                    var f = FactionsScenario.FactionByName(b.faction);
                    if (f == null || f.team != s.team) continue;
                    if ((v.CoMD - v.mainBody.GetWorldSurfacePosition(b.lat, b.lon, b.alt)).magnitude < BaseLogistics.BaseRadius) { Finish(s, SurvivorStatus.Rescued, $"delivered to {BaseLogistics.Name(b)}", v); return; }
                }
                return;
            }
        }

        void OnRecovered(ProtoVessel pv, bool quick)
        {
            if (pv == null) return;
            foreach (var s in survivors.ToArray())
                if (s.Active && pv.GetVesselCrew().Exists(c => c.name == s.kerbal))
                    Finish(s, SurvivorStatus.Rescued, s.status == SurvivorStatus.PickedUp ? "recovered aboard " + pv.vesselName : "recovered", null, pv);
        }

        void OnKerbalStatus(ProtoCrewMember pcm, ProtoCrewMember.RosterStatus from, ProtoCrewMember.RosterStatus to)
        {
            if (pcm == null || to != ProtoCrewMember.RosterStatus.Dead) return;
            var s = Find(pcm.name);
            if (s != null) Finish(s, SurvivorStatus.KIA, "");
        }

        void Finish(Survivor s, SurvivorStatus st, string how, Vessel rescuerVessel = null, ProtoVessel rescuerProto = null)
        {
            s.status = st;
            RemoveGPS(s);
            var o = CombatOperators.Get(s.kerbal);
            if (st == SurvivorStatus.Rescued)
            {
                o?.Count("rescued");
                CombatOperators.Review(o);
                // Credit the crew who brought them home.
                var crew = new List<string>();
                if (rescuerVessel != null) foreach (var c in rescuerVessel.GetVesselCrew()) crew.Add(c.name);
                else if (rescuerProto != null) foreach (var c in rescuerProto.GetVesselCrew()) crew.Add(c.name);
                foreach (var n in crew)
                {
                    if (n == s.kerbal) continue;
                    var r = CombatOperators.Get(n);
                    if (r == null) continue;
                    r.Count("rescues");
                    CombatOperators.Review(r);
                }
                ScreenMessages.PostScreenMessage($"{s.kerbal} RESCUED ({how})", 6f, ScreenMessageStyle.UPPER_CENTER);
                CombatLog.Note($"CSAR: {s.kerbal} rescued ({how})", true, 1);
                AICommander.Log($"{s.kerbal} rescued ({how}) after {KSPUtil.PrintTimeCompact(Planetarium.GetUniversalTime() - s.downUT, false)}");
            }
            else if (st == SurvivorStatus.KIA) AICommander.Log($"{s.kerbal} did not survive");
            Debug.Log($"[BDArmoryAvionics]: CSAR: {s.kerbal} -> {st} {how}");
        }

        void Capture(Survivor s, string by)
        {
            var enemy = NearestBase(s, true);
            s.status = SurvivorStatus.Captured; s.heldAt = enemy.b?.site ?? "";
            RemoveGPS(s);
            var pcm = HighLogic.CurrentGame.CrewRoster[s.kerbal];
            var eva = FindVessel(s.evaId);
            if (eva != null && pcm != null)
            {
                foreach (var p in eva.parts) p.protoModuleCrew.Remove(pcm);
                eva.Die();
            }
            if (pcm != null)
            {
                pcm.rosterStatus = ProtoCrewMember.RosterStatus.Missing;
                pcm.SetInactive(1e12, false); // held until freed
            }
            CombatOperators.Get(s.kerbal)?.Count("pow");
            ScreenMessages.PostScreenMessage($"{s.kerbal} CAPTURED by {by} - held at {(string.IsNullOrEmpty(s.heldAt) ? "unknown" : s.heldAt)}", 6f, ScreenMessageStyle.UPPER_CENTER);
            CombatLog.Note($"CSAR: {s.kerbal} captured by {by}", true, 3);
            AICommander.Log($"{s.kerbal} captured by {by}, held at {(string.IsNullOrEmpty(s.heldAt) ? "an unknown site" : BaseLogistics.Name(FactionsScenario.BaseAt(s.heldAt) ?? new Base { site = s.heldAt }))}. Take that base to free them");
        }

        /// <summary>Taking a base frees the prisoners held there.</summary>
        public static void OnBaseCaptured(Base b, Faction newOwner)
        {
            if (Instance == null || b == null || newOwner == null) return;
            foreach (var s in Instance.survivors)
            {
                if (s.status != SurvivorStatus.Captured || s.heldAt != b.site || s.team != newOwner.team) continue;
                s.status = SurvivorStatus.Freed;
                var pcm = HighLogic.CurrentGame.CrewRoster[s.kerbal];
                if (pcm != null) { pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available; pcm.inactive = false; pcm.inactiveTimeEnd = 0; }
                CombatOperators.Get(s.kerbal)?.Count("freed");
                AICommander.Log($"{s.kerbal} freed at {BaseLogistics.Name(b)}!");
            }
        }

        // ---- C2 ----

        public static IEnumerable<Survivor> ActiveFor(BDTeam team)
        {
            if (Instance == null || team == null) yield break;
            foreach (var s in Instance.survivors) if (s.Active && s.team == team.Name) yield return s;
        }

        public static string Summary(Survivor s)
        {
            double ut = Planetarium.GetUniversalTime();
            string cap = s.status == SurvivorStatus.PickedUp ? $"aboard {s.rescuer}" : double.IsInfinity(s.captureUT) ? "safe area" : $"capture in {KSPUtil.PrintTimeCompact(Math.Max(0, s.captureUT - ut), false)}";
            return $"{s.kerbal.Split(' ')[0]} ({s.aircraft}) - down {KSPUtil.PrintTimeCompact(ut - s.downUT, false)} - {cap}";
        }

        /// <summary>Send a unit to the survivor: low and slow to the beacon (land / hover within 60 m and they climb in).</summary>
        public static void Send(Vessel unit, Survivor s)
        {
            if (unit == null || s == null) return;
            var body = unit.mainBody;
            double ground = Math.Max(0, body.TerrainAltitude(s.lat, s.lon));
            var vtol = ModuleVTOLAssist.For(unit);
            if (vtol != null && (unit.srfSpeed < 40 || vtol.hoverAG != "None") && (unit.CoMD - body.GetWorldSurfacePosition(s.lat, s.lon, ground)).magnitude < 3000)
            {
                var evaV = FindVessel(s.evaId);
                if (KASOps.CanHoist(unit) && evaV != null && evaV.loaded && KASOps.Rescue(unit, evaV, s.kerbal)) { }
                else
                {
                    vtol.GoTo(s.lat, s.lon, 30, land: true);
                    ScreenMessages.PostScreenMessage($"{unit.GetName()}: VTOL pickup of {s.kerbal} - hovering in to land beside them", 5f, ScreenMessageStyle.UPPER_CENTER);
                }
            }
            else
            {
                C2Tasking.MoveTo(unit, new Vector3d(s.lat, s.lon, ground + 300));
                if (vtol != null && Instance != null) Instance.pickups[unit.persistentId] = s.kerbal; // hand over to the VTOL assist 3 km out
                ScreenMessages.PostScreenMessage($"{unit.GetName()} tasked: CSAR {s.kerbal}.{(vtol != null ? " It converts to hover and lands beside them 3 km out." : " Land or hover within 60 m and they climb in.")}", 5f, ScreenMessageStyle.UPPER_CENTER);
            }
            Debug.Log($"[BDArmoryAvionics]: CSAR: {unit.GetName()} sent to {s.kerbal}");
        }
    }
}
