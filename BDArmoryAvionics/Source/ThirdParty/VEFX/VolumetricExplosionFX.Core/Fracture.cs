using System;
using System.Collections.Generic;

namespace VolumetricExplosionFX.Core;

public static class Fracture
{
	private sealed class Heap
	{
		private double[] key;

		private int[] value;

		public int Count;

		public Heap(int capacity)
		{
			key = new double[Math.Max(capacity, 16)];
			value = new int[key.Length];
		}

		public void Push(double k, int v)
		{
			if (Count == key.Length)
			{
				Array.Resize(ref key, Count * 2);
				Array.Resize(ref value, Count * 2);
			}
			int num = Count++;
			while (num > 0)
			{
				int num2 = (num - 1) / 2;
				if (key[num2] <= k)
				{
					break;
				}
				key[num] = key[num2];
				value[num] = value[num2];
				num = num2;
			}
			key[num] = k;
			value[num] = v;
		}

		public int Pop(out double k)
		{
			k = key[0];
			int result = value[0];
			double num = key[--Count];
			int num2 = value[Count];
			int num3 = 0;
			while (true)
			{
				int num4 = 2 * num3 + 1;
				if (num4 >= Count)
				{
					break;
				}
				if (num4 + 1 < Count && key[num4 + 1] < key[num4])
				{
					num4++;
				}
				if (key[num4] >= num)
				{
					break;
				}
				key[num3] = key[num4];
				value[num3] = value[num4];
				num3 = num4;
			}
			if (Count > 0)
			{
				key[num3] = num;
				value[num3] = num2;
			}
			return result;
		}
	}

	public static FractureResult Cut(PartMesh mesh, int pieces, uint seed, int maxTriangles, double crumple)
	{
		if (mesh == null || mesh.TriangleCount == 0 || mesh.VertexCount < 3)
		{
			return null;
		}
		if (mesh.TriangleCount > maxTriangles * 4)
		{
			return null;
		}
		mesh = Complete(mesh);
		pieces = Math.Max(1, Math.Min(pieces, 64));
		SplitRandom rnd = new SplitRandom(seed);
		int num = Math.Max(16, maxTriangles / 3);
		double num2 = 0.0;
		for (int i = 0; i < mesh.TriangleCount; i++)
		{
			Vec3 vec = mesh.Positions[mesh.Triangles[3 * i]];
			Vec3 vec2 = mesh.Positions[mesh.Triangles[3 * i + 1]];
			Vec3 vec3 = mesh.Positions[mesh.Triangles[3 * i + 2]];
			num2 += 0.5 * Vec3.Cross(vec2 - vec, vec3 - vec).Length;
		}
		double num3 = 1.52 * Math.Sqrt(Math.Max(num2, 1E-09) / ((double)pieces * 48.0));
		PartMesh partMesh = Refine(mesh, 1.5 * num3, num);
		int num4 = maxTriangles;
		maxTriangles = num;
		int triangleCount = partMesh.TriangleCount;
		double num5 = Extent(partMesh.Positions);
		int[] weld = Weld(partMesh.Positions, Math.Max(num5 * 2E-05, 1E-06), out var count);
		int[] array = Neighbors(partMesh.Triangles, weld, count);
		Vec3[] array2 = new Vec3[triangleCount];
		double[] array3 = new double[triangleCount];
		Vec3[] array4 = new Vec3[triangleCount];
		for (int j = 0; j < triangleCount; j++)
		{
			Vec3 vec4 = partMesh.Positions[partMesh.Triangles[3 * j]];
			Vec3 vec5 = partMesh.Positions[partMesh.Triangles[3 * j + 1]];
			Vec3 vec6 = partMesh.Positions[partMesh.Triangles[3 * j + 2]];
			array2[j] = (vec4 + vec5 + vec6) * (1.0 / 3.0);
			Vec3 vec7 = Vec3.Cross(vec5 - vec4, vec6 - vec4);
			array3[j] = 0.5 * vec7.Length;
			array4[j] = vec7;
		}
		int[] array5 = Components(array, triangleCount, out var count2);
		double[] componentArea = new double[count2];
		for (int k = 0; k < triangleCount; k++)
		{
			componentArea[array5[k]] += array3[k];
		}
		double num6 = 0.0;
		for (int l = 0; l < count2; l++)
		{
			num6 += componentArea[l];
		}
		if (num6 <= 0.0)
		{
			return null;
		}
		int[] array6 = new int[count2];
		for (int m = 0; m < count2; m++)
		{
			array6[m] = m;
		}
		Array.Sort(array6, (int x, int y) => componentArea[y].CompareTo(componentArea[x]));
		int[] array7 = new int[count2];
		int num7 = 0;
		for (int num8 = 0; num8 < count2; num8++)
		{
			int num9 = array6[num8];
			int num10 = Math.Max(1, (int)Math.Round((double)pieces * componentArea[num9] / num6));
			if (num7 + num10 > pieces * 2)
			{
				if (num7 >= pieces)
				{
					break;
				}
				num10 = Math.Max(1, pieces * 2 - num7);
			}
			array7[num9] = num10;
			num7 += num10;
		}
		int[] array8 = new int[triangleCount];
		double[] array9 = new double[triangleCount];
		for (int num11 = 0; num11 < triangleCount; num11++)
		{
			array8[num11] = -1;
			array9[num11] = double.MaxValue;
		}
		Vec3[] array10 = new Vec3[Math.Max(num7, 1)];
		double[] array11 = new double[Math.Max(num7, 1)];
		Heap heap = new Heap(triangleCount);
		int[][] array12 = TrianglesByComponent(array5, count2, triangleCount);
		int num12 = 0;
		for (int num13 = 0; num13 < count2; num13++)
		{
			if (array7[num13] == 0)
			{
				continue;
			}
			int[] list = array12[num13];
			double num14 = Math.Sqrt(componentArea[num13] / (double)array7[num13]);
			for (int num15 = 0; num15 < array7[num13]; num15++)
			{
				int num16 = PickByArea(list, array3, componentArea[num13], ref rnd);
				if (array8[num16] < 0)
				{
					Vec3 vec8 = array4[num16].Normalized(new Vec3(0.0, 1.0, 0.0));
					array10[num12] = Vec3.Cross(vec8, rnd.Direction()).Normalized(Perpendicular(vec8));
					array11[num12] = 0.9 * rnd.Next();
					array8[num16] = num12++;
					double num17 = rnd.Next();
					array9[num16] = (0.0 - num14) * (0.12 * rnd.Next() + 1.3 * num17 * num17 * num17 * num17);
					heap.Push(array9[num16], num16);
				}
			}
		}
		while (heap.Count > 0)
		{
			int num18 = heap.Pop(out var k2);
			if (k2 > array9[num18])
			{
				continue;
			}
			for (int num19 = 0; num19 < 3; num19++)
			{
				int num20 = array[3 * num18 + num19];
				if (num20 >= 0)
				{
					Vec3 a = array2[num20] - array2[num18];
					double length = a.Length;
					int num21 = array8[num18];
					double num22 = ((length > 1E-12) ? (1.0 - Math.Abs(Vec3.Dot(a, array10[num21])) / length) : 0.0);
					double num23 = k2 + length * (0.65 + 0.7 * EdgeNoise(num18, num20, seed)) * (1.0 + array11[num21] * num22);
					if (num23 < array9[num20])
					{
						array9[num20] = num23;
						array8[num20] = array8[num18];
						heap.Push(num23, num20);
					}
				}
			}
		}
		int[] array13 = new int[num12];
		for (int num24 = 0; num24 < triangleCount; num24++)
		{
			if (array8[num24] >= 0)
			{
				array13[array8[num24]]++;
			}
		}
		bool[] array14 = new bool[num12];
		int[] array15 = new int[num12];
		for (int num25 = 0; num25 < num12; num25++)
		{
			array15[num25] = num25;
		}
		for (int num26 = num12 - 1; num26 > 0; num26--)
		{
			int num27 = (int)(rnd.Next() * (double)(num26 + 1));
			if (num27 > num26)
			{
				num27 = num26;
			}
			int num28 = array15[num26];
			array15[num26] = array15[num27];
			array15[num27] = num28;
		}
		int num29 = maxTriangles;
		int num30 = 0;
		for (int num31 = 0; num31 < num12; num31++)
		{
			int num32 = array15[num31];
			if (array13[num32] != 0 && array13[num32] <= num29)
			{
				array14[num32] = true;
				num29 -= array13[num32];
				num30++;
			}
		}
		if (num30 == 0)
		{
			return null;
		}
		double thickness = Numbers.Clamp(0.012 * num5, 0.006, 0.05);
		FractureResult fractureResult = Build(partMesh, weld, count, array8, array14, num12, array3, array4, array2, crumple, thickness, ref rnd);
		if (fractureResult == null || fractureResult.TriangleCount > num4)
		{
			return Build(partMesh, weld, count, array8, array14, num12, array3, array4, array2, crumple, 0.0, ref rnd);
		}
		return fractureResult;
	}

	private static FractureResult Build(PartMesh m, int[] weld, int welded, int[] label, bool[] keep, int labels, double[] area, Vec3[] faceNormal, Vec3[] centroid, double crumple, double thickness, ref SplitRandom rnd)
	{
		int triangleCount = m.TriangleCount;
		int vertexCount = m.VertexCount;
		int[] array = new int[labels];
		int num = 0;
		for (int i = 0; i < labels; i++)
		{
			array[i] = (keep[i] ? num++ : (-1));
		}
		int[] array2 = new int[num + 1];
		for (int j = 0; j < triangleCount; j++)
		{
			if (label[j] >= 0 && array[label[j]] >= 0)
			{
				array2[array[label[j]] + 1]++;
			}
		}
		for (int k = 0; k < num; k++)
		{
			array2[k + 1] += array2[k];
		}
		int[] array3 = new int[array2[num]];
		int[] array4 = new int[num];
		for (int l = 0; l < triangleCount; l++)
		{
			if (label[l] >= 0 && array[label[l]] >= 0)
			{
				int num2 = array[label[l]];
				array3[array2[num2] + array4[num2]++] = l;
			}
		}
		FractureResult fractureResult = new FractureResult
		{
			Count = num,
			Pivot = new Vec3[num],
			Normal = new Vec3[num],
			Area = new double[num],
			Radius = new double[num],
			FirstVertex = new int[num],
			VertexCounts = new int[num]
		};
		List<Vec3> list = new List<Vec3>();
		List<float> list2 = new List<float>();
		List<float> list3 = ((m.Tangents != null) ? new List<float>() : null);
		List<float> list4 = new List<float>();
		List<float> list5 = new List<float>();
		List<float> list6 = new List<float>();
		List<int> list7 = new List<int>();
		List<int> list8 = new List<int>();
		List<int> list9 = new List<int>();
		List<int> list10 = new List<int>();
		int[] array5 = new int[vertexCount];
		int[] array6 = new int[vertexCount];
		int[] array7 = new int[welded];
		for (int n = 0; n < vertexCount; n++)
		{
			array6[n] = -1;
		}
		for (int num3 = 0; num3 < welded; num3++)
		{
			array7[num3] = -1;
		}
		Dictionary<long, int> dictionary = new Dictionary<long, int>();
		for (int num4 = 0; num4 < num; num4++)
		{
			dictionary.Clear();
			for (int num5 = array2[num4]; num5 < array2[num4 + 1]; num5++)
			{
				int num6 = array3[num5];
				for (int num7 = 0; num7 < 3; num7++)
				{
					long key = EdgeKey(weld[m.Triangles[3 * num6 + num7]], weld[m.Triangles[3 * num6 + (num7 + 1) % 3]], welded);
					dictionary.TryGetValue(key, out var value);
					dictionary[key] = value + 1;
				}
			}
			foreach (KeyValuePair<long, int> item in dictionary)
			{
				if (item.Value == 1)
				{
					array7[(int)(item.Key / welded)] = num4;
					array7[(int)(item.Key % welded)] = num4;
				}
			}
			Vec3 vec = default;
			Vec3 vec2 = default;
			double num8 = 0.0;
			for (int num9 = array2[num4]; num9 < array2[num4 + 1]; num9++)
			{
				int num10 = array3[num9];
				vec += centroid[num10] * area[num10];
				vec2 += faceNormal[num10];
				num8 += area[num10];
			}
			vec = ((num8 > 1E-12) ? (vec * (1.0 / num8)) : centroid[array3[array2[num4]]]);
			vec2 = vec2.Normalized(new Vec3(0.0, 1.0, 0.0));
			int count = list.Count;
			int count2 = list9.Count;
			for (int num11 = array2[num4]; num11 < array2[num4 + 1]; num11++)
			{
				int num12 = array3[num11];
				for (int num13 = 0; num13 < 3; num13++)
				{
					int num14 = m.Triangles[3 * num12 + num13];
					if (array6[num14] != num4)
					{
						array6[num14] = num4;
						array5[num14] = list.Count;
						list.Add(m.Positions[num14]);
						list2.Add(m.Normals[3 * num14]);
						list2.Add(m.Normals[3 * num14 + 1]);
						list2.Add(m.Normals[3 * num14 + 2]);
						if (list3 != null)
						{
							for (int num15 = 0; num15 < 4; num15++)
							{
								list3.Add(m.Tangents[4 * num14 + num15]);
							}
						}
						list4.Add(m.Uvs[2 * num14]);
						list4.Add(m.Uvs[2 * num14 + 1]);
						list5.Add((array7[weld[num14]] == num4) ? 1 : 0);
						list6.Add(0f);
						list7.Add(num4);
						list8.Add(num14);
					}
					list9.Add(array5[num14]);
				}
				list10.Add((m.Groups != null) ? m.Groups[num12] : 0);
			}
			int num16 = list.Count - count;
			double num17 = 0.0;
			for (int num18 = count; num18 < count + num16; num18++)
			{
				num17 = Math.Max(num17, (list[num18] - vec).Length);
			}
			Vec3 vec3 = Vec3.Cross(vec2, rnd.Direction()).Normalized(Perpendicular(vec2));
			Vec3 vec4 = Vec3.Cross(vec2, vec3);
			double num19 = (rnd.Next() - 0.5) * 0.6 * num17;
			double num20 = (rnd.Next() * 2.0 - 1.0) * 0.8 * crumple;
			double num21 = ((rnd.Next() < 0.6) ? ((double)((rnd.Next() < 0.7) ? 1 : (-1)) * crumple / Math.Max(num17 * rnd.Range(0.6, 1.8), 1E-06)) : 0.0);
			if (num21 != 0.0)
			{
				num20 = 0.0;
			}
			Vec3 vec5 = vec + vec4 * num19;
			Vec3 b = Vec3.Rotate(vec3, vec2, rnd.Next() * Math.PI) * (Math.PI * 2.0 / Math.Max(num17 * rnd.Range(0.7, 1.3), 1E-06));
			Vec3 b2 = Vec3.Rotate(vec3, vec2, rnd.Next() * Math.PI) * (Math.PI * 2.0 / Math.Max(num17 * rnd.Range(0.35, 0.7), 1E-06));
			double num22 = rnd.Next() * 6.283;
			double num23 = rnd.Next() * 6.283;
			for (int num24 = count; num24 < count + num16; num24++)
			{
				Vec3 vec6 = list[num24];
				double num26;
				if (num21 != 0.0)
				{
					double num25 = Vec3.Dot(vec6 - vec, vec4);
					num26 = num21 * num25;
					vec6 = vec6 + vec4 * (Math.Sin(num26) / num21 - num25) + vec2 * ((1.0 - Math.Cos(num26)) / num21);
				}
				else
				{
					double num27 = Smooth(0.0, 0.4 * num17 + 1E-06, Vec3.Dot(vec6 - vec5, vec4));
					num26 = num20 * num27;
					if (num27 > 0.0)
					{
						vec6 = vec5 + Vec3.Rotate(vec6 - vec5, vec3, num26);
					}
				}
				if (num26 != 0.0)
				{
					RotateFloats(list2, 3 * num24, vec3, num26);
					if (list3 != null)
					{
						RotateFloats(list3, 4 * num24, vec3, num26);
					}
				}
				Vec3 a = m.Positions[list8[num24]];
				double num28 = (0.6 * Math.Sin(Vec3.Dot(a, b) + num22) + 0.4 * Math.Sin(Vec3.Dot(a, b2) + num23)) * 0.07 * num17 * crumple;
				list[num24] = vec6 + Vec3.Rotate(vec2, vec3, num26) * num28;
			}
			if (thickness > 0.0)
			{
				Thicken(num4, count, num16, count2, thickness, weld, list8, array7, list, list2, list3, list4, list5, list6, list7, list9, list10);
			}
			num16 = list.Count - count;
			fractureResult.Pivot[num4] = vec;
			fractureResult.Normal[num4] = vec2;
			fractureResult.Area[num4] = num8;
			fractureResult.Radius[num4] = Math.Max(num17, 0.001);
			fractureResult.FirstVertex[num4] = count;
			fractureResult.VertexCounts[num4] = num16;
		}
		fractureResult.Positions = list.ToArray();
		fractureResult.Normals = list2.ToArray();
		fractureResult.Tangents = list3?.ToArray();
		fractureResult.Uvs = list4.ToArray();
		fractureResult.Edge = list5.ToArray();
		fractureResult.Side = list6.ToArray();
		fractureResult.Fragment = list7.ToArray();
		fractureResult.Triangles = list9.ToArray();
		fractureResult.Groups = list10.ToArray();
		return fractureResult;
	}

	private static void Thicken(int f, int first, int count, int firstTriangle, double t, int[] weld, List<int> source, int[] rimStamp, List<Vec3> positions, List<float> normals, List<float> tangents, List<float> uvs, List<float> edge, List<float> side, List<int> fragment, List<int> triangles, List<int> groups)
	{
		int count2 = positions.Count;
		for (int i = first; i < first + count; i++)
		{
			Vec3 vec = new Vec3(normals[3 * i], normals[3 * i + 1], normals[3 * i + 2]);
			positions.Add(positions[i] - vec * t);
			normals.Add(0f - normals[3 * i]);
			normals.Add(0f - normals[3 * i + 1]);
			normals.Add(0f - normals[3 * i + 2]);
			if (tangents != null)
			{
				for (int j = 0; j < 4; j++)
				{
					tangents.Add(tangents[4 * i + j]);
				}
			}
			uvs.Add(uvs[2 * i]);
			uvs.Add(uvs[2 * i + 1]);
			edge.Add(edge[i]);
			side.Add(1f);
			fragment.Add(f);
			source.Add(source[i]);
		}
		int num = (triangles.Count - firstTriangle) / 3;
		for (int k = 0; k < num; k++)
		{
			int num2 = triangles[firstTriangle + 3 * k];
			int num3 = triangles[firstTriangle + 3 * k + 1];
			int num4 = triangles[firstTriangle + 3 * k + 2];
			triangles.Add(count2 + num2 - first);
			triangles.Add(count2 + num4 - first);
			triangles.Add(count2 + num3 - first);
			groups.Add(groups[firstTriangle / 3 + k]);
		}
		Dictionary<long, int> dictionary = new Dictionary<long, int>();
		for (int l = 0; l < num; l++)
		{
			for (int m = 0; m < 3; m++)
			{
				int index = triangles[firstTriangle + 3 * l + m];
				int index2 = triangles[firstTriangle + 3 * l + (m + 1) % 3];
				long key = PairKey(weld[source[index]], weld[source[index2]]);
				dictionary.TryGetValue(key, out var value);
				dictionary[key] = value + 1;
			}
		}
		for (int n = 0; n < num; n++)
		{
			int item = groups[firstTriangle / 3 + n];
			for (int num5 = 0; num5 < 3; num5++)
			{
				int num6 = triangles[firstTriangle + 3 * n + num5];
				int num7 = triangles[firstTriangle + 3 * n + (num5 + 1) % 3];
				if (dictionary[PairKey(weld[source[num6]], weld[source[num7]])] != 1)
				{
					continue;
				}
				Vec3 vec2 = positions[num6];
				Vec3 vec3 = positions[num7];
				Vec3 vec4 = new Vec3(normals[3 * num6] + normals[3 * num7], normals[3 * num6 + 1] + normals[3 * num7 + 1], normals[3 * num6 + 2] + normals[3 * num7 + 2]).Normalized(new Vec3(0.0, 1.0, 0.0));
				Vec3 vec5 = Vec3.Cross(vec3 - vec2, vec4).Normalized(vec4);
				int count3 = positions.Count;
				int[] array = new int[4] { num6, num6, num7, num7 };
				foreach (int num9 in array)
				{
					bool flag = positions.Count - count3 == 1 || positions.Count - count3 == 3;
					positions.Add(flag ? positions[count2 + num9 - first] : positions[num9]);
					normals.Add((float)vec5.X);
					normals.Add((float)vec5.Y);
					normals.Add((float)vec5.Z);
					if (tangents != null)
					{
						for (int num10 = 0; num10 < 4; num10++)
						{
							tangents.Add(tangents[4 * num9 + num10]);
						}
					}
					uvs.Add(uvs[2 * num9]);
					uvs.Add(uvs[2 * num9 + 1]);
					edge.Add(1f);
					side.Add(0.5f);
					fragment.Add(f);
					source.Add(source[num9]);
				}
				triangles.Add(count3);
				triangles.Add(count3 + 1);
				triangles.Add(count3 + 2);
				triangles.Add(count3 + 2);
				triangles.Add(count3 + 1);
				triangles.Add(count3 + 3);
				groups.Add(item);
				groups.Add(item);
			}
		}
	}

	private static long PairKey(int a, int b)
	{
		if (a >= b)
		{
			return ((long)b << 32) | (uint)a;
		}
		return ((long)a << 32) | (uint)b;
	}

	private static PartMesh Refine(PartMesh mesh, double target, int maxTriangles)
	{
		List<Vec3> p = new List<Vec3>(mesh.Positions);
		List<float> n = new List<float>(mesh.Normals);
		List<float> uv = new List<float>(mesh.Uvs);
		List<float> tan = ((mesh.Tangents != null) ? new List<float>(mesh.Tangents) : null);
		List<int> list = new List<int>(mesh.Triangles);
		List<int> list2 = new List<int>(mesh.Groups ?? new int[mesh.TriangleCount]);
		double val = target * target;
		double quantum = Math.Max(Extent(mesh.Positions) * 2E-05, 1E-06);
		for (int i = 0; i < 24; i++)
		{
			int num = list.Count / 3;
			int[] array = Weld(p.ToArray(), quantum, out var count);
			int[] array2 = new int[num];
			double[] array3 = new double[num];
			double num2 = 0.0;
			for (int j = 0; j < num; j++)
			{
				int num3 = 0;
				double num4 = -1.0;
				for (int k = 0; k < 3; k++)
				{
					Vec3 vec = p[list[3 * j + (k + 1) % 3]] - p[list[3 * j + k]];
					double num5 = Vec3.Dot(vec, vec);
					if (num5 > num4)
					{
						num4 = num5;
						num3 = k;
					}
				}
				array2[j] = num3;
				array3[j] = num4;
				num2 = Math.Max(num2, num4);
			}
			double num6 = Math.Max(val, num2 * 0.3);
			HashSet<long> hashSet = new HashSet<long>();
			for (int l = 0; l < num; l++)
			{
				if (array3[l] > num6)
				{
					hashSet.Add(EdgeKey(array[list[3 * l + array2[l]]], array[list[3 * l + (array2[l] + 1) % 3]], count));
				}
			}
			if (hashSet.Count == 0)
			{
				break;
			}
			bool flag = true;
			while (flag)
			{
				flag = false;
				for (int m = 0; m < num; m++)
				{
					long item = EdgeKey(array[list[3 * m + array2[m]]], array[list[3 * m + (array2[m] + 1) % 3]], count);
					if (hashSet.Contains(item))
					{
						continue;
					}
					for (int num7 = 0; num7 < 3; num7++)
					{
						if (hashSet.Contains(EdgeKey(array[list[3 * m + num7]], array[list[3 * m + (num7 + 1) % 3]], count)))
						{
							hashSet.Add(item);
							flag = true;
							break;
						}
					}
				}
			}
			int num8 = 0;
			for (int num9 = 0; num9 < num; num9++)
			{
				for (int num10 = 0; num10 < 3; num10++)
				{
					if (hashSet.Contains(EdgeKey(array[list[3 * num9 + num10]], array[list[3 * num9 + (num10 + 1) % 3]], count)))
					{
						num8++;
					}
				}
			}
			if (num + num8 > maxTriangles)
			{
				break;
			}
			Dictionary<long, int> mid = new Dictionary<long, int>();
			Func<int, int, int> func = (int a, int b) =>
			{
				long key = PairKey(a, b);
				if (mid.TryGetValue(key, out var value))
				{
					return value;
				}
				value = p.Count;
				mid[key] = value;
				p.Add((p[a] + p[b]) * 0.5);
				Vec3 vec2 = new Vec3(n[3 * a] + n[3 * b], n[3 * a + 1] + n[3 * b + 1], n[3 * a + 2] + n[3 * b + 2]).Normalized(new Vec3(0.0, 1.0, 0.0));
				n.Add((float)vec2.X);
				n.Add((float)vec2.Y);
				n.Add((float)vec2.Z);
				uv.Add((uv[2 * a] + uv[2 * b]) * 0.5f);
				uv.Add((uv[2 * a + 1] + uv[2 * b + 1]) * 0.5f);
				if (tan != null)
				{
					Vec3 vec3 = new Vec3(tan[4 * a] + tan[4 * b], tan[4 * a + 1] + tan[4 * b + 1], tan[4 * a + 2] + tan[4 * b + 2]).Normalized(new Vec3(1.0, 0.0, 0.0));
					tan.Add((float)vec3.X);
					tan.Add((float)vec3.Y);
					tan.Add((float)vec3.Z);
					tan.Add(tan[4 * a + 3]);
				}
				return value;
			};
			List<int> list3 = new List<int>(list.Count + num8 * 3);
			List<int> list4 = new List<int>(list2.Count + num8);
			for (int num11 = 0; num11 < num; num11++)
			{
				int item2 = list2[num11];
				int num12 = array2[num11];
				int num13 = list[3 * num11 + num12];
				int num14 = list[3 * num11 + (num12 + 1) % 3];
				int num15 = list[3 * num11 + (num12 + 2) % 3];
				if (!hashSet.Contains(EdgeKey(array[num13], array[num14], count)))
				{
					Add(list3, num13, num14, num15);
					list4.Add(item2);
					continue;
				}
				bool flag2 = hashSet.Contains(EdgeKey(array[num14], array[num15], count));
				bool flag3 = hashSet.Contains(EdgeKey(array[num15], array[num13], count));
				int num16 = func(num13, num14);
				if (flag3)
				{
					int c = func(num15, num13);
					Add(list3, num13, num16, c);
					Add(list3, num16, num15, c);
					list4.Add(item2);
					list4.Add(item2);
				}
				else
				{
					Add(list3, num13, num16, num15);
					list4.Add(item2);
				}
				if (flag2)
				{
					int num17 = func(num14, num15);
					Add(list3, num16, num14, num17);
					Add(list3, num16, num17, num15);
					list4.Add(item2);
					list4.Add(item2);
				}
				else
				{
					Add(list3, num16, num14, num15);
					list4.Add(item2);
				}
			}
			list = list3;
			list2 = list4;
		}
		return new PartMesh
		{
			Positions = p.ToArray(),
			Normals = n.ToArray(),
			Uvs = uv.ToArray(),
			Tangents = ((tan != null) ? tan.ToArray() : null),
			Triangles = list.ToArray(),
			Groups = list2.ToArray()
		};
	}

	private static void Add(List<int> list, int a, int b, int c)
	{
		list.Add(a);
		list.Add(b);
		list.Add(c);
	}

	private static PartMesh Complete(PartMesh m)
	{
		if (m.Normals != null && m.Normals.Length >= m.VertexCount * 3 && m.Uvs != null && m.Uvs.Length >= m.VertexCount * 2)
		{
			return m;
		}
		PartMesh partMesh = new PartMesh
		{
			Positions = m.Positions,
			Tangents = ((m.Tangents != null && m.Tangents.Length >= m.VertexCount * 4) ? m.Tangents : null),
			Triangles = m.Triangles,
			Groups = m.Groups,
			Normals = m.Normals,
			Uvs = m.Uvs
		};
		if (partMesh.Uvs == null || partMesh.Uvs.Length < m.VertexCount * 2)
		{
			partMesh.Uvs = new float[m.VertexCount * 2];
		}
		if (partMesh.Normals == null || partMesh.Normals.Length < m.VertexCount * 3)
		{
			Vec3[] array = new Vec3[m.VertexCount];
			for (int i = 0; i < m.TriangleCount; i++)
			{
				int num = m.Triangles[3 * i];
				int num2 = m.Triangles[3 * i + 1];
				int num3 = m.Triangles[3 * i + 2];
				Vec3 vec = Vec3.Cross(m.Positions[num2] - m.Positions[num], m.Positions[num3] - m.Positions[num]);
				array[num] += vec;
				array[num2] += vec;
				array[num3] += vec;
			}
			partMesh.Normals = new float[m.VertexCount * 3];
			for (int j = 0; j < m.VertexCount; j++)
			{
				Vec3 vec2 = array[j].Normalized(new Vec3(0.0, 1.0, 0.0));
				partMesh.Normals[3 * j] = (float)vec2.X;
				partMesh.Normals[3 * j + 1] = (float)vec2.Y;
				partMesh.Normals[3 * j + 2] = (float)vec2.Z;
			}
		}
		return partMesh;
	}

	private static void RotateFloats(List<float> data, int at, Vec3 axis, double angle)
	{
		Vec3 vec = Vec3.Rotate(new Vec3(data[at], data[at + 1], data[at + 2]), axis, angle);
		data[at] = (float)vec.X;
		data[at + 1] = (float)vec.Y;
		data[at + 2] = (float)vec.Z;
	}

	private static Vec3 Perpendicular(Vec3 n)
	{
		if (!(Math.Abs(n.X) < 0.9))
		{
			return Vec3.Cross(n, new Vec3(0.0, 1.0, 0.0)).Normalized(new Vec3(0.0, 0.0, 1.0));
		}
		return Vec3.Cross(n, new Vec3(1.0, 0.0, 0.0)).Normalized(new Vec3(0.0, 0.0, 1.0));
	}

	private static double Smooth(double a, double b, double x)
	{
		double num = Numbers.Clamp((x - a) / (b - a), 0.0, 1.0);
		return num * num * (3.0 - 2.0 * num);
	}

	private static double Hash01(uint h)
	{
		h ^= h >> 16;
		h *= 2146121005;
		h ^= h >> 15;
		h *= 2221713035u;
		h ^= h >> 16;
		return (double)(h & 0xFFFFFF) / 16777216.0;
	}

	private static double EdgeNoise(int a, int b, uint seed)
	{
		int num = Math.Min(a, b);
		int num2 = Math.Max(a, b);
		return Hash01((uint)((num * -2048144777) ^ (num2 * -1028477379)) ^ seed);
	}

	private static long EdgeKey(int a, int b, int n)
	{
		if (a >= b)
		{
			return (long)b * (long)n + a;
		}
		return (long)a * (long)n + b;
	}

	private static double Extent(Vec3[] p)
	{
		double num = double.MaxValue;
		double num2 = double.MaxValue;
		double num3 = double.MaxValue;
		double num4 = double.MinValue;
		double num5 = double.MinValue;
		double num6 = double.MinValue;
		for (int i = 0; i < p.Length; i++)
		{
			num = Math.Min(num, p[i].X);
			num2 = Math.Min(num2, p[i].Y);
			num3 = Math.Min(num3, p[i].Z);
			num4 = Math.Max(num4, p[i].X);
			num5 = Math.Max(num5, p[i].Y);
			num6 = Math.Max(num6, p[i].Z);
		}
		return new Vec3(num4 - num, num5 - num2, num6 - num3).Length;
	}

	private static int[] Weld(Vec3[] p, double quantum, out int count)
	{
		Dictionary<long, int> dictionary = new Dictionary<long, int>(p.Length);
		int[] array = new int[p.Length];
		for (int i = 0; i < p.Length; i++)
		{
			long num = (long)Math.Round(p[i].X / quantum) & 0x1FFFFF;
			long num2 = (long)Math.Round(p[i].Y / quantum) & 0x1FFFFF;
			long num3 = (long)Math.Round(p[i].Z / quantum) & 0x1FFFFF;
			long key = (num << 42) | (num2 << 21) | num3;
			if (!dictionary.TryGetValue(key, out var value))
			{
				value = (dictionary[key] = dictionary.Count);
			}
			array[i] = value;
		}
		count = dictionary.Count;
		return array;
	}

	private static int[] Neighbors(int[] tri, int[] weld, int welded)
	{
		int num = tri.Length / 3;
		int[] array = new int[num * 3];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = -1;
		}
		Dictionary<long, int> dictionary = new Dictionary<long, int>(num * 2);
		for (int j = 0; j < num; j++)
		{
			for (int k = 0; k < 3; k++)
			{
				int num2 = weld[tri[3 * j + k]];
				int num3 = weld[tri[3 * j + (k + 1) % 3]];
				if (num2 == num3)
				{
					continue;
				}
				long key = EdgeKey(num2, num3, welded);
				if (dictionary.TryGetValue(key, out var value))
				{
					if (value >= 0 && array[value] < 0 && value / 3 != j)
					{
						array[value] = j;
						array[3 * j + k] = value / 3;
						dictionary[key] = -1;
					}
				}
				else
				{
					dictionary[key] = 3 * j + k;
				}
			}
		}
		return array;
	}

	private static int[] Components(int[] neighbor, int T, out int count)
	{
		int[] array = new int[T];
		for (int i = 0; i < T; i++)
		{
			array[i] = -1;
		}
		Stack<int> stack = new Stack<int>();
		count = 0;
		for (int j = 0; j < T; j++)
		{
			if (array[j] >= 0)
			{
				continue;
			}
			array[j] = count;
			stack.Push(j);
			while (stack.Count > 0)
			{
				int num = stack.Pop();
				for (int k = 0; k < 3; k++)
				{
					int num2 = neighbor[3 * num + k];
					if (num2 >= 0 && array[num2] < 0)
					{
						array[num2] = count;
						stack.Push(num2);
					}
				}
			}
			count++;
		}
		return array;
	}

	private static int[][] TrianglesByComponent(int[] component, int components, int T)
	{
		int[] array = new int[components];
		for (int i = 0; i < T; i++)
		{
			array[component[i]]++;
		}
		int[][] array2 = new int[components][];
		for (int j = 0; j < components; j++)
		{
			array2[j] = new int[array[j]];
		}
		int[] array3 = new int[components];
		for (int k = 0; k < T; k++)
		{
			int num = component[k];
			array2[num][array3[num]++] = k;
		}
		return array2;
	}

	private static int PickByArea(int[] list, double[] area, double total, ref SplitRandom rnd)
	{
		double num = rnd.Next() * total;
		double num2 = 0.0;
		for (int i = 0; i < list.Length; i++)
		{
			num2 += area[list[i]];
			if (num2 >= num)
			{
				return list[i];
			}
		}
		return list[list.Length - 1];
	}
}
