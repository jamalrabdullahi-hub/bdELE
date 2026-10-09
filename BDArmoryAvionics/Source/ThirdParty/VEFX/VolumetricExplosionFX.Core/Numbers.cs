using System;

namespace VolumetricExplosionFX.Core;

public static class Numbers
{
	public static double Positive(double x)
	{
		if (!double.IsNaN(x) && !double.IsInfinity(x) && !(x < 0.0))
		{
			return x;
		}
		return 0.0;
	}

	public static double Clamp(double x, double min, double max)
	{
		return Math.Max(min, Math.Min(max, double.IsNaN(x) ? min : x));
	}
}
