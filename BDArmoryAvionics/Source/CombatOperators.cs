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

        public override void OnAwake() { Instance = this; }
        void OnDestroy() { if (Instance == this) Instance = null; }

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

        public override void OnSave(ConfigNode node)
        {
            foreach (var o in ops.Values) o.Save(node.AddNode("OPERATOR"));
        }

        public override void OnLoad(ConfigNode node)
        {
            ops.Clear();
            foreach (var n in node.GetNodes("OPERATOR"))
            {
                var o = Operator.Load(n);
                if (!string.IsNullOrEmpty(o.name)) ops[o.name] = o;
            }
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
        static readonly Dictionary<Vessel, (Vessel target, float time)> lastTargets = new();
        float nextApply, nextXp;

        void Start()
        {
            originals.Clear();
            lastTargets.Clear();
            GameEvents.onVesselWillDestroy.Add(OnVesselDestroyed);
            GameEvents.onVesselRecovered.Add(OnRecovered);
        }

        void OnDestroy()
        {
            GameEvents.onVesselWillDestroy.Remove(OnVesselDestroyed);
            GameEvents.onVesselRecovered.Remove(OnRecovered);
        }

        /// <summary>Best level in a discipline among the crew; uncrewed = baseline.</summary>
        public static int Skill(Vessel v, Discipline d)
        {
            if (v == null) return Operator.Baseline;
            var crew = v.GetVesselCrew();
            if (crew.Count == 0 || CombatOperators.Instance == null) return Operator.Baseline;
            int best = 0;
            foreach (var c in crew) { var o = CombatOperators.Get(c.name); if (o != null) best = Mathf.Max(best, o[d]); }
            return best;
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
            if (Time.time >= nextApply) { nextApply = Time.time + 2f; ApplyAll(); }
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

        void OnVesselDestroyed(Vessel dead)
        {
            if (dead == null || !AvionicsSettings.CombatOperators) return;
            foreach (var kv in lastTargets)
            {
                if (kv.Value.target != dead || Time.time - kv.Value.time > 20f || kv.Key == null) continue;
                var wm = kv.Key.ActiveController().WM;
                bool gun = wm != null && wm.selectedWeapon is ModuleWeapon;
                foreach (var c in kv.Key.GetVesselCrew())
                {
                    var o = CombatOperators.Get(c.name);
                    if (o == null) continue;
                    o.kills++;
                    Award(o, gun ? Discipline.Gunnery : Discipline.Weapons, 40f);
                    Award(o, Discipline.Pilot, 20f);
                }
                ScreenMessages.PostScreenMessage($"{kv.Key.GetName()}: kill credited ({dead.GetName()})", 3f, ScreenMessageStyle.UPPER_LEFT);
            }
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
            }
        }

        static void Award(Operator o, Discipline d, float xp)
        {
            if (o.AddXp(d, xp)) ScreenMessages.PostScreenMessage($"{o.name}: {d} level {o[d]} (combat experience)", 4f, ScreenMessageStyle.UPPER_CENTER);
        }
    }

    /// <summary>Combat Operators panel, shown beside KSP's Astronaut Complex.</summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class OperatorsWindow : MonoBehaviour
    {
        bool show;
        Rect rect = new(Screen.width - 520, 80, 480, 600);
        Vector2 scroll;
        string selected, message;

        void Start()
        {
            GameEvents.onGUIAstronautComplexSpawn.Add(OnOpen);
            GameEvents.onGUIAstronautComplexDespawn.Add(OnClose);
        }

        void OnDestroy()
        {
            GameEvents.onGUIAstronautComplexSpawn.Remove(OnOpen);
            GameEvents.onGUIAstronautComplexDespawn.Remove(OnClose);
        }

        void OnOpen() => show = AvionicsSettings.CombatOperators;
        void OnClose() => show = false;

        void OnGUI()
        {
            if (!show || CombatOperators.Instance == null) return;
            rect = GUI.Window(71730330, rect, Draw, "Combat Operators", BDArmorySetup.BDGuiSkin.window);
        }

        static readonly string[] Names = ["Pilot", "Gunnery", "Weapons", "Sensors/EW", "Command"];
        static readonly string[] Effects =
        [
            "steering, G tolerance, missile evasion",
            "gun spread",
            "radar scan/lock speed (missiles away sooner)",
            "target acquisition, countermeasure timing",
            "IADS reaction (crewed HQ / AWACS / EWR)",
        ];

        void Draw(int id)
        {
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            float w = rect.width - 16, y = 22;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), "Level 2 = stock BDArmory. Train or fight to improve. Drones fly at level 2.", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
            y += 18;

            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) { GUI.DragWindow(); return; }
            var list = new List<ProtoCrewMember>();
            foreach (var pcm in roster.Crew) if (pcm.rosterStatus != ProtoCrewMember.RosterStatus.Dead && pcm.rosterStatus != ProtoCrewMember.RosterStatus.Missing) list.Add(pcm);

            float listH = 230;
            scroll = GUI.BeginScrollView(new Rect(8, y, w, listH), scroll, new Rect(0, 0, w - 18, list.Count * 22));
            for (int i = 0; i < list.Count; i++)
            {
                var pcm = list[i];
                var o = CombatOperators.Get(pcm.name);
                string status = o.training >= 0 ? $"training {Names[o.training]}" : pcm.rosterStatus == ProtoCrewMember.RosterStatus.Assigned ? "on a craft" : "ready";
                if (GUI.Button(new Rect(0, i * 22, w - 20, 20), $"{pcm.name}  {o.Short}  · {status}", selected == pcm.name ? sel : btn)) selected = pcm.name;
            }
            GUI.EndScrollView();
            y += listH + 8;

            var cur = selected != null ? roster[selected] : null;
            if (cur != null)
            {
                var o = CombatOperators.Get(cur.name);
                AvionicsDraw.ColorLabel(new Rect(8, y, w, 18), $"{cur.name} ({cur.trait})  courage {cur.courage:P0}  stupidity {cur.stupidity:P0}", AvionicsDraw.Header, AvionicsDraw.HudText); y += 20;
                AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), $"Sorties {o.sorties} · kills {o.kills}", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 18;
                if (o.training >= 0)
                {
                    double left = o.trainingEndUT - Planetarium.GetUniversalTime();
                    AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), $"Training {Names[o.training]}: {KSPUtil.PrintTimeCompact(Math.Max(0, left), false)} left", AvionicsDraw.Label, AvionicsDraw.Friendly);
                    y += 20;
                }
                for (int i = 0; i < 5; i++)
                {
                    var d = (Discipline)i;
                    int lvl = o[d];
                    string xpText = lvl >= Operator.MaxLevel ? "max" : $"{o.xp[i]:F0}/{Operator.XpForNext(lvl):F0} xp";
                    AvionicsDraw.ColorLabel(new Rect(8, y, 200, 16), $"{Names[i]}  {lvl}/5  ({xpText})", AvionicsDraw.Label, lvl > Operator.Baseline ? AvionicsDraw.Friendly : lvl < Operator.Baseline ? AvionicsDraw.Unknown : AvionicsDraw.HudText);
                    AvionicsDraw.ColorLabel(new Rect(8, y + 15, 260, 14), Effects[i], AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
                    bool can = o.training < 0 && lvl < Operator.MaxLevel;
                    GUI.enabled = can;
                    string cost = CombatOperators.CareerFunds ? $", {CombatOperators.TrainingCost(o, d) / 1000f:F0}k" : "";
                    if (GUI.Button(new Rect(w - 190, y + 2, 190, 22), $"Train ({CombatOperators.TrainingDays(o, d, cur):F0} d{cost})", btn))
                        message = CombatOperators.StartTraining(cur, d) ?? $"{cur.name} started {Names[i]} training";
                    GUI.enabled = true;
                    y += 34;
                }
            }
            if (!string.IsNullOrEmpty(message)) AvionicsDraw.ColorLabel(new Rect(8, rect.height - 22, w, 16), message, AvionicsDraw.Label, AvionicsDraw.Unknown);
            GUI.DragWindow(new Rect(0, 0, rect.width, 20));
        }
    }
}
