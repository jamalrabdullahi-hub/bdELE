using System;

namespace VolumetricExplosionFX.Core;

public struct EffectLoad
{
	public int Blasts;

	public int FireballVolumes;

	public int FireVolumes;

	public int Particles;

	public int Wrecks;

	public int WreckPieces;

	public int Lights;

	public bool Busy
	{
		get
		{
			if (Blasts <= 0)
			{
				return Wrecks > 0;
			}
			return true;
		}
	}

	public void Peak(EffectLoad now)
	{
		Blasts = Math.Max(Blasts, now.Blasts);
		FireballVolumes = Math.Max(FireballVolumes, now.FireballVolumes);
		FireVolumes = Math.Max(FireVolumes, now.FireVolumes);
		Particles = Math.Max(Particles, now.Particles);
		Wrecks = Math.Max(Wrecks, now.Wrecks);
		WreckPieces = Math.Max(WreckPieces, now.WreckPieces);
		Lights = Math.Max(Lights, now.Lights);
	}
}
