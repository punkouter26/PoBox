using System;
using Mujoco;
using PoBox.Mj;
using UnityEngine;
using UnityEngine.Rendering;

namespace PoBox.Fx
{
    /// <summary>
    /// What <see cref="PhysicsXray.On"/> shows: every collision shape of both boxers as MuJoCo has it, read
    /// out of mjModel and mjData (not the GameObjects), drawn through the skin as a translucent fresnel shell.
    ///
    /// Each shape's colour says three things. Cyan going violet: the boxer's reserve running down. Amber: how
    /// hard the motors of the joint that carries it are working against their limits, the larger of torque
    /// over force_limit and speed over velocity_limit. Red-white: the force of whatever it is touching
    /// (mj_contactForce on mjData.contact, as MjRing reads the canvas), cooling over a quarter of a second.
    ///
    /// Every contact a boxer is in is also an arrow along the contact normal, as long as its force (a planted
    /// foot's a hand long), going from white to hot with it; a contact sunk deeper than
    /// <see cref="penetrationWarn"/> is drawn magenta instead, the solver letting a body into another.
    ///
    /// Three instanced draws (spheres and capsule ends, capsule and cylinder bodies, boxes), one material.
    /// It makes itself on the first load and sits idle with the X-ray off. In its own assembly because the
    /// game's is not allowed pointers.
    /// </summary>
    public unsafe class XrayShapes : MonoBehaviour
    {
        [Tooltip("Contact force that turns a shape fully hot, N. A planted foot carries about half a body's weight.")]
        public float fullForce = 1500f;
        [Tooltip("Reserve at which a boxer's shapes are fully violet.")]
        public float emptyReserve = 0.4f;
        public Color fresh = new Color(0.25f, 0.85f, 1f, 0.55f);
        public Color tired = new Color(0.6f, 0.3f, 1f, 0.55f);
        public Color strain = new Color(1f, 0.7f, 0.15f, 0.8f);
        public Color hot = new Color(2.5f, 0.6f, 0.3f, 1f);
        [Tooltip("Contact force whose arrow is a metre long, N.")]
        public float arrowForce = 4000f;
        [Tooltip("A contact sunk deeper than this, metres, is drawn as a warning.")]
        public float penetrationWarn = 0.01f;
        public Color arrow = new Color(1f, 1f, 1f, 0.7f);
        public Color warn = new Color(2f, 0.2f, 1.6f, 1f);
        const int MaxArrows = 96;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            PhysicsXray.On = false;
            var go = new GameObject("Physics X-ray");
            DontDestroyOnLoad(go);
            go.AddComponent<XrayShapes>();
        }

        static readonly int TintId = Shader.PropertyToID("_Tint");
        static readonly int[] P = { 0, 2, 1 };          // MuJoCo's axes in Unity's order: z up there is y up here

        Material _mat;
        Mesh _sphere, _cylinder, _cube;
        readonly Matrix4x4[][] _poses = new Matrix4x4[3][];
        readonly Vector4[][] _tints = new Vector4[3][];
        readonly int[] _count = new int[3];
        MaterialPropertyBlock[] _blocks;

        MjRing _ring;
        IntPtr _model;
        int[][] _geoms;
        int[][] _jointBody;
        float[] _heat = new float[0];
        bool[] _mine = new bool[0];

        void Awake()
        {
            // A material asset in a Resources folder, not one made here: a build keeps a shader's instanced
            // variant only for a material it carries that asks for it.
            _mat = Resources.Load<Material>("XrayShape");
            _sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");       // radius 1
            _cylinder = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");   // radius 1, y from -1 to 1
            _cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");           // side 1
            // Made here, not in field initialisers: those came out null on this object in play mode.
            _geoms = new int[2][];
            _jointBody = new int[2][];
            _blocks = new[] { new MaterialPropertyBlock(), new MaterialPropertyBlock(), new MaterialPropertyBlock() };
        }

        void LateUpdate()
        {
            if (!PhysicsXray.On || _mat == null || !MjScene.InstanceExists) return;
            MjScene scene = MjScene.Instance;
            MujocoLib.mjModel_* m = scene.Model;
            MujocoLib.mjData_* d = scene.Data;
            if (m == null || d == null) return;
            if (_ring == null) _ring = FindAnyObjectByType<MjRing>();
            if (_ring == null || !_ring.Ready) return;
            if ((IntPtr)m != _model) Bind(m);

            // What each shape is touching, by force; both shapes of a contact feel it.
            double* force = stackalloc double[6];
            float cool = Time.deltaTime * 4f;
            for (int g = 0; g < _heat.Length; g++) _heat[g] = Mathf.Max(0f, _heat[g] - cool);
            for (int i = 0; i < d->ncon; i++)
            {
                MujocoLib.mjContact_* c = d->contact + i;
                if (c->efc_address < 0) continue;
                MujocoLib.mj_contactForce(m, d, i, force);
                float h = (float)(Math.Abs(force[0]) / fullForce);
                _heat[c->geom1] = Mathf.Max(_heat[c->geom1], h);
                _heat[c->geom2] = Mathf.Max(_heat[c->geom2], h);
            }

            _count[0] = _count[1] = _count[2] = 0;
            // The arrows, before the shapes, so a full list drops shapes' ends and not the arrows.
            for (int i = 0, arrows = 0; i < d->ncon && arrows < MaxArrows; i++)
            {
                MujocoLib.mjContact_* c = d->contact + i;
                if (c->efc_address < 0 || !(_mine[c->geom1] || _mine[c->geom2])) continue;
                MujocoLib.mj_contactForce(m, d, i, force);
                float f = (float)Math.Abs(force[0]);
                bool sunk = c->dist < -penetrationWarn;
                if (f < 20f && !sunk) continue;
                arrows++;
                // The normal is the frame's first row, from geom1 to geom2; MuJoCo's z up is Unity's y.
                var n = new Vector3((float)c->frame[0], (float)c->frame[2], (float)c->frame[1]);
                var at = new Vector3((float)c->pos[0], (float)c->pos[2], (float)c->pos[1]);
                float len = Mathf.Clamp(f / arrowForce, 0.04f, 1f);
                Color col = sunk ? warn : Color.Lerp(arrow, hot, Mathf.Clamp01(f / fullForce));
                // The built-in cylinder is 2 long and 1 across: half the length, centred half way along.
                Add(1, Matrix4x4.TRS(at + n * (len * 0.5f), Quaternion.FromToRotation(Vector3.up, n), new Vector3(0.012f, len * 0.5f, 0.012f)), col);
            }
            for (int k = 0; k < 2; k++)
            {
                MjBoxer b = k == 0 ? _ring.red : _ring.blue;
                BoxerConfig cfg = b.Cfg;
                Color tank = Color.Lerp(fresh, tired, Mathf.InverseLerp(1f, emptyReserve, (float)b.Reserve));
                foreach (int g in _geoms[k])
                {
                    // The motors of the joints on this shape's body: the harder pressed of torque and speed.
                    int body = m->geom_bodyid[g];
                    float load = 0f;
                    for (int i = 0; i < _jointBody[k].Length; i++)
                    {
                        if (_jointBody[k][i] != body) continue;
                        load = Mathf.Max(load, (float)Math.Max(Math.Abs(b.Torque(i)) / cfg.force_limit[i], Math.Abs(b.JointVelocity(i)) / cfg.velocity_limit[i]));
                    }
                    Color c = Color.Lerp(Color.Lerp(tank, strain, Mathf.Clamp01(load)), hot, Mathf.Clamp01(_heat[g]));
                    Draw(m, d, g, c);
                }
            }

            var rp = new RenderParams(_mat)
            {
                worldBounds = new Bounds(new Vector3(_ring.centre.x, 1f, _ring.centre.y), new Vector3(20f, 6f, 20f)),
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            for (int s = 0; s < 3; s++)
            {
                if (_count[s] == 0) continue;
                _blocks[s].SetVectorArray(TintId, _tints[s]);
                rp.matProps = _blocks[s];
                Graphics.RenderMeshInstanced(rp, s == 0 ? _sphere : s == 1 ? _cylinder : _cube, 0, _poses[s], _count[s]);
            }
        }

        /// <summary>The boxers' shapes and the bodies their joints turn, once a model is compiled.</summary>
        void Bind(MujocoLib.mjModel_* m)
        {
            _heat = new float[m->ngeom];
            _mine = new bool[m->ngeom];
            int capacity = MaxArrows;
            for (int k = 0; k < 2; k++)
            {
                MjBoxer b = k == 0 ? _ring.red : _ring.blue;
                MjGeom[] shapes = b.GetComponentsInChildren<MjGeom>();
                _geoms[k] = new int[shapes.Length];
                for (int i = 0; i < shapes.Length; i++) { _geoms[k][i] = shapes[i].MujocoId; _mine[shapes[i].MujocoId] = true; }
                capacity += 2 * shapes.Length;
                _jointBody[k] = new int[b.Joints];
                for (int i = 0; i < b.Joints; i++)
                {
                    int j = MujocoLib.mj_name2id(m, (int)MujocoLib.mjtObj.mjOBJ_JOINT, b.prefix + b.Cfg.joint_order[i]);
                    _jointBody[k][i] = j >= 0 ? m->jnt_bodyid[j] : -1;
                }
            }
            // A property block's array keeps the length it was first given, so each is made full size once.
            for (int s = 0; s < 3; s++)
            {
                _poses[s] = new Matrix4x4[capacity];
                _tints[s] = new Vector4[capacity];
                _blocks[s].Clear();
            }
            _model = (IntPtr)m;   // last: a bind that throws is tried again next frame
        }

        /// <summary>One shape into the instance lists: its pose from mjData, in Unity's frame, scaled to its size.</summary>
        void Draw(MujocoLib.mjModel_* m, MujocoLib.mjData_* d, int g, Color c)
        {
            double* p = d->geom_xpos + 3 * g, R = d->geom_xmat + 9 * g, size = m->geom_size + 3 * g;
            float r = (float)size[0];
            // In Unity's frame a shape's own z (a capsule's axis) is its y, and its y is its z.
            switch (m->geom_type[g])
            {
                case 2: Add(0, Pose(p, R, 0f, r, r, r), c); break;                                                   // sphere
                case 4: Add(0, Pose(p, R, 0f, r, (float)size[2], (float)size[1]), c); break;                           // ellipsoid
                case 6: Add(2, Pose(p, R, 0f, 2f * r, 2f * (float)size[2], 2f * (float)size[1]), c); break;             // box
                case 5: Add(1, Pose(p, R, 0f, r, (float)size[1], r), c); break;                                       // cylinder
                case 3:                                                                                                 // capsule
                    float h = (float)size[1];
                    Add(1, Pose(p, R, 0f, r, h, r), c);
                    Add(0, Pose(p, R, h, r, r, r), c);
                    Add(0, Pose(p, R, -h, r, r, r), c);
                    break;
            }
        }

        void Add(int set, Matrix4x4 pose, Color c)
        {
            int n = _count[set];
            if (n >= _poses[set].Length) return;
            _poses[set][n] = pose;
            _tints[set][n] = c;
            _count[set] = n + 1;
        }

        /// <summary>A geom's frame in Unity's axes, moved `along` its own axis, with Unity-local scales.</summary>
        static Matrix4x4 Pose(double* p, double* R, float along, float sx, float sy, float sz)
        {
            var o = new Matrix4x4();
            for (int i = 0; i < 3; i++)
            {
                double* row = R + 3 * P[i];
                o[i, 0] = (float)row[0] * sx;
                o[i, 1] = (float)row[2] * sy;
                o[i, 2] = (float)row[1] * sz;
                o[i, 3] = (float)(p[P[i]] + row[2] * along);
            }
            o[3, 3] = 1f;
            return o;
        }
    }
}
