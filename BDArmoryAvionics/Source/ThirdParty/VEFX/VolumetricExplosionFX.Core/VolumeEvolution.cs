using System;

namespace VolumetricExplosionFX.Core;

public static class VolumeEvolution
{
	public const double ShellLimit = 0.9;

	private static readonly Vec3 Up = new Vec3(0.0, 1.0, 0.0);

	private const double Participating = 0.3;

	public const double LiftFactor = 0.55;

	private static double Smooth(double a, double b, double x)
	{
		double num = Numbers.Clamp((x - a) / (b - a), 0.0, 1.0);
		return num * num * (3.0 - 2.0 * num);
	}

	public static double SpreadMax(double air)
	{
		return 1.0 + 0.6 * air + 0.5 * (1.0 - air);
	}

	public static double Spread(double air, double tf, double age)
	{
		return 1.0 + (SpreadMax(air) - 1.0) * Smooth(0.5 * tf, 2.2 * tf, age);
	}

	public static double FireballRadius(FxPlan plan)
	{
		double num = 0.3 * Numbers.Clamp(plan.BurnableKg, 0.0, 10000000.0);
		if (!(num < 1.0))
		{
			return Numbers.Clamp(2.9 * Math.Pow(num, 1.0 / 3.0), 1.0, 60.0);
		}
		return 0.0;
	}

	public static double FireballDuration(FxPlan plan)
	{
		double num = 0.3 * Numbers.Clamp(plan.BurnableKg, 0.0, 10000000.0);
		return Numbers.Clamp((num < 30000.0) ? (0.45 * Math.Pow(num, 1.0 / 3.0)) : (2.6 * Math.Pow(num, 1.0 / 6.0)), 0.8, 9.0);
	}

	public static double DomeRadius(FxPlan plan)
	{
		return Numbers.Clamp(Math.Max(plan.Radius * 2.4, FireballRadius(plan) * 1.2), 1.2, 80.0);
	}

	public static double BoxRadius(FxPlan plan, BlastLayout layout)
	{
		double num = DomeRadius(plan) / 0.9;
		if (layout != null && layout.RadiusMeters > 0.0)
		{
			num = Math.Max(num, layout.ExtentMeters / 0.97);
		}
		return num * 1.25;
	}

	public static double TimeScale(FxPlan plan)
	{
		return Numbers.Clamp(0.1 + 0.013 * DomeRadius(plan), 0.12, 0.9);
	}

	public static double VolumeDuration(FxPlan plan)
	{
		double val = Numbers.Clamp(TimeScale(plan) * (5.0 + 5.0 * Numbers.Clamp(plan.Atmosphere, 0.0, 1.0)), 1.2, 7.0);
		double val2 = ((FireballRadius(plan) > 0.0) ? (FireballDuration(plan) * 1.15 * (1.1 + 1.0 * Numbers.Clamp(plan.Atmosphere, 0.0, 1.0)) + ((plan.VisualEnergyScore > 3.5) ? 0.8 : 0.0)) : 0.0);
		return Numbers.Clamp(Math.Max(val, val2), 1.2, 14.0);
	}

	public static VolumeState Evaluate(FxPlan plan, double age, bool explosions, bool burst, bool ground = false, bool water = false)
	{
		return Evaluate(plan, age, explosions, burst, ground, water, BlastLayout.Default(plan, ground && !water, water));
	}

	public static VolumeState Evaluate(FxPlan plan, double age, bool explosions, bool burst, bool ground, bool water, BlastLayout layout)
	{
		age = Numbers.Clamp(age, 0.0, 60.0);
		double num = Numbers.Clamp(plan.Atmosphere, 0.0, 1.0);
		ground = ground && !water;
		double num2 = (explosions ? Numbers.Clamp(plan.FireFraction, 0.0, 1.0) : 0.0);
		double num3 = (burst ? Numbers.Clamp(plan.VaporFraction, 0.0, 1.0) : 0.0);
		double num4 = ((ground | water) ? (Smooth(300.0, 2500.0, plan.ImpactSpeed) * 0.9) : 0.0);
		double val = (ground ? Smooth(1.5, 5.0, plan.VisualEnergyScore) : 0.0);
		double val2 = (water ? Smooth(1.5, 5.0, plan.VisualEnergyScore) : 0.0);
		double num5 = Math.Max(Math.Max(num2, num4), Math.Max(Math.Max(val, val2), num3));
		double num6 = TimeScale(plan);
		double num7 = BoxRadius(plan, layout);
		double num8 = DomeRadius(plan) / num7;
		double num9 = num8 / 0.9;
		VolumeState s = new VolumeState
		{
			BoxRadius = num7,
			Age = age,
			Atmosphere = num,
			Ground = ground,
			Water = water,
			Scale = num9,
			SmokeR = plan.SmokeR,
			SmokeG = plan.SmokeG,
			SmokeB = plan.SmokeB,
			Roughness = (layout?.Roughness ?? 0.6),
			WhiteFlame = Numbers.Clamp(plan.WhiteFlame, 0.0, 1.0),
			DustR = plan.DustR,
			DustG = plan.DustG,
			DustB = plan.DustB
		};
		double num10 = 1.0 - Math.Exp((0.0 - age) / (0.35 * num6));
		double num11 = Math.Min(1.0, age / (num6 * 0.6));
		s.ShellRadius = Math.Max(0.03, num8 * (num * num10 + (1.0 - num) * num11));
		s.ShellThickness = (0.05 + 0.05 * Smooth(0.0, 4.0 * num6, age)) * num9;
		double num12 = (num * num5 * ((ground | water) ? 1.0 : 0.7) + (1.0 - num) * 0.1 * Math.Max(Math.Max(num2, num4), num3)) * (0.35 + 0.65 * num5);
		num12 *= 1.0 - 0.3 * num2;
		num12 *= (water ? 0.2 : (1.0 - 0.45 * num));
		double num13 = num6 * (0.5 + 0.1 * num);
		s.ShellOpacity = Numbers.Clamp(num12 * Smooth(0.0, 0.03, age) * Math.Exp((0.0 - age) / num13), 0.0, 1.0);
		s.ShellErosion = 0.85 * Smooth(num6 * 0.3, num6 * (1.6 + 1.0 * (1.0 - num)), age);
		double num14 = Math.Max(num2, num4);
		s.Heat = Numbers.Clamp(num14 * Math.Exp((0.0 - age) / (0.18 + 0.22 * num + 0.004 * DomeRadius(plan))), 0.0, 1.0);
		s.CoreRadius = (0.07 + 0.2 * (1.0 - Math.Exp((0.0 - age) / 0.5))) * num9;
		s.Glow = Numbers.Clamp(num14 * Math.Exp((0.0 - age) / 0.06), 0.0, 1.0);
		double x = num3 * ((num > 0.05) ? Smooth(0.0, 0.3, age) : Math.Exp((0.0 - age) * 3.0));
		s.Cloud = Numbers.Clamp(x, 0.0, 1.0);
		s.Vapor = ((s.Cloud > 0.0) ? 1 : 0);
		s.Rise = Math.Min(age * 0.05, 0.3) * num * num9;
		s.Dust = Numbers.Clamp(Math.Max(val, val2) * (0.3 + 0.7 * num) * Smooth(0.0, 0.4, age) * Math.Exp((0.0 - age) / (num6 * 5.0)), 0.0, 1.0);
		double num15 = layout?.RadiusMeters ?? FireballRadius(plan);
		double tf = FireballDuration(plan) * (layout?.Tempo ?? 1.0);
		if (num15 > 0.0 && num2 > 0.02 && layout != null)
		{
			EvaluateFireball(ref s, plan, layout, age, num15, tf, num, num2, ground | water, num7);
		}
		s.ShockRadius = DomeRadius(plan) * (0.12 + 2.6 * (1.0 - Math.Exp((0.0 - age) / (0.4 * num6)))) / num7;
		s.ShockStrength = Numbers.Clamp(num * num5 * Smooth(0.0, 0.02, age) * Math.Exp((0.0 - age) / (1.2 * num6)), 0.0, 1.0);
		s.Haze = Numbers.Clamp(num * Math.Max(num2, num4 * 0.5) * Smooth(0.03, 0.25, age) * Math.Exp((0.0 - age) / (1.5 + 4.0 * num6)), 0.0, 1.0);
		s.HazeHeight = Math.Max((0.6 + 0.9 * Smooth(0.0, 2.0, age)) * num9, s.FireRise + s.FireRadius * 1.6);
		double num16 = VolumeDuration(plan);
		s.Fade = Numbers.Clamp((num16 - age) / (0.3 * num16), 0.0, 1.0);
		return s;
	}

	private static void EvaluateFireball(ref VolumeState s, FxPlan plan, BlastLayout layout, double age, double R, double tf, double air, double fire, bool surface, double box)
	{
		double num = tf * (0.12 + 0.88 * air) * (1.0 - 0.55 * s.WhiteFlame);
		double num2 = air * Smooth(0.15 * tf, 1.2 * tf, age);
		double num3 = R * 0.55 * num2;
		double num4 = 0.0;
		double num5 = 0.0;
		double num6 = 1000000000.0;
		double num7 = 1000000000.0;
		double num8 = 1000000000.0;
		double num9 = -1000000000.0;
		double num10 = -1000000000.0;
		double num11 = -1000000000.0;
		double num12 = air * (1.0 - Math.Exp((0.0 - age) / (0.1 * tf))) + (1.0 - air) * Math.Min(1.0, age / (0.12 * tf));
		double num13 = Spread(air, tf, age);
		for (int i = 0; i < 4; i++)
		{
			LobeSeed lobeSeed = layout.Lobes[i];
			double num14 = age - lobeSeed.Delay;
			if (!lobeSeed.Active || num14 < 0.0)
			{
				s.SetLobe(i, new LobeState
				{
					Axis = Up,
					Stretch = 1.0
				});
				continue;
			}
			double num15 = air * (1.0 - Math.Exp((0.0 - num14) / (0.1 * tf))) + (1.0 - air) * Math.Min(1.0, num14 / (0.12 * tf));
			double num16 = Spread(air, tf, num14);
			double num17 = lobeSeed.Size * R * num15 * num16;
			Vec3 vec = lobeSeed.Drift * (lobeSeed.Tau * (1.0 - Math.Exp((0.0 - num14) / Math.Max(lobeSeed.Tau, 0.001))));
			double num18 = (surface ? (0.45 * num17) : 0.0);
			Vec3 vec2 = (lobeSeed.Relative ? (lobeSeed.Offset * (num12 * num13)) : lobeSeed.Offset) + vec + Up * (num3 + num18);
			double num19 = 1.0 + (lobeSeed.Stretch - 1.0) * Math.Exp((0.0 - num14) / (0.8 * tf));
			if (i == 0 && Math.Abs(lobeSeed.Axis.Y) > 0.7)
			{
				num19 *= 1.0 - 0.3 * num2;
			}
			double num20 = lobeSeed.Heat * layout.HeatBias * fire * (1.0 - Smooth(0.25 * num, 1.1 * num, num14));
			if (lobeSeed.Delay > 0.0)
			{
				num5 = Math.Max(num5, lobeSeed.Heat * fire * Math.Exp((0.0 - num14) / 0.12));
			}
			num4 = Math.Max(num4, num20);
			LobeState l = new LobeState
			{
				Center = vec2 * (1.0 / box),
				Radius = num17 / box,
				Axis = lobeSeed.Axis,
				Stretch = num19,
				Heat = num20,
				Displacement = (vec + Up * num3) * (1.0 / box)
			};
			s.SetLobe(i, l);
			double num21 = l.Radius * Math.Max(num19, 1.0) * 1.9;
			num6 = Math.Min(num6, l.Center.X - num21);
			num9 = Math.Max(num9, l.Center.X + num21);
			num7 = Math.Min(num7, l.Center.Y - num21);
			num10 = Math.Max(num10, l.Center.Y + num21);
			num8 = Math.Min(num8, l.Center.Z - num21);
			num11 = Math.Max(num11, l.Center.Z + num21);
		}
		s.FireRadius = s.Lobe0.Radius;
		s.FireRise = s.Lobe0.Center.Y;
		s.FireHeat = Numbers.Clamp(num4, 0.0, 1.0);
		s.Pulse = num5;
		s.Roll = air * Math.Min(2.2, 0.8 * age / Math.Max(tf, 0.1));
		s.NoiseRadius = Math.Max(0.02, R * (0.35 + 0.65 * num12) * num13 / box / Math.Max(layout.NoiseScale, 0.5));
		s.FireSoot = Numbers.Clamp(plan.Soot * (1.0 + 2.0 * layout.SootBias) * (0.4 + 0.6 * Smooth(0.02 * tf, 0.5 * tf, age)), 0.0, 1.0);
		double num22 = ((air > 0.05) ? (1.0 - Smooth(0.7 * tf, 2.0 * tf, age)) : (1.0 - Smooth(0.03 * tf, 0.2 * tf, age)));
		double num23 = 0.12 + 0.88 * air + 0.6 * (1.0 - air) * Math.Exp((0.0 - age) / (0.06 * tf));
		s.FireBody = Numbers.Clamp(fire * num23 * Smooth(0.0, 0.03, age) * num22 / (num13 * num13 * num13) / layout.Expansion, 0.0, 1.0);
		double y = s.Lobe0.Center.Y;
		if (((layout.Stem > 0.0) & surface) && y > 0.0 && num3 > 0.2 * R)
		{
			double num24 = 0.28 * R * num13 / box;
			s.Stem = new LobeState
			{
				Center = new Vec3(s.Lobe0.Center.X, y * 0.5, s.Lobe0.Center.Z),
				Radius = num24,
				Axis = Up,
				Stretch = Math.Max(1.0, y * 0.5 / num24),
				Displacement = Up * (num3 * 0.5 / box)
			};
			s.StemDensity = layout.Stem * Smooth(0.25 * R, 0.6 * R, num3) * num22;
			num7 = Math.Min(num7, -0.05);
		}
		else
		{
			s.Stem = new LobeState
			{
				Axis = Up,
				Stretch = 1.0
			};
		}
		if (num9 < num6)
		{
			s.BoundsRadius = 0.0;
			return;
		}
		s.BoundsCenter = new Vec3((num6 + num9) * 0.5, (num7 + num10) * 0.5, (num8 + num11) * 0.5);
		s.BoundsRadius = 0.5 * Math.Sqrt((num9 - num6) * (num9 - num6) + (num10 - num7) * (num10 - num7) + (num11 - num8) * (num11 - num8));
	}

	public static void Pack(VolumeState s, double seed, out Vec4 shell, out Vec4 core, out Vec4 cloud, out Vec4 context)
	{
		Pack(s, seed, out shell, out core, out cloud, out context, out var _, out var _);
	}

	public static void Pack(VolumeState s, double seed, out Vec4 shell, out Vec4 core, out Vec4 cloud, out Vec4 context, out Vec4 fireball, out Vec4 smoke)
	{
		fireball = new Vec4(s.NoiseRadius, s.FireHeat, s.Roughness, s.FireBody);
		smoke = new Vec4(s.SmokeR, s.SmokeG, s.SmokeB, s.FireSoot);
		shell = new Vec4(s.ShellRadius, s.ShellThickness, s.ShellOpacity, s.ShellErosion);
		core = new Vec4(s.CoreRadius, s.Heat, s.Glow, s.Rise);
		cloud = new Vec4(s.Cloud, s.Dust, s.Vapor, s.Fade);
		context = new Vec4(s.Ground ? 1 : (s.Water ? 2 : 0), s.Atmosphere, s.Age, seed % 997.0);
	}

	public static void PackLobe(VolumeState s, int i, out Vec4 center, out Vec4 axis, out Vec4 displacement)
	{
		LobeState lobeState = s.Lobe(i);
		center = new Vec4(lobeState.Center.X, lobeState.Center.Y, lobeState.Center.Z, lobeState.Radius);
		axis = new Vec4(lobeState.Axis.X, lobeState.Axis.Y, lobeState.Axis.Z, Math.Max(lobeState.Stretch, 0.2));
		displacement = new Vec4(lobeState.Displacement.X, lobeState.Displacement.Y, lobeState.Displacement.Z, (i < 4) ? lobeState.Heat : s.StemDensity);
	}

	public static Vec4 Bounds(VolumeState s)
	{
		return new Vec4(s.BoundsCenter.X, s.BoundsCenter.Y, s.BoundsCenter.Z, s.BoundsRadius);
	}

	public static Vec4 Motion(VolumeState s)
	{
		return new Vec4(s.Roll, s.WhiteFlame, 0.0, 0.0);
	}

	public static Vec4 Dust(VolumeState s)
	{
		return new Vec4(s.DustR, s.DustG, s.DustB, 0.0);
	}

	public static Vec4 Flare(VolumeState s)
	{
		double z = ((s.Ground || s.Water) ? (s.CoreRadius * 0.7) : 0.0) + s.Rise + s.FireRise * 0.5;
		double num = ((s.FireBody > 0.02) ? (0.55 + 0.45 * s.Glow) : 1.0);
		return new Vec4((0.22 + 0.9 * s.Glow + 0.2 * s.Heat) * s.Scale, Numbers.Clamp(s.Glow * 0.9 + s.Heat * 0.25, 0.0, 1.0) * s.Fade * num, z, Numbers.Clamp(Math.Max(s.Heat * 1.2, s.Glow * 1.6), 0.0, 1.0));
	}

	public static void Distortion(VolumeState s, double groundFire, out Vec4 shock, out Vec4 haze)
	{
		double num = (0.045 + 0.06 * Math.Min(1.0, s.ShockRadius / Math.Max(s.Scale, 0.001))) * s.Scale;
		double num2 = Math.Max(Math.Max(s.Haze, s.FireHeat * 0.8 * s.Atmosphere), Numbers.Clamp(groundFire, 0.0, 1.0) * 0.7 * s.Atmosphere);
		double w = Math.Max((s.ShockStrength > 0.002) ? (s.ShockRadius + 3.0 * num) : 0.0, (num2 > 0.002) ? (Math.Max(s.HazeHeight, 0.9) * 1.15) : 0.0);
		shock = new Vec4(s.ShockRadius, 0.03 * s.ShockStrength, num, w);
		haze = new Vec4(0.004 * num2, s.HazeHeight, 0.45 * s.Scale, s.Age);
	}

	public static bool DistortionVisible(VolumeState s, double groundFire)
	{
		if (!(s.ShockStrength > 0.002) && !(s.Haze > 0.002))
		{
			return groundFire * s.Atmosphere > 0.003;
		}
		return true;
	}

	public static double LightIntensity(VolumeState s)
	{
		return Numbers.Clamp(1.4 * s.Heat + 1.6 * s.Glow + 1.3 * s.FireHeat + 1.2 * s.Pulse, 0.0, 2.2 + 1.3 * s.Glow) * s.Fade * (0.6 + 0.4 * s.Atmosphere);
	}

	public static double LightHeight(VolumeState s)
	{
		return Math.Max(Flare(s).Z, (s.FireHeat > 0.05) ? s.FireRise : 0.0);
	}

	public static void ShockFront(FxPlan plan, BlastLayout layout, out double speed, out double timeConstant, out double reach)
	{
		double num = TimeScale(plan);
		timeConstant = 0.4 * num;
		reach = 2.6 * DomeRadius(plan);
		speed = reach / timeConstant;
	}
}
