using UnityEngine;

namespace PoBox.Rl
{
    /// <summary>
    /// The trained fighter's body in Unity: the same links, joints, limits, gains and guard pose as the
    /// MuJoCo model it was trained on, built from that model's file by <see cref="MjcfFighterImporter"/>.
    ///
    /// This is the adapter the policy talks through. It reports joint angles and speeds in the trainer's
    /// sign convention, takes joint targets in it, and knows where the handful of things the observation
    /// needs are (head, body, gloves, feet). The rig's forward is its own +X, as in the trainer.
    /// </summary>
    public class MjcfRig : MonoBehaviour
    {
        public string fighterName = "fighter";
        public ArticulationBody root;
        [Tooltip("One per policy joint, in the trainer's order.")]
        public ArticulationBody[] joints = new ArticulationBody[0];
        public string[] jointNames = new string[0];
        [Tooltip("Guard pose, radians, trainer's sign.")]
        public float[] defaultPos = new float[0];
        public float[] lower = new float[0];
        public float[] upper = new float[0];
        [Tooltip("Trainer angle = sign x Unity angle. -1 throughout: the frames are mirror images.")]
        public float[] sign = new float[0];

        [Header("From the policy config")]
        public float standHeight = 0.95f;
        public float actionScale = 0.5f;
        public float actionClip = 3f;
        public int controlDecimation = 4;
        public float physicsStep = 0.005f;
        public float totalMass = 78f;

        [Header("What the observation looks at")]
        public Transform headGeom;
        public Transform torsoGeom;
        public Transform gloveL;
        public Transform gloveR;
        public BoxCollider footL;
        public BoxCollider footR;
        public float headRadius = 0.1f;
        public float gloveRadius = 0.075f;

        /// <summary>Set by <see cref="MujocoRing"/> when this body is the shadow of one simulated in MuJoCo.</summary>
        [System.NonSerialized] public MujocoRing ring;
        [System.NonSerialized] public int slot;
        public bool Shadowed => ring != null && ring.Ready;

        System.Collections.Generic.Dictionary<ArticulationBody, int> _index;

        /// <summary>Which policy joint a hinge link is, or -1.</summary>
        public int IndexOf(ArticulationBody hinge)
        {
            if (_index == null)
            {
                _index = new System.Collections.Generic.Dictionary<ArticulationBody, int>();
                for (int i = 0; i < joints.Length; i++) _index[joints[i]] = i;
            }
            return hinge != null && _index.TryGetValue(hinge, out int j) ? j : -1;
        }

        public Vector3 Forward => root.transform.rotation * Vector3.right;

        /// <summary>Heading in the trainer's frame: the angle of the body's forward axis about the vertical.</summary>
        public float Yaw
        {
            get
            {
                Vector3 f = Forward;
                return Mathf.Atan2(f.z, f.x);
            }
        }

        public void ReadJointState(float[] pos, float[] vel)
        {
            if (Shadowed) { ring.CopyJointState(slot, pos, vel); return; }
            for (int i = 0; i < joints.Length; i++)
            {
                ArticulationBody ab = joints[i];
                pos[i] = sign[i] * ab.jointPosition[0];
                vel[i] = sign[i] * ab.jointVelocity[0];
            }
        }

        /// <summary>PD position targets, radians, trainer's sign, clamped to the joint ranges as the trainer clamps them.</summary>
        public int ApplyTargets(float[] targets)
        {
            if (Shadowed) return ring.SetTargets(slot, targets);
            int clamped = 0;
            for (int i = 0; i < joints.Length; i++)
            {
                float t = targets[i];
                if (t < lower[i] || t > upper[i]) { clamped++; t = Mathf.Clamp(t, lower[i], upper[i]); }
                joints[i].SetDriveTarget(ArticulationDriveAxis.X, sign[i] * t * Mathf.Rad2Deg);
            }
            return clamped;
        }

        public void HoldGuard()
        {
            if (Shadowed) { ring.SetTargets(slot, defaultPos); return; }
            for (int i = 0; i < joints.Length; i++)
                joints[i].SetDriveTarget(ArticulationDriveAxis.X, sign[i] * defaultPos[i] * Mathf.Rad2Deg);
        }

        /// <summary>
        /// Stands the fighter in its guard at a spot on the floor, facing a way, with nothing moving.
        /// <paramref name="facing"/> is the direction the fighter should look, flattened to the floor.
        /// </summary>
        public void ResetPose(Vector3 floorPoint, Vector3 facing)
        {
            if (root == null) return;
            facing.y = 0f;
            if (facing.sqrMagnitude < 1e-6f) facing = Vector3.forward;
            if (Shadowed) { ring.ResetFighter(slot, floorPoint, facing.normalized); return; }
            // The rig looks along its own +X, so this is a turn about the vertical and nothing else. Not
            // FromToRotation: asked to turn +X into exactly -X it is free to pick any axis, and it picks one
            // that stands the fighter on its head.
            Quaternion rotation = Quaternion.Euler(0f, -Mathf.Atan2(facing.z, facing.x) * Mathf.Rad2Deg, 0f);
            root.TeleportRoot(floorPoint + Vector3.up * (standHeight + 0.002f), rotation);
            root.linearVelocity = Vector3.zero;
            root.angularVelocity = Vector3.zero;

            for (int i = 0; i < joints.Length; i++)
            {
                ArticulationBody ab = joints[i];
                float unityRad = sign[i] * defaultPos[i];
                ab.jointPosition = new ArticulationReducedSpace(unityRad);
                ab.jointVelocity = new ArticulationReducedSpace(0f);
                ab.jointForce = new ArticulationReducedSpace(0f);
                ab.SetDriveTarget(ArticulationDriveAxis.X, unityRad * Mathf.Rad2Deg);
            }
        }

        /// <summary>Height of the lowest corner of a foot above a floor level: what the trainer calls contact.</summary>
        public static float SoleClearance(BoxCollider foot, float floorY)
        {
            Transform t = foot.transform;
            Vector3 centre = t.TransformPoint(foot.center);
            Vector3 half = Vector3.Scale(foot.size, t.lossyScale) * 0.5f;
            float drop = Mathf.Abs(t.right.y) * half.x + Mathf.Abs(t.up.y) * half.y + Mathf.Abs(t.forward.y) * half.z;
            return centre.y - drop - floorY;
        }
    }
}
