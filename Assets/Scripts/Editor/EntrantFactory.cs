using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Unity.InferenceEngine;
using PoBox.Fx;
using PoBox.League;
using PoBox.Rl;
using PoBox.Sim;
using BodyPart = PoBox.Sim.BodyPart;   // UnityEditor has one too (for avatars)

namespace PoBox.EditorTools
{
    /// <summary>
    /// Puts a trained fighter in the scene: the body from the MuJoCo model it was trained on, the owner's
    /// skinned mesh on that body, the policy that drives it, and a puppet of it for replays.
    ///
    /// An entrant is four files in <c>Assets/Entrants/&lt;name&gt;/</c>, copied there from <c>training/</c> by
    /// <c>PoBox/Import Trained Entrants</c>:
    ///     model.xml     the MuJoCo model (the bag one; only the fighter in it is read)
    ///     config.json   joint order, guard pose, limits, gains, standing height
    ///     rig.json      which bone of the mesh is which limb
    ///     policy.onnx   the trained policy
    /// and the mesh itself, <c>Assets/Models/&lt;Name&gt;.glb</c>.
    /// </summary>
    public static class EntrantFactory
    {
        public const string EntrantsDir = "Assets/Entrants";
        const string TrainingDir = "training";

        static readonly string[] BodyOrder =
        {
            "pelvis", "torso", "upper_arm_l", "forearm_l", "upper_arm_r", "forearm_r",
            "thigh_l", "shin_l", "foot_l", "thigh_r", "shin_r", "foot_r",
        };

        // ---------------------------------------------------------------- import

        /// <summary>
        /// Copies each entrant's files out of training/ into Assets/Entrants. The policy is the one from the
        /// match if there is one, and otherwise the one from the fighter's own bag stage, so a fighter can be
        /// put in the ring to look at while the match is still training.
        /// </summary>
        [MenuItem("PoBox/Import Trained Entrants", priority = 3)]
        public static void Import()
        {
            string project = Path.GetDirectoryName(Application.dataPath);
            string training = Path.Combine(project, TrainingDir);
            int imported = 0;
            foreach (string rig in Directory.GetFiles(Path.Combine(training, "rigs"), "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(rig);
                string xml = Path.Combine(training, "models", name + "_bag.xml");
                string cfg = Path.Combine(training, "models", name + "_policy_config.json");
                if (!File.Exists(xml) || !File.Exists(cfg)) continue;

                string onnx = null, stage = null;
                foreach (string run in Directory.Exists(Path.Combine(training, "checkpoints")) ? Directory.GetDirectories(Path.Combine(training, "checkpoints")) : new string[0])
                {
                    string candidate = Path.Combine(run, $"latest_{name}.onnx");
                    if (File.Exists(candidate) && (onnx == null || File.GetLastWriteTime(candidate) > File.GetLastWriteTime(onnx))) { onnx = candidate; stage = Path.GetFileName(run); }
                }
                if (onnx == null)
                {
                    string bag = Path.Combine(training, "checkpoints", "bag_" + name, "latest.onnx");
                    if (File.Exists(bag)) { onnx = bag; stage = "bag_" + name; }
                }
                if (onnx == null) { Debug.Log($"[PoBox] {name}: a model but no trained policy yet; skipped."); continue; }
                // The config saved beside the policy is the one that says what the policy was trained as.
                string manifest = onnx.Substring(0, onnx.Length - ".onnx".Length) + "_policy_config.json";
                if (File.Exists(manifest)) cfg = manifest;

                string dir = $"{EntrantsDir}/{name}";
                AssetBakery.EnsureFolder(dir);
                string full = Path.Combine(project, dir);
                File.Copy(xml, Path.Combine(full, "model.xml"), true);
                File.Copy(cfg, Path.Combine(full, "config.json"), true);
                File.Copy(rig, Path.Combine(full, "rig.json"), true);
                File.Copy(onnx, Path.Combine(full, "policy.onnx"), true);
                string inertia = Path.Combine(training, "models", name + "_inertia.json");
                if (File.Exists(inertia)) File.Copy(inertia, Path.Combine(full, "inertia.json"), true);
                File.WriteAllText(Path.Combine(full, "source.txt"),
                    $"policy.onnx copied from training/checkpoints/{stage}/{Path.GetFileName(onnx)}\nwritten {File.GetLastWriteTime(onnx):yyyy-MM-dd HH:mm:ss}\n");
                Debug.Log($"[PoBox] imported entrant '{name}' (policy from {stage}, {File.GetLastWriteTime(onnx):HH:mm}).");
                imported++;
            }
            // The match model and its layout, for running the fight on MuJoCo itself (MujocoRing).
            foreach (string ring in Directory.GetFiles(Path.Combine(training, "models"), "*_vs_*_ring.xml"))
            {
                string layout = ring.Substring(0, ring.Length - "_ring.xml".Length) + "_layout.json";
                if (!File.Exists(layout)) continue;
                string pair = Path.GetFileName(ring).Replace("_ring.xml", "");
                AssetBakery.EnsureFolder(RingDir);
                File.Copy(ring, Path.Combine(project, RingDir, pair + ".xml"), true);
                File.Copy(layout, Path.Combine(project, RingDir, pair + ".json"), true);
                Debug.Log($"[PoBox] imported the MuJoCo match model for {pair.Replace("_vs_", " v ")}.");
            }
            AssetDatabase.Refresh();
            Debug.Log($"[PoBox] {imported} entrant(s) in {EntrantsDir}.");
        }

        public const string RingDir = "Assets/MuJoCo";

        /// <summary>
        /// Puts the match on MuJoCo if the model for exactly this pair has been exported and the library is
        /// in the project. Returns null if not, and the fighters then run on Unity's physics.
        /// </summary>
        public static MujocoRing AddRing(Fighter a, Fighter b, Vector3 centre, Transform parent)
        {
            string pair = $"{a.mjcf.fighterName}_vs_{b.mjcf.fighterName}";
            string xml = $"{RingDir}/{pair}.xml", layout = $"{RingDir}/{pair}.json";
            if (!File.Exists(xml) || !File.Exists(layout))
            {
                Debug.LogWarning($"[PoBox] no MuJoCo match model for {pair} (training/tools/export_mujoco_layout.py); the fighters will run on Unity's physics.");
                return null;
            }
            if (!File.Exists("Assets/Plugins/x86_64/mujoco.dll"))
            {
                Debug.LogWarning("[PoBox] Assets/Plugins/x86_64/mujoco.dll is missing; the fighters will run on Unity's physics.");
                return null;
            }
            var go = new GameObject("MuJoCo Ring");
            go.transform.SetParent(parent, false);
            var ring = go.AddComponent<MujocoRing>();
            ring.xml = File.ReadAllText(xml);
            ring.layoutJson = File.ReadAllText(layout);
            ring.rigs = new[] { a.mjcf, b.mjcf };
            ring.centre = centre;
            ring.controlDecimation = a.mjcf.controlDecimation;
            return ring;
        }

        /// <summary>Entrants with everything they need, in name order.</summary>
        public static string[] Available()
        {
            var names = new List<string>();
            if (!AssetDatabase.IsValidFolder(EntrantsDir)) return names.ToArray();
            foreach (string dir in AssetDatabase.GetSubFolders(EntrantsDir))
            {
                string name = Path.GetFileName(dir);
                if (name == "dev_fixture") continue;   // the trainer's rehearsal rig, not an entrant
                bool complete = File.Exists(dir + "/model.xml") && File.Exists(dir + "/config.json") && File.Exists(dir + "/rig.json")
                             && AssetDatabase.LoadAssetAtPath<ModelAsset>(dir + "/policy.onnx") != null && Mesh(name) != null;
                if (complete) names.Add(name);
                else Debug.LogWarning($"[PoBox] entrant '{name}' is incomplete (model, config, rig, policy and Assets/Models/{Title(name)}.glb are all needed).");
            }
            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
        }

        static string Title(string name) => char.ToUpperInvariant(name[0]) + name.Substring(1);

        static GameObject Mesh(string name)
        {
            foreach (string ext in new[] { ".glb", ".fbx" })
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Models/{Title(name)}{ext}");
                if (go != null) return go;
            }
            return null;
        }

        /// <summary>A ladder entry for a trained fighter: its policy is its checkpoint, its generation the iterations it trained.</summary>
        public static PolicyProfile Profile(string name)
        {
            AssetBakery.EnsureFolder(AssetBakery.LeagueDir);
            string path = $"{AssetBakery.LeagueDir}/Entrant_{name.ToUpperInvariant()}.asset";
            var p = AssetDatabase.LoadAssetAtPath<PolicyProfile>(path);
            if (p == null)
            {
                p = ScriptableObject.CreateInstance<PolicyProfile>();
                AssetDatabase.CreateAsset(p, path);
            }
            p.displayName = name.ToUpperInvariant();
            p.checkpoint = AssetDatabase.LoadAssetAtPath<ModelAsset>($"{EntrantsDir}/{name}/policy.onnx");

            Match stamp = Regex.Match(File.ReadAllText($"{EntrantsDir}/{name}/config.json"), "\"iterations\"\\s*:\\s*(\\d+)");
            p.generation = stamp.Success ? int.Parse(stamp.Groups[1].Value) : 0;
            EditorUtility.SetDirty(p);
            return p;
        }

        // ---------------------------------------------------------------- build

        static Dictionary<string, string> BoneMap(string rigJson)
        {
            var map = new Dictionary<string, string>();
            Match block = Regex.Match(rigJson, "\"map\"\\s*:\\s*\\{([^}]*)\\}");
            if (block.Success)
                foreach (Match m in Regex.Matches(block.Groups[1].Value, "\"(\\w+)\"\\s*:\\s*\"([^\"]+)\""))
                    map[m.Groups[1].Value] = m.Groups[2].Value;
            return map;
        }

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

            // Parents before children: a bone placed later moves with the ones before it.
            Map("pelvis", "pelvis");
            Map("torso", "spine");
            foreach (string s in new[] { "l", "r" })
            {
                Map($"upper_arm_{s}", $"upper_arm_{s}");
                Map($"forearm_{s}", $"forearm_{s}");
                Aim($"upper_arm_{s}", $"forearm_{s}", $"upper_arm_{s}", $"forearm_{s}");
                Aim($"forearm_{s}", $"hand_{s}", $"forearm_{s}", $"geom_glove_{s}");
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

        static GameObject AddSkin(string name, Transform rigRoot, GameObject host, Dictionary<string, string> bones, out Renderer[] renderers)
        {
            GameObject prefab = Mesh(name);
            var skin = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            skin.name = Title(name) + " Mesh";
            skin.transform.SetParent(rigRoot, false);
            skin.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            var binder = host.AddComponent<SkinBinder>();
            binder.rigRoot = rigRoot;
            binder.skin = skin;
            ConfigureBinder(binder, bones);
            renderers = skin.GetComponentsInChildren<Renderer>(true);
            return skin;
        }

        /// <summary>
        /// The live fighter, standing at <paramref name="position"/> looking along <paramref name="rotation"/>'s
        /// forward, and its replay puppet. Built with the rig in the pose the model defines as zero (arms out,
        /// legs straight): the mesh is bound to it in that pose when the scene starts, and the bout stands the
        /// fighter in its guard before the bell.
        /// </summary>
        public static Fighter Build(string name, Vector3 position, Quaternion rotation, FighterFactory.Look look, Material xray,
                                    int corner, Transform parent, out FighterSkin puppet)
        {
            string dir = $"{EntrantsDir}/{name}";
            string xml = File.ReadAllText(dir + "/model.xml");
            string cfg = File.ReadAllText(dir + "/config.json");
            Dictionary<string, string> bones = BoneMap(File.ReadAllText(dir + "/rig.json"));

            var root = new GameObject(Title(name));
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(position, rotation);

            var options = new MjcfFighterImporter.Options
            {
                prefix = "a_", xrayMaterial = xray, gloveMaterial = look.glove,
                bodyPhysics = look.body, solePhysics = look.sole, leatherPhysics = look.leather,
                inertiaJson = File.Exists(dir + "/inertia.json") ? File.ReadAllText(dir + "/inertia.json") : null,
            };
            MjcfFighterImporter.Result built = MjcfFighterImporter.Build(xml, cfg, options, root.transform);
            // The rig looks along its own +X; the fighter object looks along +Z.
            built.root.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
            MjcfRig rig = built.rig;

            var fighter = root.AddComponent<Fighter>();
            var skin = root.AddComponent<FighterSkin>();
            var brain = root.AddComponent<PolicyBrain>();

            var parts = new List<BodyPart>();
            var byName = new Dictionary<string, BodyPart>();
            foreach (string bodyName in BodyOrder)
            {
                MjcfFighterImporter.Body body = built.Find(bodyName) ?? throw new InvalidOperationException($"{name}: model has no body '{bodyName}'");
                var part = body.link.gameObject.AddComponent<BodyPart>();
                part.owner = fighter;
                part.kind = KindOf(bodyName);
                part.side = bodyName.EndsWith("_l") ? -1 : bodyName.EndsWith("_r") ? 1 : 0;
                part.body = body.link;
                part.drives = body.hinges.ToArray();
                part.strike = body.glove;
                part.renderers = body.renderers.ToArray();
                if (part.kind == PartKind.Forearm) body.link.gameObject.AddComponent<GloveSensor>();
                parts.Add(part);
                byName[bodyName] = part;
            }

            var anchor = new GameObject("head");
            anchor.transform.SetParent(byName["torso"].transform, false);
            anchor.transform.position = rig.headGeom.position;

            fighter.corner = corner;
            fighter.cornerColor = look.rim;
            fighter.displayName = name.ToUpperInvariant();
            fighter.parts = parts.ToArray();
            fighter.pelvis = byName["pelvis"]; fighter.torso = byName["torso"];
            fighter.upperArmL = byName["upper_arm_l"]; fighter.forearmL = byName["forearm_l"];
            fighter.upperArmR = byName["upper_arm_r"]; fighter.forearmR = byName["forearm_r"];
            fighter.thighL = byName["thigh_l"]; fighter.shinL = byName["shin_l"]; fighter.footL = byName["foot_l"];
            fighter.thighR = byName["thigh_r"]; fighter.shinR = byName["shin_r"]; fighter.footR = byName["foot_r"];
            fighter.headAnchor = anchor.transform;
            fighter.mjcf = rig;
            fighter.standingPelvisHeight = rig.standHeight;
            fighter.totalMass = rig.totalMass;
            // The scorekeeper's numbers were set for the stand-ins, who landed a punch every few seconds.
            // A trained fighter lands one or two a second at 5 to 20 N s, so each is worth less, and it takes
            // one of the heavy ones to the head to buckle the legs.
            fighter.graceSeconds = 6f;
            fighter.knockdownBelowHealth = 75f;
            fighter.impulseFloor = 3f;
            fighter.damagePerNs = 0.12f;
            fighter.staggerShock = 12f;
            fighter.knockdownShock = 16.5f;
            // Better: the numbers measured from these very policies sparring (TransferProbe, -probeMode spar -probeCalibrate 1).
            string scoring = $"{EntrantsDir}/scoring.json";
            if (File.Exists(scoring))
            {
                var s = JsonUtility.FromJson<TransferProbe.Scoring>(File.ReadAllText(scoring));
                if (s != null && s.damagePerNs > 0f)
                {
                    fighter.impulseFloor = s.impulseFloor;
                    fighter.damagePerNs = s.damagePerNs;
                    fighter.staggerShock = s.staggerShock;
                    fighter.knockdownShock = s.knockdownShock;
                }
            }

            brain.fighter = fighter;
            brain.rig = rig;
            brain.model = AssetDatabase.LoadAssetAtPath<ModelAsset>(dir + "/policy.onnx");
            brain.bagStage = Regex.IsMatch(cfg, "\"mode\"\\s*:\\s*\"bag\"");

            // The skin's parts: pelvis, torso, head, then the limbs, which is the order the cameras and the
            // replay expect. The head is the anchor; it has nothing of its own to draw.
            var skinParts = new List<FighterSkin.Part>();
            for (int i = 0; i < parts.Count; i++)
            {
                skinParts.Add(new FighterSkin.Part { bone = parts[i].transform, renderers = parts[i].renderers });
                if (i == 1) skinParts.Add(new FighterSkin.Part { bone = anchor.transform, renderers = new Renderer[0] });
            }
            skin.fighter = fighter;
            skin.parts = skinParts.ToArray();
            skin.rimColor = look.rim;
            skin.headOffset = Vector3.zero;
            skin.trails = new[] { Trail(rig.gloveL, look), Trail(rig.gloveR, look) };

            puppet = BuildPuppet(name, root, built, skinParts, look, bones, parent);
            AddSkin(name, built.root.transform, root, bones, out Renderer[] meshRenderers);
            skin.extraRenderers = meshRenderers;
            return fighter;
        }

        static TrailRenderer Trail(Transform glove, FighterFactory.Look look)
        {
            var go = new GameObject("Trail");
            go.transform.SetParent(glove, false);
            var trail = go.AddComponent<TrailRenderer>();
            trail.time = 0.14f;
            trail.minVertexDistance = 0.02f;
            trail.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            trail.widthMultiplier = 0.14f;
            trail.sharedMaterial = look.trail;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(look.rim, 0f), new GradientColorKey(look.rim, 1f) },
                new[] { new GradientAlphaKey(0.55f, 0f), new GradientAlphaKey(0f, 1f) });
            trail.colorGradient = gradient;
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            trail.emitting = false;
            return trail;
        }

        /// <summary>
        /// The replay puppet: one plain transform for each part of the live fighter, in the same order and at
        /// the same place, carrying copies of what is drawn on it, with its own copy of the mesh bound to them.
        /// </summary>
        static FighterSkin BuildPuppet(string name, GameObject live, MjcfFighterImporter.Result built, List<FighterSkin.Part> liveParts,
                                       FighterFactory.Look look, Dictionary<string, string> bones, Transform parent)
        {
            var root = new GameObject(Title(name) + " Replay Puppet");
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(built.root.transform.position, built.root.transform.rotation);
            var skin = root.AddComponent<FighterSkin>();
            var parts = new List<FighterSkin.Part>();

            foreach (FighterSkin.Part source in liveParts)
            {
                var bone = new GameObject(source.bone.name);
                bone.transform.SetParent(root.transform, false);
                bone.transform.SetPositionAndRotation(source.bone.position, source.bone.rotation);
                var renderers = new List<Renderer>();
                foreach (Transform child in source.bone)
                {
                    if (!child.name.StartsWith("geom_")) continue;
                    GameObject copy = UnityEngine.Object.Instantiate(child.gameObject, bone.transform);
                    copy.name = child.name;
                    copy.transform.SetPositionAndRotation(child.position, child.rotation);
                    foreach (Collider c in copy.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(c);
                    foreach (HitZone z in copy.GetComponentsInChildren<HitZone>()) UnityEngine.Object.DestroyImmediate(z);
                    foreach (TrailRenderer t in copy.GetComponentsInChildren<TrailRenderer>()) UnityEngine.Object.DestroyImmediate(t.gameObject);
                    renderers.AddRange(copy.GetComponentsInChildren<Renderer>());
                }
                parts.Add(new FighterSkin.Part { bone = bone.transform, renderers = renderers.ToArray() });
            }

            skin.parts = parts.ToArray();
            skin.rimColor = look.rim;
            skin.headOffset = Vector3.zero;
            AddSkin(name, root.transform, root, bones, out Renderer[] meshRenderers);
            skin.extraRenderers = meshRenderers;
            return skin;
        }
    }
}
