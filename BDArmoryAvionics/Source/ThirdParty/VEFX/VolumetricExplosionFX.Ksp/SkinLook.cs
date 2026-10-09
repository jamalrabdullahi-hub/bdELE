using UnityEngine;

namespace VolumetricExplosionFX.Ksp;

internal struct SkinLook
{
	internal Texture Texture;

	internal Color Color;

	internal Vector2 Scale;

	internal Vector2 Offset;

	internal bool Same(SkinLook o)
	{
		if (Texture == o.Texture && Color == o.Color && Scale == o.Scale)
		{
			return Offset == o.Offset;
		}
		return false;
	}
}
