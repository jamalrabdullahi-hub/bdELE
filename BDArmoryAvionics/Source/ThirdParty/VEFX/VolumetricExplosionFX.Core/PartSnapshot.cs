using System;

namespace VolumetricExplosionFX.Core;

public sealed class PartSnapshot
{
	public readonly uint PersistentId;

	public readonly int InstanceId;

	public readonly Guid VesselId;

	public readonly string Name;

	public readonly Vec3 Position;

	public readonly Vec3 SurfaceVelocity;

	public readonly Vec3 InertialVelocity;

	public readonly Vec3 AngularVelocity;

	public readonly Vec3 Dimensions;

	public readonly double MassKg;

	public readonly double Temperature;

	public readonly Quat Rotation;

	public readonly string[] Modules;

	public readonly ResourceSnapshot[] Resources;

	public readonly EnvironmentContext Environment;

	public PartSnapshot(uint id, int instanceId, Guid vessel, string name, Vec3 position, Vec3 surfaceVelocity, Vec3 inertialVelocity, Vec3 angularVelocity, Vec3 dimensions, double massKg, double temperature, string[] modules, ResourceSnapshot[] resources, EnvironmentContext environment, Quat? rotation = null)
	{
		PersistentId = id;
		InstanceId = instanceId;
		VesselId = vessel;
		Name = name;
		Position = position;
		SurfaceVelocity = surfaceVelocity;
		InertialVelocity = inertialVelocity;
		AngularVelocity = angularVelocity;
		Dimensions = dimensions;
		MassKg = Numbers.Positive(massKg);
		Temperature = Numbers.Positive(temperature);
		Modules = modules ?? new string[0];
		Resources = resources ?? new ResourceSnapshot[0];
		Environment = environment;
		Rotation = rotation ?? new Quat(0.0, 0.0, 0.0, 1.0);
	}

	public PartSnapshot At(Vec3 position)
	{
		return new PartSnapshot(PersistentId, InstanceId, VesselId, Name, position, SurfaceVelocity, InertialVelocity, AngularVelocity, Dimensions, MassKg, Temperature, Modules, Resources, Environment, Rotation);
	}
}
