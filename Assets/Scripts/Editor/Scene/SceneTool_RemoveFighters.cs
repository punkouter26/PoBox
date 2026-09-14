using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoBox.Editor
{
    /// <summary>
    /// One-shot: takes the named fighters out of the two shipping contest
    /// scenes, and out of each scene's spawner roster with them.
    ///
    /// WHY THIS EXISTS AS A TOOL RATHER THAN AS HAND-EDITED YAML. Removing a
    /// fighter means deleting a whole hierarchy -- root transform, every bone
    /// under it, every collider and joint on those bones -- and then deleting
    /// any fileID that pointed into that hierarchy from ELSEWHERE in the file.
    /// Miss one and the scene loads with a dangling reference, which is exactly
    /// the failure <see cref="SceneTool_Audit"/> was written to catch. Opening
    /// the scene and letting Unity's own serializer write it back is the only
    /// way to be sure the references went with it.
    ///
    /// THE ROSTER IS NOT WHAT PUTS FIGHTERS ON THE MAT. Both scenes have
    /// `Systems_MiniGameLauncher` DISABLED, so `SpawnAndBegin` never runs and
    /// the roster is inert data; the bodies standing in the scene are the cast.
    /// The roster is still edited here so the scene does not carry two
    /// contradicting lists -- the defect `Systems_FighterIdentity` records
    /// having already cost this project a silently empty slot. If the launcher
    /// is ever switched on, both lists already agree.
    ///
    /// Invoked once through the command bridge, then deleted:
    ///   echo PoBox.Editor.SceneTool_RemoveFighters.Run > Temp/agent-command.txt
    /// </summary>
    internal static class SceneTool_RemoveFighters
    {
        private static readonly string[] Scenes =
        {
            "Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity",
            "Assets/Scenes/SCN_TEST_WALK_CONTEST.unity",
        };

        private const string MenuScene = "Assets/Scenes/SCN_MENU.unity";

        /// <summary>Display names to drop. The scene objects are `Contest_` + this.</summary>
        private static readonly string[] RemovedNames = { "Standard", "Bot" };

        public static void Run()
        {
            // OpenScene discards unsaved edits without prompting. Refuse instead.
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene open = SceneManager.GetSceneAt(i);
                if (!open.isDirty) { continue; }
                Debug.LogError($"SceneTool_RemoveFighters: REFUSED. Scene '{open.name}' has unsaved " +
                               "changes, and this opens scenes, which would discard them.");
                return;
            }

            string original = SceneManager.GetActiveScene().path;
            var report = new List<string>();

            foreach (string scenePath in Scenes)
            {
                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                string label = System.IO.Path.GetFileNameWithoutExtension(scenePath);

                int bodies = RemoveBodies(scene, label, report);
                int entries = RemoveRosterEntries(label, report);

                if (bodies > 0 || entries > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
            }

            // Re-saving the menu drops the orphaned `_fighterNames` array. The
            // field no longer exists on Systems_MiniGameMenu -- the menu reads
            // Systems_FighterIdentity.PickableNames instead -- so the serialized
            // value has nothing left to deserialize into and only Unity's writer
            // can take it out.
            Scene menu = EditorSceneManager.OpenScene(MenuScene, OpenSceneMode.Single);
            EditorSceneManager.MarkSceneDirty(menu);
            EditorSceneManager.SaveScene(menu);
            report.Add("SCN_MENU_RESAVED (orphaned _fighterNames dropped)");

            if (!string.IsNullOrEmpty(original))
            {
                EditorSceneManager.OpenScene(original, OpenSceneMode.Single);
            }

            Debug.Log("CAST REMOVAL | " + string.Join(" | ", report));
        }

        /// <summary>Deletes the root objects whose name is Contest_&lt;name&gt;.</summary>
        private static int RemoveBodies(Scene scene, string label, List<string> report)
        {
            int removed = 0;
            // GetRootGameObjects, not GameObject.Find: a fighter stood down at
            // author time may sit inactive, and Find skips inactive objects.
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (string name in RemovedNames)
                {
                    if (root.name != $"Contest_{name}") { continue; }
                    report.Add($"{label}: deleted scene object '{root.name}'");
                    Object.DestroyImmediate(root);
                    removed++;
                    break;
                }
            }
            if (removed == 0)
            {
                report.Add($"{label}: no Contest_ bodies found (already gone?)");
            }
            return removed;
        }

        /// <summary>Removes this scene's roster entries for the dropped names.</summary>
        private static int RemoveRosterEntries(string label, List<string> report)
        {
            var spawner = Object.FindAnyObjectByType<Systems_ContestSpawner>(FindObjectsInactive.Include);
            if (spawner == null)
            {
                report.Add($"{label}: no spawner -- roster not touched");
                return 0;
            }

            var serialized = new SerializedObject(spawner);
            SerializedProperty roster = serialized.FindProperty("_roster");
            if (roster == null)
            {
                report.Add($"{label}: _roster field not found (renamed?)");
                return 0;
            }

            int removed = 0;
            // Backwards, so the indices still to be visited do not shift.
            for (int i = roster.arraySize - 1; i >= 0; i--)
            {
                string displayName =
                    roster.GetArrayElementAtIndex(i).FindPropertyRelative("displayName").stringValue;
                if (System.Array.IndexOf(RemovedNames, displayName) < 0) { continue; }
                report.Add($"{label}: roster entry '{displayName}' removed");
                roster.DeleteArrayElementAtIndex(i);
                removed++;
            }

            var remaining = new List<string>();
            for (int i = 0; i < roster.arraySize; i++)
            {
                remaining.Add(roster.GetArrayElementAtIndex(i).FindPropertyRelative("displayName").stringValue);
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            report.Add($"{label}: roster now [{string.Join(", ", remaining)}]");
            return removed;
        }
    }
}
