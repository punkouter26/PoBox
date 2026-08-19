using PoBox;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoBox.Editor
{
    /// <summary>
    /// Builds SCN_TEST_WALK_CONTEST: the roster lined up along one edge of a
    /// ring-sized lane, racing straight to the far edge under
    /// <see cref="Systems_WalkContest"/>. Each fighter loads its walk brain
    /// from Assets/Agents/&lt;Name&gt;_Walk/Boxer.onnx and falls back to the
    /// balance brain, then to raw physics, so the scene is always playable.
    /// Visual test scene — camera and light included, no trainer needed:
    /// just press Play.
    /// </summary>
    internal static class RigTool_WalkContestScene
    {
        private const string SCENE_PATH = "Assets/Scenes/SCN_TEST_WALK_CONTEST.unity";
        private const float RING_SIZE = 6.1f;      // matches SCN_TRAIN_WALK
        private const float EDGE_INSET = 0.25f;
        private const float SPAWN_HEIGHT = 0.03f;

        // Mirrors the balance contest roster so the two mini-games field the
        // same line-up. forceHeuristic: the code-driven PD bot (project rule).
        private static readonly (string prefabPath, string display, bool forceHeuristic)[] Roster =
        {
            ("Assets/Prefabs/Fighters/Fighter_Capsule.prefab", "Standard", false),
            ("Assets/Prefabs/Fighters/Fighter_Grandma.prefab", "Grandma", false),
            ("Assets/Prefabs/Fighters/Fighter_Grandpa.prefab", "Grandpa", false),
            ("Assets/Prefabs/Fighters/Fighter_Capsule.prefab", "Bot", true)
        };

        [MenuItem("Tools/ML Boxing/8. Create Walk Contest Scene")]
        public static void Create()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            float half = RING_SIZE * 0.5f;
            float startZ = -half + EDGE_INSET;
            float goalZ = half - EDGE_INSET;
            float goalDistance = goalZ - startZ;
            float laneSpacing = RING_SIZE / Roster.Length;
            float laneOrigin = -laneSpacing * (Roster.Length - 1) * 0.5f;

            BuildGround();
            BuildLight();
            BuildCamera(goalZ);
            BuildFinishLine(goalZ);

            int placed = 0;
            for (int rosterIndex = 0; rosterIndex < Roster.Length; rosterIndex++)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Roster[rosterIndex].prefabPath);
                if (prefab == null)
                {
                    Debug.LogWarning($"RigTool: walk contest skips missing prefab {Roster[rosterIndex].prefabPath}");
                    continue;
                }
                var position = new Vector3(laneOrigin + rosterIndex * laneSpacing, SPAWN_HEIGHT, startZ);
                GameObject instance = RigTool_ContestScene.SpawnContestant(
                    prefab, Roster[rosterIndex].display, position, Roster[rosterIndex].forceHeuristic);

                // Every racer faces the finish line; the referee measures
                // progress along this same world axis.
                instance.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
                ApplyWalkBrain(instance, Roster[rosterIndex].display, Roster[rosterIndex].forceHeuristic);
                placed++;
            }

            BuildReferee(goalDistance);

            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            Debug.Log($"RigTool: walk contest scene saved to {SCENE_PATH} — {placed} racers, " +
                $"{goalDistance:F2} m to the finish. Press Play (no trainer needed).");
        }

        // Prefers a walk brain, falls back to the balance brain the balance
        // contest already uses. SpawnContestant has warned about a missing
        // balance brain, so only the walk-specific downgrade is reported.
        private static void ApplyWalkBrain(GameObject instance, string display, bool forceHeuristic)
        {
            if (forceHeuristic)
            {
                return;
            }
            var behavior = instance.GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
            var walkModel = AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(
                $"Assets/Agents/{display}_Walk/Boxer.onnx");
            if (walkModel != null)
            {
                behavior.Model = walkModel;
                behavior.BehaviorType = Unity.MLAgents.Policies.BehaviorType.InferenceOnly;
                return;
            }
            Debug.LogWarning($"RigTool: no walk brain at Assets/Agents/{display}_Walk/Boxer.onnx — " +
                $"{display} races on its balance brain and will most likely just stand still.");
        }

        private static void BuildGround()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(40f, 1f, 40f);
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            var highFriction = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(RigTool_Config.HIGH_FRICTION_MATERIAL_PATH);
            if (highFriction != null)
            {
                ground.GetComponent<Collider>().sharedMaterial = highFriction;
            }
        }

        private static void BuildLight()
        {
            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightObject.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
        }

        // Side-on so the whole lane is visible end to end — a head-on camera
        // would hide which racer is actually ahead.
        private static void BuildCamera(float goalZ)
        {
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            cameraObject.transform.position = new Vector3(-7.5f, 3f, 0f);
            cameraObject.transform.rotation = Quaternion.Euler(12f, 90f, 0f);
            _ = goalZ;
        }

        private static void BuildFinishLine(float goalZ)
        {
            GameObject finishLine = GameObject.CreatePrimitive(PrimitiveType.Cube);
            finishLine.name = "FinishLine";
            finishLine.transform.localScale = new Vector3(RING_SIZE, 0.02f, 0.12f);
            finishLine.transform.position = new Vector3(0f, 0.01f, goalZ);
            Object.DestroyImmediate(finishLine.GetComponent<BoxCollider>());
        }

        private static void BuildReferee(float goalDistance)
        {
            var refereeObject = new GameObject("WalkContestReferee");
            var document = refereeObject.AddComponent<UIDocument>();
            document.panelSettings = RigTool_ContestScene.GetOrCreatePanelSettings();
            var referee = refereeObject.AddComponent<Systems_WalkContest>();
            referee.EditorInitialize(Vector3.forward, goalDistance);
        }
    }
}
