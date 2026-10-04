using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Targeting;
using BDArmory.Utils;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Turret control for Infernal Robotics Next servos, added by ModuleManager to every IR servo part.
    /// No setup: any rotating servos between the craft and a gun, missile launcher, targeting camera or HullCam form a turret
    /// automatically, and point it at (in order) the EO/IR camera track, the guard-mode target, the radar lock, the targeting
    /// camera's point, or the selected contact. Switch "BDA aiming" off on a servo to keep it out of turrets.
    /// </summary>
    public class ModuleIRTurret : PartModule
    {
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "BDA aiming", groupName = "irturret", groupDisplayName = "BDA turret", groupStartCollapsed = true),
         UI_Toggle(enabledText = "On", disabledText = "Off", scene = UI_Scene.All)]
        public bool aiming = true;

        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "Turret", groupName = "irturret")]
        public string status = "-";

        [KSPEvent(guiActive = true, guiName = "Show aim lines", groupName = "irturret")]
        public void ToggleAimLines() { IRTurretController.ShowAimLines = !IRTurretController.ShowAimLines; Events[nameof(ToggleAimLines)].guiName = IRTurretController.ShowAimLines ? "Hide aim lines" : "Show aim lines"; }

        public override string GetInfo() =>
            "BDArmory turret control. Servos between the craft and a gun, launcher or camera aim it automatically at the camera track, guard target, radar lock or selected contact.";

        public override string GetModuleDisplayName() => "BDA Turret";
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

        /// <summary>The servo's rotation axis in world space.</summary>
        public static Vector3 WorldAxis(PartModule s)
        {
            var local = getAxis != null ? (Vector3)getAxis.Invoke(s, null) : Vector3.up;
            return s.part.transform.TransformDirection(local).normalized;
        }
    }

    /// <summary>Finds and drives every IR turret on every loaded craft.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class IRTurretController : MonoBehaviour
    {
        public static bool ShowAimLines;

        /// <summary>One servo. Its direction (does +position turn the barrel + or - about the axis?) is found once with a small test move.</summary>
        class Joint
        {
            public ModuleIRTurret module;
            public PartModule servo;
            public float sign = 1;
            public bool calibrated, probing;
            public float probePos0, probePhase0, probeT0;
            public float lastCommand = float.NaN, lastCommandT;
        }

        class Turret
        {
            public Vessel vessel;
            public readonly List<Joint> joints = new(); // base first
            public Part aimPart;
            public ModuleWeapon weapon;
            public MissileBase missile;
            public PartModule hullcam;
            public ModuleTargetingCamera tgp;
            public string Kind => weapon != null ? "gun" : missile != null ? "launcher" : hullcam != null ? "camera" : "TGP";
        }

        readonly List<Turret> turrets = new();
        readonly Dictionary<Part, Joint> jointCache = new();
        float nextRebuild;
        const int GunTurretID = 97; // marks a gun as gimballed for BDArmory's guard-mode weapon choice

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready || !IRServo.Available) return;
            if (Time.time >= nextRebuild) { nextRebuild = Time.time + 2f; Rebuild(); }
            foreach (var t in turrets) Drive(t);
        }

        // ---- Finding turrets: walk up from each aimable part to the root, collecting enabled rotating servos ----

        void Rebuild()
        {
            turrets.Clear();
            var used = new HashSet<string>();
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed) continue;
                // Guns first, then launchers, then cameras: if a turret carries several, the gun aims it.
                var candidates = new List<(Part p, int prio)>();
                foreach (var p in v.parts)
                {
                    if (p.FindModuleImplementing<ModuleWeapon>() is ModuleWeapon w && w.fireTransforms != null && w.fireTransforms.Length > 0) candidates.Add((p, 0));
                    else if (p.FindModuleImplementing<MissileBase>() is MissileBase mb && !mb.HasFired) candidates.Add((p, 1));
                    else if (HullCam.Find(p) != null || p.FindModuleImplementing<ModuleTargetingCamera>() != null) candidates.Add((p, 2));
                }
                candidates.Sort((a, b) => a.prio.CompareTo(b.prio));
                foreach (var (p, _) in candidates)
                {
                    var chain = new List<Joint>();
                    for (var q = p.parent; q != null; q = q.parent)
                    {
                        var m = q.FindModuleImplementing<ModuleIRTurret>();
                        if (m == null || !m.aiming) continue;
                        var servo = IRServo.Find(q);
                        if (servo == null || !IRServo.Rotational(servo)) continue;
                        if (!jointCache.TryGetValue(q, out var j)) jointCache[q] = j = new Joint();
                        j.module = m; j.servo = servo;
                        chain.Insert(0, j);
                    }
                    if (chain.Count == 0) continue;
                    string key = string.Join(",", chain.ConvertAll(j => j.servo.part.persistentId.ToString()));
                    if (!used.Add(key)) continue; // this turret already has an aim part
                    var t = new Turret { vessel = v, aimPart = p };
                    t.joints.AddRange(chain);
                    t.weapon = p.FindModuleImplementing<ModuleWeapon>();
                    if (t.weapon != null && t.weapon.customTurretID <= 0) t.weapon.customTurretID = GunTurretID;
                    t.missile = t.weapon == null ? p.FindModuleImplementing<MissileBase>() : null;
                    t.hullcam = t.weapon == null && t.missile == null ? HullCam.Find(p) : null;
                    t.tgp = t.weapon == null && t.missile == null && t.hullcam == null ? p.FindModuleImplementing<ModuleTargetingCamera>() : null;
                    turrets.Add(t);
                }
            }
        }

        static void AimRay(Turret t, out Vector3 origin, out Vector3 fwd)
        {
            if (t.weapon != null) { var ft = t.weapon.fireTransforms[0]; origin = ft.position; fwd = ft.forward; return; }
            if (t.missile is MissileLauncher ml && ml.MissileReferenceTransform != null) { origin = ml.MissileReferenceTransform.position; fwd = ml.MissileReferenceTransform.forward; return; }
            if (t.missile != null) { origin = t.missile.transform.position; fwd = t.missile.GetForwardTransform(); return; }
            if (t.hullcam != null) { HullCam.Ray(t.hullcam, out origin, out fwd); return; }
            var tr = t.tgp.cameraParentTransform != null ? t.tgp.cameraParentTransform : t.tgp.part.transform;
            origin = tr.position; fwd = tr.forward;
        }

        // ---- Driving ----

        void Drive(Turret t)
        {
            if (t.vessel == null || t.aimPart == null || t.aimPart.vessel != t.vessel || (t.missile != null && t.missile.HasFired)) { nextRebuild = 0; return; }
            if (!AimPoint(t, out Vector3 aim, out string what))
            {
                foreach (var j in t.joints) { j.module.status = $"{t.Kind}: no target"; Command(j, IRServo.Rest(j.servo), IRServo.Speed(j.servo) * 0.5f); }
                lines.Remove(t);
                return;
            }
            AimRay(t, out Vector3 origin, out Vector3 f);
            Vector3 d = (aim - origin).normalized;
            float err = Vector3.Angle(f, d);
            if (ShowAimLines) lines[t] = (origin, f, aim); else lines.Clear();

            foreach (var j in t.joints)
            {
                if (IRServo.Locked(j.servo) || IRServo.FreeMoving(j.servo)) { j.module.status = "servo locked"; continue; }
                Vector3 axis = IRServo.WorldAxis(j.servo);
                float pos = IRServo.Position(j.servo);
                float phase = Phase(j, f, axis);
                if (!j.calibrated) { Probe(j, pos, phase); j.module.status = $"{t.Kind}: checking servo direction"; continue; }

                Vector3 fp = Vector3.ProjectOnPlane(f, axis), dp = Vector3.ProjectOnPlane(d, axis);
                if (fp.sqrMagnitude < 1e-4f || dp.sqrMagnitude < 1e-4f) continue;
                float phi = Vector3.SignedAngle(fp, dp, axis); // degrees to turn about this axis
                if (Mathf.Abs(phi) < 0.15f) { j.module.status = $"{t.Kind} on {what}, err {err:F1}°"; continue; }
                float target = pos + j.sign * phi;
                float min = IRServo.Min(j.servo), max = IRServo.Max(j.servo);
                if (max > min && max - min < 359f) target = Mathf.Clamp(target, min, max);
                // Slow down near the target so the servo stops on it instead of overshooting and hunting.
                float speed = IRServo.MaxSpeed(j.servo) * Mathf.Clamp(Mathf.Abs(phi) / 20f, 0.08f, 1f);
                Command(j, target, speed);
                j.module.status = $"{t.Kind} on {what}, err {err:F1}°";
            }
        }

        /// <summary>Send a move only when the goal has really changed (or every 0.25 s), not every physics frame.</summary>
        static void Command(Joint j, float target, float speed)
        {
            if (!float.IsNaN(j.lastCommand) && Mathf.Abs(target - j.lastCommand) < 0.3f && Time.time - j.lastCommandT < 0.25f) return;
            if (!float.IsNaN(j.lastCommand) && Mathf.Abs(target - j.lastCommand) < 0.05f) return;
            IRServo.MoveTo(j.servo, target, speed);
            j.lastCommand = target; j.lastCommandT = Time.time;
        }

        /// <summary>Barrel angle about the servo axis, measured against the servo's own part.</summary>
        static float Phase(Joint j, Vector3 f, Vector3 axis)
        {
            var refT = j.servo.part.parent != null ? j.servo.part.parent.transform : j.servo.part.transform;
            Vector3 refDir = Vector3.ProjectOnPlane(refT.forward, axis);
            if (refDir.sqrMagnitude < 1e-3f) refDir = Vector3.ProjectOnPlane(refT.right, axis);
            return Vector3.SignedAngle(refDir, Vector3.ProjectOnPlane(f, axis), axis);
        }

        /// <summary>Move the servo 6° and see which way the barrel turned. Done once per servo; takes about a second.</summary>
        static void Probe(Joint j, float pos, float phase)
        {
            if (!j.probing)
            {
                j.probing = true; j.probePos0 = pos; j.probePhase0 = phase; j.probeT0 = Time.time;
                float max = IRServo.Max(j.servo), min = IRServo.Min(j.servo);
                float step = (max > min && pos + 6f > max) ? -6f : 6f;
                IRServo.MoveTo(j.servo, pos + step, IRServo.Speed(j.servo));
                j.lastCommand = float.NaN;
                return;
            }
            float dPos = pos - j.probePos0, dPhase = Mathf.DeltaAngle(j.probePhase0, phase);
            if (Mathf.Abs(dPos) > 2f && Mathf.Abs(dPhase) > 1f)
            {
                j.sign = Mathf.Sign(dPhase / dPos); j.calibrated = true; j.probing = false;
                Debug.Log($"[BDArmoryAvionics]: Turret servo {j.servo.part.partInfo.title}: +position turns the barrel {(j.sign > 0 ? "positive" : "negative")}");
            }
            else if (Time.time - j.probeT0 > 3f)
            {
                j.sign = 1; j.calibrated = true; j.probing = false; // didn't see it move (barrel along the axis?): assume the usual way
                Debug.Log($"[BDArmoryAvionics]: Turret servo {j.servo.part.partInfo.title}: direction test inconclusive, assuming +");
            }
        }

        /// <summary>Where to point: EO/IR camera track, guard target (with BDArmory's own lead), radar lock, targeting camera point, selected contact.</summary>
        static bool AimPoint(Turret t, out Vector3 point, out string what)
        {
            point = default; what = "";
            var v = t.vessel;
            var wm = v.ActiveController().WM;
            if (v == FlightGlobals.ActiveVessel && EOIRView.TryTrack(out var trackPos, out var trackVel))
            { point = t.hullcam != null ? trackPos : Lead(t, trackPos, trackVel); what = "track"; return true; }
            if (wm != null && wm.guardMode && wm.currentTarget != null && wm.currentTarget.Vessel != null)
            {
                var tv = wm.currentTarget.Vessel;
                point = t.weapon != null && t.weapon.finalAimTarget != Vector3.zero && (t.weapon.finalAimTarget - (Vector3)tv.CoM).sqrMagnitude < 2000f * 2000f
                    ? t.weapon.finalAimTarget : Lead(t, tv.CoM, tv.Velocity());
                what = "guard target"; return true;
            }
            if (wm != null && wm.vesselRadarData != null && wm.vesselRadarData.locked)
            {
                var td = wm.vesselRadarData.lockedTargetData.targetData;
                if (td.exists) { point = Lead(t, td.predictedPosition, td.velocity); what = "radar lock"; return true; }
            }
            foreach (var cam in VesselModuleRegistry.GetModules<ModuleTargetingCamera>(v))
                if (cam != null && cam != t.tgp && cam.cameraEnabled && cam.groundStabilized) { point = cam.groundTargetPosition; what = "TGP point"; return true; }
            if (v == FlightGlobals.ActiveVessel && AvionicsContacts.Selected is Contact c)
            { point = c.vessel != null ? Lead(t, c.vessel.CoM, c.vessel.Velocity()) : c.position; what = "selected"; return true; }
            return false;
        }

        /// <summary>Lead for guns: where the target will be when the bullet arrives, plus drop.</summary>
        static Vector3 Lead(Turret t, Vector3 targetPos, Vector3 targetVel)
        {
            if (t.weapon == null) return targetPos;
            Vector3 origin = t.weapon.fireTransforms[0].position;
            Vector3 relVel = targetVel - (Vector3)t.vessel.Velocity();
            float speed = Mathf.Max(t.weapon.bulletVelocity, 50f);
            float tof = (targetPos - origin).magnitude / speed;
            for (int i = 0; i < 2; i++) tof = (targetPos + relVel * tof - origin).magnitude / speed;
            float g = (float)FlightGlobals.getGeeForceAtPosition(origin).magnitude;
            return targetPos + relVel * tof + (Vector3)t.vessel.upAxis * 0.5f * g * tof * tof;
        }

        // ---- Aim lines: cyan = where it points, red = where it should ----
        readonly Dictionary<Turret, (Vector3 o, Vector3 f, Vector3 aim)> lines = new();

        void OnGUI()
        {
            if (!ShowAimLines || MapView.MapIsEnabled || Event.current.type != EventType.Repaint || lines.Count == 0) return;
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null || !AvionicsDraw.Begin()) return;
            bool S(Vector3 w, out Vector2 p) { var s = cam.WorldToScreenPoint(w); p = new Vector2(s.x, Screen.height - s.y); return s.z > 0; }
            foreach (var (o, f, aim) in lines.Values)
            {
                if (S(o, out var po) && S(o + f * 300f, out var pf)) AvionicsDraw.Line(po, pf, 2, Color.cyan);
                if (S(o, out po) && S(aim, out var pa)) AvionicsDraw.Line(po, pa, 2, Color.red);
            }
            AvionicsDraw.End();
        }
    }
}
