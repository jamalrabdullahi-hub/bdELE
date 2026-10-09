using System;
using System.Globalization;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Ksp;

internal static class SettingsReader
{
	internal const string Node = "VOLUMETRIC_EXPLOSION_FX";

	internal const string OldNode = "PROJECT_DESTRUCTION_FX";

	internal static FxSettings Read()
	{
		FxSettings fxSettings = Defaults();
		ConfigNode configNode = SettingsStore.LoadUser();
		if (configNode != null)
		{
			Apply(configNode, fxSettings);
		}
		fxSettings.Validate();
		return fxSettings;
	}

	internal static FxSettings Defaults()
	{
		FxSettings fxSettings = new FxSettings();
		ConfigNode[] configNodes = GameDatabase.Instance.GetConfigNodes("VOLUMETRIC_EXPLOSION_FX");
		if (configNodes.Length != 0)
		{
			Apply(configNodes[configNodes.Length - 1], fxSettings);
		}
		fxSettings.Validate();
		return fxSettings;
	}

	internal static void Resources(ResourceClassifier resources)
	{
		string[] array = new string[2] { "VOLUMETRIC_EXPLOSION_RESOURCE", "PROJECT_DESTRUCTION_RESOURCE" };
		foreach (string typeName in array)
		{
			ConfigNode[] configNodes = GameDatabase.Instance.GetConfigNodes(typeName);
			foreach (ConfigNode configNode in configNodes)
			{
				if (Enum.TryParse<ResourceKind>(configNode.GetValue("kind"), ignoreCase: true, out var result))
				{
					resources.Register(configNode.GetValue("name"), result);
				}
			}
		}
	}

	internal static void Apply(ConfigNode n, FxSettings s)
	{
		if (Enum.TryParse<Quality>(n.GetValue("quality"), ignoreCase: true, out var result))
		{
			s.ApplyPreset(result);
		}
		s.Enabled = Flag(n, "enabled", s.Enabled);
		s.Explosions = Flag(n, "explosions", s.Explosions);
		s.Debris = Flag(n, "microDebris", s.Debris);
		s.Ground = Flag(n, "groundImpact", s.Ground);
		s.Water = Flag(n, "waterImpact", s.Water);
		s.Burst = Flag(n, "burst", s.Burst);
		s.Lighting = Flag(n, "dynamicLighting", s.Lighting);
		s.Debug = Flag(n, "debug", s.Debug);
		s.Volumetric = Flag(n, "volumetric", s.Volumetric);
		s.ReplaceStockVisuals = Flag(n, "replaceStockVisuals", s.ReplaceStockVisuals);
		s.Shockwave = Flag(n, "shockwave", s.Shockwave);
		s.PartDebris = Flag(n, "partDebris", s.PartDebris);
		s.Residue = Flag(n, "residue", s.Residue);
		s.ResidueSeconds = Number(n, "residueSeconds", s.ResidueSeconds);
		s.Distance = Number(n, "fxDistance", s.Distance);
		s.Intensity = Number(n, "intensity", s.Intensity);
		s.DebrisDensity = Number(n, "debrisDensity", s.DebrisDensity);
		if (s.Quality == Quality.Custom)
		{
			s.MaxEvents = (int)Number(n, "maximumEvents", s.MaxEvents);
			s.MaxDebris = (int)Number(n, "maximumDebris", s.MaxDebris);
			s.MaxLights = (int)Number(n, "maximumLights", s.MaxLights);
			s.ParticlesPerEvent = (int)Number(n, "particlesPerEvent", s.ParticlesPerEvent);
			s.MaxVolumes = (int)Number(n, "maximumVolumes", s.MaxVolumes);
			s.VolumeSteps = (int)Number(n, "volumeSteps", s.VolumeSteps);
			s.MaxDebrisParts = (int)Number(n, "maximumDebrisParts", s.MaxDebrisParts);
			s.DebrisTriangles = (int)Number(n, "debrisTrianglesPerPart", s.DebrisTriangles);
		}
		s.Validate();
	}

	internal static ConfigNode Write(FxSettings s)
	{
		ConfigNode configNode = new ConfigNode("VOLUMETRIC_EXPLOSION_FX");
		configNode.AddValue("quality", s.Quality.ToString());
		Add(configNode, "enabled", s.Enabled);
		Add(configNode, "explosions", s.Explosions);
		Add(configNode, "microDebris", s.Debris);
		Add(configNode, "groundImpact", s.Ground);
		Add(configNode, "waterImpact", s.Water);
		Add(configNode, "burst", s.Burst);
		Add(configNode, "dynamicLighting", s.Lighting);
		Add(configNode, "volumetric", s.Volumetric);
		Add(configNode, "replaceStockVisuals", s.ReplaceStockVisuals);
		Add(configNode, "shockwave", s.Shockwave);
		Add(configNode, "partDebris", s.PartDebris);
		Add(configNode, "residue", s.Residue);
		Add(configNode, "residueSeconds", s.ResidueSeconds);
		Add(configNode, "fxDistance", s.Distance);
		Add(configNode, "intensity", s.Intensity);
		Add(configNode, "debrisDensity", s.DebrisDensity);
		Add(configNode, "debug", s.Debug);
		Add(configNode, "maximumEvents", s.MaxEvents);
		Add(configNode, "maximumDebris", s.MaxDebris);
		Add(configNode, "maximumLights", s.MaxLights);
		Add(configNode, "particlesPerEvent", s.ParticlesPerEvent);
		Add(configNode, "maximumVolumes", s.MaxVolumes);
		Add(configNode, "volumeSteps", s.VolumeSteps);
		Add(configNode, "maximumDebrisParts", s.MaxDebrisParts);
		Add(configNode, "debrisTrianglesPerPart", s.DebrisTriangles);
		return configNode;
	}

	private static void Add(ConfigNode n, string key, bool v)
	{
		n.AddValue(key, v ? "true" : "false");
	}

	private static void Add(ConfigNode n, string key, double v)
	{
		n.AddValue(key, v.ToString("R", CultureInfo.InvariantCulture));
	}

	private static bool Flag(ConfigNode n, string key, bool fallback)
	{
		if (!bool.TryParse(n.GetValue(key), out var result))
		{
			return fallback;
		}
		return result;
	}

	private static double Number(ConfigNode n, string key, double fallback)
	{
		if (!double.TryParse(n.GetValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			return fallback;
		}
		return result;
	}
}
