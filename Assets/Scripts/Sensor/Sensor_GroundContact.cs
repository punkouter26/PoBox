using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Foot ground-contact sensor. "Ground" = any static collider (no attached
    /// rigidbody), so the canvas and apron count without needing tags.
    /// Attached to foot segments by the rig tool.
    /// </summary>
    public sealed class Sensor_GroundContact : MonoBehaviour
    {
        private int _contactCount;
        private Vector3 _lastNormal = Vector3.up;

        public bool IsGrounded => _contactCount > 0;

        /// <summary>Most recent ground contact normal; Vector3.zero when airborne.</summary>
        public Vector3 ContactNormal => _contactCount > 0 ? _lastNormal : Vector3.zero;

        /// <summary>
        /// Clears contact state after a teleport-reset. OnCollisionExit only
        /// fires after the next physics step, so a freshly reset episode would
        /// otherwise still read the pre-reset contacts.
        /// </summary>
        public void ResetContacts()
        {
            _contactCount = 0;
            _lastNormal = Vector3.up;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (collision.rigidbody == null)
            {
                _contactCount++;
                if (collision.contactCount > 0)
                {
                    _lastNormal = collision.GetContact(0).normal;
                }
            }
        }

        private void OnCollisionStay(Collision collision)
        {
            if (collision.rigidbody == null && collision.contactCount > 0)
            {
                _lastNormal = collision.GetContact(0).normal;
            }
        }

        private void OnCollisionExit(Collision collision)
        {
            if (collision.rigidbody == null)
            {
                _contactCount = Mathf.Max(0, _contactCount - 1);
            }
        }
    }
}
