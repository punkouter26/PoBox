using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PoBox.Tests
{
    /// <summary>
    /// Every scene, loaded as the game loads it, searched for what breaks quietly: a component whose script is gone, a
    /// reference to an asset or object that was deleted (the inspector's "Missing", not an empty "None"), and a
    /// document with no layout or no panel settings. Switched-off objects are searched too, so the arena's corner
    /// prefabs for every boxer (and the free-for-all's eight) are covered. The scenes hold no UnityEvent wiring; the
    /// buttons are hooked in code, and the state-flow test presses them.
    /// </summary>
    public class SceneIntegrityTests
    {
        static readonly string[] Scenes = { "Menu", "Arena", "Melee", "Testbed" };

        [UnityTest]
        public IEnumerator SceneHasNoMissingScriptsOrBrokenReferences([ValueSource(nameof(Scenes))] string scene)
        {
#if UNITY_EDITOR
            if (scene == "Testbed")
                yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/Testbed.unity", new LoadSceneParameters(LoadSceneMode.Single));
            else
            {
                if (!Application.CanStreamedLevelBeLoaded(scene)) Assert.Ignore(scene + " is not in the build");
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
            }
            yield return null;

            var problems = new List<string>();
            int searched = 0;
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    Search(t.gameObject, problems);
                    searched++;
                }
            Assert.AreEqual(scene, SceneManager.GetActiveScene().name, $"{scene} was not loaded");
            Assert.Greater(searched, 0, $"{scene} is empty");
            Assert.IsEmpty(problems, $"{scene}:\n{string.Join("\n", problems)}");
#else
            Assert.Ignore("needs the editor's serialization to tell a missing reference from an empty one");
            yield break;
#endif
        }

        /// <summary>The search itself: a reference to a mesh that has since been destroyed is found, an empty one is not.</summary>
        [Test]
        public void TheSearchFindsADeletedReferenceAndNotAnEmptyOne()
        {
#if UNITY_EDITOR
            var go = new GameObject("integrity probe");
            var empty = new GameObject("integrity probe, empty");
            try
            {
                var mesh = new Mesh();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                empty.AddComponent<MeshFilter>();
                Object.DestroyImmediate(mesh);
                var problems = new List<string>();
                Search(go, problems);
                Search(empty, problems);
                Assert.AreEqual(1, problems.Count, string.Join("\n", problems));
                StringAssert.StartsWith("integrity probe:", problems[0]);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(empty);
            }
#else
            Assert.Ignore("needs the editor");
#endif
        }

#if UNITY_EDITOR
        static void Search(GameObject go, List<string> problems)
        {
            string path = Path(go.transform);
            int missing = UnityEditor.GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
            if (missing > 0) problems.Add($"{path}: {missing} missing script(s)");

            foreach (Component c in go.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                if (c is UIDocument doc && (doc.visualTreeAsset == null || doc.panelSettings == null))
                    problems.Add($"{path}: a UIDocument without {(doc.visualTreeAsset == null ? "a layout" : "panel settings")}");

                using (var so = new UnityEditor.SerializedObject(c))
                {
                    UnityEditor.SerializedProperty p = so.GetIterator();
                    while (p.Next(true))
                        if (p.propertyType == UnityEditor.SerializedPropertyType.ObjectReference
                            && p.objectReferenceValue == null && p.objectReferenceEntityIdValue.IsValid())
                            problems.Add($"{path}: {c.GetType().Name}.{p.propertyPath} points at something deleted");
                }
            }
        }
#endif

        static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;
    }
}
