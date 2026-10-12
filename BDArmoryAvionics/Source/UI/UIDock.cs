using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using KSP.UI;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.UI;

using BDArmory.Settings;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// ELE window manager. Every ELE window goes through Draw(): it snaps to the screen edges and to other ELE windows
    /// while you drag it, can collapse to its title bar (the "–" button), hides in clean mode (Alt+H: warnings stay),
    /// and remembers its place in saved layouts (Combat, C2, Building, Clean, Testing; Alt+L cycles, the hub picks).
    /// Also folds other mods' toolbar buttons into ELE's launcher panel.
    /// </summary>
    public static class UIDock
    {
        public static bool Clean;
        static readonly HashSet<int> collapsed = new();
        static readonly Dictionary<int, Rect> lastRects = new();
        static readonly Dictionary<int, Vector2> pending = new();
        static readonly Dictionary<int, string> titles = new();
        const float Snap = 12f;

        public static Rect Draw(int id, Rect rect, GUI.WindowFunction func, string title, GUISkin skin)
        {
            titles[id] = title;
            if (pending.TryGetValue(id, out var pos)) { rect.position = pos; pending.Remove(id); }
            if (Clean && HighLogic.LoadedSceneIsFlight) { lastRects.Remove(id); return rect; }
            bool isCollapsed = collapsed.Contains(id);
            var draw = isCollapsed ? new Rect(rect.x, rect.y, Mathf.Min(rect.width, 260), 22) : rect;
            var before = draw.position;
            var m = GUI.matrix;
            float s = BDArmorySettings.UI_SCALE_ACTUAL;
            if (s != 1) GUIUtility.ScaleAroundPivot(s * Vector2.one, draw.position);
            draw = GUI.Window(id, draw, wid =>
            {
                if (!isCollapsed) func(wid);
                float bx = (isCollapsed ? draw.width : rect.width) - (isCollapsed ? 22 : 42);
                if (GUI.Button(new Rect(bx, 2, 18, 18), isCollapsed ? "+" : "–", BDArmorySetup.CloseButtonStyle)) { if (isCollapsed) collapsed.Remove(wid); else collapsed.Add(wid); }
                if (isCollapsed) GUI.DragWindow();
            }, title, skin.window);
            GUI.matrix = m;
            BDAHooks.KeepOnScreen(ref draw);
            if (draw.position != before) draw.position = SnapTo(id, draw);
            rect.position = draw.position;
            if (!isCollapsed) rect.size = new Vector2(rect.width, Mathf.Max(rect.height, draw.height));
            lastRects[id] = isCollapsed ? draw : rect;
            return rect;
        }

        /// <summary>Snap a moved window to the screen edges and to the edges of other ELE windows.</summary>
        static Vector2 SnapTo(int id, Rect r)
        {
            float x = r.x, y = r.y;
            void Try(ref float v, float target) { if (Mathf.Abs(v - target) < Snap) v = target; }
            Try(ref x, 0); Try(ref x, Screen.width - r.width); Try(ref y, 0); Try(ref y, Screen.height - r.height);
            foreach (var kv in lastRects)
            {
                if (kv.Key == id) continue;
                var o = kv.Value;
                bool vOverlap = r.yMax > o.y - Snap && r.y < o.yMax + Snap, hOverlap = r.xMax > o.x - Snap && r.x < o.xMax + Snap;
                if (vOverlap) { Try(ref x, o.xMax + 2); Try(ref x, o.x - r.width - 2); Try(ref x, o.x); }
                if (hOverlap) { Try(ref y, o.yMax + 2); Try(ref y, o.y - r.height - 2); Try(ref y, o.y); }
            }
            return new Vector2(x, y);
        }

        public static void Forget(int id) => lastRects.Remove(id);

        // ---------------- layouts ----------------
        public static readonly string[] LayoutNames = { "Combat", "C2", "Building", "Clean", "Testing" };
        public static int Layout = -1;
        static string File => Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "BDArmoryAvionics", "PluginData", "Layouts.cfg");

        /// <summary>The ELE windows a layout can switch: name, is it open, open / close.</summary>
        public static readonly List<(string name, Func<bool> get, Action<bool> set)> Windows = new()
        {
            ("Radar3D", () => AvionicsSettings.ShowRadar3D, v => AvionicsSettings.ShowRadar3D = v),
            ("TargetList", () => AvionicsSettings.ShowTargetList, v => AvionicsSettings.ShowTargetList = v),
            ("GPS", () => AvionicsSettings.ShowGPSManager, v => AvionicsSettings.ShowGPSManager = v),
            ("EW", () => AvionicsSettings.ShowEW, v => AvionicsSettings.ShowEW = v),
            ("LM", () => AvionicsSettings.ShowLM, v => AvionicsSettings.ShowLM = v),
            ("Feeds", () => AvionicsSettings.ShowFeeds, v => AvionicsSettings.ShowFeeds = v),
            ("Console", () => C4ISRSettings.ShowConsole, v => C4ISRSettings.ShowConsole = v),
            ("IADS", () => C4ISRSettings.ShowIADS, v => C4ISRSettings.ShowIADS = v),
            ("Link", () => C4ISRSettings.ShowLink, v => C4ISRSettings.ShowLink = v),
            ("C4ISRSettings", () => C4ISRSettings.ShowSettings, v => C4ISRSettings.ShowSettings = v),
            ("Theatre", () => TheatreWindow.Show, v => TheatreWindow.Show = v),
            ("Airfields", () => AirfieldsWindow.Show, v => AirfieldsWindow.Show = v),
            ("ATC", () => ATCWindow.Show, v => ATCWindow.Show = v),
            ("AAR", () => AfterActionWindow.Show, v => AfterActionWindow.Show = v),
            ("Stealth", () => LiveStealth.Show, v => LiveStealth.Show = v),
            ("Profiler", () => AvionicsSettings.Profiler, v => AvionicsSettings.Profiler = v),
            ("HudStrip", () => AvionicsSettings.HudStrip, v => AvionicsSettings.HudStrip = v),
        };

        /// <summary>Built-in layouts the first time (before you've saved your own over them).</summary>
        static readonly Dictionary<string, string[]> Defaults = new()
        {
            { "Combat", new[] { "TargetList", "HudStrip" } },
            { "C2", new[] { "Console", "IADS", "Link", "HudStrip" } },
            { "Building", new string[0] },
            { "Clean", new[] { "HudStrip" } },
            { "Testing", new[] { "Profiler", "Stealth", "HudStrip" } },
        };

        public static void Apply(int index)
        {
            Layout = index; string name = LayoutNames[index];
            var node = ConfigNode.Load(File)?.GetNodes("LAYOUT").FirstOrDefault(n => n.GetValue("name") == name);
            var open = node != null ? new HashSet<string>(node.GetValues("open")) : new HashSet<string>(Defaults[name]);
            foreach (var (n, _, set) in Windows) set(open.Contains(n));
            if (node != null)
                foreach (var p in node.GetValues("pos"))
                {
                    var q = p.Split(','); if (q.Length < 3) continue;
                    if (int.TryParse(q[0], out int id) && float.TryParse(q[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) && float.TryParse(q[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                        pending[id] = new Vector2(x, y);
                }
            if (node != null) { collapsed.Clear(); foreach (var c in node.GetValues("collapsed")) if (int.TryParse(c, out int cid)) collapsed.Add(cid); }
            Clean = false;
            ScreenMessages.PostScreenMessage($"ELE layout: {name}", 1.5f, ScreenMessageStyle.UPPER_CENTER);
        }

        public static void SaveCurrent(int index)
        {
            string name = LayoutNames[index];
            var root = ConfigNode.Load(File) ?? new ConfigNode();
            foreach (var n in root.GetNodes("LAYOUT").Where(n => n.GetValue("name") == name).ToList()) root.RemoveNode(n);
            var l = root.AddNode("LAYOUT"); l.AddValue("name", name);
            foreach (var (n, get, _) in Windows) if (get()) l.AddValue("open", n);
            foreach (var kv in lastRects) l.AddValue("pos", $"{kv.Key},{kv.Value.x.ToString(CultureInfo.InvariantCulture)},{kv.Value.y.ToString(CultureInfo.InvariantCulture)}");
            foreach (var c in collapsed) l.AddValue("collapsed", c);
            Directory.CreateDirectory(Path.GetDirectoryName(File));
            root.Save(File);
            Layout = index;
            ScreenMessages.PostScreenMessage($"ELE layout saved: {name}", 1.5f, ScreenMessageStyle.UPPER_CENTER);
        }

        // ---------------- other mods' toolbar buttons ----------------
        static readonly FieldInfo modList = typeof(ApplicationLauncher).GetField("appListMod", BindingFlags.Instance | BindingFlags.NonPublic);
        public static readonly List<ApplicationLauncherButton> Folded = new();

        /// <summary>Other mods' launcher buttons (not ELE's own).</summary>
        public static List<ApplicationLauncherButton> ModButtons(ApplicationLauncherButton own)
        {
            var l = new List<ApplicationLauncherButton>();
            if (ApplicationLauncher.Instance == null || modList == null) return l;
            if (modList.GetValue(ApplicationLauncher.Instance) is List<ApplicationLauncherButton> list)
                foreach (var b in list) if (b != null && b != own) l.Add(b);
            return l;
        }

        /// <summary>Hide (or show again) other mods' buttons on KSP's bar; they stay usable from the ELE panel.</summary>
        public static void FoldToolbar(bool fold, ApplicationLauncherButton own)
        {
            foreach (var b in ModButtons(own)) if (b != null && b.gameObject.activeSelf == fold) b.gameObject.SetActive(!fold);
        }

        public static void Toggle(ApplicationLauncherButton b)
        {
            if (b == null || b.toggleButton == null) return;
            if (b.toggleButton.CurrentState == UIRadioButton.State.True) b.SetFalse(true); else b.SetTrue(true);
        }

        public static Texture Icon(ApplicationLauncherButton b) => b != null && b.sprite != null ? b.sprite.texture : null;
    }
}
