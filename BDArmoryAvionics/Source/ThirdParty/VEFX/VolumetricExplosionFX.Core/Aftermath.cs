using System;

namespace VolumetricExplosionFX.Core;

public static class Aftermath
{
	public static double Start(FxPlan plan, double tempo = 1.0)
	{
		if (Numbers.Clamp(plan.Atmosphere, 0.0, 1.0) < 0.05 || plan.FireFraction < 0.05 || VolumeEvolution.FireballRadius(plan) <= 0.0)
		{
			return double.PositiveInfinity;
		}
		return Math.Max(0.75 * VolumeEvolution.FireballDuration(plan) * tempo, 0.55 * VolumeEvolution.VolumeDuration(plan));
	}

	public static Vec3 Wind(uint seed, double air)
	{
		SplitRandom splitRandom = new SplitRandom(seed ^ 0x57494E44);
		double num = splitRandom.Next() * Math.PI * 2.0;
		double num2 = (0.8 + 2.6 * splitRandom.Next()) * Numbers.Clamp(air, 0.0, 1.0);
		return new Vec3(Math.Cos(num) * num2, 0.0, Math.Sin(num) * num2);
	}

	public static int Build(VolumeState s, FxPlan plan, uint seed, Vec3 wind, bool surface, AftermathPuff[] output)
	{
		if (output == null || output.Length == 0 || s.BoxRadius <= 0.0)
		{
			return 0;
		}
		double num = Numbers.Clamp(plan.Atmosphere, 0.0, 1.0);
		SplitRandom rnd = new SplitRandom(seed ^ 0xAF7E12);
		double boxRadius = s.BoxRadius;
		double num2 = Numbers.Clamp(0.2 + 0.75 * Math.Min(1.0, s.FireBody * 2.2 + 0.25 * plan.Soot), 0.2, 0.85) * (0.6 + 0.4 * num) * 0.7;
		double num3 = 0.0;
		for (int i = 0; i < 4; i++)
		{
			num3 = Math.Max(num3, s.Lobe(i).Radius * boxRadius);
		}
		if (num3 < 0.5)
		{
			return 0;
		}
		int num4 = 0;
		for (int j = 0; j < 4; j++)
		{
			if (num4 >= output.Length)
			{
				break;
			}
			LobeState lobeState = s.Lobe(j);
			double num5 = lobeState.Radius * boxRadius;
			if (num5 < 0.3 * num3)
			{
				continue;
			}
			Vec3 vec = lobeState.Center * boxRadius;
			int num6 = (int)Numbers.Clamp(3.0 + 4.0 * num5 / num3, 4.0, 7.0);
			for (int k = 0; k < num6; k++)
			{
				if (num4 >= output.Length)
				{
					break;
				}
				Vec3 vec2 = rnd.Direction();
				double num7 = Math.Pow(rnd.Next(), 1.0 / 3.0);
				Vec3 vec3 = vec2 * (num5 * 0.6 * num7);
				double num8 = Math.Max(num5, 0.5 * num3) * rnd.Range(1.2, 1.6) * (1.1 - 0.25 * num7);
				Vec3 position = vec + vec3;
				if (surface)
				{
					position = new Vec3(position.X, Math.Max(position.Y, num8 * 0.3), position.Z);
				}
				AftermathPuff aftermathPuff = Puff(position, vec3, num8, num2 * 0.75, num, wind, ref rnd);
				aftermathPuff.Velocity += new Vec3(0.0, Math.Max(0.0, vec3.Y / Math.Max(num5, 1E-06)) * 1.6 * num, 0.0);
				output[num4++] = aftermathPuff;
			}
		}
		LobeState stem = s.Stem;
		if (surface && s.StemDensity > 0.05 && stem.Radius > 0.0)
		{
			double num9 = stem.Radius * boxRadius;
			double num10 = stem.Center.Y * boxRadius * 2.0;
			int num11 = (int)Numbers.Clamp(num10 / (num9 * 1.6), 2.0, 6.0);
			for (int l = 0; l < num11; l++)
			{
				if (num4 >= output.Length)
				{
					break;
				}
				double num12 = num9 * rnd.Range(2.2, 2.8);
				double y = Math.Max(num10 * ((double)l + 0.5) / (double)num11, num12 * 0.3);
				Vec3 position2 = new Vec3(stem.Center.X * boxRadius + rnd.Range(-0.3, 0.3) * num9, y, stem.Center.Z * boxRadius + rnd.Range(-0.3, 0.3) * num9);
				output[num4++] = Puff(position2, default, num12, num2 * Numbers.Clamp(s.StemDensity * 1.5, 0.3, 1.0), num, wind, ref rnd);
			}
		}
		return num4;
	}

	private static AftermathPuff Puff(Vec3 position, Vec3 offset, double size, double alpha, double air, Vec3 wind, ref SplitRandom rnd)
	{
		Vec3 vec = new Vec3(0.0, (0.5 + 1.6 * air) * rnd.Range(0.7, 1.3), 0.0);
		return new AftermathPuff
		{
			Position = position,
			Velocity = vec + offset * 0.05 + wind * rnd.Range(0.8, 1.2),
			Size = size,
			Life = rnd.Range(25.0, 50.0) * (0.6 + 0.4 * air),
			Alpha = alpha * rnd.Range(0.85, 1.1)
		};
	}
}
