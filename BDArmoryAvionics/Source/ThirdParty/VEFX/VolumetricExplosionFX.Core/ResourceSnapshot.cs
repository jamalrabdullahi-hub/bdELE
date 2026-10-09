namespace VolumetricExplosionFX.Core;

public sealed class ResourceSnapshot
{
	public readonly string Name;

	public readonly double Amount;

	public readonly double Capacity;

	public readonly double MassKg;

	public ResourceSnapshot(string name, double amount, double capacity, double massKg)
	{
		Name = name;
		Amount = Numbers.Positive(amount);
		Capacity = Numbers.Positive(capacity);
		MassKg = Numbers.Positive(massKg);
	}
}
