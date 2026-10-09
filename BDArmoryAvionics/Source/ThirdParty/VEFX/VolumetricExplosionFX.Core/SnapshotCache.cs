using System;

namespace VolumetricExplosionFX.Core;

public sealed class SnapshotCache
{
	private sealed class Entry
	{
		internal PartSnapshot Snapshot;

		internal EventKind Kind;

		internal Vec3 Position;

		internal double Time;
	}

	private readonly Entry[] slots;

	private int cursor;

	public SnapshotCache(int capacity = 256)
	{
		if (capacity < 1)
		{
			throw new ArgumentOutOfRangeException();
		}
		slots = new Entry[capacity];
	}

	private Entry Find(int id, double now)
	{
		for (int i = 0; i < slots.Length; i++)
		{
			if (slots[i] != null && slots[i].Snapshot.InstanceId == id && now - slots[i].Time >= 0.0 && now - slots[i].Time < 2.0)
			{
				return slots[i];
			}
		}
		return null;
	}

	public void Capture(PartSnapshot snapshot, EventKind kind, Vec3 position, double now)
	{
		if (snapshot != null)
		{
			Entry entry = Find(snapshot.InstanceId, now);
			if (entry == null)
			{
				entry = new Entry();
				slots[cursor] = entry;
				cursor = (cursor + 1) % slots.Length;
			}
			if (entry.Snapshot == null || entry.Kind == EventKind.Destruction || kind != EventKind.Destruction)
			{
				entry.Snapshot = snapshot;
			}
			if (kind != EventKind.Destruction || entry.Kind == EventKind.Destruction)
			{
				entry.Kind = kind;
				entry.Position = position;
			}
			entry.Time = now;
		}
	}

	public DestructionEvent Confirm(int id, double now, PartSnapshot fallback)
	{
		Entry entry = Find(id, now);
		if (entry == null)
		{
			if (fallback != null)
			{
				return new DestructionEvent(fallback, EventKind.Destruction, now);
			}
			return null;
		}
		PartSnapshot snapshot = entry.Snapshot;
		PartSnapshot part = new PartSnapshot(snapshot.PersistentId, snapshot.InstanceId, snapshot.VesselId, snapshot.Name, entry.Position, snapshot.SurfaceVelocity, snapshot.InertialVelocity, snapshot.AngularVelocity, snapshot.Dimensions, snapshot.MassKg, snapshot.Temperature, snapshot.Modules, snapshot.Resources, snapshot.Environment, snapshot.Rotation);
		entry.Time = double.NegativeInfinity;
		return new DestructionEvent(part, entry.Kind, now);
	}

	public void Shift(Vec3 offset)
	{
		for (int i = 0; i < slots.Length; i++)
		{
			if (slots[i] != null)
			{
				slots[i].Position -= offset;
			}
		}
	}
}
