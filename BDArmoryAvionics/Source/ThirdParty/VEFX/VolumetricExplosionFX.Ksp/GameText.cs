using KSP.Localization;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Ksp;

internal static class GameText
{
	private static string language;

	private static UiText current;

	internal static UiText Current
	{
		get
		{
			string currentLanguage = Localizer.CurrentLanguage;
			if (current == null || language != currentLanguage)
			{
				language = currentLanguage;
				current = new UiText(currentLanguage, (string tag) => (!Localizer.TryGetStringByTag(tag, out var value)) ? null : value);
			}
			return current;
		}
	}
}
