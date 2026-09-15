using UnityEngine;
using Mujoco;

namespace PoBox.MuJoCoCreature
{
    /// <summary>
    /// Lets Nick stand in the PhysX balance ring.
    ///
    /// WHAT MADE THIS POSSIBLE. Nick used to be unable to share that scene at
    /// all: Time.fixedDeltaTime is global, he needed 0.005 s, and the PhysX
    /// fighters need 0.02 s. Measured 2026-09-08, both directions failed --
    /// at 0.02 s his 0.005-trained brain scored a 1.06 s median against a
    /// 1.08 s PASSIVE baseline, and at 0.005 s Standard fell from 30.0 s to
    /// 2.9 s even with DecisionPeriod compensated.
    ///
    /// What broke the deadlock was not a solver setting but the BODY: the
    /// position servos are kp=400, so at armature 0.02 omega*dt is 2.83 at a
    /// 0.02 s step -- past the stability limit of 2, which is why the policy
    /// had no authority. Armature 0.2 puts omega*dt at 0.89, and a policy
    /// trained from scratch on that body (nick08) holds 30.0 s in 95% of
    /// worlds under the ring's own 150 N shove. The armature lives in the
    /// MJCF Unity generates, so the trainer and the game share one body.
    ///
    /// WHAT HE CANNOT DO. nick08 is a BALANCE-ONLY brain: 0% full-cap on the
    /// walk test, 2.22 s median. He belongs in this ring and nowhere near the
    /// walk race.
    /// </summary>
    [RequireComponent(typeof(CreatureSentisController))]
    public sealed class Systems_NickContestant : MonoBehaviour, IContestFighter
    {
        [SerializeField] private CreatureSentisController _controller;
        [SerializeField] private string _displayName = "Nick";

        /// <summary>
        /// Pelvis height above the floor below which Nick counts himself down.
        /// 0.3 m is the controller's own default fall height; the contest
        /// scenes set his _fallHeight to -1 so a fallen body STAYS fallen
        /// instead of snapping back to the rest pose, which also means the
        /// reset counter below never moves there. Without this second test
        /// ReportsDown was false with his head 12 cm off the ring.
        /// </summary>
        [Tooltip("Pelvis height above the floor (m) below which Nick reports himself down.")]
        [SerializeField] private float _downPelvisHeight = 0.3f;

        private int _resetsAtRoundStart;

        private void Awake()
        {
            if (_controller == null) { _controller = GetComponent<CreatureSentisController>(); }
        }

        public string DisplayName => _displayName;

        public bool IsReady => _controller != null && _controller.IsBound;

        public void ResetForRound()
        {
            if (_controller == null) { return; }
            _controller.ResetCreature();
            _resetsAtRoundStart = _controller.DebugResetCount;
        }

        /// <summary>The ring's only order, and the one nick08 was trained for.</summary>
        public void CommandStand()
        {
            if (_controller != null) { _controller.SetCommand(0f); }
        }

        /// <summary>
        /// The controller commands in the creature's own frame, so the Unity
        /// direction is converted: MuJoCo is Z-up, Unity Y-up, which makes
        /// Unity's (x, y, z) the creature's (x, z, y).
        /// </summary>
        public void CommandWalk(float metresPerSecond, Vector3 directionWorld)
        {
            if (_controller == null) { return; }
            // Use the plugin's exact conversion, also used to place the bodies.
            // Swapping Y/Z already changes handedness. An additional minus sign
            // sends the creature toward the wrong end of the walking course.
            var inCreatureFrame = MjEngineTool.MjVector3(directionWorld);
            _controller.SetCommand(metresPerSecond, inCreatureFrame);
        }

        /// <summary>Unity world position of the pelvis, for measuring travel.</summary>
        public Vector3 WorldPosition
        {
            get
            {
                if (IsReady) { return MjEngineTool.UnityVector3(_controller.DebugPelvisPosition); }
                Transform pelvis = _controller != null ? _controller.PelvisTransform : null;
                return pelvis != null ? pelvis.position : transform.position;
            }
        }

        /// <summary>
        /// MuJoCo reports world Z. Subtract the authored floor's elevation;
        /// otherwise a collapsed body on the raised ring still counts as tall.
        /// </summary>
        public float HeadHeightAboveGround => IsReady ? _controller.DebugHeadZ - _controller.GroundHeight : 0f;

        /// <summary>
        /// Down by either of two tells. A reset since the round began IS a
        /// fall: where the controller auto-resets a fallen creature, the body
        /// would drop, snap back to the rest pose, and read as healthy again by
        /// the time the referee next looked. And a pelvis under
        /// <see cref="_downPelvisHeight"/> is a fall in the scenes that disable
        /// that reset so the fallen body stays where it landed. MuJoCo is Z-up,
        /// so the pelvis height is its world Z above the authored floor.
        /// </summary>
        public bool ReportsDown
        {
            get
            {
                if (_controller == null) { return false; }
                if (_controller.DebugResetCount > _resetsAtRoundStart) { return true; }
                return IsReady
                    && _controller.DebugPelvisPosition.z - _controller.GroundHeight < _downPelvisHeight;
            }
        }
    }
}
