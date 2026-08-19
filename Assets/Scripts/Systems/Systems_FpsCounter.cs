using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Small always-on FPS readout, top-left under the version stamp.
    /// Smoothed over a half-second window; text updates twice per second so
    /// the label itself never churns per frame. Rides the setup menu's
    /// UIDocument (whose root stays alive through the whole match).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_FpsCounter : MonoBehaviour
    {
        private const float UPDATE_INTERVAL_SECONDS = 0.5f;

        private Label _label;
        private float _accumulatedTime;
        private int _accumulatedFrames;

        private void Start()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            _label = new Label();
            _label.style.position = Position.Absolute;
            _label.style.top = 84f; // below the version stamp
            _label.style.left = 18f;
            _label.style.fontSize = 52f;
            _label.style.color = new Color(0.5f, 1f, 0.6f, 0.8f);
            _label.pickingMode = PickingMode.Ignore;
            root.Add(_label);
        }

        private void Update()
        {
            _accumulatedTime += Time.unscaledDeltaTime;
            _accumulatedFrames++;
            if (_accumulatedTime < UPDATE_INTERVAL_SECONDS)
            {
                return;
            }
            float fps = _accumulatedFrames / _accumulatedTime;
            _label.text = $"{fps:F0} FPS";
            _accumulatedTime = 0f;
            _accumulatedFrames = 0;
        }
    }
}
