using System;
using System.Collections.Generic;
using UnityEngine;

namespace PoBox.Rl
{
    /// <summary>
    /// Puts the owner's skinned mesh on a physics rig: every frame, each mapped bone of the mesh's skeleton
    /// is placed where its physics link is. After PoDecath's binder, with one addition.
    ///
    /// The addition is the first step of <see cref="Bind"/>. A binder that simply remembers each bone's
    /// offset from its link assumes the mesh and the rig start in the same pose. The rig always starts with
    /// arms straight out and legs straight down; the mesh starts however its author left it (the zombie
    /// rests with its arms by its sides and its feet apart). So before any offset is taken, each limb bone
    /// of the mesh is turned to point the way the rig's link points. Unmapped bones (fingers, neck, head,
    /// toes) follow their parents.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class SkinBinder : MonoBehaviour
    {
        [Serializable]
        public struct BoneMap { public string body; public string bone; }

        /// <summary>Turn <c>bone</c> so that it points at <c>towardBone</c> the way <c>body</c> points at <c>towardBody</c>.</summary>
        [Serializable]
        public struct Aim { public string bone, towardBone, body, towardBody; }

        [Tooltip("The object the physics links live under.")]
        public Transform rigRoot;
        public GameObject skin;
        [Tooltip("Physics body name -> skeleton bone name.")]
        public List<BoneMap> map = new List<BoneMap>();
        [Tooltip("Limb bones to straighten onto the rig before binding, parents before children.")]
        public List<Aim> aims = new List<Aim>();
        [Tooltip("Turns the mesh, which faces +Z as imported, to face the rig's forward (+X). Only used if the skeleton cannot say which way it stands.")]
        public Vector3 skinRootEuler = new Vector3(0f, 90f, 0f);
        [Tooltip("Uniform scale for the mesh. 0 = size it to the rig's legs.")]
        public float skinScale = 0f;

        struct Link { public Transform body, bone; public Quaternion rotOffset; public Vector3 posOffset; }
        readonly List<Link> _links = new List<Link>();
        public bool IsBound => _links.Count > 0;
        public float FittedScale { get; private set; } = 1f;

        // Bound in Awake, before anything has moved: the rig is then still in the pose it was built in.
        void Awake()
        {
            if (skin != null && rigRoot != null && !IsBound) Bind();
        }

        public void Bind()
        {
            _links.Clear();
            if (rigRoot == null || skin == null) return;

            skin.transform.SetParent(rigRoot, false);
            skin.transform.localPosition = Vector3.zero;
            skin.transform.localRotation = Quaternion.Euler(skinRootEuler);
            skin.transform.localScale = Vector3.one;
            // An idle clip would pose the same bones this drives; the physics is the only thing allowed to.
            foreach (Animator animator in skin.GetComponentsInChildren<Animator>(true)) animator.enabled = false;

            var bodies = new Dictionary<string, Transform>();
            foreach (Transform t in rigRoot.GetComponentsInChildren<Transform>(true))
            {
                if (t.IsChildOf(skin.transform)) continue;
                var tag = t.GetComponent<MjcfBodyTag>();
                string key = tag != null ? tag.bodyName : t.name;
                if (!bodies.ContainsKey(key)) bodies[key] = t;
                if (!bodies.ContainsKey(t.name)) bodies[t.name] = t;
                // A body of the MuJoCo plugin carries its corner in its name (a_thigh_l); the maps do not.
                if (t.name.Length > 2 && t.name[1] == '_' && !bodies.ContainsKey(t.name.Substring(2))) bodies[t.name.Substring(2)] = t;
            }
            var bones = new Dictionary<string, Transform>();
            foreach (Transform t in skin.GetComponentsInChildren<Transform>(true))
                if (!bones.ContainsKey(t.name)) bones[t.name] = t;

            var pairs = new List<(Transform body, Transform bone)>();
            foreach (BoneMap m in map)
            {
                if (!bodies.TryGetValue(m.body, out Transform body) || !bones.TryGetValue(m.bone, out Transform bone))
                {
                    Debug.LogWarning($"[SkinBinder] could not bind body '{m.body}' to bone '{m.bone}' on {name}.", this);
                    continue;
                }
                pairs.Add((body, bone));
            }
            if (pairs.Count == 0) return;

            // 0. Stand the mesh the way the rig stands. Which way a file calls up and which way its figure
            // faces is its exporter's business (the zombie, by way of FBX and Blender, arrives lying on its
            // back), so it is read off the skeleton: up is hips to shoulders, left is right limb to left limb.
            // Snapped to whole axes, because that is all an exporter's convention ever is, and anything
            // finer would be this figure's slouch, which is not to be straightened out of it.
            var boneOf = new Dictionary<string, Transform>();
            foreach (BoneMap m in map)
                if (bones.TryGetValue(m.bone, out Transform mapped)) boneOf[m.body] = mapped;
            if (boneOf.TryGetValue("thigh_l", out Transform thighL) && boneOf.TryGetValue("thigh_r", out Transform thighR) &&
                boneOf.TryGetValue("upper_arm_l", out Transform armL) && boneOf.TryGetValue("upper_arm_r", out Transform armR))
            {
                skin.transform.localRotation = Quaternion.identity;
                Transform root = skin.transform;
                Vector3 up = Snap(root.InverseTransformDirection(0.5f * (armL.position + armR.position) - 0.5f * (thighL.position + thighR.position)));
                Vector3 left = Snap(root.InverseTransformDirection(thighL.position - thighR.position + armL.position - armR.position));
                // The rig's up is +Y and, looking along +X, its left is +Z.
                if (Mathf.Abs(Vector3.Dot(up, left)) < 0.5f) root.localRotation = Quaternion.Inverse(Quaternion.LookRotation(left, up));
                else root.localRotation = Quaternion.Euler(skinRootEuler);
            }

            // 1. Straighten the mesh's limbs onto the rig's.
            foreach (Aim a in aims)
            {
                if (!bones.TryGetValue(a.bone, out Transform bone) || !bones.TryGetValue(a.towardBone, out Transform toBone)) continue;
                if (!bodies.TryGetValue(a.body, out Transform body) || !bodies.TryGetValue(a.towardBody, out Transform toBody)) continue;
                Vector3 has = toBone.position - bone.position, wants = toBody.position - body.position;
                if (has.sqrMagnitude < 1e-8f || wants.sqrMagnitude < 1e-8f) continue;
                bone.rotation = Quaternion.FromToRotation(has, wants) * bone.rotation;
            }

            // 2. Size it. Limb lengths do not depend on the pose, so the aimed bones are what is measured.
            float s = skinScale;
            if (s <= 0f)
            {
                float rig = 0f, mesh = 0f;
                foreach (Aim a in aims)
                {
                    if (!bones.TryGetValue(a.bone, out Transform bone) || !bones.TryGetValue(a.towardBone, out Transform toBone)) continue;
                    if (!bodies.TryGetValue(a.body, out Transform body) || !bodies.TryGetValue(a.towardBody, out Transform toBody)) continue;
                    rig += Vector3.Distance(body.position, toBody.position);
                    mesh += Vector3.Distance(bone.position, toBone.position);
                }
                s = mesh > 1e-4f ? Mathf.Clamp(rig / mesh, 0.01f, 200f) : 1f;
            }
            FittedScale = s;
            skin.transform.localScale = Vector3.one * s;

            // 3. Remember where each bone sits relative to its link.
            foreach ((Transform body, Transform bone) in pairs)
            {
                Quaternion inv = Quaternion.Inverse(body.rotation);
                // Only the rotation offset is kept from the mesh; the bone sits on the link's own origin.
                // The joints are the same joints, and a position offset taken across two differently
                // proportioned rest poses would stretch the limb.
                _links.Add(new Link { body = body, bone = bone, rotOffset = inv * bone.rotation, posOffset = Vector3.zero });
            }

            // 4. Everything above the mapped bones rides on the pelvis. Some skeletons hang the pelvis and
            // the spine side by side under a hip bone that nothing here maps (Character Creator's do); left
            // where it was, that bone holds its share of the belly at the spot the fighter was built on while
            // the rest of the body walks away, and the mesh stretches across the ring like toffee.
            _carrier = pairs[0].body;
            Quaternion carrierInv = Quaternion.Inverse(_carrier.rotation);
            _rootRot = carrierInv * skin.transform.rotation;
            _rootPos = carrierInv * (skin.transform.position - pairs[0].bone.position);

            foreach (SkinnedMeshRenderer smr in skin.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.updateWhenOffscreen = true;
        }

        static Vector3 Snap(Vector3 v)
        {
            float x = Mathf.Abs(v.x), y = Mathf.Abs(v.y), z = Mathf.Abs(v.z);
            if (x >= y && x >= z) return new Vector3(Mathf.Sign(v.x), 0f, 0f);
            return y >= z ? new Vector3(0f, Mathf.Sign(v.y), 0f) : new Vector3(0f, 0f, Mathf.Sign(v.z));
        }

        Transform _carrier;
        Quaternion _rootRot;
        Vector3 _rootPos;

        void LateUpdate()
        {
            if (_carrier != null && skin != null)
                skin.transform.SetPositionAndRotation(_carrier.position + _carrier.rotation * _rootPos, _carrier.rotation * _rootRot);
            for (int i = 0; i < _links.Count; i++)
            {
                Link l = _links[i];
                if (l.body == null || l.bone == null) continue;
                l.bone.SetPositionAndRotation(l.body.position + l.body.rotation * l.posOffset, l.body.rotation * l.rotOffset);
            }
        }
    }
}
