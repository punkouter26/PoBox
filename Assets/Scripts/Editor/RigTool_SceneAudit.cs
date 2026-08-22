// filepath: Assets/Scripts/Editor/RigTool_SceneAudit.cs
//
// Static audit of the shippable (non-training) scenes.
//
// Written 2026-08-22 after a manual pass through SCN_MENU and both contest
// scenes turned up four defects that had been shipping for months and that
// nothing in the project could have caught:
//
//   * every contest fighter still pointed at Locomotion_gen7, five generations
//     stale, because the brain path is a const in a scene-generator tool and
//     nothing re-checked the scene against it
//   * three of four roster tints were MISSING references -- the .mat they
//     pointed at was gone, so Unity handed back a fake-null and the spawner's
//     `entry.tint != null` guard silently skipped tinting
//   * the walk contest's FinishLine used the default `Lit` material, making it
//     invisible against a grey floor
//   * the drama camera framed 2 of 8 fighters
//
// A broken object reference is the recurring shape here, and it is invisible in
// the inspector unless you look at the right field: Unity resolves a dangling
// GUID to a null that is NOT `ReferenceEquals(null)`. SerializedProperty exposes
// the difference, which is what MissingReference below tests.
//
// Deliberately static: it opens scenes and reads serialized data, no play mode.
// That keeps it fast enough to run before a commit and usable in batch mode:
//   Unity.exe -batchmode -quit -projectPath . \
//             -executeMethod PoBox.Editor.RigTool_SceneAudit.RunBatch

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoBox.Editor
{
    internal static class RigTool_SceneAudit
    {
        private static readonly string[] AuditedScenes =
        {
            "Assets/Scenes/SCN_MENU.unity",
            "Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity",
            "Assets/Scenes/SCN_TEST_WALK_CONTEST.unity",
        };

        [MenuItem("Tools/ML Boxing/Audit Shippable Scenes")]
        public static void RunFromMenu()
        {
            List<string> problems = Run();
            if (problems.Count == 0)
            {
                Debug.Log("Scene audit: all clear.");
                return;
            }
            Debug.LogError($"Scene audit found {problems.Count} problem(s):\n  " + string.Join("\n  ", problems));
        }

        /// <summary>Batch entry point: non-zero exit when the audit fails.</summary>
        public static void RunBatch()
        {
            List<string> problems = Run();
            foreach (string problem in problems)
            {
                Debug.LogError("Scene audit: " + problem);
            }
            EditorApplication.Exit(problems.Count == 0 ? 0 : 1);
        }

        public static List<string> Run()
        {
            var problems = new List<string>();
            string original = SceneManager.GetActiveScene().path;

            foreach (string scenePath in AuditedScenes)
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
                {
                    problems.Add($"{scenePath}: scene asset missing");
                    continue;
                }
                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                string label = System.IO.Path.GetFileNameWithoutExtension(scenePath);

                AuditRoster(label, problems);
                AuditRenderers(label, problems);
            }

            if (!string.IsNullOrEmpty(original))
            {
                EditorSceneManager.OpenScene(original, OpenSceneMode.Single);
            }
            return problems;
        }

        private static void AuditRoster(string label, List<string> problems)
        {
            var spawner = Object.FindAnyObjectByType<Systems_ContestSpawner>(FindObjectsInactive.Include);
            if (spawner == null)
            {
                return; // menu scene has no roster; not an error
            }

            var serialized = new SerializedObject(spawner);
            SerializedProperty roster = serialized.FindProperty("_roster");
            if (roster == null || roster.arraySize == 0)
            {
                problems.Add($"{label}: spawner has an empty roster");
                return;
            }

            for (int i = 0; i < roster.arraySize; i++)
            {
                SerializedProperty entry = roster.GetArrayElementAtIndex(i);
                string name = entry.FindPropertyRelative("displayName").stringValue;
                bool heuristic = entry.FindPropertyRelative("forceHeuristic").boolValue;
                string who = $"{label}: roster '{name}'";

                CheckRef(entry.FindPropertyRelative("prefab"), who + " prefab", required: true, problems);
                CheckRef(entry.FindPropertyRelative("model"), who + " model", required: !heuristic, problems);
                // tint is genuinely optional -- textured character models must NOT
                // be flattened by one -- so only a DANGLING reference is a fault.
                CheckRef(entry.FindPropertyRelative("tint"), who + " tint", required: false, problems);
            }

            SerializedProperty systemsRoot = serialized.FindProperty("_systemsRoot");
            CheckRef(systemsRoot, $"{label}: spawner _systemsRoot", required: true, problems);
        }

        /// <summary>
        /// Flags the two distinguishable failures: a dangling reference (a GUID
        /// that no longer resolves) and, when required, an empty one.
        /// </summary>
        private static void CheckRef(SerializedProperty property, string who, bool required, List<string> problems)
        {
            if (property == null)
            {
                problems.Add(who + ": field not found (renamed?)");
                return;
            }
            if (MissingReference(property))
            {
                problems.Add(who + ": MISSING reference — the asset it points at no longer exists");
                return;
            }
            if (required && property.objectReferenceValue == null)
            {
                problems.Add(who + ": not assigned");
            }
        }

        // A dangling reference keeps its instance id but resolves to null.
        private static bool MissingReference(SerializedProperty property)
        {
            return property.objectReferenceValue == null
                && property.objectReferenceEntityIdValue != default;
        }

        /// <summary>
        /// Anything left on Unity's built-in materials is almost certainly
        /// unfinished: `Lit` and `Default-Material` are what a primitive is born
        /// with. The walk contest's finish line shipped invisible that way.
        /// </summary>
        private static void AuditRenderers(string label, List<string> problems)
        {
            foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
            {
                Material material = renderer.sharedMaterial;
                if (material == null)
                {
                    problems.Add($"{label}: renderer '{renderer.name}' has no material");
                    continue;
                }
                if (material.name != "Lit" && material.name != "Default-Material")
                {
                    continue;
                }
                // Ground planes are allowed to be plain; call out the rest.
                if (renderer.gameObject.name.Contains("Ground"))
                {
                    continue;
                }
                problems.Add($"{label}: renderer '{renderer.name}' still uses the built-in '{material.name}' material");
            }
        }
    }
}
