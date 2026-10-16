using System.Text;
using PoBox.Mj;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PoBox.EditorTools
{
    /// <summary>
    /// What the features of 2026-10-15 need in the scenes, put there once and idempotent (run it again and nothing
    /// changes): the instant replay's recorder (<see cref="MjReplay"/>) beside the ring in the arena and the
    /// free-for-all. Everything else of theirs (the pick before the bell, the cup, chat plays, the brain cam, the
    /// corner, SHARE) lives in the HUDs' documents and scripts and needs nothing authored.
    /// </summary>
    public static class FeatureAuthoring
    {
        static readonly string[] Scenes = { "Assets/Scenes/Arena.unity", "Assets/Scenes/Melee.unity" };

        [MenuItem("PoBox/Do All/Features/Add Instant Replay")]
        static void AddReplayMenu() => Debug.Log("[Features] " + AddReplay());

        public static string AddReplay()
        {
            if (EditorApplication.isPlaying) return "stop play first";
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return "the open scene has unsaved changes";
            string back = EditorSceneManager.GetActiveScene().path;
            var sb = new StringBuilder();
            foreach (string path in Scenes)
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                MjRing ring = Object.FindAnyObjectByType<MjRing>(FindObjectsInactive.Include);
                if (ring == null) { sb.Append($"{path}: no ring; "); continue; }
                if (ring.GetComponent<MjReplay>() != null) { sb.Append($"{path}: already there; "); continue; }
                Undo.AddComponent<MjReplay>(ring.gameObject);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                sb.Append($"{path}: added to {ring.name}; ");
            }
            if (!string.IsNullOrEmpty(back) && back != EditorSceneManager.GetActiveScene().path) EditorSceneManager.OpenScene(back);
            return sb.ToString();
        }
    }
}
