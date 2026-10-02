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
    /// skinned mesh on that body, and the policy that drives it.
    ///
    /// An entrant is these files in <c>Assets/Entrants/&lt;name&gt;/</c>, copied there from <c>training/</c> by
    /// <c>PoBox/Import Trained Entrants</c>:
    ///     model.xml     the MuJoCo model (the bag one; only the fighter in it is read)
    ///     config.json   joint order, guard pose, limits, gains, standing height
    ///     rig.json      which bone of the mesh is which limb
    ///     policy.onnx   the trained match policy (actions, and its critic's value)
    ///     getup.onnx    the policy that gets it up off the canvas, if one has been trained
    ///     rules.json    the daze rule its match policy was trained under, so the game applies the same one
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
        /// Copies each entrant's files out of training/ into Assets/Entrants. The policy is the newest one
        /// from any match run, and otherwise the one from the fighter's own bag stage, so a fighter can be
        /// put in the ring to look at while the match is still training. The get-up policy is the newest
        /// from any get-up run. What a run trained is read from the manifest it saves beside each policy.
        /// A run whose checkpoint folder holds a file called REJECTED.txt is passed over: that is how an
        /// experiment that made the fighters worse is kept out of the game without deleting it (the file
        /// says why).
        /// </summary>
        [MenuItem("PoBox/Import Trained Entrants", priority = 4)]
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

                string onnx = null, stage = null, getUp = null, getUpStage = null;
                foreach (string run in Directory.Exists(Path.Combine(training, "checkpoints")) ? Directory.GetDirectories(Path.Combine(training, "checkpoints")) : new string[0])
                {
                    string candidate = Path.Combine(run, $"latest_{name}.onnx");
                    if (File.Exists(Path.Combine(run, "REJECTED.txt")) || !File.Exists(candidate)) continue;
                    string made = Path.Combine(run, $"latest_{name}_policy_config.json");
                    Match mode = File.Exists(made) ? Regex.Match(File.ReadAllText(made), "\"mode\"\\s*:\\s*\"(\\w+)\"") : Match.Empty;
                    if (mode.Success && mode.Groups[1].Value == "getup")
                    {
                        if (getUp == null || File.GetLastWriteTime(candidate) > File.GetLastWriteTime(getUp)) { getUp = candidate; getUpStage = Path.GetFileName(run); }
                    }
                    else if (onnx == null || File.GetLastWriteTime(candidate) > File.GetLastWriteTime(onnx)) { onnx = candidate; stage = Path.GetFileName(run); }
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
                string getUpTo = Path.Combine(full, "getup.onnx");
                if (getUp != null) File.Copy(getUp, getUpTo, true);
                else if (File.Exists(getUpTo)) { File.Delete(getUpTo); File.Delete(getUpTo + ".meta"); }
                string inertia = Path.Combine(training, "models", name + "_inertia.json");
                if (File.Exists(inertia)) File.Copy(inertia, Path.Combine(full, "inertia.json"), true);
                File.WriteAllText(Path.Combine(full, "source.txt"),
                    $"policy.onnx copied from training/checkpoints/{stage}/{Path.GetFileName(onnx)}\nwritten {File.GetLastWriteTime(onnx):yyyy-MM-dd HH:mm:ss}\n" +
                    (getUp != null ? $"getup.onnx copied from training/checkpoints/{getUpStage}/{Path.GetFileName(getUp)}\nwritten {File.GetLastWriteTime(getUp):yyyy-MM-dd HH:mm:ss}\n" : "no get-up policy\n"));
                Debug.Log($"[PoBox] imported entrant '{name}' (policy from {stage}, {File.GetLastWriteTime(onnx):HH:mm}; get-up {(getUp != null ? "from " + getUpStage : "none")}).");
                WriteRules(training, stage, Path.Combine(full, "rules.json"));
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
                // The same layout for the version of MuJoCo a phone carries (tools/export_mujoco_layout.py --android).
                string phone = ring.Substring(0, ring.Length - "_ring.xml".Length) + "_layout_android.json";
                if (File.Exists(phone)) File.Copy(phone, Path.Combine(project, RingDir, pair + "_android.json"), true);
                Debug.Log($"[PoBox] imported the MuJoCo match model for {pair.Replace("_vs_", " v ")}.");
            }
            AssetDatabase.Refresh();
            Debug.Log($"[PoBox] {imported} entrant(s) in {EntrantsDir}.");
        }

        public const string RingDir = "Assets/MuJoCo";

        /// <summary>What a punch does to a fighter, as the match policies were trained to expect it.</summary>
        [Serializable]
        public class Rules
        {
            public bool daze;
            public float dazeTau = 2.5f, dazeLo = 14f, dazeHi = 42f, dazeWeak = 0.45f, dazeBody = 0.3f;
            public string source = "";
        }

        /// <summary>
        /// Reads the daze rule off the arguments the fighter's match run was started with
        /// (training/logs/RUN.args.json) and writes it into the entrant's folder, where the scene builder
        /// finds it. One a fighter: two entrants need not have been trained under the same rule.
        /// </summary>
        static void WriteRules(string training, string stage, string rulesFile)
        {
            var rules = new Rules { source = stage };
            string args = Path.Combine(training, "logs", stage + ".args.json");
            if (File.Exists(args))
            {
                string text = File.ReadAllText(args);
                float Number(string key, float fallback)
                {
                    Match m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*([-0-9.eE]+)");
                    return m.Success ? float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
                }
                rules.daze = Regex.IsMatch(text, "\"daze\"\\s*:\\s*true");
                rules.dazeTau = Number("daze_tau", rules.dazeTau);
                rules.dazeLo = Number("daze_lo", rules.dazeLo);
                rules.dazeHi = Number("daze_hi", rules.dazeHi);
                rules.dazeWeak = Number("daze_weak", rules.dazeWeak);
            }
            File.WriteAllText(rulesFile, JsonUtility.ToJson(rules, true));
        }

        public static Rules ReadRules(string entrant)
        {
            string path = $"{EntrantsDir}/{entrant}/rules.json";
            Rules rules = File.Exists(path) ? JsonUtility.FromJson<Rules>(File.ReadAllText(path)) : null;
            return rules ?? new Rules();
        }

        /// <summary>Every entrant has a get-up policy: the count is then a real one, ten seconds long.</summary>
        public static bool AllCanGetUp(string[] entrants)
        {
            foreach (string name in entrants)
                if (AssetDatabase.LoadAssetAtPath<ModelAsset>($"{EntrantsDir}/{name}/getup.onnx") == null) return false;
            return entrants.Length > 0;
        }

        /// <summary>
        /// One MuJoCo ring, switched off, for every pair of different entrants whose match model has been
        /// exported (training/tools/export_mujoco_layout.py). <see cref="MatchSetup"/> switches on the one
        /// for the pair that is fighting and hands it their bodies. A pair with no model runs on Unity's
        /// physics, where these policies fall over.
        /// </summary>
        public static MatchSetup.Pairing[] AddRings(string[] entrants, Vector3 centre, int controlDecimation, Transform parent)
        {
            var made = new List<MatchSetup.Pairing>();
            if (!File.Exists("Assets/Plugins/x86_64/mujoco.dll"))
            {
                Debug.LogWarning("[PoBox] Assets/Plugins/x86_64/mujoco.dll is missing; the fighters will run on Unity's physics.");
                return made.ToArray();
            }
            for (int i = 0; i < entrants.Length; i++)
            {
                for (int j = i + 1; j < entrants.Length; j++)
                {
                    // The model was exported with its two fighters in one order or the other.
                    string a = entrants[i], b = entrants[j];
                    if (!File.Exists($"{RingDir}/{a}_vs_{b}.xml") && File.Exists($"{RingDir}/{b}_vs_{a}.xml")) (a, b) = (b, a);
                    string xml = $"{RingDir}/{a}_vs_{b}.xml", layout = $"{RingDir}/{a}_vs_{b}.json";
                    if (!File.Exists(xml) || !File.Exists(layout))
                    {
                        Debug.LogWarning($"[PoBox] no MuJoCo match model for {a} v {b} (training/tools/export_mujoco_layout.py --a {a} --b {b}); that pair will run on Unity's physics.");
                        continue;
                    }
                    var go = new GameObject($"MuJoCo Ring ({Title(a)} v {Title(b)})");
                    go.transform.SetParent(parent, false);
                    var ring = go.AddComponent<MujocoRing>();
                    ring.xml = File.ReadAllText(xml);
                    ring.layoutJson = File.ReadAllText(layout);
                    string phone = $"{RingDir}/{a}_vs_{b}_android.json";
                    ring.layoutJsonAndroid = File.Exists(phone) ? File.ReadAllText(phone) : "";
                    ring.centre = centre;
                    ring.controlDecimation = controlDecimation;
                    go.SetActive(false);
                    made.Add(new MatchSetup.Pairing { a = a, b = b, ring = ring });
                }
            }
            return made.ToArray();
        }

        /// <summary>What the first screen shows for an entrant: its name, its size, and its portrait if one has been taken.</summary>
        public static PoBox.UI.MenuView.Boxer Card(string name)
        {
            string cfg = File.ReadAllText($"{EntrantsDir}/{name}/config.json");
            float Number(string key)
            {
                Match m = Regex.Match(cfg, "\"" + key + "\"\\s*:\\s*([0-9.]+)");
                return m.Success ? float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0f;
            }
            float height = Number("height_m"), mass = Number("total_mass_kg");

            string portrait = $"{AssetBakery.PortraitDir}/{name}.png";
            if (File.Exists(portrait))
            {
                // Taken a moment ago, perhaps: the file may not be an asset yet.
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
            else Debug.LogWarning($"[PoBox] no portrait for {name} ({portrait}); in play mode on the arena, run the dev command 'portraits'.");

            return new PoBox.UI.MenuView.Boxer
            {
                name = name,
                displayName = DisplayName(name),
                detail = height > 0f && mass > 0f ? $"{height:0.00} m · {mass:0} kg" : "",
                portrait = AssetDatabase.LoadAssetAtPath<Texture2D>(portrait),
            };
        }

        /// <summary>
        /// Entrants with everything they need, in name order. A fighter whose only policy is from its bag
        /// stage is left out once there are two that have been through a match: it cannot box yet (a bag
        /// policy leans on a bag that is not there), and it would otherwise walk into the menu and the
        /// default pairing half way through its own training. With fewer than two match-trained fighters
        /// it is let in, so the first fighter of a new roster can be looked at in the ring.
        /// </summary>
        public static string[] Available()
        {
            var all = AllComplete();
            var boxers = new List<string>();
            foreach (string name in all)
                if (!Regex.IsMatch(File.ReadAllText($"{EntrantsDir}/{name}/config.json"), "\"mode\"\\s*:\\s*\"bag\"")) boxers.Add(name);
            if (boxers.Count < 2 || boxers.Count == all.Length) return all;
            foreach (string name in all)
                if (!boxers.Contains(name)) Debug.Log($"[PoBox] entrant '{name}' has only hit the bag so far; it is left out of the arena until it has a match policy.");
            return boxers.ToArray();
        }

        static string[] AllComplete()
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

        /// <summary>The name on the scoreboard: the rig file's "display" where it has one ("LIL MATT"), otherwise the folder's name in capitals.</summary>
        public static string DisplayName(string name)
        {
            string rig = $"{EntrantsDir}/{name}/rig.json";
            if (File.Exists(rig))
            {
                Match m = Regex.Match(File.ReadAllText(rig), "\"display\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            }
            return name.ToUpperInvariant();
        }

        internal static GameObject Mesh(string name)
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
            p.displayName = DisplayName(name);
            p.checkpoint = AssetDatabase.LoadAssetAtPath<ModelAsset>($"{EntrantsDir}/{name}/policy.onnx");

            Match stamp = Regex.Match(File.ReadAllText($"{EntrantsDir}/{name}/config.json"), "\"iterations\"\\s*:\\s*(\\d+)");
            p.generation = stamp.Success ? int.Parse(stamp.Groups[1].Value) : 0;
            EditorUtility.SetDirty(p);
            return p;
        }

        // ---------------------------------------------------------------- build

        internal static Dictionary<string, string> BoneMap(string rigJson)
        {
            var map = new Dictionary<string, string>();
            Match block = Regex.Match(rigJson, "\"map\"\\s*:\\s*\\{([^}]*)\\}");
            if (block.Success)
                foreach (Match m in Regex.Matches(block.Groups[1].Value, "\"(\\w+)\"\\s*:\\s*\"([^\"]+)\""))
                    map[m.Groups[1].Value] = m.Groups[2].Value;
            return map;
        }

        /// <param name="glove">What a glove's object is called, less its side: the old importer's, or the MuJoCo plugin's ("glove_").</param>
        internal static void ConfigureBinder(SkinBinder binder, Dictionary<string, string> bones, string glove = "geom_glove_")
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
                Aim($"forearm_{s}", $"hand_{s}", $"forearm_{s}", glove + s);
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

        static GameObject AddSkin(string name, Transform rigRoot, GameObject host, Dictionary<string, string> bones, Material overlay,
                                  out Renderer[] renderers, out Renderer[] overlays)
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

            // What the fight does to the fighter, drawn on its own mesh: a second skinned renderer over each
            // of the mesh's, sharing its bones, with the overlay material on every part of it. (A second
            // material on the mesh's own renderer would only be drawn over its last sub-mesh.)
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
                    var materials = new Material[source.sharedMesh.subMeshCount];
                    for (int i = 0; i < materials.Length; i++) materials[i] = overlay;
                    copy.sharedMaterials = materials;
                    copy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    copy.receiveShadows = false;
                    made.Add(copy);
                }
            overlays = made.ToArray();
            return skin;
        }

        /// <summary>
        /// The fighter dressed for one corner (0 red, 1 blue), standing at <paramref name="position"/> looking
        /// along <paramref name="rotation"/>'s forward. Built with the rig in the pose the model defines as
        /// zero (arms out, legs straight): the mesh is bound to it in that pose when the fighter is switched
        /// on, and the bout stands the fighter in its guard before the bell.
        /// </summary>
        public static Fighter Build(string name, Vector3 position, Quaternion rotation, FighterFactory.Look look, int corner, Transform parent)
        {
            string dir = $"{EntrantsDir}/{name}";
            string xml = File.ReadAllText(dir + "/model.xml");
            string cfg = File.ReadAllText(dir + "/config.json");
            Dictionary<string, string> bones = BoneMap(File.ReadAllText(dir + "/rig.json"));

            var root = new GameObject($"{Title(name)} ({(corner == 0 ? "Red" : "Blue")})");
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(position, rotation);

            var options = new MjcfFighterImporter.Options
            {
                prefix = "a_", gloveMaterial = look.glove,
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
            fighter.displayName = DisplayName(name);
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
            // What a punch does to a trained fighter is the rule its policy was trained under: the daze.
            // A policy trained before there was one gets the same rule with a higher line, so that a
            // fighter never taught to avoid it is not on the canvas every round.
            Rules rules = ReadRules(name);
            fighter.dazeRule = true;
            fighter.dazeTau = rules.dazeTau;
            fighter.dazeLo = rules.dazeLo;
            fighter.dazeHi = rules.daze ? rules.dazeHi : 54f;
            fighter.dazeWeak = rules.dazeWeak;
            fighter.dazeBody = rules.dazeBody;
            fighter.graceSeconds = 2f;
            // A punch is one that arrives faster than 1 m/s, as in training: 2.2 kg behind the glove.
            fighter.impulseFloor = Fighter.PunchMass;
            fighter.damagePerNs = 0.018f;
            // Better: the numbers measured from these very policies sparring (TransferProbe, -probeMode spar -probeCalibrate 1).
            string scoring = $"{EntrantsDir}/scoring.json";
            if (File.Exists(scoring))
            {
                var s = JsonUtility.FromJson<TransferProbe.Scoring>(File.ReadAllText(scoring));
                if (s != null && s.damagePerNs > 0f)
                {
                    fighter.damagePerNs = s.damagePerNs;
                }
            }

            brain.fighter = fighter;
            brain.rig = rig;
            brain.model = AssetDatabase.LoadAssetAtPath<ModelAsset>(dir + "/policy.onnx");
            brain.getUpModel = AssetDatabase.LoadAssetAtPath<ModelAsset>(dir + "/getup.onnx");
            brain.bagStage = Regex.IsMatch(cfg, "\"mode\"\\s*:\\s*\"bag\"");

            // The skin's parts: pelvis, torso, head, then the limbs, which is the order the cameras
            // expect. The head is the anchor; it has nothing of its own to draw.
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

            AddSkin(name, built.root.transform, root, bones, look.overlay, out Renderer[] meshRenderers, out Renderer[] overlays);
            skin.extraRenderers = meshRenderers;
            skin.overlayRenderers = overlays;
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
    }
}
