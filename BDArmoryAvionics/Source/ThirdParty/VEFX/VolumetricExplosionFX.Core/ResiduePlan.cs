namespace VolumetricExplosionFX.Core;

public struct ResiduePlan
{
	public double FireSeconds;

	public double FireRadius;

	public double SmokeSeconds;

	public double PlumeSpeed;

	public double FlameRate;

	public double SmokeRate;

	public double FlameHeight;

	public double PuffHz;

	public double ScorchRadius;

	public double ScorchSeconds;

	public double ScorchDarkness;

	public bool HasFire => FireSeconds > 0.0;
}
