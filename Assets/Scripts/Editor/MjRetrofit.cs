using System.IO;
using System.Linq;
using System.Text;
using Mujoco;
using PoBox.Mj;
using PoBox.Rl;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;

namespace PoBox.EditorTools
{
    /// <summary>
    /// The retrofit's importer: a boxer's MuJoCo model, as the trainer wrote it, becomes a prefab of the
    /// MuJoCo plugin's components with the owner's mesh riding on it; and the check that what the plugin
    /// compiles from the scene is the trainer's model. Run from the menu or with `unity command eval`.
    /// It is an importer, not a scene builder: what it leaves is a prefab and objects in the open scene,
    /// which are then placed, saved and edited like any others.
    /// </summary>
    public static class MjRetrofit
    {
        const string Models = "training/models/v2", Rigs = "training/rigs/v2", BoxersDir = "Assets/Boxers";

        static string Title(string name) => char.ToUpperInvariant(name[0]) + name.Substring(1);

        /// <summary>The boxer's solo model as MjBody, MjGeom, joints and drives under one root in the open scene.</summary>
        public static GameObject Import(string name)
        {
            // The plugin's own default is the built-in pipeline's shader, which this project does not have.
            const string shapeMaterial = BoxersDir + "/MjShape.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(shapeMaterial);
            if (material == null)
            {
                Directory.CreateDirectory(BoxersDir);
                material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(0.55f, 0.6f, 0.68f) };
                AssetDatabase.CreateAsset(material, shapeMaterial);
            }
            MjcfImporter.DefaultMujocoMaterial = material;
            var root = new MjcfImporter().ImportString(File.ReadAllText($"{Models}/{name}_solo.xml"), name);
            root.GetComponentInChildren<MjGlobalSettings>().UseRawGameObjectNames = true;   // the trainer's names, so the two models can be matched
            return root;
        }

        static T Copy<T>(string from, string to) where T : Object
        {
            File.Copy(from, to, true);
            AssetDatabase.ImportAsset(to);
            return AssetDatabase.LoadAssetAtPath<T>(to);
        }

        /// <summary>
        /// The boxer as a prefab in Assets/Boxers/NAME, and an instance of it in the open scene: its bodies,
        /// joints, drives and collision exclusions, its policy config, and its mesh bound to the bodies.
        /// The first boxer brought into a scene also leaves what every MuJoCo scene needs once: the solver
        /// settings, the floor and the pool of cubes. A scene that has them keeps its own.
        /// </summary>
        public static string BuildBoxer(string name)
        {
            string title = Title(name), dir = $"{BoxersDir}/{title}";
            Directory.CreateDirectory(dir);
            var imported = Import(name);

            var boxer = new GameObject(title);
            foreach (Transform child in imported.transform.Cast<Transform>().ToList())
                if (child.name.StartsWith("a_") || child.name == "actuators" || child.name == "excludes") child.SetParent(boxer.transform, true);
            // The mesh is what is seen; the shapes stay, switched off, for looking at the physics.
            foreach (var r in boxer.GetComponentsInChildren<MeshRenderer>()) r.enabled = false;

            var mj = boxer.AddComponent<MjBoxer>();
            mj.config = Copy<TextAsset>($"{Models}/{name}_policy_config.json", $"{dir}/config.json");
            Copy<TextAsset>($"{Models}/{name}_fingerprint.json", $"{dir}/fingerprint.json");
            if (File.Exists($"{Models}/{name}_reference_hold.json")) Copy<TextAsset>($"{Models}/{name}_reference_hold.json", $"{dir}/reference_hold.json");
            mj.policy = Copy<ModelAsset>($"{Models}/zero_policy.onnx", $"{BoxersDir}/zero_policy.onnx");

            string note = "no mesh";
            GameObject mesh = EntrantFactory.Mesh(name);
            if (mesh != null)
            {
                var skin = (GameObject)PrefabUtility.InstantiatePrefab(mesh);
                skin.name = title + " Mesh";
                skin.transform.SetParent(boxer.transform, false);
                var binder = boxer.AddComponent<SkinBinder>();
                binder.rigRoot = boxer.transform;
                binder.skin = skin;
                EntrantFactory.ConfigureBinder(binder, EntrantFactory.BoneMap(File.ReadAllText($"{Rigs}/{name}.json")), "glove_");
                note = $"{binder.map.Count} bones bound";
            }

            // What is left of the import is the world: keep it if this scene has none yet.
            bool hasWorld = Object.FindObjectsByType<MjGlobalSettings>(FindObjectsSortMode.None).Length > 1;
            if (hasWorld) Object.DestroyImmediate(imported);
            else
            {
                imported.name = "Mj World";
                var pool = new GameObject("Cube Pool");
                pool.transform.SetParent(imported.transform, false);
                foreach (Transform child in imported.transform.Cast<Transform>().ToList())
                    if (child.name.StartsWith("cube_")) child.SetParent(pool.transform, true);
                pool.AddComponent<MjCubePool>();
            }

            PrefabUtility.SaveAsPrefabAssetAndConnect(boxer, $"{dir}/{title}.prefab", InteractionMode.AutomatedAction);
            EditorUtility.SetDirty(boxer);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(boxer.scene);
            return $"{title}: {boxer.GetComponentsInChildren<MjBody>().Length} bodies, {boxer.GetComponentsInChildren<MjBaseJoint>().Length} joints, " +
                   $"{boxer.GetComponentsInChildren<MjActuator>().Length} drives, {boxer.GetComponentsInChildren<MjExclude>().Length} exclusions, {note}; " +
                   $"prefab {dir}/{title}.prefab; world {(hasWorld ? "kept as it was" : "made (settings, floor, cube pool)")}";
        }

        /// <summary>
        /// A trained rung into the game: the run's policy becomes the boxer's (Assets/Boxers/NAME/policy.onnx, set on
        /// its prefab), with the recordings from plain C MuJoCo that the gate tests hold Unity to, if they exist.
        /// </summary>
        public static string Promote(string name, string run)
        {
            string title = Title(name), dir = $"{BoxersDir}/{title}", from = $"training/checkpoints/{run}";
            var policy = Copy<ModelAsset>($"{from}/latest.onnx", $"{dir}/policy.onnx");
            foreach (string file in new[] { "reference_trajectory.json", "falls.json" })
                if (File.Exists($"{from}/{file}")) Copy<TextAsset>($"{from}/{file}", $"{dir}/{file}");
            string prefabPath = $"{dir}/{title}.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            root.GetComponent<MjBoxer>().policy = policy;
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            return $"{title} now runs {from}/latest.onnx";
        }

        /// <summary>The differences between the model the plugin compiles from the open scene and the trainer's, one a line.</summary>
        public static unsafe string Check(string name)
        {
            bool made = !MjScene.InstanceExists;
            var scene = MjScene.Instance;
            // The plugin takes MuJoCo's step from Unity's fixed step. In play the testbed sets it; here it is set for the check.
            float fixedStep = Time.fixedDeltaTime;
            Time.fixedDeltaTime = 0.005f;
            try
            {
                var doc = scene.CreateScene(skipCompile: true);
                Directory.CreateDirectory("Temp");
                File.WriteAllText($"Temp/mj_{name}.xml", doc.OuterXml);
                var model = MjEngineTool.LoadModelFromString(doc.OuterXml);
                try
                {
                    var diffs = ModelFingerprint.FromJson(File.ReadAllText($"{Models}/{name}_fingerprint.json")).Differences(model);
                    var kinds = diffs.GroupBy(x => x.Split(' ')[0]).Select(g => $"{g.Count()} {g.Key}");
                    var sb = new StringBuilder($"{name}: {diffs.Count} difference(s) [{string.Join(", ", kinds)}]; generated MJCF in Temp/mj_{name}.xml\n");
                    foreach (var line in diffs) sb.AppendLine(line);
                    return sb.ToString();
                }
                finally { MujocoLib.mj_deleteModel(model); }
            }
            finally
            {
                Time.fixedDeltaTime = fixedStep;
                if (made) Object.DestroyImmediate(scene.gameObject);
            }
        }

        /// <summary>Every boxer's prefab, put in the open scene's world one at a time in place of whoever is there, and checked.</summary>
        public static string CheckAll()
        {
            var present = Object.FindObjectsByType<MjBoxer>(FindObjectsSortMode.None).Select(b => b.gameObject).ToList();
            foreach (var go in present) go.SetActive(false);
            var sb = new StringBuilder();
            try
            {
                foreach (string dir in Directory.GetDirectories(BoxersDir))
                {
                    string title = Path.GetFileName(dir), name = title.ToLowerInvariant();
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{dir}/{title}.prefab");
                    if (prefab == null) continue;
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    try { sb.AppendLine(Check(name).Trim()); }
                    finally { Object.DestroyImmediate(instance); }
                }
            }
            finally { foreach (var go in present) go.SetActive(true); }
            return sb.ToString();
        }

        /// <summary>How many of Unity's own physics components the open scenes hold. The fight is MuJoCo's: the answer has to be none.</summary>
        public static string PhysXCensus()
        {
            int bodies = Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int articulations = Object.FindObjectsByType<ArticulationBody>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int colliders = Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int joints = Object.FindObjectsByType<Joint>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            return $"Rigidbody {bodies}, ArticulationBody {articulations}, Collider {colliders}, Joint {joints}";
        }

        [MenuItem("PoBox/Mj/Bring Matt Into This Scene")] static void BuildMatt() => Debug.Log(BuildBoxer("matt"));
        [MenuItem("PoBox/Mj/Check Every Boxer Against Training")] static void CheckEvery() => Debug.Log(CheckAll());
    }
}
