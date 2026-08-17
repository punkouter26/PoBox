using System.Collections.Generic;
using System.Text;
using PoBox;
using UnityEditor;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// Auto-rigs the selected hierarchy into an active-ragdoll fighter:
    /// maps bones (Mecanim humanoid first, name-pattern fallback), assigns
    /// normalized biomechanical masses, colliders, ConfigurableJoints with
    /// slerp drives from the PRD table, foot sensors, and the rig component.
    /// Refuses to build a partial rig: every segment must resolve.
    /// </summary>
    internal static class RigTool_AutoRig
    {
        [MenuItem("Tools/ML Boxing/2. Auto-Rig Selected")]
        public static void AutoRigSelected()
        {
            GameObject root = Selection.activeGameObject;
            if (root == null)
            {
                EditorUtility.DisplayDialog("Auto-Rig", "Select the fighter root object first.", "OK");
                return;
            }
            AutoRig(root);
        }

        public static Systems_FighterRig AutoRig(GameObject root)
        {
            if (root.GetComponent<Systems_FighterRig>() != null)
            {
                bool rerig = EditorUtility.DisplayDialog("Auto-Rig",
                    root.name + " is already rigged. Strip and re-rig?", "Re-Rig", "Cancel");
                if (!rerig)
                {
                    return null;
                }
                StripRig(root);
            }

            Dictionary<RigSegment, Transform> bones = MapBones(root, out List<RigSegment> missing);
            // Project rule: NO animations — this is a pure physics sim. Imported
            // models often ship with an auto-playing Animation/Animator that
            // silently overrides bone transforms and fakes stability (found on
            // 2026_GrandmaRigged: an embedded clip held her upright). Stripped
            // after MapBones so the humanoid-avatar mapping path still works.
            foreach (var animation in root.GetComponentsInChildren<Animation>(true)) { Object.DestroyImmediate(animation); }
            foreach (var animator in root.GetComponentsInChildren<Animator>(true)) { Object.DestroyImmediate(animator); }
            if (missing.Count > 0)
            {
                var message = new StringBuilder("Auto-Rig aborted. Missing segments:\n");
                for (int missingIndex = 0; missingIndex < missing.Count; missingIndex++)
                {
                    message.Append("  - ").Append(missing[missingIndex]).Append('\n');
                }
                Debug.LogError(message.ToString());
                EditorUtility.DisplayDialog("Auto-Rig", message.ToString(), "OK");
                return null;
            }

            float massScale = RigTool_Config.DEFAULT_TOTAL_MASS / RigTool_Config.TotalMassPercent();
            PhysicsMaterial highFriction = GetOrCreateHighFrictionMaterial();

            // Pass 1: rigidbodies + colliders (joints need connected bodies to exist).
            var bodies = new Dictionary<RigSegment, Rigidbody>();
            RigSegmentSpec[] specs = RigTool_Config.Specs;
            for (int specIndex = 0; specIndex < specs.Length; specIndex++)
            {
                RigSegmentSpec spec = specs[specIndex];
                Transform bone = bones[spec.segment];
                var body = bone.gameObject.AddComponent<Rigidbody>();
                body.mass = spec.massPercent * massScale;
                // Per-body solver iterations are baked at AddComponent time from the
                // global default — set explicitly so a prefab built before "Apply
                // Project Settings" cannot silently carry 6/1.
                body.solverIterations = 16;
                body.solverVelocityIterations = 16;
                bodies[spec.segment] = body;

                bool isSupport = spec.segment is RigSegment.FootL or RigSegment.FootR;
                if (bone.GetComponent<Collider>() == null)
                {
                    if (isSupport)
                    {
                        AddFootCollider(bone);
                    }
                    else
                    {
                        AddSegmentCollider(bone, bones, spec);
                    }
                }

                bool isStriker = spec.segment is RigSegment.GloveL or RigSegment.GloveR or RigSegment.Head;
                body.collisionDetectionMode = isStriker
                    ? CollisionDetectionMode.ContinuousDynamic
                    : CollisionDetectionMode.Discrete;
                if (isSupport)
                {
                    // The sole box may live on a world-aligned child (authored rigs).
                    bone.GetComponentInChildren<Collider>().sharedMaterial = highFriction;
                }
            }

            // Pass 2: joints, in canonical enum order — this order IS the action mapping.
            var entries = new List<RigJointEntry>();
            for (int specIndex = 0; specIndex < specs.Length; specIndex++)
            {
                RigSegmentSpec spec = specs[specIndex];
                if (spec.segment == RigSegment.Pelvis)
                {
                    continue;
                }
                entries.Add(CreateJoint(bones[spec.segment], bodies[spec.segment], bodies[spec.parent], spec));
            }

            var footLeftSensor = bones[RigSegment.FootL].gameObject.AddComponent<Sensor_GroundContact>();
            var footRightSensor = bones[RigSegment.FootR].gameObject.AddComponent<Sensor_GroundContact>();

            var rig = root.AddComponent<Systems_FighterRig>();
            rig.EditorInitialize(
                bodies[RigSegment.Pelvis],
                bodies[RigSegment.Torso],
                bones[RigSegment.Head],
                bones[RigSegment.GloveL],
                bones[RigSegment.GloveR],
                footLeftSensor,
                footRightSensor,
                entries);

            EditorUtility.SetDirty(root);
            Debug.Log($"RigTool: rigged '{root.name}' — {specs.Length} bodies, {entries.Count} joints, " +
                      $"{rig.DofCount} action DOF, total mass {RigTool_Config.DEFAULT_TOTAL_MASS} kg. " +
                      "Next: Tools > ML Boxing > 3. Prepare for Training.");
            return rig;
        }

        /// <summary>
        /// Segment whose bone position defines the far end of a limb capsule.
        /// Authored rigs (FBX/glTF) orient bone axes arbitrarily, so collider
        /// directions are measured from actual bone geometry, never assumed.
        /// The generated capsule biped resolves to identical colliders as the
        /// old hard-coded path.
        /// </summary>
        private static RigSegment? CapsuleTargetSegment(RigSegment segment)
        {
            switch (segment)
            {
                case RigSegment.ThighL: return RigSegment.ShinL;
                case RigSegment.ShinL: return RigSegment.FootL;
                case RigSegment.ThighR: return RigSegment.ShinR;
                case RigSegment.ShinR: return RigSegment.FootR;
                case RigSegment.UpperArmL: return RigSegment.ForearmL;
                case RigSegment.ForearmL: return RigSegment.GloveL;
                case RigSegment.UpperArmR: return RigSegment.ForearmR;
                case RigSegment.ForearmR: return RigSegment.GloveR;
                case RigSegment.Torso: return RigSegment.Head;
                default: return null;
            }
        }

        private static int DominantAxis(Vector3 v)
        {
            float ax = Mathf.Abs(v.x);
            float ay = Mathf.Abs(v.y);
            float az = Mathf.Abs(v.z);
            if (ax >= ay && ax >= az) { return 0; }
            return ay >= az ? 1 : 2;
        }

        /// <summary>
        /// Authored rigs (glTF/FBX) often bake a tiny uniform scale into every
        /// bone. Collider fields are in bone-local units, so any dimension
        /// specified in world metres must be divided by this factor.
        /// </summary>
        private static float MetresToLocal(Transform bone)
        {
            Vector3 s = bone.lossyScale;
            float uniform = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
            return uniform < 1e-6f ? 1f : uniform;
        }

        private static void AddSegmentCollider(Transform bone, Dictionary<RigSegment, Transform> bones, RigSegmentSpec spec)
        {
            float toLocal = 1f / MetresToLocal(bone);
            var capsule = bone.gameObject.AddComponent<CapsuleCollider>();
            capsule.radius = spec.capsuleRadius * toLocal;

            RigSegment? target = CapsuleTargetSegment(spec.segment);
            if (spec.segment == RigSegment.Pelvis)
            {
                capsule.height = (spec.capsuleLength + 2f * spec.capsuleRadius) * toLocal;
                capsule.direction = spec.capsuleDirection;
                capsule.center = Vector3.zero;
                return;
            }

            Vector3 localDir;
            float length; // bone-local units
            bool endsAtJoints = false;
            if (target.HasValue)
            {
                Vector3 toChild = bone.InverseTransformPoint(bones[target.Value].position);
                localDir = toChild.normalized;
                // Limbs use the measured bone distance (already local units);
                // the torso keeps its spec length (the head sits well past the
                // ribcage).
                bool isTrunk = spec.segment == RigSegment.Torso;
                length = isTrunk ? spec.capsuleLength * toLocal : toChild.magnitude;
                capsule.center = isTrunk ? localDir * (0.25f * toLocal) : localDir * (length * 0.5f);
                // Limb capsules end exactly at the joints. Overshooting by the
                // cap radius drove short shins to within centimetres of the
                // ground — any knee flex then tripped the fall sensor.
                endsAtJoints = !isTrunk;
            }
            else
            {
                // Head and gloves: extend away from the parent joint.
                Vector3 toParent = bone.InverseTransformPoint(bone.parent.position).normalized;
                localDir = -toParent;
                length = spec.capsuleLength * toLocal;
                float offset = spec.segment == RigSegment.Head ? 0.10f * toLocal : length * 0.5f;
                capsule.center = localDir * offset;
            }
            capsule.height = endsAtJoints ? length : length + 2f * spec.capsuleRadius * toLocal;
            capsule.direction = DominantAxis(localDir);
        }

        private static void AddFootCollider(Transform bone)
        {
            var box = bone.gameObject.AddComponent<BoxCollider>();

            Transform toe = null;
            foreach (Transform child in bone)
            {
                if (child.name.ToLowerInvariant().Contains("toe"))
                {
                    toe = child;
                    break;
                }
            }
            if (toe == null)
            {
                // Generated capsule biped: bone axes are identity.
                box.size = RigTool_Config.FootBoxSize;
                box.center = RigTool_Config.FootBoxCenter;
                return;
            }

            // Authored rig: ankle bones tilt toward the toe, and a BoxCollider
            // cannot rotate relative to its GameObject. Mount the box on a
            // world-aligned child so the sole is flat at build pose.
            Object.DestroyImmediate(box);
            Vector3 size = RigTool_Config.FootBoxSize; // x width, y height, z length
            Vector3 worldForward = toe.position - bone.position;
            worldForward.y = 0f;
            worldForward = worldForward.sqrMagnitude > 1e-6f ? worldForward.normalized : bone.forward;

            var sole = new GameObject(bone.name + "_Sole");
            sole.transform.SetParent(bone, false);
            sole.transform.rotation = Quaternion.LookRotation(worldForward, Vector3.up);

            // Anchor to the ANKLE, not the toe midpoint: 30% of the sole
            // behind the ankle (heel), 70% ahead. Toe-midpoint placement put
            // short-footed rigs' support patch ahead of their center of mass —
            // they tipped backward off the heel edge at zero action.
            Vector3 worldCenter = bone.position + worldForward * (size.z * 0.2f);
            worldCenter.y = size.y * 0.5f - 0.01f; // sole just below ground plane at build pose
            sole.transform.position = worldCenter;

            float toLocal = 1f / MetresToLocal(sole.transform);
            var soleBox = sole.AddComponent<BoxCollider>();
            soleBox.center = Vector3.zero;
            soleBox.size = size * toLocal;
        }

        private static RigJointEntry CreateJoint(Transform bone, Rigidbody body, Rigidbody parentBody, RigSegmentSpec spec)
        {
            var joint = bone.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = parentBody;
            joint.axis = Vector3.right;
            joint.secondaryAxis = Vector3.up;
            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
            joint.enablePreprocessing = false;

            if (spec.pitch.has)
            {
                joint.angularXMotion = ConfigurableJointMotion.Limited;
                joint.lowAngularXLimit = new SoftJointLimit { limit = spec.pitch.low };
                joint.highAngularXLimit = new SoftJointLimit { limit = spec.pitch.high };
            }
            else
            {
                joint.angularXMotion = ConfigurableJointMotion.Locked;
            }

            if (spec.roll.has)
            {
                joint.angularYMotion = ConfigurableJointMotion.Limited;
                joint.angularYLimit = new SoftJointLimit { limit = Mathf.Max(Mathf.Abs(spec.roll.low), Mathf.Abs(spec.roll.high)) };
            }
            else
            {
                joint.angularYMotion = ConfigurableJointMotion.Locked;
            }

            if (spec.yaw.has)
            {
                joint.angularZMotion = ConfigurableJointMotion.Limited;
                joint.angularZLimit = new SoftJointLimit { limit = Mathf.Max(Mathf.Abs(spec.yaw.low), Mathf.Abs(spec.yaw.high)) };
            }
            else
            {
                joint.angularZMotion = ConfigurableJointMotion.Locked;
            }

            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.slerpDrive = new JointDrive
            {
                positionSpring = spec.spring,
                positionDamper = spec.damper,
                maximumForce = spec.maxForce
            };

            return new RigJointEntry
            {
                joint = joint,
                body = body,
                hasPitch = spec.pitch.has,
                pitchLow = spec.pitch.low,
                pitchHigh = spec.pitch.high,
                hasRoll = spec.roll.has,
                rollLow = spec.roll.low,
                rollHigh = spec.roll.high,
                hasYaw = spec.yaw.has,
                yawLow = spec.yaw.low,
                yawHigh = spec.yaw.high,
                baseSpring = spec.spring,
                baseDamper = spec.damper,
                baseMaxForce = spec.maxForce
            };
        }

        private static void StripRig(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<Sensor_GroundContact>(true)) { Object.DestroyImmediate(component); }
            foreach (var component in root.GetComponentsInChildren<ConfigurableJoint>(true)) { Object.DestroyImmediate(component); }
            foreach (var component in root.GetComponentsInChildren<Collider>(true)) { Object.DestroyImmediate(component); }
            foreach (var component in root.GetComponentsInChildren<Rigidbody>(true)) { Object.DestroyImmediate(component); }
            var stale = new List<GameObject>();
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.EndsWith("_Sole")) { stale.Add(child.gameObject); }
            }
            for (int staleIndex = 0; staleIndex < stale.Count; staleIndex++) { Object.DestroyImmediate(stale[staleIndex]); }
            var rig = root.GetComponent<Systems_FighterRig>();
            if (rig != null)
            {
                Object.DestroyImmediate(rig);
            }
        }

        private static Dictionary<RigSegment, Transform> MapBones(GameObject root, out List<RigSegment> missing)
        {
            var map = new Dictionary<RigSegment, Transform>();
            var animator = root.GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman)
            {
                MapHumanoid(animator, map);
            }
            else
            {
                MapByName(root.transform, map);
            }

            missing = new List<RigSegment>();
            RigSegmentSpec[] specs = RigTool_Config.Specs;
            for (int specIndex = 0; specIndex < specs.Length; specIndex++)
            {
                if (!map.ContainsKey(specs[specIndex].segment))
                {
                    missing.Add(specs[specIndex].segment);
                }
            }
            return map;
        }

        private static void MapHumanoid(Animator animator, Dictionary<RigSegment, Transform> map)
        {
            AddIfFound(map, RigSegment.Pelvis, animator.GetBoneTransform(HumanBodyBones.Hips));
            Transform torso = animator.GetBoneTransform(HumanBodyBones.Chest);
            if (torso == null)
            {
                torso = animator.GetBoneTransform(HumanBodyBones.Spine);
            }
            AddIfFound(map, RigSegment.Torso, torso);
            AddIfFound(map, RigSegment.Head, animator.GetBoneTransform(HumanBodyBones.Head));
            AddIfFound(map, RigSegment.ThighL, animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg));
            AddIfFound(map, RigSegment.ShinL, animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg));
            AddIfFound(map, RigSegment.FootL, animator.GetBoneTransform(HumanBodyBones.LeftFoot));
            AddIfFound(map, RigSegment.ThighR, animator.GetBoneTransform(HumanBodyBones.RightUpperLeg));
            AddIfFound(map, RigSegment.ShinR, animator.GetBoneTransform(HumanBodyBones.RightLowerLeg));
            AddIfFound(map, RigSegment.FootR, animator.GetBoneTransform(HumanBodyBones.RightFoot));
            AddIfFound(map, RigSegment.UpperArmL, animator.GetBoneTransform(HumanBodyBones.LeftUpperArm));
            AddIfFound(map, RigSegment.ForearmL, animator.GetBoneTransform(HumanBodyBones.LeftLowerArm));
            AddIfFound(map, RigSegment.GloveL, animator.GetBoneTransform(HumanBodyBones.LeftHand));
            AddIfFound(map, RigSegment.UpperArmR, animator.GetBoneTransform(HumanBodyBones.RightUpperArm));
            AddIfFound(map, RigSegment.ForearmR, animator.GetBoneTransform(HumanBodyBones.RightLowerArm));
            AddIfFound(map, RigSegment.GloveR, animator.GetBoneTransform(HumanBodyBones.RightHand));
        }

        private static void AddIfFound(Dictionary<RigSegment, Transform> map, RigSegment segment, Transform bone)
        {
            if (bone != null)
            {
                map[segment] = bone;
            }
        }

        private static void MapByName(Transform root, Dictionary<RigSegment, Transform> map)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int boneIndex = 0; boneIndex < all.Length; boneIndex++)
            {
                Transform bone = all[boneIndex];
                if (bone.name.EndsWith("_vis"))
                {
                    continue;
                }
                string lowerName = bone.name.ToLowerInvariant();
                RigSegment? match = MatchSegment(lowerName);
                if (!match.HasValue)
                {
                    continue;
                }
                // Torso: depth-first order visits the spine root first, but the
                // chest (the spine bone carrying shoulders and neck) is deepest
                // — let later spine matches win. Head: a bone named exactly
                // "head" beats an earlier "neck" match.
                bool overwrite = match.Value == RigSegment.Torso
                    || (match.Value == RigSegment.Head && lowerName == "head");
                if (!map.ContainsKey(match.Value) || overwrite)
                {
                    map[match.Value] = bone;
                }
            }
        }

        private static RigSegment? MatchSegment(string lowerName)
        {
            bool left = lowerName.Contains("left") || lowerName.EndsWith("l") || lowerName.Contains("_l") || lowerName.Contains(".l");
            bool right = lowerName.Contains("right") || lowerName.EndsWith("r") || lowerName.Contains("_r") || lowerName.Contains(".r");

            if (lowerName.Contains("pelvis") || lowerName.Contains("hips") || lowerName == "hip")
            {
                return RigSegment.Pelvis;
            }
            // Specific before generic: forearm before arm, upperarm before arm.
            if (lowerName.Contains("forearm") || lowerName.Contains("lowerarm") || lowerName.Contains("elbow"))
            {
                return Sided(left, right, RigSegment.ForearmL, RigSegment.ForearmR);
            }
            if (lowerName.Contains("shoulder") || lowerName.Contains("armature"))
            {
                // Clavicles are not simulated; "armature" is a rig root, not a bone.
                return null;
            }
            if (lowerName.Contains("upperarm") || lowerName.Contains("uparm") || lowerName.Contains("arm"))
            {
                return Sided(left, right, RigSegment.UpperArmL, RigSegment.UpperArmR);
            }
            if (lowerName.Contains("glove") || lowerName.Contains("hand") || lowerName.Contains("wrist") || lowerName.Contains("fist"))
            {
                return Sided(left, right, RigSegment.GloveL, RigSegment.GloveR);
            }
            if (lowerName.Contains("thigh") || lowerName.Contains("upleg") || lowerName.Contains("upperleg"))
            {
                return Sided(left, right, RigSegment.ThighL, RigSegment.ThighR);
            }
            if (lowerName.Contains("shin") || lowerName.Contains("calf") || lowerName.Contains("lowerleg"))
            {
                return Sided(left, right, RigSegment.ShinL, RigSegment.ShinR);
            }
            // Mixamo-style "LeftLeg"/"RightLeg" is the shin ("UpLeg" was caught
            // by the thigh rule above).
            if (lowerName.Contains("leg"))
            {
                return Sided(left, right, RigSegment.ShinL, RigSegment.ShinR);
            }
            if (lowerName.Contains("foot") || lowerName.Contains("ankle"))
            {
                return Sided(left, right, RigSegment.FootL, RigSegment.FootR);
            }
            if (lowerName.Contains("spine") || lowerName.Contains("torso") || lowerName.Contains("chest"))
            {
                return RigSegment.Torso;
            }
            if (lowerName.Contains("head") || lowerName.Contains("neck"))
            {
                return RigSegment.Head;
            }
            return null;
        }

        private static RigSegment? Sided(bool left, bool right, RigSegment leftSegment, RigSegment rightSegment)
        {
            if (left && !right)
            {
                return leftSegment;
            }
            if (right)
            {
                return rightSegment;
            }
            return null;
        }

        private static PhysicsMaterial GetOrCreateHighFrictionMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(RigTool_Config.HIGH_FRICTION_MATERIAL_PATH);
            if (existing != null)
            {
                return existing;
            }
            if (!AssetDatabase.IsValidFolder(RigTool_Config.PHYSICS_FOLDER))
            {
                AssetDatabase.CreateFolder("Assets", "Physics");
            }
            var material = new PhysicsMaterial("PM_HighFriction")
            {
                staticFriction = 1f,
                dynamicFriction = 1f,
                bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Maximum,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
            AssetDatabase.CreateAsset(material, RigTool_Config.HIGH_FRICTION_MATERIAL_PATH);
            return material;
        }
    }
}
