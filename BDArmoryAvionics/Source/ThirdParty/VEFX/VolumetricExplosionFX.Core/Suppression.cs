using System;
using System.Collections.Generic;

namespace VolumetricExplosionFX.Core;

public sealed class Suppression<T> where T : class
{
	private readonly List<T> items;

	public int Count => items.Count;

	public Suppression(int capacity = 8)
	{
		items = new List<T>(capacity);
	}

	public bool Hide(T item, bool on)
	{
		if (item == null || !on)
		{
			return false;
		}
		if (!items.Contains(item))
		{
			items.Add(item);
		}
		return true;
	}

	public bool Holds(T item)
	{
		if (item != null)
		{
			return items.Contains(item);
		}
		return false;
	}

	public int Restore(Action<T> restore)
	{
		int count = items.Count;
		for (int i = 0; i < count; i++)
		{
			restore(items[i]);
		}
		items.Clear();
		return count;
	}

	public void Clear()
	{
		items.Clear();
	}
}
