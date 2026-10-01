using UnityEngine;

using BDArmory.Settings;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Flight-scene host for the C4ISR windows: console (A), map overlay and drawer (B), IADS, data link and settings.
    /// Hotkeys: Right Alt + C / I / L.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class C4ISRCore : MonoBehaviour
    {
        public static C4ISRCore Instance;
        const string LockID = "BDAC4ISRWindows";
        bool locked;
        int consoleIdx = -1, iadsIdx = -1, linkIdx = -1, settingsIdx = -1, drawerIdx = -1;

        void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
            AvionicsSettings.Load();
        }

        void Start()
        {
            consoleIdx = GUIUtils.RegisterGUIRect(new Rect());
            iadsIdx = GUIUtils.RegisterGUIRect(new Rect());
            linkIdx = GUIUtils.RegisterGUIRect(new Rect());
            settingsIdx = GUIUtils.RegisterGUIRect(new Rect());
            drawerIdx = GUIUtils.RegisterGUIRect(new Rect());
        }

        void OnDestroy()
        {
            if (locked) InputLockManager.RemoveControlLock(LockID);
            foreach (var i in new[] { consoleIdx, iadsIdx, linkIdx, settingsIdx, drawerIdx })
                if (i >= 0) GUIUtils.SetGUIRectVisible(i, false);
            C2UI.Placing = PlaceMode.None;
            if (Instance == this) Instance = null;
        }

        static bool DrawerVisible => C4ISRSettings.ShowMapOverlay && MapView.MapIsEnabled;

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (Input.GetKey(KeyCode.RightAlt) && GUIUtility.keyboardControl == 0)
            {
                if (Input.GetKeyDown(C4ISRSettings.KeyC2Window)) C4ISRSettings.ShowConsole = !C4ISRSettings.ShowConsole;
                if (Input.GetKeyDown(C4ISRSettings.KeyIADSWindow)) C4ISRSettings.ShowIADS = !C4ISRSettings.ShowIADS;
                if (Input.GetKeyDown(C4ISRSettings.KeyLinkWindow)) C4ISRSettings.ShowLink = !C4ISRSettings.ShowLink;
            }
            if (Input.GetKeyDown(KeyCode.Escape)) C2UI.Placing = PlaceMode.None;

            // Stop scroll-zoom and clicks over our windows from reaching the camera and map.
            Vector2 m = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool over = BDArmorySetup.GAME_UI_ENABLED && MouseOverC4ISRWindow(m);
            if (over != locked)
            {
                if (over) InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS | ControlTypes.MAP_UI | ControlTypes.MAP_TOGGLE, LockID);
                else InputLockManager.RemoveControlLock(LockID);
                locked = over;
            }
        }

        void LateUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            UpdateRect(consoleIdx, C4ISRSettings.ConsoleRect, C4ISRSettings.ShowConsole);
            UpdateRect(iadsIdx, C4ISRSettings.IADSRect, C4ISRSettings.ShowIADS);
            UpdateRect(linkIdx, C4ISRSettings.LinkRect, C4ISRSettings.ShowLink);
            UpdateRect(settingsIdx, C4ISRSettings.SettingsRect, C4ISRSettings.ShowSettings);
            UpdateRect(drawerIdx, C2MapOverlay.Drawer, DrawerVisible);
        }

        static void UpdateRect(int index, Rect r, bool visible)
        {
            if (index < 0) return;
            bool show = visible && BDArmorySetup.GAME_UI_ENABLED;
            if (GUIUtils.extraGUIRects == null || !GUIUtils.extraGUIRects.TryGetValue(index, out var e)) return;
            if (e.rect != r) GUIUtils.UpdateGUIRect(r, index);
            if (e.visible != show) GUIUtils.SetGUIRectVisible(index, show);
        }

        public static bool MouseOverC4ISRWindow(Vector2 m)
        {
            float s = BDArmorySettings.UI_SCALE_ACTUAL;
            bool In(Rect r) => new Rect(r.x, r.y, r.width * s, r.height * s).Contains(m);
            return (C4ISRSettings.ShowConsole && In(C4ISRSettings.ConsoleRect))
                || (C4ISRSettings.ShowIADS && In(C4ISRSettings.IADSRect))
                || (C4ISRSettings.ShowLink && In(C4ISRSettings.LinkRect))
                || (C4ISRSettings.ShowSettings && In(C4ISRSettings.SettingsRect))
                || (DrawerVisible && In(C2MapOverlay.Drawer));
        }

        void OnGUI()
        {
            if (!HighLogic.LoadedSceneIsFlight || !BDArmorySetup.GAME_UI_ENABLED) return;
            var skin = BDArmorySetup.BDGuiSkin;

            if (DrawerVisible)
            {
                C2MapOverlay.DrawOverlay();
                C2MapOverlay.HandleClick();
                var d = AvionicsCore.DrawWindow(C2MapOverlay.DrawerID, C2MapOverlay.Drawer, C2MapOverlay.DrawDrawer, "C2", skin);
                if (d.position != C2MapOverlay.Drawer.position) C4ISRSettings.DrawerRect = new Rect(d.position, C4ISRSettings.DrawerRect.size);
            }
            if (C4ISRSettings.ShowConsole)
                C4ISRSettings.ConsoleRect = Keep(AvionicsCore.DrawWindow(C2ConsoleWindow.WindowID, C4ISRSettings.ConsoleRect, C2ConsoleWindow.Draw, "C2 Console", skin), C4ISRSettings.ConsoleRect);
            if (C4ISRSettings.ShowIADS)
                C4ISRSettings.IADSRect = Keep(AvionicsCore.DrawWindow(IADSWindow.WindowID, C4ISRSettings.IADSRect, IADSWindow.Draw, "IADS", skin), C4ISRSettings.IADSRect);
            if (C4ISRSettings.ShowLink)
                C4ISRSettings.LinkRect = Keep(AvionicsCore.DrawWindow(DataLinkWindow.WindowID, C4ISRSettings.LinkRect, DataLinkWindow.Draw, "Data Link", skin), C4ISRSettings.LinkRect);
            if (C4ISRSettings.ShowSettings)
                C4ISRSettings.SettingsRect = Keep(AvionicsCore.DrawWindow(C4ISRSettingsWindow.WindowID, C4ISRSettings.SettingsRect, C4ISRSettingsWindow.Draw, "C4ISR Settings", skin), C4ISRSettings.SettingsRect);
        }

        /// <summary>Windows that resize themselves change the settings rect's size; take only the dragged position from GUI.Window.</summary>
        static Rect Keep(Rect dragged, Rect current) => new(dragged.position, current.size);
    }
}
