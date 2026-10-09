using System;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Ksp;

internal sealed class PerfProbe
{
	private readonly FrameStats idle = new FrameStats(900);

	private readonly FrameStats busy = new FrameStats(7200);

	private readonly FrameStats ours = new FrameStats(7200);

	private readonly Stopwatch watch = new Stopwatch();

	private EffectLoad peak;

	private bool episode;

	private float started;

	private float quietSince = -1f;

	private int gcAtStart;

	private double ourMs;

	private string note;

	internal int Episodes { get; private set; }

	internal void Begin()
	{
		watch.Reset();
		watch.Start();
	}

	internal void End()
	{
		if (watch.IsRunning)
		{
			watch.Stop();
			ourMs += watch.Elapsed.TotalMilliseconds;
		}
	}

	internal void Note(string what)
	{
		note = what;
	}

	internal void Frame(EffectLoad now, string quality)
	{
		float unscaledTime = Time.unscaledTime;
		double ms = (double)Time.unscaledDeltaTime * 1000.0;
		double ms2 = ourMs;
		ourMs = 0.0;
		if (now.Busy)
		{
			if (!episode)
			{
				episode = true;
				started = unscaledTime;
				gcAtStart = GC.CollectionCount(0);
				busy.Clear();
				ours.Clear();
				peak = default;
			}
			quietSince = -1f;
			busy.Add(ms);
			ours.Add(ms2);
			peak.Peak(now);
			if (unscaledTime - started >= 60f)
			{
				Report(quality, "continuing");
				episode = false;
			}
			return;
		}
		idle.Add(ms);
		if (episode)
		{
			if (quietSince < 0f)
			{
				quietSince = unscaledTime;
			}
			else if (unscaledTime - quietSince >= 1f)
			{
				Report(quality, null);
				episode = false;
			}
		}
	}

	internal void Flush(string quality)
	{
		if (episode && busy.Count > 0)
		{
			Report(quality, "scene exit");
		}
		episode = false;
	}

	private void Report(string quality, string why)
	{
		CultureInfo invariantCulture = CultureInfo.InvariantCulture;
		float num = ((quietSince >= 0f) ? quietSince : Time.unscaledTime) - started;
		string text = ((idle.Count >= 30) ? string.Format(invariantCulture, "median {0:F1} ms", idle.Median) : "not measured yet");
		UnityEngine.Debug.Log(string.Format(invariantCulture, "[VEFX] Performance: {0:F1} s of effects{1}{2} | {3}x{4} {5} | frame median {6:F1} ms, p95 {7:F1}, worst {8:F1} (without effects: {9}) | VEFX update median {10:F2} ms, p95 {11:F2}, worst {12:F2} | peak {13} blasts, {14} fireball volumes, {15} fire volumes, {16} particles, {17} wrecks / {18} pieces, {19} lights | GC {20}", num, (note != null) ? (" (" + note + ")") : "", (why != null) ? (" [" + why + "]") : "", Screen.width, Screen.height, quality, busy.Median, busy.P95, busy.Worst, text, ours.Median, ours.P95, ours.Worst, peak.Blasts, peak.FireballVolumes, peak.FireVolumes, peak.Particles, peak.Wrecks, peak.WreckPieces, peak.Lights, GC.CollectionCount(0) - gcAtStart));
		note = null;
		Episodes++;
	}
}
