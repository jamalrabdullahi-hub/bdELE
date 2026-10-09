namespace VolumetricExplosionFX.Core;

public sealed class FxPlan
{
	public PartKind PartKind;

	public ResourceKind Contents;

	public EventKind Kind;

	public double KineticJoules;

	public double VisualEnergyScore;

	public double Radius;

	public double FireFraction;

	public double SmokeFraction;

	public double VaporFraction;

	public double ElectricFraction;

	public double ExpansionSpeed;

	public double Lifetime;

	public double Atmosphere;

	public double ImpactSpeed;

	public double BurnableKg;

	public double Soot;

	public double SmokeR = 0.5;

	public double SmokeG = 0.48;

	public double SmokeB = 0.45;

	public double WhiteFlame;

	public double DustR = 0.62;

	public double DustG = 0.56;

	public double DustB = 0.46;

	public int DebrisCount;

	public int PuffCount;
}
