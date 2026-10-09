using System;
using UnityEngine;
using UnityEngine.Rendering;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Rendering;

internal sealed class HeroVolume
{
	private readonly Transform transform;

	private readonly MeshRenderer renderer;

	private readonly MeshRenderer flareRenderer;

	private readonly MeshRenderer distortRenderer;

	private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();

	private readonly MaterialPropertyBlock flareProperties = new MaterialPropertyBlock();

	private readonly MaterialPropertyBlock distortProperties = new MaterialPropertyBlock();

	private static readonly int Shell = Shader.PropertyToID("_Shell");

	private static readonly int Core = Shader.PropertyToID("_Core");

	private static readonly int Cloud = Shader.PropertyToID("_Cloud");

	private static readonly int Context = Shader.PropertyToID("_Context");

	private static readonly int Steps = Shader.PropertyToID("_Steps");

	private static readonly int Light = Shader.PropertyToID("_LightDirection");

	private static readonly int SunColor = Shader.PropertyToID("_SunColor");

	private static readonly int Flare = Shader.PropertyToID("_Flare");

	private static readonly int Fire = Shader.PropertyToID("_Fire");

	private static readonly int Smoke = Shader.PropertyToID("_Smoke");

	private static readonly int Bounds = Shader.PropertyToID("_Bounds");

	private static readonly int Motion = Shader.PropertyToID("_Motion");

	private static readonly int Dust = Shader.PropertyToID("_Dust");

	private static readonly int Shock = Shader.PropertyToID("_Shock");

	private static readonly int Haze = Shader.PropertyToID("_Haze");

	private static readonly int Seed = Shader.PropertyToID("_Seed");

	private static readonly int[] LobeCenter = new int[5]
	{
		Shader.PropertyToID("_L0"),
		Shader.PropertyToID("_L1"),
		Shader.PropertyToID("_L2"),
		Shader.PropertyToID("_L3"),
		Shader.PropertyToID("_L4")
	};

	private static readonly int[] LobeAxis = new int[5]
	{
		Shader.PropertyToID("_A0"),
		Shader.PropertyToID("_A1"),
		Shader.PropertyToID("_A2"),
		Shader.PropertyToID("_A3"),
		Shader.PropertyToID("_A4")
	};

	private static readonly int[] LobeMotion = new int[5]
	{
		Shader.PropertyToID("_D0"),
		Shader.PropertyToID("_D1"),
		Shader.PropertyToID("_D2"),
		Shader.PropertyToID("_D3"),
		Shader.PropertyToID("_D4")
	};

	private bool admitted;

	private bool distort;

	private bool ground;

	private bool water;

	private CelestialBody body;

	private BlastLayout layout;

	private int seed;

	private int steps;

	internal VolumeState State;

	private bool showVolume;

	private bool showFlare;

	private bool showDistort;

	internal Camera Target { get; private set; }

	internal bool Active
	{
		get
		{
			if (admitted)
			{
				return showVolume;
			}
			return false;
		}
	}

	internal bool Reserved => admitted;

	internal bool Distorting => showDistort;

	internal bool NeedsNearDepth
	{
		get
		{
			if (!showVolume || !(Target != CameraRig.Far))
			{
				return showDistort;
			}
			return true;
		}
	}

	internal bool NeedsFarDepth
	{
		get
		{
			if (showVolume)
			{
				if (!(Target == null))
				{
					return Target == CameraRig.Far;
				}
				return true;
			}
			return false;
		}
	}

	internal void ShowFor(Camera cam)
	{
		bool flag = CameraRig.IsFlight(cam) && (Target == null || cam == Target);
		renderer.enabled = showVolume & flag;
		if (flareRenderer != null)
		{
			flareRenderer.enabled = showFlare & flag;
		}
		if (distortRenderer != null)
		{
			distortRenderer.enabled = (showDistort & flag) && cam == CameraRig.Near;
		}
	}

	internal HeroVolume(Transform root, VolumeAssets assets)
	{
		GameObject gameObject = new GameObject("Original shock dome and fireball volume");
		gameObject.layer = EffectLayer.Value;
		gameObject.transform.SetParent(root, worldPositionStays: false);
		transform = gameObject.transform;
		gameObject.AddComponent<MeshFilter>().sharedMesh = assets.Cube;
		renderer = Configure(gameObject.AddComponent<MeshRenderer>(), assets.Material);
		if (assets.FlareMaterial != null)
		{
			flareRenderer = Child("Original core glare", assets.Quad, assets.FlareMaterial);
		}
		if (assets.DistortMaterial != null)
		{
			distortRenderer = Child("Original shock and heat refraction", assets.Quad, assets.DistortMaterial);
		}
	}

	private MeshRenderer Child(string name, Mesh mesh, Material material)
	{
		GameObject gameObject = new GameObject(name);
		gameObject.layer = EffectLayer.Value;
		gameObject.transform.SetParent(transform, worldPositionStays: false);
		gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
		return Configure(gameObject.AddComponent<MeshRenderer>(), material);
	}

	private static MeshRenderer Configure(MeshRenderer r, Material m)
	{
		r.sharedMaterial = m;
		r.shadowCastingMode = ShadowCastingMode.Off;
		r.receiveShadows = false;
		r.lightProbeUsage = LightProbeUsage.Off;
		r.reflectionProbeUsage = ReflectionProbeUsage.Off;
		r.enabled = false;
		return r;
	}

	internal void Begin(bool allow, bool allowDistortion, int clusterSeed, int marchSteps, bool groundImpact, bool waterImpact, CelestialBody mainBody, BlastLayout blast)
	{
		admitted = allow;
		distort = allowDistortion && distortRenderer != null;
		renderer.enabled = false;
		if (flareRenderer != null)
		{
			flareRenderer.enabled = false;
		}
		if (distortRenderer != null)
		{
			distortRenderer.enabled = false;
		}
		showVolume = false;
		showFlare = false;
		showDistort = false;
		seed = clusterSeed;
		steps = marchSteps;
		ground = groundImpact;
		water = waterImpact;
		body = mainBody;
		layout = blast;
	}

	internal float Coverage(Camera cam)
	{
		if (!Active || cam == null)
		{
			return 0f;
		}
		double val = Math.Max(State.ShellRadius, State.BoundsCenter.Length + State.BoundsRadius);
		float num = (float)(State.BoxRadius * Math.Min(val, 1.8));
		Vector3 position = transform.position;
		float num2 = Vector3.Distance(cam.transform.position, position);
		if (num2 <= num)
		{
			return 1f;
		}
		float num3 = num / (num2 * Mathf.Tan(cam.fieldOfView * 0.5f * ((float)Math.PI / 180f)));
		return Mathf.Clamp01((float)Math.PI * num3 * num3 / (4f * Mathf.Max(cam.aspect, 0.1f)));
	}

	internal void Tick(FxPlan plan, float age, FxSettings settings, float groundFire, float stepScale)
	{
		State = VolumeEvolution.Evaluate(plan, age, settings.Explosions, settings.Burst, ground && settings.Ground, water && settings.Water, layout);
		if (State.Fade <= 0.0)
		{
			admitted = false;
		}
		showVolume = admitted && settings.Volumetric && State.Visible;
		showFlare = flareRenderer != null && showVolume && State.Glow + State.Heat > 0.01;
		Target = CameraRig.Pick(transform.position, (float)(State.BoxRadius * 1.7320508));
		bool flag = distort && settings.Shockwave && VolumeEvolution.DistortionVisible(State, groundFire) && (Target == null || Target == CameraRig.Near);
		showDistort = (distortRenderer != null) & flag;
		renderer.enabled = showVolume;
		if (flareRenderer != null)
		{
			flareRenderer.enabled = showFlare;
		}
		if (distortRenderer != null)
		{
			distortRenderer.enabled = showDistort;
		}
		if (!showVolume && !flag)
		{
			return;
		}
		transform.localScale = Vector3.one * (float)State.BoxRadius;
		if (flag)
		{
			VolumeEvolution.Distortion(State, groundFire, out var shock, out var haze);
			distortProperties.SetVector(Shock, V(shock));
			distortProperties.SetVector(Haze, V(haze));
			distortProperties.SetFloat(Seed, seed % 997);
			distortRenderer.SetPropertyBlock(distortProperties);
		}
		if (showVolume)
		{
			VolumeEvolution.Pack(State, seed, out var shell, out var core, out var cloud, out var context, out var fireball, out var smoke);
			properties.SetVector(Fire, V(fireball));
			properties.SetVector(Smoke, V(smoke));
			properties.SetVector(Shell, V(shell));
			properties.SetVector(Core, V(core));
			properties.SetVector(Cloud, V(cloud));
			properties.SetVector(Context, V(context));
			properties.SetVector(Bounds, V(VolumeEvolution.Bounds(State)));
			properties.SetVector(Motion, V(VolumeEvolution.Motion(State)));
			properties.SetVector(Dust, V(VolumeEvolution.Dust(State)));
			for (int i = 0; i < 5; i++)
			{
				VolumeEvolution.PackLobe(State, i, out var center, out var axis, out var displacement);
				properties.SetVector(LobeCenter[i], V(center));
				properties.SetVector(LobeAxis[i], V(axis));
				properties.SetVector(LobeMotion[i], V(displacement));
			}
			properties.SetFloat(Steps, Mathf.Max(10f, (float)steps * Mathf.Clamp(stepScale, 0.35f, 1f)));
			Vector3 normalized = transform.InverseTransformDirection(SunDirection(out var strength)).normalized;
			float w = Mathf.Clamp(RenderSettings.ambientLight.grayscale, 0.12f, 0.6f);
			properties.SetVector(Light, new Vector4(normalized.x, normalized.y, normalized.z, w));
			properties.SetColor(SunColor, new Color(1f, 0.96f, 0.9f, strength));
			renderer.SetPropertyBlock(properties);
			if (showFlare)
			{
				flareProperties.SetVector(Flare, V(VolumeEvolution.Flare(State)));
				flareRenderer.SetPropertyBlock(flareProperties);
			}
		}
	}

	internal bool AddSecondary(Vector3 worldPosition, float age, double weight)
	{
		if (layout == null || !admitted)
		{
			return false;
		}
		Vector3 vector = Quaternion.Inverse(transform.parent.rotation) * (worldPosition - transform.parent.position);
		return layout.AddSecondary(new Vec3(vector.x, vector.y, vector.z), age, weight);
	}

	private Vector3 SunDirection(out float strength)
	{
		strength = 1f;
		CelestialBody celestialBody = ((Planetarium.fetch != null) ? Planetarium.fetch.Sun : null);
		if (celestialBody == null)
		{
			return Vector3.up;
		}
		Vector3d vector3d = transform.position;
		Vector3d normalized = (celestialBody.position - vector3d).normalized;
		if (body != null && body != celestialBody)
		{
			double num = Vector3d.Dot(body.position - vector3d, normalized);
			if (num > 0.0)
			{
				double magnitude = (vector3d + normalized * num - body.position).magnitude;
				strength = (float)UtilMath.Clamp01((magnitude - body.Radius * 0.995) / (body.Radius * 0.01));
			}
		}
		return normalized;
	}

	private static Vector4 V(Vec4 v)
	{
		return new Vector4((float)v.X, (float)v.Y, (float)v.Z, (float)v.W);
	}

	internal void Clear()
	{
		admitted = false;
		distort = false;
		renderer.enabled = false;
		layout = null;
		showVolume = false;
		showFlare = false;
		showDistort = false;
		Target = null;
		if (flareRenderer != null)
		{
			flareRenderer.enabled = false;
		}
		if (distortRenderer != null)
		{
			distortRenderer.enabled = false;
		}
	}
}
