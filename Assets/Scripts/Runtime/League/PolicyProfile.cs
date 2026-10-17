using UnityEngine;

namespace PoBox.League
{
    /// <summary>
    /// One entry on the ladder (Assets/League/Entrant_NAME.asset): a name and the exported ONNX checkpoint
    /// with its training generation.
    /// </summary>
    [CreateAssetMenu(menuName = "PoBox/Policy Profile", fileName = "Profile_New")]
    public class PolicyProfile : ScriptableObject
    {
        public string displayName = "ROOKIE";
        [Tooltip("Training iteration the checkpoint was exported at. 0 for a hand-written style.")]
        public int generation;
        [Tooltip("The exported ONNX policy (an Inference Engine ModelAsset).")]
        public Object checkpoint;

        public bool IsTrained => checkpoint != null;
        public string Badge => IsTrained ? "GEN " + generation : "SCRIPTED";
    }
}
