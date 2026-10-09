using System;
using UnityEngine;
using VolumetricExplosionFX.Core;
using VolumetricExplosionFX.Ksp;

namespace VolumetricExplosionFX.Rendering;

internal sealed class FxPool : IDisposable
{
	private readonly FxInstance[] slots;

	private readonly ProceduralAssets assets;

	private readonly VolumeAssets volumes;

	private readonly DebrisField debris;

	private readonly FxSettings settings;

	private bool hooked;

	private static readonly int SunId = Shader.PropertyToID("_VefxSun");

	private static readonly int AmbientId = Shader.PropertyToID("_VefxAmbient");

	private static readonly int SkyId = Shader.PropertyToID("_VefxSky");

	private static readonly int UpId = Shader.PropertyToID("_VefxUp");

	private static readonly int Fire0Id = Shader.PropertyToID("_VefxFire0");

	private static readonly int Fire1Id = Shader.PropertyToID("_VefxFire1");

	private static readonly int FireRangeId = Shader.PropertyToID("_VefxFireRange");

	internal int Dropped { get; private set; }

	internal int Spawned { get; private set; }

	internal int NoCamera { get; private set; }

	internal int OutOfRange { get; private set; }

	internal int BudgetDrops { get; private set; }

	internal int Absorbed { get; private set; }

	internal string ShaderSummary => assets.ShaderSummary + " | volume=" + volumes.Status;

	internal int Volumes
	{
		get
		{
			int num = 0;
			for (int i = 0; i < slots.Length; i++)
			{
				if (slots[i].HasVolume)
				{
					num++;
				}
			}
			return num;
		}
	}

	private int ReservedVolumes
	{
		get
		{
			int num = 0;
			for (int i = 0; i < slots.Length; i++)
			{
				if (slots[i].ReservedVolume)
				{
					num++;
				}
			}
			return num;
		}
	}

	internal int Particles
	{
		get
		{
			int num = 0;
			for (int i = 0; i < slots.Length; i++)
			{
				num += slots[i].ParticleCount;
			}
			return num;
		}
	}

	internal int Active
	{
		get
		{
			int num = 0;
			for (int i = 0; i < slots.Length; i++)
			{
				if (slots[i].Active)
				{
					num++;
				}
			}
			return num;
		}
	}

	internal int Debris
	{
		get
		{
			int num = 0;
			for (int i = 0; i < slots.Length; i++)
			{
				num += slots[i].ReservedDebris;
			}
			return num;
		}
	}

	internal int Lights
	{
		get
		{
			int num = 0;
			for (int i = 0; i < slots.Length; i++)
			{
				if (slots[i].HasLight)
				{
					num++;
				}
			}
			return num;
		}
	}

	internal int FireVolumes
	{
		get
		{
			int num = 0;
			for (int i = 0; i < slots.Length; i++)
			{
				if (slots[i].HasFireVolume)
				{
					num++;
				}
			}
			return num;
		}
	}

	internal EffectLoad Load => new EffectLoad
	{
		Blasts = Active,
		FireballVolumes = Volumes,
		FireVolumes = FireVolumes,
		Particles = Particles,
		Wrecks = (debris.Available ? debris.Active : 0),
		WreckPieces = (debris.Available ? debris.Pieces : 0),
		Lights = Lights
	};

	internal string Wreckage
	{
		get
		{
			if (!debris.Available)
			{
				return "part debris unavailable";
			}
			return debris.Active + " wrecks / " + debris.Pieces + " pieces / " + debris.Pending + " cutting / " + debris.Captured + " captured / " + debris.Missed + " missed / " + debris.Dropped + " dropped / " + debris.Failed + " failed";
		}
	}

	internal FxPool(FxSettings s)
	{
		settings = s;
		CameraRig.Setup();
		volumes = new VolumeAssets(s.Volumetric);
		assets = new ProceduralAssets(volumes.Particle, volumes.Debris);
		slots = new FxInstance[s.MaxEvents];
		debris = new DebrisField(s, volumes, assets.Glow);
		try
		{
			for (int i = 0; i < slots.Length; i++)
			{
				slots[i] = new FxInstance(i, assets, s, volumes);
			}
		}
		catch
		{
			Dispose();
			throw;
		}
		Camera.onPreCull = (Camera.CameraCallback)Delegate.Combine(Camera.onPreCull, new Camera.CameraCallback(OnPreCull));
		hooked = true;
	}

	private void OnPreCull(Camera cam)
	{
		CameraRig.SetRenderRange(cam);
		for (int i = 0; i < slots.Length; i++)
		{
			if (slots[i] != null)
			{
				slots[i].ShowFor(cam);
			}
		}
	}

	internal bool Spawn(EventCluster c, FxPlan plan)
	{
		Camera camera = ((FlightCamera.fetch != null) ? FlightCamera.fetch.mainCamera : Camera.main);
		Vector3 vector = SnapshotReader.Vector(c.Position);
		if (camera == null)
		{
			NoCamera++;
			Dropped++;
			return false;
		}
		if ((double)Vector3.Distance(camera.transform.position, vector) > settings.Distance)
		{
			OutOfRange++;
			Dropped++;
			return false;
		}
		CameraRig.Refresh();
		float num = Vector3.Distance(camera.transform.position, vector);
		FxInstance fxInstance = Covering(vector, plan.VisualEnergyScore);
		Vector3 worldPosition = vector + SnapshotReader.Vector(c.Representative.Origin - c.Representative.Part.Position);
		if (fxInstance != null)
		{
			fxInstance.AddSecondary(worldPosition, plan.VisualEnergyScore / Math.Max(fxInstance.Score, 0.001));
			Absorbed++;
			return true;
		}
		double num2 = Numbers.Clamp(1.0 - (double)num / settings.Distance, 0.1, 1.0);
		plan.DebrisCount = (int)((double)plan.DebrisCount * num2);
		plan.PuffCount = Math.Max(4, (int)((double)plan.PuffCount * num2));
		int num3 = -1;
		int num4 = -1;
		int num5 = -1;
		for (int i = 0; i < slots.Length; i++)
		{
			if (!slots[i].Active && num3 < 0)
			{
				num3 = i;
			}
			if (slots[i].Active && (num4 < 0 || slots[i].Score < slots[num4].Score))
			{
				num4 = i;
			}
			if (slots[i].ReservedVolume && (num5 < 0 || slots[i].Score < slots[num5].Score))
			{
				num5 = i;
			}
		}
		if (num3 < 0 && num4 >= 0 && plan.VisualEnergyScore > slots[num4].Score + 0.5)
		{
			slots[num4].Clear();
			num3 = num4;
		}
		if (num3 >= 0)
		{
			bool flag = c.Ground && settings.Ground;
			bool water = c.Representative.Kind == EventKind.WaterImpact && settings.Water;
			int num6;
			if (!Reentry.Applies(plan, flag, plan.ImpactSpeed) && volumes.Material != null && settings.Volumetric && !camera.orthographic && (double)num < settings.Distance)
			{
				num6 = (VolumeEvolution.Evaluate(plan, 0.1, settings.Explosions, settings.Burst, flag, water).Visible ? 1 : 0);
				if (num6 != 0 && ReservedVolumes >= settings.MaxVolumes && num5 >= 0 && plan.VisualEnergyScore > slots[num5].Score + 0.5)
				{
					slots[num5].ReleaseVolume();
				}
			}
			else
			{
				num6 = 0;
			}
			bool allowVolume = num6 != 0 && ReservedVolumes < settings.MaxVolumes;
			bool allowDistortion = settings.Shockwave && !camera.orthographic && num < 2500f;
			slots[num3].Begin(c, plan, settings, Math.Max(0, settings.MaxDebris - Debris), Lights < settings.MaxLights && num < 1500f, allowVolume, allowDistortion);
			Spawned++;
			return true;
		}
		BudgetDrops++;
		Dropped++;
		return false;
	}

	internal bool Covers(Vector3 position, double score)
	{
		return Covering(position, score) != null;
	}

	internal FxInstance Covering(Vector3 position, double score)
	{
		for (int i = 0; i < slots.Length; i++)
		{
			if (slots[i].Active && slots[i].Age < 2.5f && score <= slots[i].Score + 1.2 && (double)Vector3.Distance(slots[i].Root.transform.position, position) <= slots[i].CoverRadius)
			{
				return slots[i];
			}
		}
		return null;
	}

	internal void PrepareDebris(Part part)
	{
		if (part != null && InRange(part.transform.position))
		{
			debris.Prepare(part);
		}
	}

	internal void CaptureDebris(Part part, DestructionEvent e, FxPlan plan)
	{
		if (InRange(SnapshotReader.Vector(e.Part.Position)))
		{
			debris.Capture(part, e, plan);
		}
	}

	internal void CaptureTestDebris(CapturedPart model, DestructionEvent e, FxPlan plan, CelestialBody body)
	{
		if (InRange(SnapshotReader.Vector(e.Part.Position)))
		{
			debris.Capture(model, e, plan, body);
		}
	}

	internal bool InRange(Vector3 position)
	{
		Camera camera = ((FlightCamera.fetch != null) ? FlightCamera.fetch.mainCamera : Camera.main);
		if (camera != null)
		{
			return (double)Vector3.Distance(camera.transform.position, position) <= settings.Distance;
		}
		return false;
	}

	internal static double Starlight(Vector3d point)
	{
		CelestialBody celestialBody = ((Planetarium.fetch != null) ? Planetarium.fetch.Sun : null);
		CelestialBody currentMainBody = FlightGlobals.currentMainBody;
		if (celestialBody == null || currentMainBody == null || currentMainBody == celestialBody)
		{
			return 1.0;
		}
		Vector3d normalized = (celestialBody.position - point).normalized;
		double num = Vector3d.Dot(currentMainBody.position - point, normalized);
		if (!(num > 0.0))
		{
			return 1.0;
		}
		return UtilMath.Clamp01(((point + normalized * num - currentMainBody.position).magnitude - currentMainBody.Radius * 0.995) / (currentMainBody.Radius * 0.01));
	}

	private void PublishLighting(Camera cam)
	{
		CelestialBody celestialBody = ((Planetarium.fetch != null) ? Planetarium.fetch.Sun : null);
		if (celestialBody == null || cam == null)
		{
			return;
		}
		Vector3d vector3d = cam.transform.position;
		Vector3d normalized = (celestialBody.position - vector3d).normalized;
		double num = Starlight(vector3d);
		CelestialBody currentMainBody = FlightGlobals.currentMainBody;
		Shader.SetGlobalVector(SunId, new Vector4((float)normalized.x, (float)normalized.y, (float)normalized.z, (float)num));
		Color ambientLight = RenderSettings.ambientLight;
		Shader.SetGlobalVector(AmbientId, new Vector4(Mathf.Max(ambientLight.r, 0.07f), Mathf.Max(ambientLight.g, 0.07f), Mathf.Max(ambientLight.b, 0.08f), 1f));
		Vector3 vector = Vector3.up;
		float num2 = 0f;
		if (currentMainBody != null)
		{
			Vector3d vector3d2 = vector3d - currentMainBody.position;
			vector = vector3d2.normalized;
			if (currentMainBody.atmosphere)
			{
				num2 = (float)(Math.Pow(UtilMath.Clamp01(currentMainBody.GetPressure(vector3d2.magnitude - currentMainBody.Radius) / 101.325), 0.35) * Math.Max(num, 0.12));
			}
		}
		Shader.SetGlobalVector(SkyId, new Vector4(0.36f * num2, 0.43f * num2, 0.55f * num2, 1f));
		Shader.SetGlobalVector(UpId, new Vector4(vector.x, vector.y, vector.z, 0f));
		int num3 = -1;
		int num4 = -1;
		for (int i = 0; i < slots.Length; i++)
		{
			if (slots[i].Active && !(slots[i].FireGlow <= 0.01f))
			{
				if (num3 < 0 || slots[i].FireGlow > slots[num3].FireGlow)
				{
					num4 = num3;
					num3 = i;
				}
				else if (num4 < 0 || slots[i].FireGlow > slots[num4].FireGlow)
				{
					num4 = i;
				}
			}
		}
		Shader.SetGlobalVector(Fire0Id, Fire(num3));
		Shader.SetGlobalVector(Fire1Id, Fire(num4));
		Shader.SetGlobalVector(FireRangeId, new Vector4((num3 >= 0) ? slots[num3].FireRange : 10f, (num4 >= 0) ? slots[num4].FireRange : 10f, 0f, 0f));
	}

	private Vector4 Fire(int slot)
	{
		if (slot < 0)
		{
			return Vector4.zero;
		}
		Vector3 firePosition = slots[slot].FirePosition;
		return new Vector4(firePosition.x, firePosition.y, firePosition.z, slots[slot].FireGlow);
	}

	internal void Tick(float dt)
	{
		Camera cam = ((FlightCamera.fetch != null) ? FlightCamera.fetch.mainCamera : Camera.main);
		if (Active > 0 || debris.Active > 0)
		{
			PublishLighting(cam);
		}
		debris.Tick(Mathf.Clamp(dt, 0f, 0.1f), cam);
		float num = 0f;
		for (int i = 0; i < slots.Length; i++)
		{
			num += slots[i].Coverage(cam);
		}
		float num2 = ((num > 1.2f) ? (1.2f / num) : 1f);
		bool flag = false;
		bool flag2 = false;
		for (int j = 0; j < slots.Length; j++)
		{
			if (slots[j].Active)
			{
				slots[j].Tick(Mathf.Clamp(dt, 0f, 0.1f), settings, (slots[j].Coverage(cam) > 0.2f) ? num2 : 1f);
				flag |= slots[j].NeedsNearDepth;
				flag2 |= slots[j].NeedsFarDepth;
			}
		}
		CameraRig.RequestDepth(flag, flag2);
	}

	internal void Shift(Vector3 offset)
	{
		for (int i = 0; i < slots.Length; i++)
		{
			slots[i].Shift(offset);
		}
		debris.Shift(offset);
	}

	internal void Clear()
	{
		for (int i = 0; i < slots.Length; i++)
		{
			slots[i].Clear();
		}
		debris.Clear();
	}

	public void Dispose()
	{
		if (hooked)
		{
			Camera.onPreCull = (Camera.CameraCallback)Delegate.Remove(Camera.onPreCull, new Camera.CameraCallback(OnPreCull));
			hooked = false;
		}
		CameraRig.Release();
		for (int i = 0; i < slots.Length; i++)
		{
			if (slots[i] != null)
			{
				slots[i].Dispose();
			}
		}
		if (debris != null)
		{
			debris.Dispose();
		}
		volumes.Dispose();
		assets.Dispose();
	}
}
