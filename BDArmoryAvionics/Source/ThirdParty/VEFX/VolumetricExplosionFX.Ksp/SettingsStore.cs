using System;
using System.IO;
using UnityEngine;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Ksp;

internal static class SettingsStore
{
	private static FxSettings current;

	internal static int Version { get; private set; }

	internal static FxSettings Settings
	{
		get
		{
			if (current == null)
			{
				current = SettingsReader.Read();
			}
			return current;
		}
	}

	private static string Folder => Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmoryAvionics/VEFX/PluginData");

	private static string UserFile => Path.Combine(Folder, "settings.cfg");

	internal static ConfigNode LoadUser()
	{
		try
		{
			if (!File.Exists(UserFile))
			{
				return null;
			}
			ConfigNode configNode = ConfigNode.Load(UserFile);
			if (configNode == null)
			{
				return null;
			}
			return (configNode.GetNode("VOLUMETRIC_EXPLOSION_FX") ?? configNode.GetNode("PROJECT_DESTRUCTION_FX")) ?? configNode;
		}
		catch (Exception ex)
		{
			Debug.LogWarning("[VEFX] Settings file ignored: " + ex.GetType().Name);
			return null;
		}
	}

	internal static void Touch()
	{
		Version++;
	}

	internal static bool Save()
	{
		string text = UserFile + ".tmp";
		try
		{
			Directory.CreateDirectory(Folder);
			ConfigNode configNode = new ConfigNode();
			configNode.AddNode(SettingsReader.Write(Settings));
			if (!configNode.Save(text))
			{
				throw new IOException("ConfigNode could not write the settings file.");
			}
			if (File.Exists(UserFile))
			{
				File.Replace(text, UserFile, UserFile + ".bak");
			}
			else
			{
				File.Move(text, UserFile);
			}
			return true;
		}
		catch (Exception ex)
		{
			Debug.LogWarning("[VEFX] Settings not saved: " + ex.GetType().Name + ": " + ex.Message);
			return false;
		}
		finally
		{
			try
			{
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}

	internal static void Reset()
	{
		Settings.CopyFrom(SettingsReader.Defaults());
		Version++;
	}
}
