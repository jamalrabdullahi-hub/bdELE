using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Rendering;

internal static class FragmentMeshBuilder
{
	private static readonly List<Vector3> vertices = new List<Vector3>();

	private static readonly List<Vector3> normals = new List<Vector3>();

	private static readonly List<Vector4> uv0 = new List<Vector4>();

	private static readonly List<Vector4> uv1 = new List<Vector4>();

	private static readonly List<Vector4> uv2 = new List<Vector4>();

	private static readonly List<Vector4> uv3 = new List<Vector4>();

	private static readonly List<Vector4> uv4 = new List<Vector4>();

	private static readonly List<Vector4> uv5 = new List<Vector4>();

	private static readonly List<Color32> colors = new List<Color32>();

	private static readonly List<int> indices = new List<int>();

	internal const float Never = 1000000f;

	internal static Quat ToQuat(Quaternion q)
	{
		return new Quat(q.x, q.y, q.z, q.w);
	}

	private static Vector3 V(Vec3 v)
	{
		return new Vector3((float)v.X, (float)v.Y, (float)v.Z);
	}

	private static byte Byte(double v)
	{
		return (byte)Mathf.RoundToInt(Mathf.Clamp01((float)v) * 255f);
	}

	internal static void Build(Mesh mesh, FractureResult cut, FragmentMotion[] motion, Quaternion toEffect, Vector3 offset, int groups, double gravity, double horizon, uint seed)
	{
		mesh.Clear();
		vertices.Clear();
		normals.Clear();
		uv0.Clear();
		uv1.Clear();
		uv2.Clear();
		uv3.Clear();
		uv4.Clear();
		uv5.Clear();
		colors.Clear();
		SplitRandom splitRandom = new SplitRandom(seed);
		byte[] array = new byte[cut.Count];
		for (int i = 0; i < cut.Count; i++)
		{
			array[i] = Byte(splitRandom.Next());
		}
		for (int j = 0; j < cut.VertexCount; j++)
		{
			int num = cut.Fragment[j];
			FragmentMotion fragmentMotion = motion[num];
			vertices.Add(toEffect * V(cut.Positions[j]) + offset);
			normals.Add(toEffect * new Vector3(cut.Normals[3 * j], cut.Normals[3 * j + 1], cut.Normals[3 * j + 2]));
			uv0.Add(new Vector4(cut.Uvs[2 * j], cut.Uvs[2 * j + 1], (cut.Side != null) ? cut.Side[j] : 0f, 0f));
			float w = ((double.IsInfinity(fragmentMotion.LandTime) || fragmentMotion.LandTime > 1000000.0) ? 1000000f : ((float)fragmentMotion.LandTime));
			uv1.Add(new Vector4((float)fragmentMotion.Pivot.X, (float)fragmentMotion.Pivot.Y, (float)fragmentMotion.Pivot.Z, w));
			uv2.Add(new Vector4((float)fragmentMotion.Velocity.X, (float)fragmentMotion.Velocity.Y, (float)fragmentMotion.Velocity.Z, (float)fragmentMotion.Drag));
			Vector3 vector = V(fragmentMotion.SpinAxis) * (float)fragmentMotion.SpinRate;
			uv3.Add(new Vector4(vector.x, vector.y, vector.z, (float)fragmentMotion.RestHeight));
			Vector3 vector2 = V(fragmentMotion.SettleAxis) * (float)fragmentMotion.SettleAngle;
			uv4.Add(new Vector4(vector2.x, vector2.y, vector2.z, (float)fragmentMotion.Hop));
			uv5.Add(new Vector4((float)fragmentMotion.Slide.X, (float)fragmentMotion.Slide.Z, (float)fragmentMotion.SlideTime, (float)fragmentMotion.LandHeight));
			colors.Add(new Color32(Byte(fragmentMotion.Heat), Byte(fragmentMotion.Char), array[num], Byte(cut.Edge[j])));
		}
		mesh.indexFormat = ((vertices.Count > 65000) ? IndexFormat.UInt32 : IndexFormat.UInt16);
		mesh.SetVertices(vertices);
		mesh.SetNormals(normals);
		mesh.SetUVs(0, uv0);
		mesh.SetUVs(1, uv1);
		mesh.SetUVs(2, uv2);
		mesh.SetUVs(3, uv3);
		mesh.SetUVs(4, uv4);
		mesh.SetUVs(5, uv5);
		mesh.SetColors(colors);
		groups = Math.Max(groups, 1);
		mesh.subMeshCount = groups;
		for (int k = 0; k < groups; k++)
		{
			indices.Clear();
			for (int l = 0; l < cut.TriangleCount; l++)
			{
				if (Math.Min(cut.Groups[l], groups - 1) == k)
				{
					indices.Add(cut.Triangles[3 * l]);
					indices.Add(cut.Triangles[3 * l + 1]);
					indices.Add(cut.Triangles[3 * l + 2]);
				}
			}
			mesh.SetTriangles(indices, k, calculateBounds: false);
		}
		mesh.bounds = Reach(cut, motion, gravity, horizon);
	}

	private static Bounds Reach(FractureResult cut, FragmentMotion[] motion, double gravity, double horizon)
	{
		Bounds result = new Bounds(V(motion[0].Pivot), Vector3.zero);
		for (int i = 0; i < cut.Count; i++)
		{
			FragmentMotion m = motion[i];
			double num = Math.Min(m.LandTime, horizon);
			double num2 = Ballistics.ApexTime(m.Velocity.Y, m.Drag, gravity);
			float num3 = (float)cut.Radius[i] * 1.2f;
			double[] array = new double[6]
			{
				0.0,
				num2,
				num * 0.25,
				num * 0.5,
				num * 0.75,
				num
			};
			foreach (double num4 in array)
			{
				if (!(num4 > num))
				{
					result.Encapsulate(new Bounds(V(DebrisPlanner.Where(m, gravity, num4)), Vector3.one * (2f * num3)));
				}
			}
			if (!double.IsInfinity(m.LandTime))
			{
				result.Encapsulate(new Bounds(V(DebrisPlanner.Where(m, gravity, m.LandTime + m.SlideTime + 1.0)), Vector3.one * (2f * num3 + (float)m.Hop)));
			}
		}
		return result;
	}
}
