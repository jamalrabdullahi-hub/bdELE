using System;

namespace VolumetricExplosionFX.Core;

public static class Splash
{
	public static double ColumnHeight(FxPlan plan)
	{
		if (plan.Kind != EventKind.WaterImpact)
		{
			return 0.0;
		}
		return Numbers.Clamp(3.0 * Math.Sqrt(Math.Max(plan.ImpactSpeed, 0.0)) * Math.Sqrt(Math.Max(plan.Radius, 1.0) / 3.0), 6.0, 90.0);
	}
}
