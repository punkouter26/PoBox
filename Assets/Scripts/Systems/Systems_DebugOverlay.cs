using UnityEngine;
using UnityEngine.InputSystem;

namespace PoBox
{
    /// <summary>
    /// Debug overlay toggle for test scenes: F1 (or a four-finger touch)
    /// shows/hides the Graphy performance HUD and the in-game debug console.
    /// Both tools live as child objects wired in the scene. Reads input
    /// directly (debug harness exemption from the InputView rule).
    /// Test-scene harness only — not used in training or the game loop.
    /// </summary>
    public sealed class Systems_DebugOverlay : MonoBehaviour
    {
        [SerializeField] private GameObject _graphyRoot;
        [SerializeField] private GameObject _consoleRoot;

        private bool _visible;

        private void Start()
        {
            Apply();
        }

        private void Update()
        {
            bool toggle = Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame;
            if (!toggle && Touchscreen.current != null)
            {
                int touches = 0;
                var touchControls = Touchscreen.current.touches;
                for (int touchIndex = 0; touchIndex < touchControls.Count; touchIndex++)
                {
                    if (touchControls[touchIndex].press.isPressed)
                    {
                        touches++;
                    }
                }
                toggle = touches >= 4;
            }
            if (toggle)
            {
                _visible = !_visible;
                Apply();
            }
        }

        private void Apply()
        {
            if (_graphyRoot != null)
            {
                _graphyRoot.SetActive(_visible);
            }
            if (_consoleRoot != null)
            {
                _consoleRoot.SetActive(_visible);
            }
        }
    }
}
