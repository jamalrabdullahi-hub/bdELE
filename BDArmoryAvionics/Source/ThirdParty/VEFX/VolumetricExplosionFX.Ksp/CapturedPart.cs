using UnityEngine;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Ksp;

internal sealed class CapturedPart
{
	internal PartMesh Mesh;

	internal SkinLook[] Looks;

	internal Vector3 Origin;

	internal Quaternion Frame;

	internal Vec3 Center;

	internal double Size;
}
