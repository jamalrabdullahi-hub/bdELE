using UnityEngine;
using BDArmory.Weapons;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Loader station: sets how fast the vessel's ELE tank guns reload.
    ///   Manual: a crewed loader. Fast while fresh, slows as the ready rack empties and the loader tires; needs crew aboard.
    ///   Auto:   a carousel autoloader. Steady cycle, no crew needed, but only carouselSize ready rounds; then it refills slowly.
    /// </summary>
    public class ModuleLoader : PartModule
    {
        [KSPField] public string loaderType = "Manual";   // Manual or Auto
        [KSPField] public float cycleTime = 6f;           // seconds per round at best
        [KSPField] public int carouselSize = 22;          // ready rounds (Auto) / ready rack (Manual)
        [KSPField] public float refillTime = 18f;         // seconds per round once the ready rounds are gone
        [KSPField] public float fatiguePerShot = 0.04f;   // Manual: +4% cycle time per shot in a row
        [KSPField] public float recoverTime = 30f;        // Manual: rest this long and fatigue is gone

        [KSPField(isPersistant = true)] public int ready = -1;
        [KSPField(guiActive = true, guiName = "Ready rounds")] public string readyInfo = "";
        [KSPField(guiActive = true, guiName = "Cycle")] public string cycleInfo = "";

        float fatigue, lastShot, refillT;

        public bool Manual => loaderType != "Auto";

        public override void OnStart(StartState state) { if (ready < 0) ready = carouselSize; }

        public override string GetInfo() => Manual
            ? $"Crewed loader: {cycleTime:F1} s per round, slowing {fatiguePerShot * 100:F0}% per shot in a row.\nReady rack {carouselSize} rounds, then {refillTime:F0} s per round. Needs crew aboard."
            : $"Autoloader: steady {cycleTime:F1} s per round, no crew needed.\nCarousel {carouselSize} rounds, then {refillTime:F0} s per round while it refills.";

        /// <summary>Seconds this loader needs for the next round.</summary>
        public float NextCycle()
        {
            if (Manual && vessel.GetCrewCount() == 0) return -1; // nobody to load
            if (ready <= 0) return refillTime;
            return Manual ? cycleTime * (1 + fatigue) : cycleTime;
        }

        public void OnFired() { if (ready > 0) ready--; if (Manual) fatigue += fatiguePerShot; lastShot = Time.time; }

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            float dt = TimeWarp.fixedDeltaTime;
            if (Manual) fatigue = Mathf.Max(0, fatigue - dt / recoverTime * Mathf.Max(fatigue, 0.2f) * (Time.time - lastShot > 5 ? 1 : 0));
            // Restock the ready rounds between engagements (from the vessel's main ammo stowage).
            if (ready < carouselSize && Time.time - lastShot > 4) { refillT += dt; if (refillT >= refillTime) { refillT = 0; ready++; } } else refillT = 0;
            readyInfo = $"{ready}/{carouselSize}";
            float c = NextCycle(); cycleInfo = c < 0 ? "no crew" : $"{c:F1} s";
        }
    }

    /// <summary>
    /// Tank gun: reload time comes from the best loader on the vessel (or a slow gunner reload with none).
    /// Writes the result into the BDArmory weapon's rate of fire, never above the gun's own maximum.
    /// </summary>
    public class ModuleTankGun : PartModule
    {
        [KSPField] public float noLoaderCycle = 15f;      // the gunner loads it himself
        [KSPField(guiActive = true, guiName = "Loader")] public string loaderInfo = "none";

        ModuleWeapon weapon; float lastFired;

        public override void OnStart(StartState state)
        {
            weapon = part.FindModuleImplementing<ModuleWeapon>();
        }

        ModuleLoader Best(out float cycle)
        {
            ModuleLoader best = null; cycle = noLoaderCycle;
            foreach (var l in vessel.FindPartModulesImplementing<ModuleLoader>())
            {
                float c = l.NextCycle();
                if (c > 0 && c < cycle) { cycle = c; best = l; }
            }
            return best;
        }

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || weapon == null) return;
            var l = Best(out float cycle);
            if (weapon.timeFired > lastFired + 0.01f) { lastFired = weapon.timeFired; l?.OnFired(); l = Best(out cycle); }
            weapon.roundsPerMinute = Mathf.Min(60f / cycle, weapon.baseRPM);
            loaderInfo = l == null ? $"gunner, {cycle:F0} s" : $"{(l.Manual ? "crew" : "auto")}, {cycle:F1} s";
        }
    }
}
