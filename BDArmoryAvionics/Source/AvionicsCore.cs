using System.Collections;
using KSP.UI.Screens;
using UnityEngine;

using BDArmory.Settings;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Flight-scene entry point for the avionics windows: toolbar button, menu, hotkeys, contact refresh and drawing.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class AvionicsCore : MonoBehaviour
    {
        public static AvionicsCore Instance;
        const int MenuWindowID = 71730300;

        ApplicationLauncherButton toolbarButton;
        bool showMenu = false;
        Rect menuRect;
        const string KeyLockID = "BDAAvionicsTextInput";
        bool keyboardLocked = false;
        int radarRectIndex = -1, listRectIndex = -1, gpsRectIndex = -1, menuRectIndex = -1;

        void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
            AvionicsSettings.Load();
            ThreatWarning.Subscribe();
        }

        void Start()
        {
            menuRect = new Rect(Screen.width - 300, 90, 240, 620);
            radarRectIndex = GUIUtils.RegisterGUIRect(new Rect());
            listRectIndex = GUIUtils.RegisterGUIRect(new Rect());
            gpsRectIndex = GUIUtils.RegisterGUIRect(new Rect());
            menuRectIndex = GUIUtils.RegisterGUIRect(new Rect());
            StartCoroutine(AddToolbarButton());
        }

        IEnumerator AddToolbarButton()
        {
            float giveUp = Time.realtimeSinceStartup + 15;
            yield return new WaitUntil(() => ApplicationLauncher.Ready && (BDArmorySetup.toolbarButtonAdded || Time.realtimeSinceStartup > giveUp));
            if (toolbarButton != null) yield break;
            toolbarButton = ApplicationLauncher.Instance.AddModApplication(
                () => showMenu = true, () => showMenu = false, null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW,
                AvionicsDraw.IconTexture);
        }

        void OnDestroy()
        {
            AvionicsSettings.Save();
            ThreatWarning.Unsubscribe();
            if (toolbarButton != null && ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);
            foreach (var i in new[] { radarRectIndex, listRectIndex, gpsRectIndex, menuRectIndex })
                if (i >= 0) GUIUtils.SetGUIRectVisible(i, false);
            GPSMarkers.DropArmed = false;
            if (keyboardLocked) InputLockManager.RemoveControlLock(KeyLockID);
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (!Input.GetMouseButton(0)) { Radar3DWindow.CancelDrag(); TargetListWindow.CancelResize(); }

            // Stop typed GPS names from also triggering KSP controls
            bool lockKeys = AvionicsSettings.ShowGPSManager && BDArmorySetup.GAME_UI_ENABLED && GPSManagerWindow.TextFocused;
            if (lockKeys != keyboardLocked)
            {
                if (lockKeys) InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, KeyLockID);
                else InputLockManager.RemoveControlLock(KeyLockID);
                keyboardLocked = lockKeys;
            }

            if (GPSMarkers.TryDrop()) return;
            if (GUIUtility.keyboardControl != 0) return; // typing in a text field

            if (KeyDown(AvionicsSettings.KeyNextTarget)) AvionicsContacts.Cycle(1);
            if (KeyDown(AvionicsSettings.KeyPrevTarget)) AvionicsContacts.Cycle(-1);
            if (KeyDown(AvionicsSettings.KeyLockTarget)) AvionicsContacts.Lock(AvionicsContacts.Selected);
            if (KeyDown(AvionicsSettings.KeySlewCamera)) AvionicsContacts.SlewCamera(AvionicsContacts.Selected);
        }

        static bool KeyDown(KeyCode k) => k != KeyCode.None && Input.GetKeyDown(k);
        static float hw2(float w) => (w - 4) / 2; // half-width menu button

        void LateUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            AvionicsContacts.Refresh();
            ThreatWarning.Refresh();

            UpdateRect(radarRectIndex, AvionicsSettings.Radar3DRect, AvionicsSettings.ShowRadar3D);
            UpdateRect(listRectIndex, AvionicsSettings.TargetListRect, AvionicsSettings.ShowTargetList);
            UpdateRect(gpsRectIndex, AvionicsSettings.GPSManagerRect, AvionicsSettings.ShowGPSManager);
            UpdateRect(menuRectIndex, menuRect, showMenu);
        }

        static void UpdateRect(int index, Rect r, bool visible)
        {
            if (index < 0) return;
            bool show = visible && BDArmorySetup.GAME_UI_ENABLED;
            if (GUIUtils.extraGUIRects == null || !GUIUtils.extraGUIRects.TryGetValue(index, out var e)) return;
            if (e.rect != r) GUIUtils.UpdateGUIRect(r, index);
            if (e.visible != show) GUIUtils.SetGUIRectVisible(index, show);
        }

        public static bool MouseOverAnyWindow()
        {
            Vector2 m = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (m.y < 65) return true;
            if (AvionicsSettings.ShowRadar3D && GUIUtils.MouseIsInRect(AvionicsSettings.Radar3DRect, m)) return true;
            if (AvionicsSettings.ShowTargetList && GUIUtils.MouseIsInRect(AvionicsSettings.TargetListRect, m)) return true;
            if (AvionicsSettings.ShowGPSManager && GUIUtils.MouseIsInRect(AvionicsSettings.GPSManagerRect, m)) return true;
            if (Instance != null && Instance.showMenu && GUIUtils.MouseIsInRect(Instance.menuRect, m)) return true;
            if (BDArmorySetup.windowBDAToolBarEnabled && GUIUtils.MouseIsInRect(BDArmorySetup.WindowRectToolbar, m)) return true;
            if (ModuleTargetingCamera.windowIsOpen && GUIUtils.MouseIsInRect(BDArmorySetup.WindowRectTargetingCam, m)) return true;
            var wm = AvionicsContacts.WM;
            if (wm != null && wm.vesselRadarData && wm.vesselRadarData.guiEnabled && GUIUtils.MouseIsInRect(BDArmorySetup.WindowRectRadar, m)) return true;
            return false;
        }

        void OnGUI()
        {
            if (!HighLogic.LoadedSceneIsFlight || !BDArmorySetup.GAME_UI_ENABLED) return;

            // Markers first, so windows draw on top of them
            if (MapView.MapIsEnabled) GPSMarkers.DrawMap();
            else GPSMarkers.DrawFlight();
            GPSMarkers.DrawDropHint();
            ThreatWarning.DrawBanner();

            var skin = BDArmorySetup.BDGuiSkin;
            if (showMenu)
                menuRect = DrawWindow(MenuWindowID, menuRect, MenuWindow, "BDA: Enhanced Lethality", skin);
            if (AvionicsSettings.ShowRadar3D)
                AvionicsSettings.Radar3DRect = KeepSize(DrawWindow(Radar3DWindow.WindowID, AvionicsSettings.Radar3DRect, Radar3DWindow.Draw, "3D Radar", skin), AvionicsSettings.Radar3DRect);
            if (AvionicsSettings.ShowTargetList)
                AvionicsSettings.TargetListRect = KeepSize(DrawWindow(TargetListWindow.WindowID, AvionicsSettings.TargetListRect, TargetListWindow.Draw, "Target List", skin), AvionicsSettings.TargetListRect);
            if (AvionicsSettings.ShowGPSManager)
                AvionicsSettings.GPSManagerRect = KeepSize(DrawWindow(GPSManagerWindow.WindowID, AvionicsSettings.GPSManagerRect, GPSManagerWindow.Draw, "GPS Manager", skin), AvionicsSettings.GPSManagerRect);
        }

        /// <summary>Window functions resize their own settings rect; keep that size and take only the dragged position.</summary>
        static Rect KeepSize(Rect dragged, Rect current) => new(dragged.position, current.size);

        public static Rect DrawWindow(int id, Rect rect, GUI.WindowFunction func, string title, GUISkin skin)
        {
            var m = GUI.matrix;
            float s = BDArmorySettings.UI_SCALE_ACTUAL;
            if (s != 1) GUIUtility.ScaleAroundPivot(s * Vector2.one, rect.position);
            rect = GUI.Window(id, rect, func, title, skin.window);
            GUI.matrix = m;
            BDAHooks.KeepOnScreen(ref rect);
            return rect;
        }

        void MenuWindow(int id)
        {
            if (GUI.Button(new Rect(menuRect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle))
            {
                showMenu = false;
                if (toolbarButton != null) toolbarButton.SetFalse(false);
                return;
            }
            var btn = BDArmorySetup.ButtonStyle;
            var sel = BDArmorySetup.SelectedButtonStyle;
            float y = 24, w = menuRect.width - 16;
            AvionicsSettings.ShowRadar3D = GUI.Toggle(new Rect(8, y, w, 22), AvionicsSettings.ShowRadar3D, "3D Radar", AvionicsSettings.ShowRadar3D ? sel : btn); y += 24;
            AvionicsSettings.ShowTargetList = GUI.Toggle(new Rect(8, y, w, 22), AvionicsSettings.ShowTargetList, "Target List", AvionicsSettings.ShowTargetList ? sel : btn); y += 24;
            AvionicsSettings.ShowGPSManager = GUI.Toggle(new Rect(8, y, w, 22), AvionicsSettings.ShowGPSManager, "GPS Manager", AvionicsSettings.ShowGPSManager ? sel : btn); y += 28;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), $"Ejection seat: press {AvionicsSettings.KeyEject} twice", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
            AvionicsSettings.AutoEjectOnDestruction = GUI.Toggle(new Rect(8, y, w, 20), AvionicsSettings.AutoEjectOnDestruction, "Auto-eject if cockpit destroyed", AvionicsSettings.AutoEjectOnDestruction ? sel : btn); y += 22;
            AvionicsSettings.AutoEjectBeforeImpact = GUI.Toggle(new Rect(8, y, w, 20), AvionicsSettings.AutoEjectBeforeImpact, "Auto-eject before ground impact", AvionicsSettings.AutoEjectBeforeImpact ? sel : btn); y += 22;
            AvionicsSettings.ThreatBanner = GUI.Toggle(new Rect(8, y, w, 20), AvionicsSettings.ThreatBanner, "Missile / lock warning banner", AvionicsSettings.ThreatBanner ? sel : btn); y += 22;
            AvionicsSettings.FormationAssist = GUI.Toggle(new Rect(8, y, w, 20), AvionicsSettings.FormationAssist, "AI formation assist", AvionicsSettings.FormationAssist ? sel : btn); y += 22;
            AvionicsSettings.TakeoffAssist = GUI.Toggle(new Rect(8, y, hw2(w), 20), AvionicsSettings.TakeoffAssist, "AI takeoff assist", AvionicsSettings.TakeoffAssist ? sel : btn);
            AvionicsSettings.AirOpsTelemetry = GUI.Toggle(new Rect(12 + hw2(w), y, hw2(w), 20), AvionicsSettings.AirOpsTelemetry, "AI telemetry log", AvionicsSettings.AirOpsTelemetry ? sel : btn); y += 22;
            AvionicsSettings.AltitudeFromSeaLevel = GUI.Toggle(new Rect(8, y, w, 20), AvionicsSettings.AltitudeFromSeaLevel,
                AvionicsSettings.AltitudeFromSeaLevel ? "AI altitudes: from sea level" : "AI altitudes: from ground (stock)", AvionicsSettings.AltitudeFromSeaLevel ? sel : btn); y += 22;
            AvionicsSettings.CombatOperators = GUI.Toggle(new Rect(8, y, w, 20), AvionicsSettings.CombatOperators, "Combat operators (crew skill)", AvionicsSettings.CombatOperators ? sel : btn); y += 22;
            AfterActionWindow.Show = GUI.Toggle(new Rect(8, y, hw2(w), 20), AfterActionWindow.Show, CombatLog.Live != null ? "AAR (live)" : "After-action rpt", AfterActionWindow.Show ? sel : btn);
            AvionicsSettings.AARPopup = GUI.Toggle(new Rect(12 + hw2(w), y, hw2(w), 20), AvionicsSettings.AARPopup, "AAR at battle end", AvionicsSettings.AARPopup ? sel : btn); y += 26;
            var small = AvionicsDraw.LabelSmall;

            // C4ISR
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), "C4ISR", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
            float hw = (w - 4) / 2;
            C4ISRSettings.ShowConsole = GUI.Toggle(new Rect(8, y, hw, 20), C4ISRSettings.ShowConsole, "C2 Console", C4ISRSettings.ShowConsole ? sel : btn);
            C4ISRSettings.ShowMapOverlay = GUI.Toggle(new Rect(12 + hw, y, hw, 20), C4ISRSettings.ShowMapOverlay, "Map overlay", C4ISRSettings.ShowMapOverlay ? sel : btn); y += 22;
            C4ISRSettings.ShowIADS = GUI.Toggle(new Rect(8, y, hw, 20), C4ISRSettings.ShowIADS, "IADS", C4ISRSettings.ShowIADS ? sel : btn);
            C4ISRSettings.ShowLink = GUI.Toggle(new Rect(12 + hw, y, hw, 20), C4ISRSettings.ShowLink, "Data link", C4ISRSettings.ShowLink ? sel : btn); y += 22;
            C4ISRSettings.GlobalC2 = GUI.Toggle(new Rect(8, y, hw, 20), C4ISRSettings.GlobalC2, "Global C2", C4ISRSettings.GlobalC2 ? sel : btn);
            C4ISRSettings.ShowSettings = GUI.Toggle(new Rect(12 + hw, y, hw, 20), C4ISRSettings.ShowSettings, "Settings", C4ISRSettings.ShowSettings ? sel : btn); y += 22;
            AvionicsSettings.ShowEW = GUI.Toggle(new Rect(8, y, w, 20), AvionicsSettings.ShowEW, $"Electronic warfare · stealth {StealthRCS.ModeNames[AvionicsSettings.StealthModel]}", AvionicsSettings.ShowEW ? sel : btn); y += 22;
            AvionicsSettings.ShowLM = GUI.Toggle(new Rect(8, y, hw, 20), AvionicsSettings.ShowLM, "Loitering munitions", AvionicsSettings.ShowLM ? sel : btn);
            TheatreWindow.Show = GUI.Toggle(new Rect(12 + hw, y, hw, 20), TheatreWindow.Show, "Theatre", TheatreWindow.Show ? sel : btn); y += 22;
            AvionicsSettings.ShowFeeds = GUI.Toggle(new Rect(8, y, w, 20), AvionicsSettings.ShowFeeds, Ocisly.Available ? "Camera feeds (OCISLY)" : "Camera feeds (OCISLY not loaded)", AvionicsSettings.ShowFeeds ? sel : btn); y += 24;
            var net = DataLinkNetwork.PlayerNetwork;
            var iads = net != null ? IADSController.For(net.team) : null;
            int engaged = 0;
            if (iads != null) foreach (var s in iads.sites.Values) if (s.assigned != null) engaged++;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), net == null ? "Net: no weapon manager" : $"Net: {net.ConnectedCount}/{net.nodes.Count} linked, {net.tracks.Count} tracks", small, AvionicsDraw.ScopeText); y += 13;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), iads == null ? "IADS: idle" : $"IADS: {iads.sites.Count} SAM sites, {engaged} engaging", small, AvionicsDraw.ScopeText); y += 13;
            if (iads != null && iads.log.Count > 0) AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), iads.log[0], small, AvionicsDraw.ScopeText);
            y += 17;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), "FLIR and the pod HUD are controlled from", small, AvionicsDraw.ScopeText); y += 13;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), "the bottom of the Target Camera window.", small, AvionicsDraw.ScopeText); y += 16;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), "Keys: BDArmoryAvionics/PluginData/Avionics.cfg", small, AvionicsDraw.ScopeText);
            GUIUtils.DragWindow();
        }
    }
}
