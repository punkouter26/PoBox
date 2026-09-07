using System.IO;
using System.Text;
using System.Xml;
using Mujoco;
using PoBox.MuJoCoCreature;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoBox.Editor
{
    /// <summary>
    /// The Nick / MuJoCo Warp line's editor entry points, mirroring what
    /// RigTool_MattDemoScene does for the Isaac Lab line so the two can be
    /// judged side by side.
    ///
    ///   BuildDemoScene  Nick alone on a floor, skinned mesh bound to the
    ///                   MuJoCo ragdoll, camera, HUD, and a driver that cycles
    ///                   BALANCE and WALK and logs NICK_DEMO lines -- the same
    ///                   columns as MATT_DEMO.
    ///   ExportNickMjcf  Writes the MJCF that MjScene ACTUALLY generates from
    ///                   the demo scene to Tools/MuJoCo/nick_unity.xml. This,
    ///                   not the authored creature.xml, is what training must
    ///                   run on: the 2026-08-31 line found that a policy
    ///                   trained on the authored file collapses in Unity.
    ///   PlayDemo / StopDemo  drive the scene from outside the Editor.
    ///
    /// All reachable through Editor_CommandBridge while the Editor is open:
    ///
    ///   echo PoBox.Editor.RigTool_NickMuJoCo.BuildDemoScene > Temp/agent-command.txt
    ///
    /// Idempotent: re-running BuildDemoScene replaces the scene wholesale, so
    /// do not hand-edit it.
    /// </summary>
    internal static class RigTool_NickMuJoCo
    {
        private const string SOURCE_SCENE_PATH = "Assets/MuJoCoCreature/Scenes/MuJoCo_TestScene.unity";
        private const string SOURCE_ROOT_NAME = "Creature";
        private const string DEMO_SCENE_PATH = "Assets/MuJoCoCreature/Scenes/Nick_DemoScene.unity";
        private const string MJCF_EXPORT_PATH = "Tools/MuJoCo/nick_unity.xml";
        private const string PANEL_SETTINGS_PATH = "Assets/UI/PS_Contest.asset";
        // Written by Tools/MuJoCo/export_onnx.py. A constant, so a stale brain
        // is visible here rather than buried in a scene file.
        private const string LOCOMOTION_BRAIN_PATH = "Assets/MuJoCoCreature/Policy/nick_locomotion.onnx";
        // Training env control rate: 4 physics steps of 0.005 s per decision.
        private const int LOCOMOTION_DECIMATION = 4;
        private const float TRAINING_TIMESTEP = 0.005f;

        [MenuItem("Tools/ML Boxing/12. Build Nick MuJoCo Demo Scene")]
        public static void BuildDemoScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject nick = CloneCreatureFromSourceScene(scene);
            if (nick == null) { return; }

            BuildEnvironment(out Transform cameraTransform);
            ConfigureController(nick, out CreatureSentisController controller);
            UIDocument hud = BuildHud();

            var demo = nick.AddComponent<Systems_NickDemo>();
            var serialized = new SerializedObject(demo);
            serialized.FindProperty("_controller").objectReferenceValue = controller;
            serialized.FindProperty("_hud").objectReferenceValue = hud;
            serialized.FindProperty("_camera").objectReferenceValue = cameraTransform;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Directory.CreateDirectory(Path.GetDirectoryName(DEMO_SCENE_PATH));
            EditorSceneManager.SaveScene(scene, DEMO_SCENE_PATH);
            Debug.Log($"RigTool: built {DEMO_SCENE_PATH}. Press Play, or run it and grep NICK_DEMO.");
        }

        /// <summary>
        /// Exports the MJCF MjScene generates for the demo scene. Runs the
        /// plugin's own exporter path (CreateScene with compile skipped) so the
        /// file is byte-for-byte what the runtime would compile, except for the
        /// timestep: MjcfGenerationContext stamps Time.fixedDeltaTime, which in
        /// the Editor is the project's 0.02 s, so it is set to the 0.005 s the
        /// controller runs the creature at for the duration of the export.
        /// </summary>
        public static void ExportNickMjcf()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("RigTool: MJCF export only works outside Play mode.");
                return;
            }
            if (!File.Exists(DEMO_SCENE_PATH))
            {
                BuildDemoScene();
            }
            EditorSceneManager.OpenScene(DEMO_SCENE_PATH, OpenSceneMode.Single);
            if (MjScene.InstanceExists)
            {
                Debug.LogError("RigTool: an MjScene already exists in the demo scene; export refused.");
                return;
            }

            float previousTimestep = Time.fixedDeltaTime;
            Time.fixedDeltaTime = TRAINING_TIMESTEP;
            XmlDocument mjcf;
            try
            {
                mjcf = MjScene.Instance.CreateScene(skipCompile: true);
            }
            finally
            {
                Time.fixedDeltaTime = previousTimestep;
                if (MjScene.InstanceExists)
                {
                    Object.DestroyImmediate(MjScene.Instance.gameObject);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(MJCF_EXPORT_PATH));
            using (var stream = File.Open(MJCF_EXPORT_PATH, FileMode.Create))
            using (var writer = new XmlTextWriter(stream, new UTF8Encoding(false)))
            {
                writer.Formatting = Formatting.Indented;
                mjcf.WriteContentTo(writer);
            }
            Debug.Log($"RigTool: exported MJCF to {MJCF_EXPORT_PATH} (timestep {TRAINING_TIMESTEP}).");
        }

        public static void PlayDemo()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.Log("RigTool: already in play mode.");
                return;
            }
            if (!File.Exists(DEMO_SCENE_PATH))
            {
                Debug.LogError($"RigTool: {DEMO_SCENE_PATH} does not exist; run BuildDemoScene first.");
                return;
            }
            EditorSceneManager.OpenScene(DEMO_SCENE_PATH, OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
            Debug.Log("RigTool: entering play mode on the Nick demo. Watch for NICK_DEMO lines.");
        }

        public static void StopDemo()
        {
            if (EditorApplication.isPlaying) { EditorApplication.ExitPlaymode(); }
            Debug.Log("RigTool: exiting play mode.");
        }

        /// <summary>Harmless entry point, so a compile can be forced through the command bridge.</summary>
        public static void Ping()
        {
            Debug.Log("RigTool_NickMuJoCo: ping.");
        }

        /// <summary>
        /// Opens the demo scene with a Systems_NickParityProbe attached (in
        /// memory only) and enters play mode. Driven by Tools/MuJoCo/parity_check.py,
        /// which writes the state first and reads the observation vector back.
        /// </summary>
        public static void RunParityProbe()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("RigTool: already in play mode; stop it first.");
                return;
            }
            if (!File.Exists(DEMO_SCENE_PATH))
            {
                BuildDemoScene();
            }
            EditorSceneManager.OpenScene(DEMO_SCENE_PATH, OpenSceneMode.Single);
            var controller = Object.FindFirstObjectByType<CreatureSentisController>();
            if (controller == null)
            {
                Debug.LogError("RigTool: no CreatureSentisController in the demo scene.");
                return;
            }
            var probe = controller.gameObject.AddComponent<Systems_NickParityProbe>();
            var serialized = new SerializedObject(probe);
            serialized.FindProperty("_controller").objectReferenceValue = controller;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            // The demo driver would reset the creature and overwrite the
            // probe's state; keep it out of this run.
            var demo = controller.GetComponentInParent<Systems_NickDemo>();
            if (demo != null) { demo.enabled = false; }
            EditorApplication.EnterPlaymode();
            Debug.Log("RigTool: parity probe entering play mode. Watch for NICK_PARITY.");
        }

        /// <summary>
        /// Nick is not a prefab: the MuJoCo importer built him straight into
        /// MuJoCo_TestScene, skinned mesh and all. Cloning that root is the
        /// one way to get the same bodies, joints, actuator gains and bone
        /// bindings the shipped balance brain was validated on.
        /// </summary>
        private static GameObject CloneCreatureFromSourceScene(Scene target)
        {
            Scene source = EditorSceneManager.OpenScene(SOURCE_SCENE_PATH, OpenSceneMode.Additive);
            GameObject creature = null;
            foreach (GameObject root in source.GetRootGameObjects())
            {
                if (root.name == SOURCE_ROOT_NAME) { creature = root; break; }
            }
            if (creature == null)
            {
                EditorSceneManager.CloseScene(source, true);
                Debug.LogError($"RigTool: no root named '{SOURCE_ROOT_NAME}' in {SOURCE_SCENE_PATH}.");
                return null;
            }

            GameObject nick = Object.Instantiate(creature);
            nick.name = "Nick";
            nick.transform.position = Vector3.zero;
            SceneManager.MoveGameObjectToScene(nick, target);
            EditorSceneManager.CloseScene(source, true);
            return nick;
        }

        private static void ConfigureController(GameObject nick, out CreatureSentisController controller)
        {
            controller = nick.GetComponentInChildren<CreatureSentisController>(true);
            if (controller == null)
            {
                Debug.LogError("RigTool: the cloned creature has no CreatureSentisController.");
                return;
            }

            var brain = AssetDatabase.LoadAssetAtPath<ModelAsset>(LOCOMOTION_BRAIN_PATH);
            var serialized = new SerializedObject(controller);
            if (brain != null)
            {
                serialized.FindProperty("_onnxModelAsset").objectReferenceValue = brain;
                serialized.FindProperty("_observeLocomotionCommand").boolValue = true;
                serialized.FindProperty("_decimation").intValue = LOCOMOTION_DECIMATION;
            }
            else
            {
                // Keep the 2026-08-31 balance brain the clone already carries,
                // with its 121-observation contract, so the scene still stands
                // up; it just cannot walk until the locomotion brain exists.
                Debug.LogWarning($"RigTool: no brain at {LOCOMOTION_BRAIN_PATH}. Run " +
                    "Tools/MuJoCo/export_onnx.py first; the scene keeps the old balance brain until then.");
                serialized.FindProperty("_observeLocomotionCommand").boolValue = false;
                serialized.FindProperty("_decimation").intValue = 1;
            }
            serialized.FindProperty("_commandedSpeed").floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void BuildEnvironment(out Transform cameraTransform)
        {
            // The MuJoCo floor plane comes with the creature (an MjGeom named
            // "floor" under the root); what is added here is only light and eye.
            var lightObject = new GameObject("Sun");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var cameraObject = new GameObject("DemoCamera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.nearClipPlane = 0.05f;
            cameraObject.transform.position = new Vector3(3.2f, 1.8f, -3.2f);
            cameraObject.transform.rotation = Quaternion.Euler(12f, -45f, 0f);
            cameraTransform = cameraObject.transform;
        }

        private static UIDocument BuildHud()
        {
            var hudObject = new GameObject("Hud");
            var document = hudObject.AddComponent<UIDocument>();
            document.panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PANEL_SETTINGS_PATH);
            if (document.panelSettings == null)
            {
                Debug.LogWarning($"RigTool: no PanelSettings at {PANEL_SETTINGS_PATH}; " +
                    "the readout will not draw, but NICK_DEMO logging still works.");
            }
            // Project rule: the opening scene shows a version stamp, top-left.
            hudObject.AddComponent<Systems_VersionStamp>();
            return document;
        }
    }
}
