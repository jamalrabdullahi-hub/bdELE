using System;
using System.Collections.Generic;

namespace VolumetricExplosionFX.Core;

public sealed class ResourceClassifier
{
	private readonly Dictionary<string, ResourceKind> profiles = new Dictionary<string, ResourceKind>(StringComparer.OrdinalIgnoreCase);

	public ResourceClassifier()
	{
		Register("LiquidFuel", ResourceKind.Fuel | ResourceKind.Sooty);
		Register("Oxidizer", ResourceKind.Oxidizer);
		Register("MonoPropellant", ResourceKind.Hypergolic | ResourceKind.Pressurized | ResourceKind.Clean);
		Register("SolidFuel", ResourceKind.Solid);
		Register("ElectricCharge", ResourceKind.Electric);
		Register("LqdOxygen", ResourceKind.Oxidizer | ResourceKind.Cryogenic);
		Register("LOX", ResourceKind.Oxidizer | ResourceKind.Cryogenic);
		Register("Kerosene", ResourceKind.Fuel | ResourceKind.Sooty);
		Register("RP-1", ResourceKind.Fuel | ResourceKind.Sooty);
		Register("LqdHydrogen", ResourceKind.Fuel | ResourceKind.Cryogenic | ResourceKind.Clean);
		Register("LH2", ResourceKind.Fuel | ResourceKind.Cryogenic | ResourceKind.Clean);
		Register("LqdMethane", ResourceKind.Fuel | ResourceKind.Cryogenic);
		Register("Methane", ResourceKind.Fuel);
		Register("MMH", ResourceKind.Fuel | ResourceKind.Hypergolic);
		Register("UDMH", ResourceKind.Fuel | ResourceKind.Hypergolic);
		Register("NTO", ResourceKind.Oxidizer | ResourceKind.Hypergolic);
		Register("Nitrogen", ResourceKind.Pressurized);
		Register("XenonGas", ResourceKind.Pressurized);
		Register("ArgonGas", ResourceKind.Pressurized);
		Register("Helium", ResourceKind.Pressurized);
	}

	public void Register(string name, ResourceKind kind)
	{
		if (!string.IsNullOrEmpty(name))
		{
			profiles[name] = kind;
		}
	}

	public ResourceKind Lookup(string name)
	{
		if (name == null || !profiles.TryGetValue(name, out var value))
		{
			return ResourceKind.None;
		}
		return value;
	}

	public ResourceKind Contents(PartSnapshot p)
	{
		ResourceKind resourceKind = ResourceKind.None;
		for (int i = 0; i < p.Resources.Length; i++)
		{
			if (p.Resources[i].Amount > 0.0)
			{
				resourceKind |= Lookup(p.Resources[i].Name);
			}
		}
		return resourceKind;
	}

	public PartKind Classify(PartSnapshot p)
	{
		ResourceKind resourceKind = ResourceKind.None;
		for (int i = 0; i < p.Resources.Length; i++)
		{
			if (p.Resources[i].Capacity > 0.0)
			{
				resourceKind |= Lookup(p.Resources[i].Name);
			}
		}
		if ((resourceKind & ResourceKind.Solid) != 0)
		{
			return PartKind.SolidBooster;
		}
		for (int j = 0; j < p.Modules.Length; j++)
		{
			switch (p.Modules[j])
			{
			case "ModuleEngines":
			case "ModuleEnginesFX":
			case "ModuleEnginesRF":
				return PartKind.Engine;
			case "ModuleCommand":
				return PartKind.Capsule;
			case "ModuleLiftingSurface":
			case "ModuleControlSurface":
				return PartKind.Wing;
			case "ModuleProceduralFairing":
				return PartKind.Fairing;
			}
		}
		if ((resourceKind & (ResourceKind.Fuel | ResourceKind.Oxidizer | ResourceKind.Cryogenic | ResourceKind.Hypergolic)) != 0)
		{
			return PartKind.LiquidTank;
		}
		if ((resourceKind & ResourceKind.Pressurized) != 0)
		{
			return PartKind.PressurizedTank;
		}
		if ((resourceKind & ResourceKind.Electric) != 0)
		{
			return PartKind.Battery;
		}
		return PartKind.Structure;
	}
}
