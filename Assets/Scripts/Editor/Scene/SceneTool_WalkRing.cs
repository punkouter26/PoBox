using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoBox.Editor
{
    /// <summary>
    /// Gives the WALK contest the same boxing ring the BALANCE contest already
    /// fights in.
    ///
    /// WHY THIS EXISTS. Both contests are boxing mini-games, but only
    /// SCN_TEST_BALANCE_CONTEST instantiated the ring art -- it references
    /// Assets/Art/BoxingRing.glb and Assets/Art/Arena.glb, and the walk scene
    /// referenced neither. That left the walk race standing on a bare floor in
    /// front of a gradient sky, which reads as a test rig rather than as a
    /// fight. The ring is the game's identity; both mini-games should be in it.
    ///
    /// WHAT IT DOES. Copies the ring and arena instances out of the balance
    /// scene into the walk scene at the SAME transform, rather than re-deriving
    /// a placement -- the two contests are then guaranteed to agree about where
    /// the ring is, and a later tweak to one is visible as a diff against the
    /// other. The copies are ordinary prefab instances: selectable, movable and
    /// removable in the Inspector, which is the point (AGENTS.md: author scene
    /// objects so the user can drag them, not so code owns them).
    ///
    /// Report() changes nothing. Run it first; it prints the ring's world
    /// bounds and the canvas height, which is what decides whether the walk
    /// course can sit on the canvas or has to be re-seated.
    /// </summary>
    public static class SceneTool_WalkRing
    {
        private const string BALANCE_SCENE = "Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity";
        private const string WALK_SCENE = "Assets/Scenes/SCN_TEST_WALK_CONTEST.unity";

        /// <summary>How many of a prefab instance's children the report prints.</summary>
        private const int CHILD_SAMPLE = 8;

        /// <summary>The walk scene's big flat floor plane, replaced by the arena.</summary>
        private const string GROUND_NAME = "Ground";

        /// <summary>
        /// Ring height for the walk scene: 1 m below the balance scene's 0, so
        /// the ring's mat (authored at local y = 1.0, sitting on that scene's
        /// 1 m platform) lands at the walk floor.
        ///
        /// IT MUST BE EXACTLY THE FLOOR. The first attempt used -0.97 to dodge a
        /// coplanar-surface flicker against this scene's ground plane, which put
        /// the canvas 3 cm ABOVE the surface the feet actually rest on -- the
        /// canvas swallowed the feet, which is exactly how it looked. The
        /// flicker is instead solved by not drawing the ground plane under the
        /// ring (see <see cref="HideGroundPlane"/>), which changes nothing the
        /// physics can see.
        /// </summary>
        private const float RING_Y = -1f;

        private static readonly string[] RING_ASSETS =
        {
            "Assets/Art/BoxingRing.glb",
            "Assets/Art/Arena.glb",
        };

        /// <summary>
        /// Places the ring and the arena in the walk scene so the race is run in
        /// a ring instead of on a bare floor. Idempotent: an existing
        /// BoxingRing/Arena root is refreshed in place rather than duplicated, so
        /// running it twice is safe.
        ///
        /// THE HEIGHTS ARE THE WHOLE POINT. In the balance scene the canvas is
        /// the top of a 6.1 m platform whose surface is 1 m up, and the ring art
        /// is authored against that: its mat sits at local y = 1.0 and its ropes
        /// 1.545 m above the mat. The walk scene's floor is at grade, and its
        /// floor height is NOT free to move -- GroundY comes from the
        /// controller's ground reference and is subtracted into the observation
        /// vector the policy was trained on, so raising it would change what the
        /// brain sees. So the ring is dropped by 1 m instead: its mat lands on
        /// the walk floor, the ropes end up 1.545 m above the canvas, and
        /// not one physics value moves.
        ///
        /// The arena is nudged 2 cm up from the balance scene's value. Its slab
        /// is otherwise coplanar with this scene's 40 x 40 ground plane, and two
        /// coplanar surfaces flicker.
        /// </summary>
        [MenuItem("PoBox/Scene/Add Ring To Walk Contest")]
        public static void AddRingToWalkScene()
        {
            var sb = new StringBuilder();
            Scene walk = EditorSceneManager.OpenScene(WALK_SCENE, OpenSceneMode.Single);

            Place(sb, walk, RING_ASSETS[0], "BoxingRing", new Vector3(0f, RING_Y, 0f));
            Place(sb, walk, RING_ASSETS[1], "Arena", new Vector3(0f, 0.01f, 0f));
            HideGroundPlane(sb, walk);

            EditorSceneManager.MarkSceneDirty(walk);
            EditorSceneManager.SaveScene(walk);
            AssetDatabase.SaveAssets();

            sb.AppendLine($"saved {WALK_SCENE}");
            Describe(sb, WALK_SCENE, ringDetail: true);
            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// Stops the 40 x 40 ground plane DRAWING under the ring. Its surface is
        /// flush with the ring canvas, and two coplanar surfaces flicker.
        ///
        /// ONLY the renderer is disabled -- the object keeps its transform,
        /// because a contestant's GroundY is read from a reference transform's
        /// y and that value is subtracted into the policy's observations. Moving
        /// or deleting this object could change what the brain sees; not drawing
        /// it cannot.
        /// </summary>
        private static void HideGroundPlane(StringBuilder sb, Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name != GROUND_NAME) { continue; }
                var renderer = root.GetComponent<Renderer>();
                if (renderer == null) { sb.AppendLine($"[{GROUND_NAME}] has no renderer to hide"); return; }
                renderer.enabled = false;
                sb.AppendLine($"hid [{GROUND_NAME}] renderer (kept the object: GroundY reads its transform)");
                return;
            }
            sb.AppendLine($"no [{GROUND_NAME}] root found to hide");
        }

        private static void Place(StringBuilder sb, Scene scene, string assetPath, string name, Vector3 position)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name != name) { continue; }
                // Refresh rather than duplicate: the transform is the thing being
                // managed here, and a second copy would be invisible until
                // somebody noticed the doubled draw calls.
                root.transform.position = position;
                root.transform.rotation = Quaternion.identity;
                root.transform.localScale = Vector3.one;
                sb.AppendLine($"refreshed [{name}] at {V(position)}");
                return;
            }

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
            {
                sb.AppendLine($"MISSING ASSET {assetPath} -- [{name}] not placed");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
            instance.name = name;
            instance.transform.position = position;
            instance.transform.rotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            sb.AppendLine($"added [{name}] from {assetPath} at {V(position)}");
        }

        /// <summary>Read-only. Prints every root object in both scenes with the
        /// ring/arena instances' world bounds. Nothing is written.</summary>
        [MenuItem("PoBox/Scene/Report Ring Geometry")]
        public static void Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("RING GEOMETRY REPORT");
            Describe(sb, BALANCE_SCENE, ringDetail: true);
            Describe(sb, WALK_SCENE, ringDetail: false);
            Debug.Log(sb.ToString());
        }

        private static void Describe(StringBuilder sb, string scenePath, bool ringDetail)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            sb.AppendLine();
            sb.AppendLine($"== {scene.name} ({scenePath})");
            sb.AppendLine($"   roots: {scene.rootCount}");

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                string prefab = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
                bool isRing = IsRingAsset(prefab);
                var renderer = root.GetComponentInChildren<Renderer>();
                string bounds = renderer == null ? "no-renderer" : $"boundsY {renderer.bounds.min.y:F3}..{renderer.bounds.max.y:F3}";

                sb.AppendLine($"   [{root.name}] prefab={OrNone(prefab)} pos={V(root.transform.position)} " +
                              $"rot={V(root.transform.eulerAngles)} scale={V(root.transform.localScale)} " +
                              $"children={root.transform.childCount} {bounds}");

                if (!ringDetail || !isRing) { continue; }

                Bounds b = default;
                bool first = true;
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
                {
                    if (first) { b = r.bounds; first = false; }
                    else { b.Encapsulate(r.bounds); }
                }
                if (!first)
                {
                    sb.AppendLine($"       -> combined bounds min={V(b.min)} max={V(b.max)} size={V(b.size)}");
                }
                // The arena carries ~400 children and printing them all buries
                // the rest of the report in the Editor log.
                int shown = 0;
                foreach (Transform child in root.transform)
                {
                    if (shown++ >= CHILD_SAMPLE) { sb.AppendLine($"       ... {root.transform.childCount - CHILD_SAMPLE} more children"); break; }
                    sb.AppendLine($"       child {child.name} local={V(child.localPosition)} " +
                                  $"scale={V(child.localScale)}");
                }
            }
        }

        private static bool IsRingAsset(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath)) { return false; }
            foreach (string asset in RING_ASSETS)
            {
                if (prefabPath.Replace('\\', '/') == asset) { return true; }
            }
            return false;
        }

        private static string OrNone(string s) => string.IsNullOrEmpty(s) ? "-" : s.Replace('\\', '/');

        private static string V(Vector3 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
    }
}
