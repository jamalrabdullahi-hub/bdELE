using System;

namespace VolumetricExplosionFX.Core;

public sealed class EnvironmentContext
{
	public readonly string Body;

	public readonly double Density;

	public readonly double PressureKpa;

	public readonly double Gravity;

	public readonly double Altitude;

	public readonly bool HasOcean;

	public readonly bool Landed;

	public readonly bool Splashed;

	public readonly Vec3 Up;

	public double AtmosphereBlend => 1.0 - Math.Exp((0.0 - Density) / 0.35);

	public EnvironmentContext(string body, double density, double pressure, double gravity, double altitude, bool ocean, bool landed, bool splashed, Vec3 up)
	{
		Body = body;
		Density = Numbers.Positive(density);
		PressureKpa = Numbers.Positive(pressure);
		Gravity = Numbers.Positive(gravity);
		Altitude = altitude;
		HasOcean = ocean;
		Landed = landed;
		Splashed = splashed;
		Up = up;
	}
}
