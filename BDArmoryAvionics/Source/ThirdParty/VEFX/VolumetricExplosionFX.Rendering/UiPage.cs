using System.Collections.Generic;

namespace VolumetricExplosionFX.Rendering;

internal sealed class UiPage
{
	internal string Title;

	internal UiGlyph Glyph;

	internal readonly List<UiRow> Rows = new List<UiRow>();
}
