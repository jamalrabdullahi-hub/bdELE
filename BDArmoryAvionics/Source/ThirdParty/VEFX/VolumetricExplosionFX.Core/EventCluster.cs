namespace VolumetricExplosionFX.Core;

public sealed class EventCluster
{
	public int Id;

	public int Members;

	public DestructionEvent Representative;

	public Vec3 Position;

	public double FirstTime;

	public double LastTime;

	public double TotalScore;

	public double RepresentativeScore;

	public bool Ground;

	public Vec3 GroundPoint;

	private double groundScore;

	public const int MaxSamples = 3;

	public readonly Vec3[] SamplePositions = new Vec3[3];

	public readonly double[] SampleTimes = new double[3];

	public readonly double[] SampleScores = new double[3];

	public int SampleCount;

	internal void Note(DestructionEvent e, double score)
	{
		if (e.Kind == EventKind.GroundImpact && (!Ground || !(score <= groundScore)))
		{
			Ground = true;
			GroundPoint = e.Part.Position;
			groundScore = score;
		}
	}

	internal void Sample(Vec3 position, double time, double score)
	{
		int num = ((SampleCount < 3) ? SampleCount++ : (-1));
		if (num < 0)
		{
			int num2 = 0;
			for (int i = 1; i < 3; i++)
			{
				if (SampleScores[i] < SampleScores[num2])
				{
					num2 = i;
				}
			}
			if (score <= SampleScores[num2])
			{
				return;
			}
			num = num2;
		}
		SamplePositions[num] = position;
		SampleTimes[num] = time;
		SampleScores[num] = score;
	}
}
