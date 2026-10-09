using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;
using VolumetricExplosionFX.Core;
using VolumetricExplosionFX.Rendering;

namespace VolumetricExplosionFX.Ksp;

[KSPAddon(KSPAddon.Startup.Flight, false)]
public sealed class FlightController : MonoBehaviour
{
	private struct PendingBlast
	{
		public Vector3 Point;

		public int Kind;

		public double At;
	}

	internal const string Version = "1.0.0";

	private static FlightController active;

	private readonly SnapshotCache cache = new SnapshotCache();

	private readonly RecentEvents recent = new RecentEvents();

	private readonly ResourceClassifier classifier = new ResourceClassifier();

	private EventClusterer clusters;

	private FxSettings settings;

	private FxPool pool;

	private StockVisualFilter stock;

	private StreamWriter log;

	private int failures;

	private int confirmed;

	private int absorbed;

	private int anchored;

	private int willDieReports;

	private int groundReports;

	private int waterReports;

	private int missingSnapshots;

	private float nextSummary;

	private bool subscribed;

	private bool overlay;

	private string lastEvent = "Awaiting confirmed destruction";

	private string structure;

	private float restructureAt = -1f;

	private uint testCount;

	private readonly PerfProbe probe = new PerfProbe();

	private readonly List<PendingBlast> pendingBlasts = new List<PendingBlast>();

	internal static bool CanTest
	{
		get
		{
			if (active != null && active.pool != null && active.settings.Enabled && !MapView.MapIsEnabled && TimeWarp.CurrentRate <= 4f)
			{
				if (!(FlightGlobals.ActiveVessel == null))
				{
					return !FlightGlobals.ActiveVessel.packed;
				}
				return true;
			}
			return false;
		}
	}

	private bool ReplaceStock
	{
		get
		{
			if (settings != null && settings.Enabled && settings.ReplaceStockVisuals)
			{
				if (!settings.Explosions && !settings.Burst && !settings.Ground)
				{
					return settings.Water;
				}
				return true;
			}
			return false;
		}
	}

	/// <summary>(ELE) The standalone mod is installed too: let it run alone, so effects aren't doubled.</summary>
	internal static bool StandaloneInstalled
	{
		get { foreach (var a in AssemblyLoader.loadedAssemblies) if (a.name == "VolumetricExplosionFX") return true; return false; }
	}

	internal static bool Ready => active != null && active.pool != null && active.settings != null && active.settings.Enabled;

	/// <summary>
	/// (ELE) A weapon explosion at a point: tntKg of explosive, fuel-rich for fuel / thermobaric blasts. Built like the
	/// settings window's test blast, sized from the charge (a warhead has no tank or wreck of its own).
	/// </summary>
	internal static bool Explode(Vector3 at, double tntKg, bool fuelRich, bool splashed)
	{
		if (!Ready || MapView.MapIsEnabled || TimeWarp.CurrentRate > 4f) return false;
		try
		{
			var self = active;
			CelestialBody body = FlightGlobals.currentMainBody;
			if (body == null) return false;
			Vector3d rel = (Vector3d)at - body.position;
			Vector3 up = rel.normalized;
			double alt = rel.magnitude - body.Radius;
			double p = body.atmosphere ? body.GetPressure(alt) : 0.0, rho = p > 0 ? body.GetDensity(p, body.GetTemperature(alt)) : 0.0;
			double g = body.gravParameter / Math.Max(1.0, rel.sqrMagnitude);
			Vector3 gp = at;
			bool ground = !splashed && SnapshotReader.GroundBelow(at + up * 2f, up, (float)Math.Max(3.0, Math.Pow(tntKg, 1.0 / 3.0) * 3.0), out gp);
			Vector3 point = ground ? gp : at;
			// the charge as fuel and oxidiser of about the same energy (VEFX sizes the fireball from the burnable mass)
			double units = Math.Max(2.0, tntKg * (fuelRich ? 1.6 : 0.9));
			var res = new ResourceSnapshot[] { new ResourceSnapshot("LiquidFuel", units * 0.45, units * 0.45, units * 0.45 * 5), new ResourceSnapshot("Oxidizer", units * 0.55, units * 0.55, units * 0.55 * 5) };
			double size = Math.Max(0.3, Math.Pow(tntKg, 1.0 / 3.0) * 0.6);
			Vector3 vel = -up * 25f;
			var env = new EnvironmentContext(body.bodyName, rho, p, g, alt, body.ocean, ground, splashed, SnapshotReader.Value(up));
			self.testCount++;
			var e = new DestructionEvent(new PartSnapshot(2119630848 + self.testCount, (int)(0 - self.testCount), Guid.Empty, "warhead", SnapshotReader.Value(point), SnapshotReader.Value(vel), SnapshotReader.Value((Vector3d)vel), default, new Vec3(size, size, size), Math.Max(5.0, tntKg * 1.5), 300.0, new string[0], res, env), ground ? EventKind.GroundImpact : EventKind.Destruction, Time.time);
			FxPlan plan = VisualEnergy.Calculate(e, self.classifier, self.settings);
			self.clusters.Add(e, plan.VisualEnergyScore);
			return true;
		}
		catch (Exception ex) { active.Fail(ex); return false; }
	}

	private void Start()
	{
		if (StandaloneInstalled) { Debug.Log("[ELE VEFX] standalone Volumetric Explosion FX is installed: ELE's copy stays off"); Destroy(this); return; }
		active = this;
		try
		{
			settings = SettingsStore.Settings;
			SettingsReader.Resources(classifier);
			if (settings.Enabled)
			{
				Init();
			}
		}
		catch (Exception ex)
		{
			Fail(ex);
			OnDestroy();
		}
	}

	private void Init()
	{
		try
		{
			structure = settings.Structure;
			clusters = new EventClusterer(settings.MaxEvents);
			pool = new FxPool(settings);
			stock = new StockVisualFilter((Vector3 p) => pool != null && pool.Covers(p, 0.0), (Vector3 p) => pool != null && pool.InRange(p));
			stock.SetEnabled(ReplaceStock);
			if (settings.Debug)
			{
				string text = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmoryAvionics/VEFX/PluginData");
				Directory.CreateDirectory(text);
				log = new StreamWriter(Path.Combine(text, "visual-events.log"), append: false);
				log.AutoFlush = true;
			}
			GameEvents.onPartWillDie.Add(OnWillDie);
			GameEvents.onPartDie.Add(OnDie);
			GameEvents.onCrash.Add(OnCrash);
			GameEvents.onCrashSplashdown.Add(OnSplash);
			GameEvents.onFloatingOriginShift.Add(OnOriginShift);
			subscribed = true;
			nextSummary = Time.unscaledTime + 5f;
			Debug.Log("[VEFX] Volumetric Explosion FX 1.0.0 loaded in flight.");
			Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
			for (int num = 0; num < assemblies.Length; num++)
			{
				if (assemblies[num].GetName().Name == "ProjectDestructionFX")
				{
					Debug.LogWarning("[VEFX] An older copy of this mod (GameData/ProjectDestructionFX) is still installed: remove that folder, both together double every effect.");
				}
			}
			Debug.Log("[VEFX] Supported shader selection: " + pool.ShaderSummary);
			Debug.Log("[VEFX] Stock replacement: " + settings.ReplaceStockVisuals + " / " + stock.Status);
		}
		catch (Exception ex)
		{
			Fail(ex);
			OnDestroy();
		}
	}

	private void OnWillDie(Part part)
	{
		willDieReports++;
		Capture(part, EventKind.Destruction, null);
		if (pool != null)
		{
			try
			{
				pool.PrepareDebris(part);
			}
			catch (Exception ex)
			{
				Fail(ex);
			}
		}
	}

	private void OnCrash(EventReport report)
	{
		if (report == null || report.origin == null)
		{
			return;
		}
		groundReports++;
		try
		{
			PartSnapshot partSnapshot = SnapshotReader.Read(report.origin);
			if (partSnapshot != null && SnapshotReader.TryGround(report.origin, partSnapshot, out var point))
			{
				Capture(report.origin, EventKind.GroundImpact, SnapshotReader.Value(point));
			}
		}
		catch (Exception ex)
		{
			Fail(ex);
		}
	}

	private void OnSplash(EventReport report)
	{
		if (report == null || report.origin == null)
		{
			return;
		}
		waterReports++;
		try
		{
			Part origin = report.origin;
			CelestialBody celestialBody = ((origin.vessel != null) ? origin.vessel.mainBody : null);
			if (!(celestialBody == null) && celestialBody.ocean)
			{
				Vector3d vector3d = (Vector3d)origin.transform.position - celestialBody.position;
				Capture(origin, EventKind.WaterImpact, SnapshotReader.Value(celestialBody.position + vector3d.normalized * celestialBody.Radius));
			}
		}
		catch (Exception ex)
		{
			Fail(ex);
		}
	}

	private void Capture(Part part, EventKind kind, Vec3? point)
	{
		if (pool == null || part == null)
		{
			return;
		}
		try
		{
			PartSnapshot partSnapshot = SnapshotReader.Read(part);
			if (partSnapshot != null)
			{
				cache.Capture(partSnapshot, kind, point ?? partSnapshot.Position, Time.time);
			}
		}
		catch (Exception ex)
		{
			Fail(ex);
		}
	}

	private void OnDie(Part part)
	{
		if (pool == null || part == null)
		{
			return;
		}
		try
		{
			double now = Time.time;
			int instanceID = part.GetInstanceID();
			if (!recent.Accept(instanceID, now))
			{
				return;
			}
			DestructionEvent destructionEvent = cache.Confirm(instanceID, now, null);
			if (destructionEvent == null)
			{
				destructionEvent = cache.Confirm(instanceID, now, SnapshotReader.Read(part));
			}
			if (destructionEvent == null)
			{
				missingSnapshots++;
				return;
			}
			PartSnapshot part2 = destructionEvent.Part;
			FxPlan fxPlan = VisualEnergy.Calculate(destructionEvent, classifier, settings);
			pool.CaptureDebris(part, destructionEvent, fxPlan);
			if (destructionEvent.Kind == EventKind.Destruction && !part2.Environment.Splashed && settings.Ground && SnapshotReader.GroundBelow(SnapshotReader.Vector(part2.Position), SnapshotReader.Vector(part2.Environment.Up), (float)Numbers.Clamp(VolumeEvolution.DomeRadius(fxPlan) * 0.5, 3.0, 25.0), out var point))
			{
				destructionEvent = new DestructionEvent(part2.At(SnapshotReader.Value(point)), EventKind.GroundImpact, destructionEvent.Time, part2.Position);
				part2 = destructionEvent.Part;
				fxPlan = VisualEnergy.Calculate(destructionEvent, classifier, settings);
				anchored++;
			}
			Vector3 position = SnapshotReader.Vector(part2.Position);
			FxInstance fxInstance = pool.Covering(position, fxPlan.VisualEnergyScore);
			int num;
			if (fxInstance != null)
			{
				fxInstance.AddSecondary(SnapshotReader.Vector(destructionEvent.Origin), fxPlan.VisualEnergyScore / Math.Max(fxInstance.Score, 0.001));
				num = -1;
				absorbed++;
			}
			else
			{
				num = clusters.Add(destructionEvent, fxPlan.VisualEnergyScore);
				if (clusters.LastEvicted > 0)
				{
					stock.Release(clusters.LastEvicted);
				}
			}
			if (num != 0 && ReplaceStock && pool.InRange(position))
			{
				stock.Claim(part2.Position, now, Numbers.Clamp(part2.Dimensions.Length * 3.0, 10.0, 45.0), num);
			}
			confirmed++;
			lastEvent = string.Format(CultureInfo.InvariantCulture, "{0} | {1} | {2} | {3:F0} kg | {4:F1} m/s | score {5:F2} | rho {6:F4} | p {7:F2} kPa", destructionEvent.Kind, part2.Name, fxPlan.PartKind, part2.MassKg, part2.SurfaceVelocity.Length, fxPlan.VisualEnergyScore, part2.Environment.Density, part2.Environment.PressureKpa);
			if (log != null)
			{
				log.WriteLine(lastEvent);
			}
		}
		catch (Exception ex)
		{
			Fail(ex);
		}
	}

	private void Update()
	{
		if (pool == null)
		{
			if (settings != null && settings.Enabled && !subscribed && failures == 0)
			{
				Init();
			}
			if (pool == null)
			{
				return;
			}
		}
		if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.F8))
		{
			overlay = !overlay;
		}
		if (!settings.Enabled)
		{
			stock.SetEnabled(on: false);
			clusters.Clear();
			pool.Clear();
			return;
		}
		if (settings.Structure != structure)
		{
			if (restructureAt < 0f)
			{
				restructureAt = Time.unscaledTime + 0.6f;
			}
			else if (Time.unscaledTime >= restructureAt)
			{
				Rebuild();
				restructureAt = -1f;
			}
		}
		else
		{
			restructureAt = -1f;
		}
		stock.SetEnabled(ReplaceStock && !MapView.MapIsEnabled);
		if (MapView.MapIsEnabled || TimeWarp.CurrentRate > 4f || (FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.packed))
		{
			clusters.Clear();
			pool.Clear();
			return;
		}
		try
		{
			probe.Begin();
			for (int num = pendingBlasts.Count - 1; num >= 0; num--)
			{
				if ((double)Time.time >= pendingBlasts[num].At)
				{
					PendingBlast pendingBlast = pendingBlasts[num];
					pendingBlasts.RemoveAt(num);
					Blast(pendingBlast.Kind, pendingBlast.Point);
				}
			}
			EventCluster eventCluster;
			while ((eventCluster = clusters.TakeReady(Time.time)) != null)
			{
				FxPlan plan = VisualEnergy.Calculate(eventCluster.Representative, classifier, settings);
				if (!pool.Spawn(eventCluster, plan))
				{
					stock.Release(eventCluster.Id);
				}
			}
			pool.Tick(Time.deltaTime);
			probe.End();
			probe.Frame(pool.Load, settings.Quality.ToString());
			if (Time.unscaledTime >= nextSummary)
			{
				nextSummary = Time.unscaledTime + 5f;
				if (confirmed > 0 || testCount != 0)
				{
					ReportSummary();
				}
			}
		}
		catch (Exception ex)
		{
			Fail(ex);
			clusters.Clear();
			pool.Clear();
		}
	}

	private void Rebuild()
	{
		try
		{
			structure = settings.Structure;
			clusters.Clear();
			pool.Dispose();
			pool = null;
			clusters = new EventClusterer(settings.MaxEvents);
			pool = new FxPool(settings);
			Debug.Log("[VEFX] Effect pool rebuilt for the new settings (" + structure + ").");
		}
		catch (Exception ex)
		{
			Fail(ex);
		}
	}

	internal static bool Test(int kind)
	{
		if (active != null)
		{
			return active.TestBlast(kind);
		}
		return false;
	}

	private bool TestBlast(int kind)
	{
		if (!CanTest)
		{
			return false;
		}
		if (kind != 4)
		{
			return Blast(kind, null);
		}
		try
		{
			Camera camera = ((FlightCamera.fetch != null) ? FlightCamera.fetch.mainCamera : null);
			CelestialBody currentMainBody = FlightGlobals.currentMainBody;
			if (camera == null || currentMainBody == null)
			{
				return false;
			}
			Vector3 vector = ((!Physics.Raycast(camera.transform.position, camera.transform.forward, out var hitInfo, 1500f, SnapshotReader.SurfaceMask(), QueryTriggerInteraction.Ignore) || !(hitInfo.collider != null) || !(hitInfo.collider.GetComponentInParent<Part>() == null)) ? (camera.transform.position + camera.transform.forward * 160f) : hitInfo.point);
			Vector3 planeNormal = ((Vector3d)vector - currentMainBody.position).normalized;
			Vector3 normalized = Vector3.ProjectOnPlane(camera.transform.right, planeNormal).normalized;
			int[] array = new int[8] { 1, 0, 0, 2, 2, 1, 0, 0 };
			float[] array2 = new float[8] { 0f, 12f, -10f, 25f, -24f, 40f, -38f, 55f };
			float[] array3 = new float[8] { 0f, 0.25f, 0.45f, 0.7f, 0.9f, 1.2f, 1.5f, 1.8f };
			pendingBlasts.Clear();
			for (int i = 0; i < array.Length; i++)
			{
				pendingBlasts.Add(new PendingBlast
				{
					Kind = array[i],
					Point = vector + normalized * array2[i],
					At = Time.time + array3[i]
				});
			}
			probe.Note("heavy crash test");
			Debug.Log("[VEFX] Test: heavy crash, " + array.Length + " blasts over 1.8 s with wreckage and residue");
			return true;
		}
		catch (Exception ex)
		{
			Fail(ex);
			return false;
		}
	}

	private bool Blast(int kind, Vector3? at)
	{
		try
		{
			Camera camera = ((FlightCamera.fetch != null) ? FlightCamera.fetch.mainCamera : null);
			CelestialBody currentMainBody = FlightGlobals.currentMainBody;
			if (camera == null || currentMainBody == null || pool == null)
			{
				return false;
			}
			bool flag = kind == 3;
			bool flag2 = false;
			Vector3 vector2;
			RaycastHit hitInfo;
			if (at.HasValue)
			{
				Vector3 vector = ((Vector3d)at.Value - currentMainBody.position).normalized;
				if (!flag && SnapshotReader.GroundBelow(at.Value + vector * 40f, vector, 120f, out var point))
				{
					vector2 = point;
					flag2 = true;
				}
				else
				{
					vector2 = at.Value;
				}
			}
			else if (!flag && Physics.Raycast(camera.transform.position, camera.transform.forward, out hitInfo, 1500f, SnapshotReader.SurfaceMask(), QueryTriggerInteraction.Ignore) && hitInfo.collider != null && hitInfo.collider.GetComponentInParent<Part>() == null)
			{
				vector2 = hitInfo.point;
				flag2 = true;
			}
			else
			{
				vector2 = camera.transform.position + camera.transform.forward * (flag ? 140 : 90);
			}
			Vector3d vector3d = (Vector3d)vector2 - currentMainBody.position;
			Vector3 vector3 = vector3d.normalized;
			double altitude = vector3d.magnitude - currentMainBody.Radius;
			double num = (currentMainBody.atmosphere ? currentMainBody.GetPressure(altitude) : 0.0);
			double density = 0.0;
			if (num > 0.0)
			{
				density = currentMainBody.GetDensity(num, currentMainBody.GetTemperature(altitude));
			}
			double gravity = currentMainBody.gravParameter / Math.Max(1.0, vector3d.sqrMagnitude);
			Vector3d vector3d2 = (currentMainBody.inverseRotation ? Vector3d.zero : currentMainBody.getRFrmVel(vector2));
			Vector3 normalized = Vector3.ProjectOnPlane(camera.transform.forward, vector3).normalized;
			Vector3 vector5;
			if (flag)
			{
				Vessel activeVessel = FlightGlobals.ActiveVessel;
				Vector3 vector4 = ((activeVessel != null) ? ((Vector3)activeVessel.srf_velocity) : Vector3.zero);
				vector5 = ((vector4.magnitude > 60f) ? vector4 : (camera.transform.forward * 250f));
			}
			else
			{
				vector5 = -vector3 * kind switch
				{
					2 => 14f, 
					1 => 70f, 
					_ => 40f, 
				} + normalized * ((kind == 2) ? 2f : 9f);
			}
			ResourceSnapshot[] resources;
			double massKg;
			Vec3 dimensions;
			string[] array;
			switch (kind)
			{
			case 1:
				resources = new ResourceSnapshot[2]
				{
					new ResourceSnapshot("LiquidFuel", 2880.0, 2880.0, 14400.0),
					new ResourceSnapshot("Oxidizer", 3520.0, 3520.0, 17600.0)
				};
				massKg = 36000.0;
				dimensions = new Vec3(3.75, 14.0, 3.75);
				array = new string[4] { "Rockomax64.BW", "Rockomax64_BW", "fuelTank4-2", "fuelTank" };
				break;
			case 2:
				resources = new ResourceSnapshot[1]
				{
					new ResourceSnapshot("SolidFuel", 2600.0, 2600.0, 19500.0)
				};
				massKg = 22000.0;
				dimensions = new Vec3(1.9, 10.0, 1.9);
				array = new string[3] { "MassiveBooster", "solidBooster1-1", "solidBooster" };
				break;
			default:
				resources = new ResourceSnapshot[2]
				{
					new ResourceSnapshot("LiquidFuel", 180.0, 180.0, 900.0),
					new ResourceSnapshot("Oxidizer", 220.0, 220.0, 1100.0)
				};
				massKg = 2250.0;
				dimensions = new Vec3(1.25, 3.8, 1.25);
				array = new string[2] { "fuelTank", "fuelTankSmall" };
				break;
			}
			testCount++;
			EnvironmentContext environment = new EnvironmentContext(currentMainBody.bodyName, density, num, gravity, altitude, currentMainBody.ocean, flag2, splashed: false, SnapshotReader.Value(vector3));
			DestructionEvent e = new DestructionEvent(new PartSnapshot(2119630848 + testCount, (int)(0 - testCount), Guid.Empty, "test blast", SnapshotReader.Value(vector2), SnapshotReader.Value(vector5), SnapshotReader.Value((Vector3d)vector5 + vector3d2), default, dimensions, massKg, 300.0, new string[0], resources, environment), flag2 ? EventKind.GroundImpact : EventKind.Destruction, Time.time);
			FxPlan fxPlan = VisualEnergy.Calculate(e, classifier, settings);
			string[] array2 = array;
			for (int i = 0; i < array2.Length; i++)
			{
				AvailablePart partInfoByName = PartLoader.getPartInfoByName(array2[i]);
				if (partInfoByName != null && !(partInfoByName.partPrefab == null))
				{
					Vector3 vector6 = (flag ? normalized : (Quaternion.AngleAxis(25f, Vector3.Cross(vector3, normalized)) * vector3));
					CapturedPart capturedPart = PartCapture.ReadPrefab(partInfoByName.partPrefab, vector2, flag ? normalized : vector6, settings.DebrisTriangles);
					if (capturedPart != null)
					{
						pool.CaptureTestDebris(capturedPart, e, fxPlan, currentMainBody);
						break;
					}
				}
			}
			clusters.Add(e, fxPlan.VisualEnergyScore);
			return true;
		}
		catch (Exception ex)
		{
			Fail(ex);
			return false;
		}
	}

	private void OnOriginShift(Vector3d shift, Vector3d nonFrame)
	{
		Vec3 offset = SnapshotReader.Value(shift);
		for (int i = 0; i < pendingBlasts.Count; i++)
		{
			PendingBlast value = pendingBlasts[i];
			value.Point -= (Vector3)shift;
			pendingBlasts[i] = value;
		}
		if (clusters != null)
		{
			clusters.Shift(offset);
		}
		cache.Shift(offset);
		if (pool != null)
		{
			pool.Shift(shift);
		}
		if (stock != null)
		{
			stock.Shift(offset);
		}
	}

	private void OnGUI()
	{
		if (overlay && pool != null)
		{
			UiText current = GameText.Current;
			GUILayout.BeginArea(new Rect(20f, 80f, 680f, 440f), GUI.skin.box);
			GUILayout.Label("Volumetric Explosion FX (VEFX) 1.0.0");
			GUILayout.Label(current.Get("DiagnosticsHelp", "Alt+F8 closes this panel. Detailed logging: debug=true in Settings.cfg."));
			GUILayout.Label(current.Format("Counters", "Confirmed: {0} | active FX: {1} | debris: {2} | lights: {3} | culled: {4} | queue drops: {5} | errors: {6}", confirmed, pool.Active, pool.Debris, pool.Lights, pool.Dropped, clusters.Dropped, failures));
			GUILayout.Label(current.Format("ParticleCounters", "Spawned: {0} | particles: {1} | camera/distance/budget drops: {2}/{3}/{4}", pool.Spawned, pool.Particles, pool.NoCamera, pool.OutOfRange, pool.BudgetDrops));
			GUILayout.Label(current.Format("Shaders", "Shaders: {0}", pool.ShaderSummary));
			GUILayout.Label(current.Format("VolumeCounters", "Fireball volumes: {0} / {1} | fire volumes: {2}", pool.Volumes, settings.MaxVolumes, pool.FireVolumes));
			GUILayout.Label(current.Format("WreckageCount", "Part wreckage: {0}", pool.Wreckage));
			GUILayout.Label(current.Format("StockCounters", "Absorbed: {0} | stock visuals hidden/restored: {1}", absorbed, (stock != null) ? (stock.Suppressed + "/" + stock.Restored) : "0/0"));
			GUILayout.Label((lastEvent == "Awaiting confirmed destruction") ? current.Get("AwaitingEvent", "Awaiting confirmed destruction") : current.Format("LastEvent", "Last event: {0}", lastEvent));
			settings.Explosions = GUILayout.Toggle(settings.Explosions, current.Get("Explosions", "Explosions"));
			settings.Debris = GUILayout.Toggle(settings.Debris, current.Get("MicroDebris", "Micro debris"));
			settings.PartDebris = GUILayout.Toggle(settings.PartDebris, current.Get("PartFragments", "Part fragments"));
			settings.Residue = GUILayout.Toggle(settings.Residue, current.Get("Residue", "Ground fire and smoke"));
			settings.ReplaceStockVisuals = GUILayout.Toggle(settings.ReplaceStockVisuals, current.Get("ReplaceStock", "Replace KSP explosions"));
			settings.Shockwave = GUILayout.Toggle(settings.Shockwave, current.Get("Shockwave", "Shockwave and distortion"));
			GUILayout.EndArea();
		}
	}

	private void Fail(Exception ex)
	{
		failures++;
		if (failures == 1)
		{
			Debug.LogError("[VEFX] " + ex);
		}
		else if (failures <= 3)
		{
			Debug.LogError("[VEFX] " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private void ReportSummary()
	{
		Debug.Log(Summary(live: true));
	}

	private string Summary(bool live)
	{
		return "[VEFX] Flight counters: willDie=" + willDieReports + " ground=" + groundReports + " water=" + waterReports + " confirmed=" + confirmed + " missingSnapshots=" + missingSnapshots + " spawned=" + pool.Spawned + (live ? (" liveParticles=" + pool.Particles + " volumes=" + pool.Volumes) : " (final)") + " noCamera=" + pool.NoCamera + " outOfRange=" + pool.OutOfRange + " budgetDrops=" + pool.BudgetDrops + " queueDrops=" + clusters.Dropped + " queueReplaced=" + clusters.Replaced + " absorbed=" + absorbed + "/" + pool.Absorbed + " groundAnchored=" + anchored + " stockHidden=" + ((stock != null) ? stock.Suppressed : 0) + " stockRestored=" + ((stock != null) ? stock.Restored : 0) + " stockMissed=" + ((stock != null) ? stock.Missed : 0) + " stockGivenBack=" + ((stock != null) ? stock.Unconfirmed : 0) + " errors=" + failures + " wreckage=" + pool.Wreckage;
	}

	private void OnDestroy()
	{
		if (active == this)
		{
			active = null;
		}
		if (subscribed)
		{
			GameEvents.onPartWillDie.Remove(OnWillDie);
			GameEvents.onPartDie.Remove(OnDie);
			GameEvents.onCrash.Remove(OnCrash);
			GameEvents.onCrashSplashdown.Remove(OnSplash);
			GameEvents.onFloatingOriginShift.Remove(OnOriginShift);
			subscribed = false;
		}
		string text = null;
		if (pool != null)
		{
			try
			{
				text = Summary(live: false);
				probe.Flush(settings.Quality.ToString());
			}
			catch (Exception ex)
			{
				Fail(ex);
			}
		}
		if (stock != null)
		{
			try
			{
				stock.Dispose();
			}
			catch (Exception ex2)
			{
				Fail(ex2);
			}
			stock = null;
		}
		if (pool != null)
		{
			if (text != null)
			{
				Debug.Log(text);
			}
			try
			{
				pool.Dispose();
			}
			catch (Exception ex3)
			{
				Fail(ex3);
			}
			pool = null;
		}
		if (log != null)
		{
			log.Dispose();
			log = null;
		}
	}
}
