using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Rendering;

internal sealed class BlastParticles
{
	private readonly ParticleSystem fire;

	private readonly ParticleSystem smoke;

	private readonly ParticleSystem fragments;

	private readonly ParticleSystem spray;

	private readonly ParticleSystem embers;

	private readonly ParticleSystem chunks;

	private readonly ParticleSystem ring;

	private readonly ParticleSystem burst;

	private readonly ParticleSystem cloud;

	private readonly TrailShower shower;

	private readonly TrailShower heavy;

	private readonly TrailShower runaway;

	private readonly TrailShower plasma;

	private readonly TrailShower plasmaSmoke;

	private readonly ParticleSystem wake;

	private readonly ParticleSystem sparks;

	private readonly TrailShower dirt;

	private readonly ParticleSystem splash;

	private readonly Transform sparkGround;

	private Vector3 wind;

	private Color dust;

	private readonly ParticleSystem landingFire;

	private readonly ParticleSystem ember;

	private readonly ParticleSystem emberFlames;

	private readonly ParticleSystem emberSmoke;

	private readonly ParticleSystem groundFlames;

	private readonly bool heatDebris;

	private readonly bool sheets;

	private readonly AftermathPuff[] puffs = new AftermathPuff[48];

	private readonly List<Vector3> runawayAt = new List<Vector3>();

	private readonly List<float> runawaySize = new List<float>();

	private RunawayPath[] paths;

	private VisualRandom random;

	private BlastContext c;

	private float nextPuff;

	private float nextFlame;

	private float cloudStart;

	private float afterStart;

	private int remainingPuffs;

	private int afterLeft;

	private bool active;

	private bool cloudDone;

	private bool reentry;

	internal int ReservedDebris { get; private set; }

	internal float Horizon { get; private set; }

	internal bool Pending
	{
		get
		{
			if (active)
			{
				if (remainingPuffs <= 0)
				{
					if (!cloudDone)
					{
						return !float.IsInfinity(cloudStart);
					}
					return false;
				}
				return true;
			}
			return false;
		}
	}

	internal bool Alive
	{
		get
		{
			if (!active)
			{
				return false;
			}
			foreach (ParticleSystem item in Roots())
			{
				if (item != null && item.IsAlive())
				{
					return true;
				}
			}
			return false;
		}
	}

	internal int Count
	{
		get
		{
			if (!active)
			{
				return 0;
			}
			int num = 0;
			foreach (ParticleSystem item in Roots())
			{
				if (item != null)
				{
					num += item.particleCount;
				}
			}
			if (landingFire != null)
			{
				num += landingFire.particleCount;
			}
			if (ember != null)
			{
				num += ember.particleCount + emberFlames.particleCount + emberSmoke.particleCount;
			}
			if (wake != null)
			{
				num += wake.particleCount + sparks.particleCount;
			}
			return num;
		}
	}

	internal BlastParticles(Transform gas, Transform solid, BlastMaterials m, FxSettings settings)
	{
		heatDebris = m.HeatDebris;
		fire = System(gas, "hot lobes and ground fire", m.Glow, settings.ParticlesPerEvent);
		smoke = System(gas, "cooling cloud", m.Cloud, settings.ParticlesPerEvent);
		spray = System(gas, "ejecta and spray", m.Cloud, settings.ParticlesPerEvent);
		embers = System(gas, "embers", m.Glow, settings.ParticlesPerEvent);
		ring = System(gas, "shock dust ring", m.Cloud, 96);
		fragments = System(solid, "visual debris", m.Debris, settings.ParticlesPerEvent);
		chunks = System(solid, "burning chunks", m.Glow, 16);
		ParticleSystemRenderer component = fragments.GetComponent<ParticleSystemRenderer>();
		component.renderMode = ParticleSystemRenderMode.Mesh;
		component.mesh = m.Shard;
		ParticleSystem.RotationOverLifetimeModule rotationOverLifetime = fragments.rotationOverLifetime;
		rotationOverLifetime.enabled = true;
		rotationOverLifetime.separateAxes = true;
		rotationOverLifetime.x = new ParticleSystem.MinMaxCurve(-6f, 6f);
		rotationOverLifetime.y = new ParticleSystem.MinMaxCurve(-6f, 6f);
		rotationOverLifetime.z = new ParticleSystem.MinMaxCurve(-6f, 6f);
		ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = fragments.sizeOverLifetime;
		sizeOverLifetime.enabled = true;
		sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.88f, 1f), new Keyframe(1f, 0f)));
		ParticleSystem.MainModule main = fragments.main;
		main.startSize3D = true;
		ParticleSystem.ColorOverLifetimeModule colorOverLifetime = fragments.colorOverLifetime;
		Gradient gradient = new Gradient();
		gradient.SetKeys(new GradientColorKey[2]
		{
			new GradientColorKey(Color.white, 0f),
			new GradientColorKey(Color.white, 1f)
		}, (!heatDebris) ? new GradientAlphaKey[3]
		{
			new GradientAlphaKey(1f, 0f),
			new GradientAlphaKey(1f, 0.85f),
			new GradientAlphaKey(0f, 1f)
		} : new GradientAlphaKey[3]
		{
			new GradientAlphaKey(1f, 0f),
			new GradientAlphaKey(0.35f, 0.25f),
			new GradientAlphaKey(0f, 0.55f)
		});
		colorOverLifetime.color = gradient;
		ParticleSystem.SizeOverLifetimeModule sizeOverLifetime2 = embers.sizeOverLifetime;
		sizeOverLifetime2.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.4f)));
		ParticleSystem.NoiseModule noise = embers.noise;
		noise.strength = 0.6f;
		noise.frequency = 0.4f;
		ParticleSystem.SizeOverLifetimeModule sizeOverLifetime3 = chunks.sizeOverLifetime;
		sizeOverLifetime3.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.8f, 0.8f), new Keyframe(1f, 0.2f)));
		ParticleSystem.NoiseModule noise2 = chunks.noise;
		noise2.enabled = false;
		ParticleSystem.ColorOverLifetimeModule colorOverLifetime2 = chunks.colorOverLifetime;
		Gradient gradient2 = new Gradient();
		gradient2.SetKeys(new GradientColorKey[2]
		{
			new GradientColorKey(Color.white, 0f),
			new GradientColorKey(new Color(1f, 0.55f, 0.3f), 1f)
		}, new GradientAlphaKey[3]
		{
			new GradientAlphaKey(1f, 0f),
			new GradientAlphaKey(1f, 0.7f),
			new GradientAlphaKey(0f, 1f)
		});
		colorOverLifetime2.color = gradient2;
		ParticleSystem.TrailModule trails = chunks.trails;
		trails.enabled = false;
		trails.mode = ParticleSystemTrailMode.PerParticle;
		trails.ratio = 1f;
		trails.lifetime = new ParticleSystem.MinMaxCurve(0.45f);
		trails.minVertexDistance = 0.35f;
		trails.textureMode = ParticleSystemTrailTextureMode.Tile;
		trails.worldSpace = false;
		trails.dieWithParticles = false;
		trails.inheritParticleColor = false;
		trails.sizeAffectsWidth = true;
		trails.widthOverTrail = new ParticleSystem.MinMaxCurve(3.2f, new AnimationCurve(new Keyframe(0f, 0.45f), new Keyframe(1f, 1.6f)));
		chunks.GetComponent<ParticleSystemRenderer>().trailMaterial = m.Trail;
		ParticleSystem.NoiseModule noise3 = ring.noise;
		noise3.strength = 0.4f;
		noise3.frequency = 0.5f;
		ParticleSystem.SizeOverLifetimeModule sizeOverLifetime4 = ring.sizeOverLifetime;
		sizeOverLifetime4.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.4f), new Keyframe(0.35f, 1f), new Keyframe(1f, 1.7f)));
		ParticleSystem.ColorOverLifetimeModule colorOverLifetime3 = ring.colorOverLifetime;
		Gradient gradient3 = new Gradient();
		gradient3.SetKeys(new GradientColorKey[2]
		{
			new GradientColorKey(Color.white, 0f),
			new GradientColorKey(Color.white, 1f)
		}, new GradientAlphaKey[4]
		{
			new GradientAlphaKey(0f, 0f),
			new GradientAlphaKey(1f, 0.06f),
			new GradientAlphaKey(0.55f, 0.45f),
			new GradientAlphaKey(0f, 1f)
		});
		colorOverLifetime3.color = gradient3;
		if (m.Flame != null)
		{
			groundFlames = SmokeParticles.CreateFlameSheets(gas, "ground flames", m.Flame, 64);
		}
		sheets = m.Flame != null;
		GameObject gameObject = new GameObject("spark ground");
		gameObject.layer = EffectLayer.Value;
		gameObject.transform.SetParent(embers.transform, worldPositionStays: false);
		sparkGround = gameObject.transform;
		if (m.SmokeCloud != null)
		{
			splash = SmokeParticles.Create(gas, "splash column", m.SmokeCloud, 48, 0.3f, 1f);
			burst = SmokeParticles.Create(gas, "dust and spray", m.SmokeCloud, Math.Min(160, settings.ParticlesPerEvent + 32), 0.3f, 1f);
			ParticleSystem.SizeOverLifetimeModule sizeOverLifetime5 = burst.sizeOverLifetime;
			sizeOverLifetime5.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(0.3f, 1f), new Keyframe(1f, 1.7f)));
			cloud = SmokeParticles.Create(gas, "lingering cloud", m.SmokeCloud, puffs.Length, 0.35f, 0.9f);
			ParticleSystem.SizeOverLifetimeModule sizeOverLifetime6 = cloud.sizeOverLifetime;
			sizeOverLifetime6.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.9f), new Keyframe(0.3f, 1.3f), new Keyframe(1f, 2.1f)));
			ParticleSystem.ColorOverLifetimeModule colorOverLifetime4 = cloud.colorOverLifetime;
			Gradient gradient4 = new Gradient();
			gradient4.SetKeys(new GradientColorKey[2]
			{
				new GradientColorKey(Color.white, 0f),
				new GradientColorKey(Color.white, 1f)
			}, new GradientAlphaKey[4]
			{
				new GradientAlphaKey(0f, 0f),
				new GradientAlphaKey(1f, 0.09f),
				new GradientAlphaKey(0.7f, 0.5f),
				new GradientAlphaKey(0f, 1f)
			});
			colorOverLifetime4.color = gradient4;
			ParticleSystem.NoiseModule noise4 = cloud.noise;
			noise4.strength = 0.25f;
			noise4.frequency = 0.08f;
		}
		if (m.TrailSmoke != null)
		{
			shower = new TrailShower(gas, "burning fragments", m.Glow, m.TrailSmoke, 256);
			heavy = new TrailShower(gas, "heavy burning chunks", m.Glow, m.TrailSmoke, 24);
			runaway = new TrailShower(gas, "runaway booster", m.Glow, m.TrailSmoke, 4);
			if (m.TrailGlow != null)
			{
				plasma = new TrailShower(gas, "glowing fragments", m.Glow, m.TrailGlow, 96);
				plasma.System.GetComponent<ParticleSystemRenderer>().minParticleSize = 0.0025f;
				wake = System(plasma.System.transform, "plasma wake", m.Glow, 32);
				sparks = System(plasma.System.transform, "re-entry sparks", m.Glow, 160);
				Brake(wake, 3f);
				Brake(sparks, 1.6f);
				Render(sparks, ParticleSystemRenderMode.Stretch, 0.015f);
				sparks.GetComponent<ParticleSystemRenderer>().minParticleSize = 0.001f;
				ParticleSystem.NoiseModule noise5 = sparks.noise;
				noise5.enabled = false;
				ParticleSystem.SizeOverLifetimeModule sizeOverLifetime7 = sparks.sizeOverLifetime;
				sizeOverLifetime7.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.3f)));
			}
			plasmaSmoke = new TrailShower(gas, "re-entry smoke trails", m.Glow, m.TrailSmoke, 24);
			dirt = new TrailShower(gas, "dirt jets", m.Glow, m.TrailSmoke, 48);
			if (m.SmokeFire != null && m.SmokeCloud != null)
			{
				landingFire = ((m.Flame != null) ? SmokeParticles.CreateFlameSheets(shower.System.transform, "landing fires", m.Flame, 64) : SmokeParticles.Create(shower.System.transform, "landing fires", m.SmokeFire, 96, 0f, 0.85f));
				ember = TrailShower.Ember(heavy.System.transform, "burning chunks on the ground", m.Glow);
				emberFlames = ((m.Flame != null) ? SmokeParticles.CreateFlameSheets(ember.transform, "chunk flames", m.Flame, 128) : SmokeParticles.Create(ember.transform, "chunk flames", m.SmokeFire, 160, 0f, 0.8f));
				emberSmoke = SmokeParticles.Create(ember.transform, "chunk smoke", m.SmokeCloud, 200, 0.35f, 1f);
			}
		}
	}

	private static void Render(ParticleSystem p, ParticleSystemRenderMode mode, float velocityScale)
	{
		ParticleSystemRenderer component = p.GetComponent<ParticleSystemRenderer>();
		component.renderMode = mode;
		component.lengthScale = 1f;
		component.velocityScale = velocityScale;
	}

	private static void Brake(ParticleSystem p, float drag)
	{
		ParticleSystem.LimitVelocityOverLifetimeModule limitVelocityOverLifetime = p.limitVelocityOverLifetime;
		limitVelocityOverLifetime.enabled = true;
		limitVelocityOverLifetime.limit = 100000f;
		limitVelocityOverLifetime.dampen = 0f;
		limitVelocityOverLifetime.drag = drag;
		limitVelocityOverLifetime.multiplyDragByParticleSize = false;
		limitVelocityOverLifetime.multiplyDragByParticleVelocity = false;
	}

	private static ParticleSystem System(Transform parent, string name, Material material, int capacity)
	{
		GameObject gameObject = new GameObject(name);
		gameObject.layer = EffectLayer.Value;
		gameObject.transform.SetParent(parent, worldPositionStays: false);
		ParticleSystem particleSystem = gameObject.AddComponent<ParticleSystem>();
		particleSystem.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
		ParticleSystem.MainModule main = particleSystem.main;
		main.playOnAwake = false;
		main.loop = false;
		main.duration = 15f;
		main.simulationSpace = ParticleSystemSimulationSpace.Local;
		main.maxParticles = capacity;
		main.startSpeed = 0f;
		main.startLifetime = 1f;
		main.gravityModifier = 0f;
		main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
		particleSystem.useAutoRandomSeed = false;
		particleSystem.randomSeed = 1u;
		ParticleSystem.EmissionModule emission = particleSystem.emission;
		emission.enabled = false;
		ParticleSystem.ShapeModule shape = particleSystem.shape;
		shape.enabled = false;
		ParticleSystem.CollisionModule collision = particleSystem.collision;
		collision.enabled = false;
		ParticleSystem.NoiseModule noise = particleSystem.noise;
		noise.enabled = true;
		noise.strength = 0.2f;
		noise.frequency = 0.6f;
		ParticleSystem.ColorOverLifetimeModule colorOverLifetime = particleSystem.colorOverLifetime;
		colorOverLifetime.enabled = true;
		Gradient gradient = new Gradient();
		gradient.SetKeys(new GradientColorKey[2]
		{
			new GradientColorKey(Color.white, 0f),
			new GradientColorKey(Color.white, 1f)
		}, new GradientAlphaKey[4]
		{
			new GradientAlphaKey(0f, 0f),
			new GradientAlphaKey(1f, 0.04f),
			new GradientAlphaKey(0.65f, 0.5f),
			new GradientAlphaKey(0f, 1f)
		});
		colorOverLifetime.color = gradient;
		ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = particleSystem.sizeOverLifetime;
		sizeOverLifetime.enabled = true;
		sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(0.7f, 1.4f), new Keyframe(1f, 1.8f)));
		ParticleSystemRenderer component = particleSystem.GetComponent<ParticleSystemRenderer>();
		component.sharedMaterial = material;
		component.shadowCastingMode = ShadowCastingMode.Off;
		component.receiveShadows = false;
		return particleSystem;
	}

	private IEnumerable<ParticleSystem> Roots()
	{
		yield return fire;
		yield return smoke;
		yield return fragments;
		yield return spray;
		yield return embers;
		yield return chunks;
		yield return ring;
		if (burst != null)
		{
			yield return burst;
		}
		if (cloud != null)
		{
			yield return cloud;
		}
		if (shower != null)
		{
			yield return shower.System;
			yield return heavy.System;
			yield return runaway.System;
		}
		if (plasma != null)
		{
			yield return plasma.System;
		}
		if (plasmaSmoke != null)
		{
			yield return plasmaSmoke.System;
		}
		if (dirt != null)
		{
			yield return dirt.System;
		}
		if (splash != null)
		{
			yield return splash;
		}
		if (groundFlames != null)
		{
			yield return groundFlames;
		}
	}

	internal void Begin(BlastContext context)
	{
		Clear();
		c = context;
		active = true;
		random = new VisualRandom(c.Seed);
		uint seed = c.Seed;
		fire.randomSeed = seed + 1;
		smoke.randomSeed = seed + 2;
		fragments.randomSeed = seed + 3;
		spray.randomSeed = seed + 4;
		embers.randomSeed = seed + 5;
		chunks.randomSeed = seed + 6;
		ring.randomSeed = seed + 7;
		foreach (ParticleSystem item in Roots())
		{
			if (item != shower?.System && item != heavy?.System && item != runaway?.System && item != plasma?.System && item != plasmaSmoke?.System && item != dirt?.System)
			{
				item.Play();
			}
		}
		Vec3 vec = Aftermath.Wind(c.Seed, c.Air);
		wind = new Vector3((float)vec.X, 0f, (float)vec.Z);
		dust = new Color((float)c.Plan.DustR, (float)c.Plan.DustG, (float)c.Plan.DustB, 1f);
		FxPlan plan = c.Plan;
		FxSettings settings = c.Settings;
		float num = (float)plan.Radius;
		float num2 = (float)plan.ExpansionSpeed;
		bool flag = settings.Explosions && plan.FireFraction > 0.05;
		nextPuff = 0.12f;
		nextFlame = 0.35f;
		remainingPuffs = plan.PuffCount;
		double num3 = Aftermath.Start(plan, (c.Layout != null) ? c.Layout.Tempo : 1.0);
		cloudStart = ((c.Dome && settings.Explosions && cloud != null && !double.IsInfinity(num3)) ? ((float)num3) : float.PositiveInfinity);
		afterStart = ((!c.Dome && cloud != null && c.Air > 0.05f && settings.Explosions) ? (1.1f + 0.6f * random.Next()) : float.PositiveInfinity);
		afterLeft = (int)Mathf.Clamp(3f + (float)plan.VisualEnergyScore, 3f, 9f);
		Horizon = 6f;
		reentry = Reentry.Applies(plan, c.Surface, plan.ImpactSpeed);
		if (reentry)
		{
			EmitReentry();
			afterStart = float.PositiveInfinity;
			remainingPuffs = 0;
		}
		else if (!c.Dome)
		{
			float size = num * (flag ? 1.6f : 0.7f);
			Emit(fire, Lift(Vector3.zero, size), Vector3.zero, size, 0.22f, new Color(1f, 0.9f, 0.72f, flag ? 0.9f : 0.45f));
			if (flag)
			{
				for (int i = 0; i < 3; i++)
				{
					Emit(fire, random.Direction() * num * 0.12f, random.Direction() * num2 * 0.6f, num * (0.2f + random.Next() * 0.25f), 0.2f + random.Next() * 0.3f, new Color(1f, 0.82f, 0.55f, (float)plan.FireFraction));
				}
			}
			int num4 = 3 + (int)Math.Min(5.0, plan.VisualEnergyScore);
			for (int j = 0; j < num4; j++)
			{
				Vector3 vector = Upward(random.Direction());
				float size2 = num * (0.35f + random.Next() * 0.35f);
				Emit(smoke, Lift(vector * num * 0.15f, size2), vector * num2 * 0.25f + Vector3.up * 0.8f, size2, 1.2f + random.Next(), new Color(0.68f, 0.66f, 0.62f, 0.32f));
			}
		}
		if (settings.Burst && plan.ElectricFraction > 0.01)
		{
			for (int k = 0; k < 6; k++)
			{
				Emit(fire, Vector3.zero, random.Direction() * 5f, 0.035f, 0.12f + random.Next() * 0.15f, new Color(0.65f, 0.8f, 1f, (float)plan.ElectricFraction));
			}
		}
		if (!reentry)
		{
			EmitEmbers(flag, num, num2);
		}
		EmitDebris(num, num2);
		if (!reentry)
		{
			if (shower != null)
			{
				EmitShower();
			}
			else
			{
				EmitChunks(flag);
			}
		}
		if ((c.Kind == EventKind.GroundImpact && settings.Ground) || (c.Kind == EventKind.WaterImpact && settings.Water))
		{
			EmitSurface(num, num2);
			if (burst != null)
			{
				EmitBurst();
			}
		}
		if (settings.Shockwave && (c.Ground || c.Water) && c.Air > 0.15f)
		{
			EmitShockRing();
		}
		if (!reentry && !c.Surface && c.Air < 0.02f && burst != null)
		{
			EmitVacuum(num, num2);
		}
	}

	private Color Dust(float light, float alpha)
	{
		return new Color(Mathf.Min(1f, dust.r * light), Mathf.Min(1f, dust.g * light), Mathf.Min(1f, dust.b * light), alpha);
	}

	private void EmitVacuum(float radius, float speed)
	{
		FxPlan plan = c.Plan;
		ParticleSystem.ForceOverLifetimeModule forceOverLifetime = burst.forceOverLifetime;
		forceOverLifetime.enabled = false;
		ParticleSystem.LimitVelocityOverLifetimeModule limitVelocityOverLifetime = burst.limitVelocityOverLifetime;
		limitVelocityOverLifetime.enabled = false;
		float domeRadius = c.DomeRadius;
		if (plan.BurnableKg > 30.0 || plan.VaporFraction > 0.05)
		{
			int num = (int)Mathf.Clamp(8f + (float)plan.VisualEnergyScore * 0.5f, 8f, 12f);
			float num2 = Mathf.Clamp01(c.Sunlit);
			for (int i = 0; i < num; i++)
			{
				Vector3 vector = random.Direction();
				Emit(smoke, vector * domeRadius * 0.08f, vector * domeRadius * (0.55f + 0.45f * random.Next()), domeRadius * (1.5f + 0.8f * random.Next()), 2.5f + 1.5f * random.Next(), new Color(0.92f * num2, 0.95f * num2, num2, 0.2f * num2));
			}
		}
		int num3 = (int)Mathf.Clamp(40f + (float)plan.VisualEnergyScore * 10f, 40f, 110f);
		for (int j = 0; j < num3; j++)
		{
			Vector3 vector2 = random.Direction();
			SmokeParticles.Emit(burst, vector2 * radius * 0.1f, vector2 * (speed * (0.5f + 1.5f * random.Next()) + 5f), 0.25f + 0.35f * random.Next(), 3f + 3f * random.Next(), new Color(0.82f, 0.8f, 0.78f, 0.9f), random.Next() * 360f);
		}
		Horizon = Mathf.Max(Horizon, 8f);
	}

	private void EmitSplashColumn(float reach, float strength)
	{
		float num = (float)Splash.ColumnHeight(c.Plan);
		float num2 = Mathf.Max(c.Gravity, 1f) * 0.75f;
		if (!(num <= 0f))
		{
			ParticleSystem.ForceOverLifetimeModule forceOverLifetime = splash.forceOverLifetime;
			forceOverLifetime.enabled = true;
			forceOverLifetime.space = ParticleSystemSimulationSpace.Local;
			forceOverLifetime.x = wind.x * 0.3f;
			forceOverLifetime.y = 0f - num2;
			forceOverLifetime.z = wind.z * 0.3f;
			ParticleSystem.LimitVelocityOverLifetimeModule limitVelocityOverLifetime = splash.limitVelocityOverLifetime;
			limitVelocityOverLifetime.enabled = true;
			limitVelocityOverLifetime.limit = 100000f;
			limitVelocityOverLifetime.dampen = 0f;
			limitVelocityOverLifetime.drag = 0.15f;
			limitVelocityOverLifetime.multiplyDragByParticleSize = false;
			limitVelocityOverLifetime.multiplyDragByParticleVelocity = false;
			splash.Play();
			Render(splash, ParticleSystemRenderMode.Stretch, 0.14f);
			int num3 = (int)Mathf.Clamp(30f + 12f * strength, 30f, 44f);
			float num4 = Mathf.Clamp(num * 0.15f, 2f, Mathf.Max(2f, reach * 0.6f));
			for (int i = 0; i < num3; i++)
			{
				float num5 = num * (0.12f + 0.88f * Mathf.Sqrt(random.Next()));
				float num6 = Mathf.Sqrt(2f * num2 * num5) * 1.25f;
				Vector3 vector = random.Direction();
				vector.y = 0f;
				float size = num4 * (1.2f + 0.6f * random.Next()) * (0.75f + 0.5f * num5 / num);
				SmokeParticles.Emit(splash, Lift(vector * num4 * 0.15f, size), Vector3.up * num6 + vector * num4 * (0.05f + 0.2f * num5 / num), size, Mathf.Min(2f * num6 / num2 * 0.85f + 1f, 9f), new Color(0.95f, 0.97f, 1f, 0.5f), random.Next() * 360f);
			}
			Horizon = Mathf.Max(Horizon, 11f);
		}
	}

	private void EmitReentry()
	{
		FxPlan plan = c.Plan;
		ReentryPlan plan2 = Reentry.Plan(plan, c.Surface, plan.ImpactSpeed, c.Settings.ParticlesPerEvent);
		if (!plan2.Active)
		{
			return;
		}
		float num = (float)plan.ImpactSpeed;
		Vector3 vector = ((c.Velocity.sqrMagnitude > 1E-06f) ? c.Velocity.normalized : Vector3.forward);
		float num2 = (float)plan2.FlashSize;
		float num3 = (float)plan2.FlashSeconds;
		Emit(fire, Vector3.zero, Vector3.zero, num2, num3, new Color(1f, 0.97f, 0.9f, 1f));
		Emit(fire, Vector3.zero, Vector3.zero, num2 * 2.2f, num3 * 1.8f, new Color(1f, 0.62f, 0.4f, 0.45f));
		Horizon = Mathf.Max(Horizon, num3 * 1.8f + 1f);
		if (plasma != null)
		{
			uint seed = (uint)(random.Next() * 16777216f) ^ 0x9A5A;
			plasma.Stream(plan2, c.Velocity, c.GasRate, c.Gravity, new Color(1f, 0.97f, 0.9f, 1f), new Color(0.9f, 0.25f, 0.06f, 1f), seed, smoke: false, default);
			if (plan2.SmokeTrails > 0 && plasmaSmoke != null)
			{
				plasmaSmoke.Stream(plan2, c.Velocity, c.GasRate, c.Gravity, Color.white, Color.white, seed, smoke: true, new Color(0.72f, 0.71f, 0.69f, 1f));
			}
			wake.Play();
			sparks.Play();
			for (int i = 0; i < 16; i++)
			{
				Vector3 vector2 = random.Direction();
				Emit(wake, vector2 * num2 * 0.25f * random.Next(), vector * num * (0.05f + 0.25f * random.Next()) + vector2 * (float)plan2.Spread * 0.3f, num2 * (0.3f + 0.4f * random.Next()), 0.6f + 1.2f * random.Next(), new Color(1f, 0.5f + 0.25f * random.Next(), 0.3f + 0.15f * random.Next(), 0.45f));
			}
			int num4 = (int)Mathf.Clamp(50f + (float)plan.VisualEnergyScore * 10f, 50f, Mathf.Min(150, c.Settings.ParticlesPerEvent));
			for (int j = 0; j < num4; j++)
			{
				Vector3 vector3 = random.Direction();
				Emit(sparks, vector3 * num2 * 0.1f * random.Next(), vector * num * (0.3f + 0.65f * random.Next()) + vector3 * (float)plan2.Spread * (0.4f + 0.8f * random.Next()), 0.15f + 0.3f * random.Next(), 0.6f + 1.4f * random.Next(), new Color(1f, 0.6f + 0.35f * random.Next(), 0.3f + 0.2f * random.Next(), 1f));
			}
			Horizon = Mathf.Max(Horizon, (float)(plan2.GlowSeconds * 1.25 + ((plan2.SmokeTrails > 0) ? 6.5 : 1.5) + 1.0));
		}
	}

	private void EmitDirt()
	{
		ShowerPlan plan = Shower.Dirt(c.Plan, c.Gravity, c.Settings.ParticlesPerEvent, new Vec3(c.Velocity.x, c.Velocity.y, c.Velocity.z));
		if (plan.Kind != ShowerKind.None)
		{
			dirt.Burst(plan, Dust((c.Air > 0.05f) ? 0.7f : 0.85f, 1f), Dust(0.5f, 1f), Vector3.up * 0.5f, (float)Math.Max(0.5, c.Plan.Radius * 0.25), Vector3.zero, (uint)(random.Next() * 16777216f) ^ 0xD127, 0f, null, 0f);
			Horizon = Mathf.Max(Horizon, (float)(plan.LifeMax * plan.TrailLife / 0.8) + 2f);
		}
	}

	private void EmitBurst()
	{
		bool water = c.Water;
		float domeRadius = c.DomeRadius;
		float num = (float)Numbers.Clamp(c.Plan.VisualEnergyScore / 6.0, 0.25, 1.3);
		if (!(c.Air < 0.05f) || water)
		{
			ParticleSystem.ForceOverLifetimeModule forceOverLifetime = burst.forceOverLifetime;
			forceOverLifetime.enabled = true;
			forceOverLifetime.space = ParticleSystemSimulationSpace.Local;
			forceOverLifetime.x = 0f;
			forceOverLifetime.z = 0f;
			forceOverLifetime.y = (water ? (-0.6f) : (-0.15f));
			float num2 = (water ? 1.2f : 1.4f);
			forceOverLifetime.x = wind.x * num2;
			forceOverLifetime.z = wind.z * num2;
			ParticleSystem.LimitVelocityOverLifetimeModule limitVelocityOverLifetime = burst.limitVelocityOverLifetime;
			limitVelocityOverLifetime.enabled = true;
			limitVelocityOverLifetime.limit = 100000f;
			limitVelocityOverLifetime.dampen = 0f;
			limitVelocityOverLifetime.drag = num2;
			limitVelocityOverLifetime.multiplyDragByParticleSize = false;
			limitVelocityOverLifetime.multiplyDragByParticleVelocity = false;
			Color color = (water ? new Color(0.9f, 0.93f, 0.97f, 1f) : Dust(1.2f, 1f));
			float num3 = Mathf.Min(1f, num + 0.35f);
			float num4 = (water ? 1f : (0.8f + 0.8f * Mathf.Min(1f, num)));
			int num5 = (int)Mathf.Clamp(14f + 8f * num, 14f, 22f);
			for (int i = 0; i < num5; i++)
			{
				float f = ((float)i + random.Next() * 0.8f) / (float)num5 * (float)Math.PI * 2f;
				Vector3 vector = new Vector3(Mathf.Cos(f), 0f, Mathf.Sin(f));
				float size = domeRadius * (water ? (1.1f + 0.5f * random.Next()) : (0.9f + 0.5f * random.Next()));
				Vector3 velocity = vector * domeRadius * (water ? (0.15f + 0.2f * random.Next()) : (0.25f + 0.25f * random.Next())) + Vector3.up * (0.3f + 0.7f * random.Next());
				SmokeParticles.Emit(burst, Lift(vector * domeRadius * (0.1f + 0.2f * random.Next()), size), velocity, size, ((float)(water ? 5 : 6) + 4f * random.Next()) * num4, new Color(color.r, color.g, color.b, (water ? 0.16f : 0.2f) * num3), random.Next() * 360f);
			}
			if (water && splash != null)
			{
				EmitSplashColumn(domeRadius, num);
			}
			int num6 = ((!water || !(splash != null)) ? ((int)Mathf.Clamp(3f + 3f * num, 3f, 6f)) : 0);
			for (int j = 0; j < num6; j++)
			{
				Vector3 vector2 = random.Direction();
				vector2.y = Mathf.Abs(vector2.y) * 2.5f + 1f;
				vector2.Normalize();
				float size2 = domeRadius * (0.8f + 0.4f * random.Next());
				float num7 = (water ? (Mathf.Sqrt(c.ImpactSpeed + 20f) * (0.5f + 0.6f * random.Next())) : ((2f + 4f * num) * (0.4f + 0.8f * random.Next())));
				SmokeParticles.Emit(burst, Lift(vector2 * domeRadius * 0.1f, size2), vector2 * num7, size2, ((float)(water ? 5 : 8) + 3f * random.Next()) * num4, new Color(color.r, color.g, color.b, 0.3f * num3), random.Next() * 360f);
			}
			Horizon = Mathf.Max(Horizon, (float)(water ? 16 : 11) * num4 + 2f);
		}
	}

	private void EmitEjecta(float reach, float strength)
	{
		ParticleSystem.ForceOverLifetimeModule forceOverLifetime = burst.forceOverLifetime;
		forceOverLifetime.enabled = true;
		forceOverLifetime.space = ParticleSystemSimulationSpace.Local;
		forceOverLifetime.x = 0f;
		forceOverLifetime.z = 0f;
		forceOverLifetime.y = 0f - c.Gravity;
		ParticleSystem.LimitVelocityOverLifetimeModule limitVelocityOverLifetime = burst.limitVelocityOverLifetime;
		limitVelocityOverLifetime.enabled = false;
		int num = (int)Mathf.Clamp(40f + 30f * strength, 40f, 80f);
		float num2 = c.ImpactSpeed * 0.25f + 8f;
		for (int i = 0; i < num; i++)
		{
			float f = random.Next() * (float)Math.PI * 2f;
			float f2 = (35f + 30f * random.Next()) * ((float)Math.PI / 180f);
			float num3 = num2 * (0.5f + random.Next());
			Vector3 vector = new Vector3(Mathf.Cos(f), 0f, Mathf.Sin(f));
			float size = reach * (0.08f + 0.08f * random.Next());
			SmokeParticles.Emit(burst, Lift(vector * reach * 0.1f, size), vector * num3 * Mathf.Cos(f2) + Vector3.up * num3 * Mathf.Sin(f2), size, 2.5f + 2.5f * random.Next(), Dust(1.15f + 0.25f * random.Next(), 0.45f * Mathf.Min(1f, strength + 0.35f)), random.Next() * 360f);
		}
		Horizon = Mathf.Max(Horizon, 7f);
	}

	private void EmitEmbers(bool burning, float radius, float speed)
	{
		FxPlan plan = c.Plan;
		float air = c.Air;
		ParticleSystem.LimitVelocityOverLifetimeModule limitVelocityOverLifetime = embers.limitVelocityOverLifetime;
		limitVelocityOverLifetime.enabled = air > 0.05f;
		limitVelocityOverLifetime.limit = 8f;
		limitVelocityOverLifetime.dampen = 0.08f;
		Render(embers, (air > 0.05f) ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard, 0.05f);
		ParticleSystem.ForceOverLifetimeModule forceOverLifetime = embers.forceOverLifetime;
		forceOverLifetime.enabled = true;
		forceOverLifetime.space = ParticleSystemSimulationSpace.Local;
		forceOverLifetime.x = 0f;
		forceOverLifetime.z = 0f;
		forceOverLifetime.y = ((c.Surface || air > 0.02f) ? ((0f - c.Gravity) * ((air > 0.05f) ? 0.7f : 1f)) : 0f);
		float? num;
		if (c.Surface)
		{
			num = 0f;
		}
		else
		{
			num = ((c.Floor.HasValue && air > 0.3f) ? c.Floor : ((float?)null));
		}
		ParticleSystem.CollisionModule collision = embers.collision;
		collision.enabled = num.HasValue;
		if (num.HasValue)
		{
			sparkGround.localPosition = new Vector3(0f, num.Value, 0f);
			sparkGround.localRotation = Quaternion.identity;
			collision.type = ParticleSystemCollisionType.Planes;
			collision.SetPlane(0, sparkGround);
			collision.bounce = 0.35f;
			collision.dampen = 0.45f;
			collision.lifetimeLoss = 0.25f;
			collision.radiusScale = 0.5f;
		}
		int num2 = (int)Numbers.Clamp((burning || c.Dome) ? (20.0 + plan.VisualEnergyScore * 14.0) : (4.0 + plan.VisualEnergyScore * 3.0), 4.0, Math.Min(140, c.Settings.ParticlesPerEvent));
		float num3 = Mathf.Clamp(radius * 0.025f, 0.05f, 0.3f);
		int num4 = 3 + (int)(random.Next() * 4f);
		Vector3[] array = new Vector3[6];
		for (int i = 0; i < num4; i++)
		{
			array[i] = random.Direction();
			if (c.Surface)
			{
				array[i].y = Mathf.Abs(array[i].y) * 0.8f + 0.3f;
			}
		}
		for (int j = 0; j < num2; j++)
		{
			Vector3 vector = random.Direction();
			if (random.Next() < 0.6f)
			{
				vector = (array[Mathf.Min((int)(random.Next() * (float)num4), num4 - 1)].normalized + random.Direction() * 0.3f).normalized;
			}
			if (c.Surface && vector.y < 0f)
			{
				vector.y = (0f - vector.y) * 0.3f;
			}
			float lifetime = ((air > 0.05f) ? (1f + random.Next() * 2f) : (0.8f + random.Next() * 0.8f));
			Emit(embers, vector * radius * 0.15f, vector * speed * (0.4f + random.Next()) + Vector3.up * speed * 0.25f * air, num3 * (0.6f + random.Next()), lifetime, new Color(1f, 0.55f + random.Next() * 0.3f, 0.22f, 1f));
		}
	}

	private void EmitDebris(float radius, float speed)
	{
		FxPlan plan = c.Plan;
		ReservedDebris = (c.Settings.Debris ? Math.Min(plan.DebrisCount, c.DebrisBudget) : 0);
		float num = Mathf.Clamp(radius * 0.03f, 0.05f, 0.4f);
		for (int i = 0; i < ReservedDebris; i++)
		{
			Vector3 vector = random.Direction();
			if (c.Surface && vector.y < 0f)
			{
				vector.y = 0f - vector.y;
			}
			bool flag = i < ReservedDebris / 12;
			float num2 = num * (flag ? (2f + random.Next() * 1.5f) : (0.5f + random.Next() * 1.2f));
			ParticleSystem.EmitParams emitParams = new ParticleSystem.EmitParams
			{
				position = vector * radius * 0.08f,
				velocity = vector * speed * (0.5f + random.Next()) + Vector3.up * speed * 0.3f,
				startSize3D = new Vector3(num2 * (0.5f + random.Next() * 1.1f), num2 * (0.6f + random.Next() * 0.6f), num2 * (0.3f + random.Next() * 1.1f)),
				startLifetime = 1.5f + random.Next() * 3f
			};
			float num3 = random.Next();
			Color color = ((num3 < 0.2f) ? new Color(0.88f, 0.86f, 0.8f) : Color.Lerp(new Color(0.7f, 0.7f, 0.68f), new Color(0.3f, 0.29f, 0.27f), num3));
			float num4 = ((heatDebris && random.Next() < 0.25f + 0.6f * (float)plan.FireFraction) ? (0.6f + 0.4f * random.Next()) : 0f);
			color.a = (heatDebris ? num4 : 1f);
			emitParams.startColor = color;
			fragments.Emit(emitParams, 1);
		}
		ParticleSystem.ForceOverLifetimeModule forceOverLifetime = fragments.forceOverLifetime;
		forceOverLifetime.enabled = true;
		forceOverLifetime.space = ParticleSystemSimulationSpace.Local;
		forceOverLifetime.y = 0f - c.Gravity;
	}

	private void EmitShower()
	{
		FxPlan plan = c.Plan;
		FxSettings settings = c.Settings;
		if (!settings.Debris || !settings.Explosions)
		{
			return;
		}
		float num = ((c.Surface || c.Air > 0.02f) ? c.Gravity : 0f);
		ShowerPlan plan2 = Shower.Plan(plan, c.Surface, num, settings.ParticlesPerEvent);
		if (plan2.Kind == ShowerKind.None)
		{
			return;
		}
		bool flag = plan2.Kind == ShowerKind.Solid;
		float num2 = (flag ? 1f : 0.55f);
		Color smokeColor = new Color((float)plan.SmokeR * num2, (float)plan.SmokeG * num2, (float)plan.SmokeB * num2, 1f);
		Color head = (flag ? new Color(1f, 0.93f, 0.78f, 1f) : new Color(1f, 0.62f, 0.28f, 1f));
		Color tint = new Color((float)plan.SmokeR, (float)plan.SmokeG, (float)plan.SmokeB, 1f);
		Vector3 vector = ((!c.Surface && c.GasRate > 0.0) ? c.Velocity : Vector3.zero);
		shower.SetFrame(vector, c.GasRate);
		heavy.SetFrame(vector, c.GasRate);
		float num3 = (c.Surface ? ((float)Math.Max(1.0, c.Layout.RadiusMeters * 0.2)) : 0f);
		float? floor;
		if (c.Surface)
		{
			floor = 0f;
		}
		else
		{
			floor = ((c.Floor.HasValue && c.Air > 0.3f) ? c.Floor : ((float?)null));
		}
		bool flag2 = floor.HasValue && !c.Water && (c.Surface || !c.FloorWet) && c.Air > 0.1f;
		if (landingFire != null)
		{
			TrailShower.ConfigureLandingFire(landingFire, tint, sheets);
		}
		float spread = (float)Math.Max(0.5, plan.Radius * 0.3);
		shower.Burst(plan2, smokeColor, head, Vector3.up * num3, spread, vector, (uint)(random.Next() * 16777216f) ^ 0x51A5, floor, flag2 ? landingFire : null, sheets ? 0f : 1.2f);
		Horizon = Mathf.Max(Horizon, (float)(plan2.LifeMax * Math.Max(1.0, plan2.TrailLife) / 0.8) + 2f);
		ShowerPlan plan3 = Shower.Heavy(plan, c.Surface, num, settings.ParticlesPerEvent);
		if (plan3.Kind != ShowerKind.None)
		{
			if (ember != null)
			{
				TrailShower.ConfigureEmbers(ember, emberFlames, emberSmoke, tint, 8f, 16f, sheets);
			}
			heavy.Burst(plan3, smokeColor, head, Vector3.up * num3, spread, vector, (uint)(random.Next() * 16777216f) ^ 0x4EA7, floor, flag2 ? ember : null, 0f);
			Horizon = Mathf.Max(Horizon, (float)(plan3.LifeMax * Math.Max(1.0, plan3.TrailLife) / 0.8) + 2f);
			if (flag2)
			{
				Horizon = Mathf.Max(Horizon, (float)plan3.LifeMax + 16f + 9f + 1f);
			}
		}
		int num4 = Shower.Runaways(plan, c.Surface);
		if (num4 > 0)
		{
			StartRunaways(num4, smokeColor, floor);
		}
	}

	private void StartRunaways(int n, Color smokeColor, float? floor)
	{
		Vector3 velocity = ((!c.Surface && c.GasRate > 0.0) ? c.Velocity : Vector3.zero);
		runaway.SetFrame(velocity, c.GasRate);
		paths = new RunawayPath[n];
		float[] array = new float[n];
		Vector3[] array2 = new Vector3[n];
		runawayAt.Clear();
		runawaySize.Clear();
		for (int i = 0; i < n; i++)
		{
			paths[i] = RunawayPath.Create(c.Seed * 31 + (uint)(i * 977), new Vec3(velocity.x, velocity.y, velocity.z), c.Gravity, c.Air, floor.HasValue ? ((double)floor.Value) : double.NegativeInfinity, 12.0);
			array[i] = (float)paths[i].ThrustTime + 1.2f;
			array2[i] = Vector3.zero;
			runawayAt.Add(Vector3.zero);
			runawaySize.Add(5f);
		}
		Color color = new Color(Mathf.Min(1f, smokeColor.r * 1.05f), Mathf.Min(1f, smokeColor.g * 1.05f), Mathf.Min(1f, smokeColor.b * 1.05f), 1f);
		runaway.Trace(n, color, 4f + 8f * c.Air, array, array2, new Color(1f, 0.9f, 0.72f, 1f), 5f, 2.4, plume: true);
		Horizon = Mathf.Max(Horizon, 26.6f);
	}

	private void EmitChunks(bool burning)
	{
		FxPlan plan = c.Plan;
		float air = c.Air;
		double radiusMeters = c.Layout.RadiusMeters;
		bool flag = plan.PartKind == PartKind.SolidBooster;
		if (!c.Settings.Debris || !burning || (radiusMeters < 4.0 && !flag))
		{
			return;
		}
		int num = Math.Min(flag ? 12 : 8, (flag ? 5 : 2) + (int)(random.Next() * (float)(flag ? 6 : 5)));
		ParticleSystem.TrailModule trails = chunks.trails;
		trails.enabled = air > 0.1f;
		Color color = (flag ? new Color(0.92f, 0.9f, 0.86f) : new Color(0.3f, 0.28f, 0.26f));
		Gradient gradient = new Gradient();
		gradient.SetKeys(new GradientColorKey[2]
		{
			new GradientColorKey(color, 0f),
			new GradientColorKey(Color.Lerp(color, new Color(0.6f, 0.58f, 0.55f), 0.6f), 1f)
		}, new GradientAlphaKey[3]
		{
			new GradientAlphaKey(0.85f, 0f),
			new GradientAlphaKey(0.45f, 0.5f),
			new GradientAlphaKey(0f, 1f)
		});
		trails.colorOverTrail = new ParticleSystem.MinMaxGradient(gradient);
		ParticleSystem.ForceOverLifetimeModule forceOverLifetime = chunks.forceOverLifetime;
		forceOverLifetime.enabled = true;
		forceOverLifetime.space = ParticleSystemSimulationSpace.Local;
		forceOverLifetime.y = 0f - c.Gravity;
		ParticleSystem.LimitVelocityOverLifetimeModule limitVelocityOverLifetime = chunks.limitVelocityOverLifetime;
		limitVelocityOverLifetime.enabled = air > 0.05f;
		limitVelocityOverLifetime.limit = 60f;
		limitVelocityOverLifetime.dampen = 0.02f * air;
		float num2 = Mathf.Clamp(Mathf.Sqrt((float)radiusMeters) * 7f, 12f, 55f);
		for (int i = 0; i < num; i++)
		{
			Vector3 vector = random.Direction();
			if (c.Surface)
			{
				vector.y = Mathf.Abs(vector.y) * 0.8f + 0.35f;
			}
			vector.Normalize();
			Emit(chunks, vector * (float)radiusMeters * 0.1f, vector * num2 * (0.6f + random.Next() * 0.6f), 0.3f + random.Next() * 0.35f, flag ? (3f + random.Next() * 2f) : (2f + random.Next() * 1.5f), new Color(1f, 0.62f + random.Next() * 0.2f, 0.25f, 1f));
		}
	}

	private void EmitSurface(float radius, float speed)
	{
		FxPlan plan = c.Plan;
		bool water = c.Water;
		float air = c.Air;
		float domeRadius = c.DomeRadius;
		Vector3 velocity = c.Velocity;
		velocity.y = 0f;
		velocity = Vector3.ClampMagnitude(velocity, 30f) * 0.12f;
		float num = Mathf.Clamp(Mathf.Sqrt(c.ImpactSpeed) * radius, 1f, 40f);
		bool flag = air < 0.1f && !water;
		Render(spray, ParticleSystemRenderMode.Stretch, flag ? 0.08f : 0.035f);
		ParticleSystem.NoiseModule noise = spray.noise;
		noise.enabled = !flag;
		int num2;
		if (flag)
		{
			num2 = Math.Min(c.Settings.ParticlesPerEvent, plan.PuffCount * 3);
		}
		else if (water)
		{
			num2 = Math.Min(c.Settings.ParticlesPerEvent, plan.PuffCount * 2);
		}
		else
		{
			num2 = (c.Dome ? Math.Max(3, plan.PuffCount / 4) : plan.PuffCount);
		}
		if (burst != null && !flag)
		{
			num2 = Math.Max(3, num2 / 3);
		}
		float num3 = (float)c.Layout.RadiusMeters;
		for (int i = 0; i < num2; i++)
		{
			Vector3 vector = random.Direction();
			vector.y = 0f;
			vector.Normalize();
			Vector3 local = vector * 0.2f;
			Vector3 velocity2;
			float size;
			float lifetime;
			Color color;
			if (water)
			{
				int num4 = i % 10;
				if (num4 < 3)
				{
					local = vector * (num3 + radius) * 0.05f * random.Next();
					velocity2 = vector * speed * 0.08f + Vector3.up * num * (1.8f + random.Next() * 0.8f) + velocity * 0.5f;
					size = radius * (0.06f + random.Next() * 0.05f);
					lifetime = 2.2f + random.Next() * 1.2f;
					color = new Color(0.9f, 0.95f, 1f, 0.45f);
				}
				else if (num4 < 7)
				{
					local = vector * domeRadius * (0.15f + random.Next() * 0.25f);
					velocity2 = vector * speed * (0.3f + random.Next() * 0.3f) + Vector3.up * num * (1f + random.Next() * 0.6f) + velocity;
					size = radius * (0.07f + random.Next() * 0.06f);
					lifetime = 1.8f + random.Next() * 1.2f;
					color = new Color(0.86f, 0.93f, 1f, 0.5f);
				}
				else
				{
					velocity2 = vector * speed * (0.7f + random.Next() * 0.5f) + Vector3.up * num * (0.3f + random.Next() * 0.4f) + velocity;
					size = radius * (0.06f + random.Next() * 0.06f);
					lifetime = 1.2f + random.Next();
					color = new Color(0.85f, 0.94f, 1f, 0.5f);
				}
			}
			else if (flag)
			{
				float f = 0.5f + random.Next() * 0.6f;
				float num5 = speed * (0.6f + random.Next() * 0.9f) + num * 0.4f;
				velocity2 = vector * num5 * Mathf.Cos(f) + Vector3.up * num5 * Mathf.Sin(f) + velocity;
				size = Mathf.Clamp(radius * (0.02f + random.Next() * 0.03f), 0.15f, 0.6f);
				lifetime = 1.8f + random.Next() * 2.2f;
				color = Dust(1.1f + 0.35f * random.Next(), 0.95f);
			}
			else
			{
				velocity2 = vector * speed + Vector3.up * num * (0.4f + random.Next()) + velocity;
				if (c.Dome)
				{
					velocity2 *= 1.6f;
				}
				size = radius * (c.Dome ? (0.03f + random.Next() * 0.05f) : (0.08f + random.Next() * 0.12f));
				lifetime = 1f + random.Next() * 2f;
				color = Dust(0.9f, 0.5f);
			}
			Emit(spray, Lift(local, size), velocity2, size, lifetime, color);
		}
		if (dirt != null && !water)
		{
			EmitDirt();
		}
		ParticleSystem.ForceOverLifetimeModule forceOverLifetime = spray.forceOverLifetime;
		forceOverLifetime.enabled = true;
		forceOverLifetime.space = ParticleSystemSimulationSpace.Local;
		forceOverLifetime.y = 0f - c.Gravity;
		if (water)
		{
			for (int j = 0; j < 8; j++)
			{
				Vector3 vector2 = random.Direction();
				vector2.y = Mathf.Abs(vector2.y) * 0.5f;
				float size2 = radius * (0.4f + random.Next() * 0.4f);
				Emit(smoke, Lift(vector2 * domeRadius * 0.25f, size2), vector2 * speed * 0.12f + Vector3.up * (1.5f + random.Next() * 2f), size2, 2.5f + random.Next() * 1.5f, new Color(0.88f, 0.92f, 0.96f, 0.22f));
			}
		}
	}

	private void EmitShockRing()
	{
		VolumeEvolution.ShockFront(c.Plan, c.Layout, out var speed, out var timeConstant, out var reach);
		float num = (float)Numbers.Clamp(c.Plan.VisualEnergyScore / 6.0, 0.2, 1.0);
		float num2 = Mathf.Clamp((float)timeConstant * 4f, 0.8f, 2.2f);
		ParticleSystem.LimitVelocityOverLifetimeModule limitVelocityOverLifetime = ring.limitVelocityOverLifetime;
		limitVelocityOverLifetime.enabled = true;
		limitVelocityOverLifetime.dampen = 1f;
		Keyframe[] array = new Keyframe[6];
		for (int i = 0; i < array.Length; i++)
		{
			float num3 = (float)i / ((float)array.Length - 1f);
			array[i] = new Keyframe(num3, Mathf.Exp((0f - num3) * num2 / (float)timeConstant));
		}
		limitVelocityOverLifetime.limit = new ParticleSystem.MinMaxCurve((float)speed, new AnimationCurve(array));
		int num4 = (int)(56f + 64f * num);
		float num5 = Mathf.Clamp((float)reach * 0.042f, 1.8f, 12f);
		Color color = (c.Water ? new Color(0.88f, 0.93f, 0.97f, 0.5f * num) : Dust(1.1f, 0.55f * num));
		for (int j = 0; j < num4; j++)
		{
			float f = ((float)j + random.Next() * 0.6f) / (float)num4 * (float)Math.PI * 2f;
			Vector3 vector = new Vector3(Mathf.Cos(f), 0f, Mathf.Sin(f));
			Emit(ring, vector * (float)reach * 0.03f + Vector3.up * num5 * 0.45f, vector * (float)speed * (0.92f + random.Next() * 0.16f) + Vector3.up * random.Next() * 2f, num5 * (0.7f + random.Next() * 0.6f), num2 * (0.85f + random.Next() * 0.3f), color);
		}
	}

	internal void Tick(float age, float dt, float groundFire, bool volume, in VolumeState state)
	{
		if (!active)
		{
			return;
		}
		FxPlan plan = c.Plan;
		FxSettings settings = c.Settings;
		if (groundFire > 0f && age >= nextFlame)
		{
			nextFlame = age + ((groundFlames != null) ? 0.3f : 0.07f);
			float num = c.DomeRadius * 0.16f;
			float num2 = (float)plan.Radius;
			if (groundFlames != null)
			{
				Vector2 vector = new Vector2(random.Next() * 2f - 1f, random.Next() * 2f - 1f) * num;
				float size = num2 * (0.6f + 0.6f * random.Next()) * (0.5f + 0.5f * groundFire);
				Emit(groundFlames, new Vector3(vector.x, 0f, vector.y), wind * 0.08f, size, 1.2f + 0.6f * random.Next(), new Color(1f, 1f, 1f, 0.6f + 0.4f * groundFire));
			}
			else
			{
				for (int i = 0; i < 2; i++)
				{
					Vector2 vector2 = new Vector2(random.Next() * 2f - 1f, random.Next() * 2f - 1f) * num;
					float size2 = num2 * (0.15f + random.Next() * 0.2f);
					Emit(fire, Lift(new Vector3(vector2.x, 0.1f, vector2.y), size2), new Vector3(0f, 1.5f + random.Next() * 2f, 0f), size2, 0.5f + random.Next() * 0.4f, new Color(1f, 0.5f + random.Next() * 0.2f, 0.15f, 0.85f * groundFire));
				}
			}
			if (random.Next() < 0.35f)
			{
				Emit(smoke, new Vector3(random.Next() - 0.5f, 1f, random.Next() - 0.5f) * num, new Vector3(0f, 2.5f, 0f), num2 * 0.5f, 2.5f, new Color(0.5f, 0.48f, 0.45f, 0.18f * groundFire));
			}
		}
		if (age >= nextPuff && remainingPuffs > 0)
		{
			int num3 = Math.Min(4, remainingPuffs);
			remainingPuffs -= num3;
			nextPuff += 0.12f;
			for (int j = 0; j < num3; j++)
			{
				float num4 = Mathf.Clamp01(1f - age / 0.8f) * (float)plan.FireFraction;
				Vector3 vector3 = Upward(random.Direction());
				if (!c.Dome && settings.Explosions && num4 > 0.02f)
				{
					Emit(fire, vector3 * (float)plan.Radius * age, vector3 * (float)plan.ExpansionSpeed * 0.4f, (float)plan.Radius * (0.15f + random.Next() * 0.25f), 0.3f + random.Next() * 0.4f, new Color(1f, 0.55f + num4 * 0.35f, 0.2f, num4 * 0.6f));
				}
				float num5 = (float)((settings.Explosions ? plan.SmokeFraction : 0.0) + (settings.Burst ? plan.VaporFraction : 0.0));
				float num6 = Mathf.Min((float)plan.Lifetime, 3.5f);
				if (plan.SmokeFraction < 0.01)
				{
					num6 = Mathf.Min(num6, 1.2f);
				}
				if (!c.Dome && num5 > 0.01f)
				{
					Color color = ((settings.Burst && plan.VaporFraction > plan.SmokeFraction) ? new Color(0.85f, 0.9f, 0.94f, num5 * 0.35f) : new Color(0.5f, 0.48f, 0.45f, num5 * 0.28f));
					Emit(smoke, vector3 * (float)plan.Radius * (0.2f + age * 0.3f), vector3 * (float)plan.ExpansionSpeed * 0.1f + Vector3.up * 0.5f, (float)plan.Radius * (0.2f + random.Next() * 0.35f), num6, color);
				}
			}
		}
		if (paths != null)
		{
			for (int k = 0; k < paths.Length; k++)
			{
				Vec3 vec = paths[k].At(age);
				runawayAt[k] = new Vector3((float)vec.X, (float)vec.Y, (float)vec.Z);
				runawaySize[k] = (((double)age < paths[k].ThrustTime) ? (4.2f + 1.6f * random.Next()) : 1.2f);
			}
			runaway.Update(age, runawayAt, runawaySize);
		}
		if (shower != null)
		{
			shower.Update(age);
			heavy.Update(age);
		}
		if (plasma != null)
		{
			plasma.Update(age);
		}
		if (plasmaSmoke != null)
		{
			plasmaSmoke.Update(age);
		}
		if (dirt != null)
		{
			dirt.Update(age);
		}
		if (!cloudDone && age >= cloudStart)
		{
			cloudDone = true;
			if (volume)
			{
				EmitCloud(age, in state);
			}
		}
		if (age >= afterStart)
		{
			EmitAfterSmoke(age);
			afterStart = ((--afterLeft > 0) ? (age + 0.25f + 0.25f * random.Next()) : float.PositiveInfinity);
		}
	}

	private void EmitCloud(float age, in VolumeState state)
	{
		FxPlan plan = c.Plan;
		Vec3 vec = Aftermath.Wind(c.Seed, c.Air);
		int num = Aftermath.Build(state, plan, c.Seed, vec, c.Surface, puffs);
		if (num != 0)
		{
			double num2 = Numbers.Clamp(plan.Soot, 0.0, 1.0);
			Color color = new Color((float)Numbers.Clamp(plan.SmokeR * (1.1 - 0.3 * num2), 0.08, 1.0), (float)Numbers.Clamp(plan.SmokeG * (1.1 - 0.3 * num2), 0.08, 1.0), (float)Numbers.Clamp(plan.SmokeB * (1.1 - 0.3 * num2), 0.08, 1.0), 1f);
			cloud.Play();
			float num3 = 0f;
			for (int i = 0; i < num; i++)
			{
				AftermathPuff aftermathPuff = puffs[i];
				float num4 = 0.9f + 0.16f * random.Next();
				SmokeParticles.Emit(cloud, V(aftermathPuff.Position), V(aftermathPuff.Velocity), (float)aftermathPuff.Size, (float)aftermathPuff.Life, new Color(Mathf.Min(1f, color.r * num4), Mathf.Min(1f, color.g * num4), Mathf.Min(1f, color.b * num4), (float)Numbers.Clamp(aftermathPuff.Alpha, 0.0, 1.0)), random.Next() * 360f);
				num3 = Mathf.Max(num3, (float)aftermathPuff.Life);
			}
			Horizon = Mathf.Max(Horizon, age + num3 + 1f);
		}
	}

	private void EmitAfterSmoke(float age)
	{
		FxPlan plan = c.Plan;
		bool flag = plan.FireFraction > 0.05;
		float num = (float)plan.Radius;
		double num2 = Numbers.Clamp(plan.Soot, 0.0, 1.0);
		Color color = (flag ? new Color((float)Numbers.Clamp(plan.SmokeR * (1.1 - 0.3 * num2), 0.08, 1.0), (float)Numbers.Clamp(plan.SmokeG * (1.1 - 0.3 * num2), 0.08, 1.0), (float)Numbers.Clamp(plan.SmokeB * (1.1 - 0.3 * num2), 0.08, 1.0), 1f) : Dust(1.15f, 1f));
		cloud.Play();
		Vector3 vector = Upward(random.Direction());
		float size = num * (1.3f + 0.9f * random.Next());
		Vector3 velocity = Vector3.up * (0.6f + 1.4f * random.Next()) + wind * (0.8f + 0.4f * random.Next()) + vector * 0.6f;
		float num3 = 0.9f + 0.16f * random.Next();
		SmokeParticles.Emit(cloud, Lift(vector * num * 0.45f, size), velocity, size, 9f + 9f * random.Next(), new Color(Mathf.Min(1f, color.r * num3), Mathf.Min(1f, color.g * num3), Mathf.Min(1f, color.b * num3), flag ? 0.5f : 0.32f), random.Next() * 360f);
		Horizon = Mathf.Max(Horizon, age + 20f);
	}

	private static Vector3 V(Vec3 v)
	{
		return new Vector3((float)v.X, (float)v.Y, (float)v.Z);
	}

	private Vector3 Lift(Vector3 local, float size)
	{
		if (c.Surface)
		{
			local.y = Mathf.Max(local.y, 0f) + size * 0.5f;
		}
		return local;
	}

	private Vector3 Upward(Vector3 d)
	{
		if (c.Surface && d.y < 0f)
		{
			d.y = 0f - d.y;
		}
		return d;
	}

	private static void Emit(ParticleSystem p, Vector3 position, Vector3 velocity, float size, float lifetime, Color color)
	{
		p.Emit(new ParticleSystem.EmitParams
		{
			position = position,
			velocity = velocity,
			startSize = size,
			startLifetime = lifetime,
			startColor = color
		}, 1);
	}

	internal void Simulate(float dt)
	{
		foreach (ParticleSystem item in Roots())
		{
			if (item != null)
			{
				item.Simulate(dt, withChildren: true, restart: false, fixedTimeStep: false);
			}
		}
	}

	internal void Clear()
	{
		foreach (ParticleSystem item in Roots())
		{
			if (item != null)
			{
				item.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
			}
		}
		TrailShower[] array = new TrailShower[6] { shower, heavy, runaway, plasma, plasmaSmoke, dirt };
		for (int i = 0; i < array.Length; i++)
		{
			array[i]?.Clear();
		}
		active = false;
		ReservedDebris = 0;
		paths = null;
		cloudDone = false;
		remainingPuffs = 0;
		Horizon = 0f;
		afterStart = float.PositiveInfinity;
	}
}
