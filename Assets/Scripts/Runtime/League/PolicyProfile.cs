using UnityEngine;

namespace PoBox.League
{
    /// <summary>
    /// One entry on the ladder: a name, a look, and what drives the fighter.
    ///
    /// Today "what drives it" is the style numbers below, a stand-in for what the boxer's policy does.
    /// The slot that matters is <see cref="checkpoint"/>: once a policy has been trained in MuJoCo/Newton on
    /// the owner's skinned mesh, each exported ONNX checkpoint becomes one of these assets with its
    /// generation number, and the ladder turns into checkpoint against checkpoint without any other change.
    /// </summary>
    [CreateAssetMenu(menuName = "PoBox/Policy Profile", fileName = "Profile_New")]
    public class PolicyProfile : ScriptableObject
    {
        public string displayName = "ROOKIE";
        [Tooltip("Training iteration the checkpoint was exported at. 0 for a hand-written style.")]
        public int generation;
        [Tooltip("The exported ONNX policy (an Inference Engine ModelAsset). Empty means the scripted stand-in is used.")]
        public Object checkpoint;
        public Color trunks = Color.white;

        [Header("Scripted style (stand-in until a checkpoint is assigned)")]
        [Range(0f, 1f)] public float aggression = 0.5f;
        [Tooltip("Preferred distance between the two pelvises, metres.")]
        [Range(0.7f, 1.3f)] public float range = 0.95f;
        [Tooltip("Top walking speed, m/s.")]
        [Range(0.3f, 1.6f)] public float footSpeed = 0.9f;
        [Range(0f, 1f)] public float combo = 0.3f;
        [Range(0f, 1f)] public float hookBias = 0.25f;
        [Range(0f, 1f)] public float bodyBias = 0.15f;
        [Range(0f, 1f)] public float uppercutBias = 0.1f;
        [Range(0f, 1f)] public float headMovement = 0.4f;
        [Tooltip("How quickly the hands come back and how high they sit.")]
        [Range(0f, 1f)] public float guard = 0.5f;

        public bool IsTrained => checkpoint != null;
        public string Badge => IsTrained ? "GEN " + generation : "SCRIPTED";
    }
}
