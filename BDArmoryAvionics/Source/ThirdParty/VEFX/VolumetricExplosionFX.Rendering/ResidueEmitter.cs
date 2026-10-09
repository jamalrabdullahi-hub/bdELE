using System;
using UnityEngine;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Rendering;

internal sealed class ResidueEmitter
{
	private ResiduePlan plan;

	private SplitRandom rnd;

	private float flames;

	private float smoke;

	private float phase;

	private float smokeStart;

	private Color tint;

	private Vector3 drift;

	internal bool Sheets;

	internal bool Volume;

	private float floor;

	internal bool Active { get; private set; }

	internal ResiduePlan Plan => plan;

	internal void Begin(ResiduePlan residue, FxPlan fx, uint seed, float groundY, Vector3 wind)
	{
		plan = residue;
		rnd = new SplitRandom(seed ^ 0x5EED);
		flames = 0f;
		smoke = 0f;
		floor = groundY;
		Active = residue.HasFire;
		double num = Numbers.Clamp(fx.Soot, 0.0, 1.0);
		tint = new Color((float)Numbers.Clamp(fx.SmokeR * (1.1 - 0.25 * num), 0.1, 1.0), (float)Numbers.Clamp(fx.SmokeG * (1.1 - 0.25 * num), 0.1, 1.0), (float)Numbers.Clamp(fx.SmokeB * (1.1 - 0.25 * num), 0.1, 1.0), 1f);
		drift = wind;
		phase = (float)(rnd.Next() * 6.283);
		smokeStart = (float)Residue.SmokeStart(fx);
	}

	internal void Stop()
	{
		Active = false;
	}

	internal void Tick(float age, float dt, ParticleSystem fire, ParticleSystem column, ParticleSystem tongues = null)
	{
		if (!Active)
		{
			return;
		}
		if ((double)age > plan.SmokeSeconds)
		{
			Active = false;
			return;
		}
		float num = (float)Residue.FireLevel(plan, age);
		float num2 = (float)plan.FireRadius;
		float num3 = 2f * num2;
		float num4 = (float)plan.FlameHeight * (0.55f + 0.45f * Mathf.Sqrt(num));
		float num5 = 1f + 0.55f * Mathf.Sin(age * ((float)Math.PI * 2f) * (float)plan.PuffHz + phase);
		flames += (Volume ? 0f : (dt * (float)plan.FlameRate * num * num5 * ((Sheets && tongues != null) ? 0.12f : 1f)));
		while (flames >= 1f)
		{
			flames--;
			if (Sheets && tongues != null)
			{
				double num6 = rnd.Next() * Math.PI * 2.0;
				double num7 = Math.Sqrt(rnd.Next()) * (double)num2 * 0.75;
				float num8 = 1f - 0.4f * (float)(num7 * num7 / Math.Max(num2 * num2, 0.001));
				float size = num4 * (0.6f + 0.45f * (float)rnd.Next()) * num8;
				Vector3 position = new Vector3((float)(Math.Cos(num6) * num7), floor, (float)(Math.Sin(num6) * num7));
				Vector3 velocity = new Vector3(0f - position.x, 0f, 0f - position.z) * 0.05f + drift * 0.1f;
				SmokeParticles.Emit(tongues, position, velocity, size, 1.6f + 1.2f * (float)rnd.Next(), new Color(1f, 1f, 1f, 0.75f + 0.25f * num), 0f);
				continue;
			}
			double num9 = rnd.Next() * Math.PI * 2.0;
			double num10 = Math.Sqrt(rnd.Next()) * (double)num2 * 0.85;
			float num11 = num3 * (0.5f + 0.35f * (float)rnd.Next()) * (0.55f + 0.45f * Mathf.Sqrt(num));
			float num12 = 1.1f + 0.7f * (float)rnd.Next();
			Vector3 vector = new Vector3((float)(Math.Cos(num9) * num10), floor + ((Sheets && tongues != null) ? (num4 * (0.2f + 0.25f * (float)rnd.Next())) : (num11 * 0.2f)), (float)(Math.Sin(num9) * num10));
			Vector3 vector2 = new Vector3(0f - vector.x, 0f, 0f - vector.z) * (0.35f / num12);
			float num13 = num4 / (num12 * 0.55f) * (0.7f + 0.5f * (float)rnd.Next()) * num5 * 0.7f;
			Vector3 velocity2 = new Vector3((float)(rnd.Next() - 0.5) * 0.08f * num13, num13, (float)(rnd.Next() - 0.5) * 0.08f * num13) + vector2 + drift * 0.3f;
			SmokeParticles.Emit(fire, vector, velocity2, num11, num12, new Color(tint.r, tint.g, tint.b, 0.92f), (float)(rnd.Next() * 360.0));
			int num14 = 0;
			while (tongues != null && !Sheets && num14 < ((!(rnd.Next() < 0.6)) ? 1 : 2))
			{
				float num15 = 0.5f + 0.45f * (float)rnd.Next();
				Vector3 vector3 = new Vector3((float)(rnd.Next() - 0.5), 0f, (float)(rnd.Next() - 0.5)) * num11 * 0.4f;
				Vector3 velocity3 = new Vector3((float)(rnd.Next() - 0.5) * 0.1f * num13, num4 / num15 * (0.6f + 0.5f * (float)rnd.Next()) * num5 * 0.55f, (float)(rnd.Next() - 0.5) * 0.1f * num13) + vector2 * 0.5f + drift * 0.2f;
				SmokeParticles.Emit(tongues, vector + vector3 + Vector3.up * num11 * 0.1f, velocity3, num3 * (0.28f + 0.22f * (float)rnd.Next()) * (0.6f + 0.4f * num), num15, new Color(1f, 0.6f + 0.28f * (float)rnd.Next(), 0.24f, 0.65f + 0.3f * num), (float)(rnd.Next() * 360.0));
				num14++;
			}
		}
		float num16 = (((double)age < plan.FireSeconds) ? 1f : Mathf.Clamp01(1f - (float)(((double)age - plan.FireSeconds) / Math.Max(plan.SmokeSeconds - plan.FireSeconds, 0.1))));
		float num17 = Mathf.Clamp01((age - smokeStart) / 4f);
		num17 = num17 * num17 * (3f - 2f * num17);
		float num18 = Mathf.Max(num, 0.35f * num16) * num17;
		smoke += dt * (float)plan.SmokeRate * num18;
		while (smoke >= 1f)
		{
			smoke--;
			double num19 = rnd.Next() * Math.PI * 2.0;
			double num20 = Math.Sqrt(rnd.Next()) * (double)num2 * 0.5;
			float size2 = num3 * (0.7f + 0.45f * (float)rnd.Next());
			Vector3 position2 = new Vector3((float)(Math.Cos(num19) * num20), floor + num4 * (0.75f + 0.3f * (float)rnd.Next()), (float)(Math.Sin(num19) * num20));
			Vector3 velocity4 = Vector3.up * (float)plan.PlumeSpeed * (0.7f + 0.5f * (float)rnd.Next()) + drift;
			float num21 = 0.9f + 0.18f * (float)rnd.Next();
			SmokeParticles.Emit(column, position2, velocity4, size2, 8f + 5f * (float)rnd.Next(), new Color(Mathf.Min(1f, tint.r * num21), Mathf.Min(1f, tint.g * num21), Mathf.Min(1f, tint.b * num21), 0.3f + 0.18f * num18), (float)(rnd.Next() * 360.0));
		}
	}
}
