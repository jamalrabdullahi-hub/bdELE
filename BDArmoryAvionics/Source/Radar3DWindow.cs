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

        // Centre: null = own ship, else a contact key (right-click a contact to centre on it).
        static string centerKey;
        // Track history: contact key -> recent positions relative to the planet (one per second, 30 s).
        static readonly Dictionary<string, List<Vector3>> trails = new();
        static float nextTrail;
        const int TrailPoints = 30;
        public static bool ShowTrails = true, ShowPredict = true, ShowGroups = true, ShowIntercept = true;
        const float GroupLinkM = 6000f; // contacts closer than this to any member join the group

        /// <summary>A formation: aircraft of one side flying close together.</summary>
        class Group { public List<Contact> members = new(); public Vector3 centre, velocity; public float spread; public double altMin, altMax; public IFF iff; public string Key => "grp:" + members[0].Key; }
        static readonly List<Group> groups = new();

        static void BuildGroups()
        {
            groups.Clear();
            var pool = new List<Contact>();
            foreach (var c in AvionicsContacts.Contacts)
                if (!c.IsGPS && !c.isMissile && ShowOnScope(c) && (c.iff == IFF.Hostile || c.iff == IFF.Unknown)) pool.Add(c);
            while (pool.Count > 0)
            {
                var g = new Group(); g.members.Add(pool[0]); pool.RemoveAt(0);
                for (int i = 0; i < g.members.Count; i++)
                    for (int j = pool.Count - 1; j >= 0; j--)
                        if (pool[j].iff == g.members[0].iff && (pool[j].position - g.members[i].position).sqrMagnitude < GroupLinkM * GroupLinkM) { g.members.Add(pool[j]); pool.RemoveAt(j); }
                if (g.members.Count < 2) continue;
                g.members.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key)); // stable key while the group holds together
                g.iff = g.members[0].iff; g.altMin = double.MaxValue; g.altMax = double.MinValue;
                foreach (var m in g.members) { g.centre += m.position; g.velocity += m.velocity; g.altMin = System.Math.Min(g.altMin, m.altitude); g.altMax = System.Math.Max(g.altMax, m.altitude); }
                g.centre /= g.members.Count; g.velocity /= g.members.Count;
                foreach (var m in g.members) g.spread = Mathf.Max(g.spread, (m.position - g.centre).magnitude);
                groups.Add(g);
            }
        }

        static Group GroupOf(string key)
        {
            foreach (var g in groups) { if (g.Key == key) return g; foreach (var m in g.members) if (m.Key == key) return g; }
            return null;
        }

        /// <summary>Time for own ship at its current speed to meet a target flying straight (|P + V t| = s t); -1 if it can't.</summary>
        static float InterceptTime(Vector3 rel, Vector3 vel, float speed)
        {
            float a = vel.sqrMagnitude - speed * speed, b = 2 * Vector3.Dot(rel, vel), c = rel.sqrMagnitude;
            if (Mathf.Abs(a) < 1e-3f) return b < 0 ? -c / b : -1;
            float d = b * b - 4 * a * c;
            if (d < 0) return -1;
            float sq = Mathf.Sqrt(d), t1 = (-b - sq) / (2 * a), t2 = (-b + sq) / (2 * a);
            float t = Mathf.Min(t1, t2) > 0 ? Mathf.Min(t1, t2) : Mathf.Max(t1, t2);
            return t > 0 ? t : -1;
        }
        static readonly Vector2[] trailPts = new Vector2[TrailPoints + 1];

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
            AvionicsSettings.Radar3DShowDebris = GUI.Toggle(new Rect(x, y, 40, h), AvionicsSettings.Radar3DShowDebris, "DEB", AvionicsSettings.Radar3DShowDebris ? BDArmorySetup.SelectedButtonStyle : btn);
            x += 42;
            AvionicsSettings.WorldContactMarkers = GUI.Toggle(new Rect(x, y, 52, h), AvionicsSettings.WorldContactMarkers, "WORLD", AvionicsSettings.WorldContactMarkers ? BDArmorySetup.SelectedButtonStyle : btn);
            x += 54;
            if (x + 44 < rect.width && GUI.Button(new Rect(x, y, 44, h), "View", btn)) { AvionicsSettings.Radar3DYaw = 0; AvionicsSettings.Radar3DElevation = 30; }

            // Second row: centre and overlays
            x = 6; y += 22;
            var sel = BDArmorySetup.SelectedButtonStyle;
            var cc = CenterContact();
            if (centerKey != null && cc == null && (!centerKey.StartsWith("grp:") || GroupOf(centerKey) == null)) centerKey = null; // lost track: back to own ship
            if (GUI.Button(new Rect(x, y, 46, h), "OWN", centerKey == null ? sel : btn)) centerKey = null;
            x += 48;
            if (GUI.Button(new Rect(x, y, 40, h), "SEL", centerKey != null && centerKey == AvionicsContacts.SelectedKey ? sel : btn) && AvionicsContacts.Selected != null) centerKey = AvionicsContacts.SelectedKey;
            x += 42;
            if (GUI.Button(new Rect(x, y, 22, h), "<", btn)) CycleCenter(-1);
            x += 24;
            if (GUI.Button(new Rect(x, y, 22, h), ">", btn)) CycleCenter(1);
            x += 26;
            ShowTrails = GUI.Toggle(new Rect(x, y, 50, h), ShowTrails, "TRAIL", ShowTrails ? sel : btn);
            x += 52;
            ShowPredict = GUI.Toggle(new Rect(x, y, 46, h), ShowPredict, "PRED", ShowPredict ? sel : btn);
            x += 48;
            AvionicsSettings.Radar3DShowThreats = GUI.Toggle(new Rect(x, y, 46, h), AvionicsSettings.Radar3DShowThreats, "THRT", AvionicsSettings.Radar3DShowThreats ? sel : btn);
            x += 50;
            ShowGroups = GUI.Toggle(new Rect(x, y, 40, h), ShowGroups, "GRP", ShowGroups ? sel : btn);
            x += 42;
            ShowIntercept = GUI.Toggle(new Rect(x, y, 34, h), ShowIntercept, "INT", ShowIntercept ? sel : btn);
            x += 38;
            var cg = centerKey != null && centerKey.StartsWith("grp:") ? GroupOf(centerKey) : null;
            string cname = cg != null ? $"group of {cg.members.Count}" : cc != null ? cc.name : "own ship";
            if (cname.Length > 16) cname = cname.Substring(0, 16);
            AvionicsDraw.ColorLabel(new Rect(x, y + 3, Mathf.Max(10, rect.width - x - 6), h), "CTR " + cname, AvionicsDraw.LabelSmall, cc != null ? cc.IFFColor : AvionicsDraw.HudText);

            TargetListWindow.DrawResizeHandle(ref AvionicsSettings.Radar3DRect, 380, 360);
            Rect scope = new(6, 68, rect.width - 12, rect.height - 88);
            HandleInput(scope);
            DrawScope(scope);

            GUIUtils.DragWindow();
        }

        static float Range => AvionicsSettings.Radar3DRanges[AvionicsSettings.Radar3DRangeIndex];

        // ---- Debris ages: only debris that appears after the scene loads is shown, for DebrisShowTime seconds ----
        const float DebrisShowTime = 5f, DebrisFadeTime = 2f, DebrisBurstTime = 0.7f;
        static readonly Dictionary<uint, float> debrisSeen = new(); // persistentId -> time first seen (-inf = was already there)
        static float lastLevelTime = float.MaxValue;
        static bool baselined;

        static void UpdateDebrisAges()
        {
            // New scene (or reload): forget everything and treat the debris already lying around as old.
            if (Time.timeSinceLevelLoad < lastLevelTime) { debrisSeen.Clear(); baselined = false; }
            lastLevelTime = Time.timeSinceLevelLoad;
            foreach (var v in FlightGlobals.Vessels)
            {
                if (v == null || v.vesselType != VesselType.Debris || debrisSeen.ContainsKey(v.persistentId)) continue;
                debrisSeen[v.persistentId] = baselined ? Time.time : float.NegativeInfinity;
            }
            baselined = true;
        }

        static void HandleInput(Rect scope)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 1 && scope.Contains(e.mousePosition))
            {
                string k = PickKey(e.mousePosition);
                centerKey = k != null && k != "own" && k != centerKey ? k : null; // right-click a contact: centre on it; again, or empty space: own ship
                e.Use();
            }
            else if (e.type == EventType.MouseDown && e.button == 0 && scope.Contains(e.mousePosition))
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

        static string PickKey(Vector2 mouse)
        {
            float best = 14 * 14;
            string bestKey = null;
            for (int i = 0; i < picks.Count; i++)
            {
                float d = (picks[i].pos - mouse).sqrMagnitude;
                if (d < best) { best = d; bestKey = picks[i].key; }
            }
            return bestKey;
        }

        static Contact CenterContact()
        {
            if (centerKey == null) return null;
            foreach (var c in AvionicsContacts.Contacts) if (c.Key == centerKey) return c;
            return null; // group centres are handled by GroupOf
        }

        static void CycleCenter(int step)
        {
            var list = new List<Contact>();
            foreach (var c in AvionicsContacts.Contacts) if (!c.IsGPS && ShowOnScope(c)) list.Add(c);
            if (list.Count == 0) { centerKey = null; return; }
            var keys = new List<string>();
            foreach (var g in groups) keys.Add(g.Key);
            foreach (var c in list) keys.Add(c.Key);
            int i = keys.IndexOf(centerKey);
            i = i < 0 ? (step > 0 ? 0 : keys.Count - 1) : i + step;
            centerKey = i < 0 || i >= keys.Count ? null : keys[i]; // stepping past either end returns to own ship
        }

        static void UpdateTrails(Vessel own)
        {
            if (Time.time < nextTrail) return;
            nextTrail = Time.time + 1f;
            Vector3 body = own.mainBody.position;
            var live = new HashSet<string>();
            void Add(string k, Vector3 pos)
            {
                live.Add(k);
                if (!trails.TryGetValue(k, out var l)) trails[k] = l = new List<Vector3>();
                l.Add(pos - body);
                if (l.Count > TrailPoints) l.RemoveAt(0);
            }
            foreach (var c in AvionicsContacts.Contacts) if (!c.IsGPS) Add(c.Key, c.position);
            Add("own", own.CoM);
            var dead = new List<string>();
            foreach (var k in trails.Keys) if (!live.Contains(k)) dead.Add(k);
            foreach (var k in dead) trails.Remove(k);
        }

        static void DrawTrail(string key, Vector3 centre, Vector3 body, Color col)
        {
            if (!trails.TryGetValue(key, out var l) || l.Count < 2) return;
            int n = 0;
            for (int i = 0; i < l.Count; i++) trailPts[n++] = Project(ToLocal(l[i] + body - centre));
            for (int i = 1; i < n; i++) CLine(trailPts[i - 1], trailPts[i], 1, new Color(col.r, col.g, col.b, col.a * i / n));
        }

        /// <summary>Selected missile's max range against the selected contact, as a dashed ring around own ship.</summary>
        static void DrawRmaxRing(Vessel own, Vector3 ownLocal)
        {
            var wm = AvionicsContacts.WM;
            var m = wm != null ? wm.CurrentMissile : null;
            var c = AvionicsContacts.Selected;
            if (m == null || c == null || c.IsGPS) return;
            var est = MissileEstimator.Estimate(m, own.CoM, own.Velocity(), c.position, c.velocity, withMaxRange: true, targetId: c.vessel != null ? c.vessel.persistentId : 0);
            if (!est.valid || est.maxRange <= 0) return;
            Color col = est.canReach ? AvionicsDraw.Friendly : AvionicsDraw.Unknown;
            col.a = 0.6f;
            for (int i = 0; i <= 64; i++)
            {
                float t = i * Mathf.PI * 2 / 64;
                ring[i] = Project(new Vector3(ownLocal.x + Mathf.Cos(t) * est.maxRange, 0, ownLocal.z + Mathf.Sin(t) * est.maxRange));
            }
            for (int i = 0; i < 64; i += 2) CLine(ring[i], ring[i + 1], 1.5f, col);
        }

        /// <summary>BRAA of the selected contact from own ship: bearing, range, altitude, aspect.</summary>
        static void DrawBRAA(Rect scope, Vessel own)
        {
            var c = AvionicsContacts.Selected;
            if (c == null) return;
            Vector3 upO = (Vector3)own.upAxis;
            Vector3 to = c.position - (Vector3)own.CoM;
            Vector3 north = VectorUtils.GetNorthVector(own.CoM, own.mainBody);
            Vector3 flat = Vector3.ProjectOnPlane(to, upO);
            float brg = Mathf.Repeat(Vector3.SignedAngle(north, flat, upO), 360);
            string aspect = "";
            if (!c.IsGPS && c.velocity.sqrMagnitude > 25)
            {
                float a = Vector3.Angle(Vector3.ProjectOnPlane(c.velocity, upO), -flat); // 0 = flying straight at us
                aspect = a < 30 ? "HOT" : a > 150 ? "COLD" : a < 70 ? "FLANK" : a > 110 ? "DRAG" : "BEAM";
                aspect += $"  Vc {c.closure:0} m/s";
            }
            string n = c.name.Length > 14 ? c.name.Substring(0, 14) : c.name;
            AvionicsDraw.ShadowLabel(new Rect(scope.x + 6, scope.yMax - 32, scope.width - 12, 14),
                $"BRAA {n}: {brg:000}°  {AvionicsDraw.FormatRange(to.magnitude)}  {AvionicsDraw.FormatAlt(c.altitude)}  {aspect}", AvionicsDraw.LabelSmall, c.IFFColor);
        }

        static void Pick(Vector2 mouse)
        {
            string bestKey = PickKey(mouse);
            if (bestKey == null || bestKey == "own" || bestKey.StartsWith("grp:")) return;
            foreach (var c in AvionicsContacts.Contacts)
                if (c.Key == bestKey)
                {
                    if (c.IsGPS) { AvionicsContacts.Select(c); AvionicsContacts.SetGPSTarget(c); }
                    else AvionicsContacts.Lock(c);
                    return;
                }
        }

        // Everything drawn on the scope is clipped to it (trails and prediction lines used to run out over the game view).
        static Rect clip;
        static bool Clip(ref Vector2 a, ref Vector2 b)
        {
            float t0 = 0, t1 = 1, dx = b.x - a.x, dy = b.y - a.y;
            float[] p = { -dx, dx, -dy, dy }, q = { a.x - clip.xMin, clip.xMax - a.x, a.y - clip.yMin, clip.yMax - a.y };
            for (int i = 0; i < 4; i++)
            {
                if (Mathf.Abs(p[i]) < 1e-6f) { if (q[i] < 0) return false; continue; }
                float r = q[i] / p[i];
                if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
            }
            Vector2 a0 = a; a = a0 + (b - a0) * t0; b = a0 + (b - a0) * t1;
            return true;
        }
        static void CLine(Vector2 a, Vector2 b, float w, Color c) { if (Clip(ref a, ref b)) AvionicsDraw.Line(a, b, w, c); }
        static void CDashed(Vector2 a, Vector2 b, float w, Color c, float dash = 4, float gap = 4) { if (Clip(ref a, ref b)) AvionicsDraw.DashedLine(a, b, w, c, dash, gap); }

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

            clip = scope;
            if (!AvionicsDraw.Begin()) return;
            AvionicsDraw.FilledRect(scope, AvionicsDraw.ScopeBackground);

            if (own == null || AvionicsContacts.WM == null)
            {
                AvionicsDraw.End();
                AvionicsDraw.ColorLabel(new Rect(scope.x, scope.center.y - 8, scope.width, 20), "No weapon manager on this vessel", AvionicsDraw.LabelCenter, AvionicsDraw.ScopeText);
                return;
            }

            UpdateTrails(own);
            BuildGroups();
            var cc = CenterContact();
            var cgrp = centerKey != null && centerKey.StartsWith("grp:") ? GroupOf(centerKey) : null;
            if (cgrp != null) centerKey = cgrp.Key; // the lead can change as members come and go
            Vector3 body = own.mainBody.position;
            Vector3 ownPos = cgrp != null ? cgrp.centre : cc != null ? cc.position : (Vector3)own.CoM;

            // Reference frame: heading-stabilised (or north-up), level with the horizon, at the centre
            up = (ownPos - body).normalized;
            Vector3 north = VectorUtils.GetNorthVector(ownPos, own.mainBody);
            Vector3 heading = cgrp != null ? Vector3.ProjectOnPlane(cgrp.velocity, up) : cc != null ? Vector3.ProjectOnPlane(cc.velocity, up) : Vector3.ProjectOnPlane(own.ReferenceTransform.up, up);
            if (heading.sqrMagnitude < 1e-4f && cc == null && cgrp == null) heading = Vector3.ProjectOnPlane(-own.ReferenceTransform.forward, up);
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
            CLine(Project(new Vector3(-range, 0, 0)), Project(new Vector3(range, 0, 0)), 1, AvionicsDraw.GridFaint);
            CLine(Project(new Vector3(0, 0, -range)), Project(new Vector3(0, 0, range)), 1, AvionicsDraw.GridFaint);

            // Own ship, pointing along its heading (at the centre, or where it is when centred on a contact)
            Vector3 ownLocal = ToLocal((Vector3)own.CoM - ownPos);
            Vector3 ownHeading = Vector3.ProjectOnPlane(own.ReferenceTransform.up, up);
            if (ownHeading.sqrMagnitude < 1e-4f) ownHeading = heading;
            Vector3 headingLocal = ToLocal(ownHeading.normalized);
            if (ShowTrails) DrawTrail("own", ownPos, body, new Color(1, 1, 1, 0.45f));
            if (new Vector2(ownLocal.x, ownLocal.z).magnitude <= range)
            {
                Vector2 c0 = Project(ownLocal), c1 = Project(ownLocal + headingLocal * range * 0.05f);
                float angle = Vector2.SignedAngle(Vector2.down, c1 - c0);
                if ((c1 - c0).sqrMagnitude < 0.5f) angle = 0;
                if (cc != null) { CDashed(Project(new Vector3(ownLocal.x, 0, ownLocal.z)), c0, 1.5f, AvionicsDraw.HudText, 3, 3); picks.Add((c0, "own")); }
                AvionicsDraw.Triangle(c0, 7, angle, 2, AvionicsDraw.HudText);
            }
            DrawRmaxRing(own, ownLocal);

            // Contacts
            int beyond = 0;
            var labels = new List<(Vector2 pos, Contact c)>();
            var selectedKey = AvionicsContacts.SelectedKey;
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
                if (ShowTrails && !c.IsGPS) DrawTrail(c.Key, ownPos, body, new Color(col.r, col.g, col.b, 0.45f));
                // Predicted path: where it will be in 30 s at its current velocity (selected, locked, hostile, missiles).
                if (ShowPredict && !c.IsGPS && c.velocity.sqrMagnitude > 25 && (c.Key == selectedKey || c.radarLocked || c.isMissile || c.iff == IFF.Hostile))
                {
                    Vector2 p30 = Project(local + ToLocal(c.velocity * 30f));
                    CDashed(head, p30, 1, new Color(col.r, col.g, col.b, 0.7f), 2, 4);
                    AvionicsDraw.Circle(p30, 2.5f, 1, col, 8);
                }
                if (c.Key == centerKey) AvionicsDraw.Circle(head, 20, 1, AvionicsDraw.HudText, 4);

                // Stalk: solid above, dashed below
                if (local.y >= 0) CLine(basePt, head, 1.5f, col);
                else CDashed(basePt, head, 1.5f, col, 3, 3);
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
                        CLine(head, head + dir.normalized * len, 1.5f, col);
                    }
                }

                // Symbol
                if (c.IsGPS) { CLine(head + new Vector2(-4, 0), head + new Vector2(4, 0), 1.5f, col); CLine(head + new Vector2(0, -4), head + new Vector2(0, 4), 1.5f, col); }
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

            // Debris: new wreckage and spent stages, shown for a few seconds (burst ring, then fade) so kills are visible without clutter.
            var debrisLabels = new List<(Vector2 pos, Vessel v, float dist)>();
            int debrisCount = 0;
            if (AvionicsSettings.Radar3DShowDebris)
            {
                UpdateDebrisAges();
                foreach (var v in FlightGlobals.Vessels)
                {
                    if (v == null || v == own || v.vesselType != VesselType.Debris || v.mainBody != own.mainBody) continue;
                    if (!debrisSeen.TryGetValue(v.persistentId, out float born) || float.IsNegativeInfinity(born)) continue; // old debris
                    float age = Time.time - born;
                    if (age > DebrisShowTime) continue;
                    float alpha = age < DebrisShowTime - DebrisFadeTime ? 0.95f : 0.95f * (DebrisShowTime - age) / DebrisFadeTime;
                    Color dc = new(1f, 0.72f, 0.35f, alpha); // hot orange: fresh wreckage
                    Vector3 wpos = v.loaded ? (Vector3)v.CoM : (Vector3)v.GetWorldPos3D();
                    Vector3 local = ToLocal(wpos - ownPos);
                    if (new Vector2(local.x, local.z).magnitude > range) continue;
                    debrisCount++;
                    Vector2 basePt = Project(new Vector3(local.x, 0, local.z));
                    Vector2 head = Project(local);
                    head.x = Mathf.Clamp(head.x, scope.xMin + 4, scope.xMax - 4);
                    head.y = Mathf.Clamp(head.y, scope.yMin + 4, scope.yMax - 4);
                    CDashed(basePt, head, 1, dc, 2, 3);
                    CLine(head + new Vector2(-3, -3), head + new Vector2(3, 3), 1.5f, dc);
                    CLine(head + new Vector2(-3, 3), head + new Vector2(3, -3), 1.5f, dc);
                    if (age < DebrisBurstTime) // expanding burst ring when it first appears
                    {
                        float k = age / DebrisBurstTime;
                        AvionicsDraw.Circle(head, 4 + 18 * k, 2f * (1 - k) + 0.5f, new Color(1f, 0.85f, 0.4f, 1 - k), 20);
                    }
                    Vector3 vel = v.loaded ? (Vector3)v.Velocity() : (Vector3)v.srf_velocity;
                    if (vel.sqrMagnitude > 25)
                    {
                        Vector2 vEnd = Project(local + ToLocal(vel).normalized * range * 0.02f);
                        if ((vEnd - head).sqrMagnitude > 0.01f) CLine(head, head + (vEnd - head).normalized * 10f, 1, dc);
                    }
                    debrisLabels.Add((head, v, (wpos - ownPos).magnitude));
                }
            }

            // Groups: a ring round each formation on the ground plane, sized to its spread.
            var groupLabels = new List<(Vector2 pos, Group g)>();
            if (ShowGroups)
                foreach (var g in groups)
                {
                    Vector3 gl = ToLocal(g.centre - ownPos);
                    if (new Vector2(gl.x, gl.z).magnitude > range * 1.1f) continue;
                    float r = Mathf.Max(g.spread * 1.15f, range * 0.025f);
                    Color gc = g.iff == IFF.Hostile ? AvionicsDraw.Hostile : AvionicsDraw.Unknown; gc.a = 0.55f;
                    for (int i = 0; i <= 32; i++) { float t = i * Mathf.PI * 2 / 32; ring[i] = Project(new Vector3(gl.x + Mathf.Cos(t) * r, 0, gl.z + Mathf.Sin(t) * r)); }
                    AvionicsDraw.Polyline(ring, 33, 1.5f, gc, false);
                    Vector2 lp = Project(new Vector3(gl.x, 0, gl.z + r));
                    picks.Add((Project(new Vector3(gl.x, 0, gl.z)), g.Key));
                    if (g.Key == centerKey) AvionicsDraw.Circle(Project(new Vector3(gl.x, 0, gl.z)), 6, 1, AvionicsDraw.HudText, 4);
                    groupLabels.Add((lp, g));
                }

            // Intercept: where own ship meets the selected contact at its current speed, if both hold course.
            string interceptText = null;
            var selc = AvionicsContacts.Selected;
            if (ShowIntercept && selc != null && !selc.IsGPS && own.srfSpeed > 20)
            {
                Vector3 rel = selc.position - (Vector3)own.CoM;
                float ti = InterceptTime(rel, selc.velocity, (float)own.srfSpeed);
                if (ti > 0 && ti < 900)
                {
                    Vector3 meet = selc.position + selc.velocity * ti;
                    Vector3 ml = ToLocal(meet - ownPos);
                    Vector2 mp = Project(ml);
                    Color ic = new(0.4f, 0.9f, 1f, 0.9f);
                    CDashed(Project(ToLocal((Vector3)own.CoM - ownPos)), mp, 1, ic, 5, 4);
                    CLine(mp + new Vector2(-5, -5), mp + new Vector2(5, 5), 2, ic);
                    CLine(mp + new Vector2(-5, 5), mp + new Vector2(5, -5), 2, ic);
                    Vector3 hdg = Vector3.ProjectOnPlane(meet - (Vector3)own.CoM, (Vector3)own.upAxis);
                    float brg = Mathf.Repeat(Vector3.SignedAngle(VectorUtils.GetNorthVector(own.CoM, own.mainBody), hdg, (Vector3)own.upAxis), 360);
                    interceptText = $"INTERCEPT  steer {brg:000}°  merge {ti:0}s  {AvionicsDraw.FormatRange((meet - (Vector3)own.CoM).magnitude)}";
                }
                else interceptText = "INTERCEPT  none at current speed";
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
                    CLine(inner, outer, blink ? 4 : 2.5f, t.Color);
                    if (t.isMissile && flat.magnitude <= range)
                        CDashed(Project(local), Project(Vector3.zero), 1.5f, AvionicsDraw.Missile, 4, 3);
                    threatLabels.Add((outer, t));
                }
            AvionicsDraw.End();
            DrawBRAA(scope, own);
            foreach (var (pos, g) in groupLabels)
            {
                string alt = $"{AvionicsDraw.FormatAlt(g.altMin)}-{AvionicsDraw.FormatAlt(g.altMax)}";
                AvionicsDraw.ShadowLabel(new Rect(pos.x - 70, pos.y - 14, 140, 13), $"GRP {g.members.Count}  {AvionicsDraw.FormatRange(g.spread * 2)} wide  {alt}", AvionicsDraw.LabelCenter, g.iff == IFF.Hostile ? AvionicsDraw.Hostile : AvionicsDraw.Unknown);
            }
            if (interceptText != null)
                AvionicsDraw.ShadowLabel(new Rect(scope.x + 6, scope.yMax - 46, scope.width - 12, 14), interceptText, AvionicsDraw.LabelSmall, new Color(0.4f, 0.9f, 1f));
            foreach (var (pos, t) in threatLabels)
            {
                string tti = t.timeToImpact > 0 && t.timeToImpact < 999 ? $" {t.timeToImpact:F0}s" : "";
                AvionicsDraw.ShadowLabel(new Rect(pos.x - 40, pos.y - 16, 80, 13), t.Kind + tti, AvionicsDraw.LabelCenter, t.Color);
            }
            DrawWeaponEstimate(scope, own);

            // Label the nearest few pieces of debris; count the rest.
            debrisLabels.Sort((a, b) => a.dist.CompareTo(b.dist));
            for (int i = 0; i < debrisLabels.Count && i < 5; i++)
            {
                var (pos, v, dist) = debrisLabels[i];
                string n = v.GetName();
                if (n.Length > 14) n = n.Substring(0, 14);
                AvionicsDraw.ShadowLabel(new Rect(pos.x + 6, pos.y - 6, 120, 13), $"{n} {AvionicsDraw.FormatRange(dist)}", AvionicsDraw.LabelSmall, new Color(0.7f, 0.7f, 0.68f));
            }
            if (debrisCount > 0)
                AvionicsDraw.ColorLabel(new Rect(scope.x + 6, scope.yMax - 30, 200, 14), $"Debris: {debrisCount} in range", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);

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

            AvionicsDraw.ColorLabel(new Rect(scope.x + 6, scope.yMax - 16, 320, 14), "drag orbit · scroll zoom · click lock · right-click centre", small, AvionicsDraw.ScopeText);
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
