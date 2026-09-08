using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// A contestant the balance ring can referee that is NOT a
    /// <see cref="Systems_FighterRig"/>.
    ///
    /// WHY THIS EXISTS. The ring was written around one body: a PhysX
    /// ConfigurableJoint ragdoll with an Agent_FighterBoxing on it and
    /// Sensor_GroundContact on its head, shins and gloves. Nick is none of
    /// those -- he is a MuJoCo creature driven by CreatureSentisController,
    /// in a different assembly (PoBox.MuJoCoCreature), and the ring's
    /// assembly deliberately does not reference the MuJoCo plugin: a player
    /// build of the shipping game must not have to carry it.
    ///
    /// So the ring asks for the four things it actually needs and does not
    /// care what answers them. Nick implements this on his side; the PhysX
    /// fighters keep their existing path untouched.
    ///
    /// Kept deliberately small. Anything richer would pull ring rules into
    /// the fighters, and the referee is the one place they belong.
    /// </summary>
    public interface IContestFighter
    {
        /// <summary>Name on the scoreboard and in the CONTEST_ROUND line.</summary>
        string DisplayName { get; }

        /// <summary>
        /// Canonical pose, velocities zeroed, contacts cleared -- the state
        /// every round must start from. Round one starting from anything else
        /// is a bug this project has already shipped once.
        /// </summary>
        void ResetForRound();

        /// <summary>Command "hold station": the balance ring's only order.</summary>
        void CommandStand();

        /// <summary>
        /// Command "walk that way at that speed": the walk race's order.
        /// Direction is a UNITY world-space vector; an implementation on a
        /// creature that thinks in another frame converts it.
        /// </summary>
        void CommandWalk(float metresPerSecond, Vector3 directionWorld);

        /// <summary>
        /// Unity world position, for measuring distance travelled. Not the
        /// creature's own frame: the race projects this onto a goal direction
        /// authored in Unity.
        /// </summary>
        Vector3 WorldPosition { get; }

        /// <summary>
        /// Head height ABOVE THE FLOOR, never raw world Y -- the ring canvas
        /// sits at Systems_ContestSpawner.RING_FLOOR_Y, so an absolute height
        /// silently rescales with altitude.
        /// </summary>
        float HeadHeightAboveGround { get; }

        /// <summary>
        /// True when the fighter has gone down by its own reckoning, for a body
        /// whose collapse is not visible to Sensor_GroundContact. Fighters that
        /// have no such notion return false and are judged on head height alone.
        /// </summary>
        bool ReportsDown { get; }
    }
}
