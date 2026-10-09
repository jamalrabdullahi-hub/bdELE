using UnityEngine;

namespace VolumetricExplosionFX.Rendering;

internal delegate bool GroundProbe(Vector3 local, out Vector3 point, out Vector3 normal);
