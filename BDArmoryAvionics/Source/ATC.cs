using System.Collections.Generic;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Air traffic control for AI aircraft: one landing direction per runway at a time, arrivals sequenced
    /// (one on the approach, one on final), the rest stacked in a hold 300 m apart; landing clearance only when the
    /// runway is clear, departures held while someone is on short final.
    /// </summary>
    public static class ATC
    {
        public class Arrival { public Vessel v; public string runway; public bool reversed; public float since; }
        public static readonly List<Arrival> Arrivals = new();
        static readonly Dictionary<Vessel, (string rw, float t)> departures = new();
        public static readonly List<string> Log = new();
        const float HoldBase = 1500f, HoldStep = 300f;

        public static bool On => AirfieldsScenario.ATCEnabled;

        static string Callsign(Vessel v) => v != null ? v.GetName() : "?";
        static string Rwy(Runway rw)
        {
            var d = AirfieldsScenario.Find(rw.name);
            string n = d != null ? d.Numbers : Runways.IsReversed(rw.name) ? "rev" : "";
            var ends = n.Split('/');
            string num = ends.Length == 2 ? (Runways.IsReversed(rw.name) ? ends[1] : ends[0]) : "";
            return $"{Runways.BaseName(rw.name)}{(num != "" ? " " + num : "")}";
        }

        static void Say(Vessel v, string text)
        {
            string line = $"[{System.DateTime.Now:HH:mm:ss}] {text}";
            Log.Insert(0, line); if (Log.Count > 40) Log.RemoveAt(Log.Count - 1);
            Debug.Log($"[BDArmoryAvionics]: ATC {text}");
            var my = BDArmorySetup.Instance?.ActiveWeaponManager?.Team;
            var team = v != null ? v.ActiveController().WM?.Team : null;
            if (my == null || team == null || team == my) ScreenMessages.PostScreenMessage($"<color=#7fe0ff>ATC: {text}</color>", 4f, ScreenMessageStyle.UPPER_LEFT);
        }

        static void Prune()
        {
            Arrivals.RemoveAll(a => a.v == null || !a.v.loaded || AirOps.OpFor(a.v)?.kind != AirOpKind.Landing);
            var dead = new List<Vessel>();
            foreach (var kv in departures) if (kv.Key == null || !kv.Key.loaded || kv.Key.radarAltitude > 150 || Time.time - kv.Value.t > 120) dead.Add(kv.Key);
            foreach (var v in dead) departures.Remove(v);
        }

        /// <summary>Landing direction already in use at this runway (so arrivals don't meet head-on), or null.</summary>
        public static bool? ActiveDirection(string runway)
        {
            if (!On) return null;
            Prune();
            string b = Runways.BaseName(runway);
            foreach (var a in Arrivals) if (a.runway == b) return a.reversed;
            return null;
        }

        public static void Request(Vessel v, Runway rw)
        {
            Prune();
            Arrivals.RemoveAll(a => a.v == v);
            Arrivals.Add(new Arrival { v = v, runway = Runways.BaseName(rw.name), reversed = Runways.IsReversed(rw.name), since = Time.time });
            if (On) Say(v, $"{Callsign(v)}, expect runway {Rwy(rw)}, number {Number(v)}");
        }

        public static void Done(Vessel v, string why)
        {
            if (Arrivals.RemoveAll(a => a.v == v) > 0 && On && why != null) Say(v, $"{Callsign(v)}, {why}");
        }

        /// <summary>1-based position in the arrival sequence for its runway.</summary>
        public static int Number(Vessel v)
        {
            var me = Arrivals.Find(a => a.v == v);
            if (me == null) return 0;
            int n = 0;
            foreach (var a in Arrivals) { if (a.runway != me.runway) continue; n++; if (a.v == v) return n; }
            return 0;
        }

        /// <summary>Hold altitude above the runway for this aircraft's place in the stack.</summary>
        public static float HoldAltitude(Vessel v) => HoldBase + HoldStep * Mathf.Max(0, Number(v) - 2);

        /// <summary>Cleared for the approach: first in the sequence among those not yet on it, and nobody else between the IAF and final.</summary>
        public static bool ClearApproach(Vessel v, Runway rw)
        {
            if (!On) return true;
            Prune();
            string b = Runways.BaseName(rw.name);
            bool ahead = true;
            foreach (var a in Arrivals)
            {
                if (a.runway != b) continue;
                if (a.v == v) { ahead = false; continue; }
                var op = AirOps.OpFor(a.v);
                if (op == null) continue;
                if (op.cleared && (op.phase == "procedure" || op.phase == "approach")) return false; // someone already on the approach
                if (ahead && !op.cleared) return false; // someone ahead of us in the sequence is still waiting
            }
            Say(v, $"{Callsign(v)}, cleared {AirfieldsScenario.ApproachFor(rw)?.name ?? "straight-in"} approach runway {Rwy(rw)}");
            return true;
        }

        /// <summary>The runway is clear for landing: nobody rolling out or vacating ahead, no departure rolling.</summary>
        public static bool ClearToLand(Vessel v, Runway rw, ref bool told)
        {
            if (!On) return true;
            Prune();
            string b = Runways.BaseName(rw.name);
            foreach (var a in Arrivals)
            {
                if (a.runway != b || a.v == v) continue;
                var op = AirOps.OpFor(a.v);
                if (op != null && (op.phase == "flare" || op.phase == "rollout" || op.phase == "vacate")) return false;
            }
            foreach (var kv in departures) if (kv.Value.rw == b && kv.Key != v) return false;
            if (!told) { told = true; Say(v, $"{Callsign(v)}, runway {Rwy(rw)}, cleared to land"); }
            return true;
        }

        public static void GoAround(Vessel v, string why) { if (On) Say(v, $"{Callsign(v)}, go around{(why != null ? ", " + why : "")}"); }

        /// <summary>Departure from the runway this aircraft is sitting on: hold while an arrival is on final inside 6 km or landing.</summary>
        public static bool CanDepart(Vessel v)
        {
            if (!On) return true;
            Prune();
            if (departures.ContainsKey(v)) return true;
            var rw = RunwayUnder(v);
            if (rw == null) return true;
            string b = Runways.BaseName(rw.name);
            foreach (var a in Arrivals)
            {
                if (a.runway != b || a.v == v) continue;
                var op = AirOps.OpFor(a.v);
                if (op == null) continue;
                if (op.phase == "flare" || op.phase == "rollout" || (op.phase == "final" && op.finalDistance < 6000)) return false;
            }
            departures[v] = (b, Time.time);
            Say(v, $"{Callsign(v)}, runway {Runways.BaseName(rw.name)}, cleared for takeoff");
            return true;
        }

        static Runway RunwayUnder(Vessel v)
        {
            var team = v.ActiveController().WM?.Team;
            foreach (var r in Runways.All(v.mainBody, null))
            {
                Vector3 a = (Vector3)r.A, b = (Vector3)r.B, p = v.CoM;
                Vector3 ab = b - a;
                float t = Vector3.Dot(p - a, ab) / ab.sqrMagnitude;
                if (t < -0.05f || t > 1.05f) continue;
                if ((a + ab * Mathf.Clamp01(t) - p).magnitude < 60f) return r;
            }
            return null;
        }
    }

    /// <summary>ATC window: arrivals per runway in sequence, departures and the radio log.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ATCWindow : MonoBehaviour
    {
        public static bool Show;
        const int WindowID = 71730361;
        static Rect rect = new(Screen.width - 560, 140, 440, 420);
        Vector2 scroll, logScroll;

        void OnGUI()
        {
            if (!Show || !BDArmorySetup.GAME_UI_ENABLED) return;
            rect = AvionicsCore.DrawWindow(WindowID, rect, Draw, "ATC", BDArmorySetup.BDGuiSkin);
        }

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; return; }
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle; var small = AvionicsDraw.LabelSmall;
            float w = rect.width - 16, y = 24;
            AirfieldsScenario.ATCEnabled = GUI.Toggle(new Rect(8, y, 120, 20), AirfieldsScenario.ATCEnabled, AirfieldsScenario.ATCEnabled ? "ATC active" : "ATC off", AirfieldsScenario.ATCEnabled ? sel : btn);
            if (GUI.Button(new Rect(132, y, 100, 20), "Airfields…", AirfieldsWindow.Show ? sel : btn)) AirfieldsWindow.Show = !AirfieldsWindow.Show;
            y += 26;

            var rows = new List<string>();
            var vessels = new List<Vessel>();
            var byRunway = new SortedDictionary<string, List<ATC.Arrival>>();
            foreach (var a in ATC.Arrivals) { if (a.v == null) continue; if (!byRunway.TryGetValue(a.runway, out var l)) byRunway[a.runway] = l = new(); l.Add(a); }
            float h = 0; foreach (var kv in byRunway) h += 18 + kv.Value.Count * 22;
            scroll = GUI.BeginScrollView(new Rect(8, y, w, 190), scroll, new Rect(0, 0, w - 18, Mathf.Max(h, 20)));
            float yy = 0;
            if (byRunway.Count == 0) AvionicsDraw.ColorLabel(new Rect(0, 0, w, 16), "No arrivals. Order Land or RTB from the C2 console.", small, AvionicsDraw.ScopeText);
            foreach (var kv in byRunway)
            {
                AvionicsDraw.ColorLabel(new Rect(0, yy, w, 16), $"{kv.Key}  landing {(kv.Value[0].reversed ? "reverse" : "forward")} direction", AvionicsDraw.Header, AvionicsDraw.HudText); yy += 18;
                int n = 1;
                foreach (var a in kv.Value)
                {
                    var op = AirOps.OpFor(a.v);
                    string st = op == null ? "-" : op.phase == "procedure" && !op.cleared ? $"holding {ATC.HoldAltitude(a.v):F0} m" : op.phase == "final" ? $"final {op.finalDistance / 1000f:F1} km" : op.phase;
                    AvionicsDraw.ColorLabel(new Rect(0, yy + 3, w - 160, 16), $"#{n++} {a.v.GetName()}  {st}  {a.v.srfSpeed:F0} m/s", small, op != null && op.cleared ? AvionicsDraw.Friendly : AvionicsDraw.HudText);
                    if (GUI.Button(new Rect(w - 156, yy, 66, 20), "Go around", btn) && op != null) op.forceGoAround = true;
                    if (GUI.Button(new Rect(w - 86, yy, 66, 20), "Cancel", btn)) { AirOps.Cancel(a.v); ATC.Done(a.v, "landing cancelled"); }
                    yy += 22;
                }
            }
            GUI.EndScrollView();
            y += 196;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), "Radio", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
            logScroll = GUI.BeginScrollView(new Rect(8, y, w, rect.height - y - 8), logScroll, new Rect(0, 0, w - 18, ATC.Log.Count * 15));
            for (int i = 0; i < ATC.Log.Count; i++) AvionicsDraw.ColorLabel(new Rect(0, i * 15, w - 18, 15), ATC.Log[i], small, AvionicsDraw.ScopeText);
            GUI.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, rect.width - 22, 20));
        }
    }
}
