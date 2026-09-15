using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Mujoco;
using PoBox.MuJoCoCreature;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoBox.Editor
{
    /// <summary>
    /// The pipeline that turns a supplied skinned mesh into a MuJoCo
    /// contestant, for every creature after Nick. Nick's own tool
    /// (<see cref="RigTool_NickMuJoCo"/>) clones a hand-built body; this one
    /// builds the body from an MJCF that
    /// <c>Tools/MuJoCo/make_creature_mjcf.py</c> derived from the GLB's bind
    /// pose, so proportions come from the mesh and nothing is hand-guessed.
    ///
    /// One creature, four steps, each a parameterless entry point so the
    /// command bridge can drive it:
    ///
    ///   Import        MJCF -> MjBody hierarchy (the plugin's importer), the
    ///                 controller wired to pelvis/feet/floor, the contestant,
    ///                 the skinned mesh bound to the bodies. Saved as
    ///                 Assets/MuJoCoCreature/Scenes/&lt;Name&gt;_Source.unity.
    ///   ExportMjcf    the MJCF MjScene ACTUALLY generates from that scene, to
    ///                 Tools/MuJoCo/&lt;name&gt;_unity.xml -- what training must
    ///                 use, never the authored file (CLAUDE.md).
    ///   ApplyLimits   the torque/speed budgets prepare_human_limits.py wrote
    ///                 for that export, copied onto the MjActuators so the
    ///                 game body is the training body.
    ///   AddToContests clones the finished creature into both contest scenes
    ///                 with its trained brain. Refuses without a brain: a
    ///                 contestant that can never bind stalls the referee.
    ///
    ///   echo PoBox.Editor.RigTool_CreatureMuJoCo.ImportGrandma > Temp/agent-command.txt
    /// </summary>
    public static class RigTool_CreatureMuJoCo
    {
        private const string SCENES_DIR = "Assets/MuJoCoCreature/Scenes";
        private const string BALANCE_SCENE_PATH = "Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity";
        private const string WALK_SCENE_PATH = "Assets/Scenes/SCN_TEST_WALK_CONTEST.unity";
        private const float TRAINING_TIMESTEP = 0.005f;

        private sealed class Creature
        {
            public string name;            // display name and scene root name
            public string key;             // lower-case file stem
            public string glbPath;
            public Color plate;
            public Vector3 balanceSlot;    // a free mark in the ring (Nick holds 0.75, 1, -0.7)
            public Vector3 walkSlot;       // a free lane on the start line (Nick holds x = 2.75)
            public string AuthoredMjcf => $"Tools/MuJoCo/{key}.xml";
            public string ExportedMjcf => $"Tools/MuJoCo/{key}_unity.xml";
            public string LimitsJson => $"Tools/MuJoCo/{key}_human.limits.json";
            public string SourceScene => $"{SCENES_DIR}/{name}_Source.unity";
            public string BrainPath => $"Assets/Agents/{name}_Balance001/{key}_balance_001.onnx";
        }

        private static readonly Creature Grandma = new Creature
        {
            name = "Grandma", key = "grandma", glbPath = "Assets/Art/2026_GrandmaRigged.glb",
            plate = new Color(0.95f, 0.55f, 0.75f, 1f),
            balanceSlot = new Vector3(-0.75f, Systems_ContestSpawner.RING_FLOOR_Y, -0.7f),
            walkSlot = new Vector3(1.65f, 0.03f, -2.8f),
        };

        private static readonly Creature Grandpa = new Creature
        {
            name = "Grandpa", key = "grandpa", glbPath = "Assets/Art/2026_GrandpaRigged.glb",
            plate = new Color(0.55f, 0.75f, 0.95f, 1f),
            balanceSlot = new Vector3(0.75f, Systems_ContestSpawner.RING_FLOOR_Y, 0.7f),
            walkSlot = new Vector3(0.55f, 0.03f, -2.8f),
        };

        [MenuItem("PoBox/Creatures/Grandma/1 Import Body")] public static void ImportGrandma() => Import(Grandma);
        [MenuItem("PoBox/Creatures/Grandma/2 Export MJCF")] public static void ExportGrandmaMjcf() => ExportMjcf(Grandma);
        [MenuItem("PoBox/Creatures/Grandma/3 Apply Human Limits")] public static void ApplyGrandmaLimits() => ApplyLimits(Grandma);
        [MenuItem("PoBox/Creatures/Grandma/4 Add To Contests")] public static void AddGrandmaToContests() => AddToContests(Grandma);

        [MenuItem("PoBox/Creatures/Grandpa/1 Import Body")] public static void ImportGrandpa() => Import(Grandpa);
        [MenuItem("PoBox/Creatures/Grandpa/2 Export MJCF")] public static void ExportGrandpaMjcf() => ExportMjcf(Grandpa);
        [MenuItem("PoBox/Creatures/Grandpa/3 Apply Human Limits")] public static void ApplyGrandpaLimits() => ApplyLimits(Grandpa);
        [MenuItem("PoBox/Creatures/Grandpa/4 Add To Contests")] public static void AddGrandpaToContests() => AddToContests(Grandpa);

        // ------------------------------------------------------------ 1 import
        private static void Import(Creature creature)
        {
            if (EditorApplication.isPlaying) { Debug.LogError("RigTool: leave play mode first."); return; }
            if (!File.Exists(creature.AuthoredMjcf))
            {
                Debug.LogError($"RigTool: {creature.AuthoredMjcf} missing — run Tools/MuJoCo/make_creature_mjcf.py first.");
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var importer = new MjImporterWithAssets();
            GameObject root = importer.ImportFile(Path.GetFullPath(creature.AuthoredMjcf));
            if (root == null) { Debug.LogError("RigTool: the MuJoCo importer returned nothing."); return; }
            root.name = creature.name;

            MjBody pelvis = FindBody(root, "Pelvis");
            MjBody footL = FindBody(root, "FootL");
            MjBody footR = FindBody(root, "FootR");
            MjGeom floor = FindGeom(root, "floor");
            if (pelvis == null || footL == null || footR == null || floor == null)
            {
                Debug.LogError("RigTool: imported body lacks Pelvis/FootL/FootR/floor; check the MJCF.");
                return;
            }

            var controller = root.AddComponent<CreatureSentisController>();
            var so = new SerializedObject(controller);
            so.FindProperty("_pelvis").objectReferenceValue = pelvis;
            so.FindProperty("_footLeft").objectReferenceValue = footL;
            so.FindProperty("_footRight").objectReferenceValue = footR;
            so.FindProperty("_groundReference").objectReferenceValue = floor.transform;
            so.FindProperty("_fallHeight").floatValue = 0.3f;
            so.FindProperty("_fixedTimestepOverride").floatValue = TRAINING_TIMESTEP;
            so.FindProperty("_observeLocomotionCommand").boolValue = true;
            so.FindProperty("_decimation").intValue = 1;
            so.FindProperty("_commandedSpeed").floatValue = 0f;
            so.ApplyModifiedPropertiesWithoutUndo();

            var contestant = root.AddComponent<Systems_NickContestant>();
            var cso = new SerializedObject(contestant);
            cso.FindProperty("_controller").objectReferenceValue = controller;
            cso.FindProperty("_displayName").stringValue = creature.name;
            cso.FindProperty("_plateColor").colorValue = creature.plate;
            cso.ApplyModifiedPropertiesWithoutUndo();

            int positional = PositionActuators(root);
            int links = AttachSkin(root, creature);

            var lightObject = new GameObject("Sun");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var cameraObject = new GameObject("Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(3.0f, 1.6f, -3.0f);
            cameraObject.transform.LookAt(new Vector3(0f, 0.9f, 0f));

            Directory.CreateDirectory(SCENES_DIR);
            EditorSceneManager.SaveScene(scene, creature.SourceScene);
            Debug.Log($"RigTool: imported {creature.name} from {creature.AuthoredMjcf} — " +
                      $"{root.GetComponentsInChildren<MjBody>(true).Length} bodies, " +
                      $"{root.GetComponentsInChildren<MjActuator>(true).Length} actuators ({positional} made positional), {links} bone links. " +
                      $"Saved {creature.SourceScene}.");
        }

        /// <summary>
        /// Binds the GLB's skinned mesh to the physics bodies, as Nick's tool
        /// does: physics runs on the capsules, the mesh follows them, the
        /// capsule renderers stop drawing. Returns the bone links made — a
        /// silent 0 would mean the mesh hangs in its bind pose while the
        /// capsules move underneath it.
        /// </summary>
        private static int AttachSkin(GameObject root, Creature creature)
        {
            var glb = AssetDatabase.LoadAssetAtPath<GameObject>(creature.glbPath);
            if (glb == null)
            {
                Debug.LogError($"RigTool: no rigged mesh at {creature.glbPath}; {creature.name} keeps the capsules.");
                return 0;
            }
            var skin = (GameObject)PrefabUtility.InstantiatePrefab(glb, root.transform);
            skin.name = creature.name + "Skin";
            skin.transform.localPosition = Vector3.zero;
            skin.transform.localRotation = Quaternion.identity;
            if (skin.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0)
            {
                Debug.LogError($"RigTool: {creature.glbPath} has no SkinnedMeshRenderer.");
                UnityEngine.Object.DestroyImmediate(skin);
                return 0;
            }
            var binder = skin.AddComponent<SkinnedRigBinder>();
            int links = binder.Rebind(skin.transform);
            if (links == 0)
            {
                Debug.LogError("RigTool: SkinnedRigBinder matched NO bones to bodies; capsules kept.");
                UnityEngine.Object.DestroyImmediate(skin);
                return 0;
            }
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.name.StartsWith("g_")) { renderer.enabled = false; }
            }
            return links;
        }

        // ------------------------------------------------------------ 2 export
        private static void ExportMjcf(Creature creature)
        {
            if (EditorApplication.isPlaying) { Debug.LogError("RigTool: MJCF export only works outside Play mode."); return; }
            if (!File.Exists(creature.SourceScene)) { Debug.LogError($"RigTool: {creature.SourceScene} missing — import first."); return; }
            Scene sourceScene = EditorSceneManager.OpenScene(creature.SourceScene, OpenSceneMode.Single);
            if (MjScene.InstanceExists)
            {
                Debug.LogError("RigTool: an MjScene already exists in the source scene; export refused.");
                return;
            }
            GameObject sourceRoot = GameObject.Find(creature.name);
            if (sourceRoot != null && PositionActuators(sourceRoot) > 0)
            {
                EditorSceneManager.MarkSceneDirty(sourceScene);
                EditorSceneManager.SaveScene(sourceScene);
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
                if (MjScene.InstanceExists) { UnityEngine.Object.DestroyImmediate(MjScene.Instance.gameObject); }
            }
            using (var stream = File.Open(creature.ExportedMjcf, FileMode.Create))
            using (var writer = new XmlTextWriter(stream, new UTF8Encoding(false)))
            {
                writer.Formatting = Formatting.Indented;
                mjcf.WriteContentTo(writer);
            }
            Debug.Log($"RigTool: exported {creature.ExportedMjcf} (timestep {TRAINING_TIMESTEP}).");
        }

        // ------------------------------------------------------------ 3 limits
        [Serializable] private class LimitsFile { public LimitEntry[] actuators; }
        [Serializable] private class LimitEntry { public string name; public float[] force_range_nm; public float kp; public float kv; }

        /// <summary>
        /// Copies prepare_human_limits.py's budgets onto the scene's
        /// MjActuators, matched on the actuator name stem (the plugin suffixes
        /// exported names: a_ThighL_pitch_68). After this a fresh ExportMjcf
        /// must reproduce &lt;name&gt;_human.xml's actuator arrays.
        /// </summary>
        private static void ApplyLimits(Creature creature)
        {
            if (!File.Exists(creature.LimitsJson))
            {
                Debug.LogError($"RigTool: {creature.LimitsJson} missing — run prepare_human_limits.py on {creature.ExportedMjcf} first.");
                return;
            }
            var limits = JsonUtility.FromJson<LimitsFile>(File.ReadAllText(creature.LimitsJson));
            Scene scene = EditorSceneManager.OpenScene(creature.SourceScene, OpenSceneMode.Single);
            GameObject root = GameObject.Find(creature.name);
            if (root == null) { Debug.LogError($"RigTool: no root named {creature.name} in {creature.SourceScene}."); return; }

            int applied = 0;
            foreach (MjActuator actuator in root.GetComponentsInChildren<MjActuator>(true))
            {
                string stem = Regex.Replace(actuator.name, @"_\d+$", "");
                LimitEntry entry = Array.Find(limits.actuators, e => e.name == stem);
                if (entry == null) { Debug.LogWarning($"RigTool: no limit for actuator {actuator.name}"); continue; }
                actuator.CommonParams.ForceLimited = true;
                actuator.CommonParams.ForceRange = new Vector2(entry.force_range_nm[0], entry.force_range_nm[1]);
                actuator.CustomParams.Kp = entry.kp;
                actuator.CustomParams.Kvp = entry.kv;
                EditorUtility.SetDirty(actuator);
                applied++;
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"RigTool: applied {applied} actuator limits to {creature.name} from {creature.LimitsJson}.");
        }

        // ------------------------------------------------------------ 4 contests
        private static void AddToContests(Creature creature)
        {
            if (EditorApplication.isPlaying) { Debug.LogError("RigTool: leave play mode first."); return; }
            var brain = AssetDatabase.LoadAssetAtPath<ModelAsset>(creature.BrainPath);
            if (brain == null)
            {
                Debug.LogError($"RigTool: no brain at {creature.BrainPath}; {creature.name} is not added. " +
                               "A contestant that cannot bind stalls the referee for every other contestant.");
                return;
            }
            AddToContest(creature, brain, BALANCE_SCENE_PATH, creature.balanceSlot, Quaternion.identity);
            AddToContest(creature, brain, WALK_SCENE_PATH, creature.walkSlot, Quaternion.Euler(0f, 180f, 0f));
        }

        private static void AddToContest(Creature creature, ModelAsset brain, string scenePath, Vector3 position, Quaternion rotation)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            foreach (GameObject existing in scene.GetRootGameObjects())
            {
                if (existing.name == creature.name) { UnityEngine.Object.DestroyImmediate(existing); }
            }
            GameObject clone = CloneFromSourceScene(creature, scene);
            if (clone == null) { return; }

            var controller = clone.GetComponentInChildren<CreatureSentisController>(true);
            var so = new SerializedObject(controller);
            so.FindProperty("_onnxModelAsset").objectReferenceValue = brain;
            so.FindProperty("_observeLocomotionCommand").boolValue = true;
            so.FindProperty("_decimation").intValue = 1;
            // The ring keeps its own 0.02 s; the brain was trained at that step.
            so.FindProperty("_fixedTimestepOverride").floatValue = 0f;
            // No auto-reset in a contest: a fallen body stays fallen and is
            // judged by head height like everyone else (see Nick's tool).
            so.FindProperty("_fallHeight").floatValue = -1f;
            so.ApplyModifiedPropertiesWithoutUndo();

            clone.transform.SetPositionAndRotation(position, rotation);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"RigTool: {creature.name} added to {Path.GetFileName(scenePath)} at {position} with {creature.BrainPath}.");
        }

        private static GameObject CloneFromSourceScene(Creature creature, Scene target)
        {
            if (!File.Exists(creature.SourceScene)) { Debug.LogError($"RigTool: {creature.SourceScene} missing."); return null; }
            Scene source = EditorSceneManager.OpenScene(creature.SourceScene, OpenSceneMode.Additive);
            GameObject found = null;
            foreach (GameObject root in source.GetRootGameObjects())
            {
                if (root.name == creature.name) { found = root; break; }
            }
            if (found == null)
            {
                EditorSceneManager.CloseScene(source, true);
                Debug.LogError($"RigTool: no root named {creature.name} in {creature.SourceScene}.");
                return null;
            }
            GameObject clone = UnityEngine.Object.Instantiate(found);
            clone.name = creature.name;
            SceneManager.MoveGameObjectToScene(clone, target);
            EditorSceneManager.CloseScene(source, true);
            return clone;
        }

        /// <summary>
        /// The plugin's importer round-trips the MJCF through MuJoCo's own
        /// saver, which writes every actuator in its general form (gainprm
        /// [kp 0 0], biasprm [0 -kp -kv]) -- so the position servos arrive as
        /// General actuators, and a later export would carry no kp/kv for
        /// prepare_human_limits.py to read. Nick's are Position actuators;
        /// these are turned back into the same, with the kp and the kv MuJoCo
        /// resolved from dampratio. Returns how many were converted.
        /// </summary>
        private static int PositionActuators(GameObject root)
        {
            int converted = 0;
            foreach (MjActuator actuator in root.GetComponentsInChildren<MjActuator>(true))
            {
                if (actuator.Type != MjActuator.ActuatorType.General) { continue; }
                var gain = actuator.CustomParams.GainPrm;
                var bias = actuator.CustomParams.BiasPrm;
                if (gain == null || gain.Count < 1 || bias == null || bias.Count < 3) { continue; }
                actuator.Type = MjActuator.ActuatorType.Position;
                actuator.CustomParams.Kp = gain[0];
                actuator.CustomParams.Kvp = -bias[2];
                EditorUtility.SetDirty(actuator);
                converted++;
            }
            return converted;
        }

        // ------------------------------------------------------------ helpers
        private static MjBody FindBody(GameObject root, string name)
        {
            foreach (MjBody body in root.GetComponentsInChildren<MjBody>(true))
            {
                if (body.name == name) { return body; }
            }
            return null;
        }

        private static MjGeom FindGeom(GameObject root, string name)
        {
            foreach (MjGeom geom in root.GetComponentsInChildren<MjGeom>(true))
            {
                if (geom.name == name) { return geom; }
            }
            return null;
        }
    }
}
