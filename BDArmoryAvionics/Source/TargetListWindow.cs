using UnityEngine;

using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// One list of everything the vessel knows about (radar, IRST, data link, team reports, targeting camera, GPS).
    /// Selecting a target here drives the radar lock, the weapon manager target and the targeting camera.
    /// </summary>
    public static class TargetListWindow
    {
        public const int WindowID = 71730302;
        static Vector2 scroll;

        static readonly float[] colX = [6, 168, 206, 340, 406, 466];
        const float rowH = 20;

        public static void Draw(int id)
        {
            var rect = AvionicsSettings.TargetListRect;
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle))
            {
                AvionicsSettings.ShowTargetList = false;
                return;
            }
            var btn = BDArmorySetup.ButtonStyle;
            var sel = BDArmorySetup.SelectedButtonStyle;

            // Filters
            float y = 22;
            AvionicsSettings.TargetListShowFriendlies = GUI.Toggle(new Rect(6, y, 90, 20), AvionicsSettings.TargetListShowFriendlies, "Friendlies", AvionicsSettings.TargetListShowFriendlies ? sel : btn);
            AvionicsSettings.TargetListShowMissiles = GUI.Toggle(new Rect(98, y, 80, 20), AvionicsSettings.TargetListShowMissiles, "Missiles", AvionicsSettings.TargetListShowMissiles ? sel : btn);
            AvionicsSettings.TargetListShowGPS = GUI.Toggle(new Rect(180, y, 60, 20), AvionicsSettings.TargetListShowGPS, "GPS", AvionicsSettings.TargetListShowGPS ? sel : btn);
            string keys = $"Keys: {Key(AvionicsSettings.KeyPrevTarget)}/{Key(AvionicsSettings.KeyNextTarget)} cycle · {Key(AvionicsSettings.KeyLockTarget)} lock · {Key(AvionicsSettings.KeySlewCamera)} camera";
            AvionicsDraw.ColorLabel(new Rect(250, y + 4, rect.width - 256, 16), keys, AvionicsDraw.LabelRight, AvionicsDraw.ScopeText);

            // Header
            y += 26;
            var hdr = AvionicsDraw.Header;
            GUI.Label(new Rect(colX[0], y, 150, 16), "TARGET", hdr);
            GUI.Label(new Rect(colX[1], y, 36, 16), "IFF", hdr);
            GUI.Label(new Rect(colX[2], y, 130, 16), "SEEN BY", hdr);
            GUI.Label(new Rect(colX[3], y, 64, 16), "RANGE", hdr);
            GUI.Label(new Rect(colX[4], y, 56, 16), "ALT", hdr);
            GUI.Label(new Rect(colX[5], y, 70, 16), "CLOSING", hdr);
            y += 18;

            // Rows
            float listH = rect.height - y - 62;
            int count = 0;
            foreach (var c in AvionicsContacts.Contacts) if (Visible(c)) count++;
            var view = new Rect(0, 0, rect.width - 28, Mathf.Max(count * rowH, listH - 4));
            scroll = GUI.BeginScrollView(new Rect(4, y, rect.width - 8, listH), scroll, view);
            float ry = 0;
            string selectedKey = AvionicsContacts.SelectedKey;
            if (count == 0)
                AvionicsDraw.ColorLabel(new Rect(6, 6, view.width, 18), AvionicsContacts.WM == null ? "No weapon manager on this vessel." : "No contacts. Turn on a radar or IRST, or add GPS points.", AvionicsDraw.Label, AvionicsDraw.ScopeText);
            foreach (var c in AvionicsContacts.Contacts)
            {
                if (!Visible(c)) continue;
                Rect row = new(0, ry, view.width, rowH);
                bool isSel = c.Key == selectedKey;
                if (GUI.Button(row, "", isSel ? sel : GUIStyle.none)) AvionicsContacts.Select(c);
                Color col = c.IFFColor;
                string marker = c.activeLock ? "## " : c.radarLocked ? "# " : isSel ? "> " : "  ";
                AvionicsDraw.ColorLabel(new Rect(colX[0] - 4, ry + 3, 160, 16), marker + Trim(c.name, 20), AvionicsDraw.Label, isSel ? AvionicsDraw.LockBox : col);
                AvionicsDraw.ColorLabel(new Rect(colX[1] - 4, ry + 3, 36, 16), c.IsGPS ? "GND" : c.isMissile ? "MSL" : c.IFFString, AvionicsDraw.Label, col);
                AvionicsDraw.ColorLabel(new Rect(colX[2] - 4, ry + 3, 132, 16), c.SourceString(), AvionicsDraw.Label, AvionicsDraw.HudText);
                AvionicsDraw.ColorLabel(new Rect(colX[3] - 4, ry + 3, 60, 16), AvionicsDraw.FormatRange(c.range), AvionicsDraw.LabelRight, AvionicsDraw.HudText);
                AvionicsDraw.ColorLabel(new Rect(colX[4] - 4, ry + 3, 52, 16), c.IsGPS ? "gnd" : AvionicsDraw.FormatAlt(c.altitude), AvionicsDraw.LabelRight, AvionicsDraw.HudText);
                AvionicsDraw.ColorLabel(new Rect(colX[5] - 4, ry + 3, 60, 16), c.IsGPS ? "" : $"{c.closure:+0;-0;0} m/s", AvionicsDraw.LabelRight, c.closure > 50 ? AvionicsDraw.Hostile : AvionicsDraw.HudText);
                ry += rowH;
            }
            GUI.EndScrollView();

            // Actions for the selected contact
            y = rect.height - 56;
            var s = AvionicsContacts.Selected;
            GUI.enabled = s != null;
            float bx = 6;
            if (s == null || !s.IsGPS)
            {
                if (GUI.Button(new Rect(bx, y, 70, 22), "Lock", btn)) AvionicsContacts.Lock(s); bx += 72;
                GUI.enabled = s != null && s.radarLocked;
                if (GUI.Button(new Rect(bx, y, 70, 22), "Unlock", btn)) AvionicsContacts.Unlock(s); bx += 72;
                GUI.enabled = s != null;
            }
            else
            {
                if (GUI.Button(new Rect(bx, y, 142, 22), "Set as GPS target", btn)) AvionicsContacts.SetGPSTarget(s); bx += 144;
            }
            if (GUI.Button(new Rect(bx, y, 100, 22), "Point camera", btn)) AvionicsContacts.SlewCamera(s); bx += 102;
            GUI.enabled = s != null && !s.IsGPS;
            if (GUI.Button(new Rect(bx, y, 90, 22), "Save GPS", btn)) AvionicsContacts.MakeGPS(s); bx += 92;
            GUI.enabled = true;
            if (s != null)
                AvionicsDraw.ColorLabel(new Rect(bx + 4, y + 4, rect.width - bx - 10, 16), $"{Trim(s.name, 18)} · {AvionicsDraw.FormatRange(s.range)}", AvionicsDraw.Label, AvionicsDraw.HudText);

            if (Time.time - AvionicsContacts.LastMessageTime < 4)
                AvionicsDraw.ColorLabel(new Rect(8, rect.height - 28, rect.width - 16, 16), AvionicsContacts.LastMessage, AvionicsDraw.Label, AvionicsDraw.LockBox);

            DrawResizeHandle(ref AvionicsSettings.TargetListRect, 420, 180);
            GUIUtils.DragWindow();
        }

        static bool Visible(Contact c)
        {
            if (c.IsGPS) return AvionicsSettings.TargetListShowGPS;
            if (c.iff == IFF.Friendly && !AvionicsSettings.TargetListShowFriendlies) return false;
            if (c.isMissile && !AvionicsSettings.TargetListShowMissiles) return false;
            return true;
        }

        static string Trim(string s, int n) => s == null ? "" : s.Length > n ? s.Substring(0, n - 2) + ".." : s;
        static string Key(KeyCode k) => k == KeyCode.None ? "-" : k.ToString().Replace("Keypad", "Num");

        // Simple resize handle shared by the avionics windows
        static bool resizing;
        static Rect resizingRect;
        public static void DrawResizeHandle(ref Rect windowRect, float minW, float minH)
        {
            Rect handle = new(windowRect.width - 18, windowRect.height - 18, 16, 16);
            GUI.DrawTexture(handle, GUIUtils.resizeTexture, ScaleMode.StretchToFill, true);
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && handle.Contains(e.mousePosition))
            {
                resizing = true;
                resizingRect = windowRect;
                e.Use();
            }
            else if (resizing && e.type == EventType.MouseDrag && resizingRect.position == windowRect.position)
            {
                windowRect.width = Mathf.Max(minW, windowRect.width + e.delta.x);
                windowRect.height = Mathf.Max(minH, windowRect.height + e.delta.y);
                resizingRect = windowRect;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && resizing)
            {
                resizing = false;
                e.Use();
            }
        }
        public static void CancelResize() => resizing = false;
    }
}
