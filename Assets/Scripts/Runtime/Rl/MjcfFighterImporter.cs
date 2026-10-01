using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using UnityEngine;
using PoBox.Sim;

namespace PoBox.Rl
{
    /// <summary>
    /// Builds one fighter's ArticulationBody hierarchy from the MuJoCo model it was trained on, so the body
    /// the policy drives in Unity is the body it learned in: same link lengths, masses, joint axes and
    /// ranges, PD gains and torque limits. Follows PoDecath's importer.
    ///
    /// A MuJoCo body with several hinge joints becomes a chain of links, one revolute joint each, the last
    /// of which carries the colliders; that reproduces MuJoCo's joint order exactly. The training files hold
    /// a fighter with every name prefixed (a_ or b_); this reads the one fighter with the given prefix and
    /// drops the prefix, ignoring the bag, the ring and the other fighter.
    /// </summary>
    public static class MjcfFighterImporter
    {
        public class Options
        {
            public string prefix = "a_";
            [Tooltip("Drawn inside each limb and through the skin: glows with joint stress.")]
            public Material xrayMaterial;
            public Material gloveMaterial;
            public PhysicsMaterial bodyPhysics, solePhysics, leatherPhysics;
            [Tooltip("Each body's mass, centre of mass and inertia as MuJoCo computed them (training/tools/export_reference.py).")]
            public string inertiaJson;
        }

        [Serializable]
        public class Inertia
        {
            public InertiaBody[] bodies;
        }

        [Serializable]
        public class InertiaBody
        {
            public string name;
            public float mass;
            public float[] com, quat, inertia;
        }

        /// <summary>
        /// Gives every link MuJoCo's own mass properties. Left alone, Unity spreads a link's mass over its
        /// colliders by volume; the model says how much each shape weighs. On the torso, which carries the
        /// head, that moves the centre of mass by centimetres, and the policy balances to the millimetre.
        /// </summary>
        static bool ApplyInertia(string json, Result result)
        {
            Inertia table = string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Inertia>(json);
            if (table == null || table.bodies == null || table.bodies.Length == 0) return false;
            foreach (InertiaBody b in table.bodies)
            {
                Body body = result.Find(b.name);
                if (body == null) continue;
                ArticulationBody link = body.link;
                link.mass = b.mass;
                link.automaticCenterOfMass = false;
                link.centerOfMass = CoordinateTransform.ExternalToUnity(new Vector3(b.com[0], b.com[1], b.com[2]));
                link.automaticInertiaTensor = false;
                // Swapping two axes turns a rotation of w about n into one of -w about the swapped n, and
                // swaps which principal moment sits on which of the last two axes.
                link.inertiaTensorRotation = new Quaternion(-b.quat[1], -b.quat[3], -b.quat[2], b.quat[0]);
                link.inertiaTensor = new Vector3(b.inertia[0], b.inertia[2], b.inertia[1]);
            }
            return true;
        }

        /// <summary>One MuJoCo body as built: its last link, the hinge links leading to it, and what is drawn on it.</summary>
        public class Body
        {
            public string name;
            public ArticulationBody link;
            public List<ArticulationBody> hinges = new List<ArticulationBody>();
            public List<Renderer> renderers = new List<Renderer>();
            public Collider glove;
        }

        public class Result
        {
            public GameObject root;
            public MjcfRig rig;
            public List<Body> bodies = new List<Body>();
            public Body Find(string name) => bodies.Find(b => b.name == name);
        }

        /// <summary>The fields of <c>&lt;name&gt;_policy_config.json</c> this side needs, named as the file names them.</summary>
        [Serializable]
        public class Config
        {
            public string name;
            public string[] joint_order;
            public float[] default_joint_pos, lower, upper, kp, kv, force_limit, velocity_limit;
            public float total_mass_kg, stand_height, action_scale, action_clip, ring_half, height_m;
            public int physics_hz, control_decimation;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static Result Build(string xmlText, string configJson, Options opt, Transform parent)
        {
            Config cfg = JsonUtility.FromJson<Config>(configJson);
            if (cfg == null || cfg.joint_order == null || cfg.joint_order.Length == 0)
                throw new InvalidOperationException("policy config has no joint_order");

            var doc = new XmlDocument();
            doc.LoadXml(xmlText);
            XmlElement mujoco = doc.DocumentElement;
            bool degrees = !(mujoco["compiler"] != null && mujoco["compiler"].GetAttribute("angle") == "radian");
            XmlElement world = mujoco["worldbody"] ?? throw new InvalidOperationException("MJCF has no <worldbody>");

            var gains = new Dictionary<string, (float kp, float kv, float limit)>();
            XmlElement act = mujoco["actuator"];
            if (act != null)
                foreach (XmlNode n in act.ChildNodes)
                    if (n is XmlElement a && a.HasAttribute("joint"))
                    {
                        float limit = 0f;
                        if (a.HasAttribute("forcerange")) { float[] fr = Vec(a.GetAttribute("forcerange")); limit = Mathf.Max(Mathf.Abs(fr[0]), Mathf.Abs(fr[1])); }
                        gains[a.GetAttribute("joint")] = (F(a, "kp", 0f), F(a, "kv", 0f), limit);
                    }

            var result = new Result();
            var rootGo = new GameObject("Rig");
            rootGo.transform.SetParent(parent, false);
            var rig = rootGo.AddComponent<MjcfRig>();
            result.root = rootGo;
            result.rig = rig;

            var byJoint = new Dictionary<string, ArticulationBody>();
            foreach (XmlNode n in world.ChildNodes)
                if (n is XmlElement b && b.Name == "body" && b.GetAttribute("name").StartsWith(opt.prefix, StringComparison.Ordinal))
                {
                    ArticulationBody top = BuildBody(b, rootGo.transform, true, opt, degrees, gains, byJoint, result, cfg);
                    if (rig.root == null) rig.root = top;
                }
            if (rig.root == null) throw new InvalidOperationException($"MJCF has no body with prefix '{opt.prefix}'");
            if (!ApplyInertia(opt.inertiaJson, result))
                Debug.LogWarning($"[PoBox] {cfg.name}: no inertia table, so Unity will guess each link's centre of mass from its colliders. " +
                                 "Run training/tools/export_reference.py --name " + cfg.name);

            // Joints in the policy's order, with the config's guard pose and ranges.
            int count = cfg.joint_order.Length;
            rig.fighterName = cfg.name;
            rig.jointNames = cfg.joint_order;
            rig.joints = new ArticulationBody[count];
            rig.sign = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (!byJoint.TryGetValue(cfg.joint_order[i], out rig.joints[i]))
                    throw new InvalidOperationException($"joint '{cfg.joint_order[i]}' is in the policy config but not in the model");
                rig.sign[i] = -1f;
            }
            rig.defaultPos = cfg.default_joint_pos;
            rig.lower = cfg.lower;
            rig.upper = cfg.upper;
            rig.standHeight = cfg.stand_height;
            rig.actionScale = cfg.action_scale > 0f ? cfg.action_scale : 0.5f;
            rig.actionClip = cfg.action_clip > 0f ? cfg.action_clip : 3f;
            rig.controlDecimation = Mathf.Max(1, cfg.control_decimation);
            rig.physicsStep = cfg.physics_hz > 0 ? 1f / cfg.physics_hz : 0.005f;
            rig.totalMass = cfg.total_mass_kg;

            rig.root.immovable = false;
            rig.root.solverIterations = 12;
            rig.root.solverVelocityIterations = 4;
            // The trainer's bodies never go to sleep. One that does here stops answering its drives, and a
            // policy looking at a body that is not moving keeps asking for the same thing for ever.
            rig.root.sleepThreshold = 0f;
            rig.root.transform.localPosition = new Vector3(0f, cfg.stand_height, 0f);
            rig.root.transform.localRotation = Quaternion.identity;

            rig.headGeom = FindGeom(rootGo, "head_geom");
            rig.torsoGeom = FindGeom(rootGo, "torso_geom");
            rig.gloveL = FindGeom(rootGo, "glove_l");
            rig.gloveR = FindGeom(rootGo, "glove_r");
            rig.footL = FindGeom(rootGo, "foot_l_geom")?.GetComponent<BoxCollider>();
            rig.footR = FindGeom(rootGo, "foot_r_geom")?.GetComponent<BoxCollider>();
            if (rig.headGeom != null) rig.headRadius = rig.headGeom.GetComponent<SphereCollider>().radius;
            if (rig.gloveL != null) rig.gloveRadius = rig.gloveL.GetComponent<SphereCollider>().radius;

            // Pairs the model says must not collide: links joined by a joint, which overlap by construction.
            // PhysX skips a link's direct parent on its own, but the hinge chains put extra links in between.
            var a1 = new List<Collider>();
            var b1 = new List<Collider>();
            XmlElement contact = mujoco["contact"];
            if (contact != null)
                foreach (XmlNode n in contact.ChildNodes)
                {
                    if (!(n is XmlElement e) || e.Name != "exclude") continue;
                    string n1 = e.GetAttribute("body1"), n2 = e.GetAttribute("body2");
                    if (!n1.StartsWith(opt.prefix, StringComparison.Ordinal) || !n2.StartsWith(opt.prefix, StringComparison.Ordinal)) continue;
                    Body x = result.Find(n1.Substring(opt.prefix.Length)), y = result.Find(n2.Substring(opt.prefix.Length));
                    if (x == null || y == null) continue;
                    foreach (Collider ca in x.link.GetComponentsInChildren<Collider>())
                    {
                        if (ca.GetComponentInParent<ArticulationBody>() != x.link) continue;
                        foreach (Collider cb in y.link.GetComponentsInChildren<Collider>())
                        {
                            if (cb.GetComponentInParent<ArticulationBody>() != y.link) continue;
                            a1.Add(ca);
                            b1.Add(cb);
                        }
                    }
                }
            var excludes = rootGo.AddComponent<MjcfContactExcludes>();
            excludes.a = a1.ToArray();
            excludes.b = b1.ToArray();
            return result;
        }

        static Transform FindGeom(GameObject root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == "geom_" + name) return t;
            return null;
        }

        static ArticulationBody BuildBody(XmlElement b, Transform parent, bool isRoot, Options opt, bool degrees,
            Dictionary<string, (float kp, float kv, float limit)> gains, Dictionary<string, ArticulationBody> byJoint, Result result, Config cfg)
        {
            string name = b.GetAttribute("name").Substring(opt.prefix.Length);
            Vector3 posU = isRoot ? Vector3.zero : CoordinateTransform.ExternalToUnity(b.HasAttribute("pos") ? V3(b.GetAttribute("pos")) : Vector3.zero);

            var hinges = new List<XmlElement>();
            foreach (XmlNode n in b.ChildNodes)
                if (n is XmlElement e && e.Name == "joint" && (e.GetAttribute("type") == "hinge" || !e.HasAttribute("type")))
                    hinges.Add(e);

            var body = new Body { name = name };
            Transform chainParent = parent;
            ArticulationBody last = null;
            int links = Mathf.Max(1, hinges.Count);
            for (int k = 0; k < links; k++)
            {
                bool real = k == links - 1;
                var go = new GameObject(real ? name : $"{name}__link{k}");
                go.transform.SetParent(chainParent, false);
                go.transform.localPosition = k == 0 ? posU : Vector3.zero;
                var ab = go.AddComponent<ArticulationBody>();
                ab.useGravity = true;
                ab.linearDamping = 0f;
                ab.angularDamping = 0f;
                // Unity gives every articulation joint a little friction unless told otherwise (0.05). The
                // trainer's joints have none, only the damping written on them; with it left on, the ankles
                // moved a fifth as far as MuJoCo's under the same commands.
                ab.jointFriction = 0f;
                ab.maxJointVelocity = 100f;
                ab.maxAngularVelocity = 100f;

                if (hinges.Count > 0)
                {
                    XmlElement jn = hinges[k];
                    string joint = jn.GetAttribute("name").Substring(opt.prefix.Length);
                    Vector3 axisU = CoordinateTransform.ExternalToUnity(V3(jn.GetAttribute("axis")).normalized);
                    float lo = -Mathf.PI, hi = Mathf.PI;
                    if (jn.HasAttribute("range"))
                    {
                        float[] r = Vec(jn.GetAttribute("range"));
                        lo = r[0]; hi = r[1];
                        if (degrees) { lo *= Mathf.Deg2Rad; hi *= Mathf.Deg2Rad; }
                    }
                    gains.TryGetValue(opt.prefix + joint, out var g);

                    ab.jointType = ArticulationJointType.RevoluteJoint;
                    ab.matchAnchors = true;
                    ab.anchorPosition = Vector3.zero;
                    ab.anchorRotation = Quaternion.FromToRotation(Vector3.right, axisU);
                    ab.twistLock = ArticulationDofLock.LimitedMotion;
                    // The frames are mirror images, so the Unity angle is minus the trainer's.
                    float a = -lo * Mathf.Rad2Deg, c = -hi * Mathf.Rad2Deg;
                    ArticulationDrive d = ab.xDrive;
                    d.lowerLimit = Mathf.Min(a, c);
                    d.upperLimit = Mathf.Max(a, c);
                    d.stiffness = g.kp;
                    d.damping = g.kv + F(jn, "damping", 0f);
                    d.forceLimit = g.limit > 0f ? g.limit : float.MaxValue;
                    d.driveType = ArticulationDriveType.Force;   // a real PD spring; Target would position-lock the joint
                    ab.xDrive = d;
                    byJoint[joint] = ab;
                    body.hinges.Add(ab);
                }
                if (!real)
                {
                    ab.mass = 0.02f;
                    ab.inertiaTensor = Vector3.one * 1e-4f;
                    ab.inertiaTensorRotation = Quaternion.identity;
                }
                chainParent = go.transform;
                last = ab;
            }

            float mass = 0f;
            foreach (XmlNode n in b.ChildNodes)
                if (n is XmlElement g && g.Name == "geom")
                    mass += AddGeom(g, last.gameObject, opt, body);
            last.mass = Mathf.Max(mass, 0.05f);
            // A glove at 8 m/s moves 4 cm in one physics step, so it is first noticed already inside the
            // head, and the engine's cure for overlap is to throw the two apart at up to 10 m/s. MuJoCo's
            // contact is a soft, dead one: nothing springs back. Seen coming (speculative contacts) the
            // glove stops at the surface instead, and what overlap there still is gets eased out, not fired out.
            if (body.glove != null) last.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            last.maxDepenetrationVelocity = 1f;
            last.gameObject.AddComponent<MjcfBodyTag>().bodyName = name;
            body.link = last;
            result.bodies.Add(body);

            foreach (XmlNode n in b.ChildNodes)
                if (n is XmlElement child && child.Name == "body")
                    BuildBody(child, last.transform, false, opt, degrees, gains, byJoint, result, cfg);
            return hinges.Count > 0 ? body.hinges[0] : last;
        }

        static PartKind ZoneOf(string geom)
        {
            if (geom.StartsWith("head")) return PartKind.Head;
            if (geom.StartsWith("torso") || geom.StartsWith("chest")) return PartKind.Torso;
            if (geom.StartsWith("pelvis")) return PartKind.Pelvis;
            if (geom.StartsWith("upper_arm")) return PartKind.UpperArm;
            if (geom.StartsWith("forearm") || geom.StartsWith("glove")) return PartKind.Forearm;
            if (geom.StartsWith("thigh")) return PartKind.Thigh;
            if (geom.StartsWith("shin")) return PartKind.Shin;
            return PartKind.Foot;
        }

        static float AddGeom(XmlElement g, GameObject bodyGo, Options opt, Body body)
        {
            string type = g.HasAttribute("type") ? g.GetAttribute("type") : "sphere";
            string name = g.GetAttribute("name").Substring(opt.prefix.Length);
            float[] size = Vec(g.GetAttribute("size"));
            var holder = new GameObject("geom_" + name);
            holder.transform.SetParent(bodyGo.transform, false);
            holder.AddComponent<HitZone>().kind = ZoneOf(name);
            bool glove = name.StartsWith("glove");
            bool foot = name.StartsWith("foot");
            Collider collider;
            PrimitiveType shape;
            Vector3 scale;

            if (type == "capsule")
            {
                float r = size[0];
                float[] ft = Vec(g.GetAttribute("fromto"));
                Vector3 a = CoordinateTransform.ExternalToUnity(new Vector3(ft[0], ft[1], ft[2]));
                Vector3 b = CoordinateTransform.ExternalToUnity(new Vector3(ft[3], ft[4], ft[5]));
                Vector3 dir = b - a;
                float len = dir.magnitude;
                holder.transform.localPosition = 0.5f * (a + b);
                holder.transform.localRotation = len > 1e-5f ? Quaternion.FromToRotation(Vector3.up, dir / len) : Quaternion.identity;
                var c = holder.AddComponent<CapsuleCollider>();
                c.direction = 1; c.radius = r; c.height = len + 2f * r;
                collider = c; shape = PrimitiveType.Capsule; scale = new Vector3(2f * r, (len + 2f * r) * 0.5f, 2f * r);
            }
            else if (type == "sphere")
            {
                float r = size[0];
                holder.transform.localPosition = CoordinateTransform.ExternalToUnity(V3(g.GetAttribute("pos")));
                var c = holder.AddComponent<SphereCollider>();
                c.radius = r;
                collider = c; shape = PrimitiveType.Sphere; scale = Vector3.one * (2f * r);
            }
            else
            {
                Vector3 halfU = CoordinateTransform.ExternalToUnity(new Vector3(size[0], size[1], size[2]));
                holder.transform.localPosition = CoordinateTransform.ExternalToUnity(V3(g.GetAttribute("pos")));
                var c = holder.AddComponent<BoxCollider>();
                c.size = 2f * halfU;
                collider = c; shape = PrimitiveType.Cube; scale = 2f * halfU;
            }
            collider.sharedMaterial = glove ? opt.leatherPhysics : foot ? opt.solePhysics : opt.bodyPhysics;
            if (glove) body.glove = collider;

            // What is drawn: the glove as a glove, and every other shape as a stress glow inside the limb.
            Material material = glove ? opt.gloveMaterial : opt.xrayMaterial;
            if (material != null && !name.StartsWith("head"))
            {
                GameObject v = GameObject.CreatePrimitive(shape);
                v.name = "vis";
                Collider vc = v.GetComponent<Collider>();
                if (Application.isPlaying) UnityEngine.Object.Destroy(vc); else UnityEngine.Object.DestroyImmediate(vc);
                v.transform.SetParent(holder.transform, false);
                // A little thinner than the collider, so the glow sits inside the skin rather than on it.
                v.transform.localScale = glove ? scale * 1.15f : Vector3.Scale(scale, shape == PrimitiveType.Capsule ? new Vector3(0.8f, 1f, 0.8f) : Vector3.one * 0.9f);
                var mr = v.GetComponent<MeshRenderer>();
                mr.sharedMaterials = glove && opt.xrayMaterial != null ? new[] { material } : new[] { material };
                mr.shadowCastingMode = glove ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = glove;
                body.renderers.Add(mr);
            }
            return F(g, "mass", 0f);
        }

        static float F(XmlElement e, string attribute, float fallback) =>
            e.HasAttribute(attribute) ? float.Parse(e.GetAttribute(attribute).Trim(), Inv) : fallback;

        static float[] Vec(string s)
        {
            string[] parts = s.Trim().Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var v = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++) v[i] = float.Parse(parts[i], Inv);
            return v;
        }

        static Vector3 V3(string s)
        {
            float[] v = Vec(s);
            return new Vector3(v[0], v.Length > 1 ? v[1] : 0f, v.Length > 2 ? v[2] : 0f);
        }
    }
}
