using System;
using System.Collections.Generic;

namespace VolumetricExplosionFX.Rendering;

internal sealed class UiModel
{
	internal string Title;

	internal string Subtitle;

	internal string Reset;

	internal readonly List<UiPage> Pages = new List<UiPage>();

	internal Action Close;

	internal Action ResetAll;

	internal Action Changed;

	internal Func<string> Status;
}
