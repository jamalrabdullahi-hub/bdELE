using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Settings;
using BDArmory.UI;
using BDArmory.VesselSpawning;

namespace BDArmory.Avionics
{
    public enum FlightMission { Strike, SEAD, Intercept, CAP }
    public enum FlightState { Outbound, Materialized, Returning, Done }

    /// <summary>
    /// A sortie the AI commander has launched. While nobody is near it, it is "virtual": it moves along its route on game
    /// time (also in the Space Center / during warp) and its strike is resolved by numbers. When it comes within range of
    /// the player's craft, it materialises as real craft (spawned airborne from the base's craft file) flown by BDArmory's AI.
    /// </summary>
    public class AirFlight
    {
        public int id;
        public string faction = "", fromBase = "", craft = "", path = "";
        public FlightMission mission;
        public FlightState state = FlightState.Outbound;
        public int count = 1, alive = 1;
        public double lat, lon, alt, tLat, tLon;
        public float speed = 220f;
        public string targetBase = "";
        public uint targetVessel;
        public double launchedUT, materializedUT;
        public readonly List<uint> vessels = new();
        public string Callsign => $"{(mission == FlightMission.Intercept || mission == FlightMission.CAP ? "Hawk" : mission == FlightMission.SEAD ? "Weasel" : "Hammer")} {id}";

        public void Save(ConfigNode n)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            n.AddValue("id", id); n.AddValue("faction", faction); n.AddValue("fromBase", fromBase); n.AddValue("craft", craft); n.AddValue("path", path);
            n.AddValue("mission", mission); n.AddValue("state", state == FlightState.Materialized ? FlightState.Returning : state); // materialised craft don't survive a reload as virtual
            n.AddValue("count", count); n.AddValue("alive", alive);
            n.AddValue("pos", string.Join(",", Array.ConvertAll(new[] { lat, lon, alt, tLat, tLon }, d => d.ToString("R", ci))));
            n.AddValue("speed", speed.ToString("R", ci)); n.AddValue("targetBase", targetBase); n.AddValue("launchedUT", launchedUT.ToString("R", ci));
        }

        public static AirFlight Load(ConfigNode n)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture; var st = System.Globalization.NumberStyles.Float;
            var f = new AirFlight { faction = n.GetValue("faction") ?? "", fromBase = n.GetValue("fromBase") ?? "", craft = n.GetValue("craft") ?? "", path = n.GetValue("path") ?? "", targetBase = n.GetValue("targetBase") ?? "" };
            int.TryParse(n.GetValue("id"), out f.id); int.TryParse(n.GetValue("count"), out f.count); int.TryParse(n.GetValue("alive"), out f.alive);
            Enum.TryParse(n.GetValue("mission"), out f.mission); Enum.TryParse(n.GetValue("state"), out f.state);
            var p = (n.GetValue("pos") ?? "").Split(',');
            if (p.Length == 5) { double.TryParse(p[0], st, ci, out f.lat); double.TryParse(p[1], st, ci, out f.lon); double.TryParse(p[2], st, ci, out f.alt); double.TryParse(p[3], st, ci, out f.tLat); double.TryParse(p[4], st, ci, out f.tLon); }
            float.TryParse(n.GetValue("speed"), st, ci, out f.speed); double.TryParse(n.GetValue("launchedUT"), st, ci, out f.launchedUT);
            return f;
        }
    }

    /// <summary>Per AI faction commander settings.</summary>
    public class CommanderSettings
    {
        public string faction = "";
        public string aggression = "Normal"; // Passive, Defensive, Normal, Aggressive
        public double nextPlanUT = -1;
        public static readonly string[] Levels = ["Passive", "Defensive", "Normal", "Aggressive"];
        public double PlanHours => aggression == "Aggressive" ? 1 : aggression == "Normal" ? 3 : 6;
        public bool Strikes => aggression == "Normal" || aggression == "Aggressive";
        public bool Defends => aggression != "Passive";
    }

    /// <summary>
    /// The AI commander for every AI-controlled faction: plans strikes / SEAD against enemy bases, scrambles interceptors
    /// against enemy aircraft near its bases, moves sorties on game time, materialises them near the player and resolves
    /// them by numbers elsewhere. Uses the bases' inventories, runways and status (phase 2).
    /// </summary>
    public static class AICommander
    {
        // Sorties become real craft within 150 km of you and stay real until 175 km (needs Physics Range Extender >= 175 km).
        // Theatre bubble: AI flights become real craft inside this slant range of a player/enemy craft that's in the atmosphere.
        public static float MaterializeKm => AvionicsSettings.TheatreSpawnKm;
        public static float DematerializeKm => AvionicsSettings.TheatreSpawnKm + 25f;
        public const float ScrambleKm = 80f;

        /// <summary>Only craft inside the atmosphere bring the theatre to life; satellites and orbiting craft just look (they don't spawn units).</summary>
        public static bool InTheatre(Vessel v)
        {
            if (v == null || v.mainBody == null) return false;
            double ceiling = v.mainBody.atmosphere ? v.mainBody.atmosphereDepth : 20000;
            return v.altitude < ceiling && v.situation != Vessel.Situations.ORBITING && v.situation != Vessel.Situations.ESCAPING;
        }
        public static bool RevealFlights;

        static FactionsScenario SC => FactionsScenario.Instance;
        static CelestialBody Body => FlightGlobals.GetHomeBody();

        public static CommanderSettings SettingsFor(Faction f)
        {
            var s = SC.commanders.Find(c => c.faction == f.name);
            if (s == null) { s = new CommanderSettings { faction = f.name }; SC.commanders.Add(s); }
            return s;
        }

        public static void Log(string text)
        {
            if (SC == null) return;
            string line = $"[{KSPUtil.PrintDateCompact(Planetarium.GetUniversalTime(), true)}] {text}";
            SC.theatreLog.Insert(0, line);
            while (SC.theatreLog.Count > 60) SC.theatreLog.RemoveAt(SC.theatreLog.Count - 1);
            Debug.Log($"[BDArmoryAvionics]: theatre: {text}");
            if (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER) ScreenMessages.PostScreenMessage(text, 5f, ScreenMessageStyle.UPPER_LEFT);
        }

        // ---- Tick (every scene where the scenario runs) ----

        static double lastUT = -1;

        public static void Tick()
        {
            if (SC == null || Body == null) return;
            double ut = Planetarium.GetUniversalTime();
            double dt = lastUT < 0 ? 0 : Math.Max(0, ut - lastUT);
            lastUT = ut;

            foreach (var f in SC.factions)
            {
                if (f.IsPlayer) continue;
                var cs = SettingsFor(f);
                if (cs.aggression == "Passive") continue;
                if (cs.nextPlanUT < 0) cs.nextPlanUT = ut + 600;
                if (ut >= cs.nextPlanUT) { cs.nextPlanUT = ut + cs.PlanHours * 3600; Plan(f, cs); }
                if (cs.Defends && HighLogic.LoadedSceneIsFlight) Defend(f);
            }

            foreach (var fl in SC.flights.ToArray())
            {
                switch (fl.state)
                {
                    case FlightState.Outbound:
                    case FlightState.Returning:
                        Move(fl, dt);
                        if (HighLogic.LoadedSceneIsFlight && fl.state == FlightState.Outbound && NearPlayer(fl, MaterializeKm)) Materializer.Request(fl);
                        break;
                    case FlightState.Materialized:
                        if (HighLogic.LoadedSceneIsFlight) Materializer.Watch(fl);
                        else fl.state = FlightState.Returning; // left the flight scene
                        break;
                }
            }
            SC.flights.RemoveAll(fl => fl.state == FlightState.Done);
        }

        // ---- Planning ----

        static void Plan(Faction f, CommanderSettings cs)
        {
            if (!cs.Strikes) return;
            if (BaseLogistics.AutoStock(f) > 0) Log($"{f.name}: bases stocked with aircraft from the craft files");
            if (SC.flights.Exists(x => x.faction == f.name && (x.mission == FlightMission.Strike || x.mission == FlightMission.SEAD) && x.state != FlightState.Done)) return; // one strike package at a time
            Base from = null; InvItem item = null;
            foreach (var b in SC.bases)
            {
                if (b.faction != f.name || b.status == BaseStatus.Destroyed || !b.RunwayOpen) continue;
                var i = b.inventory.Find(x => (x.Can("Strike") || x.Can("SEAD")) && x.available > 0 && !string.IsNullOrEmpty(x.path));
                if (i != null) { from = b; item = i; break; }
            }
            if (from == null) { Log($"{f.name}: no strike aircraft ready (station Strike/SEAD/Multirole craft at an operational base with an open runway)"); return; }
            var target = NearestEnemyBase(f, from);
            if (target == null) return;
            int n = Mathf.Min(2, item.available);
            var fl = Launch(f, from, item, n, item.role == "SEAD" || (item.role == "Multirole" && UnityEngine.Random.value < 0.3f) ? FlightMission.SEAD : FlightMission.Strike, target.lat, target.lon);
            fl.targetBase = target.site;
            Log($"{f.name}: {fl.Callsign} ({n}x {item.craft}) launched from {BaseLogistics.Name(from)} to strike {BaseLogistics.Name(target)}");
        }

        /// <summary>Scramble interceptors when enemy aircraft fly within 80 km of a base (flight scene only).</summary>
        static void Defend(Faction f)
        {
            foreach (var b in SC.bases)
            {
                if (b.faction != f.name || b.status == BaseStatus.Destroyed || !b.RunwayOpen) continue;
                if (SC.flights.Exists(x => x.fromBase == b.site && x.mission == FlightMission.Intercept && x.state != FlightState.Done)) continue;
                var bp = Body.GetWorldSurfacePosition(b.lat, b.lon, b.alt);
                Vessel threat = null; double bd = ScrambleKm * 1000;
                foreach (var v in FlightGlobals.VesselsLoaded)
                {
                    if (v == null || v.LandedOrSplashed) continue;
                    var wm = v.ActiveController().WM;
                    if (wm == null || wm.Team == null || !f.BDTeam.IsEnemy(wm.Team)) continue;
                    double d = (v.CoMD - bp).magnitude;
                    if (d < bd) { bd = d; threat = v; }
                }
                if (threat == null) continue;
                if (b.inventory.Count == 0) BaseLogistics.AutoStock(f);
                var item = b.inventory.Find(x => x.role == "Fighter" && x.available > 0 && !string.IsNullOrEmpty(x.path))
                        ?? b.inventory.Find(x => x.Can("Fighter") && x.available > 0 && !string.IsNullOrEmpty(x.path));
                if (item == null) continue;
                int n = Mathf.Min(2, item.available);
                var fl = Launch(f, b, item, n, FlightMission.Intercept, threat.latitude, threat.longitude);
                fl.targetVessel = threat.persistentId;
                Log($"{f.name}: {fl.Callsign} ({n}x {item.craft}) scrambled from {BaseLogistics.Name(b)} against {threat.GetName()} ({bd / 1000:F0} km)");
            }
        }

        static AirFlight Launch(Faction f, Base from, InvItem item, int n, FlightMission m, double tLat, double tLon)
        {
            item.available -= n;
            var fl = new AirFlight
            {
                id = ++SC.nextFlightId, faction = f.name, fromBase = from.site, craft = item.craft, path = item.path, mission = m, count = n, alive = n,
                lat = from.lat, lon = from.lon, alt = m == FlightMission.Intercept ? 5000 : 3000, tLat = tLat, tLon = tLon,
                speed = m == FlightMission.Intercept ? 260 : 210, launchedUT = Planetarium.GetUniversalTime(),
            };
            SC.flights.Add(fl);
            return fl;
        }

        static Base NearestEnemyBase(Faction f, Base from)
        {
            Base best = null; double bd = double.MaxValue;
            var p = Body.GetWorldSurfacePosition(from.lat, from.lon, 0);
            foreach (var b in SC.bases)
            {
                var o = FactionsScenario.FactionByName(b.faction);
                if (o == null || o == f || !f.BDTeam.IsEnemy(o.BDTeam) || b.status == BaseStatus.Destroyed) continue;
                double d = (Body.GetWorldSurfacePosition(b.lat, b.lon, 0) - p).magnitude;
                if (d < bd) { bd = d; best = b; }
            }
            return best;
        }

        // ---- Movement and abstract resolution ----

        public static Vector3d Position(AirFlight fl) => Body.GetWorldSurfacePosition(fl.lat, fl.lon, fl.alt);

        static void Move(AirFlight fl, double dt)
        {
            if (dt <= 0) return;
            // Interceptors chase their target if it's loaded.
            if (fl.state == FlightState.Outbound && fl.targetVessel != 0)
                foreach (var v in FlightGlobals.VesselsLoaded) if (v != null && v.persistentId == fl.targetVessel) { fl.tLat = v.latitude; fl.tLon = v.longitude; }
            var p = Position(fl);
            var t = Body.GetWorldSurfacePosition(fl.tLat, fl.tLon, fl.alt);
            var d = t - p; double dist = d.magnitude, step = fl.speed * dt;
            Vector3d np = dist <= step ? t : p + d / dist * step;
            fl.lat = Body.GetLatitude(np); fl.lon = Body.GetLongitude(np);
            if (dist > step) return;

            if (fl.state == FlightState.Outbound) Arrive(fl);
            else Recover(fl);
        }

        static void Arrive(AirFlight fl)
        {
            var target = FactionsScenario.BaseAt(fl.targetBase);
            if ((fl.mission == FlightMission.Strike || fl.mission == FlightMission.SEAD) && target != null)
            {
                // Base defences: stationed fighters and the base's state decide how many get through.
                int defenders = 0; foreach (var i in target.inventory) if (i.role == "Fighter") defenders += i.available;
                int sams = BaseDefense.Standing(target);
                float pLoss = Mathf.Clamp01(0.12f + 0.08f * Mathf.Min(defenders, 4) + 0.07f * Mathf.Min(sams, 4) + (target.status == BaseStatus.Operational ? 0.08f : 0f));
                int lost = 0;
                for (int i = 0; i < fl.alive; i++) if (UnityEngine.Random.value < pLoss) lost++;
                int hit = fl.alive - lost;
                float dmg = 0;
                for (int i = 0; i < hit; i++) dmg += UnityEngine.Random.Range(8f, 20f);
                target.health = Mathf.Max(0, target.health - dmg);
                bool cratered = hit > 0 && target.hasRunway && UnityEngine.Random.value < 0.5f;
                if (cratered) target.runwayClosedUntil = Math.Max(target.runwayClosedUntil, Planetarium.GetUniversalTime()) + UnityEngine.Random.Range(2f, 6f) * 3600;
                BaseLogistics.UpdateStatus(target);
                Lose(fl, lost);
                if (lost > 0 && sams > 0 && UnityEngine.Random.value < 0.25f) { var dead = target.defenses.Find(d => d.alive); if (dead != null) dead.alive = false; } // strikers sometimes take a site with them
                Log($"{fl.Callsign} struck {BaseLogistics.Name(target)} ({sams} SAM sites): {hit} got through, {lost} shot down, base -{dmg:F0}% ({target.health:F0}%){(cratered ? ", runway cratered" : "")}");
            }
            else if (fl.mission == FlightMission.Intercept) Log($"{fl.Callsign} found nothing and is returning");
            ReturnHome(fl);
        }

        static void ReturnHome(AirFlight fl)
        {
            var home = FactionsScenario.BaseAt(fl.fromBase);
            if (home == null || fl.alive <= 0) { fl.state = FlightState.Done; return; }
            fl.state = FlightState.Returning;
            fl.tLat = home.lat; fl.tLon = home.lon; fl.targetVessel = 0;
        }

        static void Recover(AirFlight fl)
        {
            var home = FactionsScenario.BaseAt(fl.fromBase);
            var item = home?.inventory.Find(i => i.craft == fl.craft);
            if (item != null) item.available += fl.alive;
            fl.state = FlightState.Done;
            Debug.Log($"[BDArmoryAvionics]: {fl.Callsign} back at {fl.fromBase} ({fl.alive}/{fl.count})");
        }

        public static void Lose(AirFlight fl, int n)
        {
            if (n <= 0) return;
            fl.alive = Mathf.Max(0, fl.alive - n);
            var item = FactionsScenario.BaseAt(fl.fromBase)?.inventory.Find(i => i.craft == fl.craft);
            if (item != null) item.lost += n;
        }

        public static bool NearPlayer(AirFlight fl, float km)
        {
            var p = Position(fl);
            var f = FactionsScenario.FactionByName(fl.faction);
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed || !InTheatre(v)) continue;
                if (v != FlightGlobals.ActiveVessel)
                {
                    var wm = v.ActiveController().WM;
                    if (wm == null || wm.Team == null || f == null || !f.BDTeam.IsEnemy(wm.Team)) continue;
                }
                if ((v.CoMD - p).magnitude < km * 1000) return true;
            }
            return false;
        }

        /// <summary>Whether the player's side can see a virtual flight (radar range of a linked radar, or 30 km of any unit).</summary>
        public static bool Detected(AirFlight fl)
        {
            if (RevealFlights) return true;
            var net = DataLinkNetwork.PlayerNetwork;
            if (net == null) return false;
            var p = Position(fl);
            foreach (var n in net.nodes)
            {
                if (n.vessel == null) continue;
                double d = (n.vessel.CoMD - p).magnitude;
                if (d < 30000 || (n.radarOn && d < n.radarRange)) return true;
            }
            return false;
        }
    }

    /// <summary>Flight scene: turns virtual flights into real craft near the player and back again.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class Materializer : MonoBehaviour
    {
        static Materializer instance;
        static readonly Queue<AirFlight> queue = new();
        public static readonly HashSet<uint> despawning = new();
        bool busy;
        static readonly FieldInfo latestName = AccessTools.Field(typeof(VesselSpawnerBase), "latestSpawnedVesselName");
        static readonly FieldInfo spawned = AccessTools.Field(typeof(VesselSpawnerBase), "spawnedVessels");

        void Awake() { instance = this; queue.Clear(); despawning.Clear(); GameEvents.onVesselGoOffRails.Add(OnOffRails); }

        // While a craft is being spawned: catch it the frame KSP starts its physics (goes off rails), not seconds later
        // when BDArmory's spawn routine finishes. Until then it would be falling with nobody at the controls.
        HashSet<uint> spawnBefore; Vector3 spawnDir; float spawnSpeed, spawnStart; bool spawnCaught;
        void OnOffRails(Vessel nv)
        {
            if (spawnBefore == null || spawnCaught || nv == null || spawnBefore.Contains(nv.persistentId) || nv.vesselType == VesselType.Debris || nv.isEVA) return;
            spawnCaught = true;
            Debug.Log($"[BDArmoryAvionics]: {nv.GetName()} off rails {Time.time - spawnStart:F2} s after spawn request - spawn hold attached");
            SpawnHold.Attach(nv, spawnDir, spawnSpeed);
        }
        public static bool Busy => instance != null && instance.busy;

        /// <summary>The craft BDArmory's single-vessel spawner just made (null if it failed).</summary>
        public static Vessel LatestSpawned()
        {
            var sp = SingleVesselSpawning.Instance;
            if (sp == null || !sp.vesselSpawnSuccess) return null;
            if (latestName?.GetValue(sp) is string name && spawned?.GetValue(sp) is Dictionary<string, Vessel> dict && dict.TryGetValue(name, out var v)) return v;
            return null;
        }
        void OnDestroy() { if (instance == this) instance = null; GameEvents.onVesselGoOffRails.Remove(OnOffRails); }

        public static void Request(AirFlight fl)
        {
            if (instance == null || queue.Contains(fl) || fl.state != FlightState.Outbound) return;
            fl.state = FlightState.Materialized;
            fl.materializedUT = Planetarium.GetUniversalTime();
            queue.Enqueue(fl);
        }

        void Update()
        {
            if (busy || BaseDefense.Spawning || queue.Count == 0 || !FlightGlobals.ready || SingleVesselSpawning.Instance == null || SingleVesselSpawning.Instance.vesselsSpawning) return;
            StartCoroutine(Spawn(queue.Dequeue()));
        }

        IEnumerator Spawn(AirFlight fl)
        {
            busy = true;
            var f = FactionsScenario.FactionByName(fl.faction);
            var body = FlightGlobals.GetHomeBody();
            var p = AICommander.Position(fl);
            var t = body.GetWorldSurfacePosition(fl.tLat, fl.tLon, fl.alt);
            var up = (p - body.position).normalized;
            var north = Vector3.ProjectOnPlane(body.transform.up, up).normalized;
            var dir = Vector3.ProjectOnPlane((Vector3)(t - p), up).normalized;
            float heading = Vector3.SignedAngle(north, dir, up); if (heading < 0) heading += 360;
            var right = Vector3.Cross(up, dir).normalized;
            AICommander.Log($"{fl.Callsign} ({fl.alive}x {fl.craft}, {f?.name}) inbound - contact!");
            for (int i = 0; i < fl.alive; i++)
            {
                if (!System.IO.File.Exists(fl.path)) { Debug.LogWarning($"[BDArmoryAvionics]: {fl.Callsign}: craft file missing: {fl.path}"); break; }
                var sp = p + (Vector3d)(right * (i * 300f) - dir * (i * 150f));
                double lat = body.GetLatitude(sp), lon = body.GetLongitude(sp);
                double agl = Math.Max(800, fl.alt - Math.Max(0, body.TerrainAltitude(lat, lon)));
                // Catch the new craft the moment it appears (BDArmory's spawn sequence takes a few seconds) and hold it level.
                var before = new HashSet<uint>(); foreach (var ev in FlightGlobals.Vessels) if (ev != null) before.Add(ev.persistentId);
                spawnBefore = before; spawnDir = dir; spawnSpeed = fl.speed; spawnStart = Time.time; spawnCaught = false;
                var spawn = StartCoroutine(SingleVesselSpawning.Instance.SpawnVessel(fl.path, lat, lon, agl, heading, 0f));
                bool held = false; float tSpawn = Time.time;
                while (SingleVesselSpawning.Instance.vesselsSpawning || Time.time - tSpawn < 0.2f)
                {
                    if (spawnCaught) held = true;
                    if (!held)
                        foreach (var nv in FlightGlobals.VesselsLoaded)
                            if (nv != null && !before.Contains(nv.persistentId) && nv.vesselType != VesselType.Debris && nv.parts.Count > 1)
                            { SpawnHold.Attach(nv, dir, fl.speed); held = true; break; }
                    if (Time.time - tSpawn > 30f) break;
                    yield return null;
                }
                yield return spawn;
                spawnBefore = null;
                Vessel v = null;
                if (SingleVesselSpawning.Instance.vesselSpawnSuccess && latestName?.GetValue(SingleVesselSpawning.Instance) is string name && spawned?.GetValue(SingleVesselSpawning.Instance) is Dictionary<string, Vessel> dict) dict.TryGetValue(name, out v);
                if (v == null) { Debug.LogWarning($"[BDArmoryAvionics]: {fl.Callsign}: spawn failed ({SingleVesselSpawning.Instance.spawnFailureReason})"); continue; }
                yield return null;
                Setup(v, fl, f, dir);
                fl.vessels.Add(v.persistentId);
                yield return new WaitForSeconds(1.5f); // one craft at a time: spreads the load hitches out
            }
            if (fl.vessels.Count == 0) { fl.state = FlightState.Returning; AICommander.Log($"{fl.Callsign} could not be spawned - stays virtual"); }
            yield return new WaitForSeconds(4f); // breathing room before the next flight
            busy = false;
        }

        static void Setup(Vessel v, AirFlight fl, Faction f, Vector3 dir)
        {
            v.vesselName = $"{fl.Callsign}-{fl.vessels.Count + 1} {fl.craft}";
            var wm = v.ActiveController().WM;
            if (wm != null && f != null) { wm.SetTeam(f.BDTeam); if (!wm.guardMode) wm.ToggleGuardMode(); }
            var ai = v.ActiveController().PilotAI;
            if (ai != null && !ai.pilotEnabled) ai.ActivatePilot();
            if (v.GetComponent<SpawnHold>() == null) SpawnHold.Attach(v, dir, fl.speed); // keeps it level until the AI is really flying
            var target = FactionsScenario.BaseAt(fl.targetBase);
            if (fl.mission == FlightMission.Strike || fl.mission == FlightMission.SEAD) { if (target != null) C2Tasking.Strike(v, new Vector3d(target.lat, target.lon, target.alt)); }
            else C2Tasking.MoveTo(v, new Vector3d(fl.tLat, fl.tLon, fl.alt));
            var home = FactionsScenario.BaseAt(fl.fromBase);
            if (home != null) BaseLogistics.Track(v, home, fl.craft);
            Debug.Log($"[BDArmoryAvionics]: {v.GetName()} materialised: team {f?.team}, guard {(wm != null && wm.guardMode)}, AI {(ai != null && ai.pilotEnabled)}");
        }

        /// <summary>Count survivors; send the flight back to virtual once it is far from the player again.</summary>
        public static void Watch(AirFlight fl)
        {
            if (instance == null) return;
            var alive = new List<Vessel>();
            foreach (var id in fl.vessels)
                foreach (var v in FlightGlobals.Vessels)
                    if (v != null && v.persistentId == id && v.state != Vessel.State.DEAD && v.ActiveController().WM != null) alive.Add(v);
            int lost = fl.vessels.Count - alive.Count;
            if (lost > 0) { AICommander.Lose(fl, lost); fl.vessels.RemoveAll(id => !alive.Exists(v => v.persistentId == id)); AICommander.Log($"{fl.Callsign}: {lost} lost ({fl.alive} left)"); }
            if (fl.alive <= 0) { fl.state = FlightState.Done; return; }
            if (alive.Count == 0) return; // still spawning
            // Back to virtual when every craft is far from the player, or after 30 minutes of action.
            bool far = true;
            foreach (var v in alive) if (AICommander.NearPlayer(new AirFlight { lat = v.latitude, lon = v.longitude, alt = v.altitude, faction = fl.faction }, AICommander.DematerializeKm)) far = false;
            if (!far && Planetarium.GetUniversalTime() - fl.materializedUT < 1800) return;
            var lead = alive[0];
            fl.lat = lead.latitude; fl.lon = lead.longitude;
            foreach (var v in alive) { despawning.Add(v.persistentId); BaseLogistics.Untrack(v); v.Die(); }
            fl.vessels.Clear();
            fl.state = FlightState.Returning;
            var home = FactionsScenario.BaseAt(fl.fromBase);
            if (home != null) { fl.tLat = home.lat; fl.tLon = home.lon; }
            Debug.Log($"[BDArmoryAvionics]: {fl.Callsign} left the area ({fl.alive} aircraft), returning to {fl.fromBase}");
        }
    }
}
