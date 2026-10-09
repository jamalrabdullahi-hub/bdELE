using System;

namespace VolumetricExplosionFX.Core;

public static class VisualEnergy
{
	public static FxPlan Calculate(DestructionEvent e, ResourceClassifier classifier, FxSettings settings)
	{
		PartSnapshot part = e.Part;
		double atmosphereBlend = part.Environment.AtmosphereBlend;
		ResourceKind contents = classifier.Contents(part);
		double num = 0.0;
		double num2 = 0.0;
		double num3 = 0.0;
		double num4 = 0.0;
		double num5 = 0.0;
		double num6 = 0.0;
		double num7 = 0.0;
		double num8 = 0.0;
		double num9 = 0.0;
		for (int i = 0; i < part.Resources.Length; i++)
		{
			ResourceSnapshot resourceSnapshot = part.Resources[i];
			ResourceKind resourceKind = classifier.Lookup(resourceSnapshot.Name);
			if ((resourceKind & ResourceKind.Electric) != 0)
			{
				num5 += resourceSnapshot.Amount;
			}
			if ((resourceKind & ResourceKind.Fuel) != 0)
			{
				num += resourceSnapshot.MassKg;
			}
			if ((resourceKind & ResourceKind.Oxidizer) != 0)
			{
				num2 += resourceSnapshot.MassKg;
			}
			if ((resourceKind & ResourceKind.Solid) != 0 || ((resourceKind & ResourceKind.Hypergolic) != 0 && (resourceKind & ResourceKind.Fuel) == 0 && (resourceKind & ResourceKind.Oxidizer) == 0))
			{
				num3 += resourceSnapshot.MassKg;
			}
			if ((resourceKind & ResourceKind.Cryogenic) != 0)
			{
				num4 += resourceSnapshot.MassKg;
			}
			else if ((resourceKind & ResourceKind.Pressurized) != 0 && (resourceKind & (ResourceKind.Fuel | ResourceKind.Oxidizer | ResourceKind.Solid | ResourceKind.Hypergolic)) == 0)
			{
				num4 += resourceSnapshot.MassKg * 0.6;
			}
			if ((resourceKind & ResourceKind.Fuel) != 0 || (resourceKind & ResourceKind.Solid) != 0 || ((resourceKind & ResourceKind.Hypergolic) != 0 && (resourceKind & ResourceKind.Oxidizer) == 0))
			{
				if ((resourceKind & ResourceKind.Solid) != 0)
				{
					num8 += resourceSnapshot.MassKg;
				}
				else if ((resourceKind & ResourceKind.Sooty) != 0)
				{
					num6 += resourceSnapshot.MassKg;
				}
				else if ((resourceKind & ResourceKind.Clean) != 0)
				{
					num7 += resourceSnapshot.MassKg;
				}
				else
				{
					num6 += resourceSnapshot.MassKg * 0.35;
				}
			}
			if ((resourceKind & ResourceKind.Hypergolic) != 0 && (resourceKind & ResourceKind.Oxidizer) != 0)
			{
				num9 += resourceSnapshot.MassKg;
			}
		}
		double num10 = num3 + Math.Min(num, num2) + num * atmosphereBlend * 0.2;
		if (e.Kind == EventKind.WaterImpact)
		{
			num10 *= 0.35;
		}
		double num11 = Numbers.Clamp(part.SurfaceVelocity.Length, 0.0, 15000.0);
		double num12 = ((e.Kind == EventKind.Destruction) ? 0.0 : (0.5 * Math.Min(part.MassKg, 10000000.0) * num11 * num11));
		double num13 = Numbers.Clamp(Math.Log10(1.0 + num12 / 1000.0 + Math.Min(num10, 1000000.0) * 40.0 + Math.Min(part.MassKg, 10000000.0) * 0.05), 0.0, 9.0);
		double num14 = Numbers.Clamp((Numbers.Clamp(part.Dimensions.Length * 0.25, 0.15, 12.0) + Math.Pow(1.0 + num10 + num12 / 500000.0, 1.0 / 3.0) * 0.5) * settings.Intensity, 0.35, 30.0);
		double num15 = num10 / (num10 + 10.0);
		double num16 = Math.Max(num6 + num7 + num8 + num9, 1E-09);
		double num17 = Numbers.Clamp(num6 / num16, 0.0, 1.0);
		double num18 = num8 / num16;
		double num19 = num9 / num16;
		double num20 = 0.56 - 0.26 * num17;
		double num21 = 0.54 - 0.26 * num17;
		double num22 = 0.51 - 0.25 * num17;
		num20 += (0.86 - num20) * num18;
		num21 += (0.85 - num21) * num18;
		num22 += (0.83 - num22) * num18;
		num20 += (0.6 - num20) * num19;
		num21 += (0.36 - num21) * num19;
		num22 += (0.18 - num22) * num19;
		Vec3 vec = BodyLook.Dust((part.Environment != null) ? part.Environment.Body : null);
		return new FxPlan
		{
			DustR = vec.X,
			DustG = vec.Y,
			DustB = vec.Z,
			PartKind = classifier.Classify(part),
			Contents = contents,
			Kind = e.Kind,
			Atmosphere = atmosphereBlend,
			ImpactSpeed = num11,
			KineticJoules = num12,
			VisualEnergyScore = num13,
			BurnableKg = Math.Min(num10, 10000000.0),
			Soot = num17 * (1.0 - num18),
			SmokeR = num20,
			SmokeG = num21,
			SmokeB = num22,
			WhiteFlame = Numbers.Clamp(num18, 0.0, 1.0),
			Radius = num14,
			FireFraction = num15,
			SmokeFraction = atmosphereBlend * num15,
			VaporFraction = num4 / (num4 + 10.0),
			ElectricFraction = num5 / (num5 + 50.0),
			ExpansionSpeed = num14 * (2.5 - 1.5 * atmosphereBlend),
			Lifetime = 1.2 + atmosphereBlend * 5.0,
			DebrisCount = (settings.Debris ? ((int)Numbers.Clamp((8.0 + num13 * 12.0) * settings.DebrisDensity, 0.0, settings.ParticlesPerEvent)) : 0),
			PuffCount = (int)Numbers.Clamp(8.0 + num13 * 7.0, 8.0, settings.ParticlesPerEvent / 2)
		};
	}
}
