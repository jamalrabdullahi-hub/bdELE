using System;

namespace VolumetricExplosionFX.Core;

public sealed class BlastLayout
{
	public const int Count = 4;

	public const double SurfaceReach = 1.9;

	private static readonly Vec3 Up = new Vec3(0.0, 1.0, 0.0);

	public readonly LobeSeed[] Lobes = new LobeSeed[4];

	public BlastForm Form;

	public Vec3 Direction = Up;

	public double Speed;

	public double RadiusMeters;

	public double ExtentMeters;

	public double NoiseScale = 1.0;

	public double Roughness = 0.6;

	public double Stem;

	public double Expansion = 1.0;

	public double Tempo = 1.0;

	public double HeatBias = 1.0;

	public double SootBias;

	private double spreadMax;

	private double liftMax;

	public int ActiveCount
	{
		get
		{
			int num = 0;
			for (int i = 0; i < 4; i++)
			{
				if (Lobes[i].Active)
				{
					num++;
				}
			}
			return num;
		}
	}

	public static double ExpansionFor(double air)
	{
		air = Numbers.Clamp(air, 0.0, 0.999);
		if (air < 0.05)
		{
			return 1.0;
		}
		double num = Math.Max(-0.35 * Math.Log(1.0 - air) / 1.225, 0.001);
		return Numbers.Clamp(Math.Pow(1.0 / num, 1.0 / 3.0), 1.0, 2.5);
	}

	public static BlastLayout Default(FxPlan plan, bool ground = false, bool water = false)
	{
		return Create(plan, default, ground, water, 1u, null);
	}

	public static BlastLayout Create(FxPlan plan, Vec3 velocity, bool ground, bool water, uint seed, BlastSource[] sources)
	{
		BlastLayout blastLayout = new BlastLayout();
		SplitRandom rnd = new SplitRandom(seed);
		double num = VolumeEvolution.FireballRadius(plan);
		double num2 = Numbers.Clamp(plan.Atmosphere, 0.0, 1.0);
		double val = VolumeEvolution.FireballDuration(plan);
		if (water)
		{
			num *= 0.6;
		}
		blastLayout.Expansion = ExpansionFor(num2);
		num = (blastLayout.RadiusMeters = num * blastLayout.Expansion);
		SplitRandom rnd2 = new SplitRandom(seed ^ 0x6D6F6F64);
		blastLayout.Tempo = rnd2.Range(0.85, 1.15);
		blastLayout.HeatBias = rnd2.Range(0.82, 1.05);
		blastLayout.SootBias = rnd2.Range(-0.2, 0.25);
		double num3 = Numbers.Clamp(velocity.Length, 0.0, 15000.0);
		Vec3 vec = ((num3 > 0.001) ? (velocity * (1.0 / num3)) : Up);
		double num4 = Math.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z);
		double num5 = Math.Max(0.0, 0.0 - velocity.Y);
		Vec3 vec2 = ((num4 > 0.001) ? new Vec3(velocity.X / num4, 0.0, velocity.Z / num4) : new Vec3(1.0, 0.0, 0.0));
		Vec3 vec3 = new Vec3(0.0 - vec2.Z, 0.0, vec2.X);
		blastLayout.Speed = num3;
		blastLayout.Direction = vec;
		if (water)
		{
			blastLayout.Form = BlastForm.Water;
		}
		else if (num2 < 0.05)
		{
			blastLayout.Form = BlastForm.Vacuum;
		}
		else if (!ground)
		{
			blastLayout.Form = ((num3 > 40.0) ? BlastForm.Streak : BlastForm.Rupture);
		}
		else if (num3 < 25.0)
		{
			blastLayout.Form = BlastForm.Rupture;
		}
		else if (num5 > 1.2 * num4)
		{
			blastLayout.Form = BlastForm.Vertical;
		}
		else
		{
			blastLayout.Form = BlastForm.Grazing;
		}
		blastLayout.NoiseScale = rnd.Range(0.8, 1.25);
		blastLayout.Roughness = rnd.Range(0.5, 0.75);
		double cap = 3.0 * num;
		LobeSeed s = new LobeSeed
		{
			Active = true,
			Size = 1.0,
			Heat = 1.0,
			Axis = Up,
			Stretch = 1.0,
			Tau = 0.15
		};
		switch (blastLayout.Form)
		{
		case BlastForm.Grazing:
			s.Axis = vec2;
			s.Stretch = 1.0 + Math.Min(0.6, num4 / 250.0);
			s.Drift = vec2 * (num4 * 0.25);
			s.Tau = 0.15;
			s.Size = 0.85;
			break;
		case BlastForm.Vertical:
			s.Stretch = 0.45;
			s.Size = 0.9;
			break;
		case BlastForm.Streak:
			s.Axis = vec;
			s.Stretch = 1.0 + Math.Min(1.0, num3 / 400.0);
			s.Tau = Numbers.Clamp(0.08 / Math.Max(num2, 0.08), 0.05, 1.2);
			break;
		case BlastForm.Vacuum:
			s.Axis = (ground ? Tilted(ref rnd, 0.9) : rnd.Direction());
			s.Stretch = rnd.Range(1.0, 1.3);
			break;
		case BlastForm.Water:
			s.Stretch = 0.7;
			break;
		default:
			s.Axis = Tilted(ref rnd, 0.35);
			s.Stretch = rnd.Range(0.9, 1.2);
			break;
		}
		blastLayout.Lobes[0] = Capped(s, cap);
		int num6 = 1;
		if (sources != null)
		{
			for (int i = 0; i < sources.Length; i++)
			{
				if (num6 >= 4)
				{
					break;
				}
				Vec3 offset = ClampLength(sources[i].Offset, 2.5 * num);
				blastLayout.Lobes[num6++] = Capped(new LobeSeed
				{
					Active = true,
					Offset = offset,
					Delay = Numbers.Clamp(sources[i].Delay, 0.0, 1.5),
					Size = Numbers.Clamp(0.45 + 0.4 * Numbers.Clamp(sources[i].Weight, 0.0, 1.0), 0.4, 0.9),
					Heat = rnd.Range(0.85, 1.0),
					Axis = s.Axis,
					Stretch = s.Stretch,
					Drift = s.Drift * 0.8,
					Tau = s.Tau
				}, cap);
			}
		}
		int num7 = blastLayout.Form switch
		{
			BlastForm.Rupture => 1 + (int)(rnd.Next() * 3.0), 
			BlastForm.Water => 1 + (int)(rnd.Next() * 2.0), 
			_ => 2 + (int)(rnd.Next() * 2.0), 
		};
		double num8 = rnd.Next() * Math.PI * 2.0;
		for (int j = 1; j <= num7; j++)
		{
			if (num6 >= 4)
			{
				break;
			}
			LobeSeed s2 = new LobeSeed
			{
				Active = true,
				Heat = 1.0,
				Stretch = 1.0,
				Axis = Up,
				Tau = 0.3,
				Relative = true
			};
			switch (blastLayout.Form)
			{
			case BlastForm.Grazing:
				s2.Offset = vec2 * (num * (0.25 + 0.22 * (double)j)) + vec3 * (num * rnd.Range(-0.3, 0.3)) + Up * (num * rnd.Range(0.0, 0.25));
				s2.Drift = vec2 * (num4 * (0.3 + 0.12 * (double)j) * rnd.Range(0.85, 1.15)) + vec3 * (num4 * rnd.Range(-0.1, 0.1)) + Up * rnd.Range(2.0, 10.0);
				s2.Tau = 0.1 + 0.1 * (double)j;
				s2.Size = 0.8 - 0.1 * (double)j + rnd.Range(-0.06, 0.06);
				s2.Delay = 0.02 * (double)j + rnd.Range(0.0, 0.06);
				s2.Axis = vec2;
				s2.Stretch = 1.0 + Math.Min(0.6, num4 / 250.0);
				s2.Relative = false;
				break;
			case BlastForm.Vertical:
			{
				double num11 = Math.PI * 2.0 * (double)j / (double)num7 + rnd.Range(-0.4, 0.4);
				Vec3 vec5 = new Vec3(Math.Cos(num11), 0.0, Math.Sin(num11));
				s2.Offset = vec5 * (num * 0.3);
				s2.Drift = vec5 * ((0.5 * num5 + 15.0) * rnd.Range(0.8, 1.2));
				s2.Tau = 0.25;
				s2.Size = rnd.Range(0.5, 0.62);
				s2.Delay = rnd.Range(0.02, 0.08);
				s2.Heat = 0.95;
				s2.Axis = vec5;
				s2.Stretch = 2.0;
				s2.Size += 0.08;
				break;
			}
			case BlastForm.Streak:
				s2.Offset = vec * ((0.0 - num) * 0.1 * (double)j);
				s2.Drift = vec * ((0.0 - num3) * (0.25 + 0.15 * (double)j) * rnd.Range(0.9, 1.1)) + rnd.Direction() * (num3 * 0.05);
				s2.Tau = Numbers.Clamp((0.06 + 0.04 * (double)j) / Math.Max(num2, 0.08), 0.05, 1.5);
				s2.Size = 0.8 - 0.12 * (double)j;
				s2.Delay = 0.02 * (double)j;
				s2.Axis = vec;
				s2.Stretch = 1.0 + Math.Min(1.2, num3 / 300.0);
				break;
			case BlastForm.Vacuum:
			{
				Vec3 vec7 = (ground ? Tilted(ref rnd, 0.9) : rnd.Direction());
				s2.Offset = vec7 * (num * 0.25);
				s2.Drift = vec7 * (num * rnd.Range(0.6, 1.2) / Math.Max(val, 0.5));
				s2.Tau = Math.Max(val, 0.5);
				s2.Size = rnd.Range(0.5, 0.7);
				s2.Delay = rnd.Range(0.0, 0.05);
				s2.Heat = rnd.Range(0.8, 1.0);
				s2.Axis = vec7;
				s2.Stretch = rnd.Range(1.1, 1.6);
				break;
			}
			case BlastForm.Water:
			{
				double num12 = rnd.Next() * Math.PI * 2.0;
				Vec3 vec6 = new Vec3(Math.Cos(num12), 0.0, Math.Sin(num12));
				s2.Offset = vec6 * (num * 0.3);
				s2.Drift = vec6 * rnd.Range(5.0, 15.0);
				s2.Size = 0.5;
				s2.Delay = rnd.Range(0.02, 0.1);
				s2.Heat = 0.8;
				s2.Stretch = 0.6;
				break;
			}
			default:
			{
				double num9 = num8 + (double)j * 2.39996 + rnd.Range(-0.35, 0.35);
				double num10 = (ground ? rnd.Range(0.25, 0.95) : rnd.Range(-0.9, 1.0));
				Vec3 vec4 = new Vec3(Math.Cos(num10) * Math.Cos(num9), Math.Sin(num10), Math.Cos(num10) * Math.Sin(num9));
				s2.Offset = vec4 * (num * rnd.Range(0.65, 0.9));
				s2.Drift = vec4 * rnd.Range(2.0, 8.0);
				s2.Size = rnd.Range(0.38, 0.58);
				s2.Delay = rnd.Range(0.03, 0.3);
				s2.Heat = rnd.Range(0.8, 1.0);
				s2.Axis = vec4;
				s2.Stretch = rnd.Range(1.0, 1.3);
				break;
			}
			}
			blastLayout.Lobes[num6++] = Capped(s2, cap);
		}
		if (plan.VisualEnergyScore > 3.5 && num2 > 0.05 && !water && rnd2.Next() < 0.45)
		{
			int num13 = ((num6 < 4) ? num6++ : 3);
			if (num13 > 0 && (sources == null || num13 > sources.Length))
			{
				Vec3 vec8 = (ground ? Tilted(ref rnd2, 0.8) : rnd2.Direction());
				blastLayout.Lobes[num13] = Capped(new LobeSeed
				{
					Active = true,
					Heat = 1.0,
					Stretch = rnd2.Range(1.0, 1.25),
					Axis = vec8,
					Tau = 0.3,
					Offset = vec8 * (num * rnd2.Range(0.45, 0.9)),
					Drift = vec8 * rnd2.Range(3.0, 9.0) + Up * rnd2.Range(2.0, 6.0),
					Size = rnd2.Range(0.5, 0.72),
					Delay = rnd2.Range(0.35, 1.1)
				}, cap);
			}
		}
		bool flag = blastLayout.Form == BlastForm.Rupture || blastLayout.Form == BlastForm.Grazing || blastLayout.Form == BlastForm.Vertical;
		blastLayout.Stem = (((ground && num2 > 0.3 && num > 5.0) & flag) ? rnd.Range(0.6, 1.0) : 0.0);
		blastLayout.spreadMax = VolumeEvolution.SpreadMax(num2);
		blastLayout.liftMax = num2 * num * 0.55 + ((ground | water) ? (0.45 * num * blastLayout.spreadMax) : 0.0);
		double num14 = 0.0;
		for (int k = 0; k < 4; k++)
		{
			if (blastLayout.Lobes[k].Active)
			{
				num14 = Math.Max(num14, blastLayout.Reach(blastLayout.Lobes[k]));
			}
		}
		blastLayout.ExtentMeters = num14;
		return blastLayout;
	}

	private double Reach(LobeSeed s)
	{
		return s.Offset.Length * (s.Relative ? spreadMax : 1.0) + s.Drift.Length * s.Tau + s.Size * RadiusMeters * spreadMax * Math.Max(s.Stretch, 1.0) * 1.9 + liftMax;
	}

	public bool AddSecondary(Vec3 offset, double delay, double weight)
	{
		if (RadiusMeters <= 0.0)
		{
			return false;
		}
		for (int i = 1; i < 4; i++)
		{
			if (!Lobes[i].Active)
			{
				LobeSeed lobeSeed = new LobeSeed
				{
					Active = true,
					Heat = 1.0,
					Axis = Up,
					Stretch = 1.0,
					Tau = 0.3,
					Drift = Up * 3.0,
					Size = Numbers.Clamp(0.4 + 0.35 * Numbers.Clamp(weight, 0.0, 1.0), 0.35, 0.75),
					Delay = Math.Max(0.0, delay)
				};
				double num = ExtentMeters - lobeSeed.Size * RadiusMeters * spreadMax * 1.9 - liftMax - lobeSeed.Drift.Length * lobeSeed.Tau;
				if (num <= 0.0)
				{
					return false;
				}
				lobeSeed.Offset = ClampLength(offset, num);
				Lobes[i] = lobeSeed;
				return true;
			}
		}
		return false;
	}

	private static LobeSeed Capped(LobeSeed s, double cap)
	{
		double num = s.Drift.Length * s.Tau;
		double num2 = Math.Max(0.0, cap - s.Offset.Length);
		if (num > num2 && num > 1E-09)
		{
			s.Drift *= num2 / num;
		}
		return s;
	}

	private static Vec3 ClampLength(Vec3 v, double max)
	{
		double length = v.Length;
		if (!(length > max) || !(length > 1E-09))
		{
			return v;
		}
		return v * (max / length);
	}

	private static Vec3 Tilted(ref SplitRandom rnd, double spread)
	{
		Vec3 vec = rnd.Direction();
		Vec3 vec2 = new Vec3(vec.X * spread, Math.Abs(vec.Y) * (1.0 - spread) + 0.25, vec.Z * spread);
		double length = vec2.Length;
		return vec2 * (1.0 / length);
	}
}
