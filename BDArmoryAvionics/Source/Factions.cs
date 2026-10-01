using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI.Screens;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    public enum BaseStatus { Operational, Damaged, Destroyed }

    /// <summary>A side in the campaign. Maps onto a BDArmory team, so guard mode, IFF, the data link and IADS follow it.</summary>
    public class Faction
    {
        public string name = "", team = "A", controller = "Player";
        public Color colour = Color.cyan;
        public bool IsPlayer => controller == "Player";
        public BDTeam BDTeam => BDTeam.Get(team);

        public void Save(ConfigNode n)
        {
            n.AddValue("name", name); n.AddValue("team", team); n.AddValue("controller", controller);
            n.AddValue("colour", $"{colour.r:F2},{colour.g:F2},{colour.b:F2}");
        }

        public static Faction Load(ConfigNode n)
        {
            var f = new Faction { name = n.GetValue("name") ?? "", team = n.GetValue("team") ?? "A", controller = n.GetValue("controller") ?? "Player" };
            var c = (n.GetValue("colour") ?? "").Split(',');
            if (c.Length == 3 && float.TryParse(c[0], out float r) && float.TryParse(c[1], out float g) && float.TryParse(c[2], out float b)) f.colour = new Color(r, g, b);
            return f;
        }
    }

    /// <summary>A Kerbal Konstructs site (or the stock KSC) owned by a faction.</summary>
    public class Base
    {
        public string site = "", faction = "", category = "";
        public double lat, lon, alt;
        public float length;
        public bool hasRunway;
        public double latA, lonA, latB, lonB;
        public BaseStatus status = BaseStatus.Operational;
        // Phase 2: logistics and damage
        public float health = 100f;                 // 0..100
        public double runwayClosedUntil;            // UT
        public float fuel = 20000f, fuelCap = 20000f;
        public float ordnance = 500f, ordnanceCap = 500f;
        public double lastDailyUT = -1;
        public readonly List<InvItem> inventory = new();
        public float captureProgress;               // runtime: seconds an enemy has held it
        public string capturer;

        public bool RunwayOpen => Planetarium.GetUniversalTime() >= runwayClosedUntil && status != BaseStatus.Destroyed;

        public Runway Runway(CelestialBody body) => hasRunway ? new Runway { name = site, body = body, latA = latA, lonA = lonA, latB = latB, lonB = lonB } : null;

        public void Save(ConfigNode n)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            n.AddValue("site", site); n.AddValue("faction", faction); n.AddValue("category", category);
            n.AddValue("lat", lat.ToString("R", ci)); n.AddValue("lon", lon.ToString("R", ci)); n.AddValue("alt", alt.ToString("R", ci));
            n.AddValue("length", length.ToString("R", ci));
            n.AddValue("status", status);
            if (hasRunway) n.AddValue("runway", string.Join(",", Array.ConvertAll(new[] { latA, lonA, latB, lonB }, d => d.ToString("R", ci))));
            n.AddValue("health", health.ToString("R", ci));
            n.AddValue("runwayClosedUntil", runwayClosedUntil.ToString("R", ci));
            n.AddValue("fuel", fuel.ToString("R", ci)); n.AddValue("fuelCap", fuelCap.ToString("R", ci));
            n.AddValue("ordnance", ordnance.ToString("R", ci)); n.AddValue("ordnanceCap", ordnanceCap.ToString("R", ci));
            n.AddValue("lastDailyUT", lastDailyUT.ToString("R", ci));
            foreach (var i in inventory) { var c = n.AddNode("AIRCRAFT"); c.AddValue("craft", i.craft); c.AddValue("authorized", i.authorized); c.AddValue("available", i.available); c.AddValue("lost", i.lost); c.AddValue("path", i.path ?? ""); }
        }

        public static Base Load(ConfigNode n)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture; var st = System.Globalization.NumberStyles.Float;
            var b = new Base { site = n.GetValue("site") ?? "", faction = n.GetValue("faction") ?? "", category = n.GetValue("category") ?? "" };
            double.TryParse(n.GetValue("lat"), st, ci, out b.lat); double.TryParse(n.GetValue("lon"), st, ci, out b.lon); double.TryParse(n.GetValue("alt"), st, ci, out b.alt);
            float.TryParse(n.GetValue("length"), st, ci, out b.length);
            if (Enum.TryParse(n.GetValue("status"), out BaseStatus s)) b.status = s;
            var r = (n.GetValue("runway") ?? "").Split(',');
            if (r.Length == 4 && double.TryParse(r[0], st, ci, out b.latA) && double.TryParse(r[1], st, ci, out b.lonA) && double.TryParse(r[2], st, ci, out b.latB) && double.TryParse(r[3], st, ci, out b.lonB)) b.hasRunway = true;
            if (float.TryParse(n.GetValue("health"), st, ci, out float h)) b.health = h;
            double.TryParse(n.GetValue("runwayClosedUntil"), st, ci, out b.runwayClosedUntil);
            if (float.TryParse(n.GetValue("fuel"), st, ci, out float fu)) b.fuel = fu;
            if (float.TryParse(n.GetValue("fuelCap"), st, ci, out float fc)) b.fuelCap = fc;
            if (float.TryParse(n.GetValue("ordnance"), st, ci, out float o)) b.ordnance = o;
            if (float.TryParse(n.GetValue("ordnanceCap"), st, ci, out float oc)) b.ordnanceCap = oc;
            if (!double.TryParse(n.GetValue("lastDailyUT"), st, ci, out b.lastDailyUT)) b.lastDailyUT = -1;
            foreach (var c in n.GetNodes("AIRCRAFT"))
            {
                var i = new InvItem { craft = c.GetValue("craft") ?? "", path = c.GetValue("path") ?? "" };
                int.TryParse(c.GetValue("authorized"), out i.authorized); int.TryParse(c.GetValue("available"), out i.available); int.TryParse(c.GetValue("lost"), out i.lost);
                b.inventory.Add(i);
            }
            return b;
        }
    }

    /// <summary>Reads Kerbal Konstructs launch sites by reflection (soft dependency: everything works without KK).</summary>
    public static class KKBridge
    {
        public class Site { public string name, category; public double lat, lon, alt; public float length; public CelestialBody body; public object instance; }

        static bool resolved, available;
        static PropertyInfo allSites;
        static FieldInfo fName, fCat, fLat, fLon, fAlt, fLen, fBody, fStatic, fGo, fTransform;

        public static bool Available { get { Resolve(); return available; } }

        static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            try
            {
                Type mgr = null, site = null, inst = null;
                foreach (var a in AssemblyLoader.loadedAssemblies)
                {
                    if (a.name != "KerbalKonstructs") continue;
                    mgr = a.assembly.GetType("KerbalKonstructs.Core.LaunchSiteManager");
                    site = a.assembly.GetType("KerbalKonstructs.Core.KKLaunchSite");
                    inst = a.assembly.GetType("KerbalKonstructs.Core.StaticInstance");
                }
                if (mgr == null || site == null || inst == null) return;
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                allSites = mgr.GetProperty("AllLaunchSites", bf);
                fName = site.GetField("LaunchSiteName", bf); fCat = site.GetField("sitecategory", bf);
                fLat = site.GetField("refLat", bf); fLon = site.GetField("refLon", bf); fAlt = site.GetField("refAlt", bf);
                fLen = site.GetField("LaunchSiteLength", bf); fBody = site.GetField("body", bf); fStatic = site.GetField("staticInstance", bf);
                fGo = inst.GetField("gameObject", bf); fTransform = inst.GetField("transform", bf);
                available = allSites != null && fName != null && fLat != null;
                Debug.Log($"[BDArmoryAvionics]: Kerbal Konstructs {(available ? "found" : "found but its API differs")}");
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: Kerbal Konstructs bridge failed: {e.Message}"); }
        }

        public static List<Site> Sites()
        {
            var list = new List<Site>();
            if (!Available) return list;
            try
            {
                if (allSites.GetValue(null) is not IEnumerable sites) return list;
                foreach (var s in sites)
                {
                    if (s == null) continue;
                    list.Add(new Site
                    {
                        name = fName.GetValue(s) as string,
                        category = fCat?.GetValue(s)?.ToString() ?? "",
                        lat = Convert.ToDouble(fLat.GetValue(s)), lon = Convert.ToDouble(fLon.GetValue(s)), alt = fAlt != null ? Convert.ToDouble(fAlt.GetValue(s)) : 0,
                        length = fLen != null ? Convert.ToSingle(fLen.GetValue(s)) : 0,
                        body = fBody?.GetValue(s) as CelestialBody,
                        instance = fStatic?.GetValue(s),
                    });
                }
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmoryAvionics]: reading KK launch sites failed: {e.Message}"); }
            return list;
        }

        /// <summary>Runway ends from the spawned runway model: its longest horizontal mesh axis.</summary>
        public static bool RunwayEnds(Site s, out double latA, out double lonA, out double latB, out double lonB)
        {
            latA = lonA = latB = lonB = 0;
            if (s.instance == null || fGo == null || s.body == null) return false;
            var go = fGo.GetValue(s.instance) as GameObject;
            var t = fTransform?.GetValue(s.instance) as Transform ?? go?.transform;
            if (go == null || t == null || !go.activeInHierarchy) return false;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            int n = 0;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var l = t.InverseTransformPoint(mf.transform.TransformPoint(c));
                    min = Vector3.Min(min, l); max = Vector3.Max(max, l); n++;
                }
            }
            if (n == 0) return false;
            var size = max - min; var mid = (min + max) / 2;
            bool alongX = size.x > size.z;
            float len = alongX ? size.x : size.z;
            if (len < 300) return false; // not a runway
            Vector3 axis = alongX ? Vector3.right : Vector3.forward;
            var a = t.TransformPoint(mid - axis * len * 0.45f);
            var bb = t.TransformPoint(mid + axis * len * 0.45f);
            latA = s.body.GetLatitude(a); lonA = s.body.GetLongitude(a);
            latB = s.body.GetLatitude(bb); lonB = s.body.GetLongitude(bb);
            return true;
        }
    }

    /// <summary>Factions and their bases, kept with the save.</summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.EDITOR, GameScenes.TRACKSTATION)]
    public class FactionsScenario : ScenarioModule
    {
        public static FactionsScenario Instance;
        public readonly List<Faction> factions = new();
        public readonly List<Base> bases = new();
        bool seeded;

        public override void OnAwake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        public static Faction FactionByName(string name) { if (Instance != null) foreach (var f in Instance.factions) if (f.name == name) return f; return null; }
        public static Faction FactionOfTeam(string team) { if (Instance != null && team != null) foreach (var f in Instance.factions) if (f.team == team) return f; return null; }
        public static Base BaseAt(string site) { if (Instance != null && site != null) foreach (var b in Instance.bases) if (b.site == site) return b; return null; }

        public override void OnSave(ConfigNode node)
        {
            node.AddValue("seeded", seeded);
            foreach (var f in factions) f.Save(node.AddNode("FACTION"));
            foreach (var b in bases) b.Save(node.AddNode("BASE"));
        }

        public override void OnLoad(ConfigNode node)
        {
            factions.Clear(); bases.Clear();
            bool.TryParse(node.GetValue("seeded"), out seeded);
            foreach (var n in node.GetNodes("FACTION")) factions.Add(Faction.Load(n));
            foreach (var n in node.GetNodes("BASE")) bases.Add(Base.Load(n));
        }

        void Start() => StartCoroutine(SeedWhenReady());

        float nextTick;
        void Update()
        {
            if (Time.time < nextTick || HighLogic.CurrentGame == null) return;
            nextTick = Time.time + 2f;
            BaseLogistics.Daily(this);
        }

        /// <summary>First run: Blue (player, team A) holds KSC and Kerman Atoll; Red (AI, team B) holds Kojave Sands and Baikerbanur.</summary>
        IEnumerator SeedWhenReady()
        {
            yield return new WaitForSeconds(2f);
            if (factions.Count == 0)
            {
                factions.Add(new Faction { name = "Blue Force", team = "A", controller = "Player", colour = new Color(0.36f, 0.6f, 1f) });
                factions.Add(new Faction { name = "Red Force", team = "B", controller = "AI", colour = new Color(1f, 0.35f, 0.3f) });
            }
            if (!seeded && KKBridge.Available)
            {
                var plan = new Dictionary<string, string> { ["Runway"] = "Blue Force", ["KSC"] = "Blue Force", ["Kerman Atoll"] = "Blue Force", ["Kojave Sands"] = "Red Force", ["Baikerbanur Runway"] = "Red Force" };
                foreach (var s in KKBridge.Sites())
                    if (s.name != null && plan.TryGetValue(s.name, out var fac) && BaseAt(s.name) == null) bases.Add(FromSite(s, fac));
                seeded = bases.Count > 0;
                Debug.Log($"[BDArmoryAvionics]: factions seeded: {string.Join(", ", bases.ConvertAll(b => $"{b.site} ({b.faction})"))}");
            }
            RefreshFromKK();
        }

        public static Base FromSite(KKBridge.Site s, string faction)
        {
            var b = new Base { site = s.name, faction = faction, category = s.category, lat = s.lat, lon = s.lon, alt = s.alt, length = s.length };
            if (s.name == "Runway" || s.name == "KSC") { b.hasRunway = true; b.latA = -0.0486; b.lonA = -74.7247; b.latB = -0.0502; b.lonB = -74.4910; }
            return b;
        }

        /// <summary>Update positions and, where the runway model is loaded, its ends (for autoland and spawning).</summary>
        public void RefreshFromKK()
        {
            if (!KKBridge.Available) return;
            foreach (var s in KKBridge.Sites())
            {
                var b = BaseAt(s.name);
                if (b == null) continue;
                b.lat = s.lat; b.lon = s.lon; b.alt = s.alt; b.category = s.category; b.length = s.length;
                if (!b.hasRunway && s.category == "Runway" && KKBridge.RunwayEnds(s, out b.latA, out b.lonA, out b.latB, out b.lonB))
                {
                    b.hasRunway = true;
                    float km = (float)(Vector3d.Distance(s.body.GetWorldSurfacePosition(b.latA, b.lonA, 0), s.body.GetWorldSurfacePosition(b.latB, b.lonB, 0)) / 1000);
                    Debug.Log($"[BDArmoryAvionics]: base {b.site}: runway {b.latA:F4},{b.lonA:F4} -> {b.latB:F4},{b.lonB:F4} ({km:F2} km)");
                }
            }
        }

        /// <summary>Runways of a team's bases (for autoland).</summary>
        public static IEnumerable<Runway> RunwaysFor(CelestialBody body, BDTeam team)
        {
            if (Instance == null) yield break;
            foreach (var b in Instance.bases)
            {
                if (!b.hasRunway || !b.RunwayOpen || b.site == "Runway" || b.site == "KSC") continue; // KSC is built in; closed runways are skipped
                var f = FactionByName(b.faction);
                if (team != null && f != null && f.team != team.Name) continue;
                yield return b.Runway(body);
            }
        }
    }

    /// <summary>In flight: craft launched from a faction's base join its BDArmory team; base runways are measured when loaded.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class FactionFlight : MonoBehaviour
    {
        float next;
        readonly HashSet<uint> assigned = new();

        void Start()
        {
            BaseLogistics.Subscribe();
            GameEvents.onGameSceneLoadRequested.Add(OnScene);
        }

        void OnScene(GameScenes s) => BaseLogistics.SceneLeaving();

        void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnScene);
            BaseLogistics.Unsubscribe();
        }

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight || FactionsScenario.Instance == null || Time.time < next) return;
            next = Time.time + 3f;
            FactionsScenario.Instance.RefreshFromKK();
            BaseLogistics.FlightTick();
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed || assigned.Contains(v.persistentId) || v.missionTime > 120) continue;
                var b = FactionsScenario.BaseAt(v.launchedFrom) ?? FactionsScenario.BaseAt(v.landedAt);
                var f = b != null ? FactionsScenario.FactionByName(b.faction) : null;
                var wm = v.ActiveController().WM;
                if (f == null || wm == null) continue;
                assigned.Add(v.persistentId);
                BaseLogistics.OnLaunch(v, b, f);
                if (wm.Team == null || wm.Team.Name != f.team)
                {
                    wm.SetTeam(BDTeam.Get(f.team));
                    Debug.Log($"[BDArmoryAvionics]: {v.GetName()} launched from {b.site}: joined {f.name} (team {f.team})");
                    ScreenMessages.PostScreenMessage($"{v.GetName()}: {f.name}", 3f, ScreenMessageStyle.UPPER_CENTER);
                }
            }
        }
    }

    /// <summary>Theatre window: factions (name, BDArmory team, player/AI, colour) and base ownership. Space Center toolbar button; in flight from the menu.</summary>
    public class TheatreWindow : MonoBehaviour
    {
        public static bool Show;
        const int WindowID = 71730340;
        static Rect rect = new(120, 60, 600, 640);
        Base selectedBase;
        Vector2 scroll;
        int tab;
        List<KKBridge.Site> sites;
        float nextSites;
        static readonly Color[] Palette = [new(0.36f, 0.6f, 1f), new(1f, 0.35f, 0.3f), new(0.4f, 0.85f, 0.4f), new(1f, 0.8f, 0.3f), new(0.8f, 0.5f, 1f), new(0.9f, 0.9f, 0.9f)];

        protected virtual void OnGUI()
        {
            if (!Show || FactionsScenario.Instance == null) return;
            rect = GUI.Window(WindowID, rect, Draw, "Theatre: factions & bases", BDArmorySetup.BDGuiSkin.window);
        }

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 20, 2, 18, 18), " X", BDArmorySetup.CloseButtonStyle)) { Show = false; return; }
            var btn = BDArmorySetup.ButtonStyle; var sel = BDArmorySetup.SelectedButtonStyle;
            var sc = FactionsScenario.Instance;
            float w = rect.width - 16, y = 22;
            string[] tabs = ["Factions", "Bases"];
            for (int i = 0; i < tabs.Length; i++) if (GUI.Button(new Rect(8 + i * 104, y, 100, 22), tabs[i], tab == i ? sel : btn)) tab = i;
            y += 28;

            if (tab == 0)
            {
                for (int i = 0; i < sc.factions.Count; i++)
                {
                    var f = sc.factions[i];
                    AvionicsDraw.FilledRect(new Rect(8, y + 2, 14, 16), f.colour);
                    f.name = GUI.TextField(new Rect(28, y, 170, 20), f.name, 32);
                    if (GUI.Button(new Rect(204, y, 70, 20), $"Team {f.team}", btn)) f.team = NextTeam(f.team);
                    if (GUI.Button(new Rect(278, y, 80, 20), f.controller, f.IsPlayer ? sel : btn)) f.controller = f.IsPlayer ? "AI" : "Player";
                    if (GUI.Button(new Rect(362, y, 60, 20), "Colour", btn)) f.colour = Palette[(Array.FindIndex(Palette, c => c == f.colour) + 1) % Palette.Length];
                    int nb = sc.bases.FindAll(b => b.faction == f.name).Count;
                    AvionicsDraw.ColorLabel(new Rect(428, y + 3, 80, 16), $"{nb} bases", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
                    if (sc.factions.Count > 2 && GUI.Button(new Rect(w - 16, y, 24, 20), "X", btn)) { foreach (var b in sc.bases) if (b.faction == f.name) b.faction = ""; sc.factions.RemoveAt(i); break; }
                    y += 24;
                }
                if (GUI.Button(new Rect(8, y, 140, 22), "Add faction", btn))
                    sc.factions.Add(new Faction { name = $"Faction {sc.factions.Count + 1}", team = NextTeam(sc.factions[sc.factions.Count - 1].team), controller = "AI", colour = Palette[sc.factions.Count % Palette.Length] });
                y += 30;
                AvionicsDraw.ColorLabel(new Rect(8, y, w, 60), "Each faction is a BDArmory team: craft launched from its bases join that team automatically.\nPlayer factions are yours to command; AI factions will plan and fly their own sorties\n(AI commander arrives in phase 3).", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
            }
            else
            {
                if (!KKBridge.Available) { AvionicsDraw.ColorLabel(new Rect(8, y, w, 16), "Kerbal Konstructs not found.", AvionicsDraw.Label, AvionicsDraw.Unknown); GUI.DragWindow(); return; }
                if (sites == null || Time.time > nextSites) { sites = KKBridge.Sites(); nextSites = Time.time + 5f; }
                float listH = selectedBase != null ? 150 : rect.height - y - 8;
                scroll = GUI.BeginScrollView(new Rect(8, y, w, listH), scroll, new Rect(0, 0, w - 18, sites.Count * 24));
                for (int i = 0; i < sites.Count; i++)
                {
                    var s = sites[i];
                    var b = FactionsScenario.BaseAt(s.name);
                    var f = b != null ? FactionsScenario.FactionByName(b.faction) : null;
                    float yy = i * 24;
                    if (f != null) AvionicsDraw.FilledRect(new Rect(0, yy + 3, 10, 14), f.colour);
                    if (b != null && GUI.Button(new Rect(14, yy, 232, 20), "", GUIStyle.none)) selectedBase = selectedBase == b ? null : b;
                    AvionicsDraw.ColorLabel(new Rect(16, yy + 3, 230, 16), $"{s.name}{(b != null ? $"  {b.health:F0}%" : "")}", AvionicsDraw.Label, b == selectedBase && b != null ? AvionicsDraw.LockBox : b != null ? AvionicsDraw.HudText : AvionicsDraw.ScopeText);
                    AvionicsDraw.ColorLabel(new Rect(250, yy + 4, 120, 16), $"{s.category}{(s.length > 0 ? $" {s.length:F0} m" : "")}{(b != null && b.hasRunway ? " RWY" : "")}", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
                    if (GUI.Button(new Rect(w - 160, yy, 140, 20), f != null ? f.name : "unowned", f != null ? sel : btn))
                    {
                        // cycle: unowned -> each faction -> unowned
                        int idx = f == null ? -1 : sc.factions.IndexOf(f);
                        if (idx + 1 < sc.factions.Count) { if (b == null) sc.bases.Add(b = FactionsScenario.FromSite(s, "")); b.faction = sc.factions[idx + 1].name; }
                        else sc.bases.Remove(b);
                    }
                }
                GUI.EndScrollView();
                if (selectedBase != null && !sc.bases.Contains(selectedBase)) selectedBase = null;
                if (selectedBase != null) BaseLogistics.DrawBase(new Rect(8, y + listH + 6, w, rect.height - y - listH - 14), selectedBase);
                else AvionicsDraw.ColorLabel(new Rect(8, rect.height - 22, w, 16), "Click an owned base's name for its stock, aircraft and damage.", AvionicsDraw.LabelSmall, AvionicsDraw.ScopeText);
            }
            GUI.DragWindow(new Rect(0, 0, rect.width - 22, 20));
        }

        static string NextTeam(string t) => t.Length == 1 && t[0] >= 'A' && t[0] < 'Z' ? ((char)(t[0] + 1)).ToString() : "A";
    }

    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class TheatreWindowKSC : TheatreWindow
    {
        ApplicationLauncherButton button;

        IEnumerator Start()
        {
            yield return new WaitUntil(() => ApplicationLauncher.Ready);
            button = ApplicationLauncher.Instance.AddModApplication(() => Show = true, () => Show = false, null, null, null, null, ApplicationLauncher.AppScenes.SPACECENTER, AvionicsDraw.IconTexture);
        }

        void OnDestroy() { if (button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(button); }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class TheatreWindowFlight : TheatreWindow
    {
        protected override void OnGUI() { if (BDArmorySetup.GAME_UI_ENABLED) base.OnGUI(); }
    }
}
