using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mujoco;
using PoBox.Fx;
using PoBox.Mj;
using PoBox.Rl;
using PoBox.Sim;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;
using BodyPart = PoBox.Sim.BodyPart;   // UnityEditor has one too (for avatars)

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

        /// <summary>The boxers there are: a folder of Assets/Boxers with a prefab in it, by name.</summary>
        public static string[] Boxers()
        {
            if (!AssetDatabase.IsValidFolder(BoxersDir)) return new string[0];
            return AssetDatabase.GetSubFolders(BoxersDir)
                .Select(Path.GetFileName)
                .Where(t => File.Exists($"{BoxersDir}/{t}/{t}.prefab"))
                .Select(t => t.ToLowerInvariant())
                .OrderBy(n => n, System.StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>The name on the scoreboard: the rig file's "display" ("LIL MATT"), otherwise the name in capitals.</summary>
        public static string DisplayName(string name)
        {
            string rig = $"{Rigs}/{name}.json";
            if (File.Exists(rig))
            {
                Match m = Regex.Match(File.ReadAllText(rig), "\"display\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            }
            return name.ToUpperInvariant();
        }

        /// <summary>A boxer's card on the first screen: name, size, and the portrait taken with the dev command 'portraits'.</summary>
        public static PoBox.UI.MenuView.Boxer Card(string name)
        {
            var cfg = JsonUtility.FromJson<Sizes>(File.ReadAllText($"{BoxersDir}/{Title(name)}/config.json"));
            string portrait = $"{AssetBakery.PortraitDir}/{name}.png";
            if (File.Exists(portrait))
            {
                AssetDatabase.ImportAsset(portrait);
                var importer = AssetImporter.GetAtPath(portrait) as TextureImporter;
                if (importer != null && (importer.npotScale != TextureImporterNPOTScale.None || importer.mipmapEnabled || importer.wrapMode != TextureWrapMode.Clamp))
                {
                    importer.npotScale = TextureImporterNPOTScale.None;
                    importer.mipmapEnabled = false;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.SaveAndReimport();
                }
            }
            return new PoBox.UI.MenuView.Boxer
            {
                name = name,
                displayName = DisplayName(name),
                detail = cfg.height_m > 0f && cfg.total_mass_kg > 0f ? $"{cfg.height_m:0.00} m · {cfg.total_mass_kg:0} kg" : "",
                portrait = AssetDatabase.LoadAssetAtPath<Texture2D>(portrait),
            };
        }

        [System.Serializable] class Sizes { public float height_m, total_mass_kg; }

        /// <summary>The owner's mesh for a boxer, Assets/Models/NAME.glb (or .fbx).</summary>
        static GameObject Mesh(string name)
        {
            foreach (string ext in new[] { ".glb", ".fbx" })
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Models/{Title(name)}{ext}");
                if (go != null) return go;
            }
            return null;
        }

        /// <summary>Which bone of the mesh is which limb: the rig file's "map".</summary>
        static Dictionary<string, string> BoneMap(string rigJson)
        {
            var map = new Dictionary<string, string>();
            Match block = Regex.Match(rigJson, "\"map\"\\s*:\\s*\\{([^}]*)\\}");
            if (block.Success)
                foreach (Match m in Regex.Matches(block.Groups[1].Value, "\"(\\w+)\"\\s*:\\s*\"([^\"]+)\""))
                    map[m.Groups[1].Value] = m.Groups[2].Value;
            return map;
        }

        /// <summary>Which bone rides on which body, and which limbs are straightened onto the body's before binding (parents before children).</summary>
        static void ConfigureBinder(SkinBinder binder, Dictionary<string, string> bones)
        {
            void Map(string body, string key)
            {
                if (bones.TryGetValue(key, out string bone)) binder.map.Add(new SkinBinder.BoneMap { body = body, bone = bone });
            }
            void Aim(string key, string towardKey, string body, string towardBody)
            {
                if (bones.TryGetValue(key, out string bone) && bones.TryGetValue(towardKey, out string toward))
                    binder.aims.Add(new SkinBinder.Aim { bone = bone, towardBone = toward, body = body, towardBody = towardBody });
            }
            Map("pelvis", "pelvis");
            Map("torso", "spine");
            foreach (string s in new[] { "l", "r" })
            {
                Map($"upper_arm_{s}", $"upper_arm_{s}");
                Map($"forearm_{s}", $"forearm_{s}");
                Aim($"upper_arm_{s}", $"forearm_{s}", $"upper_arm_{s}", $"forearm_{s}");
                Aim($"forearm_{s}", $"hand_{s}", $"forearm_{s}", $"glove_{s}");
            }
            foreach (string s in new[] { "l", "r" })
            {
                Map($"thigh_{s}", $"thigh_{s}");
                Map($"shin_{s}", $"shin_{s}");
                Map($"foot_{s}", $"foot_{s}");
                Aim($"thigh_{s}", $"shin_{s}", $"thigh_{s}", $"shin_{s}");
                Aim($"shin_{s}", $"foot_{s}", $"shin_{s}", $"foot_{s}");
            }
        }

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
            GameObject mesh = Mesh(name);
            if (mesh != null)
            {
                var skin = (GameObject)PrefabUtility.InstantiatePrefab(mesh);
                skin.name = title + " Mesh";
                skin.transform.SetParent(boxer.transform, false);
                var binder = boxer.AddComponent<SkinBinder>();
                binder.rigRoot = boxer.transform;
                binder.skin = skin;
                ConfigureBinder(binder, BoneMap(File.ReadAllText($"{Rigs}/{name}.json")));
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

        static readonly string[] BodyOrder =
        {
            "pelvis", "torso", "upper_arm_l", "forearm_l", "upper_arm_r", "forearm_r",
            "thigh_l", "shin_l", "foot_l", "thigh_r", "shin_r", "foot_r",
        };
        static readonly Color[] CornerColour = { new Color(0.89f, 0.24f, 0.24f), new Color(0.20f, 0.49f, 0.94f) };

        static PartKind KindOf(string body)
        {
            if (body == "pelvis") return PartKind.Pelvis;
            if (body == "torso") return PartKind.Torso;
            if (body.StartsWith("upper_arm")) return PartKind.UpperArm;
            if (body.StartsWith("forearm")) return PartKind.Forearm;
            if (body.StartsWith("thigh")) return PartKind.Thigh;
            if (body.StartsWith("shin")) return PartKind.Shin;
            return PartKind.Foot;
        }

        static Transform FindDeep(Transform t, string name) =>
            t.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == name);

        /// <summary>
        /// The boxer dressed for each corner: Assets/Boxers/NAME/NAME Red.prefab and NAME Blue.prefab, variants of
        /// its prefab with the game's parts on it (Fighter, MjBrain, FighterSkin, a BodyPart on every body with
        /// the joints that move it). The blue one's objects are named b_ in place of a_, so that a boxer can
        /// meet itself: the plugin names MuJoCo's objects after them. A policy promoted onto the prefab reaches both.
        /// </summary>
        public static string BuildCorners(string name)
        {
            string title = Title(name), dir = $"{BoxersDir}/{title}";
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{dir}/{title}.prefab");
            var cfg = JsonUtility.FromJson<BoxerConfig>(File.ReadAllText($"{dir}/config.json"));
            var overlay = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Fighter_Overlay.mat");
            var trail = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Fx_Trail.mat");
            var profile = AssetDatabase.LoadAssetAtPath<League.PolicyProfile>($"{AssetBakery.LeagueDir}/Entrant_{name.ToUpperInvariant()}.asset");
            var sb = new StringBuilder(title);
            for (int corner = 0; corner < 2; corner++)
            {
                string prefix = corner == 0 ? "a_" : "b_", side = corner == 0 ? "Red" : "Blue";
                var go = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
                go.name = $"{title} {side}";
                if (corner == 1)
                    foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                        if (t.name.StartsWith("a_")) t.name = "b_" + t.name.Substring(2);
                // The exclusions carry no prefix of their own (exclude_0): two boxers in one model may not share one.
                foreach (MjExclude e in go.GetComponentsInChildren<MjExclude>(true))
                    if (!e.name.StartsWith(prefix)) e.name = prefix + e.name;
                var mj = go.GetComponent<MjBoxer>();
                mj.prefix = prefix;

                var fighter = go.AddComponent<Fighter>();
                var brain = go.AddComponent<MjBrain>();
                var skin = go.AddComponent<FighterSkin>();

                var byName = new Dictionary<string, BodyPart>();
                foreach (string body in BodyOrder)
                {
                    Transform t = FindDeep(go.transform, prefix + body) ?? throw new System.InvalidOperationException($"{title}: no body {prefix}{body}");
                    var part = t.gameObject.AddComponent<BodyPart>();
                    part.owner = fighter;
                    part.kind = KindOf(body);
                    part.side = body.EndsWith("_l") ? -1 : body.EndsWith("_r") ? 1 : 0;
                    // The joints that move this body are the joint components on it (not those of the bodies below).
                    part.joints = t.Cast<Transform>().Select(c => c.GetComponent<MjBaseJoint>()).Where(j => j != null && !(j is MjFreeJoint))
                        .Select(j => System.Array.IndexOf(cfg.joint_order, j.name.Substring(2))).Where(i => i >= 0).ToArray();
                    part.renderers = new Renderer[0];   // the shapes stay off; the mesh is what is drawn
                    byName[body] = part;
                }
                Transform head = FindDeep(go.transform, prefix + "head_geom");

                fighter.corner = corner;
                fighter.cornerColor = CornerColour[corner];
                fighter.displayName = DisplayName(name);
                fighter.profile = profile;
                fighter.parts = BodyOrder.Select(b => byName[b]).ToArray();
                fighter.pelvis = byName["pelvis"]; fighter.torso = byName["torso"];
                fighter.upperArmL = byName["upper_arm_l"]; fighter.forearmL = byName["forearm_l"];
                fighter.upperArmR = byName["upper_arm_r"]; fighter.forearmR = byName["forearm_r"];
                fighter.thighL = byName["thigh_l"]; fighter.shinL = byName["shin_l"]; fighter.footL = byName["foot_l"];
                fighter.thighR = byName["thigh_r"]; fighter.shinR = byName["shin_r"]; fighter.footR = byName["foot_r"];
                fighter.headAnchor = head;
                fighter.boxer = mj;
                fighter.standingPelvisHeight = (float)cfg.stand_height;
                fighter.totalMass = (float)cfg.total_mass_kg;
                // The rule the match policies are trained under (queue: --daze --daze-hi 42), and the scoring
                // measured from these boxers sparring in the game (2026-10-01).
                fighter.dazeRule = true;
                fighter.dazeTau = 2.5f; fighter.dazeLo = 14f; fighter.dazeHi = 42f; fighter.dazeWeak = 0.45f; fighter.dazeBody = 0.3f;
                fighter.graceSeconds = 2f;
                fighter.impulseFloor = Fighter.PunchMass;
                fighter.damagePerNs = 0.018f;

                brain.fighter = fighter;
                brain.boxer = mj;

                // The skin's parts: pelvis, torso, head, then the limbs, the order the cameras expect.
                var parts = new List<FighterSkin.Part>();
                for (int i = 0; i < fighter.parts.Length; i++)
                {
                    parts.Add(new FighterSkin.Part { bone = fighter.parts[i].transform, renderers = new Renderer[0] });
                    if (i == 1) parts.Add(new FighterSkin.Part { bone = head, renderers = new Renderer[0] });
                }
                skin.fighter = fighter;
                skin.parts = parts.ToArray();
                skin.rimColor = CornerColour[corner];
                skin.headOffset = Vector3.zero;
                skin.trails = new[] { Trail(FindDeep(go.transform, prefix + "glove_l"), trail, CornerColour[corner]),
                                      Trail(FindDeep(go.transform, prefix + "glove_r"), trail, CornerColour[corner]) };
                var mesh = go.GetComponent<SkinBinder>()?.skin;
                skin.extraRenderers = mesh != null ? mesh.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
                skin.overlayRenderers = Overlays(skin.extraRenderers, overlay);

                string path = $"{dir}/{title} {side}.prefab";
                PrefabUtility.SaveAsPrefabAsset(go, path);
                Object.DestroyImmediate(go);
                sb.Append($"; {side}: {fighter.parts.Sum(p => p.joints.Length)} joints on {fighter.parts.Length} parts");
            }
            return sb.ToString();
        }

        public static string BuildAllCorners() => string.Join("\n", Boxers().Select(BuildCorners));

        /// <summary>
        /// Moves the open arena onto the MuJoCo plugin, once: out go the old fighters, their rings, every
        /// collider and the components whose scripts are gone; in come the trainer's ring as MuJoCo objects
        /// (the floor, the four rope walls and the cubes of the spar model), one MjRing, and every boxer
        /// twice (its corner prefabs, switched off) for <see cref="MatchSetup"/> to choose from. What is left
        /// is an ordinary scene, saved and then edited by hand.
        /// </summary>
        public static string RetrofitArena()
        {
            var sb = new StringBuilder();
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            GameObject fighters = scene.GetRootGameObjects().First(g => g.name == "Fighters");
            int old = fighters.transform.childCount;
            foreach (Transform c in fighters.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(c.gameObject);
            int colliders = 0, missing = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    missing += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                    foreach (Collider c in t.GetComponents<Collider>()) { Object.DestroyImmediate(c); colliders++; }
                }
            sb.Append($"removed {old} old fighters and rings, {colliders} colliders, {missing} missing scripts\n");

            // The world of the trainer's ring: what the spar model holds besides the two boxers.
            var world = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Mj World");
            if (world == null)
            {
                MjcfImporter.DefaultMujocoMaterial = AssetDatabase.LoadAssetAtPath<Material>(BoxersDir + "/MjShape.mat");
                world = new MjcfImporter().ImportString(File.ReadAllText($"{Models}/matt_spar.xml"), "Mj World");
                world.GetComponentInChildren<MjGlobalSettings>().UseRawGameObjectNames = true;
                foreach (Transform c in world.transform.Cast<Transform>().ToList())
                    if (c.name.StartsWith("a_") || c.name.StartsWith("b_") || c.name == "actuators" || c.name == "excludes" || c.name == "sensors")
                        Object.DestroyImmediate(c.gameObject);
                var pool = new GameObject("Cube Pool");
                pool.transform.SetParent(world.transform, false);
                foreach (Transform c in world.transform.Cast<Transform>().ToList())
                    if (c.name.StartsWith("cube_")) c.SetParent(pool.transform, true);
                pool.AddComponent<MjCubePool>();
                // The arena draws its own canvas and ropes; the MuJoCo shapes are there to be collided with.
                foreach (var r in world.GetComponentsInChildren<MeshRenderer>())
                    if (!r.transform.IsChildOf(pool.transform)) r.enabled = false;
            }
            sb.Append($"world: {string.Join(", ", world.transform.Cast<Transform>().Select(t => t.name))}\n");

            var ringGo = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Mj Ring") ?? new GameObject("Mj Ring");
            var ring = ringGo.GetComponent<MjRing>() ?? ringGo.AddComponent<MjRing>();
            ring.centre = Vector2.zero;
            ring.ringHalf = 3.05f;

            Transform redCorner = GameObject.Find("Corners/Red Corner").transform, blueCorner = GameObject.Find("Corners/Blue Corner").transform;
            var entries = new List<MatchSetup.Boxer>();
            foreach (string name in Boxers())
            {
                string title = Title(name);
                var entry = new MatchSetup.Boxer { name = name };
                for (int corner = 0; corner < 2; corner++)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{BoxersDir}/{title}/{title} {(corner == 0 ? "Red" : "Blue")}.prefab");
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, fighters.transform);
                    go.transform.position = (corner == 0 ? redCorner : blueCorner).position;
                    var f = go.GetComponent<Fighter>();
                    f.ring = ring;
                    f.ringCentre = Vector3.zero;
                    f.ringHalf = ring.ringHalf;
                    go.GetComponent<MjBrain>().ring = ring;
                    go.SetActive(false);
                    if (corner == 0) entry.red = f; else entry.blue = f;
                }
                entries.Add(entry);
            }
            var setup = Object.FindAnyObjectByType<MatchSetup>();
            setup.boxers = entries.ToArray();
            setup.ring = ring;
            sb.Append($"{entries.Count} boxers in both corners; default bout {setup.defaultRed} v {setup.defaultBlue}\n");

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            sb.Append(PhysXCensus());
            return sb.ToString();
        }

        /// <summary>What the fight does to a boxer, drawn on its own mesh: a second skinned renderer over each of the mesh's, sharing its bones.</summary>
        static Renderer[] Overlays(Renderer[] renderers, Material overlay)
        {
            var made = new List<Renderer>();
            if (overlay != null)
                foreach (Renderer r in renderers)
                {
                    if (!(r is SkinnedMeshRenderer source) || source.sharedMesh == null) continue;
                    var go = new GameObject(source.name + " (overlay)");
                    go.transform.SetParent(source.transform, false);
                    var copy = go.AddComponent<SkinnedMeshRenderer>();
                    copy.sharedMesh = source.sharedMesh;
                    copy.bones = source.bones;
                    copy.rootBone = source.rootBone;
                    copy.localBounds = source.localBounds;
                    copy.updateWhenOffscreen = true;
                    copy.sharedMaterials = Enumerable.Repeat(overlay, source.sharedMesh.subMeshCount).ToArray();
                    copy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    copy.receiveShadows = false;
                    made.Add(copy);
                }
            return made.ToArray();
        }

        static TrailRenderer Trail(Transform glove, Material material, Color colour)
        {
            var go = new GameObject("Trail");
            go.transform.SetParent(glove, false);
            var trail = go.AddComponent<TrailRenderer>();
            trail.time = 0.14f;
            trail.minVertexDistance = 0.02f;
            trail.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            trail.widthMultiplier = 0.14f;
            trail.sharedMaterial = material;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(colour, 0f), new GradientColorKey(colour, 1f) },
                             new[] { new GradientAlphaKey(0.55f, 0f), new GradientAlphaKey(0f, 1f) });
            trail.colorGradient = gradient;
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            trail.emitting = false;
            return trail;
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
        [MenuItem("PoBox/Mj/Dress Every Boxer For Both Corners")] static void Corners() => Debug.Log(BuildAllCorners());
        [MenuItem("PoBox/Mj/Check Every Boxer Against Training")] static void CheckEvery() => Debug.Log(CheckAll());
    }
}
