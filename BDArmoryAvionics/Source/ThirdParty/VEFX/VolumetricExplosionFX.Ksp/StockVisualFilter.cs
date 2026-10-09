using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Ksp;

internal sealed class StockVisualFilter : IDisposable
{
	private struct Tracked
	{
		public GameObject Effect;

		public int Owner;

		public double Since;
	}

	private const int Tentative = -2;

	private const double TentativeGrace = 0.35;

	private readonly VisualReplacementWindows windows = new VisualReplacementWindows(96);

	private readonly Tracked[] tracked = new Tracked[96];

	private readonly Suppression<Renderer>[] hiddenRenderers = new Suppression<Renderer>[96];

	private readonly Suppression<Light>[] hiddenLights = new Suppression<Light>[96];

	private readonly int[] rejected = new int[64];

	private readonly int[] logged = new int[32];

	private readonly string[] described = new string[4];

	private readonly List<Renderer> renderers = new List<Renderer>(32);

	private readonly List<Light> lights = new List<Light>(4);

	private static readonly FieldInfo Fetch = typeof(FXMonger).GetField("fetch", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

	private static readonly FieldInfo Objects = typeof(FXMonger).GetField("explosionObjects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

	private readonly Func<Vector3, bool> insideOurEffect;

	private readonly Func<Vector3, bool> inRange;

	private int rejectedCursor;

	private int loggedCursor;

	private int lastFrame = -1;

	private int trackedCount;

	private int diagnostics;

	private bool enabled;

	private bool subscribed;

	private bool failed;

	private static StockVisualFilter current;

	private int errors;

	internal int Suppressed { get; private set; }

	internal int Restored { get; private set; }

	internal int Missed { get; private set; }

	internal string Status
	{
		get
		{
			if (!subscribed)
			{
				return "stock filter API unavailable; stock visuals unchanged";
			}
			if (!failed)
			{
				return "stock explosion renderers/lights where our effects are shown";
			}
			return "stock filter stopped after an error; stock visuals restored";
		}
	}

	internal int Unconfirmed { get; private set; }

	internal StockVisualFilter(Func<Vector3, bool> covered, Func<Vector3, bool> withinRange)
	{
		insideOurEffect = covered;
		inRange = withinRange;
		for (int i = 0; i < tracked.Length; i++)
		{
			hiddenRenderers[i] = new Suppression<Renderer>();
			hiddenLights[i] = new Suppression<Light>(2);
		}
		if (current != null)
		{
			try
			{
				current.Dispose();
			}
			catch
			{
			}
		}
		current = this;
		if (!(Fetch == null) && !(Objects == null))
		{
			Camera.onPreCull = (Camera.CameraCallback)Delegate.Combine(Camera.onPreCull, new Camera.CameraCallback(PreCull));
			subscribed = true;
		}
	}

	internal void SetEnabled(bool on)
	{
		if (enabled && !on)
		{
			RestoreAll();
			windows.Clear();
		}
		enabled = on;
	}

	internal void Claim(Vec3 position, double now, double radius, int owner)
	{
		if (subscribed && enabled && !failed)
		{
			windows.Claim(position, now, radius, owner);
		}
	}

	internal void Release(int owner)
	{
		if (owner == 0)
		{
			return;
		}
		windows.Release(owner);
		for (int i = 0; i < tracked.Length; i++)
		{
			if (tracked[i].Effect != null && tracked[i].Owner == owner)
			{
				Untrack(i, restore: true);
			}
		}
	}

	internal void Shift(Vec3 offset)
	{
		windows.Shift(offset);
	}

	private void PreCull(Camera camera)
	{
		if (Time.frameCount == lastFrame)
		{
			return;
		}
		lastFrame = Time.frameCount;
		if (!enabled || failed)
		{
			return;
		}
		try
		{
			Scan(Time.time);
			Hide();
			errors = 0;
		}
		catch (Exception ex)
		{
			if (++errors < 10)
			{
				if (errors == 1)
				{
					Debug.LogWarning("[VEFX] Stock visual filter frame skipped: " + ex);
				}
			}
			else
			{
				failed = true;
				RestoreAll();
				Debug.LogWarning("[VEFX] Stock visual filter stopped after repeated errors: " + ex.GetType().Name);
			}
		}
	}

	private void Scan(double now)
	{
		FXMonger fXMonger = Fetch.GetValue(null) as FXMonger;
		if (fXMonger == null || !(Objects.GetValue(fXMonger) is List<FXObject> { Count: not 0 } list))
		{
			return;
		}
		bool flag = windows.Any(now);
		Confirm(flag, now);
		for (int i = Math.Max(0, list.Count - 128); i < list.Count; i++)
		{
			GameObject gameObject = list[i]?.effectObj;
			if (gameObject == null || !gameObject.activeInHierarchy || !Matches(gameObject, fXMonger.explosions))
			{
				continue;
			}
			int instanceID = gameObject.GetInstanceID();
			if (IsTracked(gameObject) || Contains(rejected, instanceID))
			{
				continue;
			}
			Vector3 position = gameObject.transform.position;
			int num = (flag ? windows.OwnerAt(SnapshotReader.Value(position), now) : 0);
			if (num == 0 && insideOurEffect != null && insideOurEffect(position))
			{
				num = -1;
			}
			if (num == 0)
			{
				if (inRange == null || !inRange(position))
				{
					Diagnose(gameObject, "unclaimed (out of range)", -1.0);
					continue;
				}
				num = -2;
			}
			if (gameObject.GetComponentInParent<Part>() != null)
			{
				rejected[rejectedCursor] = instanceID;
				rejectedCursor = (rejectedCursor + 1) % rejected.Length;
				Diagnose(gameObject, "attached to a part", 0.0);
			}
			else
			{
				Describe(gameObject);
				Track(gameObject, num, now);
				Diagnose(gameObject, num switch
				{
					-1 => "hidden (inside our effect)", 
					-2 => "hidden (provisional)", 
					_ => "hidden (claimed)", 
				}, 0.0);
			}
		}
	}

	private void Hide()
	{
		if (trackedCount == 0)
		{
			return;
		}
		for (int i = 0; i < tracked.Length; i++)
		{
			if (tracked[i].Owner == 0)
			{
				continue;
			}
			GameObject effect = tracked[i].Effect;
			if (effect == null)
			{
				Forget(i);
				continue;
			}
			effect.GetComponentsInChildren(includeInactive: true, renderers);
			for (int j = 0; j < renderers.Count; j++)
			{
				Renderer renderer = renderers[j];
				if (renderer != null && hiddenRenderers[i].Hide(renderer, !renderer.forceRenderingOff))
				{
					renderer.forceRenderingOff = true;
					Suppressed++;
				}
			}
			effect.GetComponentsInChildren(includeInactive: true, lights);
			for (int k = 0; k < lights.Count; k++)
			{
				Light light = lights[k];
				if (light != null && hiddenLights[i].Hide(light, light.enabled))
				{
					light.enabled = false;
				}
			}
		}
		renderers.Clear();
		lights.Clear();
	}

	private void Confirm(bool claims, double now)
	{
		if (trackedCount == 0)
		{
			return;
		}
		for (int i = 0; i < tracked.Length; i++)
		{
			if (tracked[i].Owner == -2 && !(tracked[i].Effect == null))
			{
				Vector3 position = tracked[i].Effect.transform.position;
				int num = (claims ? windows.OwnerAt(SnapshotReader.Value(position), now) : 0);
				if (num == 0 && insideOurEffect != null && insideOurEffect(position))
				{
					num = -1;
				}
				if (num != 0)
				{
					tracked[i].Owner = num;
					Diagnose(tracked[i].Effect, "provisional confirmed", 0.0);
				}
				else if (!(now - tracked[i].Since < 0.35))
				{
					GameObject effect = tracked[i].Effect;
					rejected[rejectedCursor] = effect.GetInstanceID();
					rejectedCursor = (rejectedCursor + 1) % rejected.Length;
					Diagnose(effect, "unclaimed", windows.NearestDistance(SnapshotReader.Value(position), now));
					Untrack(i, restore: true);
					Unconfirmed++;
				}
			}
		}
	}

	private void Track(GameObject effect, int owner, double now)
	{
		for (int i = 0; i < tracked.Length; i++)
		{
			if (tracked[i].Owner == 0)
			{
				tracked[i] = new Tracked
				{
					Effect = effect,
					Owner = owner,
					Since = now
				};
				trackedCount++;
				hiddenRenderers[i].Clear();
				hiddenLights[i].Clear();
				return;
			}
		}
		Missed++;
	}

	private bool IsTracked(GameObject effect)
	{
		if (trackedCount == 0)
		{
			return false;
		}
		for (int i = 0; i < tracked.Length; i++)
		{
			if (tracked[i].Owner != 0 && tracked[i].Effect == effect)
			{
				return true;
			}
		}
		return false;
	}

	private void Untrack(int i, bool restore)
	{
		if (restore && tracked[i].Effect != null)
		{
			Restored += hiddenRenderers[i].Restore((Renderer r) =>
			{
				if (r != null && r.forceRenderingOff)
				{
					r.forceRenderingOff = false;
				}
			});
			hiddenLights[i].Restore((Light l) =>
			{
				if (l != null && !l.enabled)
				{
					l.enabled = true;
				}
			});
		}
		Forget(i);
	}

	private void Forget(int i)
	{
		if (tracked[i].Owner != 0)
		{
			trackedCount--;
		}
		tracked[i] = default;
		hiddenRenderers[i].Clear();
		hiddenLights[i].Clear();
	}

	private static bool Matches(GameObject effect, GameObject[] prefabs)
	{
		if (prefabs == null)
		{
			return false;
		}
		string name = effect.name;
		for (int i = 0; i < prefabs.Length; i++)
		{
			if (prefabs[i] != null && StockNames.IsInstanceOf(name, prefabs[i].name))
			{
				return true;
			}
		}
		return false;
	}

	private void Describe(GameObject effect)
	{
		string name = effect.name;
		for (int i = 0; i < described.Length && !(described[i] == name); i++)
		{
			if (described[i] != null)
			{
				continue;
			}
			described[i] = name;
			effect.GetComponentsInChildren(includeInactive: true, renderers);
			effect.GetComponentsInChildren(includeInactive: true, lights);
			int num = 0;
			int num2 = 0;
			for (int j = 0; j < renderers.Count; j++)
			{
				if (renderers[j] != null && !renderers[j].forceRenderingOff)
				{
					num++;
				}
			}
			for (int k = 0; k < lights.Count; k++)
			{
				if (lights[k] != null && lights[k].enabled)
				{
					num2++;
				}
			}
			Debug.Log("[VEFX] Stock FX " + name + " holds " + renderers.Count + " renderer(s) (" + num + " visible) and " + lights.Count + " light(s) (" + num2 + " on)");
			renderers.Clear();
			lights.Clear();
			break;
		}
	}

	private static bool Contains(int[] ids, int id)
	{
		for (int i = 0; i < ids.Length; i++)
		{
			if (ids[i] == id)
			{
				return true;
			}
		}
		return false;
	}

	private void Diagnose(GameObject effect, string decision, double nearestClaim)
	{
		if (diagnostics < 24)
		{
			int instanceID = effect.GetInstanceID();
			int num;
			if (decision.StartsWith("unclaimed", StringComparison.Ordinal))
			{
				num = instanceID;
			}
			else
			{
				num = ((decision == "provisional confirmed") ? (instanceID ^ 0x55555555) : (~instanceID));
			}
			if (!Contains(logged, num))
			{
				logged[loggedCursor] = num;
				loggedCursor = (loggedCursor + 1) % logged.Length;
				diagnostics++;
				CultureInfo invariantCulture = CultureInfo.InvariantCulture;
				string name = effect.name;
				string arg = decision;
				string arg2;
				if (decision == "unclaimed")
				{
					arg2 = ((nearestClaim < 0.0) ? " after grace, restored (no open claim)" : string.Format(CultureInfo.InvariantCulture, " after grace, restored (nearest claim {0:F1} m)", nearestClaim));
				}
				else
				{
					arg2 = "";
				}
				Debug.Log(string.Format(invariantCulture, "[VEFX] Stock FX {0}: {1}{2}", name, arg, arg2));
			}
		}
	}

	private void RestoreAll()
	{
		for (int i = 0; i < tracked.Length; i++)
		{
			if (tracked[i].Owner != 0)
			{
				Untrack(i, restore: true);
			}
		}
	}

	public void Dispose()
	{
		if (current == this)
		{
			current = null;
		}
		if (subscribed)
		{
			Camera.onPreCull = (Camera.CameraCallback)Delegate.Remove(Camera.onPreCull, new Camera.CameraCallback(PreCull));
			subscribed = false;
		}
		RestoreAll();
		windows.Clear();
	}
}
