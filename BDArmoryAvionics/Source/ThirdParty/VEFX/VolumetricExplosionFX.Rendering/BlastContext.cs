using UnityEngine;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Rendering;

internal struct BlastContext
{
	internal FxPlan Plan;

	internal BlastLayout Layout;

	internal FxSettings Settings;

	internal EventKind Kind;

	internal bool Ground;

	internal bool Water;

	internal bool Surface;

	internal bool Dome;

	internal float Air;

	internal float DomeRadius;

	internal float Gravity;

	internal float ImpactSpeed;

	internal Vector3 Velocity;

	internal float? Floor;

	internal float Sunlit;

	internal double GasRate;

	internal bool FloorWet;

	internal int DebrisBudget;

	internal uint Seed;
}
