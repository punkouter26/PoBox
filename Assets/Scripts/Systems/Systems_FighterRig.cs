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
    // Ahead of the agent (-100) and the rewards (-99): the agent reaches into
    // this rig from its very first callback, so the rig has to be the first
    // thing on a fighter to wake. See EnsureInitialized for what went wrong
    // while this was left at the default 0.
    [DefaultExecutionOrder(-200)]
    public sealed class Systems_FighterRig : MonoBehaviour
    {
        private const float MAX_ANGULAR_VELOCITY = 20f;
        private const float GROUND_PROBE_METERS = 10f;

        // Reused by the one-shot ground probe in Awake. Static is safe: Awake is
        // main-thread and the buffer is consumed before the next call.
        private static readonly RaycastHit[] GroundProbeHits = new RaycastHit[8];

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
        // Muscle-like command lag: joint targets drift toward the commanded
        // pose over this many seconds instead of snapping (0 = off). Changes
        // dynamics — only enable on rigs whose brain trained with it.
        [SerializeField] private float _actionSmoothingSeconds;
        // Human strength proportions: ankles/arms much weaker than hips, plus
        // crisper foot contacts. Changes dynamics — same caveat as above.
        [SerializeField] private bool _realismProfile;

        private Transform[] _poseTransforms;
        private Vector3[] _startLocalPositions;
        private Quaternion[] _startLocalRotations;
        private float _currentSpringScale = 1f;
        private float _strengthScale = 1f;
        private float[] _smoothedTargets; // 3 per joint (pitch, roll, yaw)
        private float _groundY;
        private bool _initialized;


        /// <summary>
        /// World Y of the floor this fighter stands on, probed once in Awake.
        /// Height observations and collapse checks are measured against it so a
        /// rig behaves identically at any altitude: a ring canvas at y = 1 reads
        /// exactly the same to the brain as a training ground at y = 0.
        /// </summary>
        public float GroundY
        {
            get
            {
                EnsureInitialized();
                return _groundY;
            }
        }

        public Rigidbody Pelvis => _pelvis;        public Rigidbody Torso => _torso;
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
            EnsureInitialized();
        }

        /// <summary>
        /// One-time runtime setup. Safe to call before Awake, and safe to call
        /// again afterwards.
        ///
        /// The execution-order attribute above is the intended guarantee; this
        /// guard is the one that holds when something reaches the rig outside
        /// the ordinary Awake sequence. On the contest spawn path a fighter is
        /// activated by being reparented out of the inactive holder, and
        /// ML-Agents runs Agent.OnEnable -> LazyInitialize -> OnEpisodeBegin
        /// inline from that reparent — which lands in ResetToStartPose before
        /// there is a start pose to restore. Measured 2026-08-21: four
        /// NullReferenceExceptions, one per fighter, on the *second* contest of
        /// a session (menu -> balance -> MENU -> menu -> walk) and none on the
        /// first, which is why it went unnoticed. Every public entry point that
        /// needs this state calls here first rather than trusting the order.
        /// </summary>
        public void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;
            ProbeGroundY();
            CaptureStartPose();
            for (int jointIndex = 0; jointIndex < _joints.Count; jointIndex++)
            {
                RigJointEntry entry = _joints[jointIndex];
                if (entry.body != null)
                {
                    entry.body.maxAngularVelocity = MAX_ANGULAR_VELOCITY;
                }
            }
            _smoothedTargets = new float[_joints.Count * 3];
            if (_realismProfile)
            {
                ApplyRealismProfile();
            }
            DisableIntraRigCollisions();
        }

        // Probed once, never per step: the floor does not move, and a per-tick
        // cast would cost one raycast per fighter per FixedUpdate across a
        // 16-fighter training grid.
        private void ProbeGroundY()
        {
            Vector3 origin = _pelvis != null ? _pelvis.position : transform.position;
            int hitCount = Physics.RaycastNonAlloc(origin, Vector3.down, GroundProbeHits, GROUND_PROBE_METERS);
            float highest = float.NegativeInfinity;
            for (int hitIndex = 0; hitIndex < hitCount; hitIndex++)
            {
                // Skip this fighter's own colliders - the ray leaves the pelvis
                // and passes straight down through its own legs and feet.
                if (GroundProbeHits[hitIndex].transform.IsChildOf(transform))
                {
                    continue;
                }
                float hitY = GroundProbeHits[hitIndex].point.y;
                if (hitY > highest)
                {
                    highest = hitY;
                }
            }
            _groundY = highest > float.NegativeInfinity ? highest : transform.position.y;
        }

        // Human strength proportions by segment group, applied once to the
        // captured base drive values, plus crisper foot contacts. Fatigue and
        // curriculum scaling multiply on top unchanged.
        private void ApplyRealismProfile()
        {
            for (int jointIndex = 0; jointIndex < _joints.Count; jointIndex++)
            {
                RigJointEntry entry = _joints[jointIndex];
                float groupScale = StrengthGroupScale(entry.body.name);
                entry.baseSpring *= groupScale;
                entry.baseDamper *= groupScale;
                entry.baseMaxForce *= groupScale;
                JointDrive drive = entry.joint.slerpDrive;
                drive.positionSpring = entry.baseSpring;
                drive.positionDamper = entry.baseDamper;
                drive.maximumForce = entry.baseMaxForce;
                entry.joint.slerpDrive = drive;
            }
            TightenFootContacts(_footLeftSensor);
            TightenFootContacts(_footRightSensor);
        }

        private static float StrengthGroupScale(string bodyName)
        {
            if (bodyName.IndexOf("foot", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0.5f;  // ankles: weakest joints in the chain
            }
            if (bodyName.IndexOf("shin", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0.8f;  // knees
            }
            if (bodyName.IndexOf("arm", System.StringComparison.OrdinalIgnoreCase) >= 0
                || bodyName.IndexOf("glove", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0.6f;  // arms: balance aids, not lift muscles
            }
            if (bodyName.IndexOf("head", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0.4f;  // neck
            }
            return 1f;        // hips/thighs/torso: the power column
        }

        private static void TightenFootContacts(Sensor_GroundContact footSensor)
        {
            if (footSensor == null)
            {
                return;
            }
            var colliders = footSensor.GetComponents<Collider>();
            for (int colliderIndex = 0; colliderIndex < colliders.Length; colliderIndex++)
            {
                colliders[colliderIndex].contactOffset = 0.01f;
            }
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
            EnsureInitialized();
            int cursor = offset;
            float sign = _invertTargetRotation ? -1f : 1f;
            // Muscle lag: exponential drift toward the commanded angles.
            // alpha 1 (instant) when smoothing is 0.
            float alpha = _actionSmoothingSeconds > 0f
                ? 1f - Mathf.Exp(-Time.fixedDeltaTime / _actionSmoothingSeconds)
                : 1f;
            for (int jointIndex = 0; jointIndex < _joints.Count; jointIndex++)
            {
                RigJointEntry entry = _joints[jointIndex];
                int smoothBase = jointIndex * 3;
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
                _smoothedTargets[smoothBase] += (pitch - _smoothedTargets[smoothBase]) * alpha;
                _smoothedTargets[smoothBase + 1] += (roll - _smoothedTargets[smoothBase + 1]) * alpha;
                _smoothedTargets[smoothBase + 2] += (yaw - _smoothedTargets[smoothBase + 2]) * alpha;
                entry.joint.targetRotation = Quaternion.Euler(
                    sign * _smoothedTargets[smoothBase],
                    sign * _smoothedTargets[smoothBase + 1],
                    sign * _smoothedTargets[smoothBase + 2]);
            }
        }

        private static float MapZeroCentered(float action, float low, float high)
        {
            return action >= 0f ? action * high : -action * low;
        }

        /// <summary>Scales every joint's slerp-drive spring. Used by stamina attenuation.</summary>
        public void SetSpringScale(float scale01)
        {
            EnsureInitialized();
            _currentSpringScale = scale01;
            ApplyDriveScales();
        }

        /// <summary>
        /// Global strength multiplier for the "strength_scale" curriculum:
        /// scales both spring and force cap, composing with the stamina spring
        /// scale. 1 = authored strength.
        /// </summary>
        public void SetStrengthScale(float scale)
        {
            EnsureInitialized();
            _strengthScale = scale;
            ApplyDriveScales();
        }

        private void ApplyDriveScales()
        {
            for (int jointIndex = 0; jointIndex < _joints.Count; jointIndex++)
            {
                RigJointEntry entry = _joints[jointIndex];
                JointDrive drive = entry.joint.slerpDrive;
                drive.positionSpring = entry.baseSpring * _currentSpringScale * _strengthScale;
                drive.positionDamper = entry.baseDamper;
                drive.maximumForce = entry.baseMaxForce * _strengthScale;
                entry.joint.slerpDrive = drive;
            }
        }

        /// <summary>Restores the captured start pose and zeroes all velocities.</summary>
        public void ResetToStartPose()
        {
            EnsureInitialized();
            if (_smoothedTargets != null)
            {
                System.Array.Clear(_smoothedTargets, 0, _smoothedTargets.Length);
            }
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
