using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Targeting;
using BDArmory.Utils;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Turret / gimbal control for Infernal Robotics Next servos, added by ModuleManager to every IR servo part.
    /// Put the servos of one turret (e.g. a rotator for yaw and a hinge for pitch) in the same turret group in the VAB/SPH,
    /// and mount a gun, missile launcher or camera at the tip. The group then points that tip at the target:
    /// the weapon manager's guard-mode target, the radar lock, the targeting camera's point, or the selected contact.
    /// In guard mode BDArmory itself fires the gun once it's on target.
    /// </summary>
    public class ModuleIRTurret : PartModule
    {
        public static readonly string[] Groups = ["Off", "1", "2", "3", "4"];
        public static readonly string[] Sources = ["Auto", "Guard target", "Radar lock", "Targeting camera", "Selected contact"];

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Turret group", groupName = "irturret", groupDisplayName = "BDA turret control", groupStartCollapsed = true),
         UI_ChooseOption(options = ["Off", "1", "2", "3", "4"], scene = UI_Scene.All)]
        public string turretGroup = "Off";

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Aim at", groupName = "irturret"),
         UI_ChooseOption(options = ["Auto", "Guard target", "Radar lock", "Targeting camera", "Selected contact"], scene = UI_Scene.All)]
        public string source = "Auto";

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Slew speed", guiUnits = " %", groupName = "irturret"),
         UI_FloatRange(minValue = 10f, maxValue = 100f, stepIncrement = 5f, scene = UI_Scene.All)]
        public float slewSpeed = 100f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Return to rest", groupName = "irturret"),
         UI_Toggle(enabledText = "Yes", disabledText = "No", scene = UI_Scene.All)]
        public bool returnToRest = true;

        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "Turret", groupName = "irturret")]
        public string status = "-";

        public bool Active => turretGroup != "Off";

        public override string GetInfo() =>
            "BDArmory turret control for this servo. Put a turret's servos in the same group and mount a gun, launcher or camera at the tip; " +
            "the group aims it at the guard target, radar lock, targeting camera point or selected contact.";

        public override string GetModuleDisplayName() => "BDA Turret Control";
    }

    /// <summary>Infernal Robotics Next servo access by reflection (no hard dependency on IR).</summary>
    public static class IRServo
    {
        static bool resolved;
        static Type servoType;
        static PropertyInfo position, minPos, maxPos, isRotational, isLocked, isFreeMoving, defaultSpeed, maxSpeed, defaultPosition;
        static MethodInfo moveTo, getAxis;

        public static bool Available { get { Resolve(); return servoType != null; } }

        static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            try
            {
                foreach (var a in AssemblyLoader.loadedAssemblies)
                {
                    if (a.name != "InfernalRoboticsNext") continue;
                    var iservo = a.assembly.GetType("InfernalRobotics_v3.Interfaces.IServo");
                    servoType = a.assembly.GetType("InfernalRobotics_v3.Module.ModuleIRServo_v3");
                    if (iservo == null || servoType == null) { servoType = null; break; }
                    position = iservo.GetProperty("Position");
                    minPos = iservo.GetProperty("MinPosition");
                    maxPos = iservo.GetProperty("MaxPosition");
                    isRotational = iservo.GetProperty("IsRotational");
                    isLocked = iservo.GetProperty("IsLocked");
                    isFreeMoving = iservo.GetProperty("IsFreeMoving");
                    defaultSpeed = iservo.GetProperty("DefaultSpeed");
                    maxSpeed = iservo.GetProperty("MaxSpeed");
                    defaultPosition = iservo.GetProperty("DefaultPosition");
                    moveTo = iservo.GetMethod("MoveTo", new[] { typeof(float), typeof(float) });
                    getAxis = servoType.GetMethod("GetAxis", Type.EmptyTypes);
                    break;
                }
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: Infernal Robotics lookup failed: {e.Message}"); servoType = null; }
            if (servoType != null) Debug.Log("[BDArmoryAvionics]: Infernal Robotics Next found, turret control available");
        }

        public static PartModule Find(Part p) => Available && p != null ? p.Modules.GetModule(servoType.Name) : null;

        public static float Position(PartModule s) => (float)position.GetValue(s);
        public static float Min(PartModule s) => (float)minPos.GetValue(s);
        public static float Max(PartModule s) => (float)maxPos.GetValue(s);
        public static bool Rotational(PartModule s) => (bool)isRotational.GetValue(s);
        public static bool Locked(PartModule s) => (bool)isLocked.GetValue(s);
        public static bool FreeMoving(PartModule s) => (bool)isFreeMoving.GetValue(s);
        public static float Speed(PartModule s) => Mathf.Max((float)defaultSpeed.GetValue(s), 1f);
        public static float MaxSpeed(PartModule s) => Mathf.Max((float)maxSpeed.GetValue(s), Speed(s));
        public static float Rest(PartModule s) => (float)defaultPosition.GetValue(s);
        public static void MoveTo(PartModule s, float pos, float speed) => moveTo.Invoke(s, new object[] { pos, speed });

        /// <summary>The servo's rotation axis in world space. Rotation about an axis leaves it unchanged, so either half of the servo gives the same answer.</summary>
        public static Vector3 WorldAxis(PartModule s)
        {
            var local = getAxis != null ? (Vector3)getAxis.Invoke(s, null) : Vector3.up;
            return s.part.transform.TransformDirection(local).normalized;
        }
    }

    /// <summary>Drives every IR turret group on every loaded craft.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class IRTurretController : MonoBehaviour
    {
        class Joint
        {
            public ModuleIRTurret module;
            public PartModule servo;
            public int depth;
            public float sign = 1;          // which way a positive position change turns the turret (learned in flight)
            public bool calibrated;
            public float lastPos, lastPhase;
            public bool haveLast;
            public int contrary;            // observations disagreeing with the learned sign
            public float lastCommand = float.NaN;
        }

        class Group
        {
            public Vessel vessel;
            public string id;
            public readonly List<Joint> joints = new();
            public Part tip;
            public ModuleWeapon weapon;
            public MissileBase missile;
            public Transform aimTransform;
            public bool alongForward; // guns and cameras look along transform.forward; plain parts along transform.up
        }

        readonly Dictionary<(Vessel, string), Group> groups = new();
        readonly Dictionary<Part, Joint> jointCache = new();
        float nextRebuild;
        const int GunTurretID = 97; // marks a gun as gimballed for BDArmory's guard-mode weapon choice

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready || !IRServo.Available) return;
            if (Time.time >= nextRebuild) { nextRebuild = Time.time + 2f; Rebuild(); }
            foreach (var g in groups.Values) Drive(g);
        }

        void Rebuild()
        {
            groups.Clear();
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed) continue;
                foreach (var p in v.parts)
                {
                    var m = p.FindModuleImplementing<ModuleIRTurret>();
                    if (m == null || !m.Active) continue;
                    var servo = IRServo.Find(p);
                    if (servo == null || !IRServo.Rotational(servo)) { m.status = "not a rotating servo"; continue; }
                    if (!jointCache.TryGetValue(p, out var j)) jointCache[p] = j = new Joint();
                    j.module = m; j.servo = servo; j.depth = Depth(p);
                    var key = (v, m.turretGroup);
                    if (!groups.TryGetValue(key, out var g)) groups[key] = g = new Group { vessel = v, id = m.turretGroup };
                    g.joints.Add(j);
                }
            }
            foreach (var g in groups.Values)
            {
                g.joints.Sort((a, b) => a.depth.CompareTo(b.depth)); // base first
                FindAimReference(g);
            }
        }

        static int Depth(Part p) { int d = 0; while (p.parent != null) { p = p.parent; d++; } return d; }

        /// <summary>The thing the turret points: a gun's barrel, a missile launcher, a camera, or else the tip part itself.</summary>
        static void FindAimReference(Group g)
        {
            var tipServo = g.joints[g.joints.Count - 1].servo.part;
            g.tip = tipServo;
            g.weapon = null;
            g.missile = null;
            g.aimTransform = null;
            g.alongForward = true;
            foreach (var p in Subtree(tipServo))
            {
                if (p == tipServo) continue;
                var w = p.FindModuleImplementing<ModuleWeapon>();
                if (w != null && w.fireTransforms != null && w.fireTransforms.Length > 0)
                {
                    g.weapon = w;
                    g.aimTransform = w.fireTransforms[0];
                    if (w.customTurretID <= 0) w.customTurretID = GunTurretID; // let guard mode pick it against aircraft from the ground
                    return;
                }
            }
            foreach (var p in Subtree(tipServo))
            {
                if (p == tipServo) continue;
                if (p.FindModuleImplementing<MissileBase>() is MissileBase mb && !mb.HasFired) { g.missile = mb; g.aimTransform = mb.part.transform; return; }
                if (p.FindModuleImplementing<ModuleTargetingCamera>() is ModuleTargetingCamera cam) { g.aimTransform = cam.cameraParentTransform != null ? cam.cameraParentTransform : cam.part.transform; return; }
            }
            g.aimTransform = tipServo.children.Count > 0 ? tipServo.children[0].transform : tipServo.transform;
            g.alongForward = false;
        }

        static IEnumerable<Part> Subtree(Part root)
        {
            var stack = new Stack<Part>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                yield return p;
                foreach (var c in p.children) stack.Push(c);
            }
        }

        /// <summary>Forward direction of the aim reference. Guns and launchers point along forward; plain parts along up.</summary>
        static Vector3 AimForward(Group g)
        {
            if (g.missile != null && !g.missile.HasFired) return g.missile.GetForwardTransform();
            return g.alongForward ? g.aimTransform.forward : g.aimTransform.up;
        }

        void Drive(Group g)
        {
            if (g.vessel == null || g.aimTransform == null || g.joints.Count == 0) return;
            var settings = g.joints[0].module;
            bool haveTarget = TryGetAimPoint(g, settings.source, out Vector3 aimPoint, out string what);

            if (!haveTarget)
            {
                foreach (var j in g.joints)
                {
                    j.module.status = $"G{g.id} idle";
                    if (!settings.returnToRest || IRServo.Locked(j.servo)) continue;
                    float rest = IRServo.Rest(j.servo);
                    if (float.IsNaN(j.lastCommand) || Mathf.Abs(rest - j.lastCommand) > 0.2f)
                    {
                        IRServo.MoveTo(j.servo, rest, IRServo.Speed(j.servo) * 0.5f);
                        j.lastCommand = rest;
                    }
                }
                return;
            }

            Vector3 origin = g.aimTransform.position;
            Vector3 d = (aimPoint - origin).normalized;
            Vector3 f = AimForward(g);
            float err = Vector3.Angle(f, d);

            // Cyclic coordinate descent: each joint (base first) turns to line the barrel up with the target around its own axis.
            foreach (var j in g.joints)
            {
                if (IRServo.Locked(j.servo) || IRServo.FreeMoving(j.servo)) continue;
                Vector3 axis = IRServo.WorldAxis(j.servo);
                Vector3 fp = Vector3.ProjectOnPlane(f, axis), dp = Vector3.ProjectOnPlane(d, axis);
                if (fp.sqrMagnitude < 1e-4f || dp.sqrMagnitude < 1e-4f) continue;
                float phi = Vector3.SignedAngle(fp, dp, axis); // degrees to turn about this axis
                float pos = IRServo.Position(j.servo);
                Calibrate(j, pos, f, axis);
                float target = pos + j.sign * phi;
                float min = IRServo.Min(j.servo), max = IRServo.Max(j.servo);
                if (max > min && max - min < 359f) target = Mathf.Clamp(target, min, max);
                if (float.IsNaN(j.lastCommand) || Mathf.Abs(target - j.lastCommand) > 0.2f)
                {
                    IRServo.MoveTo(j.servo, target, IRServo.MaxSpeed(j.servo) * settings.slewSpeed / 100f);
                    j.lastCommand = target;
                }
                j.module.status = $"G{g.id} {what} err {err:F1}°{(j.calibrated ? "" : " (learning)")}";
            }
        }

        /// <summary>
        /// Learn which way a positive servo position turns the barrel about the axis by watching it move.
        /// IR servos differ (inverted, mounted upside down, hinge vs rotor), so this avoids guessing.
        /// </summary>
        static void Calibrate(Joint j, float pos, Vector3 f, Vector3 axis)
        {
            // Phase of the barrel about the axis, measured against the servo's own fixed side.
            var parent = j.servo.part.parent != null ? j.servo.part.parent.transform : j.servo.part.transform;
            Vector3 refDir = Vector3.ProjectOnPlane(parent.forward, axis);
            if (refDir.sqrMagnitude < 1e-3f) refDir = Vector3.ProjectOnPlane(parent.right, axis);
            float phase = Vector3.SignedAngle(refDir, Vector3.ProjectOnPlane(f, axis), axis);
            if (j.haveLast)
            {
                float dPos = pos - j.lastPos, dPhase = Mathf.DeltaAngle(j.lastPhase, phase);
                if (Mathf.Abs(dPos) > 1.5f && Mathf.Abs(dPhase) > 0.75f)
                {
                    float s = Mathf.Sign(dPhase / dPos);
                    if (!j.calibrated) { j.sign = s; j.calibrated = true; j.contrary = 0; }
                    else if (s != j.sign) { if (++j.contrary >= 2) { j.sign = s; j.contrary = 0; } }
                    else j.contrary = 0;
                    j.lastPos = pos; j.lastPhase = phase;
                }
            }
            else { j.lastPos = pos; j.lastPhase = phase; j.haveLast = true; }
        }

        /// <summary>Where to point, by priority: guard target (with BDArmory's own lead when the gun is tracking it), radar lock, targeting camera, selected contact.</summary>
        static bool TryGetAimPoint(Group g, string source, out Vector3 point, out string what)
        {
            point = default; what = "";
            var v = g.vessel;
            var wm = v.ActiveController().WM;
            bool auto = source == "Auto";

            if ((auto || source == "Guard target") && wm != null && wm.guardMode && wm.currentTarget != null && wm.currentTarget.Vessel != null)
            {
                // BDArmory's finalAimTarget already has lead and bullet drop worked out for this gun.
                if (g.weapon != null && g.weapon.finalAimTarget != Vector3.zero && (g.weapon.finalAimTarget - (Vector3)wm.currentTarget.Vessel.CoM).sqrMagnitude < 2000f * 2000f)
                    point = g.weapon.finalAimTarget;
                else point = Lead(g, wm.currentTarget.Vessel.CoM, wm.currentTarget.Vessel.Velocity());
                what = "guard";
                return true;
            }
            if ((auto || source == "Radar lock") && wm != null && wm.vesselRadarData != null && wm.vesselRadarData.locked)
            {
                var t = wm.vesselRadarData.lockedTargetData.targetData;
                if (t.exists) { point = Lead(g, t.predictedPosition, t.velocity); what = "radar"; return true; }
            }
            if (auto || source == "Targeting camera")
            {
                foreach (var cam in VesselModuleRegistry.GetModules<ModuleTargetingCamera>(v))
                    if (cam != null && cam.cameraEnabled && cam.groundStabilized)
                    { point = cam.groundTargetPosition; what = "camera"; return true; }
            }
            if ((auto || source == "Selected contact") && v == FlightGlobals.ActiveVessel)
            {
                var c = AvionicsContacts.Selected;
                if (c != null) { point = c.vessel != null ? Lead(g, c.vessel.CoM, c.vessel.Velocity()) : c.position; what = "selected"; return true; }
            }
            return false;
        }

        /// <summary>Simple lead: where the target will be when a bullet (or the barrel's line, for cameras) gets there, plus bullet drop.</summary>
        static Vector3 Lead(Group g, Vector3 targetPos, Vector3 targetVel)
        {
            if (g.weapon == null) return targetPos;
            Vector3 origin = g.aimTransform.position;
            Vector3 relVel = targetVel - (Vector3)g.vessel.Velocity();
            float speed = Mathf.Max(g.weapon.bulletVelocity, 50f);
            float t = (targetPos - origin).magnitude / speed;
            for (int i = 0; i < 2; i++) t = (targetPos + relVel * t - origin).magnitude / speed;
            Vector3 aim = targetPos + relVel * t;
            float gAcc = (float)FlightGlobals.getGeeForceAtPosition(origin).magnitude;
            return aim + (Vector3)g.vessel.upAxis * 0.5f * gAcc * t * t;
        }
    }
}
