using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Weapons;

namespace BDArmory.Avionics
{
    /// <summary>
    /// ELE multi-function display: a clickable IVA screen (works from IVA and first person).
    /// Bottom row picks the page, the right column does that page's actions (labels are drawn next to the buttons).
    ///   CAM  live feed from the vessel's HullCams / targeting cameras / sensor balls   PREV NEXT ZM+ ZM-
    ///   WPN  BDArmory weapons: selected weapon, rounds, loader, guard mode            PREV NEXT RND GRD
    ///   ENG  engine blocks, fuel, power, speed                                        ENG  BRK  LGT SAS
    ///   RDR  top-down contact scope from the merged ELE contact list                  RNG+ RNG- SEL LCK
    ///   SYS  most damaged parts, crew, temperatures                                   UP   DN
    /// Everything is drawn into a RenderTexture with GL (no OnGUI), only while the IVA is visible, at refreshHz.
    /// </summary>
    public class InternalELEMfd : InternalModule
    {
        [KSPField] public string screenName = "screen";
        [KSPField] public string rootName = "mfdRoot";
        [KSPField] public Vector3 modelRotation = Vector3.zero; // fix-up if a cockpit mounts the prop on its side
        [KSPField] public int resolution = 512;
        [KSPField] public float refreshHz = 12f;

        static readonly string[] Pages = { "CAM", "WPN", "ENG", "RDR", "SYS" };
        static readonly string[][] Actions =
        {
            new[] { "PREV", "NEXT", "ZM+", "ZM-" },
            new[] { "PREV", "NEXT", "RND", "GRD" },
            new[] { "ENG", "BRK", "LGT", "SAS" },
            new[] { "RNG+", "RNG-", "SEL", "LCK" },
            new[] { "UP", "DN", "", "" },
        };
        // Button positions on the model (metres, prop space) mapped onto the screen for the labels.
        const float ScrX0 = -0.105f, ScrX1 = 0.075f, ScrZ0 = -0.07f, ScrZ1 = 0.09f;
        static readonly float[] BottomX = { -0.09f, -0.0525f, -0.015f, 0.0225f, 0.06f };
        static readonly float[] RightZ = { 0.065f, 0.03f, -0.005f, -0.04f };

        int page, camIndex, sysScroll;
        float fov = 40f, radarRange = 20000f, nextDraw;
        RenderTexture rt, camRT;
        Camera cam;
        Material lineMat, textMat;
        Font font;
        Color green = new(0.2f, 1f, 0.35f), dim = new(0.1f, 0.5f, 0.18f), amber = new(1f, 0.75f, 0.2f), red = new(1f, 0.25f, 0.2f);

        public override void OnAwake()
        {
            var root = internalProp.FindModelTransform(rootName);
            if (root != null && modelRotation != Vector3.zero) root.localRotation = Quaternion.Euler(modelRotation);
            rt = new RenderTexture(resolution, resolution, 0) { name = "ELE MFD" }; rt.Create();
            var scr = internalProp.FindModelTransform(screenName);
            if (scr != null && scr.GetComponent<Renderer>() is Renderer r)
            {
                var m = new Material(Shader.Find("KSP/Unlit") ?? Shader.Find("Unlit/Texture")); m.mainTexture = rt; m.color = Color.white;
                r.material = m;
            }
            for (int i = 0; i < Pages.Length; i++) Hook("btnPage" + i, i, true);
            for (int i = 0; i < 4; i++) Hook("btnAct" + i, i, false);
            lineMat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            lineMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always); lineMat.SetInt("_Cull", 0); lineMat.SetInt("_ZWrite", 0);
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            textMat = new Material(font.material) { hideFlags = HideFlags.HideAndDontSave };
            font.RequestCharactersInTexture(" !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~°", 22);
            Font.textureRebuilt += OnFontRebuilt;
        }

        void OnFontRebuilt(Font f) { if (f == font) nextDraw = 0; }

        void Hook(string name, int index, bool pageButton)
        {
            var t = internalProp.FindModelTransform(name); if (t == null) return;
            InternalButton.Create(t.gameObject).OnTap(() => { if (pageButton) { page = index; sysScroll = 0; } else Act(index); nextDraw = 0; });
        }

        void OnDestroy()
        {
            Font.textureRebuilt -= OnFontRebuilt;
            if (rt != null) rt.Release(); if (camRT != null) camRT.Release();
            if (cam != null) Destroy(cam.gameObject);
            if (lineMat != null) Destroy(lineMat); if (textMat != null) Destroy(textMat);
        }

        public override void OnUpdate()
        {
            if (vessel == null || Time.time < nextDraw) return;
            nextDraw = Time.time + 1f / refreshHz;
            try { Draw(); } catch (Exception e) { Debug.LogWarning("[ELE MFD] " + e.Message); }
        }

        // ---------- actions ----------
        void Act(int i)
        {
            var wm = vessel.FindPartModuleImplementing<MissileFire>();
            switch (page)
            {
                case 0:
                    var n = Sources().Count; if (n == 0) return;
                    if (i == 0) camIndex = (camIndex + n - 1) % n; else if (i == 1) camIndex = (camIndex + 1) % n;
                    else if (i == 2) fov = Mathf.Max(0.5f, fov / 2); else fov = Mathf.Min(60f, fov * 2);
                    break;
                case 1:
                    if (wm == null) return;
                    if (i == 0) wm.CycleWeapon(false); else if (i == 1) wm.CycleWeapon(true);
                    else if (i == 2 && wm.selectedWeapon?.GetWeaponModule() is ModuleWeapon w && w.ammoList != null && w.ammoList.Count > 1)
                    {
                        foreach (var g in vessel.FindPartModulesImplementing<ModuleWeapon>())
                            if (g.WeaponName == w.WeaponName) { g.AmmoTypeNum = g.AmmoTypeNum % g.ammoList.Count + 1; g.SetupAmmo(null, null); }
                    }
                    else if (i == 3) wm.ToggleGuardMode();
                    break;
                case 2:
                    if (i == 0)
                    {
                        var engines = vessel.FindPartModulesImplementing<ModuleEngineBlock>();
                        bool on = !engines.Any(e => e.running);
                        foreach (var e in engines) if (on) e.StartEngine(); else e.StopEngine();
                    }
                    else vessel.ActionGroups.ToggleGroup(i == 1 ? KSPActionGroup.Brakes : i == 2 ? KSPActionGroup.Light : KSPActionGroup.SAS);
                    break;
                case 3:
                    if (i == 0) radarRange = Mathf.Min(320000f, radarRange * 2); else if (i == 1) radarRange = Mathf.Max(2500f, radarRange / 2);
                    else
                    {
                        var list = InRange();
                        if (list.Count == 0) return;
                        if (i == 2) { int k = list.FindIndex(c => c.Key == AvionicsContacts.SelectedKey); AvionicsContacts.Select(list[(k + 1) % list.Count]); }
                        else if (AvionicsContacts.Selected is Contact c) AvionicsContacts.Lock(c);
                    }
                    break;
                case 4:
                    sysScroll = i == 0 ? Mathf.Max(0, sysScroll - 5) : i == 1 ? sysScroll + 5 : sysScroll;
                    break;
            }
        }

        // ---------- drawing ----------
        void Draw()
        {
            var prev = RenderTexture.active;
            if (page == 0) RenderCamera();
            RenderTexture.active = rt;
            GL.PushMatrix(); GL.LoadPixelMatrix(0, resolution, resolution, 0);
            GL.Clear(true, true, new Color(0.01f, 0.03f, 0.02f));
            float s = resolution / 512f;
            switch (page)
            {
                case 0: DrawCam(s); break;
                case 1: DrawWpn(s); break;
                case 2: DrawEng(s); break;
                case 3: DrawRdr(s); break;
                case 4: DrawSys(s); break;
            }
            // frame: page tabs along the bottom, action labels down the right
            for (int i = 0; i < Pages.Length; i++)
            {
                float x = (BottomX[i] - ScrX0) / (ScrX1 - ScrX0) * resolution;
                Text(Pages[i], x - 18 * s, resolution - 26 * s, 18, i == page ? green : dim, i == page);
            }
            for (int i = 0; i < 4; i++)
            {
                string a = Actions[page][i]; if (a == "") continue;
                float y = (ScrZ1 - RightZ[i]) / (ScrZ1 - ScrZ0) * resolution;
                Text(a, resolution - 54 * s, y - 8 * s, 18, green, true);
            }
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        void DrawCam(float s)
        {
            var src = Sources();
            if (src.Count == 0) { Text("NO CAMERA", 180 * s, 240 * s, 22, amber); return; }
            camIndex %= src.Count;
            GL.PushMatrix(); GL.LoadPixelMatrix(0, resolution, 0, resolution); // DrawTexture: y up
            Graphics.DrawTexture(new Rect(0, 0, resolution, resolution), camRT);
            GL.PopMatrix(); GL.LoadPixelMatrix(0, resolution, resolution, 0);
            float c = resolution / 2f;
            Lines(green, c - 30 * s, c, c - 8 * s, c, c + 8 * s, c, c + 30 * s, c, c, c - 30 * s, c, c - 8 * s, c, c + 8 * s, c, c + 30 * s);
            Text($"{src[camIndex].name}", 10 * s, 10 * s, 18, green);
            Text($"FOV {fov:0.#}°  {camIndex + 1}/{src.Count}", 10 * s, 32 * s, 16, green);
        }

        void DrawWpn(float s)
        {
            var wm = vessel.FindPartModuleImplementing<MissileFire>();
            float y = 12 * s;
            if (wm == null) { Text("NO WEAPON MANAGER", 10 * s, y, 20, amber); return; }
            Text($"WEAPON  {wm.selectedWeaponString}", 10 * s, y, 20, green, true); y += 30 * s;
            Text($"GUARD   {(wm.guardMode ? "ON" : "OFF")}", 10 * s, y, 18, wm.guardMode ? amber : green); y += 26 * s;
            if (wm.selectedWeapon?.GetWeaponModule() is ModuleWeapon w)
            {
                Text($"ROUND   {w.currentType}", 10 * s, y, 18, green); y += 24 * s;
                Text($"AMMO    {w.ammoCount:F0}   RPM {w.roundsPerMinute:F1}", 10 * s, y, 18, green); y += 24 * s;
                if (w.part.FindModuleImplementing<ModuleTankGun>() is ModuleTankGun tg) { Text($"LOADER  {tg.loaderInfo}", 10 * s, y, 18, green); y += 24 * s; }
                if (w.isReloading || w.isOverheated) { Text(w.isOverheated ? "OVERHEAT" : "RELOADING", 10 * s, y, 18, amber); y += 24 * s; }
            }
            foreach (var l in vessel.FindPartModulesImplementing<ModuleLoader>()) { Text($"{(l.Manual ? "RACK" : "CAROUSEL")}  {l.readyInfo}  {l.cycleInfo}", 10 * s, y, 16, dim); y += 20 * s; }
            y += 8 * s; Text("WEAPONS", 10 * s, y, 16, dim); y += 20 * s;
            if (wm.weaponArray != null)
                foreach (var a in wm.weaponArray.Take(10))
                {
                    if (a == null) continue;
                    Text((a.GetShortName() == wm.selectedWeaponString ? "> " : "  ") + a.GetShortName(), 10 * s, y, 16, a.GetShortName() == wm.selectedWeaponString ? green : dim); y += 19 * s;
                }
        }

        void DrawEng(float s)
        {
            float y = 12 * s;
            var engines = vessel.FindPartModulesImplementing<ModuleEngineBlock>();
            Text($"SPEED   {vessel.srfSpeed * 3.6:F0} km/h", 10 * s, y, 22, green, true); y += 32 * s;
            Bar("FUEL", "LiquidFuel", ref y, s); Bar("POWER", "ElectricCharge", ref y, s);
            y += 6 * s;
            if (engines.Count == 0) Text("NO ENGINE BLOCK", 10 * s, y, 18, dim);
            foreach (var e in engines) { Text($"{e.part.partInfo.title}: {e.status}", 10 * s, y, 16, e.running ? green : dim); y += 20 * s; }
            y += 10 * s;
            var ag = vessel.ActionGroups;
            Text($"BRAKES {(ag[KSPActionGroup.Brakes] ? "ON" : "off")}   LIGHTS {(ag[KSPActionGroup.Light] ? "ON" : "off")}   SAS {(ag[KSPActionGroup.SAS] ? "ON" : "off")}", 10 * s, y, 16, green);
        }

        void Bar(string label, string res, ref float y, float s)
        {
            var def = PartResourceLibrary.Instance.GetDefinition(res);
            if (def == null) return;
            vessel.GetConnectedResourceTotals(def.id, out double a, out double m);
            float k = m > 0 ? (float)(a / m) : 0;
            Text($"{label}", 10 * s, y, 16, green);
            Rect(110 * s, y + 2 * s, 260 * s, 14 * s, dim, false); Rect(110 * s, y + 2 * s, 260 * s * k, 14 * s, k < 0.2f ? amber : green, true);
            Text($"{a:F0}/{m:F0}", 380 * s, y, 16, green); y += 24 * s;
        }

        List<Contact> InRange()
        {
            AvionicsContacts.Refresh();
            var own = vessel.CoM;
            return AvionicsContacts.Contacts.Where(c => c.vessel != vessel && (c.position - (Vector3)own).magnitude < radarRange).OrderBy(c => (c.position - (Vector3)own).sqrMagnitude).ToList();
        }

        void DrawRdr(float s)
        {
            var list = InRange();
            float cx = 220 * s, cy = 270 * s, R = 200 * s;
            for (int i = 1; i <= 4; i++) Circle(cx, cy, R * i / 4, dim);
            Lines(dim, cx, cy - R, cx, cy + R, cx - R, cy, cx + R, cy);
            Vector3 up = (vessel.CoM - vessel.mainBody.position).normalized;
            Vector3 fwd = Vector3.ProjectOnPlane(vessel.ReferenceTransform.up, up).normalized; if (fwd.sqrMagnitude < 0.1f) fwd = Vector3.ProjectOnPlane(vessel.ReferenceTransform.forward, up).normalized;
            Vector3 right = Vector3.Cross(up, fwd);
            foreach (var c in list)
            {
                var d = c.position - (Vector3)vessel.CoM;
                float x = cx + Vector3.Dot(d, right) / radarRange * R, y = cy - Vector3.Dot(d, fwd) / radarRange * R;
                var col = c.iff == IFF.Hostile ? red : c.iff == IFF.Friendly ? green : amber;
                bool sel = c.Key == AvionicsContacts.SelectedKey;
                Rect(x - 5 * s, y - 5 * s, 10 * s, 10 * s, col, !sel);
                if (sel) Rect(x - 9 * s, y - 9 * s, 18 * s, 18 * s, col, false);
            }
            Text($"RNG {radarRange / 1000:F1} km   {list.Count} contacts", 10 * s, 10 * s, 18, green);
            if (AvionicsContacts.Selected is Contact sc)
                Text($"{sc.name}  {sc.IFFString}  {sc.range / 1000:F1}km  {(sc.radarLocked ? "LOCK" : "")}", 10 * s, 34 * s, 16, amber);
        }

        void DrawSys(float s)
        {
            float y = 12 * s;
            Text($"CREW {vessel.GetCrewCount()}/{vessel.GetCrewCapacity()}   PARTS {vessel.parts.Count}", 10 * s, y, 18, green, true); y += 28 * s;
            var parts = new List<(string name, float hp, float temp)>();
            foreach (var p in vessel.parts)
            {
                var hp = p.Modules.GetModule("HitpointTracker");
                float f = 1;
                if (hp != null && hp.Fields.GetValue("Hitpoints") is float h && hp.Fields.GetValue("maxHitPoints") is float mh && mh > 0) f = h / mh;
                parts.Add((p.partInfo.title, f, (float)(p.temperature / p.maxTemp)));
            }
            var sorted = parts.OrderBy(p => p.hp).ThenByDescending(p => p.temp).ToList();
            sysScroll = Mathf.Min(sysScroll, Mathf.Max(0, sorted.Count - 5));
            foreach (var p in sorted.Skip(sysScroll).Take(16))
            {
                var col = p.hp < 0.33f || p.temp > 0.8f ? red : p.hp < 0.75f || p.temp > 0.6f ? amber : dim;
                Text($"{Short(p.name),-24} {p.hp * 100,3:F0}%  T{p.temp * 100,3:F0}%", 10 * s, y, 15, col); y += 18 * s;
            }
        }

        static string Short(string n) => n.Length > 24 ? n.Substring(0, 24) : n;

        // ---------- camera ----------
        struct Source { public string name; public Transform t; public Vector3 pos, fwd, up; }

        List<Source> Sources()
        {
            var list = new List<Source>();
            foreach (var p in vessel.parts)
                foreach (PartModule m in p.Modules)
                {
                    string mn = m.moduleName;
                    if (mn == "ModuleTargetingCamera")
                    {
                        var t = p.FindModelTransform(m.Fields.GetValue("cameraTransformName") as string ?? "camTransform");
                        if (t != null) list.Add(new Source { name = p.partInfo.title, t = t, fwd = Vector3.forward, up = Vector3.up });
                    }
                    else if (mn.StartsWith("MuMechModuleHullCamera"))
                    {
                        var name = m.Fields.GetValue("cameraName") as string;
                        list.Add(new Source { name = string.IsNullOrEmpty(name) ? p.partInfo.title : name, t = p.transform,
                            pos = m.Fields.GetValue("cameraPosition") is Vector3 cp ? cp : Vector3.zero,
                            fwd = m.Fields.GetValue("cameraForward") is Vector3 cf ? cf : Vector3.up,
                            up = m.Fields.GetValue("cameraUp") is Vector3 cu ? cu : Vector3.back });
                    }
                }
            return list;
        }

        void RenderCamera()
        {
            var src = Sources(); if (src.Count == 0) return;
            var sr = src[camIndex % src.Count];
            if (cam == null)
            {
                var go = new GameObject("ELE MFD camera"); cam = go.AddComponent<Camera>(); cam.enabled = false;
                camRT = new RenderTexture(resolution, resolution, 24) { name = "ELE MFD cam" }; camRT.Create();
                cam.targetTexture = camRT;
                var fc = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
                cam.cullingMask = fc != null ? fc.cullingMask & ~(1 << 16) & ~(1 << 20) : (1 << 0) | (1 << 15) | (1 << 4);
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.45f, 0.55f, 0.65f);
                cam.nearClipPlane = 0.3f; cam.farClipPlane = 30000f;
            }
            cam.transform.position = sr.t.TransformPoint(sr.pos);
            cam.transform.rotation = Quaternion.LookRotation(sr.t.TransformDirection(sr.fwd), sr.t.TransformDirection(sr.up));
            cam.transform.position += cam.transform.forward * 0.2f; // clear of our own part
            cam.fieldOfView = fov;
            cam.Render();
        }

        // ---------- primitives (pixel space, y down) ----------
        void Lines(Color c, params float[] xy)
        {
            lineMat.SetPass(0); GL.Begin(GL.LINES); GL.Color(c);
            for (int i = 0; i + 3 < xy.Length; i += 4) { GL.Vertex3(xy[i], xy[i + 1], 0); GL.Vertex3(xy[i + 2], xy[i + 3], 0); }
            GL.End();
        }

        void Rect(float x, float y, float w, float h, Color c, bool fill)
        {
            lineMat.SetPass(0);
            if (fill) { GL.Begin(GL.QUADS); GL.Color(c); GL.Vertex3(x, y, 0); GL.Vertex3(x + w, y, 0); GL.Vertex3(x + w, y + h, 0); GL.Vertex3(x, y + h, 0); GL.End(); }
            else Lines(c, x, y, x + w, y, x + w, y, x + w, y + h, x + w, y + h, x, y + h, x, y + h, x, y);
        }

        void Circle(float cx, float cy, float r, Color c)
        {
            lineMat.SetPass(0); GL.Begin(GL.LINES); GL.Color(c);
            for (int i = 0; i < 48; i++)
            {
                float a0 = i * Mathf.PI * 2 / 48, a1 = (i + 1) * Mathf.PI * 2 / 48;
                GL.Vertex3(cx + Mathf.Cos(a0) * r, cy + Mathf.Sin(a0) * r, 0); GL.Vertex3(cx + Mathf.Cos(a1) * r, cy + Mathf.Sin(a1) * r, 0);
            }
            GL.End();
        }

        void Text(string str, float x, float y, int size, Color c, bool bold = false)
        {
            if (string.IsNullOrEmpty(str)) return;
            float scale = resolution / 512f;
            int px = Mathf.RoundToInt(size * scale);
            var style = bold ? FontStyle.Bold : FontStyle.Normal;
            font.RequestCharactersInTexture(str, px, style);
            textMat.mainTexture = font.material.mainTexture; textMat.SetPass(0);
            float baseline = y + font.ascent * px / Mathf.Max(1, font.fontSize);
            GL.Begin(GL.QUADS); GL.Color(c);
            foreach (char ch in str)
            {
                if (!font.GetCharacterInfo(ch, out var ci, px, style)) continue;
                float x0 = x + ci.minX, x1 = x + ci.maxX, y0 = baseline - ci.maxY, y1 = baseline - ci.minY;
                GL.TexCoord(ci.uvTopLeft); GL.Vertex3(x0, y0, 0);
                GL.TexCoord(ci.uvTopRight); GL.Vertex3(x1, y0, 0);
                GL.TexCoord(ci.uvBottomRight); GL.Vertex3(x1, y1, 0);
                GL.TexCoord(ci.uvBottomLeft); GL.Vertex3(x0, y1, 0);
                x += ci.advance;
            }
            GL.End();
        }
    }
}
