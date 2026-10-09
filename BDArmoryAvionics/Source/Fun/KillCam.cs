using System.Collections.Generic;
using UnityEngine;

using BDArmory.UI;

namespace BDArmory.Avionics
{
    /// <summary>
    /// ELE kill cam: keeps the last few seconds of every nearby craft (each part's position and rotation, 20 per second)
    /// and, when a kill happens, freezes the game and replays it with stand-in copies of the parts' meshes, slowing down
    /// around the kill while the camera swings round the victim. The real craft are hidden for the replay, then everything
    /// is put back and the game resumes exactly where it was.
    ///   Alt+R  replay the last 8 s around the active craft      Space / Esc / Enter  end the replay
    ///   Right-drag orbit · wheel zoom · Tab cycle focus (victim / killer)
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class KillCam : MonoBehaviour
    {
        public static readonly string[] ModeNames = { "Kill cam off", "Kill cam: my fights", "Kill cam: all nearby" };
        public static bool Playing => instance != null && instance.playing;
        static KillCam instance;

        const float Keep = 9f, Rate = 0.05f, RangeM = 4000f;
        const int MaxVessels = 16, MaxParts = 1500;
        const string LockID = "ELEKillCam";

        // ---------------- recording ----------------
        class Shape { public readonly List<(Mesh mesh, Material[] mats, Matrix4x4 local)> pieces = new(); }
        struct Frame { public float t; public uint[] ids; public Vector3d[] pos; public Quaternion[] rot; public uint vessel; }

        readonly Dictionary<uint, Shape> shapes = new();          // part persistentId -> meshes
        readonly Dictionary<uint, uint> partVessel = new();       // part -> vessel at last sighting
        readonly Dictionary<uint, string> vesselNames = new();
        readonly LinkedList<Frame[]> history = new();             // one entry per sample: a frame per vessel
        float nextSample;

        void Awake() { instance = this; GameEvents.onPartWillDie.Add(PartWillDie); CombatLog.OnKill += Killed; }
        void OnDestroy()
        {
            GameEvents.onPartWillDie.Remove(PartWillDie);
            CombatLog.OnKill -= Killed;
            if (playing) Stop();
            if (instance == this) instance = null;
        }

        void FixedUpdate()
        {
            if (playing || AvionicsSettings.KillCamMode == 0 && !manualArmed || Time.time < nextSample) return;
            nextSample = Time.time + Rate;
            var active = FlightGlobals.ActiveVessel;
            if (active == null) return;
            var body = active.mainBody.position;

            var near = new List<(Vessel v, float d)>();
            foreach (var v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed || v.vesselType == VesselType.Flag || v.vesselType == VesselType.SpaceObject) continue;
                float d = (float)(v.CoMD - active.CoMD).magnitude;
                if (d < RangeM) near.Add((v, d));
            }
            near.Sort((a, b) => a.d.CompareTo(b.d));

            var sample = new List<Frame>();
            int parts = 0;
            foreach (var (v, _) in near)
            {
                if (sample.Count >= MaxVessels || parts + v.parts.Count > MaxParts) break;
                int n = v.parts.Count; parts += n;
                var f = new Frame { t = Time.time, vessel = v.persistentId, ids = new uint[n], pos = new Vector3d[n], rot = new Quaternion[n] };
                for (int i = 0; i < n; i++)
                {
                    var p = v.parts[i];
                    uint id = p.persistentId;
                    f.ids[i] = id; f.pos[i] = (Vector3d)p.transform.position - body; f.rot[i] = p.transform.rotation;
                    if (!shapes.ContainsKey(id)) shapes[id] = Capture(p, false);
                    partVessel[id] = v.persistentId;
                }
                vesselNames[v.persistentId] = v.GetDisplayName();
                sample.Add(f);
            }
            if (sample.Count == 0) return;
            history.AddLast(sample.ToArray());
            while (Time.time - history.First.Value[0].t > Keep) history.RemoveFirst();
        }

        /// <summary>Copy the references to a part's visible meshes. Dying parts get real copies, since their meshes may be destroyed with them.</summary>
        static Shape Capture(Part p, bool deepCopy)
        {
            var s = new Shape();
            var toPart = p.transform.worldToLocalMatrix;
            foreach (var mf in p.GetComponentsInChildren<MeshFilter>(false))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null || !mr.enabled) continue;
                var mesh = deepCopy ? Instantiate(mf.sharedMesh) : mf.sharedMesh;
                var mats = mr.sharedMaterials;
                if (deepCopy) { mats = (Material[])mats.Clone(); for (int i = 0; i < mats.Length; i++) if (mats[i] != null) mats[i] = new Material(mats[i]); }
                s.pieces.Add((mesh, mats, toPart * mf.transform.localToWorldMatrix));
            }
            return s;
        }

        void PartWillDie(Part p)
        {
            if (p == null || playing || !shapes.ContainsKey(p.persistentId)) return;
            shapes[p.persistentId] = Capture(p, true);
        }

        // ---------------- triggers ----------------
        bool manualArmed = true;   // Alt+R works even with automatic replays off
        float triggerAt = -1; uint pendingVictim; Vessel pendingKiller; string pendingText;

        void Killed(uint victim, Vessel killer, string killerName, string how)
        {
            if (AvionicsSettings.KillCamMode == 0 || playing || triggerAt > 0) return;
            var me = FlightGlobals.ActiveVessel;
            if (me == null) return;
            bool mine = victim == me.persistentId || killer == me;
            if (AvionicsSettings.KillCamMode == 1 && !mine) return;
            if (!partVessel.ContainsValue(victim)) return;   // never recorded: too far
            pendingVictim = victim; pendingKiller = killer;
            pendingText = $"{killerName} > {(vesselNames.TryGetValue(victim, out var n) ? n : "?")}   {how}";
            triggerAt = Time.time + 1.5f;   // let the break-up play out first
        }

        void Update()
        {
            if (playing) { PlayUpdate(); return; }
            if (triggerAt > 0 && Time.time >= triggerAt) { triggerAt = -1; Begin(pendingVictim, pendingKiller != null ? pendingKiller.persistentId : 0, pendingText, true); }
            if (GUIUtility.keyboardControl == 0 && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) && Input.GetKeyDown(KeyCode.R))
            {
                var me = FlightGlobals.ActiveVessel;
                if (me != null) Begin(me.persistentId, 0, "INSTANT REPLAY", false);
            }
        }

        // ---------------- playback ----------------
        bool playing, wasFrozen, killReplay;
        readonly Dictionary<uint, GameObject> ghosts = new();
        readonly List<Renderer> hidden = new();
        Frame[][] clip;
        float clipStart, clipEnd, killTime, playT;
        uint focusVessel, otherVessel;
        string caption;
        float yaw, pitch = 15, dist = -1;
        Vector3 camLocalPos; Quaternion camLocalRot;

        void Begin(uint focus, uint other, string text, bool isKill)
        {
            if (history.Count < 10 || FlightCamera.fetch == null) return;
            clip = new Frame[history.Count][]; history.CopyTo(clip, 0);
            clipStart = clip[0].Length > 0 ? clip[0][0].t : 0;
            var last = clip[clip.Length - 1];
            clipEnd = last.Length > 0 ? last[0].t : clipStart;
            if (clipEnd - clipStart < 1) return;
            killReplay = isKill;
            killTime = isKill ? clipEnd - 1.5f : clipEnd;
            playT = Mathf.Max(clipStart, killTime - 6f);
            focusVessel = focus; otherVessel = other; caption = text;

            if (CameraManager.Instance != null && CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight)
                CameraManager.Instance.SetCameraFlight();

            playing = true;
            wasFrozen = SlowMo.Frozen; SlowMo.Frozen = true; Time.timeScale = 0;
            InputLockManager.SetControlLock(ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.ACTIONS_ALL | ControlTypes.STAGING | ControlTypes.TIMEWARP | ControlTypes.MAP_TOGGLE, LockID);

            // hide the real world craft, build the stand-ins
            hidden.Clear();
            foreach (var v in FlightGlobals.VesselsLoaded)
                if (v != null) foreach (var r in v.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; hidden.Add(r); }
            foreach (var frames in clip) foreach (var f in frames) foreach (var id in f.ids)
                        if (!ghosts.ContainsKey(id) && shapes.TryGetValue(id, out var s)) ghosts[id] = Build(s);

            var cam = FlightCamera.fetch;
            camLocalPos = cam.mainCamera.transform.localPosition; camLocalRot = cam.mainCamera.transform.localRotation;
            cam.DeactivateUpdate();
            dist = -1; yaw = 0; pitch = 12;
            var fv = FocusPoint(focusVessel, playT, out _); var ov = FocusPoint(otherVessel, playT, out bool haveOther);
            if (haveOther && (ov - fv).sqrMagnitude > 1) { var d = (ov - fv).normalized; yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; }
        }

        static GameObject Build(Shape s)
        {
            var root = new GameObject("ELEKillCamGhost");
            foreach (var (mesh, mats, local) in s.pieces)
            {
                if (mesh == null) continue;
                var g = new GameObject("piece");
                g.transform.SetParent(root.transform, false);
                g.transform.localPosition = local.GetColumn(3);
                g.transform.localRotation = local.rotation;
                g.transform.localScale = local.lossyScale;
                g.AddComponent<MeshFilter>().sharedMesh = mesh;
                g.AddComponent<MeshRenderer>().sharedMaterials = mats;
            }
            root.SetActive(false);
            return root;
        }

        void PlayUpdate()
        {
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Return) || playT >= clipEnd) { Stop(); return; }
            if (Input.GetKeyDown(KeyCode.Tab) && otherVessel != 0) (focusVessel, otherVessel) = (otherVessel, focusVessel);

            // time: full speed, slowing to 1/4 within 1.5 s of the kill
            float rate = killReplay && Mathf.Abs(playT - killTime) < 1.5f ? 0.25f : 1f;
            playT += Time.unscaledDeltaTime * rate;

            // place the ghosts for this instant
            int i = Index(playT);
            var a = clip[i]; var b = clip[Mathf.Min(i + 1, clip.Length - 1)];
            float ta = a.Length > 0 ? a[0].t : playT, tb = b.Length > 0 ? b[0].t : ta;
            float u = tb > ta ? Mathf.Clamp01((playT - ta) / (tb - ta)) : 0;
            var body = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.mainBody.position : Vector3d.zero;
            foreach (var g in ghosts.Values) g.SetActive(false);
            var nextPos = new Dictionary<uint, (Vector3d p, Quaternion r)>();
            foreach (var f in b) for (int k = 0; k < f.ids.Length; k++) nextPos[f.ids[k]] = (f.pos[k], f.rot[k]);
            foreach (var f in a)
                for (int k = 0; k < f.ids.Length; k++)
                {
                    if (!ghosts.TryGetValue(f.ids[k], out var g)) continue;
                    Vector3d p = f.pos[k]; Quaternion r = f.rot[k];
                    if (nextPos.TryGetValue(f.ids[k], out var n)) { p = Vector3d.Lerp(p, n.p, u); r = Quaternion.Slerp(r, n.r, u); }
                    g.transform.SetPositionAndRotation((Vector3)(body + p), r);
                    g.SetActive(true);
                }

            // camera: orbit the focus craft
            if (Input.GetMouseButton(1)) { yaw += Input.GetAxis("Mouse X") * 4; pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * 4, -80, 80); }
            else yaw += Time.unscaledDeltaTime * (rate < 1 ? 12 : 6);   // slow swing
            var centre = FocusPoint(focusVessel, playT, out _, out float size);
            if (dist < 0) dist = Mathf.Max(25, size * 3.5f);
            dist = Mathf.Clamp(dist * (1 - Input.GetAxis("Mouse ScrollWheel")), 8, 3000);
            var up = (centre - (Vector3)body).normalized;
            var north = Vector3.ProjectOnPlane(Vector3.up, up).sqrMagnitude > 0.01f ? Vector3.ProjectOnPlane(Vector3.up, up).normalized : Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
            var dir = Quaternion.AngleAxis(yaw, up) * Quaternion.AngleAxis(-pitch, Vector3.Cross(up, north)) * -north;
            var cam = FlightCamera.fetch;
            cam.transform.position = centre + dir * dist;
            cam.transform.rotation = Quaternion.LookRotation(centre - cam.transform.position, up);
            cam.mainCamera.transform.localPosition = Vector3.zero; cam.mainCamera.transform.localRotation = Quaternion.identity;
        }

        int Index(float t)
        {
            int lo = 0, hi = clip.Length - 1;
            while (lo < hi) { int m = (lo + hi + 1) / 2; float mt = clip[m].Length > 0 ? clip[m][0].t : 0; if (mt <= t) lo = m; else hi = m - 1; }
            return lo;
        }

        Vector3 FocusPoint(uint vessel, float t, out bool found) => FocusPoint(vessel, t, out found, out _);

        /// <summary>Centre of a recorded craft at time t (its last recorded position if it has broken up), in world space.</summary>
        Vector3 FocusPoint(uint vessel, float t, out bool found, out float size)
        {
            found = false; size = 10;
            var body = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.mainBody.position : Vector3d.zero;
            for (int i = Index(t); i >= 0; i--)
                foreach (var f in clip[i])
                {
                    if (f.vessel != vessel || f.ids.Length == 0) continue;
                    Vector3d c = Vector3d.zero; foreach (var p in f.pos) c += p; c /= f.ids.Length;
                    float r = 0; foreach (var p in f.pos) r = Mathf.Max(r, (float)(p - c).magnitude);
                    found = true; size = Mathf.Max(4, r * 2);
                    return (Vector3)(body + c);
                }
            // the victim may have split: fall back to anything holding its old parts
            return FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.transform.position : Vector3.zero;
        }

        void Stop()
        {
            playing = false;
            foreach (var g in ghosts.Values) if (g != null) Destroy(g);
            ghosts.Clear();
            foreach (var r in hidden) if (r != null) r.enabled = true;
            hidden.Clear();
            var cam = FlightCamera.fetch;
            if (cam != null) { cam.mainCamera.transform.localPosition = camLocalPos; cam.mainCamera.transform.localRotation = camLocalRot; cam.ActivateUpdate(); }
            InputLockManager.RemoveControlLock(LockID);
            SlowMo.Frozen = wasFrozen;
            if (!wasFrozen) Time.timeScale = SlowMo.Active ? SlowMo.Factor : 1f;
            clip = null;
        }

        void OnGUI()
        {
            if (!playing) return;
            var big = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            var small = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            float w = Screen.width;
            GUI.color = new Color(0, 0, 0, 0.55f); GUI.DrawTexture(new Rect(0, 0, w, 70), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(0, Screen.height - 50, w, 50), Texture2D.whiteTexture);
            GUI.color = killReplay ? new Color(1f, 0.35f, 0.3f) : Color.white;
            GUI.Label(new Rect(0, 8, w, 30), killReplay ? "KILL CAM" : "REPLAY", big);
            GUI.color = Color.white;
            GUI.Label(new Rect(0, 38, w, 24), caption, small);
            float t = playT - killTime;
            GUI.Label(new Rect(0, Screen.height - 44, w, 20), $"{(killReplay ? $"T{(t < 0 ? "−" : "+")}{Mathf.Abs(t):0.0} s" : $"{playT - clipEnd:0.0} s")}   ·   right-drag orbit · wheel zoom · Tab switch craft · Space end", small);
            // progress bar
            float u = Mathf.InverseLerp(clipStart, clipEnd, playT);
            GUI.color = new Color(1, 1, 1, 0.25f); GUI.DrawTexture(new Rect(w * 0.2f, Screen.height - 18, w * 0.6f, 4), Texture2D.whiteTexture);
            GUI.color = new Color(1f, 0.4f, 0.3f); GUI.DrawTexture(new Rect(w * 0.2f, Screen.height - 18, w * 0.6f * u, 4), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
