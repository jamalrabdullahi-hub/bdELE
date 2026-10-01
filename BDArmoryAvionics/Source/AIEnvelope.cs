using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

using BDArmory.Control;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Wider flight envelope for BDArmory's pilot AI.
    /// - Every pilot AI setting is shown in the part menu, including "Unclamp tuning" (normally hidden behind KSP's Advanced Tweakables).
    /// - With Unclamp tuning on, speed and altitude sliders go much further than stock (10 km/s, 400 km), for fast high-altitude cruisers.
    /// - Presets for the VAB/SPH window.
    /// </summary>
    public static class AIEnvelope
    {
        /// <summary>Maximums used when Unclamp tuning is on (stock unclamped values in comments).</summary>
        public static readonly Dictionary<string, float> ExtendedMax = new()
        {
            { nameof(BDModulePilotAI.defaultAltitude), 300000f },       // 100 km
            { nameof(BDModulePilotAI.minAltitude), 200000f },           // 100 km
            { nameof(BDModulePilotAI.maxAltitude), 400000f },           // 150 km
            { nameof(BDModulePilotAI.maxSpeed), 10000f },               // 3000 m/s
            { nameof(BDModulePilotAI.idleSpeed), 10000f },              // 3000 m/s
            { nameof(BDModulePilotAI.cornerSpeed), 10000f },            // 3000 m/s
            { nameof(BDModulePilotAI.lowSpeedSwitch), 10000f },         // 3000 m/s
            { nameof(BDModulePilotAI.strafingSpeed), 5000f },           // 2000 m/s
            { nameof(BDModulePilotAI.takeOffSpeed), 3000f },            // 2000 m/s
            { nameof(BDModulePilotAI.minSpeed), 3000f },                // 2000 m/s
            { nameof(BDModulePilotAI.altitudeSteerLimiterAltitude), 300000f },
            { nameof(BDModulePilotAI.extendDistanceAirToAir), 100000f }, // 20 km
        };

        public class Preset
        {
            public string name, description;
            public bool unclamp;
            public float defaultAlt, minAlt, maxAlt, idle, max, corner, maxG;
        }

        public static readonly Preset[] Presets =
        [
            new Preset { name = "Low-level strike", description = "Hugs the ground", unclamp = false, defaultAlt = 300, minAlt = 60, maxAlt = 2000, idle = 220, max = 320, corner = 180, maxG = 20 },
            new Preset { name = "Dogfighter", description = "BDArmory-style defaults", unclamp = false, defaultAlt = 2000, minAlt = 200, maxAlt = 7500, idle = 200, max = 350, corner = 200, maxG = 25 },
            new Preset { name = "High-alt cruiser", description = "15 km, Mach 2-3", unclamp = true, defaultAlt = 15000, minAlt = 4000, maxAlt = 25000, idle = 600, max = 1200, corner = 500, maxG = 15 },
            new Preset { name = "Hypersonic", description = "30 km, 1.5-2.8 km/s", unclamp = true, defaultAlt = 30000, minAlt = 10000, maxAlt = 45000, idle = 1500, max = 2800, corner = 1200, maxG = 12 },
        ];

        /// <summary>Apply a preset. Unclamp first (BDArmory re-applies the old values a frame after toggling), then the values.</summary>
        public static System.Collections.IEnumerator Apply(BDModulePilotAI ai, Preset p)
        {
            if (ai == null) yield break;
            if (ai.UpToEleven != p.unclamp)
            {
                ai.UpToEleven = p.unclamp;
                for (int i = 0; i < 3; i++) yield return new WaitForFixedUpdate();
                yield return null;
            }
            if (ai == null) yield break;
            ai.defaultAltitude = p.defaultAlt;
            ai.minAltitude = p.minAlt;
            ai.maxAltitude = p.maxAlt;
            ai.idleSpeed = p.idle;
            ai.maxSpeed = p.max;
            ai.cornerSpeed = p.corner;
            ai.maxAllowedGForce = p.maxG;
            Debug.Log($"[BDArmoryAvionics]: Applied AI preset '{p.name}' to {ai.part.partInfo.title}");
        }

        static void RaiseLimits(BDModulePilotAI ai)
        {
            if (ai == null || !ai.UpToEleven) return;
            foreach (var kv in ExtendedMax)
            {
                var f = ai.Fields[kv.Key];
                if (f == null) continue;
                foreach (var ctl in new[] { f.uiControlEditor, f.uiControlFlight })
                    if (ctl is UI_FloatRange r && r.maxValue < kv.Value) r.maxValue = kv.Value;
            }
        }

        [HarmonyPatch(typeof(BDModulePilotAI), "Start")] // the pilot AI's own Unity Start (OnStart is PartModule's, shared by every module)
        static class Patch_Start
        {
            static void Postfix(BDModulePilotAI __instance)
            {
                if (!AvionicsSettings.ShowAllAITweakables) return;
                foreach (BaseField f in __instance.Fields) f.advancedTweakable = false; // show Unclamp tuning and the rest
                foreach (BaseEvent e in __instance.Events) e.advancedTweakable = false;
                RaiseLimits(__instance);
            }
        }

        [HarmonyPatch(typeof(BDModulePilotAI), "TurnItUpToEleven")]
        static class Patch_TurnItUpToEleven
        {
            static void Postfix(BDModulePilotAI __instance) => RaiseLimits(__instance);
        }
    }
}
