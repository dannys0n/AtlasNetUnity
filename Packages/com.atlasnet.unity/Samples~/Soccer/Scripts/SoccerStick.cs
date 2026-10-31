using UnityEngine;
using UnityEngine.EventSystems;

namespace AtlasNet.Samples.Soccer
{
    public sealed class SoccerStick : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public static Vector2 Input { get; private set; }
        public RectTransform handle;
        int pointer = int.MinValue;
        public void OnPointerDown(PointerEventData e) { if (pointer != int.MinValue) return; pointer = e.pointerId; OnDrag(e); }
        public void OnDrag(PointerEventData e)
        {
            if (pointer != e.pointerId) return;
            var rect = (RectTransform)transform;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, e.position, e.pressEventCamera, out var point);
            Input = Vector2.ClampMagnitude(point / (rect.rect.width * .4f), 1);
            handle.anchoredPosition = Input * (rect.rect.width * .32f);
        }
        public void OnPointerUp(PointerEventData e) { if (pointer == e.pointerId) ResetStick(); }
        void OnDisable() => ResetStick();
        void ResetStick() { pointer = int.MinValue; Input = Vector2.zero; if (handle) handle.anchoredPosition = Vector2.zero; }
    }
}
