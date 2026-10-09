using System;

namespace VolumetricExplosionFX.Core;

public static class Shower
{
	public static ShowerPlan Plan(FxPlan plan, bool surface, double gravity, int budget)
	{
		ShowerPlan s = new ShowerPlan
		{
			Kind = ShowerKind.None,
			Gravity = Math.Max(gravity, 0.0)
		};
		double num = Numbers.Clamp(plan.Atmosphere, 0.0, 1.0);
		double num2 = Numbers.Clamp(plan.FireFraction, 0.0, 1.0);
		double num3 = Violence(plan);
		budget = Math.Max(0, budget);
		if (budget < 4)
		{
			return s;
		}
		double num4 = VolumeEvolution.FireballRadius(plan);
		if (Solid(plan))
		{
			s.Kind = ShowerKind.Solid;
			s.Count = (int)Math.Min(Math.Min(220, budget), 30.0 + plan.BurnableKg / 40.0);
			s.SpeedMin = 25.0 * num3;
			s.SpeedMax = Numbers.Clamp(110.0 * num3, 60.0, 160.0);
			s.LifeMin = 2.0;
			s.LifeMax = 6.5;
			s.HeadSize = 1.4;
			s.TrailWidth = 0.6 + 3.4 * num;
			s.Drag = 0.08 + 0.8 * num;
			s.TrailLife = 3.2;
			s.TrailStep = 2.5;
			s.Clumps = 7;
			s.ClumpShare = 0.55;
		}
		else
		{
			if (!(num2 > 0.3) || !(num4 >= 4.0))
			{
				return s;
			}
			s.Kind = ShowerKind.Liquid;
			s.Count = (int)Math.Min(Math.Min(40, budget), 6.0 + num4 * 1.2);
			s.SpeedMin = 25.0 * num3;
			s.SpeedMax = Numbers.Clamp(110.0 * num3, 50.0, 160.0);
			s.LifeMin = 1.2 * (0.3 + 0.7 * num);
			s.LifeMax = 3.8 * (0.3 + 0.7 * num);
			s.HeadSize = 1.6;
			s.TrailWidth = 0.9 + 4.5 * num;
			s.Drag = 0.05 + 0.35 * num;
			s.TrailLife = 2.6;
			s.TrailStep = 2.0;
			s.Clumps = 4;
			s.ClumpShare = 0.5;
		}
		double upBias;
		if (surface)
		{
			upBias = ((s.Kind == ShowerKind.Liquid) ? 0.6 : 0.85);
		}
		else
		{
			upBias = 0.25;
		}
		s.UpBias = upBias;
		Thin(ref s, num);
		return s;
	}

	public static ShowerPlan Heavy(FxPlan plan, bool surface, double gravity, int budget)
	{
		ShowerPlan s = new ShowerPlan
		{
			Kind = ShowerKind.None,
			Gravity = Math.Max(gravity, 0.0)
		};
		if (!Solid(plan) || plan.BurnableKg < 400.0 || budget < 8)
		{
			return s;
		}
		double num = Numbers.Clamp(plan.Atmosphere, 0.0, 1.0);
		double num2 = Violence(plan);
		s.Kind = ShowerKind.Solid;
		s.Count = (int)Numbers.Clamp(4.0 + plan.BurnableKg / 700.0, 4.0, Math.Min(18, budget / 8));
		s.SpeedMin = 12.0 * num2;
		s.SpeedMax = Numbers.Clamp(45.0 * num2, 25.0, 60.0);
		s.LifeMin = 7.0;
		s.LifeMax = 13.0;
		s.HeadSize = 2.6;
		s.TrailWidth = 1.2 + 4.8 * num;
		s.Drag = 0.02 + 0.1 * num;
		s.UpBias = (surface ? 0.92 : 0.3);
		s.TrailLife = 2.2;
		s.TrailStep = 2.0;
		Thin(ref s, num);
		return s;
	}

	public static ShowerPlan Dirt(FxPlan plan, double gravity, int budget, Vec3 velocity)
	{
		ShowerPlan result = new ShowerPlan
		{
			Kind = ShowerKind.None,
			Gravity = Math.Max(gravity, 0.0)
		};
		if (plan.Kind != EventKind.GroundImpact || budget < 8 || (plan.ImpactSpeed < 20.0 && plan.VisualEnergyScore < 3.5))
		{
			return result;
		}
		double num = Numbers.Clamp(plan.Atmosphere, 0.0, 1.0);
		double num2 = Numbers.Clamp((plan.VisualEnergyScore - 2.0) / 4.0, 0.2, 1.2);
		result.Kind = ShowerKind.Dirt;
		result.Count = (int)Numbers.Clamp(8.0 + 12.0 * num2 + plan.ImpactSpeed / 15.0, 8.0, Math.Min(40, budget / 3));
		result.SpeedMin = 10.0 + 0.04 * plan.ImpactSpeed;
		result.SpeedMax = Numbers.Clamp(20.0 + 0.15 * plan.ImpactSpeed + 10.0 * num2, 25.0, 70.0);
		result.LifeMin = 1.0;
		result.LifeMax = 2.5 + 1.5 * num2;
		result.HeadSize = 0.05;
		result.TrailWidth = (1.1 + 2.0 * num2) * ((num > 0.02) ? 1.0 : 0.6);
		result.Drag = 0.08 + 0.35 * num;
		result.Trails = true;
		result.TrailLife = 1.6;
		result.TrailStep = 1.5;
		Vec3 vec = new Vec3(velocity.X, 0.0, velocity.Z);
		double num3 = vec.Length / (vec.Length + Math.Abs(velocity.Y) + 1E-06);
		result.Axis = (new Vec3(0.0, 1.0, 0.0) + vec.Normalized(default) * (0.8 * num3)).Normalized(new Vec3(0.0, 1.0, 0.0));
		result.Cone = 1.1;
		result.MinUp = 0.3;
		result.Clumps = 5;
		result.ClumpShare = 0.6;
		return result;
	}

	private static void Thin(ref ShowerPlan s, double air)
	{
		s.Trails = air > 0.15;
		double num = Numbers.Clamp((air - 0.1) / 0.5, 0.25, 1.0);
		s.TrailLife *= num;
		s.TrailWidth *= 0.5 + 0.5 * num;
	}

	public static bool Solid(FxPlan plan)
	{
		if (plan.PartKind == PartKind.SolidBooster)
		{
			return plan.BurnableKg > 20.0;
		}
		return false;
	}

	private static double Violence(FxPlan plan)
	{
		return Numbers.Clamp(plan.VisualEnergyScore / 6.0, 0.3, 1.4);
	}

	public static int Runaways(FxPlan plan, bool surface)
	{
		if (surface || !Solid(plan) || plan.BurnableKg < 800.0 || Numbers.Clamp(plan.Atmosphere, 0.0, 1.0) < 0.2)
		{
			return 0;
		}
		if (!(plan.BurnableKg > 5000.0))
		{
			return 1;
		}
		return 2;
	}
}
