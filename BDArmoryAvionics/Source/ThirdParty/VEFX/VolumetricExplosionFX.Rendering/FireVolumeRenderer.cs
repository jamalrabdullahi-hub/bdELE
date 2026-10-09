using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace VolumetricExplosionFX.Rendering;

internal sealed class FireVolumeRenderer
{
	private static readonly int BoxId = Shader.PropertyToID("_Box");

	private static readonly int FireId = Shader.PropertyToID("_Fire");

	private static readonly int LeanId = Shader.PropertyToID("_Lean");

	private readonly Transform transform;

	private readonly MeshRenderer renderer;

	private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

	private float radius;

	private float height;

	private float halfWidth;

	private float halfHeight;

	private float rise;

	private float seed;

	private Vector2 lean;

	private bool visible;

	internal bool Visible => visible;

	internal Vector3 Center => transform.position;

	internal float Reach => Mathf.Sqrt(2f * halfWidth * halfWidth + halfHeight * halfHeight);

	internal FireVolumeRenderer(Transform parent, Mesh cube, Material material, int layer)
	{
		GameObject gameObject = new GameObject("Original volumetric pool fire");
		gameObject.layer = layer;
		gameObject.transform.SetParent(parent, worldPositionStays: false);
		transform = gameObject.transform;
		gameObject.AddComponent<MeshFilter>().sharedMesh = cube;
		renderer = gameObject.AddComponent<MeshRenderer>();
		renderer.sharedMaterial = material;
		renderer.shadowCastingMode = ShadowCastingMode.Off;
		renderer.receiveShadows = false;
		renderer.lightProbeUsage = LightProbeUsage.Off;
		renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
		renderer.enabled = false;
	}

	internal void Begin(double poolRadius, double flameHeight, Vector3 wind, double gravity, uint randomSeed)
	{
		radius = (float)Math.Max(poolRadius, 0.3);
		height = (float)Math.Max(flameHeight, 0.5);
		float num = 1.4f * Mathf.Sqrt((float)Math.Max(gravity, 0.5) * height);
		lean = new Vector2(wind.x, wind.z) / Mathf.Max(num, 1f);
		if (lean.magnitude > 0.6f)
		{
			lean = lean.normalized * 0.6f;
		}
		rise = Mathf.Clamp(0.8f * num / height, 0.25f, 3f);
		seed = (float)(randomSeed % 997) * 0.137f;
		halfHeight = height * 0.9f;
		halfWidth = radius * 1.25f + lean.magnitude * height * 1.7f + 0.5f;
		transform.localPosition = new Vector3(0f, halfHeight, 0f);
		transform.localRotation = Quaternion.identity;
		transform.localScale = new Vector3(halfWidth, halfHeight, halfWidth);
		visible = false;
		renderer.enabled = false;
	}

	internal void Tick(float level, float flame, double puffHz, float steps, float flicker)
	{
		visible = level > 0.01f;
		if (!visible)
		{
			renderer.enabled = false;
			return;
		}
		block.SetVector(BoxId, new Vector4(halfWidth, halfHeight, steps, seed));
		block.SetVector(FireId, new Vector4(radius, Mathf.Min(flame, 2f * halfHeight / 1.75f), Mathf.Clamp01(level), (float)puffHz));
		block.SetVector(LeanId, new Vector4(lean.x, lean.y, rise, flicker));
		renderer.SetPropertyBlock(block);
	}

	internal void Show(bool on)
	{
		renderer.enabled = visible & on;
	}

	internal void Clear()
	{
		visible = false;
		renderer.enabled = false;
	}

	internal float Coverage(Camera cam)
	{
		if (!visible || cam == null)
		{
			return 0f;
		}
		float reach = Reach;
		float num = Vector3.Distance(cam.transform.position, Center);
		if (num <= reach)
		{
			return 1f;
		}
		float num2 = reach / (num * Mathf.Tan(cam.fieldOfView * 0.5f * ((float)Math.PI / 180f)));
		return Mathf.Clamp01((float)Math.PI * num2 * num2 / (4f * Mathf.Max(cam.aspect, 0.1f)));
	}
}
