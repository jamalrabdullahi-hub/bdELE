using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Weapons;
using BDArmory.CounterMeasure;

namespace BDArmory.Avionics
{
    /// <summary>
    /// ELE pages for RasterPropMonitor screens (ASET ALCOR MFD). RPM finds this module by name, so ELE has no hard
    /// dependency on RPM. Each PAGE gets a PAGEHANDLER (text, buttons) and optionally a BACKGROUNDHANDLER (graphics),
    /// both with "page = CAM|WPN|ENG|RDR|SYS|C2|AP|HOME"; the two instances share state per screen.
    /// Buttons (globalButtons order): 0 UP, 1 DOWN, 2 ENTER, 3 ESC, 4 HOME, 5 RIGHT, 6 LEFT, 7 R9, 8 R10.
    /// </summary>
    public class ELERPMPages : InternalModule
    {
        [KSPField] public string page = "HOME";

        class State { public int cam, cursor, scroll; public float fov = 40f, range = 20000f; public string flash; public float flashUntil; }
        static readonly Dictionary<(int, string), State> states = new();
        State S { get { var k = (internalProp != null ? internalProp.GetInstanceID() : 0, page); if (!states.TryGetValue(k, out var s)) states[k] = s = new State(); return s; } }

        const string G = "[#3cff60ff]", D = "[#1f8a35ff]", A = "[#ffc040ff]", R = "[#ff4a38ff]", W = "[#d8f0ffff]";

        public void Configure(ConfigNode node) { if (node.HasValue("page")) page = node.GetValue("page"); }
        public void PageActive(bool active, int pageNumber) { }

        // ---------------- text ----------------
        public string ShowPage(int w, int h)
        {
            try
            {
                if (vessel == null) return "";
                var sb = new StringBuilder();
                switch (page)
                {
                    case "CAM": Cam(sb); break;
                    case "WPN": Wpn(sb); break;
                    case "ENG": Eng(sb); break;
                    case "RDR": Rdr(sb); break;
                    case "SYS": Sys(sb, h); break;
                    case "C2": C2(sb); break;
                    case "AP": Ap(sb); break;
                    default: Home(sb); break;
                }
                var st = S;
                if (st.flash != null && Time.time < st.flashUntil) sb.Append($"\n{A}{st.flash}");
                return sb.ToString();
            }
            catch (Exception e) { return $"{R}ELE page error\n{e.Message}"; }
        }

        void Flash(string s) { var st = S; st.flash = s; st.flashUntil = Time.time + 2f; }
        static string Bar(float k, int n) { int f = Mathf.Clamp(Mathf.RoundToInt(k * n), 0, n); return new string('|', f) + new string('.', n - f); }

        void Home(StringBuilder sb)
        {
            sb.Append($"{G}  ENHANCED LETHALITY EDITION\n{D}  ---------------------------\n\n");
            sb.Append($"{W}  R1 CAM   sensors / cameras\n  R2 WPN   weapons, rounds, guard\n  R3 ENG   engines, fuel, power\n  R4 RDR   contact scope\n");
            sb.Append($"  R5 SYS   damage, crew, heat\n  R6 C2    tracks, threats, EW\n  R7 AP    VTOL autopilot\n\n");
            sb.Append($"{D}  {vessel.vesselName}\n  {vessel.srfSpeed * 3.6:F0} km/h  {vessel.altitude:F0} m");
        }

        void Cam(StringBuilder sb)
        {
            var src = CamFeed.Sources(vessel); var st = S;
            if (src.Count == 0) { sb.Append($"{A}\n\n\n      NO CAMERA ON VESSEL"); return; }
            st.cam = (st.cam % src.Count + src.Count) % src.Count;
            sb.Append($"{G}{src[st.cam].name}\n{D}FOV {st.fov:0.#}  {st.cam + 1}/{src.Count}");
            sb.Append($"\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n{D}<> CAMERA   ^v ZOOM   HOME RESET");
        }

        void Wpn(StringBuilder sb)
        {
            var wm = vessel.FindPartModuleImplementing<MissileFire>(); var st = S;
            if (wm == null) { sb.Append($"{A}NO WEAPON MANAGER"); return; }
            sb.Append($"{G}WPN  {W}{wm.selectedWeaponString}\n");
            sb.Append($"{(wm.guardMode ? A + "GUARD ON " : D + "guard off")}{D}   team {wm.Team?.Name}\n");
            if (wm.selectedWeapon?.GetWeaponModule() is ModuleWeapon g)
            {
                sb.Append($"{G}RND  {W}{g.currentType}\n{G}AMMO {W}{g.ammoCount:F0}  {G}RPM {W}{g.roundsPerMinute:F0}\n");
                if (g.part.FindModuleImplementing<ModuleTankGun>() is ModuleTankGun tg) sb.Append($"{G}LDR  {W}{tg.loaderInfo}\n");
                if (g.isReloading || g.isOverheated) sb.Append($"{A}{(g.isOverheated ? "OVERHEAT" : "RELOADING")}\n");
            }
            else sb.Append("\n\n");
            foreach (var l in vessel.FindPartModulesImplementing<ModuleLoader>()) sb.Append($"{D}{(l.Manual ? "RACK" : "CAROUSEL")} {l.readyInfo} {l.cycleInfo}\n");
            sb.Append($"{D}----------------------------------------\n");
            var list = Weapons(wm);
            st.cursor = Mathf.Clamp(st.cursor, 0, Mathf.Max(0, list.Count - 1));
            for (int i = 0; i < list.Count && i < 9; i++)
                sb.Append($"{(i == st.cursor ? A + ">" : " ")}{(list[i] == wm.selectedWeaponString ? G : W)}{list[i]}\n");
            sb.Append($"{D}^v PICK  ENT SELECT  > ROUND  R9 GUARD");
        }

        static List<string> Weapons(MissileFire wm) => wm.weaponArray == null ? new List<string>() : wm.weaponArray.Where(x => x != null).Select(x => x.GetShortName()).ToList();

        void Eng(StringBuilder sb)
        {
            sb.Append($"{G}ENG  {W}{vessel.srfSpeed * 3.6:F0} km/h  M{vessel.mach:F2}\n");
            Res(sb, "FUEL", "LiquidFuel"); Res(sb, "PWR ", "ElectricCharge");
            sb.Append($"{D}----------------------------------------\n");
            foreach (var e in vessel.FindPartModulesImplementing<ModuleEngineBlock>()) sb.Append($"{(e.running ? G : D)}BLOCK {e.status}\n");
            foreach (var j in vessel.FindPartModulesImplementing<ModuleDynamicJet>())
            {
                var en = j.part.FindModuleImplementing<ModuleEngines>();
                string thr = en != null && en.EngineIgnited ? $"{en.finalThrust:F0} kN" : "off";
                sb.Append($"{(en != null && en.EngineIgnited ? G : D)}{j.kind.ToUpper(),-10} {thr}{(j.reverse ? A + " REV" : "")}\n");
            }
            foreach (var r in vessel.FindPartModulesImplementing<ModuleHeliRotor>()) sb.Append($"{G}ROTOR {W}{r.status}\n");
            var ag = vessel.ActionGroups;
            sb.Append($"{D}----------------------------------------\n{W}BRK {(ag[KSPActionGroup.Brakes] ? A + "ON " : D + "off")}{W} LGT {(ag[KSPActionGroup.Light] ? A + "ON " : D + "off")}{W} SAS {(ag[KSPActionGroup.SAS] ? A + "ON " : D + "off")}{W} GEAR {(ag[KSPActionGroup.Gear] ? A + "DN" : D + "up")}\n");
            sb.Append($"{D}ENT ENGINES  > REVERSE  < BRAKES  R9 GEAR");
        }

        void Res(StringBuilder sb, string label, string res)
        {
            var def = PartResourceLibrary.Instance.GetDefinition(res); if (def == null) return;
            vessel.GetConnectedResourceTotals(def.id, out double a, out double m);
            float k = m > 0 ? (float)(a / m) : 0;
            sb.Append($"{G}{label} {(k < 0.2f ? A : W)}{Bar(k, 20)} {a:F0}\n");
        }

        List<Contact> InRange()
        {
            AvionicsContacts.Refresh();
            var own = (Vector3)vessel.CoM; float r = S.range;
            return AvionicsContacts.Contacts.Where(c => c.vessel != vessel && (c.position - own).magnitude < r).OrderBy(c => (c.position - own).sqrMagnitude).ToList();
        }

        void Rdr(StringBuilder sb)
        {
            var list = InRange();
            sb.Append($"{G}RDR {S.range / 1000:F1} km  {W}{list.Count} contacts\n");
            if (AvionicsContacts.Selected is Contact sc) sb.Append($"{A}{sc.name} {sc.IFFString} {sc.range / 1000:F1}km{(sc.radarLocked ? " LOCK" : "")}\n"); else sb.Append("\n");
            sb.Append("\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n");
            sb.Append($"{D}^v RANGE  ENT NEXT  > LOCK");
        }

        void Sys(StringBuilder sb, int h)
        {
            sb.Append($"{G}SYS  {W}crew {vessel.GetCrewCount()}/{vessel.GetCrewCapacity()}  parts {vessel.parts.Count}\n{D}PART                     HP   HEAT\n");
            var rows = new List<(string n, float hp, float t)>();
            foreach (var p in vessel.parts)
            {
                float f = 1; var hp = p.Modules.GetModule("HitpointTracker");
                if (hp != null && hp.Fields.GetValue("Hitpoints") is float a && hp.Fields.GetValue("maxHitPoints") is float m && m > 0) f = a / m;
                rows.Add((p.partInfo.title, f, (float)(p.temperature / p.maxTemp)));
            }
            var sorted = rows.OrderBy(x => x.hp).ThenByDescending(x => x.t).ToList();
            var st = S; st.scroll = Mathf.Clamp(st.scroll, 0, Mathf.Max(0, sorted.Count - 15));
            foreach (var (n, hp, t) in sorted.Skip(st.scroll).Take(16))
            {
                string c = hp < 0.33f || t > 0.8f ? R : hp < 0.75f || t > 0.6f ? A : D;
                sb.Append($"{c}{(n.Length > 24 ? n.Substring(0, 24) : n),-24} {hp * 100,3:F0}%  {t * 100,3:F0}%\n");
            }
            sb.Append($"{D}^v SCROLL");
        }

        void C2(StringBuilder sb)
        {
            AvionicsContacts.Refresh();
            var own = (Vector3)vessel.CoM;
            var all = AvionicsContacts.Contacts.Where(c => c.vessel != vessel).ToList();
            int hos = all.Count(c => c.iff == IFF.Hostile), frd = all.Count(c => c.iff == IFF.Friendly), msl = all.Count(c => c.isMissile && c.iff != IFF.Friendly);
            sb.Append($"{G}C2  {W}tracks {all.Count}  {R}hostile {hos}  {G}friendly {frd}\n");
            sb.Append(msl > 0 ? $"{R}MISSILES INBOUND: {msl}\n" : "\n");
            sb.Append($"{D}THREATS (closest first)\n");
            foreach (var c in all.Where(c => c.iff == IFF.Hostile || c.isMissile).OrderBy(c => (c.position - own).sqrMagnitude).Take(7))
                sb.Append($"{(c.isMissile ? R : A)}{(c.name ?? "?").PadRight(18).Substring(0, 18)} {c.range / 1000,5:F1}km {(c.closure > 0 ? "CLOSING" : "")}\n");
            sb.Append($"{D}----------------------------------------\n{G}EW\n");
            var jams = vessel.FindPartModulesImplementing<ModuleECMJammer>();
            if (jams.Count == 0) sb.Append($"{D}no jammer on vessel\n");
            foreach (var j in jams.Take(3)) sb.Append($"{(j.jammerEnabled ? A + "JAMMING " : D + "standby ")}{W}{j.part.partInfo.title}\n");
            sb.Append($"{D}ENT JAMMER ON/OFF");
        }

        void Ap(StringBuilder sb)
        {
            var fc = ModuleVTOLAssist.For(vessel);
            if (fc == null) { sb.Append($"{A}NO VTOL FLIGHT COMPUTER"); return; }
            bool on = fc.mode == VTOLMode.Auto;
            sb.Append($"{G}VTOL AP  {(on ? A + "ENGAGED" : D + "off")}\n{D}mode {fc.mode}\n\n");
            sb.Append($"{G}ALT  {W}{fc.apAlt,6:F0} m {(fc.apAGL ? "AGL" : "ASL")}   {D}now {(fc.apAGL ? vessel.radarAltitude : vessel.altitude):F0}\n");
            sb.Append($"{G}SPD  {W}{fc.apSpeed,6:F0} m/s      {D}now {vessel.srfSpeed:F0}\n");
            sb.Append($"{G}HDG  {W}{fc.apHeading,6:000}          {D}\n\n");
            sb.Append($"{D}----------------------------------------\n{W}^v ALT +-25   <> HDG +-10\nR9/R10 SPD -+5   ESC AGL/ASL\nENT AP ON/OFF   HOME HOVER HERE");
        }

        // ---------------- buttons ----------------
        public void ButtonProcessor(int id)
        {
            try
            {
                var st = S;
                switch (page)
                {
                    case "CAM":
                        if (id == 5) st.cam++; else if (id == 6) st.cam--;
                        else if (id == 0) st.fov = Mathf.Max(0.5f, st.fov / 1.6f); else if (id == 1) st.fov = Mathf.Min(70f, st.fov * 1.6f); else if (id == 4) st.fov = 40f;
                        break;
                    case "WPN":
                        {
                            var wm = vessel.FindPartModulesImplementing<MissileFire>().FirstOrDefault(); if (wm == null) return;
                            var list = Weapons(wm);
                            if (id == 0) st.cursor = Mathf.Max(0, st.cursor - 1); else if (id == 1) st.cursor = Mathf.Min(list.Count - 1, st.cursor + 1);
                            else if (id == 2 && list.Count > 0) wm.CycleWeapon(st.cursor + 1);
                            else if (id == 5 && wm.selectedWeapon?.GetWeaponModule() is ModuleWeapon g && g.ammoList != null && g.ammoList.Count > 1)
                            { foreach (var x in vessel.FindPartModulesImplementing<ModuleWeapon>()) if (x.WeaponName == g.WeaponName) { x.AmmoTypeNum = x.AmmoTypeNum % x.ammoList.Count + 1; x.SetupAmmo(null, null); } Flash("ROUND " + g.currentType); }
                            else if (id == 7) wm.ToggleGuardMode();
                            break;
                        }
                    case "ENG":
                        if (id == 2)
                        {
                            var bl = vessel.FindPartModulesImplementing<ModuleEngineBlock>(); bool start = !bl.Any(b => b.running);
                            foreach (var b in bl) if (start) b.StartEngine(); else b.StopEngine();
                            if (bl.Count == 0) foreach (var e in vessel.FindPartModulesImplementing<ModuleEngines>()) { if (e.EngineIgnited) e.Shutdown(); else e.Activate(); }
                        }
                        else if (id == 5) foreach (var j in vessel.FindPartModulesImplementing<ModuleDynamicJet>()) { if (j.kind == "Turboprop") j.ToggleReverse(); }
                        else if (id == 6) vessel.ActionGroups.ToggleGroup(KSPActionGroup.Brakes);
                        else if (id == 7) vessel.ActionGroups.ToggleGroup(KSPActionGroup.Gear);
                        break;
                    case "RDR":
                        if (id == 0) st.range = Mathf.Max(2500f, st.range / 2); else if (id == 1) st.range = Mathf.Min(320000f, st.range * 2);
                        else
                        {
                            var list = InRange(); if (list.Count == 0) return;
                            if (id == 2) { int k = list.FindIndex(c => c.Key == AvionicsContacts.SelectedKey); AvionicsContacts.Select(list[(k + 1) % list.Count]); }
                            else if (id == 5 && AvionicsContacts.Selected is Contact c) AvionicsContacts.Lock(c);
                        }
                        break;
                    case "SYS":
                        if (id == 0) st.scroll = Mathf.Max(0, st.scroll - 5); else if (id == 1) st.scroll += 5;
                        break;
                    case "C2":
                        if (id == 2) foreach (var j in vessel.FindPartModulesImplementing<ModuleECMJammer>()) j.Toggle();
                        break;
                    case "AP":
                        {
                            var fc = ModuleVTOLAssist.For(vessel); if (fc == null) return;
                            if (id == 0) fc.apAlt += 25; else if (id == 1) fc.apAlt = Mathf.Max(fc.apAGL ? 3 : -50, fc.apAlt - 25);
                            else if (id == 5) fc.apHeading = Mathf.Repeat(fc.apHeading + 10, 360); else if (id == 6) fc.apHeading = Mathf.Repeat(fc.apHeading - 10, 360);
                            else if (id == 7) fc.apSpeed = Mathf.Max(0, fc.apSpeed - 5); else if (id == 8) fc.apSpeed += 5;
                            else if (id == 3) { double gr = vessel.altitude - vessel.radarAltitude; fc.apAlt = (float)Math.Round(fc.apAGL ? fc.apAlt + gr : Math.Max(3, fc.apAlt - gr)); fc.apAGL = !fc.apAGL; }
                            else if (id == 2) { if (fc.mode == VTOLMode.Auto) fc.Disengage("off"); else fc.EngageAP(); }
                            else if (id == 4) fc.EngageAP(true);
                            break;
                        }
                }
            }
            catch (Exception e) { Debug.LogWarning("[ELE RPM] " + e.Message); }
        }

        // ---------------- graphics ----------------
        static Material lineMat;

        public bool RenderBackground(RenderTexture screen, float aspect)
        {
            try
            {
                if (vessel == null) return false;
                if (page == "CAM")
                {
                    var src = CamFeed.Sources(vessel); if (src.Count == 0) return false;
                    var st = S; st.cam = (st.cam % src.Count + src.Count) % src.Count;
                    CamFeed.Render(src[st.cam], st.fov, screen);
                    Overlay(screen, true);
                    return true;
                }
                if (page == "RDR") { Scope(screen); return true; }
            }
            catch (Exception e) { Debug.LogWarning("[ELE RPM] " + e.Message); }
            return false;
        }

        static void Begin(RenderTexture rt, bool clear)
        {
            if (lineMat == null)
            {
                lineMat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                lineMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always); lineMat.SetInt("_Cull", 0); lineMat.SetInt("_ZWrite", 0);
            }
            RenderTexture.active = rt;
            GL.PushMatrix(); GL.LoadPixelMatrix(0, rt.width, rt.height, 0);
            if (clear) GL.Clear(true, true, new Color(0.01f, 0.03f, 0.02f));
            lineMat.SetPass(0);
        }

        static void Line(float x0, float y0, float x1, float y1, Color c) { GL.Begin(GL.LINES); GL.Color(c); GL.Vertex3(x0, y0, 0); GL.Vertex3(x1, y1, 0); GL.End(); }

        static void Overlay(RenderTexture rt, bool cross)
        {
            var prev = RenderTexture.active; Begin(rt, false);
            float c = rt.width / 2f, m = rt.height / 2f, s = rt.width / 512f; var g = new Color(0.25f, 1f, 0.4f);
            if (cross) { Line(c - 30 * s, m, c - 8 * s, m, g); Line(c + 8 * s, m, c + 30 * s, m, g); Line(c, m - 30 * s, c, m - 8 * s, g); Line(c, m + 8 * s, c, m + 30 * s, g); }
            GL.PopMatrix(); RenderTexture.active = prev;
        }

        void Scope(RenderTexture rt)
        {
            var prev = RenderTexture.active; Begin(rt, true);
            float W = rt.width, H = rt.height, cx = W / 2, cy = H * 0.58f, Rr = Mathf.Min(W, H) * 0.36f;
            var dim = new Color(0.1f, 0.45f, 0.18f);
            for (int i = 1; i <= 4; i++) Circle(cx, cy, Rr * i / 4, dim);
            Line(cx, cy - Rr, cx, cy + Rr, dim); Line(cx - Rr, cy, cx + Rr, cy, dim);
            Vector3 up = (vessel.CoM - vessel.mainBody.position).normalized;
            Vector3 fwd = Vector3.ProjectOnPlane(vessel.ReferenceTransform.up, up).normalized; if (fwd.sqrMagnitude < 0.1f) fwd = Vector3.ProjectOnPlane(-vessel.ReferenceTransform.forward, up).normalized;
            Vector3 right = Vector3.Cross(up, fwd);
            float range = S.range;
            foreach (var c in InRange())
            {
                var d = c.position - (Vector3)vessel.CoM;
                float x = cx + Vector3.Dot(d, right) / range * Rr, y = cy - Vector3.Dot(d, fwd) / range * Rr;
                var col = c.isMissile && c.iff != IFF.Friendly ? new Color(1f, 0.3f, 0.2f) : c.iff == IFF.Hostile ? new Color(1f, 0.3f, 0.2f) : c.iff == IFF.Friendly ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.75f, 0.25f);
                float s = c.Key == AvionicsContacts.SelectedKey ? 9 : 5;
                Line(x - s, y - s, x + s, y - s, col); Line(x + s, y - s, x + s, y + s, col); Line(x + s, y + s, x - s, y + s, col); Line(x - s, y + s, x - s, y - s, col);
            }
            GL.PopMatrix(); RenderTexture.active = prev;
        }

        static void Circle(float cx, float cy, float r, Color c)
        {
            GL.Begin(GL.LINES); GL.Color(c);
            for (int i = 0; i < 48; i++) { float a0 = i * Mathf.PI / 24, a1 = (i + 1) * Mathf.PI / 24; GL.Vertex3(cx + Mathf.Cos(a0) * r, cy + Mathf.Sin(a0) * r, 0); GL.Vertex3(cx + Mathf.Cos(a1) * r, cy + Mathf.Sin(a1) * r, 0); }
            GL.End();
        }
    }

    /// <summary>Camera feeds from the vessel's HullCams and targeting cameras, rendered into a texture (shared by ELE screens).</summary>
    public static class CamFeed
    {
        public struct Source { public string name; public Transform t; public Vector3 pos, fwd, up; }
        static Camera cam;

        public static List<Source> Sources(Vessel v)
        {
            var list = new List<Source>();
            foreach (var p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (m.moduleName == "ModuleTargetingCamera")
                    {
                        var t = p.FindModelTransform(m.Fields.GetValue("cameraTransformName") as string ?? "camTransform");
                        if (t != null) list.Add(new Source { name = p.partInfo.title, t = t, fwd = Vector3.forward, up = Vector3.up });
                    }
                    else if (m.moduleName.StartsWith("MuMechModuleHullCamera"))
                    {
                        var name = m.Fields.GetValue("cameraName") as string;
                        list.Add(new Source
                        {
                            name = string.IsNullOrEmpty(name) ? p.partInfo.title : name, t = p.transform,
                            pos = m.Fields.GetValue("cameraPosition") is Vector3 cp ? cp : Vector3.zero,
                            fwd = m.Fields.GetValue("cameraForward") is Vector3 cf ? cf : Vector3.up,
                            up = m.Fields.GetValue("cameraUp") is Vector3 cu ? cu : Vector3.back,
                        });
                    }
                }
            return list;
        }

        public static void Render(Source s, float fov, RenderTexture target)
        {
            if (cam == null)
            {
                var go = new GameObject("ELE screen camera"); UnityEngine.Object.DontDestroyOnLoad(go); cam = go.AddComponent<Camera>(); cam.enabled = false;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.45f, 0.55f, 0.65f);
                cam.nearClipPlane = 0.3f; cam.farClipPlane = 30000f;
            }
            var fc = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            cam.cullingMask = fc != null ? fc.cullingMask & ~(1 << 16) & ~(1 << 20) : (1 << 0) | (1 << 15) | (1 << 4);
            cam.transform.position = s.t.TransformPoint(s.pos);
            cam.transform.rotation = Quaternion.LookRotation(s.t.TransformDirection(s.fwd), s.t.TransformDirection(s.up));
            cam.transform.position += cam.transform.forward * 0.2f;
            cam.fieldOfView = fov; cam.aspect = (float)target.width / target.height;
            cam.targetTexture = target; cam.Render(); cam.targetTexture = null;
        }
    }
}
