using System;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Rendering;

internal static class SettingsModel
{
	internal sealed class Hooks
	{
		internal string Version;

		internal UiText Text;

		internal Action Changed;

		internal Action Close;

		internal Action Reset;

		internal Func<bool> CanTest;

		internal Func<int, bool> Test;

		internal Func<string> Status;
	}

	private static int testKind;

	internal static UiModel Build(FxSettings s, Hooks h)
	{
		UiText text = h.Text ?? new UiText("en-us");
		UiModel uiModel = new UiModel
		{
			Title = "Volumetric Explosion FX",
			Subtitle = "VEFX  ·  " + L("Settings", "Settings") + "  ·  " + h.Version,
			Reset = L("Defaults", "Defaults"),
			Close = h.Close,
			ResetAll = h.Reset,
			Changed = h.Changed,
			Status = h.Status
		};
		UiPage uiPage = Page(uiModel, L("General", "General"), UiGlyph.General);
		Toggle(uiPage, L("Enabled", "Enable effects"), L("EnabledHint", "Explosions, flames and visual fragments."), () => s.Enabled, (bool v) =>
		{
			s.Enabled = v;
		});
		uiPage.Rows.Add(new UiRow
		{
			Kind = UiRowKind.Choice,
			Label = L("Quality", "Quality"),
			Hint = L("QualityHint", "Preset rendering limits."),
			Options = new string[5]
			{
				L("Low", "Low"),
				L("Medium", "Medium"),
				L("High", "High"),
				L("Ultra", "Ultra"),
				L("Custom", "Custom")
			},
			GetChoice = () => (int)s.Quality,
			SetChoice = (int i) =>
			{
				if (i < 4)
				{
					s.ApplyPreset((Quality)i);
				}
				else
				{
					s.Quality = Quality.Custom;
				}
			}
		});
		Slider(uiPage, L("Intensity", "Intensity"), L("IntensityHint", "1.00: reference setting."), 0.25f, 2f, 0.05f, () => (float)s.Intensity, (float v) =>
		{
			s.Intensity = v;
		}, (float v) => v.ToString("0.00", text.Culture) + " ×");
		Toggle(uiPage, L("ReplaceStock", "Replace KSP explosions"), L("ReplaceStockHint", "Hides stock visuals when VEFX replaces them. Keeps their sound."), () => s.ReplaceStockVisuals, (bool v) =>
		{
			s.ReplaceStockVisuals = v;
		});
		Slider(uiPage, L("SpawnDistance", "Spawn distance"), L("SpawnDistanceHint", "Existing effects remain visible."), 0.1f, 30f, 0.1f, () => (float)(s.Distance / 1000.0), (float v) =>
		{
			s.Distance = v * 1000f;
		}, (float v) => v.ToString("0.#", text.Culture) + " km");
		Section(uiPage, L("Preview", "In-flight preview"));
		uiPage.Rows.Add(new UiRow
		{
			Kind = UiRowKind.Action,
			Label = L("TestExplosion", "Test explosion"),
			Hint = L("TestHint", "Preview in front of the camera, without damage. Big crash: eight explosions, with performance recorded in KSP.log."),
			Options = new string[5]
			{
				L("Tank", "Tank"),
				L("Stage", "Stage"),
				L("Booster", "Booster"),
				L("InFlight", "In flight"),
				L("BigCrash", "Big crash")
			},
			GetChoice = () => testKind,
			SetChoice = (int i) =>
			{
				testKind = i;
			},
			Button = L("Test", "Test"),
			Click = () =>
			{
				if (h.Test != null)
				{
					h.Test(testKind);
				}
			},
			Available = h.CanTest
		});
		UiPage p = Page(uiModel, L("Explosions", "Explosions"), UiGlyph.Blast);
		Section(p, L("Fireballs", "Fireballs"));
		Toggle(p, L("Explosions", "Explosions"), L("ExplosionsHint", "Fire and smoke based on the destroyed part contents."), () => s.Explosions, (bool v) =>
		{
			s.Explosions = v;
		});
		Toggle(p, L("Volumetric", "Volumetric rendering"), L("VolumetricHint", "3D volumes for explosions and ground fires. Higher GPU cost."), () => s.Volumetric, (bool v) =>
		{
			s.Volumetric = v;
		});
		Toggle(p, L("Shockwave", "Shockwave and distortion"), L("ShockwaveHint", "Shockwaves and heat distort the image around the explosion."), () => s.Shockwave, (bool v) =>
		{
			s.Shockwave = v;
		});
		Toggle(p, L("Lighting", "Dynamic lighting"), L("LightingHint", "Explosions and fires light the ground, rockets and smoke."), () => s.Lighting, (bool v) =>
		{
			s.Lighting = v;
		});
		Section(p, L("Impacts", "Impacts"));
		Toggle(p, L("Ground", "Ground crashes"), L("GroundHint", "Thrown soil and dust, with a blast along the ground."), () => s.Ground, (bool v) =>
		{
			s.Ground = v;
		});
		Toggle(p, L("Water", "Water impacts"), L("WaterHint", "Water column, spray and a foam ring."), () => s.Water, (bool v) =>
		{
			s.Water = v;
		});
		Toggle(p, L("Burst", "Bursts and vapour"), L("BurstHint", "Pressurized tanks, vapour clouds and electrical sparks."), () => s.Burst, (bool v) =>
		{
			s.Burst = v;
		});
		UiPage uiPage2 = Page(uiModel, L("FireSmoke", "Fire & smoke"), UiGlyph.Flame);
		Toggle(uiPage2, L("Residue", "Ground fire and smoke"), L("ResidueHint", "After a fuelled crash: fire, smoke and ground marks."), () => s.Residue, (bool v) =>
		{
			s.Residue = v;
		});
		Slider(uiPage2, L("ResidueDuration", "Residue duration"), L("ResidueDurationHint", "Ground fires, smoke and fragments."), 0f, 180f, 5f, () => (float)s.ResidueSeconds, (float v) =>
		{
			s.ResidueSeconds = v;
		}, (float v) => (!(v < 1f)) ? (v.ToString("0", text.Culture) + " s") : L("None", "None"));
		uiPage2.Rows.Add(new UiRow
		{
			Kind = UiRowKind.Info,
			Label = L("FlameRendering", "Flame rendering"),
			Text = () => L("FlameRenderingHint", "Volumetric rendering is set under Explosions. Without a compatible shader, VEFX uses its fallback renderer.")
		});
		UiPage p2 = Page(uiModel, L("Wreckage", "Wreckage"), UiGlyph.Wreck);
		Toggle(p2, L("PartFragments", "Part fragments"), L("PartFragmentsHint", "Visual pieces from the destroyed part model."), () => s.PartDebris, (bool v) =>
		{
			s.PartDebris = v;
		});
		Slider(p2, L("FragmentDetail", "Fragment detail"), L("FragmentDetailHint", "Triangles per destroyed part."), 300f, 16000f, 100f, () => s.DebrisTriangles, (float v) =>
		{
			s.Quality = Quality.Custom;
			s.DebrisTriangles = (int)v;
		}, (float v) => (v / 1000f).ToString("0.0", text.Culture) + " k");
		Slider(p2, L("RetainedParts", "Retained parts"), L("RetainedPartsHint", "The oldest parts are replaced."), 0f, 48f, 1f, () => s.MaxDebrisParts, (float v) =>
		{
			s.Quality = Quality.Custom;
			s.MaxDebrisParts = (int)v;
		}, (float v) => v.ToString("0", text.Culture));
		Toggle(p2, L("MicroDebris", "Micro debris"), L("MicroDebrisHint", "Shards and small pieces thrown by the explosion."), () => s.Debris, (bool v) =>
		{
			s.Debris = v;
		});
		Slider(p2, L("DebrisDensity", "Debris density"), L("DebrisDensityHint", "Amount per explosion."), 0f, 2f, 0.05f, () => (float)s.DebrisDensity, (float v) =>
		{
			s.DebrisDensity = v;
		}, (float v) => v.ToString("0.00", text.Culture) + " ×");
		UiPage uiPage3 = Page(uiModel, L("Performance", "Performance"), UiGlyph.Gauge);
		uiPage3.Rows.Add(new UiRow
		{
			Kind = UiRowKind.Info,
			Label = L("Limits", "Limits"),
			Text = () => text.Format("LimitsHelp", "Changing a limit selects Custom quality. Preset: {0}. Changing capacity clears active effects.", QualityName(s.Quality, text))
		});
		Limit(uiPage3, s, text, L("MaxEvents", "Simultaneous explosions"), L("MaxEventsHint", "Beyond this limit, weaker effects give way to stronger ones."), 1, 24, 1, () => s.MaxEvents, (int v) =>
		{
			s.MaxEvents = v;
		});
		Limit(uiPage3, s, text, L("MaxVolumes", "Explosion volumes"), L("MaxVolumesHint", "Ground fires are counted separately."), 0, 8, 1, () => s.MaxVolumes, (int v) =>
		{
			s.MaxVolumes = v;
		});
		Limit(uiPage3, s, text, L("VolumeSteps", "Volume detail"), L("VolumeStepsHint", "More steps increase detail and GPU cost."), 16, 56, 4, () => s.VolumeSteps, (int v) =>
		{
			s.VolumeSteps = v;
		});
		Limit(uiPage3, s, text, L("Particles", "Particles per explosion"), L("ParticlesHint", "Sparks, flames and smoke in one explosion."), 16, 256, 16, () => s.ParticlesPerEvent, (int v) =>
		{
			s.ParticlesPerEvent = v;
		});
		Limit(uiPage3, s, text, L("MaxLights", "Dynamic lights"), L("MaxLightsHint", "Explosion lights active at the same time."), 0, 6, 1, () => s.MaxLights, (int v) =>
		{
			s.MaxLights = v;
		});
		Limit(uiPage3, s, text, L("MaxDebris", "Total micro debris"), L("MaxDebrisHint", "All flying shards at once."), 0, 4096, 64, () => s.MaxDebris, (int v) =>
		{
			s.MaxDebris = v;
		});
		return uiModel;
		string L(string key, string fallback)
		{
			return text.Get(key, fallback);
		}
	}

	private static string QualityName(Quality q, UiText text)
	{
		return q switch
		{
			Quality.Low => text.Get("Low", "Low"), 
			Quality.Medium => text.Get("Medium", "Medium"), 
			Quality.High => text.Get("High", "High"), 
			Quality.Ultra => text.Get("Ultra", "Ultra"), 
			_ => text.Get("Custom", "Custom"), 
		};
	}

	private static UiPage Page(UiModel m, string title, UiGlyph glyph)
	{
		UiPage uiPage = new UiPage
		{
			Title = title,
			Glyph = glyph
		};
		m.Pages.Add(uiPage);
		return uiPage;
	}

	private static void Section(UiPage p, string title)
	{
		p.Rows.Add(new UiRow
		{
			Kind = UiRowKind.Section,
			Label = title
		});
	}

	private static void Toggle(UiPage p, string label, string hint, Func<bool> get, Action<bool> set)
	{
		p.Rows.Add(new UiRow
		{
			Kind = UiRowKind.Toggle,
			Label = label,
			Hint = hint,
			GetBool = get,
			SetBool = set
		});
	}

	private static void Slider(UiPage p, string label, string hint, float min, float max, float step, Func<float> get, Action<float> set, Func<float, string> format)
	{
		p.Rows.Add(new UiRow
		{
			Kind = UiRowKind.Slider,
			Label = label,
			Hint = hint,
			Min = min,
			Max = max,
			Step = step,
			GetValue = get,
			SetValue = set,
			Format = format
		});
	}

	private static void Limit(UiPage p, FxSettings s, UiText text, string label, string hint, int min, int max, int step, Func<int> get, Action<int> set)
	{
		Slider(p, label, hint, min, max, step, () => get(), (float v) =>
		{
			s.Quality = Quality.Custom;
			set((int)Math.Round(v));
			s.Validate();
		}, (float v) => v.ToString("0", text.Culture));
	}
}
