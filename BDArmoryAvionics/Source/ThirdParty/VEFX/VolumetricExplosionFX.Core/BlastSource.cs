namespace VolumetricExplosionFX.Core;

public struct BlastSource(Vec3 offset, double delay, double weight)
{
	public Vec3 Offset = offset;

	public double Delay = delay;

	public double Weight = weight;
}
