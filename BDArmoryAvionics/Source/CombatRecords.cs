using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    public enum WeaponKind { Gun, Rocket, Missile, Fire, Ram, Manoeuvre, Other }
    public enum Domain { Air, Ground, Sea, Space }

    /// <summary>A named group of combat operators. Its commander lifts the whole squadron while flying with it.</summary>
    public class Squadron
    {
        public string name = "", commander = "", motto = "";

        public void Save(ConfigNode n) { n.AddValue("name", name); n.AddValue("commander", commander ?? ""); n.AddValue("motto", motto ?? ""); }
        public static Squadron Load(ConfigNode n) => new() { name = n.GetValue("name") ?? "", commander = n.GetValue("commander") ?? "", motto = n.GetValue("motto") ?? "" };
    }

    /// <summary>Ranks come from merit: sorties, battles, kills, medals and training. Never demoted.</summary>
    public static class Ranks
    {
        public static readonly string[] Names = ["Cadet", "Second Lieutenant", "First Lieutenant", "Captain", "Major", "Lieutenant Colonel", "Colonel", "Brigadier General"];
        public static readonly string[] Abbrev = ["Cdt", "2Lt", "1Lt", "Capt", "Maj", "LtCol", "Col", "BrigGen"];
        static readonly int[] Threshold = [0, 40, 90, 160, 260, 400, 600, 900];

        public static int Merit(Operator o) => o.sorties * 5 + o.Stat("battles") * 10 + o.kills * 25 + o.medals.Count * 15 + o.LevelSum * 10;

        public static int Index(Operator o)
        {
            int m = Merit(o), r = 0;
            for (int i = 0; i < Threshold.Length; i++) if (m >= Threshold[i]) r = i;
            return r;
        }

        public static int Current(Operator o) => Mathf.Clamp(Mathf.Max(o.rank, Index(o)), 0, Names.Length - 1);
        public static string Short(Operator o) => Abbrev[Current(o)];
        public static string Long(Operator o) => Names[Current(o)];
        public static int NextMerit(Operator o) { int r = Current(o); return r + 1 < Threshold.Length ? Threshold[r + 1] : -1; }
    }

    public static class Medals
    {
        public static int AirKills(Operator o) => o.Stat("kill.vs.Air");
        public static int SurfaceKills(Operator o) => o.Stat("kill.vs.Ground") + o.Stat("kill.vs.Sea");

        public static IEnumerable<string> Earned(Operator o, int killsThisBattle)
        {
            if (AirKills(o) >= 5) yield return "Ace";
            if (AirKills(o) >= 10) yield return "Double Ace";
            if (AirKills(o) >= 15) yield return "Triple Ace";
            if (killsThisBattle >= 5) yield return "Ace in a Day";
            if (SurfaceKills(o) >= 5) yield return "Strike Medal";
            if (o.Stat("kill.Gun") >= 5) yield return "Gunfighter";
            if (o.Stat("kill.Missile") >= 10) yield return "Missileer";
            if (o.Stat("survived") >= 1) yield return "Survivor (shot down, lived)";
            if (o.Stat("battles") >= 10) yield return "Veteran (10 battles)";
            bool master = true; foreach (var l in o.level) if (l < Operator.MaxLevel) master = false;
            if (master) yield return "Master Operator";
        }
    }

    public class BattleEvent
    {
        public float t;          // seconds since the battle started
        public int kind;         // 0 info, 1 kill, 2 loss, 3 friendly fire / KIA
        public string text = "";
    }

    public class BattleRow
    {
        public string name = "", team = "", crew = "", status = "";
        public bool manned, alive = true;
        public int kills, hits;
        public float dealt, taken;
    }

    /// <summary>After-action report of one engagement, kept with the save (last 25) and written to PluginData/AAR.</summary>
    public class BattleReport
    {
        public double startUT, endUT;
        public string place = "", realTime = "";
        public readonly List<BattleEvent> events = new();
        public readonly List<BattleRow> rows = new();
        static readonly System.Globalization.CultureInfo CI = System.Globalization.CultureInfo.InvariantCulture;

        public double Duration => Math.Max(0, endUT - startUT);
        public string Title => $"{KSPUtil.PrintDate(startUT, false)}  {place}";

        public IEnumerable<(string team, int units, int kills, int lost, int mannedLost)> Teams()
        {
            var order = new List<string>();
            var d = new Dictionary<string, (int units, int kills, int lost, int mannedLost)>();
            foreach (var r in rows)
            {
                string t = string.IsNullOrEmpty(r.team) ? "-" : r.team;
                if (!d.ContainsKey(t)) { d[t] = (0, 0, 0, 0); order.Add(t); }
                var x = d[t];
                d[t] = (x.units + 1, x.kills + r.kills, x.lost + (r.alive ? 0 : 1), x.mannedLost + (!r.alive && r.manned ? 1 : 0));
            }
            foreach (var t in order) { var x = d[t]; yield return (t, x.units, x.kills, x.lost, x.mannedLost); }
        }

        public BattleRow Mvp
        {
            get
            {
                BattleRow best = null;
                foreach (var r in rows) if (r.kills > 0 && (best == null || r.kills > best.kills || r.kills == best.kills && r.dealt > best.dealt)) best = r;
                return best;
            }
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("BD Armory: Enhanced Lethality Edition - After-Action Report");
            sb.AppendLine($"{Title}   (real time {realTime})");
            sb.AppendLine($"Duration {KSPUtil.PrintTimeCompact(Duration, false)}");
            sb.AppendLine();
            foreach (var t in Teams()) sb.AppendLine($"Team {t.team}: {t.units} units, {t.kills} kills, {t.lost} lost ({t.mannedLost} manned)");
            var mvp = Mvp; if (mvp != null) sb.AppendLine($"MVP: {mvp.name} ({mvp.crew}) {mvp.kills} kills");
            sb.AppendLine();
            sb.AppendLine("Unit                      Team  Crew                          Kills Hits  Dealt   Taken   Result");
            foreach (var r in rows)
                sb.AppendLine($"{Pad(r.name, 25)} {Pad(r.team, 5)} {Pad(r.manned ? r.crew : "uncrewed", 29)} {r.kills,5} {r.hits,4} {r.dealt,7:F0} {r.taken,7:F0}   {r.status}");
            sb.AppendLine();
            sb.AppendLine("Timeline");
            foreach (var e in events) sb.AppendLine($"  [{Clock(e.t)}] {e.text}");
            return sb.ToString();
        }

        static string Pad(string s, int n) { s ??= ""; return s.Length > n ? s.Substring(0, n) : s.PadRight(n); }
        public static string Clock(float t) => $"{(int)(t / 60):00}:{(int)(t % 60):00}";

        public void Save(ConfigNode n)
        {
            n.AddValue("startUT", startUT.ToString("R", CI));
            n.AddValue("endUT", endUT.ToString("R", CI));
            n.AddValue("place", place);
            n.AddValue("realTime", realTime);
            foreach (var r in rows)
            {
                var c = n.AddNode("UNIT");
                c.AddValue("name", r.name); c.AddValue("team", r.team); c.AddValue("crew", r.crew); c.AddValue("status", r.status);
                c.AddValue("manned", r.manned); c.AddValue("alive", r.alive); c.AddValue("kills", r.kills); c.AddValue("hits", r.hits);
                c.AddValue("dealt", r.dealt.ToString("F0", CI)); c.AddValue("taken", r.taken.ToString("F0", CI));
            }
            foreach (var e in events)
            {
                var c = n.AddNode("EVENT");
                c.AddValue("t", e.t.ToString("F1", CI)); c.AddValue("kind", e.kind); c.AddValue("text", e.text);
            }
        }

        public static BattleReport Load(ConfigNode n)
        {
            var b = new BattleReport { place = n.GetValue("place") ?? "", realTime = n.GetValue("realTime") ?? "" };
            double.TryParse(n.GetValue("startUT"), System.Globalization.NumberStyles.Float, CI, out b.startUT);
            double.TryParse(n.GetValue("endUT"), System.Globalization.NumberStyles.Float, CI, out b.endUT);
            foreach (var c in n.GetNodes("UNIT"))
            {
                var r = new BattleRow { name = c.GetValue("name") ?? "", team = c.GetValue("team") ?? "", crew = c.GetValue("crew") ?? "", status = c.GetValue("status") ?? "" };
                bool.TryParse(c.GetValue("manned"), out r.manned);
                if (!bool.TryParse(c.GetValue("alive"), out r.alive)) r.alive = true;
                int.TryParse(c.GetValue("kills"), out r.kills); int.TryParse(c.GetValue("hits"), out r.hits);
                float.TryParse(c.GetValue("dealt"), System.Globalization.NumberStyles.Float, CI, out r.dealt);
                float.TryParse(c.GetValue("taken"), System.Globalization.NumberStyles.Float, CI, out r.taken);
                b.rows.Add(r);
            }
            foreach (var c in n.GetNodes("EVENT"))
            {
                var e = new BattleEvent { text = c.GetValue("text") ?? "" };
                float.TryParse(c.GetValue("t"), System.Globalization.NumberStyles.Float, CI, out e.t);
                int.TryParse(c.GetValue("kind"), out e.kind);
                b.events.Add(e);
            }
            return b;
        }
    }

    /// <summary>
    /// Flight-scene kill log: attributes BDArmory damage (guns, rockets, missiles, fire, rams) to the craft that caused it,
    /// classifies kills (A-A, A-G, G-A, manned/unmanned, weapon), credits the crews' service records,
    /// and turns each engagement into an after-action report.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class CombatLog : MonoBehaviour
    {
        public static CombatLog Instance;
        public static BattleReport Live;
        internal static bool inApplyScore;
        const float CreditWindow = 30f, BattleTimeout = 45f;

        class Combatant
        {
            public Vessel vessel;
            public uint id;
            public string name, team = "";
            public readonly List<string> crew = new();
            public bool manned, hadWM, hadCommand, alive = true;
            public int kills, hits;
            public float dealt, taken;
            public string status = "survived";
            // last hostile damage
            public string lastAttacker;
            public Combatant lastAttackerC;
            public WeaponKind lastKind;
            public string lastWeapon;
            public float lastHitTime = -999f;
            public Domain victimDomain, attackerDomain;
        }

        readonly Dictionary<uint, Combatant> combatants = new();
        readonly Dictionary<string, List<Vessel>> nameCache = new(); // BDArmory reports craft by name; copies of one craft share it
        readonly Dictionary<string, int> killsThisBattle = new();
        readonly HashSet<string> shotDown = new();
        bool battle, leaving;
        float lastActivity, nextTick;
        static bool warned;
        Vector2 scroll;

        void Awake() { Instance = this; Live = null; }

        void Start()
        {
            GameEvents.onVesselWillDestroy.Add(OnVesselWillDestroy);
            GameEvents.OnVesselRecoveryRequested.Add(OnRecoveryRequested);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
        }

        void OnDestroy()
        {
            GameEvents.onVesselWillDestroy.Remove(OnVesselWillDestroy);
            GameEvents.OnVesselRecoveryRequested.Remove(OnRecoveryRequested);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
            if (battle) EndBattle();
            if (Instance == this) Instance = null;
        }

        void OnSceneChange(GameScenes s) { if (battle) EndBattle(); leaving = true; }

        void OnRecoveryRequested(Vessel v)
        {
            if (v != null) combatants.Remove(v.persistentId); // recovered, not lost
        }

        void OnVesselWillDestroy(Vessel v)
        {
            if (v == null || leaving) return;
            if (combatants.TryGetValue(v.persistentId, out var c)) Safe(() => Die(c, "destroyed"));
        }

        // ---- Damage attribution (called from the Harmony patches below) ----

        public static void Hit(string attacker, Vessel victim, WeaponKind kind, float damage, string weapon, bool newHit)
        {
            if (Instance == null || victim == null || string.IsNullOrEmpty(attacker) || Instance.leaving) return;
            Safe(() => Instance.RecordHit(attacker, victim, kind, damage, weapon, newHit));
        }

        public static void HitByName(string attacker, string victim, WeaponKind kind, float damage)
        {
            if (Instance == null || string.IsNullOrEmpty(victim)) return;
            if (Instance.nameCache.TryGetValue(victim, out var list) && list.Count > 0) Hit(attacker, list[0], kind, damage, null, true);
        }

        static void Safe(Action a)
        {
            try { a(); }
            catch (Exception e) { if (!warned) { warned = true; Debug.LogWarning($"[BDArmoryAvionics]: combat log error: {e}"); } }
        }

        static bool Ignored(Vessel v) => v.vesselType == VesselType.Debris || v.vesselType == VesselType.EVA || v.vesselType == VesselType.SpaceObject || v.vesselType == VesselType.Flag;

        void RecordHit(string attacker, Vessel victim, WeaponKind kind, float damage, string weapon, bool newHit)
        {
            if (victim.GetName() == attacker || Ignored(victim)) return;
            var vc = Get(victim);
            if (vc == null || !vc.alive) return;
            var av = ResolveAttacker(attacker, victim, vc);
            var ac = av != null ? Get(av) : FindByName(attacker);
            if (ac != null && ac.team != "" && ac.team == vc.team && kind == WeaponKind.Fire) return; // own fire spreading
            if (!battle) StartBattle();
            lastActivity = Time.time;

            bool newStrike = kind == WeaponKind.Gun ? newHit : (vc.lastAttacker != attacker || Time.time - vc.lastHitTime > 0.5f);
            if (ac != null) { ac.dealt += damage; if (newStrike) ac.hits++; }
            vc.taken += damage;
            if (kind == WeaponKind.Fire && vc.lastAttacker == attacker && Time.time - vc.lastHitTime < CreditWindow) { vc.lastHitTime = Time.time; return; } // keep the weapon that started it
            vc.lastAttacker = attacker;
            vc.lastAttackerC = ac;
            vc.lastKind = kind;
            if (!string.IsNullOrEmpty(weapon)) vc.lastWeapon = weapon; else if (kind != WeaponKind.Gun) vc.lastWeapon = null;
            vc.lastHitTime = Time.time;
            vc.victimDomain = DomainOf(victim);
            vc.attackerDomain = av != null ? DomainOf(av) : vc.attackerDomain;
        }

        Combatant Get(Vessel v)
        {
            if (v == null) return null;
            if (!combatants.TryGetValue(v.persistentId, out var c))
            {
                if (Ignored(v)) return null;
                c = new Combatant { vessel = v, id = v.persistentId, name = v.GetName() };
                combatants[v.persistentId] = c;
                Refresh(c);
            }
            return c;
        }

        /// <summary>The craft with this name that most likely fired: not the victim, preferably on another team, nearest.</summary>
        Vessel ResolveAttacker(string name, Vessel victim, Combatant vc)
        {
            if (!nameCache.TryGetValue(name, out var list)) return null;
            Vessel best = null; float bestScore = float.MaxValue;
            foreach (var v in list)
            {
                if (v == null || v == victim) continue;
                float score = (v.transform.position - victim.transform.position).sqrMagnitude;
                var wm = v.ActiveController().WM;
                if (wm != null && wm.Team != null && wm.Team.Name == vc.team) score += 1e12f;
                if (score < bestScore) { bestScore = score; best = v; }
            }
            return best;
        }

        Combatant FindByName(string name)
        {
            foreach (var c in combatants.Values) if (c.name == name) return c;
            return null;
        }

        static void Refresh(Combatant c)
        {
            var v = c.vessel;
            if (v == null) return;
            c.name = v.GetName();
            var wm = v.ActiveController().WM;
            if (wm != null) { c.hadWM = true; if (wm.Team != null) c.team = wm.Team.Name; }
            if (VesselModuleRegistry.GetModules<ModuleCommand>(v).Count > 0) c.hadCommand = true;
            foreach (var k in v.GetVesselCrew())
                if (!c.crew.Contains(k.name)) { c.crew.Add(k.name); c.manned = true; }
        }

        public static Domain DomainOf(Vessel v)
        {
            switch (v.situation)
            {
                case Vessel.Situations.LANDED:
                case Vessel.Situations.PRELAUNCH: return Domain.Ground;
                case Vessel.Situations.SPLASHED: return Domain.Sea;
            }
            var b = v.mainBody;
            if (b != null && (!b.atmosphere || v.altitude > b.atmosphereDepth)) return Domain.Space;
            return Domain.Air;
        }

        static string Letter(Domain d) => d switch { Domain.Air => "A", Domain.Ground => "G", Domain.Sea => "S", _ => "O" };

        // ---- Tick ----

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight || leaving || Time.time < nextTick) return;
            nextTick = Time.time + 0.5f;
            Safe(Tick);
        }

        void Tick()
        {
            nameCache.Clear();
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null) continue;
                string n = v.GetName();
                if (!nameCache.TryGetValue(n, out var l)) nameCache[n] = l = new List<Vessel>();
                l.Add(v);
                if (!v.packed && !Ignored(v) && v.ActiveController().WM != null) Get(v); // armed craft in range take part
            }

            var dead = new List<(Combatant, string)>();
            foreach (var c in combatants.Values)
            {
                if (!c.alive) continue;
                if (c.vessel == null) { dead.Add((c, "destroyed")); continue; }
                if (!c.vessel.loaded || c.vessel.packed) continue;
                bool hadCrew = c.manned && c.crew.Count > 0;
                Refresh(c);
                if (c.hadWM && VesselModuleRegistry.GetModules<MissileFire>(c.vessel).Count == 0) dead.Add((c, "weapons manager destroyed"));
                else if (c.hadCommand && VesselModuleRegistry.GetModules<ModuleCommand>(c.vessel).Count == 0) dead.Add((c, "lost control"));
                else if (hadCrew && c.vessel.GetCrewCount() == 0) dead.Add((c, "crew bailed out / killed"));
            }
            foreach (var (c, why) in dead) Die(c, why);

            if (battle)
            {
                UpdateRows();
                if (Time.time - lastActivity > BattleTimeout) EndBattle();
            }
        }

        // ---- Battles ----

        void StartBattle()
        {
            battle = true;
            lastActivity = Time.time;
            killsThisBattle.Clear();
            shotDown.Clear();
            var av = FlightGlobals.ActiveVessel;
            string place = "";
            if (av != null && av.mainBody != null)
            {
                string biome = "";
                try { biome = ScienceUtil.GetExperimentBiome(av.mainBody, av.latitude, av.longitude); } catch { }
                place = string.IsNullOrEmpty(biome) ? av.mainBody.bodyName : $"{av.mainBody.bodyName}, {biome}";
            }
            Live = new BattleReport { startUT = Planetarium.GetUniversalTime(), place = place, realTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            Note("Engagement started", false, 0);
            Debug.Log($"[BDArmoryAvionics]: battle started at {place}");
        }

        public static void Note(string text, bool important, int kind = 3)
        {
            if (Live == null) return;
            Live.events.Add(new BattleEvent { t = (float)(Planetarium.GetUniversalTime() - Live.startUT), kind = kind, text = text });
        }

        string CrewLabel(Combatant c, List<string> names)
        {
            if (names.Count == 0) return "uncrewed";
            var parts = new List<string>();
            foreach (var n in names)
            {
                var o = CombatOperators.Get(n);
                parts.Add(o != null ? $"{Ranks.Short(o)} {n.Split(' ')[0]}" : n.Split(' ')[0]);
            }
            return string.Join(", ", parts);
        }

        void Die(Combatant c, string why)
        {
            if (!c.alive || leaving) return;
            c.alive = false;
            bool recent = c.lastAttacker != null && Time.time - c.lastHitTime < CreditWindow;
            if (!battle)
            {
                if (!recent) { combatants.Remove(c.id); return; } // not in a fight: a test crash, staging, etc.
                StartBattle();
            }
            lastActivity = Time.time;

            string killerName = recent ? c.lastAttacker : null;
            Combatant killer = recent ? (c.lastAttackerC ?? FindByName(c.lastAttacker)) : null;
            WeaponKind kind = c.lastKind;
            Domain aDom = c.attackerDomain, vDom = recent ? c.victimDomain : (c.vessel != null ? DomainOf(c.vessel) : Domain.Air);
            string weapon = c.lastWeapon;

            if (!recent && c.vessel != null)
                foreach (var kv in OperatorEffects.lastTargets)
                    if (kv.Key != null && kv.Key != c.vessel && kv.Value.target == c.vessel && Time.time - kv.Value.time < 20f)
                    {
                        killer = Get(kv.Key);
                        if (killer == null || killer.team == c.team) { killer = null; continue; }
                        killerName = killer.name; kind = WeaponKind.Manoeuvre; aDom = DomainOf(kv.Key); weapon = null;
                        break;
                    }

            bool friendly = killer != null && killer.team != "" && killer.team == c.team;
            string code = $"{Letter(aDom)}-{Letter(vDom)}";
            string victimCrew = c.manned ? $"manned ({c.crew.Count})" : "unmanned";
            string how = kind == WeaponKind.Manoeuvre ? "manoeuvre kill" : $"{kind.ToString().ToLower()}{(string.IsNullOrEmpty(weapon) ? "" : $" ({weapon})")}";

            if (killerName != null && !friendly)
            {
                var crewNow = new List<string>();
                if (killer?.vessel != null) foreach (var k in killer.vessel.GetVesselCrew()) crewNow.Add(k.name);
                else if (killer != null) crewNow.AddRange(killer.crew);
                bool byMunition = false;
                if (crewNow.Count == 0)
                {
                    var op = LoiteringMunitions.OperatorOf(killerName); // a loitering munition's kill belongs to its launcher / operator crew
                    if (op != null) { foreach (var k in op.GetVesselCrew()) crewNow.Add(k.name); byMunition = true; }
                }
                if (killer != null) killer.kills++;
                foreach (var n in crewNow)
                {
                    var o = CombatOperators.Get(n);
                    if (o == null) continue;
                    o.kills++;
                    o.Count("kill." + code);
                    o.Count("kill.vs." + vDom);
                    o.Count(c.manned ? "kill.manned" : "kill.unmanned");
                    o.Count("kill." + kind);
                    if (byMunition) o.Count("kill.LM");
                    killsThisBattle[n] = (killsThisBattle.TryGetValue(n, out int k) ? k : 0) + 1;
                    OperatorEffects.CreditKillXp(o, kind == WeaponKind.Gun);
                    CombatOperators.Review(o, killsThisBattle[n]);
                }
                c.status = $"killed by {killerName}: {code} {how}";
                string text = $"{killerName} [{CrewLabel(killer, crewNow)}] killed {c.name} ({c.team}, {victimCrew}): {code} {how}";
                Note(text, true, 1);
                ScreenMessages.PostScreenMessage($"KILL  {killerName} > {c.name}  {code} {how}", 4f, ScreenMessageStyle.UPPER_LEFT);
                Debug.Log($"[BDArmoryAvionics]: kill: {text}; {why}");
            }
            else if (friendly)
            {
                c.status = $"friendly fire ({killerName}, {how})";
                if (killer?.vessel != null) foreach (var k in killer.vessel.GetVesselCrew()) CombatOperators.Get(k.name)?.Count("ff");
                Note($"FRIENDLY FIRE: {killerName} killed {c.name} ({victimCrew}), {how}", true, 3);
                Debug.Log($"[BDArmoryAvionics]: friendly fire: {killerName} killed {c.name}");
            }
            else
            {
                c.status = $"lost ({why})";
                Note($"{c.name} ({c.team}, {victimCrew}) lost: {why}", true, 2);
                Debug.Log($"[BDArmoryAvionics]: loss: {c.name} {why}, no hostile damage in the last {CreditWindow:F0} s");
            }

            foreach (var n in c.crew)
            {
                var o = CombatOperators.Get(n);
                if (o == null) continue;
                o.Count("losses");
                shotDown.Add(n);
            }
            UpdateRows();
        }

        void UpdateRows()
        {
            if (Live == null) return;
            Live.rows.Clear();
            foreach (var c in combatants.Values)
            {
                if (c.hits == 0 && c.taken <= 0 && c.kills == 0 && c.alive && !c.hadWM) continue;
                Live.rows.Add(new BattleRow
                {
                    name = c.name, team = c.team, crew = CrewLabel(c, c.crew), manned = c.manned, alive = c.alive,
                    kills = c.kills, hits = c.hits, dealt = c.dealt, taken = c.taken, status = c.alive ? "survived" : c.status
                });
            }
            Live.rows.Sort((a, b) => string.CompareOrdinal(a.team, b.team) != 0 ? string.CompareOrdinal(a.team, b.team) : b.kills.CompareTo(a.kills));
        }

        void EndBattle()
        {
            if (!battle) return;
            battle = false;
            var b = Live;
            if (b == null) return;
            b.endUT = Planetarium.GetUniversalTime() - (leaving ? 0 : BattleTimeout);
            if (b.endUT < b.startUT) b.endUT = b.startUT;
            UpdateRows();

            // Service records: every crew member who took part logs a battle; shot down and alive = survivor.
            var crews = new HashSet<string>();
            foreach (var c in combatants.Values)
                if (c.hits > 0 || c.taken > 0 || c.kills > 0 || !c.alive) foreach (var n in c.crew) crews.Add(n);
            var roster = HighLogic.CurrentGame?.CrewRoster;
            foreach (var n in crews)
            {
                var o = CombatOperators.Get(n);
                if (o == null) continue;
                o.Count("battles");
                if (shotDown.Contains(n) && roster?[n] != null && roster[n].rosterStatus != ProtoCrewMember.RosterStatus.Dead) o.Count("survived");
                CombatOperators.Review(o, killsThisBattle.TryGetValue(n, out int k) ? k : 0);
            }
            Note("Engagement over", false, 0);

            CombatOperators.AddBattle(b);
            string text = b.ToText();
            try
            {
                string dir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmoryAvionics/PluginData/AAR");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"AAR_{DateTime.Now:yyyyMMdd_HHmmss}.txt"), text);
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: couldn't write the AAR file: {e.Message}"); }
            Debug.Log($"[BDArmoryAvionics]: battle over, after-action report:\n{text}");

            if (AvionicsSettings.AARPopup && !leaving) { AfterActionWindow.Report = b; AfterActionWindow.Show = true; }
            Live = null;
            combatants.Clear();
        }

        // ---- In-flight AAR window ----

        void OnGUI()
        {
            if (!HighLogic.LoadedSceneIsFlight || !BDArmorySetup.GAME_UI_ENABLED || !AfterActionWindow.Show) return;
            AfterActionWindow.rect = AvionicsCore.DrawWindow(AfterActionWindow.WindowID, AfterActionWindow.rect, DrawAAR, "After-Action Report", BDArmorySetup.BDGuiSkin);
        }

        void DrawAAR(int id)
        {
            var r = AfterActionWindow.rect;
            if (GUI.Button(new Rect(r.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { AfterActionWindow.Show = false; return; }
            var b = Live ?? AfterActionWindow.Report ?? (CombatOperators.Instance != null && CombatOperators.Instance.battles.Count > 0 ? CombatOperators.Instance.battles[0] : null);
            if (b == null) AvionicsDraw.ColorLabel(new Rect(8, 26, r.width - 16, 16), "No engagement yet. The log starts at the first hit.", AvionicsDraw.Label, AvionicsDraw.ScopeText);
            else BattleReportView.Draw(new Rect(8, 24, r.width - 16, r.height - 30), b, b == Live, ref scroll);
            GUI.DragWindow(new Rect(0, 0, r.width - 22, 20));
        }
    }

    public static class AfterActionWindow
    {
        public const int WindowID = 71730332;
        public static bool Show;
        public static BattleReport Report;
        public static Rect rect = new(Screen.width / 2 - 330, 100, 660, 520);
    }

    /// <summary>Draws a battle report (in flight and in the Astronaut Complex panel).</summary>
    public static class BattleReportView
    {
        static readonly Color KillColor = AvionicsDraw.Friendly, LossColor = AvionicsDraw.Hostile, FFColor = AvionicsDraw.Unknown;

        public static void Draw(Rect area, BattleReport b, bool live, ref Vector2 scroll)
        {
            float x = area.x, y = area.y, w = area.width;
            double dur = live ? Planetarium.GetUniversalTime() - b.startUT : b.Duration;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 16), $"{b.place}   {KSPUtil.PrintTimeCompact(dur, false)}{(live ? "   (in progress)" : "")}", AvionicsDraw.Header, AvionicsDraw.HudText); y += 18;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"{KSPUtil.PrintDate(b.startUT, true)}   real time {b.realTime}", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 16;
            foreach (var t in b.Teams())
            {
                AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"Team {t.team}: {t.units} units, {t.kills} kills, {t.lost} lost ({t.mannedLost} manned)", AvionicsDraw.Label, AvionicsDraw.HudText);
                y += 15;
            }
            var mvp = b.Mvp;
            if (mvp != null) { AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"MVP: {mvp.name} ({mvp.crew}), {mvp.kills} kills", AvionicsDraw.Label, AvionicsDraw.LockBox); y += 15; }
            y += 4;

            float rowsH = 18 + b.rows.Count * 15, eventsH = 18 + b.events.Count * 15;
            var view = new Rect(x, y, w, area.yMax - y);
            scroll = GUI.BeginScrollView(view, scroll, new Rect(0, 0, w - 18, rowsH + eventsH + 10));
            float yy = 0;
            var s = AvionicsDraw.LabelSmall;
            AvionicsDraw.ColorLabel(new Rect(0, yy, 170, 14), "Unit", AvionicsDraw.Header, AvionicsDraw.ScopeText);
            AvionicsDraw.ColorLabel(new Rect(172, yy, 40, 14), "Team", AvionicsDraw.Header, AvionicsDraw.ScopeText);
            AvionicsDraw.ColorLabel(new Rect(214, yy, 130, 14), "Crew", AvionicsDraw.Header, AvionicsDraw.ScopeText);
            AvionicsDraw.ColorLabel(new Rect(346, yy, 60, 14), "K / hits", AvionicsDraw.Header, AvionicsDraw.ScopeText);
            AvionicsDraw.ColorLabel(new Rect(408, yy, 80, 14), "dealt/taken", AvionicsDraw.Header, AvionicsDraw.ScopeText);
            AvionicsDraw.ColorLabel(new Rect(490, yy, w - 508, 14), "Result", AvionicsDraw.Header, AvionicsDraw.ScopeText);
            yy += 18;
            foreach (var r in b.rows)
            {
                var col = r.alive ? AvionicsDraw.HudText : LossColor;
                AvionicsDraw.ColorLabel(new Rect(0, yy, 170, 14), Clip(r.name, 30), s, col);
                AvionicsDraw.ColorLabel(new Rect(172, yy, 40, 14), Clip(r.team, 6), s, col);
                AvionicsDraw.ColorLabel(new Rect(214, yy, 130, 14), Clip(r.manned ? r.crew : "uncrewed", 26), s, col);
                AvionicsDraw.ColorLabel(new Rect(346, yy, 60, 14), $"{r.kills} / {r.hits}", s, r.kills > 0 ? KillColor : col);
                AvionicsDraw.ColorLabel(new Rect(408, yy, 80, 14), $"{Short(r.dealt)}/{Short(r.taken)}", s, col);
                AvionicsDraw.ColorLabel(new Rect(490, yy, w - 508, 14), Clip(r.status, 40), s, col);
                yy += 15;
            }
            yy += 6;
            AvionicsDraw.ColorLabel(new Rect(0, yy, w, 14), "Timeline", AvionicsDraw.Header, AvionicsDraw.ScopeText); yy += 18;
            foreach (var e in b.events)
            {
                var col = e.kind == 1 ? KillColor : e.kind == 2 ? LossColor : e.kind == 3 ? FFColor : AvionicsDraw.ScopeText;
                AvionicsDraw.ColorLabel(new Rect(0, yy, w - 20, 14), $"[{BattleReport.Clock(e.t)}] {e.text}", s, col);
                yy += 15;
            }
            GUI.EndScrollView();
        }

        static string Clip(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length > n ? s.Substring(0, n - 1) + "…" : s;
        static string Short(float v) => v >= 10000 ? $"{v / 1000:F0}k" : v >= 1000 ? $"{v / 1000:F1}k" : $"{v:F0}";
    }

    // ---- Harmony: BDArmory reports every hit to its competition scoring, even outside competitions ----

    [HarmonyPatch]
    static class Patch_ApplyScore
    {
        static System.Reflection.MethodBase TargetMethod() => AccessTools.Method("BDArmory.Utils.ProjectileUtils:ApplyScore"); // internal class

        // (Part hitPart, string sourceVessel, double distanceTraveled, float damage, string name, ExplosionSourceType source, bool newhit)
        static void Prefix(Part __0, string __1, float __3, string __4, ExplosionSourceType __5, bool __6)
        {
            CombatLog.inApplyScore = true;
            if (__0 != null) CombatLog.Hit(__1, __0.vessel, Kind(__5), __3, __4, __6);
        }

        static Exception Finalizer(Exception __exception) { CombatLog.inApplyScore = false; return __exception; }

        internal static WeaponKind Kind(ExplosionSourceType s) => s switch
        {
            ExplosionSourceType.Bullet => WeaponKind.Gun,
            ExplosionSourceType.Rocket => WeaponKind.Rocket,
            ExplosionSourceType.Missile => WeaponKind.Missile,
            ExplosionSourceType.BattleDamage => WeaponKind.Fire,
            _ => WeaponKind.Other,
        };
    }

    // Nukes and a few effects score directly, without ApplyScore.
    [HarmonyPatch(typeof(CompetitionScores), nameof(CompetitionScores.RegisterMissileDamage))]
    static class Patch_RegisterMissileDamage
    {
        static void Prefix(string __0, string __1, float __2) { if (!CombatLog.inApplyScore) CombatLog.HitByName(__0, __1, WeaponKind.Missile, __2); }
    }

    [HarmonyPatch(typeof(CompetitionScores), nameof(CompetitionScores.RegisterRocketDamage))]
    static class Patch_RegisterRocketDamage
    {
        static void Prefix(string __0, string __1, float __2) { if (!CombatLog.inApplyScore) CombatLog.HitByName(__0, __1, WeaponKind.Rocket, __2); }
    }

    [HarmonyPatch(typeof(CompetitionScores), nameof(CompetitionScores.RegisterBattleDamage))]
    static class Patch_RegisterBattleDamage
    {
        static void Prefix(string __0, Vessel __1, float __2) { if (!CombatLog.inApplyScore) CombatLog.Hit(__0, __1, WeaponKind.Fire, __2, null, false); }
    }

    [HarmonyPatch(typeof(CompetitionScores), nameof(CompetitionScores.RegisterRam))]
    static class Patch_RegisterRam
    {
        static void Prefix(string __0, string __1) => CombatLog.HitByName(__0, __1, WeaponKind.Ram, 0);
    }
}
