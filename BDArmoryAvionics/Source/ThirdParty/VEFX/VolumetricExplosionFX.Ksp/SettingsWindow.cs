using System;
using KSP.UI.Screens;
using UnityEngine;
using VolumetricExplosionFX.Core;
using VolumetricExplosionFX.Rendering;

namespace VolumetricExplosionFX.Ksp;

public abstract class SettingsWindow : MonoBehaviour
{
	private const string LockId = "VolumetricExplosionFX_settings";

	private ApplicationLauncherButton button;

	private Texture2D icon;

	private SettingsPanel panel;

	private UiText text;

	private Font fallbackFont;

	private bool locked;

	private bool dirty;

	private bool saveFailed;

	private float saveAt;

	private float savedAt = -100f;

	private float testedAt = -100f;

	private string testNote;

	private static int lastPage;

	protected void Start()
	{
		if (FlightController.StandaloneInstalled) { Destroy(this); return; }   // (ELE) the standalone mod has its own button
		GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
		GameEvents.onGUIApplicationLauncherUnreadifying.Add(OnUnreadifying);
		if (ApplicationLauncher.Ready)
		{
			AddButton();
		}
	}

	private void AddButton()
	{
		try
		{
			if (!(button != null) && !(ApplicationLauncher.Instance == null))
			{
				if (icon == null)
				{
					icon = UiArt.Icon(64);
				}
				button = ApplicationLauncher.Instance.AddModApplication((Callback)Open, (Callback)Hide, (Callback)null, (Callback)null, (Callback)null, (Callback)null, ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW, (Texture)icon);
			}
		}
		catch (Exception ex)
		{
			Debug.LogWarning("[VEFX] Settings button: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private void OnUnreadifying(GameScenes scene)
	{
		Hide();
		RemoveButton();
	}

	private void RemoveButton()
	{
		if (button != null && ApplicationLauncher.Instance != null)
		{
			ApplicationLauncher.Instance.RemoveModApplication(button);
		}
		button = null;
	}

	private void Open()
	{
		try
		{
			SyncLanguage();
			if (panel == null)
			{
				panel = new SettingsPanel(Model(), Font(), null, Scale());
			}
			panel.Page = lastPage;
			panel.Visible = true;
		}
		catch (Exception ex)
		{
			Debug.LogError("[VEFX] Settings window: " + ex);
		}
	}

	private void Hide()
	{
		if (panel != null)
		{
			panel.Visible = false;
		}
		if (dirty)
		{
			SaveNow();
		}
		Unlock();
	}

	private void CloseFromWindow()
	{
		if (button != null)
		{
			button.SetFalse();
		}
		else
		{
			Hide();
		}
	}

	private void SyncLanguage()
	{
		UiText current = GameText.Current;
		if (text != current)
		{
			text = current;
			if (panel != null)
			{
				bool visible = panel.Visible;
				lastPage = panel.Page;
				panel.Dispose();
				panel = null;
				Unlock();
				panel = new SettingsPanel(Model(), Font(), null, Scale());
				panel.Page = lastPage;
				panel.Visible = visible;
			}
		}
	}

	private UiModel Model()
	{
		return SettingsModel.Build(SettingsStore.Settings, new SettingsModel.Hooks
		{
			Version = "1.0.0",
			Text = text,
			Changed = () =>
			{
				SettingsStore.Touch();
				dirty = true;
				saveFailed = false;
				saveAt = Time.unscaledTime + 1f;
			},
			Close = CloseFromWindow,
			Reset = () =>
			{
				SettingsStore.Reset();
				dirty = true;
				saveFailed = false;
				saveAt = Time.unscaledTime + 0.2f;
			},
			CanTest = () => FlightController.CanTest,
			Test = (int kind) =>
			{
				bool flag = FlightController.Test(kind);
				testedAt = Time.unscaledTime;
				testNote = (flag ? "PreviewStarted" : "PreviewUnavailable");
				return flag;
			},
			Status = Status
		});
	}

	private string Status()
	{
		float unscaledTime = Time.unscaledTime;
		if (unscaledTime - testedAt < 3f && testNote != null)
		{
			if (!(testNote == "PreviewStarted"))
			{
				return text.Get("PreviewUnavailable", "Preview unavailable here");
			}
			return text.Get("PreviewStarted", "Preview started");
		}
		if (saveFailed)
		{
			return text.Get("SaveFailed", "Settings applied; unable to save");
		}
		if (dirty)
		{
			return text.Get("Saving", "Saving…");
		}
		if (unscaledTime - savedAt < 3f)
		{
			return text.Get("Saved", "Settings saved");
		}
		return text.Get("NextEffects", "Applied to new effects");
	}

	private Font Font()
	{
		Font font = ((HighLogic.Skin != null) ? HighLogic.Skin.font : null);
		if (font == null)
		{
			try
			{
				font = Resources.GetBuiltinResource<Font>("Arial.ttf");
			}
			catch (Exception)
			{
			}
		}
		if (font == null)
		{
			font = ((GUI.skin != null) ? GUI.skin.font : null);
		}
		string sample = text.Get("General", "General") + text.Get("Enabled", "Enable effects");
		if (HasGlyphs(font, sample))
		{
			return font;
		}
		try
		{
			if (fallbackFont == null)
			{
				fallbackFont = UnityEngine.Font.CreateDynamicFontFromOSFont(new string[10] { "Microsoft YaHei", "Meiryo", "Yu Gothic", "PingFang SC", "Hiragino Sans", "Noto Sans CJK SC", "Noto Sans CJK JP", "Arial Unicode MS", "DejaVu Sans", "Arial" }, 14);
			}
			if (HasGlyphs(fallbackFont, sample))
			{
				return fallbackFont;
			}
		}
		catch (Exception)
		{
		}
		return font;
	}

	private static bool HasGlyphs(Font font, string sample)
	{
		if (font == null)
		{
			return false;
		}
		if (font.dynamic)
		{
			font.RequestCharactersInTexture(sample, 14, FontStyle.Normal);
		}
		foreach (char c in sample)
		{
			if (!char.IsWhiteSpace(c) && !font.HasCharacter(c))
			{
				return false;
			}
		}
		return true;
	}

	private static float Scale()
	{
		return Mathf.Clamp(GameSettings.UI_SCALE, 0.5f, 2.5f);
	}

	protected void Update()
	{
		if (panel == null)
		{
			return;
		}
		try
		{
			SyncLanguage();
			if (panel == null)
			{
				return;
			}
			if (panel.Visible && Input.GetKeyDown(KeyCode.Escape))
			{
				CloseFromWindow();
				return;
			}
			panel.SetScale(Scale());
			panel.Tick(Time.unscaledDeltaTime);
			lastPage = panel.Page;
			bool flag = panel.Contains(Input.mousePosition);
			if (!flag && Input.GetMouseButtonDown(0))
			{
				panel.ReleaseFocus();
			}
			bool flag2 = flag || panel.HasKeyboardFocus;
			if (flag2 && !locked)
			{
				InputLockManager.SetControlLock(ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.CAMERACONTROLS, "VolumetricExplosionFX_settings");
				locked = true;
			}
			else if (!flag2 && locked)
			{
				Unlock();
			}
			if (dirty && !saveFailed && Time.unscaledTime >= saveAt)
			{
				SaveNow();
			}
		}
		catch (Exception ex)
		{
			Debug.LogError("[VEFX] Settings window: " + ex);
			if (panel != null)
			{
				panel.ReleaseFocus();
				panel.Visible = false;
			}
			Unlock();
		}
	}

	private void SaveNow()
	{
		if (SettingsStore.Save())
		{
			dirty = false;
			saveFailed = false;
			savedAt = Time.unscaledTime;
		}
		else
		{
			saveFailed = true;
		}
	}

	private void Unlock()
	{
		if (locked)
		{
			InputLockManager.RemoveControlLock("VolumetricExplosionFX_settings");
			locked = false;
		}
	}

	protected void OnDestroy()
	{
		GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
		GameEvents.onGUIApplicationLauncherUnreadifying.Remove(OnUnreadifying);
		RemoveButton();
		if (dirty)
		{
			SaveNow();
		}
		Unlock();
		if (panel != null)
		{
			panel.Dispose();
			panel = null;
		}
		if (fallbackFont != null)
		{
			UnityEngine.Object.Destroy(fallbackFont);
			fallbackFont = null;
		}
		if (icon != null)
		{
			UnityEngine.Object.Destroy(icon);
			icon = null;
		}
		UiArt.Release();
	}
}
