using System;

namespace VolumetricExplosionFX.Rendering;

internal sealed class UiRow
{
	internal UiRowKind Kind;

	internal string Label;

	internal string Hint;

	internal Func<bool> GetBool;

	internal Action<bool> SetBool;

	internal Func<float> GetValue;

	internal Action<float> SetValue;

	internal float Min;

	internal float Max;

	internal float Step;

	internal Func<float, string> Format;

	internal string[] Options;

	internal Func<int> GetChoice;

	internal Action<int> SetChoice;

	internal string Button;

	internal Action Click;

	internal Func<string> Text;

	internal Func<bool> Available;
}
