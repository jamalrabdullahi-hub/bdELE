using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.UI;
using BDArmory.VesselSpawning;

namespace BDArmory.Avionics
{
    /// <summary>One SAM site at a base: a craft file (weapon manager + missiles, no pilot AI) and whether it still stands.</summary>
    public class DefSite
    {
        public string craft = "", path = "";
        public bool alive = true;
        [NonSerialized] public uint vesselId;
    }

    /// <summary>
    /// Base air defence. Every faction base gets SAM sites (auto-stocked from SAM craft: weapon manager + missiles,
    /// no pilot AI). When you come within 20 km of a base, its standing sites spawn on the ground around it
    /// (clear of the runway, on land), join the owner's team in guard mode and so its IADS; they despawn when
    /// you are 40 km away. Destroyed sites stay down until rebuilt (one per day while the base is operational).
    /// Away from you, standing sites raise the losses of incoming strikes.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class BaseDefense : MonoBehaviour
    {
        public const float SpawnKm = 20f, DespawnKm = 40f;
        public static bool Spawning;
        static readonly HashSet<string> active = new();   // bases whose sites are spawned
        float next;

        void Awake() { active.Clear(); Spawning = false; }

        // ---- Stocking ----

        public static List<(string name, string path)> SamCraft()
        {
            var list = new List<(string, string)>();
            string root = Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "Ships");
            foreach (var dir in new[] { "SPH", "VAB" })
            {
                string d = Path.Combine(root, dir);
                if (!Directory.Exists(d)) continue;
                foreach (var f in Directory.GetFiles(d, "*.craft"))
                {
                    if (Path.GetFileName(f).StartsWith("Auto-Saved")) continue;
                    string name = Path.GetFileNameWithoutExtension(f); bool wm = false, ai = false; int missiles = 0;
                    try
                    {
                        foreach (var raw in File.ReadLines(f))
                        {
                            var l = raw.Trim();
                            if (l.StartsWith("ship = ")) name = l.Substring(7).Trim();
                            else if (l == "name = MissileFire") wm = true;
                            else if (l == "name = BDModulePilotAI" || l == "name = BDModuleSurfaceAI" || l == "name = BDModuleVTOLAI") ai = true;
                            else if (l == "name = MissileLauncher") missiles++;
                        }
                    }
                    catch { continue; }
                    if (wm && !ai && missiles >= 2 && !list.Exists(x => x.Item1 == name)) list.Add((name, f));
                }
            }
            return list;
        }

        /// <summary>Give bases without air defence 3 SAM sites (cycling through the SAM craft).</summary>
        public static int AutoStock(bool force = false)
        {
            var sc = FactionsScenario.Instance;
            if (sc == null) return 0;
            var sams = SamCraft();
            if (sams.Count == 0) return 0;
            int added = 0;
            foreach (var b in sc.bases)
            {
                if (string.IsNullOrEmpty(b.faction) || (!force && b.defenses.Count > 0)) continue;
                if (force) b.defenses.Clear();
                for (int i = 0; i < 3; i++) { var s = sams[i % sams.Count]; b.defenses.Add(new DefSite { craft = s.name, path = s.path }); added++; }
                Debug.Log($"[BDArmoryAvionics]: air defence {b.site}: {string.Join(", ", b.defenses.ConvertAll(d => d.craft))}");
            }
            return added;
        }

        public static int Standing(Base b) { int n = 0; foreach (var d in b.defenses) if (d.alive) n++; return n; }

        /// <summary>Daily: rebuild one destroyed site per day while operational.</summary>
        public static void Rebuild(Base b, float days)
        {
            if (b.status != BaseStatus.Operational) return;
            int n = Mathf.FloorToInt(days + UnityEngine.Random.value * (days % 1));
            foreach (var d in b.defenses) { if (n <= 0) break; if (!d.alive) { d.alive = true; n--; Debug.Log($"[BDArmoryAvionics]: {b.site}: SAM site {d.craft} rebuilt"); } }
        }

        // ---- Flight: spawn / watch / despawn ----

        void Update()
        {
            if (!FlightGlobals.ready || Time.time < next || FactionsScenario.Instance == null) return;
            next = Time.time + 2f;
            var av = FlightGlobals.ActiveVessel;
            if (av == null) return;
            if (FactionsScenario.Instance.bases.Exists(b => b.defenses.Count == 0 && !string.IsNullOrEmpty(b.faction))) AutoStock();
            foreach (var b in FactionsScenario.Instance.bases)
            {
                if (b.defenses.Count == 0) continue;
                var center = av.mainBody.GetWorldSurfacePosition(b.lat, b.lon, b.alt);
                double km = (av.CoMD - center).magnitude / 1000;
                if (active.Contains(b.site))
                {
                    Watch(b);
                    if (km > DespawnKm) Despawn(b);
                }
                else if (km < SpawnKm && !Spawning && b.status != BaseStatus.Destroyed && Standing(b) > 0 && FlightGlobals.ActiveVessel.mainBody == FlightGlobals.GetHomeBody())
                    StartCoroutine(Spawn(b));
            }
        }

        IEnumerator Spawn(Base b)
        {
            Spawning = true;
            active.Add(b.site);
            yield return new WaitWhile(() => Materializer.Busy || SingleVesselSpawning.Instance == null || SingleVesselSpawning.Instance.vesselsSpawning);
            var f = FactionsScenario.FactionByName(b.faction);
            var body = FlightGlobals.GetHomeBody();
            var center = body.GetWorldSurfacePosition(b.lat, b.lon, b.alt);
            var up = (center - body.position).normalized;
            Vector3 along = b.hasRunway ? Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(b.latB, b.lonB, 0) - body.GetWorldSurfacePosition(b.latA, b.lonA, 0)), up).normalized : Vector3.ProjectOnPlane(body.transform.up, up).normalized;
            var across = Vector3.Cross(up, along).normalized;
            int i = 0, placed = 0;
            foreach (var d in b.defenses)
            {
                i++;
                if (!d.alive) continue;
                var name = $"{BaseLogistics.Name(b)} SAM-{i}";
                var existing = FindByName(name);
                if (existing != null) { d.vesselId = existing.persistentId; Arm(existing, f); placed++; continue; } // left over from a save
                if (!File.Exists(d.path)) { Debug.LogWarning($"[BDArmoryAvionics]: SAM craft missing: {d.path}"); continue; }
                // Alternate sides of the runway, spread along it, 700-1000 m out; must be dry land.
                Vector3d pos = center; bool ok = false;
                for (int tries = 0; tries < 8 && !ok; tries++)
                {
                    float side = ((i + tries) % 2 == 0 ? 1 : -1) * (700f + 150f * tries);
                    float alongOff = ((i % 3) - 1) * 900f + tries * 120f;
                    pos = center + (Vector3d)(across * side + along * alongOff);
                    ok = body.TerrainAltitude(body.GetLatitude(pos), body.GetLongitude(pos)) > 2;
                }
                if (!ok) { Debug.Log($"[BDArmoryAvionics]: {name}: no dry land around {b.site}"); continue; }
                float heading = Vector3.SignedAngle(Vector3.ProjectOnPlane(body.transform.up, up), (Vector3)(pos - center), up);
                yield return SingleVesselSpawning.Instance.SpawnVessel(d.path, body.GetLatitude(pos), body.GetLongitude(pos), 1.5, heading < 0 ? heading + 360 : heading, 0f);
                var v = Materializer.LatestSpawned();
                if (v == null) { Debug.LogWarning($"[BDArmoryAvionics]: {name}: spawn failed ({SingleVesselSpawning.Instance.spawnFailureReason})"); continue; }
                yield return null;
                v.vesselName = name;
                d.vesselId = v.persistentId;
                Arm(v, f);
                placed++;
            }
            Spawning = false;
            Debug.Log($"[BDArmoryAvionics]: {b.site}: {placed} SAM site(s) up ({f?.name})");
            if (placed > 0) ScreenMessages.PostScreenMessage($"{BaseLogistics.Name(b)}: {placed} SAM site(s) active", 3f, ScreenMessageStyle.UPPER_LEFT);
        }

        static void Arm(Vessel v, Faction f)
        {
            var wm = v.ActiveController().WM;
            if (wm == null) return;
            if (f != null) wm.SetTeam(f.BDTeam);
            if (!wm.guardMode) wm.ToggleGuardMode();
        }

        static Vessel FindByName(string name) { foreach (var v in FlightGlobals.VesselsLoaded) if (v != null && v.GetName() == name) return v; return null; }
        static Vessel FindById(uint id) { foreach (var v in FlightGlobals.Vessels) if (v != null && v.persistentId == id) return v; return null; }

        /// <summary>A site whose vessel is gone, or lost its weapon manager, is destroyed.</summary>
        static void Watch(Base b)
        {
            if (Spawning) return;
            foreach (var d in b.defenses)
            {
                if (!d.alive || d.vesselId == 0) continue;
                var v = FindById(d.vesselId);
                if (v != null && v.state != Vessel.State.DEAD && v.ActiveController().WM != null) continue;
                d.alive = false; d.vesselId = 0;
                AICommander.Log($"{BaseLogistics.Name(b)}: SAM site {d.craft} destroyed ({Standing(b)} left)");
            }
        }

        static void Despawn(Base b)
        {
            foreach (var d in b.defenses)
            {
                if (d.vesselId == 0) continue;
                var v = FindById(d.vesselId);
                d.vesselId = 0;
                if (v == null) continue;
                Materializer.despawning.Add(v.persistentId);
                v.Die();
            }
            active.Remove(b.site);
            Debug.Log($"[BDArmoryAvionics]: {b.site}: SAM sites stood down (you left)");
        }

        void OnDestroy() { Spawning = false; }
    }
}
