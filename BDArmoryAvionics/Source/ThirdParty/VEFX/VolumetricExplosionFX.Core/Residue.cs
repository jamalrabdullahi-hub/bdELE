using System;

namespace VolumetricExplosionFX.Core;

public static class Residue
{
	public static ResiduePlan Plan(FxPlan plan, bool ground, bool water, double maxSeconds)
	{
		ResiduePlan result = default;
		maxSeconds = Numbers.Clamp(maxSeconds, 0.0, 180.0);
		if (maxSeconds <= 0.0 || !(ground | water))
		{
			return result;
		}
		double num = Numbers.Clamp(plan.Atmosphere, 0.0, 1.0);
		double num2 = Numbers.Clamp(plan.FireFraction, 0.0, 1.0);
		double num3 = Math.Max(VolumeEvolution.FireballRadius(plan), 0.0);
		double num4 = Numbers.Clamp(plan.BurnableKg, 0.0, 10000000.0);
		if (num > 0.1 && num2 > 0.08 && num4 > 5.0)
		{
			double x = (4.0 + 9.0 * Math.Log10(1.0 + num4 / 200.0)) * (water ? 0.5 : 1.0) * (0.4 + 0.6 * num);
			result.FireSeconds = Numbers.Clamp(x, 3.0, maxSeconds);
			result.FireRadius = Numbers.Clamp(0.35 * num3, 1.2, 25.0);
			result.SmokeSeconds = Math.Min(result.FireSeconds + 8.0, maxSeconds + 8.0);
			double num5 = 2.0 * result.FireRadius;
			double x2 = Math.PI * 1700.0 * result.FireRadius * result.FireRadius * num2 * (water ? 0.5 : 1.0);
			result.FlameHeight = Numbers.Clamp(0.235 * Math.Pow(x2, 0.4) - 1.02 * num5, 0.8 * num5, 45.0);
			result.PuffHz = Numbers.Clamp(1.5 / Math.Sqrt(Math.Max(num5, 0.5)), 0.2, 2.0);
			result.PlumeSpeed = Numbers.Clamp(2.5 + 0.35 * result.FireRadius, 2.5, 10.0);
			result.FlameRate = Numbers.Clamp(8.0 + 3.0 * result.FireRadius * num2, 6.0, 40.0);
			result.SmokeRate = Numbers.Clamp(4.0 + 1.6 * result.FireRadius, 4.0, 28.0);
		}
		bool flag = num2 > 0.15 && num4 > 20.0;
		bool flag2 = plan.ImpactSpeed > 60.0 && plan.VisualEnergyScore > 3.5;
		if (ground && (flag | flag2))
		{
			double x3 = Math.Max(0.7 * num3, 0.6 * plan.Radius);
			result.ScorchRadius = Numbers.Clamp(x3, 2.0, 45.0);
			result.ScorchDarkness = (flag ? Numbers.Clamp(0.2 + 0.45 * num2, 0.2, 0.65) : 0.3);
			if (num < 0.1)
			{
				result.ScorchDarkness = Math.Min(result.ScorchDarkness, 0.35);
			}
			result.ScorchSeconds = maxSeconds;
		}
		return result;
	}

	public static double FireLevel(ResiduePlan r, double age)
	{
		if (!r.HasFire || age < 0.0 || age >= r.FireSeconds)
		{
			return 0.0;
		}
		double num = age / r.FireSeconds;
		double num2 = Math.Min(1.0, age / 1.8);
		return num2 * num2 * (3.0 - 2.0 * num2) * Math.Sqrt(1.0 - num);
	}

	public static double SmokeStart(FxPlan plan)
	{
		return Math.Max(1.2, 0.7 * VolumeEvolution.FireballDuration(plan));
	}

	public static double ScorchLevel(ResiduePlan r, double age)
	{
		if (r.ScorchRadius <= 0.0 || age < 0.0 || age >= r.ScorchSeconds)
		{
			return 0.0;
		}
		double num = Math.Min(1.0, age / 0.8);
		double num2 = Math.Min(1.0, (r.ScorchSeconds - age) / Math.Min(6.0, r.ScorchSeconds * 0.3 + 0.001));
		return num * num2 * r.ScorchDarkness;
	}
}
