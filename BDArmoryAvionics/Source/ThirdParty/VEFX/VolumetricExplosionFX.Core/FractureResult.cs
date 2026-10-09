namespace VolumetricExplosionFX.Core;

public sealed class FractureResult
{
	public int Count;

	public Vec3[] Pivot;

	public Vec3[] Normal;

	public double[] Area;

	public double[] Radius;

	public int[] FirstVertex;

	public int[] VertexCounts;

	public Vec3[] Positions;

	public float[] Normals;

	public float[] Tangents;

	public float[] Uvs;

	public float[] Edge;

	public float[] Side;

	public int[] Fragment;

	public int[] Triangles;

	public int[] Groups;

	public int VertexCount
	{
		get
		{
			if (Positions != null)
			{
				return Positions.Length;
			}
			return 0;
		}
	}

	public int TriangleCount
	{
		get
		{
			if (Triangles != null)
			{
				return Triangles.Length / 3;
			}
			return 0;
		}
	}
}
