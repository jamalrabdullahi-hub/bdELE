using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// GPS point manager: bearing and range to each point, sorting, groups, rename, copy/paste coordinates,
    /// and one-click target designation. Points are BDArmory's own team GPS list, so they stay shared with the team.
    /// </summary>
    public static class GPSManagerWindow
    {
        public const int WindowID = 71730303;
        static Vector2 scroll;
        static int editNameIndex = -1, editGroupIndex = -1;
        static string editText = "";
        static string pasteText = "";
        static string pasteError = "";
        /// <summary>True while one of this window's text fields has keyboard focus (KSP keyboard controls are locked meanwhile).</summary>
        public static bool TextFocused;

        struct Row { public int index; public GPSTargetInfo info; public float range; public float bearing; public string group; }
        static readonly List<Row> rows = new();
        static readonly List<string> groups = new();

        public static void Draw(int id)
        {
            var rect = AvionicsSettings.GPSManagerRect;
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle))
            {
                AvionicsSettings.ShowGPSManager = false;
                return;
            }
            var wm = AvionicsContacts.WM;
            var own = AvionicsContacts.Own;
            var btn = BDArmorySetup.ButtonStyle;
            var sel = BDArmorySetup.SelectedButtonStyle;
            if (wm == null || own == null)
            {
                AvionicsDraw.ColorLabel(new Rect(8, 30, rect.width - 16, 20), "No weapon manager on this vessel.", AvionicsDraw.Label, AvionicsDraw.ScopeText);
                GUIUtils.DragWindow();
                return;
            }
            var list = BDATargetManager.GPSTargetList(wm.Team);
            BuildRows(list, own);
            if (Event.current.type == EventType.Repaint) TextFocused = GUI.GetNameOfFocusedControl().StartsWith("BDAAv");

            // Current target
            float y = 22;
            string tgt = wm.designatedGPSCoords != Vector3d.zero ? $"GPS target: {wm.designatedGPSInfo.name}   {BodyUtils.FormattedGeoPosShort(wm.designatedGPSCoords, false)}" : "GPS target: none";
            AvionicsDraw.ColorLabel(new Rect(8, y + 3, rect.width - 90, 16), tgt, AvionicsDraw.Label, AvionicsDraw.LockBox);
            if (wm.designatedGPSCoords != Vector3d.zero && GUI.Button(new Rect(rect.width - 80, y, 72, 20), "Clear", btn))
                wm.designatedGPSInfo = new GPSTargetInfo();

            // Toolbar
            y += 24;
            string[] sortNames = ["Range", "Name", "Group"];
            if (GUI.Button(new Rect(6, y, 92, 20), $"Sort: {sortNames[AvionicsSettings.GPSSortMode]}", btn)) AvionicsSettings.GPSSortMode = (AvionicsSettings.GPSSortMode + 1) % 3;
            string filterLabel = string.IsNullOrEmpty(AvionicsSettings.GPSGroupFilter) ? "All groups" : AvionicsSettings.GPSGroupFilter;
            if (GUI.Button(new Rect(100, y, 110, 20), filterLabel, btn)) CycleFilter();
            if (GUI.Button(new Rect(212, y, 96, 20), "+ Here", btn))
                AvionicsContacts.AddGPS(VectorUtils.WorldPositionToGeoCoords(own.CoM, own.mainBody), "Waypoint");
            if (GUI.Button(new Rect(310, y, 110, 20), GPSMarkers.DropArmed ? "Click terrain..." : "+ Click to drop", GPSMarkers.DropArmed ? sel : btn))
                GPSMarkers.DropArmed = !GPSMarkers.DropArmed;
            AvionicsSettings.GPSWorldMarkers = GUI.Toggle(new Rect(422, y, 46, 20), AvionicsSettings.GPSWorldMarkers, "3D", AvionicsSettings.GPSWorldMarkers ? sel : btn);
            AvionicsSettings.GPSMapMarkers = GUI.Toggle(new Rect(470, y, 44, 20), AvionicsSettings.GPSMapMarkers, "Map", AvionicsSettings.GPSMapMarkers ? sel : btn);

            // Paste coordinates
            y += 24;
            AvionicsDraw.ColorLabel(new Rect(8, y + 3, 80, 16), "Coordinates", AvionicsDraw.Label, AvionicsDraw.ScopeText);
            GUI.SetNextControlName("BDAAvPaste");
            pasteText = GUI.TextField(new Rect(90, y, 220, 20), pasteText, 64, BDArmorySetup.BDGuiSkin.textField);
            if (GUI.Button(new Rect(312, y, 50, 20), "Paste", btn)) pasteText = GUIUtility.systemCopyBuffer ?? "";
            if (GUI.Button(new Rect(364, y, 50, 20), "Add", btn)) AddFromText(own);
            if (!string.IsNullOrEmpty(pasteError)) AvionicsDraw.ColorLabel(new Rect(420, y + 3, rect.width - 426, 16), pasteError, AvionicsDraw.Label, AvionicsDraw.Hostile);

            // Header
            y += 26;
            var hdr = AvionicsDraw.Header;
            GUI.Label(new Rect(8, y, 120, 16), "NAME", hdr);
            GUI.Label(new Rect(128, y, 90, 16), "GROUP", hdr);
            GUI.Label(new Rect(218, y, 44, 16), "BRG", hdr);
            GUI.Label(new Rect(262, y, 64, 16), "RANGE", hdr);
            y += 18;

            float listH = rect.height - y - 26;
            float rowH = 22;
            var view = new Rect(0, 0, rect.width - 28, Mathf.Max(rows.Count * rowH, listH - 4));
            scroll = GUI.BeginScrollView(new Rect(4, y, rect.width - 8, listH), scroll, view);
            int removeIndex = -1;
            float ry = 0;
            if (rows.Count == 0)
                AvionicsDraw.ColorLabel(new Rect(6, 6, view.width, 18), list.Count == 0 ? "No GPS points yet. Use + Here, + Click to drop, or paste coordinates." : "No points in this group.", AvionicsDraw.Label, AvionicsDraw.ScopeText);
            foreach (var r in rows)
            {
                bool isTarget = wm.designatedGPSCoords != Vector3d.zero && r.info.EqualsTarget(wm.designatedGPSInfo);
                Color col = isTarget ? AvionicsDraw.LockBox : AvionicsDraw.HudText;

                // Name (click to rename)
                if (editNameIndex == r.index)
                {
                    GUI.SetNextControlName("BDAAvName");
                    editText = GUI.TextField(new Rect(2, ry, 120, 20), editText, 12, BDArmorySetup.BDGuiSkin.textField);
                    if (Confirmed() || GUI.Button(new Rect(124, ry, 28, 20), "OK", btn)) { Rename(list, r.index, wm); editNameIndex = -1; }
                }
                else if (editGroupIndex == r.index)
                {
                    AvionicsDraw.ColorLabel(new Rect(6, ry + 3, 120, 16), r.info.name, AvionicsDraw.Label, col);
                    GUI.SetNextControlName("BDAAvGroup");
                    editText = GUI.TextField(new Rect(122, ry, 90, 20), editText, 16, BDArmorySetup.BDGuiSkin.textField);
                    if (Confirmed() || GUI.Button(new Rect(214, ry, 28, 20), "OK", btn))
                    {
                        AvionicsSettings.SetGPSGroup(r.info.gpsCoordinates, editText.Trim());
                        AvionicsSettings.Save();
                        editGroupIndex = -1;
                    }
                }
                else
                {
                    if (GUI.Button(new Rect(2, ry, 120, 20), r.info.name, btn)) { editNameIndex = r.index; editGroupIndex = -1; editText = r.info.name; }
                    if (GUI.Button(new Rect(124, ry, 90, 20), string.IsNullOrEmpty(r.group) ? "-" : r.group, btn)) { editGroupIndex = r.index; editNameIndex = -1; editText = r.group; }
                    AvionicsDraw.ColorLabel(new Rect(216, ry + 3, 44, 16), $"{r.bearing:000}°", AvionicsDraw.Label, col);
                    AvionicsDraw.ColorLabel(new Rect(258, ry + 3, 62, 16), AvionicsDraw.FormatRange(r.range), AvionicsDraw.LabelRight, col);
                }

                float bx = 326;
                if (GUI.Button(new Rect(bx, ry, 52, 20), "Target", isTarget ? sel : btn)) { wm.designatedGPSInfo = r.info; wm.designatedGPSCoordsIndex = r.index; }
                bx += 54;
                if (GUI.Button(new Rect(bx, ry, 34, 20), "Cam", btn))
                {
                    var cam = AvionicsContacts.ActiveCamera;
                    if (cam != null) cam.StartCoroutine(cam.PointToPositionRoutine(r.info.worldPos, -1));
                }
                bx += 36;
                if (GUI.Button(new Rect(bx, ry, 40, 20), "Copy", btn))
                    GUIUtility.systemCopyBuffer = FormatCoords(r.info.gpsCoordinates);
                bx += 42;
                if (GUI.Button(new Rect(bx, ry, 22, 20), "X", btn)) removeIndex = r.index;
                ry += rowH;
            }
            GUI.EndScrollView();

            if (removeIndex >= 0 && removeIndex < list.Count)
            {
                AvionicsSettings.SetGPSGroup(list[removeIndex].gpsCoordinates, "");
                list.RemoveAt(removeIndex);
                BDATargetManager.Instance.SaveGPSTargets();
                editNameIndex = editGroupIndex = -1;
            }

            if (Time.time - AvionicsContacts.LastMessageTime < 4)
                AvionicsDraw.ColorLabel(new Rect(8, rect.height - 22, rect.width - 30, 16), AvionicsContacts.LastMessage, AvionicsDraw.Label, AvionicsDraw.LockBox);
            else
                AvionicsDraw.ColorLabel(new Rect(8, rect.height - 22, rect.width - 30, 16), "Click a name to rename it, or a group to change it. Names can't contain ; : or ,", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);

            TargetListWindow.DrawResizeHandle(ref AvionicsSettings.GPSManagerRect, 520, 220);
            GUIUtils.DragWindow();
        }

        static bool Confirmed()
        {
            var e = Event.current;
            return e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
        }

        static void Rename(List<GPSTargetInfo> list, int index, Control.MissileFire wm)
        {
            if (index < 0 || index >= list.Count) return;
            string name = AvionicsContacts.SanitizeGPSName(editText);
            var old = list[index];
            if (wm.designatedGPSInfo.EqualsTarget(old)) wm.designatedGPSInfo.name = name;
            list[index] = new GPSTargetInfo(old.gpsCoordinates, name, old.gpsVessel);
            BDATargetManager.Instance.SaveGPSTargets();
        }

        static void BuildRows(List<GPSTargetInfo> list, Vessel own)
        {
            rows.Clear();
            groups.Clear();
            Vector3 up = (Vector3)own.upAxis;
            Vector3 north = VectorUtils.GetNorthVector(own.CoM, own.mainBody);
            Vector3 east = Vector3.Cross(up, north);
            for (int i = 0; i < list.Count; i++)
            {
                var info = list[i];
                string group = AvionicsSettings.GetGPSGroup(info.gpsCoordinates);
                if (!string.IsNullOrEmpty(group) && !groups.Contains(group)) groups.Add(group);
                if (!string.IsNullOrEmpty(AvionicsSettings.GPSGroupFilter) && group != AvionicsSettings.GPSGroupFilter) continue;
                Vector3 d = (Vector3)info.worldPos - own.CoM;
                float bearing = Mathf.Repeat(Mathf.Atan2(Vector3.Dot(d, east), Vector3.Dot(d, north)) * Mathf.Rad2Deg, 360);
                rows.Add(new Row { index = i, info = info, range = d.magnitude, bearing = bearing, group = group });
            }
            switch (AvionicsSettings.GPSSortMode)
            {
                case 0: rows.Sort((a, b) => a.range.CompareTo(b.range)); break;
                case 1: rows.Sort((a, b) => string.Compare(a.info.name, b.info.name, System.StringComparison.OrdinalIgnoreCase)); break;
                case 2: rows.Sort((a, b) => { int c = string.Compare(a.group, b.group, System.StringComparison.OrdinalIgnoreCase); return c != 0 ? c : a.range.CompareTo(b.range); }); break;
            }
            groups.Sort();
            if (!string.IsNullOrEmpty(AvionicsSettings.GPSGroupFilter) && !groups.Contains(AvionicsSettings.GPSGroupFilter))
                AvionicsSettings.GPSGroupFilter = "";
        }

        static void CycleFilter()
        {
            if (groups.Count == 0) { AvionicsSettings.GPSGroupFilter = ""; return; }
            int i = groups.IndexOf(AvionicsSettings.GPSGroupFilter);
            AvionicsSettings.GPSGroupFilter = i + 1 < groups.Count ? groups[i + 1] : "";
        }

        public static string FormatCoords(Vector3d c) => string.Format(CultureInfo.InvariantCulture, "{0:F6}, {1:F6}, {2:F1}", c.x, c.y, c.z);

        static void AddFromText(Vessel own)
        {
            pasteError = "";
            var parts = pasteText.Replace(";", ",").Split(new[] { ',', ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            var nums = new List<double>();
            foreach (var p in parts)
                if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) nums.Add(v);
            if (nums.Count < 2 || nums[0] < -90 || nums[0] > 90 || nums[1] < -180 || nums[1] > 360)
            {
                pasteError = "Use: latitude, longitude[, altitude]";
                return;
            }
            var body = own.mainBody;
            double alt = nums.Count >= 3 ? nums[2] : body.TerrainAltitude(nums[0], nums[1], true);
            if (nums.Count < 3 && body.ocean && alt < 0) alt = 0;
            AvionicsContacts.AddGPS(new Vector3d(nums[0], nums[1], alt), "Pasted");
            pasteText = "";
        }
    }
}
