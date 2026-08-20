using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Soft physics ropes around the ring: each rope is a row of light capsule
    /// segments (gravity off, rotation frozen) pinned to their rest line by
    /// spring joints, so fighters and cubes sink in and get flung back.
    /// Rendered with a LineRenderer that follows the live segments; the GLB
    /// ring's painted ropes stay as backdrop just outside these.
    /// Segments carry rigidbodies, so fall sensors ignore rope contact.
    /// Test-scene harness only.
    /// </summary>
    public sealed class Systems_RingRopes : MonoBehaviour
    {
        private const int SIDE_COUNT = 4;

        // Post centers sit at ±3.05 (6.1 m canvas); anchors at ±2.95 land on
        // the post's inner face so ropes visually meet the wood.
        [SerializeField] private float _halfExtent = 2.95f;
        [SerializeField] private float[] _ropeHeights = { 0.55f, 0.95f, 1.35f };
        [SerializeField] private int _segmentsPerSide = 8;
        [SerializeField] private float _segmentMass = 0.3f;
        [SerializeField] private float _ropeRadius = 0.04f;
        // Collider is fatter than the visual rope so bodies engage reliably.
        [SerializeField] private float _colliderRadius = 0.08f;
        // Overdamped on purpose (damper > 2·sqrt(spring·mass)): the rope holds
        // a taut straight line, folds under a fighter's weight, and returns
        // smoothly without residual waving.
        [SerializeField] private float _springForce = 4000f;
        [SerializeField] private float _springDamper = 120f;
        [SerializeField] private float _bounciness = 0.7f;
        // Asset materials (red/white/blue): runtime Shader.Find materials get
        // stripped from device builds — ropes rendered invisible on Android.
        [SerializeField] private Material[] _ropeMaterials;

        private static readonly Color[] RopeColors =
        {
            new(0.85f, 0.1f, 0.1f), new(0.95f, 0.95f, 0.95f), new(0.15f, 0.25f, 0.8f)
        };

        private Rigidbody[][] _ropeSegments;
        private LineRenderer[] _lines;
        private Vector3[] _ropeCornersA;
        private Vector3[] _ropeCornersB;

        private void Start()
        {
            var bounceMaterial = new PhysicsMaterial("PM_RopeBounce")
            {
                bounciness = _bounciness,
                bounceCombine = PhysicsMaterialCombine.Maximum,
                staticFriction = 0.2f,
                dynamicFriction = 0.2f
            };

            int ropeCount = SIDE_COUNT * _ropeHeights.Length;
            _ropeSegments = new Rigidbody[ropeCount][];
            _lines = new LineRenderer[ropeCount];
            _ropeCornersA = new Vector3[ropeCount];
            _ropeCornersB = new Vector3[ropeCount];

            int ropeIndex = 0;
            for (int heightIndex = 0; heightIndex < _ropeHeights.Length; heightIndex++)
            {
                float height = _ropeHeights[heightIndex];
                for (int sideIndex = 0; sideIndex < SIDE_COUNT; sideIndex++)
                {
                    Vector3 cornerA = Corner(sideIndex, height);
                    Vector3 cornerB = Corner(sideIndex + 1, height);
                    _ropeCornersA[ropeIndex] = cornerA;
                    _ropeCornersB[ropeIndex] = cornerB;
                    _ropeSegments[ropeIndex] = BuildRope(cornerA, cornerB, bounceMaterial, ropeIndex);
                    _lines[ropeIndex] = BuildLine(ropeIndex, heightIndex);
                    ropeIndex++;
                }
            }
            IgnoreRopeSelfCollisions();
        }

        // Segments overlap by design (gap-free rope) and adjacent sides share
        // corners; without this, PhysX depenetration shoves segments off the
        // rest line and the ropes never sit straight.
        private void IgnoreRopeSelfCollisions()
        {
            var colliders = GetComponentsInChildren<Collider>(true);
            for (int firstIndex = 0; firstIndex < colliders.Length; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1; secondIndex < colliders.Length; secondIndex++)
                {
                    Physics.IgnoreCollision(colliders[firstIndex], colliders[secondIndex], true);
                }
            }
        }

        // _ropeHeights are heights ABOVE THE CANVAS, so the posts are offset by
        // this component's own transform. They used to be raw world Y, which
        // pinned the ropes to y = 0 no matter where the ring stood - raising the
        // ring to its real 1 m platform left the ropes lying on the arena floor.
        private Vector3 Corner(int cornerIndex, float height)
        {
            Vector3 origin = transform.position;
            switch (cornerIndex % SIDE_COUNT)
            {
                case 0: return origin + new Vector3(-_halfExtent, height, -_halfExtent);
                case 1: return origin + new Vector3(_halfExtent, height, -_halfExtent);
                case 2: return origin + new Vector3(_halfExtent, height, _halfExtent);
                default: return origin + new Vector3(-_halfExtent, height, _halfExtent);
            }
        }

        private Rigidbody[] BuildRope(Vector3 cornerA, Vector3 cornerB, PhysicsMaterial bounceMaterial, int ropeIndex)
        {
            var segments = new Rigidbody[_segmentsPerSide];
            Vector3 side = cornerB - cornerA;
            float segmentLength = side.magnitude / _segmentsPerSide;
            Vector3 axis = side.normalized;
            bool alongX = Mathf.Abs(axis.x) > Mathf.Abs(axis.z);

            for (int segmentIndex = 0; segmentIndex < _segmentsPerSide; segmentIndex++)
            {
                var segmentObject = new GameObject($"Rope{ropeIndex}_Seg{segmentIndex}");
                segmentObject.transform.SetParent(transform, false);
                Vector3 restPosition = cornerA + axis * segmentLength * (segmentIndex + 0.5f);
                segmentObject.transform.position = restPosition;

                var collider = segmentObject.AddComponent<CapsuleCollider>();
                collider.direction = alongX ? 0 : 2;
                collider.radius = _colliderRadius;
                collider.height = segmentLength * 1.3f; // overlap: no gaps between segments
                collider.sharedMaterial = bounceMaterial;

                var body = segmentObject.AddComponent<Rigidbody>();
                body.mass = _segmentMass;
                body.useGravity = false;
                body.linearDamping = 1f;
                body.constraints = RigidbodyConstraints.FreezeRotation;
                // Speculative CCD: fast limbs and cubes can't tunnel through.
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

                var spring = segmentObject.AddComponent<SpringJoint>();
                spring.autoConfigureConnectedAnchor = false;
                spring.connectedAnchor = restPosition; // world anchor: the rest line
                spring.anchor = Vector3.zero;
                spring.spring = _springForce;
                spring.damper = _springDamper;
                spring.tolerance = 0f; // default 0.025 m leaves permanent kinks

                segments[segmentIndex] = body;
            }
            return segments;
        }

        private LineRenderer BuildLine(int ropeIndex, int heightIndex)
        {
            var lineObject = new GameObject($"RopeLine{ropeIndex}");
            lineObject.transform.SetParent(transform, false);
            var line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = _segmentsPerSide + 2; // + both post anchor points
            line.startWidth = _ropeRadius * 2f;
            line.endWidth = _ropeRadius * 2f;
            line.numCornerVertices = 2;
            if (_ropeMaterials != null && _ropeMaterials.Length > 0)
            {
                line.sharedMaterial = _ropeMaterials[heightIndex % _ropeMaterials.Length];
            }
            else
            {
                // Editor-only fallback; stripped shaders make this invisible in builds.
                var lineMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                lineMaterial.SetColor("_BaseColor", RopeColors[heightIndex % RopeColors.Length]);
                line.sharedMaterial = lineMaterial;
            }
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return line;
        }

        private void LateUpdate()
        {
            if (_ropeSegments == null)
            {
                return;
            }
            for (int ropeIndex = 0; ropeIndex < _ropeSegments.Length; ropeIndex++)
            {
                Rigidbody[] segments = _ropeSegments[ropeIndex];
                LineRenderer line = _lines[ropeIndex];
                line.SetPosition(0, _ropeCornersA[ropeIndex]);
                for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
                {
                    line.SetPosition(segmentIndex + 1, segments[segmentIndex].position);
                }
                line.SetPosition(segments.Length + 1, _ropeCornersB[ropeIndex]);
            }
        }
    }
}
