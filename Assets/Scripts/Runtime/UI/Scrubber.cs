using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox.UI
{
    /// <summary>
    /// The replay's timeline: a track, a fill up to the playhead, a tick where the moment itself is, and a
    /// knob to drag. Hand-built rather than a Slider because the stock one is styled for a mouse and an
    /// inspector, and this one has to be grabbed by a thumb.
    /// </summary>
    [UxmlElement]
    public partial class Scrubber : VisualElement
    {
        readonly VisualElement _track, _fill, _mark, _knob;
        float _value;

        /// <summary>Raised while the knob is dragged, with the new position 0..1.</summary>
        public event Action<float> scrubbed;
        public bool Dragging { get; private set; }

        public Scrubber()
        {
            AddToClassList("scrub");
            _track = new VisualElement { pickingMode = PickingMode.Ignore };
            _track.AddToClassList("scrub-track");
            _fill = new VisualElement { pickingMode = PickingMode.Ignore };
            _fill.AddToClassList("scrub-fill");
            _mark = new VisualElement { pickingMode = PickingMode.Ignore };
            _mark.AddToClassList("scrub-mark");
            _knob = new VisualElement { pickingMode = PickingMode.Ignore };
            _knob.AddToClassList("scrub-knob");
            _track.Add(_fill);
            _track.Add(_mark);
            Add(_track);
            Add(_knob);

            RegisterCallback<PointerDownEvent>(OnDown);
            RegisterCallback<PointerMoveEvent>(OnMove);
            RegisterCallback<PointerUpEvent>(OnUp);
            RegisterCallback<PointerCancelEvent>(e => Release(e.pointerId));
            RegisterCallback<GeometryChangedEvent>(e => Layout());
        }

        public float value
        {
            get => _value;
            set { _value = Mathf.Clamp01(value); Layout(); }
        }

        /// <summary>Where the tick sits, 0..1. Negative hides it.</summary>
        public void SetMark(float position)
        {
            _mark.style.display = position < 0f ? DisplayStyle.None : DisplayStyle.Flex;
            _mark.style.left = Length.Percent(Mathf.Clamp01(position) * 100f);
        }

        void Layout()
        {
            _fill.style.width = Length.Percent(_value * 100f);
            float w = contentRect.width;
            if (w > 1f) _knob.style.left = _value * w - _knob.resolvedStyle.width * 0.5f;
        }

        void OnDown(PointerDownEvent e)
        {
            Dragging = true;
            this.CapturePointer(e.pointerId);
            Drag(e.localPosition.x);
            e.StopPropagation();
        }

        void OnMove(PointerMoveEvent e)
        {
            if (Dragging && this.HasPointerCapture(e.pointerId)) Drag(e.localPosition.x);
        }

        void OnUp(PointerUpEvent e) => Release(e.pointerId);

        void Release(int pointerId)
        {
            Dragging = false;
            if (this.HasPointerCapture(pointerId)) this.ReleasePointer(pointerId);
        }

        void Drag(float x)
        {
            float w = contentRect.width;
            if (w < 1f) return;
            value = x / w;
            scrubbed?.Invoke(_value);
        }
    }
}
