using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Blueprint reference images in the VAB/SPH: put PNG/JPG pictures (three-view drawings, photos) in KSP/Blueprints,
    /// add them to the scene as see-through boards, then move, rotate, size, stretch, crop, flip and fade them to build over.
    /// Boards never block clicks on parts. Layouts are saved per craft (Blueprints/_layouts/&lt;craft&gt;.cfg) and come back with it.
    /// Opened from the ELE editor window.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class Blueprints : MonoBehaviour
    {
        public static bool Show;
        const int WindowID = 71730391;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static string Folder => Path.Combine(KSPUtil.ApplicationRootPath, "Blueprints");
        static string LayoutFolder => Path.Combine(Folder, "_layouts");

        class Board
        {
            public string file;
            public Vector3 pos;              // relative to the craft's root part position
            public Vector3 euler;            // degrees
            public float width = 10f;        // metres across (before stretch)
            public float stretchX = 1f, stretchY = 1f;
            public float opacity = 0.6f;
            public float cropL, cropR, cropT, cropB;
            public bool flipX, flipY, visible = true, locked;
            // runtime
            public GameObject go; public Material mat; public Texture2D tex; public Mesh mesh;
            public string Name => Path.GetFileNameWithoutExtension(file);
        }

        readonly List<Board> boards = new();
        int sel = -1;
        Rect rect = new(Screen.width - 700, 120, 330, 560);
        Vector2 fileScroll;
        string[] files = new string[0];
        bool picking;
        string widthText = "", craftKey = "";
        float step = 0.5f;

        // ---------------- lifecycle ----------------
        void Start()
        {
            Directory.CreateDirectory(Folder);
            var readme = Path.Combine(Folder, "README.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme, "Put PNG or JPG reference images here (three-view drawings, photos).\nIn the VAB/SPH open ELE > Blueprints to place them.\nLayouts per craft are saved in _layouts.\n");
            GameEvents.onEditorLoad.Add(OnLoad);
            GameEvents.onEditorShipModified.Add(OnShipModified);
            Refresh();
            LoadLayout();
        }

        void OnDestroy()
        {
            SaveLayout();
            GameEvents.onEditorLoad.Remove(OnLoad);
            GameEvents.onEditorShipModified.Remove(OnShipModified);
            foreach (var b in boards) Destroy(b);
            if (locked && EditorLogic.fetch != null) EditorLogic.fetch.Unlock(LockId);
        }

        void OnLoad(ShipConstruct s, KSP.UI.Screens.CraftBrowserDialog.LoadType t) { foreach (var b in boards) Destroy(b); boards.Clear(); sel = -1; dirty = false; LoadLayout(); }   // edits were already saved as they happened
        // renaming the craft (or naming a new one) moves its boards to the new name
        void OnShipModified(ShipConstruct s) { if (boards.Count > 0 && CraftKey() != craftKey) SaveLayout(); }

        static string CraftKey()
        {
            var n = EditorLogic.fetch != null && EditorLogic.fetch.ship != null ? EditorLogic.fetch.ship.shipName : "";
            if (string.IsNullOrEmpty(n)) n = "Untitled Space Craft";
            foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n;
        }

        void Refresh()
        {
            files = Directory.Exists(Folder)
                ? Directory.GetFiles(Folder).Where(f => f.EndsWith(".png", true, Inv) || f.EndsWith(".jpg", true, Inv) || f.EndsWith(".jpeg", true, Inv)).Select(Path.GetFileName).OrderBy(x => x).ToArray()
                : new string[0];
        }

        static Vector3 Origin => EditorLogic.RootPart != null ? EditorLogic.RootPart.transform.position : Vector3.zero;

        // ---------------- boards ----------------
        Board Add(string file)
        {
            var b = new Board { file = file };
            // start beside the craft, facing sideways (a side view), sized to the craft
            float len = CraftLength();
            b.width = len > 1 ? len * 1.15f : 10f;
            b.euler = new Vector3(0, 90, 0);
            boards.Add(b);
            Build(b);
            sel = boards.Count - 1;
            widthText = b.width.ToString("0.##", Inv);
            SaveLayout();
            return b;
        }

        static float CraftLength()
        {
            var ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            if (ship == null || ship.parts.Count == 0) return 0;
            var bounds = new Bounds(ship.parts[0].transform.position, Vector3.zero);
            foreach (var p in ship.parts) foreach (var r in p.GetComponentsInChildren<Renderer>()) if (r.enabled) bounds.Encapsulate(r.bounds);
            return Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        }

        void Build(Board b)
        {
            Destroy(b);
            var path = Path.Combine(Folder, b.file);
            if (!File.Exists(path)) return;
            b.tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            if (!b.tex.LoadImage(File.ReadAllBytes(path))) { Debug.LogWarning($"[BDArmoryAvionics]: blueprint {b.file} isn't a readable image"); return; }
            var shader = Shader.Find("KSP/Alpha/Unlit Transparent") ?? Shader.Find("Unlit/Transparent") ?? Shader.Find("Sprites/Default");
            b.mat = new Material(shader) { mainTexture = b.tex, renderQueue = 3100 };
            b.mesh = new Mesh { name = "blueprint" };
            b.go = new GameObject("ELE blueprint " + b.Name);
            b.go.AddComponent<MeshFilter>().sharedMesh = b.mesh;
            var mr = b.go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = b.mat; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            Apply(b);
        }

        static void Destroy(Board b)
        {
            if (b.go != null) UnityEngine.Object.Destroy(b.go);
            if (b.mat != null) UnityEngine.Object.Destroy(b.mat);
            if (b.tex != null) UnityEngine.Object.Destroy(b.tex);
            if (b.mesh != null) UnityEngine.Object.Destroy(b.mesh);
            b.go = null; b.mat = null; b.tex = null; b.mesh = null;
        }

        /// <summary>Rebuild the quad (size, stretch, crop, flip; both faces) and place it.</summary>
        static void Apply(Board b)
        {
            if (b.go == null || b.tex == null) return;
            float aspect = (float)b.tex.height / Mathf.Max(1, b.tex.width);
            float u0 = b.cropL, u1 = 1 - b.cropR, v0 = b.cropB, v1 = 1 - b.cropT;
            if (u1 - u0 < 0.02f || v1 - v0 < 0.02f) return;
            float w = b.width * b.stretchX * (u1 - u0);
            float h = b.width * aspect * b.stretchY * (v1 - v0);
            // the crop keeps the picture's scale: cropping trims the board instead of stretching what's left
            float cx = b.width * b.stretchX * ((u0 + u1) / 2 - 0.5f), cy = b.width * aspect * b.stretchY * ((v0 + v1) / 2 - 0.5f);
            float ua = b.flipX ? u1 : u0, ub = b.flipX ? u0 : u1, va = b.flipY ? v1 : v0, vb = b.flipY ? v0 : v1;
            var v = new[] { new Vector3(cx - w / 2, cy - h / 2), new Vector3(cx + w / 2, cy - h / 2), new Vector3(cx + w / 2, cy + h / 2), new Vector3(cx - w / 2, cy + h / 2) };
            b.mesh.Clear();
            b.mesh.vertices = v.Concat(v).ToArray();
            var uv = new[] { new Vector2(ua, va), new Vector2(ub, va), new Vector2(ub, vb), new Vector2(ua, vb) };
            b.mesh.uv = uv.Concat(uv).ToArray();
            b.mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7 };   // front and back faces
            b.mesh.RecalculateBounds();
            b.go.transform.position = Origin + b.pos;
            b.go.transform.rotation = Quaternion.Euler(b.euler);
            b.mat.color = new Color(1, 1, 1, b.opacity);
            b.go.SetActive(b.visible);
        }

        // ---------------- save / load ----------------
        void SaveLayout()
        {
            craftKey = CraftKey();
            try
            {
                Directory.CreateDirectory(LayoutFolder);
                var path = Path.Combine(LayoutFolder, craftKey + ".cfg");
                if (boards.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
                var root = new ConfigNode("ELE_BLUEPRINTS");
                foreach (var b in boards)
                {
                    var n = root.AddNode("BOARD");
                    n.AddValue("file", b.file); n.AddValue("pos", V(b.pos)); n.AddValue("euler", V(b.euler));
                    n.AddValue("width", F(b.width)); n.AddValue("stretch", $"{F(b.stretchX)},{F(b.stretchY)}"); n.AddValue("opacity", F(b.opacity));
                    n.AddValue("crop", $"{F(b.cropL)},{F(b.cropR)},{F(b.cropT)},{F(b.cropB)}");
                    n.AddValue("flipX", b.flipX); n.AddValue("flipY", b.flipY); n.AddValue("visible", b.visible); n.AddValue("locked", b.locked);
                }
                var wrap = new ConfigNode(); wrap.AddNode(root); wrap.Save(path);
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: blueprint layout save failed: {e.Message}"); }
        }

        void LoadLayout()
        {
            craftKey = CraftKey();
            var path = Path.Combine(LayoutFolder, craftKey + ".cfg");
            if (!File.Exists(path)) return;
            var root = ConfigNode.Load(path)?.GetNode("ELE_BLUEPRINTS");
            if (root == null) return;
            foreach (var n in root.GetNodes("BOARD"))
            {
                var b = new Board { file = n.GetValue("file") ?? "" };
                b.pos = P3(n.GetValue("pos")); b.euler = P3(n.GetValue("euler"));
                float.TryParse(n.GetValue("width"), NumberStyles.Float, Inv, out b.width);
                var st = P(n.GetValue("stretch"), 2, 1); b.stretchX = st[0]; b.stretchY = st[1];
                float.TryParse(n.GetValue("opacity"), NumberStyles.Float, Inv, out b.opacity);
                var c = P(n.GetValue("crop"), 4, 0); b.cropL = c[0]; b.cropR = c[1]; b.cropT = c[2]; b.cropB = c[3];
                bool.TryParse(n.GetValue("flipX"), out b.flipX); bool.TryParse(n.GetValue("flipY"), out b.flipY);
                b.visible = n.GetValue("visible") != "False"; bool.TryParse(n.GetValue("locked"), out b.locked);
                if (b.width <= 0) b.width = 10;
                boards.Add(b); Build(b);
            }
            if (boards.Count > 0) { sel = 0; widthText = boards[0].width.ToString("0.##", Inv); }
        }

        static string F(float f) => f.ToString("0.####", Inv);
        static string V(Vector3 v) => $"{F(v.x)},{F(v.y)},{F(v.z)}";
        static float[] P(string s, int n, float def)
        {
            var r = Enumerable.Repeat(def, n).ToArray();
            if (string.IsNullOrEmpty(s)) return r;
            var p = s.Split(',');
            for (int i = 0; i < n && i < p.Length; i++) float.TryParse(p[i], NumberStyles.Float, Inv, out r[i]);
            return r;
        }
        static Vector3 P3(string s) { var r = P(s, 3, 0); return new Vector3(r[0], r[1], r[2]); }

        // ---------------- per-frame ----------------
        const string LockId = "ELE_Blueprints";
        bool locked;
        float nextSave;
        bool dirty;

        void Update()
        {
            // the craft's root can move (re-rooting, dragging the whole craft): boards follow it
            foreach (var b in boards) if (b.go != null) b.go.transform.position = Origin + b.pos;

            Vector2 m = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool over = Show && rect.Contains(m);
            if (over != locked && EditorLogic.fetch != null) { EditorLogic.fetch.Unlock(LockId); locked = over; }

            if (dirty && Time.time > nextSave) { dirty = false; SaveLayout(); }
        }

        void Changed(Board b) { Apply(b); dirty = true; nextSave = Time.time + 1f; }

        // ---------------- window ----------------
        void OnGUI()
        {
            if (!Show) return;
            rect = AvionicsCore.DrawWindow(WindowID, rect, Draw, "Blueprints", BDArmorySetup.BDGuiSkin);
        }

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; return; }
            var btn = BDArmorySetup.ButtonStyle; var on = BDArmorySetup.SelectedButtonStyle;
            var small = AvionicsDraw.LabelSmall;
            float x = 8, w = rect.width - 16, y = 24, hw = (w - 4) / 2, tw = (w - 8) / 3;

            // board list
            for (int i = 0; i < boards.Count; i++)
            {
                var b = boards[i];
                if (GUI.Toggle(new Rect(x, y, w - 104, 20), sel == i, $"{b.Name}{(b.locked ? "  (locked)" : "")}", sel == i ? on : btn)) { if (sel != i) { sel = i; widthText = b.width.ToString("0.##", Inv); } }
                if (GUI.Button(new Rect(x + w - 100, y, 48, 20), b.visible ? "Hide" : "Show", btn)) { b.visible = !b.visible; Changed(b); }
                if (GUI.Button(new Rect(x + w - 50, y, 50, 20), "Del", btn)) { Destroy(b); boards.RemoveAt(i); sel = Mathf.Min(sel, boards.Count - 1); dirty = true; GUIUtility.ExitGUI(); }
                y += 22;
            }
            if (GUI.Button(new Rect(x, y, hw, 22), picking ? "Cancel" : "+ Add image", picking ? on : btn)) { picking = !picking; Refresh(); }
            if (GUI.Button(new Rect(x + hw + 4, y, hw, 22), "Open folder", btn)) Application.OpenURL("file:///" + Folder.Replace('\\', '/'));
            y += 26;

            if (picking)
            {
                if (files.Length == 0) { AvionicsDraw.ColorLabel(new Rect(x, y, w, 30), "No PNG/JPG in KSP/Blueprints yet.\nCopy images there, then press Refresh.", small, AvionicsDraw.ScopeText); y += 32; }
                var view = new Rect(x, y, w, Mathf.Min(160, files.Length * 22 + 4));
                fileScroll = GUI.BeginScrollView(view, fileScroll, new Rect(0, 0, w - 16, files.Length * 22));
                for (int i = 0; i < files.Length; i++) if (GUI.Button(new Rect(0, i * 22, w - 18, 20), files[i], btn)) { Add(files[i]); picking = false; }
                GUI.EndScrollView();
                y += view.height + 4;
                if (GUI.Button(new Rect(x, y, w, 20), "Refresh", btn)) Refresh();
                y += 24;
            }

            if (sel >= 0 && sel < boards.Count && !picking)
            {
                var b = boards[sel];
                if (b.tex == null) AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"Can't load {b.file} (moved or not an image)", small, AvionicsDraw.Hostile);
                y += 2;
                b.locked = GUI.Toggle(new Rect(x, y, w, 20), b.locked, b.locked ? "Locked (unlock to edit)" : "Lock this board", b.locked ? on : btn); y += 24;
                GUI.enabled = !b.locked;

                // view presets
                AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "VIEW", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
                if (GUI.Button(new Rect(x, y, tw, 20), "Side", btn)) { b.euler = new Vector3(0, 90, 0); b.pos = Vector3.zero; Changed(b); }
                if (GUI.Button(new Rect(x + tw + 4, y, tw, 20), "Top", btn)) { b.euler = new Vector3(90, 0, 0); b.pos = Vector3.zero; Changed(b); }
                if (GUI.Button(new Rect(x + 2 * (tw + 4), y, tw, 20), "Front", btn)) { b.euler = Vector3.zero; b.pos = Vector3.zero; Changed(b); }
                y += 24;

                // move
                AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), $"MOVE  (step {step:0.##} m)", AvionicsDraw.Header, AvionicsDraw.HudText);
                if (GUI.Button(new Rect(x + w - 120, y - 2, 120, 16), "step 0.05/0.5/5 m", btn)) step = step >= 5 ? 0.05f : step * 10; y += 16;
                string[] ax = { "X", "Y", "Z" };
                for (int a = 0; a < 3; a++)
                {
                    float bw = (w - 80) / 2;
                    if (GUI.Button(new Rect(x, y, bw, 20), $"− {ax[a]}", btn)) { var p = b.pos; p[a] -= step; b.pos = p; Changed(b); }
                    GUI.Label(new Rect(x + bw + 4, y, 72, 20), b.pos[a].ToString("0.00", Inv), small);
                    if (GUI.Button(new Rect(x + bw + 80, y, bw, 20), $"+ {ax[a]}", btn)) { var p = b.pos; p[a] += step; b.pos = p; Changed(b); }
                    y += 22;
                }

                // rotate
                AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "ROTATE", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
                for (int a = 0; a < 3; a++)
                {
                    float val = Slider(ref y, x, w, $"{ax[a]} {b.euler[a]:0}°", b.euler[a], -180, 180);
                    val = Mathf.Round(val);
                    if (Mathf.Abs(Mathf.DeltaAngle(val, b.euler[a])) > 0.4f) { var e = b.euler; e[a] = val; b.euler = e; Changed(b); }
                }
                if (GUI.Button(new Rect(x, y, hw, 20), "Turn 90°", btn)) { b.euler.z = Mathf.Repeat(b.euler.z + 90 + 180, 360) - 180; Changed(b); }
                if (GUI.Button(new Rect(x + hw + 4, y, hw, 20), "Spin 180°", btn)) { b.euler.y = Mathf.Repeat(b.euler.y + 180 + 180, 360) - 180; Changed(b); }
                y += 24;

                // size and shape
                AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "SIZE / SHAPE", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
                GUI.Label(new Rect(x, y, 120, 20), "Width (m):", small);
                widthText = GUI.TextField(new Rect(x + 90, y, 70, 20), widthText);
                if (GUI.Button(new Rect(x + 164, y, w - 164, 20), "Set", btn) && float.TryParse(widthText, NumberStyles.Float, Inv, out var wv) && wv > 0.05f) { b.width = wv; Changed(b); }
                y += 22;
                if (GUI.Button(new Rect(x, y, hw, 20), "Fit craft length", btn)) { float l = CraftLength(); if (l > 0.5f) { b.width = l; widthText = l.ToString("0.##", Inv); Changed(b); } }
                if (GUI.Button(new Rect(x + hw + 4, y, (hw - 4) / 2, 20), "×0.9", btn)) { b.width *= 0.9f; widthText = b.width.ToString("0.##", Inv); Changed(b); }
                if (GUI.Button(new Rect(x + hw + 4 + (hw + 4) / 2, y, (hw - 4) / 2, 20), "×1.1", btn)) { b.width *= 1.1f; widthText = b.width.ToString("0.##", Inv); Changed(b); }
                y += 22;
                float sx = Slider(ref y, x, w, $"Stretch across ×{b.stretchX:0.00}", b.stretchX, 0.25f, 3f); if (Mathf.Abs(sx - b.stretchX) > 0.004f) { b.stretchX = sx; Changed(b); }
                float sy = Slider(ref y, x, w, $"Stretch up ×{b.stretchY:0.00}", b.stretchY, 0.25f, 3f); if (Mathf.Abs(sy - b.stretchY) > 0.004f) { b.stretchY = sy; Changed(b); }
                if (GUI.Button(new Rect(x, y, hw, 20), b.flipX ? "Mirrored ←→" : "Mirror ←→", b.flipX ? on : btn)) { b.flipX = !b.flipX; Changed(b); }
                if (GUI.Button(new Rect(x + hw + 4, y, hw, 20), b.flipY ? "Flipped ↑↓" : "Flip ↑↓", b.flipY ? on : btn)) { b.flipY = !b.flipY; Changed(b); }
                y += 24;

                // crop: for one view out of a three-view sheet
                AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "CROP  (pick one view out of a three-view sheet)", AvionicsDraw.Header, AvionicsDraw.HudText); y += 16;
                float c;
                c = Slider(ref y, x, w, $"Left {b.cropL * 100:0}%", b.cropL, 0, 0.95f); if (Mathf.Abs(c - b.cropL) > 0.002f) { b.cropL = Mathf.Min(c, 0.98f - b.cropR); Changed(b); }
                c = Slider(ref y, x, w, $"Right {b.cropR * 100:0}%", b.cropR, 0, 0.95f); if (Mathf.Abs(c - b.cropR) > 0.002f) { b.cropR = Mathf.Min(c, 0.98f - b.cropL); Changed(b); }
                c = Slider(ref y, x, w, $"Top {b.cropT * 100:0}%", b.cropT, 0, 0.95f); if (Mathf.Abs(c - b.cropT) > 0.002f) { b.cropT = Mathf.Min(c, 0.98f - b.cropB); Changed(b); }
                c = Slider(ref y, x, w, $"Bottom {b.cropB * 100:0}%", b.cropB, 0, 0.95f); if (Mathf.Abs(c - b.cropB) > 0.002f) { b.cropB = Mathf.Min(c, 0.98f - b.cropT); Changed(b); }

                float o = Slider(ref y, x, w, $"Opacity {b.opacity * 100:0}%", b.opacity, 0.05f, 1f); if (Mathf.Abs(o - b.opacity) > 0.004f) { b.opacity = o; Changed(b); }
                if (GUI.Button(new Rect(x, y, hw, 20), "Duplicate", btn)) { var d = Add(b.file); d.pos = b.pos; d.euler = b.euler; d.width = b.width; d.stretchX = b.stretchX; d.stretchY = b.stretchY; d.opacity = b.opacity; d.cropL = b.cropL; d.cropR = b.cropR; d.cropT = b.cropT; d.cropB = b.cropB; d.flipX = b.flipX; d.flipY = b.flipY; Changed(d); }
                if (GUI.Button(new Rect(x + hw + 4, y, hw, 20), "Reset", btn)) { b.pos = Vector3.zero; b.euler = new Vector3(0, 90, 0); b.stretchX = b.stretchY = 1; b.cropL = b.cropR = b.cropT = b.cropB = 0; b.flipX = b.flipY = false; b.opacity = 0.6f; Changed(b); }
                y += 24;
                GUI.enabled = true;
            }
            AvionicsDraw.ColorLabel(new Rect(x, y, w, 14), "Boards don't block clicks. Saved per craft.", small, AvionicsDraw.ScopeText); y += 16;
            rect.height = y + 6;
            GUI.DragWindow();
        }

        static float Slider(ref float y, float x, float w, string label, float v, float min, float max)
        {
            GUI.Label(new Rect(x, y, w, 16), label, AvionicsDraw.LabelSmall);
            y += 15;
            v = GUI.HorizontalSlider(new Rect(x, y, w, 14), v, min, max);
            y += 18;
            return v;
        }
    }
}
