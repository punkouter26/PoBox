using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using PoBox.Announcer;

namespace PoBox.EditorTools
{
    /// <summary>
    /// Builds the ring announcer (the owner's teeth, <see cref="TeethAnnouncer"/>) as a prefab, and hangs it
    /// over the ring whenever the arena is built. The arena's builder is not changed for it: this subscribes
    /// to <see cref="PoBoxBuilder.BuildingArena"/>.
    ///
    /// The model is Assets/Announcer/Teeth.glb, made from the two scans by training/tools/teeth_to_glb.py:
    /// two meshes, TeethUpper and TeethLower, in metres at life size, both with their origin on the jaw's
    /// hinge. The voice is the recordings in Assets/Announcer/Voice, made by Assets/Announcer/make_voice.ps1.
    /// To move or resize it, edit the prefab (or the object in the scene); to pick up a new model or new
    /// recordings, menu PoBox/Announcer/Rebuild Prefab.
    /// </summary>
    [InitializeOnLoad]
    public static class AnnouncerBuilder
    {
        const string Dir = "Assets/Announcer";
        const string ModelPath = Dir + "/Teeth.glb";
        const string PrefabPath = Dir + "/TeethAnnouncer.prefab";
        const string CablePath = Dir + "/Cable.mat";
        const string VoiceDir = Dir + "/Voice";
        /// <summary>Times life size: a jaw 7 cm across is nothing from the back of a hall.</summary>
        const float Size = 12f;

        static AnnouncerBuilder()
        {
            PoBoxBuilder.BuildingArena -= Place;
            PoBoxBuilder.BuildingArena += Place;
        }

        static void Place(Transform environment)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null) prefab = BuildPrefab();
            if (prefab == null) return;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, environment);
            // Over the middle of the canvas, which is the origin.
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
        }

        [MenuItem("PoBox/Announcer/Rebuild Prefab", priority = 60)]
        public static GameObject BuildPrefab()
        {
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (model == null)
            {
                Debug.LogError($"[PoBox] {ModelPath} is missing or did not import; no announcer. Make it with training/tools/teeth_to_glb.py.");
                return null;
            }

            var root = new GameObject("TeethAnnouncer");
            var announcer = root.AddComponent<TeethAnnouncer>();
            var voice = root.AddComponent<AudioSource>();
            voice.playOnAwake = false;
            voice.spatialBlend = 0f;        // the hall's loudspeakers, not a point in the room
            voice.volume = 0.9f;
            voice.priority = 32;

            GameObject cable = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cable.name = "Cable";
            Object.DestroyImmediate(cable.GetComponent<Collider>());
            cable.transform.SetParent(root.transform, false);
            cable.transform.localScale = new Vector3(0.025f, 1f, 0.025f);
            cable.GetComponent<Renderer>().sharedMaterial = CableMaterial();

            var mount = new GameObject("Mount");
            mount.transform.SetParent(root.transform, false);
            mount.transform.localPosition = new Vector3(0f, announcer.restHeight, 0f);

            var mouth = new GameObject("Mouth");
            mouth.transform.SetParent(mount.transform, false);
            mouth.transform.localScale = Vector3.one * Size;
            var teeth = (GameObject)PrefabUtility.InstantiatePrefab(model, mouth.transform);
            teeth.name = "Teeth";

            // The model's origin is the hinge, behind the teeth. The mount should hold the mouth by its middle.
            Renderer[] renderers = teeth.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                Bounds b = renderers[0].bounds;
                foreach (Renderer r in renderers) b.Encapsulate(r.bounds);
                mouth.transform.position -= b.center - mount.transform.position;
            }

            announcer.mount = mount.transform;
            announcer.cable = cable.transform;
            announcer.voice = voice;
            announcer.upperJaw = FindDeep(teeth.transform, "TeethUpper");
            announcer.lowerJaw = FindDeep(teeth.transform, "TeethLower");
            if (announcer.upperJaw == null || announcer.lowerJaw == null)
                Debug.LogWarning("[PoBox] Teeth.glb has no TeethUpper or TeethLower: the announcer's jaw will not move.");

            if (AssetDatabase.IsValidFolder(VoiceDir))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { VoiceDir }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    announcer.keys.Add(Path.GetFileNameWithoutExtension(path));
                    announcer.clips.Add(AssetDatabase.LoadAssetAtPath<AudioClip>(path));
                }
            }

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            Debug.Log($"[PoBox] built {PrefabPath}: {renderers.Length} meshes, {announcer.keys.Count} recordings.");
            return prefab;
        }

        /// <summary>Hangs the announcer in the scene that is open, without building the arena again.</summary>
        [MenuItem("PoBox/Announcer/Add To Open Scene", priority = 61)]
        public static void AddToOpenScene()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("[PoBox] Stop Play first.");
                return;
            }
            foreach (GameObject top in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                foreach (TeethAnnouncer old in top.GetComponentsInChildren<TeethAnnouncer>(true))
                    Object.DestroyImmediate(old.gameObject);
            GameObject environment = GameObject.Find("Environment");
            Place(environment != null ? environment.transform : null);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        static Material CableMaterial()
        {
            Material m = AssetDatabase.LoadAssetAtPath<Material>(CablePath);
            if (m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(0.05f, 0.05f, 0.06f) };
            AssetDatabase.CreateAsset(m, CablePath);
            return m;
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform child in t)
            {
                Transform hit = FindDeep(child, name);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
