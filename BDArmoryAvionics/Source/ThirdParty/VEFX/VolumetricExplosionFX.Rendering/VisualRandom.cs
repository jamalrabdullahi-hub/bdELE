using System;
using UnityEngine;

namespace VolumetricExplosionFX.Rendering;

internal struct VisualRandom
{
	private uint state;

	internal VisualRandom(uint seed)
	{
		state = ((seed == 0) ? 1u : seed);
	}

	internal float Next()
	{
		state ^= state << 13;
		state ^= state >> 17;
		state ^= state << 5;
		return (float)(state & 0xFFFFFF) / 16777216f;
	}

	internal Vector3 Direction()
	{
		float num = Next() * 2f - 1f;
		float f = Next() * (float)Math.PI * 2f;
		float num2 = Mathf.Sqrt(Mathf.Max(0f, 1f - num * num));
		return new Vector3(num2 * Mathf.Cos(f), num, num2 * Mathf.Sin(f));
	}
}
