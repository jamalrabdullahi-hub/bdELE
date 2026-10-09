using System;

namespace VolumetricExplosionFX.Core;

public sealed class EventClusterer
{
	private readonly EventCluster[] slots;

	private readonly double window;

	private readonly double radius;

	private int nextId;

	public int Dropped { get; private set; }

	public int Replaced { get; private set; }

	public int LastEvicted { get; private set; }

	public EventClusterer(int capacity = 24, double window = 0.08, double radius = 12.0)
	{
		if (capacity < 1 || window <= 0.0 || radius <= 0.0)
		{
			throw new ArgumentOutOfRangeException();
		}
		slots = new EventCluster[capacity];
		this.window = window;
		this.radius = radius;
	}

	public int Add(DestructionEvent e, double score)
	{
		int num = -1;
		int num2 = -1;
		LastEvicted = 0;
		for (int i = 0; i < slots.Length; i++)
		{
			EventCluster eventCluster = slots[i];
			if (eventCluster == null)
			{
				if (num < 0)
				{
					num = i;
				}
				continue;
			}
			if (num2 < 0 || eventCluster.RepresentativeScore < slots[num2].RepresentativeScore)
			{
				num2 = i;
			}
			if (eventCluster.Members < 128 && e.Time >= eventCluster.FirstTime && e.Time - eventCluster.FirstTime <= window && e.Part.Environment.Body == eventCluster.Representative.Part.Environment.Body && e.Kind == EventKind.WaterImpact == (eventCluster.Representative.Kind == EventKind.WaterImpact) && (e.Part.Position - eventCluster.Position).Length <= radius)
			{
				eventCluster.Position = (eventCluster.Position * eventCluster.Members + e.Part.Position) * (1.0 / (double)(eventCluster.Members + 1));
				eventCluster.Members++;
				eventCluster.LastTime = e.Time;
				eventCluster.TotalScore = Numbers.Clamp(eventCluster.TotalScore + score, 0.0, 100.0);
				if (score > eventCluster.RepresentativeScore)
				{
					eventCluster.Sample(eventCluster.Representative.Origin, eventCluster.Representative.Time, eventCluster.RepresentativeScore);
					eventCluster.Representative = e;
					eventCluster.RepresentativeScore = score;
				}
				else
				{
					eventCluster.Sample(e.Origin, e.Time, score);
				}
				eventCluster.Note(e, score);
				return eventCluster.Id;
			}
		}
		if (num < 0)
		{
			if (num2 < 0 || score <= slots[num2].RepresentativeScore)
			{
				Dropped++;
				return 0;
			}
			num = num2;
			Replaced++;
			LastEvicted = slots[num2].Id;
		}
		slots[num] = new EventCluster
		{
			Id = ++nextId,
			Members = 1,
			Representative = e,
			Position = e.Part.Position,
			FirstTime = e.Time,
			LastTime = e.Time,
			TotalScore = score,
			RepresentativeScore = score
		};
		slots[num].Note(e, score);
		return slots[num].Id;
	}

	public EventCluster TakeReady(double now)
	{
		int num = -1;
		for (int i = 0; i < slots.Length; i++)
		{
			if (slots[i] != null && now - slots[i].FirstTime >= window && (num < 0 || slots[i].RepresentativeScore > slots[num].RepresentativeScore))
			{
				num = i;
			}
		}
		if (num < 0)
		{
			return null;
		}
		EventCluster result = slots[num];
		slots[num] = null;
		return result;
	}

	public void Shift(Vec3 offset)
	{
		for (int i = 0; i < slots.Length; i++)
		{
			if (slots[i] != null)
			{
				slots[i].Position -= offset;
				slots[i].GroundPoint -= offset;
				for (int j = 0; j < slots[i].SampleCount; j++)
				{
					slots[i].SamplePositions[j] -= offset;
				}
			}
		}
	}

	public void Clear()
	{
		Array.Clear(slots, 0, slots.Length);
	}
}
