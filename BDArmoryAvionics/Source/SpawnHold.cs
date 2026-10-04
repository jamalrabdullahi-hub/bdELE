using HarmonyLib;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Holds a freshly spawned AI aircraft level at cruise speed until BDArmory's pilot AI is really flying it:
    /// engines lit and producing thrust, gear up, pilot AI and guard mode on, level or climbing. After the handover it keeps
    /// watching for 30 s; if the craft dives toward the ground it catches it again and logs what the AI, engines and
    /// throttle were doing (so the cause shows in KSP.log).
    /// </summary>
    public class SpawnHold : MonoBehaviour
    {
        static readonly System.Reflection.FieldInfo statusField = AccessTools.Field(typeof(BDGenericAIBase), "currentStatus");
        Vessel v;
        Vector3 dir;
        float speed, t0, aiSince = -1, nextActivate, releasedAt = -1, nextLog, holdAlt;
        bool velocitySet, holding = true;
        public bool armed = true; // false while BDArmory is still placing the craft (teleports / re-rotations)
        int catches;

        public static SpawnHold Attach(Vessel v, Vector3 dir, float speed, bool armed = true)
        {
            if (v == null) return null;
            if (v.GetComponent<SpawnHold>() is SpawnHold existing) return existing;
            var h = v.gameObject.AddComponent<SpawnHold>();
            h.v = v; h.dir = dir.normalized; h.speed = speed; h.t0 = Time.time; h.holdAlt = (float)v.altitude;
            h.armed = armed;
            v.OnFlyByWire += h.Fly;
            Debug.Log($"[BDArmoryAvionics]: spawn hold on {v.GetName()}{(armed ? "" : " (waiting for the spawner)")}");
            return h;
        }

        void FixedUpdate()
        {
            if (v == null || v.packed || !v.loaded) return;
            if (!armed) { v.IgnoreGForces(2); t0 = Time.time; return; } // don't fight the spawner: just keep G-forces from breaking it
            if (!velocitySet)
            {
                // speed along the craft's real nose (level), so it doesn't arrive flying sideways after the spawner re-rotated it
                Vector3 upv = (v.CoM - v.mainBody.position).normalized;
                Vector3 nose = Vector3.ProjectOnPlane(v.ReferenceTransform.up, upv);
                if (nose.sqrMagnitude < 0.01f) nose = dir;
                v.ChangeWorldVelocity((Vector3d)(nose.normalized * speed) - v.srf_velocity);
                v.IgnoreGForces(10);
                holdAlt = (float)v.altitude; velocitySet = true;
            }
            var ai = v.ActiveController().PilotAI;
            var wm = v.ActiveController().WM;

            if (holding)
            {
                if (Time.time >= nextActivate)
                {
                    nextActivate = Time.time + 0.5f;
                    if (ai != null && !ai.pilotEnabled) ai.ActivatePilot();
                    if (wm != null && !wm.guardMode) wm.ToggleGuardMode();
                    EnsureEngines();
                    if (!v.LandedOrSplashed && v.radarAltitude > 50) v.ActionGroups.SetGroup(KSPActionGroup.Gear, false);
                }
                if (ai != null && ai.pilotEnabled && Thrust() > 0) { if (aiSince < 0) aiSince = Time.time; }
                else aiSince = -1;
                bool steady = v.verticalSpeed > -15;
                if ((aiSince > 0 && Time.time - aiSince > 2f && steady) || Time.time - t0 > 30f) Release(ai, wm);
                return;
            }

            // Watch after the handover.
            float since = Time.time - releasedAt;
            if (since > 30f) { Destroy(this); return; }
            if (Time.time >= nextLog) { nextLog = Time.time + 2f; Log("after handover", ai, wm); }
            bool diving = v.verticalSpeed < -40 && v.radarAltitude < 2500 && !v.LandedOrSplashed;
            if (diving && catches < 3)
            {
                catches++;
                Log("DIVING - caught again", ai, wm);
                holding = true; aiSince = -1; t0 = Time.time; holdAlt = Mathf.Max((float)v.altitude, holdAlt);
                v.OnFlyByWire -= Fly; v.OnFlyByWire += Fly; // after the AI, so the hold wins
            }
        }

        void EnsureEngines()
        {
            int active = 0;
            foreach (var e in VesselModuleRegistry.GetModuleEngines(v)) if (e != null && e.EngineIgnited) active++;
            if (active > 0) return;
            foreach (var e in VesselModuleRegistry.GetModuleEngines(v)) if (e != null && !e.EngineIgnited && !e.part.FindModuleImplementing<Weapons.Missiles.MissileBase>()) e.Activate();
            Debug.Log($"[BDArmoryAvionics]: {v.GetName()}: no engines were running after spawn - activated them");
        }

        float Thrust() { float t = 0; foreach (var e in VesselModuleRegistry.GetModuleEngines(v)) if (e != null && e.EngineIgnited) t += e.finalThrust; return t; }

        void Release(BDModulePilotAI ai, MissileFire wm)
        {
            holding = false; releasedAt = Time.time; nextLog = Time.time;
            if (v != null) v.OnFlyByWire -= Fly;
            Log($"released after {Time.time - t0:F1} s", ai, wm);
        }

        void Log(string what, BDModulePilotAI ai, MissileFire wm)
        {
            int engines = 0; foreach (var e in VesselModuleRegistry.GetModuleEngines(v)) if (e != null && e.EngineIgnited) engines++;
            string status = ai != null && statusField != null ? statusField.GetValue(ai) as string : "-";
            Debug.Log($"[BDArmoryAvionics]: spawn {v.GetName()} {what}: alt {v.altitude:F0} m AGL {v.radarAltitude:F0}, V/S {v.verticalSpeed:F0}, speed {v.srfSpeed:F0}, " +
                      $"throttle {v.ctrlState.mainThrottle:F2}, engines {engines} thrust {Thrust():F0} kN, gear {(v.ActionGroups[KSPActionGroup.Gear] ? "down" : "up")}, " +
                      $"AI {(ai != null && ai.pilotEnabled ? "on" : "OFF")} cmd {ai?.currentCommand} status '{status}', guard {(wm != null && wm.guardMode)}, active vessel {v == FlightGlobals.ActiveVessel}");
        }

        void OnDestroy() { if (v != null) v.OnFlyByWire -= Fly; }

        /// <summary>Wings level, climb back to the spawn altitude (gently), full throttle.</summary>
        void Fly(FlightCtrlState s)
        {
            if (!holding || !armed || v == null || v.packed) return;
            var rt = v.ReferenceTransform;
            Vector3 up = (v.CoM - v.mainBody.position).normalized;
            Vector3 nose = rt.up, right = rt.right, top = -rt.forward;
            float bank = Mathf.Atan2(Vector3.Dot(up, right), Vector3.Dot(up, top));
            float pitch = Mathf.Asin(Mathf.Clamp(Vector3.Dot(nose, up), -1f, 1f));
            float wantPitch = Mathf.Clamp((holdAlt - (float)v.altitude) / 400f, 0.03f, 0.25f); // 2-14 degrees nose up
            Vector3 w = v.angularVelocity;
            s.roll = Mathf.Clamp(1.5f * bank - 0.5f * -w.y, -1f, 1f);
            s.pitch = Mathf.Clamp(2f * (wantPitch - pitch) - 0.6f * -w.x, -1f, 1f);
            s.yaw = Mathf.Clamp(-0.3f * -w.z, -1f, 1f);
            s.mainThrottle = 1f;
        }
    }
}
