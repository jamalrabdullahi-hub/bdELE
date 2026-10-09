using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Rendering;

internal sealed class TrailShower
{
	private struct Head
	{
		internal Vec3 Start;

		internal Vec3 Velocity;

		internal double Drag;

		internal double Burn;

		internal double Active;

		internal double Land;

		internal float Size;

		internal bool Landed;

		internal Vector3 Last;

		internal double Fade;

		internal double Flare;

		internal double Warm;
	}

	private enum Shape
	{
		Spread,
		Plume,
		Taper,
		Dust
	}

	private const float TrailShare = 0.8f;

	internal readonly ParticleSystem System;

	private ParticleSystem.Particle[] buffer;

	private Head[] heads = new Head[0];

	private int count;

	private double gravity;

	private bool traced;

	private Color headColor;

	private ParticleSystem landing;

	private float rise;

	private Vec3 frameVelocity;

	private double frameRate;

	private bool moving;

	private bool streaming;

	private Color coolColor;

	internal bool Alive
	{
		get
		{
			if (System != null)
			{
				return System.IsAlive();
			}
			return false;
		}
	}

	internal int Count
	{
		get
		{
			if (!(System != null))
			{
				return 0;
			}
			return System.particleCount;
		}
	}

	internal TrailShower(Transform parent, string name, Material head, Material trail, int capacity)
	{
		GameObject gameObject = new GameObject(name);
		gameObject.layer = EffectLayer.Value;
		gameObject.transform.SetParent(parent, worldPositionStays: false);
		System = gameObject.AddComponent<ParticleSystem>();
		System.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
		ParticleSystem.MainModule main = System.main;
		main.playOnAwake = false;
		main.loop = false;
		main.duration = 60f;
		main.simulationSpace = ParticleSystemSimulationSpace.Local;
		main.maxParticles = capacity;
		main.startSpeed = 0f;
		main.startLifetime = 4f;
		main.gravityModifier = 0f;
		main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
		System.useAutoRandomSeed = false;
		System.randomSeed = 1u;
		ParticleSystem.EmissionModule emission = System.emission;
		emission.enabled = false;
		ParticleSystem.ShapeModule shape = System.shape;
		shape.enabled = false;
		ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = System.sizeOverLifetime;
		sizeOverLifetime.enabled = false;
		ParticleSystem.ColorOverLifetimeModule colorOverLifetime = System.colorOverLifetime;
		colorOverLifetime.enabled = false;
		ParticleSystem.TrailModule trails = System.trails;
		trails.enabled = true;
		trails.mode = ParticleSystemTrailMode.PerParticle;
		trails.ratio = 1f;
		trails.lifetime = new ParticleSystem.MinMaxCurve(0.8f);
		trails.minVertexDistance = 1.5f;
		trails.textureMode = ParticleSystemTrailTextureMode.Stretch;
		trails.worldSpace = false;
		trails.dieWithParticles = false;
		trails.inheritParticleColor = false;
		trails.sizeAffectsWidth = false;
		trails.sizeAffectsLifetime = false;
		ParticleSystemRenderer component = System.GetComponent<ParticleSystemRenderer>();
		component.renderMode = ParticleSystemRenderMode.Billboard;
		component.sharedMaterial = head;
		component.trailMaterial = trail;
		component.shadowCastingMode = ShadowCastingMode.Off;
		component.receiveShadows = false;
	}

	internal static void ConfigureLandingFire(ParticleSystem p, Color tint, bool sheets = false)
	{
		ParticleSystem.MainModule main = p.main;
		if (sheets)
		{
			main.startLifetime = new ParticleSystem.MinMaxCurve(2f, 3.5f);
			main.startSize = new ParticleSystem.MinMaxCurve(1.3f, 2.6f);
			main.startSpeed = 0f;
			main.startRotation = 0f;
			main.startColor = new Color(1f, 1f, 1f, 0.9f);
		}
		else
		{
			main.startLifetime = new ParticleSystem.MinMaxCurve(2f, 4f);
			main.startSize = new ParticleSystem.MinMaxCurve(0.9f, 1.8f);
			main.startSpeed = 0f;
			main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
			main.startColor = new Color(tint.r, tint.g, tint.b, 0.75f);
		}
	}

	internal static ParticleSystem Ember(Transform parent, string name, Material glow)
	{
		GameObject gameObject = new GameObject(name);
		gameObject.layer = EffectLayer.Value;
		gameObject.transform.SetParent(parent, worldPositionStays: false);
		ParticleSystem particleSystem = gameObject.AddComponent<ParticleSystem>();
		particleSystem.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
		ParticleSystem.MainModule main = particleSystem.main;
		main.playOnAwake = false;
		main.loop = false;
		main.duration = 60f;
		main.simulationSpace = ParticleSystemSimulationSpace.Local;
		main.maxParticles = 32;
		main.gravityModifier = 0f;
		main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
		particleSystem.useAutoRandomSeed = false;
		particleSystem.randomSeed = 3u;
		ParticleSystem.EmissionModule emission = particleSystem.emission;
		emission.enabled = false;
		ParticleSystem.ShapeModule shape = particleSystem.shape;
		shape.enabled = false;
		ParticleSystem.ColorOverLifetimeModule colorOverLifetime = particleSystem.colorOverLifetime;
		colorOverLifetime.enabled = true;
		Gradient gradient = new Gradient();
		gradient.SetKeys(new GradientColorKey[2]
		{
			new GradientColorKey(new Color(1f, 0.85f, 0.6f), 0f),
			new GradientColorKey(new Color(1f, 0.45f, 0.2f), 1f)
		}, new GradientAlphaKey[3]
		{
			new GradientAlphaKey(1f, 0f),
			new GradientAlphaKey(0.75f, 0.6f),
			new GradientAlphaKey(0f, 1f)
		});
		colorOverLifetime.color = gradient;
		ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = particleSystem.sizeOverLifetime;
		sizeOverLifetime.enabled = true;
		sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.5f)));
		ParticleSystemRenderer component = particleSystem.GetComponent<ParticleSystemRenderer>();
		component.renderMode = ParticleSystemRenderMode.Billboard;
		component.sharedMaterial = glow;
		component.shadowCastingMode = ShadowCastingMode.Off;
		component.receiveShadows = false;
		return particleSystem;
	}

	internal static void ConfigureEmbers(ParticleSystem ember, ParticleSystem flames, ParticleSystem smoke, Color tint, float minLife, float maxLife, bool sheets = false)
	{
		ParticleSystem.MainModule main = ember.main;
		main.startLifetime = new ParticleSystem.MinMaxCurve(minLife, maxLife);
		main.startSize = new ParticleSystem.MinMaxCurve(1.2f, 2.4f);
		main.startSpeed = 0f;
		main.startColor = new Color(1f, 0.7f, 0.4f, 0.9f);
		ParticleSystem.SubEmittersModule subEmitters = ember.subEmitters;
		subEmitters.enabled = true;
		if (subEmitters.subEmittersCount == 0)
		{
			subEmitters.AddSubEmitter(flames, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing);
			subEmitters.AddSubEmitter(smoke, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing);
		}
		if (sheets)
		{
			Feed(flames, 1.1f, 1.5f, 2.3f, 1.6f, 3.2f, 0.02f, 0.08f, new Color(1f, 1f, 1f, 0.85f));
		}
		else
		{
			Feed(flames, 2.6f, 0.8f, 1.4f, 1.2f, 2.6f, 0.8f, 1.8f, new Color(tint.r, tint.g, tint.b, 0.85f));
		}
		Feed(smoke, 1.1f, 6f, 9f, 2f, 3.8f, 1.4f, 2.6f, new Color(tint.r, tint.g, tint.b, 0.5f));
	}

	private static void Feed(ParticleSystem p, float rate, float minLife, float maxLife, float minSize, float maxSize, float minSpeed, float maxSpeed, Color color)
	{
		ParticleSystem.MainModule main = p.main;
		main.startLifetime = new ParticleSystem.MinMaxCurve(minLife, maxLife);
		main.startSize = new ParticleSystem.MinMaxCurve(minSize, maxSize);
		main.startSpeed = new ParticleSystem.MinMaxCurve(minSpeed, maxSpeed);
		main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
		main.startColor = color;
		ParticleSystem.EmissionModule emission = p.emission;
		emission.enabled = true;
		emission.rateOverTime = rate;
		ParticleSystem.ShapeModule shape = p.shape;
		shape.enabled = true;
		shape.shapeType = ParticleSystemShapeType.Cone;
		shape.angle = 15f;
		shape.radius = 0.4f;
		shape.rotation = new Vector3(-90f, 0f, 0f);
	}

	private void Look(Color smoke, double width, bool trailsOn, double step, Shape shape)
	{
		ParticleSystem.TrailModule trails = System.trails;
		trails.enabled = trailsOn;
		trails.inheritParticleColor = false;
		trails.lifetime = new ParticleSystem.MinMaxCurve(0.8f);
		trails.minVertexDistance = (float)Math.Max(0.5, step);
		AnimationCurve curve;
		GradientAlphaKey[] alphaKeys;
		switch (shape)
		{
		case Shape.Plume:
			curve = new AnimationCurve(new Keyframe(0f, 0.3f), new Keyframe(0.06f, 0.75f), new Keyframe(0.4f, 1.25f), new Keyframe(1f, 2.4f));
			alphaKeys = new GradientAlphaKey[5]
			{
				new GradientAlphaKey(0.9f, 0f),
				new GradientAlphaKey(1f, 0.05f),
				new GradientAlphaKey(0.6f, 0.4f),
				new GradientAlphaKey(0.25f, 0.8f),
				new GradientAlphaKey(0f, 1f)
			};
			break;
		case Shape.Dust:
			curve = new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(0.3f, 1f), new Keyframe(1f, 2.2f));
			alphaKeys = new GradientAlphaKey[3]
			{
				new GradientAlphaKey(0.85f, 0f),
				new GradientAlphaKey(0.55f, 0.25f),
				new GradientAlphaKey(0f, 1f)
			};
			break;
		case Shape.Taper:
			curve = new AnimationCurve(new Keyframe(0f, 0.7f), new Keyframe(0.2f, 1f), new Keyframe(1f, 1.5f));
			alphaKeys = new GradientAlphaKey[4]
			{
				new GradientAlphaKey(1f, 0f),
				new GradientAlphaKey(0.6f, 0.25f),
				new GradientAlphaKey(0.25f, 0.6f),
				new GradientAlphaKey(0f, 1f)
			};
			break;
		default:
			curve = new AnimationCurve(new Keyframe(0f, 0.12f), new Keyframe(0.15f, 0.45f), new Keyframe(0.5f, 1.1f), new Keyframe(1f, 2.2f));
			alphaKeys = new GradientAlphaKey[5]
			{
				new GradientAlphaKey(0.5f, 0f),
				new GradientAlphaKey(0.85f, 0.04f),
				new GradientAlphaKey(0.42f, 0.35f),
				new GradientAlphaKey(0.18f, 0.7f),
				new GradientAlphaKey(0f, 1f)
			};
			break;
		}
		trails.widthOverTrail = new ParticleSystem.MinMaxCurve((float)width, curve);
		Gradient gradient = new Gradient();
		if (shape == Shape.Taper)
		{
			gradient.SetKeys(new GradientColorKey[3]
			{
				new GradientColorKey(smoke, 0f),
				new GradientColorKey(new Color(smoke.r, smoke.g * 0.62f, smoke.b * 0.35f), 0.35f),
				new GradientColorKey(new Color(smoke.r * 0.85f, smoke.g * 0.3f, smoke.b * 0.1f), 1f)
			}, alphaKeys);
		}
		else
		{
			gradient.SetKeys(new GradientColorKey[2]
			{
				new GradientColorKey(smoke, 0f),
				new GradientColorKey(smoke, 1f)
			}, alphaKeys);
		}
		trails.colorOverTrail = new ParticleSystem.MinMaxGradient(gradient);
	}

	private void Ensure(int n)
	{
		if (heads.Length < n)
		{
			heads = new Head[Math.Max(n, 8)];
		}
		count = n;
	}

	private static Vector3 V(Vec3 v)
	{
		return new Vector3((float)v.X, (float)v.Y, (float)v.Z);
	}

	internal void Burst(ShowerPlan plan, Color smoke, Color head, Vector3 origin, float spread, Vector3 inherited, uint seed, float? floor, ParticleSystem onLanding, float rise)
	{
		if (plan.Kind == ShowerKind.None || plan.Count <= 0)
		{
			return;
		}
		Look(smoke, plan.TrailWidth, plan.Trails, (plan.TrailStep > 0.0) ? plan.TrailStep : 1.5, (plan.Kind == ShowerKind.Dirt) ? Shape.Dust : Shape.Spread);
		ParticleSystem.ForceOverLifetimeModule forceOverLifetime = System.forceOverLifetime;
		forceOverLifetime.enabled = false;
		ParticleSystem.LimitVelocityOverLifetimeModule limitVelocityOverLifetime = System.limitVelocityOverLifetime;
		limitVelocityOverLifetime.enabled = false;
		traced = false;
		streaming = false;
		gravity = plan.Gravity;
		headColor = head;
		landing = onLanding;
		this.rise = rise;
		Ensure(plan.Count);
		SplitRandom rnd = new SplitRandom(seed);
		double num = ((plan.TrailLife > 0.0) ? plan.TrailLife : 1.6);
		System.Play();
		if (landing != null)
		{
			landing.Play();
		}
		int num2 = Math.Max(0, Math.Min(plan.Clumps, 12));
		Vec3[] array = new Vec3[Math.Max(num2, 1)];
		for (int i = 0; i < num2; i++)
		{
			array[i] = Direction(plan, ref rnd);
		}
		for (int j = 0; j < plan.Count; j++)
		{
			Vec3 vec = Direction(plan, ref rnd);
			if (num2 > 0 && rnd.Next() < plan.ClumpShare)
			{
				vec = Climb(plan, (array[Math.Min((int)(rnd.Next() * (double)num2), num2 - 1)] + rnd.Direction() * 0.22).Normalized(vec));
			}
			double num3 = plan.SpeedMin + (plan.SpeedMax - plan.SpeedMin) * Math.Pow(rnd.Next(), 0.7);
			Vec3 vec2 = new Vec3(origin.x, origin.y, origin.z) + vec * ((double)spread * rnd.Next());
			Vec3 velocity = vec * num3 + new Vec3(inherited.x, inherited.y, inherited.z);
			double drag = plan.Drag * rnd.Range(0.75, 1.3);
			double num4 = plan.LifeMin + (plan.LifeMax - plan.LifeMin) * rnd.Next();
			double num5 = (floor.HasValue ? Ballistics.LandingTime(vec2.Y, velocity.Y, drag, gravity, floor.Value, num4 + 1.0) : double.PositiveInfinity);
			if (num5 <= 0.0)
			{
				num5 = double.PositiveInfinity;
			}
			double num6 = Math.Min(num4, num5);
			float num7 = (float)(plan.HeadSize * (0.6 + 0.8 * rnd.Next()));
			heads[j] = new Head
			{
				Start = vec2,
				Velocity = velocity,
				Drag = drag,
				Burn = num4,
				Active = num6,
				Land = num5,
				Size = num7,
				Last = V(vec2),
				Flare = -1.0
			};
			ParticleSystem.EmitParams emitParams = new ParticleSystem.EmitParams
			{
				position = V(vec2),
				velocity = Vector3.zero,
				startSize = num7,
				startColor = head,
				startLifetime = (float)Math.Max(num * num4 / 0.800000011920929, num6 + 0.1),
				randomSeed = (uint)j
			};
			System.Emit(emitParams, 1);
		}
	}

	internal void SetFrame(Vector3 velocity, double rate)
	{
		frameVelocity = new Vec3(velocity.x, velocity.y, velocity.z);
		frameRate = Math.Max(rate, 0.0);
		moving = velocity.sqrMagnitude > 1E-08f;
		if (System != null)
		{
			System.transform.localPosition = Vector3.zero;
		}
	}

	private Vec3 FrameOffset(double t)
	{
		t = Math.Max(t, 0.0);
		return frameVelocity * (t * Ballistics.E1(frameRate * t));
	}

	internal void Stream(ReentryPlan plan, Vector3 airVelocity, double frameRate, double g, Color hot, Color cool, uint seed, bool smoke, Color smokeColor)
	{
		if (!plan.Active || plan.Count <= 0)
		{
			return;
		}
		int num = (smoke ? Math.Min(plan.SmokeTrails, plan.Count) : plan.Count);
		if (num <= 0)
		{
			return;
		}
		double num2 = Math.Max(airVelocity.magnitude, 1f);
		double num3 = (smoke ? Numbers.Clamp(plan.GlowSeconds * 0.6, 2.0, 5.0) : Numbers.Clamp(plan.WakeLength / num2, 0.08, 1.0));
		double num4 = plan.GlowSeconds * 1.25;
		double num5 = (num4 + num3 + 0.3) / 0.75;
		Look(smoke ? smokeColor : Color.white, smoke ? (plan.TrailWidth * 3.0) : plan.TrailWidth, trailsOn: true, Math.Max(3.0, num2 * 0.025), (!smoke) ? Shape.Taper : Shape.Spread);
		ParticleSystem.TrailModule trails = System.trails;
		trails.inheritParticleColor = !smoke;
		trails.lifetime = new ParticleSystem.MinMaxCurve((float)(num3 / num5));
		ParticleSystem.ForceOverLifetimeModule forceOverLifetime = System.forceOverLifetime;
		forceOverLifetime.enabled = false;
		ParticleSystem.LimitVelocityOverLifetimeModule limitVelocityOverLifetime = System.limitVelocityOverLifetime;
		limitVelocityOverLifetime.enabled = false;
		SetFrame(airVelocity, frameRate);
		traced = false;
		streaming = true;
		landing = null;
		gravity = g;
		headColor = hot;
		coolColor = cool;
		Ensure(plan.Count);
		SplitRandom splitRandom = new SplitRandom(seed);
		System.Play();
		double num6 = (double)num / (double)plan.Count;
		int num7 = 0;
		for (int i = 0; i < plan.Count; i++)
		{
			if (num7 >= num)
			{
				break;
			}
			Vec3 vec = splitRandom.Direction();
			Vec3 vec2 = vec * (plan.Spread * Math.Sqrt(splitRandom.Next()));
			double num8 = splitRandom.Next();
			double num9 = plan.DragMin * Math.Pow(plan.DragMax / Math.Max(plan.DragMin, 1E-06), num8);
			double num10 = Math.Sqrt(plan.DragMin / Math.Max(num9, 1E-06));
			double num11 = Math.Min(plan.GlowSeconds * (0.3 + 0.7 * num10) * (0.8 + 0.4 * splitRandom.Next()), num4);
			Vec3 vec3 = vec * (plan.FlashSize * 0.08 * splitRandom.Next());
			float num12 = (float)(plan.HeadSize * (0.45 + 0.9 * Math.Pow(num10, 0.6)) * (0.8 + 0.4 * splitRandom.Next()));
			double num13 = splitRandom.Next();
			double num14 = splitRandom.Range(0.15, 0.75) * num11;
			if (!smoke || !(num8 >= num6))
			{
				heads[num7] = new Head
				{
					Start = vec3,
					Velocity = frameVelocity + vec2,
					Drag = num9,
					Burn = num11,
					Active = num11,
					Land = double.PositiveInfinity,
					Size = (smoke ? 0.05f : num12),
					Last = V(vec3),
					Fade = 0.6 * num9 + 0.35 / Math.Max(num11, 0.1),
					Flare = ((!smoke && num13 < plan.FlareShare) ? num14 : (-1.0)),
					Warm = 0.35 * (1.0 - num10)
				};
				ParticleSystem.EmitParams emitParams = new ParticleSystem.EmitParams
				{
					position = V(vec3),
					velocity = Vector3.zero,
					startSize = heads[num7].Size,
					startColor = (smoke ? new Color(smokeColor.r, smokeColor.g, smokeColor.b, 1f) : hot),
					startLifetime = (float)(num5 * (0.5 + 0.75 * num10)),
					randomSeed = (uint)num7
				};
				System.Emit(emitParams, 1);
				num7++;
			}
		}
		count = num7;
	}

	private static Vec3 Direction(ShowerPlan plan, ref SplitRandom rnd)
	{
		Vec3 vec = rnd.Direction();
		if (plan.Cone > 0.0)
		{
			return Climb(plan, (plan.Axis + vec * (plan.Cone * rnd.Next())).Normalized(plan.Axis));
		}
		double y = Math.Abs(vec.Y) * plan.UpBias + vec.Y * (1.0 - plan.UpBias);
		return Climb(plan, new Vec3(vec.X, y, vec.Z).Normalized(new Vec3(0.0, 1.0, 0.0)));
	}

	private static Vec3 Climb(ShowerPlan plan, Vec3 dir)
	{
		double num = Math.Max(plan.MinUp, (plan.UpBias > 0.5) ? 0.05 : (-1.0));
		if (!(dir.Y >= num))
		{
			return new Vec3(dir.X, num, dir.Z).Normalized(new Vec3(0.0, 1.0, 0.0));
		}
		return dir;
	}

	internal void Trace(int n, Color smoke, double width, float[] active, Vector3[] start, Color head, float size, double persistence = 1.4, bool plume = false)
	{
		Look(smoke, width, trailsOn: true, plume ? 2.5 : 1.5, plume ? Shape.Plume : Shape.Spread);
		ParticleSystem.ForceOverLifetimeModule forceOverLifetime = System.forceOverLifetime;
		forceOverLifetime.enabled = false;
		ParticleSystem.LimitVelocityOverLifetimeModule limitVelocityOverLifetime = System.limitVelocityOverLifetime;
		limitVelocityOverLifetime.enabled = false;
		traced = true;
		streaming = false;
		headColor = head;
		landing = null;
		gravity = 0.0;
		Ensure(n);
		System.Play();
		for (int i = 0; i < n; i++)
		{
			heads[i] = new Head
			{
				Burn = active[i],
				Active = active[i],
				Land = double.PositiveInfinity,
				Size = size,
				Last = start[i]
			};
			if (!(active[i] <= 0.05f))
			{
				ParticleSystem.EmitParams emitParams = new ParticleSystem.EmitParams
				{
					position = start[i],
					velocity = Vector3.zero,
					startSize = size,
					startColor = head,
					startLifetime = (float)Math.Max(persistence * (double)active[i] / 0.800000011920929, (double)active[i] + 0.1),
					randomSeed = (uint)i
				};
				System.Emit(emitParams, 1);
			}
		}
	}

	internal void Update(float age, IList<Vector3> positions = null, IList<float> sizes = null)
	{
		if (System == null)
		{
			return;
		}
		if (moving)
		{
			System.transform.localPosition = -V(FrameOffset(age));
		}
		int particleCount = System.particleCount;
		if (particleCount == 0)
		{
			return;
		}
		if (buffer == null || buffer.Length < particleCount)
		{
			buffer = new ParticleSystem.Particle[Math.Max(particleCount, 8)];
		}
		particleCount = System.GetParticles(buffer, particleCount);
		for (int i = 0; i < particleCount; i++)
		{
			int randomSeed = (int)buffer[i].randomSeed;
			if (randomSeed < 0 || randomSeed >= count)
			{
				continue;
			}
			bool flag = (double)age < heads[randomSeed].Active;
			if (streaming)
			{
				heads[randomSeed].Last = V(Ballistics.Position(heads[randomSeed].Start, heads[randomSeed].Velocity, heads[randomSeed].Drag, gravity, Math.Min(age, heads[randomSeed].Active)));
				buffer[i].position = heads[randomSeed].Last;
				buffer[i].velocity = Vector3.zero;
				float num = Mathf.Clamp01((float)((double)age / Math.Max(heads[randomSeed].Burn, 0.05)));
				float num2 = Mathf.Max(1f - (1f - (float)heads[randomSeed].Warm) * Mathf.Exp(0f - (float)(heads[randomSeed].Fade * (double)age)), num * num);
				float num3 = ((heads[randomSeed].Flare >= 0.0) ? (2.4f * Mathf.Exp(0f - Mathf.Pow((float)(((double)age - heads[randomSeed].Flare) / 0.08), 2f))) : 0f);
				Color color = Color.Lerp(headColor, coolColor, num2);
				color.a = (flag ? Mathf.Clamp01((1f - 0.7f * num2) * Mathf.Clamp01((1f - num) * 3f) * (1f + num3)) : 0f);
				buffer[i].startColor = color;
				buffer[i].startSize = (flag ? (heads[randomSeed].Size * (0.5f + 0.5f * (1f - num2)) * (1f + num3)) : 0f);
				continue;
			}
			if (traced)
			{
				if (flag && positions != null && randomSeed < positions.Count)
				{
					heads[randomSeed].Last = positions[randomSeed];
				}
			}
			else
			{
				heads[randomSeed].Last = V(Ballistics.Position(heads[randomSeed].Start, heads[randomSeed].Velocity, heads[randomSeed].Drag, gravity, Math.Min(age, heads[randomSeed].Active)));
			}
			buffer[i].position = heads[randomSeed].Last;
			buffer[i].velocity = Vector3.zero;
			float num4 = Mathf.Clamp01((float)((double)age / Math.Max(heads[randomSeed].Burn, 0.05)));
			ref ParticleSystem.Particle reference = ref buffer[i];
			float startSize;
			if (flag)
			{
				startSize = ((sizes != null && randomSeed < sizes.Count) ? sizes[randomSeed] : (heads[randomSeed].Size * Shrink(num4)));
			}
			else
			{
				startSize = 0f;
			}
			reference.startSize = startSize;
			Color color2 = Color.Lerp(headColor, new Color(headColor.r, headColor.g * 0.72f, headColor.b * 0.55f), num4);
			color2.a = headColor.a * Mathf.Clamp01((1f - num4) * 4f);
			buffer[i].startColor = color2;
			if (!heads[randomSeed].Landed && heads[randomSeed].Land <= heads[randomSeed].Burn && (double)age >= heads[randomSeed].Land)
			{
				heads[randomSeed].Landed = true;
				if (landing != null)
				{
					ParticleSystem.EmitParams emitParams = new ParticleSystem.EmitParams
					{
						position = heads[randomSeed].Last,
						velocity = new Vector3(0f, rise * (0.5f + (float)((double)randomSeed * 0.618034 % 1.0)), 0f)
					};
					landing.Emit(emitParams, 1);
				}
			}
		}
		System.SetParticles(buffer, particleCount);
	}

	private static float Shrink(float u)
	{
		if (!(u < 0.7f))
		{
			return Mathf.Max(0f, 0.85f * (1f - (u - 0.7f) / 0.3f));
		}
		return 1f - 0.15f * u / 0.7f;
	}

	internal void Clear()
	{
		if (System != null)
		{
			System.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
			System.transform.localPosition = Vector3.zero;
		}
		count = 0;
		landing = null;
		streaming = false;
		moving = false;
		frameVelocity = default;
		frameRate = 0.0;
	}
}
