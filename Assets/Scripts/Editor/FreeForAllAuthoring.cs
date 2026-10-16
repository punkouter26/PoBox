using System.Linq;
using System.Text;
using PoBox.Broadcast;
using PoBox.Fx;
using PoBox.Mj;
using PoBox.Sim;
using PoBox.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox.EditorTools
{
    /// <summary>
    /// The free-for-all, made once in the editor and then an ordinary scene:
    ///   * <see cref="BuildSokol"/>: Sokol's prefab, checked against the trainer's model, his corner prefabs, and his
    ///     footwork brain (f_sokol) promoted onto them. He has no boxing or get-up brain yet: he stands and turns to
    ///     face, and the referee stands him up.
    ///   * <see cref="BuildScene"/>: Assets/Scenes/Melee.unity, a copy of the arena without its two-boxer parts
    ///     (the bout and its judges, the broadcast director and its shots, the HUD, spoken commentary, the crowd's
    ///     mood), with one red-corner prefab of every boxer, <see cref="FreeForAll"/>, <see cref="MeleeCamera"/>
    ///     and <see cref="MeleeHud"/>; added to the build after the arena.
    /// </summary>
    public static class FreeForAllAuthoring
    {
        const string Arena = "Assets/Scenes/Arena.unity", Melee = "Assets/Scenes/Melee.unity";

        [MenuItem("PoBox/Free-for-all/Build Sokol")]
        static void BuildSokolMenu() => Debug.Log("[FreeForAll] " + BuildSokol());

        [MenuItem("PoBox/Free-for-all/Build Melee Scene")]
        static void BuildSceneMenu() => Debug.Log("[FreeForAll] " + BuildScene());

        public static string BuildSokol()
        {
            if (!Leave()) return "the open scene has unsaved changes: save or discard them first";
            var sb = new StringBuilder();
            AssetDatabase.Refresh();
            // An empty scene: the check compiles everything in it, and has to find only Sokol and his solo world.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            sb.AppendLine(MjRetrofit.BuildBoxer("sokol"));
            sb.AppendLine(MjRetrofit.Check("sokol").Trim());
            sb.AppendLine(MjRetrofit.BuildCorners("sokol"));
            sb.AppendLine(MjRetrofit.Promote("sokol", "f_sokol"));
            EditorSceneManager.OpenScene(Arena);
            return sb.ToString();
        }

        public static string BuildScene()
        {
            if (!Leave()) return "the open scene has unsaved changes: save or discard them first";
            var sb = new StringBuilder();
            AssetDatabase.DeleteAsset(Melee);
            if (!AssetDatabase.CopyAsset(Arena, Melee)) return "could not copy the arena";
            var scene = EditorSceneManager.OpenScene(Melee);
            GameObject Root(string name) => scene.GetRootGameObjects().FirstOrDefault(g => g.name == name);

            foreach (string name in new[] { "Bout", "Broadcast", "HUD", "Speech Commentary" })
                if (Root(name) != null) { Object.DestroyImmediate(Root(name)); sb.Append($"-{name} "); }
            foreach (var mood in Object.FindObjectsByType<ArenaMood>(FindObjectsInactive.Include)) Object.DestroyImmediate(mood);
            // Eight boxers' punches, steps and voices at once.
            foreach (var audio in Object.FindObjectsByType<PoBox.Audio.AudioDirector>(FindObjectsInactive.Include)) audio.voices = 28;

            // One camera, framing whoever is still in.
            Transform cameras = Root("Cameras").transform;
            foreach (Transform c in cameras.Cast<Transform>().ToList())
                if (c.name != "Main Camera") Object.DestroyImmediate(c.gameObject);
            GameObject main = cameras.Find("Main Camera").gameObject;
            foreach (var brain in main.GetComponents<Component>().Where(c => c.GetType().Name == "CinemachineBrain").ToList()) Object.DestroyImmediate(brain);
            var cam = main.AddComponent<MeleeCamera>();

            // Every boxer once, dressed for the red corner; FreeForAll gives each a prefix and a colour of its own.
            var ring = Object.FindAnyObjectByType<MjRing>();
            Transform fighters = Root("Fighters").transform;
            foreach (Transform c in fighters.Cast<Transform>().ToList()) Object.DestroyImmediate(c.gameObject);
            float floorY = GameObject.Find("Corners/Red Corner").transform.position.y;
            var list = new System.Collections.Generic.List<Fighter>();
            foreach (string name in MjRetrofit.Boxers())
            {
                string title = char.ToUpperInvariant(name[0]) + name.Substring(1);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Boxers/{title}/{title} Red.prefab");
                if (prefab == null) { sb.Append($"(no {title} Red) "); continue; }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, fighters);
                go.transform.position = new Vector3(0f, floorY, 0f);
                var f = go.GetComponent<Fighter>();
                f.ring = ring;
                f.ringCentre = Vector3.zero;
                f.ringHalf = ring.ringHalf;
                go.GetComponent<MjBrain>().ring = ring;
                go.SetActive(false);
                list.Add(f);
            }

            var rulesGo = new GameObject("Free For All");
            rulesGo.transform.position = new Vector3(ring.centre.x, floorY, ring.centre.y);
            var rules = rulesGo.AddComponent<FreeForAll>();
            rules.fighters = list.ToArray();
            rules.ring = ring;
            cam.rules = rules;

            var hudGo = new GameObject("HUD");
            var doc = hudGo.AddComponent<UIDocument>();
            doc.panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/UI/PoBoxPanel.asset");
            doc.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI/Melee.uxml");
            hudGo.AddComponent<MeleeHud>().rules = rules;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            var scenes = EditorBuildSettings.scenes.ToList();
            if (!scenes.Any(s => s.path == Melee))
            {
                int at = scenes.FindIndex(s => s.path == Arena);
                scenes.Insert(at < 0 ? scenes.Count : at + 1, new EditorBuildSettingsScene(Melee, true));
                EditorBuildSettings.scenes = scenes.ToArray();
            }
            sb.Append($"\n{list.Count} boxers: {string.Join(", ", list.Select(f => f.displayName))}; {MjRetrofit.PhysXCensus()}");
            EditorSceneManager.OpenScene(Arena);
            return sb.ToString();
        }

        /// <summary>The open scene may be closed: nothing in it unsaved.</summary>
        static bool Leave()
        {
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                if (EditorSceneManager.GetSceneAt(i).isDirty) return false;
            return true;
        }
    }
}
