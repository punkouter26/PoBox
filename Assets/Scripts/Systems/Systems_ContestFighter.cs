using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// A contestant the referees, the cameras and the booth can work with,
    /// whatever simulates it.
    ///
    /// WHY THIS EXISTS. The ring was written around one body: a PhysX
    /// ConfigurableJoint ragdoll. Nick is not that -- he is a MuJoCo creature
    /// driven by CreatureSentisController, in a different assembly
    /// (PoBox.MuJoCoCreature), and the ring's assembly deliberately does not
    /// reference the MuJoCo plugin: a player build of the shipping game must
    /// not have to carry it. So the ring asks for the things it actually needs
    /// and does not care what answers them. Since the PhysX cast was removed
    /// (2026-09-14) every contestant answers this interface and nothing else
    /// -- the referees, cameras, announcer and hazards know no other body.
    ///
    /// Kept deliberately small. Anything richer would pull ring rules into the
    /// fighters, and the referee is the one place they belong.
    /// </summary>
    public interface IContestFighter
    {
        /// <summary>Name on the scoreboard and in the CONTEST_ROUND line.</summary>
        string DisplayName { get; }

        /// <summary>Swatch colour that identifies this contestant on its plate and in the tally.</summary>
        Color PlateColor { get; }

        /// <summary>The scene object the contestant lives on, for cameras that follow a transform.</summary>
        Transform Root { get; }

        /// <summary>True once reset, pose and height measurements are available.</summary>
        bool IsReady { get; }

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
        /// Unity world position of the pelvis, for measuring distance travelled
        /// and for framing. Not the creature's own frame: the race projects
        /// this onto a goal direction authored in Unity.
        /// </summary>
        Vector3 WorldPosition { get; }

        /// <summary>World Y of the floor this contestant stands on.</summary>
        float GroundY { get; }

        /// <summary>
        /// Head height ABOVE THE FLOOR, never raw world Y -- the ring canvas
        /// sits at Systems_ContestSpawner.RING_FLOOR_Y, so an absolute height
        /// silently rescales with altitude.
        /// </summary>
        float HeadHeightAboveGround { get; }

        /// <summary>
        /// Pelvis angular velocity in Unity world axes, rad/s. Only its
        /// magnitude is read (the drama camera's wobble estimate), so an
        /// implementation need not agonise over handedness.
        /// </summary>
        Vector3 PelvisAngularVelocity { get; }

        /// <summary>
        /// True when the fighter has gone down by its own reckoning. Fighters
        /// that have no such notion return false and are judged on head height
        /// alone.
        /// </summary>
        bool ReportsDown { get; }

        /// <summary>
        /// A horizontal push on the pelvis, Unity world newtons, held for
        /// <paramref name="seconds"/>. The hazard director's wind gust.
        /// </summary>
        void Shove(Vector3 forceWorldNewtons, float seconds);

        /// <summary>
        /// Mirror a change of world gravity into the contestant's own
        /// simulator. The hazard director's gravity lean tilts Physics.gravity,
        /// which a body that is not simulated by PhysX would otherwise never
        /// feel. Implementations that share one simulator may apply it once.
        /// </summary>
        void SetGravity(Vector3 gravityWorld);
    }

    /// <summary>
    /// The one way the presentation layer finds contestants, so every system
    /// sees the same field in the same order.
    /// </summary>
    public static class Systems_Contestants
    {
        /// <summary>
        /// Every contestant in the scene, in instance-id order. Active ones
        /// only by default: the launcher stands down the author-placed bodies
        /// the line-up did not pick, and a camera bound to those would frame
        /// fighters that are not in the contest.
        /// </summary>
        public static IContestFighter[] FindAll(bool includeInactive = false)
        {
            var found = new System.Collections.Generic.List<IContestFighter>();
            MonoBehaviour[] behaviours = Object.FindObjectsByType<MonoBehaviour>(
                includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
                FindObjectsSortMode.InstanceID);
            for (int index = 0; index < behaviours.Length; index++)
            {
                if (behaviours[index] is IContestFighter fighter)
                {
                    found.Add(fighter);
                }
            }
            return found.ToArray();
        }

        /// <summary>The contestant called <paramref name="displayName"/>, or null.</summary>
        public static IContestFighter FindByName(IContestFighter[] fighters, string displayName)
        {
            if (fighters == null || string.IsNullOrEmpty(displayName)) { return null; }
            for (int index = 0; index < fighters.Length; index++)
            {
                if (string.Equals(fighters[index].DisplayName, displayName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return fighters[index];
                }
            }
            return null;
        }

        /// <summary>
        /// Head height as a fraction of the standing height, floored so a
        /// contestant measured at its own ground level cannot produce an
        /// infinity that poisons every comparison after it (a NaN wobble never
        /// recovers, and the camera stops choosing a subject at all).
        /// </summary>
        public static float HeadFraction(IContestFighter fighter, float startHeadHeight)
        {
            return fighter.HeadHeightAboveGround / Mathf.Max(0.01f, startHeadHeight);
        }
    }
}
