using System;
using System.Collections.Generic;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    [Flags]
    public enum ContactSource
    {
        None = 0,
        Radar = 1 << 0,     // this vessel's own radar
        IRST = 1 << 1,      // this vessel's own IRST
        Datalink = 1 << 2,  // radar/IRST on a data-linked vessel
        Team = 1 << 3,      // reported to the team database (visual or radar, by any team member)
        TGP = 1 << 4,       // tracked by this vessel's targeting camera
        GPS = 1 << 5,       // a saved GPS point
    }

    public enum IFF { Unknown, Hostile, Friendly, Neutral }

    public class Contact
    {
        public Vessel vessel;             // null for GPS points and unidentified radar returns
        public string name;
        public Vector3 position;          // world position as reported by the sensor
        public Vector3 velocity;
        public ContactSource sources;
        public IFF iff;
        public bool isMissile;
        public bool radarLocked;
        public bool activeLock;
        public int gpsIndex = -1;
        public GPSTargetInfo gps;
        public float range;
        public double altitude;
        public float closure;             // positive = closing

        public bool IsGPS => (sources & ContactSource.GPS) != 0 && vessel == null;
        public bool IsSensorContact => (sources & (ContactSource.Radar | ContactSource.IRST | ContactSource.Datalink)) != 0;
        public string Key => vessel != null ? vessel.persistentId.ToString() : IsGPS ? $"gps:{gps.name}:{AvionicsSettings.GPSKey(gps.gpsCoordinates)}" : $"pos:{position.GetHashCode()}";

        public string SourceString()
        {
            var s = new System.Text.StringBuilder();
            if ((sources & ContactSource.Radar) != 0) s.Append("RDR ");
            if ((sources & ContactSource.IRST) != 0) s.Append("IRST ");
            if ((sources & ContactSource.Datalink) != 0) s.Append("DLNK ");
            if ((sources & ContactSource.TGP) != 0) s.Append("TGP ");
            if ((sources & ContactSource.Team) != 0) s.Append("TEAM ");
            if ((sources & ContactSource.GPS) != 0) s.Append("GPS ");
            return s.ToString().TrimEnd();
        }

        public Color IFFColor => isMissile && iff != IFF.Friendly ? AvionicsDraw.Missile : iff switch
        {
            IFF.Hostile => AvionicsDraw.Hostile,
            IFF.Friendly => AvionicsDraw.Friendly,
            IFF.Neutral => AvionicsDraw.Neutral,
            _ => AvionicsDraw.Unknown,
        };

        public string IFFString => iff switch { IFF.Hostile => "HOS", IFF.Friendly => "FRD", IFF.Neutral => "NEU", _ => "UNK" };
    }

    /// <summary>
    /// Merges everything the active vessel knows about into one contact list.
    /// Only sensor data is used: radar/IRST contacts come from BDArmory's own contact lists, so jamming,
    /// chaff, notching and terrain masking still apply.
    /// </summary>
    public static class AvionicsContacts
    {
        public static readonly List<Contact> Contacts = new();
        public static string SelectedKey;
        public static MissileFire WM;
        public static Vessel Own;

        static readonly Dictionary<Vessel, Contact> byVessel = new();
        static readonly Stack<Contact> pool = new();

        public static Contact Selected
        {
            get
            {
                if (SelectedKey == null) return null;
                for (int i = 0; i < Contacts.Count; i++) if (Contacts[i].Key == SelectedKey) return Contacts[i];
                return null;
            }
        }

        public static void Refresh()
        {
            for (int i = 0; i < Contacts.Count; i++) pool.Push(Contacts[i]);
            Contacts.Clear();
            byVessel.Clear();

            WM = BDArmorySetup.Instance != null ? BDArmorySetup.Instance.ActiveWeaponManager : null;
            Own = FlightGlobals.ActiveVessel;
            if (WM == null || Own == null) return;
            BDTeam myTeam = WM.Team;
            var vrd = WM.vesselRadarData;

            // Radar (own + data-linked)
            if (vrd != null && vrd.DisplayedTargets != null)
            {
                var list = vrd.DisplayedTargets;
                Vessel activeLockVessel = null;
                if (vrd.locked && vrd.numLockedTargets > 0)
                {
                    try { activeLockVessel = vrd.lockedTargetData.vessel; } catch { activeLockVessel = null; }
                }
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t.vessel == Own) continue;
                    var src = t.detectedByRadar != null && t.detectedByRadar.vessel != Own ? ContactSource.Datalink : ContactSource.Radar;
                    var c = Get(t.vessel, t.targetData.predictedPosition, t.targetData.velocity, src, t.targetData.Team, myTeam);
                    c.isMissile |= t.targetData.targetInfo != null && t.targetData.targetInfo.isMissile;
                    c.radarLocked |= t.locked;
                    if (t.vessel != null && t.vessel == activeLockVessel) c.activeLock = true;
                }
            }

            // IRST (own + data-linked)
            if (vrd != null && vrd.DisplayedIRTargets != null)
            {
                var list = vrd.DisplayedIRTargets;
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t.vessel == Own) continue;
                    var src = t.detectedByIRST != null && t.detectedByIRST.vessel != Own ? ContactSource.Datalink : ContactSource.IRST;
                    var c = Get(t.vessel, t.targetData.predictedPosition, t.targetData.velocity, src, t.targetData.Team, myTeam);
                    c.isMissile |= t.targetData.targetInfo != null && t.targetData.targetInfo.isMissile;
                }
            }

            // Team database (visual and radar reports from the whole team)
            {
                var teamTargets = BDATargetManager.TargetList(myTeam);
                for (int i = 0; i < teamTargets.Count; i++)
                {
                    var ti = teamTargets[i];
                    if (ti == null || ti.Vessel == null || ti.Vessel == Own) continue;
                    var c = Get(ti.Vessel, ti.Vessel.CoM, ti.Vessel.Velocity(), ContactSource.Team, ti.Team, myTeam);
                    c.isMissile |= ti.isMissile;
                }
            }

            // Targeting camera track
            var cam = ActiveCamera;
            if (cam != null && cam.lockedVessel != null && cam.lockedVessel != Own)
                Get(cam.lockedVessel, cam.lockedVessel.CoM, cam.lockedVessel.Velocity(), ContactSource.TGP, TeamOf(cam.lockedVessel), myTeam);

            // GPS points
            var gpsList = BDATargetManager.GPSTargetList(myTeam);
            for (int i = 0; i < gpsList.Count; i++)
            {
                var g = gpsList[i];
                var c = pool.Count > 0 ? pool.Pop() : new Contact();
                Reset(c);
                c.gps = g;
                c.gpsIndex = i;
                c.name = g.name;
                c.position = g.worldPos;
                c.sources = ContactSource.GPS;
                c.iff = IFF.Unknown;
                Contacts.Add(c);
            }

            // Derived values
            Vector3 ownPos = Own.CoM;
            Vector3 ownVel = Own.Velocity();
            var body = Own.mainBody;
            for (int i = 0; i < Contacts.Count; i++)
            {
                var c = Contacts[i];
                Vector3 rel = c.position - ownPos;
                c.range = rel.magnitude;
                c.altitude = body != null ? FlightGlobals.getAltitudeAtPos(c.position, body) : 0;
                c.closure = c.range > 1 ? -Vector3.Dot(c.velocity - ownVel, rel / c.range) : 0;
                if (c.name == null) c.name = c.vessel != null ? c.vessel.GetName() : "Unknown";
            }
            Contacts.Sort((a, b) => a.range.CompareTo(b.range));
        }

        static Contact Get(Vessel v, Vector3 pos, Vector3 vel, ContactSource src, BDTeam team, BDTeam myTeam)
        {
            Contact c;
            if (v != null && byVessel.TryGetValue(v, out c))
            {
                // Prefer own-sensor data over team/datalink reports for position
                if ((src & (ContactSource.Radar | ContactSource.IRST)) != 0) { c.position = pos; c.velocity = vel; }
                c.sources |= src;
                if (c.iff == IFF.Unknown && team != null) c.iff = GetIFF(team, myTeam);
                return c;
            }
            c = pool.Count > 0 ? pool.Pop() : new Contact();
            Reset(c);
            c.vessel = v;
            c.position = pos;
            c.velocity = vel;
            c.sources = src;
            c.iff = team != null ? GetIFF(team, myTeam) : IFF.Unknown;
            c.name = v != null ? v.GetName() : "Unknown";
            Contacts.Add(c);
            if (v != null) byVessel[v] = c;
            return c;
        }

        static void Reset(Contact c)
        {
            c.vessel = null; c.name = null; c.position = Vector3.zero; c.velocity = Vector3.zero;
            c.sources = ContactSource.None; c.iff = IFF.Unknown; c.isMissile = false;
            c.radarLocked = false; c.activeLock = false; c.gpsIndex = -1; c.gps = default;
            c.range = 0; c.altitude = 0; c.closure = 0;
        }

        static IFF GetIFF(BDTeam team, BDTeam myTeam)
        {
            if (team == null || myTeam == null) return IFF.Unknown;
            if (team.Neutral) return IFF.Neutral;
            if (myTeam.IsEnemy(team)) return IFF.Hostile;
            return IFF.Friendly;
        }

        static BDTeam TeamOf(Vessel v)
        {
            if (v == null) return null;
            var ti = v.gameObject.GetComponent<TargetInfo>();
            return ti != null ? ti.Team : null;
        }

        public static ModuleTargetingCamera ActiveCamera
        {
            get
            {
                if (WM == null) return null;
                if (ModuleTargetingCamera.activeCam != null && ModuleTargetingCamera.activeCam.vessel == WM.vessel && ModuleTargetingCamera.activeCam.cameraEnabled)
                    return ModuleTargetingCamera.activeCam;
                var pods = WM.targetingPods;
                for (int i = 0; i < pods.Count; i++)
                    if (pods[i] != null && pods[i].cameraEnabled) return pods[i];
                return null;
            }
        }

        // ---- Actions ----

        public static string LastMessage = "";
        public static float LastMessageTime = -100;
        static void Message(string m) { LastMessage = m; LastMessageTime = Time.time; }

        public static void Select(Contact c) => SelectedKey = c?.Key;

        /// <summary>Lock with radar if a radar can see it, and make it the weapon manager's target.</summary>
        public static void Lock(Contact c)
        {
            if (c == null || WM == null) return;
            Select(c);
            if (c.IsGPS) { SetGPSTarget(c); return; }
            bool radarOk = false;
            var vrd = WM.vesselRadarData;
            if (vrd != null && c.vessel != null && (c.sources & (ContactSource.Radar | ContactSource.Datalink)) != 0)
            {
                if (c.radarLocked) radarOk = vrd.SwitchActiveLockedTarget(c.vessel);
                else radarOk = vrd.TryLockTarget(c.vessel);
            }
            else if (vrd != null && c.vessel == null && (c.sources & ContactSource.Radar) != 0)
            {
                vrd.TryLockTarget(c.position);
                radarOk = true;
            }
            if (c.vessel != null)
            {
                var ti = c.vessel.gameObject.GetComponent<TargetInfo>();
                if (ti != null) WM.SetTarget(ti);
            }
            Message(radarOk ? $"Locking {c.name}" : c.vessel != null ? $"{c.name} set as target (no radar lock)" : "Can't lock this contact");
        }

        public static void Unlock(Contact c)
        {
            if (c == null || WM == null || c.vessel == null) return;
            var vrd = WM.vesselRadarData;
            if (vrd == null || !c.radarLocked) return;
            if (vrd.SwitchActiveLockedTarget(c.vessel)) vrd.UnlockCurrentTarget();
            Message($"Unlocked {c.name}");
        }

        public static void SlewCamera(Contact c)
        {
            var cam = ActiveCamera;
            if (c == null) return;
            if (cam == null) { Message("No targeting camera is on"); return; }
            cam.StartCoroutine(cam.PointToPositionRoutine(c.vessel != null ? c.vessel.CoM : c.position, -1, c.vessel));
            Message($"Camera slewing to {c.name}");
        }

        public static void MakeGPS(Contact c)
        {
            if (c == null || WM == null || Own == null) return;
            Vector3d geo = VectorUtils.WorldPositionToGeoCoords(c.vessel != null ? c.vessel.CoM : c.position, Own.mainBody);
            string name = SanitizeGPSName(c.name);
            BDATargetManager.GPSTargetList(WM.Team).Add(new GPSTargetInfo(geo, name));
            BDATargetManager.Instance.SaveGPSTargets();
            Message($"Saved GPS point \"{name}\"");
        }

        public static void AddGPS(Vector3d geo, string name)
        {
            if (WM == null) { Message("No weapon manager on this vessel"); return; }
            name = SanitizeGPSName(name);
            BDATargetManager.GPSTargetList(WM.Team).Add(new GPSTargetInfo(geo, name));
            BDATargetManager.Instance.SaveGPSTargets();
            Message($"Saved GPS point \"{name}\"");
        }

        public static void SetGPSTarget(Contact c)
        {
            if (c == null || WM == null || !c.IsGPS) return;
            WM.designatedGPSInfo = c.gps;
            WM.designatedGPSCoordsIndex = c.gpsIndex;
            Message($"GPS target: {c.name}");
        }

        /// <summary>BDArmory's GPS save format can't hold ';' ':' or ',' and the stock editor limits names to 12 characters.</summary>
        public static string SanitizeGPSName(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "Target";
            name = name.Replace(";", "").Replace(":", "").Replace(",", "").Trim();
            if (name.Length > 12) name = name.Substring(0, 12);
            return name.Length == 0 ? "Target" : name;
        }

        /// <summary>Hostile and unknown non-GPS contacts in range order, used for hotkey cycling.</summary>
        public static void Cycle(int dir)
        {
            var candidates = new List<Contact>();
            for (int i = 0; i < Contacts.Count; i++)
            {
                var c = Contacts[i];
                if (c.IsGPS || c.iff == IFF.Friendly) continue;
                candidates.Add(c);
            }
            if (candidates.Count == 0) { Message("No contacts"); return; }
            int idx = candidates.FindIndex(c => c.Key == SelectedKey);
            idx = idx < 0 ? (dir > 0 ? 0 : candidates.Count - 1) : (idx + dir + candidates.Count) % candidates.Count;
            Select(candidates[idx]);
            Message($"Selected {candidates[idx].name}");
        }
    }
}
