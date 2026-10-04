using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>Kerbal Attachment System (KAS) winches and cable links, by reflection (ELE works without KAS).</summary>
    public static class KASBridge
    {
        static bool resolved;
        static Type winchI, sourceI, targetI, peerI, actorT;
        static MethodInfo setMotor, releaseCable, linkTo, breakLink;
        static PropertyInfo cableLen, maxLen, motorMax, linkTarget, linkType, linkState, isLinked, peerPart, otherPeer;
        static object actorAPI;

        public static bool Available { get { Resolve(); return winchI != null && linkTo != null; } }

        static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            try
            {
                foreach (var a in AssemblyLoader.loadedAssemblies)
                {
                    if (a.name != "KAS-API-v2") continue;
                    winchI = a.assembly.GetType("KASAPIv2.IWinchControl");
                    sourceI = a.assembly.GetType("KASAPIv2.ILinkSource");
                    targetI = a.assembly.GetType("KASAPIv2.ILinkTarget");
                    peerI = a.assembly.GetType("KASAPIv2.ILinkPeer");
                    actorT = a.assembly.GetType("KASAPIv2.LinkActorType");
                }
                if (winchI == null || sourceI == null || targetI == null || peerI == null || actorT == null) { winchI = null; return; }
                setMotor = winchI.GetMethod("SetMotor");
                releaseCable = winchI.GetMethod("ReleaseCable");
                cableLen = winchI.GetProperty("currentCableLength");
                maxLen = winchI.GetProperty("cfgMaxCableLength");
                motorMax = winchI.GetProperty("cfgMotorMaxSpeed");
                linkTo = sourceI.GetMethod("LinkToTarget", new[] { actorT, targetI });
                breakLink = sourceI.GetMethod("BreakCurrentLink", new[] { actorT });
                linkTarget = sourceI.GetProperty("linkTarget");
                linkType = peerI.GetProperty("cfgLinkType");
                linkState = peerI.GetProperty("linkState");
                isLinked = peerI.GetProperty("isLinked");
                peerPart = peerI.GetProperty("part");
                otherPeer = peerI.GetProperty("otherPeer");
                actorAPI = Enum.Parse(actorT, "API");
                Debug.Log($"[BDArmoryAvionics]: KAS found, winch operations {(Available ? "available" : "unavailable (API differs)")}");
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: KAS bridge failed: {e.Message}"); winchI = null; }
        }

        public static List<PartModule> Winches(Vessel v)
        {
            var list = new List<PartModule>();
            Resolve();
            if (v == null || winchI == null) return list;
            foreach (var p in v.parts) foreach (PartModule m in p.Modules) if (winchI.IsInstanceOfType(m)) list.Add(m);
            return list;
        }

        public static string LinkType(PartModule peer) => linkType?.GetValue(peer) as string ?? "";
        public static float Length(PartModule w) => cableLen != null ? (float)cableLen.GetValue(w) : 0;
        public static float MaxLength(PartModule w) => maxLen != null ? (float)maxLen.GetValue(w) : 50;
        public static float MotorMax(PartModule w) => motorMax != null ? (float)motorMax.GetValue(w) : 2;
        public static void Motor(PartModule w, float speed) { try { setMotor?.Invoke(w, new object[] { speed }); } catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: KAS SetMotor: {e.InnerException?.Message ?? e.Message}"); } }
        public static void Release(PartModule w) { try { releaseCable?.Invoke(w, null); } catch { } }
        public static bool IsLinked(PartModule peer) { try { return isLinked != null && (bool)isLinked.GetValue(peer); } catch { return false; } }
        public static string State(PartModule peer) { try { return linkState?.GetValue(peer)?.ToString() ?? "?"; } catch { return "?"; } }

        /// <summary>The vessel at the other end of the winch's cable, if any.</summary>
        public static Vessel LinkedVessel(PartModule winch)
        {
            try
            {
                var t = linkTarget?.GetValue(winch);
                if (t == null) return null;
                return (peerPart?.GetValue(t) as Part)?.vessel;
            }
            catch { return null; }
        }

        /// <summary>Cable link points on a vessel that accept this winch's cable type (kerbals have one for every cable).</summary>
        public static List<PartModule> Targets(Vessel v, string type)
        {
            var list = new List<PartModule>();
            Resolve();
            if (v == null || targetI == null) return list;
            foreach (var p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (targetI.IsInstanceOfType(m) && LinkType(m) == type && !IsLinked(m)) list.Add(m);
            return list;
        }

        public static bool Link(PartModule winch, PartModule target, out string why)
        {
            why = "";
            try
            {
                bool ok = (bool)linkTo.Invoke(winch, new[] { actorAPI, target });
                if (!ok) why = $"KAS refused the link (winch {State(winch)}, target {State(target)})";
                return ok;
            }
            catch (Exception e) { why = e.InnerException?.Message ?? e.Message; return false; }
        }

        public static void Unlink(PartModule winch) { try { breakLink?.Invoke(winch, new[] { actorAPI }); } catch { } }
    }

    public enum LiftKind { Rescue, Sling }

    /// <summary>
    /// Winch operations with the VTOL flight computer: rescue hoist (survivors are reeled up and climb aboard) and sling loads
    /// (hook cargo, lift, fly, lower, release). Cargo set down at one of your faction's bases is used up as supplies.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class KASOps : MonoBehaviour
    {
        public class Op
        {
            public LiftKind kind;
            public Vessel heli, target;
            public PartModule winch;
            public string phase = "approach", survivor;
            public float phaseT0;
            public double deliverLat, deliverLon; public bool haveDelivery;
            public void Phase(string p) { phase = p; phaseT0 = Time.time; Debug.Log($"[BDArmoryAvionics]: Lift {heli?.GetName()} {kind}: {p}"); }
        }

        public static readonly Dictionary<Vessel, Op> Ops = new();
        public static KASOps Instance;
        const float HoistAGL = 15f, SlingLength = 10f;

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        static PartModule BestWinch(Vessel v)
        {
            var ws = KASBridge.Winches(v);
            PartModule best = null; float bestFree = -1;
            foreach (var w in ws) { if (KASBridge.IsLinked(w) && KASBridge.LinkedVessel(w) != null) continue; float l = KASBridge.MaxLength(w); if (l > bestFree) { bestFree = l; best = w; } }
            return best ?? (ws.Count > 0 ? ws[0] : null);
        }

        public static bool CanHoist(Vessel v) => KASBridge.Available && ModuleVTOLAssist.For(v) != null && KASBridge.Winches(v).Count > 0;

        static void Msg(string s) => ScreenMessages.PostScreenMessage(s, 4f, ScreenMessageStyle.UPPER_CENTER);

        /// <summary>Hoist a survivor (CSAR): hover over them, lower the cable, hook them, reel them up, they climb aboard.</summary>
        public static bool Rescue(Vessel heli, Vessel eva, string kerbal)
        {
            if (!CanHoist(heli) || eva == null) return false;
            var w = BestWinch(heli);
            Ops[heli] = new Op { kind = LiftKind.Rescue, heli = heli, target = eva, winch = w, survivor = kerbal, phaseT0 = Time.time };
            var b = eva.mainBody;
            ModuleVTOLAssist.For(heli).GoTo(b.GetLatitude(eva.CoM), b.GetLongitude(eva.CoM), HoistAGL, false);
            Msg($"{heli.GetName()}: hoist rescue of {kerbal}");
            return true;
        }

        /// <summary>Hook a cargo craft (it needs a KAS cable point the winch's cable fits) and lift it.</summary>
        public static bool Sling(Vessel heli, Vessel cargo)
        {
            if (!CanHoist(heli) || cargo == null || cargo == heli) return false;
            var w = BestWinch(heli);
            if (KASBridge.Targets(cargo, KASBridge.LinkType(w)).Count == 0) { Msg($"{cargo.GetName()} has no free {KASBridge.LinkType(w)} attachment point"); return false; }
            Ops[heli] = new Op { kind = LiftKind.Sling, heli = heli, target = cargo, winch = w, phaseT0 = Time.time };
            var b = cargo.mainBody;
            ModuleVTOLAssist.For(heli).GoTo(b.GetLatitude(cargo.CoM), b.GetLongitude(cargo.CoM), CargoHeight(cargo) + SlingLength + 6, false);
            Msg($"{heli.GetName()}: picking up {cargo.GetName()}");
            return true;
        }

        public static void Deliver(Vessel heli, double lat, double lon)
        {
            if (!Ops.TryGetValue(heli, out var op) || op.kind != LiftKind.Sling) { Msg("Hook a load first"); return; }
            op.deliverLat = lat; op.deliverLon = lon; op.haveDelivery = true;
            if (op.phase == "carrying") StartCarry(op);
            Msg($"{heli.GetName()}: delivering {op.target?.GetName()}");
        }

        public static void Cancel(Vessel heli, string why)
        {
            if (!Ops.TryGetValue(heli, out var op)) return;
            if (op.winch != null) KASBridge.Motor(op.winch, 0);
            Ops.Remove(heli);
            Debug.Log($"[BDArmoryAvionics]: Lift {heli.GetName()} cancelled: {why}");
        }

        static float CargoHeight(Vessel c) => Mathf.Max(2f, c.GetRadius() * 1.5f);

        static void StartCarry(Op op)
        {
            var fc = ModuleVTOLAssist.For(op.heli);
            fc?.GoTo(op.deliverLat, op.deliverLon, CargoHeight(op.target) + SlingLength + 15, false);
            op.Phase("carrying");
        }

        void FixedUpdate()
        {
            if (Ops.Count == 0) return;
            foreach (var op in new List<Op>(Ops.Values))
            {
                try { Step(op); } catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: Lift step failed: {e}"); Cancel(op.heli, "error"); }
            }
        }

        void Step(Op op)
        {
            var h = op.heli;
            if (h == null || !h.loaded || op.winch == null) { Ops.Remove(h); return; }
            var fc = ModuleVTOLAssist.For(h);
            float maxSpd = KASBridge.MotorMax(op.winch);
            Vector3 winchPos = op.winch.part.transform.position;
            float t = Time.time - op.phaseT0;

            if (op.target == null || !op.target.loaded)
            {
                // Survivor boarded (their EVA vessel is gone): reel in and finish.
                if (op.kind == LiftKind.Rescue && op.phase == "hoisting") { KASBridge.Motor(op.winch, -maxSpd); Msg($"{op.survivor} is aboard {h.GetName()}"); }
                Ops.Remove(h);
                return;
            }
            Vector3 tPos = op.target.CoM;
            Vector3 up = (h.CoM - h.mainBody.position).normalized;
            float horiz = Vector3.ProjectOnPlane(tPos - (Vector3)h.CoM, up).magnitude;
            float dist = (tPos - winchPos).magnitude;

            switch (op.phase)
            {
                case "approach":
                    if (op.kind == LiftKind.Rescue && !op.target.LandedOrSplashed && t < 60) return; // still under the chute
                    if (horiz < 6f && h.srfSpeed < 2.5f && !h.LandedOrSplashed) { op.Phase("lowering"); KASBridge.Motor(op.winch, maxSpd); }
                    else if (t > 180) Fallback(op, "couldn't get into position");
                    return;
                case "lowering":
                    {
                        KASBridge.Motor(op.winch, maxSpd);
                        if (KASBridge.Length(op.winch) >= Mathf.Min(dist - 2f, KASBridge.MaxLength(op.winch) - 1f) || t > 25)
                        {
                            var targets = KASBridge.Targets(op.target, KASBridge.LinkType(op.winch));
                            string why = "no attachment point";
                            foreach (var tg in targets) if (KASBridge.Link(op.winch, tg, out why)) { why = null; break; }
                            if (why == null) { op.Phase(op.kind == LiftKind.Rescue ? "hoisting" : "reeling"); return; }
                            if (dist > KASBridge.MaxLength(op.winch)) { Fallback(op, $"cable too short ({KASBridge.MaxLength(op.winch):F0} m)"); return; }
                            if (t > 30) Fallback(op, why);
                        }
                        return;
                    }
                case "hoisting": // rescue: reel the kerbal up, then they climb aboard
                    {
                        KASBridge.Motor(op.winch, -maxSpd);
                        if (dist < 4f || KASBridge.Length(op.winch) < 1.5f)
                        {
                            KASBridge.Unlink(op.winch);
                            Board(op);
                        }
                        else if (t > 60) Fallback(op, "hoist stalled");
                        return;
                    }
                case "reeling": // sling: shorten to the carry length, then climb away
                    {
                        float len = KASBridge.Length(op.winch);
                        KASBridge.Motor(op.winch, len > SlingLength ? -maxSpd : 0);
                        if (len <= SlingLength + 0.5f)
                        {
                            KASBridge.Motor(op.winch, 0);
                            if (fc != null) fc.holdAlt = h.altitude + 25; // lift
                            if (op.haveDelivery) StartCarry(op); else { op.Phase("carrying"); fc?.Engage(VTOLMode.Hover); Msg($"{h.GetName()}: load lifted - pick a drop point (Deliver ▸)"); }
                        }
                        return;
                    }
                case "carrying":
                    if (!op.haveDelivery) return;
                    var dropW = h.mainBody.GetWorldSurfacePosition(op.deliverLat, op.deliverLon, 0);
                    if (Vector3.ProjectOnPlane((Vector3)(dropW - h.CoMD), up).magnitude < 12f && h.srfSpeed < 3f) { op.Phase("setting down"); KASBridge.Motor(op.winch, maxSpd * 0.5f); }
                    return;
                case "setting down":
                    KASBridge.Motor(op.winch, maxSpd * 0.5f);
                    if (op.target.LandedOrSplashed && t > 2f)
                    {
                        KASBridge.Unlink(op.winch);
                        KASBridge.Motor(op.winch, -maxSpd);
                        Delivered(op);
                        if (fc != null) fc.holdAlt = h.altitude + 20;
                        Ops.Remove(h);
                    }
                    else if (t > 40) { KASBridge.Unlink(op.winch); Ops.Remove(h); Msg("Load released (didn't touch down)"); }
                    return;
            }
        }

        static void Board(Op op)
        {
            var h = op.heli;
            Part seat = null;
            foreach (var p in h.parts) if (p.CrewCapacity > p.protoModuleCrew.Count) { seat = p; break; }
            var k = op.target.GetComponent<KerbalEVA>() ?? op.target.rootPart?.FindModuleImplementing<KerbalEVA>();
            if (seat == null || k == null) { Msg($"{h.GetName()}: no free seat - {op.survivor} stays on the cable"); op.Phase("full"); return; }
            try { k.BoardPart(seat); op.Phase("hoisting"); Debug.Log($"[BDArmoryAvionics]: Hoist: {op.survivor} boarded {h.GetName()}"); }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: Hoist board failed: {e.Message}"); }
        }

        /// <summary>Hoist didn't work: fall back to landing beside the survivor (or just release the load).</summary>
        static void Fallback(Op op, string why)
        {
            Debug.Log($"[BDArmoryAvionics]: Lift {op.heli.GetName()}: {why}");
            KASBridge.Unlink(op.winch); KASBridge.Motor(op.winch, -KASBridge.MotorMax(op.winch));
            Ops.Remove(op.heli);
            if (op.kind == LiftKind.Rescue)
            {
                var b = op.target.mainBody;
                ModuleVTOLAssist.For(op.heli)?.GoTo(b.GetLatitude(op.target.CoM), b.GetLongitude(op.target.CoM), 30, true);
                Msg($"{op.heli.GetName()}: hoist failed ({why}) - landing beside {op.survivor} instead");
            }
            else Msg($"{op.heli.GetName()}: pickup failed ({why})");
        }

        /// <summary>Cargo set down at one of your bases: used up as supplies (fuel, ordnance, repairs by mass).</summary>
        static void Delivered(Op op)
        {
            var cargo = op.target;
            var team = op.heli.ActiveController().WM?.Team?.Name;
            Base best = null; double bestD = 600;
            if (FactionsScenario.Instance != null)
                foreach (var b in FactionsScenario.Instance.bases)
                {
                    var f = FactionsScenario.FactionByName(b.faction);
                    if (f == null || f.team != team) continue;
                    double d = (cargo.mainBody.GetWorldSurfacePosition(b.lat, b.lon, b.alt) - cargo.CoMD).magnitude;
                    if (d < bestD) { bestD = d; best = b; }
                }
            if (best == null) { Msg($"{cargo.GetName()} set down"); return; }
            float t = (float)cargo.totalMass;
            best.fuel = Mathf.Min(best.fuelCap, best.fuel + t * 1500f);
            best.ordnance = Mathf.Min(best.ordnanceCap, best.ordnance + t * 40f);
            best.health = Mathf.Min(100f, best.health + t * 4f);
            BaseLogistics.UpdateStatus(best);
            string name = cargo.GetName();
            Msg($"{name} delivered to {BaseLogistics.Name(best)}: +{t * 1500f:F0} fuel, +{t * 40f:F0} ordnance, +{t * 4f:F0}% repairs");
            AICommander.Log($"Sling load {name} ({t:F1} t) delivered to {BaseLogistics.Name(best)}");
            if (cargo != FlightGlobals.ActiveVessel) cargo.Die(); // unloaded into the base's stores
        }

        // ---- Flight panel (active craft with a winch and the VTOL computer) ----
        static int click; // 1 hook cargo, 2 deliver

        void Update()
        {
            var v = FlightGlobals.ActiveVessel;
            if (click == 0 || v == null || !Input.GetMouseButtonDown(0)) return;
            Vector2 gm = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (PanelRect.Contains(gm)) return;
            var ray = FlightCamera.fetch.mainCamera.ScreenPointToRay(Input.mousePosition);
            if (click == 1)
            {
                foreach (var hit in Physics.RaycastAll(ray, 20000f, 1 << 0))
                {
                    var p = hit.collider.GetComponentInParent<Part>();
                    if (p != null && p.vessel != null && p.vessel != v) { Sling(v, p.vessel); break; }
                }
            }
            else if (Physics.Raycast(ray, out var g, 100000f, (1 << 15) | (1 << 28)))
                Deliver(v, v.mainBody.GetLatitude(g.point), v.mainBody.GetLongitude(g.point));
            click = 0;
        }

        static Rect PanelRect => new(Screen.width / 2f - 300, Screen.height - 280, 600, 46);

        void OnGUI()
        {
            var v = FlightGlobals.ActiveVessel;
            if (v == null || !BDArmorySetup.GAME_UI_ENABLED || MapView.MapIsEnabled || !KASBridge.Available) return;
            var ws = KASBridge.Winches(v);
            if (ws.Count == 0 || ModuleVTOLAssist.For(v) == null) return;
            var r = PanelRect;
            GUI.Box(r, "", BDArmorySetup.BDGuiSkin.box);
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            Ops.TryGetValue(v, out var op);
            float bw = (r.width - 8) / 6;
            float x = r.x + 4;
            if (GUI.Button(new Rect(x, r.y + 4, bw - 3, 20), "Rescue nearest", op?.kind == LiftKind.Rescue ? sel : btn)) RescueNearest(v); x += bw;
            if (GUI.Button(new Rect(x, r.y + 4, bw - 3, 20), "Hook load ▸", click == 1 ? sel : btn)) click = click == 1 ? 0 : 1; x += bw;
            if (GUI.Button(new Rect(x, r.y + 4, bw - 3, 20), "Deliver ▸", click == 2 ? sel : btn)) click = click == 2 ? 0 : 2; x += bw;
            var w = op?.winch ?? ws[0];
            if (GUI.RepeatButton(new Rect(x, r.y + 4, bw - 3, 20), "Winch ▼", btn)) KASBridge.Motor(w, KASBridge.MotorMax(w)); x += bw;
            if (GUI.RepeatButton(new Rect(x, r.y + 4, bw - 3, 20), "Winch ▲", btn)) KASBridge.Motor(w, -KASBridge.MotorMax(w)); x += bw;
            if (GUI.Button(new Rect(x, r.y + 4, bw - 3, 20), "Release", btn)) { KASBridge.Unlink(w); KASBridge.Motor(w, 0); Cancel(v, "released"); }
            if (Event.current.type == EventType.MouseUp && op == null) KASBridge.Motor(w, 0);
            var linked = KASBridge.LinkedVessel(w);
            string line = $"Winch: cable {KASBridge.Length(w):F1}/{KASBridge.MaxLength(w):F0} m" + (linked != null ? $" · on {linked.GetName()} ({linked.totalMass:F1} t)" : "") +
                          (op != null ? $" · {op.kind}: {op.phase}" : "") + (click == 1 ? " · click the load" : click == 2 ? " · click the drop point" : "");
            AvionicsDraw.ShadowLabel(new Rect(r.x, r.y + 27, r.width, 16), line, AvionicsDraw.LabelCenter, op != null ? AvionicsDraw.Friendly : AvionicsDraw.HudText);
        }

        static void RescueNearest(Vessel v)
        {
            var team = v.ActiveController().WM?.Team;
            Vessel best = null; string name = null; double bestD = 5000;
            if (CSARScenario.Instance != null)
                foreach (var s in CSARScenario.Instance.survivors)
                {
                    if (s.status != SurvivorStatus.Down || (team != null && s.team != team.Name)) continue;
                    foreach (var e in FlightGlobals.VesselsLoaded)
                        if (e.isEVA && e.persistentId == s.evaId) { double d = (e.CoMD - v.CoMD).magnitude; if (d < bestD) { bestD = d; best = e; name = s.kerbal; } }
                }
            if (best == null) { Msg("No downed friendly pilot within 5 km"); return; }
            Rescue(v, best, name);
        }
    }
}
