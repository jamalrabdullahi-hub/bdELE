using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Targeting;
using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// ELE wingmen: friendly BDArmory AI aircraft near you become your flight ("Two", "Three", "Four") and take orders from
    /// a command bar, no voice needed. the ` key (left of 1) opens the bar; while it is open the number keys give orders:
    ///   1 Form up · 2 Engage my target · 3 Cover me · 4 Attack my GPS point · 5 Weapons free / hold · 6 Hold here · 7 RTB · 0 Free
    /// They answer in the radio box (text plus a click) and report hits, kills, losses and bingo fuel by themselves.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class Wingman : MonoBehaviour
    {
        public static bool BarOpen;
        enum Order { Free, Form, Engage, Cover, Attack, Hold, RTB }
        static readonly string[] Callsigns = { "Two", "Three", "Four" };
        const float JoinRangeM = 30000f;

        class Wing
        {
            public Vessel v; public string call; public Order order = Order.Free; public Vessel target;
            public float lastHitMsg = -99, lastCheck; public bool bingo;
        }
        readonly List<Wing> flight = new();
        readonly List<(float t, string text, Color c)> radio = new();
        AudioSource audio; AudioClip click;
        float nextTick;

        void Start()
        {
            audio = gameObject.AddComponent<AudioSource>(); audio.spatialBlend = 0; audio.playOnAwake = false;
            click = MakeClick();
            CombatLog.OnKill += Killed;
            CombatLog.OnHit += Hit;
        }
        void OnDestroy() { CombatLog.OnKill -= Killed; CombatLog.OnHit -= Hit; }

        static MissileFire WM(Vessel v) => v != null ? v.ActiveController().WM : null;
        static IBDAIControl AI(Vessel v) => v != null ? v.ActiveController().AI : null;
        static string TeamOf(Vessel v) { var wm = WM(v); return wm != null && wm.Team != null ? wm.Team.Name : null; }

        // ---------------- flight membership ----------------
        void RefreshFlight()
        {
            var me = FlightGlobals.ActiveVessel;
            flight.RemoveAll(w => w.v == null || !w.v.loaded || w.v == me || TeamOf(w.v) != TeamOf(me));
            string team = TeamOf(me);
            if (me == null || team == null) return;
            var free = FlightGlobals.VesselsLoaded
                .Where(v => v != null && v != me && !v.LandedOrSplashed && flight.All(w => w.v != v) && TeamOf(v) == team && AI(v) is BDModulePilotAI)
                .OrderBy(v => (v.CoMD - me.CoMD).sqrMagnitude)
                .Where(v => (v.CoMD - me.CoMD).magnitude < JoinRangeM);
            foreach (var v in free)
            {
                if (flight.Count >= Callsigns.Length) break;
                var call = Callsigns.First(c => flight.All(w => w.call != c));
                flight.Add(new Wing { v = v, call = call });
                Say(call, $"{call}, checking in. ({v.GetDisplayName()})");
            }
        }

        // ---------------- orders ----------------
        void Give(Order o)
        {
            RefreshFlight();
            if (flight.Count == 0) { Say("", "No wingmen in range (friendly AI aircraft, same team, airborne, within 30 km).", true); return; }
            var me = FlightGlobals.ActiveVessel;
            Vessel tgt = null;
            if (o == Order.Engage)
            {
                tgt = AvionicsContacts.Selected?.vessel ?? WM(me)?.currentTarget?.Vessel;
                if (tgt == null) { Say("", "No target selected (pick one in the Target List or lock it).", true); return; }
            }
            Vector3d gps = Vector3d.zero;
            if (o == Order.Attack)
            {
                var wm = WM(me);
                gps = wm != null ? wm.designatedGPSCoords : Vector3d.zero;
                if (gps == Vector3d.zero) { Say("", "No GPS point designated (set one with the targeting camera or GPS Manager).", true); return; }
            }

            foreach (var w in flight)
            {
                C2Tasking.Orders.Remove(w.v);   // the wingman bar overrides C2 tasking
                var ai = AI(w.v);
                w.order = o; w.target = tgt;
                switch (o)
                {
                    case Order.Free: ai?.ReleaseCommand(); Say(w.call, $"{w.call}, free."); break;
                    case Order.Form: Say(w.call, $"{w.call}, rejoining."); break;
                    case Order.Cover: C2Tasking.SetWeaponsFree(w.v, true); Say(w.call, $"{w.call}, I've got your six."); break;
                    case Order.Engage:
                        C2Tasking.SetWeaponsFree(w.v, true); ai?.ReleaseCommand();
                        var ti = tgt.gameObject.GetComponent<TargetInfo>();
                        if (ti != null) WM(w.v)?.SetOverrideTarget(ti);
                        Say(w.call, $"{w.call}, engaging {tgt.GetDisplayName()}."); break;
                    case Order.Attack: C2Tasking.SetWeaponsFree(w.v, true); ai?.CommandAttack(gps); Say(w.call, $"{w.call}, in on the mark."); break;
                    case Order.Hold: ai?.CommandFlyTo(new Vector3d(me.latitude, me.longitude, me.altitude)); Say(w.call, $"{w.call}, holding here."); break;
                    case Order.RTB: C2Tasking.RTB(w.v); w.order = Order.Free; Say(w.call, $"{w.call}, RTB."); break;
                }
            }
        }

        void ToggleWeapons()
        {
            RefreshFlight();
            if (flight.Count == 0) return;
            bool free = !C2Tasking.WeaponsFree(flight[0].v);
            foreach (var w in flight) { C2Tasking.SetWeaponsFree(w.v, free); Say(w.call, free ? $"{w.call}, weapons free." : $"{w.call}, weapons hold."); }
        }

        // ---------------- keeping orders going ----------------
        void FixedUpdate()
        {
            if (Time.time < nextTick || KillCam.Playing) return;
            nextTick = Time.time + 0.5f;
            var me = FlightGlobals.ActiveVessel;
            if (me == null) return;
            flight.RemoveAll(w => w.v == null || !w.v.loaded);
            for (int i = 0; i < flight.Count; i++)
            {
                var w = flight[i];
                var ai = AI(w.v);
                if (w.order == Order.Form || w.order == Order.Cover)
                {
                    // echelon right, 2 s ahead of where you'll be: Form tight (80 m spacing), Cover loose (500 m behind, 150 m up)
                    Vector3 fwd = me.srf_velocity.sqrMagnitude > 25 ? (Vector3)me.srf_velocity.normalized : me.ReferenceTransform.up;
                    Vector3 up = (me.CoM - me.mainBody.position).normalized;
                    Vector3 right = Vector3.Cross(up, fwd).normalized;
                    Vector3 slot = w.order == Order.Form ? right * 80f * (i + 1) - fwd * 60f * (i + 1) : -fwd * 500f + right * 200f * (i % 2 == 0 ? 1 : -1) + up * 150f;
                    Vector3 p = me.CoM + (Vector3)me.srf_velocity * 2f + slot;
                    var body = me.mainBody;
                    ai?.CommandFlyTo(new Vector3d(body.GetLatitude(p), body.GetLongitude(p), body.GetAltitude(p)));
                }
                else if (w.order == Order.Engage)
                {
                    if (w.target == null || !w.target.loaded) { w.order = Order.Form; Say(w.call, $"{w.call}, target gone, rejoining."); }
                    else { var ti = w.target.gameObject.GetComponent<TargetInfo>(); if (ti != null) WM(w.v)?.SetOverrideTarget(ti); }
                }

                // fuel report
                double f = 0, m = 0;
                foreach (var p in w.v.parts) foreach (PartResource r in p.Resources) if (r.resourceName == "LiquidFuel") { f += r.amount; m += r.maxAmount; }
                if (m > 0 && !w.bingo && f / m < 0.2) { w.bingo = true; Say(w.call, $"{w.call}, bingo fuel."); }
            }
        }

        void Hit(Vessel v, WeaponKind kind, float dmg)
        {
            var w = flight.FirstOrDefault(x => x.v == v);
            if (w == null || Time.time - w.lastHitMsg < 12) return;
            w.lastHitMsg = Time.time;
            Say(w.call, kind == WeaponKind.Missile ? $"{w.call}, taking missile damage!" : $"{w.call}, I'm hit!", true);
        }

        void Killed(uint victim, Vessel killer, string killerName, string how)
        {
            var dead = flight.FirstOrDefault(x => x.v != null && x.v.persistentId == victim);
            if (dead != null) { Say("", $"{dead.call} is down!", true); flight.Remove(dead); return; }
            var w = flight.FirstOrDefault(x => x.v == killer);
            if (w != null) Say(w.call, Random.value < 0.5f ? $"{w.call}, splash one!" : $"{w.call}, good kill, good kill.");
            if (killer == FlightGlobals.ActiveVessel && flight.Count > 0 && Random.value < 0.6f) { var x = flight[Random.Range(0, flight.Count)]; Say(x.call, $"{x.call}, nice shot, Lead."); }
        }

        // ---------------- input + drawing ----------------
        static readonly (KeyCode key, string label)[] Keys =
        {
            (KeyCode.Alpha1, "1 Form up"), (KeyCode.Alpha2, "2 Engage my target"), (KeyCode.Alpha3, "3 Cover me"), (KeyCode.Alpha4, "4 Attack GPS point"),
            (KeyCode.Alpha5, "5 Weapons free/hold"), (KeyCode.Alpha6, "6 Hold here"), (KeyCode.Alpha7, "7 RTB"), (KeyCode.Alpha0, "0 Free"),
        };
        const string LockID = "ELEWingmanBar";

        void Update()
        {
            if (GUIUtility.keyboardControl == 0 && Input.GetKeyDown(KeyCode.BackQuote)) SetBar(!BarOpen);   // Alt+letters are KSP trim
            if (!BarOpen) return;
            if (Input.GetKeyDown(KeyCode.Escape)) { SetBar(false); return; }
            for (int i = 0; i < Keys.Length; i++)
                if (Input.GetKeyDown(Keys[i].key)) { Issue(i); SetBar(false); return; }
        }

        public static void ToggleBar() { if (instance != null) instance.SetBar(!BarOpen); }
        static Wingman instance;
        void Awake() => instance = this;

        void SetBar(bool open)
        {
            BarOpen = open;
            if (open) { RefreshFlight(); InputLockManager.SetControlLock(ControlTypes.STAGING | ControlTypes.ACTIONS_ALL, LockID); }   // number keys are action groups otherwise
            else InputLockManager.RemoveControlLock(LockID);
        }

        void Issue(int i)
        {
            switch (i)
            {
                case 0: Give(Order.Form); break; case 1: Give(Order.Engage); break; case 2: Give(Order.Cover); break; case 3: Give(Order.Attack); break;
                case 4: ToggleWeapons(); break; case 5: Give(Order.Hold); break; case 6: Give(Order.RTB); break; default: Give(Order.Free); break;
            }
        }

        void Say(string who, string text, bool urgent = false)
        {
            radio.Add((Time.unscaledTime, text, urgent ? new Color(1f, 0.55f, 0.4f) : new Color(0.6f, 1f, 0.65f)));
            if (radio.Count > 6) radio.RemoveAt(0);
            if (audio != null && click != null) { audio.volume = GameSettings.UI_VOLUME * 0.5f; audio.PlayOneShot(click); }
        }

        void OnGUI()
        {
            if (!BDArmorySetup.GAME_UI_ENABLED || KillCam.Playing) return;
            var small = new GUIStyle(GUI.skin.label) { fontSize = 13 };

            // radio box, bottom left above the staging stack area
            float y = Screen.height * 0.62f;
            radio.RemoveAll(r => Time.unscaledTime - r.t > 9);
            foreach (var (t, text, c) in radio)
            {
                float a = Mathf.Clamp01(9 - (Time.unscaledTime - t));
                GUI.color = new Color(0, 0, 0, 0.45f * a); GUI.DrawTexture(new Rect(10, y, 360, 20), Texture2D.whiteTexture);
                GUI.color = new Color(c.r, c.g, c.b, a); GUI.Label(new Rect(16, y + 1, 354, 20), text, small);
                y += 22;
            }

            // wingman tags in the world
            var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam != null && !MapView.MapIsEnabled)
                foreach (var w in flight)
                {
                    if (w.v == null) continue;
                    var sp = cam.WorldToScreenPoint(w.v.CoM);
                    if (sp.z < 0) continue;
                    GUI.color = new Color(0.5f, 1f, 0.6f, 0.9f);
                    GUI.Label(new Rect(sp.x - 40, Screen.height - sp.y - 30, 80, 18), $"◇ {w.call} · {w.order}", new GUIStyle(small) { alignment = TextAnchor.MiddleCenter, fontSize = 11 });
                }

            if (BarOpen)
            {
                var r = new Rect(Screen.width / 2f - 150, Screen.height * 0.55f, 300, 30 + Keys.Length * 24);
                GUI.color = Color.white;
                GUI.Box(r, "", BDArmorySetup.BDGuiSkin.box);
                GUI.Label(new Rect(r.x + 10, r.y + 4, r.width - 20, 20), flight.Count == 0 ? "WINGMEN: none in range" : $"WINGMEN: {string.Join(", ", flight.Select(w => w.call))}", new GUIStyle(small) { fontStyle = FontStyle.Bold });
                for (int i = 0; i < Keys.Length; i++)
                    if (GUI.Button(new Rect(r.x + 10, r.y + 26 + i * 24, r.width - 20, 22), Keys[i].label, BDArmorySetup.ButtonStyle)) { Issue(i); SetBar(false); }
            }
            GUI.color = Color.white;
        }

        static AudioClip MakeClick()
        {
            int rate = 44100, n = rate / 12;
            var d = new float[n];
            for (int i = 0; i < n; i++) { float t = (float)i / rate; d[i] = (Random.value * 2 - 1) * Mathf.Exp(-t * 60) * 0.35f + Mathf.Sin(2 * Mathf.PI * 1800 * t) * Mathf.Exp(-t * 40) * 0.15f; }
            var c = AudioClip.Create("ele_radio", n, 1, rate, false); c.SetData(d, 0); return c;
        }
    }
}
