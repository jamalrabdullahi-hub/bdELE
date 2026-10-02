using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>An aircraft type held at a base: craft (ship name), authorized strength, available airframes, losses.</summary>
    public class InvItem
    {
        public string craft = "", path = "";
        public int authorized = 2, available = 2, lost;
        public string role = "Fighter"; // Fighter, Strike, SEAD - what the AI commander uses it for
        public static readonly string[] Roles = ["Fighter", "Strike", "SEAD", "Multirole"];
        public bool Can(string mission) => role == "Multirole" || role == mission;
    }

    /// <summary>
    /// Phase 2: base logistics and base warfare.
    /// - Aircraft inventory per base: launching a listed craft takes an airframe; recovering it at a base returns one;
    ///   losses are counted; replacements arrive every 2 days while the base is operational.
    /// - Fuel and ordnance stock: craft landed and stopped at a friendly base are refuelled and re-armed from it; stock regrows daily.
    /// - Damage: BDArmory explosions within 1.5 km hurt the base; hits on the runway close it; health repairs daily.
    ///   Below 60 % the base is Damaged (half-rate replacements), at 0 Destroyed (no runway, no replacements, no service).
    /// - Capture: armed enemy ground units holding the base for 60 s, with no armed defenders within 1.5 km, take it over.
    /// </summary>
    public static class BaseLogistics
    {
        public const float BaseRadius = 1500f, CaptureSeconds = 60f;
        const double Day = 21600;
        static readonly Dictionary<uint, (Base b, string craft)> launched = new();
        static readonly Dictionary<uint, float> stoppedSince = new();
        static readonly HashSet<uint> serviced = new();
        static float lastFlightTick;

        static double DaySeconds => KSPUtil.dateTimeFormatter != null ? KSPUtil.dateTimeFormatter.Day : Day;

        // ---- Daily: repair, resupply, replacements ----

        public static void Daily(FactionsScenario sc)
        {
            double ut = Planetarium.GetUniversalTime();
            foreach (var b in sc.bases)
            {
                if (b.lastDailyUT < 0) { b.lastDailyUT = ut; continue; }
                double days = (ut - b.lastDailyUT) / DaySeconds;
                if (days < 0.25) continue; // update every quarter day
                b.lastDailyUT = ut;
                float d = (float)days;
                b.health = Mathf.Min(100f, b.health + 25f * d);                       // repair crews
                UpdateStatus(b);
                float rate = b.status == BaseStatus.Operational ? 1f : b.status == BaseStatus.Damaged ? 0.5f : 0f;
                b.fuel = Mathf.Min(b.fuelCap, b.fuel + b.fuelCap * 0.5f * d * rate);
                b.ordnance = Mathf.Min(b.ordnanceCap, b.ordnance + b.ordnanceCap * 0.25f * d * rate);
                BaseDefense.Rebuild(b, d);
                foreach (var i in b.inventory)
                {
                    if (i.available >= i.authorized || rate <= 0) continue;
                    // one airframe per 2 days at full rate; carry fractional progress in the timestamp granularity
                    float add = d * rate / 2f;
                    if (UnityEngine.Random.value < add - Mathf.Floor(add)) add = Mathf.Ceil(add); else add = Mathf.Floor(add);
                    if (add > 0) { i.available = Mathf.Min(i.authorized, i.available + (int)add); Debug.Log($"[BDArmoryAvionics]: {b.site}: {(int)add} {i.craft} replacement(s) arrived ({i.available}/{i.authorized})"); }
                }
            }
        }

        public static void UpdateStatus(Base b)
        {
            var old = b.status;
            b.status = b.health <= 0.5f ? BaseStatus.Destroyed : b.health < 60f ? BaseStatus.Damaged : BaseStatus.Operational;
            if (old != b.status)
            {
                Debug.Log($"[BDArmoryAvionics]: base {b.site} is now {b.status} ({b.health:F0}%)");
                if (HighLogic.LoadedSceneIsFlight) ScreenMessages.PostScreenMessage($"{Name(b)} {b.status.ToString().ToUpper()} ({b.health:F0}%)", 4f, ScreenMessageStyle.UPPER_CENTER);
                CombatLog.Note($"{Name(b)} is {b.status} ({b.health:F0}%)", true, b.status == BaseStatus.Operational ? 0 : 2);
            }
        }

        public static string Name(Base b) => b.site == "Runway" ? "KSC" : b.site;

        // ---- Launch / recovery / loss ----

        public static void OnLaunch(Vessel v, Base b, Faction f)
        {
            if (b == null) return;
            if (b.status == BaseStatus.Destroyed) ScreenMessages.PostScreenMessage($"{Name(b)} is destroyed - this sortie is off the books", 4f, ScreenMessageStyle.UPPER_CENTER);
            var item = b.inventory.Find(i => i.craft == v.GetName());
            if (item == null) return; // not a tracked type: free (e.g. test craft)
            if (item.available <= 0) { ScreenMessages.PostScreenMessage($"{Name(b)} has no {item.craft} available (0/{item.authorized}) - flying anyway", 4f, ScreenMessageStyle.UPPER_CENTER); Debug.Log($"[BDArmoryAvionics]: {v.GetName()} launched from {b.site} with none in stock"); }
            else item.available--;
            launched[v.persistentId] = (b, item.craft);
            Debug.Log($"[BDArmoryAvionics]: {v.GetName()} launched from {b.site}: {item.available}/{item.authorized} left");
        }

        public static void Track(Vessel v, Base b, string craft) => launched[v.persistentId] = (b, craft);
        public static void Untrack(Vessel v) => launched.Remove(v.persistentId);

        public static void Subscribe()
        {
            GameEvents.onVesselRecovered.Add(OnRecovered);
            GameEvents.onVesselWillDestroy.Add(OnDestroyed);
            GameEvents.OnVesselRecoveryRequested.Add(OnRecoveryRequested);
        }

        public static void Unsubscribe()
        {
            GameEvents.onVesselRecovered.Remove(OnRecovered);
            GameEvents.onVesselWillDestroy.Remove(OnDestroyed);
            GameEvents.OnVesselRecoveryRequested.Remove(OnRecoveryRequested);
            leaving = false;
        }

        static bool leaving;
        public static void SceneLeaving() => leaving = true;
        static void OnRecoveryRequested(Vessel v) { if (v != null) launched.Remove(v.persistentId); }

        static void OnRecovered(ProtoVessel pv, bool quick)
        {
            if (pv == null) return;
            launched.Remove(pv.persistentId);
            var b = FactionsScenario.BaseAt(pv.landedAt) ?? NearestOwnedBase(pv.latitude, pv.longitude, 3000);
            if (b == null) return;
            var item = b.inventory.Find(i => i.craft == pv.vesselName);
            if (item == null) return;
            item.available++;
            Debug.Log($"[BDArmoryAvionics]: {pv.vesselName} recovered at {b.site}: {item.available}/{item.authorized}");
        }

        static void OnDestroyed(Vessel v)
        {
            if (v == null || leaving || !launched.TryGetValue(v.persistentId, out var l)) return;
            launched.Remove(v.persistentId);
            var item = l.b.inventory.Find(i => i.craft == l.craft);
            if (item != null) item.lost++;
            Debug.Log($"[BDArmoryAvionics]: {l.craft} from {l.b.site} lost");
        }

        static Base NearestOwnedBase(double lat, double lon, double maxM)
        {
            var sc = FactionsScenario.Instance; var body = FlightGlobals.GetHomeBody();
            if (sc == null || body == null) return null;
            Base best = null; double bd = maxM;
            var p = body.GetWorldSurfacePosition(lat, lon, 0);
            foreach (var b in sc.bases)
            {
                double d = (body.GetWorldSurfacePosition(b.lat, b.lon, 0) - p).magnitude;
                if (d < bd && !string.IsNullOrEmpty(b.faction)) { bd = d; best = b; }
            }
            return best;
        }

        // ---- Flight: ground service and capture ----

        public static void FlightTick()
        {
            var sc = FactionsScenario.Instance;
            if (sc == null) return;
            float dt = lastFlightTick > 0 ? Time.time - lastFlightTick : 0; lastFlightTick = Time.time;
            foreach (var b in sc.bases)
            {
                var body = FlightGlobals.GetHomeBody();
                var center = body.GetWorldSurfacePosition(b.lat, b.lon, b.alt);
                var owner = FactionsScenario.FactionByName(b.faction);
                bool defended = false; Vessel attacker = null;
                foreach (var v in FlightGlobals.VesselsLoaded)
                {
                    if (v == null || v.packed || (v.CoMD - center).magnitude > BaseRadius) continue;
                    var wm = v.ActiveController().WM;
                    if (v.LandedOrSplashed && wm != null && wm.Team != null)
                    {
                        if (owner != null && wm.Team.Name == owner.team) defended = true;
                        else if (owner == null || owner.BDTeam.IsEnemy(wm.Team)) attacker = v;
                    }
                    if (owner != null && wm != null && wm.Team != null && wm.Team.Name == owner.team) Service(v, b);
                }
                // Capture
                if (attacker != null && !defended)
                {
                    var af = FactionsScenario.FactionOfTeam(attacker.ActiveController().WM.Team.Name);
                    if (af == null) continue;
                    if (b.capturer != af.name) { b.capturer = af.name; b.captureProgress = 0; ScreenMessages.PostScreenMessage($"{af.name} is taking {Name(b)}!", 4f, ScreenMessageStyle.UPPER_CENTER); }
                    b.captureProgress += dt;
                    if (b.captureProgress >= CaptureSeconds)
                    {
                        string old = b.faction;
                        b.faction = af.name; b.captureProgress = 0; b.capturer = null;
                        foreach (var i in b.inventory) i.available = 0; // the loser's aircraft are gone
                        foreach (var ds in b.defenses) ds.alive = false; // and so are their SAM sites
                        ScreenMessages.PostScreenMessage($"{Name(b)} CAPTURED by {af.name}", 6f, ScreenMessageStyle.UPPER_CENTER);
                        CombatLog.Note($"{Name(b)} captured by {af.name} from {old}", true, 1);
                        Debug.Log($"[BDArmoryAvionics]: base {b.site} captured by {af.name} from {old}");
                        CSARScenario.OnBaseCaptured(b, af);
                    }
                }
                else if (b.captureProgress > 0) { b.captureProgress = Mathf.Max(0, b.captureProgress - dt * 2); if (b.captureProgress == 0) b.capturer = null; }
            }
        }

        /// <summary>Refuel and re-arm a friendly craft stopped on the ground at a base (once per stop).</summary>
        static void Service(Vessel v, Base b)
        {
            if (!v.LandedOrSplashed || v.srfSpeed > 1 || b.status == BaseStatus.Destroyed) { stoppedSince.Remove(v.persistentId); serviced.Remove(v.persistentId); return; }
            if (serviced.Contains(v.persistentId)) return;
            if (!stoppedSince.TryGetValue(v.persistentId, out float t0)) { stoppedSince[v.persistentId] = Time.time; return; }
            if (Time.time - t0 < 10f) return;
            serviced.Add(v.persistentId);
            double fuelUsed = 0, ordUsed = 0;
            foreach (var p in v.parts)
                foreach (PartResource r in p.Resources)
                {
                    if (r.resourceName == "ElectricCharge" || r.resourceName == "IntakeAir" || r.amount >= r.maxAmount) continue;
                    double need = r.maxAmount - r.amount;
                    bool ammo = r.resourceName.IndexOf("Ammo", StringComparison.OrdinalIgnoreCase) >= 0 || r.resourceName.IndexOf("Shell", StringComparison.OrdinalIgnoreCase) >= 0 || r.resourceName.IndexOf("Round", StringComparison.OrdinalIgnoreCase) >= 0 || r.resourceName.IndexOf("Bullet", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (ammo)
                    {
                        double cost = need / r.maxAmount * 10.0; // 10 ordnance points per full magazine
                        double frac = Math.Min(1, b.ordnance / Math.Max(cost, 1e-6));
                        r.amount += need * frac; b.ordnance -= (float)(cost * frac); ordUsed += cost * frac;
                    }
                    else
                    {
                        double frac = Math.Min(1, b.fuel / need);
                        r.amount += need * frac; b.fuel -= (float)(need * frac); fuelUsed += need * frac;
                    }
                }
            if (fuelUsed + ordUsed > 0)
            {
                ScreenMessages.PostScreenMessage($"{v.GetName()} serviced at {Name(b)}: {fuelUsed:F0} fuel, {ordUsed:F0} ordnance", 4f, ScreenMessageStyle.UPPER_CENTER);
                Debug.Log($"[BDArmoryAvionics]: {v.GetName()} serviced at {b.site}: fuel {fuelUsed:F0}, ordnance {ordUsed:F0} (base left {b.fuel:F0} / {b.ordnance:F0})");
            }
        }

        // ---- Damage from BDArmory explosions ----

        public static void Explosion(Vector3 pos, float tnt, string team)
        {
            var sc = FactionsScenario.Instance;
            if (sc == null || tnt <= 0) return;
            var body = FlightGlobals.currentMainBody;
            if (body == null) return;
            foreach (var b in sc.bases)
            {
                var center = (Vector3)body.GetWorldSurfacePosition(b.lat, b.lon, b.alt);
                float d = (pos - center).magnitude;
                if (d > BaseRadius) continue;
                var owner = FactionsScenario.FactionByName(b.faction);
                if (owner != null && team == owner.team) continue; // own ordnance (e.g. a defender's missile) doesn't wreck the base
                float dmg = Mathf.Clamp(tnt * 0.25f, 0.01f, 30f) * Mathf.Clamp01(1.2f - d / BaseRadius);
                b.health = Mathf.Max(0, b.health - dmg);
                // Runway craters: within 60 m of the runway centreline
                if (b.hasRunway && tnt >= 5f)
                {
                    Vector3 a = body.GetWorldSurfacePosition(b.latA, b.lonA, b.alt), c = body.GetWorldSurfacePosition(b.latB, b.lonB, b.alt);
                    Vector3 ac = c - a; float t = Mathf.Clamp01(Vector3.Dot(pos - a, ac) / ac.sqrMagnitude);
                    if ((a + ac * t - pos).magnitude < 60f)
                    {
                        double ut = Planetarium.GetUniversalTime();
                        double hours = Mathf.Clamp(tnt / 10f, 0.5f, 6f) * 2.0; // 1 h per 5 kg, max 12 h per hit
                        b.runwayClosedUntil = Math.Max(b.runwayClosedUntil, ut) + hours * 3600;
                        ScreenMessages.PostScreenMessage($"{Name(b)} runway cratered - closed {KSPUtil.PrintTimeCompact(b.runwayClosedUntil - ut, false)}", 4f, ScreenMessageStyle.UPPER_CENTER);
                        Debug.Log($"[BDArmoryAvionics]: {b.site} runway hit ({tnt:F1} kg), closed until UT {b.runwayClosedUntil:F0}");
                    }
                }
                UpdateStatus(b);
            }
        }

        // ---- Theatre window: base detail ----

        /// <summary>
        /// Craft an AI faction can fly: weapon manager + pilot AI, not a transport (8+ seats). Role from the weapons:
        /// air-to-air and ground attack = Multirole, anti-radiation only = SEAD, ground attack = Strike, else Fighter.
        /// </summary>
        public static List<(string name, string path, string role)> ClassifyCraft()
        {
            var list = new List<(string, string, string)>();
            string[] aa = ["aim", "r73", "r77", "r27", "meteor", "amraam", "sidewinder", "python", "mica", "iris", "hhq", "phoenix", "sparrow"];
            string[] ag = ["bomb", "mk8", "jdam", "gbu", "cbu", "agm", "harm", "tow", "hellfire", "maverick", "cruise", "rbs", "kh", "rocket", "hydra", "jsow", "torpedo"];
            string[] arm = ["harm", "antirad", "kh31", "alarm"];
            foreach (var (name, path) in CraftFiles())
            {
                bool wm = false, ai = false, hasAA = false, hasAG = false, hasARM = false;
                string part = "";
                try
                {
                    foreach (var raw in File.ReadLines(path))
                    {
                        var l = raw.Trim();
                        if (l.StartsWith("part = ")) part = l.Substring(7).ToLowerInvariant();
                        else if (l == "name = MissileFire") wm = true;
                        else if (l == "name = BDModulePilotAI") ai = true;
                        else if (l == "name = MissileLauncher" || l == "name = BDModularGuidance")
                        {
                            bool isAA = false; foreach (var k in aa) if (part.Contains(k)) isAA = true;
                            bool isAG = false; foreach (var k in ag) if (part.Contains(k)) isAG = true;
                            foreach (var k in arm) if (part.Contains(k)) hasARM = true;
                            hasAA |= isAA; hasAG |= isAG && !isAA;
                        }
                    }
                }
                catch { continue; }
                if (!wm || !ai) continue;
                // seats: from the part prefabs
                int capacity = 0;
                try
                {
                    foreach (var raw in File.ReadLines(path))
                    {
                        var l = raw.Trim();
                        if (!l.StartsWith("part = ")) continue;
                        string pn = l.Substring(7); int us = pn.LastIndexOf('_'); if (us > 0) pn = pn.Substring(0, us);
                        var ap = PartLoader.getPartInfoByName(pn);
                        if (ap?.partPrefab != null) capacity += ap.partPrefab.CrewCapacity;
                    }
                }
                catch { }
                if (capacity >= 6) { Debug.Log($"[BDArmoryAvionics]: auto-stock: {name} skipped (transport, {capacity} seats)"); continue; }
                string role = hasAA && hasAG ? "Multirole" : hasARM && !hasAG ? "SEAD" : hasAG ? "Strike" : "Fighter";
                list.Add((name, path, role));
            }
            // one entry per craft name
            var seen = new HashSet<string>(); var outList = new List<(string, string, string)>();
            foreach (var c in list) if (seen.Add(c.Item1)) outList.Add(c);
            return outList;
        }

        /// <summary>Give an AI faction's empty bases their aircraft (2 of each suitable craft).</summary>
        public static int AutoStock(Faction f, bool force = false)
        {
            var sc = FactionsScenario.Instance;
            if (sc == null || f == null) return 0;
            craftFiles = null; // re-read the Ships folder
            var craft = ClassifyCraft();
            int added = 0;
            foreach (var b in sc.bases)
            {
                if (b.faction != f.name || (!force && b.inventory.Count > 0)) continue;
                foreach (var (name, path, role) in craft)
                {
                    if (b.inventory.Exists(i => i.craft == name)) continue;
                    b.inventory.Add(new InvItem { craft = name, path = path, role = role, authorized = 2, available = 2 });
                    added++;
                }
                if (added > 0) Debug.Log($"[BDArmoryAvionics]: auto-stock {b.site} ({f.name}): {string.Join(", ", b.inventory.ConvertAll(i => $"{i.craft} [{i.role}]"))}");
            }
            return added;
        }

        static List<(string name, string path)> craftFiles;
        static int craftIdx;

        static List<(string name, string path)> CraftFiles()
        {
            if (craftFiles != null) return craftFiles;
            craftFiles = new();
            try
            {
                string root = Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "Ships");
                foreach (var dir in new[] { "SPH", "VAB" })
                {
                    string d = Path.Combine(root, dir);
                    if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d, "*.craft"))
                    {
                        string name = Path.GetFileNameWithoutExtension(f);
                        foreach (var line in File.ReadLines(f)) { var l = line.Trim(); if (l.StartsWith("ship =")) { name = l.Substring(6).Trim(); break; } }
                        craftFiles.Add((name, f));
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: reading craft files failed: {e.Message}"); }
            return craftFiles;
        }

        public static void DrawBase(Rect r, Base b)
        {
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle; var small = AvionicsDraw.LabelSmall;
            float x = r.x, y = r.y, w = r.width;
            var f = FactionsScenario.FactionByName(b.faction);
            var col = f != null ? f.colour : AvionicsDraw.Neutral;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 18), $"{Name(b)}  -  {f?.name ?? "unowned"}  -  {b.status}", AvionicsDraw.Header, col); y += 20;
            Bar(new Rect(x, y, w, 14), b.health / 100f, $"Health {b.health:F0}%", b.health >= 60 ? AvionicsDraw.Friendly : b.health > 0 ? AvionicsDraw.Unknown : AvionicsDraw.Hostile); y += 17;
            Bar(new Rect(x, y, w / 2 - 2, 14), b.fuel / Mathf.Max(1, b.fuelCap), $"Fuel {b.fuel:F0}/{b.fuelCap:F0}", AvionicsDraw.Friendly);
            Bar(new Rect(x + w / 2 + 2, y, w / 2 - 2, 14), b.ordnance / Mathf.Max(1, b.ordnanceCap), $"Ordnance {b.ordnance:F0}/{b.ordnanceCap:F0}", AvionicsDraw.LockBox); y += 18;
            double closed = b.runwayClosedUntil - Planetarium.GetUniversalTime();
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), !b.hasRunway ? "Runway: not measured yet (fly near the base once)" : closed > 0 ? $"Runway CLOSED for {KSPUtil.PrintTimeCompact(closed, false)} (craters)" : b.status == BaseStatus.Destroyed ? "Runway unusable (base destroyed)" : "Runway open", small, closed > 0 || b.status == BaseStatus.Destroyed ? AvionicsDraw.Hostile : AvionicsDraw.ScopeText); y += 14;
            if (b.captureProgress > 0) { AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"Being captured by {b.capturer}: {b.captureProgress / CaptureSeconds:P0}", small, AvionicsDraw.Hostile); y += 14; }
            y += 4;

            AvionicsDraw.ColorLabel(new Rect(x, y + 2, w - 130, 14), b.defenses.Count == 0 ? "AIR DEFENCE: none (no SAM craft, or press Re-stock)" : $"AIR DEFENCE: {BaseDefense.Standing(b)}/{b.defenses.Count} SAM sites up  ({string.Join(", ", b.defenses.ConvertAll(d => d.craft + (d.alive ? "" : " X")))})", small, BaseDefense.Standing(b) > 0 ? AvionicsDraw.Friendly : AvionicsDraw.Unknown);
            if (GUI.Button(new Rect(x + w - 124, y, 124, 18), "Re-stock SAMs", btn)) { b.defenses.Clear(); BaseDefense.AutoStock(); }
            y += 20;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "AIRCRAFT (available / authorized, lost)", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
            for (int i = 0; i < b.inventory.Count; i++)
            {
                var it = b.inventory[i];
                if (y > r.yMax - 48) break;
                AvionicsDraw.ColorLabel(new Rect(x, y + 2, w - 270, 16), $"{it.craft}   {it.available}/{it.authorized}   lost {it.lost}", AvionicsDraw.Label, it.available > 0 ? AvionicsDraw.HudText : AvionicsDraw.Unknown);
                if (GUI.Button(new Rect(x + w - 266, y, 66, 18), it.role, btn)) it.role = InvItem.Roles[(Array.IndexOf(InvItem.Roles, it.role) + 1) % InvItem.Roles.Length];
                if (GUI.Button(new Rect(x + w - 196, y, 28, 18), "-", btn)) { it.authorized = Mathf.Max(0, it.authorized - 1); it.available = Mathf.Min(it.available, it.authorized); }
                if (GUI.Button(new Rect(x + w - 164, y, 28, 18), "+", btn)) it.authorized++;
                if (GUI.Button(new Rect(x + w - 132, y, 70, 18), "Fill", btn)) it.available = it.authorized;
                if (GUI.Button(new Rect(x + w - 58, y, 58, 18), "Remove", btn)) { b.inventory.RemoveAt(i); break; }
                y += 20;
            }
            var files = CraftFiles();
            if (files.Count == 0) { AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "No craft files in this save's Ships folders.", small, AvionicsDraw.ScopeText); return; }
            craftIdx = Mathf.Clamp(craftIdx, 0, files.Count - 1);
            if (GUI.Button(new Rect(x, y, 24, 20), "<", btn)) craftIdx = (craftIdx + files.Count - 1) % files.Count;
            AvionicsDraw.ColorLabel(new Rect(x + 30, y + 2, w - 190, 16), files[craftIdx].name, AvionicsDraw.Label, AvionicsDraw.HudText);
            if (GUI.Button(new Rect(x + w - 156, y, 24, 20), ">", btn)) craftIdx = (craftIdx + 1) % files.Count;
            if (GUI.Button(new Rect(x + w - 126, y, 126, 20), "Station here (x2)", btn) && b.inventory.Find(i => i.craft == files[craftIdx].name) == null)
                b.inventory.Add(new InvItem { craft = files[craftIdx].name, path = files[craftIdx].path, authorized = 2, available = 2 });
            y += 24;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 28), "Launching a stationed craft from here takes an airframe; recovering it returns one. Replacements: 1 per 2 days.\nLand and stop at a friendly base for 10 s to refuel and re-arm guns from base stock.", small, AvionicsDraw.ScopeText);
        }

        static void Bar(Rect r, float frac, string text, Color c)
        {
            AvionicsDraw.FilledRect(r, new Color(0.1f, 0.12f, 0.12f, 0.9f));
            AvionicsDraw.FilledRect(new Rect(r.x, r.y, r.width * Mathf.Clamp01(frac), r.height), c * 0.6f);
            AvionicsDraw.ColorLabel(new Rect(r.x + 4, r.y, r.width, r.height), text, AvionicsDraw.LabelSmall, AvionicsDraw.HudText);
        }
    }

    [HarmonyPatch]
    static class Patch_ExplosionBaseDamage
    {
        static System.Reflection.MethodBase TargetMethod() => AccessTools.Method("BDArmory.FX.ExplosionFx:CreateExplosion");
        static void Prefix(Vector3 __0, float __1, Part __6, string __7, string __8, bool __12)
        {
            if (__12) return; // visual only
            try { Fragmentation.Detonate(__6, __0, __1, __7, __8); } catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: fragmentation: {e.Message}"); }
            try { BaseLogistics.Explosion(__0, __1, __8); } catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: base damage: {e.Message}"); }
        }
    }
}
