using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Ground-contact sensor. "Ground" = any static collider (no attached
    /// rigidbody), so the canvas and apron count without needing tags.
    /// Attached to feet and to every part whose touching the floor means a fall.
    ///
    /// CONTACT IS RE-AFFIRMED EVERY PHYSICS STEP, not counted.
    ///
    /// This used to keep a running total of OnCollisionEnter minus
    /// OnCollisionExit and report grounded when it was positive. That is only
    /// sound while every Enter is eventually matched by its Exit, and on this
    /// project's rigs it is not:
    ///
    ///   - ResetContacts() zeroes the total by design, after a teleport-reset,
    ///     while the foot is still resting on the floor. No new Enter is ever
    ///     generated for a contact pair that never separated, so the foot reads
    ///     airborne until it physically leaves the ground and lands again.
    ///   - A rigidbody that goes to sleep stops generating callbacks entirely,
    ///     freezing the total at whatever it happened to be.
    ///
    /// Measured 2026-09-07 with -evalDiagnoseFeet, on a fighter standing still:
    ///
    ///   Capsule/Fighter_00  L[sensor on 'FootL',    grounded=True,  minY=-0.0024]
    ///   Grandma/Fighter_13  L[sensor on 'LeftFoot', grounded=False, minY=-0.0043]
    ///                       R[sensor on 'RightFoot',grounded=False, minY=-0.0083]
    ///
    /// Grandma's soles are four to eight MILLIMETRES BELOW the floor plane --
    /// interpenetrating it -- while both sensors report airborne. The capsule
    /// carries its collider on the same GameObject as its sensor and reports
    /// correctly at the same penetration; the imported characters put theirs on
    /// a child ('LeftFoot_Sole'), and that asymmetry is where the bookkeeping
    /// diverges.
    ///
    /// WHAT IT COST. Over a standing episode the heuristic PD bot -- whose ankle
    /// strategy keeps both feet planted, identical code on every rig -- reported
    /// its feet grounded 97% of the time on the capsule and 41% on Grandma. Every
    /// quantity downstream is computed from these flags: singleSupport, the
    /// clearance gate, the support term, and fall detection itself. So ten of
    /// sixteen fighters trained against a corrupted objective, and the per-body
    /// ordering that twenty generations read as "the character rigs are harder"
    /// tracks sensor health almost exactly:
    ///
    ///     body      feet grounded   steps between falls
    ///     Capsule    0.97 / 0.98           104
    ///     Grandpa    0.61 / 1.00            66
    ///     Grandma    0.41 / 0.41            38
    ///
    /// Reading OnCollisionStay instead makes the flag a statement about THIS
    /// step rather than a running tally that can drift, so no reset, no missing
    /// Exit and no compound-collider asymmetry can desynchronise it.
    /// </summary>
    // Before the agent (-100) and the rewards (-99): the roll-over below has to
    // happen before anything reads IsGrounded this step.
    // Deliberately NOT [RequireComponent(typeof(Rigidbody))]: that would ADD a
    // rigidbody to any part that lacks one, and these sensors are attached from
    // code to whatever the rig tool points them at. Inserting an unplanned body
    // into a ragdoll changes the physics rather than fixing the sensor.
    [DefaultExecutionOrder(-101)]
    public sealed class Sensor_GroundContact : MonoBehaviour
    {
        private bool _touchingThisStep;
        private bool _groundedLastStep;
        private Vector3 _lastNormal = Vector3.up;

        /// <summary>
        /// True when this part was touching static geometry as of the last
        /// completed physics step — one step of latency, 20 ms at the project's
        /// fixed 50 Hz, in exchange for a flag that cannot drift.
        /// </summary>
        public bool IsGrounded => _groundedLastStep;

        /// <summary>Most recent ground contact normal; Vector3.zero when airborne.</summary>
        public Vector3 ContactNormal => _groundedLastStep ? _lastNormal : Vector3.zero;

        private void Awake()
        {
            // Never sleep. A resting rigidbody stops emitting collision
            // callbacks altogether, which would freeze this sensor reading
            // airborne for exactly the fighter that is standing most still --
            // the one case the balance game is about.
            var body = GetComponent<Rigidbody>();
            if (body != null)
            {
                body.sleepThreshold = 0f;
            }
        }

        /// <summary>
        /// Clears contact state after a teleport-reset. Still needed, and now
        /// harmless: the next physics step re-affirms contact from scratch
        /// instead of waiting for an Enter that will never come.
        /// </summary>
        public void ResetContacts()
        {
            _touchingThisStep = false;
            _groundedLastStep = false;
            _lastNormal = Vector3.up;
        }

        private void FixedUpdate()
        {
            // Callbacks for step N-1 have all been delivered by now; publish
            // them and start collecting step N.
            _groundedLastStep = _touchingThisStep;
            _touchingThisStep = false;
        }

        private void OnCollisionEnter(Collision collision)
        {
            Observe(collision);
        }

        private void OnCollisionStay(Collision collision)
        {
            Observe(collision);
        }

        private void Observe(Collision collision)
        {
            // Ground is anything static. A collision with another fighter, or
            // with a hazard prop, carries a rigidbody and is not the floor.
            if (collision.rigidbody != null)
            {
                return;
            }
            _touchingThisStep = true;
            if (collision.contactCount > 0)
            {
                _lastNormal = collision.GetContact(0).normal;
            }
        }
    }
}
