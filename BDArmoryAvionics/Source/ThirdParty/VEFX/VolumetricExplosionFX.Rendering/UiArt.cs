using System;
using System.Collections.Generic;
using UnityEngine;

namespace VolumetricExplosionFX.Rendering;

internal static class UiArt
{
	private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();

	private static readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

	internal static void Release()
	{
		foreach (UnityEngine.Object item in owned)
		{
			if (item != null)
			{
				UnityEngine.Object.Destroy(item);
			}
		}
		owned.Clear();
		sprites.Clear();
	}

	private static Sprite Make(string key, int size, float border, Func<float, float, Color> pixel)
	{
		if (sprites.TryGetValue(key, out var value) && value != null)
		{
			return value;
		}
		Texture2D texture2D = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false);
		texture2D.name = "Original settings art " + key;
		texture2D.wrapMode = TextureWrapMode.Clamp;
		texture2D.filterMode = FilterMode.Bilinear;
		Color[] array = new Color[size * size];
		for (int i = 0; i < size; i++)
		{
			for (int j = 0; j < size; j++)
			{
				array[i * size + j] = pixel((float)j + 0.5f, (float)i + 0.5f);
			}
		}
		texture2D.SetPixels(array);
		texture2D.Apply(updateMipmaps: false, makeNoLongerReadable: true);
		value = Sprite.Create(texture2D, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
		owned.Add(texture2D);
		owned.Add(value);
		sprites[key] = value;
		return value;
	}

	internal static Sprite Fill(int radius)
	{
		int n = 2 * radius + 4;
		return Make("fill" + radius, n, radius + 2, (float x, float y) =>
		{
			float a = Mathf.Abs(x - (float)n * 0.5f) - ((float)n * 0.5f - (float)radius);
			float num = Mathf.Abs(y - (float)n * 0.5f) - ((float)n * 0.5f - (float)radius);
			float num2 = Mathf.Max(a, 0f);
			float num3 = Mathf.Max(num, 0f);
			float num4 = Mathf.Sqrt(num2 * num2 + num3 * num3) + Mathf.Min(Mathf.Max(a, num), 0f) - (float)radius;
			return new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - num4));
		});
	}

	internal static Sprite Check()
	{
		return Make("check", 24, 0f, (float x, float y) =>
		{
			Vector2 p = new Vector2(x, y);
			float num = Mathf.Min(Segment(p, new Vector2(4f, 12f), new Vector2(10f, 6f)), Segment(p, new Vector2(10f, 6f), new Vector2(20f, 19f)));
			return new Color(1f, 1f, 1f, Mathf.Clamp01(2f - num));
		});
	}

	private static float Segment(Vector2 p, Vector2 a, Vector2 b)
	{
		Vector2 vector = b - a;
		return (p - a - vector * Mathf.Clamp01(Vector2.Dot(p - a, vector) / vector.sqrMagnitude)).magnitude;
	}

	private static bool Flame(float x, float y)
	{
		if (x * x + (y + 0.32f) * (y + 0.32f) < 0.36f)
		{
			return true;
		}
		if (y < -0.32f || y > 0.95f)
		{
			return false;
		}
		float num = (y + 0.32f) / 1.27f;
		float num2 = 0.6f * Mathf.Pow(1f - num, 1.15f);
		float num3 = 0.2f * Mathf.Sin(num * 3.4f) * num;
		return Mathf.Abs(x - num3) < num2;
	}

	internal static Texture2D Icon(int size)
	{
		Texture2D texture2D = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false);
		texture2D.name = "Original VolumetricExplosionFX icon";
		texture2D.wrapMode = TextureWrapMode.Clamp;
		Color[] array = new Color[size * size];
		for (int i = 0; i < size; i++)
		{
			for (int j = 0; j < size; j++)
			{
				int num = 0;
				int num2 = 0;
				for (int k = 0; k < 4; k++)
				{
					for (int l = 0; l < 4; l++)
					{
						float num3 = ((float)j + ((float)l + 0.5f) / 4f) / (float)size * 2.3f - 1.15f;
						float num4 = ((float)i + ((float)k + 0.5f) / 4f) / (float)size * 2.3f - 1.15f;
						if (Flame(num3, num4))
						{
							num++;
						}
						if (Flame(num3 / 0.5f, (num4 + 0.4f) / 0.5f))
						{
							num2++;
						}
					}
				}
				Color color = Color.Lerp(Color.Lerp(new Color(0.95f, 0.25f, 0.05f), new Color(1f, 0.78f, 0.25f), (float)i / (float)size), new Color(1f, 0.97f, 0.85f), (float)num2 / 16f);
				color.a = (float)num / 16f;
				array[i * size + j] = color;
			}
		}
		texture2D.SetPixels(array);
		texture2D.Apply(updateMipmaps: false, makeNoLongerReadable: false);
		return texture2D;
	}
}
