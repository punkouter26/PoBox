using UnityEngine;

namespace PoBox.Editor
{
    internal struct RigDofRange
    {
        public bool has;
        public float low;
        public float high;

        public static RigDofRange None => default;

        public static RigDofRange Range(float low, float high)
        {
            return new RigDofRange { has = true, low = low, high = high };
        }

        /// <summary>Mirrored range for right-side segments (negate and swap).</summary>
        public RigDofRange Mirrored()
        {
            return has ? Range(-high, -low) : None;
        }
    }

    internal struct RigSegmentSpec
    {
        public RigSegment segment;
        public RigSegment parent;
        public float massPercent;
        public RigDofRange pitch;   // joint X
        public RigDofRange roll;    // joint Y (PhysX limit is symmetric; asymmetry enforced in action mapping)
        public RigDofRange yaw;     // joint Z
        public float spring;
        public float damper;
        public float maxForce;
        public float capsuleLength;
        public float capsuleRadius;
        public int capsuleDirection; // 0=X 1=Y 2=Z
        public Vector3 localPosition; // capsule-biped local position under parent
    }
}
