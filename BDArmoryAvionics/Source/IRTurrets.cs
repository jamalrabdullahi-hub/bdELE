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
        // Measured kinematics (by moving the servo a little and watching the mounted part turn): no guessing from configs.
        [KSPField(isPersistant = true)] public int calFrame = -1;            // reference transform: 0 fixed mesh, 1 moving mesh, 2 servo part, 3 parent part; -1 = not measured
        [KSPField(isPersistant = true)] public Vector3 calAxis = Vector3.up; // rotation axis in that frame, pointing so +position turns positive
        [KSPField(isPersistant = true)] public Vector3 calPivot = Vector3.zero; // a point on the axis, in that frame
        [KSPField(isPersistant = true)] public float calGain = 1f;           // degrees of turn per unit of servo position
        public bool Measured => calFrame >= 0;
        // VTOL tilt mechanism (set in the VTOL designer): 0 not a tilt servo, 1 tilt, 2 tilt + differential yaw. Tilt servos aren't turrets.
        [KSPField(isPersistant = true)] public int tiltRole = 0;
        [KSPField(isPersistant = true)] public float tiltHover = 0f, tiltCruise = 90f, tiltYaw = 10f;
        // Per turret: the craft's directions as you defined them in the tool (root-part frame; zero = use the root part's own).
        [KSPField(isPersistant = true)] public Vector3 refNose = Vector3.zero;
        [KSPField(isPersistant = true)] public Vector3 refUp = Vector3.zero;
        // Per turret (kept on its base servo):
        [KSPField(isPersistant = true)] public string priority = TurretLogic.DefaultPriority; // order; "-" prefix = disabled
        [KSPField(isPersistant = true)] public string aimWith = "Auto";       // Auto, Gun, Launcher, Camera
        [KSPField(isPersistant = true)] public float slewLimit = 100f;        // % of servo max speed
        [KSPField(isPersistant = true)] public float slowAngle = 20f;         // start slowing this many degrees out
        [KSPField(isPersistant = true)] public float deadband = 0.15f;        // degrees
        [KSPField(isPersistant = true)] public bool restWhenIdle = true;
        [KSPField(isPersistant = true)] public float maxRangeKm = 0f;         // 0 = no limit
        // Missiles / SAMs on the turret: point ahead of the target and pitch up for range.
        [KSPField(isPersistant = true)] public float loftAngle = -1f;         // degrees above the line of sight at long range; -1 = the missile's own loft (AAMLoft) else 15
        [KSPField(isPersistant = true)] public float loftFullKm = 8f;         // full loft from this range (none inside 1.5 km)
        [KSPField(isPersistant = true)] public bool followSelected = true;    // gun + missiles on one turret: aim with whichever the weapon manager has selected

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
        static PropertyInfo editorPosition;
        static PropertyInfo position, minPos, maxPos, isRotational, isLocked, isFreeMoving, defaultSpeed, maxSpeed, defaultPosition;
        static MethodInfo moveTo, editorSetTo;
        static FieldInfo movingMesh, fixedMesh;

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
                    editorSetTo = servoType.GetMethod("EditorSetTo", new[] { typeof(float) });
                    editorPosition = servoType.GetProperty("EditorCommandedPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    movingMesh = servoType.GetField("movingMeshTransform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    fixedMesh = servoType.GetField("fixedMeshTransform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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

        /// <summary>Editor only: put the servo at a position right away (moves the parts on it).</summary>
        public static void EditorSetTo(PartModule s, float pos) => editorSetTo?.Invoke(s, new object[] { pos });
        public static bool CanEditorMove => editorSetTo != null;
        /// <summary>Where the servo really is in the editor (after IR applied its own limits).</summary>
        public static float EditorPosition(PartModule s)
        {
            try { if (editorPosition != null) return (float)editorPosition.GetValue(s); } catch { }
            return Position(s);
        }
        /// <summary>No limits (or a full circle): positions wrap at 360.</summary>
        public static bool Continuous(PartModule s) { float mn = Min(s), mx = Max(s); return mx <= mn || mx - mn >= 359f; }

        /// <summary>Candidate reference transforms for a servo (0 fixed mesh, 1 moving mesh, 2 servo part, 3 parent part).</summary>
        public static Transform Frame(PartModule s, int which)
        {
            switch (which)
            {
                case 0: return fixedMesh?.GetValue(s) as Transform;
                case 1: return movingMesh?.GetValue(s) as Transform;
                case 2: return s.part.transform;
                case 3: return s.part.parent != null ? s.part.parent.transform : null;
            }
            return null;
        }
    }

    /// <summary>
    /// Servo kinematics measured from the parts themselves: move the servo by a few units and see how the mounted part turned.
    /// That gives the true axis, which way + turns, the degrees per unit and the pivot, whatever the servo model, mounting or IR settings.
    /// </summary>
    public static class ServoKin
    {
        public class Sample
        {
            public float pos;
            public readonly Quaternion[] frameRot = new Quaternion[4];
            public readonly bool[] has = new bool[4];
            public Quaternion aimRot;
            public Vector3 aimPos;
        }

        /// <summary>Record the servo's candidate frames and the mounted part, relative to a reference that doesn't move (the craft's root).</summary>
        public static Sample Take(PartModule servo, Part aim, Transform reference)
        {
            var q = Quaternion.Inverse(reference.rotation);
            var smp = new Sample { pos = IRServo.Position(servo), aimRot = q * aim.transform.rotation, aimPos = reference.InverseTransformPoint(aim.transform.position) };
            for (int i = 0; i < 4; i++) { var t = IRServo.Frame(servo, i); smp.has[i] = t != null; if (t != null) smp.frameRot[i] = q * t.rotation; }
            return smp;
        }

        /// <summary>Work out the kinematics from two samples; false if the move didn't show.</summary>
        public static bool Solve(ModuleIRTurret m, PartModule servo, Part aim, Transform reference, Sample a, Sample b, out string why)
        {
            why = "";
            float dPos = b.pos - a.pos;
            if (Mathf.Abs(dPos) < 0.5f) { why = "servo didn't move"; return false; }
            Quaternion d = b.aimRot * Quaternion.Inverse(a.aimRot); // the turn, in reference coordinates
            d.ToAngleAxis(out float ang, out Vector3 ax);
            if (ang > 180) { ang = 360 - ang; ax = -ax; }
            if (ang < 0.5f || float.IsNaN(ax.x) || float.IsInfinity(ax.x)) { why = "mounted part didn't turn"; return false; }
            if (dPos < 0) ax = -ax;
            // Frame: the candidate that stayed still (it sits on the craft side of this servo).
            int best = -1; float bestTurn = float.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                if (!a.has[i] || !b.has[i]) continue;
                float turn = Quaternion.Angle(a.frameRot[i], b.frameRot[i]);
                if (turn < bestTurn - 0.01f) { bestTurn = turn; best = i; }
            }
            if (best < 0 || bestTurn > ang * 0.25f) { why = "no fixed reference"; return false; }
            // Pivot: centre of the circle the mounted part moved on.
            Vector3 x0 = a.aimPos, x1 = b.aimPos, chord = Vector3.ProjectOnPlane(x1 - x0, ax);
            Vector3 c = x0;
            if (chord.magnitude > 0.01f)
            {
                Vector3 mid = (x0 + x1) * 0.5f;
                float sgn = dPos < 0 ? -1 : 1; // ax was flipped for dPos < 0; the circle centre follows the real turn direction
                c = mid + Vector3.Cross(ax * sgn, chord).normalized * (chord.magnitude * 0.5f / Mathf.Tan(ang * 0.5f * Mathf.Deg2Rad));
            }
            var frame = IRServo.Frame(servo, best);
            Quaternion frameInRef = best >= 0 ? b.frameRot[best] : Quaternion.identity;
            // store in the frame's local coordinates
            Vector3 axisRefWorld = reference.rotation * ax, pivotWorld = reference.TransformPoint(c);
            m.calFrame = best;
            m.calAxis = frame.InverseTransformDirection(axisRefWorld).normalized;
            m.calPivot = frame.InverseTransformPoint(pivotWorld);
            m.calGain = ang / Mathf.Abs(dPos);
            return true;
        }

        /// <summary>World axis (pointing so +position turns positive), pivot and degrees per unit; false if not measured.</summary>
        public static bool Get(PartModule servo, ModuleIRTurret m, out Vector3 axis, out Vector3 pivot, out float gain)
        {
            axis = Vector3.up; pivot = servo.part.transform.position; gain = 1;
            if (m == null || !m.Measured) return false;
            var f = IRServo.Frame(servo, m.calFrame);
            if (f == null) return false;
            axis = f.TransformDirection(m.calAxis).normalized;
            pivot = f.TransformPoint(m.calPivot);
            gain = Mathf.Max(0.01f, m.calGain);
            return true;
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
                    if (m == null || (!m.aiming && !includeDisabled) || m.tiltRole != 0) continue;
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

        /// <summary>One servo. If it wasn't measured in the SPH/VAB, it's measured once here with a small test move.</summary>
        class Joint
        {
            public ModuleIRTurret module;
            public PartModule servo;
            public bool probing, failed;
            public ServoKin.Sample probe0;
            public float probeT0, probeTarget;
            public float lastCommand = float.NaN, lastCommandT;
        }

        class Turret
        {
            public Vessel vessel;
            public ModuleIRTurret settings;
            public List<(Part part, int kind)> options = new();
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
                    var t = new Turret { vessel = v, aimPart = d.aimPart, settings = d.Settings, options = new List<(Part, int)>(d.options) };
                    foreach (var (part, servo, module) in d.chain)
                    {
                        if (!jointCache.TryGetValue(part, out var j)) jointCache[part] = j = new Joint();
                        j.module = module; j.servo = servo;
                        t.joints.Add(j);
                    }
                    foreach (var o in d.options)
                        if (o.kind == 0 && o.part.FindModuleImplementing<ModuleWeapon>() is ModuleWeapon g && g.customTurretID <= 0) g.customTurretID = GunTurretID;
                    SetAim(t, d.aimPart, d.kind);
                    turrets.Add(t);
                }
            }
        }

        static void SetAim(Turret t, Part p, int kind)
        {
            t.aimPart = p;
            t.weapon = kind == 0 ? p.FindModuleImplementing<ModuleWeapon>() : null;
            t.missile = kind == 1 ? p.FindModuleImplementing<MissileBase>() : null;
            t.hullcam = kind == 2 ? HullCam.Find(p) : null;
            t.tgp = kind == 2 && t.hullcam == null ? p.FindModuleImplementing<ModuleTargetingCamera>() : null;
        }

        /// <summary>Gun and missiles on the same turret: aim with the one the weapon manager has selected (and the next missile on the rail).</summary>
        static void FollowSelected(Turret t)
        {
            if (!t.settings.followSelected || t.settings.aimWith != "Auto" || t.options.Count < 2) return;
            var wm = t.vessel.ActiveController().WM;
            if (wm == null) return;
            Part want = null; int kind = -1;
            var sw = wm.selectedWeapon;
            var gun = sw?.GetWeaponModule();
            if (gun != null) { foreach (var o in t.options) if (o.part == gun.part) { want = o.part; kind = 0; break; } }
            else if (sw != null)
            {
                // A missile type is selected: the current missile if it's on this turret, else any unfired one of that type here.
                var cm = wm.CurrentMissile;
                foreach (var o in t.options)
                {
                    if (o.kind != 1) continue;
                    var mb = o.part.FindModuleImplementing<MissileBase>();
                    if (mb == null || mb.HasFired) continue;
                    if (cm != null && mb == cm) { want = o.part; kind = 1; break; }
                    if (want == null && mb.GetShortName() == sw.GetShortName()) { want = o.part; kind = 1; }
                }
            }
            if (want != null && want != t.aimPart) SetAim(t, want, kind);
        }

        static void AimRay(Turret t, out Vector3 origin, out Vector3 fwd)
        {
            TurretLogic.AimRay(t.aimPart, out origin, out fwd);
        }

        // ---- Driving ----

        void Drive(Turret t)
        {
            if (t.vessel == null || t.aimPart == null || t.aimPart.vessel != t.vessel || (t.missile != null && t.missile.HasFired)) { nextRebuild = 0; return; }
            FollowSelected(t);
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
                float pos = IRServo.Position(j.servo);
                if (!ServoKin.Get(j.servo, j.module, out Vector3 axis, out _, out float gain))
                {
                    if (!j.failed) { Probe(t, j); j.module.status = $"{t.Kind}: measuring servo"; }
                    else j.module.status = "couldn't measure this servo";
                    continue;
                }

                Vector3 fp = Vector3.ProjectOnPlane(f, axis), dp = Vector3.ProjectOnPlane(d, axis);
                if (fp.sqrMagnitude < 1e-4f || dp.sqrMagnitude < 1e-4f) continue;
                float phi = Vector3.SignedAngle(fp, dp, axis); // degrees to turn about this axis
                if (Mathf.Abs(phi) < t.settings.deadband) { j.module.status = $"{t.Kind} on {what}, err {err:F1}°"; continue; }
                float target = pos + phi / gain;
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

        /// <summary>Move the servo 8 units and measure how the mounted part turned (relative to the craft's root part).</summary>
        static void Probe(Turret t, Joint j)
        {
            var reference = t.vessel.rootPart.transform;
            if (!j.probing)
            {
                j.probing = true; j.probeT0 = Time.time;
                j.probe0 = ServoKin.Take(j.servo, t.aimPart, reference);
                float pos = j.probe0.pos, max = IRServo.Max(j.servo), min = IRServo.Min(j.servo);
                j.probeTarget = pos + ((max > min && pos + 8f > max) ? -8f : 8f);
                IRServo.MoveTo(j.servo, j.probeTarget, IRServo.Speed(j.servo));
                j.lastCommand = float.NaN;
                return;
            }
            bool arrived = Mathf.Abs(IRServo.Position(j.servo) - j.probeTarget) < 0.5f;
            if (!arrived && Time.time - j.probeT0 < 4f) return;
            var s1 = ServoKin.Take(j.servo, t.aimPart, reference);
            j.probing = false;
            if (ServoKin.Solve(j.module, j.servo, t.aimPart, reference, j.probe0, s1, out string why))
                Debug.Log($"[BDArmoryAvionics]: Turret servo {j.servo.part.partInfo.title} measured: {j.module.calGain:F2}°/unit, frame {j.module.calFrame}");
            else { j.failed = true; Debug.Log($"[BDArmoryAvionics]: Turret servo {j.servo.part.partInfo.title}: measuring failed ({why})"); }
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

        /// <summary>Lead for guns: where the target will be when the bullet arrives, plus drop. Missiles: lead and loft.</summary>
        static Vector3 Lead(Turret t, Vector3 targetPos, Vector3 targetVel)
        {
            if (t.missile != null) return MissileAim(t, targetPos, targetVel);
            if (t.weapon == null) return targetPos;
            Vector3 origin = t.weapon.fireTransforms[0].position;
            Vector3 relVel = targetVel - (Vector3)t.vessel.Velocity();
            float speed = Mathf.Max(t.weapon.bulletVelocity, 50f);
            float tof = (targetPos - origin).magnitude / speed;
            for (int i = 0; i < 2; i++) tof = (targetPos + relVel * tof - origin).magnitude / speed;
            float g = (float)FlightGlobals.getGeeForceAtPosition(origin).magnitude;
            return targetPos + relVel * tof + (Vector3)t.vessel.upAxis * 0.5f * g * tof * tof;
        }

        /// <summary>
        /// Missile/SAM: point at where the target will be (average missile speed from the estimator), then pitch up by the loft angle,
        /// fading in from 1.5 km to the full-loft range so close shots go straight and long shots climb.
        /// </summary>
        static Vector3 MissileAim(Turret t, Vector3 targetPos, Vector3 targetVel)
        {
            var m = t.missile;
            Vector3 origin = m.transform.position;
            float range = (targetPos - origin).magnitude;
            float avgSpeed = 600f;
            try
            {
                var est = MissileEstimator.Estimate(m, origin, t.vessel.Velocity(), targetPos, targetVel, withMaxRange: false, targetId: 0);
                if (est.valid && est.timeOfFlight > 0.1f) avgSpeed = Mathf.Max(150f, range / est.timeOfFlight);
            }
            catch { }
            float tof = range / avgSpeed;
            Vector3 aim = targetPos + (targetVel - (Vector3)t.vessel.Velocity()) * tof;
            float loft = LoftFor(t.settings, m);
            float k = Mathf.Clamp01((range - 1500f) / Mathf.Max(500f, t.settings.loftFullKm * 1000f - 1500f));
            loft *= k;
            if (loft < 0.1f) return aim;
            Vector3 up = t.vessel.upAxis;
            Vector3 d = aim - origin;
            float elev = 90f - Vector3.Angle(up, d);
            float newElev = Mathf.Min(elev + loft, 75f);
            Vector3 right = Vector3.Cross(up, d);
            if (right.sqrMagnitude < 1e-4f) return aim;
            return origin + Quaternion.AngleAxis(-(newElev - elev), right.normalized) * d;
        }

        public static float LoftFor(ModuleIRTurret s, MissileBase m)
        {
            if (s.loftAngle >= 0) return s.loftAngle;
            if (m != null && (m.GuidanceMode == MissileBase.GuidanceModes.AAMLoft)) return Mathf.Clamp(m.LoftAngle, 0, 60);
            return 15f;
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
