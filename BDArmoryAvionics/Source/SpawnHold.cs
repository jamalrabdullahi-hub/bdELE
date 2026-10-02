using UnityEngine;

using BDArmory.Extensions;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Holds a freshly spawned AI aircraft level at cruise speed until BDArmory's pilot AI is actually flying it.
    /// The spawn sequence and AI activation take a couple of seconds; without this the craft falls into a dive.
    /// Keeps re-activating the pilot (and guard mode) meanwhile, then hands over and removes itself.
    /// </summary>
    public class SpawnHold : MonoBehaviour
    {
        Vessel v;
        Vector3 dir;
        float speed, t0, aiSince = -1, nextActivate;
        bool velocitySet;

        public static void Attach(Vessel v, Vector3 dir, float speed)
        {
            if (v == null || v.GetComponent<SpawnHold>() != null) return;
            var h = v.gameObject.AddComponent<SpawnHold>();
            h.v = v; h.dir = dir.normalized; h.speed = speed; h.t0 = Time.time;
            v.OnFlyByWire += h.Fly;
            Debug.Log($"[BDArmoryAvionics]: spawn hold on {v.GetName()}");
        }

        void FixedUpdate()
        {
            if (v == null || v.packed) return;
            if (!velocitySet && v.loaded) { v.ChangeWorldVelocity((Vector3d)(dir * speed) - v.srf_velocity); velocitySet = true; }
            var ai = v.ActiveController().PilotAI;
            var wm = v.ActiveController().WM;
            if (Time.time >= nextActivate)
            {
                nextActivate = Time.time + 0.5f;
                if (ai != null && !ai.pilotEnabled) ai.ActivatePilot();
                if (wm != null && !wm.guardMode) wm.ToggleGuardMode();
            }
            if (ai != null && ai.pilotEnabled) { if (aiSince < 0) aiSince = Time.time; }
            else aiSince = -1;
            // Hand over once the AI has been flying for 2 s (or give up after 30 s).
            if ((aiSince > 0 && Time.time - aiSince > 2f) || Time.time - t0 > 30f) Release();
        }

        void Release()
        {
            if (v != null) v.OnFlyByWire -= Fly;
            Debug.Log($"[BDArmoryAvionics]: spawn hold released on {v?.GetName()} after {Time.time - t0:F1} s (AI {(aiSince > 0 ? "flying" : "NOT active")})");
            Destroy(this);
        }

        void OnDestroy() { if (v != null) v.OnFlyByWire -= Fly; }

        /// <summary>Wings level, nose on the horizon (slight climb), full throttle. Added before the AI starts, so once the AI is active its own inputs take over.</summary>
        void Fly(FlightCtrlState s)
        {
            if (v == null || v.packed) return;
            var rt = v.ReferenceTransform;
            Vector3 up = (v.CoM - v.mainBody.position).normalized;
            Vector3 nose = rt.up, right = rt.right, top = -rt.forward;
            float bank = Mathf.Atan2(Vector3.Dot(up, right), Vector3.Dot(up, top));          // + = right wing up... roll right to level
            float pitch = Mathf.Asin(Mathf.Clamp(Vector3.Dot(nose, up), -1f, 1f));
            Vector3 w = v.angularVelocity;
            float pitchRate = -w.x, rollRate = -w.y, yawRate = -w.z;
            s.roll = Mathf.Clamp(1.5f * bank - 0.5f * rollRate, -1f, 1f);
            s.pitch = Mathf.Clamp(2f * (3f * Mathf.Deg2Rad - pitch) - 0.6f * pitchRate, -1f, 1f);
            s.yaw = Mathf.Clamp(-0.3f * yawRate, -1f, 1f);
            s.mainThrottle = 1f;
        }
    }
}
