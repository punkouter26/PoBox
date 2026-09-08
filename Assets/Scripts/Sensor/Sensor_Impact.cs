using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Reports how hard this body was just hit, so the presentation layer can
    /// scale a scuff, a thud and a camera shake by it instead of playing the
    /// same burst for a brush and for a faceplant.
    ///
    /// IT MEASURES IMPULSE, IN NEWTON-SECONDS, NOT FORCE. Force is
    /// impulse / fixedDeltaTime, and this project runs two different fixed
    /// steps: 0.02 s normally and 0.005 s whenever a MuJoCo creature is in the
    /// scene, because <c>CreatureSentisController</c> pins the step for
    /// everyone (see the timestep section of CLAUDE.md). The identical landing
    /// therefore reads four times louder at the finer step if it is expressed
    /// as a force. Impulse is what the solver actually applied and is the same
    /// number either way.
    ///
    /// Attached at runtime by <see cref="Systems_ImpactFx"/> and never
    /// serialized into a prefab or a scene: training scenes must not pay for a
    /// collision callback per limb, and a ragdoll in a sixteen-fighter training
    /// ring generates a great many of them.
    ///
    /// Deliberately NOT [RequireComponent(typeof(Rigidbody))], for the same
    /// reason <see cref="Sensor_GroundContact"/> is not: that attribute would
    /// ADD a rigidbody to a part that lacks one, and inserting an unplanned
    /// body into a ragdoll changes the physics rather than fixing the sensor.
    /// </summary>
    public sealed class Sensor_Impact : MonoBehaviour
    {
        /// <summary>
        /// Raised on the physics step a collision begins.
        /// Arguments: world contact point, contact normal, impulse magnitude in
        /// newton-seconds.
        ///
        /// Assigned by the director that created this component rather than
        /// exposed as a C# event, because there is exactly one listener and a
        /// multicast delegate per limb per fighter is pure overhead in a hot
        /// collision path.
        /// </summary>
        public System.Action<Vector3, Vector3, float> Impacted;

        /// <summary>
        /// The transform every part of this fighter hangs off. Contacts between
        /// two bodies sharing it are the ragdoll folding against itself, which
        /// is constant, silent in reality, and not an impact.
        /// </summary>
        public Transform Owner { get; set; }

        private void OnCollisionEnter(Collision collision)
        {
            if (Impacted == null)
            {
                return;
            }
            // A fighter's own thigh hitting its own shin is not an event. Every
            // fallen ragdoll generates these continuously.
            if (Owner != null && collision.transform != null &&
                collision.transform.IsChildOf(Owner))
            {
                return;
            }
            if (collision.contactCount == 0)
            {
                return;
            }

            // Collision.impulse is only valid inside the collision callback and
            // is expressed in the impulse's own direction; magnitude is what
            // scales the presentation.
            float impulse = collision.impulse.magnitude;
            if (impulse <= 0f)
            {
                return;
            }
            ContactPoint contact = collision.GetContact(0);
            Impacted(contact.point, contact.normal, impulse);
        }
    }
}
