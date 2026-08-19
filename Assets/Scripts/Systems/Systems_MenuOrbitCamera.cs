using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Slow cinematic orbit around the empty ring while the setup menu is up.
    /// The spawner disables this and enables the drama camera when the
    /// contest starts. Test-scene harness only.
    /// </summary>
    public sealed class Systems_MenuOrbitCamera : MonoBehaviour
    {
        [SerializeField] private float _orbitRadius = 6.5f;
        [SerializeField] private float _orbitHeight = 2.4f;
        [SerializeField] private float _degreesPerSecond = 8f;

        private float _angleDegrees;

        private void LateUpdate()
        {
            _angleDegrees += _degreesPerSecond * Time.deltaTime;
            float radians = _angleDegrees * Mathf.Deg2Rad;
            var position = new Vector3(
                Mathf.Cos(radians) * _orbitRadius,
                _orbitHeight,
                Mathf.Sin(radians) * _orbitRadius);
            transform.position = position;
            transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0.9f, 0f) - position, Vector3.up);
        }
    }
}
