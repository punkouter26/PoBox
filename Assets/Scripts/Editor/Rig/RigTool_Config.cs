using System.Collections.Generic;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// The biomechanical table from the PRD (75 kg baseline). Mass percents
    /// are normalized at build time so the total always hits the target mass.
    /// </summary>
    internal static class RigTool_Config
    {
        public const float DEFAULT_TOTAL_MASS = 75f;
        public const string PREFAB_FOLDER = "Assets/Prefabs/Fighters";
        public const string PHYSICS_FOLDER = "Assets/Physics";
        public const string HIGH_FRICTION_MATERIAL_PATH = "Assets/Physics/PM_HighFriction.physicMaterial";

        // Feet are flat boxes, not capsules: a rounded sole rolls under load,
        // which makes standing needlessly hard to learn. Bottom face sits at
        // y = -0.06 — identical to the old capsule bottom, so leg geometry and
        // spawn heights are unchanged. Size is a realistic shoe footprint.
        public static readonly Vector3 FootBoxSize = new(0.18f, 0.08f, 0.34f);
        public static readonly Vector3 FootBoxCenter = new(0f, -0.02f, 0.06f);

        private static RigSegmentSpec[] _specs;

        public static RigSegmentSpec[] Specs => _specs ??= Build();

        public static RigSegmentSpec GetSpec(RigSegment segment)
        {
            RigSegmentSpec[] specs = Specs;
            for (int specIndex = 0; specIndex < specs.Length; specIndex++)
            {
                if (specs[specIndex].segment == segment)
                {
                    return specs[specIndex];
                }
            }
            return default;
        }

        public static float TotalMassPercent()
        {
            float total = 0f;
            RigSegmentSpec[] specs = Specs;
            for (int specIndex = 0; specIndex < specs.Length; specIndex++)
            {
                total += specs[specIndex].massPercent;
            }
            return total;
        }

        private static RigSegmentSpec[] Build()
        {
            var specs = new List<RigSegmentSpec>
            {
                new()
                {
                    segment = RigSegment.Pelvis, parent = RigSegment.Pelvis, massPercent = 14f,
                    pitch = RigDofRange.None, roll = RigDofRange.None, yaw = RigDofRange.None,
                    capsuleLength = 0.10f, capsuleRadius = 0.11f, capsuleDirection = 0,
                    localPosition = new Vector3(0f, 1.00f, 0f)
                },
                new()
                {
                    segment = RigSegment.Torso, parent = RigSegment.Pelvis, massPercent = 33f,
                    pitch = RigDofRange.Range(-30f, 30f), roll = RigDofRange.Range(-20f, 20f), yaw = RigDofRange.Range(-35f, 35f),
                    spring = 3500f, damper = 250f, maxForce = 5000f,
                    capsuleLength = 0.50f, capsuleRadius = 0.14f, capsuleDirection = 1,
                    localPosition = new Vector3(0f, 0.15f, 0f)
                },
                new()
                {
                    segment = RigSegment.Head, parent = RigSegment.Torso, massPercent = 7f,
                    pitch = RigDofRange.Range(-40f, 40f), roll = RigDofRange.Range(-30f, 30f), yaw = RigDofRange.Range(-45f, 45f),
                    spring = 1200f, damper = 90f, maxForce = 2000f,
                    capsuleLength = 0.22f, capsuleRadius = 0.10f, capsuleDirection = 1,
                    localPosition = new Vector3(0f, 0.55f, 0f)
                }
            };

            AddMirroredChain(specs, left: true);
            AddMirroredChain(specs, left: false);
            return specs.ToArray();
        }

        private static void AddMirroredChain(List<RigSegmentSpec> specs, bool left)
        {
            float side = left ? -1f : 1f;

            RigDofRange thighRoll = RigDofRange.Range(-20f, 45f);
            RigDofRange upperArmRoll = RigDofRange.Range(-30f, 90f);
            if (!left)
            {
                thighRoll = thighRoll.Mirrored();
                upperArmRoll = upperArmRoll.Mirrored();
            }

            specs.Add(new RigSegmentSpec
            {
                segment = left ? RigSegment.ThighL : RigSegment.ThighR, parent = RigSegment.Pelvis, massPercent = 11.5f,
                pitch = RigDofRange.Range(-110f, 20f), roll = thighRoll, yaw = RigDofRange.Range(-30f, 30f),
                spring = 4500f, damper = 350f, maxForce = 7000f,
                capsuleLength = 0.42f, capsuleRadius = 0.07f, capsuleDirection = 1,
                localPosition = new Vector3(side * 0.10f, -0.05f, 0f)
            });
            specs.Add(new RigSegmentSpec
            {
                segment = left ? RigSegment.ShinL : RigSegment.ShinR, parent = left ? RigSegment.ThighL : RigSegment.ThighR, massPercent = 5f,
                pitch = RigDofRange.Range(0f, 130f), roll = RigDofRange.None, yaw = RigDofRange.None,
                spring = 4000f, damper = 300f, maxForce = 6000f,
                capsuleLength = 0.40f, capsuleRadius = 0.05f, capsuleDirection = 1,
                localPosition = new Vector3(0f, -0.45f, 0f)
            });
            specs.Add(new RigSegmentSpec
            {
                segment = left ? RigSegment.FootL : RigSegment.FootR, parent = left ? RigSegment.ShinL : RigSegment.ShinR, massPercent = 1.5f,
                pitch = RigDofRange.Range(-40f, 25f), roll = RigDofRange.Range(-20f, 20f), yaw = RigDofRange.None,
                spring = 2500f, damper = 180f, maxForce = 3500f,
                capsuleLength = 0.22f, capsuleRadius = 0.04f, capsuleDirection = 2,
                localPosition = new Vector3(0f, -0.44f, 0f)
            });
            specs.Add(new RigSegmentSpec
            {
                segment = left ? RigSegment.UpperArmL : RigSegment.UpperArmR, parent = RigSegment.Torso, massPercent = 3f,
                pitch = RigDofRange.Range(-60f, 120f), roll = upperArmRoll, yaw = RigDofRange.Range(-60f, 60f),
                spring = 2000f, damper = 150f, maxForce = 3000f,
                capsuleLength = 0.28f, capsuleRadius = 0.05f, capsuleDirection = 1,
                localPosition = new Vector3(side * 0.22f, 0.45f, 0f)
            });
            specs.Add(new RigSegmentSpec
            {
                segment = left ? RigSegment.ForearmL : RigSegment.ForearmR, parent = left ? RigSegment.UpperArmL : RigSegment.UpperArmR, massPercent = 2f,
                pitch = RigDofRange.Range(0f, 140f), roll = RigDofRange.None, yaw = RigDofRange.None,
                spring = 1800f, damper = 120f, maxForce = 2500f,
                capsuleLength = 0.26f, capsuleRadius = 0.04f, capsuleDirection = 1,
                localPosition = new Vector3(0f, -0.30f, 0f)
            });
            specs.Add(new RigSegmentSpec
            {
                segment = left ? RigSegment.GloveL : RigSegment.GloveR, parent = left ? RigSegment.ForearmL : RigSegment.ForearmR, massPercent = 1f,
                pitch = RigDofRange.Range(-60f, 60f), roll = RigDofRange.None, yaw = RigDofRange.Range(-25f, 25f),
                spring = 1000f, damper = 80f, maxForce = 1500f,
                capsuleLength = 0.12f, capsuleRadius = 0.06f, capsuleDirection = 1,
                localPosition = new Vector3(0f, -0.28f, 0f)
            });
        }
    }
}
