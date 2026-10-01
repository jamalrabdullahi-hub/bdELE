using System.Collections.Generic;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.UI;
using BDArmory.Weapons.Missiles;
using RWR = BDArmory.Radar.RadarWarningReceiver;

namespace BDArmory.Avionics
{
    public class Threat
    {
        public Vessel source;
        public Vector3 position;
        public RWR.RWRThreatTypes type;
        public float lastSeen;
        public float expires;
        public bool isMissile;
        public bool heatSeeker;      // IR missile (flares) vs radar missile (chaff)
        public float timeToImpact = -1;

        public int Priority => isMissile ? 0 : type == RWR.RWRThreatTypes.MissileLaunch ? 1 : type == RWR.RWRThreatTypes.MissileLock ? 2
            : type == RWR.RWRThreatTypes.Jamming ? 4 : 3;

        public string Kind => isMissile ? (heatSeeker ? "MISSILE (IR)" : "MISSILE") : type switch
        {
            RWR.RWRThreatTypes.MissileLaunch => "LAUNCH",
            RWR.RWRThreatTypes.MissileLock => "LOCK",
            RWR.RWRThreatTypes.SAM => "SAM",
            RWR.RWRThreatTypes.Fighter => "FIGHTER",
            RWR.RWRThreatTypes.AWACS => "AWACS",
            RWR.RWRThreatTypes.Jamming => "JAMMER",
            RWR.RWRThreatTypes.MWS => "MWS",
            _ => "RADAR",
        };

        public Color Color => isMissile || type == RWR.RWRThreatTypes.MissileLaunch ? AvionicsDraw.Missile
            : type == RWR.RWRThreatTypes.MissileLock ? AvionicsDraw.Hostile
            : type == RWR.RWRThreatTypes.Jamming ? AvionicsDraw.Neutral : AvionicsDraw.Unknown;
    }

    /// <summary>
    /// Threat picture for the active vessel, from BDArmory's radar warning receiver events (radar pings, locks, launches)
    /// and the weapon manager's incoming-missile tracking. Radar warnings need an enabled RWR on the vessel, like the stock RWR display.
    /// Shown as strobes on the 3D radar and a warning banner at the top of the screen.
    /// </summary>
    public static class ThreatWarning
    {
        public static readonly List<Threat> Threats = new();
        static readonly Dictionary<string, Threat> byKey = new();
        static bool subscribed;

        public static Threat Top { get; private set; }

        public static void Subscribe()
        {
            if (subscribed) return;
            RWR.OnRadarPing += OnPing;
            RWR.OnMissileLaunch += OnLaunch;
            subscribed = true;
        }

        public static void Unsubscribe()
        {
            if (!subscribed) return;
            RWR.OnRadarPing -= OnPing;
            RWR.OnMissileLaunch -= OnLaunch;
            subscribed = false;
            Threats.Clear();
            byKey.Clear();
            Top = null;
        }

        static bool HasRWR
        {
            get
            {
                var wm = AvionicsContacts.WM;
                var rwr = wm != null ? wm.rwr : null;
                return rwr != null && rwr.rwrEnabled;
            }
        }

        static void OnPing(Vessel v, Vector3 source, RWR.RWRThreatTypes type, float persistTime, Vessel vSource)
        {
            if (v == null || v != FlightGlobals.ActiveVessel || !HasRWR) return;
            if (type == RWR.RWRThreatTypes.Detection || type == RWR.RWRThreatTypes.None) return;
            var t = Get($"{(vSource != null ? vSource.persistentId.ToString() : source.ToString("F0"))}:{type}");
            t.source = vSource;
            t.position = vSource != null ? (Vector3)vSource.CoM : source;
            t.type = type;
            t.lastSeen = Time.time;
            t.expires = Time.time + Mathf.Max(persistTime, 2f);
        }

        static void OnLaunch(Vector3 source, Vector3 direction, bool radar, Vessel vSource)
        {
            var own = FlightGlobals.ActiveVessel;
            if (own == null || !HasRWR) return;
            // Only launches pointed roughly at us
            Vector3 toUs = (Vector3)own.CoM - source;
            if (toUs.sqrMagnitude > 80000f * 80000f || Vector3.Angle(direction, toUs) > 35f) return;
            var t = Get($"launch:{(vSource != null ? vSource.persistentId.ToString() : source.ToString("F0"))}");
            t.source = vSource;
            t.position = source;
            t.type = RWR.RWRThreatTypes.MissileLaunch;
            t.heatSeeker = !radar;
            t.lastSeen = Time.time;
            t.expires = Time.time + 4f;
        }

        static Threat Get(string key)
        {
            if (!byKey.TryGetValue(key, out var t))
            {
                t = new Threat();
                byKey[key] = t;
                Threats.Add(t);
            }
            t.isMissile = false;
            return t;
        }

        /// <summary>Called every frame by the avionics core.</summary>
        public static void Refresh()
        {
            float now = Time.time;
            var wm = AvionicsContacts.WM;

            // Incoming missile from the weapon manager's own missile warning
            if (wm != null && wm.missileIsIncoming && wm.incomingMissileVessel != null)
            {
                var mv = wm.incomingMissileVessel;
                var t = Get($"msl:{mv.persistentId}");
                t.isMissile = true;
                t.source = mv;
                t.position = mv.CoM;
                t.type = RWR.RWRThreatTypes.MissileLock;
                var mb = mv.FindPartModuleImplementing<MissileBase>();
                t.heatSeeker = mb != null && mb.TargetingMode == MissileBase.TargetingModes.Heat;
                t.timeToImpact = wm.incomingMissileTime;
                t.lastSeen = now;
                t.expires = now + 1f;
            }

            // Track moving sources, drop stale ones
            for (int i = Threats.Count - 1; i >= 0; i--)
            {
                var t = Threats[i];
                if (t.source != null && t.source.loaded) t.position = t.source.CoM;
                if (now > t.expires || (t.isMissile && t.source == null))
                {
                    Threats.RemoveAt(i);
                    foreach (var kv in byKey) if (kv.Value == t) { byKey.Remove(kv.Key); break; }
                }
            }
            Threats.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.timeToImpact.CompareTo(b.timeToImpact));
            Top = Threats.Count > 0 ? Threats[0] : null;
        }

        /// <summary>"4 o'clock high" style direction from the active vessel.</summary>
        public static string Clock(Vector3 position)
        {
            var own = FlightGlobals.ActiveVessel;
            if (own == null) return "";
            Vector3 up = own.upAxis;
            Vector3 rel = position - own.CoM;
            Vector3 nose = Vector3.ProjectOnPlane(own.ReferenceTransform.up, up);
            if (nose.sqrMagnitude < 1e-4f) nose = Vector3.ProjectOnPlane(-own.ReferenceTransform.forward, up);
            float bearing = Vector3.SignedAngle(nose, Vector3.ProjectOnPlane(rel, up), up);
            int clock = Mathf.RoundToInt(((bearing + 360f) % 360f) / 30f);
            if (clock == 0) clock = 12;
            float elev = Vector3.Angle(rel, up);
            string hl = elev < 70 ? " high" : elev > 110 ? " low" : "";
            return $"{clock} o'clock{hl}";
        }

        /// <summary>Warning banner at the top of the flight view (also drawn in map view).</summary>
        public static void DrawBanner()
        {
            if (!AvionicsSettings.ThreatBanner || Event.current.type != EventType.Repaint) return;
            var t = Top;
            if (t == null) return;
            if (!t.isMissile && t.type != RWR.RWRThreatTypes.MissileLaunch && t.type != RWR.RWRThreatTypes.MissileLock) return; // only the urgent ones
            bool blink = t.isMissile && Mathf.Repeat(Time.time, 0.5f) < 0.25f;
            string range = AvionicsDraw.FormatRange((t.position - (Vector3)FlightGlobals.ActiveVessel.CoM).magnitude);
            string tti = t.timeToImpact > 0 && t.timeToImpact < 999 ? $"  {t.timeToImpact:F1} s" : "";
            string text = $"{t.Kind}   {Clock(t.position)}   {range}{tti}";
            int others = Threats.Count - 1;
            if (others > 0) text += $"   (+{others})";
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;
            var r = new Rect(Screen.width / 2f - 230, 70, 460, 24);
            if (AvionicsDraw.Begin()) { AvionicsDraw.FilledRect(r, new Color(0.15f, 0.02f, 0.02f, blink ? 0.95f : 0.75f)); AvionicsDraw.End(); }
            AvionicsDraw.ColorLabel(new Rect(r.x, r.y + 5, r.width, 16), text, AvionicsDraw.LabelCenter, blink ? Color.white : t.Color);
            GUI.matrix = old;
        }
    }
}
