using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// ELE slow motion for watching physics: 1x, 1/2, 1/4, 1/10, 1/25, plus freeze and single physics steps.
    /// The physics timestep shrinks with game speed (as KSP's own physics warp does the other way), so slow motion
    /// stays smooth (50 physics steps per real second) and each step is more precise, not less.
    ///   Alt+,  slower     Alt+.  faster     Alt+/  freeze / unfreeze     Alt+;  one physics step (while frozen)
    /// Turns itself off if you time-warp; respects KSP's own pause menu.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class SlowMo : MonoBehaviour
    {
        public static readonly float[] Speeds = { 1f, 0.5f, 0.25f, 0.1f, 0.04f };
        public static readonly string[] Names = { "1x", "1/2", "1/4", "1/10", "1/25" };
        public static int Index;            // into Speeds
        public static bool Frozen;
        static int steps;                   // physics steps left to run while frozen
        public static SlowMo Instance;

        public static float Factor => Speeds[Mathf.Clamp(Index, 0, Speeds.Length - 1)];
        public static bool Active => Index > 0 || Frozen;

        void Awake() { Instance = this; Index = 0; Frozen = false; }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (Active) { Index = 0; Frozen = false; Apply(1f); }
        }

        public static void Set(int index) { Index = Mathf.Clamp(index, 0, Speeds.Length - 1); if (Index == 0) Frozen = false; Message(); }
        public static void Slower() => Set(Index + 1);
        public static void Faster() => Set(Index - 1);
        public static void ToggleFreeze() { Frozen = !Frozen; Message(); }
        public static void Step() { if (!Frozen) Frozen = true; steps = 1; }

        static void Message() =>
            ScreenMessages.PostScreenMessage(Frozen ? "ELE slow-mo: FROZEN (Alt+; steps)" : $"ELE slow-mo: {Names[Index]}", 1.5f, ScreenMessageStyle.UPPER_CENTER);

        static bool owned; // ELE changed the clock and must give it back

        static void Apply(float scale)
        {
            owned = !Mathf.Approximately(scale, 1f);
            if (Mathf.Approximately(Time.timeScale, scale)) return;
            Time.timeScale = scale;
            if (scale > 0) Time.fixedDeltaTime = 0.02f * scale;
        }

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (GUIUtility.keyboardControl == 0 && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
            {
                if (Input.GetKeyDown(KeyCode.Comma)) Slower();
                if (Input.GetKeyDown(KeyCode.Period)) Faster();
                if (Input.GetKeyDown(KeyCode.Slash)) ToggleFreeze();
                if (Input.GetKeyDown(KeyCode.Semicolon)) Step();
            }
            if (FlightDriver.Pause) return;                                      // KSP's pause menu owns the clock
            if (TimeWarp.CurrentRateIndex > 0) { if (Active) { Index = 0; Frozen = false; } owned = false; return; } // warping: off (KSP owns the clock)
            if (!Active) { if (owned) Apply(1f); return; }
            if (Frozen) Apply(steps > 0 ? Factor : 0f);
            else Apply(Factor);
        }

        void FixedUpdate()
        {
            if (Frozen && steps > 0 && --steps == 0) Apply(0f); // ran the one step: freeze again
        }

        void OnGUI()
        {
            if (!Active || KillCam.Playing || !BDArmory.UI.BDArmorySetup.GAME_UI_ENABLED) return;
            var r = new Rect(Screen.width / 2f - 150, 80, 300, 24);
            var btn = BDArmory.UI.BDArmorySetup.ButtonStyle; var sel = BDArmory.UI.BDArmorySetup.SelectedButtonStyle;
            GUI.Box(r, "", BDArmory.UI.BDArmorySetup.BDGuiSkin.box);
            float w = (r.width - 8) / 7f, x = r.x + 4;
            for (int i = 0; i < Speeds.Length; i++) { if (GUI.Button(new Rect(x, r.y + 2, w - 2, 20), Names[i], i == Index && !Frozen ? sel : btn)) { Frozen = false; Set(i); } x += w; }
            if (GUI.Button(new Rect(x, r.y + 2, w - 2, 20), Frozen ? "▶" : "❚❚", Frozen ? sel : btn)) ToggleFreeze(); x += w;
            if (GUI.Button(new Rect(x, r.y + 2, w - 2, 20), "step", btn)) Step();
        }
    }
}
