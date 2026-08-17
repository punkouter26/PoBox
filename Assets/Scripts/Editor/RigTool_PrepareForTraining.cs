using System.IO;
using PoBox;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// Attaches the ML-Agents stack to a rigged fighter and saves the prefab.
    /// Observation and action sizes are computed from the rig — never typed.
    /// </summary>
    internal static class RigTool_PrepareForTraining
    {
        private const string BEHAVIOR_NAME = "Boxer";

        [MenuItem("Tools/ML Boxing/3. Prepare for Training")]
        public static void PrepareSelected()
        {
            GameObject root = Selection.activeGameObject;
            if (root == null)
            {
                EditorUtility.DisplayDialog("Prepare for Training", "Select the rigged fighter root first.", "OK");
                return;
            }
            Prepare(root);
        }

        public static GameObject Prepare(GameObject root)
        {
            var rig = root.GetComponent<Systems_FighterRig>();
            if (rig == null)
            {
                EditorUtility.DisplayDialog("Prepare for Training",
                    root.name + " has no Systems_FighterRig. Run Auto-Rig first.", "OK");
                return null;
            }

            var stamina = root.GetComponent<Systems_Stamina>();
            if (stamina == null)
            {
                stamina = root.AddComponent<Systems_Stamina>();
            }
            stamina.EditorInitialize(rig);

            var agent = root.GetComponent<Agent_FighterBoxing>();
            if (agent == null)
            {
                agent = root.AddComponent<Agent_FighterBoxing>();
            }
            agent.EditorInitialize(rig, stamina, rig.FootLeftSensor, rig.FootRightSensor);
            agent.MaxStep = 0;

            int observationCount = Agent_FighterBoxing.ComputeObservationCount(rig.JointCount);
            int actionCount = rig.DofCount;

            var behavior = root.GetComponent<BehaviorParameters>();
            behavior.BehaviorName = BEHAVIOR_NAME;
            behavior.BrainParameters.VectorObservationSize = observationCount;
            behavior.BrainParameters.NumStackedVectorObservations = 1;
            behavior.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(actionCount);

            var requester = root.GetComponent<DecisionRequester>();
            if (requester == null)
            {
                requester = root.AddComponent<DecisionRequester>();
            }
            requester.DecisionPeriod = 1;

            EditorUtility.SetDirty(root);
            GameObject prefab = SavePrefab(root);
            Debug.Log($"RigTool: '{root.name}' prepared — behavior '{BEHAVIOR_NAME}', " +
                      $"{observationCount} observations, {actionCount} continuous actions. " +
                      $"Prefab: {AssetDatabase.GetAssetPath(prefab)}");
            return prefab;
        }

        private static GameObject SavePrefab(GameObject root)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
            {
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            }
            if (!AssetDatabase.IsValidFolder(RigTool_Config.PREFAB_FOLDER))
            {
                AssetDatabase.CreateFolder("Assets/Prefabs", "Fighters");
            }
            string path = Path.Combine(RigTool_Config.PREFAB_FOLDER, root.name + ".prefab").Replace('\\', '/');
            return PrefabUtility.SaveAsPrefabAssetAndConnect(root, path, InteractionMode.UserAction);
        }
    }
}
