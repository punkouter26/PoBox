using System.Collections.Generic;
using PoBox;
using UnityEditor;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// Builds the PhysX raptor fighter — the non-humanoid biped ported from
    /// the MuJoCo creature pipeline (see PoBox-MuJoCo/RAPTOR.md) — end to end:
    /// skeleton, colliders, ConfigurableJoints, sensors, rig, agent, prefab.
    ///
    /// One tool rather than the 1/2/3 humanoid chain because the generator and
    /// auto-rigger are keyed to the humanoid RigSegment enum, and the raptor's
    /// morphology (neck, two tail links, metatarsus, no arms) does not fit it.
    /// Everything downstream of the prefab — Systems_FighterRig, the agent,
    /// rewards, contest — is morphology-generic and used unchanged.
    ///
    /// The segment table is GENERATED from PoBox-MuJoCo/scripts/raptor_mjcf.py
    /// (the proven MuJoCo skeleton), mapped mj(x,y,z) -> unity(x,z,-y) so the
    /// raptor faces +Z. Bones carry identity rotations with the rest pose baked
    /// into positions — action 0 commands the digitigrade crouch it stands in,
    /// exactly as the MJCF does. MuJoCo roll/yaw swap slots here (unity Y is
    /// mj Z): the turn DOF lands in the roll slot and the lateral DOF in yaw.
    /// Both layouts are internally consistent; they are NOT interchangeable —
    /// a brain trained on one reads garbage on the other, at identical tensor
    /// shape (114 obs / 21 actions both). Never wire the MuJoCo raptor brain
    /// (raptor_balance.onnx) into this fighter.
    /// </summary>
    internal static class RigTool_RaptorRig
    {
        private const float TOTAL_MASS = 20f;
        private const string ROOT_NAME = "Fighter_Raptor";
        // Torque per radian of error. Scaled from the humanoid PRD table for a
        // 20 kg body with short limbs; damper and force cap keep the humanoid
        // table's ratios (damper ~8% of spring, cap 1.5x).
        private const float DAMPER_FRACTION = 0.08f;
        private const float MAX_FORCE_FACTOR = 1.5f;

        private readonly struct RaptorSegment
        {
            public readonly string name;
            public readonly string parent;
            public readonly Vector3 origin;      // rest-pose world position
            public readonly Vector3 end;         // capsule far end (child joint / tip)
            public readonly float radius;
            public readonly float mass;
            public readonly RigDofRange pitch;   // X — sagittal
            public readonly RigDofRange roll;    // Y — turn (mj yaw)
            public readonly RigDofRange yaw;     // Z — lateral (mj roll)
            public readonly float spring;

            public RaptorSegment(string name, string parent, Vector3 origin, Vector3 end,
                float radius, float mass, RigDofRange pitch, RigDofRange roll, RigDofRange yaw, float spring)
            {
                this.name = name;
                this.parent = parent;
                this.origin = origin;
                this.end = end;
                this.radius = radius;
                this.mass = mass;
                this.pitch = pitch;
                this.roll = roll;
                this.yaw = yaw;
                this.spring = spring;
            }
        }

        // Generated from raptor_mjcf.py — regenerate there, do not hand-tune.
        // (mj roll/yaw already swapped into unity slots; all swapped ranges
        // are symmetric so no negation was needed.)
        private static readonly RaptorSegment[] Segments =
        {
            new RaptorSegment("Pelvis", null, new Vector3(0f, 0.6628f, 0f), new Vector3(0f, 0.6628f, 0f),
                0.070f, 4.00f, RigDofRange.None, RigDofRange.None, RigDofRange.None, 0f),
            new RaptorSegment("Torso", "Pelvis", new Vector3(0f, 0.6628f, 0f), new Vector3(0f, 0.7628f, 0.3000f),
                0.085f, 6.00f, RigDofRange.Range(-25f, 25f), RigDofRange.Range(-30f, 30f), RigDofRange.None, 800f),
            new RaptorSegment("Neck", "Torso", new Vector3(0f, 0.7628f, 0.3000f), new Vector3(0f, 0.8928f, 0.4000f),
                0.040f, 0.80f, RigDofRange.Range(-40f, 30f), RigDofRange.Range(-45f, 45f), RigDofRange.None, 200f),
            new RaptorSegment("Head", "Neck", new Vector3(0f, 0.8928f, 0.4000f), new Vector3(0f, 0.9128f, 0.5600f),
                0.052f, 1.00f, RigDofRange.Range(-30f, 30f), RigDofRange.None, RigDofRange.None, 100f),
            new RaptorSegment("Tail01", "Pelvis", new Vector3(0f, 0.6628f, 0f), new Vector3(0f, 0.6928f, -0.2800f),
                0.048f, 1.60f, RigDofRange.Range(-35f, 35f), RigDofRange.Range(-40f, 40f), RigDofRange.None, 300f),
            new RaptorSegment("Tail02", "Tail01", new Vector3(0f, 0.6928f, -0.2800f), new Vector3(0f, 0.6728f, -0.5800f),
                0.032f, 1.00f, RigDofRange.Range(-35f, 35f), RigDofRange.Range(-40f, 40f), RigDofRange.None, 180f),
            new RaptorSegment("ThighL", "Pelvis", new Vector3(0.0900f, 0.6628f, 0f), new Vector3(0.0900f, 0.4377f, 0.1300f),
                0.048f, 1.60f, RigDofRange.Range(-60f, 35f), RigDofRange.Range(-20f, 20f), RigDofRange.Range(-20f, 20f), 900f),
            new RaptorSegment("ShinL", "ThighL", new Vector3(0.0900f, 0.4377f, 0.1300f), new Vector3(0.0900f, 0.2232f, -0.0500f),
                0.032f, 0.80f, RigDofRange.Range(-10f, 70f), RigDofRange.None, RigDofRange.None, 700f),
            new RaptorSegment("MetaL", "ShinL", new Vector3(0.0900f, 0.2232f, -0.0500f), new Vector3(0.0900f, 0.0300f, 0.0018f),
                0.024f, 0.30f, RigDofRange.Range(-55f, 35f), RigDofRange.None, RigDofRange.None, 500f),
            new RaptorSegment("FootL", "MetaL", new Vector3(0.0900f, 0.0300f, 0.0018f), new Vector3(0.0900f, 0.0300f, 0.1218f),
                0.030f, 0.10f, RigDofRange.Range(-30f, 45f), RigDofRange.None, RigDofRange.None, 250f),
            new RaptorSegment("ThighR", "Pelvis", new Vector3(-0.0900f, 0.6628f, 0f), new Vector3(-0.0900f, 0.4377f, 0.1300f),
                0.048f, 1.60f, RigDofRange.Range(-60f, 35f), RigDofRange.Range(-20f, 20f), RigDofRange.Range(-20f, 20f), 900f),
            new RaptorSegment("ShinR", "ThighR", new Vector3(-0.0900f, 0.4377f, 0.1300f), new Vector3(-0.0900f, 0.2232f, -0.0500f),
                0.032f, 0.80f, RigDofRange.Range(-10f, 70f), RigDofRange.None, RigDofRange.None, 700f),
            new RaptorSegment("MetaR", "ShinR", new Vector3(-0.0900f, 0.2232f, -0.0500f), new Vector3(-0.0900f, 0.0300f, 0.0018f),
                0.024f, 0.30f, RigDofRange.Range(-55f, 35f), RigDofRange.None, RigDofRange.None, 500f),
            new RaptorSegment("FootR", "MetaR", new Vector3(-0.0900f, 0.0300f, 0.0018f), new Vector3(-0.0900f, 0.0300f, 0.1218f),
                0.030f, 0.10f, RigDofRange.Range(-30f, 45f), RigDofRange.None, RigDofRange.None, 250f),
        };

        [MenuItem("Tools/ML Boxing/10. Build Raptor Fighter")]
        public static void BuildMenu()
        {
            GameObject prefab = Build();
            if (prefab != null)
            {
                Selection.activeObject = prefab;
            }
        }

        public static GameObject Build()
        {
            var existing = GameObject.Find(ROOT_NAME);
            if (existing != null)
            {
                Object.DestroyImmediate(existing);
            }

            float massSum = 0f;
            for (int i = 0; i < Segments.Length; i++)
            {
                massSum += Segments[i].mass;
            }
            float massScale = TOTAL_MASS / massSum;

            var root = new GameObject(ROOT_NAME);
            var bones = new Dictionary<string, Transform>();
            var bodies = new Dictionary<string, Rigidbody>();
            var bodyMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/MuJoCoCreature/Materials/M_Raptor.mat");
            var highFriction = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(
                RigTool_Config.HIGH_FRICTION_MATERIAL_PATH);

            // Pass 1: bones, rigidbodies, colliders, visuals.
            for (int i = 0; i < Segments.Length; i++)
            {
                RaptorSegment seg = Segments[i];
                var bone = new GameObject(seg.name);
                Transform parent = seg.parent == null ? root.transform : bones[seg.parent];
                bone.transform.SetParent(parent, false);
                Vector3 parentOrigin = seg.parent == null ? Vector3.zero : FindSegment(seg.parent).origin;
                bone.transform.localPosition = seg.origin - parentOrigin;
                bones[seg.name] = bone.transform;

                var body = bone.AddComponent<Rigidbody>();
                body.mass = seg.mass * massScale;
                body.solverIterations = 16;
                body.solverVelocityIterations = 16;
                body.collisionDetectionMode = seg.name == "Head"
                    ? CollisionDetectionMode.ContinuousDynamic
                    : CollisionDetectionMode.Discrete;
                bodies[seg.name] = body;

                AddCapsule(bone.transform, seg, bodyMaterial,
                    isFoot: seg.name.StartsWith("Foot"), highFriction);
            }

            // Pass 2: joints, in table order — this order IS the action mapping.
            var entries = new List<RigJointEntry>();
            for (int i = 0; i < Segments.Length; i++)
            {
                RaptorSegment seg = Segments[i];
                if (seg.parent == null)
                {
                    continue;
                }
                entries.Add(CreateJoint(bones[seg.name], bodies[seg.name], bodies[seg.parent], seg));
            }

            var footLeftSensor = bones["FootL"].gameObject.AddComponent<Sensor_GroundContact>();
            var footRightSensor = bones["FootR"].gameObject.AddComponent<Sensor_GroundContact>();

            var rig = root.AddComponent<Systems_FighterRig>();
            // No gloves: the raptor has no arms. Everything glove-adjacent is
            // boxing-phase machinery and null-guarded on the balance paths.
            rig.EditorInitialize(bodies["Pelvis"], bodies["Torso"], bones["Head"],
                gloveLeft: null, gloveRight: null, footLeftSensor, footRightSensor, entries);

            // Balance phase, foot-height generation (13 joints -> 114 obs) —
            // set BEFORE Prepare so it sizes BrainParameters from these flags.
            var agent = root.AddComponent<Agent_FighterBoxing>();
            var agentSo = new SerializedObject(agent);
            agentSo.FindProperty("_observeOpponent").boolValue = false;
            agentSo.FindProperty("_observeFootHeight").boolValue = true;
            agentSo.ApplyModifiedPropertiesWithoutUndo();

            GameObject prefab = RigTool_PrepareForTraining.Prepare(root);
            Debug.Log($"RigTool: raptor built — {Segments.Length} bodies, {entries.Count} joints, " +
                      $"{rig.DofCount} action DOF, {TOTAL_MASS} kg. " +
                      "Next: Tools > ML Boxing > 5d. Create Raptor Balance Scene.");
            return prefab;
        }

        private static RaptorSegment FindSegment(string name)
        {
            for (int i = 0; i < Segments.Length; i++)
            {
                if (Segments[i].name == name)
                {
                    return Segments[i];
                }
            }
            return default;
        }

        /// <summary>
        /// Collider + visual on a child aligned to the bone->end axis. The
        /// humanoid auto-rigger snaps diagonal limbs to the dominant axis,
        /// which its near-vertical limbs tolerate; the raptor's thigh and shin
        /// sit 30–40° off vertical, so the capsule is mounted on a rotated
        /// child instead (the same trick its foot soles use).
        /// </summary>
        private static void AddCapsule(Transform bone, RaptorSegment seg, Material material,
            bool isFoot, PhysicsMaterial highFriction)
        {
            Vector3 dir = seg.end - seg.origin;
            bool isPelvis = seg.parent == null;
            if (isPelvis)
            {
                // Short capsule along the spine axis, matching the MJCF pelvis.
                dir = new Vector3(0f, 0.02f, 0.16f);
            }
            float length = dir.magnitude;
            Vector3 localDir = dir.normalized;

            var holder = new GameObject(seg.name + "_col");
            holder.transform.SetParent(bone, false);
            holder.transform.localRotation = Quaternion.FromToRotation(Vector3.up, localDir);
            holder.transform.localPosition = isPelvis ? new Vector3(0f, 0f, -0.06f) + dir * 0.5f : dir * 0.5f;

            var capsule = holder.AddComponent<CapsuleCollider>();
            capsule.direction = 1; // Y of the aligned holder
            capsule.radius = seg.radius;
            // Leg segments end exactly at the joints (auto-rigger lesson: a
            // cap-radius overshoot on short shins trips ground sensors); the
            // trunk/head/tail tips keep their rounded ends.
            bool endsAtJoints = seg.name.StartsWith("Thigh") || seg.name.StartsWith("Shin")
                || seg.name.StartsWith("Meta");
            capsule.height = endsAtJoints ? length : length + 2f * seg.radius;
            capsule.center = Vector3.zero;
            if (isFoot && highFriction != null)
            {
                capsule.sharedMaterial = highFriction;
            }

            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = seg.name + "_vis";
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.transform.SetParent(holder.transform, false);
            visual.transform.localScale = new Vector3(seg.radius * 2f, (length + 2f * seg.radius) * 0.5f, seg.radius * 2f);
            if (material != null)
            {
                visual.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
        }

        private static RigJointEntry CreateJoint(Transform bone, Rigidbody body, Rigidbody parentBody, RaptorSegment seg)
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

            if (seg.pitch.has)
            {
                joint.angularXMotion = ConfigurableJointMotion.Limited;
                joint.lowAngularXLimit = new SoftJointLimit { limit = seg.pitch.low };
                joint.highAngularXLimit = new SoftJointLimit { limit = seg.pitch.high };
            }
            else
            {
                joint.angularXMotion = ConfigurableJointMotion.Locked;
            }
            if (seg.roll.has)
            {
                joint.angularYMotion = ConfigurableJointMotion.Limited;
                joint.angularYLimit = new SoftJointLimit
                {
                    limit = Mathf.Max(Mathf.Abs(seg.roll.low), Mathf.Abs(seg.roll.high))
                };
            }
            else
            {
                joint.angularYMotion = ConfigurableJointMotion.Locked;
            }
            if (seg.yaw.has)
            {
                joint.angularZMotion = ConfigurableJointMotion.Limited;
                joint.angularZLimit = new SoftJointLimit
                {
                    limit = Mathf.Max(Mathf.Abs(seg.yaw.low), Mathf.Abs(seg.yaw.high))
                };
            }
            else
            {
                joint.angularZMotion = ConfigurableJointMotion.Locked;
            }

            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.slerpDrive = new JointDrive
            {
                positionSpring = seg.spring,
                positionDamper = seg.spring * DAMPER_FRACTION,
                maximumForce = seg.spring * MAX_FORCE_FACTOR
            };

            return new RigJointEntry
            {
                joint = joint,
                body = body,
                hasPitch = seg.pitch.has,
                pitchLow = seg.pitch.low,
                pitchHigh = seg.pitch.high,
                hasRoll = seg.roll.has,
                rollLow = seg.roll.low,
                rollHigh = seg.roll.high,
                hasYaw = seg.yaw.has,
                yawLow = seg.yaw.low,
                yawHigh = seg.yaw.high,
                baseSpring = seg.spring,
                baseDamper = seg.spring * DAMPER_FRACTION,
                baseMaxForce = seg.spring * MAX_FORCE_FACTOR
            };
        }
    }
}
