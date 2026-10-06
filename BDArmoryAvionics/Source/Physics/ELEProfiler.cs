using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BDArmory.Avionics
{
    /// <summary>
    /// ELE profiler: times KSP, BDArmory and ELE systems per frame so we know what to optimise / thread.
    /// Hooks (Harmony prefix/postfix stopwatches) are only installed while the window is open: zero cost when off.
    /// Shows ms per frame (average over 2 s and worst), share of the frame, calls; can log a CSV for battles.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ELEProfiler : MonoBehaviour
    {
        static readonly (string label, string method)[] Targets =
        {
            ("KSP flight integrator (all)", "FlightIntegrator:FixedUpdate"),
            ("KSP thermal", "FlightIntegrator:UpdateThermodynamics"),
            ("KSP conduction", "FlightIntegrator:UpdateConduction"),
            ("KSP aero (per part)", "FlightIntegrator:UpdateAerodynamics"),
            ("KSP occlusion", "FlightIntegrator:UpdateOcclusion"),
            ("BDA bullets", "BDArmory.Bullets.PooledBullet:FixedUpdate"),
            ("BDA weapons", "BDArmory.Weapons.ModuleWeapon:FixedUpdate"),
            ("BDA pilot AI", "BDArmory.Control.BDModulePilotAI:AutoPilot"),
            ("BDA surface AI", "BDArmory.Control.BDModuleSurfaceAI:AutoPilot"),
            ("BDA radar scan", "BDArmory.Radar.RadarUtils:RadarUpdateScanLock"),
            ("BDA explosions", "BDArmory.FX.ExplosionFx:ExecutePartBlastEvent"),
            ("ELE shrapnel", "BDArmory.Avionics.Shrapnel:Detonate"),
            ("ELE sensor engine", "BDArmory.Avionics.SensorEngine:Update"),
            ("ELE kraken guard", "BDArmory.Avionics.KrakenGuard:FixedUpdate"),
            ("ELE dynamic damage", "BDArmory.Avionics.DynamicDamage:Update"),
            ("ELE AI commander", "BDArmory.Avionics.Materializer:Update"),
        };

        static Harmony harmony;
        static readonly Dictionary<MethodBase, int> index = new();
        static long[] acc, calls;
        static float[,] hist; static long[,] histCalls; static int head, frames;
        static float[] frameMs = new float[120];
        static bool installed;
        const int H = 120;

        Rect win = new(60, 120, 470, 60);
        bool csv; float nextCsv; StreamWriter writer;

        void Update()
        {
            if (AvionicsSettings.Profiler && !installed) Install();
            if (!AvionicsSettings.Profiler && installed) Uninstall();
            if (!installed) return;
            // roll the frame
            for (int i = 0; i < Targets.Length; i++)
            {
                hist[head, i] = acc[i] * 1000f / Stopwatch.Frequency; histCalls[head, i] = calls[i];
                acc[i] = 0; calls[i] = 0;
            }
            frameMs[head] = Time.unscaledDeltaTime * 1000f;
            head = (head + 1) % H; frames = Mathf.Min(frames + 1, H);
            if (csv && Time.unscaledTime > nextCsv) { nextCsv = Time.unscaledTime + 1f; WriteCsv(); }
        }

        void Install()
        {
            harmony ??= new Harmony("BDArmoryAvionics.Profiler");
            acc = new long[Targets.Length]; calls = new long[Targets.Length];
            hist = new float[H, Targets.Length]; histCalls = new long[H, Targets.Length]; head = 0; frames = 0;
            var pre = new HarmonyMethod(AccessTools.Method(typeof(ELEProfiler), nameof(Pre)));
            var post = new HarmonyMethod(AccessTools.Method(typeof(ELEProfiler), nameof(Post)));
            index.Clear();
            for (int i = 0; i < Targets.Length; i++)
            {
                try
                {
                    var m = AccessTools.Method(Targets[i].method);
                    if (m == null) continue;
                    index[m] = i;
                    harmony.Patch(m, pre, post);
                }
                catch (System.Exception e) { Debug.LogWarning($"[ELE Profiler] {Targets[i].method}: {e.Message}"); }
            }
            installed = true;
            Debug.Log($"[ELE Profiler] timing {index.Count} systems");
        }

        void Uninstall()
        {
            try { harmony?.UnpatchAll("BDArmoryAvionics.Profiler"); } catch { }
            installed = false; csv = false; writer?.Close(); writer = null;
        }

        void OnDestroy() { if (installed) Uninstall(); }

        static void Pre(out long __state) { __state = Stopwatch.GetTimestamp(); }
        static void Post(long __state, MethodBase __originalMethod)
        {
            if (acc != null && index.TryGetValue(__originalMethod, out int i)) { acc[i] += Stopwatch.GetTimestamp() - __state; calls[i]++; }
        }

        (float avg, float max, float calls) Stat(int i)
        {
            float sum = 0, max = 0, c = 0; int n = Mathf.Max(1, frames);
            for (int k = 0; k < frames; k++) { sum += hist[k, i]; max = Mathf.Max(max, hist[k, i]); c += histCalls[k, i]; }
            return (sum / n, max, c / n);
        }

        void WriteCsv()
        {
            try
            {
                if (writer == null)
                {
                    var path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmoryAvionics/PluginData/ELE_profile.csv");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    bool fresh = !File.Exists(path);
                    writer = new StreamWriter(path, true);
                    if (fresh) writer.WriteLine("time,frame_ms,vessels_loaded,parts_loaded," + string.Join(",", Targets.Select(t => t.label.Replace(",", ""))));
                }
                float fm = 0; for (int k = 0; k < frames; k++) fm += frameMs[k]; fm /= Mathf.Max(1, frames);
                int parts = 0; foreach (var v in FlightGlobals.VesselsLoaded) parts += v.parts.Count;
                writer.WriteLine($"{Time.time:F1},{fm:F2},{FlightGlobals.VesselsLoaded.Count},{parts}," + string.Join(",", Enumerable.Range(0, Targets.Length).Select(i => Stat(i).avg.ToString("F3"))));
                writer.Flush();
            }
            catch (System.Exception e) { Debug.LogWarning("[ELE Profiler] csv: " + e.Message); csv = false; }
        }

        void OnGUI()
        {
            if (!installed || !BDArmory.UI.BDArmorySetup.GAME_UI_ENABLED) return;
            win = GUI.Window(GetInstanceID(), win, Draw, "ELE Profiler", BDArmory.UI.BDArmorySetup.BDGuiSkin.window);
        }

        void Draw(int id)
        {
            var btn = BDArmory.UI.BDArmorySetup.ButtonStyle; var sel = BDArmory.UI.BDArmorySetup.SelectedButtonStyle;
            float fm = 0, fmax = 0; for (int k = 0; k < frames; k++) { fm += frameMs[k]; fmax = Mathf.Max(fmax, frameMs[k]); } fm /= Mathf.Max(1, frames);
            int parts = 0; foreach (var v in FlightGlobals.VesselsLoaded) parts += v.parts.Count;
            float y = 22;
            GUI.Label(new Rect(8, y, 450, 18), $"frame {fm:F1} ms ({1000f / Mathf.Max(1, fm):F0} fps), worst {fmax:F0} ms   |   {FlightGlobals.VesselsLoaded.Count} vessels, {parts} parts"); y += 20;
            GUI.Label(new Rect(8, y, 220, 18), "system"); GUI.Label(new Rect(230, y, 70, 18), "ms avg"); GUI.Label(new Rect(300, y, 60, 18), "worst"); GUI.Label(new Rect(360, y, 50, 18), "% frame"); GUI.Label(new Rect(415, y, 50, 18), "calls"); y += 18;
            var rows = Enumerable.Range(0, Targets.Length).Where(i => index.ContainsValue(i)).Select(i => (i, s: Stat(i))).OrderByDescending(r => r.s.avg);
            foreach (var (i, s) in rows)
            {
                GUI.Label(new Rect(8, y, 220, 18), Targets[i].label);
                GUI.Label(new Rect(230, y, 70, 18), s.avg.ToString("F2")); GUI.Label(new Rect(300, y, 60, 18), s.max.ToString("F1"));
                GUI.Label(new Rect(360, y, 50, 18), (s.avg / Mathf.Max(0.01f, fm) * 100f).ToString("F0")); GUI.Label(new Rect(415, y, 50, 18), s.calls.ToString("F0"));
                y += 17;
            }
            y += 4;
            GUI.Label(new Rect(8, y, 450, 18), $"Kraken guard: {KrakenGuard.Fixes} runaway parts caught, {KrakenGuard.Unpacks} gentle unpacks"); y += 18;
            if (!string.IsNullOrEmpty(KrakenGuard.LastEvent)) { GUI.Label(new Rect(8, y, 450, 18), "last: " + KrakenGuard.LastEvent); y += 18; }
            if (GUI.Button(new Rect(8, y, 150, 20), csv ? "Logging CSV..." : "Log CSV", csv ? sel : btn)) { csv = !csv; if (!csv) { writer?.Close(); writer = null; } }
            if (GUI.Button(new Rect(162, y, 100, 20), "Reset", btn)) { frames = 0; head = 0; }
            if (GUI.Button(new Rect(266, y, 100, 20), "Close", btn)) AvionicsSettings.Profiler = false;
            y += 26;
            win.height = y;
            GUI.DragWindow();
        }
    }
}
