using UnityEngine;
using Mujoco;

namespace PoBox.MuJoCoCreature
{
    /// <summary>
    /// Lets a MuJoCo creature stand in the contest scenes: the referees,
    /// cameras, announcer and hazards talk to it through
    /// <see cref="IContestFighter"/> and never see the MuJoCo plugin.
    ///
    /// WHAT MADE THIS POSSIBLE. Nick used to be unable to share the ring at
    /// all: Time.fixedDeltaTime is global, he needed 0.005 s, and the ring
    /// runs at 0.02 s. What broke the deadlock was not a solver setting but
    /// the BODY: the position servos are kp=400, so at armature 0.02 omega*dt
    /// is 2.83 at a 0.02 s step -- past the stability limit of 2, which is
    /// why the policy had no authority. Armature 0.2 puts omega*dt at 0.89,
    /// and a policy trained on that body at 0.02 s holds the ring. The
    /// armature lives in the MJCF Unity generates, so the trainer and the game
    /// share one body.
    ///
    /// One of these per creature. The display name and plate colour are per
    /// instance, so Grandma and Grandpa on their own MuJoCo bodies use this
    /// same component.
    /// </summary>
    [RequireComponent(typeof(CreatureSentisController))]
    public sealed class Systems_NickContestant : MonoBehaviour, IContestFighter
    {
        [SerializeField] private CreatureSentisController _controller;
        [SerializeField] private string _displayName = "Nick";
        [Tooltip("Swatch that identifies this contestant on its scoreboard plate and in the match tally.")]
        [SerializeField] private Color _plateColor = new Color(0.25f, 0.85f, 1f, 1f);

        /// <summary>
        /// Pelvis height above the floor below which the creature counts itself
        /// down. 0.3 m is the controller's own default fall height; the contest
        /// scenes set its _fallHeight to -1 so a fallen body STAYS fallen
        /// instead of snapping back to the rest pose, which also means the
        /// reset counter below never moves there. Without this second test
        /// ReportsDown was false with Nick's head 12 cm off the ring.
        /// </summary>
        [Tooltip("Pelvis height above the floor (m) below which the creature reports itself down.")]
        [SerializeField] private float _downPelvisHeight = 0.3f;

        private int _resetsAtRoundStart;

        private void Awake()
        {
            if (_controller == null) { _controller = GetComponent<CreatureSentisController>(); }
        }

        public string DisplayName => _displayName;

        public Color PlateColor => _plateColor;

        public Transform Root => transform;

        public bool IsReady => _controller != null && _controller.IsBound;

        public void ResetForRound()
        {
            if (_controller == null) { return; }
            _controller.ResetCreature();
            _resetsAtRoundStart = _controller.DebugResetCount;
        }

        /// <summary>The ring's only order: the 0 m/s end of the locomotion command.</summary>
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

        /// <summary>Unity world position of the pelvis, for measuring travel and framing.</summary>
        public Vector3 WorldPosition
        {
            get
            {
                if (IsReady) { return MjEngineTool.UnityVector3(_controller.DebugPelvisPosition); }
                Transform pelvis = _controller != null ? _controller.PelvisTransform : null;
                return pelvis != null ? pelvis.position : transform.position;
            }
        }

        /// <summary>The authored floor's elevation, which every height here is measured from.</summary>
        public float GroundY => _controller != null ? _controller.GroundHeight : transform.position.y;

        /// <summary>
        /// MuJoCo reports world Z. Subtract the authored floor's elevation;
        /// otherwise a collapsed body on the raised ring still counts as tall.
        /// </summary>
        public float HeadHeightAboveGround => IsReady ? _controller.DebugHeadZ - _controller.GroundHeight : 0f;

        /// <summary>Only the magnitude is read, so the axis swap's handedness does not matter.</summary>
        public Vector3 PelvisAngularVelocity =>
            IsReady ? MjEngineTool.UnityVector3(_controller.DebugPelvisAngVel) : Vector3.zero;

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

        /// <summary>The hazard director's wind gust, handed to MuJoCo as a pelvis force.</summary>
        public void Shove(Vector3 forceWorldNewtons, float seconds)
        {
            if (_controller != null) { _controller.Shove(MjEngineTool.MjVector3(forceWorldNewtons), seconds); }
        }

        /// <summary>
        /// The hazard director's gravity lean, mirrored into MuJoCo. The MjScene
        /// is shared by every creature in the scene, so each one writing the
        /// same vector is harmless.
        /// </summary>
        public void SetGravity(Vector3 gravityWorld)
        {
            if (_controller != null) { _controller.SetGravity(MjEngineTool.MjVector3(gravityWorld)); }
        }
    }
}
