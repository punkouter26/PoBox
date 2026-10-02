using System.Collections.Generic;
using UnityEngine;
using PoBox.Fx;
using PoBox.Sim;

namespace PoBox.EditorTools
{
    /// <summary>
    /// Builds a fighter out of primitives: thirteen articulation links, 72 kg in human proportions, with
    /// joint ranges, drive strengths and torque limits taken from what a person can do rather than from
    /// what is convenient.
    ///
    /// This body is the stand-in. The house rule is that the trained fighter's rig comes from the owner's
    /// skinned mesh, read into MuJoCo and mirrored here; when that exists it replaces what this file
    /// produces, and <see cref="Fighter"/> and everything downstream of it carry on unchanged as long as the
    /// parts keep their order.
    /// </summary>
    public static class FighterFactory
    {
        public class Look
        {
            public Material skin, trunks, glove, boot, overlay, trail;
            public Color rim = Color.white;
            public PhysicsMaterial body, sole, leather;
        }

        enum Skin { Body, Trunks, Glove, Boot }

        struct Geom
        {
            public PrimitiveType type;
            public Vector3 pos, scale, euler;
            public Skin skin;
            public bool collide, strike;
        }

        class Link
        {
            public string name;
            public PartKind kind;
            public int side, parent;
            public Vector3 pivot;          // in the fighter's own frame, feet on y = 0, facing +z
            public bool spherical;
            public float mass, torque, stiffness, damping;
            public Vector2 x, y, z;        // Euler limits about the link's axes, degrees
            public Geom[] geoms;
        }

        public const float PelvisHeight = 0.97f;

        static Geom Capsule(Vector3 pos, float radius, float length, Skin skin, bool collide = true, bool sideways = false) => new Geom
        {
            type = PrimitiveType.Capsule, pos = pos, skin = skin, collide = collide,
            // A unit capsule is 2 tall and 1 across; length here is end to end, caps included.
            scale = new Vector3(radius * 2f, length * 0.5f, radius * 2f),
            euler = sideways ? new Vector3(0f, 0f, 90f) : Vector3.zero,
        };

        static Geom Ball(Vector3 pos, float radius, Skin skin, bool collide = true, bool strike = false) => new Geom
        {
            type = PrimitiveType.Sphere, pos = pos, skin = skin, collide = collide, strike = strike,
            scale = Vector3.one * (radius * 2f),
        };

        static Geom Box(Vector3 pos, Vector3 size, Skin skin) => new Geom
        {
            type = PrimitiveType.Cube, pos = pos, skin = skin, collide = true, scale = size,
        };

        /// <summary>
        /// The body, pelvis first, in the order every other system indexes it by:
        /// pelvis, torso, head, left arm (upper, fore), right arm, left leg (thigh, shin, foot), right leg.
        /// </summary>
        static List<Link> Links()
        {
            var l = new List<Link>();

            l.Add(new Link
            {
                name = "Pelvis", kind = PartKind.Pelvis, parent = -1, pivot = new Vector3(0f, PelvisHeight, 0f), mass = 11f,
                geoms = new[] { Capsule(new Vector3(0f, -0.01f, 0f), 0.11f, 0.34f, Skin.Trunks, true, true) },
            });
            l.Add(new Link
            {
                name = "Torso", kind = PartKind.Torso, parent = 0, pivot = new Vector3(0f, 1.05f, 0f), spherical = true,
                mass = 24f, torque = 200f, stiffness = 2500f, damping = 220f,
                x = new Vector2(-15f, 40f), y = new Vector2(-60f, 60f), z = new Vector2(-25f, 25f),
                geoms = new[]
                {
                    Capsule(new Vector3(0f, 0.14f, 0f), 0.13f, 0.36f, Skin.Body),
                    Capsule(new Vector3(0f, 0.31f, 0f), 0.125f, 0.46f, Skin.Body, true, true),
                },
            });
            l.Add(new Link
            {
                name = "Head", kind = PartKind.Head, parent = 1, pivot = new Vector3(0f, 1.50f, 0f), spherical = true,
                mass = 5f, torque = 35f, stiffness = 300f, damping = 30f,
                x = new Vector2(-25f, 35f), y = new Vector2(-50f, 50f), z = new Vector2(-25f, 25f),
                geoms = new[]
                {
                    Capsule(new Vector3(0f, 0.02f, 0f), 0.05f, 0.14f, Skin.Body, false),
                    Ball(new Vector3(0f, 0.125f, 0.01f), 0.105f, Skin.Body),
                },
            });

            for (int s = -1; s <= 1; s += 2)
            {
                string tag = s < 0 ? "L" : "R";
                int upper = l.Count;
                l.Add(new Link
                {
                    name = "UpperArm" + tag, kind = PartKind.UpperArm, side = s, parent = 1,
                    pivot = new Vector3(s * 0.25f, 1.43f, 0f), spherical = true,
                    mass = 2.1f, torque = 110f, stiffness = 900f, damping = 45f,
                    // Flexion (hand forward and up) is a negative turn about X; abduction is towards the arm's own side.
                    x = new Vector2(-150f, 45f), y = new Vector2(-60f, 60f),
                    z = s < 0 ? new Vector2(-110f, 25f) : new Vector2(-25f, 110f),
                    geoms = new[]
                    {
                        Ball(Vector3.zero, 0.062f, Skin.Body, false),
                        Capsule(new Vector3(0f, -0.145f, 0f), 0.05f, 0.37f, Skin.Body),
                    },
                });
                l.Add(new Link
                {
                    name = "Forearm" + tag, kind = PartKind.Forearm, side = s, parent = upper,
                    pivot = new Vector3(s * 0.25f, 1.14f, 0f),
                    mass = 2.0f, torque = 70f, stiffness = 500f, damping = 22f,
                    x = new Vector2(-145f, 0f),
                    geoms = new[]
                    {
                        Capsule(new Vector3(0f, -0.125f, 0f), 0.042f, 0.32f, Skin.Body),
                        Ball(new Vector3(0f, -0.31f, 0f), 0.08f, Skin.Glove, true, true),
                    },
                });
            }

            for (int s = -1; s <= 1; s += 2)
            {
                string tag = s < 0 ? "L" : "R";
                int thigh = l.Count;
                l.Add(new Link
                {
                    name = "Thigh" + tag, kind = PartKind.Thigh, side = s, parent = 0,
                    pivot = new Vector3(s * 0.095f, 0.92f, 0f), spherical = true,
                    mass = 7.5f, torque = 220f, stiffness = 2200f, damping = 200f,
                    x = new Vector2(-110f, 25f), y = new Vector2(-35f, 35f),
                    z = s < 0 ? new Vector2(-45f, 20f) : new Vector2(-20f, 45f),
                    geoms = new[]
                    {
                        Capsule(new Vector3(0f, -0.21f, 0f), 0.07f, 0.52f, Skin.Body),
                        Capsule(new Vector3(0f, -0.11f, 0f), 0.076f, 0.26f, Skin.Trunks, false),
                    },
                });
                l.Add(new Link
                {
                    name = "Shin" + tag, kind = PartKind.Shin, side = s, parent = thigh,
                    pivot = new Vector3(s * 0.095f, 0.50f, 0f),
                    mass = 3.5f, torque = 240f, stiffness = 2000f, damping = 180f,
                    x = new Vector2(0f, 140f),
                    geoms = new[] { Capsule(new Vector3(0f, -0.21f, 0f), 0.055f, 0.49f, Skin.Body) },
                });
                l.Add(new Link
                {
                    name = "Foot" + tag, kind = PartKind.Foot, side = s, parent = thigh + 1,
                    pivot = new Vector3(s * 0.095f, 0.08f, 0f), spherical = true,
                    mass = 1.0f, torque = 120f, stiffness = 600f, damping = 60f,
                    x = new Vector2(-30f, 40f), y = new Vector2(-15f, 15f), z = new Vector2(-15f, 15f),
                    geoms = new[] { Box(new Vector3(0f, -0.045f, 0.055f), new Vector3(0.10f, 0.07f, 0.25f), Skin.Boot) },
                });
            }
            return l;
        }

        static Material MaterialFor(Skin skin, Look look)
        {
            switch (skin)
            {
                case Skin.Trunks: return look.trunks;
                case Skin.Glove: return look.glove;
                case Skin.Boot: return look.boot;
                default: return look.skin;
            }
        }

        static ArticulationDrive Drive(Vector2 euler, float sign, Link link)
        {
            float a = sign * euler.x, b = sign * euler.y;
            return new ArticulationDrive
            {
                lowerLimit = Mathf.Min(a, b),
                upperLimit = Mathf.Max(a, b),
                stiffness = link.stiffness,
                damping = link.damping,
                forceLimit = link.torque,
                driveType = ArticulationDriveType.Force,   // a real PD spring; Target would position-lock the joint
                target = 0f,
            };
        }

        static Renderer[] AddGeoms(Transform parent, Link link, Look look, bool physics, out Collider strike, out Transform gloveCentre)
        {
            strike = null;
            gloveCentre = null;
            var renderers = new List<Renderer>();
            for (int g = 0; g < link.geoms.Length; g++)
            {
                Geom geom = link.geoms[g];
                GameObject go = GameObject.CreatePrimitive(geom.type);
                go.name = link.name + "_" + geom.skin + g;
                go.transform.SetParent(parent, false);
                go.transform.localPosition = geom.pos;
                go.transform.localRotation = Quaternion.Euler(geom.euler);
                go.transform.localScale = geom.scale;

                var renderer = go.GetComponent<MeshRenderer>();
                Material baseMaterial = MaterialFor(geom.skin, look);
                renderer.sharedMaterials = look.overlay != null ? new[] { baseMaterial, look.overlay } : new[] { baseMaterial };
                renderers.Add(renderer);

                Collider collider = go.GetComponent<Collider>();
                if (!physics || !geom.collide) Object.DestroyImmediate(collider);
                else
                {
                    collider.sharedMaterial = geom.strike ? look.leather : link.kind == PartKind.Foot ? look.sole : look.body;
                    if (geom.strike) strike = collider;
                }
                if (geom.strike) gloveCentre = go.transform;
            }
            return renderers.ToArray();
        }

        static TrailRenderer AddTrail(Transform glove, Look look)
        {
            var go = new GameObject("Trail");
            go.transform.SetParent(glove, false);
            var trail = go.AddComponent<TrailRenderer>();
            trail.time = 0.14f;
            trail.minVertexDistance = 0.02f;
            trail.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            // The glove is scaled; the trail's width is in the glove's local units.
            trail.widthMultiplier = 0.14f / Mathf.Max(0.01f, glove.lossyScale.x);
            trail.sharedMaterial = look.trail;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(look.rim, 0f), new GradientColorKey(look.rim, 1f) },
                new[] { new GradientAlphaKey(0.55f, 0f), new GradientAlphaKey(0f, 1f) });
            trail.colorGradient = gradient;
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            trail.receiveShadows = false;
            trail.emitting = false;
            return trail;
        }

        /// <summary>The live, physical fighter, standing at <paramref name="position"/> facing <paramref name="rotation"/>.</summary>
        public static Fighter Build(string name, Vector3 position, Quaternion rotation, Look look, int corner, Transform parent)
        {
            List<Link> links = Links();
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(position, rotation);

            var fighter = root.AddComponent<Fighter>();
            var skin = root.AddComponent<FighterSkin>();
            root.AddComponent<ScriptedBoxer>();

            var objects = new GameObject[links.Count];
            var parts = new BodyPart[links.Count];
            var skinParts = new FighterSkin.Part[links.Count];
            var trails = new TrailRenderer[2];
            Vector3 s = RigSigns.S;
            float mass = 0f;

            for (int i = 0; i < links.Count; i++)
            {
                Link link = links[i];
                var go = new GameObject(link.name);
                Transform p = link.parent < 0 ? root.transform : objects[link.parent].transform;
                go.transform.SetParent(p, false);
                go.transform.localPosition = link.pivot - (link.parent < 0 ? Vector3.zero : links[link.parent].pivot);
                objects[i] = go;

                var ab = go.AddComponent<ArticulationBody>();
                ab.useGravity = true;
                ab.mass = link.mass;
                ab.linearDamping = 0.02f;
                ab.angularDamping = 0.05f;
                ab.jointFriction = 0.05f;
                mass += link.mass;

                if (link.parent < 0)
                {
                    ab.immovable = false;
                    ab.solverIterations = 16;
                    ab.solverVelocityIterations = 4;
                }
                else
                {
                    ab.jointType = link.spherical ? ArticulationJointType.SphericalJoint : ArticulationJointType.RevoluteJoint;
                    ab.matchAnchors = true;
                    ab.anchorPosition = Vector3.zero;
                    ab.anchorRotation = Quaternion.identity;
                    // 22 rad/s is about as fast as a human shoulder turns in a punch.
                    ab.maxJointVelocity = 22f;
                    ab.twistLock = ArticulationDofLock.LimitedMotion;
                    ab.xDrive = Drive(link.x, s.x, link);
                    if (link.spherical)
                    {
                        ab.swingYLock = ArticulationDofLock.LimitedMotion;
                        ab.swingZLock = ArticulationDofLock.LimitedMotion;
                        ab.yDrive = Drive(link.y, s.y, link);
                        ab.zDrive = Drive(link.z, s.z, link);
                    }
                    if (link.kind == PartKind.Forearm) ab.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                }

                var part = go.AddComponent<BodyPart>();
                part.owner = fighter;
                part.kind = link.kind;
                part.side = link.side;
                part.body = ab;
                part.torqueLimit = Mathf.Max(1f, link.torque);
                part.renderers = AddGeoms(go.transform, link, look, true, out Collider strike, out Transform glove);
                part.strike = strike;
                if (link.kind == PartKind.Forearm)
                {
                    go.AddComponent<GloveSensor>();
                    if (glove != null) trails[link.side < 0 ? 0 : 1] = AddTrail(glove, look);
                }

                parts[i] = part;
                skinParts[i] = new FighterSkin.Part { bone = go.transform, renderers = part.renderers };
            }

            fighter.corner = corner;
            fighter.cornerColor = look.rim;
            fighter.parts = parts;
            fighter.pelvis = parts[0]; fighter.torso = parts[1]; fighter.head = parts[2];
            fighter.upperArmL = parts[3]; fighter.forearmL = parts[4];
            fighter.upperArmR = parts[5]; fighter.forearmR = parts[6];
            fighter.thighL = parts[7]; fighter.shinL = parts[8]; fighter.footL = parts[9];
            fighter.thighR = parts[10]; fighter.shinR = parts[11]; fighter.footR = parts[12];
            fighter.standingPelvisHeight = PelvisHeight;
            fighter.totalMass = mass;

            skin.fighter = fighter;
            skin.parts = skinParts;
            skin.trails = trails;
            skin.rimColor = look.rim;
            return fighter;
        }
    }
}
