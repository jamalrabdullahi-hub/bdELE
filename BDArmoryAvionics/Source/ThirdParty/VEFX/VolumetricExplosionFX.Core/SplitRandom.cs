using System;

namespace VolumetricExplosionFX.Core;

public struct SplitRandom
{
	private ulong state;

	public SplitRandom(uint seed)
	{
		state = unchecked((ulong)(-7046029254386353131L ^ (seed * -3335678366873096957L)));   // (ELE: decompiled primary-constructor initialiser moved here)
		if (state == 0L)
		{
			state = 1uL;
		}
	}

	public double Next()
	{
		state ^= state << 13;
		state ^= state >> 7;
		state ^= state << 17;
		return (double)(state >> 11) * 1.1102230246251565E-16;
	}

	public double Range(double a, double b)
	{
		return a + (b - a) * Next();
	}

	public Vec3 Direction()
	{
		double num = Next() * 2.0 - 1.0;
		double num2 = Next() * Math.PI * 2.0;
		double num3 = Math.Sqrt(Math.Max(0.0, 1.0 - num * num));
		return new Vec3(num3 * Math.Cos(num2), num, num3 * Math.Sin(num2));
	}
}
