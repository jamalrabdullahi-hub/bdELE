using System;
using System.Text;
using UnityEngine;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Rendering;

internal static class CameraRig
{
	internal static Camera Near;

	internal static Camera Far;

	private static bool addedNear;

	private static bool addedFar;

	private static float lastNear;

	private static float lastFar;

	internal static void Setup()
	{
		Refresh();
		int num = ((Near != null) ? Near.cullingMask : (-1));
		int num2 = ((Far != null) ? Far.cullingMask : (-1));
		int num3 = num & num2;
		EffectLayer.Value = (((num3 & 2) != 0 || (num3 & 1) == 0) ? 1 : 0);
		StringBuilder stringBuilder = new StringBuilder("[VEFX] Flight cameras:");
		FlightCamera fetch = FlightCamera.fetch;
		if (fetch != null && fetch.cameras != null)
		{
			Camera[] cameras = fetch.cameras;
			foreach (Camera camera in cameras)
			{
				if (camera != null)
				{
					stringBuilder.Append(" | " + camera.name + " near " + camera.nearClipPlane.ToString("F2") + " far " + camera.farClipPlane.ToString("F0") + " mask 0x" + camera.cullingMask.ToString("X8") + " depth " + camera.depthTextureMode.ToString() + ((camera == Near) ? " (near)" : ((camera == Far) ? " (far)" : "")));
				}
			}
		}
		stringBuilder.Append(" | effect layer " + EffectLayer.Value);
		Debug.Log(stringBuilder.ToString());
	}

	internal static void Refresh()
	{
		FlightCamera fetch = FlightCamera.fetch;
		Near = ((fetch != null) ? fetch.mainCamera : Camera.main);
		Far = null;
		if (fetch == null || fetch.cameras == null)
		{
			return;
		}
		Camera[] cameras = fetch.cameras;
		foreach (Camera camera in cameras)
		{
			if (camera != null && camera != Near && (Far == null || camera.farClipPlane > Far.farClipPlane))
			{
				Far = camera;
			}
		}
		if (Far != null && Near != null && Far.farClipPlane <= Near.farClipPlane)
		{
			Far = null;
		}
	}

	internal static Camera Pick(Vector3 center, float reach)
	{
		if (Near == null || Far == null)
		{
			return null;
		}
		float num = Vector3.Dot(center - Near.transform.position, Near.transform.forward);
		double split = Math.Min(Near.farClipPlane, Far.nearClipPlane);
		return VolumeCameraRange.Select(num, reach, split) switch
		{
			VolumeCameraSelection.Far => Far, 
			VolumeCameraSelection.Near => Near, 
			_ => null, 
		};
	}

	internal static void SetRenderRange(Camera c)
	{
		float y;
		if (c == Near && Far != null)
		{
			y = Mathf.Min(c.farClipPlane, Far.nearClipPlane);
		}
		else
		{
			y = ((c != null) ? c.farClipPlane : 0f);
		}
		Shader.SetGlobalVector("_VefxClipRange", IsFlight(c) ? new Vector4(c.nearClipPlane, y, 0f, 0f) : Vector4.zero);
	}

	internal static bool IsFlight(Camera c)
	{
		if (c != null)
		{
			if (!(c == Near))
			{
				return c == Far;
			}
			return true;
		}
		return false;
	}

	internal static void RequestDepth(bool near, bool far)
	{
		float unscaledTime = Time.unscaledTime;
		if (near)
		{
			lastNear = unscaledTime;
		}
		if (far)
		{
			lastFar = unscaledTime;
		}
		Want(Near, near || unscaledTime - lastNear < 3f, ref addedNear);
		Want(Far, far || unscaledTime - lastFar < 3f, ref addedFar);
	}

	private static void Want(Camera c, bool want, ref bool added)
	{
		if (c == null)
		{
			added = false;
			return;
		}
		bool flag = (c.depthTextureMode & DepthTextureMode.Depth) != 0;
		if (want && !flag)
		{
			c.depthTextureMode |= DepthTextureMode.Depth;
			added = true;
		}
		else if (!want & flag & added)
		{
			c.depthTextureMode &= ~DepthTextureMode.Depth;
			added = false;
		}
	}

	internal static void Release()
	{
		Want(Near, want: false, ref addedNear);
		Want(Far, want: false, ref addedFar);
		Near = null;
		Far = null;
		Shader.SetGlobalVector("_VefxClipRange", Vector4.zero);
	}
}
