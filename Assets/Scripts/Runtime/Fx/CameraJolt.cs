using Unity.Cinemachine;
using UnityEngine;
using PoBox.Broadcast;
using PoBox.Sim;

namespace PoBox.Fx
{
    /// <summary>
    /// The picture jolted by a heavy punch or a body landing, for a camera Cinemachine does not drive: the
    /// free-for-all's <see cref="MeleeCamera"/>. (The arena's broadcast cameras shake already: the broadcast
    /// director fires the impulses and every virtual camera there has an impulse listener.)
    ///
    /// It fires Cinemachine impulses (so the shake's shape and decay are Cinemachine's, real time) and, being
    /// on the camera, also listens to them: after the camera has placed itself for the frame, the impulse's
    /// offset is laid on top. Put on the camera by PoBox/Do All/Graphics; runs after MeleeCamera.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class CameraJolt : MonoBehaviour
    {
        [Tooltip("Impulse of a punch that shakes the picture at all, and that shakes it fully, N s.")]
        public Vector2 hitImpulse = new Vector2(8f, 25f);
        [Tooltip("Impact of a body on the canvas that shakes it fully, N s.")]
        public float fullFall = 120f;
        [Tooltip("How hard the fullest shake is (the impulse's velocity, m/s).")]
        public float strength = 0.35f;

        CinemachineImpulseSource _source;
        Vector3 _offset, _placed;
        Quaternion _turn = Quaternion.identity;

        void Awake()
        {
            _source = gameObject.AddComponent<CinemachineImpulseSource>();
            CinemachineImpulseDefinition def = _source.ImpulseDefinition;
            // As the broadcast director's: Dissipating (Legacy needs a signal asset), a short bump, its own channel.
            def.ImpulseType = CinemachineImpulseDefinition.ImpulseTypes.Dissipating;
            def.ImpulseShape = CinemachineImpulseDefinition.ImpulseShapes.Bump;
            def.ImpulseDuration = 0.22f;
            def.DissipationDistance = 40f;
            def.DissipationRate = 0.25f;
            def.ImpulseChannel = 2;
            CinemachineImpulseManager.Instance.IgnoreTimeScale = true;
        }

        void OnEnable()
        {
            SimBus.Hit += OnHit;
            SimBus.FloorImpact += OnFloor;
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SimBus.FloorImpact -= OnFloor;
        }

        void OnHit(HitEvent e)
        {
            float k = Mathf.InverseLerp(hitImpulse.x, hitImpulse.y, e.impulse);
            if (k <= 0f) return;
            Vector3 n = e.normal.sqrMagnitude > 0.01f ? e.normal.normalized : Vector3.down;
            _source.GenerateImpulseAtPositionWithVelocity(e.point, -n * (k * strength));
        }

        void OnFloor(Vector3 point, float impulse)
        {
            float k = Mathf.Clamp01(impulse / fullFall);
            if (k > 0.3f) _source.GenerateImpulseAtPositionWithVelocity(point, Vector3.down * (k * strength));
        }

        void LateUpdate()
        {
            // The camera did not place itself this frame: take off last frame's shake before laying on this one.
            if (transform.position == _placed) transform.SetPositionAndRotation(transform.position - _offset, transform.rotation * Quaternion.Inverse(_turn));
            CinemachineImpulseManager.Instance.GetImpulseAt(transform.position, false, 2, out _offset, out _turn);      // channel 2 (a mask: bit 1), not the broadcast's 1
            transform.SetPositionAndRotation(transform.position + _offset, transform.rotation * _turn);
            _placed = transform.position;
        }
    }
}
