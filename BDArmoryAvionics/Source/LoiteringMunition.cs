using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons;

namespace BDArmory.Avionics
{
    public enum LMState { Safe, Transit, Loiter, Pending, Terminal, Done }

    /// <summary>
    /// Loitering munition (a game part setup: probe core + engine + wings + a BDArmory warhead, optional HullCam camera).
    /// Once armed it flies to a loiter point, circles and searches; strikes on its own (Autonomous), after the operator
    /// approves (Approval) or only on a target the operator designates. The operator can watch through its camera,
    /// take over and fly it, designate what's under the crosshair, wave off or detonate at any time.
    /// </summary>
    public class ModuleLoiteringMunition : PartModule
    {
        const string Group = "loiterMunition";
        public static readonly string[] StrikeModes = ["Autonomous", "Approval", "Designate"];
        public static readonly string[] TargetTypes = ["Any", "Ground/ships", "Air", "Radars (SEAD)"];

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Loitering munition", groupName = Group, groupDisplayName = "Loitering munition", groupStartCollapsed = true),
         UI_Toggle(enabledText = "Yes", disabledText = "No", scene = UI_Scene.All)]
        public bool lmEnabled = false;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Strike", groupName = Group),
         UI_ChooseOption(options = ["Autonomous", "Approval", "Designate"], scene = UI_Scene.All)]
        public string strikeMode = "Approval";

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Targets", groupName = Group),
         UI_ChooseOption(options = ["Any", "Ground/ships", "Air", "Radars (SEAD)"], scene = UI_Scene.All)]
        public string targetType = "Ground/ships";

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Loiter altitude (ASL)", guiUnits = " m", groupName = Group),
         UI_FloatRange(minValue = 200f, maxValue = 8000f, stepIncrement = 50f, scene = UI_Scene.All)]
        public float loiterAlt = 1200f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Loiter radius", guiUnits = " m", groupName = Group),
         UI_FloatRange(minValue = 300f, maxValue = 6000f, stepIncrement = 50f, scene = UI_Scene.All)]
        public float loiterRadius = 1200f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Cruise speed", guiUnits = " m/s", groupName = Group),
         UI_FloatRange(minValue = 25f, maxValue = 400f, stepIncrement = 5f, scene = UI_Scene.All)]
        public float cruiseSpeed = 70f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Search range", guiUnits = " km", groupName = Group),
         UI_FloatRange(minValue = 1f, maxValue = 30f, stepIncrement = 0.5f, scene = UI_Scene.All)]
        public float searchRange = 6f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Dive angle", guiUnits = "°", groupName = Group),
         UI_FloatRange(minValue = 15f, maxValue = 85f, stepIncrement = 5f, scene = UI_Scene.All)]
        public float diveAngle = 45f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Proximity fuze", guiUnits = " m", groupName = Group),
         UI_FloatRange(minValue = 1f, maxValue = 40f, stepIncrement = 1f, scene = UI_Scene.All)]
        public float fuzeDistance = 6f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Arm on separation", groupName = Group),
         UI_Toggle(enabledText = "Yes", disabledText = "No", scene = UI_Scene.All)]
        public bool autoArm = true;

        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "LM status", groupName = Group)]
        public string status = "Safe";

        // Persistent flight state
        [KSPField(isPersistant = true)] public bool armed;
        [KSPField(isPersistant = true)] public string teamName = "";
        [KSPField(isPersistant = true)] public double loiterLat, loiterLon;
        [KSPField(isPersistant = true)] public uint launchVesselId, homeVesselId;
        [KSPField(isPersistant = true)] public string homeName = "";

        public LMState state = LMState.Safe;
        public bool manual;                       // operator is flying it
        public Vessel target;                     // target craft (null with a point target)
        public Vector3d targetPoint;              // lat/lon/alt of a point target
        public bool pointTarget;
        public string targetLabel = "";
        public float lastSearch, stateSince;
        readonly Dictionary<Vessel, float> waivedOff = new();
        Vessel registered;
        Vector3 cmdDir; float cmdThrottle; float cmdMaxG = 2f;
        float lastDist = float.MaxValue;
        public BDTeam Team => string.IsNullOrEmpty(teamName) ? null : BDTeam.Get(teamName);
        public Vessel Home { get { foreach (var v in FlightGlobals.Vessels) if (v != null && v.persistentId == homeVesselId) return v; return null; } }
        public string Label => vessel != null ? vessel.GetName() : "LM";
        public bool CanStrike => target != null || pointTarget;

        public override void OnStart(StartState st)
        {
            if (part.CrewCapacity > 0) { foreach (var f in Fields) if (f.group?.name == Group) { f.guiActive = f.guiActiveEditor = false; } return; }
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (launchVesselId == 0) launchVesselId = vessel.persistentId;
            if (armed) state = LMState.Transit;
            LoiteringMunitions.Register(this);
        }

        void OnDestroy()
        {
            Unhook();
            LoiteringMunitions.Unregister(this);
        }

        // ---- Part menu ----

        [KSPEvent(guiActive = true, guiName = "Arm / launch", groupName = Group)]
        public void ArmEvent() => Arm(null);

        [KSPEvent(guiActive = true, guiName = "Wave off (return to loiter)", groupName = Group)]
        public void AbortEvent() => WaveOff();

        [KSPEvent(guiActive = true, guiName = "Detonate", groupName = Group)]
        public void DetonateEvent() => Detonate("operator");

        [KSPAction("Arm loitering munition")] public void ArmAction(KSPActionParam p) => Arm(null);

        public void Arm(Vessel home)
        {
            if (!lmEnabled || armed || vessel == null) return;
            int siblings = 0;
            foreach (var m in VesselModuleRegistry.GetModules<ModuleLoiteringMunition>(vessel)) if (m != null && m.lmEnabled) siblings++;
            if (vessel.GetCrewCount() > 0 || siblings > 1)
            {
                ScreenMessages.PostScreenMessage($"{Label}: separate it from the launcher first (it would fly the whole craft)", 4f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            home ??= Home;
            if (home == null || home == vessel)
            {
                var av = FlightGlobals.ActiveVessel;
                home = av != null && av != vessel ? av : null;
            }
            homeVesselId = home != null ? home.persistentId : 0;
            homeName = home != null ? home.GetName() : "";
            var team = (home != null ? home.ActiveController().WM?.Team : null) ?? vessel.ActiveController().WM?.Team
                       ?? (BDArmorySetup.Instance?.ActiveWeaponManager?.Team);
            teamName = team != null ? team.Name : "";

            // Loiter at the launcher's designated GPS point, else 2 km ahead.
            Vector3d p = Vector3d.zero;
            var wm = home != null ? home.ActiveController().WM : null;
            if (wm != null && wm.designatedGPSInfo.worldPos != Vector3d.zero) p = wm.designatedGPSInfo.worldPos;
            if (p == Vector3d.zero) p = (Vector3d)vessel.CoM + (Vector3d)Horizontal(vessel.srf_velocity.sqrMagnitude > 25 ? (Vector3)vessel.srf_velocity : vessel.ReferenceTransform.up) * 2000;
            SetLoiter(p);

            int n = LoiteringMunitions.NextNumber();
            vessel.vesselName = string.IsNullOrEmpty(homeName) ? $"LM-{n}" : $"LM-{n} ({homeName})";
            foreach (var w in VesselModuleRegistry.GetModules<BDExplosivePart>(vessel))
            {
                if (w == null) continue;
                w.Armed = true;
                if (team != null) w.Team = team;
                w.sourcevessel = vessel;
            }
            foreach (var e in VesselModuleRegistry.GetModules<ModuleEngines>(vessel)) if (e != null && !e.EngineIgnited) e.Activate();
            armed = true;
            SetState(LMState.Transit);
            Debug.Log($"[BDArmoryAvionics]: {Label} armed: team {teamName}, home {homeName}, mode {strikeMode}, targets {targetType}, loiter {loiterLat:F4},{loiterLon:F4} at {loiterAlt:F0} m");
            ScreenMessages.PostScreenMessage($"{Label} armed - {strikeMode}, {targetType}", 3f, ScreenMessageStyle.UPPER_CENTER);
            if (team == null) ScreenMessages.PostScreenMessage($"{Label}: no team (launch it from a craft with a weapon manager, or give it one) - it will only strike designated targets", 5f, ScreenMessageStyle.UPPER_CENTER);
        }

        public void SetLoiter(Vector3d world)
        {
            var b = vessel.mainBody;
            loiterLat = b.GetLatitude(world);
            loiterLon = b.GetLongitude(world);
            if (state == LMState.Loiter) SetState(LMState.Transit);
        }

        public Vector3d LoiterWorld => vessel.mainBody.GetWorldSurfacePosition(loiterLat, loiterLon, LoiterAltitude);
        double LoiterAltitude
        {
            get
            {
                double ground = Math.Max(0, vessel.mainBody.TerrainAltitude(loiterLat, loiterLon));
                return Math.Max(loiterAlt, ground + 150);
            }
        }

        void SetState(LMState s) { state = s; stateSince = Time.time; lastDist = float.MaxValue; }

        public void Strike(Vessel v, Vector3d? point, string why)
        {
            if (!armed) return;
            target = v;
            pointTarget = v == null && point.HasValue;
            if (pointTarget)
            {
                var b = vessel.mainBody; var w = point.Value;
                targetPoint = new Vector3d(b.GetLatitude(w), b.GetLongitude(w), b.GetAltitude(w));
            }
            if (!CanStrike) return;
            targetLabel = v != null ? v.GetName() : $"point {targetPoint.x:F4},{targetPoint.y:F4}";
            SetState(LMState.Terminal);
            Debug.Log($"[BDArmoryAvionics]: {Label} striking {targetLabel} ({why})");
            ScreenMessages.PostScreenMessage($"{Label}: STRIKE {targetLabel}", 3f, ScreenMessageStyle.UPPER_CENTER);
        }

        public void Approve()
        {
            if (state == LMState.Pending && CanStrike) Strike(target, pointTarget ? (Vector3d?)TargetPos : null, "operator approved");
        }

        public void WaveOff()
        {
            if (!armed) return;
            if (target != null) waivedOff[target] = Time.time + 30f;
            Debug.Log($"[BDArmoryAvionics]: {Label} waved off {targetLabel}");
            target = null; pointTarget = false; targetLabel = "";
            SetState(LMState.Transit);
        }

        public void Detonate(string why)
        {
            if (vessel == null) return;
            Debug.Log($"[BDArmoryAvionics]: {Label} detonating ({why}){(target != null ? $", {(target.CoM - vessel.CoM).magnitude:F1} m from {target.GetName()}" : "")}");
            bool any = false;
            foreach (var w in VesselModuleRegistry.GetModules<BDExplosivePart>(vessel)) if (w != null) { w.Armed = true; w.DetonateIfPossible(); any = true; }
            if (!any) ScreenMessages.PostScreenMessage($"{Label} has no warhead", 3f, ScreenMessageStyle.UPPER_CENTER);
            SetState(LMState.Done);
        }

        public Vector3 TargetPos => target != null ? target.CoM : (Vector3)vessel.mainBody.GetWorldSurfacePosition(targetPoint.x, targetPoint.y, targetPoint.z);

        // ---- Flight ----

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || !lmEnabled || vessel == null || part.CrewCapacity > 0) return;
            if (!armed)
            {
                status = "Safe";
                if (autoArm && launchVesselId != 0 && vessel.persistentId != launchVesselId && !vessel.packed)
                {
                    Vessel home = null;
                    foreach (var v in FlightGlobals.VesselsLoaded) if (v != null && v.persistentId == launchVesselId) home = v;
                    homeVesselId = launchVesselId;
                    Arm(home);
                }
                return;
            }
            if (vessel.packed || !vessel.loaded) return;
            Hook();
            if (state == LMState.Done) return;

            if (target != null && (target.state == Vessel.State.DEAD || !target.loaded)) { Debug.Log($"[BDArmoryAvionics]: {Label} lost {targetLabel}"); target = null; pointTarget = false; if (state == LMState.Terminal || state == LMState.Pending) SetState(LMState.Transit); }

            Fuze();
            if (state == LMState.Done) return;

            switch (state)
            {
                case LMState.Transit:
                case LMState.Loiter:
                case LMState.Pending:
                    Loiter();
                    if (Time.time - lastSearch > 1f) { lastSearch = Time.time; Search(); }
                    break;
                case LMState.Terminal:
                    Terminal();
                    break;
            }
            status = $"{(manual ? "MANUAL " : "")}{state}{(CanStrike ? $": {targetLabel}" : "")}";
        }

        void Hook()
        {
            if (registered == vessel) return;
            Unhook();
            vessel.OnFlyByWire += Fly;
            registered = vessel;
        }

        void Unhook()
        {
            if (registered != null) registered.OnFlyByWire -= Fly;
            registered = null;
        }

        Vector3 Up => (vessel.CoM - vessel.mainBody.position).normalized;
        Vector3 Horizontal(Vector3 v) { var u = (vessel.CoM - vessel.mainBody.position).normalized; var h = v - Vector3.Dot(v, u) * u; return h.sqrMagnitude > 1e-4f ? h.normalized : vessel.ReferenceTransform.up; }

        void Loiter()
        {
            var up = Up;
            Vector3 pos = vessel.CoM;
            Vector3 rel = pos - (Vector3)LoiterWorld;
            Vector3 hrel = rel - Vector3.Dot(rel, up) * up;
            float d = hrel.magnitude;
            Vector3 hdir;
            if (d > loiterRadius * 1.5f || d < 1f) hdir = -Horizontal(hrel.sqrMagnitude > 1 ? hrel : vessel.ReferenceTransform.up);
            else
            {
                if (state == LMState.Transit) SetState(LMState.Loiter);
                Vector3 radial = hrel / d;
                Vector3 tangent = Vector3.Cross(up, radial);
                hdir = (tangent - radial * Mathf.Clamp((d - loiterRadius) / loiterRadius * 2f, -1f, 1f)).normalized;
            }
            double alt = Math.Max(LoiterAltitude, vessel.terrainAltitude + 120);
            float gamma = Mathf.Clamp((float)(alt - vessel.altitude) / 400f, -0.35f, 0.35f);
            cmdDir = hdir * Mathf.Cos(gamma) + up * Mathf.Sin(gamma);
            if (vessel.LandedOrSplashed) cmdDir = (Horizontal(vessel.ReferenceTransform.up) + up * (vessel.srfSpeed > 25 ? 0.25f : 0f)).normalized;
            cmdThrottle = Mathf.Clamp(0.4f + (cruiseSpeed - (float)vessel.srfSpeed) * 0.05f, 0.05f, 1f);
            cmdMaxG = 2.5f;
        }

        void Terminal()
        {
            var up = Up;
            Vector3 pos = vessel.CoM, tp = TargetPos;
            Vector3 tv = target != null ? (Vector3)target.Velocity() : Vector3.zero;
            float V = Mathf.Max(30f, (float)vessel.srfSpeed);
            Vector3 rel = tp - pos;
            float dist = rel.magnitude;
            Vector3 aim = tp + tv * (dist / V);
            Vector3 toAim = aim - pos;
            float h = -Vector3.Dot(toAim, up);                      // height above the aim point
            float hd = (toAim + up * h).magnitude;                   // horizontal distance
            bool airTarget = target != null && !target.LandedOrSplashed;
            if (!airTarget && h > 0 && Mathf.Atan2(h, hd) * Mathf.Rad2Deg < diveAngle && hd > 50f)
            {
                // Approach level until the target sits at the dive angle below, then push over (top attack).
                Vector3 hdir = Horizontal(toAim);
                float gamma = Mathf.Clamp((float)(Math.Max(vessel.altitude, vessel.terrainAltitude + 100) - vessel.altitude) / 300f, -0.2f, 0.3f);
                cmdDir = hdir * Mathf.Cos(gamma) + up * Mathf.Sin(gamma);
            }
            else cmdDir = toAim.normalized;
            cmdThrottle = 1f;
            cmdMaxG = 7f;
        }

        /// <summary>Proximity, closest-approach and impact fuzes (also while the operator flies it).</summary>
        void Fuze()
        {
            if (state != LMState.Terminal && !manual) return;
            float dist = CanStrike ? (TargetPos - vessel.CoM).magnitude : float.MaxValue;
            if (dist < fuzeDistance) { Detonate("proximity"); return; }
            {
                if (dist < fuzeDistance * 3f && dist > lastDist + 0.05f) { Detonate("closest approach"); return; }
                lastDist = dist;
                var v = vessel.srf_velocity;
                float look = (float)vessel.srfSpeed * Time.fixedDeltaTime * 3f + 2f;
                if (v.sqrMagnitude > 1 && (dist < 500f || manual) && Physics.Raycast(vessel.CoM + (Vector3)v.normalized * 3f, v.normalized, out var hit, look, (int)(LayerMasks.Scenery | LayerMasks.Parts | LayerMasks.EVA | LayerMasks.Wheels)))
                {
                    var p = hit.collider.GetComponentInParent<Part>();
                    if (p == null || p.vessel != vessel) { Detonate("impact"); return; }
                }
            }
        }

        void Search()
        {
            var team = Team;
            if (strikeMode == "Designate" || team == null) return;
            if (state == LMState.Pending && target != null) return;
            Vessel best = null; float bestScore = float.MaxValue;
            float range = searchRange * 1000f;
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v == vessel || v.packed || VesselModuleRegistry.IgnoredVesselTypes.Contains(v.vesselType) || LoiteringMunitions.IsMunition(v)) continue;
                var wm = v.ActiveController().WM;
                if (wm == null || wm.Team == null || !team.IsEnemy(wm.Team)) continue;
                if (waivedOff.TryGetValue(v, out float until) && Time.time < until) continue;
                bool air = !v.LandedOrSplashed;
                bool radar = false;
                foreach (var r in VesselModuleRegistry.GetModules<ModuleRadar>(v)) if (r != null && r.radarEnabled) { radar = true; break; }
                if (targetType == "Ground/ships" && air) continue;
                if (targetType == "Air" && !air) continue;
                if (targetType == "Radars (SEAD)" && !radar) continue;
                float d = (v.CoM - vessel.CoM).magnitude;
                if (d > range) continue;
                if (Physics.Linecast(vessel.CoM, v.CoM, (int)LayerMasks.Scenery)) continue; // hidden by terrain
                float score = d * (radar ? 0.7f : 1f);
                if (score < bestScore) { bestScore = score; best = v; }
            }
            if (best == null) { if (state == LMState.Pending) { target = null; SetState(LMState.Loiter); } return; }
            if (strikeMode == "Autonomous") { Strike(best, null, "autonomous"); return; }
            if (state != LMState.Pending || target != best)
            {
                target = best; pointTarget = false; targetLabel = best.GetName();
                SetState(LMState.Pending);
                ScreenMessages.PostScreenMessage($"{Label}: target {targetLabel} - approve? (Loitering munitions window / {AvionicsSettings.KeyLMStrike})", 5f, ScreenMessageStyle.UPPER_CENTER);
                Debug.Log($"[BDArmoryAvionics]: {Label} found {targetLabel} at {(best.CoM - vessel.CoM).magnitude:F0} m, awaiting approval");
            }
        }

        /// <summary>Bank-to-turn autopilot: roll the lift vector toward the turn (plus gravity), pull toward the commanded direction.</summary>
        void Fly(FlightCtrlState s)
        {
            if (!armed || manual || state == LMState.Done || vessel == null || vessel.packed) return;
            var rt = vessel.ReferenceTransform;
            Vector3 nose = rt.up, right = rt.right, top = -rt.forward;
            Vector3 v = vessel.srf_velocity;
            float V = v.magnitude;
            Vector3 vhat = V > 5f ? v / V : nose;
            Vector3 D = cmdDir.sqrMagnitude > 0.5f ? cmdDir.normalized : nose;
            Vector3 g = FlightGlobals.getGeeForceAtPosition(vessel.CoM);

            Vector3 a = (D - vhat) * Mathf.Max(V, 20f) * 0.8f;
            a = Vector3.ClampMagnitude(a - Vector3.Dot(a, vhat) * vhat, cmdMaxG * 9.81f);
            Vector3 L = a - (g - Vector3.Dot(g, vhat) * vhat);        // lift needed = turn + hold against gravity
            Vector3 Lp = L - Vector3.Dot(L, nose) * nose;
            bool push = Vector3.Dot(Lp, top) < 0;                      // asking for negative g: stay upright and push
            Vector3 rollRef = Lp.sqrMagnitude > 0.25f ? (push ? -Lp : Lp) : (Up - Vector3.Dot(Up, nose) * nose);
            float rollErr = Mathf.Atan2(Vector3.Dot(rollRef, right), Vector3.Dot(rollRef, top));
            float pitchErr = Mathf.Atan2(Vector3.Dot(D, top), Vector3.Dot(D, nose));
            float yawErr = Mathf.Atan2(Vector3.Dot(D, right), Vector3.Dot(D, nose));

            Vector3 w = vessel.angularVelocity;
            float pitchRate = -w.x, rollRate = -w.y, yawRate = -w.z;
            s.pitch = Mathf.Clamp(2.2f * pitchErr - 0.6f * pitchRate + 0.05f, -1f, 1f);
            s.roll = Mathf.Clamp(1.6f * rollErr - 0.45f * rollRate, -1f, 1f);
            s.yaw = Mathf.Clamp(0.5f * yawErr - 0.3f * yawRate, -1f, 1f);
            s.mainThrottle = cmdThrottle;
            if (vessel.LandedOrSplashed) { s.roll = Mathf.Clamp(-0.45f * rollRate, -1, 1); s.wheelSteer = 0; }
        }
    }

    /// <summary>Registry, operator window, hull-camera handover and terminal HUD for loitering munitions.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class LoiteringMunitions : MonoBehaviour
    {
        public static LoiteringMunitions Instance;
        static readonly List<ModuleLoiteringMunition> all = new();
        static readonly Dictionary<string, uint> operatorByName = new(); // munition name -> home/operator vessel id (kill credit)
        static int counter;
        public const int WindowID = 71730334;
        static Rect rect = new(Screen.width - 820, 160, 470, 360);
        Vector2 scroll;
        ModuleLoiteringMunition selected;
        Vessel operatorHome;

        static Type hullCamType;
        static FieldInfo hullCurrent;
        static MethodInfo hullActivate, hullLeave;

        void Awake() { Instance = this; counter = 0; }
        void OnDestroy() { if (Instance == this) Instance = null; }

        public static void Register(ModuleLoiteringMunition m) { if (!all.Contains(m)) all.Add(m); }
        public static void Unregister(ModuleLoiteringMunition m) => all.Remove(m);
        public static int NextNumber() => ++counter;
        public static bool IsMunition(Vessel v) { foreach (var m in all) if (m != null && m.vessel == v && m.armed) return true; return false; }
        public static ModuleLoiteringMunition For(Vessel v) { foreach (var m in all) if (m != null && m.vessel == v && m.lmEnabled) return m; return null; }

        /// <summary>The craft that launched/controlled a munition (by name), for kill credit.</summary>
        public static Vessel OperatorOf(string munitionName)
        {
            if (string.IsNullOrEmpty(munitionName)) return null;
            uint id = 0;
            if (!operatorByName.TryGetValue(munitionName, out id))
                foreach (var m in all) if (m != null && m.vessel != null && m.vessel.GetName() == munitionName) id = m.homeVesselId;
            if (id == 0) return null;
            foreach (var v in FlightGlobals.Vessels) if (v != null && v.persistentId == id) return v;
            return null;
        }

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            foreach (var m in all) if (m != null && m.armed && m.vessel != null && m.homeVesselId != 0) operatorByName[m.vessel.GetName()] = m.homeVesselId;

            var av = FlightGlobals.ActiveVessel;
            var lm = av != null ? For(av) : null;
            if (lm == null || !lm.armed || GUIUtility.keyboardControl != 0) return;
            if (Down(AvionicsSettings.KeyLMStrike)) DesignateCrosshair(lm);
            if (Down(AvionicsSettings.KeyLMManual)) { lm.manual = !lm.manual; ScreenMessages.PostScreenMessage(lm.manual ? $"{lm.Label}: MANUAL - you have control" : $"{lm.Label}: autonomous", 2f, ScreenMessageStyle.UPPER_CENTER); }
            if (Down(AvionicsSettings.KeyLMDetonate)) lm.Detonate("operator key");
            if (Down(AvionicsSettings.KeyLMReturn)) ReturnHome();
        }

        static bool Down(KeyCode k) => k != KeyCode.None && Input.GetKeyDown(k);

        /// <summary>Strike whatever is under the camera's centre: a craft, or that point on the ground.</summary>
        void DesignateCrosshair(ModuleLoiteringMunition lm)
        {
            if (lm.state == LMState.Pending && lm.target != null) { lm.manual = false; lm.Approve(); return; }
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null) return;
            var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
            ray.origin += ray.direction * 5f;
            if (Physics.Raycast(ray, out var hit, 30000f, (int)(LayerMasks.Scenery | LayerMasks.Parts | LayerMasks.Wheels)))
            {
                var p = hit.collider.GetComponentInParent<Part>();
                lm.manual = false;
                if (p != null && p.vessel != null && p.vessel != lm.vessel) lm.Strike(p.vessel, null, "operator designated (camera)");
                else lm.Strike(null, hit.point, "operator designated point (camera)");
            }
            else ScreenMessages.PostScreenMessage("Nothing under the crosshair", 2f, ScreenMessageStyle.UPPER_CENTER);
        }

        // ---- Camera handover ----

        public void Watch(ModuleLoiteringMunition lm, bool takeControl)
        {
            if (lm?.vessel == null) return;
            var av = FlightGlobals.ActiveVessel;
            if (av != null && For(av) == null) operatorHome = av;
            lm.manual = takeControl;
            if (av != lm.vessel) FlightGlobals.ForceSetActiveVessel(lm.vessel);
            StartCoroutine(ActivateHullCam(lm));
        }

        IEnumerator ActivateHullCam(ModuleLoiteringMunition lm)
        {
            yield return new WaitForSeconds(0.6f);
            if (lm == null || lm.vessel == null) yield break;
            if (!ResolveHullCam()) { ScreenMessages.PostScreenMessage("HullCam VDS not found - using the normal camera", 3f, ScreenMessageStyle.UPPER_CENTER); yield break; }
            PartModule cam = null;
            foreach (var p in lm.vessel.parts) { foreach (PartModule m in p.Modules) if (hullCamType.IsInstanceOfType(m)) { cam = m; break; } if (cam != null) break; }
            if (cam == null) { ScreenMessages.PostScreenMessage($"{lm.Label} has no hull camera - using the normal camera", 3f, ScreenMessageStyle.UPPER_CENTER); yield break; }
            try { if (hullCurrent.GetValue(null) as PartModule != cam) hullActivate.Invoke(cam, null); }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: HullCam activation failed: {e.InnerException?.Message ?? e.Message}"); }
        }

        static bool ResolveHullCam()
        {
            if (hullCamType != null) return true;
            foreach (var a in AssemblyLoader.loadedAssemblies)
            {
                var t = a.assembly.GetType("HullcamVDS.MuMechModuleHullCamera");
                if (t == null) continue;
                hullCamType = t;
                const BindingFlags bf = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                hullCurrent = t.GetField("sCurrentCamera", bf);
                hullActivate = t.GetMethod("ActivateCamera", bf);
                hullLeave = t.GetMethod("LeaveCamera", bf);
                return hullCurrent != null && hullActivate != null;
            }
            return false;
        }

        public void ReturnHome()
        {
            try { if (ResolveHullCam() && hullLeave != null && hullCurrent.GetValue(null) != null) hullLeave.Invoke(null, null); } catch { }
            var home = operatorHome;
            if (home == null || !home.loaded)
            {
                var lm = FlightGlobals.ActiveVessel != null ? For(FlightGlobals.ActiveVessel) : null;
                home = lm?.Home;
            }
            if (home != null && home.loaded) FlightGlobals.ForceSetActiveVessel(home);
            else ScreenMessages.PostScreenMessage("Home craft not loaded", 2f, ScreenMessageStyle.UPPER_CENTER);
        }

        void LateUpdate()
        {
            // If the munition we were watching is gone, go back to the operator's craft.
            if (operatorHome != null && operatorHome.loaded && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel != operatorHome)
            {
                var lm = For(FlightGlobals.ActiveVessel);
                if (lm == null && returnPending < 0) returnPending = Time.time + 1.5f;
            }
            if (returnPending > 0 && Time.time > returnPending)
            {
                returnPending = -1;
                if (operatorHome != null && operatorHome.loaded && For(FlightGlobals.ActiveVessel) == null && FlightGlobals.ActiveVessel != operatorHome) FlightGlobals.ForceSetActiveVessel(operatorHome);
                operatorHome = null;
            }
        }
        float returnPending = -1;

        // ---- GUI ----

        void OnGUI()
        {
            if (!HighLogic.LoadedSceneIsFlight || !BDArmorySetup.GAME_UI_ENABLED) return;
            var av = FlightGlobals.ActiveVessel;
            var lm = av != null ? For(av) : null;
            if (lm != null && lm.armed) DrawHUD(lm);
            if (AvionicsSettings.ShowLM) rect = AvionicsCore.DrawWindow(WindowID, rect, DrawWindow, "Loitering Munitions", BDArmorySetup.BDGuiSkin);
        }

        static void DrawHUD(ModuleLoiteringMunition lm)
        {
            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            var col = lm.state == LMState.Terminal ? AvionicsDraw.Hostile : lm.manual ? AvionicsDraw.LockBox : AvionicsDraw.Friendly;
            if (AvionicsDraw.Begin())
            {
                AvionicsDraw.Line(new Vector2(cx - 30, cy), new Vector2(cx - 8, cy), 1.5f, col);
                AvionicsDraw.Line(new Vector2(cx + 8, cy), new Vector2(cx + 30, cy), 1.5f, col);
                AvionicsDraw.Line(new Vector2(cx, cy - 30), new Vector2(cx, cy - 8), 1.5f, col);
                AvionicsDraw.Line(new Vector2(cx, cy + 8), new Vector2(cx, cy + 30), 1.5f, col);
                var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
                if (cam != null && lm.CanStrike)
                {
                    var sp = cam.WorldToScreenPoint(lm.TargetPos);
                    if (sp.z > 0) AvionicsDraw.Box(new Vector2(sp.x, Screen.height - sp.y), 14, 2f, lm.state == LMState.Pending ? AvionicsDraw.Unknown : AvionicsDraw.Hostile);
                }
                AvionicsDraw.End();
            }
            var v = lm.vessel;
            string tgt = lm.CanStrike ? $"{lm.targetLabel}  {(lm.TargetPos - v.CoM).magnitude:F0} m" : "no target";
            var lines = new[]
            {
                $"{lm.Label}   {(lm.manual ? "MANUAL CONTROL" : "AUTONOMOUS")}   {lm.state}   {lm.strikeMode} / {lm.targetType}",
                $"ALT {v.altitude:F0} m   AGL {v.radarAltitude:F0} m   SPD {v.srfSpeed:F0} m/s   FUEL {FuelFraction(v):P0}",
                $"TGT {tgt}",
                $"{AvionicsSettings.KeyLMStrike}: {(lm.state == LMState.Pending ? "approve strike" : "strike what's under the crosshair")}   {AvionicsSettings.KeyLMManual}: take/give control   {AvionicsSettings.KeyLMDetonate}: detonate   {AvionicsSettings.KeyLMReturn}: back to home craft",
            };
            for (int i = 0; i < lines.Length; i++)
                AvionicsDraw.ShadowLabel(new Rect(cx - 330, Screen.height - 150 + i * 16, 660, 16), lines[i], AvionicsDraw.LabelCenter, i == 0 ? col : AvionicsDraw.HudText);
        }

        static float FuelFraction(Vessel v)
        {
            double amt = 0, max = 0;
            foreach (var p in v.parts) foreach (PartResource r in p.Resources)
                    if (r.resourceName == "LiquidFuel" || r.resourceName == "SolidFuel" || r.resourceName == "ElectricCharge" && max == 0) { amt += r.amount; max += r.maxAmount; }
            return max > 0 ? (float)(amt / max) : 0;
        }

        void DrawWindow(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { AvionicsSettings.ShowLM = false; return; }
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            float w = rect.width - 16, y = 24;
            var playerTeam = BDArmorySetup.Instance?.ActiveWeaponManager?.Team;
            var list = new List<ModuleLoiteringMunition>();
            foreach (var m in all)
                if (m != null && m.vessel != null && m.lmEnabled && m.state != LMState.Done && (playerTeam == null || !m.armed || m.Team == null || m.Team == playerTeam || m.vessel == FlightGlobals.ActiveVessel)) list.Add(m);
            if (list.Count == 0)
            {
                AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), "No loitering munitions. In the SPH/VAB, set 'Loitering munition: Yes' on its probe core.", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
                GUI.DragWindow(); return;
            }
            float listH = Mathf.Min(list.Count * 22, 132);
            scroll = GUI.BeginScrollView(new Rect(8, y, w, listH), scroll, new Rect(0, 0, w - 18, list.Count * 22));
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                string s = m.armed ? $"{m.Label}  {m.state}{(m.CanStrike ? $" > {m.targetLabel}" : "")}  {m.strikeMode}" : $"{m.Label}  SAFE (not armed)";
                if (GUI.Button(new Rect(0, i * 22, w - 20, 20), s, selected == m ? sel : btn)) selected = m;
            }
            GUI.EndScrollView();
            y += listH + 6;
            if (selected == null || !list.Contains(selected)) selected = list[0];
            var lm = selected;
            var small = AvionicsDraw.LabelSmall;
            var v = lm.vessel;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), $"{lm.Label}: alt {v.altitude:F0} m, {v.srfSpeed:F0} m/s, fuel {FuelFraction(v):P0}, {(v.CoM - (Vector3)lm.LoiterWorld).magnitude / 1000f:F1} km from loiter point", small, AvionicsDraw.HudText); y += 16;
            float bw = (w - 12) / 4;
            if (!lm.armed)
            {
                if (GUI.Button(new Rect(8, y, w, 22), "ARM / LAUNCH", btn)) lm.Arm(null);
                GUI.DragWindow(); return;
            }
            GUI.enabled = lm.state == LMState.Pending;
            if (GUI.Button(new Rect(8, y, bw, 22), "APPROVE", lm.state == LMState.Pending ? sel : btn)) lm.Approve();
            GUI.enabled = lm.state == LMState.Terminal || lm.state == LMState.Pending;
            if (GUI.Button(new Rect(12 + bw, y, bw, 22), "Wave off", btn)) lm.WaveOff();
            GUI.enabled = true;
            if (GUI.Button(new Rect(16 + 2 * bw, y, bw, 22), "Watch (cam)", btn)) Watch(lm, false);
            if (GUI.Button(new Rect(20 + 3 * bw, y, bw, 22), lm.manual ? "MANUAL" : "Take control", lm.manual ? sel : btn)) { if (lm.manual) lm.manual = false; else Watch(lm, true); }
            y += 26;
            var wm = BDArmorySetup.Instance?.ActiveWeaponManager;
            var contact = AvionicsContacts.Selected;
            float hw = (w - 4) / 2;
            GUI.enabled = contact != null;
            if (GUI.Button(new Rect(8, y, hw, 22), contact != null ? $"Strike {Clip(contact.name)}" : "Strike selected contact", btn))
                lm.Strike(contact.vessel, contact.vessel == null ? (Vector3d?)(Vector3d)contact.position : null, "operator: selected contact");
            bool gps = wm != null && wm.designatedGPSInfo.worldPos != Vector3d.zero;
            GUI.enabled = gps;
            if (GUI.Button(new Rect(12 + hw, y, hw, 22), "Strike GPS target", btn)) lm.Strike(null, wm.designatedGPSInfo.worldPos, "operator: GPS");
            y += 24;
            if (GUI.Button(new Rect(8, y, hw, 22), "Loiter at GPS target", btn)) lm.SetLoiter(wm.designatedGPSInfo.worldPos);
            GUI.enabled = true;
            if (GUI.Button(new Rect(12 + hw, y, hw, 22), "Loiter here", btn)) lm.SetLoiter(v.CoM);
            y += 24;
            int mi = Array.IndexOf(ModuleLoiteringMunition.StrikeModes, lm.strikeMode);
            if (GUI.Button(new Rect(8, y, hw, 22), $"Strike: {lm.strikeMode}", btn)) lm.strikeMode = ModuleLoiteringMunition.StrikeModes[(mi + 1) % ModuleLoiteringMunition.StrikeModes.Length];
            int ti = Array.IndexOf(ModuleLoiteringMunition.TargetTypes, lm.targetType);
            if (GUI.Button(new Rect(12 + hw, y, hw, 22), $"Targets: {lm.targetType}", btn)) lm.targetType = ModuleLoiteringMunition.TargetTypes[(ti + 1) % ModuleLoiteringMunition.TargetTypes.Length];
            y += 26;
            if (GUI.Button(new Rect(8, y, hw, 22), "Back to home craft", btn)) ReturnHome();
            if (GUI.Button(new Rect(12 + hw, y, hw, 22), "DETONATE", btn)) lm.Detonate("operator (window)");
            y += 26;
            AvionicsDraw.ColorLabel(new Rect(8, y, w, 14), $"In its camera: {AvionicsSettings.KeyLMStrike} strike crosshair / approve · {AvionicsSettings.KeyLMManual} manual · {AvionicsSettings.KeyLMDetonate} detonate · {AvionicsSettings.KeyLMReturn} home", small, AvionicsDraw.ScopeText);
            GUI.DragWindow(new Rect(0, 0, rect.width - 22, 20));
        }

        static string Clip(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 18 ? s.Substring(0, 17) + "…" : s;
    }
}
