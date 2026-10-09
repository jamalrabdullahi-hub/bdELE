using System;
using UnityEngine;
using VolumetricExplosionFX.Core;

namespace VolumetricExplosionFX.Ksp;

internal static class SnapshotReader
{
	internal static Vec3 Value(Vector3d v)
	{
		return new Vec3(v.x, v.y, v.z);
	}

	internal static Vec3 Value(Vector3 v)
	{
		return new Vec3(v.x, v.y, v.z);
	}

	internal static Vector3 Vector(Vec3 v)
	{
		return new Vector3((float)v.X, (float)v.Y, (float)v.Z);
	}

	internal static PartSnapshot Read(Part p)
	{
		if (p == null || p.vessel == null || p.vessel.mainBody == null || p.packed)
		{
			return null;
		}
		Vessel vessel = p.vessel;
		CelestialBody mainBody = vessel.mainBody;
		Vector3 position = p.transform.position;
		Vector3d vector3d = (Vector3d)position - mainBody.position;
		Vector3 v = vector3d.normalized;
		double altitude = vector3d.magnitude - mainBody.Radius;
		Vector3d vector3d2 = ((p.rb != null) ? ((Vector3d)p.rb.velocity) : ((Vector3d)vessel.rb_velocity)) + Krakensbane.GetFrameVelocity();
		Vector3d vector3d3 = (mainBody.inverseRotation ? Vector3d.zero : mainBody.getRFrmVel(position));
		Vector3d v2 = vector3d2 - vector3d3;
		ResourceSnapshot[] array = new ResourceSnapshot[p.Resources.Count];
		for (int i = 0; i < array.Length; i++)
		{
			PartResource partResource = p.Resources[i];
			array[i] = new ResourceSnapshot(partResource.resourceName, partResource.amount, partResource.maxAmount, partResource.amount * (double)partResource.info.density * 1000.0);
		}
		string[] array2 = new string[p.Modules.Count];
		for (int j = 0; j < array2.Length; j++)
		{
			array2[j] = p.Modules[j].moduleName;
		}
		Renderer[] componentsInChildren = p.GetComponentsInChildren<Renderer>();
		Bounds bounds = new Bounds(position, Vector3.one * 0.5f);
		for (int k = 0; k < componentsInChildren.Length; k++)
		{
			if (!(componentsInChildren[k] is ParticleSystemRenderer) && componentsInChildren[k].GetComponentInParent<Part>() == p)
			{
				bounds.Encapsulate(componentsInChildren[k].bounds);
			}
		}
		double gravity = mainBody.gravParameter / Math.Max(1.0, vector3d.sqrMagnitude);
		EnvironmentContext environment = new EnvironmentContext(mainBody.bodyName, p.atmDensity, p.staticPressureAtm * 101.325, gravity, altitude, mainBody.ocean, vessel.Landed, vessel.Splashed, Value(v));
		return new PartSnapshot(p.persistentId, p.GetInstanceID(), vessel.id, (p.partInfo != null) ? p.partInfo.name : "unknown", Value(position), Value(v2), Value(vector3d2), (p.rb != null) ? Value(p.rb.angularVelocity) : default(Vec3), Value(bounds.size), (p.mass + p.GetResourceMass()) * 1000f, p.temperature, array2, array, environment, new Quat(p.transform.rotation.x, p.transform.rotation.y, p.transform.rotation.z, p.transform.rotation.w));
	}

	internal static bool GroundBelow(Vector3 position, Vector3 up, float reach, out Vector3 point)
	{
		if (Physics.Raycast(position + up, -up, out var hitInfo, reach + 1f, SurfaceMask(), QueryTriggerInteraction.Ignore) && hitInfo.collider != null && hitInfo.collider.GetComponentInParent<Part>() == null)
		{
			point = hitInfo.point;
			return true;
		}
		point = position;
		return false;
	}

	internal static int SurfaceMask()
	{
		int num = 1073774592;
		int num2 = LayerMask.NameToLayer("TerrainColliders");
		int num3 = LayerMask.NameToLayer("Local Scenery");
		if (num2 >= 0)
		{
			num |= 1 << num2;
		}
		if (num3 >= 0)
		{
			num |= 1 << num3;
		}
		return num;
	}

	internal static bool TryGround(Part p, PartSnapshot s, out Vector3 point)
	{
		Vector3 vector = Vector(s.Environment.Up);
		Vector3 position = p.transform.position;
		float num = (float)Numbers.Clamp(s.Dimensions.Length * 0.5, 0.25, 20.0);
		int num2 = 1073774592;
		int num3 = LayerMask.NameToLayer("TerrainColliders");
		int num4 = LayerMask.NameToLayer("Local Scenery");
		if (num3 >= 0)
		{
			num2 |= 1 << num3;
		}
		if (num4 >= 0)
		{
			num2 |= 1 << num4;
		}
		if (Physics.Raycast(position + vector * num, -vector, out var hitInfo, num * 2f + 1f, num2, QueryTriggerInteraction.Ignore))
		{
			point = hitInfo.point;
			if (hitInfo.collider != null)
			{
				return hitInfo.collider.GetComponentInParent<Part>() == null;
			}
			return false;
		}
		point = position;
		return false;
	}
}
