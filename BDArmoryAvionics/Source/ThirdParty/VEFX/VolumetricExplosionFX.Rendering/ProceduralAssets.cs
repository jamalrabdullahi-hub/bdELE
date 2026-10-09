using System;
using System.Collections.Generic;
using UnityEngine;

namespace VolumetricExplosionFX.Rendering;

internal sealed class ProceduralAssets : IDisposable
{
	internal readonly Material Glow;

	internal readonly Material Cloud;

	internal readonly Material Debris;

	internal readonly Material Trail;

	internal readonly Mesh Shard;

	internal readonly string ShaderSummary;

	private readonly Texture2D glow;

	private readonly Texture2D cloud;

	private readonly Texture2D trail;

	internal readonly bool HeatDebris;

	internal ProceduralAssets(Shader original, Shader debris)
	{
		Shader shader = ((original != null) ? original : SupportedShader("glow", new string[3] { "Particles/Standard Unlit", "Legacy Shaders/Particles/Additive", "Particles/Additive" }));
		Shader shader2 = ((original != null) ? original : SupportedShader("cloud", new string[3] { "Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended", "Particles/Standard Unlit" }));
		Shader shader3 = debris;
		if (shader3 == null)
		{
			string[] array = new string[3] { "KSP/Diffuse", "Legacy Shaders/Diffuse", "Diffuse" };
			for (int i = 0; i < array.Length; i++)
			{
				Shader shader4 = Shader.Find(array[i]);
				if (shader4 != null && shader4.isSupported)
				{
					shader3 = shader4;
					break;
				}
			}
		}
		HeatDebris = debris != null;
		ShaderSummary = "API=" + SystemInfo.graphicsDeviceType.ToString() + " | glow=" + shader.name + " | cloud=" + shader2.name + " | debris=" + ((shader3 != null) ? shader3.name : "glow fallback");
		glow = Texture(turbulent: false);
		cloud = Texture(turbulent: true);
		Glow = new Material(shader);
		Glow.mainTexture = glow;
		Configure(Glow, additive: true);
		Cloud = new Material(shader2);
		Cloud.mainTexture = cloud;
		Configure(Cloud, additive: false);
		if (original != null)
		{
			Glow.SetFloat("_SrcBlend", 5f);
			Glow.SetFloat("_DstBlend", 1f);
			Cloud.SetFloat("_SrcBlend", 5f);
			Cloud.SetFloat("_DstBlend", 10f);
			Glow.renderQueue = 3001;
			Cloud.renderQueue = 3000;
		}
		trail = TrailTexture();
		Trail = new Material(shader2);
		Trail.mainTexture = trail;
		Trail.mainTextureScale = new Vector2(0.4f, 1f);
		Configure(Trail, additive: false);
		if (original != null)
		{
			Trail.SetFloat("_SrcBlend", 5f);
			Trail.SetFloat("_DstBlend", 10f);
			Trail.renderQueue = 3000;
		}
		if (shader3 != null)
		{
			Debris = new Material(shader3);
			if (!HeatDebris)
			{
				Debris.color = new Color(0.62f, 0.62f, 0.6f, 1f);
			}
		}
		else
		{
			Debris = Glow;
		}
		Shard = new Mesh();
		Shard.name = "Original procedural visual shard";
		Vector3[] array2 = new Vector3[7]
		{
			new Vector3(-0.5f, 0f, -0.18f),
			new Vector3(-0.12f, 0.06f, -0.34f),
			new Vector3(0.38f, 0.02f, -0.22f),
			new Vector3(0.52f, -0.05f, 0.12f),
			new Vector3(0.1f, 0.08f, 0.3f),
			new Vector3(-0.36f, 0.03f, 0.2f),
			new Vector3(0f, 0.1f, 0f)
		};
		Vector3[] array3 = new Vector3[array2.Length * 2];
		for (int j = 0; j < array2.Length; j++)
		{
			array3[j] = array2[j];
			array3[j + array2.Length] = array2[j] - new Vector3(0f, 0.035f, 0f);
		}
		List<int> list = new List<int>();
		for (int k = 0; k < 6; k++)
		{
			int num = k;
			int num2 = (k + 1) % 6;
			list.AddRange(new int[3] { 6, num2, num });
			list.AddRange(new int[3]
			{
				13,
				num + 7,
				num2 + 7
			});
		}
		for (int l = 0; l < 6; l++)
		{
			int num3 = l;
			int num4 = (l + 1) % 6;
			list.AddRange(new int[12]
			{
				num3,
				num4,
				num4 + 7,
				num3,
				num4 + 7,
				num3 + 7,
				num4,
				num3,
				num4 + 7,
				num4 + 7,
				num3,
				num3 + 7
			});
		}
		Shard.vertices = array3;
		Shard.triangles = list.ToArray();
		Shard.RecalculateNormals();
		Shard.RecalculateBounds();
	}

	private static Shader SupportedShader(string role, string[] candidates)
	{
		for (int i = 0; i < candidates.Length; i++)
		{
			Shader shader = Shader.Find(candidates[i]);
			if (shader != null && shader.isSupported)
			{
				return shader;
			}
			Debug.LogWarning("[VEFX] " + role + " shader " + candidates[i] + ": " + ((shader == null) ? "missing" : "unsupported") + " on " + SystemInfo.graphicsDeviceType.ToString() + "; trying fallback.");
		}
		throw new InvalidOperationException("No supported " + role + " particle shader on " + SystemInfo.graphicsDeviceType.ToString() + ".");
	}

	private static void Configure(Material material, bool additive)
	{
		if (material.HasProperty("_TintColor"))
		{
			material.SetColor("_TintColor", Color.white);
		}
		if (material.HasProperty("_Color"))
		{
			material.SetColor("_Color", Color.white);
		}
		if (material.HasProperty("_Mode"))
		{
			material.SetFloat("_Mode", additive ? 4 : 2);
			material.SetInt("_SrcBlend", 5);
			material.SetInt("_DstBlend", additive ? 1 : 10);
			material.SetInt("_ZWrite", 0);
			material.DisableKeyword("_ALPHATEST_ON");
			material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
			material.EnableKeyword("_ALPHABLEND_ON");
			material.renderQueue = 3000;
		}
	}

	private static Texture2D Texture(bool turbulent)
	{
		Texture2D texture2D = new Texture2D(64, 64, TextureFormat.RGBA32, mipChain: false);
		texture2D.name = (turbulent ? "Original layered noise puff" : "Original soft spark");
		texture2D.wrapMode = TextureWrapMode.Clamp;
		Color[] array = new Color[4096];
		for (int i = 0; i < 64; i++)
		{
			for (int j = 0; j < 64; j++)
			{
				float num = ((float)j + 0.5f) / 64f * 2f - 1f;
				float num2 = ((float)i + 0.5f) / 64f * 2f - 1f;
				float num3 = Mathf.Clamp01(1f - num * num - num2 * num2);
				float num4 = (turbulent ? (0.45f + 0.35f * Mathf.PerlinNoise(num * 3.7f + 12f, num2 * 3.7f + 7f) + 0.2f * Mathf.PerlinNoise(num * 11f + 2f, num2 * 11f + 19f)) : 1f);
				float num5 = num3 * num3 * num4;
				if (!turbulent)
				{
					num5 = 0.5f * Mathf.Exp((0f - (num * num + num2 * num2)) * 18f) * (float)((num3 > 0f) ? 1 : 0) + 0.5f * num3 * num3;
				}
				array[i * 64 + j] = (turbulent ? new Color(num4, num4, num4, num5) : new Color(num5, num5, num5, num5));
			}
		}
		texture2D.SetPixels(array);
		texture2D.Apply(updateMipmaps: false, makeNoLongerReadable: true);
		return texture2D;
	}

	private static Texture2D TrailTexture()
	{
		Texture2D texture2D = new Texture2D(64, 32, TextureFormat.RGBA32, mipChain: false);
		texture2D.name = "Original smoke trail band";
		texture2D.wrapMode = TextureWrapMode.Repeat;
		Color[] array = new Color[2048];
		for (int i = 0; i < 32; i++)
		{
			for (int j = 0; j < 64; j++)
			{
				float num = ((float)j + 0.5f) / 64f;
				float num2 = ((float)i + 0.5f) / 32f * 2f - 1f;
				float num3 = Mathf.Cos(num * (float)Math.PI * 2f) * 1.6f;
				float num4 = Mathf.Sin(num * (float)Math.PI * 2f) * 1.6f;
				float num5 = 0.55f + 0.45f * Mathf.PerlinNoise(num3 + 5f + num2 * 0.8f, num4 + 9f);
				float num6 = Mathf.Clamp01(1f - num2 * num2);
				num6 *= num6;
				array[i * 64 + j] = new Color(num5, num5, num5, num6 * num5);
			}
		}
		texture2D.SetPixels(array);
		texture2D.Apply(updateMipmaps: false, makeNoLongerReadable: true);
		return texture2D;
	}

	public void Dispose()
	{
		if (Debris != Glow)
		{
			UnityEngine.Object.Destroy(Debris);
		}
		UnityEngine.Object.Destroy(Glow);
		UnityEngine.Object.Destroy(Cloud);
		UnityEngine.Object.Destroy(Trail);
		UnityEngine.Object.Destroy(glow);
		UnityEngine.Object.Destroy(cloud);
		UnityEngine.Object.Destroy(trail);
		UnityEngine.Object.Destroy(Shard);
	}
}
