namespace VolumetricExplosionFX.Core;

public sealed class RecentEvents
{
	private readonly int[] ids;

	private readonly double[] times;

	private int cursor;

	public RecentEvents(int capacity = 512)
	{
		ids = new int[capacity];
		times = new double[capacity];
		for (int i = 0; i < times.Length; i++)
		{
			times[i] = double.NegativeInfinity;
		}
	}

	public bool Accept(int instanceId, double now)
	{
		for (int i = 0; i < ids.Length; i++)
		{
			if (ids[i] == instanceId && now - times[i] < 2.0)
			{
				return false;
			}
		}
		ids[cursor] = instanceId;
		times[cursor] = now;
		cursor = (cursor + 1) % ids.Length;
		return true;
	}
}
