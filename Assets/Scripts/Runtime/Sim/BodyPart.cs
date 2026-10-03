using UnityEngine;

namespace PoBox.Sim
{
    /// <summary>
    /// One limb of a fighter, for what is drawn on it: which MuJoCo joints move it (their stress is shown
    /// as a glow), and the bruise it carries. The body itself is the MuJoCo plugin's (<see cref="Mj.MjBoxer"/>).
    /// </summary>
    public class BodyPart : MonoBehaviour
    {
        public Fighter owner;
        public PartKind kind;
        [Tooltip("-1 left, +1 right, 0 on the centre line.")]
        public int side;
        [Tooltip("The boxer's joint indices (MjBoxer order) of the joint above this part.")]
        public int[] joints = new int[0];
        public Renderer[] renderers;

        /// <summary>Drive torque over the joint's limit, 0..1, fast up and slow down so a punch is visible.</summary>
        [System.NonSerialized] public float stress;
        /// <summary>Accumulated damage on this part, 0..1. Shown as a bruise.</summary>
        [System.NonSerialized] public float bruise;
    }
}
