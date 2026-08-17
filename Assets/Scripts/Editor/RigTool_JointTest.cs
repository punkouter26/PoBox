using PoBox;
using UnityEditor;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// Attaches the joint-range sweep tester to a rigged fighter. Run in
    /// SCN_RIGSTAGE, press Play, watch the console for FLIPPED verdicts
    /// before starting any training run.
    /// </summary>
    internal static class RigTool_JointTest
    {
        [MenuItem("Tools/ML Boxing/6. Add Joint Range Tester To Selected")]
        public static void AddTester()
        {
            GameObject root = Selection.activeGameObject;
            if (root == null || root.GetComponent<Systems_FighterRig>() == null)
            {
                EditorUtility.DisplayDialog("Joint Range Tester", "Select a rigged fighter root first.", "OK");
                return;
            }
            var tester = root.GetComponent<Systems_JointRangeTester>();
            if (tester == null)
            {
                tester = root.AddComponent<Systems_JointRangeTester>();
            }
            tester.EditorInitialize(root.GetComponent<Systems_FighterRig>());
            EditorUtility.SetDirty(root);
            Debug.Log("RigTool: tester attached. Press Play in SCN_RIGSTAGE and watch the console. Remove the component before saving the prefab.");
        }
    }
}
