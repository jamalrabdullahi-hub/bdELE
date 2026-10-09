using System;

namespace VolumetricExplosionFX.Core;

[Flags]
public enum ResourceKind
{
	None = 0,
	Fuel = 1,
	Oxidizer = 2,
	Cryogenic = 4,
	Solid = 8,
	Hypergolic = 0x10,
	Electric = 0x20,
	Pressurized = 0x40,
	Sooty = 0x80,
	Clean = 0x100
}
