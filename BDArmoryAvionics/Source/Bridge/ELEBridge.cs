using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace BDArmory.Avionics
{
    /// <summary>
    /// ELE Bridge: a tiny HTTP server on 127.0.0.1 (localhost only) so scripts outside KSP can read the flight and fly it.
    /// The socket runs on a background thread; every request is handed to the main thread and answered there, so all
    /// game access stays on Unity's thread. Plain GET requests with query strings, JSON answers. Off unless enabled
    /// (Alt+B toggles; or set ELE_BRIDGE_AUTOSTART=1). Port 8471.
    ///   /state                         active vessel: position, attitude, speeds, controls, fuel, parts
    ///   /vessels                       all vessels in range with distance, team-agnostic
    ///   /control?throttle=&pitch=&yaw=&roll=&release=1    hold stick/throttle (-1..1, throttle 0..1)
    ///   /autopilot?heading=&pitch=&speed=&alt=&off=1      simple hold autopilot (bank-to-turn)
    ///   /action?stage=1 | sas=1 | rcs=0 | gear=1 | brakes=1 | lights=1 | abort=1 | ag=1..10
    ///   /event?part=<name or *>&event=<gui name>          fire a part's right-click event (e.g. BDA guard/AI toggles)
    ///   /events                        list available part events on the active vessel
    ///   /switch?id=<persistentId>      switch active vessel
    ///   /slowmo?index=0..4 | freeze=1  ELE slow-mo
    ///   /screenshot                    writes Screenshots/ele_bridge_*.png, returns its path
    ///   /log?n=50                      last lines ELE posted to the bridge log (screen messages + its own)
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ELEBridge : MonoBehaviour
    {
        public const int Port = 8471;
        static TcpListener listener;
        static Thread thread;
        static volatile bool running;
        static readonly ConcurrentQueue<Request> queue = new ConcurrentQueue<Request>();
        static readonly List<string> log = new List<string>();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        class Request
        {
            public string Path;
            public Dictionary<string, string> Query;
            public string Response;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        // held control state, applied every physics frame through fly-by-wire
        static bool holdControls; static float hThrottle, hPitch, hYaw, hRoll;
        static bool ap; static float apHeading, apPitch = float.NaN, apSpeed = float.NaN, apAlt = float.NaN, apThrottle;
        Vessel hooked;

        void Start()
        {
            if (Environment.GetEnvironmentVariable("ELE_BRIDGE_AUTOSTART") == "1") StartServer();
        }

        void OnDestroy()
        {
            Unhook();
            holdControls = false; ap = false;
            // the server keeps running across scene reloads only if Flight is re-entered; stop it to be safe
            StopServer();
        }

        public static void Log(string s)
        {
            lock (log) { log.Add($"{DateTime.Now:HH:mm:ss} {s}"); if (log.Count > 500) log.RemoveRange(0, 100); }
        }

        public static bool Running => running;
        public static void Toggle() { if (running) StopServer(); else StartServer(); }

        static void StartServer()
        {
            if (running) return;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
                running = true;
                thread = new Thread(Serve) { IsBackground = true, Name = "ELEBridge" };
                thread.Start();
                Debug.Log($"[ELE Bridge] listening on 127.0.0.1:{Port}");
                ScreenMessages.PostScreenMessage($"ELE Bridge ON (127.0.0.1:{Port})", 3f, ScreenMessageStyle.UPPER_CENTER);
            }
            catch (Exception e) { Debug.LogError("[ELE Bridge] start failed: " + e.Message); running = false; }
        }

        static void StopServer()
        {
            if (!running) return;
            running = false;
            try { listener?.Stop(); } catch { }
            Debug.Log("[ELE Bridge] stopped");
        }

        // ---------------- socket thread ----------------
        static void Serve()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); } catch { break; }
                try
                {
                    client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    string first = reader.ReadLine() ?? "";
                    string line; while (!string.IsNullOrEmpty(line = reader.ReadLine())) { }   // skip headers
                    var parts = first.Split(' ');
                    string target = parts.Length > 1 ? parts[1] : "/";
                    var req = new Request { Path = target, Query = new Dictionary<string, string>() };
                    int q = target.IndexOf('?');
                    if (q >= 0)
                    {
                        req.Path = target.Substring(0, q);
                        foreach (var kv in target.Substring(q + 1).Split('&'))
                        {
                            if (kv.Length == 0) continue;
                            int e = kv.IndexOf('=');
                            string k = Uri.UnescapeDataString(e < 0 ? kv : kv.Substring(0, e));
                            string v = e < 0 ? "1" : Uri.UnescapeDataString(kv.Substring(e + 1).Replace('+', ' '));
                            req.Query[k] = v;
                        }
                    }
                    queue.Enqueue(req);
                    string body = req.Done.WaitOne(10000) ? req.Response : "{\"error\":\"timeout (game paused or loading?)\"}";
                    var bytes = Encoding.UTF8.GetBytes(body ?? "{}");
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    stream.Write(head, 0, head.Length); stream.Write(bytes, 0, bytes.Length);
                }
                catch { }
                finally { try { client.Close(); } catch { } }
            }
        }

        // ---------------- main thread ----------------
        void Update()
        {
            if (GUIUtility.keyboardControl == 0 && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) && Input.GetKeyDown(KeyCode.B))
            {
                if (running) { StopServer(); ScreenMessages.PostScreenMessage("ELE Bridge OFF", 2f, ScreenMessageStyle.UPPER_CENTER); }
                else StartServer();
            }

            var v = FlightGlobals.ActiveVessel;
            if (v != hooked) { Unhook(); if (v != null) { v.OnFlyByWire += FlyByWire; hooked = v; } }

            int n = 0;
            while (n++ < 20 && queue.TryDequeue(out var req))
            {
                try { req.Response = Handle(req); }
                catch (Exception e) { req.Response = Obj(("error", Str(e.GetType().Name + ": " + e.Message))); }
                req.Done.Set();
            }
        }

        void Unhook() { if (hooked != null) { hooked.OnFlyByWire -= FlyByWire; hooked = null; } }

        string Handle(Request r)
        {
            var v = FlightGlobals.ActiveVessel;
            switch (r.Path.TrimEnd('/').ToLowerInvariant())
            {
                case "": return Obj(("bridge", Str("ELE")), ("port", Port.ToString()), ("endpoints", "[\"/state\",\"/vessels\",\"/control\",\"/autopilot\",\"/action\",\"/event\",\"/events\",\"/switch\",\"/slowmo\",\"/screenshot\",\"/log\"]"));
                case "/state": return v == null ? Err("no active vessel") : State(v);
                case "/vessels": return Vessels(v);
                case "/control": return Control(r.Query);
                case "/autopilot": return Autopilot(r.Query, v);
                case "/action": return v == null ? Err("no active vessel") : Action(r.Query, v);
                case "/event": return v == null ? Err("no active vessel") : FireEvent(r.Query, v);
                case "/events": return v == null ? Err("no active vessel") : ListEvents(v);
                case "/switch": return Switch(r.Query);
                case "/slowmo": return Slowmo(r.Query);
                case "/screenshot": return Screenshot();
                case "/log": return Log(r.Query);
                default: return Err("unknown path " + r.Path);
            }
        }

        // ---------------- endpoints ----------------
        static string State(Vessel v)
        {
            var up = (v.CoMD - v.mainBody.position).normalized;
            var north = Vector3d.Exclude(up, v.mainBody.transform.up).normalized;
            var fwd = (Vector3d)v.ReferenceTransform.up;
            var east = Vector3d.Cross(up, north);
            double pitch = 90 - Vector3d.Angle(fwd, up);
            var fh = Vector3d.Exclude(up, fwd);
            double heading = (Math.Atan2(Vector3d.Dot(fh, east), Vector3d.Dot(fh, north)) * Mathf.Rad2Deg + 360) % 360;
            var right = (Vector3d)v.ReferenceTransform.right;
            double roll = Vector3d.Angle(Vector3d.Exclude(fwd, up), -(Vector3d)v.ReferenceTransform.forward) * Math.Sign(Vector3d.Dot(right, up)) ;
            var srf = v.srf_velocity;
            double aoa = srf.sqrMagnitude > 1 ? Vector3d.Angle(fwd, srf) : 0;
            var c = v.ctrlState;
            double fuel = 0, fuelMax = 0;
            foreach (var p in v.parts) foreach (PartResource res in p.Resources)
                if (res.resourceName == "LiquidFuel" || res.resourceName == "Oxidizer" || res.resourceName == "MonoPropellant") { fuel += res.amount; fuelMax += res.maxAmount; }

            return Obj(
                ("name", Str(v.vesselName)), ("id", v.persistentId.ToString()), ("situation", Str(v.situation.ToString())),
                ("body", Str(v.mainBody.bodyName)), ("lat", N(v.latitude)), ("lon", N(v.longitude)),
                ("alt", N(v.altitude)), ("radarAlt", N(v.radarAltitude)), ("vspeed", N(v.verticalSpeed)),
                ("speed", N(v.srfSpeed)), ("ias", N(v.indicatedAirSpeed)), ("mach", N(v.mach)), ("gForce", N(v.geeForce)),
                ("heading", N(heading)), ("pitch", N(pitch)), ("roll", N(roll)), ("aoa", N(aoa)),
                ("throttle", N(c.mainThrottle)), ("stick", $"[{N(c.pitch)},{N(c.yaw)},{N(c.roll)}]"),
                ("sas", B(v.ActionGroups[KSPActionGroup.SAS])), ("gear", B(v.ActionGroups[KSPActionGroup.Gear])),
                ("brakes", B(v.ActionGroups[KSPActionGroup.Brakes])), ("stage", v.currentStage.ToString()),
                ("fuel", N(fuel)), ("fuelMax", N(fuelMax)), ("mass", N(v.totalMass)), ("parts", v.parts.Count.ToString()),
                ("apoapsis", N(v.orbit.ApA)), ("periapsis", N(v.orbit.PeA)),
                ("held", B(holdControls)), ("autopilot", ap ? Obj(("heading", N(apHeading)), ("pitch", N(apPitch)), ("speed", N(apSpeed)), ("alt", N(apAlt))) : "null"),
                ("slowmo", Str(SlowMo.Frozen ? "frozen" : SlowMo.Names[SlowMo.Index])), ("ut", N(Planetarium.GetUniversalTime())));
        }

        static string Vessels(Vessel active)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var o in FlightGlobals.Vessels)
            {
                if (o == null || o.vesselType == VesselType.Debris || o.vesselType == VesselType.SpaceObject || o.vesselType == VesselType.Flag) continue;
                if (!first) sb.Append(','); first = false;
                double d = active != null ? Vector3d.Distance(o.GetWorldPos3D(), active.GetWorldPos3D()) : 0;
                sb.Append(Obj(("name", Str(o.vesselName)), ("id", o.persistentId.ToString()), ("type", Str(o.vesselType.ToString())),
                    ("loaded", B(o.loaded)), ("packed", B(o.packed)), ("active", B(o == active)), ("distance", N(d)),
                    ("alt", N(o.altitude)), ("speed", N(o.srfSpeed)), ("lat", N(o.latitude)), ("lon", N(o.longitude)), ("situation", Str(o.situation.ToString()))));
            }
            return sb.Append(']').ToString();
        }

        static string Control(Dictionary<string, string> q)
        {
            if (q.ContainsKey("release")) { holdControls = false; return Obj(("held", "false")); }
            holdControls = true;
            if (q.TryGetValue("throttle", out var t)) hThrottle = Mathf.Clamp01(F(t));
            if (q.TryGetValue("pitch", out var p)) hPitch = Mathf.Clamp(F(p), -1, 1);
            if (q.TryGetValue("yaw", out var y)) hYaw = Mathf.Clamp(F(y), -1, 1);
            if (q.TryGetValue("roll", out var r)) hRoll = Mathf.Clamp(F(r), -1, 1);
            return Obj(("held", "true"), ("throttle", N(hThrottle)), ("pitch", N(hPitch)), ("yaw", N(hYaw)), ("roll", N(hRoll)));
        }

        static string Autopilot(Dictionary<string, string> q, Vessel v)
        {
            if (q.ContainsKey("off")) { ap = false; return Obj(("autopilot", "false")); }
            if (!ap && v != null) { apHeading = (float)Heading(v); apPitch = float.NaN; apSpeed = float.NaN; apAlt = float.NaN; }
            ap = true;
            if (q.TryGetValue("heading", out var h)) apHeading = (F(h) % 360 + 360) % 360;
            if (q.TryGetValue("pitch", out var p)) { apPitch = F(p); apAlt = float.NaN; }
            if (q.TryGetValue("alt", out var a)) { apAlt = F(a); apPitch = float.NaN; }
            if (q.TryGetValue("speed", out var s)) apSpeed = F(s);
            return Obj(("autopilot", "true"), ("heading", N(apHeading)), ("pitch", N(apPitch)), ("alt", N(apAlt)), ("speed", N(apSpeed)));
        }

        static string Action(Dictionary<string, string> q, Vessel v)
        {
            var done = new List<string>();
            bool On(string k) => q[k] != "0" && q[k].ToLowerInvariant() != "false";
            if (q.ContainsKey("stage")) { KSP.UI.Screens.StageManager.ActivateNextStage(); done.Add("stage"); }
            void Set(string key, KSPActionGroup g) { if (q.ContainsKey(key)) { v.ActionGroups.SetGroup(g, On(key)); done.Add(key); } }
            Set("sas", KSPActionGroup.SAS); Set("rcs", KSPActionGroup.RCS); Set("gear", KSPActionGroup.Gear);
            Set("brakes", KSPActionGroup.Brakes); Set("lights", KSPActionGroup.Light); Set("abort", KSPActionGroup.Abort);
            if (q.TryGetValue("ag", out var ag) && int.TryParse(ag, out int n) && n >= 1 && n <= 10)
            {
                var g = (KSPActionGroup)Enum.Parse(typeof(KSPActionGroup), "Custom" + n.ToString("00"));
                v.ActionGroups.ToggleGroup(g); done.Add("ag" + n);
            }
            Log("action " + string.Join(",", done));
            return Obj(("done", "[" + string.Join(",", done.Select(Str)) + "]"));
        }

        static string FireEvent(Dictionary<string, string> q, Vessel v)
        {
            if (!q.TryGetValue("event", out var ev)) return Err("event= required");
            q.TryGetValue("part", out var pn); pn = pn ?? "*";
            int fired = 0;
            foreach (var p in v.parts)
            {
                if (pn != "*" && !p.partInfo.name.Equals(pn, StringComparison.OrdinalIgnoreCase) && !p.partInfo.title.Equals(pn, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (PartModule m in p.Modules)
                    foreach (BaseEvent e in m.Events)
                        if (e.active && e.guiActive && (e.guiName.Equals(ev, StringComparison.OrdinalIgnoreCase) || e.name.Equals(ev, StringComparison.OrdinalIgnoreCase)))
                        { e.Invoke(); fired++; }
            }
            Log($"event '{ev}' on {pn}: {fired}");
            return Obj(("fired", fired.ToString()));
        }

        static string ListEvents(Vessel v)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var p in v.parts)
                foreach (PartModule m in p.Modules)
                    foreach (BaseEvent e in m.Events)
                    {
                        if (!e.active || !e.guiActive) continue;
                        if (!first) sb.Append(','); first = false;
                        sb.Append(Obj(("part", Str(p.partInfo.name)), ("module", Str(m.moduleName)), ("event", Str(e.guiName)), ("name", Str(e.name))));
                    }
            return sb.Append(']').ToString();
        }

        static string Switch(Dictionary<string, string> q)
        {
            if (!q.TryGetValue("id", out var id) || !uint.TryParse(id, out uint pid)) return Err("id= required");
            var t = FlightGlobals.Vessels.FirstOrDefault(o => o.persistentId == pid);
            if (t == null) return Err("no such vessel");
            if (!t.loaded) return Err("vessel not loaded (too far)");
            FlightGlobals.SetActiveVessel(t);
            holdControls = false; ap = false;
            return Obj(("switched", Str(t.vesselName)));
        }

        static string Slowmo(Dictionary<string, string> q)
        {
            if (q.TryGetValue("index", out var i) && int.TryParse(i, out int n)) SlowMo.Set(n);
            if (q.ContainsKey("freeze")) SlowMo.ToggleFreeze();
            return Obj(("slowmo", Str(SlowMo.Frozen ? "frozen" : SlowMo.Names[SlowMo.Index])));
        }

        static string Screenshot()
        {
            var dir = Path.Combine(KSPUtil.ApplicationRootPath, "Screenshots");
            Directory.CreateDirectory(dir);
            var path = Path.GetFullPath(Path.Combine(dir, $"ele_bridge_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png"));
            ScreenCapture.CaptureScreenshot(path);   // written at end of frame
            return Obj(("path", Str(path)));
        }

        static string Log(Dictionary<string, string> q)
        {
            int n = q.TryGetValue("n", out var s) && int.TryParse(s, out int k) ? k : 50;
            lock (log) return "[" + string.Join(",", log.Skip(Math.Max(0, log.Count - n)).Select(Str)) + "]";
        }

        // ---------------- flying ----------------
        static double Heading(Vessel v)
        {
            var up = (v.CoMD - v.mainBody.position).normalized;
            var north = Vector3d.Exclude(up, v.mainBody.transform.up).normalized;
            var fh = Vector3d.Exclude(up, (Vector3d)v.ReferenceTransform.up);
            return (Math.Atan2(Vector3d.Dot(fh, Vector3d.Cross(up, north)), Vector3d.Dot(fh, north)) * Mathf.Rad2Deg + 360) % 360;
        }

        void FlyByWire(FlightCtrlState s)
        {
            var v = hooked;
            if (v == null) return;
            if (holdControls)
            {
                s.mainThrottle = hThrottle; s.pitch = hPitch; s.yaw = hYaw; s.roll = hRoll;
            }
            if (!ap) return;

            // attitude from the vessel's control frame
            var t = v.ReferenceTransform;
            var up = (Vector3)(v.CoMD - v.mainBody.position).normalized;
            var fwd = t.up; var right = t.right;
            float pitch = 90 - Vector3.Angle(fwd, up);
            float bank = Vector3.SignedAngle(Vector3.ProjectOnPlane(up, fwd), -t.forward, fwd);   // + = right wing down
            float hdg = (float)Heading(v);
            var w = v.angularVelocity;   // vessel frame: x pitch, y roll, z yaw (rad/s)

            // heading -> bank (bank-to-turn), altitude -> pitch
            float hErr = Mathf.DeltaAngle(hdg, apHeading);
            float wantBank = Mathf.Clamp(hErr * 1.5f, -45, 45);
            float wantPitch = !float.IsNaN(apPitch) ? apPitch
                            : !float.IsNaN(apAlt) ? Mathf.Clamp((apAlt - (float)v.altitude) * 0.05f - (float)v.verticalSpeed * 0.15f, -20, 20)
                            : 0;

            s.roll = Mathf.Clamp((wantBank - bank) * -0.03f - w.y * 0.3f, -1, 1);
            s.pitch = Mathf.Clamp((wantPitch - pitch) * 0.06f / Mathf.Max(0.3f, Mathf.Cos(bank * Mathf.Deg2Rad)) - w.x * 0.6f, -1, 1);
            s.yaw = Mathf.Clamp(-w.z * 0.4f, -1, 1);
            if (!float.IsNaN(apSpeed))   // integrating throttle: nudges toward the speed, held between frames
            {
                apThrottle = Mathf.Clamp01(apThrottle + Mathf.Clamp((apSpeed - (float)v.srfSpeed) * 0.002f, -0.02f, 0.02f));
                s.mainThrottle = apThrottle;
            }
            else apThrottle = s.mainThrottle;
        }

        // ---------------- json ----------------
        static float F(string s) => float.Parse(s, NumberStyles.Float, Inv);
        static string N(double d) => double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("0.###", Inv);
        static string B(bool b) => b ? "true" : "false";
        static string Str(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
        static string Err(string m) => Obj(("error", Str(m)));
        static string Obj(params (string k, string v)[] kv) => "{" + string.Join(",", kv.Select(p => Str(p.k) + ":" + p.v)) + "}";
    }
}
