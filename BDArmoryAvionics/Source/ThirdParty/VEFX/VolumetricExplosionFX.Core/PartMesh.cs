namespace VolumetricExplosionFX.Core;

public sealed class PartMesh
{
	public Vec3[] Positions;

	public float[] Normals;

	public float[] Tangents;

	public float[] Uvs;

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
