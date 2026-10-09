using System;

namespace VolumetricExplosionFX.Core;

public sealed class FrameStats
{
	private readonly double[] samples;

	private readonly double[] sorted;

	private int count;

	private int next;

	public int Count => count;

	public double Median => Percentile(0.5);

	public double P95 => Percentile(0.95);

	public double Worst => Percentile(1.0);

	public FrameStats(int capacity)
	{
		samples = new double[Math.Max(1, capacity)];
		sorted = new double[samples.Length];
	}

	public void Add(double ms)
	{
		if (!double.IsNaN(ms) && !double.IsInfinity(ms))
		{
			samples[next] = ms;
			next = (next + 1) % samples.Length;
			if (count < samples.Length)
			{
				count++;
			}
		}
	}

	public void Clear()
	{
		count = 0;
		next = 0;
	}

	public double Percentile(double q)
	{
		if (count == 0)
		{
			return 0.0;
		}
		Array.Copy(samples, sorted, count);
		Array.Sort(sorted, 0, count);
		int val = (int)Math.Ceiling(Numbers.Clamp(q, 0.0, 1.0) * (double)count) - 1;
		return sorted[Math.Max(0, Math.Min(count - 1, val))];
	}
}
