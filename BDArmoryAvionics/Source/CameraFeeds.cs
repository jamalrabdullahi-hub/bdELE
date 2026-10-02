using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Bridge to OfCourseIStillLoveYou (OCISLY): opens HullCam VDS cameras as OCISLY feeds and turns streaming on, so they
    /// show in its desktop client (second monitor). Reflection only: ELE works without OCISLY installed.
    /// </summary>
    public static class Ocisly
    {
        static bool resolved;
        static Type hullCamType, trackingType;
        static IDictionary tracked;            // Core.TrackedCameras (int -> TrackingCamera)
        static ConstructorInfo ctor;
        static MethodInfo setStreaming, disable;
        static PropertyInfo streaming, enabledProp;
        static FieldInfo hullField;

        public static bool Available { get { Resolve(); return tracked != null && ctor != null; } }

        static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            try
            {
                foreach (var a in AssemblyLoader.loadedAssemblies)
                {
                    if (a.name == "HullcamVDSContinued") hullCamType = a.assembly.GetType("HullcamVDS.MuMechModuleHullCamera");
                    if (a.name != "OfCourseIStillLoveYou") continue;
                    var core = a.assembly.GetType("OfCourseIStillLoveYou.Core");
                    trackingType = a.assembly.GetType("OfCourseIStillLoveYou.TrackingCamera");
                    tracked = core?.GetField("TrackedCameras", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as IDictionary;
                }
                if (trackingType == null || hullCamType == null) return;
                const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                ctor = trackingType.GetConstructor(bf, null, [typeof(int), hullCamType], null);
                streaming = trackingType.GetProperty("StreamingEnabled", bf);
                setStreaming = streaming?.GetSetMethod(true);
                enabledProp = trackingType.GetProperty("Enabled", bf);
                disable = trackingType.GetMethod("Disable", bf);
                hullField = trackingType.GetField("_hullcamera", bf);
                Debug.Log($"[BDArmoryAvionics]: OCISLY {(Available ? "found" : "found but its API differs")}");
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: OCISLY bridge failed: {e.Message}"); }
        }

        public static List<PartModule> Cameras(Vessel v)
        {
            var list = new List<PartModule>();
            Resolve();
            if (v == null || hullCamType == null) return list;
            foreach (var p in v.parts) foreach (PartModule m in p.Modules) if (hullCamType.IsInstanceOfType(m)) list.Add(m);
            return list;
        }

        public static bool IsOpen(PartModule cam) => Available && cam != null && tracked.Contains(cam.GetInstanceID());

        public static bool IsStreaming(PartModule cam)
        {
            if (!IsOpen(cam)) return false;
            try { return (bool)streaming.GetValue(tracked[cam.GetInstanceID()]); } catch { return false; }
        }

        /// <summary>Open a camera as an OCISLY feed (its in-game window plus streaming to the desktop client).</summary>
        public static bool Open(PartModule cam, string why)
        {
            if (!Available || cam == null) return false;
            try
            {
                int id = cam.GetInstanceID();
                if (!tracked.Contains(id)) tracked.Add(id, ctor.Invoke([id, cam]));
                setStreaming?.Invoke(tracked[id], [true]);
                Debug.Log($"[BDArmoryAvionics]: OCISLY feed: {cam.vessel?.GetName()} / {cam.part.partInfo.title} ({why})");
                return true;
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: OCISLY open failed: {e.InnerException?.Message ?? e.Message}"); return false; }
        }

        public static void Close(PartModule cam)
        {
            if (!IsOpen(cam)) return;
            try { var tc = tracked[cam.GetInstanceID()]; disable?.Invoke(tc, null); tracked.Remove(cam.GetInstanceID()); }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: OCISLY close failed: {e.Message}"); }
        }

        /// <summary>Open the first camera on a craft (no-op if it has none, or one is already open).</summary>
        public static void OpenFirst(Vessel v, string why)
        {
            if (!AvionicsSettings.AutoFeeds || !Available) return;
            var cams = Cameras(v);
            if (cams.Count == 0 || cams.Exists(IsOpen)) return;
            Open(cams[0], why);
        }

        /// <summary>Drop feeds whose camera is gone (destroyed / unloaded).</summary>
        public static void Prune()
        {
            if (!Available) return;
            var dead = new List<object>();
            foreach (DictionaryEntry kv in tracked)
            {
                var cam = hullField?.GetValue(kv.Value) as PartModule;
                if (cam == null || cam.vessel == null || !cam.vessel.loaded) dead.Add(kv.Key);
            }
            foreach (var k in dead) { try { disable?.Invoke(tracked[k], null); } catch { } tracked.Remove(k); }
        }
    }

    /// <summary>Camera feeds window (menu "Camera feeds"): every HullCam on your team, one click to stream it via OCISLY.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class CameraFeedsWindow : MonoBehaviour
    {
        const int WindowID = 71730345;
        static Rect rect = new(Screen.width - 900, 200, 430, 360);
        Vector2 scroll;
        float nextPrune;

        void Update()
        {
            if (Time.time < nextPrune) return;
            nextPrune = Time.time + 3f;
            try { Ocisly.Prune(); } catch { }
        }

        void OnGUI()
        {
            if (!AvionicsSettings.ShowFeeds || !BDArmorySetup.GAME_UI_ENABLED) return;
            rect = AvionicsCore.DrawWindow(WindowID, rect, Draw, "Camera feeds (OCISLY)", BDArmorySetup.BDGuiSkin);
        }

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { AvionicsSettings.ShowFeeds = false; return; }
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle; var small = AvionicsDraw.LabelSmall;
            float w = rect.width - 16, y = 24;
            if (!Ocisly.Available)
            {
                AvionicsDraw.ColorLabel(new Rect(8, y, w, 60), "OfCourseIStillLoveYou isn't loaded.\nInstall its plugin in GameData (and HullCam VDS).", AvionicsDraw.Label, AvionicsDraw.Unknown);
                GUI.DragWindow(); return;
            }
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 28), "Start OfCourseIStillLoveYou.Server, then the DesktopClient\n(Documents\\KSP-Mods\\OCISLY) to see the feeds outside KSP.", small, AvionicsDraw.ScopeText); y += 30;
            AvionicsSettings.AutoFeeds = GUI.Toggle(new Rect(8, y, w, 20), AvionicsSettings.AutoFeeds, "Auto feeds: loitering munitions, CSAR pickups", AvionicsSettings.AutoFeeds ? sel : btn); y += 26;

            var team = BDArmorySetup.Instance?.ActiveWeaponManager?.Team;
            var rows = new List<(Vessel v, PartModule cam)>();
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null) continue;
                var wm = v.ActiveController().WM;
                bool ours = v == FlightGlobals.ActiveVessel || team == null || (wm != null && wm.Team == team) || LoiteringMunitions.For(v) != null;
                if (!ours) continue;
                foreach (var c in Ocisly.Cameras(v)) rows.Add((v, c));
            }
            if (rows.Count == 0) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), "No HullCam cameras on your team's loaded craft.", AvionicsDraw.Label, AvionicsDraw.ScopeText); GUI.DragWindow(); return; }
            scroll = GUI.BeginScrollView(new Rect(8, y, w, rect.height - y - 6), scroll, new Rect(0, 0, w - 18, rows.Count * 22));
            for (int i = 0; i < rows.Count; i++)
            {
                var (v, cam) = rows[i];
                bool open = Ocisly.IsOpen(cam);
                AvionicsDraw.ColorLabel(new Rect(0, i * 22 + 3, w - 110, 16), $"{v.GetName()} - {cam.part.partInfo.title}{(open ? (Ocisly.IsStreaming(cam) ? "  STREAMING" : "  open") : "")}", small, open ? AvionicsDraw.Friendly : AvionicsDraw.HudText);
                if (GUI.Button(new Rect(w - 104, i * 22, 84, 20), open ? "Close" : "Stream", open ? sel : btn)) { if (open) Ocisly.Close(cam); else Ocisly.Open(cam, "feeds window"); }
            }
            GUI.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, rect.width - 22, 20));
        }
    }
}
