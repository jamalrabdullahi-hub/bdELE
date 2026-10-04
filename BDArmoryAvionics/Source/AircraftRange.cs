using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

using BDArmory.UI;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Aircraft range from how the craft really flies. In flight, steady cruise is logged per craft: fuel burn (kg/s) by
    /// altitude band (1 km) and speed band (25 m/s). In the VAB/SPH, the range window turns that into range, endurance and
    /// combat radius for the fuel on board; before there's data, a rough estimate from the engines at a chosen throttle.
    /// Aircraft only (air-breathing engines); craft carrying missile bodies as their airframe are left out.
    /// </summary>
    public static class AircraftRange
    {
        public class Bin { public int altKm, speed; public double flow; public int n; }

        static string Dir => Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmoryAvionics/PluginData/Range");
        static string FileFor(string craft) { foreach (var c in Path.GetInvalidFileNameChars()) craft = craft.Replace(c, '_'); return Path.Combine(Dir, craft + ".cfg"); }

        public static Dictionary<(int, int), Bin> Load(string craft)
        {
            var d = new Dictionary<(int, int), Bin>();
            var f = FileFor(craft);
            if (!File.Exists(f)) return d;
            var node = ConfigNode.Load(f);
            foreach (var b in node?.GetNodes("BIN") ?? Array.Empty<ConfigNode>())
            {
                var bin = new Bin();
                int.TryParse(b.GetValue("alt"), out bin.altKm); int.TryParse(b.GetValue("spd"), out bin.speed);
                double.TryParse(b.GetValue("flow"), NumberStyles.Float, CultureInfo.InvariantCulture, out bin.flow); int.TryParse(b.GetValue("n"), out bin.n);
                d[(bin.altKm, bin.speed)] = bin;
            }
            return d;
        }

        public static void Save(string craft, Dictionary<(int, int), Bin> bins)
        {
            Directory.CreateDirectory(Dir);
            var node = new ConfigNode("RANGE");
            foreach (var b in bins.Values)
            {
                var n = node.AddNode("BIN");
                n.AddValue("alt", b.altKm); n.AddValue("spd", b.speed); n.AddValue("flow", b.flow.ToString("R", CultureInfo.InvariantCulture)); n.AddValue("n", b.n);
            }
            var root = new ConfigNode(); root.AddNode(node);
            node.Save(FileFor(craft));
        }

        /// <summary>Air-breathing aircraft (and not a missile body).</summary>
        public static bool IsAircraft(IEnumerable<Part> parts)
        {
            bool air = false;
            foreach (var p in parts)
            {
                if (p.FindModuleImplementing<MissileBase>() != null && p.parent == null) return false;
                foreach (var e in p.FindModulesImplementing<ModuleEngines>())
                    foreach (var pr in e.propellants) if (pr.name == "IntakeAir" || pr.name == "IntakeAtm") air = true;
            }
            return air;
        }

        /// <summary>Burnable fuel mass in tonnes (everything engines use except intake air).</summary>
        public static double FuelMass(IEnumerable<Part> parts)
        {
            var used = new HashSet<string>();
            foreach (var p in parts) foreach (var e in p.FindModulesImplementing<ModuleEngines>()) foreach (var pr in e.propellants) if (pr.name != "IntakeAir" && pr.name != "IntakeAtm") used.Add(pr.name);
            double m = 0;
            foreach (var p in parts) foreach (PartResource r in p.Resources) if (used.Contains(r.resourceName) && r.info != null) m += r.amount * r.info.density;
            return m;
        }
    }

    /// <summary>Logs steady cruise of the active aircraft.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class AircraftRangeLogger : MonoBehaviour
    {
        Vessel v; Dictionary<(int, int), AircraftRange.Bin> bins; string craft;
        double lastFuel = -1, lastSpeed; float lastT, lastThrottle, steadySince = -1; bool dirty; float nextSave;

        void FixedUpdate()
        {
            var a = FlightGlobals.ActiveVessel;
            if (a != v) { SaveNow(); v = a; bins = null; lastFuel = -1; steadySince = -1; }
            if (v == null || v.LandedOrSplashed || v.packed) { lastFuel = -1; return; }
            if (bins == null)
            {
                if (!AircraftRange.IsAircraft(v.parts)) { bins = new(); craft = null; return; }
                craft = v.GetName(); bins = AircraftRange.Load(craft);
            }
            if (craft == null || Time.time - lastT < 1f) return;
            float dt = Time.time - lastT; lastT = Time.time;
            double fuel = AircraftRange.FuelMass(v.parts);
            float thr = v.ctrlState.mainThrottle;
            bool steady = Math.Abs(v.verticalSpeed) < 4 && v.srfSpeed > 40 && Mathf.Abs(thr - lastThrottle) < 0.02f && thr > 0.05f && Math.Abs(v.srfSpeed - lastSpeed) < 2; lastSpeed = v.srfSpeed;
            lastThrottle = thr;
            if (!steady) { steadySince = -1; lastFuel = fuel; return; }
            if (steadySince < 0) { steadySince = Time.time; lastFuel = fuel; return; }
            if (Time.time - steadySince < 5f) { lastFuel = fuel; return; } // settle first
            double flow = (lastFuel - fuel) * 1000.0 / dt; // kg/s
            lastFuel = fuel;
            if (flow <= 0 || flow > 1000) return;
            var key = ((int)(v.altitude / 1000), (int)(v.srfSpeed / 25) * 25);
            if (!bins.TryGetValue(key, out var b)) bins[key] = b = new AircraftRange.Bin { altKm = key.Item1, speed = key.Item2 };
            b.flow = (b.flow * b.n + flow) / (b.n + 1); b.n = Math.Min(b.n + 1, 600);
            dirty = true;
            if (Time.time > nextSave) { nextSave = Time.time + 30f; SaveNow(); }
        }

        void SaveNow() { if (dirty && craft != null && bins != null) { try { AircraftRange.Save(craft, bins); } catch { } dirty = false; } }
        void OnDestroy() => SaveNow();
    }

    /// <summary>VAB/SPH range window.</summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorRangeWindow : MonoBehaviour
    {
        public static bool Show;
        const int WindowID = 71730364;
        Rect rect = new(Screen.width - 480, 260, 420, 360);
        float cruiseThrottle = 0.6f, cruiseSpeed = 250f, reserve = 0.1f;
        Vector2 scroll;

        void OnGUI()
        {
            if (!Show) return;
            rect = AvionicsCore.DrawWindow(WindowID, rect, Draw, "Aircraft range", BDArmorySetup.BDGuiSkin);
        }

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; return; }
            var small = AvionicsDraw.LabelSmall;
            float w = rect.width - 16, y = 24;
            var ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            if (ship == null || ship.parts.Count == 0) { GUI.DragWindow(); return; }
            if (!AircraftRange.IsAircraft(ship.parts)) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 30), "Range estimates are for aircraft with air-breathing engines.", small, AvionicsDraw.ScopeText); GUI.DragWindow(); return; }
            double fuelT = AircraftRange.FuelMass(ship.parts);
            reserve = Mathf.Round(Slider(ref y, w, $"Reserve {reserve:P0}", reserve, 0, 0.3f) * 20) / 20;
            double usable = fuelT * 1000 * (1 - reserve);
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), $"Fuel {fuelT:F2} t, usable {usable / 1000:F2} t", small, AvionicsDraw.HudText); y += 20;

            var bins = AircraftRange.Load(ship.shipName);
            if (bins.Count > 0)
            {
                AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), "MEASURED IN FLIGHT", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
                var list = new List<AircraftRange.Bin>(bins.Values);
                list.RemoveAll(b => b.n < 5 || b.flow <= 0);
                list.Sort((a, b) => (b.speed / b.flow).CompareTo(a.speed / a.flow));
                if (list.Count > 0)
                {
                    var best = list[0];
                    double rangeKm = best.speed * usable / best.flow / 1000;
                    AvionicsDraw.ColorLabel(new Rect(8, y, w, 30), $"Best range {rangeKm:F0} km at {best.altKm} km, {best.speed} m/s\nCombat radius {rangeKm / 2 * 0.85:F0} km (out and back, 15% for fighting)", small, AvionicsDraw.Friendly); y += 34;
                    scroll = GUI.BeginScrollView(new Rect(8, y, w, rect.height - y - 8), scroll, new Rect(0, 0, w - 18, list.Count * 16));
                    for (int i = 0; i < list.Count; i++)
                    {
                        var b = list[i];
                        double r = b.speed * usable / b.flow / 1000, endH = usable / b.flow / 3600;
                        AvionicsDraw.ColorLabel(new Rect(0, i * 16, w - 18, 16), $"{b.altKm,2} km  {b.speed,4} m/s  {b.flow:F2} kg/s  ->  {r:F0} km, {endH:F1} h", small, AvionicsDraw.HudText);
                    }
                    GUI.EndScrollView();
                    GUI.DragWindow(); return;
                }
            }
            // Rough estimate from the engines
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 30), "No flight data yet (fly steady for a while and it learns).\nRough estimate from the engines:", small, AvionicsDraw.Unknown); y += 34;
            cruiseThrottle = Mathf.Round(Slider(ref y, w, $"Cruise throttle {cruiseThrottle:P0}", cruiseThrottle, 0.1f, 1f) * 20) / 20;
            cruiseSpeed = Mathf.Round(Slider(ref y, w, $"Cruise speed {cruiseSpeed:F0} m/s", cruiseSpeed, 50, 1500) / 10) * 10;
            double flowMax = 0;
            foreach (var p in ship.parts) foreach (var e in p.FindModulesImplementing<ModuleEngines>()) if (e.part.FindModuleImplementing<MissileBase>() == null) flowMax += e.maxFuelFlow * 1000 * e.thrustPercentage / 100; // kg/s at full throttle
            double fuelShare = 1;
            double flowCruise = flowMax * cruiseThrottle * fuelShare;
            if (flowCruise > 0)
            {
                double rangeKm = cruiseSpeed * usable / flowCruise / 1000;
                AvionicsDraw.ColorLabel(new Rect(8, y, w, 46), $"~{rangeKm:F0} km range, ~{usable / flowCruise / 3600:F1} h endurance\n~{rangeKm / 2 * 0.85:F0} km combat radius\n(engines' rated burn; real burn varies with speed and altitude)", small, AvionicsDraw.HudText);
            }
            GUI.DragWindow();
        }

        static float Slider(ref float y, float w, string label, float value, float min, float max)
        {
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), label, AvionicsDraw.LabelSmall, AvionicsDraw.HudText);
            float v = GUI.HorizontalSlider(new Rect(8, y + 15, w, 14), value, min, max);
            y += 32; return v;
        }
    }
}
