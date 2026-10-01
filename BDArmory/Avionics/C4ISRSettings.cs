using UnityEngine;

namespace BDArmory.Avionics
{
    public enum LinkModel { CommNet, LineOfSight }
    public enum EmconLevel { A_AllOn, B_CueOn, C_EWROnly, D_Silent }
    public enum MapSource { KerbNet, SCANsat, Grid }

    /// <summary>
    /// Settings for the data link network, IADS and the C2 map. Stored in the C4ISR node of Avionics.cfg.
    /// </summary>
    public static class C4ISRSettings
    {
        // Command and control
        public static bool GlobalC2 = true;            // whole team linked, no range limit
        public static LinkModel LinkModel = LinkModel.CommNet; // used when GlobalC2 is off
        public static float LOSLinkRangeKm = 150f;
        public static bool RequireAntenna = true;      // CommNet model: a vessel with no CommNet connection is cut off

        // Tracks
        public static float NetUpdateHz = 4f;
        public static float TrackTimeout = 8f;         // seconds without a report before a track is dropped

        // IADS
        public static bool IADSEnabled = true;
        public static bool IADSForAITeams = true;      // run a controller for every team, not only the player's
        public static bool IADSAutoAssign = true;
        public static bool LaunchOnRemote = true;
        public static bool ShootLookShoot = true;
        public static bool EngageMissiles = false;
        public static int Salvo = 2;
        public static EmconLevel Emcon = EmconLevel.B_CueOn;
        public static float WakeRingKm = 60f;

        // Map
        public static MapSource MapSource = MapSource.KerbNet;
        public static bool MapRequiresCoverage = true;

        // Windows and keys (the windows themselves are built once a layout is chosen)
        public static KeyCode KeyC2Window = KeyCode.C;    // with RightAlt
        public static KeyCode KeyIADSWindow = KeyCode.I;  // with RightAlt
        public static KeyCode KeyLinkWindow = KeyCode.L;  // with RightAlt

        public static void Load(ConfigNode n)
        {
            if (n == null) return;
            GlobalC2 = Get(n, nameof(GlobalC2), GlobalC2);
            LinkModel = GetEnum(n, nameof(LinkModel), LinkModel);
            LOSLinkRangeKm = Get(n, nameof(LOSLinkRangeKm), LOSLinkRangeKm);
            RequireAntenna = Get(n, nameof(RequireAntenna), RequireAntenna);
            NetUpdateHz = Mathf.Clamp(Get(n, nameof(NetUpdateHz), NetUpdateHz), 0.5f, 20f);
            TrackTimeout = Mathf.Clamp(Get(n, nameof(TrackTimeout), TrackTimeout), 1f, 120f);
            IADSEnabled = Get(n, nameof(IADSEnabled), IADSEnabled);
            IADSForAITeams = Get(n, nameof(IADSForAITeams), IADSForAITeams);
            IADSAutoAssign = Get(n, nameof(IADSAutoAssign), IADSAutoAssign);
            LaunchOnRemote = Get(n, nameof(LaunchOnRemote), LaunchOnRemote);
            ShootLookShoot = Get(n, nameof(ShootLookShoot), ShootLookShoot);
            EngageMissiles = Get(n, nameof(EngageMissiles), EngageMissiles);
            Salvo = Mathf.Clamp((int)Get(n, nameof(Salvo), (float)Salvo), 1, 8);
            Emcon = GetEnum(n, nameof(Emcon), Emcon);
            WakeRingKm = Get(n, nameof(WakeRingKm), WakeRingKm);
            MapSource = GetEnum(n, nameof(MapSource), MapSource);
            MapRequiresCoverage = Get(n, nameof(MapRequiresCoverage), MapRequiresCoverage);
            KeyC2Window = GetEnum(n, nameof(KeyC2Window), KeyC2Window);
            KeyIADSWindow = GetEnum(n, nameof(KeyIADSWindow), KeyIADSWindow);
            KeyLinkWindow = GetEnum(n, nameof(KeyLinkWindow), KeyLinkWindow);
        }

        public static void Save(ConfigNode n)
        {
            n.AddValue(nameof(GlobalC2), GlobalC2);
            n.AddValue(nameof(LinkModel), LinkModel.ToString());
            n.AddValue(nameof(LOSLinkRangeKm), LOSLinkRangeKm);
            n.AddValue(nameof(RequireAntenna), RequireAntenna);
            n.AddValue(nameof(NetUpdateHz), NetUpdateHz);
            n.AddValue(nameof(TrackTimeout), TrackTimeout);
            n.AddValue(nameof(IADSEnabled), IADSEnabled);
            n.AddValue(nameof(IADSForAITeams), IADSForAITeams);
            n.AddValue(nameof(IADSAutoAssign), IADSAutoAssign);
            n.AddValue(nameof(LaunchOnRemote), LaunchOnRemote);
            n.AddValue(nameof(ShootLookShoot), ShootLookShoot);
            n.AddValue(nameof(EngageMissiles), EngageMissiles);
            n.AddValue(nameof(Salvo), Salvo);
            n.AddValue(nameof(Emcon), Emcon.ToString());
            n.AddValue(nameof(WakeRingKm), WakeRingKm);
            n.AddValue(nameof(MapSource), MapSource.ToString());
            n.AddValue(nameof(MapRequiresCoverage), MapRequiresCoverage);
            n.AddValue(nameof(KeyC2Window), KeyC2Window.ToString());
            n.AddValue(nameof(KeyIADSWindow), KeyIADSWindow.ToString());
            n.AddValue(nameof(KeyLinkWindow), KeyLinkWindow.ToString());
        }

        public static string EmconName(EmconLevel e) => e switch
        {
            EmconLevel.A_AllOn => "A: all radars on",
            EmconLevel.B_CueOn => "B: SAM radars on when cued",
            EmconLevel.C_EWROnly => "C: early-warning radars only",
            _ => "D: all radars silent",
        };

        static bool Get(ConfigNode n, string k, bool d) => n.HasValue(k) && bool.TryParse(n.GetValue(k), out var v) ? v : d;
        static float Get(ConfigNode n, string k, float d) => n.HasValue(k) && float.TryParse(n.GetValue(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
        static T GetEnum<T>(ConfigNode n, string k, T d) where T : struct => n.HasValue(k) && System.Enum.TryParse(n.GetValue(k), true, out T v) ? v : d;
    }
}
