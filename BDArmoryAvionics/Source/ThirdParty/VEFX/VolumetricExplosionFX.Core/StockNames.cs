using System;

namespace VolumetricExplosionFX.Core;

public static class StockNames
{
	public static bool IsInstanceOf(string name, string prefab)
	{
		if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(prefab))
		{
			return false;
		}
		if (name == prefab)
		{
			return true;
		}
		if (name.Length == prefab.Length + 7 && name.StartsWith(prefab, StringComparison.Ordinal))
		{
			return name.EndsWith("(Clone)", StringComparison.Ordinal);
		}
		return false;
	}
}
