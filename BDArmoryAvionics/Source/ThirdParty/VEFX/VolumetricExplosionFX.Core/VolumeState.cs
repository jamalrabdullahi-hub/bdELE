namespace VolumetricExplosionFX.Core;

public struct VolumeState
{
	public double BoxRadius;

	public double Age;

	public double Atmosphere;

	public double Scale;

	public bool Ground;

	public bool Water;

	public double ShellRadius;

	public double ShellThickness;

	public double ShellOpacity;

	public double ShellErosion;

	public double Heat;

	public double CoreRadius;

	public double Glow;

	public double Cloud;

	public double Vapor;

	public double Dust;

	public double Rise;

	public double Fade;

	public double FireRadius;

	public double FireHeat;

	public double FireRise;

	public double FireSoot;

	public double FireBody;

	public double SmokeR;

	public double SmokeG;

	public double SmokeB;

	public double WhiteFlame;

	public double DustR;

	public double DustG;

	public double DustB;

	public LobeState Lobe0;

	public LobeState Lobe1;

	public LobeState Lobe2;

	public LobeState Lobe3;

	public LobeState Stem;

	public Vec3 BoundsCenter;

	public double BoundsRadius;

	public double NoiseRadius;

	public double Roughness;

	public double StemDensity;

	public double Pulse;

	public double Roll;

	public double ShockRadius;

	public double ShockStrength;

	public double Haze;

	public double HazeHeight;

	public bool Visible
	{
		get
		{
			if (Fade > 0.0)
			{
				if (!(Heat > 0.002) && !(ShellOpacity * (1.0 - ShellErosion) > 0.002) && !(Cloud > 0.002) && !(Dust > 0.002))
				{
					return FireBody > 0.002;
				}
				return true;
			}
			return false;
		}
	}

	public LobeState Lobe(int i)
	{
		return i switch
		{
			0 => Lobe0, 
			1 => Lobe1, 
			2 => Lobe2, 
			3 => Lobe3, 
			_ => Stem, 
		};
	}

	public void SetLobe(int i, LobeState l)
	{
		switch (i)
		{
		case 0:
			Lobe0 = l;
			break;
		case 1:
			Lobe1 = l;
			break;
		case 2:
			Lobe2 = l;
			break;
		case 3:
			Lobe3 = l;
			break;
		default:
			Stem = l;
			break;
		}
	}
}
