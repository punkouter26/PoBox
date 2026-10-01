using UnityEngine;

namespace PoBox.Sim
{
    /// <summary>
    /// One link of a fighter. Carries what the telemetry needs to know about it (which joint, how much
    /// torque that joint is allowed) and reports the first touch of anything that is not the fighter itself.
    /// </summary>
    public class BodyPart : MonoBehaviour
    {
        public Fighter owner;
        public PartKind kind;
        [Tooltip("-1 left, +1 right, 0 on the centre line.")]
        public int side;
        public ArticulationBody body;
        [Tooltip("The glove, on a forearm. A hit only counts when this collider is the one that touched.")]
        public Collider strike;
        [Tooltip("Torque the joint above this link may produce, N m. Stress is drive torque over this.")]
        public float torqueLimit = 100f;
        [Tooltip("The hinge links that make up the joint above this part, on a body built from a MuJoCo model. Empty: the part's own link is its joint.")]
        public ArticulationBody[] drives = new ArticulationBody[0];
        public Renderer[] renderers;

        public int DriveCount => drives.Length > 0 ? drives.Length : 1;
        public ArticulationBody Drive(int i) => drives.Length > 0 ? drives[i] : body;
        /// <summary>Torque the i-th hinge may produce, N m.</summary>
        public float DriveLimit(int i) => drives.Length > 0 ? drives[i].xDrive.forceLimit : torqueLimit;

        /// <summary>Drive torque over the joint's limit, 0..1, fast up and slow down so a punch is visible.</summary>
        [System.NonSerialized] public float stress;
        /// <summary>Accumulated damage on this part, 0..1. Fades the heat map into a bruise.</summary>
        [System.NonSerialized] public float bruise;

        void OnCollisionEnter(Collision c)
        {
            if (owner != null) owner.OnPartTouched(this, c, true);
        }
    }
}
