using UnityEditor;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// One click: generate capsule biped, auto-rig it, prepare for training,
    /// save the prefab. The individual steps stay available for FBX imports.
    /// </summary>
    internal static class RigTool_QuickBuild
    {
        [MenuItem("Tools/ML Boxing/Quick - Build Capsule Fighter Prefab")]
        public static void BuildCapsuleFighter()
        {
            GameObject root = RigTool_CapsuleBiped.Build("Fighter_Capsule");
            var rig = RigTool_AutoRig.AutoRig(root);
            if (rig == null)
            {
                Object.DestroyImmediate(root);
                return;
            }
            GameObject prefab = RigTool_PrepareForTraining.Prepare(root);
            if (prefab == null)
            {
                Object.DestroyImmediate(root);
                return;
            }
            Selection.activeGameObject = root;
        }
    }
}
