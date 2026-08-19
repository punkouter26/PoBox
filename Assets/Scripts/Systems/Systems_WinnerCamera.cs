using Unity.Cinemachine;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Cinemachine winner shot: when a round ends, a virtual camera slowly
    /// orbits the winner while the drama camera stands down; when the next
    /// round starts, control returns to the drama camera. The Cinemachine
    /// brain only drives the camera while the virtual camera is live.
    /// Test-scene harness only.
    /// </summary>
    public sealed class Systems_WinnerCamera : MonoBehaviour
    {
        private const float ORBIT_DEGREES_PER_SECOND = 30f;
        private const float ORBIT_RADIUS = 2.6f;
        private const float ORBIT_HEIGHT = 1.3f;

        [SerializeField] private CinemachineCamera _virtualCamera;
        [SerializeField] private Systems_DramaCamera _dramaCamera;

        private Systems_BalanceContest _contest;
        private Systems_FighterRig[] _rigs;
        private Transform _focus;
        private float _angleDegrees;
        private bool _active;

        // Called by the editor scene tool.
        public void EditorInitialize(CinemachineCamera virtualCamera, Systems_DramaCamera dramaCamera)
        {
            _virtualCamera = virtualCamera;
            _dramaCamera = dramaCamera;
        }

        private void Start()
        {
            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            _contest = FindFirstObjectByType<Systems_BalanceContest>();
            if (_contest != null)
            {
                _contest.RoundEnded += OnRoundEnded;
                _contest.RoundStarted += OnRoundStarted;
            }
            if (_virtualCamera != null)
            {
                _virtualCamera.gameObject.SetActive(false);
            }
        }

        private void OnDestroy()
        {
            if (_contest != null)
            {
                _contest.RoundEnded -= OnRoundEnded;
                _contest.RoundStarted -= OnRoundStarted;
            }
        }

        private void OnRoundEnded(string winnerName)
        {
            _focus = null;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                if (_rigs[rigIndex].gameObject.name.Contains(winnerName))
                {
                    _focus = _rigs[rigIndex].Pelvis.transform;
                    break;
                }
            }
            if (_focus == null || _virtualCamera == null)
            {
                return;
            }
            _active = true;
            _angleDegrees = 0f;
            if (_dramaCamera != null)
            {
                _dramaCamera.enabled = false;
            }
            _virtualCamera.LookAt = _focus;
            _virtualCamera.gameObject.SetActive(true);
        }

        private void OnRoundStarted(int round)
        {
            _active = false;
            if (_virtualCamera != null)
            {
                _virtualCamera.gameObject.SetActive(false);
            }
            if (_dramaCamera != null)
            {
                _dramaCamera.enabled = true;
            }
        }

        private void LateUpdate()
        {
            if (!_active || _focus == null || _virtualCamera == null)
            {
                return;
            }
            // Unscaled: the winner shot plays through the knockout slow-mo.
            _angleDegrees += ORBIT_DEGREES_PER_SECOND * Time.unscaledDeltaTime;
            float radians = _angleDegrees * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians)) * ORBIT_RADIUS;
            _virtualCamera.transform.position = _focus.position + offset + Vector3.up * ORBIT_HEIGHT;
        }
    }
}
