using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VolumetricExplosionFX.Rendering;

internal static class SmokeParticles
{
	private static readonly List<ParticleSystemVertexStream> Streams = new List<ParticleSystemVertexStream>
	{
		ParticleSystemVertexStream.Position,
		ParticleSystemVertexStream.Normal,
		ParticleSystemVertexStream.Color,
		ParticleSystemVertexStream.UV,
		ParticleSystemVertexStream.UV2,
		ParticleSystemVertexStream.AnimBlend,
		ParticleSystemVertexStream.Tangent,
		ParticleSystemVertexStream.Center,
		ParticleSystemVertexStream.SizeX
	};

	private static readonly List<ParticleSystemVertexStream> FlameStreams = new List<ParticleSystemVertexStream>
	{
		ParticleSystemVertexStream.Position,
		ParticleSystemVertexStream.Color,
		ParticleSystemVertexStream.Center,
		ParticleSystemVertexStream.SizeX,
		ParticleSystemVertexStream.UV,
		ParticleSystemVertexStream.AgePercent,
		ParticleSystemVertexStream.StableRandomX
	};

	internal static ParticleSystem Create(Transform parent, string name, Material material, int capacity, float firstFrame, float lastFrame)
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
		main.maxParticles = capacity;
		main.startSpeed = 0f;
		main.startLifetime = 4f;
		main.gravityModifier = 0f;
		main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
		particleSystem.useAutoRandomSeed = false;
		particleSystem.randomSeed = 1u;
		ParticleSystem.EmissionModule emission = particleSystem.emission;
		emission.enabled = false;
		ParticleSystem.ShapeModule shape = particleSystem.shape;
		shape.enabled = false;
		ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = particleSystem.sizeOverLifetime;
		sizeOverLifetime.enabled = true;
		sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(0.25f, 1f), new Keyframe(1f, 2.6f)));
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
			new GradientAlphaKey(1f, 0.06f),
			new GradientAlphaKey(0.75f, 0.6f),
			new GradientAlphaKey(0f, 1f)
		});
		colorOverLifetime.color = gradient;
		ParticleSystem.RotationOverLifetimeModule rotationOverLifetime = particleSystem.rotationOverLifetime;
		rotationOverLifetime.enabled = true;
		rotationOverLifetime.z = new ParticleSystem.MinMaxCurve(-0.35f, 0.35f);
		ParticleSystem.NoiseModule noise = particleSystem.noise;
		noise.enabled = true;
		noise.strength = 0.35f;
		noise.frequency = 0.25f;
		noise.scrollSpeed = 0.2f;
		ParticleSystem.TextureSheetAnimationModule textureSheetAnimation = particleSystem.textureSheetAnimation;
		textureSheetAnimation.enabled = true;
		textureSheetAnimation.numTilesX = 8;
		textureSheetAnimation.numTilesY = 8;
		textureSheetAnimation.animation = ParticleSystemAnimationType.SingleRow;
		textureSheetAnimation.rowMode = ParticleSystemAnimationRowMode.Random;
		textureSheetAnimation.cycleCount = 1;
		textureSheetAnimation.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, firstFrame, 1f, lastFrame));
		ParticleSystemRenderer component = particleSystem.GetComponent<ParticleSystemRenderer>();
		component.renderMode = ParticleSystemRenderMode.Billboard;
		component.sharedMaterial = material;
		component.sortMode = ParticleSystemSortMode.Distance;
		component.normalDirection = 1f;
		component.SetActiveVertexStreams(Streams);
		component.shadowCastingMode = ShadowCastingMode.Off;
		component.receiveShadows = false;
		component.maxParticleSize = 2f;
		return particleSystem;
	}

	internal static ParticleSystem CreateFlameSheets(Transform parent, string name, Material flame, int capacity)
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
		main.maxParticles = capacity;
		main.startSpeed = 0f;
		main.startLifetime = 1f;
		main.gravityModifier = 0f;
		main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
		particleSystem.useAutoRandomSeed = false;
		particleSystem.randomSeed = 7u;
		ParticleSystem.EmissionModule emission = particleSystem.emission;
		emission.enabled = false;
		ParticleSystem.ShapeModule shape = particleSystem.shape;
		shape.enabled = false;
		ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = particleSystem.sizeOverLifetime;
		sizeOverLifetime.enabled = true;
		sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.7f), new Keyframe(0.3f, 1f), new Keyframe(1f, 0.85f)));
		ParticleSystemRenderer component = particleSystem.GetComponent<ParticleSystemRenderer>();
		component.renderMode = ParticleSystemRenderMode.Billboard;
		component.sharedMaterial = flame;
		component.sortMode = ParticleSystemSortMode.Distance;
		component.SetActiveVertexStreams(FlameStreams);
		component.shadowCastingMode = ShadowCastingMode.Off;
		component.receiveShadows = false;
		component.maxParticleSize = 4f;
		return particleSystem;
	}

	internal static void Bend(ParticleSystem p, Vector3 wind, float rate)
	{
		ParticleSystem.ForceOverLifetimeModule forceOverLifetime = p.forceOverLifetime;
		forceOverLifetime.enabled = wind.sqrMagnitude > 0.0001f;
		forceOverLifetime.space = ParticleSystemSimulationSpace.Local;
		forceOverLifetime.x = wind.x * rate;
		forceOverLifetime.y = 0f;
		forceOverLifetime.z = wind.z * rate;
	}

	internal static ParticleSystem CreateFlames(Transform parent, string name, Material glow, int capacity)
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
		main.maxParticles = capacity;
		main.startSpeed = 0f;
		main.startLifetime = 1f;
		main.gravityModifier = 0f;
		main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
		particleSystem.useAutoRandomSeed = false;
		particleSystem.randomSeed = 5u;
		ParticleSystem.EmissionModule emission = particleSystem.emission;
		emission.enabled = false;
		ParticleSystem.ShapeModule shape = particleSystem.shape;
		shape.enabled = false;
		ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = particleSystem.sizeOverLifetime;
		sizeOverLifetime.enabled = true;
		sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(0.3f, 1f), new Keyframe(1f, 0.25f)));
		ParticleSystem.ColorOverLifetimeModule colorOverLifetime = particleSystem.colorOverLifetime;
		colorOverLifetime.enabled = true;
		Gradient gradient = new Gradient();
		gradient.SetKeys(new GradientColorKey[3]
		{
			new GradientColorKey(new Color(1f, 0.85f, 0.55f), 0f),
			new GradientColorKey(new Color(1f, 0.5f, 0.15f), 0.5f),
			new GradientColorKey(new Color(0.8f, 0.2f, 0.05f), 1f)
		}, new GradientAlphaKey[4]
		{
			new GradientAlphaKey(0f, 0f),
			new GradientAlphaKey(1f, 0.15f),
			new GradientAlphaKey(0.6f, 0.6f),
			new GradientAlphaKey(0f, 1f)
		});
		colorOverLifetime.color = gradient;
		ParticleSystem.NoiseModule noise = particleSystem.noise;
		noise.enabled = true;
		noise.strength = 0.6f;
		noise.frequency = 0.5f;
		noise.scrollSpeed = 0.6f;
		ParticleSystemRenderer component = particleSystem.GetComponent<ParticleSystemRenderer>();
		component.renderMode = ParticleSystemRenderMode.Stretch;
		component.lengthScale = 1.6f;
		component.velocityScale = 0.08f;
		component.sharedMaterial = glow;
		component.shadowCastingMode = ShadowCastingMode.Off;
		component.receiveShadows = false;
		component.maxParticleSize = 1f;
		return particleSystem;
	}

	internal static void Emit(ParticleSystem p, Vector3 position, Vector3 velocity, float size, float lifetime, Color color, float rotation)
	{
		p.Emit(new ParticleSystem.EmitParams
		{
			position = position,
			velocity = velocity,
			startSize = size,
			startLifetime = lifetime,
			startColor = color,
			rotation = rotation
		}, 1);
	}
}
