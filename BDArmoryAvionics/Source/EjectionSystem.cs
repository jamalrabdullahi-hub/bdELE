using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Rocket ejection seats for every crewed part.
    /// Press the eject key twice (default P, P) to eject everyone from the active vessel.
    /// Optionally ejects automatically when a crewed part is about to be destroyed, or just before a high-speed ground impact.
    /// Sequence per kerbal: exit through the hatch, rocket burn away from the craft, then the EVA parachute deploys.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class EjectionSystem : MonoBehaviour
    {
        public static EjectionSystem Instance;

        // Tuning
        const float DoubleTapWindow = 0.45f;   // seconds between the two key presses
        const float InitialKick = 12f;         // m/s added at the moment of ejection
        const float RocketAccel = 14f * 9.81f; // m/s^2 during the rocket burn (about 14 g)
        const float RocketBurnTime = 0.4f;     // seconds
        const float StaggerTime = 0.25f;       // seconds between crew members

        float lastTapTime = -10f;
        readonly HashSet<ProtoCrewMember> ejecting = new();
        readonly HashSet<Part> hooked = new();
        float nextHookScan = 0;

        public static string LastMessage = "";
        public static float LastMessageTime = -100f;
        static void Message(string m)
        {
            LastMessage = m;
            LastMessageTime = Time.time;
            ScreenMessages.PostScreenMessage(m, 3f, ScreenMessageStyle.UPPER_CENTER);
        }

        void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
            AvionicsSettings.Load();
        }

        void OnDestroy()
        {
            foreach (var p in hooked) if (p != null) p.OnJustAboutToDie -= OnCrewedPartDying(p);
            hooked.Clear();
            dyingHandlers.Clear();
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready) return;
            if (AvionicsSettings.KeyEject == KeyCode.None || GUIUtility.keyboardControl != 0) return;
            if (!Input.GetKeyDown(AvionicsSettings.KeyEject)) return;

            if (Time.time - lastTapTime <= DoubleTapWindow)
            {
                lastTapTime = -10f;
                EjectVessel(FlightGlobals.ActiveVessel, "Ejecting!");
            }
            else
            {
                lastTapTime = Time.time;
                var v = FlightGlobals.ActiveVessel;
                if (v != null && !v.isEVA && v.GetCrewCount() > 0)
                    ScreenMessages.PostScreenMessage($"Press {AvionicsSettings.KeyEject} again to eject", DoubleTapWindow + 0.3f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready) return;

            // Keep the "about to die" hooks on every crewed part of every loaded vessel.
            if (Time.time >= nextHookScan)
            {
                nextHookScan = Time.time + 1f;
                RefreshHooks();
            }
        }

        // ---- Auto-eject when a crewed part is about to be destroyed ----

        readonly Dictionary<Part, Callback> dyingHandlers = new();
        Callback OnCrewedPartDying(Part p)
        {
            if (!dyingHandlers.TryGetValue(p, out var cb))
            {
                cb = () =>
                {
                    if (!AvionicsSettings.AutoEjectOnDestruction || p == null || p.vessel == null) return;
                    if (p.protoModuleCrew.Count == 0) return;
                    EjectFromPart(p, $"{p.vessel.GetDisplayName()}: {p.partInfo.title} destroyed, ejecting!");
                };
                dyingHandlers[p] = cb;
            }
            return cb;
        }

        void RefreshHooks()
        {
            hooked.RemoveWhere(p => p == null);
            var vessels = FlightGlobals.VesselsLoaded;
            for (int i = 0; i < vessels.Count; i++)
            {
                var v = vessels[i];
                if (v == null || v.isEVA) continue;
                foreach (var p in v.parts)
                {
                    if (p == null || p.CrewCapacity == 0 || hooked.Contains(p)) continue;
                    p.OnJustAboutToDie += OnCrewedPartDying(p);
                    hooked.Add(p);
                }
            }
        }

        // ---- Ejection ----

        public void EjectVessel(Vessel v, string message)
        {
            if (v == null || v.isEVA) return;
            var parts = new List<Part>();
            foreach (var p in v.parts)
                if (p != null && p.protoModuleCrew.Count > 0) parts.Add(p);
            if (parts.Count == 0)
            {
                if (v == FlightGlobals.ActiveVessel) Message("No crew to eject");
                return;
            }
            // Command seats: kerbals already on EVA sitting in an external seat
            foreach (var seat in v.FindPartModulesImplementing<KerbalSeat>())
                if (seat.Occupant != null) StartCoroutine(EjectFromSeat(seat));

            Message(message);
            bool focusFirst = v == FlightGlobals.ActiveVessel;
            float delay = 0;
            foreach (var p in parts)
            {
                foreach (var crew in new List<ProtoCrewMember>(p.protoModuleCrew))
                {
                    StartCoroutine(EjectCrew(crew, p, delay, focusFirst));
                    focusFirst = false;
                    delay += StaggerTime;
                }
            }
        }

        public void EjectFromPart(Part p, string message)
        {
            if (p == null) return;
            Message(message);
            float delay = 0;
            // The part is about to die, so eject the first kerbal immediately and the rest as fast as possible.
            foreach (var crew in new List<ProtoCrewMember>(p.protoModuleCrew))
            {
                StartCoroutine(EjectCrew(crew, p, delay, p.vessel == FlightGlobals.ActiveVessel && delay == 0));
                delay += 0.05f;
            }
        }

        IEnumerator EjectCrew(ProtoCrewMember crew, Part fromPart, float delay, bool focus)
        {
            if (crew == null || ejecting.Contains(crew)) yield break;
            ejecting.Add(crew);
            try
            {
                if (delay > 0) yield return new WaitForSeconds(delay);
                if (fromPart == null || fromPart.vessel == null || !fromPart.protoModuleCrew.Contains(crew)) yield break;
                if (FlightEVA.hatchInsideFairing(fromPart))
                {
                    Message($"{crew.name} can't eject: the hatch is inside a fairing");
                    yield break;
                }

                Vector3 dir = EjectDirection(fromPart);
                Vector3 craftVelocity = fromPart.vessel.Velocity();
                Vector3 startPos = fromPart.transform.position + dir * EjectClearance(fromPart);
                Vessel previousActive = FlightGlobals.ActiveVessel;
                Vessel fromVessel = fromPart.vessel;
                var ejectTeam = fromVessel.ActiveController().WM?.Team; // read now: an auto-ejecting cockpit may be gone in a moment
                string ejectFrom = fromVessel.GetName();

                KerbalEVA eva = FlightEVA.fetch.spawnEVA(crew, fromPart, fromPart.airlock, true);
                if (eva == null || eva.vessel == null)
                {
                    Message($"{crew.name} can't eject: every hatch is blocked");
                    yield break;
                }

                // Camera: follow the first kerbal out of the player's vessel, otherwise keep the current vessel.
                if (!focus && previousActive != null && FlightGlobals.ActiveVessel != previousActive && previousActive.loaded)
                    FlightGlobals.ForceSetActiveVessel(previousActive);

                if (crew.KerbalRef != null) crew.KerbalRef.state = Kerbal.States.BAILED_OUT;
                if (fromVessel != null && fromVessel.GetVesselCrew().Contains(crew)) fromVessel.RemoveCrew(crew);
                eva.autoGrabLadderOnStart = false;
                eva.StartNonCollidePeriod(5f, 1f, fromPart, fromPart.airlock);
                EnsureParachute(eva);
                try { CSARScenario.ReportEjection(eva.vessel, crew, ejectTeam, ejectFrom); } catch (System.Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: CSAR report failed: {e.Message}"); }

                // KSP puts a kerbal leaving a hatch onto the nearest ladder/rail, where forces barely move it.
                // Step 1 (what pressing F does): let go. KSP ignores "let go" until the grab has finished,
                // so keep asking until the kerbal is actually free, and only then fire the seat.
                var wait = new WaitForFixedUpdate();
                yield return wait;
                if (eva == null || eva.vessel == null) yield break;
                string grabbed = StateName(eva);
                bool wasJointed = AnchorJoint(eva) != null || eva.IsLadderJointed;
                float releaseStart = Time.time;
                while (eva != null && eva.vessel != null && OnLadder(eva) && Time.time - releaseStart < MaxReleaseWait)
                {
                    LetGo(eva);
                    yield return wait;
                }
                if (eva == null || eva.vessel == null) yield break;
                Debug.Log($"[BDArmoryAvionics]: Eject {crew.name}: spawned in '{grabbed}'{(wasJointed ? " (jointed to the moving craft)" : "")}, {(OnLadder(eva) ? "STILL HOLDING" : "free")} after {Time.time - releaseStart:F2} s, now '{StateName(eva)}'");

                // Step 2: place the kerbal clear of the canopy, moving with the craft, then fire the seat rocket.
                if (fromPart != null && fromPart.vessel != null)
                {
                    dir = EjectDirection(fromPart);
                    craftVelocity = fromPart.vessel.Velocity();
                    startPos = fromPart.transform.position + dir * EjectClearance(fromPart);
                }
                else startPos = eva.vessel.CoM + dir * 1.5f; // craft already gone (auto-eject on destruction)
                eva.vessel.SetPosition(startPos);
                eva.vessel.SetVelocity(craftVelocity + dir * InitialKick);
                yield return StartCoroutine(RocketBurn(eva, dir));
                yield return StartCoroutine(DeployChute(eva));
            }
            finally
            {
                ejecting.Remove(crew);
            }
        }

        IEnumerator EjectFromSeat(KerbalSeat seat)
        {
            var occupant = seat.Occupant;
            var eva = occupant != null ? occupant.GetComponent<KerbalEVA>() : null;
            if (eva == null) yield break;
            Vector3 dir = EjectDirection(seat.part);
            seat.LeaveSeat(new KSPActionParam(KSPActionGroup.Abort, KSPActionType.Activate));
            yield return new WaitForFixedUpdate();
            if (eva == null || eva.vessel == null) yield break;
            LetGo(eva);
            EnsureParachute(eva);
            eva.vessel.SetVelocity((Vector3)eva.vessel.Velocity() + dir * InitialKick);
            yield return StartCoroutine(RocketBurn(eva, dir));
            yield return StartCoroutine(DeployChute(eva));
        }

        /// <summary>
        /// Out through the top of the canopy. KSP aircraft cockpits have their top along -transform.forward;
        /// for pods with a side hatch the airlock direction is used instead.
        /// </summary>
        static Vector3 EjectDirection(Part p)
        {
            Vector3 up = -p.transform.forward;
            if (p.airlock != null)
            {
                Vector3 toHatch = Vector3.ProjectOnPlane(p.airlock.position - p.transform.position, p.transform.up);
                // Use the hatch side only for pods whose hatch isn't roughly on top already
                if (toHatch.sqrMagnitude > 0.04f && Vector3.Angle(toHatch, up) > 60f) up = toHatch.normalized;
            }
            return up.normalized;
        }

        static float EjectClearance(Part p)
        {
            float r = 0;
            foreach (var c in p.GetComponentsInChildren<Collider>())
                if (c != null && c.enabled && !c.isTrigger) r = Mathf.Max(r, c.bounds.extents.magnitude);
            return Mathf.Clamp(r * 0.6f, 0.8f, 3f);
        }

        IEnumerator RocketBurn(KerbalEVA eva, Vector3 dir)
        {
            GameObject fx = StartRocketFX(eva, dir);
            float t = 0;
            var wait = new WaitForFixedUpdate();
            while (t < RocketBurnTime && eva != null && eva.part != null && eva.part.Rigidbody != null)
            {
                LetGo(eva); // the kerbal passes ladders and rails on the way out; don't let it grab one
                eva.part.Rigidbody.AddForce(dir * RocketAccel, ForceMode.Acceleration);
                t += Time.fixedDeltaTime;
                yield return wait;
            }
            StopRocketFX(fx);
            // Keep it from grabbing anything while it coasts clear of the craft.
            for (float c = 0; c < NoGrabAfterBurn && eva != null && eva.vessel != null; c += Time.fixedDeltaTime)
            {
                LetGo(eva);
                yield return wait;
            }
        }

        const float NoGrabAfterBurn = 1.5f; // seconds
        const float MaxReleaseWait = 1.5f;  // seconds to wait for the kerbal to let go before firing anyway

        static string StateName(KerbalEVA eva) => eva != null && eva.fsm != null && eva.fsm.CurrentState != null ? eva.fsm.CurrentState.name : "?";

        static readonly System.Reflection.FieldInfo onLadderField = HarmonyLib.AccessTools.Field(typeof(KerbalEVA), "onLadder"); // private in KSP
        static readonly System.Reflection.FieldInfo ladderTriggersField = HarmonyLib.AccessTools.Field(typeof(KerbalEVA), "currentLadderTriggers"); // private in KSP

        // When the craft is moving, KSP physically joints a kerbal on a ladder to it (anchorJoint) so it isn't left behind.
        // Static craft have no joint, which is why ejecting on the ground was fine and ejecting in flight wasn't.
        static readonly System.Reflection.FieldInfo anchorJointField = HarmonyLib.AccessTools.Field(typeof(KerbalEVA), "anchorJoint");
        static readonly System.Reflection.FieldInfo ladderJointedField = HarmonyLib.AccessTools.Field(typeof(KerbalEVA), "isLadderJointed");
        static readonly System.Reflection.FieldInfo currentLadderField = HarmonyLib.AccessTools.Field(typeof(KerbalEVA), "currentLadder");
        static readonly System.Reflection.PropertyInfo currentLadderPartProp = HarmonyLib.AccessTools.Property(typeof(KerbalEVA), "currentLadderPart");

        static FixedJoint AnchorJoint(KerbalEVA eva) => anchorJointField?.GetValue(eva) as FixedJoint;

        static bool OnLadder(KerbalEVA eva)
        {
            if (onLadderField != null && onLadderField.GetValue(eva) is bool b && b) return true;
            if (eva.IsLadderJointed || AnchorJoint(eva) != null) return true;
            var state = eva.fsm != null ? eva.fsm.CurrentState : null;
            return state != null && state.name != null && state.name.StartsWith("Ladder");
        }

        /// <summary>
        /// Release any ladder or rail the kerbal is holding: let go (F), else push off (Space); then break the
        /// joint that holds it to a moving craft, and as a last resort put it straight into the floating state.
        /// </summary>
        static void LetGo(KerbalEVA eva)
        {
            if (eva == null || eva.fsm == null) return;
            (ladderTriggersField?.GetValue(eva) as List<Collider>)?.Clear(); // forget nearby ladders so it can't grab (or re-grab) one
            if (!OnLadder(eva)) return;
            try
            {
                // A grab that's still in progress can't be let go of; finish it first.
                if (eva.fsm.CurrentState == eva.st_ladder_acquire) eva.fsm.RunEvent(eva.On_ladderGrabComplete);
                eva.fsm.RunEvent(eva.On_ladderLetGo);
                if (OnLadder(eva)) eva.fsm.RunEvent(eva.On_LadderPushOff);

                // Moving craft: the ladder joint can outlive the ladder state. Remove it.
                var joint = AnchorJoint(eva);
                if (joint != null)
                {
                    DestroyImmediate(joint);
                    anchorJointField.SetValue(eva, null);
                }
                ladderJointedField?.SetValue(eva, false);
                onLadderField?.SetValue(eva, false);
                currentLadderField?.SetValue(eva, null);
                if (currentLadderPartProp != null && currentLadderPartProp.CanWrite) currentLadderPartProp.SetValue(eva, null);

                var state = eva.fsm.CurrentState;
                if (state != null && state.name != null && state.name.StartsWith("Ladder"))
                    eva.fsm.StartFSM(eva.st_idle_fl); // last resort: floating, hands free
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BDArmoryAvionics]: Couldn't release the ladder: {e.Message}");
            }
        }

        static GameObject StartRocketFX(KerbalEVA eva, Vector3 dir)
        {
            if (eva == null) return null;
            GameObject fx = null;
            try
            {
                fx = GameDatabase.Instance.GetModel("BDArmory/Models/exhaust/smallExhaust");
                if (fx != null)
                {
                    fx.SetActive(true);
                    fx.transform.SetParent(eva.transform, false);
                    fx.transform.localPosition = Vector3.zero;
                    fx.transform.rotation = Quaternion.LookRotation(-dir); // exhaust points away from the direction of travel
                    foreach (var e in fx.GetComponentsInChildren<KSPParticleEmitter>(true)) e.emit = true;
                }
                var clip = SoundUtils.GetAudioClip("BDArmory/Sounds/launch");
                if (clip != null)
                {
                    var audio = eva.gameObject.AddComponent<AudioSource>();
                    audio.spatialBlend = 1;
                    audio.minDistance = 5;
                    audio.maxDistance = 1000;
                    audio.PlayOneShot(clip, GameSettings.SHIP_VOLUME);
                    Destroy(audio, clip.length + 0.5f);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BDArmory.EjectionSystem]: Ejection FX failed: {e.Message}");
            }
            return fx;
        }

        static void StopRocketFX(GameObject fx)
        {
            if (fx == null) return;
            foreach (var e in fx.GetComponentsInChildren<KSPParticleEmitter>(true)) e.emit = false;
            fx.transform.SetParent(null, true); // leave the smoke behind to fade out
            Destroy(fx, 4f);
        }

        static void EnsureParachute(KerbalEVA eva)
        {
            var inv = eva.ModuleInventoryPartReference;
            if (inv != null && !inv.ContainsPart("evaChute"))
            {
                inv.SetInventoryDefaults(); // KSP's default EVA kit includes the parachute
                eva.part.UpdateMass();
            }
        }

        /// <summary>Deploy the chute once the seat rocket has burned out and the kerbal is near the top of the arc.</summary>
        IEnumerator DeployChute(KerbalEVA eva)
        {
            float timeout = Time.time + 2.5f;
            var wait = new WaitForFixedUpdate();
            while (eva != null && eva.vessel != null && Time.time < timeout && eva.vessel.verticalSpeed > 2) yield return wait;
            if (eva == null || eva.vessel == null || eva.IsSeated() || eva.vessel.LandedOrSplashed) yield break;
            var chute = VesselModuleRegistry.GetModule<ModuleEvaChute>(eva.vessel);
            if (chute == null)
            {
                Message($"{eva.vessel.GetDisplayName()} has no parachute");
                yield break;
            }
            eva.vessel.altimeterDisplayState = AltimeterDisplayState.AGL;
            if (chute.deploymentState != ModuleParachute.deploymentStates.SEMIDEPLOYED)
                chute.deploymentState = ModuleParachute.deploymentStates.STOWED;
            // Below 300 m the chute opens fully straight away; higher up it stays semi-deployed until 300 m.
            chute.deployAltitude = 300f;
            chute.Deploy();
        }
    }
}
