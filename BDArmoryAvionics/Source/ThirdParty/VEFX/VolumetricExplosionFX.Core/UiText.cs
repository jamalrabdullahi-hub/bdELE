using System;
using System.Collections.Generic;
using System.Globalization;

namespace VolumetricExplosionFX.Core;

public sealed class UiText
{
	private readonly Func<string, string> lookup;

	private readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.Ordinal);

	public string Language { get; private set; }

	public CultureInfo Culture { get; private set; }

	public UiText(string language, Func<string, string> lookup = null)
	{
		Language = Normalize(language);
		this.lookup = lookup;
		string name = ((Language == "ja") ? "ja-JP" : ((Language == "ru") ? "ru-RU" : Language));
		try
		{
			Culture = CultureInfo.GetCultureInfo(name);
		}
		catch (CultureNotFoundException)
		{
			Culture = CultureInfo.InvariantCulture;
		}
	}

	public static string Normalize(string language)
	{
		return (language ?? "").Trim().Replace('_', '-').ToLowerInvariant()
			.Split('-')[0] switch
		{
			"fr" => "fr-fr", 
			"de" => "de-de", 
			"es" => "es-es", 
			"it" => "it-it", 
			"pt" => "pt-br", 
			"ja" => "ja", 
			"ru" => "ru", 
			"zh" => "zh-cn", 
			_ => "en-us", 
		};
	}

	public string Get(string key, string fallback)
	{
		if (cache.TryGetValue(key, out var value))
		{
			return value;
		}
		value = ((lookup != null) ? lookup("#VEFX_" + key) : null);
		if (string.IsNullOrEmpty(value) || value.StartsWith("#VEFX_", StringComparison.Ordinal))
		{
			value = fallback;
		}
		cache[key] = value;
		return value;
	}

	public string Format(string key, string fallback, params object[] args)
	{
		string text = Get(key, fallback);
		for (int i = 0; i < args.Length; i++)
		{
			text = text.Replace("<<" + (i + 1) + ">>", "{" + i + "}");
		}
		try
		{
			return string.Format(Culture, text, args);
		}
		catch (FormatException)
		{
			return string.Format(Culture, fallback, args);
		}
	}
}
