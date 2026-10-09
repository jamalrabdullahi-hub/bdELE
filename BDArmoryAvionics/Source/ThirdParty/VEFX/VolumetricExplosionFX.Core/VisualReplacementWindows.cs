using System;

namespace VolumetricExplosionFX.Core;

public sealed class VisualReplacementWindows
{
	private struct Window
	{
		public Vec3 Position;

		public double Until;

		public double Radius;

		public int Owner;
	}

	public const int Covered = -1;

	private readonly Window[] windows;

	private int cursor;

	public VisualReplacementWindows(int capacity = 64)
	{
		if (capacity < 1)
		{
			throw new ArgumentOutOfRangeException();
		}
		windows = new Window[capacity];
	}

	public void Claim(Vec3 position, double now, double radius, int owner)
	{
		if (owner != 0)
		{
			windows[cursor] = new Window
			{
				Position = position,
				Until = now + 0.5,
				Radius = Numbers.Clamp(radius, 2.0, 60.0),
				Owner = owner
			};
			cursor = (cursor + 1) % windows.Length;
		}
	}

	public int OwnerAt(Vec3 position, double now)
	{
		for (int i = 0; i < windows.Length; i++)
		{
			if (windows[i].Owner != 0 && windows[i].Until >= now && (position - windows[i].Position).Length <= windows[i].Radius)
			{
				return windows[i].Owner;
			}
		}
		return 0;
	}

	public double NearestDistance(Vec3 position, double now)
	{
		double num = -1.0;
		for (int i = 0; i < windows.Length; i++)
		{
			if (windows[i].Owner != 0 && windows[i].Until >= now)
			{
				double length = (position - windows[i].Position).Length;
				if (num < 0.0 || length < num)
				{
					num = length;
				}
			}
		}
		return num;
	}

	public void Release(int owner)
	{
		if (owner == 0)
		{
			return;
		}
		for (int i = 0; i < windows.Length; i++)
		{
			if (windows[i].Owner == owner)
			{
				windows[i].Owner = 0;
			}
		}
	}

	public bool Any(double now)
	{
		for (int i = 0; i < windows.Length; i++)
		{
			if (windows[i].Owner != 0 && windows[i].Until >= now)
			{
				return true;
			}
		}
		return false;
	}

	public void Shift(Vec3 offset)
	{
		for (int i = 0; i < windows.Length; i++)
		{
			windows[i].Position -= offset;
		}
	}

	public void Clear()
	{
		Array.Clear(windows, 0, windows.Length);
	}
}
