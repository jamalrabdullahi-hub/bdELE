using System;

namespace VolumetricExplosionFX.Core;

public static class Ballistics
{
	public static double E1(double u)
	{
		if (!(u < 0.02))
		{
			return (1.0 - Math.Exp(0.0 - u)) / u;
		}
		return 1.0 - u * (0.5 - u * (1.0 / 6.0 - u / 24.0));
	}

	public static double E2(double u)
	{
		if (!(u < 0.02))
		{
			return (u - 1.0 + Math.Exp(0.0 - u)) / (u * u);
		}
		return 0.5 - u * (1.0 / 6.0 - u * (1.0 / 24.0 - u / 120.0));
	}

	public static Vec3 Position(Vec3 start, Vec3 velocity, double drag, double gravity, double t)
	{
		t = Math.Max(t, 0.0);
		double u = Math.Max(drag, 0.0) * t;
		double num = t * E1(u);
		double num2 = t * t * E2(u);
		return new Vec3(start.X + velocity.X * num, start.Y + velocity.Y * num - gravity * num2, start.Z + velocity.Z * num);
	}

	public static double Height(double y0, double vy, double drag, double gravity, double t)
	{
		t = Math.Max(t, 0.0);
		double u = Math.Max(drag, 0.0) * t;
		return y0 + vy * t * E1(u) - gravity * t * t * E2(u);
	}

	public static double SpinAngle(double rate, double drag, double t)
	{
		t = Math.Max(t, 0.0);
		return rate * t * E1(Math.Max(drag, 0.0) * t);
	}

	public static double ApexTime(double vy, double drag, double gravity)
	{
		if (vy <= 0.0 || gravity <= 0.0)
		{
			return 0.0;
		}
		if (!(drag > 1E-06))
		{
			return vy / gravity;
		}
		return Math.Log(1.0 + drag * vy / gravity) / drag;
	}

	public static double LandingTime(double y0, double vy, double drag, double gravity, double floor, double horizon)
	{
		drag = Math.Max(drag, 0.0);
		double num = ApexTime(vy, drag, gravity);
		if (Height(y0, vy, drag, gravity, num) <= floor)
		{
			return 0.0;
		}
		double num2 = num;
		double num3 = num + 0.5;
		while (Height(y0, vy, drag, gravity, num3) > floor)
		{
			if (num3 > horizon)
			{
				return double.PositiveInfinity;
			}
			num2 = num3;
			num3 = num3 * 2.0 + 0.5;
		}
		for (int i = 0; i < 60; i++)
		{
			double num4 = (num2 + num3) * 0.5;
			if (Height(y0, vy, drag, gravity, num4) > floor)
			{
				num2 = num4;
			}
			else
			{
				num3 = num4;
			}
		}
		return num3;
	}
}
