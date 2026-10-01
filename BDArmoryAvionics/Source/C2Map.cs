using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

using BDArmory.Competition;

namespace BDArmory.Avionics
{
    public enum C2MapLayer { Terrain, Biome }

    /// <summary>A map image for a lat/lon box, built a few rows per frame.</summary>
    public class C2MapTile
    {
        public CelestialBody body;
        public double latMin, latMax, lonMin, lonMax;
        public C2MapLayer layer;
        public MapSource source;
        public Texture2D texture;
        public bool done;
        public float progress;

        public bool Matches(CelestialBody b, double la0, double la1, double lo0, double lo1, C2MapLayer l, MapSource s) =>
            body == b && layer == l && source == s && latMin == la0 && latMax == la1 && lonMin == lo0 && lonMax == lo1;
    }

    /// <summary>
    /// Map data for the C2 window.
    /// KerbNet (stock): terrain height and biome straight from the planet, shown when the team has a KerbNet-capable part (probe core).
    /// SCANsat (optional, found by reflection): the same layers, but only where SCANsat has scanned; unscanned areas stay dark.
    /// Grid: no imagery, just the plotting grid.
    /// </summary>
    public static class C2Map
    {
        // SCANsat bridge (no hard dependency)
        static bool scanChecked;
        static MethodInfo scanIsCovered;
        const int SCAN_Altimetry = 3; // SCANtype.AltimetryLoRes | AltimetryHiRes
        const int SCAN_Biome = 8;     // SCANtype.Biome

        public static bool SCANsatInstalled
        {
            get
            {
                if (!scanChecked)
                {
                    scanChecked = true;
                    try
                    {
                        foreach (var a in AssemblyLoader.loadedAssemblies)
                        {
                            if (a.name != "SCANsat") continue;
                            var t = a.assembly.GetType("SCANsat.SCANUtil");
                            scanIsCovered = t?.GetMethod("isCovered", BindingFlags.Public | BindingFlags.Static, null,
                                new[] { typeof(double), typeof(double), typeof(CelestialBody), typeof(int) }, null);
                            break;
                        }
                    }
                    catch (Exception e) { Debug.LogWarning($"[BDArmory.C2Map]: SCANsat lookup failed: {e.Message}"); }
                    if (scanIsCovered != null) Debug.Log("[BDArmory.C2Map]: SCANsat found, scanned-coverage maps available");
                }
                return scanIsCovered != null;
            }
        }

        static bool ScanCovered(double lat, double lon, CelestialBody body, int type)
        {
            try { return (bool)scanIsCovered.Invoke(null, new object[] { lon, lat, body, type }); }
            catch { return false; }
        }

        /// <summary>The map source actually usable right now (falls back when SCANsat is missing or there's no KerbNet part).</summary>
        public static MapSource Effective(BDTeam team)
        {
            var s = C4ISRSettings.MapSource;
            if (s == MapSource.SCANsat && !SCANsatInstalled) s = MapSource.KerbNet;
            if (s == MapSource.KerbNet && C4ISRSettings.MapRequiresCoverage && !TeamHasKerbNet(team)) s = MapSource.Grid;
            return s;
        }

        public static bool TeamHasKerbNet(BDTeam team)
        {
            var net = DataLinkNetwork.For(team);
            if (net == null) return false;
            foreach (var n in net.nodes)
            {
                if (!n.connected || n.vessel == null) continue;
                foreach (var p in n.vessel.parts)
                    if (p != null && p.FindModuleImplementing<ModuleKerbNetAccess>() != null) return true;
            }
            return false;
        }

        /// <summary>Start building a tile; run the returned routine with StartCoroutine.</summary>
        public static IEnumerator Build(C2MapTile tile, int width, int height, int rowsPerFrame = 8)
        {
            tile.done = false;
            tile.progress = 0;
            if (tile.texture == null || tile.texture.width != width || tile.texture.height != height)
                tile.texture = new Texture2D(width, height, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[width * height];
            var body = tile.body;
            bool scan = tile.source == MapSource.SCANsat && SCANsatInstalled;
            int scanType = tile.layer == C2MapLayer.Biome ? SCAN_Biome : SCAN_Altimetry;
            double maxAlt = Math.Max(1000, body.pqsController != null ? body.pqsController.mapMaxHeight : 6000);

            for (int y = 0; y < height; y++)
            {
                double lat = tile.latMin + (tile.latMax - tile.latMin) * (y + 0.5) / height;
                for (int x = 0; x < width; x++)
                {
                    double lon = tile.lonMin + (tile.lonMax - tile.lonMin) * (x + 0.5) / width;
                    Color c;
                    if (tile.source == MapSource.Grid || (scan && !ScanCovered(lat, lon, body, scanType)))
                        c = new Color(0.07f, 0.10f, 0.11f);
                    else if (tile.layer == C2MapLayer.Biome && body.BiomeMap != null)
                        c = body.BiomeMap.GetAtt(lat * Mathf.Deg2Rad, lon * Mathf.Deg2Rad).mapColor * 0.75f;
                    else
                        c = TerrainColor(body.TerrainAltitude(lat, lon, true), maxAlt, body.ocean);
                    px[y * width + x] = c;
                }
                if (y % rowsPerFrame == rowsPerFrame - 1)
                {
                    tile.progress = (float)y / height;
                    yield return null;
                    if (tile.texture == null) yield break; // window closed
                }
            }
            tile.texture.SetPixels32(px);
            tile.texture.Apply();
            tile.progress = 1;
            tile.done = true;
        }

        /// <summary>Muted relief colours so tracks and symbols stay readable on top.</summary>
        public static Color TerrainColor(double alt, double maxAlt, bool ocean)
        {
            if (ocean && alt <= 0)
            {
                float d = Mathf.Clamp01((float)(-alt / 2000.0));
                return Color.Lerp(new Color(0.10f, 0.20f, 0.28f), new Color(0.04f, 0.09f, 0.15f), d);
            }
            float h = Mathf.Clamp01((float)(alt / maxAlt));
            if (h < 0.25f) return Color.Lerp(new Color(0.12f, 0.22f, 0.14f), new Color(0.20f, 0.27f, 0.15f), h / 0.25f);
            if (h < 0.6f) return Color.Lerp(new Color(0.20f, 0.27f, 0.15f), new Color(0.30f, 0.26f, 0.18f), (h - 0.25f) / 0.35f);
            return Color.Lerp(new Color(0.30f, 0.26f, 0.18f), new Color(0.62f, 0.62f, 0.60f), (h - 0.6f) / 0.4f);
        }
    }
}
