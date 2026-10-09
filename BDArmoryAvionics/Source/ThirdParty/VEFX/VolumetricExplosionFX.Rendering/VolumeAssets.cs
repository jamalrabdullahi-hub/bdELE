using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace VolumetricExplosionFX.Rendering;

internal sealed class VolumeAssets : IDisposable
{
	internal Material Material;

	internal Material FlareMaterial;

	internal Material DistortMaterial;

	internal Material SmokeFire;

	internal Material SmokeCloud;

	internal Material Scorch;

	internal Material TrailMaterial;

	internal Material TrailGlow;

	internal Material Flame;

	internal Material FireMaterial;

	internal Shader Fragment;

	internal Mesh Cube;

	internal Mesh Quad;

	internal Shader Particle;

	internal Shader Debris;

	private Shader ownedShader;

	private Shader ownedFlare;

	private Shader ownedDistort;

	private Shader ownedSmoke;

	private Shader ownedScorch;

	private Shader ownedTrail;

	private Shader ownedFlame;

	private Shader ownedFire;

	private Texture2D smokeA;

	private Texture2D smokeB;

	private Texture3D noise;

	private Texture2D blueNoise;

	internal string Status = "disabled";

	internal Texture3D Noise => noise;

	private bool InUse
	{
		get
		{
			if (!(Material != null) && !(Particle != null) && !(Debris != null) && !(SmokeFire != null) && !(Fragment != null) && !(Scorch != null) && !(TrailMaterial != null))
			{
				return Flame != null;
			}
			return true;
		}
	}

	internal VolumeAssets(bool volumetric)
	{
		GraphicsDeviceType graphicsDeviceType = SystemInfo.graphicsDeviceType;
		if (graphicsDeviceType != GraphicsDeviceType.Direct3D11 && graphicsDeviceType != GraphicsDeviceType.OpenGLCore && graphicsDeviceType != GraphicsDeviceType.Metal)
		{
			Status = "unsupported API; particles";
			return;
		}
		string text = ((Application.platform == RuntimePlatform.WindowsPlayer) ? "windows" : ((Application.platform == RuntimePlatform.OSXPlayer) ? "mac" : null));
		if (text == null)
		{
			Status = "unsupported platform; particles";
			return;
		}
		string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmoryAvionics/VEFX/Assets/vefx-" + text + ".unity3d");
		if (!File.Exists(path))
		{
			Status = "missing " + text + " bundle; particles";
			return;
		}
		AssetBundle assetBundle = null;
		try
		{
			assetBundle = AssetBundle.LoadFromFile(path);
			if (assetBundle == null)
			{
				Status = "bundle load failed; particles";
				return;
			}
			Particle = Supported(assetBundle, "Assets/Shaders/Particle.shader");
			Debris = Supported(assetBundle, "Assets/Shaders/Debris.shader");
			noise = assetBundle.LoadAsset<Texture3D>("Assets/Noise/VefxNoise3D.asset");
			LoadResidue(assetBundle);
			string text2 = ((SmokeFire != null) ? " + lit smoke" : "") + ((Fragment != null) ? " + part fragments" : "") + ((Scorch != null) ? " + scorch" : "") + ((TrailMaterial != null) ? " + smoke trails" : "") + ((Flame != null) ? " + flames" : "");
			if (!volumetric)
			{
				Status = "volumes disabled; particles" + ((Particle != null) ? " (original sprites)" : "") + text2;
				return;
			}
			Shader shader = assetBundle.LoadAsset<Shader>("Assets/Shaders/HeroVolume.shader");
			if (shader == null || !shader.isSupported)
			{
				Status = "unsupported volume shader; particles" + text2;
				return;
			}
			if (noise == null)
			{
				Status = "noise texture missing; particles" + text2;
				return;
			}
			Material = new Material(shader);
			Material.SetTexture("_NoiseTex", noise);
			blueNoise = assetBundle.LoadAsset<Texture2D>("Assets/Noise/VefxBlueNoise.asset");
			if (blueNoise != null)
			{
				Material.SetTexture("_BlueNoise", blueNoise);
			}
			ownedShader = shader;
			Shader shader2 = Supported(assetBundle, "Assets/Shaders/HeroFlare.shader");
			if (shader2 != null)
			{
				FlareMaterial = new Material(shader2);
				ownedFlare = shader2;
			}
			Shader shader3 = Supported(assetBundle, "Assets/Shaders/HeatDistortion.shader");
			if (shader3 != null)
			{
				DistortMaterial = new Material(shader3);
				ownedDistort = shader3;
			}
			Shader shader4 = Supported(assetBundle, "Assets/Shaders/FireVolume.shader");
			if (shader4 != null)
			{
				ownedFire = shader4;
				FireMaterial = new Material(shader4);
				FireMaterial.SetTexture("_NoiseTex", noise);
				if (blueNoise != null)
				{
					FireMaterial.SetTexture("_BlueNoise", blueNoise);
				}
			}
			if (FlareMaterial != null || DistortMaterial != null)
			{
				Quad = new Mesh();
				Quad.name = "Original camera-facing billboard";
				Quad.vertices = new Vector3[4]
				{
					new Vector3(-1f, -1f, 0f),
					new Vector3(1f, -1f, 0f),
					new Vector3(1f, 1f, 0f),
					new Vector3(-1f, 1f, 0f)
				};
				Quad.triangles = new int[6] { 0, 1, 2, 0, 2, 3 };
				Quad.bounds = new Bounds(Vector3.zero, Vector3.one * 8f);
			}
			Cube = new Mesh();
			Cube.name = "Original volume bounds";
			Cube.vertices = new Vector3[8]
			{
				new Vector3(-1f, -1f, -1f),
				new Vector3(1f, -1f, -1f),
				new Vector3(1f, 1f, -1f),
				new Vector3(-1f, 1f, -1f),
				new Vector3(-1f, -1f, 1f),
				new Vector3(1f, -1f, 1f),
				new Vector3(1f, 1f, 1f),
				new Vector3(-1f, 1f, 1f)
			};
			Cube.triangles = new int[36]
			{
				0, 2, 1, 0, 3, 2, 4, 5, 6, 4,
				6, 7, 0, 4, 7, 0, 7, 3, 1, 2,
				6, 1, 6, 5, 0, 1, 5, 0, 5, 4,
				3, 7, 6, 3, 6, 2
			};
			Cube.RecalculateBounds();
			Status = "shock dome / " + text + " / " + graphicsDeviceType.ToString() + ((FlareMaterial != null) ? " + glare" : " (no glare)") + ((DistortMaterial != null) ? " + refraction" : " (no refraction)") + ((FireMaterial != null) ? " + 3D fires" : "") + ((Particle != null) ? " + original sprites" : "") + text2 + " (runtime visual test required)";
		}
		catch (Exception ex)
		{
			Dispose();
			Material = null;
			Cube = null;
			FlareMaterial = null;
			DistortMaterial = null;
			FireMaterial = null;
			Quad = null;
			Particle = null;
			Debris = null;
			SmokeFire = null;
			SmokeCloud = null;
			Scorch = null;
			TrailMaterial = null;
			TrailGlow = null;
			Flame = null;
			Fragment = null;
			blueNoise = null;
			noise = null;
			smokeA = null;
			smokeB = null;
			Status = "bundle load failed; particles";
			Debug.LogWarning("[VEFX] Volume fallback: " + ex.GetType().Name);
		}
		finally
		{
			if (assetBundle != null)
			{
				assetBundle.Unload(!InUse);
			}
		}
		if (Material == null && Fragment == null && Scorch == null && TrailMaterial == null && Flame == null && noise != null)
		{
			UnityEngine.Object.Destroy(noise);
			noise = null;
		}
	}

	private static Shader Supported(AssetBundle bundle, string asset)
	{
		Shader shader = bundle.LoadAsset<Shader>(asset);
		if (!(shader != null) || !shader.isSupported)
		{
			return null;
		}
		return shader;
	}

	private void LoadResidue(AssetBundle bundle)
	{
		Shader shader = Supported(bundle, "Assets/Shaders/Smoke.shader");
		Texture2D texture2D = bundle.LoadAsset<Texture2D>("Assets/Smoke/VefxSmokeA.asset");
		Texture2D texture2D2 = bundle.LoadAsset<Texture2D>("Assets/Smoke/VefxSmokeB.asset");
		if (shader != null && texture2D != null && texture2D2 != null)
		{
			ownedSmoke = shader;
			smokeA = texture2D;
			smokeB = texture2D2;
			SmokeFire = SmokeMaterial(shader, 1.3f);
			SmokeCloud = SmokeMaterial(shader, 0f);
		}
		if (!(noise == null))
		{
			Shader shader2 = Supported(bundle, "Assets/Shaders/PartFragment.shader");
			if (shader2 != null)
			{
				Fragment = shader2;
			}
			Shader shader3 = Supported(bundle, "Assets/Shaders/Scorch.shader");
			if (shader3 != null)
			{
				ownedScorch = shader3;
				Scorch = new Material(shader3);
				Scorch.SetTexture("_NoiseTex", noise);
			}
			Shader shader4 = Supported(bundle, "Assets/Shaders/Trail.shader");
			if (shader4 != null)
			{
				ownedTrail = shader4;
				TrailMaterial = new Material(shader4);
				TrailMaterial.SetTexture("_NoiseTex", noise);
				TrailGlow = new Material(shader4);
				TrailGlow.SetTexture("_NoiseTex", noise);
				TrailGlow.SetFloat("_Glow", 1f);
			}
			Shader shader5 = Supported(bundle, "Assets/Shaders/Flame.shader");
			if (shader5 != null)
			{
				ownedFlame = shader5;
				Flame = new Material(shader5);
				Flame.SetTexture("_NoiseTex", noise);
			}
		}
	}

	private Material SmokeMaterial(Shader shader, float emission)
	{
		Material material = new Material(shader);
		material.SetTexture("_SmokeA", smokeA);
		material.SetTexture("_SmokeB", smokeB);
		material.SetFloat("_Emission", emission);
		return material;
	}

	public void Dispose()
	{
		if (Material != null)
		{
			UnityEngine.Object.Destroy(Material);
		}
		if (Cube != null)
		{
			UnityEngine.Object.Destroy(Cube);
		}
		if (FlareMaterial != null)
		{
			UnityEngine.Object.Destroy(FlareMaterial);
		}
		if (Quad != null)
		{
			UnityEngine.Object.Destroy(Quad);
		}
		if (DistortMaterial != null)
		{
			UnityEngine.Object.Destroy(DistortMaterial);
		}
		if (ownedDistort != null)
		{
			UnityEngine.Object.Destroy(ownedDistort);
		}
		if (ownedShader != null)
		{
			UnityEngine.Object.Destroy(ownedShader);
		}
		if (ownedFlare != null)
		{
			UnityEngine.Object.Destroy(ownedFlare);
		}
		if (SmokeFire != null)
		{
			UnityEngine.Object.Destroy(SmokeFire);
		}
		if (SmokeCloud != null)
		{
			UnityEngine.Object.Destroy(SmokeCloud);
		}
		if (Scorch != null)
		{
			UnityEngine.Object.Destroy(Scorch);
		}
		if (ownedScorch != null)
		{
			UnityEngine.Object.Destroy(ownedScorch);
		}
		if (TrailMaterial != null)
		{
			UnityEngine.Object.Destroy(TrailMaterial);
		}
		if (TrailGlow != null)
		{
			UnityEngine.Object.Destroy(TrailGlow);
		}
		if (ownedTrail != null)
		{
			UnityEngine.Object.Destroy(ownedTrail);
		}
		if (Flame != null)
		{
			UnityEngine.Object.Destroy(Flame);
		}
		if (ownedFlame != null)
		{
			UnityEngine.Object.Destroy(ownedFlame);
		}
		if (FireMaterial != null)
		{
			UnityEngine.Object.Destroy(FireMaterial);
		}
		if (ownedFire != null)
		{
			UnityEngine.Object.Destroy(ownedFire);
		}
		if (ownedSmoke != null)
		{
			UnityEngine.Object.Destroy(ownedSmoke);
		}
		if (Fragment != null)
		{
			UnityEngine.Object.Destroy(Fragment);
		}
		if (smokeA != null)
		{
			UnityEngine.Object.Destroy(smokeA);
		}
		if (smokeB != null)
		{
			UnityEngine.Object.Destroy(smokeB);
		}
		if (Particle != null)
		{
			UnityEngine.Object.Destroy(Particle);
		}
		if (Debris != null)
		{
			UnityEngine.Object.Destroy(Debris);
		}
		if (noise != null)
		{
			UnityEngine.Object.Destroy(noise);
		}
		if (blueNoise != null)
		{
			UnityEngine.Object.Destroy(blueNoise);
		}
	}
}
