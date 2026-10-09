using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using VolumetricExplosionFX.Core;
using VolumetricExplosionFX.Ksp;

namespace VolumetricExplosionFX.Rendering;

internal sealed class DebrisField : IDisposable
{
	private sealed class Job
	{
		internal PartMesh Mesh;

		internal FxPlan Plan;

		internal int Pieces;

		internal int MaxTriangles;

		internal uint Seed;

		internal Vec3 Center;

		internal Vec3 Velocity;

		internal bool Surface;

		internal bool Landing;

		internal double Floor;

		internal double Gravity;

		internal double Horizon;

		internal SkinLook[] Looks;

		internal Quaternion Frame;

		internal Vector3 Origin;

		internal Vector3d InertialVelocity;

		internal CelestialBody Body;

		internal double Latitude;

		internal double Longitude;

		internal double Altitude;

		internal double Sea;

		internal bool Inertial;

		internal bool Water;

		internal float Born;

		internal float Air;

		internal int Generation;

		internal FractureResult Cut;

		internal FragmentMotion[] Motion;

		internal Exception Error;

		internal Queue<Job> Finished;
	}

	private sealed class Batch
	{
		internal GameObject Root;

		internal MeshRenderer Renderer;

		internal Mesh Mesh;

		internal ParticleSystem Fire;

		internal ParticleSystem Smoke;

		internal ParticleSystem Flames;

		internal readonly DebrisTrails Trails = new DebrisTrails();

		internal TrailShower Tracers;

		internal float TraceStart;

		internal readonly List<int> Traced = new List<int>();

		internal readonly List<Vector3> TracerAt = new List<Vector3>();

		internal FragmentMotion[] Motion;

		internal double Gravity;

		internal readonly MaterialPropertyBlock Properties = new MaterialPropertyBlock();

		internal Material[] Skins;

		internal bool Active;

		internal bool Inertial;

		internal float Age;

		internal float End;

		internal CelestialBody Body;

		internal double Latitude;

		internal double Longitude;

		internal double Altitude;

		internal Vector3d Velocity;

		internal int Pieces;
	}

	private sealed class Prepared
	{
		internal CapturedPart Part;

		internal int Frame;
	}

	private static readonly Quat Identity = new Quat(0.0, 0.0, 0.0, 1.0);

	private static readonly int AgeId = Shader.PropertyToID("_Age");

	private static readonly int GravityId = Shader.PropertyToID("_Gravity");

	private static readonly int LifeId = Shader.PropertyToID("_Life");

	private static readonly int CoolId = Shader.PropertyToID("_Cool");

	private const int MaxInFlight = 2;

	private const int MaxQueued = 48;

	private readonly VolumeAssets assets;

	private readonly Material glow;

	private readonly FxSettings settings;

	private readonly Batch[] batches;

	private readonly Queue<Job> waiting = new Queue<Job>();

	private readonly Queue<Job> finished = new Queue<Job>();

	private readonly List<Job> running = new List<Job>();

	private readonly Dictionary<string, Material> skins = new Dictionary<string, Material>();

	private readonly Dictionary<int, Prepared> prepared = new Dictionary<int, Prepared>();

	private readonly List<int> stale = new List<int>();

	private int inFlight;

	private int generation;

	private int captureFrame = -1;

	private int capturedThisFrame;

	internal int Captured { get; private set; }

	internal int Missed { get; private set; }

	internal int Dropped { get; private set; }

	internal int Failed { get; private set; }

	internal int Active
	{
		get
		{
			int num = 0;
			for (int i = 0; i < batches.Length; i++)
			{
				if (batches[i] != null && batches[i].Active)
				{
					num++;
				}
			}
			return num;
		}
	}

	internal int Pieces
	{
		get
		{
			int num = 0;
			for (int i = 0; i < batches.Length; i++)
			{
				if (batches[i] != null && batches[i].Active)
				{
					num += batches[i].Pieces;
				}
			}
			return num;
		}
	}

	internal int Pending => waiting.Count + inFlight;

	internal bool Available
	{
		get
		{
			if (assets.Fragment != null && assets.Noise != null)
			{
				return batches.Length != 0;
			}
			return false;
		}
	}

	internal DebrisField(FxSettings s, VolumeAssets volumes, Material glowMaterial)
	{
		settings = s;
		assets = volumes;
		glow = glowMaterial;
		batches = new Batch[Math.Max(0, s.MaxDebrisParts)];
	}

	internal void Prepare(Part part)
	{
		if (!Available || !settings.PartDebris || part == null || part.vessel == null || part.vessel.mainBody == null)
		{
			return;
		}
		int instanceID = part.GetInstanceID();
		if (prepared.ContainsKey(instanceID))
		{
			return;
		}
		if (Time.frameCount != captureFrame)
		{
			captureFrame = Time.frameCount;
			capturedThisFrame = 0;
			Prune();
		}
		if (capturedThisFrame >= Math.Max(6, batches.Length))
		{
			Dropped++;
			return;
		}
		capturedThisFrame++;
		Vector3 up = ((Vector3d)part.transform.position - part.vessel.mainBody.position).normalized;
		CapturedPart capturedPart = PartCapture.Read(part, up, settings.DebrisTriangles);
		if (capturedPart != null)
		{
			prepared[instanceID] = new Prepared
			{
				Part = capturedPart,
				Frame = Time.frameCount
			};
		}
	}

	private void Prune()
	{
		if (prepared.Count == 0)
		{
			return;
		}
		stale.Clear();
		foreach (KeyValuePair<int, Prepared> item in prepared)
		{
			if (Time.frameCount - item.Value.Frame > 30)
			{
				stale.Add(item.Key);
			}
		}
		for (int i = 0; i < stale.Count; i++)
		{
			prepared.Remove(stale[i]);
		}
	}

	internal void Capture(Part part, DestructionEvent e, FxPlan plan)
	{
		if (Available && settings.PartDebris && !(part == null) && !(part.vessel == null) && !(part.vessel.mainBody == null))
		{
			if (!prepared.TryGetValue(part.GetInstanceID(), out var value))
			{
				Missed++;
				return;
			}
			prepared.Remove(part.GetInstanceID());
			Enqueue(value.Part, e, plan, part.vessel.mainBody);
		}
	}

	internal void Capture(CapturedPart captured, DestructionEvent e, FxPlan plan, CelestialBody body)
	{
		if (Available && settings.PartDebris && captured != null && !(body == null))
		{
			Enqueue(captured, e, plan, body);
		}
	}

	private void Enqueue(CapturedPart captured, DestructionEvent e, FxPlan plan, CelestialBody body)
	{
		PartSnapshot part = e.Part;
		Vector3 up = captured.Frame * Vector3.up;
		if (waiting.Count >= 48)
		{
			waiting.Dequeue();
			Dropped++;
		}
		double num = Numbers.Clamp(plan.Atmosphere, 0.0, 1.0);
		Job job = new Job
		{
			Mesh = captured.Mesh,
			Plan = plan,
			MaxTriangles = settings.DebrisTriangles,
			Looks = captured.Looks,
			Frame = captured.Frame,
			Origin = captured.Origin,
			Body = body,
			Born = Time.time,
			Air = (float)num,
			Generation = generation,
			Gravity = part.Environment.Gravity,
			Horizon = settings.ResidueSeconds + 5.0,
			Center = captured.Center
		};
		job.Seed = (uint)(((int)part.PersistentId * -1640531535) ^ (Time.frameCount * 40503));
		job.Pieces = DebrisPlanner.Pieces(plan, captured.Size);
		double num2 = Floor(body, captured.Origin, up, (float)captured.Size + 3f);
		job.Sea = (body.ocean ? (0.0 - body.GetAltitude(captured.Origin)) : double.NegativeInfinity);
		if (job.Sea > num2)
		{
			num2 = job.Sea;
			job.Water = true;
		}
		job.Floor = num2;
		Quaternion quaternion = Quaternion.Inverse(captured.Frame);
		job.Inertial = num < 0.02 && num2 < -2000.0;
		if (job.Inertial)
		{
			job.InertialVelocity = new Vector3d(part.InertialVelocity.X, part.InertialVelocity.Y, part.InertialVelocity.Z);
			job.Gravity = 0.0;
			job.Landing = false;
			job.Velocity = default;
		}
		else
		{
			job.Latitude = body.GetLatitude(captured.Origin);
			job.Longitude = body.GetLongitude(captured.Origin);
			job.Altitude = body.GetAltitude(captured.Origin);
			Vector3 vector = quaternion * SnapshotReader.Vector(part.SurfaceVelocity);
			job.Velocity = new Vec3(vector.x, vector.y, vector.z);
			job.Landing = !double.IsNegativeInfinity(num2);
		}
		job.Surface = e.Kind != EventKind.Destruction || num2 > 0.0 - (captured.Size + 4.0);
		waiting.Enqueue(job);
		Captured++;
	}

	internal static double Floor(CelestialBody body, Vector3 origin, Vector3 up, float lift)
	{
		if (Physics.Raycast(origin + up * lift, -up, out var hitInfo, 1500f + lift, SnapshotReader.SurfaceMask(), QueryTriggerInteraction.Ignore) && hitInfo.collider != null && hitInfo.collider.GetComponentInParent<Part>() == null)
		{
			return Vector3.Dot(hitInfo.point - origin, up);
		}
		if (body.pqsController == null)
		{
			return double.NegativeInfinity;
		}
		double altitude = body.GetAltitude(origin);
		return body.TerrainAltitude(body.GetLatitude(origin), body.GetLongitude(origin), allowNegative: true) - altitude;
	}

	private static void Work(object state)
	{
		Job job = (Job)state;
		try
		{
			job.Cut = Fracture.Cut(job.Mesh, job.Pieces, job.Seed, job.MaxTriangles, 1.0);
			if (job.Cut != null)
			{
				job.Motion = DebrisPlanner.Launch(job.Plan, job.Cut, Identity, default, job.Center, job.Velocity, job.Surface, job.Seed);
				if (job.Landing)
				{
					for (int i = 0; i < job.Motion.Length; i++)
					{
						DebrisPlanner.Land(job.Cut, i, Identity, ref job.Motion[i], job.Floor, job.Gravity, job.Horizon, job.Water);
					}
				}
			}
		}
		catch (Exception error)
		{
			job.Error = error;
			job.Cut = null;
		}
		try
		{
			Queue<Job> queue = job.Finished;
			lock (queue)
			{
				queue.Enqueue(job);
			}
		}
		catch (Exception)
		{
		}
	}

	internal void Tick(float dt, Camera cam)
	{
		while (true)
		{
			Job job = null;
			lock (finished)
			{
				if (finished.Count > 0)
				{
					job = finished.Dequeue();
				}
			}
			if (job == null)
			{
				break;
			}
			inFlight--;
			running.Remove(job);
			if (job.Generation != generation)
			{
				continue;
			}
			if (job.Cut == null || job.Motion == null)
			{
				Failed++;
				continue;
			}
			try
			{
				Apply(job);
			}
			catch (Exception ex)
			{
				Failed++;
				if (Failed <= 2)
				{
					Debug.LogWarning("[VEFX] Part debris skipped: " + ex.GetType().Name + ": " + ex.Message);
				}
			}
		}
		while (inFlight < 2 && waiting.Count > 0)
		{
			Job job2 = waiting.Dequeue();
			job2.Finished = finished;
			inFlight++;
			running.Add(job2);
			if (!ThreadPool.QueueUserWorkItem(Work, job2))
			{
				inFlight--;
				running.Remove(job2);
				Failed++;
			}
		}
		for (int i = 0; i < batches.Length; i++)
		{
			Batch batch = batches[i];
			if (batch == null || !batch.Active)
			{
				continue;
			}
			batch.Age += dt;
			if (batch.Inertial)
			{
				batch.Root.transform.position += (Vector3)((batch.Velocity - Krakensbane.GetFrameVelocity()) * dt);
			}
			else if (batch.Body != null)
			{
				batch.Root.transform.position = batch.Body.GetWorldSurfacePosition(batch.Latitude, batch.Longitude, batch.Altitude);
			}
			batch.Properties.SetFloat(AgeId, batch.Age);
			batch.Renderer.SetPropertyBlock(batch.Properties);
			if (batch.Fire != null)
			{
				batch.Trails.Tick(batch.Age, dt, batch.Fire, batch.Smoke, batch.Flames);
			}
			if (batch.Tracers != null && batch.Traced.Count > 0)
			{
				for (int j = 0; j < batch.Traced.Count; j++)
				{
					Vec3 vec = DebrisPlanner.Where(batch.Motion[batch.Traced[j]], batch.Gravity, batch.Age);
					batch.TracerAt[j] = new Vector3((float)vec.X, (float)vec.Y, (float)vec.Z);
				}
				batch.Tracers.Update(batch.Age - batch.TraceStart, batch.TracerAt);
			}
			if (batch.Age > batch.End)
			{
				Release(batch);
			}
		}
	}

	private void Apply(Job job)
	{
		Batch batch = Take();
		if (batch == null)
		{
			Dropped++;
			return;
		}
		float num = Mathf.Max(0f, Time.time - job.Born);
		Vector3 vector = ((!job.Inertial) ? ((Vector3)job.Body.GetWorldSurfacePosition(job.Latitude, job.Longitude, job.Altitude)) : (job.Origin + (Vector3)((job.InertialVelocity - Krakensbane.GetFrameVelocity()) * num)));
		if (job.Landing)
		{
			Refine(job, vector);
		}
		FragmentMeshBuilder.Build(batch.Mesh, job.Cut, job.Motion, Quaternion.identity, Vector3.zero, job.Looks.Length, job.Gravity, job.Horizon, job.Seed);
		if (batch.Skins == null || batch.Skins.Length != job.Looks.Length)
		{
			batch.Skins = new Material[job.Looks.Length];
		}
		for (int i = 0; i < job.Looks.Length; i++)
		{
			batch.Skins[i] = Skin(job.Looks[i]);
		}
		batch.Renderer.sharedMaterials = batch.Skins;
		batch.Root.transform.SetPositionAndRotation(vector, job.Frame);
		batch.Inertial = job.Inertial;
		batch.Velocity = job.InertialVelocity;
		batch.Body = job.Body;
		batch.Latitude = job.Latitude;
		batch.Longitude = job.Longitude;
		batch.Altitude = job.Altitude;
		float a = (job.Inertial ? Mathf.Min((float)settings.ResidueSeconds, 30f) : ((float)settings.ResidueSeconds));
		batch.End = Mathf.Max(a, 8f);
		batch.Age = num;
		batch.Pieces = job.Cut.Count;
		batch.Properties.Clear();
		batch.Properties.SetFloat(AgeId, num);
		batch.Properties.SetFloat(GravityId, (float)job.Gravity);
		batch.Properties.SetVector(LifeId, new Vector4(batch.End - 4f, batch.End, 0f, 0f));
		batch.Properties.SetFloat(CoolId, (job.Air > 0.05f) ? 1.8f : 1.4f);
		batch.Renderer.SetPropertyBlock(batch.Properties);
		batch.Root.SetActive(value: true);
		batch.Renderer.enabled = true;
		batch.Active = true;
		if (batch.Fire != null)
		{
			batch.Trails.FlyingPuffs = batch.Tracers == null;
			batch.Trails.Begin(job.Motion, job.Gravity, 6, job.Plan, job.Seed, job.Water ? 0f : ((float)settings.ResidueSeconds), job.Water);
			batch.Fire.Play();
			batch.Smoke.Play();
			if (batch.Flames != null)
			{
				batch.Flames.Play();
			}
		}
		batch.Motion = job.Motion;
		batch.Gravity = job.Gravity;
		batch.Traced.Clear();
		batch.TracerAt.Clear();
		if (batch.Tracers != null && job.Air > 0.15f)
		{
			StartTracers(batch, job, num);
		}
	}

	private void StartTracers(Batch b, Job job, float age)
	{
		float[] array = new float[6];
		Vector3[] array2 = new Vector3[6];
		for (int i = 0; i < job.Motion.Length; i++)
		{
			if (b.Traced.Count >= 6)
			{
				break;
			}
			FragmentMotion m = job.Motion[i];
			if (m.Burning)
			{
				float num = (float)Math.Min(m.BurnTime, m.LandTime) - age;
				if (!(num <= 0.1f))
				{
					Vec3 vec = DebrisPlanner.Where(m, job.Gravity, age);
					array[b.Traced.Count] = num;
					array2[b.Traced.Count] = new Vector3((float)vec.X, (float)vec.Y, (float)vec.Z);
					b.Traced.Add(i);
					b.TracerAt.Add(array2[b.Traced.Count - 1]);
				}
			}
		}
		if (b.Traced.Count != 0)
		{
			b.TraceStart = age;
			FxPlan plan = job.Plan;
			float num2 = (float)(1.0 - 0.5 * Numbers.Clamp(plan.Soot, 0.0, 1.0));
			Color smoke = new Color((float)plan.SmokeR * num2, (float)plan.SmokeG * num2, (float)plan.SmokeB * num2, 1f);
			b.Tracers.Trace(b.Traced.Count, smoke, 1.2 + 2.2 * (double)job.Air, array, array2, new Color(1f, 0.66f, 0.32f, 1f), 0.9f);
		}
	}

	private void Refine(Job job, Vector3 origin)
	{
		Vector3 vector = job.Frame * Vector3.up;
		int layerMask = SnapshotReader.SurfaceMask();
		for (int i = 0; i < job.Motion.Length; i++)
		{
			FragmentMotion fragmentMotion = job.Motion[i];
			if (!double.IsInfinity(fragmentMotion.LandTime))
			{
				Vec3 vec = Ballistics.Position(fragmentMotion.Pivot, fragmentMotion.Velocity, fragmentMotion.Drag, job.Gravity, fragmentMotion.LandTime);
				Vector3 vector2 = origin + job.Frame * new Vector3((float)vec.X, (float)job.Floor, (float)vec.Z);
				double num = job.Floor;
				bool flag = job.Water;
				if (Physics.Raycast(vector2 + vector * 80f, -vector, out var hitInfo, 200f, layerMask, QueryTriggerInteraction.Ignore) && hitInfo.collider != null && hitInfo.collider.GetComponentInParent<Part>() == null)
				{
					double num2 = Vector3.Dot(hitInfo.point - origin, vector);
					flag = job.Sea > num2;
					num = (flag ? job.Sea : num2);
				}
				if (Math.Abs(num - job.Floor) > 0.25 || flag != job.Water)
				{
					DebrisPlanner.Land(job.Cut, i, Identity, ref job.Motion[i], num, job.Gravity, job.Horizon, flag);
				}
				if (flag && !double.IsInfinity(job.Motion[i].LandTime))
				{
					job.Motion[i].RestHeight -= 1.5;
				}
			}
		}
	}

	private Batch Take()
	{
		Batch batch = null;
		for (int i = 0; i < batches.Length; i++)
		{
			if (batches[i] == null)
			{
				batches[i] = Create(i);
			}
			if (!batches[i].Active)
			{
				return batches[i];
			}
			if (batch == null || batches[i].Age > batch.Age)
			{
				batch = batches[i];
			}
		}
		if (batch != null)
		{
			Release(batch);
			Dropped++;
		}
		return batch;
	}

	private Batch Create(int index)
	{
		Batch batch = new Batch();
		batch.Root = new GameObject("VolumetricExplosionFX part wreckage " + index);
		batch.Root.layer = EffectLayer.Value;
		batch.Mesh = new Mesh();
		batch.Mesh.name = "Original pieces of a destroyed part";
		batch.Root.AddComponent<MeshFilter>().sharedMesh = batch.Mesh;
		batch.Renderer = batch.Root.AddComponent<MeshRenderer>();
		batch.Renderer.shadowCastingMode = ShadowCastingMode.On;
		batch.Renderer.receiveShadows = false;
		batch.Renderer.lightProbeUsage = LightProbeUsage.Off;
		batch.Renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
		if (assets.SmokeFire != null)
		{
			batch.Fire = SmokeParticles.Create(batch.Root.transform, "burning piece flames", assets.SmokeFire, 96, 0f, 0.7f);
			batch.Smoke = SmokeParticles.Create(batch.Root.transform, "burning piece smoke", assets.SmokeCloud, 192, 0.35f, 1f);
			if (assets.Flame != null)
			{
				batch.Flames = SmokeParticles.CreateFlameSheets(batch.Root.transform, "burning piece flames (procedural)", assets.Flame, 64);
			}
		}
		if (assets.TrailMaterial != null && glow != null)
		{
			batch.Tracers = new TrailShower(batch.Root.transform, "burning piece trails", glow, assets.TrailMaterial, 8);
		}
		batch.Root.SetActive(value: false);
		return batch;
	}

	private Material Skin(SkinLook look)
	{
		string[] array = new string[7]
		{
			((look.Texture != null) ? look.Texture.GetInstanceID() : 0).ToString(),
			"|",
			null,
			null,
			null,
			null,
			null
		};
		Color color = look.Color;
		array[2] = color.ToString();
		array[3] = "|";
		Vector2 scale = look.Scale;
		array[4] = scale.ToString();
		array[5] = "|";
		scale = look.Offset;
		array[6] = scale.ToString();
		string key = string.Concat(array);
		if (skins.TryGetValue(key, out var value) && value != null)
		{
			return value;
		}
		if (skins.Count >= 96)
		{
			TrimSkins();
		}
		value = new Material(assets.Fragment);
		value.name = "Original part fragment skin";
		if (look.Texture != null)
		{
			value.SetTexture("_MainTex", look.Texture);
		}
		value.SetTextureScale("_MainTex", look.Scale);
		value.SetTextureOffset("_MainTex", look.Offset);
		value.SetColor("_Color", look.Color);
		value.SetTexture("_NoiseTex", assets.Noise);
		skins[key] = value;
		return value;
	}

	private void TrimSkins()
	{
		HashSet<Material> hashSet = new HashSet<Material>();
		for (int i = 0; i < batches.Length; i++)
		{
			if (batches[i] != null && batches[i].Active && batches[i].Skins != null)
			{
				hashSet.UnionWith(batches[i].Skins);
			}
		}
		foreach (string item in new List<string>(skins.Keys))
		{
			if (!hashSet.Contains(skins[item]))
			{
				UnityEngine.Object.Destroy(skins[item]);
				skins.Remove(item);
			}
		}
	}

	private void Release(Batch b)
	{
		b.Active = false;
		b.Renderer.enabled = false;
		b.Pieces = 0;
		if (b.Fire != null)
		{
			b.Trails.Stop();
			b.Fire.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
			b.Smoke.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
		}
		if (b.Flames != null)
		{
			b.Flames.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
		}
		if (b.Tracers != null)
		{
			b.Tracers.Clear();
		}
		b.Traced.Clear();
		b.TracerAt.Clear();
		b.Mesh.Clear();
		b.Root.SetActive(value: false);
	}

	internal void Shift(Vector3 offset)
	{
		for (int i = 0; i < batches.Length; i++)
		{
			if (batches[i] != null && batches[i].Active && batches[i].Inertial)
			{
				batches[i].Root.transform.position -= offset;
			}
		}
		foreach (Job item in waiting)
		{
			item.Origin -= offset;
		}
		for (int j = 0; j < running.Count; j++)
		{
			running[j].Origin -= offset;
		}
	}

	internal void Clear()
	{
		generation++;
		waiting.Clear();
		prepared.Clear();
		for (int i = 0; i < batches.Length; i++)
		{
			if (batches[i] != null && batches[i].Active)
			{
				Release(batches[i]);
			}
		}
	}

	public void Dispose()
	{
		generation++;
		waiting.Clear();
		prepared.Clear();
		for (int i = 0; i < batches.Length; i++)
		{
			if (batches[i] != null)
			{
				if (batches[i].Root != null)
				{
					UnityEngine.Object.Destroy(batches[i].Root);
				}
				if (batches[i].Mesh != null)
				{
					UnityEngine.Object.Destroy(batches[i].Mesh);
				}
			}
		}
		foreach (Material value in skins.Values)
		{
			if (value != null)
			{
				UnityEngine.Object.Destroy(value);
			}
		}
		skins.Clear();
	}
}
