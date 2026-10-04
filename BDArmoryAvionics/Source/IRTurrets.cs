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

        // Logic settings, edited in the SPH/VAB turret tool. Per servo:
        [KSPField(isPersistant = true)] public int direction = 0;            // 0 auto (probe in flight), 1 = +, -1 = -
        // Per turret (kept on its base servo):
        [KSPField(isPersistant = true)] public string priority = TurretLogic.DefaultPriority; // order; "-" prefix = disabled
        [KSPField(isPersistant = true)] public string aimWith = "Auto";       // Auto, Gun, Launcher, Camera
        [KSPField(isPersistant = true)] public float slewLimit = 100f;        // % of servo max speed
        [KSPField(isPersistant = true)] public float slowAngle = 20f;         // start slowing this many degrees out
        [KSPField(isPersistant = true)] public float deadband = 0.15f;        // degrees
        [KSPField(isPersistant = true)] public bool restWhenIdle = true;
        [KSPField(isPersistant = true)] public float maxRangeKm = 0f;         // 0 = no limit

        [KSPEvent(guiActive = true, guiName = "Show aim lines", groupName = "irturret")]
        public void ToggleAimLines() { IRTurretController.ShowAimLines = !IRTurretController.ShowAimLines; Events[nameof(ToggleAimLines)].guiName = IRTurretController.ShowAimLines ? "Hide aim lines" : "Show aim lines"; }

        public override string GetInfo() =>
            "BDArmory turret control. Servos between the craft and a gun, launcher or camera aim it automatically. Tune it in the SPH/VAB with ELE's turret tool.";

        public override string GetModuleDisplayName() => "BDA Turret";
    }

    /// <summary>Infernal Robotics Next servo access by reflection (no hard dependency on IR).</summary>
    public static class IRServo
    {
        static bool resolved;
        static Type servoType;
        static PropertyInfo position, minPos, maxPos, isRotational, isLocked, isFreeMoving, defaultSpeed, maxSpeed, defaultPosition;
        static MethodInfo moveTo, getAxis;
        static FieldInfo axisField;

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
                    axisField = servoType.GetField("axis", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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
            // GetAxis needs the servo's physics joint, which doesn't exist in the editor (it throws there): use the configured axis.
            Vector3 local = Vector3.zero;
            if (HighLogic.LoadedSceneIsFlight && getAxis != null)
                try { local = (Vector3)getAxis.Invoke(s, null); } catch { }
            if (local == Vector3.zero && axisField != null) local = (Vector3)axisField.GetValue(s);
            if (local == Vector3.zero) local = Vector3.up;
            return s.part.transform.TransformDirection(local).normalized;
        }
    }

    /// <summary>Turret detection and settings, shared by flight control and the SPH/VAB tool.</summary>
    public static class TurretLogic
    {
        public const string DefaultPriority = "track,guard,radar,tgp,selected";
        public static readonly Dictionary<string, string> SourceNames = new()
        {
            { "track", "EO/IR camera track" }, { "guard", "Guard-mode target" }, { "radar", "Radar lock" }, { "tgp", "Targeting pod point" }, { "selected", "Selected contact" },
        };
        public static readonly string[] AimWithOptions = { "Auto", "Gun", "Launcher", "Camera" };

        public class Def
        {
            public readonly List<(Part part, PartModule servo, ModuleIRTurret module)> chain = new(); // base first
            public Part aimPart;
            public int kind; // 0 gun, 1 launcher, 2 camera
            public readonly List<(Part part, int kind)> options = new(); // every aimable part on this chain
            public ModuleIRTurret Settings => chain[0].module;
            public string Key => string.Join(",", chain.ConvertAll(c => c.part.persistentId.ToString()));
            public string KindName => kind == 0 ? "gun" : kind == 1 ? "launcher" : "camera";
        }

        public static int AimKind(Part p)
        {
            if (p.FindModuleImplementing<ModuleWeapon>() is ModuleWeapon w && w.fireTransforms != null && w.fireTransforms.Length > 0) return 0;
            if (p.FindModuleImplementing<MissileBase>() is MissileBase mb && !mb.HasFired) return 1;
            if (HullCam.Find(p) != null || p.FindModuleImplementing<ModuleTargetingCamera>() != null) return 2;
            return -1;
        }

        /// <summary>Every turret on a craft: rotating, enabled servos between each aimable part and the root.</summary>
        public static List<Def> Detect(IEnumerable<Part> parts, bool includeDisabled = false)
        {
            var byKey = new Dictionary<string, Def>();
            var order = new List<Def>();
            foreach (var p in parts)
            {
                int k = AimKind(p);
                if (k < 0) continue;
                var def = new Def();
                for (var q = p.parent; q != null; q = q.parent)
                {
                    var m = q.FindModuleImplementing<ModuleIRTurret>();
                    if (m == null || (!m.aiming && !includeDisabled)) continue;
                    var servo = IRServo.Find(q);
                    if (servo == null || !IRServo.Rotational(servo)) continue;
                    def.chain.Insert(0, (q, servo, m));
                }
                if (def.chain.Count == 0) continue;
                if (!byKey.TryGetValue(def.Key, out var d)) { byKey[def.Key] = d = def; order.Add(d); }
                d.options.Add((p, k));
            }
            foreach (var d in order)
            {
                string pref = d.Settings.aimWith;
                int want = pref == "Gun" ? 0 : pref == "Launcher" ? 1 : pref == "Camera" ? 2 : -1;
                var pick = d.options.Find(o => o.kind == want);
                if (pick.part == null) { d.options.Sort((a, b) => a.kind.CompareTo(b.kind)); pick = d.options[0]; }
                d.aimPart = pick.part; d.kind = pick.kind;
            }
            return order;
        }

        public static List<(string id, bool on)> ParsePriority(string s)
        {
            var list = new List<(string, bool)>();
            foreach (var t in (s ?? DefaultPriority).Split(','))
            {
                string id = t.Trim().TrimStart('-');
                if (SourceNames.ContainsKey(id) && !list.Exists(x => x.Item1 == id)) list.Add((id, !t.Trim().StartsWith("-")));
            }
            foreach (var id in SourceNames.Keys) if (!list.Exists(x => x.Item1 == id)) list.Add((id, true));
            return list;
        }

        public static string FormatPriority(List<(string id, bool on)> list) => string.Join(",", list.ConvertAll(x => (x.on ? "" : "-") + x.id));

        /// <summary>Aim ray of a part: gun barrel, launcher rail, camera line of sight.</summary>
        public static void AimRay(Part p, out Vector3 origin, out Vector3 fwd)
        {
            if (p.FindModuleImplementing<ModuleWeapon>() is ModuleWeapon w && w.fireTransforms != null && w.fireTransforms.Length > 0) { origin = w.fireTransforms[0].position; fwd = w.fireTransforms[0].forward; return; }
            if (p.FindModuleImplementing<MissileLauncher>() is MissileLauncher ml && ml.MissileReferenceTransform != null) { origin = ml.MissileReferenceTransform.position; fwd = ml.MissileReferenceTransform.forward; return; }
            if (p.FindModuleImplementing<MissileBase>() is MissileBase mb) { origin = mb.transform.position; fwd = HighLogic.LoadedSceneIsFlight ? mb.GetForwardTransform() : mb.transform.forward; return; }
            var hc = HullCam.Find(p);
            if (hc != null) { HullCam.Ray(hc, out origin, out fwd); return; }
            var tgp = p.FindModuleImplementing<ModuleTargetingCamera>();
            var tr = tgp != null && tgp.cameraParentTransform != null ? tgp.cameraParentTransform : p.transform;
            origin = tr.position; fwd = tr.forward;
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
            public ModuleIRTurret settings;
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
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed) continue;
                foreach (var d in TurretLogic.Detect(v.parts))
                {
                    var t = new Turret { vessel = v, aimPart = d.aimPart, settings = d.Settings };
                    foreach (var (part, servo, module) in d.chain)
                    {
                        if (!jointCache.TryGetValue(part, out var j)) jointCache[part] = j = new Joint();
                        j.module = module; j.servo = servo;
                        if (module.direction != 0) { j.sign = module.direction; j.calibrated = true; j.probing = false; } // set in the SPH/VAB tool
                        t.joints.Add(j);
                    }
                    t.weapon = d.kind == 0 ? d.aimPart.FindModuleImplementing<ModuleWeapon>() : null;
                    if (t.weapon != null && t.weapon.customTurretID <= 0) t.weapon.customTurretID = GunTurretID;
                    t.missile = d.kind == 1 ? d.aimPart.FindModuleImplementing<MissileBase>() : null;
                    t.hullcam = d.kind == 2 ? HullCam.Find(d.aimPart) : null;
                    t.tgp = d.kind == 2 && t.hullcam == null ? d.aimPart.FindModuleImplementing<ModuleTargetingCamera>() : null;
                    turrets.Add(t);
                }
            }
        }

        static void AimRay(Turret t, out Vector3 origin, out Vector3 fwd)
        {
            TurretLogic.AimRay(t.aimPart, out origin, out fwd);
        }

        // ---- Driving ----

        void Drive(Turret t)
        {
            if (t.vessel == null || t.aimPart == null || t.aimPart.vessel != t.vessel || (t.missile != null && t.missile.HasFired)) { nextRebuild = 0; return; }
            if (!AimPoint(t, out Vector3 aim, out string what))
            {
                foreach (var j in t.joints) { j.module.status = $"{t.Kind}: no target"; if (t.settings.restWhenIdle) Command(j, IRServo.Rest(j.servo), IRServo.Speed(j.servo) * 0.5f); }
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
                if (Mathf.Abs(phi) < t.settings.deadband) { j.module.status = $"{t.Kind} on {what}, err {err:F1}°"; continue; }
                float target = pos + j.sign * phi;
                float min = IRServo.Min(j.servo), max = IRServo.Max(j.servo);
                if (max > min && max - min < 359f) target = Mathf.Clamp(target, min, max);
                // Slow down near the target so the servo stops on it instead of overshooting and hunting.
                float speed = IRServo.MaxSpeed(j.servo) * t.settings.slewLimit / 100f * Mathf.Clamp(Mathf.Abs(phi) / Mathf.Max(1f, t.settings.slowAngle), 0.08f, 1f);
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
            foreach (var (id, on) in TurretLogic.ParsePriority(t.settings.priority))
            {
                if (!on || !Source(t, id, out point)) continue;
                if (t.settings.maxRangeKm > 0 && (point - t.aimPart.transform.position).magnitude > t.settings.maxRangeKm * 1000f) continue;
                what = TurretLogic.SourceNames[id];
                return true;
            }
            return false;
        }

        static bool Source(Turret t, string id, out Vector3 point)
        {
            point = default;
            var v = t.vessel;
            var wm = v.ActiveController().WM;
            switch (id)
            {
                case "track":
                    if (v != FlightGlobals.ActiveVessel || !EOIRView.TryTrack(out var trackPos, out var trackVel)) return false;
                    point = t.hullcam != null ? trackPos : Lead(t, trackPos, trackVel); return true;
                case "guard":
                    if (wm == null || !wm.guardMode || wm.currentTarget == null || wm.currentTarget.Vessel == null) return false;
                    var tv = wm.currentTarget.Vessel;
                    point = t.weapon != null && t.weapon.finalAimTarget != Vector3.zero && (t.weapon.finalAimTarget - (Vector3)tv.CoM).sqrMagnitude < 2000f * 2000f
                        ? t.weapon.finalAimTarget : Lead(t, tv.CoM, tv.Velocity());
                    return true;
                case "radar":
                    if (wm == null || wm.vesselRadarData == null || !wm.vesselRadarData.locked) return false;
                    var td = wm.vesselRadarData.lockedTargetData.targetData;
                    if (!td.exists) return false;
                    point = Lead(t, td.predictedPosition, td.velocity); return true;
                case "tgp":
                    foreach (var cam in VesselModuleRegistry.GetModules<ModuleTargetingCamera>(v))
                        if (cam != null && cam != t.tgp && cam.cameraEnabled && cam.groundStabilized) { point = cam.groundTargetPosition; return true; }
                    return false;
                case "selected":
                    if (v != FlightGlobals.ActiveVessel || AvionicsContacts.Selected is not Contact c) return false;
                    point = c.vessel != null ? Lead(t, c.vessel.CoM, c.vessel.Velocity()) : c.position; return true;
            }
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
