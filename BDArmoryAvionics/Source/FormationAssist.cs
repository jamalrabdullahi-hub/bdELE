using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Formation keeping for BDArmory's pilot AI (Follow command / wing commander).
    ///
    /// Stock behaviour: the AI banks toward its fly-to point with only a small upward bias, so a formation slot a few degrees
    /// below the nose makes it roll inverted to "pull down" to it, then roll back: flipping and twirling. Its speed hold takes its
    /// derivative from frame-to-frame changes in slot distance, which bobs.
    ///
    /// With assist on, near the slot the follower flies like a wingman: it holds the leader's bank, adds a limited bank only to
    /// fix sideways error, and corrects height with pitch alone. Further out it may bank normally but never past 60° from upright.
    /// Speed = leader speed + slot error, damped by the actual closure rate. BDArmory's terrain avoidance still overrides all of it.
    /// </summary>
    public static class FormationAssist
    {
        public const float WingmanRange = 400f;  // within this of the slot: wingman roll law
        public const float AssistRange = 1500f;  // within this: no inverted pulls, smoothed speed

        static readonly AccessTools.FieldRef<BDModulePilotAI, Vector3> rollTarget = AccessTools.FieldRefAccess<BDModulePilotAI, Vector3>("rollTarget");
        static readonly AccessTools.FieldRef<BDModulePilotAI, Vector3> upDirection = AccessTools.FieldRefAccess<BDModulePilotAI, Vector3>("upDirection");
        static readonly AccessTools.FieldRef<BDModulePilotAI, float> maxSpeed = AccessTools.FieldRefAccess<BDModulePilotAI, float>("maxSpeed");
        static readonly MethodInfo getFormationPosition = AccessTools.Method(typeof(BDModulePilotAI), "GetFormationPosition");
        static readonly MethodInfo adjustThrottle = AccessTools.Method(typeof(BDModulePilotAI), "AdjustThrottle");
        static readonly Dictionary<BDModulePilotAI, float> smoothedSpeed = new();

        static bool InFormation(BDModulePilotAI ai, out Vector3 slot, out float dist)
        {
            slot = default; dist = float.MaxValue;
            if (!AvionicsSettings.FormationAssist || ai == null || ai.vessel == null) return false;
            if (ai.currentCommand != PilotCommands.Follow || ai.commandLeader == null || ai.commandLeader.vessel == null) return false;
            slot = (Vector3)(Vector3d)getFormationPosition.Invoke(ai, null);
            dist = (slot - (Vector3)ai.vessel.CoM).magnitude;
            return dist < AssistRange;
        }

        /// <summary>Called from inside FlyToPosition, right after the stock follow hints and before terrain-avoidance overrides.</summary>
        public static void AdjustRollTarget(BDModulePilotAI ai)
        {
            if (!InFormation(ai, out Vector3 slot, out float dist)) return;
            var leader = ai.commandLeader.vessel;
            Vector3 up = upDirection(ai);
            Vector3 leaderVel = leader.Velocity();
            Vector3 leaderFwd = leaderVel.sqrMagnitude > 1 ? leaderVel.normalized : leader.ReferenceTransform.up;
            Vector3 leaderUp = Vector3.ProjectOnPlane(-leader.ReferenceTransform.forward, leaderFwd).normalized;
            if (leaderUp.sqrMagnitude < 0.5f) leaderUp = Vector3.ProjectOnPlane(up, leaderFwd).normalized;

            if (dist < WingmanRange)
            {
                // Wingman: match the leader's bank, lean toward the slot by up to MaxBank to fix sideways error.
                Vector3 right = Vector3.Cross(leaderUp, leaderFwd).normalized;
                float lateralErr = Vector3.Dot(slot - (Vector3)ai.vessel.CoM, right);
                float lateralVel = Vector3.Dot((Vector3)ai.vessel.Velocity() - leaderVel, right);
                float bank = Mathf.Clamp(0.12f * lateralErr - 1.2f * lateralVel, -AvionicsSettings.FormationMaxBank, AvionicsSettings.FormationMaxBank);
                Vector3 desired = Vector3.RotateTowards(leaderUp, bank >= 0 ? right : -right, Mathf.Abs(bank) * Mathf.Deg2Rad, 0);
                // Blend in from the edge of the wingman zone so there's no step change.
                float w = Mathf.Clamp01((WingmanRange - dist) / (WingmanRange * 0.5f));
                Vector3 current = rollTarget(ai).sqrMagnitude > 1e-4f ? rollTarget(ai).normalized : desired;
                rollTarget(ai) = Vector3.Slerp(current, desired, w) * 100f;
            }
            else
            {
                // Rejoining: let the stock roll law turn us, but never further than 60° from upright (no inverted pulls).
                var rt = rollTarget(ai);
                if (rt.sqrMagnitude < 1e-4f) return;
                float ang = Vector3.Angle(rt, up);
                if (ang > 60f) rollTarget(ai) = Vector3.RotateTowards(rt, up * rt.magnitude, (ang - 60f) * Mathf.Deg2Rad, 0);
            }
        }

        /// <summary>After the stock follow logic: replace its speed target with a damped one.</summary>
        public static void AdjustSpeed(BDModulePilotAI ai)
        {
            if (!InFormation(ai, out Vector3 slot, out _)) { smoothedSpeed.Remove(ai); return; }
            var leader = ai.commandLeader.vessel;
            Vector3 leaderVel = leader.Velocity();
            float leaderSpeed = leaderVel.magnitude;
            if (leaderSpeed < 1) return;
            Vector3 leaderFwd = leaderVel / leaderSpeed;
            float alongErr = Vector3.Dot(slot - (Vector3)ai.vessel.CoM, leaderFwd);          // + = slot is ahead of us
            float closure = Vector3.Dot(leaderVel - (Vector3)ai.vessel.Velocity(), leaderFwd); // + = slot pulling away
            float target = leaderSpeed + Mathf.Clamp(0.15f * alongErr + 0.9f * closure, -60f, 60f);
            target = Mathf.Clamp(target, 0f, maxSpeed(ai));
            if (smoothedSpeed.TryGetValue(ai, out float prev)) target = Mathf.Lerp(prev, target, 0.15f);
            smoothedSpeed[ai] = target;
            adjustThrottle.Invoke(ai, new object[] { target, true, true, false, -1f });
        }
    }

    [HarmonyPatch(typeof(BDModulePilotAI), "UpdateFollowCommand")]
    static class Patch_PilotAI_UpdateFollowCommand
    {
        static void Postfix(BDModulePilotAI __instance) => FormationAssist.AdjustSpeed(__instance);
    }

    /// <summary>Insert FormationAssist.AdjustRollTarget(this) just before "if (invertRollTarget) rollTarget = -rollTarget;" (after the follow hints).</summary>
    [HarmonyPatch(typeof(BDModulePilotAI), "FlyToPosition")]
    static class Patch_PilotAI_FlyToPosition
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var followHints = AccessTools.Field(typeof(BDModulePilotAI), "useFollowHints");
            var invertRoll = AccessTools.Field(typeof(BDModulePilotAI), "invertRollTarget");
            var hook = AccessTools.Method(typeof(FormationAssist), nameof(FormationAssist.AdjustRollTarget));
            int afterHints = list.FindIndex(i => i.LoadsField(followHints));
            int at = afterHints < 0 ? -1 : list.FindIndex(afterHints, i => i.LoadsField(invertRoll));
            if (at < 1 || list[at - 1].opcode != OpCodes.Ldarg_0)
            {
                Debug.LogWarning("[BDArmoryAvionics]: Pilot AI roll logic not recognised; formation roll assist disabled (speed assist still on)");
                return list;
            }
            var call0 = new CodeInstruction(OpCodes.Ldarg_0);
            call0.MoveLabelsFrom(list[at - 1]); // jumps that landed on the stock check now run our hook first
            list.InsertRange(at - 1, new[] { call0, new CodeInstruction(OpCodes.Call, hook) });
            return list;
        }
    }
}
