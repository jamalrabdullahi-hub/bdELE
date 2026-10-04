using UnityEngine;

using BDArmory.Extensions;
using BDArmory.FX;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Explosion FX test panel (ELE menu): spawns each explosion type where the camera looks (visual only, no damage):
    /// BDArmory's fireball (sized by warhead), ELE's type effects, volumetrics, shockwave, scorch marks and fires.
    /// Also a salvo for checking frame rate.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ExplosionTestWindow : MonoBehaviour
    {
        public static bool Show;
        const int WindowID = 71730365;
        Rect rect = new(Screen.width / 2 - 200, 120, 400, 330);
        float scale = 1f;
        float fps, fpsMin = 999, fpsT;

        static readonly (string name, ExplosionKind kind, float tnt)[] Tests =
        {
            ("Air burst (AIM-120)", ExplosionKind.AirBurst, 20f),
            ("Ground HE (250 kg bomb)", ExplosionKind.GroundHE, 250f),
            ("Water impact", ExplosionKind.Water, 100f),
            ("Fuel kill (wreck)", ExplosionKind.FuelKill, 50f),
            ("Thermobaric", ExplosionKind.Thermobaric, 200f),
            ("Shaped charge", ExplosionKind.ShapedCharge, 5f),
            ("Cannon shell", ExplosionKind.Small, 0.3f),
            ("Nuke-scale", ExplosionKind.Nuke, 8000f),
        };

        void Update()
        {
            if (!Show) return;
            fps = Mathf.Lerp(fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.1f);
            if (Time.unscaledTime > fpsT) { fpsT = Time.unscaledTime + 5f; fpsMin = 999; }
            fpsMin = Mathf.Min(fpsMin, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f));
        }

        void OnGUI()
        {
            if (!Show || !BDArmorySetup.GAME_UI_ENABLED) return;
            rect = AvionicsCore.DrawWindow(WindowID, rect, Draw, "Explosion FX test", BDArmorySetup.BDGuiSkin);
        }

        /// <summary>Where the camera looks: the ground it points at (within 3 km), else 400 m ahead in the air.</summary>
        static Vector3 Point(bool air)
        {
            var cam = FlightCamera.fetch.mainCamera;
            if (!air && Physics.Raycast(cam.transform.position, cam.transform.forward, out var hit, 3000f, (1 << 15) | (1 << 28)))
                return hit.point + hit.normal * 1f;
            return cam.transform.position + cam.transform.forward * 400f;
        }

        static void Blast(Vector3 pos, ExplosionKind kind, float tnt)
        {
            ExplosionDirector.ForceKind = kind;
            try
            {
                // BDArmory's own (visual-only) explosion: its fireball gets sized by ELE and ELE's layers ride along.
                ExplosionFx.CreateExplosion(pos, tnt, "BDArmory/Models/explosion/explosion", "BDArmory/Sounds/explode1", ExplosionSourceType.Other,
                    120f, null, null, null, "FX test", default, 100f, isfx: true);
            }
            finally { ExplosionDirector.ForceKind = null; }
            if (kind == ExplosionKind.Water) ExplosionDirector.Splash(pos, tnt * 20f);
        }

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; return; }
            var btn = BDArmorySetup.ButtonStyle; var small = AvionicsDraw.LabelSmall;
            float w = rect.width - 16, y = 24;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 28), "Point the camera at the ground (or the sky for air bursts) and click.\nVisual only: nothing is damaged.", small, AvionicsDraw.ScopeText); y += 32;
            for (int i = 0; i < Tests.Length; i++)
            {
                var (name, kind, tnt) = Tests[i];
                float bw = (w - 4) / 2;
                if (GUI.Button(new Rect(8 + (i % 2) * (bw + 4), y + (i / 2) * 24, bw, 22), $"{name}", btn))
                    Blast(Point(kind == ExplosionKind.AirBurst), kind, tnt * scale);
            }
            y += ((Tests.Length + 1) / 2) * 24 + 4;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), $"Warhead size x{scale:F1}", small, AvionicsDraw.HudText);
            scale = Mathf.Round(GUI.HorizontalSlider(new Rect(8, y + 15, w, 14), scale, 0.2f, 5f) * 10) / 10; y += 34;
            if (GUI.Button(new Rect(8, y, w / 2 - 2, 22), "Salvo: 8 ground hits", btn))
            {
                var p0 = Point(false); var up = (Vector3)FlightGlobals.getUpAxis(p0);
                for (int i = 0; i < 8; i++) { var off = Vector3.ProjectOnPlane(Random.insideUnitSphere, up).normalized * Random.Range(20f, 150f); Blast(p0 + off, ExplosionKind.GroundHE, 120f * scale); }
            }
            if (GUI.Button(new Rect(8 + w / 2 + 2, y, w / 2 - 2, 22), "Shockwave only", btn)) VolumetricFX.Shock(Point(true), 120f * scale);
            y += 28;
            Color fc = fps > 50 ? AvionicsDraw.Friendly : fps > 30 ? AvionicsDraw.Unknown : AvionicsDraw.Hostile;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), $"FPS {fps:F0} (lowest in 5 s: {fpsMin:F0})   Quality: {ExplosionDirector.QualityNames[AvionicsSettings.ExplosionQuality]}   Volumetric: {(VolumetricFX.Enabled ? "on" : "off")}", small, fc);
            GUI.DragWindow(new Rect(0, 0, rect.width - 22, 20));
        }
    }
}
