namespace VolumetricExplosionFX.Core;

public sealed class DestructionEvent
{
	public readonly PartSnapshot Part;

	public readonly EventKind Kind;

	public readonly double Time;

	public readonly Vec3 Origin;

	public DestructionEvent(PartSnapshot part, EventKind kind, double time)
	{
		Part = part;
		Kind = kind;
		Time = time;
		Origin = part.Position;
	}

	public DestructionEvent(PartSnapshot part, EventKind kind, double time, Vec3 origin)
	{
		Part = part;
		Kind = kind;
		Time = time;
		Origin = origin;
	}
}
