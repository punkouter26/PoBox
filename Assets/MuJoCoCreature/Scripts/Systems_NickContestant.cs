using UnityEngine;

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

        private int _resetsAtRoundStart;

        private void Awake()
        {
            if (_controller == null) { _controller = GetComponent<CreatureSentisController>(); }
        }

        public string DisplayName => _displayName;

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
        /// MuJoCo is Z-up, so the head's height is DebugHeadZ -- not a Unity
        /// transform's y. It is already measured from the creature's own floor,
        /// which is what the ring wants: height above the floor, never world Y.
        /// </summary>
        public float HeadHeightAboveGround => _controller != null ? _controller.DebugHeadZ : 0f;

        /// <summary>
        /// The controller auto-resets the creature when it judges it fallen, so
        /// a reset since the round began IS a fall. Without this the ring could
        /// miss a collapse entirely: the creature would drop, snap back to the
        /// rest pose, and its head height would look healthy again by the time
        /// the referee next looked.
        /// </summary>
        public bool ReportsDown =>
            _controller != null && _controller.DebugResetCount > _resetsAtRoundStart;
    }
}
