using UnityEngine;

using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Draws GPS points (and optionally sensor contacts) in the flight view and map view,
    /// and handles "click on terrain to drop a GPS point".
    /// </summary>
    public static class GPSMarkers
    {
        public static bool DropArmed = false;

        // ---- Click to drop ----

        /// <summary>Called from Update. Returns true if the click was consumed.</summary>
        public static bool TryDrop()
        {
            if (!DropArmed || !Input.GetMouseButtonDown(0)) return false;
            if (AvionicsCore.MouseOverAnyWindow()) return false;
            if (Input.GetKey(KeyCode.Escape)) { DropArmed = false; return false; }
            var own = AvionicsContacts.Own;
            if (own == null) return false;
            var body = FlightGlobals.currentMainBody;
            if (body == null) return false;

            bool ok = MapView.MapIsEnabled ? RaycastMap(body, out double lat, out double lon) : RaycastFlight(body, out lat, out lon);
            if (!ok)
            {
                AvionicsContacts.LastMessage = "No terrain under the cursor. Click on the ground, or press the button again to cancel.";
                AvionicsContacts.LastMessageTime = Time.time;
                return true;
            }
            double alt = body.TerrainAltitude(lat, lon, true);
            if (body.ocean && alt < 0) alt = 0;
            AvionicsContacts.AddGPS(new Vector3d(lat, lon, alt), "Marked");
            if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) DropArmed = false; // hold Shift to drop several
            return true;
        }

        static bool RaycastFlight(CelestialBody body, out double lat, out double lon)
        {
            lat = lon = 0;
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null) return false;
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            Vector3d hitPos;
            if (Physics.Raycast(ray, out RaycastHit hit, 200000f, (int)LayerMasks.Scenery))
                hitPos = hit.point;
            else
            {
                // Distant terrain has no colliders: intersect a sphere at the local terrain height, then refine once.
                double h = System.Math.Max(0, AvionicsContacts.Own.terrainAltitude);
                if (!RaySphere(ray.origin, ray.direction, body.position, body.Radius + h, out hitPos)) return false;
                double hLat = body.GetLatitude(hitPos), hLon = body.GetLongitude(hitPos);
                double terrain = System.Math.Max(body.ocean ? 0 : double.MinValue, body.TerrainAltitude(hLat, hLon, true));
                if (!RaySphere(ray.origin, ray.direction, body.position, body.Radius + terrain, out hitPos)) return false;
            }
            lat = body.GetLatitude(hitPos);
            lon = body.GetLongitude(hitPos);
            return true;
        }

        static bool RaycastMap(CelestialBody body, out double lat, out double lon)
        {
            lat = lon = 0;
            var cam = PlanetariumCamera.Camera;
            if (cam == null || body.scaledBody == null) return false;
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            Vector3d center = body.scaledBody.transform.position;
            double radius = body.Radius / ScaledSpace.ScaleFactor;
            if (!RaySphere(ray.origin, ray.direction, center, radius, out Vector3d hitScaled)) return false;
            Vector3d world = ScaledSpace.ScaledToLocalSpace(hitScaled);
            lat = body.GetLatitude(world);
            lon = body.GetLongitude(world);
            return true;
        }

        static bool RaySphere(Vector3d origin, Vector3d dir, Vector3d center, double radius, out Vector3d hit)
        {
            hit = Vector3d.zero;
            dir = dir.normalized;
            Vector3d oc = origin - center;
            double b = Vector3d.Dot(oc, dir);
            double c = oc.sqrMagnitude - radius * radius;
            double disc = b * b - c;
            if (disc < 0) return false;
            double t = -b - System.Math.Sqrt(disc);
            if (t < 0) t = -b + System.Math.Sqrt(disc);
            if (t < 0) return false;
            hit = origin + dir * t;
            return true;
        }

        // ---- Drawing ----

        public static void DrawFlight()
        {
            var wm = AvionicsContacts.WM;
            var own = AvionicsContacts.Own;
            if (wm == null || own == null || Event.current.type != EventType.Repaint) return;
            var cam = GUIUtils.GetMainCamera();
            if (cam == null) return;

            var gpsList = BDATargetManager.GPSTargetList(wm.Team);
            float maxDist = AvionicsSettings.GPSMarkerMaxDistanceKm * 1000f;
            var markers = new System.Collections.Generic.List<(Vector2 pos, string name, string info, Color col, bool box)>();

            if (AvionicsSettings.GPSWorldMarkers)
                for (int i = 0; i < gpsList.Count; i++)
                {
                    var g = gpsList[i];
                    Vector3 wp = g.worldPos;
                    float d = Vector3.Distance(wp, own.CoM);
                    if (d > maxDist) continue;
                    if (!GUIUtils.WorldToGUIPos(wp, out Vector2 gp)) continue;
                    bool isTarget = wm.designatedGPSCoords != Vector3d.zero && g.EqualsTarget(wm.designatedGPSInfo);
                    markers.Add((gp, g.name, AvionicsDraw.FormatRange(d), isTarget ? AvionicsDraw.LockBox : Color.white, false));
                }

            if (AvionicsSettings.WorldContactMarkers)
                foreach (var c in AvionicsContacts.Contacts)
                {
                    if (c.IsGPS || c.iff == IFF.Friendly || !c.IsSensorContact) continue;
                    if (!GUIUtils.WorldToGUIPos(c.position, out Vector2 gp)) continue;
                    markers.Add((gp, c.name, $"{AvionicsDraw.FormatRange(c.range)} {c.SourceString()}", c.IFFColor, true));
                }

            if (markers.Count == 0) return;
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;
            if (AvionicsDraw.Begin())
            {
                foreach (var m in markers)
                {
                    if (m.box) AvionicsDraw.Box(m.pos, 9, 2, m.col);
                    else AvionicsDraw.Diamond(m.pos, 8, 2, m.col);
                }
                AvionicsDraw.End();
            }
            foreach (var m in markers)
            {
                AvionicsDraw.ShadowLabel(new Rect(m.pos.x + 12, m.pos.y - 14, 160, 14), m.name, AvionicsDraw.LabelSmall, m.col);
                AvionicsDraw.ShadowLabel(new Rect(m.pos.x + 12, m.pos.y, 160, 14), m.info, AvionicsDraw.LabelSmall, m.col);
            }
            GUI.matrix = oldMatrix;
        }

        public static void DrawMap()
        {
            var wm = AvionicsContacts.WM;
            if (wm == null || !AvionicsSettings.GPSMapMarkers || Event.current.type != EventType.Repaint) return;
            var cam = PlanetariumCamera.Camera;
            var body = FlightGlobals.currentMainBody;
            if (cam == null || body == null || body.scaledBody == null) return;
            var gpsList = BDATargetManager.GPSTargetList(wm.Team);
            if (gpsList.Count == 0) return;

            Vector3 bodyScaled = body.scaledBody.transform.position;
            var markers = new System.Collections.Generic.List<(Vector2 pos, string name, Color col)>();
            for (int i = 0; i < gpsList.Count; i++)
            {
                var g = gpsList[i];
                Vector3d world = body.GetWorldSurfacePosition(g.gpsCoordinates.x, g.gpsCoordinates.y, g.gpsCoordinates.z);
                Vector3 scaled = ScaledSpace.LocalToScaledSpace(world);
                // Skip points on the far side of the planet
                if (Vector3.Dot(scaled - bodyScaled, cam.transform.position - scaled) < 0) continue;
                Vector3 sp = cam.WorldToScreenPoint(scaled);
                if (sp.z < 0) continue;
                bool isTarget = wm.designatedGPSCoords != Vector3d.zero && g.EqualsTarget(wm.designatedGPSInfo);
                markers.Add((new Vector2(sp.x, Screen.height - sp.y), g.name, isTarget ? AvionicsDraw.LockBox : Color.white));
            }
            if (markers.Count == 0) return;
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;
            if (AvionicsDraw.Begin())
            {
                foreach (var m in markers) AvionicsDraw.Diamond(m.pos, 6, 2, m.col);
                AvionicsDraw.End();
            }
            foreach (var m in markers)
                AvionicsDraw.ShadowLabel(new Rect(m.pos.x + 9, m.pos.y - 7, 140, 14), m.name, AvionicsDraw.LabelSmall, m.col);
            GUI.matrix = oldMatrix;
        }

        public static void DrawDropHint()
        {
            if (!DropArmed) return;
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;
            Vector2 m = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            AvionicsDraw.ShadowLabel(new Rect(m.x + 16, m.y + 10, 260, 14), "Click to drop GPS point (Shift: keep dropping)", AvionicsDraw.LabelSmall, AvionicsDraw.Friendly);
            GUI.matrix = oldMatrix;
        }
    }
}
