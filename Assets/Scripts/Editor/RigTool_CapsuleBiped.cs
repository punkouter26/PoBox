using UnityEditor;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// Generates the v1 capsule-biped fighter skeleton: a bone hierarchy with
    /// primitive capsule visuals, named so the auto-rigger's fallback matcher
    /// resolves every segment. No physics components — run Auto-Rig next.
    /// </summary>
    internal static class RigTool_CapsuleBiped
    {
        [MenuItem("Tools/ML Boxing/1. Generate Capsule Biped")]
        public static void Generate()
        {
            GameObject root = Build("Fighter_Capsule");
            Selection.activeGameObject = root;
            Undo.RegisterCreatedObjectUndo(root, "Generate Capsule Biped");
            Debug.Log("RigTool: capsule biped generated. Next: Tools > ML Boxing > 2. Auto-Rig Selected.");
        }

        public static GameObject Build(string rootName)
        {
            var root = new GameObject(rootName);
            RigSegmentSpec[] specs = RigTool_Config.Specs;
            var boneBySegment = new System.Collections.Generic.Dictionary<RigSegment, Transform>();

            for (int specIndex = 0; specIndex < specs.Length; specIndex++)
            {
                RigSegmentSpec spec = specs[specIndex];
                var bone = new GameObject(spec.segment.ToString());
                Transform parent = spec.segment == RigSegment.Pelvis
                    ? root.transform
                    : boneBySegment[spec.parent];
                bone.transform.SetParent(parent, false);
                bone.transform.localPosition = spec.localPosition;
                boneBySegment[spec.segment] = bone.transform;
                AddVisual(bone.transform, spec);
            }
            return root;
        }

        private static void AddVisual(Transform bone, RigSegmentSpec spec)
        {
            bool isFoot = spec.segment is RigSegment.FootL or RigSegment.FootR;
            GameObject visual = GameObject.CreatePrimitive(isFoot ? PrimitiveType.Cube : PrimitiveType.Capsule);
            visual.name = bone.name + "_vis";
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.transform.SetParent(bone, false);

            if (isFoot)
            {
                visual.transform.localScale = RigTool_Config.FootBoxSize;
                visual.transform.localPosition = RigTool_Config.FootBoxCenter;
                return;
            }

            float totalLength = spec.capsuleLength + 2f * spec.capsuleRadius;
            visual.transform.localScale = new Vector3(spec.capsuleRadius * 2f, totalLength * 0.5f, spec.capsuleRadius * 2f);

            Vector3 center = ColliderCenter(spec);
            visual.transform.localPosition = center;
            visual.transform.localRotation = spec.capsuleDirection switch
            {
                0 => Quaternion.Euler(0f, 0f, 90f),
                2 => Quaternion.Euler(90f, 0f, 0f),
                _ => Quaternion.identity
            };
        }

        /// <summary>Collider/visual center shared with the auto-rigger.</summary>
        public static Vector3 ColliderCenter(RigSegmentSpec spec)
        {
            switch (spec.segment)
            {
                case RigSegment.Pelvis:
                    return Vector3.zero;
                case RigSegment.Torso:
                    return new Vector3(0f, 0.25f, 0f);
                case RigSegment.Head:
                    return new Vector3(0f, 0.10f, 0f);
                case RigSegment.FootL:
                case RigSegment.FootR:
                    return new Vector3(0f, -0.02f, 0.05f);
                default:
                    return new Vector3(0f, -spec.capsuleLength * 0.5f, 0f);
            }
        }
    }
}
