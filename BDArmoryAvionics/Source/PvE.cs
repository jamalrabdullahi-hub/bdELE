using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// PvE tuning: difficulty presets and battle intensity (pacing). Intensity rises as the AI loses aircraft and bases,
    /// making it plan faster and send bigger packages; past a point it pulls back to regroup, giving you breathing room.
    /// </summary>
    public static class PvE
    {
        public static readonly string[] Levels = { "Recruit", "Regular", "Veteran", "Ace" };
        static int D => Mathf.Clamp(AvionicsSettings.Difficulty, 0, 3);
        public static int MaxFlight => new[] { 1, 2, 3, 4 }[D];
        public static float PlanMult => new[] { 2.0f, 1.0f, 0.7f, 0.5f }[D];
        public static float ScrambleKm => new[] { 50f, 80f, 110f, 140f }[D];
        public static float GMult => new[] { 0.7f, 0.85f, 1f, 1.1f }[D];
        public static bool Escorts => D >= 1;
        public static bool Packages => D >= 1; // SEAD first, strike follows
        public static bool LowLevel => D >= 2;  // terrain-masked ingress against SAM-defended targets

        static readonly Dictionary<string, float> heat = new();
        static readonly Dictionary<string, double> lastUT = new();

        /// <summary>0..1 battle intensity for an AI faction (decays ~0.1 per game hour).</summary>
        public static float Heat(string faction)
        {
            double ut = Planetarium.GetUniversalTime();
            heat.TryGetValue(faction, out float h);
            if (lastUT.TryGetValue(faction, out double t0)) h = Mathf.Max(0, h - (float)((ut - t0) / 3600.0) * 0.1f);
            heat[faction] = h; lastUT[faction] = ut;
            return h;
        }

        public static void AddHeat(string faction, float d) { float h = Heat(faction); heat[faction] = Mathf.Clamp01(h + d); }

        /// <summary>Plan interval multiplier: hotter battles plan faster, until it's time to regroup.</summary>
        public static float PacingMult(string faction, out bool regroup)
        {
            float h = Heat(faction);
            regroup = h > 0.85f;
            return regroup ? 2.5f : Mathf.Lerp(1.3f, 0.6f, h);
        }
    }

    public enum MissionType { Strike, Intercept, Recon, CSAR, HuntSupport }

    public class Mission
    {
        public MissionType type; public string target = "", enemy = ""; public int goal = 1, progress;
        public float funds, rep; public double issuedUT, expiresUT; public bool done, failed;
        public string Title => type switch
        {
            MissionType.Strike => $"Strike {target}: damage it below 50%",
            MissionType.Intercept => $"Intercept: shoot down {goal} {enemy} aircraft",
            MissionType.Recon => $"Recon {target}: get eyes on it (within 15 km, any altitude - satellites count)",
            MissionType.CSAR => $"CSAR: bring {goal} downed pilot(s) home",
            _ => $"Hunt the {enemy} support aircraft (AWACS)",
        };

        public void Save(ConfigNode n)
        {
            var ci = CultureInfo.InvariantCulture;
            n.AddValue("type", type); n.AddValue("target", target); n.AddValue("enemy", enemy); n.AddValue("goal", goal); n.AddValue("progress", progress);
            n.AddValue("funds", funds.ToString(ci)); n.AddValue("rep", rep.ToString(ci)); n.AddValue("issued", issuedUT.ToString("R", ci)); n.AddValue("expires", expiresUT.ToString("R", ci));
            n.AddValue("done", done); n.AddValue("failed", failed);
        }

        public static Mission Load(ConfigNode n)
        {
            var ci = CultureInfo.InvariantCulture; var st = NumberStyles.Float;
            var m = new Mission { target = n.GetValue("target") ?? "", enemy = n.GetValue("enemy") ?? "" };
            Enum.TryParse(n.GetValue("type"), out m.type); int.TryParse(n.GetValue("goal"), out m.goal); int.TryParse(n.GetValue("progress"), out m.progress);
            float.TryParse(n.GetValue("funds"), st, ci, out m.funds); float.TryParse(n.GetValue("rep"), st, ci, out m.rep);
            double.TryParse(n.GetValue("issued"), st, ci, out m.issuedUT); double.TryParse(n.GetValue("expires"), st, ci, out m.expiresUT);
            bool.TryParse(n.GetValue("done"), out m.done); bool.TryParse(n.GetValue("failed"), out m.failed);
            return m;
        }
    }

    /// <summary>Mission board: up to 3 objectives at a time from the theatre's state, checked as you play; rewards in career.</summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
    public class MissionsScenario : ScenarioModule
    {
        public static MissionsScenario Instance;
        public readonly List<Mission> missions = new();
        public int completed, failedCount; public float merit;
        double nextIssue = -1; float nextCheck;

        public override void OnAwake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        public override void OnSave(ConfigNode node)
        {
            node.AddValue("completed", completed); node.AddValue("failed", failedCount); node.AddValue("merit", merit.ToString(CultureInfo.InvariantCulture));
            foreach (var m in missions) m.Save(node.AddNode("MISSION"));
        }

        public override void OnLoad(ConfigNode node)
        {
            missions.Clear();
            int.TryParse(node.GetValue("completed"), out completed); int.TryParse(node.GetValue("failed"), out failedCount);
            float.TryParse(node.GetValue("merit"), NumberStyles.Float, CultureInfo.InvariantCulture, out merit);
            foreach (var n in node.GetNodes("MISSION")) missions.Add(Mission.Load(n));
        }

        static Faction Player { get { if (FactionsScenario.Instance != null) foreach (var f in FactionsScenario.Instance.factions) if (f.IsPlayer) return f; return null; } }

        void Update()
        {
            if (Time.time < nextCheck || FactionsScenario.Instance == null) return;
            nextCheck = Time.time + 2f;
            double ut = Planetarium.GetUniversalTime();
            if (nextIssue < 0) nextIssue = ut + 60;
            missions.RemoveAll(m => (m.done || m.failed) && ut - m.expiresUT > 6 * 3600);
            if (ut >= nextIssue && missions.FindAll(m => !m.done && !m.failed).Count < 3) { nextIssue = ut + 2 * 3600 * PvE.PlanMult; Issue(ut); }
            foreach (var m in missions) if (!m.done && !m.failed) Check(m, ut);
        }

        void Issue(double ut)
        {
            var me = Player; if (me == null) return;
            var sc = FactionsScenario.Instance;
            var enemyBases = sc.bases.FindAll(b => { var f = FactionsScenario.FactionByName(b.faction); return f != null && f != me && me.BDTeam.IsEnemy(f.BDTeam) && b.status != BaseStatus.Destroyed; });
            var options = new List<MissionType> { MissionType.Intercept };
            if (enemyBases.Count > 0) { options.Add(MissionType.Strike); options.Add(MissionType.Recon); }
            if (CSARScenario.Instance != null && CSARScenario.Instance.survivors.Exists(s => s.status == SurvivorStatus.Down && s.team == me.team)) options.Add(MissionType.CSAR);
            if (sc.flights.Exists(f => f.mission == FlightMission.AWACS && f.state != FlightState.Done && FactionsScenario.FactionByName(f.faction) is Faction ff && me.BDTeam.IsEnemy(ff.BDTeam))) options.Add(MissionType.HuntSupport);
            options.RemoveAll(t => missions.Exists(m => !m.done && !m.failed && m.type == t));
            if (options.Count == 0) return;
            var type = options[UnityEngine.Random.Range(0, options.Count)];
            var mm = new Mission { type = type, issuedUT = ut, expiresUT = ut + 12 * 3600 };
            var enemy = enemyBases.Count > 0 ? FactionsScenario.FactionByName(enemyBases[0].faction) : null;
            mm.enemy = enemy != null ? enemy.name : "enemy";
            switch (type)
            {
                case MissionType.Strike: { var b = enemyBases[UnityEngine.Random.Range(0, enemyBases.Count)]; mm.target = b.site; mm.funds = 60000; mm.rep = 15; break; }
                case MissionType.Recon: { var b = enemyBases[UnityEngine.Random.Range(0, enemyBases.Count)]; mm.target = b.site; mm.funds = 25000; mm.rep = 6; break; }
                case MissionType.Intercept: mm.goal = 2 + PvE.MaxFlight / 2; mm.funds = 40000; mm.rep = 10; break;
                case MissionType.CSAR: mm.goal = 1; mm.funds = 35000; mm.rep = 12; break;
                case MissionType.HuntSupport: mm.funds = 80000; mm.rep = 20; break;
            }
            float diff = new[] { 0.7f, 1f, 1.3f, 1.7f }[Mathf.Clamp(AvionicsSettings.Difficulty, 0, 3)];
            mm.funds *= diff; mm.rep *= diff;
            missions.Add(mm);
            AICommander.Log($"NEW MISSION: {mm.Title} (reward {mm.funds:N0} funds, {mm.rep:F0} rep)");
        }

        void Check(Mission m, double ut)
        {
            if (ut > m.expiresUT) { m.failed = true; failedCount++; AICommander.Log($"MISSION EXPIRED: {m.Title}"); return; }
            switch (m.type)
            {
                case MissionType.Strike: { var b = FactionsScenario.BaseAt(m.target); if (b != null && b.health <= 50) Complete(m); break; }
                case MissionType.Recon:
                    {
                        var b = FactionsScenario.BaseAt(m.target); var me = Player;
                        if (b == null || me == null || !HighLogic.LoadedSceneIsFlight) break;
                        var body = FlightGlobals.GetHomeBody();
                        Vector3d bp = body.GetWorldSurfacePosition(b.lat, b.lon, b.alt), up = (bp - body.position).normalized;
                        foreach (var v in FlightGlobals.Vessels)
                        {
                            if (v == null || v.mainBody != body) continue;
                            var wm = v.loaded ? v.ActiveController().WM : null;
                            if (v != FlightGlobals.ActiveVessel && (wm == null || wm.Team == null || wm.Team.Name != me.team)) continue;
                            if (Vector3d.Exclude(up, v.GetWorldPos3D() - bp).magnitude < 15000) { Complete(m); break; }
                        }
                        break;
                    }
                case MissionType.CSAR:
                    if (CSARScenario.Instance != null && m.progress < m.goal)
                    {
                        int n = CSARScenario.Instance.survivors.FindAll(s => s.status == SurvivorStatus.Rescued && s.downUT >= m.issuedUT - 6 * 3600).Count;
                        m.progress = n; if (n >= m.goal) Complete(m);
                    }
                    break;
            }
        }

        /// <summary>Called by the AI commander when an AI faction loses aircraft.</summary>
        public static void EnemyLost(AirFlight fl, int n)
        {
            if (Instance == null || n <= 0) return;
            foreach (var m in Instance.missions)
            {
                if (m.done || m.failed) continue;
                if (m.type == MissionType.Intercept) { m.progress += n; if (m.progress >= m.goal) Instance.Complete(m); }
                if (m.type == MissionType.HuntSupport && fl.mission == FlightMission.AWACS) Instance.Complete(m);
            }
        }

        void Complete(Mission m)
        {
            m.done = true; completed++; merit += m.rep;
            try
            {
                if (Funding.Instance != null) Funding.Instance.AddFunds(m.funds, TransactionReasons.ContractReward);
                if (Reputation.Instance != null) Reputation.Instance.AddReputation(m.rep, TransactionReasons.ContractReward);
            }
            catch { }
            AICommander.Log($"MISSION COMPLETE: {m.Title} (+{m.funds:N0} funds, +{m.rep:F0} rep)");
        }

        // ---- Theatre window tab ----
        public void Draw(Rect r)
        {
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle; var small = AvionicsDraw.LabelSmall;
            float x = r.x, y = r.y, w = r.width;
            AvionicsDraw.ColorLabel(new Rect(x, y + 3, 80, 16), "Difficulty:", small, AvionicsDraw.HudText);
            for (int i = 0; i < PvE.Levels.Length; i++)
                if (GUI.Button(new Rect(x + 80 + i * 88, y, 84, 20), PvE.Levels[i], AvionicsSettings.Difficulty == i ? sel : btn)) AvionicsSettings.Difficulty = i;
            y += 26;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 16), $"Completed {completed} · failed {failedCount} · merit {merit:F0}", small, AvionicsDraw.HudText); y += 18;
            if (FactionsScenario.Instance != null)
                foreach (var f in FactionsScenario.Instance.factions)
                    if (!f.IsPlayer) { float h = PvE.Heat(f.name); AvionicsDraw.ColorLabel(new Rect(x, y, w, 16), $"{f.name} battle intensity {h:P0}{(h > 0.85f ? " - regrouping" : "")}", small, f.colour); y += 16; }
            y += 6;
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "MISSIONS", AvionicsDraw.Header, AvionicsDraw.HudText); y += 18;
            double ut = Planetarium.GetUniversalTime();
            if (missions.Count == 0) { AvionicsDraw.ColorLabel(new Rect(x, y, w, 16), "None yet - new objectives are issued every couple of hours.", small, AvionicsDraw.ScopeText); y += 18; }
            foreach (var m in missions)
            {
                Color c = m.done ? AvionicsDraw.Friendly : m.failed ? AvionicsDraw.Hostile : AvionicsDraw.HudText;
                string state = m.done ? "COMPLETE" : m.failed ? "EXPIRED" : $"{KSPUtil.PrintTimeCompact(Math.Max(0, m.expiresUT - ut), false)} left";
                string prog = m.goal > 1 || m.type == MissionType.Intercept ? $" ({m.progress}/{m.goal})" : "";
                AvionicsDraw.ColorLabel(new Rect(x, y, w, 16), $"{m.Title}{prog}", small, c); y += 15;
                AvionicsDraw.ColorLabel(new Rect(x + 12, y, w, 16), $"{state} · {m.funds:N0} funds, {m.rep:F0} rep", small, AvionicsDraw.ScopeText); y += 18;
            }
            if (GUI.Button(new Rect(x, y + 4, 160, 20), "Issue a mission now", btn)) Issue(Planetarium.GetUniversalTime());
        }
    }
}
