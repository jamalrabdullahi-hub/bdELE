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

        // Windows
        public static bool ShowConsole = false;
        public static bool ShowMapOverlay = true;      // C2 symbols in KSP's map view
        public static bool ShowIADS = false;
        public static bool ShowLink = false;
        public static bool ShowSettings = false;
        public static Rect ConsoleRect = new(60, 80, 1120, 680);
        public static Rect IADSRect = new(120, 120, 900, 520);
        public static Rect LinkRect = new(160, 140, 640, 420);
        public static Rect SettingsRect = new(200, 120, 420, 560);
        public static Rect DrawerRect = new(-1, 80, 300, 560); // map overlay drawer; x < 0 = docked right

        // Console map
        public static C2MapLayer MapLayer = C2MapLayer.Terrain;
        public static int MapZoomIndex = 3;
        public static readonly float[] MapSpansKm = [10, 20, 40, 80, 160, 320, 640, 1280];
        public static bool MapRings = true;
        public static bool MapTracks = true;
        public static bool OverlayTracks = true, OverlayLinks = true, OverlayRings = false, OverlayLabels = true;

        // Keys (pressed together with RightAlt)
        public static KeyCode KeyC2Window = KeyCode.C;
        public static KeyCode KeyIADSWindow = KeyCode.I;
        public static KeyCode KeyLinkWindow = KeyCode.L;

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
            ShowConsole = Get(n, nameof(ShowConsole), ShowConsole);
            ShowMapOverlay = Get(n, nameof(ShowMapOverlay), ShowMapOverlay);
            ShowIADS = Get(n, nameof(ShowIADS), ShowIADS);
            ShowLink = Get(n, nameof(ShowLink), ShowLink);
            ConsoleRect = GetRect(n, nameof(ConsoleRect), ConsoleRect);
            IADSRect = GetRect(n, nameof(IADSRect), IADSRect);
            LinkRect = GetRect(n, nameof(LinkRect), LinkRect);
            SettingsRect = GetRect(n, nameof(SettingsRect), SettingsRect);
            DrawerRect = GetRect(n, nameof(DrawerRect), DrawerRect);
            MapLayer = GetEnum(n, nameof(MapLayer), MapLayer);
            MapZoomIndex = Mathf.Clamp((int)Get(n, nameof(MapZoomIndex), (float)MapZoomIndex), 0, MapSpansKm.Length - 1);
            MapRings = Get(n, nameof(MapRings), MapRings);
            MapTracks = Get(n, nameof(MapTracks), MapTracks);
            OverlayTracks = Get(n, nameof(OverlayTracks), OverlayTracks);
            OverlayLinks = Get(n, nameof(OverlayLinks), OverlayLinks);
            OverlayRings = Get(n, nameof(OverlayRings), OverlayRings);
            OverlayLabels = Get(n, nameof(OverlayLabels), OverlayLabels);
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
            n.AddValue(nameof(ShowConsole), ShowConsole);
            n.AddValue(nameof(ShowMapOverlay), ShowMapOverlay);
            n.AddValue(nameof(ShowIADS), ShowIADS);
            n.AddValue(nameof(ShowLink), ShowLink);
            n.AddValue(nameof(ConsoleRect), RectString(ConsoleRect));
            n.AddValue(nameof(IADSRect), RectString(IADSRect));
            n.AddValue(nameof(LinkRect), RectString(LinkRect));
            n.AddValue(nameof(SettingsRect), RectString(SettingsRect));
            n.AddValue(nameof(DrawerRect), RectString(DrawerRect));
            n.AddValue(nameof(MapLayer), MapLayer.ToString());
            n.AddValue(nameof(MapZoomIndex), MapZoomIndex);
            n.AddValue(nameof(MapRings), MapRings);
            n.AddValue(nameof(MapTracks), MapTracks);
            n.AddValue(nameof(OverlayTracks), OverlayTracks);
            n.AddValue(nameof(OverlayLinks), OverlayLinks);
            n.AddValue(nameof(OverlayRings), OverlayRings);
            n.AddValue(nameof(OverlayLabels), OverlayLabels);
        }

        static string RectString(Rect r) => System.FormattableString.Invariant($"{r.x:F0};{r.y:F0};{r.width:F0};{r.height:F0}");
        static Rect GetRect(ConfigNode n, string k, Rect d)
        {
            if (!n.HasValue(k)) return d;
            var p = n.GetValue(k).Split(';');
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var f = System.Globalization.NumberStyles.Float;
            return p.Length == 4 && float.TryParse(p[0], f, ci, out var x) && float.TryParse(p[1], f, ci, out var y)
                && float.TryParse(p[2], f, ci, out var w) && float.TryParse(p[3], f, ci, out var h) ? new Rect(x, y, w, h) : d;
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
