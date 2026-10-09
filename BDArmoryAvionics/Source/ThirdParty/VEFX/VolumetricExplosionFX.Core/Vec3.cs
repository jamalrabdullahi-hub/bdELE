using System;

namespace VolumetricExplosionFX.Core;

public struct Vec3(double x, double y, double z)
{
	public readonly double X = x;

	public readonly double Y = y;

	public readonly double Z = z;

	public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

	public static Vec3 operator +(Vec3 a, Vec3 b)
	{
		return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
	}

	public static Vec3 operator -(Vec3 a, Vec3 b)
	{
		return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
	}

	public static Vec3 operator *(Vec3 a, double b)
	{
		return new Vec3(a.X * b, a.Y * b, a.Z * b);
	}

	public static double Dot(Vec3 a, Vec3 b)
	{
		return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
	}

	public static Vec3 Cross(Vec3 a, Vec3 b)
	{
		return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
	}

	public Vec3 Normalized(Vec3 fallback)
	{
		double length = Length;
		if (!(length > 1E-12))
		{
			return fallback;
		}
		return this * (1.0 / length);
	}

	public static Vec3 Rotate(Vec3 v, Vec3 axis, double angle)
	{
		double num = Math.Cos(angle);
		double num2 = Math.Sin(angle);
		return v * num + Cross(axis, v) * num2 + axis * (Dot(axis, v) * (1.0 - num));
	}
}
