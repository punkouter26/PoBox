// Drives a skinned character's bones from the MuJoCo ragdoll simulating it.
//
// MuJoCo does not know about the skin. The MJCF carries collision capsules and
// inertias only, so the physics runs on 15 rigid bodies while the visible mesh
// is an ordinary SkinnedMeshRenderer with a 24-bone armature. This component is
// the join: each frame it copies the world transform of an MjBody onto the
// matching bone.
//
// Only bones that HAVE a MuJoCo body are driven. The rig's spine chain,
// clavicles and toes were collapsed away when the 24-bone armature was reduced
// to the 14-joint physics model, so those bones simply follow their parent --
// which is what makes the mesh deform sensibly rather than tear.
//
// Runs in LateUpdate, after MjScene has stepped and written its transforms.

using System;
using System.Collections.Generic;
using Mujoco;
using UnityEngine;

namespace PoBox.MuJoCoCreature
{
    [DefaultExecutionOrder(200)] // after MjScene has published its transforms
    public sealed class SkinnedRigBinder : MonoBehaviour
    {
        [Serializable]
        public sealed class BoneLink
        {
            public Transform bone;
            public MjBody body;
            [Tooltip("Rotation offset from the physics body to the bone's bind pose.")]
            public Quaternion boneLocalOffset = Quaternion.identity;
        }

        // glTF bone name -> MuJoCo body name. Bones absent from this table are
        // left alone and inherit their parent's motion.
        private static readonly (string bone, string body)[] DefaultMap =
        {
            ("Hips", "Pelvis"),
            ("Spine", "Torso"),
            ("Head", "Head"),
            ("LeftUpLeg", "ThighL"), ("LeftLeg", "ShinL"), ("LeftFoot", "FootL"),
            ("RightUpLeg", "ThighR"), ("RightLeg", "ShinR"), ("RightFoot", "FootR"),
            ("LeftArm", "UpperArmL"), ("LeftForeArm", "ForearmL"), ("LeftHand", "GloveL"),
            ("RightArm", "UpperArmR"), ("RightForeArm", "ForearmR"), ("RightHand", "GloveR"),
        };

        [SerializeField] private Transform _armatureRoot;
        [SerializeField] private List<BoneLink> _links = new List<BoneLink>();
        [Tooltip("Drive bone position as well as rotation. Off keeps the rig's own bone lengths, which avoids stretching the skin.")]
        [SerializeField] private bool _drivePositions = false;

        public int LinkCount => _links.Count;

        /// <summary>Builds the bone→body links by name. Editor-time setup.</summary>
        public int Rebind(Transform armatureRoot)
        {
            _armatureRoot = armatureRoot;
            _links.Clear();

            var bones = new Dictionary<string, Transform>();
            foreach (var t in armatureRoot.GetComponentsInChildren<Transform>(true))
            {
                bones[t.name] = t;
            }

            var bodies = new Dictionary<string, MjBody>();
            foreach (var b in FindObjectsByType<MjBody>(FindObjectsInactive.Include))
            {
                // Unity's MJCF exporter suffixes names (Torso -> Torso_4).
                var stem = System.Text.RegularExpressions.Regex.Replace(b.name, "_\\d+$", "");
                if (!bodies.ContainsKey(stem))
                {
                    bodies[stem] = b;
                }
            }

            foreach (var (boneName, bodyName) in DefaultMap)
            {
                if (!bones.TryGetValue(boneName, out var bone) ||
                    !bodies.TryGetValue(bodyName, out var body))
                {
                    continue;
                }
                _links.Add(new BoneLink
                {
                    bone = bone,
                    body = body,
                    // Captured in the bind pose: the physics capsule and the
                    // art bone rarely share an axis convention, and baking the
                    // difference once is cheaper and steadier than guessing a
                    // global correction.
                    boneLocalOffset = Quaternion.Inverse(body.transform.rotation) * bone.rotation,
                });
            }
            return _links.Count;
        }

        private void LateUpdate()
        {
            for (int i = 0; i < _links.Count; i++)
            {
                var link = _links[i];
                if (link.bone == null || link.body == null)
                {
                    continue;
                }
                link.bone.rotation = link.body.transform.rotation * link.boneLocalOffset;
                if (_drivePositions)
                {
                    link.bone.position = link.body.transform.position;
                }
            }
            // The hips carry the whole character, so its position is driven
            // regardless — otherwise the mesh animates in place while the
            // physics body walks away.
            if (_links.Count > 0 && _links[0].bone != null && _links[0].body != null)
            {
                _links[0].bone.position = _links[0].body.transform.position;
            }
        }
    }
}
