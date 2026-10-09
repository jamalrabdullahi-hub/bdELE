using System;
using UnityEngine;
using UnityEngine.Rendering;
using VolumetricExplosionFX.Core;
using VolumetricExplosionFX.Ksp;

namespace VolumetricExplosionFX.Rendering;

internal sealed class FxInstance : IDisposable
{
	internal readonly GameObject Root;

	internal readonly GameObject DebrisRoot;

	private readonly BlastParticles particles;

	private readonly ParticleSystem poolFire;

	private readonly ParticleSystem column;

	private readonly ParticleSystem tongues;

	private readonly FireVolumeRenderer fireVolume;

	private Camera fireTarget;

	private readonly MeshRenderer scorch;

	private readonly Mesh scorchMesh;

	private readonly MaterialPropertyBlock scorchProperties = new MaterialPropertyBlock();

	private readonly ResidueEmitter residueEmitter = new ResidueEmitter();

	private static readonly int ScorchId = Shader.PropertyToID("_Scorch");

	private readonly Light flash;

	private readonly HeroVolume volume;

	private FxPlan plan;

	private EventKind kind;

	private BlastLayout layout;

	private ResiduePlan residue;

	private bool hasScorch;

	private uint scorchSeed;

	private Vector3d inertialVelocity;

	private Vector3d airVelocity;

	private CelestialBody body;

	private double latitude;

	private double longitude;

	private double surfaceAltitude;

	private float age;

	private float groundFireEnd;

	private float domeRadius;

	private float air;

	private float flickerPhase;

	private double gasRate;

	private bool active;

	private bool surface;

	private bool lit;

	private bool dome;

	internal bool HasVolume
	{
		get
		{
			if (active && volume != null)
			{
				return volume.Active;
			}
			return false;
		}
	}

	internal bool ReservedVolume
	{
		get
		{
			if (active && volume != null)
			{
				return volume.Reserved;
			}
			return false;
		}
	}

	internal double Score
	{
		get
		{
			if (plan != null)
			{
				return plan.VisualEnergyScore * (Lingering ? 0.25 : 1.0);
			}
			return 0.0;
		}
	}

	private bool Lingering
	{
		get
		{
			if (plan != null)
			{
				return (double)age > Math.Max(VolumeEvolution.VolumeDuration(plan), groundFireEnd) + 2.0;
			}
			return false;
		}
	}

	internal float Age => age;

	internal double CoverRadius
	{
		get
		{
			if (plan != null)
			{
				if (!ReservedVolume)
				{
					return Math.Max(4.0, plan.Radius * 1.5);
				}
				return VolumeEvolution.DomeRadius(plan) * 1.5;
			}
			return 0.0;
		}
	}

	private bool FireShown
	{
		get
		{
			if (fireVolume != null)
			{
				return fireVolume.Visible;
			}
			return false;
		}
	}

	internal bool HasFireVolume
	{
		get
		{
			if (active)
			{
				return FireShown;
			}
			return false;
		}
	}

	internal bool NeedsNearDepth
	{
		get
		{
			if (active)
			{
				if (volume == null || !volume.NeedsNearDepth)
				{
					if (FireShown)
					{
						return fireTarget != CameraRig.Far;
					}
					return false;
				}
				return true;
			}
			return false;
		}
	}

	internal bool NeedsFarDepth
	{
		get
		{
			if (active)
			{
				if (volume == null || !volume.NeedsFarDepth)
				{
					if (FireShown)
					{
						if (!(fireTarget == null))
						{
							return fireTarget == CameraRig.Far;
						}
						return true;
					}
					return false;
				}
				return true;
			}
			return false;
		}
	}

	internal float FireGlow { get; private set; }

	internal Vector3 FirePosition { get; private set; }

	internal float FireRange { get; private set; }

	internal int ReservedDebris
	{
		get
		{
			if (!active)
			{
				return 0;
			}
			return particles.ReservedDebris;
		}
	}

	internal bool HasLight
	{
		get
		{
			if (active)
			{
				return flash.enabled;
			}
			return false;
		}
	}

	internal bool Active => active;

	internal int ParticleCount
	{
		get
		{
			if (!active)
			{
				return 0;
			}
			int num = ((poolFire != null && column != null) ? (poolFire.particleCount + column.particleCount + tongues.particleCount) : 0);
			return particles.Count + num;
		}
	}

	internal void ReleaseVolume()
	{
		if (volume != null)
		{
			volume.Clear();
		}
	}

	internal void ShowFor(Camera cam)
	{
		if (active)
		{
			if (volume != null)
			{
				volume.ShowFor(cam);
			}
			if (fireVolume != null)
			{
				fireVolume.Show(CameraRig.IsFlight(cam) && (fireTarget == null || cam == fireTarget));
			}
		}
	}

	internal FxInstance(int index, ProceduralAssets assets, FxSettings settings, VolumeAssets volumes)
	{
		Root = new GameObject("VolumetricExplosionFX visual slot " + index);
		Root.layer = EffectLayer.Value;
		DebrisRoot = new GameObject("VolumetricExplosionFX visual debris " + index);
		DebrisRoot.layer = EffectLayer.Value;
		particles = new BlastParticles(Root.transform, DebrisRoot.transform, new BlastMaterials
		{
			Glow = assets.Glow,
			Cloud = assets.Cloud,
			Debris = assets.Debris,
			Trail = assets.Trail,
			Shard = assets.Shard,
			HeatDebris = assets.HeatDebris,
			SmokeFire = volumes.SmokeFire,
			SmokeCloud = volumes.SmokeCloud,
			TrailSmoke = volumes.TrailMaterial,
			TrailGlow = volumes.TrailGlow,
			Flame = volumes.Flame
		}, settings);
		GameObject gameObject = new GameObject("core light");
		gameObject.layer = EffectLayer.Value;
		gameObject.transform.SetParent(Root.transform, worldPositionStays: false);
		flash = gameObject.AddComponent<Light>();
		flash.type = LightType.Point;
		flash.shadows = LightShadows.None;
		flash.enabled = false;
		if (volumes.Material != null)
		{
			volume = new HeroVolume(Root.transform, volumes);
		}
		if (volumes.FireMaterial != null && volumes.Cube != null)
		{
			fireVolume = new FireVolumeRenderer(Root.transform, volumes.Cube, volumes.FireMaterial, EffectLayer.Value);
		}
		if (volumes.SmokeFire != null)
		{
			poolFire = SmokeParticles.Create(Root.transform, "pool fire", volumes.SmokeFire, Math.Min(160, settings.ParticlesPerEvent + 32), 0f, 0.45f);
			column = SmokeParticles.Create(Root.transform, "smoke column", volumes.SmokeCloud, Math.Min(240, settings.ParticlesPerEvent + 96), 0.3f, 1f);
			tongues = ((volumes.Flame != null) ? SmokeParticles.CreateFlameSheets(Root.transform, "flames", volumes.Flame, 200) : SmokeParticles.CreateFlames(Root.transform, "flame tongues", assets.Glow, 160));
			residueEmitter.Sheets = volumes.Flame != null;
		}
		if (volumes.Scorch != null)
		{
			GameObject gameObject2 = new GameObject("scorched ground");
			gameObject2.layer = EffectLayer.Value;
			gameObject2.transform.SetParent(Root.transform, worldPositionStays: false);
			scorchMesh = new Mesh();
			scorchMesh.name = "Original scorch draped on the ground";
			gameObject2.AddComponent<MeshFilter>().sharedMesh = scorchMesh;
			scorch = gameObject2.AddComponent<MeshRenderer>();
			scorch.sharedMaterial = volumes.Scorch;
			scorch.shadowCastingMode = ShadowCastingMode.Off;
			scorch.receiveShadows = false;
			scorch.enabled = false;
		}
		Root.SetActive(value: false);
		DebrisRoot.SetActive(value: false);
	}

	internal void Begin(EventCluster cluster, FxPlan fx, FxSettings settings, int debrisBudget, bool light, bool allowVolume, bool allowDistortion)
	{
		Clear();
		active = true;
		plan = fx;
		kind = cluster.Representative.Kind;
		PartSnapshot part = cluster.Representative.Part;
		bool flag = cluster.Ground && settings.Ground;
		bool flag2 = kind == EventKind.WaterImpact && settings.Water;
		surface = flag || kind != EventKind.Destruction;
		air = (float)Numbers.Clamp(plan.Atmosphere, 0.0, 1.0);
		Vector3 vector = SnapshotReader.Vector(flag ? cluster.GroundPoint : cluster.Position);
		Quaternion quaternion = Quaternion.FromToRotation(Vector3.up, SnapshotReader.Vector(part.Environment.Up));
		Root.transform.position = vector;
		Root.transform.rotation = quaternion;
		DebrisRoot.transform.position = vector;
		DebrisRoot.transform.rotation = quaternion;
		Root.SetActive(value: true);
		DebrisRoot.SetActive(value: true);
		inertialVelocity = new Vector3d(part.InertialVelocity.X, part.InertialVelocity.Y, part.InertialVelocity.Z);
		airVelocity = inertialVelocity - new Vector3d(part.SurfaceVelocity.X, part.SurfaceVelocity.Y, part.SurfaceVelocity.Z);
		body = null;
		for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
		{
			if (FlightGlobals.Bodies[i].bodyName == part.Environment.Body)
			{
				body = FlightGlobals.Bodies[i];
				break;
			}
		}
		if (body != null && surface)
		{
			latitude = body.GetLatitude(vector);
			longitude = body.GetLongitude(vector);
			surfaceAltitude = body.GetAltitude(vector);
		}
		float num = (float)Math.Min(1.8, 1.0 + Math.Log(1 + cluster.Members) * 0.15);
		plan.Radius *= num;
		Quaternion quaternion2 = Quaternion.Inverse(quaternion);
		Vector3 velocity = quaternion2 * SnapshotReader.Vector(part.SurfaceVelocity);
		BlastSource[] array = null;
		if (cluster.SampleCount > 0)
		{
			array = new BlastSource[cluster.SampleCount];
			for (int j = 0; j < cluster.SampleCount; j++)
			{
				Vector3 vector2 = quaternion2 * (SnapshotReader.Vector(cluster.SamplePositions[j]) - vector);
				array[j] = new BlastSource(new Vec3(vector2.x, vector2.y, vector2.z), cluster.SampleTimes[j] - cluster.FirstTime, cluster.SampleScores[j] / Math.Max(cluster.RepresentativeScore, 0.001));
			}
		}
		layout = BlastLayout.Create(plan, new Vec3(velocity.x, velocity.y, velocity.z), flag, flag2, (uint)(cluster.Id * -1640531535) ^ part.PersistentId, array);
		double num2 = Reentry.GasTime(air, layout.RadiusMeters);
		gasRate = (double.IsInfinity(num2) ? 0.0 : (1.0 / num2));
		residue = ((settings.Residue && settings.Explosions && (flag | flag2)) ? Residue.Plan(plan, flag, flag2, settings.ResidueSeconds) : default(ResiduePlan));
		if (poolFire == null)
		{
			residue.FireSeconds = 0.0;
		}
		uint num3 = (uint)(cluster.Id * 7919) ^ part.PersistentId;
		Vec3 vec = Aftermath.Wind(num3, air);
		Vector3 wind = new Vector3((float)vec.X, 0f, (float)vec.Z);
		flickerPhase = (float)(num3 % 1000) * 0.01f;
		bool flag3 = residue.HasFire && fireVolume != null && settings.Volumetric;
		residueEmitter.Volume = flag3;
		if (flag3)
		{
			fireVolume.Begin(residue.FireRadius, residue.FlameHeight, wind, part.Environment.Gravity, num3);
		}
		else if (fireVolume != null)
		{
			fireVolume.Clear();
		}
		if (residue.HasFire)
		{
			residueEmitter.Begin(residue, plan, (uint)(cluster.Id * 747796405) ^ part.PersistentId, 0f, wind);
			SmokeParticles.Bend(column, wind, 0.12f);
			poolFire.Play();
			column.Play();
			tongues.Play();
		}
		else
		{
			residueEmitter.Stop();
		}
		scorchSeed = (uint)(cluster.Id * -1403630843) ^ part.PersistentId;
		hasScorch = scorch != null && residue.ScorchRadius > 0.0 && BuildScorch((float)residue.ScorchRadius);
		groundFireEnd = (((!residue.HasFire & flag) && settings.Explosions && plan.FireFraction > 0.05 && (double)air > 0.1) ? (2.9f + 3f * (float)plan.FireFraction) : 0f);
		if (volume != null)
		{
			volume.Begin(allowVolume, allowDistortion, cluster.Id, settings.VolumeSteps, flag, flag2, body, layout);
			volume.Tick(plan, 0f, settings, 0f, 1f);
		}
		dome = volume != null && volume.Reserved;
		domeRadius = (float)VolumeEvolution.DomeRadius(plan);
		age = 0f;
		float? floor = null;
		bool floorWet = false;
		if (!surface && body != null && air > 0.3f)
		{
			double num4 = DebrisField.Floor(body, vector, quaternion * Vector3.up, 2f);
			double num5 = (body.ocean ? (0.0 - body.GetAltitude(vector)) : double.NegativeInfinity);
			if (num5 > num4)
			{
				num4 = num5;
				floorWet = true;
			}
			if (num4 > -1500.0)
			{
				floor = (float)num4;
			}
		}
		particles.Begin(new BlastContext
		{
			Plan = plan,
			Layout = layout,
			Settings = settings,
			Kind = kind,
			Ground = flag,
			Water = flag2,
			Surface = surface,
			Dome = dome,
			Air = air,
			DomeRadius = domeRadius,
			Gravity = (float)part.Environment.Gravity,
			ImpactSpeed = (float)Math.Abs(Vec3.Dot(part.SurfaceVelocity, part.Environment.Up)),
			Velocity = velocity,
			Floor = floor,
			FloorWet = floorWet,
			Sunlit = (float)FxPool.Starlight(vector),
			DebrisBudget = debrisBudget,
			Seed = num3,
			GasRate = gasRate
		});
		bool flag4 = settings.Explosions && plan.FireFraction > 0.05;
		lit = light && settings.Lighting && (dome ? (VolumeEvolution.LightIntensity(volume.State) > 0.05) : flag4);
		flash.enabled = lit;
		flash.color = Color.Lerp(new Color(0.92f, 0.94f, 1f), new Color(1f, 0.84f, 0.62f), air);
		flash.transform.localPosition = Vector3.zero;
		flash.range = Mathf.Clamp((float)Math.Max(layout.RadiusMeters * 2.5, plan.Radius * 5.0), 10f, 120f);
		flash.intensity = (dome ? ((float)VolumeEvolution.LightIntensity(volume.State)) : (3f * (float)plan.FireFraction));
	}

	internal bool AddSecondary(Vector3 worldPosition, double weight)
	{
		if (!active || volume == null)
		{
			return false;
		}
		return volume.AddSecondary(worldPosition, age, weight);
	}

	internal float Coverage(Camera cam)
	{
		if (!active)
		{
			return 0f;
		}
		return ((volume != null) ? volume.Coverage(cam) : 0f) + ((fireVolume != null) ? fireVolume.Coverage(cam) : 0f);
	}

	internal bool Tick(float dt, FxSettings settings, float stepScale)
	{
		if (!active)
		{
			return false;
		}
		age += dt;
		if (!surface)
		{
			Vector3d frameVelocity = Krakensbane.GetFrameVelocity();
			Vector3d vector3d = airVelocity + (inertialVelocity - airVelocity) * Math.Exp((0.0 - gasRate) * (double)age);
			Vector3d vector3d2 = airVelocity + (inertialVelocity - airVelocity) * Math.Exp(-0.8 * (double)air * (double)age);
			Root.transform.position += (Vector3)(vector3d - frameVelocity) * dt;
			DebrisRoot.transform.position += (Vector3)(vector3d2 - frameVelocity) * dt;
		}
		else if (body != null)
		{
			Vector3 position = body.GetWorldSurfacePosition(latitude, longitude, surfaceAltitude);
			Root.transform.position = position;
			DebrisRoot.transform.position = position;
		}
		float num;
		if (residue.HasFire)
		{
			num = (float)Residue.FireLevel(residue, age);
		}
		else
		{
			num = ((age < groundFireEnd) ? Mathf.Clamp01((groundFireEnd - age) / (groundFireEnd - 0.4f)) : 0f);
		}
		if (volume != null)
		{
			volume.Tick(plan, age, settings, num, stepScale);
		}
		if (residue.HasFire)
		{
			residueEmitter.Tick(age, dt, poolFire, column, tongues);
		}
		if (residueEmitter.Volume && fireVolume != null)
		{
			float num2 = (residue.HasFire ? ((float)Residue.FireLevel(residue, age)) : 0f);
			float flame = (float)residue.FlameHeight * (0.55f + 0.45f * Mathf.Sqrt(num2));
			float flicker = 1f + 0.08f * Mathf.Sin(age * 9.1f + flickerPhase);
			fireVolume.Tick(num2, flame, residue.PuffHz, Mathf.Max(16f, 40f * Mathf.Clamp(stepScale, 0.4f, 1f)), flicker);
			fireTarget = CameraRig.Pick(fireVolume.Center, fireVolume.Reach);
		}
		particles.Tick(age, dt, residue.HasFire ? 0f : num, HasVolume, (volume != null) ? volume.State : default(VolumeState));
		if (hasScorch)
		{
			scorchProperties.SetVector(ScorchId, new Vector4((float)Residue.ScorchLevel(residue, age), Mathf.Clamp01(1f - age / 14f) * (float)plan.FireFraction * (float)((air > 0.1f) ? 1 : 0), scorchSeed % 997, (air < 0.1f) ? 1 : 0));
			scorch.SetPropertyBlock(scorchProperties);
		}
		float num3 = 1f + 0.15f * (0.6f * Mathf.Sin(age * 13.7f + flickerPhase) + 0.4f * Mathf.Sin(age * 23.3f + flickerPhase * 2.3f));
		if (HasVolume)
		{
			float num4 = (float)VolumeEvolution.LightIntensity(volume.State) * ((age > 0.3f) ? num3 : 1f);
			flash.enabled = lit && num4 > 0.03f;
			flash.intensity = num4;
			flash.transform.localPosition = Vector3.up * (float)(VolumeEvolution.LightHeight(volume.State) * volume.State.BoxRadius);
		}
		else if (num > 0f && lit)
		{
			flash.enabled = true;
			flash.intensity = 0.8f * num * num3;
			flash.range = (residue.HasFire ? Mathf.Max(domeRadius, (float)residue.FireRadius * 8f) : domeRadius);
			flash.transform.localPosition = Vector3.up * (residue.HasFire ? ((float)residue.FlameHeight * 0.35f) : 1f);
		}
		else if (age > 0.18f)
		{
			flash.enabled = false;
		}
		else
		{
			flash.intensity = 3f * (float)plan.FireFraction * Mathf.Pow(Mathf.Clamp01(1f - age / 0.18f), 2f);
		}
		float num5 = (HasVolume ? ((float)VolumeEvolution.LightIntensity(volume.State)) : 0f);
		float num6 = num * 1.4f;
		FireGlow = Mathf.Max(num5, num6) * ((age > 0.3f) ? num3 : 1f);
		FirePosition = ((num5 >= num6) ? flash.transform.position : Root.transform.TransformPoint(Vector3.up * (residue.HasFire ? ((float)residue.FlameHeight * 0.35f) : 1f)));
		FireRange = ((num5 >= num6) ? Mathf.Max(flash.range, 10f) : Mathf.Max((float)residue.FireRadius * 8f + 5f, 10f));
		bool flag = particles.Alive || (poolFire != null && (poolFire.IsAlive() || column.IsAlive() || tongues.IsAlive())) || FireShown;
		bool flag2 = volume != null && volume.Distorting;
		double num7 = Math.Max(residue.HasFire ? residue.SmokeSeconds : 0.0, hasScorch ? residue.ScorchSeconds : 0.0);
		bool flag3 = (double)age < num7;
		double num8 = Math.Max(Math.Max(VolumeEvolution.VolumeDuration(plan), groundFireEnd) + 4.0, Math.Max(num7 + 14.0, particles.Horizon));
		if ((double)age > num8 || (!HasVolume && !flag2 && !flag && !particles.Pending && num <= 0f && !flag3))
		{
			Clear();
			return false;
		}
		return true;
	}

	internal void Shift(Vector3 offset)
	{
		if (active)
		{
			Root.transform.position -= offset;
			DebrisRoot.transform.position -= offset;
		}
	}

	private bool BuildScorch(float radius)
	{
		Transform frame = Root.transform;
		int mask = SnapshotReader.SurfaceMask();
		bool flag = ScorchMesh.Build(scorchMesh, radius, 16, delegate(Vector3 local, out Vector3 point, out Vector3 normal)
		{
			Vector3 vector = frame.TransformPoint(local);
			Vector3 up = frame.up;
			if (Physics.Raycast(vector + up * (radius + 5f), -up, out var hitInfo, 2f * radius + 10f, mask, QueryTriggerInteraction.Ignore) && hitInfo.collider != null && hitInfo.collider.GetComponentInParent<Part>() == null)
			{
				point = frame.InverseTransformPoint(hitInfo.point);
				normal = frame.InverseTransformDirection(hitInfo.normal);
				return true;
			}
			point = local;
			normal = Vector3.up;
			return false;
		});
		scorch.enabled = flag;
		return flag;
	}

	internal void Clear()
	{
		if (volume != null)
		{
			volume.Clear();
		}
		particles.Clear();
		if (poolFire != null)
		{
			poolFire.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
			column.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
			tongues.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
		}
		residueEmitter.Stop();
		residue = default;
		FireGlow = 0f;
		if (fireVolume != null)
		{
			fireVolume.Clear();
		}
		fireTarget = null;
		if (scorch != null)
		{
			scorch.enabled = false;
		}
		hasScorch = false;
		active = false;
		flash.enabled = false;
		layout = null;
		Root.SetActive(value: false);
		DebrisRoot.SetActive(value: false);
	}

	public void Dispose()
	{
		UnityEngine.Object.Destroy(Root);
		UnityEngine.Object.Destroy(DebrisRoot);
	}
}
