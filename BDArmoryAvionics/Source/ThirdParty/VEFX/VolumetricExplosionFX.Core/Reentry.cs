using System;

namespace VolumetricExplosionFX.Core;

public static class Reentry
{
	public const double MinSpeed = 900.0;

	public static bool Applies(FxPlan plan, bool surface, double airSpeed)
	{
		double num = Numbers.Clamp(plan.Atmosphere, 0.0, 1.0);
		if (!surface && plan.Kind == EventKind.Destruction && airSpeed >= 900.0 && num > 0.0002)
		{
			return num < 0.45;
		}
		return false;
	}

	public static ReentryPlan Plan(FxPlan plan, bool surface, double airSpeed, int budget)
	{
		ReentryPlan result = default;
		if (!Applies(plan, surface, airSpeed) || budget < 8)
		{
			return result;
		}
		double num = Numbers.Clamp(plan.Atmosphere, 0.0, 1.0);
		double num2 = Numbers.Clamp(Math.Pow(airSpeed / 2500.0, 3.0), 0.1, 2.5);
		double num3 = Numbers.Clamp(plan.Radius, 0.5, 30.0);
		result.Active = true;
		result.Count = (int)Math.Min(Numbers.Clamp(14.0 + plan.VisualEnergyScore * 5.0 + 18.0 * Math.Min(num2, 1.5), 12.0, 96.0), budget);
		double num4 = 0.02 + 1.6 * num;
		result.DragMin = num4 * 0.3;
		result.DragMax = num4 * 3.5;
		result.Spread = Numbers.Clamp(25.0 + 0.02 * airSpeed, 25.0, 120.0);
		result.HeadSize = Numbers.Clamp(0.8 + 0.4 * num3, 1.0, 6.0) * (0.8 + 0.4 * Math.Min(num2, 1.0));
		result.TrailWidth = Numbers.Clamp(0.5 + 0.25 * num3, 0.6, 4.0);
		result.GlowSeconds = Numbers.Clamp(2.5 + 3.0 * num2, 2.5, 9.0);
		result.FlareShare = 0.18;
		result.FlashSize = Numbers.Clamp(6.0 * num3, 4.0, 120.0);
		result.FlashSeconds = Numbers.Clamp(0.25 + 0.1 * num3, 0.25, 1.2);
		result.WakeLength = Numbers.Clamp(airSpeed * 0.12, 60.0, 600.0);
		result.SmokeTrails = ((num > 0.05) ? ((int)Numbers.Clamp((double)result.Count * Numbers.Clamp((num - 0.05) * 4.0, 0.0, 0.5), 0.0, 24.0)) : 0);
		return result;
	}

	public static double GasTime(double air, double fireballRadius)
	{
		air = Numbers.Clamp(air, 0.0, 1.0);
		if (air < 0.002)
		{
			return double.PositiveInfinity;
		}
		return Numbers.Clamp((0.45 + 0.035 * Math.Max(fireballRadius, 0.0)) / Math.Sqrt(air), 0.35, 60.0);
	}
}
