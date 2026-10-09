using System;
using System.Collections.Generic;
using UnityEngine;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Ksp;

internal static class PartCapture
{
	private static readonly List<MeshFilter> filters = new List<MeshFilter>();

	private static readonly List<Vec3> positions = new List<Vec3>();

	private static readonly List<float> normals = new List<float>();

	private static readonly List<float> uvs = new List<float>();

	private static readonly List<int> triangles = new List<int>();

	private static readonly List<int> groups = new List<int>();

	private static readonly List<SkinLook> looks = new List<SkinLook>();

	internal static CapturedPart Read(Part part, Vector3 up, int maxTriangles)
	{
		if (part == null || part.packed || part.isKerbalEVA())
		{
			return null;
		}
		List<MeshFilter> list = part.FindModelComponents<MeshFilter>();
		if (list == null || list.Count == 0)
		{
			return null;
		}
		Vector3 position = part.transform.position;
		Quaternion quaternion = Quaternion.FromToRotation(Vector3.up, up);
		return Copy(list, Matrix4x4.TRS(position, quaternion, Vector3.one).inverse, position, quaternion, maxTriangles, live: true);
	}

	internal static CapturedPart ReadPrefab(Part prefab, Vector3 origin, Vector3 up, int maxTriangles)
	{
		if (prefab == null)
		{
			return null;
		}
		List<MeshFilter> list = prefab.FindModelComponents<MeshFilter>();
		if (list == null || list.Count == 0)
		{
			return null;
		}
		return Copy(list, prefab.transform.worldToLocalMatrix, origin, Quaternion.FromToRotation(Vector3.up, up), maxTriangles, live: false);
	}

	private static CapturedPart Copy(List<MeshFilter> found, Matrix4x4 toFrame, Vector3 origin, Quaternion frame, int maxTriangles, bool live)
	{
		filters.Clear();
		filters.AddRange(found);
		filters.Sort((MeshFilter a, MeshFilter b) => Triangles(b).CompareTo(Triangles(a)));
		positions.Clear();
		normals.Clear();
		uvs.Clear();
		triangles.Clear();
		groups.Clear();
		looks.Clear();
		int num = Math.Max(maxTriangles, 1) * 4;
		int num2 = 0;
		for (int num3 = 0; num3 < filters.Count; num3++)
		{
			MeshFilter meshFilter = filters[num3];
			Mesh mesh = ((meshFilter != null) ? meshFilter.sharedMesh : null);
			if (mesh == null || !mesh.isReadable || (live && !meshFilter.gameObject.activeInHierarchy))
			{
				continue;
			}
			MeshRenderer component = meshFilter.GetComponent<MeshRenderer>();
			if (component == null || (live && !component.enabled))
			{
				continue;
			}
			Material[] sharedMaterials = component.sharedMaterials;
			if (sharedMaterials == null || sharedMaterials.Length == 0)
			{
				continue;
			}
			int num4 = 0;
			for (int num5 = 0; num5 < mesh.subMeshCount; num5++)
			{
				if (Opaque(sharedMaterials[Math.Min(num5, sharedMaterials.Length - 1)]))
				{
					num4 += (int)(mesh.GetIndexCount(num5) / 3);
				}
			}
			if (num4 == 0 || num2 + num4 > num)
			{
				continue;
			}
			Vector3[] vertices = mesh.vertices;
			Vector3[] array = mesh.normals;
			Vector2[] uv = mesh.uv;
			if (vertices == null || vertices.Length == 0 || array == null || array.Length != vertices.Length)
			{
				continue;
			}
			Matrix4x4 matrix4x = toFrame * meshFilter.transform.localToWorldMatrix;
			Matrix4x4 transpose = matrix4x.inverse.transpose;
			bool flag = matrix4x.determinant < 0f;
			int count = positions.Count;
			for (int num6 = 0; num6 < vertices.Length; num6++)
			{
				Vector3 vector = matrix4x.MultiplyPoint3x4(vertices[num6]);
				Vector3 normalized = transpose.MultiplyVector(array[num6]).normalized;
				positions.Add(new Vec3(vector.x, vector.y, vector.z));
				normals.Add(normalized.x);
				normals.Add(normalized.y);
				normals.Add(normalized.z);
				Vector2 vector2 = ((uv != null && uv.Length == vertices.Length) ? uv[num6] : Vector2.zero);
				uvs.Add(vector2.x);
				uvs.Add(vector2.y);
			}
			for (int num7 = 0; num7 < mesh.subMeshCount; num7++)
			{
				Material m = sharedMaterials[Math.Min(num7, sharedMaterials.Length - 1)];
				if (Opaque(m))
				{
					int item = Look(m);
					int[] array2 = mesh.GetTriangles(num7);
					for (int num8 = 0; num8 + 2 < array2.Length; num8 += 3)
					{
						triangles.Add(count + array2[num8]);
						triangles.Add(count + (flag ? array2[num8 + 2] : array2[num8 + 1]));
						triangles.Add(count + (flag ? array2[num8 + 1] : array2[num8 + 2]));
						groups.Add(item);
					}
				}
			}
			num2 += num4;
		}
		if (triangles.Count == 0)
		{
			return null;
		}
		double num9 = 1000000000.0;
		double num10 = 1000000000.0;
		double num11 = 1000000000.0;
		double num12 = -1000000000.0;
		double num13 = -1000000000.0;
		double num14 = -1000000000.0;
		for (int num15 = 0; num15 < positions.Count; num15++)
		{
			Vec3 vec = positions[num15];
			num9 = Math.Min(num9, vec.X);
			num12 = Math.Max(num12, vec.X);
			num10 = Math.Min(num10, vec.Y);
			num13 = Math.Max(num13, vec.Y);
			num11 = Math.Min(num11, vec.Z);
			num14 = Math.Max(num14, vec.Z);
		}
		return new CapturedPart
		{
			Mesh = new PartMesh
			{
				Positions = positions.ToArray(),
				Normals = normals.ToArray(),
				Uvs = uvs.ToArray(),
				Triangles = triangles.ToArray(),
				Groups = groups.ToArray()
			},
			Looks = looks.ToArray(),
			Origin = origin,
			Frame = frame,
			Center = new Vec3((num9 + num12) * 0.5, (num10 + num13) * 0.5, (num11 + num14) * 0.5),
			Size = Math.Max(num12 - num9, Math.Max(num13 - num10, num14 - num11))
		};
	}

	private static int Triangles(MeshFilter f)
	{
		Mesh mesh = ((f != null) ? f.sharedMesh : null);
		if (mesh == null)
		{
			return 0;
		}
		long num = 0L;
		for (int i = 0; i < mesh.subMeshCount; i++)
		{
			num += mesh.GetIndexCount(i);
		}
		return (int)Math.Min(num / 3, 2147483647L);
	}

	private static bool Opaque(Material m)
	{
		if (m != null)
		{
			return m.renderQueue < 2450;
		}
		return false;
	}

	private static int Look(Material m)
	{
		SkinLook skinLook = new SkinLook
		{
			Color = (m.HasProperty("_Color") ? m.color : Color.white),
			Scale = Vector2.one,
			Offset = Vector2.zero
		};
		if (m.HasProperty("_MainTex"))
		{
			skinLook.Texture = m.mainTexture;
			skinLook.Scale = m.mainTextureScale;
			skinLook.Offset = m.mainTextureOffset;
		}
		for (int i = 0; i < looks.Count; i++)
		{
			if (looks[i].Same(skinLook))
			{
				return i;
			}
		}
		looks.Add(skinLook);
		return looks.Count - 1;
	}
}
