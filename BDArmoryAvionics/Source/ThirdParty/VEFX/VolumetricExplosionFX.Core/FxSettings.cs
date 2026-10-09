namespace VolumetricExplosionFX.Core;

public sealed class FxSettings
{
	public bool Enabled = true;

	public bool Explosions = true;

	public bool Debris = true;

	public bool Ground = true;

	public bool Water = true;

	public bool Burst = true;

	public bool Lighting = true;

	public bool Debug;

	public bool Volumetric = true;

	public bool ReplaceStockVisuals = true;

	public bool Shockwave = true;

	public bool Breakup;

	public bool PartDebris = true;

	public bool Residue = true;

	public Quality Quality = Quality.Ultra;

	public int MaxEvents = 24;

	public int MaxDebris = 3072;

	public int MaxLights = 6;

	public int ParticlesPerEvent = 256;

	public int MaxVolumes = 8;

	public int VolumeSteps = 48;

	public int MaxDebrisParts = 32;

	public int DebrisTriangles = 12000;

	public double ResidueSeconds = 45.0;

	public double Distance = 10000.0;

	public double Intensity = 1.0;

	public double DebrisDensity = 1.0;

	public string Structure => Volumetric + "|" + MaxEvents + "|" + ParticlesPerEvent + "|" + MaxDebrisParts;

	public void ApplyPreset(Quality quality)
	{
		Quality = quality;
		switch (quality)
		{
		case Quality.Low:
			MaxEvents = 6;
			MaxDebris = 256;
			MaxLights = 1;
			ParticlesPerEvent = 64;
			MaxVolumes = 2;
			VolumeSteps = 24;
			MaxDebrisParts = 6;
			DebrisTriangles = 2400;
			break;
		case Quality.Medium:
			MaxEvents = 12;
			MaxDebris = 768;
			MaxLights = 3;
			ParticlesPerEvent = 128;
			MaxVolumes = 4;
			VolumeSteps = 32;
			MaxDebrisParts = 16;
			DebrisTriangles = 4500;
			break;
		case Quality.High:
			MaxEvents = 20;
			MaxDebris = 1536;
			MaxLights = 4;
			ParticlesPerEvent = 192;
			MaxVolumes = 6;
			VolumeSteps = 40;
			MaxDebrisParts = 24;
			DebrisTriangles = 7500;
			break;
		case Quality.Ultra:
			MaxEvents = 24;
			MaxDebris = 3072;
			MaxLights = 6;
			ParticlesPerEvent = 256;
			MaxVolumes = 8;
			VolumeSteps = 48;
			MaxDebrisParts = 32;
			DebrisTriangles = 12000;
			break;
		}
		Validate();
	}

	public void CopyFrom(FxSettings o)
	{
		Enabled = o.Enabled;
		Explosions = o.Explosions;
		Debris = o.Debris;
		Ground = o.Ground;
		Water = o.Water;
		Burst = o.Burst;
		Lighting = o.Lighting;
		Debug = o.Debug;
		Volumetric = o.Volumetric;
		ReplaceStockVisuals = o.ReplaceStockVisuals;
		Shockwave = o.Shockwave;
		Breakup = o.Breakup;
		PartDebris = o.PartDebris;
		Residue = o.Residue;
		Quality = o.Quality;
		MaxEvents = o.MaxEvents;
		MaxDebris = o.MaxDebris;
		MaxLights = o.MaxLights;
		ParticlesPerEvent = o.ParticlesPerEvent;
		MaxVolumes = o.MaxVolumes;
		VolumeSteps = o.VolumeSteps;
		MaxDebrisParts = o.MaxDebrisParts;
		DebrisTriangles = o.DebrisTriangles;
		ResidueSeconds = o.ResidueSeconds;
		Distance = o.Distance;
		Intensity = o.Intensity;
		DebrisDensity = o.DebrisDensity;
	}

	public void Validate()
	{
		MaxEvents = (int)Numbers.Clamp(MaxEvents, 1.0, 24.0);
		MaxDebris = (int)Numbers.Clamp(MaxDebris, 0.0, 4096.0);
		MaxLights = (int)Numbers.Clamp(MaxLights, 0.0, 6.0);
		ParticlesPerEvent = (int)Numbers.Clamp(ParticlesPerEvent, 16.0, 256.0);
		Distance = Numbers.Clamp(Distance, 100.0, 30000.0);
		Intensity = Numbers.Clamp(Intensity, 0.25, 2.0);
		DebrisDensity = Numbers.Clamp(DebrisDensity, 0.0, 2.0);
		MaxVolumes = (int)Numbers.Clamp(MaxVolumes, 0.0, 8.0);
		VolumeSteps = (int)Numbers.Clamp(VolumeSteps, 16.0, 56.0);
		MaxDebrisParts = (int)Numbers.Clamp(MaxDebrisParts, 0.0, 48.0);
		DebrisTriangles = (int)Numbers.Clamp(DebrisTriangles, 300.0, 16000.0);
		ResidueSeconds = Numbers.Clamp(ResidueSeconds, 0.0, 180.0);
	}
}
