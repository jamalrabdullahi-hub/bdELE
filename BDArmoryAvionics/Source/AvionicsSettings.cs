using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Settings for the avionics windows (3D radar, target list, GPS manager, FLIR, TGP HUD).
    /// Stored in GameData/BDArmoryAvionics/PluginData/Avionics.cfg (never in BDArmory's folder).
    /// </summary>
    public static class AvionicsSettings
    {
        static string SettingsPath => Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmoryAvionics/PluginData/Avionics.cfg");

        // Settings written by earlier builds of these features; read once if this mod has no settings file yet.
        static readonly string[] LegacyPaths =
        [
            "GameData/BDTargetingExpansion/PluginData/Settings.cfg",
            "GameData/BDArmory/PluginData/Avionics.cfg",
        ];

        static string LoadPath()
        {
            if (File.Exists(SettingsPath)) return SettingsPath;
            foreach (var p in LegacyPaths)
            {
                var full = Path.Combine(KSPUtil.ApplicationRootPath, p);
                if (File.Exists(full)) return full;
            }
            return null;
        }

        // Windows
        public static bool ShowRadar3D = false;
        public static bool ShowTargetList = false;
        public static bool ShowGPSManager = false;
        public static Rect Radar3DRect = new(80, 120, 420, 470);
        public static Rect TargetListRect = new(520, 120, 560, 300);
        public static Rect GPSManagerRect = new(520, 440, 520, 320);

        // 3D radar
        public static int Radar3DRangeIndex = 3;
        public static float Radar3DYaw = 0f;
        public static float Radar3DElevation = 30f;
        public static int Radar3DAltScaleIndex = 1;
        public static bool Radar3DNorthUp = false;
        public static bool Radar3DShowIRST = true;
        public static bool Radar3DShowTeam = true;
        public static bool Radar3DShowDebris = true;

        // Pilot AI formation keeping
        public static bool FormationAssist = true;
        public static float FormationMaxBank = 30f; // degrees of bank allowed for sideways slot corrections

        // Pilot AI takeoff / landing / refuelling
        public static bool TakeoffAssist = true;
        public static float RotateRate = 3f;          // degrees per second of nose-up during rotation
        public static float MaxRotateAttitude = 10f;  // degrees: nose attitude cap while the wheels are on the ground
        public static bool AirOpsTelemetry = false;   // log formation / landing / refuelling state every 2 s for tuning
        public static bool ShowAllAITweakables = true; // show every pilot AI setting (incl. Unclamp tuning) without KSP's Advanced Tweakables
        public static bool AltitudeFromSeaLevel = true; // AI altitude settings measured from sea level (true) or the ground below (stock)
        public static bool CombatOperators = true;      // crew skill affects guard mode / weapons / AI; Astronaut Complex training
        public static bool AARPopup = true;             // open the after-action report when an engagement ends
        public static int StealthModel = 1;             // 0 stock RCS, 1 low, 2 medium, 3 high specular shaping
        public static bool EWLinkJamming = true;        // enemy jammers degrade / cut data links
        public static bool EWESM = true;                // RWR-equipped nodes share passive emitter tracks
        public static bool ShowEW = false;
        public static bool WorldContactMarkers = false;

        // Target list
        public static bool TargetListShowFriendlies = true;
        public static bool TargetListShowMissiles = true;
        public static bool TargetListShowGPS = true;

        // GPS
        public static bool GPSWorldMarkers = true;
        public static bool GPSMapMarkers = true;
        public static float GPSMarkerMaxDistanceKm = 100f;
        public static int GPSSortMode = 0; // 0 = range, 1 = name, 2 = group
        public static string GPSGroupFilter = "";
        public static Dictionary<string, string> GPSGroups = new();

        // Targeting camera
        public static int FLIRMode = 0; // 0 = off (stock), 1 = white-hot, 2 = black-hot, 3 = green
        public static float FLIRContrast = 1.3f;
        public static bool FLIRHeatGlow = true;
        public static bool TGPHud = true;

        // Hotkeys (Unity KeyCode names). Set to "None" to disable.
        public static KeyCode KeyNextTarget = KeyCode.Keypad6;
        public static KeyCode KeyPrevTarget = KeyCode.Keypad4;
        public static KeyCode KeyLockTarget = KeyCode.Keypad5;
        public static KeyCode KeySlewCamera = KeyCode.Keypad8;
        public static KeyCode KeyEject = KeyCode.P; // press twice
        public static KeyCode KeyCMProgram = KeyCode.Keypad0; // run the craft's countermeasure program
        public static KeyCode KeyLMStrike = KeyCode.KeypadEnter;   // loitering munition: strike the crosshair / approve
        public static KeyCode KeyLMManual = KeyCode.Keypad5;       // loitering munition: take / give control
        public static KeyCode KeyLMDetonate = KeyCode.KeypadPeriod;
        public static KeyCode KeyLMReturn = KeyCode.KeypadMinus;   // back to the operator's craft
        public static bool ShowLM = false;
        public static bool MissileDLZ = true;
        public static bool ShowFeeds = false;
        public static int Difficulty = 1;               // PvE: 0 Recruit, 1 Regular, 2 Veteran, 3 Ace
        public static bool FCSAll = true;               // ELE flight control on every AI aircraft (not only craft with the part-menu toggle on)
        public static float TheatreSpawnKm = 150f;      // AI flights materialise inside this slant range (atmospheric craft only)
        public static bool RadarTerrainMask = true;     // ELE sensor engine: radars can't see through hills at any range
        public static bool KrakenGuard = true;          // gentle unpack, stiffer solver, runaway-energy watchdog
        public static bool KrakenWatchdog = true;
        public static bool Profiler = false;            // ELE performance profiler window
        public static bool PhysicsLOD = true;           // full physics near / engaged, rigid on rails further, unloaded ghosts beyond
        public static float LODUnpackKm = 8f, LODLoadKm = 30f, LODMarkerKm = 400f;
        public static bool LODMarkers = true;
        public static bool DynamicDamage = true;        // dents, scorch marks and bent parts from hits
        public static float DamageIntensity = 1f;
        public static bool VaporCones = true;           // transonic vapor cones + shock ring at Mach 1 (volumetric)
        public static bool Volumetric = true;           // real volumetric explosions / haze / shock (needs the ELE shader bundle)
        public static int ExplosionQuality = 1;         // 0 off, 1 low, 2 medium, 3 high (extra explosion layers)
        public static bool ExplosionShake = true;
        public static bool ExplosionScale = true;       // fireballs sized by warhead (cube root of TNT, 20 kg = 1x)
        public static float ExplosionSizeCurve = 1f, ExplosionSizeCap = 6f;
        public static bool Shrapnel = true;             // fragments on every explosion, out to ShrapnelRangeMult x the blast radius
        public static float ShrapnelRangeMult = 2f;
        public static int ShrapnelPattern = 0;          // Auto, Sphere, Half sphere, Cone
        public static bool AutoFeeds = true;            // OCISLY: stream munition / CSAR cameras automatically          // modular missiles get their launch range from the flight estimator
        public static KeyCode KeyVTOLHover = KeyCode.Keypad7;   // VTOL assist: hover hold on/off
        public static KeyCode KeyVTOLLand = KeyCode.Keypad9;    // VTOL assist: auto-land

        // Ejection seats
        public static bool AutoEjectOnDestruction = true;
        public static bool AutoEjectBeforeImpact = false; // removed feature: never ejects before ground impact

        // Threat warning
        public static bool ThreatBanner = true;
        public static bool Radar3DShowThreats = true;

        public static readonly float[] Radar3DRanges = [2000, 5000, 10000, 20000, 40000, 80000, 160000, 320000];
        public static readonly float[] AltScales = [1, 2, 4, 8];

        static bool loaded = false;

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            var path = LoadPath();
            if (path == null) return;
            try
            {
                var root = ConfigNode.Load(path);
                C4ISRSettings.Load(root?.GetNode("C4ISR"));
                var node = root?.GetNode("AVIONICS");
                if (node == null) return;

                ShowRadar3D = GetBool(node, "ShowRadar3D", ShowRadar3D);
                ShowTargetList = GetBool(node, "ShowTargetList", ShowTargetList);
                ShowGPSManager = GetBool(node, "ShowGPSManager", ShowGPSManager);
                Radar3DRect = GetRect(node, "Radar3DRect", Radar3DRect);
                TargetListRect = GetRect(node, "TargetListRect", TargetListRect);
                GPSManagerRect = GetRect(node, "GPSManagerRect", GPSManagerRect);

                Radar3DRangeIndex = Mathf.Clamp(GetInt(node, "Radar3DRangeIndex", Radar3DRangeIndex), 0, Radar3DRanges.Length - 1);
                Radar3DYaw = GetFloat(node, "Radar3DYaw", Radar3DYaw);
                Radar3DElevation = Mathf.Clamp(GetFloat(node, "Radar3DElevation", Radar3DElevation), 0, 90);
                Radar3DAltScaleIndex = Mathf.Clamp(GetInt(node, "Radar3DAltScaleIndex", Radar3DAltScaleIndex), 0, AltScales.Length - 1);
                Radar3DNorthUp = GetBool(node, "Radar3DNorthUp", Radar3DNorthUp);
                Radar3DShowIRST = GetBool(node, "Radar3DShowIRST", Radar3DShowIRST);
                Radar3DShowTeam = GetBool(node, "Radar3DShowTeam", Radar3DShowTeam);
                Radar3DShowDebris = GetBool(node, "Radar3DShowDebris", Radar3DShowDebris);
                FormationAssist = GetBool(node, "FormationAssist", FormationAssist);
                FormationMaxBank = Mathf.Clamp(GetFloat(node, "FormationMaxBank", FormationMaxBank), 5f, 60f);
                TakeoffAssist = GetBool(node, "TakeoffAssist", TakeoffAssist);
                RotateRate = Mathf.Clamp(GetFloat(node, "RotateRate", RotateRate), 0.5f, 15f);
                MaxRotateAttitude = Mathf.Clamp(GetFloat(node, "MaxRotateAttitude", MaxRotateAttitude), 3f, 25f);
                AirOpsTelemetry = GetBool(node, "AirOpsTelemetry", AirOpsTelemetry);
                ShowAllAITweakables = GetBool(node, "ShowAllAITweakables", ShowAllAITweakables);
                AltitudeFromSeaLevel = GetBool(node, "AltitudeFromSeaLevel", AltitudeFromSeaLevel);
                CombatOperators = GetBool(node, "CombatOperators", CombatOperators);
                AARPopup = GetBool(node, "AARPopup", AARPopup);
                if (int.TryParse(node.GetValue("StealthModel"), out int sm)) StealthModel = Mathf.Clamp(sm, 0, 3);
                EWLinkJamming = GetBool(node, "EWLinkJamming", EWLinkJamming);
                EWESM = GetBool(node, "EWESM", EWESM);
                ShowEW = GetBool(node, "ShowEW", ShowEW);
                WorldContactMarkers = GetBool(node, "WorldContactMarkers", WorldContactMarkers);

                TargetListShowFriendlies = GetBool(node, "TargetListShowFriendlies", TargetListShowFriendlies);
                TargetListShowMissiles = GetBool(node, "TargetListShowMissiles", TargetListShowMissiles);
                TargetListShowGPS = GetBool(node, "TargetListShowGPS", TargetListShowGPS);

                GPSWorldMarkers = GetBool(node, "GPSWorldMarkers", GPSWorldMarkers);
                GPSMapMarkers = GetBool(node, "GPSMapMarkers", GPSMapMarkers);
                GPSMarkerMaxDistanceKm = GetFloat(node, "GPSMarkerMaxDistanceKm", GPSMarkerMaxDistanceKm);
                GPSSortMode = Mathf.Clamp(GetInt(node, "GPSSortMode", GPSSortMode), 0, 2);
                GPSGroupFilter = node.GetValue("GPSGroupFilter") ?? "";

                FLIRMode = Mathf.Clamp(GetInt(node, "FLIRMode", FLIRMode), 0, 4);
                FLIRContrast = Mathf.Clamp(GetFloat(node, "FLIRContrast", FLIRContrast), 1f, 2.5f);
                FLIRHeatGlow = GetBool(node, "FLIRHeatGlow", FLIRHeatGlow);
                TGPHud = GetBool(node, "TGPHud", TGPHud);

                KeyNextTarget = GetKey(node, "KeyNextTarget", KeyNextTarget);
                KeyPrevTarget = GetKey(node, "KeyPrevTarget", KeyPrevTarget);
                KeyLockTarget = GetKey(node, "KeyLockTarget", KeyLockTarget);
                KeySlewCamera = GetKey(node, "KeySlewCamera", KeySlewCamera);
                KeyEject = GetKey(node, "KeyEject", KeyEject);
                AutoEjectOnDestruction = GetBool(node, "AutoEjectOnDestruction", AutoEjectOnDestruction);
                KeyCMProgram = GetKey(node, "KeyCMProgram", KeyCMProgram);
                KeyLMStrike = GetKey(node, "KeyLMStrike", KeyLMStrike);
                KeyLMManual = GetKey(node, "KeyLMManual", KeyLMManual);
                KeyLMDetonate = GetKey(node, "KeyLMDetonate", KeyLMDetonate);
                KeyLMReturn = GetKey(node, "KeyLMReturn", KeyLMReturn);
                KeyVTOLHover = GetKey(node, "KeyVTOLHover", KeyVTOLHover);
                KeyVTOLLand = GetKey(node, "KeyVTOLLand", KeyVTOLLand);
                ShowLM = GetBool(node, "ShowLM", ShowLM);
                MissileDLZ = GetBool(node, "MissileDLZ", MissileDLZ);
                ShowFeeds = GetBool(node, "ShowFeeds", ShowFeeds);
                AutoFeeds = GetBool(node, "AutoFeeds", AutoFeeds);
                Shrapnel = GetBool(node, "Shrapnel", Shrapnel);
                ExplosionScale = GetBool(node, "ExplosionScale", ExplosionScale);
                ExplosionQuality = Mathf.Clamp(GetInt(node, "ExplosionQuality", ExplosionQuality), 0, 3);
                ExplosionShake = GetBool(node, "ExplosionShake", ExplosionShake);
                Volumetric = GetBool(node, "Volumetric", Volumetric);
                VaporCones = GetBool(node, "VaporCones", VaporCones);
                DynamicDamage = GetBool(node, "DynamicDamage", DynamicDamage);
                KrakenGuard = GetBool(node, "KrakenGuard", KrakenGuard);
                KrakenWatchdog = GetBool(node, "KrakenWatchdog", KrakenWatchdog);
                PhysicsLOD = GetBool(node, "PhysicsLOD", PhysicsLOD);
                LODMarkers = GetBool(node, "LODMarkers", LODMarkers);
                LODUnpackKm = Mathf.Clamp(GetFloat(node, "LODUnpackKm", LODUnpackKm), 2.5f, 50f);
                LODLoadKm = Mathf.Clamp(GetFloat(node, "LODLoadKm", LODLoadKm), 5f, 150f);
                LODMarkerKm = Mathf.Clamp(GetFloat(node, "LODMarkerKm", LODMarkerKm), 10f, 3000f);
                if (float.TryParse(node.GetValue("DamageIntensity"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var di)) DamageIntensity = di;
                RadarTerrainMask = GetBool(node, "RadarTerrainMask", RadarTerrainMask);
                TheatreSpawnKm = Mathf.Clamp(GetFloat(node, "TheatreSpawnKm", TheatreSpawnKm), 20f, 200f);
                FCSAll = GetBool(node, "FCSAll", FCSAll);
                Difficulty = Mathf.Clamp(GetInt(node, "Difficulty", Difficulty), 0, 3);
                ExplosionSizeCurve = Mathf.Clamp(GetFloat(node, "ExplosionSizeCurve", ExplosionSizeCurve), 0.3f, 3f);
                ExplosionSizeCap = Mathf.Clamp(GetFloat(node, "ExplosionSizeCap", ExplosionSizeCap), 1f, 12f);
                ShrapnelRangeMult = Mathf.Clamp(GetFloat(node, "ShrapnelRangeMult", ShrapnelRangeMult), 1f, 4f);
                ShrapnelPattern = Mathf.Clamp(GetInt(node, "ShrapnelPattern", ShrapnelPattern), 0, 3);
                ThreatBanner = GetBool(node, "ThreatBanner", ThreatBanner);
                Radar3DShowThreats = GetBool(node, "Radar3DShowThreats", Radar3DShowThreats);

                GPSGroups.Clear();
                var groups = node.GetNode("GPS_GROUPS");
                if (groups != null)
                    foreach (ConfigNode.Value v in groups.values)
                    {
                        // Stored as "lat_lon = group". ConfigNode keys can't contain commas, so underscores are used.
                        GPSGroups[v.name.Replace('_', ',')] = v.value;
                    }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BDArmoryAvionics]: Failed to load {path}: {e.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                var root = new ConfigNode();
                var node = root.AddNode("AVIONICS");
                node.AddValue("ShowRadar3D", ShowRadar3D);
                node.AddValue("ShowTargetList", ShowTargetList);
                node.AddValue("ShowGPSManager", ShowGPSManager);
                node.AddValue("Radar3DRect", RectToString(Radar3DRect));
                node.AddValue("TargetListRect", RectToString(TargetListRect));
                node.AddValue("GPSManagerRect", RectToString(GPSManagerRect));

                node.AddValue("Radar3DRangeIndex", Radar3DRangeIndex);
                node.AddValue("Radar3DYaw", Radar3DYaw);
                node.AddValue("Radar3DElevation", Radar3DElevation);
                node.AddValue("Radar3DAltScaleIndex", Radar3DAltScaleIndex);
                node.AddValue("Radar3DNorthUp", Radar3DNorthUp);
                node.AddValue("Radar3DShowIRST", Radar3DShowIRST);
                node.AddValue("Radar3DShowTeam", Radar3DShowTeam);
                node.AddValue("Radar3DShowDebris", Radar3DShowDebris);
                node.AddValue("FormationAssist", FormationAssist);
                node.AddValue("FormationMaxBank", FormationMaxBank);
                node.AddValue("TakeoffAssist", TakeoffAssist);
                node.AddValue("RotateRate", RotateRate);
                node.AddValue("MaxRotateAttitude", MaxRotateAttitude);
                node.AddValue("AirOpsTelemetry", AirOpsTelemetry);
                node.AddValue("ShowAllAITweakables", ShowAllAITweakables);
                node.AddValue("AltitudeFromSeaLevel", AltitudeFromSeaLevel);
                node.AddValue("CombatOperators", CombatOperators);
                node.AddValue("AARPopup", AARPopup);
                node.AddValue("StealthModel", StealthModel);
                node.AddValue("EWLinkJamming", EWLinkJamming);
                node.AddValue("EWESM", EWESM);
                node.AddValue("ShowEW", ShowEW);
                node.AddValue("WorldContactMarkers", WorldContactMarkers);

                node.AddValue("TargetListShowFriendlies", TargetListShowFriendlies);
                node.AddValue("TargetListShowMissiles", TargetListShowMissiles);
                node.AddValue("TargetListShowGPS", TargetListShowGPS);

                node.AddValue("GPSWorldMarkers", GPSWorldMarkers);
                node.AddValue("GPSMapMarkers", GPSMapMarkers);
                node.AddValue("GPSMarkerMaxDistanceKm", GPSMarkerMaxDistanceKm);
                node.AddValue("GPSSortMode", GPSSortMode);
                node.AddValue("GPSGroupFilter", GPSGroupFilter);

                node.AddValue("FLIRMode", FLIRMode);
                node.AddValue("FLIRContrast", FLIRContrast);
                node.AddValue("FLIRHeatGlow", FLIRHeatGlow);
                node.AddValue("TGPHud", TGPHud);

                node.AddValue("KeyNextTarget", KeyNextTarget.ToString());
                node.AddValue("KeyPrevTarget", KeyPrevTarget.ToString());
                node.AddValue("KeyLockTarget", KeyLockTarget.ToString());
                node.AddValue("KeySlewCamera", KeySlewCamera.ToString());
                node.AddValue("KeyEject", KeyEject.ToString());
                node.AddValue("AutoEjectOnDestruction", AutoEjectOnDestruction);
                node.AddValue("KeyCMProgram", KeyCMProgram.ToString());
                node.AddValue("KeyLMStrike", KeyLMStrike.ToString());
                node.AddValue("KeyLMManual", KeyLMManual.ToString());
                node.AddValue("KeyLMDetonate", KeyLMDetonate.ToString());
                node.AddValue("KeyLMReturn", KeyLMReturn.ToString());
                node.AddValue("KeyVTOLHover", KeyVTOLHover.ToString());
                node.AddValue("KeyVTOLLand", KeyVTOLLand.ToString());
                node.AddValue("ShowLM", ShowLM);
                node.AddValue("MissileDLZ", MissileDLZ);
                node.AddValue("ShowFeeds", ShowFeeds);
                node.AddValue("AutoFeeds", AutoFeeds);
                node.AddValue("Shrapnel", Shrapnel);
                node.AddValue("ExplosionScale", ExplosionScale);
                node.AddValue("ExplosionQuality", ExplosionQuality);
                node.AddValue("ExplosionShake", ExplosionShake);
                node.AddValue("Volumetric", Volumetric);
                node.AddValue("VaporCones", VaporCones);
                node.AddValue("DynamicDamage", DynamicDamage);
                node.AddValue("KrakenGuard", KrakenGuard);
                node.AddValue("KrakenWatchdog", KrakenWatchdog);
                node.AddValue("PhysicsLOD", PhysicsLOD);
                node.AddValue("LODMarkers", LODMarkers);
                node.AddValue("LODUnpackKm", LODUnpackKm);
                node.AddValue("LODLoadKm", LODLoadKm);
                node.AddValue("LODMarkerKm", LODMarkerKm);
                node.AddValue("DamageIntensity", DamageIntensity.ToString(System.Globalization.CultureInfo.InvariantCulture));
                node.AddValue("RadarTerrainMask", RadarTerrainMask);
                node.AddValue("TheatreSpawnKm", TheatreSpawnKm);
                node.AddValue("FCSAll", FCSAll);
                node.AddValue("Difficulty", Difficulty);
                node.AddValue("ExplosionSizeCurve", ExplosionSizeCurve);
                node.AddValue("ExplosionSizeCap", ExplosionSizeCap);
                node.AddValue("ShrapnelRangeMult", ShrapnelRangeMult);
                node.AddValue("ShrapnelPattern", ShrapnelPattern);
                node.AddValue("ThreatBanner", ThreatBanner);
                node.AddValue("Radar3DShowThreats", Radar3DShowThreats);

                var groups = node.AddNode("GPS_GROUPS");
                foreach (var kvp in GPSGroups)
                    if (!string.IsNullOrEmpty(kvp.Value))
                        groups.AddValue(kvp.Key.Replace(',', '_'), kvp.Value);

                C4ISRSettings.Save(root.AddNode("C4ISR"));


                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                root.Save(SettingsPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BDArmoryAvionics]: Failed to save {SettingsPath}: {e.Message}");
            }
        }

        public static string GPSKey(Vector3d coords) => $"{coords.x:F5},{coords.y:F5}";

        public static string GetGPSGroup(Vector3d coords) => GPSGroups.TryGetValue(GPSKey(coords), out var g) ? g : "";

        public static void SetGPSGroup(Vector3d coords, string group)
        {
            var key = GPSKey(coords);
            if (string.IsNullOrEmpty(group)) GPSGroups.Remove(key);
            else GPSGroups[key] = group;
        }

        static bool GetBool(ConfigNode n, string k, bool d) => n.HasValue(k) && bool.TryParse(n.GetValue(k), out var v) ? v : d;
        static int GetInt(ConfigNode n, string k, int d) => n.HasValue(k) && int.TryParse(n.GetValue(k), out var v) ? v : d;
        static float GetFloat(ConfigNode n, string k, float d) => n.HasValue(k) && float.TryParse(n.GetValue(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
        static KeyCode GetKey(ConfigNode n, string k, KeyCode d)
        {
            if (!n.HasValue(k)) return d;
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), n.GetValue(k), true); }
            catch { return d; }
        }
        static string RectToString(Rect r) => FormattableString.Invariant($"{r.x:F0};{r.y:F0};{r.width:F0};{r.height:F0}");
        static Rect GetRect(ConfigNode n, string k, Rect d)
        {
            if (!n.HasValue(k)) return d;
            var parts = n.GetValue(k).Split(';');
            if (parts.Length != 4) return d;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, ci, out var x) &&
                float.TryParse(parts[1], System.Globalization.NumberStyles.Float, ci, out var y) &&
                float.TryParse(parts[2], System.Globalization.NumberStyles.Float, ci, out var w) &&
                float.TryParse(parts[3], System.Globalization.NumberStyles.Float, ci, out var h))
                return new Rect(x, y, w, h);
            return d;
        }
    }
}
