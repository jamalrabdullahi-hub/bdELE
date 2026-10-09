namespace VolumetricExplosionFX.Core;

public struct ShowerPlan
{
	public ShowerKind Kind;

	public int Count;

	public double SpeedMin;

	public double SpeedMax;

	public double LifeMin;

	public double LifeMax;

	public double Drag;

	public double UpBias;

	public double HeadSize;

	public double TrailWidth;

	public double Gravity;

	public bool Trails;

	public double TrailLife;

	public double TrailStep;

	public Vec3 Axis;

	public double Cone;

	public double MinUp;

	public int Clumps;

	public double ClumpShare;
}
