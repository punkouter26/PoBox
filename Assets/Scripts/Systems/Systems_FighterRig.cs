using System;
using System.Collections.Generic;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Serialized description of one driven joint: which axes are active,
    /// their true (possibly asymmetric) ranges, and the base drive values
    /// captured at rig time. PhysX Y/Z limits are symmetric, so asymmetric
    /// roll/yaw ranges are enforced here via the action mapping, not the joint.
    /// </summary>
    [Serializable]
    public sealed class RigJointEntry
    {
        public ConfigurableJoint joint;
        public Rigidbody body;
        public bool hasPitch;
        public float pitchLow;
        public float pitchHigh;
        public bool hasRoll;
        public float rollLow;
        public float rollHigh;
        public bool hasYaw;
        public float yawLow;
        public float yawHigh;
        public float baseSpring;
        public float baseDamper;
        public float baseMaxForce;

        public int DofCount => (hasPitch ? 1 : 0) + (hasRoll ? 1 : 0) + (hasYaw ? 1 : 0);
    }

    /// <summary>
    /// Runtime handle to a rigged active-ragdoll fighter. Built by the editor
    /// rig tool. Applies normalized [-1,1] actions as joint target rotations
    /// and scales joint springs for the stamina system.
    /// </summary>
    public sealed class Systems_FighterRig : MonoBehaviour
    {
        private const float MAX_ANGULAR_VELOCITY = 20f;

        [SerializeField] private Rigidbody _pelvis;
        [SerializeField] private Rigidbody _torso;
        [SerializeField] private Transform _head;
        [SerializeField] private Transform _gloveLeft;
        [SerializeField] private Transform _gloveRight;
        [SerializeField] private Sensor_GroundContact _footLeftSensor;
        [SerializeField] private Sensor_GroundContact _footRightSensor;
        [SerializeField] private List<RigJointEntry> _joints = new();
        // Set true if the joint-range test (Systems_JointRangeTester in SCN_RIGSTAGE)
        // shows drives moving opposite to the commanded direction.
        [SerializeField] private bool _invertTargetRotation;

        private Transform[] _poseTransforms;
        private Vector3[] _startLocalPositions;
        private Quaternion[] _startLocalRotations;
        private float _currentSpringScale = 1f;

        public Rigidbody Pelvis => _pelvis;
        public Rigidbody Torso => _torso;
        public Transform Head => _head;
        public Transform GloveLeft => _gloveLeft;
        public Transform GloveRight => _gloveRight;
        public Sensor_GroundContact FootLeftSensor => _footLeftSensor;
        public Sensor_GroundContact FootRightSensor => _footRightSensor;
        public IReadOnlyList<RigJointEntry> Joints => _joints;
        public float CurrentSpringScale => _currentSpringScale;

        public int JointCount => _joints.Count;

        public int DofCount
        {
            get
            {
                int count = 0;
                for (int jointIndex = 0; jointIndex < _joints.Count; jointIndex++)
                {
                    count += _joints[jointIndex].DofCount;
                }
                return count;
            }
        }

        // Called by the editor rig tool while building the prefab.
        public void EditorInitialize(Rigidbody pelvis, Rigidbody torso, Transform head, Transform gloveLeft, Transform gloveRight,
            Sensor_GroundContact footLeftSensor, Sensor_GroundContact footRightSensor, List<RigJointEntry> joints)
        {
            _pelvis = pelvis;
            _torso = torso;
            _head = head;
            _gloveLeft = gloveLeft;
            _gloveRight = gloveRight;
            _footLeftSensor = footLeftSensor;
            _footRightSensor = footRightSensor;
            _joints = joints;
        }

        private void Awake()
        {
            CaptureStartPose();
            for (int jointIndex = 0; jointIndex < _joints.Count; jointIndex++)
            {
                RigJointEntry entry = _joints[jointIndex];
                if (entry.body != null)
                {
                    entry.body.maxAngularVelocity = MAX_ANGULAR_VELOCITY;
                }
            }
            DisableIntraRigCollisions();
        }

        // The rest pose has intentional segment overlap (gloves near pelvis
        // etc.); without this, PhysX depenetration impulses kick every reset.
        // Fighters still collide with each other — this only ignores pairs
        // inside one rig. IgnoreCollision is runtime state, so it runs in
        // Awake, not at rig-build time.
        private void DisableIntraRigCollisions()
        {
            var colliders = GetComponentsInChildren<Collider>(true);
            for (int firstIndex = 0; firstIndex < colliders.Length; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1; secondIndex < colliders.Length; secondIndex++)
                {
                    Physics.IgnoreCollision(colliders[firstIndex], colliders[secondIndex], true);
                }
            }
        }

        /// <summary>
        /// Maps a flat normalized action array onto joint target rotations.
        /// Order: joints in list order; per joint pitch(X), roll(Y), yaw(Z),
        /// skipping inactive axes. Zero-centered: action 0 always commands the
        /// rest pose, even on asymmetric ranges — a fresh zero-mean policy must
        /// not command a squat. Call from FixedUpdate only.
        /// </summary>
        public void ApplyActions(float[] actions, int offset)
        {
            int cursor = offset;
            float sign = _invertTargetRotation ? -1f : 1f;
            for (int jointIndex = 0; jointIndex < _joints.Count; jointIndex++)
            {
                RigJointEntry entry = _joints[jointIndex];
                float pitch = 0f;
                float roll = 0f;
                float yaw = 0f;
                if (entry.hasPitch)
                {
                    pitch = MapZeroCentered(actions[cursor], entry.pitchLow, entry.pitchHigh);
                    cursor++;
                }
                if (entry.hasRoll)
                {
                    roll = MapZeroCentered(actions[cursor], entry.rollLow, entry.rollHigh);
                    cursor++;
                }
                if (entry.hasYaw)
                {
                    yaw = MapZeroCentered(actions[cursor], entry.yawLow, entry.yawHigh);
                    cursor++;
                }
                entry.joint.targetRotation = Quaternion.Euler(sign * pitch, sign * roll, sign * yaw);
            }
        }

        private static float MapZeroCentered(float action, float low, float high)
        {
            return action >= 0f ? action * high : -action * low;
        }

        /// <summary>Scales every joint's slerp-drive spring. Used by stamina attenuation.</summary>
        public void SetSpringScale(float scale01)
        {
            _currentSpringScale = scale01;
            for (int jointIndex = 0; jointIndex < _joints.Count; jointIndex++)
            {
                RigJointEntry entry = _joints[jointIndex];
                JointDrive drive = entry.joint.slerpDrive;
                drive.positionSpring = entry.baseSpring * scale01;
                drive.positionDamper = entry.baseDamper;
                drive.maximumForce = entry.baseMaxForce;
                entry.joint.slerpDrive = drive;
            }
        }

        /// <summary>Restores the captured start pose and zeroes all velocities.</summary>
        public void ResetToStartPose()
        {
            for (int poseIndex = 0; poseIndex < _poseTransforms.Length; poseIndex++)
            {
                _poseTransforms[poseIndex].SetLocalPositionAndRotation(_startLocalPositions[poseIndex], _startLocalRotations[poseIndex]);
            }
            for (int jointIndex = 0; jointIndex < _joints.Count; jointIndex++)
            {
                RigJointEntry entry = _joints[jointIndex];
                entry.joint.targetRotation = Quaternion.identity;
                if (entry.body != null)
                {
                    entry.body.linearVelocity = Vector3.zero;
                    entry.body.angularVelocity = Vector3.zero;
                }
            }
            if (_pelvis != null)
            {
                _pelvis.linearVelocity = Vector3.zero;
                _pelvis.angularVelocity = Vector3.zero;
            }
        }

        private void CaptureStartPose()
        {
            var all = GetComponentsInChildren<Transform>();
            _poseTransforms = all;
            _startLocalPositions = new Vector3[all.Length];
            _startLocalRotations = new Quaternion[all.Length];
            for (int poseIndex = 0; poseIndex < all.Length; poseIndex++)
            {
                all[poseIndex].GetLocalPositionAndRotation(out _startLocalPositions[poseIndex], out _startLocalRotations[poseIndex]);
            }
        }
    }
}
