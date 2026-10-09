using System;
using UnityEngine;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Rendering;

internal sealed class DebrisTrails
{
	private FragmentMotion[] motion;

	private int[] burning;

	private float[] acc;

	private int count;

	private double gravity;

	private SplitRandom rnd;

	private Color tint;

	private float smoulder;

	private bool[] landed;

	private Color dust;

	private bool wet;

	private float firstTick = -1f;

	internal bool FlyingPuffs = true;

	internal int Count => count;

	internal void Begin(FragmentMotion[] pieces, double g, int maximum, FxPlan fx, uint seed, float residueSeconds, bool water = false)
	{
		if (landed == null || landed.Length < pieces.Length)
		{
			landed = new bool[pieces.Length];
		}
		for (int i = 0; i < landed.Length; i++)
		{
			landed[i] = false;
		}
		wet = water;
		firstTick = -1f;
		dust = (water ? new Color(0.9f, 0.93f, 0.97f, 1f) : new Color((float)Math.Min(1.0, fx.DustR * 1.1), (float)Math.Min(1.0, fx.DustG * 1.1), (float)Math.Min(1.0, fx.DustB * 1.1), 1f));
		motion = pieces;
		gravity = g;
		rnd = new SplitRandom(seed ^ 0x7A11);
		count = 0;
		if (burning == null || burning.Length < maximum)
		{
			burning = new int[maximum];
			acc = new float[maximum];
		}
		for (int j = 0; j < pieces.Length; j++)
		{
			if (count >= maximum)
			{
				break;
			}
			if (pieces[j].Burning)
			{
				burning[count] = j;
				acc[count] = (float)rnd.Next();
				count++;
			}
		}
		double num = Numbers.Clamp(fx.Soot, 0.0, 1.0);
		tint = new Color((float)Numbers.Clamp(fx.SmokeR * (1.15 - 0.2 * num), 0.12, 1.0), (float)Numbers.Clamp(fx.SmokeG * (1.15 - 0.2 * num), 0.12, 1.0), (float)Numbers.Clamp(fx.SmokeB * (1.15 - 0.2 * num), 0.12, 1.0), 1f);
		smoulder = Mathf.Min(residueSeconds * 0.5f, 20f);
	}

	internal void Stop()
	{
		count = 0;
		motion = null;
	}

	internal void Tick(float age, float dt, ParticleSystem fire, ParticleSystem smoke, ParticleSystem flames = null)
	{
		if (motion != null)
		{
			Landings(age, smoke);
		}
		for (int i = 0; i < count; i++)
		{
			FragmentMotion m = motion[burning[i]];
			bool flag = (double)age < m.LandTime;
			float num = (float)m.BurnTime;
			float num2;
			if (flag)
			{
				num2 = ((FlyingPuffs && age < num) ? 30 : 0);
			}
			else
			{
				num2 = ((smoulder > 0f && age - (float)m.LandTime < smoulder) ? (3.5f * (1f - (age - (float)m.LandTime) / smoulder) * Mathf.Clamp01((age - (float)m.LandTime) / 2.5f)) : 0f);
			}
			bool flag2 = !flag && smoulder > 0f && age - (float)m.LandTime < smoulder * 0.4f;
			if (flag2)
			{
				num2 += ((flames != null) ? 1.6f : 4f);
			}
			if (num2 <= 0f)
			{
				continue;
			}
			acc[i] += dt * num2;
			if (acc[i] < 1f)
			{
				continue;
			}
			acc[i]--;
			Vec3 vec = DebrisPlanner.Where(m, gravity, age);
			Vector3 vector = new Vector3((float)vec.X, (float)vec.Y, (float)vec.Z);
			float num3 = (float)Math.Max(1.0, Math.Min(5.0, m.Heat * 3.0 + 0.8));
			if (flag)
			{
				SmokeParticles.Emit(fire, vector, Vector3.up * 0.6f, num3 * 0.7f, 0.5f + 0.3f * (float)rnd.Next(), new Color(tint.r, tint.g, tint.b, 0.85f), (float)(rnd.Next() * 360.0));
				SmokeParticles.Emit(smoke, vector, Vector3.up * 0.4f, num3 * 1.3f, 5f + 3f * (float)rnd.Next(), new Color(tint.r, tint.g, tint.b, 0.42f), (float)(rnd.Next() * 360.0));
			}
			else if (flag2 && (flames != null || rnd.Next() < 0.55))
			{
				if (flames != null)
				{
					float num4 = 1f - (age - (float)m.LandTime) / (smoulder * 0.4f);
					Vector3 position = vector + new Vector3((float)(rnd.Next() - 0.5), 0f, (float)(rnd.Next() - 0.5)) * num3 * 0.3f;
					SmokeParticles.Emit(flames, position, Vector3.zero, num3 * (1f + 0.6f * (float)rnd.Next()) * (0.5f + 0.5f * num4), 1.4f + 0.8f * (float)rnd.Next(), new Color(1f, 1f, 1f, 0.65f + 0.35f * num4), 0f);
				}
				else
				{
					SmokeParticles.Emit(fire, vector + Vector3.up * num3 * 0.3f, Vector3.up * (0.8f + (float)rnd.Next()), num3 * 0.8f, 0.6f + 0.5f * (float)rnd.Next(), new Color(tint.r, tint.g, tint.b, 0.85f), (float)(rnd.Next() * 360.0));
				}
			}
			else
			{
				SmokeParticles.Emit(smoke, vector + Vector3.up * num3 * 0.4f, Vector3.up * (1.2f + (float)rnd.Next()), num3 * 1.4f, 5f + 3f * (float)rnd.Next(), new Color(tint.r, tint.g, tint.b, 0.4f), (float)(rnd.Next() * 360.0));
			}
		}
	}

	private void Landings(float age, ParticleSystem smoke)
	{
		if (firstTick < 0f)
		{
			firstTick = age;
		}
		for (int i = 0; i < motion.Length && i < landed.Length; i++)
		{
			if (landed[i])
			{
				continue;
			}
			double landTime = motion[i].LandTime;
			if (!double.IsInfinity(landTime) && !((double)age < landTime))
			{
				landed[i] = true;
				if (!(landTime < (double)firstTick - 0.2))
				{
					Vec3 vec = DebrisPlanner.Where(motion[i], gravity, landTime);
					float num = (wet ? 1.6f : 1.1f) + 1.1f * (float)rnd.Next();
					Vector3 vector = new Vector3((float)(rnd.Next() - 0.5), 0f, (float)(rnd.Next() - 0.5)) * 2.5f;
					SmokeParticles.Emit(smoke, new Vector3((float)vec.X, (float)vec.Y + num * 0.25f, (float)vec.Z), Vector3.up * (0.3f + 0.4f * (float)rnd.Next()) + vector, num, 1.4f + 1.2f * (float)rnd.Next(), new Color(dust.r, dust.g, dust.b, wet ? 0.32f : 0.3f), (float)(rnd.Next() * 360.0));
				}
			}
		}
	}
}
