using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace VolumetricExplosionFX.Rendering;

internal sealed class UiWindowDrag : MonoBehaviour, IDragHandler, IEventSystemHandler
{
	internal Action<Vector2> Move;

	public void OnDrag(PointerEventData e)
	{
		if (e.button == PointerEventData.InputButton.Left && Move != null)
		{
			Move(e.delta);
		}
	}
}
