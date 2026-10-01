using System;
using System.Collections.Generic;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons;

namespace BDArmory.Avionics
{
    public enum Discipline { Pilot, Gunnery, Weapons, Sensors, Command }

    /// <summary>One kerbal's combat qualifications.</summary>
    public class Operator
    {
        public const int MaxLevel = 5, Baseline = 2; // level 2 = stock BDArmory behaviour
        public string name;
        public readonly int[] level = new int[5];
        public readonly float[] xp = new float[5];
        public int sorties, kills;
        public int training = -1;      // discipline being trained, -1 = none
        public double trainingEndUT;
        public int rank;               // highest rank reached (never demoted)
        public string squadron = "";
        public bool kia;
        public readonly Dictionary<string, int> stats = new();   // combat record: kill.A-A, kill.manned, kill.Gun, losses, battles...
        public readonly HashSet<string> medals = new();

        public int Stat(string key) => stats.TryGetValue(key, out int v) ? v : 0;
        public void Count(string key, int n = 1) => stats[key] = Stat(key) + n;
        public int LevelSum { get { int s = 0; foreach (var l in level) s += l; return s; } }

        public int this[Discipline d] => level[(int)d];
        public static float XpForNext(int lvl) => 100f * (lvl + 1);

        /// <summary>Add combat XP; levels up when enough is earned (combat experience alone can take a kerbal to the top).</summary>
        public bool AddXp(Discipline d, float amount)
        {
            int i = (int)d;
            if (level[i] >= MaxLevel) return false;
            xp[i] += amount;
            bool up = false;
            while (level[i] < MaxLevel && xp[i] >= XpForNext(level[i])) { xp[i] -= XpForNext(level[i]); level[i]++; up = true; }
            return up;
        }

        public string Short => $"P{level[0]} G{level[1]} W{level[2]} S{level[3]} C{level[4]}";

        public void Save(ConfigNode n)
        {
            n.AddValue("name", name);
            n.AddValue("level", string.Join(",", level));
            n.AddValue("xp", string.Join(",", Array.ConvertAll(xp, x => x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
            n.AddValue("sorties", sorties);
            n.AddValue("kills", kills);
            n.AddValue("training", training);
            n.AddValue("trainingEndUT", trainingEndUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            n.AddValue("rank", rank);
            n.AddValue("squadron", squadron ?? "");
            n.AddValue("kia", kia);
            var st = new List<string>(); foreach (var kv in stats) st.Add($"{kv.Key}:{kv.Value}");
            n.AddValue("stats", string.Join(";", st));
            n.AddValue("medals", string.Join(";", medals));
        }

        public static Operator Load(ConfigNode n)
        {
            var o = new Operator { name = n.GetValue("name") };
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var lv = (n.GetValue("level") ?? "").Split(',');
            var xs = (n.GetValue("xp") ?? "").Split(',');
            for (int i = 0; i < 5; i++)
            {
                if (i < lv.Length && int.TryParse(lv[i], out int l)) o.level[i] = Mathf.Clamp(l, 0, MaxLevel);
                if (i < xs.Length && float.TryParse(xs[i], System.Globalization.NumberStyles.Float, ci, out float x)) o.xp[i] = x;
            }
            int.TryParse(n.GetValue("sorties"), out o.sorties);
            int.TryParse(n.GetValue("kills"), out o.kills);
            if (!int.TryParse(n.GetValue("training"), out o.training)) o.training = -1;
            double.TryParse(n.GetValue("trainingEndUT"), System.Globalization.NumberStyles.Float, ci, out o.trainingEndUT);
            int.TryParse(n.GetValue("rank"), out o.rank);
            o.squadron = n.GetValue("squadron") ?? "";
            bool.TryParse(n.GetValue("kia"), out o.kia);
            foreach (var kv in (n.GetValue("stats") ?? "").Split(';'))
            {
                int c = kv.LastIndexOf(':');
                if (c > 0 && int.TryParse(kv.Substring(c + 1), out int v)) o.stats[kv.Substring(0, c)] = v;
            }
            foreach (var m in (n.GetValue("medals") ?? "").Split(';')) if (m.Length > 0) o.medals.Add(m);
            return o;
        }
    }

    /// <summary>The save game's roster of combat operators, kept with the save file.</summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.EDITOR, GameScenes.TRACKSTATION)]
    public class CombatOperators : ScenarioModule
    {
        public static CombatOperators Instance;
        readonly Dictionary<string, Operator> ops = new();
        float nextCheck;

        public override void OnAwake()
        {
            Instance = this;
            GameEvents.onKerbalStatusChange.Add(OnKerbalStatus);
        }
        void OnDestroy()
        {
            GameEvents.onKerbalStatusChange.Remove(OnKerbalStatus);
            if (Instance == this) Instance = null;
        }

        public static Operator Get(string kerbal)
        {
            if (Instance == null || string.IsNullOrEmpty(kerbal)) return null;
            if (!Instance.ops.TryGetValue(kerbal, out var o))
            {
                o = new Operator { name = kerbal };
                // New kerbals start green; veterans (the original four) start at the stock baseline.
                var pcm = HighLogic.CurrentGame?.CrewRoster?[kerbal];
                int start = pcm != null && pcm.veteran ? Operator.Baseline : 1;
                for (int i = 0; i < 5; i++) o.level[i] = start;
                Instance.ops[kerbal] = o;
            }
            return o;
        }

        public static IEnumerable<Operator> All => Instance != null ? Instance.ops.Values : (IEnumerable<Operator>)Array.Empty<Operator>();

        // ---- Squadrons and battle reports ----

        public readonly List<Squadron> squadrons = new();
        public readonly List<BattleReport> battles = new();   // newest first
        public const int MaxBattles = 25;

        public static Squadron FindSquadron(string name)
        {
            if (Instance == null || string.IsNullOrEmpty(name)) return null;
            foreach (var s in Instance.squadrons) if (s.name == name) return s;
            return null;
        }

        public static IEnumerable<Operator> Members(Squadron s)
        {
            foreach (var o in All) if (o.squadron == s.name) yield return o;
        }

        /// <summary>The squadron's commander: the one assigned, else its highest ranked living member.</summary>
        public static string Commander(Squadron s)
        {
            Operator best = null;
            foreach (var o in Members(s))
            {
                if (o.kia) continue;
                if (o.name == s.commander) return o.name;
                if (best == null || Ranks.Merit(o) > Ranks.Merit(best)) best = o;
            }
            return best?.name;
        }

        public static void AddBattle(BattleReport b)
        {
            if (Instance == null || b == null) return;
            Instance.battles.Insert(0, b);
            while (Instance.battles.Count > MaxBattles) Instance.battles.RemoveAt(Instance.battles.Count - 1);
        }

        /// <summary>Check for promotion and medals after a kerbal's record changed.</summary>
        public static void Review(Operator o, int killsThisBattle = 0)
        {
            if (o == null) return;
            int r = Ranks.Index(o);
            if (r > o.rank)
            {
                o.rank = r;
                ScreenMessages.PostScreenMessage($"{o.name} promoted to {Ranks.Names[r]}", 5f, ScreenMessageStyle.UPPER_CENTER);
                Debug.Log($"[BDArmoryAvionics]: {o.name} promoted to {Ranks.Names[r]}");
            }
            foreach (var m in Medals.Earned(o, killsThisBattle))
                if (o.medals.Add(m))
                {
                    ScreenMessages.PostScreenMessage($"{o.name} awarded: {m}", 5f, ScreenMessageStyle.UPPER_CENTER);
                    Debug.Log($"[BDArmoryAvionics]: {o.name} awarded {m}");
                }
        }

        public override void OnSave(ConfigNode node)
        {
            foreach (var o in ops.Values) o.Save(node.AddNode("OPERATOR"));
            foreach (var s in squadrons) s.Save(node.AddNode("SQUADRON"));
            foreach (var b in battles) b.Save(node.AddNode("BATTLE"));
        }

        public override void OnLoad(ConfigNode node)
        {
            ops.Clear(); squadrons.Clear(); battles.Clear();
            foreach (var n in node.GetNodes("OPERATOR"))
            {
                var o = Operator.Load(n);
                if (!string.IsNullOrEmpty(o.name)) ops[o.name] = o;
            }
            foreach (var n in node.GetNodes("SQUADRON")) { var s = Squadron.Load(n); if (!string.IsNullOrEmpty(s.name)) squadrons.Add(s); }
            foreach (var n in node.GetNodes("BATTLE")) battles.Add(BattleReport.Load(n));
        }

        void OnKerbalStatus(ProtoCrewMember pcm, ProtoCrewMember.RosterStatus from, ProtoCrewMember.RosterStatus to)
        {
            if (pcm == null || to != ProtoCrewMember.RosterStatus.Dead || !ops.TryGetValue(pcm.name, out var o) || o.kia) return;
            o.kia = true;
            o.Count("kia");
            CombatLog.Note($"KIA: {Ranks.Short(o)} {pcm.name}", true);
            Debug.Log($"[BDArmoryAvionics]: {pcm.name} killed in action");
        }

        // ---- Training ----

        public static double DaySeconds => KSPUtil.dateTimeFormatter != null ? KSPUtil.dateTimeFormatter.Day : 21600;

        public static double TrainingDays(Operator o, Discipline d, ProtoCrewMember pcm)
        {
            float slow = pcm != null ? 1f + pcm.stupidity * 0.6f : 1f;      // dimmer kerbals take longer
            return (o[d] + 1) * 3.0 * slow;
        }

        public static float TrainingCost(Operator o, Discipline d) => 15000f * (o[d] + 1);

        public static bool CareerFunds => HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode == Game.Modes.CAREER && Funding.Instance != null;

        public static string StartTraining(ProtoCrewMember pcm, Discipline d)
        {
            var o = Get(pcm.name);
            if (o == null) return "No roster";
            if (o.training >= 0) return $"{pcm.name} is already training";
            if (o[d] >= Operator.MaxLevel) return $"{pcm.name} is already at the top level";
            if (pcm.rosterStatus != ProtoCrewMember.RosterStatus.Available || pcm.inactive) return $"{pcm.name} isn't available";
            float cost = TrainingCost(o, d);
            if (CareerFunds)
            {
                if (Funding.Instance.Funds < cost) return $"Not enough funds ({cost:N0} needed)";
                Funding.Instance.AddFunds(-cost, TransactionReasons.CrewRecruited);
            }
            double seconds = TrainingDays(o, d, pcm) * DaySeconds;
            o.training = (int)d;
            o.trainingEndUT = Planetarium.GetUniversalTime() + seconds;
            pcm.SetInactive(seconds, false); // KSP's own "unavailable until" state: can't be put on a craft while training
            return null;
        }

        void Update()
        {
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + 1f;
            double ut = Planetarium.GetUniversalTime();
            foreach (var o in ops.Values)
            {
                if (o.training < 0 || ut < o.trainingEndUT) continue;
                var d = (Discipline)o.training;
                o.level[o.training] = Mathf.Min(o.level[o.training] + 1, Operator.MaxLevel);
                o.xp[o.training] = 0;
                o.training = -1;
                ScreenMessages.PostScreenMessage($"{o.name} completed {d} training: level {o[d]}", 5f, ScreenMessageStyle.UPPER_CENTER);
                Review(o);
            }
        }
    }

    /// <summary>
    /// In flight: the best qualified crew aboard each combat craft set how well its guard mode, weapons, sensors and pilot AI perform.
    /// Level 2 is stock BDArmory; uncrewed craft (drones) count as level 2. Also awards combat XP.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class OperatorEffects : MonoBehaviour
    {
        static readonly Dictionary<PartModule, Dictionary<string, float>> originals = new();
        internal static readonly Dictionary<Vessel, (Vessel target, float time)> lastTargets = new();
        static readonly HashSet<string> ledSquadrons = new(); // squadrons whose commander is flying a combat craft here
        float nextApply, nextXp;

        void Start()
        {
            originals.Clear();
            lastTargets.Clear();
            ledSquadrons.Clear();
            GameEvents.onVesselRecovered.Add(OnRecovered);
        }

        void OnDestroy()
        {
            GameEvents.onVesselRecovered.Remove(OnRecovered);
        }

        /// <summary>Best level in a discipline among the crew; uncrewed = baseline.
        /// A squadron flies one level better (except Command) while its commander is flying a combat craft in the same battle.</summary>
        public static int Skill(Vessel v, Discipline d)
        {
            if (v == null) return Operator.Baseline;
            var crew = v.GetVesselCrew();
            if (crew.Count == 0 || CombatOperators.Instance == null) return Operator.Baseline;
            int best = 0;
            foreach (var c in crew)
            {
                var o = CombatOperators.Get(c.name);
                if (o == null) continue;
                int lvl = o[d];
                if (d != Discipline.Command && LedBonus(o)) lvl = Mathf.Min(lvl + 1, Operator.MaxLevel);
                best = Mathf.Max(best, lvl);
            }
            return best;
        }

        public static bool LedBonus(Operator o) => !string.IsNullOrEmpty(o.squadron) && ledSquadrons.Contains(o.squadron);

        static void UpdateLeadership()
        {
            ledSquadrons.Clear();
            if (CombatOperators.Instance == null) return;
            foreach (var s in CombatOperators.Instance.squadrons)
            {
                string cmd = CombatOperators.Commander(s);
                if (cmd == null) continue;
                foreach (var v in FlightGlobals.VesselsLoaded)
                {
                    if (v == null || v.packed || v.ActiveController().WM == null) continue;
                    bool aboard = false;
                    foreach (var c in v.GetVesselCrew()) if (c.name == cmd) { aboard = true; break; }
                    if (aboard) { ledSquadrons.Add(s.name); break; }
                }
            }
        }

        public static string CrewSummary(Vessel v)
        {
            if (v == null || CombatOperators.Instance == null) return "";
            var crew = v.GetVesselCrew();
            if (crew.Count == 0) return "uncrewed (stock skill)";
            var o = CombatOperators.Get(crew[0].name);
            return crew.Count == 1 ? $"{crew[0].name.Split(' ')[0]} {o?.Short}" : $"{crew.Count} crew, best P{Skill(v, Discipline.Pilot)} G{Skill(v, Discipline.Gunnery)} W{Skill(v, Discipline.Weapons)} S{Skill(v, Discipline.Sensors)} C{Skill(v, Discipline.Command)}";
        }

        static float Step(int lvl, float perLevel) => 1f + (lvl - Operator.Baseline) * perLevel;

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || !AvionicsSettings.CombatOperators || CombatOperators.Instance == null) return;
            if (Time.time >= nextApply) { nextApply = Time.time + 2f; UpdateLeadership(); ApplyAll(); }
            if (Time.time >= nextXp) { nextXp = Time.time + 10f; CombatXp(); }
        }

        static void ApplyAll()
        {
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed) continue;
                var wm = v.ActiveController().WM;
                if (wm == null) continue;
                int pilot = Skill(v, Discipline.Pilot), gun = Skill(v, Discipline.Gunnery), weap = Skill(v, Discipline.Weapons), sens = Skill(v, Discipline.Sensors);

                // Sensors / EW: faster target acquisition, earlier and denser countermeasures.
                Set(wm, nameof(MissileFire.targetScanInterval), o => Mathf.Max(0.1f, o * Step(sens, -0.12f)));
                Set(wm, nameof(MissileFire.cmThreshold), o => o * Step(sens, 0.10f));
                Set(wm, nameof(MissileFire.cmInterval), o => Mathf.Max(0.05f, o * Step(sens, -0.08f)));

                // Gunnery: tighter spread.
                foreach (var w in VesselModuleRegistry.GetModules<ModuleWeapon>(v))
                    if (w != null) Set(w, nameof(ModuleWeapon.maxDeviation), o => Mathf.Max(o * 0.3f, o * Step(gun, -0.12f)));

                // Weapons: radar scans and locks faster, so missiles get away sooner.
                foreach (var r in VesselModuleRegistry.GetModules<ModuleRadar>(v))
                    if (r != null)
                    {
                        Set(r, nameof(ModuleRadar.scanRotationSpeed), o => o * Step(weap, 0.10f));
                        Set(r, nameof(ModuleRadar.lockRotationSpeed), o => o * Step(weap, 0.10f));
                    }

                // Pilot: sharper steering, more G tolerance, earlier missile evasion.
                var ai = v.ActiveController().PilotAI;
                if (ai != null)
                {
                    Set(ai, nameof(BDModulePilotAI.steerMult), o => o * Step(pilot, 0.06f));
                    Set(ai, nameof(BDModulePilotAI.maxAllowedGForce), o => o * Step(pilot, 0.05f));
                    Set(ai, nameof(BDModulePilotAI.evasionThreshold), o => o * Step(pilot, 0.10f));
                }
            }
        }

        /// <summary>Set a float field to f(original), remembering the craft's own value the first time.</summary>
        static void Set(PartModule m, string field, Func<float, float> f)
        {
            var fi = m.GetType().GetField(field);
            if (fi == null || fi.FieldType != typeof(float)) return;
            if (!originals.TryGetValue(m, out var d)) originals[m] = d = new Dictionary<string, float>();
            if (!d.TryGetValue(field, out float orig)) d[field] = orig = (float)fi.GetValue(m);
            fi.SetValue(m, f(orig));
        }

        /// <summary>Command: IADS reaction-time multiplier for a team (best Command level aboard a crewed HQ/AEW/EWR node).</summary>
        public static float CommandReaction(Competition.BDTeam team)
        {
            if (!AvionicsSettings.CombatOperators) return 1f;
            var net = DataLinkNetwork.For(team);
            if (net == null) return 1f;
            int best = Operator.Baseline;
            bool any = false;
            foreach (var n in net.nodes)
                if (n.connected && (n.role == NodeRole.HQ || n.role == NodeRole.AEW || n.role == NodeRole.EWR) && n.vessel.GetCrewCount() > 0)
                { int c = Skill(n.vessel, Discipline.Command); best = any ? Mathf.Max(best, c) : c; any = true; }
            return Mathf.Clamp(Step(best, -0.12f), 0.4f, 1.5f);
        }

        // ---- Combat XP ----

        static void CombatXp()
        {
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed || v.GetCrewCount() == 0) continue;
                var wm = v.ActiveController().WM;
                if (wm == null || !wm.guardMode || wm.currentTarget == null || wm.currentTarget.Vessel == null) continue;
                lastTargets[v] = (wm.currentTarget.Vessel, Time.time);
                foreach (var c in v.GetVesselCrew())
                {
                    var o = CombatOperators.Get(c.name);
                    if (o == null) continue;
                    float courage = 0.75f + c.courage * 0.5f;
                    Award(o, Discipline.Sensors, 2f * courage);
                    if (v.ActiveController().PilotAI != null && !v.LandedOrSplashed) Award(o, Discipline.Pilot, 3f * courage);
                    if (wm.selectedWeapon != null) Award(o, wm.selectedWeapon is ModuleWeapon ? Discipline.Gunnery : Discipline.Weapons, 2f * courage);
                }
            }
        }

        /// <summary>Kill XP for the crew of the craft credited by the combat log.</summary>
        public static void CreditKillXp(Operator o, bool gun)
        {
            if (!AvionicsSettings.CombatOperators) return;
            Award(o, gun ? Discipline.Gunnery : Discipline.Weapons, 40f);
            Award(o, Discipline.Pilot, 20f);
        }

        void OnRecovered(ProtoVessel pv, bool quick)
        {
            if (pv == null || !AvionicsSettings.CombatOperators) return;
            foreach (var c in pv.GetVesselCrew())
            {
                var o = CombatOperators.Get(c.name);
                if (o == null) continue;
                o.sorties++;
                for (int i = 0; i < 5; i++) Award(o, (Discipline)i, 10f);
                CombatOperators.Review(o);
            }
        }

        static void Award(Operator o, Discipline d, float xp)
        {
            if (o.AddXp(d, xp)) ScreenMessages.PostScreenMessage($"{o.name}: {d} level {o[d]} (combat experience)", 4f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
