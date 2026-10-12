using System.Linq;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// The combat strip: one thin bar along the bottom of the screen (between the staging stack and the navball) with what
    /// you need while flying: target and lock, how many radars see you, wingmen, fuel, G, network. Click an item to open its
    /// full window. Replaces keeping those windows open.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class HudStrip : MonoBehaviour
    {
        GUIStyle cell, head;
        float nextData; string tgt = "", seen = "", wing = "", fuel = "", g = "", net = "";
        Color tgtC, seenC, fuelC, gC;

        void Update()
        {
            if (!AvionicsSettings.HudStrip || Time.time < nextData) return;
            nextData = Time.time + 0.25f;
            var v = FlightGlobals.ActiveVessel; if (v == null) return;

            var c = AvionicsContacts.Selected;
            var wm = v.ActiveController().WM;
            if (c != null) { float km = (c.position - (Vector3)v.CoM).magnitude / 1000f; tgt = $"TGT {Clip(c.name, 14)} {km:F1}km{(c.activeLock ? " LOCK" : c.radarLocked ? " trk" : "")}"; tgtC = c.activeLock ? AvionicsDraw.Hostile : AvionicsDraw.HudText; }
            else if (wm != null && wm.currentTarget != null && wm.currentTarget.Vessel != null) { tgt = $"TGT {Clip(wm.currentTarget.Vessel.GetDisplayName(), 14)} (guard)"; tgtC = AvionicsDraw.HudText; }
            else { tgt = "TGT none"; tgtC = AvionicsDraw.ScopeText; }

            LiveStealth.Want();
            int det = LiveStealth.Threats.Count(t => t.detected), lck = LiveStealth.Threats.Count(t => t.locked);
            seen = LiveStealth.Threats.Count == 0 ? "RDR clear" : $"RDR {LiveStealth.Threats.Count} · seen {det}{(lck > 0 ? $" · LOCK {lck}" : "")}";
            seenC = lck > 0 ? AvionicsDraw.Hostile : det > 0 ? new Color(1f, 0.6f, 0.2f) : AvionicsDraw.Friendly;

            wing = Wingman.FlightCount > 0 ? $"WING {Wingman.FlightCount}" : "WING -";

            double f = 0, m = 0;
            foreach (var p in v.parts) foreach (PartResource r in p.Resources) if (r.resourceName == "LiquidFuel") { f += r.amount; m += r.maxAmount; }
            float frac = m > 0 ? (float)(f / m) : 0;
            fuel = m > 0 ? $"FUEL {frac * 100:F0}%" : "FUEL -"; fuelC = frac < 0.2f ? AvionicsDraw.Hostile : frac < 0.35f ? AvionicsDraw.Unknown : AvionicsDraw.HudText;

            g = $"G {CockpitLife.Gz:F1}"; gC = Mathf.Abs(CockpitLife.Gz) > 7 ? AvionicsDraw.Hostile : AvionicsDraw.HudText;

            var n = DataLinkNetwork.PlayerNetwork;
            net = n == null ? "NET -" : $"NET {n.ConnectedCount}/{n.nodes.Count} · {n.tracks.Count} trk";
        }

        static string Clip(string s, int n) => string.IsNullOrEmpty(s) ? "?" : s.Length > n ? s.Substring(0, n) : s;

        void OnGUI()
        {
            if (!AvionicsSettings.HudStrip || !BDArmorySetup.GAME_UI_ENABLED || KillCam.Playing || MapView.MapIsEnabled || FlightGlobals.ActiveVessel == null) return;
            cell ??= new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };
            float x0 = 190, x1 = Screen.width * 0.42f, h = 20, y = Screen.height - h - 2;
            var bar = new Rect(x0, y, x1 - x0, h);
            GUI.color = new Color(0, 0, 0, 0.5f); GUI.DrawTexture(bar, Texture2D.whiteTexture); GUI.color = Color.white;
            // cells (relative widths), each opens its window
            var items = new (string text, Color col, float w, System.Action click)[]
            {
                (tgt, tgtC, 2.2f, () => AvionicsSettings.ShowTargetList = !AvionicsSettings.ShowTargetList),
                (seen, seenC, 1.8f, () => LiveStealth.Show = !LiveStealth.Show),
                (wing, AvionicsDraw.HudText, 0.8f, Wingman.ToggleBar),
                (fuel, fuelC, 0.9f, null),
                (g, gC, 0.6f, null),
                (net, AvionicsDraw.ScopeText, 1.4f, () => C4ISRSettings.ShowLink = !C4ISRSettings.ShowLink),
            };
            float total = items.Sum(i => i.w), x = x0;
            foreach (var (text, col, w, click) in items)
            {
                float cw = bar.width * w / total;
                var r = new Rect(x, y, cw, h);
                GUI.color = col;
                if (click != null ? GUI.Button(r, text, cell) : false) click();
                else if (click == null) GUI.Label(r, text, cell);
                GUI.color = new Color(1, 1, 1, 0.15f); GUI.DrawTexture(new Rect(x + cw - 1, y + 3, 1, h - 6), Texture2D.whiteTexture);
                x += cw;
            }
            GUI.color = Color.white;
        }
    }
}
