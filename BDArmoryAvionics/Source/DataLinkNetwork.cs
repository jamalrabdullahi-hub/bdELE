using System.Collections.Generic;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Avionics
{
    public enum NodeRole { Fighter, AEW, SAM, EWR, HQ, Ground, Ship }
    public enum LinkPath { None, Global, Direct, Relay }

    /// <summary>A team vessel with a weapon manager taking part in the data link.</summary>
    public class NetNode
    {
        public Vessel vessel;
        public MissileFire wm;
        public VesselRadarData vrd;
        public ModuleC4ISRTerminal terminal; // null: automatic settings
        public NodeRole role;
        public LinkPath path;
        public int hops;
        public float quality;          // 0..1
        public double delay;           // seconds (CommNet signal delay)
        public bool connected;
        public float lastConnectedTime;
        public int trackCount;         // tracks this node is currently contributing
        public int readyMissiles;
        public float maxMissileRange;  // metres, longest engagement range of any ready missile
        public bool hasRadar;
        public bool radarOn;
        public float radarRange;       // metres, longest detection range of any radar
        public float jam;              // jamming received (J/S, 0 = clear)
        public bool jammed;            // link cut by enemy jamming
        public bool hasRWR;            // can locate emitters passively (ESM)

        public string Name => vessel != null ? vessel.GetName() : "(lost)";
        public bool IsShooter => role == NodeRole.SAM && readyMissiles > 0 && (terminal == null || terminal.iadsMember);
        public bool LinkEnabled => terminal == null || terminal.dataLink;
        public bool IsAir => role == NodeRole.Fighter || role == NodeRole.AEW;
        public bool IsSensor => hasRadar && (role == NodeRole.EWR || role == NodeRole.AEW);
    }

    /// <summary>One fused track in a team's shared air picture.</summary>
    public class NetTrack
    {
        public int id;
        public Vessel vessel;
        public BDTeam team;
        public TargetInfo targetInfo;
        public Vector3 position;
        public Vector3 velocity;
        public float lastSeen;
        public bool isMissile;
        public bool hostile;
        public bool byRadar, byIRST, byTeamDB, byESM;
        public bool emitterJammer;     // located by its jamming
        public readonly List<NetNode> reporters = new();
        public NetNode bestRadarReporter;  // closest node holding it on radar (used for launch-on-remote)

        public string Label => $"T{id:00}";
        public string Name => vessel != null ? vessel.GetName() : Label;
        public string SourceString => (byRadar ? "RDR " : "") + (byIRST ? "IRST " : "") + (byESM ? (emitterJammer ? "ESM-JAM " : "ESM ") : "") + (byTeamDB ? "TEAM" : "");
    }

    /// <summary>A team's data link network: its nodes, which of them are connected, and the fused track picture.</summary>
    public class TeamNetwork
    {
        public BDTeam team;
        public readonly List<NetNode> nodes = new();
        public readonly Dictionary<uint, NetTrack> tracks = new();
        public int nextTrackId = 1;

        public NetNode Node(Vessel v)
        {
            for (int i = 0; i < nodes.Count; i++) if (nodes[i].vessel == v) return nodes[i];
            return null;
        }

        public NetTrack Track(Vessel v) => v != null && tracks.TryGetValue(v.persistentId, out var t) ? t : null;

        public int ConnectedCount
        {
            get { int n = 0; for (int i = 0; i < nodes.Count; i++) if (nodes[i].connected) n++; return n; }
        }
    }

    /// <summary>
    /// Builds a data link network per team every update.
    /// Global C2: every team vessel is connected with no range limit.
    /// CommNet: a vessel is connected when CommNet says it can reach home (directly or through relays).
    /// Line of sight: vessels link directly within range when the planet isn't in the way; the largest group is the network.
    /// Only connected vessels contribute their own radar and IRST contacts to the shared picture.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class DataLinkNetwork : MonoBehaviour
    {
        public static DataLinkNetwork Instance;
        public static readonly Dictionary<BDTeam, TeamNetwork> Networks = new();

        float nextUpdate;
        readonly Dictionary<Vessel, NetNode> nodeCache = new();

        void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
            AvionicsSettings.Load();
            Networks.Clear();
        }

        void OnDestroy()
        {
            Networks.Clear();
            if (Instance == this) Instance = null;
        }

        public static TeamNetwork For(BDTeam team) => team != null && Networks.TryGetValue(team, out var n) ? n : null;

        /// <summary>The network of the team flying the active vessel.</summary>
        public static TeamNetwork PlayerNetwork
        {
            get
            {
                var wm = BDArmorySetup.Instance != null ? BDArmorySetup.Instance.ActiveWeaponManager : null;
                return wm != null ? For(wm.Team) : null;
            }
        }

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready) return;
            if (Time.time < nextUpdate) return;
            nextUpdate = Time.time + 1f / Mathf.Max(0.5f, C4ISRSettings.NetUpdateHz);
            Rebuild();
        }

        void Rebuild()
        {
            foreach (var net in Networks.Values) net.nodes.Clear();

            // Nodes
            var vessels = FlightGlobals.VesselsLoaded;
            var seen = new HashSet<Vessel>();
            for (int i = 0; i < vessels.Count; i++)
            {
                var v = vessels[i];
                if (v == null || !v.loaded || v.packed || v.isEVA) continue;
                if (VesselModuleRegistry.IgnoredVesselTypes.Contains(v.vesselType)) continue;
                var wm = v.ActiveController().WM;
                if (wm == null || wm.Team == null) continue;
                if (!Networks.TryGetValue(wm.Team, out var net)) Networks[wm.Team] = net = new TeamNetwork { team = wm.Team };
                if (!nodeCache.TryGetValue(v, out var node)) nodeCache[v] = node = new NetNode { vessel = v };
                node.wm = wm;
                node.vrd = wm.vesselRadarData != null ? wm.vesselRadarData : v.gameObject.GetComponent<VesselRadarData>();
                node.terminal = ModuleC4ISRTerminal.For(v, wm.part);
                Survey(node);
                net.nodes.Add(node);
                seen.Add(v);
            }
            var stale = new List<Vessel>();
            foreach (var v in nodeCache.Keys) if (v == null || !seen.Contains(v)) stale.Add(v);
            foreach (var v in stale) nodeCache.Remove(v);
            ElectronicWarfare.Survey();

            foreach (var net in Networks.Values)
            {
                Connect(net);
                foreach (var n in net.nodes) if (!n.LinkEnabled) SetLink(n, false, LinkPath.None, 0, 0, 0); // switched off on the terminal
                ElectronicWarfare.ApplyLinkJamming(net);
                FuseTracks(net);
                foreach (var n in net.nodes)
                    if (n.terminal != null)
                        n.terminal.status = !n.LinkEnabled ? "Off" : n.jammed ? "JAMMED" : !n.connected ? "No link" : $"{n.role}, {n.path}, {n.trackCount} trk{(n.jam > 0.05f ? $", jam {n.jam:F1}" : "")}";
            }
        }

        /// <summary>Weapons, sensors and role of one node.</summary>
        static void Survey(NetNode n)
        {
            var v = n.vessel;
            n.readyMissiles = 0;
            n.maxMissileRange = 0;
            foreach (var m in VesselModuleRegistry.GetModules<MissileBase>(v))
            {
                if (m == null || m.HasFired || m.launched) continue;
                n.readyMissiles++;
                n.maxMissileRange = Mathf.Max(n.maxMissileRange, m.GetEngageRange());
            }
            n.hasRadar = false;
            n.radarOn = false;
            n.radarRange = 0;
            foreach (var r in VesselModuleRegistry.GetModules<ModuleRadar>(v))
            {
                if (r == null) continue;
                n.hasRadar = true;
                n.radarOn |= r.radarEnabled;
                n.radarRange = Mathf.Max(n.radarRange, r.radarMaxDistanceDetect * 1000f);
            }
            n.hasRWR = false;
            foreach (var w in VesselModuleRegistry.GetModules<RadarWarningReceiver>(v)) if (w != null && w.rwrEnabled) { n.hasRWR = true; break; }

            bool onSurface = v.LandedOrSplashed;
            if (onSurface)
            {
                if (n.readyMissiles > 0) n.role = NodeRole.SAM;
                else if (n.hasRadar) n.role = NodeRole.EWR;
                else n.role = v.Splashed ? NodeRole.Ship : NodeRole.Ground;
            }
            else
            {
                bool guns = false;
                foreach (var w in VesselModuleRegistry.GetModules<Weapons.ModuleWeapon>(v)) { if (w != null) { guns = true; break; } }
                n.role = n.hasRadar && n.readyMissiles == 0 && !guns ? NodeRole.AEW : NodeRole.Fighter;
            }
            if (n.terminal != null && n.terminal.HasRoleOverride) n.role = n.terminal.RoleOverride;
        }

        /// <summary>The role a craft would get in flight, for the editor window (no vessel yet, so landed/flying is a parameter).</summary>
        public static NodeRole PredictRole(bool onSurface, bool splashed, int missiles, bool radar, bool guns)
        {
            if (onSurface) return missiles > 0 ? NodeRole.SAM : radar ? NodeRole.EWR : splashed ? NodeRole.Ship : NodeRole.Ground;
            return radar && missiles == 0 && !guns ? NodeRole.AEW : NodeRole.Fighter;
        }

        // ---- Connectivity ----

        static void Connect(TeamNetwork net)
        {
            var nodes = net.nodes;
            bool global = C4ISRSettings.GlobalC2;
            bool commNetOn = CommNet.CommNetScenario.CommNetEnabled;
            if (!global && C4ISRSettings.LinkModel == LinkModel.CommNet && !commNetOn) global = true; // CommNet switched off in the difficulty settings

            if (global)
            {
                foreach (var n in nodes) SetLink(n, true, LinkPath.Global, 1, 1f, 0);
                return;
            }

            float range = C4ISRSettings.LOSLinkRangeKm * 1000f;
            if (C4ISRSettings.LinkModel == LinkModel.CommNet)
            {
                foreach (var n in nodes)
                {
                    var c = n.vessel.Connection;
                    if (c != null && c.IsConnected)
                    {
                        int hops = c.ControlPath != null ? c.ControlPath.Count : 1;
                        SetLink(n, true, hops <= 1 ? LinkPath.Direct : LinkPath.Relay, hops, (float)c.SignalStrength, c.SignalDelay);
                    }
                    else SetLink(n, false, LinkPath.None, 0, 0, 0);
                }
                // Without the antenna requirement, vessels with no CommNet path can still join through a nearby connected vessel.
                if (!C4ISRSettings.RequireAntenna) Mesh(nodes, range, seeds: null);
                return;
            }

            // Line of sight: link groups, keep the largest (ties go to the group holding the active vessel).
            foreach (var n in nodes) SetLink(n, false, LinkPath.None, 0, 0, 0);
            var unassigned = new List<NetNode>(nodes);
            List<NetNode> best = null;
            bool bestHasActive = false;
            while (unassigned.Count > 0)
            {
                var group = new List<NetNode> { unassigned[0] };
                unassigned.RemoveAt(0);
                for (int g = 0; g < group.Count; g++)
                    for (int u = unassigned.Count - 1; u >= 0; u--)
                        if (LineOfSight(group[g].vessel, unassigned[u].vessel, range))
                        {
                            group.Add(unassigned[u]);
                            unassigned.RemoveAt(u);
                        }
                bool hasActive = group.Exists(x => x.vessel == FlightGlobals.ActiveVessel);
                if (best == null || group.Count > best.Count || (group.Count == best.Count && hasActive && !bestHasActive))
                {
                    best = group;
                    bestHasActive = hasActive;
                }
            }
            if (best != null)
                foreach (var n in best)
                    SetLink(n, true, LinkPath.Direct, 1, 1f, 0);
        }

        /// <summary>Connect unconnected nodes that can see an already-connected node (repeats until nothing changes).</summary>
        static void Mesh(List<NetNode> nodes, float range, List<NetNode> seeds)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var n in nodes)
                {
                    if (n.connected) continue;
                    foreach (var m in nodes)
                    {
                        if (!m.connected || m == n) continue;
                        if (!LineOfSight(n.vessel, m.vessel, range)) continue;
                        SetLink(n, true, LinkPath.Direct, m.hops + 1, m.quality * 0.9f, m.delay);
                        changed = true;
                        break;
                    }
                }
            }
        }

        internal static void SetLink(NetNode n, bool connected, LinkPath path, int hops, float quality, double delay)
        {
            n.connected = connected;
            n.path = path;
            n.hops = hops;
            n.quality = Mathf.Clamp01(quality);
            n.delay = delay;
            if (connected) n.lastConnectedTime = Time.time;
        }

        /// <summary>Within range and the straight line between the two doesn't pass through the planet.</summary>
        public static bool LineOfSight(Vessel a, Vessel b, float range)
        {
            if (a == null || b == null) return false;
            Vector3d pa = a.CoMD, pb = b.CoMD;
            if ((pa - pb).sqrMagnitude > (double)range * range) return false;
            var body = a.mainBody;
            if (body == null) return true;
            Vector3d c = body.position;
            Vector3d d = pb - pa;
            double t = d.sqrMagnitude > 0 ? Vector3d.Dot(c - pa, d) / d.sqrMagnitude : 0;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            Vector3d closest = pa + d * t;
            return (closest - c).magnitude > body.Radius * 0.999; // small allowance so vessels on the surface see each other
        }

        // ---- Track fusion ----

        static void FuseTracks(TeamNetwork net)
        {
            float now = Time.time;
            foreach (var t in net.tracks.Values)
            {
                t.reporters.Clear();
                t.byRadar = t.byIRST = t.byTeamDB = t.byESM = t.emitterJammer = false;
                t.bestRadarReporter = null;
            }
            foreach (var n in net.nodes) n.trackCount = 0;

            // Passive emitter tracks first, so any radar or IRST report of the same craft overrides the rougher ESM position.
            ElectronicWarfare.ReportEmitters(net, now);

            foreach (var n in net.nodes)
            {
                if (!n.connected || n.vrd == null) continue;
                var radar = n.vrd.DisplayedTargets();
                if (radar != null)
                    for (int i = 0; i < radar.Count; i++)
                    {
                        var d = radar[i];
                        // Only this node's own radars, so linked radar data isn't counted twice.
                        if (d.detectedByRadar == null || d.detectedByRadar.vessel != n.vessel) continue;
                        var t = Report(net, d.vessel, d.targetData.predictedPosition, d.targetData.velocity, d.targetData.targetInfo, n, now);
                        if (t == null) continue;
                        t.byRadar = true;
                        if (t.bestRadarReporter == null || Dist(n, t) < Dist(t.bestRadarReporter, t)) t.bestRadarReporter = n;
                    }
                var ir = n.vrd.DisplayedIRTargets();
                if (ir != null)
                    for (int i = 0; i < ir.Count; i++)
                    {
                        var d = ir[i];
                        if (d.detectedByIRST == null || d.detectedByIRST.vessel != n.vessel) continue;
                        var t = Report(net, d.vessel, d.targetData.predictedPosition, d.targetData.velocity, d.targetData.targetInfo, n, now);
                        if (t != null) t.byIRST = true;
                    }
            }

            // BDArmory's own team database (visual and radar reports) has no range limit, so it only feeds the picture under Global C2.
            if (C4ISRSettings.GlobalC2)
            {
                var db = BDATargetManager.TargetList(net.team);
                for (int i = 0; i < db.Count; i++)
                {
                    var ti = db[i];
                    if (ti == null || ti.Vessel == null) continue;
                    var t = Report(net, ti.Vessel, ti.Vessel.CoM, ti.Vessel.Velocity(), ti, null, now);
                    if (t != null) t.byTeamDB = true;
                }
            }

            // Drop stale tracks
            var drop = new List<uint>();
            foreach (var kv in net.tracks)
                if (kv.Value.vessel == null || now - kv.Value.lastSeen > C4ISRSettings.TrackTimeout) drop.Add(kv.Key);
            foreach (var k in drop) net.tracks.Remove(k);
        }

        static float Dist(NetNode n, NetTrack t) => n.vessel != null ? (n.vessel.CoM - t.position).sqrMagnitude : float.MaxValue;

        internal static NetTrack Report(TeamNetwork net, Vessel v, Vector3 pos, Vector3 vel, TargetInfo ti, NetNode reporter, float now)
        {
            if (v == null) return null;
            if (net.Node(v) != null) return null; // our own team's linked units are nodes, not tracks
            if (!net.tracks.TryGetValue(v.persistentId, out var t))
            {
                t = new NetTrack { id = net.nextTrackId++, vessel = v };
                net.tracks[v.persistentId] = t;
            }
            if (ti == null) ti = v.gameObject.GetComponent<TargetInfo>();
            t.targetInfo = ti;
            t.team = ti != null ? ti.Team : null;
            t.isMissile = ti != null && ti.isMissile;
            t.hostile = t.team != null && net.team.IsEnemy(t.team);
            t.position = pos;
            t.velocity = vel;
            t.lastSeen = now;
            if (reporter != null && !t.reporters.Contains(reporter))
            {
                t.reporters.Add(reporter);
                reporter.trackCount++;
            }
            return t;
        }
    }
}
