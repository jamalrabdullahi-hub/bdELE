using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Settings for the avionics windows (3D radar, target list, GPS manager, FLIR, TGP HUD).
    /// Stored in GameData/BDArmoryAvionics/PluginData/Avionics.cfg.
    /// </summary>
    public static class AvionicsSettings
    {
        static string SettingsPath => Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmoryAvionics/PluginData/Avionics.cfg");
        static string OldSettingsPath => Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmory/PluginData/Avionics.cfg"); // before the mod had its own folder

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

        // Ejection seats
        public static bool AutoEjectOnDestruction = true;
        public static bool AutoEjectBeforeImpact = true;

        public static readonly float[] Radar3DRanges = [2000, 5000, 10000, 20000, 40000, 80000, 160000, 320000];
        public static readonly float[] AltScales = [1, 2, 4, 8];

        static bool loaded = false;

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(SettingsPath) && File.Exists(OldSettingsPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                    File.Move(OldSettingsPath, SettingsPath);
                }
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmory.Avionics]: Couldn't move the old settings file: {e.Message}"); }
            if (!File.Exists(SettingsPath)) return;
            try
            {
                var root = ConfigNode.Load(SettingsPath);
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
                WorldContactMarkers = GetBool(node, "WorldContactMarkers", WorldContactMarkers);

                TargetListShowFriendlies = GetBool(node, "TargetListShowFriendlies", TargetListShowFriendlies);
                TargetListShowMissiles = GetBool(node, "TargetListShowMissiles", TargetListShowMissiles);
                TargetListShowGPS = GetBool(node, "TargetListShowGPS", TargetListShowGPS);

                GPSWorldMarkers = GetBool(node, "GPSWorldMarkers", GPSWorldMarkers);
                GPSMapMarkers = GetBool(node, "GPSMapMarkers", GPSMapMarkers);
                GPSMarkerMaxDistanceKm = GetFloat(node, "GPSMarkerMaxDistanceKm", GPSMarkerMaxDistanceKm);
                GPSSortMode = Mathf.Clamp(GetInt(node, "GPSSortMode", GPSSortMode), 0, 2);
                GPSGroupFilter = node.GetValue("GPSGroupFilter") ?? "";

                FLIRMode = Mathf.Clamp(GetInt(node, "FLIRMode", FLIRMode), 0, 3);
                FLIRContrast = Mathf.Clamp(GetFloat(node, "FLIRContrast", FLIRContrast), 1f, 2.5f);
                FLIRHeatGlow = GetBool(node, "FLIRHeatGlow", FLIRHeatGlow);
                TGPHud = GetBool(node, "TGPHud", TGPHud);

                KeyNextTarget = GetKey(node, "KeyNextTarget", KeyNextTarget);
                KeyPrevTarget = GetKey(node, "KeyPrevTarget", KeyPrevTarget);
                KeyLockTarget = GetKey(node, "KeyLockTarget", KeyLockTarget);
                KeySlewCamera = GetKey(node, "KeySlewCamera", KeySlewCamera);
                KeyEject = GetKey(node, "KeyEject", KeyEject);
                AutoEjectOnDestruction = GetBool(node, "AutoEjectOnDestruction", AutoEjectOnDestruction);
                AutoEjectBeforeImpact = GetBool(node, "AutoEjectBeforeImpact", AutoEjectBeforeImpact);

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
                Debug.LogWarning($"[BDArmory.Avionics]: Failed to load {SettingsPath}: {e.Message}");
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
                node.AddValue("AutoEjectBeforeImpact", AutoEjectBeforeImpact);

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
                Debug.LogWarning($"[BDArmory.Avionics]: Failed to save {SettingsPath}: {e.Message}");
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
