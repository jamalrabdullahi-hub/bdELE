using System.Collections.Generic;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Radar;
using BDArmory.Targeting;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Integrated air defence: one controller per team, running on top of that team's data link network.
    /// - Scores hostile tracks and hands each one to the best SAM site (closest in range, with missiles, not busy).
    /// - Launch on remote: SAM sites are radar-linked to whichever networked sensor holds their target,
    ///   so they can lock and fire through it with their own radar off.
    /// - EMCON: decides which radars may transmit (enforced through ModuleRadar.EnableRadar).
    /// SAM sites must be in guard mode; the controller only tells their weapon manager which target to engage.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class IADSController : MonoBehaviour
    {
        public static IADSController Instance;
        public static readonly Dictionary<BDTeam, TeamIADS> Teams = new();
        const float UpdateInterval = 0.5f;
        const float MissileAvgSpeed = 800f; // rough, for time-to-intercept until the flight estimator exists

        public class Site
        {
            public NetNode node;
            public NetTrack assigned;
            public bool manual;            // assigned by the player; auto-assign leaves it alone
            public bool radarAllowed = true;
            public float originalSalvo = -1;
            public VesselRadarData linkedSensor; // the remote VRD we linked for launch-on-remote
            public bool LaunchingOnRemote => linkedSensor != null && assigned != null && !node.radarOn;
            public string State =>
                assigned != null ? (LaunchingOnRemote ? $"LOR {assigned.Label}" : $"ENGAGE {assigned.Label}")
                : node.radarOn ? "SEARCH" : "SILENT";
        }

        public class TeamIADS
        {
            public BDTeam team;
            public readonly Dictionary<Vessel, Site> sites = new();
            public readonly Dictionary<NetTrack, float> threat = new();
            public readonly List<string> log = new();
            public void Log(string s)
            {
                log.Insert(0, $"{KSPUtil.PrintTimeCompact(Planetarium.GetUniversalTime(), false)}  {s}");
                if (log.Count > 60) log.RemoveAt(log.Count - 1);
            }
            public Site SiteFor(Vessel v) => v != null && sites.TryGetValue(v, out var s) ? s : null;
        }

        float nextUpdate;

        void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
            AvionicsSettings.Load();
            Teams.Clear();
            ModuleRadar.EmconBlocked = RadarBlocked;
        }

        void OnDestroy()
        {
            if (ModuleRadar.EmconBlocked == RadarBlocked) ModuleRadar.EmconBlocked = null;
            foreach (var t in Teams.Values) foreach (var s in t.sites.Values) Release(s);
            Teams.Clear();
            if (Instance == this) Instance = null;
        }

        public static TeamIADS For(BDTeam team) => team != null && Teams.TryGetValue(team, out var t) ? t : null;

        static bool Controls(BDTeam team)
        {
            if (!C4ISRSettings.IADSEnabled || team == null) return false;
            if (C4ISRSettings.IADSForAITeams) return true;
            var wm = BDArmorySetup.Instance != null ? BDArmorySetup.Instance.ActiveWeaponManager : null;
            return wm != null && wm.Team == team;
        }

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready) return;
            if (Time.time < nextUpdate) return;
            nextUpdate = Time.time + UpdateInterval;

            foreach (var net in DataLinkNetwork.Networks.Values)
            {
                if (!Teams.TryGetValue(net.team, out var iads)) Teams[net.team] = iads = new TeamIADS { team = net.team };
                if (!Controls(net.team))
                {
                    foreach (var s in iads.sites.Values) Release(s);
                    iads.sites.Clear();
                    continue;
                }
                RefreshSites(iads, net);
                ScoreThreats(iads, net);
                if (C4ISRSettings.IADSAutoAssign) AutoAssign(iads, net);
                Apply(iads, net);
            }
        }

        // ---- Sites ----

        static void RefreshSites(TeamIADS iads, TeamNetwork net)
        {
            var gone = new List<Vessel>();
            foreach (var kv in iads.sites)
            {
                var n = net.Node(kv.Key);
                if (n == null || n.role != NodeRole.SAM) gone.Add(kv.Key);
            }
            foreach (var v in gone) { Release(iads.sites[v]); iads.sites.Remove(v); }
            foreach (var n in net.nodes)
            {
                if (n.role != NodeRole.SAM) continue;
                if (!iads.sites.TryGetValue(n.vessel, out var s)) { iads.sites[n.vessel] = s = new Site(); iads.Log($"{n.Name} joined the IADS"); }
                s.node = n;
                if (s.assigned != null && (s.assigned.vessel == null || !net.tracks.ContainsValue(s.assigned)))
                {
                    iads.Log($"{n.Name}: {s.assigned.Label} lost");
                    s.assigned = null;
                    s.manual = false;
                }
            }
        }

        /// <summary>Put a site's weapon manager and radar links back the way they were.</summary>
        static void Release(Site s)
        {
            if (s.node == null) return;
            var wm = s.node.wm;
            if (wm != null && s.originalSalvo >= 0) wm.maxMissilesOnTarget = s.originalSalvo;
            s.originalSalvo = -1;
            Unlink(s);
            s.assigned = null;
        }

        // ---- Threat evaluation and assignment ----

        static void ScoreThreats(TeamIADS iads, TeamNetwork net)
        {
            iads.threat.Clear();
            foreach (var t in net.tracks.Values)
            {
                if (!t.hostile || t.vessel == null) continue;
                if (t.isMissile && !C4ISRSettings.EngageMissiles) continue;
                float nearest = float.MaxValue, closing = 0;
                foreach (var s in iads.sites.Values)
                {
                    var rel = t.position - s.node.vessel.CoM;
                    float d = rel.magnitude;
                    if (d < nearest) { nearest = d; closing = d > 1 ? -Vector3.Dot(t.velocity, rel / d) : 0; }
                }
                if (nearest == float.MaxValue) continue;
                float score = (t.isMissile ? 3f : 0f)
                    + Mathf.Clamp(closing, 0, 1000) / 200f
                    + Mathf.Max(0, 100000f - nearest) / 20000f;
                iads.threat[t] = score;
            }
        }

        static void AutoAssign(TeamIADS iads, TeamNetwork net)
        {
            // Release auto assignments that no longer make sense
            foreach (var s in iads.sites.Values)
            {
                if (s.assigned == null || s.manual) continue;
                bool keep = iads.threat.ContainsKey(s.assigned) && s.node.connected && s.node.readyMissiles > 0
                    && Range(s, s.assigned) <= s.node.maxMissileRange * 1.2f;
                if (!keep) s.assigned = null;
            }

            var order = new List<NetTrack>(iads.threat.Keys);
            order.Sort((a, b) => iads.threat[b].CompareTo(iads.threat[a]));
            foreach (var t in order)
            {
                if (AssignedTo(iads, t) != null) continue;
                if (C4ISRSettings.ShootLookShoot && MissilesInFlight(iads, t) > 0) continue; // look before shooting again
                Site best = null;
                float bestRange = float.MaxValue;
                foreach (var s in iads.sites.Values)
                {
                    if (s.assigned != null || !s.node.connected || !s.node.IsShooter || s.node.wm == null || !s.node.wm.guardMode) continue;
                    float r = Range(s, t);
                    if (r > s.node.maxMissileRange || r >= bestRange) continue;
                    best = s;
                    bestRange = r;
                }
                if (best == null) continue;
                best.assigned = t;
                best.manual = false;
                iads.Log($"{best.node.Name} assigned {t.Label} ({t.Name}), {bestRange / 1000f:F0} km");
            }
        }

        public static Site AssignedTo(TeamIADS iads, NetTrack t)
        {
            foreach (var s in iads.sites.Values) if (s.assigned == t) return s;
            return null;
        }

        static int MissilesInFlight(TeamIADS iads, NetTrack t)
        {
            if (t.targetInfo == null) return 0;
            int n = 0;
            foreach (var s in iads.sites.Values)
                if (s.node.wm != null) n += s.node.wm.GetMissilesAway(t.targetInfo).numAway;
            return n;
        }

        static float Range(Site s, NetTrack t) => (t.position - s.node.vessel.CoM).magnitude;

        public static float TimeToIntercept(Site s, NetTrack t) => Range(s, t) / MissileAvgSpeed;

        /// <summary>Player tasking: give a track to a site (null track clears it).</summary>
        public static void Assign(BDTeam team, Vessel site, NetTrack track)
        {
            var iads = For(team);
            var s = iads?.SiteFor(site);
            if (s == null) return;
            s.assigned = track;
            s.manual = track != null;
            iads.Log(track != null ? $"{s.node.Name} manually assigned {track.Label}" : $"{s.node.Name} assignment cleared");
        }

        // ---- Applying orders: targets, salvo, launch-on-remote links, EMCON ----

        static void Apply(TeamIADS iads, TeamNetwork net)
        {
            foreach (var s in iads.sites.Values)
            {
                var wm = s.node.wm;
                if (wm == null) continue;

                if (s.originalSalvo < 0) s.originalSalvo = wm.maxMissilesOnTarget;
                wm.maxMissilesOnTarget = C4ISRSettings.Salvo;

                s.radarAllowed = SiteRadarAllowed(iads, net, s);

                // Launch on remote: link to the closest networked radar holding the target.
                VesselRadarData want = null;
                if (C4ISRSettings.LaunchOnRemote && s.assigned != null && s.node.connected)
                {
                    var src = s.assigned.bestRadarReporter;
                    if (src != null && src != s.node && src.vrd != null && src.connected) want = src.vrd;
                }
                if (want != s.linkedSensor)
                {
                    Unlink(s);
                    if (want != null && EnsureVRD(s.node) is VesselRadarData vrd)
                    {
                        vrd.LinkVRD(want);
                        s.linkedSensor = want;
                        iads.Log($"{s.node.Name} launch-on-remote via {want.vessel.GetName()}");
                    }
                }

                if (s.assigned != null && s.assigned.targetInfo != null && wm.guardMode)
                    wm.SetOverrideTarget(s.assigned.targetInfo);

                EnforceRadars(s.node, s.radarAllowed);
            }

            // Early-warning radars only go quiet in EMCON D.
            foreach (var n in net.nodes)
                if (n.role == NodeRole.EWR || n.role == NodeRole.AEW)
                    EnforceRadars(n, C4ISRSettings.Emcon != EmconLevel.D_Silent);
        }

        static bool SiteRadarAllowed(TeamIADS iads, TeamNetwork net, Site s)
        {
            switch (C4ISRSettings.Emcon)
            {
                case EmconLevel.A_AllOn: return true;
                case EmconLevel.D_Silent: return false;
                case EmconLevel.C_EWROnly:
                    // Only if nobody else can guide the shot.
                    return s.assigned != null && (s.linkedSensor == null || !C4ISRSettings.LaunchOnRemote);
                default: // B: cue on
                    if (s.assigned != null) return true;
                    float ring = C4ISRSettings.WakeRingKm * 1000f;
                    foreach (var t in net.tracks.Values)
                        if (t.hostile && (t.position - s.node.vessel.CoM).sqrMagnitude < ring * ring) return true;
                    return false;
            }
        }

        static void EnforceRadars(NetNode n, bool allowed)
        {
            if (n.vessel == null || n.wm == null) return;
            if (IsPlayerFlown(n)) return;
            foreach (var r in Utils.VesselModuleRegistry.GetModules<ModuleRadar>(n.vessel))
            {
                if (r == null || r.sonarMode != ModuleRadar.SonarModes.None) continue;
                if (!allowed && r.radarEnabled) r.DisableRadar();
                else if (allowed && !r.radarEnabled && n.wm.guardMode) r.EnableRadar();
            }
        }

        static bool IsPlayerFlown(NetNode n) => n.vessel == FlightGlobals.ActiveVessel && (n.wm == null || !n.wm.guardMode);

        static VesselRadarData EnsureVRD(NetNode n)
        {
            if (n.vrd != null) return n.vrd;
            var go = n.vessel.gameObject;
            var vrd = go.GetComponent<VesselRadarData>();
            if (vrd == null) vrd = go.AddComponent<VesselRadarData>();
            vrd.weaponManager = n.wm;
            n.vrd = vrd;
            return vrd;
        }

        static void Unlink(Site s)
        {
            if (s.linkedSensor != null && s.node?.vrd != null && s.node.vrd.IsLinkedVRD(s.linkedSensor))
                s.node.vrd.UnlinkVRD(s.linkedSensor);
            s.linkedSensor = null;
        }

        /// <summary>Hooked into ModuleRadar.EnableRadar: true stops the radar from switching on.</summary>
        static bool RadarBlocked(ModuleRadar r)
        {
            if (r == null || r.vessel == null || r.sonarMode != ModuleRadar.SonarModes.None) return false;
            var wm = r.WeaponManager;
            if (wm == null || !Controls(wm.Team)) return false;
            var net = DataLinkNetwork.For(wm.Team);
            var n = net?.Node(r.vessel);
            if (n == null || IsPlayerFlown(n)) return false;
            if (n.role == NodeRole.EWR || n.role == NodeRole.AEW) return C4ISRSettings.Emcon == EmconLevel.D_Silent;
            if (n.role != NodeRole.SAM) return false;
            var s = For(wm.Team)?.SiteFor(r.vessel);
            return s != null && !s.radarAllowed;
        }
    }
}
