using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Floating balance bar over each fighter's head: full and green while
    /// upright, drains and turns red as the fighter tilts. Bars track head
    /// positions on screen every frame via the UI Toolkit panel transform.
    /// Self-discovers contestants at Start. Test-scene harness only — not
    /// used in training or the game loop.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_WobbleMeter : MonoBehaviour
    {
        // Head-height fractions of the start pose: full bar above UPRIGHT_FRACTION,
        // empty at COLLAPSE_FRACTION (matches Systems_BalanceContest's fall rule).
        private const float COLLAPSE_FRACTION = 0.4f;
        private const float UPRIGHT_FRACTION = 0.95f;
        private const float BAR_SMOOTH_RATE = 6f;
        private const float BAR_WIDTH = 90f;
        private const float BAR_HEIGHT = 12f;
        private const float HEAD_OFFSET_METERS = 0.35f;

        private static readonly Color FullColor = new(0.18f, 0.80f, 0.30f);
        private static readonly Color MidColor = new(0.95f, 0.75f, 0.10f);
        private static readonly Color EmptyColor = new(0.85f, 0.15f, 0.10f);

        private Camera _camera;
        private Systems_FighterRig[] _rigs;
        private VisualElement[] _bars;
        private VisualElement[] _fills;
        private float[] _smoothedBalance;
        private float[] _startHeadHeights;

        private void Start()
        {
            _camera = Camera.main;
            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            _bars = new VisualElement[_rigs.Length];
            _fills = new VisualElement[_rigs.Length];
            _smoothedBalance = new float[_rigs.Length];
            _startHeadHeights = new float[_rigs.Length];
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                _startHeadHeights[rigIndex] = _rigs[rigIndex].Head.position.y;
            }

            var root = GetComponent<UIDocument>().rootVisualElement;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                var bar = new VisualElement();
                bar.style.position = Position.Absolute;
                bar.style.width = BAR_WIDTH;
                bar.style.height = BAR_HEIGHT;
                bar.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
                bar.style.borderTopLeftRadius = 3f;
                bar.style.borderTopRightRadius = 3f;
                bar.style.borderBottomLeftRadius = 3f;
                bar.style.borderBottomRightRadius = 3f;
                bar.pickingMode = PickingMode.Ignore;

                var fill = new VisualElement();
                fill.style.position = Position.Absolute;
                fill.style.left = 1f;
                fill.style.top = 1f;
                fill.style.bottom = 1f;
                fill.style.width = BAR_WIDTH - 2f;
                fill.style.backgroundColor = FullColor;
                fill.style.borderTopLeftRadius = 2f;
                fill.style.borderTopRightRadius = 2f;
                fill.style.borderBottomLeftRadius = 2f;
                fill.style.borderBottomRightRadius = 2f;
                fill.pickingMode = PickingMode.Ignore;
                bar.Add(fill);

                root.Add(bar);
                _bars[rigIndex] = bar;
                _fills[rigIndex] = fill;
                _smoothedBalance[rigIndex] = 1f;
            }
        }

        private void LateUpdate()
        {
            if (_rigs == null || _camera == null)
            {
                return;
            }

            // Unscaled: bars must keep updating through the frozen countdown.
            float dt = Time.unscaledDeltaTime;
            IPanel panel = _bars.Length > 0 ? _bars[0].panel : null;
            if (panel == null)
            {
                return;
            }

            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                Systems_FighterRig rig = _rigs[rigIndex];
                float headFraction = rig.Head.position.y / _startHeadHeights[rigIndex];
                float balance = Mathf.Clamp01(
                    (headFraction - COLLAPSE_FRACTION) / (UPRIGHT_FRACTION - COLLAPSE_FRACTION));
                _smoothedBalance[rigIndex] = Mathf.Lerp(
                    _smoothedBalance[rigIndex], balance, dt * BAR_SMOOTH_RATE);
                float smoothed = _smoothedBalance[rigIndex];

                Vector3 worldPoint = rig.Head.position + Vector3.up * HEAD_OFFSET_METERS;
                Vector3 viewPoint = _camera.WorldToViewportPoint(worldPoint);
                VisualElement bar = _bars[rigIndex];
                if (viewPoint.z <= 0f)
                {
                    bar.style.display = DisplayStyle.None;
                    continue;
                }
                bar.style.display = DisplayStyle.Flex;

                Vector2 panelPoint = RuntimePanelUtils.CameraTransformWorldToPanel(
                    panel, worldPoint, _camera);
                bar.style.left = panelPoint.x - BAR_WIDTH * 0.5f;
                bar.style.top = panelPoint.y - BAR_HEIGHT;

                VisualElement fill = _fills[rigIndex];
                fill.style.width = Mathf.Max(0f, (BAR_WIDTH - 2f) * smoothed);
                fill.style.backgroundColor = smoothed > 0.5f
                    ? Color.Lerp(MidColor, FullColor, (smoothed - 0.5f) * 2f)
                    : Color.Lerp(EmptyColor, MidColor, smoothed * 2f);
            }
        }
    }
}
