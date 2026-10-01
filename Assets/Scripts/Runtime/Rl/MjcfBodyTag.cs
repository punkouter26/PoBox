using UnityEngine;

namespace PoBox.Rl
{
    /// <summary>Records the trainer's name for a body on the ArticulationBody that stands for it.</summary>
    // In a file of its own: Unity can only save a component into a scene if its class has the file's name.
    public class MjcfBodyTag : MonoBehaviour
    {
        public string bodyName;
    }
}
