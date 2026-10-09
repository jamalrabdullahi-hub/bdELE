using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VolumetricExplosionFX.Rendering;

internal sealed class SettingsPanel : IDisposable
{
	private static readonly Color Ink = new Color(0.86f, 0.87f, 0.86f);

	private static readonly Color Soft = new Color(0.6f, 0.63f, 0.64f);

	private static readonly Color Accent = new Color(0.85f, 0.7f, 0.4f);

	private static readonly Color Face = new Color(0.23f, 0.25f, 0.26f);

	private static readonly Color Rule = new Color(0.3f, 0.32f, 0.33f);

	private static readonly Color Selected = new Color(0.34f, 0.32f, 0.26f);

	internal const float Width = 620f;

	internal const float Height = 530f;

	private const float Pad = 16f;

	private const float Controls = 282f;

	private readonly UiModel model;

	private readonly Font font;

	private readonly Camera camera;

	private readonly CanvasScaler scaler;

	private readonly List<RectTransform> pages = new List<RectTransform>();

	private readonly List<Button> tabs = new List<Button>();

	private readonly List<GameObject> tabMarks = new List<GameObject>();

	private readonly List<List<Action>> updates = new List<List<Action>>();

	private readonly ScrollRect scroll;

	private readonly Text status;

	private readonly Image scrollTrack;

	private readonly RectTransform scrollThumb;

	internal readonly GameObject Root;

	internal readonly RectTransform Window;

	internal static Vector2 Position;

	private int page;

	private int screenW = -1;

	private int screenH = -1;

	private float preferredScale;

	private float appliedScale;

	private bool visible;

	internal bool Visible
	{
		get
		{
			return visible;
		}
		set
		{
			if (!value)
			{
				ReleaseFocus();
			}
			visible = value;
			Root.SetActive(value);
			if (value)
			{
				Fit();
				Refresh();
			}
		}
	}

	internal bool Shown => Root.activeSelf;

	internal int Page
	{
		get
		{
			return page;
		}
		set
		{
			Select(Mathf.Clamp(value, 0, pages.Count - 1));
		}
	}

	internal bool HasKeyboardFocus
	{
		get
		{
			GameObject gameObject = ((EventSystem.current != null) ? EventSystem.current.currentSelectedGameObject : null);
			if (visible && gameObject != null)
			{
				return gameObject.transform.IsChildOf(Root.transform);
			}
			return false;
		}
	}

	internal SettingsPanel(UiModel model, Font font, Camera camera, float scale)
	{
		SettingsPanel settingsPanel = this;
		this.model = model;
		this.font = font;
		this.camera = camera;
		preferredScale = scale;
		Root = new GameObject("VolumetricExplosionFX settings", typeof(RectTransform));
		Canvas canvas = Root.AddComponent<Canvas>();
		canvas.renderMode = ((camera != null) ? RenderMode.ScreenSpaceCamera : RenderMode.ScreenSpaceOverlay);
		if (camera != null)
		{
			canvas.worldCamera = camera;
			canvas.planeDistance = 1f;
		}
		canvas.sortingOrder = 60;
		scaler = Root.AddComponent<CanvasScaler>();
		scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
		scaler.referencePixelsPerUnit = 100f;
		Root.AddComponent<GraphicRaycaster>();
		Window = Rect("window", Root.transform);
		Window.anchorMin = (Window.anchorMax = (Window.pivot = new Vector2(0.5f, 0.5f)));
		Window.sizeDelta = new Vector2(620f, 530f);
		Window.anchoredPosition = Position;
		Stretch(Img("border", Window, UiArt.Fill(5), Rule).rectTransform, 0f, 0f, 0f, 0f);
		Stretch(Img("body", Window, UiArt.Fill(4), new Color(0.15f, 0.17f, 0.18f, 0.98f)).rectTransform, 1f, 1f, 1f, 1f);
		Image image = Img("title bar", Window, UiArt.Fill(4), new Color(0.2f, 0.22f, 0.23f));
		Top(image.rectTransform, 1f, 44f, 1f, 1f);
		image.raycastTarget = true;
		image.gameObject.AddComponent<UiWindowDrag>().Move = (Vector2 delta) =>
		{
			settingsPanel.Window.anchoredPosition += delta / Mathf.Max(settingsPanel.appliedScale, 0.01f);
			settingsPanel.ClampPosition();
		};
		At(Label("title", image.transform, model.Title, 17, FontStyle.Normal, Ink).rectTransform, 16f, -12f, 350f, 24f);
		Text text = Label("version", image.transform, model.Subtitle, 11, FontStyle.Normal, Soft);
		text.alignment = TextAnchor.MiddleRight;
		At(text.rectTransform, 355f, -12f, 207f, 24f);
		At(Button("close", image.transform, "×", () =>
		{
			if (model.Close != null)
			{
				model.Close();
			}
		}).GetComponent<RectTransform>(), 578f, -9f, 26f, 26f);
		RectTransform rectTransform = Rect("tabs", Window);
		Top(rectTransform, 51f, 34f, 16f, 16f);
		for (int num = 0; num < model.Pages.Count; num++)
		{
			int index = num;
			Button button = Button("tab " + model.Pages[num].Title, rectTransform, model.Pages[num].Title, () =>
			{
				settingsPanel.Select(index);
			});
			RectTransform component = button.GetComponent<RectTransform>();
			component.anchorMin = new Vector2((float)num / (float)model.Pages.Count, 0f);
			component.anchorMax = new Vector2((float)(num + 1) / (float)model.Pages.Count, 1f);
			component.offsetMin = new Vector2(0f, 0f);
			component.offsetMax = new Vector2(-3f, 0f);
			Image image2 = Img("selection", component, null, Accent);
			Bottom(image2.rectTransform, 0f, 2f, 4f, 4f);
			tabs.Add(button);
			tabMarks.Add(image2.gameObject);
		}
		RectTransform rectTransform2 = Rect("viewport", Window);
		Stretch(rectTransform2, 16f, 46f, 28f, 99f);
		rectTransform2.gameObject.AddComponent<RectMask2D>();
		rectTransform2.gameObject.AddComponent<Image>().color = Color.clear;
		scroll = rectTransform2.gameObject.AddComponent<ScrollRect>();
		scroll.horizontal = false;
		scroll.vertical = true;
		scroll.movementType = ScrollRect.MovementType.Clamped;
		scroll.scrollSensitivity = 26f;
		scroll.viewport = rectTransform2;
		for (int num2 = 0; num2 < model.Pages.Count; num2++)
		{
			BuildPage(model.Pages[num2], rectTransform2);
		}
		scrollTrack = Img("scroll track", Window, null, Face);
		RectTransform rectTransform3 = scrollTrack.rectTransform;
		rectTransform3.anchorMin = new Vector2(1f, 0f);
		rectTransform3.anchorMax = new Vector2(1f, 1f);
		rectTransform3.offsetMin = new Vector2(-21f, 49f);
		rectTransform3.offsetMax = new Vector2(-17f, -102f);
		scrollThumb = Img("scroll thumb", rectTransform3, null, Soft).rectTransform;
		scrollThumb.anchorMin = new Vector2(0f, 1f);
		scrollThumb.anchorMax = new Vector2(1f, 1f);
		scrollThumb.pivot = new Vector2(0.5f, 1f);
		Bottom(Img("footer rule", Window, null, Rule).rectTransform, 39f, 1f, 16f, 16f);
		if (model.Reset != null)
		{
			RectTransform component2 = Button("reset", Window, model.Reset, () =>
			{
				if (model.ResetAll != null)
				{
					model.ResetAll();
				}
				settingsPanel.Changed();
			}).GetComponent<RectTransform>();
			component2.anchorMin = (component2.anchorMax = (component2.pivot = Vector2.zero));
			component2.anchoredPosition = new Vector2(16f, 8f);
			component2.sizeDelta = new Vector2(112f, 25f);
		}
		status = Label("status", Window, "", 11, FontStyle.Normal, Soft);
		status.alignment = TextAnchor.MiddleRight;
		RectTransform rectTransform4 = status.rectTransform;
		rectTransform4.anchorMin = (rectTransform4.anchorMax = (rectTransform4.pivot = new Vector2(1f, 0f)));
		rectTransform4.sizeDelta = new Vector2(460f, 30f);
		rectTransform4.anchoredPosition = new Vector2(-16f, 6f);
		Select(0);
		SetLayer(Root.transform, 5);
		Fit();
		Root.SetActive(value: false);
	}

	internal void SetScale(float scale)
	{
		if (!(Mathf.Abs(preferredScale - scale) < 0.001f))
		{
			preferredScale = scale;
			screenW = -1;
			Fit();
		}
	}

	private void Fit()
	{
		int num = ((camera != null) ? camera.pixelWidth : Screen.width);
		int num2 = ((camera != null) ? camera.pixelHeight : Screen.height);
		if (num != screenW || num2 != screenH)
		{
			screenW = num;
			screenH = num2;
			appliedScale = Mathf.Max(0.1f, Mathf.Min(Mathf.Clamp(preferredScale, 0.5f, 2.5f), Mathf.Min((float)(num - 24) / 620f, (float)(num2 - 24) / 530f)));
			scaler.scaleFactor = appliedScale;
			ClampPosition();
		}
	}

	private void ClampPosition()
	{
		float num = Mathf.Max(0f, (float)(screenW - 24) / appliedScale * 0.5f - 310f);
		float num2 = Mathf.Max(0f, (float)(screenH - 24) / appliedScale * 0.5f - 265f);
		Vector2 anchoredPosition = Window.anchoredPosition;
		anchoredPosition.x = Mathf.Clamp(anchoredPosition.x, 0f - num, num);
		anchoredPosition.y = Mathf.Clamp(anchoredPosition.y, 0f - num2, num2);
		Window.anchoredPosition = anchoredPosition;
		Position = anchoredPosition;
	}

	private void Changed()
	{
		if (model.Changed != null)
		{
			model.Changed();
		}
		Refresh();
	}

	private void Select(int index)
	{
		page = index;
		for (int i = 0; i < pages.Count; i++)
		{
			pages[i].gameObject.SetActive(i == page);
			tabMarks[i].SetActive(i == page);
			Colours(tabs[i], (i == page) ? Selected : Color.clear);
		}
		if (page < pages.Count)
		{
			scroll.content = pages[page];
			pages[page].anchoredPosition = Vector2.zero;
		}
		scroll.velocity = Vector2.zero;
		Refresh();
	}

	private void Refresh()
	{
		if (page < updates.Count)
		{
			foreach (Action item in updates[page])
			{
				item();
			}
		}
		if (status != null && model.Status != null)
		{
			TextIfChanged(status, model.Status());
		}
	}

	internal void Tick(float dt)
	{
		if (visible)
		{
			Fit();
			Refresh();
			RectTransform content = scroll.content;
			float height = scroll.viewport.rect.height;
			float num = ((content != null) ? content.sizeDelta.y : 0f);
			bool flag = num > height + 1f;
			scrollTrack.enabled = flag;
			scrollThumb.gameObject.SetActive(flag);
			if (flag)
			{
				float height2 = scrollTrack.rectTransform.rect.height;
				float num2 = Mathf.Max(24f, height2 * height / num);
				float num3 = Mathf.Clamp01(content.anchoredPosition.y / (num - height));
				scrollThumb.sizeDelta = new Vector2(0f, num2);
				scrollThumb.anchoredPosition = new Vector2(0f, (0f - (height2 - num2)) * num3);
			}
		}
	}

	private void BuildPage(UiPage p, RectTransform view)
	{
		RectTransform rectTransform = Rect("page " + p.Title, view);
		rectTransform.anchorMin = new Vector2(0f, 1f);
		rectTransform.anchorMax = new Vector2(1f, 1f);
		rectTransform.pivot = new Vector2(0.5f, 1f);
		List<Action> list = new List<Action>();
		updates.Add(list);
		float num = 2f;
		foreach (UiRow row in p.Rows)
		{
			num += Row(row, rectTransform, num, list) + 3f;
		}
		rectTransform.sizeDelta = new Vector2(0f, num + 4f);
		pages.Add(rectTransform);
	}

	private float Row(UiRow row, RectTransform parent, float y, List<Action> refresh)
	{
		if (row.Kind == UiRowKind.Section)
		{
			Top(Label("section", parent, row.Label, 12, FontStyle.Bold, Soft).rectTransform, y + 8f, 18f, 0f, 0f);
			return 27f;
		}
		float num = 576f;
		float num2 = num - 282f;
		RectTransform rectTransform = Rect("row " + row.Label, parent);
		float num3;
		if (row.Kind == UiRowKind.Action || row.Kind == UiRowKind.Info)
		{
			num3 = num;
		}
		else
		{
			num3 = ((row.Kind == UiRowKind.Toggle) ? (num - 40f) : (num2 - 12f));
		}
		Text text = Label("label", rectTransform, row.Label, 13, FontStyle.Normal, Ink);
		text.horizontalOverflow = HorizontalWrapMode.Wrap;
		At(text.rectTransform, 0f, -7f, num3, 20f);
		float num4 = Mathf.Max(20f, Mathf.Ceil(text.preferredHeight));
		text.rectTransform.sizeDelta = new Vector2(num3, num4);
		float num5 = 7f + num4;
		float num6 = 0f;
		if (!string.IsNullOrEmpty(row.Hint))
		{
			Text text2 = Label("hint", rectTransform, row.Hint, 10, FontStyle.Normal, Soft);
			text2.horizontalOverflow = HorizontalWrapMode.Wrap;
			At(text2.rectTransform, 0f, 0f - num5, num3, 16f);
			num6 = Mathf.Ceil(text2.preferredHeight);
			text2.rectTransform.sizeDelta = new Vector2(num3, num6);
		}
		float num7 = Mathf.Max(44f, num5 + num6 + 8f);
		List<Selectable> selectables = new List<Selectable>();
		switch (row.Kind)
		{
		case UiRowKind.Toggle:
		{
			Image image = Img("checkbox", rectTransform, UiArt.Fill(2), Face);
			At(image.rectTransform, num - 25f, -10f, 22f, 22f);
			image.raycastTarget = true;
			Stretch(Img("rim", image.transform, UiArt.Fill(2), Rule).rectTransform, 0f, 0f, 0f, 0f);
			Stretch(Img("inner", image.transform, UiArt.Fill(1), Face).rectTransform, 1f, 1f, 1f, 1f);
			Image image2 = Img("check", image.transform, UiArt.Check(), Accent);
			Stretch(image2.rectTransform, 2f, 2f, 2f, 2f);
			Toggle toggle = image.gameObject.AddComponent<Toggle>();
			toggle.targetGraphic = image;
			toggle.graphic = image2;
			toggle.toggleTransition = Toggle.ToggleTransition.None;
			toggle.SetIsOnWithoutNotify(row.GetBool());
			toggle.onValueChanged.AddListener((bool v) =>
			{
				if (toggle.IsActive() && Available(row) && v != row.GetBool())
				{
					row.SetBool(v);
					Changed();
				}
			});
			refresh.Add(() =>
			{
				toggle.SetIsOnWithoutNotify(row.GetBool());
			});
			selectables.Add(toggle);
			break;
		}
		case UiRowKind.Slider:
			Slider(row, rectTransform, num2, num, refresh, selectables);
			break;
		case UiRowKind.Choice:
			Choices(row, rectTransform, num2, 8f, 282f, refresh, selectables);
			break;
		case UiRowKind.Action:
		{
			float num10 = num5 + num6 + 8f;
			Choices(row, rectTransform, 0f, num10, num - 92f, refresh, selectables);
			Button button = Button("action", rectTransform, row.Button, () =>
			{
				if (Available(row) && row.Click != null)
				{
					row.Click();
				}
			});
			At(button.GetComponent<RectTransform>(), num - 82f, 0f - num10, 82f, 27f);
			selectables.Add(button);
			num7 = num10 + 35f;
			break;
		}
		default:
		{
			Text text3 = Label("info", rectTransform, (row.Text != null) ? row.Text() : "", 11, FontStyle.Normal, Soft);
			text3.horizontalOverflow = HorizontalWrapMode.Wrap;
			float num8 = num5 + num6;
			At(text3.rectTransform, 0f, 0f - num8, num, 16f);
			float num9 = Mathf.Ceil(text3.preferredHeight) + 4f;
			text3.rectTransform.sizeDelta = new Vector2(num, num9);
			num7 = num8 + num9 + 8f;
			refresh.Add(() =>
			{
				if (row.Text != null)
				{
					TextIfChanged(text3, row.Text());
				}
			});
			break;
		}
		}
		Top(rectTransform, y, num7, 0f, 0f);
		Bottom(Img("divider", rectTransform, null, new Color(Rule.r, Rule.g, Rule.b, 0.45f)).rectTransform, 0f, 1f, 0f, 0f);
		CanvasGroup fade = rectTransform.gameObject.AddComponent<CanvasGroup>();
		bool? oldAvailable = null;
		refresh.Add(() =>
		{
			bool flag = Available(row);
			if (oldAvailable == flag)
			{
				return;
			}
			oldAvailable = flag;
			fade.alpha = (flag ? 1f : 0.45f);
			foreach (Selectable item in selectables)
			{
				item.interactable = flag;
			}
		});
		return num7;
	}

	private static bool Available(UiRow row)
	{
		if (row.Available != null)
		{
			return row.Available();
		}
		return true;
	}

	private void Slider(UiRow row, RectTransform band, float left, float width, List<Action> refresh, List<Selectable> selectables)
	{
		RectTransform rectTransform = Rect("slider", band);
		At(rectTransform, left, -8f, 217f, 28f);
		rectTransform.gameObject.AddComponent<Image>().color = Color.clear;
		Stretch(Img("track", rectTransform, UiArt.Fill(1), Face).rectTransform, 5f, 12f, 5f, 12f);
		RectTransform rectTransform2 = Rect("fill area", rectTransform);
		Stretch(rectTransform2, 5f, 12f, 5f, 12f);
		Image image = Img("fill", rectTransform2, UiArt.Fill(1), Accent);
		Stretch(image.rectTransform, 0f, 0f, 0f, 0f);
		RectTransform rectTransform3 = Rect("handle area", rectTransform);
		rectTransform3.anchorMin = new Vector2(0f, 0.5f);
		rectTransform3.anchorMax = new Vector2(1f, 0.5f);
		rectTransform3.offsetMin = new Vector2(5f, -8f);
		rectTransform3.offsetMax = new Vector2(-5f, 8f);
		Image image2 = Img("handle", rectTransform3, UiArt.Fill(2), Ink);
		image2.raycastTarget = true;
		RectTransform rectTransform4 = image2.rectTransform;
		Vector2 anchorMin = (image2.rectTransform.anchorMax = new Vector2(0f, 0.5f));
		rectTransform4.anchorMin = anchorMin;
		image2.rectTransform.sizeDelta = new Vector2(10f, 0f);
		Slider slider = rectTransform.gameObject.AddComponent<Slider>();
		slider.minValue = row.Min;
		slider.maxValue = row.Max;
		slider.fillRect = image.rectTransform;
		slider.handleRect = image2.rectTransform;
		slider.targetGraphic = image2;
		slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
		slider.SetValueWithoutNotify(row.GetValue());
		slider.onValueChanged.AddListener((float v) =>
		{
			if (slider.IsActive() && Available(row))
			{
				if (row.Step > 0f)
				{
					v = row.Min + Mathf.Round((v - row.Min) / row.Step) * row.Step;
				}
				v = Mathf.Clamp(v, row.Min, row.Max);
				if (Mathf.Abs(v - row.GetValue()) > 0.0001f)
				{
					row.SetValue(v);
					Changed();
				}
			}
		});
		Text text = Label("value", band, "", 12, FontStyle.Normal, Ink);
		text.alignment = TextAnchor.MiddleRight;
		At(text.rectTransform, width - 61f, -8f, 61f, 28f);
		float last = float.NaN;
		refresh.Add(() =>
		{
			float num = row.GetValue();
			if (num != last)
			{
				last = num;
				slider.SetValueWithoutNotify(num);
				TextIfChanged(text, (row.Format != null) ? row.Format(num) : num.ToString("0.##"));
			}
		});
		selectables.Add(slider);
	}

	private void Choices(UiRow row, RectTransform band, float left, float top, float width, List<Action> refresh, List<Selectable> selectables)
	{
		if (row.Options == null || row.Options.Length == 0)
		{
			return;
		}
		Button[] buttons = new Button[row.Options.Length];
		float num = width / (float)buttons.Length;
		for (int i = 0; i < buttons.Length; i++)
		{
			int index = i;
			Button button = Button("option " + row.Options[i], band, row.Options[i], () =>
			{
				if (Available(row) && row.GetChoice() != index)
				{
					row.SetChoice(index);
					Changed();
				}
			});
			At(button.GetComponent<RectTransform>(), left + (float)i * num, 0f - top, num - 3f, 27f);
			buttons[i] = button;
			selectables.Add(button);
		}
		int last = int.MinValue;
		refresh.Add(() =>
		{
			int num2 = row.GetChoice();
			if (num2 != last)
			{
				last = num2;
				for (int j = 0; j < buttons.Length; j++)
				{
					Colours(buttons[j], (j == num2) ? Selected : Face);
					buttons[j].GetComponentInChildren<Text>().color = ((j == num2) ? Accent : Ink);
				}
			}
		});
	}

	private Button Button(string name, Transform parent, string text, Action action)
	{
		Image image = Img(name, parent, UiArt.Fill(2), Color.white);
		image.raycastTarget = true;
		Button button = image.gameObject.AddComponent<Button>();
		button.targetGraphic = image;
		Colours(button, Face);
		Text text2 = Label("label", image.transform, text, 11, FontStyle.Normal, Ink);
		text2.alignment = TextAnchor.MiddleCenter;
		Stretch(text2.rectTransform, 3f, 0f, 3f, 0f);
		button.onClick.AddListener(() =>
		{
			action();
		});
		return button;
	}

	private static void Colours(Selectable s, Color normal)
	{
		ColorBlock colors = s.colors;
		colors.normalColor = normal;
		colors.highlightedColor = new Color(0.34f, 0.36f, 0.37f);
		colors.selectedColor = colors.highlightedColor;
		colors.pressedColor = new Color(0.4f, 0.42f, 0.43f);
		colors.disabledColor = normal;
		colors.colorMultiplier = 1f;
		colors.fadeDuration = 0.06f;
		s.colors = colors;
	}

	private static void TextIfChanged(Text t, string value)
	{
		if (t.text != value)
		{
			t.text = value ?? "";
		}
	}

	private static void SetLayer(Transform t, int layer)
	{
		t.gameObject.layer = layer;
		for (int i = 0; i < t.childCount; i++)
		{
			SetLayer(t.GetChild(i), layer);
		}
	}

	private static RectTransform Rect(string name, Transform parent)
	{
		RectTransform rectTransform = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
		rectTransform.SetParent(parent, worldPositionStays: false);
		return rectTransform;
	}

	private static Image Img(string name, Transform parent, Sprite sprite, Color colour)
	{
		Image image = Rect(name, parent).gameObject.AddComponent<Image>();
		image.sprite = sprite;
		image.type = ((sprite != null && sprite.border != Vector4.zero) ? Image.Type.Sliced : Image.Type.Simple);
		image.color = colour;
		image.raycastTarget = false;
		return image;
	}

	private Text Label(string name, Transform parent, string text, int size, FontStyle style, Color colour)
	{
		Text text2 = Rect(name, parent).gameObject.AddComponent<Text>();
		text2.font = font;
		text2.fontSize = size;
		text2.fontStyle = style;
		text2.color = colour;
		text2.text = text ?? "";
		text2.raycastTarget = false;
		text2.supportRichText = false;
		text2.alignment = TextAnchor.UpperLeft;
		text2.horizontalOverflow = HorizontalWrapMode.Overflow;
		text2.verticalOverflow = VerticalWrapMode.Overflow;
		return text2;
	}

	private static void At(RectTransform r, float x, float y, float w, float h)
	{
		Vector2 anchorMin = (r.anchorMax = new Vector2(0f, 1f));
		r.anchorMin = anchorMin;
		r.pivot = new Vector2(0f, 1f);
		r.anchoredPosition = new Vector2(x, y);
		r.sizeDelta = new Vector2(w, h);
	}

	private static void Stretch(RectTransform r, float left, float bottom, float right, float top)
	{
		r.anchorMin = Vector2.zero;
		r.anchorMax = Vector2.one;
		r.pivot = new Vector2(0.5f, 0.5f);
		r.offsetMin = new Vector2(left, bottom);
		r.offsetMax = new Vector2(0f - right, 0f - top);
	}

	private static void Top(RectTransform r, float from, float height, float left, float right)
	{
		r.anchorMin = new Vector2(0f, 1f);
		r.anchorMax = new Vector2(1f, 1f);
		r.pivot = new Vector2(0.5f, 1f);
		r.offsetMin = new Vector2(left, 0f - from - height);
		r.offsetMax = new Vector2(0f - right, 0f - from);
	}

	private static void Bottom(RectTransform r, float from, float height, float left, float right)
	{
		r.anchorMin = new Vector2(0f, 0f);
		r.anchorMax = new Vector2(1f, 0f);
		r.pivot = new Vector2(0.5f, 0f);
		r.offsetMin = new Vector2(left, from);
		r.offsetMax = new Vector2(0f - right, from + height);
	}

	internal bool Contains(Vector2 screen)
	{
		if (visible && Root.activeSelf)
		{
			return RectTransformUtility.RectangleContainsScreenPoint(Window, screen, camera);
		}
		return false;
	}

	internal void ReleaseFocus()
	{
		if (HasKeyboardFocus)
		{
			EventSystem.current.SetSelectedGameObject(null);
		}
	}

	public void Dispose()
	{
		if (Root != null)
		{
			UnityEngine.Object.Destroy(Root);
		}
	}
}
