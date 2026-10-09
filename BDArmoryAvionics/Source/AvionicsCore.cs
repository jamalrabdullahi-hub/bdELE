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
            menuRect = new Rect(Screen.width - 320, 90, 260, 300);
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
                { menuRect = DrawWindow(MenuWindowID, menuRect, MenuWindow, "BDA: Enhanced Lethality", skin); menuRect.height = menuHeight; }
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

        // ---------------- hub: tabs instead of one long list ----------------
        static readonly string[] Tabs = { "Sensors", "Command", "Air ops", "Effects", "Physics", "System" };
        static int tab;
        const float RowH = 22, Gap = 4;
        float y, w, menuHeight = 300;
        GUIStyle btn, sel;

        /// <summary>Full-width toggle row.</summary>
        bool Row(bool on, string label) { bool r = GUI.Toggle(new Rect(8, y, w, RowH - 2), on, label, on ? sel : btn); y += RowH; return r; }
        /// <summary>Two half-width toggles on one row.</summary>
        void Pair(ref bool a, string la, ref bool b, string lb)
        {
            float h = hw2(w);
            a = GUI.Toggle(new Rect(8, y, h, RowH - 2), a, la, a ? sel : btn);
            b = GUI.Toggle(new Rect(12 + h, y, h, RowH - 2), b, lb, b ? sel : btn);
            y += RowH;
        }
        /// <summary>Toggle on the left, cycling value button on the right.</summary>
        bool ToggleCycle(ref bool on, string label, string value, bool valueLit = false)
        {
            float h = hw2(w);
            on = GUI.Toggle(new Rect(8, y, h, RowH - 2), on, label, on ? sel : btn);
            bool click = GUI.Button(new Rect(12 + h, y, h, RowH - 2), value, valueLit ? sel : btn);
            y += RowH; return click;
        }
        void Header(string text) { y += 4; AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), text, AvionicsDraw.Header, AvionicsDraw.HudText); y += 16; }
        void Note(string text) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), text, AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText); y += 13; }

        /// <summary>Every ELE flight window, for the open count and "close all".</summary>
        static int OpenWindows(bool close)
        {
            int n = 0;
            void C(ref bool b) { if (b) { n++; if (close) b = false; } }
            C(ref AvionicsSettings.ShowRadar3D); C(ref AvionicsSettings.ShowTargetList); C(ref AvionicsSettings.ShowGPSManager);
            C(ref AvionicsSettings.ShowEW); C(ref AvionicsSettings.ShowLM); C(ref AvionicsSettings.ShowFeeds);
            C(ref C4ISRSettings.ShowConsole); C(ref C4ISRSettings.ShowIADS); C(ref C4ISRSettings.ShowLink); C(ref C4ISRSettings.ShowSettings);
            C(ref TheatreWindow.Show); C(ref AirfieldsWindow.Show); C(ref ATCWindow.Show); C(ref AfterActionWindow.Show);
            C(ref ExplosionTestWindow.Show); C(ref AvionicsSettings.Profiler);
            return n;
        }

        void MenuWindow(int id)
        {
            if (GUI.Button(new Rect(menuRect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle))
            {
                showMenu = false;
                if (toolbarButton != null) toolbarButton.SetFalse(false);
                return;
            }
            btn = BDArmorySetup.ButtonStyle; sel = BDArmorySetup.SelectedButtonStyle;
            w = menuRect.width - 16; y = 24;

            // tab strip: two rows of three
            float tw = (w - 8) / 3;
            for (int i = 0; i < Tabs.Length; i++)
                if (GUI.Toggle(new Rect(8 + (i % 3) * (tw + 4), y + (i / 3) * RowH, tw, RowH - 2), tab == i, Tabs[i], tab == i ? sel : btn)) tab = i;
            y += 2 * RowH + Gap;

            switch (tab)
            {
                case 0: SensorsTab(); break;
                case 1: CommandTab(); break;
                case 2: AirOpsTab(); break;
                case 3: EffectsTab(); break;
                case 4: PhysicsTab(); break;
                default: SystemTab(); break;
            }

            // footer: always-visible status + window housekeeping
            y += Gap;
            int open = OpenWindows(false);
            if (GUI.Button(new Rect(8, y, w, RowH - 2), open > 0 ? $"Close all ELE windows ({open})" : "No ELE windows open", btn)) OpenWindows(true);
            y += RowH;
            Status();
            menuHeight = y + 6;   // window fits its tab (applied after GUI.Window returns)
            GUIUtils.DragWindow();
        }

        void SensorsTab()
        {
            Pair(ref AvionicsSettings.ShowRadar3D, "3D Radar", ref AvionicsSettings.ShowTargetList, "Target List");
            AvionicsSettings.ShowGPSManager = Row(AvionicsSettings.ShowGPSManager, "GPS Manager");
            AvionicsSettings.ShowFeeds = Row(AvionicsSettings.ShowFeeds, Ocisly.Available ? "Camera feeds (OCISLY)" : "Camera feeds (OCISLY not loaded)");
            AvionicsSettings.ThreatBanner = Row(AvionicsSettings.ThreatBanner, "Missile / lock warning banner");
            Note("FLIR and the pod HUD: bottom of the Target Camera window.");
        }

        void CommandTab()
        {
            Pair(ref C4ISRSettings.ShowConsole, "C2 Console", ref C4ISRSettings.ShowMapOverlay, "Map overlay");
            Pair(ref C4ISRSettings.ShowIADS, "IADS", ref C4ISRSettings.ShowLink, "Data link");
            Pair(ref C4ISRSettings.GlobalC2, "Global C2", ref C4ISRSettings.ShowSettings, "C4ISR settings");
            AvionicsSettings.ShowEW = Row(AvionicsSettings.ShowEW, $"Electronic warfare · stealth {StealthRCS.ModeNames[AvionicsSettings.StealthModel]}");
            Pair(ref AvionicsSettings.ShowLM, "Loitering munitions", ref TheatreWindow.Show, "Theatre");
            Header("Records");
            bool aar = AfterActionWindow.Show;
            Pair(ref aar, CombatLog.Live != null ? "AAR (live)" : "After-action rpt", ref AvionicsSettings.AARPopup, "AAR at battle end");
            AfterActionWindow.Show = aar;
            AvionicsSettings.CombatOperators = Row(AvionicsSettings.CombatOperators, "Combat operators (crew skill)");
        }

        void AirOpsTab()
        {
            Pair(ref AirfieldsWindow.Show, "Airfields", ref ATCWindow.Show, "ATC");
            Header("AI pilots");
            Pair(ref AvionicsSettings.TakeoffAssist, "Takeoff assist", ref AvionicsSettings.FormationAssist, "Formation assist");
            AvionicsSettings.AltitudeFromSeaLevel = Row(AvionicsSettings.AltitudeFromSeaLevel, AvionicsSettings.AltitudeFromSeaLevel ? "Altitudes: from sea level" : "Altitudes: from ground (stock)");
            AvionicsSettings.FCSAll = Row(AvionicsSettings.FCSAll, AvionicsSettings.FCSAll ? "ELE flight control: all AI aircraft" : "ELE flight control: per-craft only");
            AvionicsSettings.AirOpsTelemetry = Row(AvionicsSettings.AirOpsTelemetry, "AI telemetry log");
            Header("Ejection");
            AvionicsSettings.AutoEjectOnDestruction = Row(AvionicsSettings.AutoEjectOnDestruction, "Auto-eject if cockpit destroyed");
            Note($"Manual: press {AvionicsSettings.KeyEject} twice.");
        }

        void EffectsTab()
        {
            if (ToggleCycle(ref AvionicsSettings.Shrapnel, AvionicsSettings.Shrapnel ? $"Shrapnel ×{AvionicsSettings.ShrapnelRangeMult:0.#}" : "Shrapnel off", Shrapnel.Patterns[AvionicsSettings.ShrapnelPattern]))
                AvionicsSettings.ShrapnelPattern = (AvionicsSettings.ShrapnelPattern + 1) % Shrapnel.Patterns.Length;
            if (ToggleCycle(ref AvionicsSettings.ExplosionScale, AvionicsSettings.ExplosionScale ? "Sized fireballs" : "BDA fireballs", $"Blast FX: {ExplosionDirector.QualityNames[AvionicsSettings.ExplosionQuality]}", AvionicsSettings.ExplosionQuality > 0))
                AvionicsSettings.ExplosionQuality = (AvionicsSettings.ExplosionQuality + 1) % 4;
            bool vol = AvionicsSettings.Volumetric;
            Pair(ref vol, !VolumetricShaders.Loaded ? "No shaders" : vol ? "Volumetric FX" : "Volumetric off", ref AvionicsSettings.VaporCones, AvionicsSettings.VaporCones ? "Vapor cones" : "Cones off");
            AvionicsSettings.Volumetric = vol;
            if (ToggleCycle(ref AvionicsSettings.DynamicDamage, AvionicsSettings.DynamicDamage ? "Dents & bends" : "Damage FX off", $"Damage x{AvionicsSettings.DamageIntensity:0.#}"))
                AvionicsSettings.DamageIntensity = AvionicsSettings.DamageIntensity >= 2f ? 0.5f : AvionicsSettings.DamageIntensity + 0.5f;
            ExplosionTestWindow.Show = Row(ExplosionTestWindow.Show, "Explosion FX test panel");
        }

        void PhysicsTab()
        {
            if (ToggleCycle(ref AvionicsSettings.PhysicsLOD, AvionicsSettings.PhysicsLOD ? "Physics LOD" : "Physics LOD off", $"Full {AvionicsSettings.LODUnpackKm:F0} km"))
                AvionicsSettings.LODUnpackKm = AvionicsSettings.LODUnpackKm >= 25 ? 5 : AvionicsSettings.LODUnpackKm + 5;
            if (ToggleCycle(ref AvionicsSettings.LODMarkers, AvionicsSettings.LODMarkers ? "Far markers" : "Markers off", $"Loaded {AvionicsSettings.LODLoadKm:F0} km"))
                AvionicsSettings.LODLoadKm = AvionicsSettings.LODLoadKm >= 100 ? 15 : AvionicsSettings.LODLoadKm + 15;
            if (AvionicsSettings.PhysicsLOD) Note($"Craft: {PhysicsLOD.Full} full · {PhysicsLOD.Rigid} rigid · {PhysicsLOD.Ghost} ghost");
            Pair(ref AvionicsSettings.KrakenGuard, AvionicsSettings.KrakenGuard ? $"Kraken guard ({KrakenGuard.Fixes})" : "Kraken guard off", ref AvionicsSettings.Profiler, "Profiler");
            Header("Time");
            float h = hw2(w);
            if (GUI.Button(new Rect(8, y, h, RowH - 2), $"Slow-mo: {SlowMo.Names[SlowMo.Index]}", SlowMo.Index > 0 ? sel : btn)) SlowMo.Set((SlowMo.Index + 1) % SlowMo.Speeds.Length);
            if (GUI.Button(new Rect(12 + h, y, h, RowH - 2), SlowMo.Frozen ? "Unfreeze" : "Freeze", SlowMo.Frozen ? sel : btn)) SlowMo.ToggleFreeze();
            y += RowH;
            Note("Alt+, slower · Alt+. faster · Alt+/ freeze · Alt+; step");
        }

        void SystemTab()
        {
            if (GUI.Button(new Rect(8, y, w, RowH - 2), ELEBridge.Running ? $"ELE Bridge ON (127.0.0.1:{ELEBridge.Port})" : "ELE Bridge off", ELEBridge.Running ? sel : btn)) ELEBridge.Toggle();
            y += RowH;
            Note("Lets local scripts read and fly the craft. Alt+B.");
            Header("Keys");
            Note($"Targets: {AvionicsSettings.KeyPrevTarget} / {AvionicsSettings.KeyNextTarget} · lock {AvionicsSettings.KeyLockTarget}");
            Note($"Slew camera: {AvionicsSettings.KeySlewCamera} · camera reset: Ctrl+Shift+C");
            Note("Rebind: BDArmoryAvionics/PluginData/Avionics.cfg");
        }

        /// <summary>Two status lines shown under every tab.</summary>
        void Status()
        {
            var net = DataLinkNetwork.PlayerNetwork;
            var iads = net != null ? IADSController.For(net.team) : null;
            int engaged = 0;
            if (iads != null) foreach (var s in iads.sites.Values) if (s.assigned != null) engaged++;
            Note(net == null ? "Net: no weapon manager" : $"Net: {net.ConnectedCount}/{net.nodes.Count} linked, {net.tracks.Count} tracks");
            Note(iads == null ? "IADS: idle" : $"IADS: {iads.sites.Count} SAM sites, {engaged} engaging");
            if (iads != null && iads.log.Count > 0) Note(iads.log[0]);
        }
    }
}
