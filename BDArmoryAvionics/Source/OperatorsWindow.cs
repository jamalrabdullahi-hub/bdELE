using System;
using System.Collections.Generic;
using UnityEngine;

using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>Combat Operators panel, shown beside KSP's Astronaut Complex: roster and training, squadrons, service records, battle reports.</summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class OperatorsWindow : MonoBehaviour
    {
        bool show;
        Rect rect = new(Screen.width - 640, 60, 600, 680);
        Vector2 scroll, scroll2, reportScroll;
        string selected, selectedSquadron, message, newSquadron = "";
        int tab, selectedBattle = -1;
        static readonly string[] Tabs = ["Roster", "Squadrons", "Records", "Battles"];

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

        static GUIStyle Btn => BDArmorySetup.ButtonStyle;
        static GUIStyle Sel => BDArmorySetup.SelectedButtonStyle;
        static void L(float x, float y, float w, string t, Color c, GUIStyle s = null) => AvionicsDraw.ColorLabel(new Rect(x, y, w, 16), t, s ?? AvionicsDraw.Label, c);

        void Draw(int id)
        {
            float w = rect.width - 16;
            float tw = (w - 3 * 4) / 4;
            for (int i = 0; i < Tabs.Length; i++)
                if (GUI.Button(new Rect(8 + i * (tw + 4), 22, tw, 22), Tabs[i], tab == i ? Sel : Btn)) { tab = i; message = null; }
            var area = new Rect(8, 50, w, rect.height - 76);
            switch (tab)
            {
                case 0: DrawRoster(area); break;
                case 1: DrawSquadrons(area); break;
                case 2: DrawRecords(area); break;
                case 3: DrawBattles(area); break;
            }
            if (!string.IsNullOrEmpty(message)) L(8, rect.height - 22, w, message, AvionicsDraw.Unknown);
            GUI.DragWindow(new Rect(0, 0, rect.width, 20));
        }

        static List<ProtoCrewMember> Living()
        {
            var list = new List<ProtoCrewMember>();
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return list;
            foreach (var pcm in roster.Crew)
                if (pcm.rosterStatus != ProtoCrewMember.RosterStatus.Dead && pcm.rosterStatus != ProtoCrewMember.RosterStatus.Missing) list.Add(pcm);
            return list;
        }

        // ---- Roster ----

        void DrawRoster(Rect a)
        {
            float x = a.x, y = a.y, w = a.width;
            L(x, y, w, "Level 2 = stock BDArmory. Train or fight to improve. Drones fly at level 2.", AvionicsDraw.ScopeText, AvionicsDraw.LabelSmall);
            y += 16;
            var list = Living();
            float listH = 170;
            scroll = GUI.BeginScrollView(new Rect(x, y, w, listH), scroll, new Rect(0, 0, w - 18, list.Count * 22));
            for (int i = 0; i < list.Count; i++)
            {
                var pcm = list[i];
                var o = CombatOperators.Get(pcm.name);
                string status = o.training >= 0 ? $"training {Names[o.training]}" : pcm.rosterStatus == ProtoCrewMember.RosterStatus.Assigned ? "on a craft" : "ready";
                string sq = string.IsNullOrEmpty(o.squadron) ? "" : $" [{o.squadron}]";
                if (GUI.Button(new Rect(0, i * 22, w - 20, 20), $"{Ranks.Short(o)} {pcm.name}{sq}  {o.Short}  K{o.kills} · {status}", selected == pcm.name ? Sel : Btn)) selected = pcm.name;
            }
            GUI.EndScrollView();
            y += listH + 6;

            var cur = selected != null ? HighLogic.CurrentGame?.CrewRoster?[selected] : null;
            if (cur == null) { L(x, y, w, "Select a kerbal.", AvionicsDraw.ScopeText); return; }
            var op = CombatOperators.Get(cur.name);

            L(x, y, w, $"{Ranks.Long(op)} {cur.name} ({cur.trait})   courage {cur.courage:P0}  stupidity {cur.stupidity:P0}", AvionicsDraw.HudText, AvionicsDraw.Header); y += 18;
            int next = Ranks.NextMerit(op);
            L(x, y, w, $"Merit {Ranks.Merit(op)}{(next > 0 ? $" (next rank at {next})" : " (top rank)")}", AvionicsDraw.ScopeText, AvionicsDraw.LabelSmall);

            // Squadron assignment: cycle through the squadrons
            var squads = CombatOperators.Instance.squadrons;
            string sqLabel = string.IsNullOrEmpty(op.squadron) ? "Squadron: none  >" : $"Squadron: {op.squadron}  >";
            if (GUI.Button(new Rect(x + w - 220, y - 2, 220, 20), sqLabel, Btn))
            {
                if (squads.Count == 0) message = "Create a squadron in the Squadrons tab first";
                else
                {
                    int idx = squads.FindIndex(s => s.name == op.squadron);
                    op.squadron = idx + 1 < squads.Count ? squads[idx + 1].name : "";
                }
            }
            y += 20;
            y = DrawRecord(op, x, y, w);
            y += 4;

            if (op.training >= 0)
            {
                double left = op.trainingEndUT - Planetarium.GetUniversalTime();
                L(x, y, w, $"Training {Names[op.training]}: {KSPUtil.PrintTimeCompact(Math.Max(0, left), false)} left", AvionicsDraw.Friendly);
                y += 18;
            }
            for (int i = 0; i < 5; i++)
            {
                var d = (Discipline)i;
                int lvl = op[d];
                string xpText = lvl >= Operator.MaxLevel ? "max" : $"{op.xp[i]:F0}/{Operator.XpForNext(lvl):F0} xp";
                L(x, y, 220, $"{Names[i]}  {lvl}/5  ({xpText})", lvl > Operator.Baseline ? AvionicsDraw.Friendly : lvl < Operator.Baseline ? AvionicsDraw.Unknown : AvionicsDraw.HudText);
                L(x + 220, y, 200, Effects[i], AvionicsDraw.ScopeText, AvionicsDraw.LabelSmall);
                GUI.enabled = op.training < 0 && lvl < Operator.MaxLevel;
                string cost = CombatOperators.CareerFunds ? $", {CombatOperators.TrainingCost(op, d) / 1000f:F0}k" : "";
                if (GUI.Button(new Rect(x + w - 150, y - 2, 150, 20), $"Train ({CombatOperators.TrainingDays(op, d, cur):F0} d{cost})", Btn))
                    message = CombatOperators.StartTraining(cur, d) ?? $"{cur.name} started {Names[i]} training";
                GUI.enabled = true;
                y += 22;
            }
        }

        /// <summary>Service record block: kills by engagement type, target and weapon; battles, losses, medals.</summary>
        static float DrawRecord(Operator o, float x, float y, float w)
        {
            var s = AvionicsDraw.LabelSmall;
            int surface = o.Stat("kill.A-G") + o.Stat("kill.A-S");
            L(x, y, w, $"Kills {o.kills}:  A-A {o.Stat("kill.A-A")} · A-G {surface} · G-A {o.Stat("kill.G-A") + o.Stat("kill.S-A")} · G-G {o.Stat("kill.G-G") + o.Stat("kill.G-S") + o.Stat("kill.S-G") + o.Stat("kill.S-S")} · space {o.Stat("kill.vs.Space")}", AvionicsDraw.HudText); y += 16;
            L(x, y, w, $"Targets: manned {o.Stat("kill.manned")} · unmanned {o.Stat("kill.unmanned")}     Weapons: gun {o.Stat("kill.Gun")} · missile {o.Stat("kill.Missile")} · rocket {o.Stat("kill.Rocket")} · ram {o.Stat("kill.Ram")} · manoeuvre {o.Stat("kill.Manoeuvre")}", AvionicsDraw.ScopeText, s); y += 14;
            L(x, y, w, $"Battles {o.Stat("battles")} · sorties {o.sorties} · shot down {o.Stat("losses")} · survived {o.Stat("survived")} · friendly fire {o.Stat("ff")}{(o.kia ? " · KILLED IN ACTION" : "")}", o.kia ? AvionicsDraw.Hostile : AvionicsDraw.ScopeText, s); y += 14;
            L(x, y, w, o.medals.Count == 0 ? "Medals: none yet" : $"Medals: {string.Join(", ", o.medals)}", o.medals.Count > 0 ? AvionicsDraw.LockBox : AvionicsDraw.ScopeText, s); y += 14;
            return y;
        }

        // ---- Squadrons ----

        void DrawSquadrons(Rect a)
        {
            float x = a.x, y = a.y, w = a.width;
            var sc = CombatOperators.Instance;
            newSquadron = GUI.TextField(new Rect(x, y, w - 130, 20), newSquadron ?? "", 32);
            if (GUI.Button(new Rect(x + w - 124, y, 124, 20), "Create squadron", Btn))
            {
                string n = newSquadron.Trim().Replace(";", "").Replace(":", "");
                if (n.Length == 0) message = "Type a squadron name first";
                else if (CombatOperators.FindSquadron(n) != null) message = $"{n} already exists";
                else { sc.squadrons.Add(new Squadron { name = n }); selectedSquadron = n; newSquadron = ""; message = $"Squadron {n} formed"; }
            }
            y += 26;

            float listH = 130;
            scroll = GUI.BeginScrollView(new Rect(x, y, w, listH), scroll, new Rect(0, 0, w - 18, sc.squadrons.Count * 22));
            for (int i = 0; i < sc.squadrons.Count; i++)
            {
                var sq = sc.squadrons[i];
                int members = 0, kills = 0;
                foreach (var o in CombatOperators.Members(sq)) { members++; kills += o.kills; }
                if (GUI.Button(new Rect(0, i * 22, w - 20, 20), $"{sq.name}   {members} operators · {kills} kills · CO {CombatOperators.Commander(sq) ?? "-"}", selectedSquadron == sq.name ? Sel : Btn))
                    selectedSquadron = sq.name;
            }
            GUI.EndScrollView();
            y += listH + 6;

            var s = CombatOperators.FindSquadron(selectedSquadron);
            if (s == null) { L(x, y, w, "Create or select a squadron. Assign kerbals here or from the Roster tab.", AvionicsDraw.ScopeText); return; }

            var mem = new List<Operator>(CombatOperators.Members(s));
            mem.Sort((p, q) => Ranks.Merit(q).CompareTo(Ranks.Merit(p)));
            int k = 0, aa = 0, ag = 0, man = 0, unman = 0, lost = 0, kia = 0, battles = 0;
            foreach (var o in mem)
            {
                k += o.kills; aa += o.Stat("kill.A-A"); ag += o.Stat("kill.A-G") + o.Stat("kill.A-S");
                man += o.Stat("kill.manned"); unman += o.Stat("kill.unmanned"); lost += o.Stat("losses"); kia += o.kia ? 1 : 0; battles = Math.Max(battles, o.Stat("battles"));
            }
            string cmd = CombatOperators.Commander(s);
            L(x, y, w, $"{s.name}   commander: {(cmd == null ? "none" : $"{Ranks.Short(CombatOperators.Get(cmd))} {cmd}")}{(string.IsNullOrEmpty(s.commander) && cmd != null ? " (senior member)" : "")}", AvionicsDraw.HudText, AvionicsDraw.Header); y += 18;
            L(x, y, w, $"Kills {k}: A-A {aa} · A-G {ag} · manned {man} · unmanned {unman}    shot down {lost} · KIA {kia}", AvionicsDraw.ScopeText, AvionicsDraw.LabelSmall); y += 14;
            L(x, y, w, "With its commander flying a combat craft in the same battle, the squadron fights one level better (not Command).", AvionicsDraw.ScopeText, AvionicsDraw.LabelSmall); y += 18;

            var cur = selected != null ? CombatOperators.Get(selected) : null;
            GUI.enabled = cur != null && cur.squadron != s.name && !cur.kia;
            if (GUI.Button(new Rect(x, y, 240, 20), cur != null ? $"Add {selected.Split(' ')[0]} (roster selection)" : "Add (select a kerbal in Roster)", Btn)) { cur.squadron = s.name; message = $"{selected} joined {s.name}"; }
            GUI.enabled = true;
            if (GUI.Button(new Rect(x + w - 120, y, 120, 20), "Disband", Btn))
            {
                foreach (var o in mem) o.squadron = "";
                sc.squadrons.Remove(s);
                message = $"{s.name} disbanded";
                selectedSquadron = null;
                return;
            }
            y += 26;

            scroll2 = GUI.BeginScrollView(new Rect(x, y, w, a.yMax - y), scroll2, new Rect(0, 0, w - 18, mem.Count * 22));
            for (int i = 0; i < mem.Count; i++)
            {
                var o = mem[i];
                float yy = i * 22;
                bool isCO = o.name == cmd;
                L(0, yy + 2, w - 260, $"{(isCO ? "CO " : "")}{Ranks.Short(o)} {o.name}  {o.Short}  K{o.kills} (A-A {o.Stat("kill.A-A")}){(o.kia ? "  KIA" : "")}", o.kia ? AvionicsDraw.Hostile : isCO ? AvionicsDraw.LockBox : AvionicsDraw.HudText);
                GUI.enabled = !isCO && !o.kia;
                if (GUI.Button(new Rect(w - 250, yy, 130, 20), "Make commander", Btn)) { s.commander = o.name; message = $"{o.name} commands {s.name}"; }
                GUI.enabled = true;
                if (GUI.Button(new Rect(w - 116, yy, 96, 20), "Remove", Btn)) { o.squadron = ""; if (s.commander == o.name) s.commander = ""; }
            }
            GUI.EndScrollView();
        }

        // ---- Records (leaderboard, including the fallen) ----

        void DrawRecords(Rect a)
        {
            float x = a.x, y = a.y, w = a.width;
            var all = new List<Operator>(CombatOperators.All);
            all.Sort((p, q) => q.kills != p.kills ? q.kills.CompareTo(p.kills) : Ranks.Merit(q).CompareTo(Ranks.Merit(p)));
            var h = AvionicsDraw.Header;
            L(x, y, 30, "#", AvionicsDraw.ScopeText, h);
            L(x + 24, y, 200, "Operator", AvionicsDraw.ScopeText, h);
            L(x + 226, y, 70, "Squadron", AvionicsDraw.ScopeText, h);
            L(x + 300, y, 40, "Kills", AvionicsDraw.ScopeText, h);
            L(x + 342, y, 110, "A-A A-G G-A", AvionicsDraw.ScopeText, h);
            L(x + 440, y, 70, "man/unm", AvionicsDraw.ScopeText, h);
            L(x + 506, y, 80, "btl/lost", AvionicsDraw.ScopeText, h);
            y += 18;
            var s = AvionicsDraw.LabelSmall;
            float detailH = 84;
            scroll = GUI.BeginScrollView(new Rect(x, y, w, a.yMax - y - detailH), scroll, new Rect(0, 0, w - 18, all.Count * 18));
            for (int i = 0; i < all.Count; i++)
            {
                var o = all[i];
                float yy = i * 18;
                var col = o.kia ? AvionicsDraw.Hostile : o.name == selected ? AvionicsDraw.LockBox : AvionicsDraw.HudText;
                if (GUI.Button(new Rect(0, yy, w - 20, 17), "", GUIStyle.none)) selected = o.name;
                L(0, yy, 24, $"{i + 1}", col, s);
                L(24, yy, 200, $"{Ranks.Short(o)} {o.name}{(o.kia ? " (KIA)" : "")}{(o.medals.Contains("Ace") ? " *ACE*" : "")}", col, s);
                L(226, yy, 70, o.squadron, col, s);
                L(300, yy, 40, $"{o.kills}", col, s);
                L(342, yy, 110, $"{o.Stat("kill.A-A"),3} {o.Stat("kill.A-G") + o.Stat("kill.A-S"),3} {o.Stat("kill.G-A") + o.Stat("kill.S-A"),3}", col, s);
                L(440, yy, 70, $"{o.Stat("kill.manned")}/{o.Stat("kill.unmanned")}", col, s);
                L(506, yy, 80, $"{o.Stat("battles")}/{o.Stat("losses")}", col, s);
            }
            GUI.EndScrollView();
            var cur = selected != null ? CombatOperators.Get(selected) : null;
            if (cur != null)
            {
                float yd = a.yMax - detailH + 6;
                L(x, yd, w, $"{Ranks.Long(cur)} {cur.name}{(string.IsNullOrEmpty(cur.squadron) ? "" : $", {cur.squadron}")}", AvionicsDraw.HudText, AvionicsDraw.Header);
                DrawRecord(cur, x, yd + 18, w);
            }
        }

        // ---- Battles ----

        void DrawBattles(Rect a)
        {
            float x = a.x, y = a.y, w = a.width;
            var list = CombatOperators.Instance.battles;
            if (list.Count == 0) { L(x, y, w, "No battles recorded yet. A battle starts at the first hit in flight and ends 45 s after the last.", AvionicsDraw.ScopeText); return; }
            float listH = 120;
            scroll = GUI.BeginScrollView(new Rect(x, y, w, listH), scroll, new Rect(0, 0, w - 18, list.Count * 22));
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                int kills = 0, lost = 0;
                foreach (var r in b.rows) { kills += r.kills; if (!r.alive) lost++; }
                if (GUI.Button(new Rect(0, i * 22, w - 20, 20), $"{b.Title}   {KSPUtil.PrintTimeCompact(b.Duration, false)} · {kills} kills · {lost} lost", selectedBattle == i ? Sel : Btn)) selectedBattle = i;
            }
            GUI.EndScrollView();
            y += listH + 6;
            if (selectedBattle < 0 || selectedBattle >= list.Count) { L(x, y, w, "Select a battle for its after-action report. Text copies are in BDArmoryAvionics/PluginData/AAR.", AvionicsDraw.ScopeText); return; }
            BattleReportView.Draw(new Rect(x, y, w, a.yMax - y), list[selectedBattle], false, ref reportScroll);
        }
    }
}
