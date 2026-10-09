using System;

namespace VolumetricExplosionFX.Core;

public sealed class RunawayPath
{
	public const double Step = 0.05;

	private readonly Vec3[] points;

	public readonly double ThrustTime;

	public readonly double Duration;

	public int Count => points.Length;

	private RunawayPath(Vec3[] p, double thrust)
	{
		points = p;
		ThrustTime = thrust;
		Duration = (double)(p.Length - 1) * 0.05;
	}

	public Vec3 At(double t)
	{
		int num = points.Length - 1;
		double num2 = Numbers.Clamp(t / 0.05, 0.0, num);
		int num3 = (int)Math.Floor(num2);
		if (num3 >= num)
		{
			return points[num];
		}
		return points[num3] + (points[num3 + 1] - points[num3]) * (num2 - (double)num3);
	}

	public static RunawayPath Create(uint seed, Vec3 velocity, double gravity, double air, double floor, double duration)
	{
		SplitRandom splitRandom = new SplitRandom(seed ^ 0x52554E41);
		air = Numbers.Clamp(air, 0.0, 1.0);
		gravity = Math.Max(gravity, 0.0);
		Vec3 vec = velocity.Normalized(new Vec3(0.0, 1.0, 0.0));
		Vec3 axis = Vec3.Cross(vec, splitRandom.Direction()).Normalized(new Vec3(1.0, 0.0, 0.0));
		Vec3 vec2 = Vec3.Rotate(vec, axis, splitRandom.Range(35.0, 90.0) * Math.PI / 180.0);
		Vec3 vec3 = velocity + vec2 * splitRandom.Range(40.0, 80.0);
		double num = splitRandom.Range(3.0, 7.0);
		double num2 = splitRandom.Range(30.0, 50.0);
		Vec3 vec4 = splitRandom.Direction();
		Vec3 axis2 = splitRandom.Direction();
		double num3 = splitRandom.Range(1.0, 2.4) * (double)((!(splitRandom.Next() < 0.5)) ? 1 : (-1));
		double num4 = splitRandom.Range(0.4, 1.0);
		double num5 = 0.015 + 0.06 * air;
		int num6 = (int)Math.Ceiling(Numbers.Clamp(duration, 0.05, 120.0) / 0.05) + 1;
		Vec3[] array = new Vec3[num6];
		Vec3 vec5 = default;
		bool flag = false;
		for (int i = 0; i < num6; i++)
		{
			array[i] = vec5;
			if (!flag)
			{
				double num7 = (double)i * 0.05;
				Vec3 vec6 = new Vec3(0.0, 0.0 - gravity, 0.0) - vec3 * num5;
				if (num7 < num)
				{
					vec6 += vec2 * num2;
					vec4 = Vec3.Rotate(vec4, axis2, num4 * 0.05).Normalized(vec4);
					vec2 = Vec3.Rotate(vec2, vec4, num3 * 0.05).Normalized(vec2);
				}
				vec3 += vec6 * 0.05;
				Vec3 vec7 = vec5 + vec3 * 0.05;
				if (vec7.Y <= floor)
				{
					vec7 = new Vec3(vec7.X, floor, vec7.Z);
					flag = true;
				}
				vec5 = vec7;
			}
		}
		return new RunawayPath(array, num);
	}
}
