using System.Collections.Generic;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// 3D radar scope: a tilted disc centred on the active vessel with contacts on altitude stalks.
    /// Drag to orbit, scroll to change range, click a contact to select and lock it.
    /// </summary>
    public static class Radar3DWindow
    {
        public const int WindowID = 71730301;

        static bool mouseDownInScope, dragging;
        static Vector2 dragStart, lastMouse;
        static readonly List<(Vector2 pos, string key)> picks = new();
        static readonly Vector2[] ring = new Vector2[65];

        // Projection state for the current frame
        static Vector2 center;
        static float pxPerMeter, sinE, cosE, sinY, cosY, altScale;
        static Vector3 fwd, up, right;

        public static void Draw(int id)
        {
            var rect = AvionicsSettings.Radar3DRect;
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle))
            {
                AvionicsSettings.ShowRadar3D = false;
                return;
            }

            // Controls row
            float x = 6, y = 22, h = 20;
            var btn = BDArmorySetup.ButtonStyle;
            if (GUI.Button(new Rect(x, y, 22, h), "-", btn)) AvionicsSettings.Radar3DRangeIndex = Mathf.Max(0, AvionicsSettings.Radar3DRangeIndex - 1);
            x += 24;
            AvionicsDraw.ColorLabel(new Rect(x, y + 3, 56, h), AvionicsDraw.FormatRange(Range), AvionicsDraw.LabelCenter, AvionicsDraw.HudText);
            x += 58;
            if (GUI.Button(new Rect(x, y, 22, h), "+", btn)) AvionicsSettings.Radar3DRangeIndex = Mathf.Min(AvionicsSettings.Radar3DRanges.Length - 1, AvionicsSettings.Radar3DRangeIndex + 1);
            x += 28;
            if (GUI.Button(new Rect(x, y, 50, h), $"ALT ×{AvionicsSettings.AltScales[AvionicsSettings.Radar3DAltScaleIndex]:0}", btn))
                AvionicsSettings.Radar3DAltScaleIndex = (AvionicsSettings.Radar3DAltScaleIndex + 1) % AvionicsSettings.AltScales.Length;
            x += 52;
            if (GUI.Button(new Rect(x, y, 46, h), AvionicsSettings.Radar3DNorthUp ? "N-UP" : "HDG", btn)) AvionicsSettings.Radar3DNorthUp = !AvionicsSettings.Radar3DNorthUp;
            x += 48;
            AvionicsSettings.Radar3DShowIRST = GUI.Toggle(new Rect(x, y, 42, h), AvionicsSettings.Radar3DShowIRST, "IRST", AvionicsSettings.Radar3DShowIRST ? BDArmorySetup.SelectedButtonStyle : btn);
            x += 44;
            AvionicsSettings.Radar3DShowTeam = GUI.Toggle(new Rect(x, y, 46, h), AvionicsSettings.Radar3DShowTeam, "TEAM", AvionicsSettings.Radar3DShowTeam ? BDArmorySetup.SelectedButtonStyle : btn);
            x += 48;
            AvionicsSettings.WorldContactMarkers = GUI.Toggle(new Rect(x, y, 52, h), AvionicsSettings.WorldContactMarkers, "WORLD", AvionicsSettings.WorldContactMarkers ? BDArmorySetup.SelectedButtonStyle : btn);
            x += 54;
            if (x + 44 < rect.width && GUI.Button(new Rect(x, y, 44, h), "View", btn)) { AvionicsSettings.Radar3DYaw = 0; AvionicsSettings.Radar3DElevation = 30; }

            TargetListWindow.DrawResizeHandle(ref AvionicsSettings.Radar3DRect, 380, 360);
            Rect scope = new(6, 46, rect.width - 12, rect.height - 66);
            HandleInput(scope);
            DrawScope(scope);

            GUIUtils.DragWindow();
        }

        static float Range => AvionicsSettings.Radar3DRanges[AvionicsSettings.Radar3DRangeIndex];

        static void HandleInput(Rect scope)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && scope.Contains(e.mousePosition))
            {
                mouseDownInScope = true;
                dragging = false;
                dragStart = lastMouse = e.mousePosition;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && mouseDownInScope)
            {
                if (!dragging && (e.mousePosition - dragStart).sqrMagnitude > 9) dragging = true;
                if (dragging)
                {
                    Vector2 d = e.mousePosition - lastMouse;
                    AvionicsSettings.Radar3DYaw = Mathf.Repeat(AvionicsSettings.Radar3DYaw + d.x * 0.4f, 360);
                    AvionicsSettings.Radar3DElevation = Mathf.Clamp(AvionicsSettings.Radar3DElevation + d.y * 0.3f, 0, 90);
                }
                lastMouse = e.mousePosition;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && mouseDownInScope)
            {
                if (!dragging) Pick(e.mousePosition);
                mouseDownInScope = false;
                dragging = false;
                e.Use();
            }
            else if (e.type == EventType.ScrollWheel && scope.Contains(e.mousePosition))
            {
                int step = e.delta.y > 0 ? 1 : -1;
                AvionicsSettings.Radar3DRangeIndex = Mathf.Clamp(AvionicsSettings.Radar3DRangeIndex + step, 0, AvionicsSettings.Radar3DRanges.Length - 1);
                e.Use();
            }
        }

        /// <summary>Called by the core addon when the mouse button is released outside the window.</summary>
        public static void CancelDrag() { mouseDownInScope = false; dragging = false; }

        static void Pick(Vector2 mouse)
        {
            float best = 14 * 14;
            string bestKey = null;
            for (int i = 0; i < picks.Count; i++)
            {
                float d = (picks[i].pos - mouse).sqrMagnitude;
                if (d < best) { best = d; bestKey = picks[i].key; }
            }
            if (bestKey == null) return;
            foreach (var c in AvionicsContacts.Contacts)
                if (c.Key == bestKey)
                {
                    if (c.IsGPS) { AvionicsContacts.Select(c); AvionicsContacts.SetGPSTarget(c); }
                    else AvionicsContacts.Lock(c);
                    return;
                }
        }

        static Vector2 Project(Vector3 local)
        {
            float xr = local.x * cosY - local.z * sinY;
            float zr = local.x * sinY + local.z * cosY;
            return new Vector2(center.x + xr * pxPerMeter, center.y - (zr * sinE + local.y * altScale * cosE) * pxPerMeter);
        }

        static Vector3 ToLocal(Vector3 worldOffset) => new(Vector3.Dot(worldOffset, right), Vector3.Dot(worldOffset, up), Vector3.Dot(worldOffset, fwd));

        static void DrawScope(Rect scope)
        {
            var own = AvionicsContacts.Own;
            if (Event.current.type != EventType.Repaint) return;
            picks.Clear();

            if (!AvionicsDraw.Begin()) return;
            AvionicsDraw.FilledRect(scope, AvionicsDraw.ScopeBackground);

            if (own == null || AvionicsContacts.WM == null)
            {
                AvionicsDraw.End();
                AvionicsDraw.ColorLabel(new Rect(scope.x, scope.center.y - 8, scope.width, 20), "No weapon manager on this vessel", AvionicsDraw.LabelCenter, AvionicsDraw.ScopeText);
                return;
            }

            // Reference frame: heading-stabilised (or north-up), level with the horizon
            up = (Vector3)own.upAxis;
            Vector3 north = VectorUtils.GetNorthVector(own.CoM, own.mainBody);
            Vector3 heading = Vector3.ProjectOnPlane(own.ReferenceTransform.up, up);
            if (heading.sqrMagnitude < 1e-4f) heading = Vector3.ProjectOnPlane(-own.ReferenceTransform.forward, up);
            if (heading.sqrMagnitude < 1e-4f) heading = north;
            heading.Normalize();
            fwd = AvionicsSettings.Radar3DNorthUp ? north : heading;
            right = Vector3.Cross(up, fwd).normalized;

            float yaw = AvionicsSettings.Radar3DYaw * Mathf.Deg2Rad;
            float elev = AvionicsSettings.Radar3DElevation * Mathf.Deg2Rad;
            sinY = Mathf.Sin(yaw); cosY = Mathf.Cos(yaw);
            sinE = Mathf.Sin(elev); cosE = Mathf.Cos(elev);
            altScale = AvionicsSettings.AltScales[AvionicsSettings.Radar3DAltScaleIndex];
            center = scope.center + new Vector2(0, scope.height * 0.06f);
            float radiusPx = Mathf.Min(scope.width * 0.5f - 14, scope.height * 0.5f - 14);
            float range = Range;
            pxPerMeter = radiusPx / range;

            // Range rings and cross
            for (int r = 1; r <= 4; r++)
            {
                float rr = range * r / 4f;
                for (int i = 0; i <= 64; i++)
                {
                    float t = i * Mathf.PI * 2 / 64;
                    ring[i] = Project(new Vector3(Mathf.Cos(t) * rr, 0, Mathf.Sin(t) * rr));
                }
                AvionicsDraw.Polyline(ring, 65, 1, r == 4 ? AvionicsDraw.Grid : AvionicsDraw.GridFaint, false);
            }
            AvionicsDraw.Line(Project(new Vector3(-range, 0, 0)), Project(new Vector3(range, 0, 0)), 1, AvionicsDraw.GridFaint);
            AvionicsDraw.Line(Project(new Vector3(0, 0, -range)), Project(new Vector3(0, 0, range)), 1, AvionicsDraw.GridFaint);

            // Own ship, pointing along the vessel heading
            Vector3 headingLocal = ToLocal(heading);
            Vector2 c0 = Project(Vector3.zero), c1 = Project(headingLocal * range * 0.05f);
            float angle = Vector2.SignedAngle(Vector2.down, c1 - c0);
            if ((c1 - c0).sqrMagnitude < 0.5f) angle = 0;
            AvionicsDraw.Triangle(c0, 7, angle, 2, AvionicsDraw.HudText);

            // Contacts
            int beyond = 0;
            var labels = new List<(Vector2 pos, Contact c)>();
            var selectedKey = AvionicsContacts.SelectedKey;
            Vector3 ownPos = own.CoM;
            foreach (var c in AvionicsContacts.Contacts)
            {
                if (!ShowOnScope(c)) continue;
                Vector3 local = ToLocal(c.position - ownPos);
                float horiz = new Vector2(local.x, local.z).magnitude;
                if (horiz > range) { beyond++; continue; }

                Vector2 basePt = Project(new Vector3(local.x, 0, local.z));
                Vector2 head = Project(local);
                head.x = Mathf.Clamp(head.x, scope.xMin + 4, scope.xMax - 4);
                head.y = Mathf.Clamp(head.y, scope.yMin + 4, scope.yMax - 4);
                Color col = c.IFFColor;
                if (c.IsGPS) col = new Color(col.r, col.g, col.b, 0.6f);

                // Stalk: solid above, dashed below
                if (local.y >= 0) AvionicsDraw.Line(basePt, head, 1.5f, col);
                else AvionicsDraw.DashedLine(basePt, head, 1.5f, col, 3, 3);
                AvionicsDraw.Circle(basePt, 2, 1, col, 8);

                // Heading line
                if (!c.IsGPS && c.velocity.sqrMagnitude > 1)
                {
                    Vector3 vLocal = ToLocal(c.velocity);
                    Vector2 vEnd = Project(local + vLocal.normalized * range * 0.02f);
                    Vector2 dir = (vEnd - head);
                    if (dir.sqrMagnitude > 0.01f)
                    {
                        float len = Mathf.Lerp(8, 26, Mathf.Clamp01(c.velocity.magnitude / 700f));
                        AvionicsDraw.Line(head, head + dir.normalized * len, 1.5f, col);
                    }
                }

                // Symbol
                if (c.IsGPS) { AvionicsDraw.Line(head + new Vector2(-4, 0), head + new Vector2(4, 0), 1.5f, col); AvionicsDraw.Line(head + new Vector2(0, -4), head + new Vector2(0, 4), 1.5f, col); }
                else if (c.isMissile) AvionicsDraw.Triangle(head, 6, 0, 2, col);
                else if (c.iff == IFF.Hostile) AvionicsDraw.Diamond(head, 6, 2, col);
                else if (c.iff == IFF.Friendly) AvionicsDraw.Circle(head, 5, 2, col, 16);
                else AvionicsDraw.Box(head, 4.5f, 2, col);

                if (c.radarLocked) AvionicsDraw.Box(head, 10, 1.5f, AvionicsDraw.LockBox);
                if (c.activeLock) AvionicsDraw.Box(head, 13, 1.5f, AvionicsDraw.LockBox);
                if (c.Key == selectedKey) AvionicsDraw.Circle(head, 16, 1, Color.white, 24);

                picks.Add((head, c.Key));
                if (!c.IsGPS || c.Key == selectedKey) labels.Add((head, c));
            }

            // Threats: strobes on the outer ring at the threat's bearing; incoming missiles get a line to own ship.
            var threatLabels = new List<(Vector2 pos, Threat t)>();
            if (AvionicsSettings.Radar3DShowThreats)
                foreach (var t in ThreatWarning.Threats)
                {
                    Vector3 local = ToLocal(t.position - ownPos);
                    Vector2 flat = new(local.x, local.z);
                    if (flat.sqrMagnitude < 1) continue;
                    Vector2 d = flat.normalized;
                    Vector2 outer = Project(new Vector3(d.x * range * 1.08f, 0, d.y * range * 1.08f));
                    Vector2 inner = Project(new Vector3(d.x * range * 0.9f, 0, d.y * range * 0.9f));
                    bool blink = (t.isMissile || t.type == RadarWarningReceiver.RWRThreatTypes.MissileLaunch) && Mathf.Repeat(Time.time, 0.4f) < 0.2f;
                    AvionicsDraw.Line(inner, outer, blink ? 4 : 2.5f, t.Color);
                    if (t.isMissile && flat.magnitude <= range)
                        AvionicsDraw.DashedLine(Project(local), Project(Vector3.zero), 1.5f, AvionicsDraw.Missile, 4, 3);
                    threatLabels.Add((outer, t));
                }
            AvionicsDraw.End();
            foreach (var (pos, t) in threatLabels)
            {
                string tti = t.timeToImpact > 0 && t.timeToImpact < 999 ? $" {t.timeToImpact:F0}s" : "";
                AvionicsDraw.ShadowLabel(new Rect(pos.x - 40, pos.y - 16, 80, 13), t.Kind + tti, AvionicsDraw.LabelCenter, t.Color);
            }
            DrawWeaponEstimate(scope, own);

            // Labels (drawn after the GL batch)
            var small = AvionicsDraw.LabelSmall;
            for (int r = 1; r <= 4; r++)
            {
                Vector2 p = Project(new Vector3(range * r / 4f, 0, 0));
                AvionicsDraw.ColorLabel(new Rect(p.x + 3, p.y - 12, 60, 14), AvionicsDraw.FormatRange(range * r / 4f), small, AvionicsDraw.ScopeText);
            }
            Vector2 nPos = Project(ToLocal(VectorUtils.GetNorthVector(own.CoM, own.mainBody)) * range * 1.04f);
            AvionicsDraw.ColorLabel(new Rect(nPos.x - 5, nPos.y - 8, 12, 14), "N", small, AvionicsDraw.HudText);

            int shown = 0;
            foreach (var (pos, c) in labels)
            {
                bool important = c.Key == selectedKey || c.radarLocked || c.isMissile;
                if (!important && shown >= 10) continue;
                shown++;
                string name = c.name.Length > 14 ? c.name.Substring(0, 14) : c.name;
                string info = c.IsGPS ? "GPS" : $"{AvionicsDraw.FormatAlt(c.altitude)}  {c.velocity.magnitude:0} m/s";
                float lx = pos.x + 12;
                if (lx + 110 > scope.xMax) lx = pos.x - 122;
                AvionicsDraw.ShadowLabel(new Rect(lx, pos.y - 13, 110, 13), name, small, c.IFFColor);
                AvionicsDraw.ShadowLabel(new Rect(lx, pos.y, 110, 13), info, small, c.IFFColor);
            }

            AvionicsDraw.ColorLabel(new Rect(scope.x + 6, scope.yMax - 16, 260, 14), "drag to orbit · scroll to zoom · click to lock", small, AvionicsDraw.ScopeText);
            string beyondText = beyond > 0 ? $"+{beyond} beyond range" : "";
            AvionicsDraw.ColorLabel(new Rect(scope.xMax - 146, scope.yMax - 16, 140, 14), beyondText, AvionicsDraw.LabelRight, AvionicsDraw.ScopeText);
            if (AvionicsContacts.WM.vesselRadarData == null)
                AvionicsDraw.ColorLabel(new Rect(scope.x + 6, scope.y + 4, 260, 14), "No radar or IRST on this vessel", small, AvionicsDraw.Unknown);
        }

        /// <summary>Selected missile against the selected contact: predicted time of flight and max range from the flight estimator.</summary>
        static void DrawWeaponEstimate(Rect scope, Vessel own)
        {
            var wm = AvionicsContacts.WM;
            var m = wm != null ? wm.CurrentMissile : null;
            var c = AvionicsContacts.Selected;
            if (m == null || c == null || c.IsGPS) return;
            var est = MissileEstimator.Estimate(m, own.CoM, own.Velocity(), c.position, c.velocity, withMaxRange: true, targetId: c.vessel != null ? c.vessel.persistentId : 0);
            if (!est.valid) return;
            string name = m.GetShortName();
            if (name.Length > 16) name = name.Substring(0, 16);
            string inRange = est.canReach ? "IN RANGE" : "OUT OF RANGE";
            string text = $"{name}  Rmax {AvionicsDraw.FormatRange(est.maxRange)}  " +
                          (est.canReach ? $"TOF {est.timeOfFlight:F0}s  {est.arrivalSpeed:F0} m/s" : "") + $"  {inRange}{(est.approximate ? " (est.)" : "")}";
            AvionicsDraw.ShadowLabel(new Rect(scope.x + 6, scope.y + 18, scope.width - 12, 14), text, AvionicsDraw.LabelSmall, est.canReach ? AvionicsDraw.Friendly : AvionicsDraw.Unknown);
        }

        static bool ShowOnScope(Contact c)
        {
            if (c.IsGPS) return true;
            var s = c.sources;
            if ((s & (ContactSource.Radar | ContactSource.Datalink | ContactSource.TGP)) != 0) return true;
            if ((s & ContactSource.IRST) != 0 && AvionicsSettings.Radar3DShowIRST) return true;
            if ((s & ContactSource.Team) != 0 && AvionicsSettings.Radar3DShowTeam) return true;
            return false;
        }
    }
}
