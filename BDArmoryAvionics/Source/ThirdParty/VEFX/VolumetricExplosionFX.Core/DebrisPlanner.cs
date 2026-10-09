using System;

namespace VolumetricExplosionFX.Core;

public static class DebrisPlanner
{
	private static readonly Vec3 Up = new Vec3(0.0, 1.0, 0.0);

	public static int Pieces(FxPlan plan, double partSize)
	{
		double num = Numbers.Clamp(plan.VisualEnergyScore / 6.0, 0.0, 1.5);
		double d = Numbers.Clamp(partSize, 0.2, 30.0);
		return (int)Numbers.Clamp(Math.Round(4.0 + 7.0 * Math.Sqrt(d) * (0.45 + 0.55 * num) + 8.0 * plan.FireFraction), 3.0, 48.0);
	}

	public static double LaunchSpeed(FxPlan plan)
	{
		double num = Numbers.Clamp(plan.FireFraction, 0.0, 1.0);
		double num2 = Numbers.Clamp(Math.Sqrt(Math.Max(plan.KineticJoules, 0.0)) / 400.0, 0.0, 30.0);
		return Numbers.Clamp(4.0 + num * (10.0 + 8.0 * Numbers.Clamp(plan.VisualEnergyScore, 0.0, 9.0)) + num2, 4.0, 90.0);
	}

	public static FragmentMotion[] Launch(FxPlan plan, FractureResult cut, Quat toEffect, Vec3 offset, Vec3 center, Vec3 partVelocity, bool surface, uint seed)
	{
		SplitRandom splitRandom = new SplitRandom(seed);
		double num = Numbers.Clamp(plan.Atmosphere, 0.0, 1.0);
		double num2 = Numbers.Clamp(plan.FireFraction, 0.0, 1.0);
		double num3 = LaunchSpeed(plan);
		double num4 = 0.0;
		for (int i = 0; i < cut.Count; i++)
		{
			num4 += cut.Radius[i];
		}
		num4 = Math.Max(num4 / (double)Math.Max(cut.Count, 1), 0.05);
		Vec3 vec = (surface ? new Vec3(partVelocity.X * 0.35, Math.Max(0.0, 0.0 - partVelocity.Y) * 0.06, partVelocity.Z * 0.35) : partVelocity);
		FragmentMotion[] array = new FragmentMotion[cut.Count];
		for (int j = 0; j < cut.Count; j++)
		{
			FragmentMotion fragmentMotion = new FragmentMotion
			{
				Pivot = Rotate(toEffect, cut.Pivot[j]) + offset
			};
			Vec3 vec2 = Rotate(toEffect, cut.Normal[j]);
			Vec3 vec3 = (fragmentMotion.Pivot - center).Normalized(vec2);
			Vec3 vec4 = (vec3 * 0.6 + vec2 * 0.3 + splitRandom.Direction() * 0.5).Normalized(vec3);
			if (surface && vec4.Y < 0.12)
			{
				vec4 = new Vec3(vec4.X, 0.12 + Math.Abs(vec4.Y) * 0.6, vec4.Z).Normalized(Up);
			}
			double num5 = Math.Max(cut.Radius[j], 0.04);
			double num6 = Numbers.Clamp(Math.Pow(num4 / num5, 0.5), 0.5, 2.0);
			fragmentMotion.Velocity = vec4 * (num3 * num6 * (0.5 + 0.7 * splitRandom.Next())) + vec;
			fragmentMotion.SpinAxis = splitRandom.Direction();
			fragmentMotion.SpinRate = Numbers.Clamp(fragmentMotion.Velocity.Length / Math.Max(num5, 0.2) * (0.08 + 0.22 * splitRandom.Next()), 0.4, 18.0);
			fragmentMotion.Drag = Numbers.Clamp(num * (0.35 + 0.7 * splitRandom.Next()) / Math.Max(num5 * 1.2, 0.25), 0.0, 3.0);
			bool flag = splitRandom.Next() < 0.25 + 0.6 * num2;
			fragmentMotion.Heat = (flag ? Numbers.Clamp(num2 * (0.35 + 0.65 * splitRandom.Next()) + 0.25 * Math.Max(0.0, 1.0 - num) * num2, 0.0, 1.0) : (0.15 * splitRandom.Next() * Math.Max(num2, 0.2)));
			fragmentMotion.Char = Numbers.Clamp(num2 * (0.15 + 0.55 * splitRandom.Next()) + 0.1 * splitRandom.Next(), 0.0, 1.0);
			fragmentMotion.Burning = num > 0.1 && num2 > 0.1 && fragmentMotion.Heat > 0.5 && num5 >= num4 * 0.7;
			fragmentMotion.BurnTime = (fragmentMotion.Burning ? (1.5 + 5.0 * fragmentMotion.Heat * splitRandom.Next()) : 0.0);
			fragmentMotion.LandTime = double.PositiveInfinity;
			fragmentMotion.RestHeight = -1000000000.0;
			array[j] = fragmentMotion;
		}
		return array;
	}

	public static double LowestPoint(FractureResult cut, int fragment, Quat toEffect, Vec3 axis, double angle)
	{
		return LowestPoint(cut, fragment, toEffect, axis, angle, new Vec3(1.0, 0.0, 0.0), 0.0);
	}

	public static double LowestPoint(FractureResult cut, int fragment, Quat toEffect, Vec3 axis, double angle, Vec3 axis2, double angle2)
	{
		Vec3 vec = Rotate(toEffect, cut.Pivot[fragment]);
		double num = 0.0;
		int num2 = cut.FirstVertex[fragment];
		int num3 = num2 + cut.VertexCounts[fragment];
		for (int i = num2; i < num3; i++)
		{
			Vec3 v = Vec3.Rotate(Rotate(toEffect, cut.Positions[i]) - vec, axis, angle);
			if (angle2 != 0.0)
			{
				v = Vec3.Rotate(v, axis2, angle2);
			}
			if (v.Y < num)
			{
				num = v.Y;
			}
		}
		return num;
	}

	public static void Land(FractureResult cut, int fragment, Quat toEffect, ref FragmentMotion m, double floor, double gravity, double horizon, bool wet = false)
	{
		m.Hop = 0.0;
		m.Slide = default;
		m.SlideTime = 0.0;
		m.SettleAngle = 0.0;
		m.SettleAxis = new Vec3(1.0, 0.0, 0.0);
		double num = Ballistics.LandingTime(m.Pivot.Y, m.Velocity.Y, m.Drag, gravity, floor, horizon);
		if (double.IsPositiveInfinity(num))
		{
			m.LandTime = num;
			m.RestHeight = floor;
			m.LandHeight = floor;
			return;
		}
		double num2 = floor;
		for (int i = 0; i < 2; i++)
		{
			double num3 = LowestPoint(cut, fragment, toEffect, m.SpinAxis, Ballistics.SpinAngle(m.SpinRate, m.Drag, num));
			num2 = floor - num3;
			num = Ballistics.LandingTime(m.Pivot.Y, m.Velocity.Y, m.Drag, gravity, num2, horizon);
			if (double.IsPositiveInfinity(num))
			{
				break;
			}
		}
		m.LandTime = num;
		m.LandHeight = num2;
		m.RestHeight = num2;
		if (double.IsPositiveInfinity(num))
		{
			return;
		}
		double num4 = Math.Max(m.Drag, 0.0);
		double num5 = Math.Exp((0.0 - num4) * num);
		double num6 = m.Velocity.Y * num5 - ((num4 > 1E-06) ? (gravity * (1.0 - num5) / num4) : (gravity * num));
		if (!wet)
		{
			m.Hop = Math.Min(0.22 * Math.Max(0.0 - num6, 0.0), 5.0);
			if (m.Hop < 0.6)
			{
				m.Hop = 0.0;
			}
			double num7 = 0.5 * Math.Max(gravity, 0.5);
			Vec3 slide = new Vec3(m.Velocity.X * num5, 0.0, m.Velocity.Z * num5) * 0.35;
			double length = slide.Length;
			if (length * length / (2.0 * num7) > 30.0)
			{
				slide *= Math.Sqrt(60.0 * num7) / length;
				length = slide.Length;
			}
			m.Slide = slide;
			m.SlideTime = length / num7;
		}
		double angle = Ballistics.SpinAngle(m.SpinRate, m.Drag, num);
		Vec3 a = Vec3.Rotate(Rotate(toEffect, cut.Normal[fragment]), m.SpinAxis, angle);
		Vec3 b = ((a.Y >= 0.0) ? Up : new Vec3(0.0, -1.0, 0.0));
		Vec3 vec = Vec3.Cross(a, b);
		double length2 = vec.Length;
		if (length2 > 1E-06)
		{
			m.SettleAxis = vec * (1.0 / length2);
			m.SettleAngle = Math.Atan2(length2, Vec3.Dot(a, b));
		}
		m.RestHeight = floor - LowestPoint(cut, fragment, toEffect, m.SpinAxis, angle, m.SettleAxis, m.SettleAngle);
	}

	public static double HopHeight(FragmentMotion m, double gravity, double tau)
	{
		if (m.Hop <= 0.0 || gravity <= 0.0)
		{
			return 0.0;
		}
		double num = 2.0 * m.Hop / gravity;
		if (!(tau > 0.0) || !(tau < num))
		{
			return 0.0;
		}
		return m.Hop * tau - 0.5 * gravity * tau * tau;
	}

	public static double Settled(FragmentMotion m, double gravity, double tau)
	{
		double num = ((m.Hop > 0.0 && gravity > 0.0) ? (2.0 * m.Hop / gravity) : 0.0);
		double num2 = Numbers.Clamp(tau / (num + 0.35 + 0.25 * Math.Min(m.SettleAngle, 1.6)), 0.0, 1.0);
		return num2 * num2 * (3.0 - 2.0 * num2);
	}

	public static Vec3 Where(FragmentMotion m, double gravity, double age)
	{
		if (age >= m.LandTime)
		{
			Vec3 vec = Ballistics.Position(m.Pivot, m.Velocity, m.Drag, gravity, m.LandTime);
			double num = age - m.LandTime;
			double num2 = Math.Min(num, m.SlideTime);
			double num3 = ((m.SlideTime > 1E-06) ? (num2 - num2 * num2 / (2.0 * m.SlideTime)) : 0.0);
			double num4 = Settled(m, gravity, num);
			double num5 = ((num4 >= 1.0) ? m.RestHeight : (m.LandHeight + (m.RestHeight - m.LandHeight) * num4));
			return new Vec3(vec.X + m.Slide.X * num3, num5 + HopHeight(m, gravity, num), vec.Z + m.Slide.Z * num3);
		}
		return Ballistics.Position(m.Pivot, m.Velocity, m.Drag, gravity, age);
	}

	public static Vec3 Rotate(Quat q, Vec3 v)
	{
		Vec3 a = new Vec3(q.X, q.Y, q.Z);
		Vec3 vec = Vec3.Cross(a, v) * 2.0;
		return v + vec * q.W + Vec3.Cross(a, vec);
	}
}
