using System.Collections.Generic;
using UnityEngine;

namespace VolumetricExplosionFX.Rendering;

internal static class ScorchMesh
{
	private static readonly List<Vector3> vertices = new List<Vector3>();

	private static readonly List<Vector2> uvs = new List<Vector2>();

	private static readonly List<int> indices = new List<int>();

	private static readonly List<float> around = new List<float>();

	internal static bool Build(Mesh mesh, float radius, int cells, GroundProbe probe)
	{
		mesh.Clear();
		vertices.Clear();
		uvs.Clear();
		indices.Clear();
		int num = cells + 1;
		bool[] array = new bool[num * num];
		int num2 = 0;
		for (int i = 0; i < num; i++)
		{
			for (int j = 0; j < num; j++)
			{
				float num3 = (float)j / (float)cells;
				float num4 = (float)i / (float)cells;
				Vector3 vector = new Vector3((num3 * 2f - 1f) * radius, 0f, (num4 * 2f - 1f) * radius);
				bool flag = probe(vector, out var point, out var normal);
				array[i * num + j] = flag;
				if (flag)
				{
					num2++;
				}
				vertices.Add(flag ? (point + normal * Mathf.Clamp(radius * 0.004f, 0.04f, 0.15f)) : vector);
				uvs.Add(new Vector2(num3, num4));
			}
		}
		if (num2 < num * num / 3)
		{
			return false;
		}
		float num5 = Mathf.Clamp(radius * 0.03f, 0.3f, 1.2f);
		float[] array2 = new float[num * num];
		for (int k = 0; k < num * num; k++)
		{
			array2[k] = vertices[k].y;
		}
		for (int l = 0; l < num; l++)
		{
			for (int m = 0; m < num; m++)
			{
				int num6 = l * num + m;
				if (!array[num6])
				{
					continue;
				}
				around.Clear();
				for (int n = -1; n <= 1; n++)
				{
					for (int num7 = -1; num7 <= 1; num7++)
					{
						int num8 = m + num7;
						int num9 = l + n;
						if ((num7 != 0 || n != 0) && num8 >= 0 && num9 >= 0 && num8 < num && num9 < num && array[num9 * num + num8])
						{
							around.Add(array2[num9 * num + num8]);
						}
					}
				}
				if (around.Count >= 3)
				{
					around.Sort();
					float num10 = around[around.Count / 2];
					if (array2[num6] > num10 + num5)
					{
						vertices[num6] = new Vector3(vertices[num6].x, num10, vertices[num6].z);
					}
				}
			}
		}
		for (int num11 = 0; num11 < cells; num11++)
		{
			for (int num12 = 0; num12 < cells; num12++)
			{
				int num13 = num11 * num + num12;
				int num14 = num13 + 1;
				int num15 = num13 + num;
				int num16 = num15 + 1;
				if (array[num13] && array[num14] && array[num15] && array[num16])
				{
					indices.Add(num13);
					indices.Add(num15);
					indices.Add(num14);
					indices.Add(num14);
					indices.Add(num15);
					indices.Add(num16);
				}
			}
		}
		if (indices.Count == 0)
		{
			return false;
		}
		mesh.SetVertices(vertices);
		mesh.SetUVs(0, uvs);
		mesh.SetTriangles(indices, 0, calculateBounds: true);
		mesh.RecalculateNormals();
		return true;
	}
}
