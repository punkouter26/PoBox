using System;
using System.Collections.Generic;
using Mujoco;
using UnityEngine;

namespace PoBox.Mj
{
    /// <summary>
    /// What a compiled MuJoCo model is, in numbers, as the trainer wrote it down
    /// (training/tools/export_fingerprint.py), and the check that the model the MuJoCo plugin compiled
    /// out of the Unity scene is the same one. Everything is matched by name: the plugin writes its model
    /// from the scene's hierarchy and need not keep the trainer's order.
    ///
    /// Two things are compared by what they mean and not by their numbers, because the same shape can be
    /// written two ways: a body's inertia (as the tensor in the body's frame, not as principal axes), and
    /// a shape's turn (a capsule by its axis, a ball not at all, a box up to half turns).
    /// </summary>
    [Serializable]
    public class ModelFingerprint
    {
        [Serializable] public class Sizes { public int nq, nv, nu, nbody, njnt, ngeom, nexclude; }
        [Serializable] public class Option
        {
            public double timestep, tolerance, impratio;
            public int integrator, solver, cone, iterations, ls_iterations, disableflags, enableflags;
            public double[] gravity;
        }
        [Serializable] public class Body { public string name, parent; public double mass; public double[] pos, quat, inertia, ipos, iquat; }
        [Serializable] public class Joint
        {
            public string name, body; public int type; public bool limited;
            public double stiffness, damping, armature, frictionloss;
            public double[] pos, axis, range, key_qpos;
        }
        [Serializable] public class Geom
        {
            public string name, body; public int type, contype, conaffinity, condim; public double margin;
            public double[] size, pos, quat, friction, solref, solimp;
        }
        [Serializable] public class Actuator { public string name, joint; public double gear; public double[] gainprm, biasprm, ctrlrange, forcerange; }
        [Serializable] public class Pair { public string a, b; }

        public string model, mujoco;
        public Sizes sizes;
        public Option option;
        public Body[] bodies;
        public Joint[] joints;
        public Geom[] geoms;
        public Actuator[] actuators;
        public string[] actuator_order;
        public Pair[] excludes;

        const int Plane = 0, Sphere = 2, Capsule = 3;      // mjtGeom
        const int NGain = 10, NBias = 10, NGear = 6;       // mjNGAIN, mjNBIAS, a gear's length

        public static ModelFingerprint FromJson(string json) => JsonUtility.FromJson<ModelFingerprint>(json);

        /// <summary>Every way the model differs from this fingerprint; empty when it is the same model.</summary>
        public unsafe List<string> Differences(MujocoLib.mjModel_* m, double tol = 1e-6)
        {
            var d = new List<string>();
            int Id(MujocoLib.mjtObj kind, string name) => MujocoLib.mj_name2id(m, (int)kind, name);
            bool Near(double a, double b) => Math.Abs(a - b) <= tol * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
            void Num(string what, double want, double got) { if (!Near(want, got)) d.Add($"{what}: {want:R} in training, {got:R} here"); }
            void Vec(string what, double[] want, double* got, int n)
            {
                for (int i = 0; i < n; i++)
                    if (!Near(want[i], got[i])) { d.Add($"{what}[{i}]: {want[i]:R} in training, {got[i]:R} here"); return; }
            }

            Num("nq", sizes.nq, m->nq); Num("nv", sizes.nv, m->nv); Num("nu", sizes.nu, m->nu);
            Num("nbody", sizes.nbody, m->nbody); Num("njnt", sizes.njnt, m->njnt); Num("ngeom", sizes.ngeom, m->ngeom);
            Num("nexclude", sizes.nexclude, m->nexclude);

            Num("timestep", option.timestep, m->opt.timestep); Num("tolerance", option.tolerance, m->opt.tolerance);
            Num("impratio", option.impratio, m->opt.impratio); Num("integrator", option.integrator, m->opt.integrator);
            Num("solver", option.solver, m->opt.solver); Num("cone", option.cone, m->opt.cone);
            Num("iterations", option.iterations, m->opt.iterations); Num("ls_iterations", option.ls_iterations, m->opt.ls_iterations);
            Num("disableflags", option.disableflags, m->opt.disableflags); Num("enableflags", option.enableflags, m->opt.enableflags);
            Vec("gravity", option.gravity, m->opt.gravity, 3);

            foreach (var b in bodies)
            {
                int i = Id(MujocoLib.mjtObj.mjOBJ_BODY, b.name);
                if (i < 0) { d.Add($"body {b.name}: not in the model"); continue; }
                if (i > 0 && m->body_parentid[i] != Id(MujocoLib.mjtObj.mjOBJ_BODY, b.parent)) d.Add($"body {b.name}: its parent is not {b.parent}");
                Num($"body {b.name} mass", b.mass, m->body_mass[i]);
                Vec($"body {b.name} pos", b.pos, m->body_pos + 3 * i, 3);
                if (!SameTurn(b.quat, m->body_quat + 4 * i, tol)) d.Add($"body {b.name}: turned differently");
                Vec($"body {b.name} centre of mass", b.ipos, m->body_ipos + 3 * i, 3);
                var want = Tensor(b.inertia, b.iquat);
                var got = Tensor(new[] { m->body_inertia[3 * i], m->body_inertia[3 * i + 1], m->body_inertia[3 * i + 2] },
                                 new[] { m->body_iquat[4 * i], m->body_iquat[4 * i + 1], m->body_iquat[4 * i + 2], m->body_iquat[4 * i + 3] });
                for (int k = 0; k < 9; k++)
                    if (!Near(want[k], got[k])) { d.Add($"body {b.name} inertia[{k}]: {want[k]:R} in training, {got[k]:R} here"); break; }
            }

            foreach (var j in joints)
            {
                int i = Id(MujocoLib.mjtObj.mjOBJ_JOINT, j.name);
                if (i < 0) { d.Add($"joint {j.name}: not in the model"); continue; }
                int dof = m->jnt_dofadr[i];
                Num($"joint {j.name} type", j.type, m->jnt_type[i]);
                if (m->jnt_bodyid[i] != Id(MujocoLib.mjtObj.mjOBJ_BODY, j.body)) d.Add($"joint {j.name}: not on body {j.body}");
                Vec($"joint {j.name} pos", j.pos, m->jnt_pos + 3 * i, 3);
                Vec($"joint {j.name} axis", j.axis, m->jnt_axis + 3 * i, 3);
                Num($"joint {j.name} limited", j.limited ? 1 : 0, m->jnt_limited[i]);
                if (j.limited) Vec($"joint {j.name} range", j.range, m->jnt_range + 2 * i, 2);
                Num($"joint {j.name} stiffness", j.stiffness, m->jnt_stiffness[i]);
                Num($"joint {j.name} damping", j.damping, m->dof_damping[dof]);
                Num($"joint {j.name} armature", j.armature, m->dof_armature[dof]);
                Num($"joint {j.name} frictionloss", j.frictionloss, m->dof_frictionloss[dof]);
            }

            foreach (var g in geoms)
            {
                int i = Id(MujocoLib.mjtObj.mjOBJ_GEOM, g.name);
                if (i < 0) { d.Add($"shape {g.name}: not in the model"); continue; }
                Num($"shape {g.name} type", g.type, m->geom_type[i]);
                if (m->geom_bodyid[i] != Id(MujocoLib.mjtObj.mjOBJ_BODY, g.body)) d.Add($"shape {g.name}: not on body {g.body}");
                // A plane's size is how it is drawn, not what it is.
                if (g.type != Plane) Vec($"shape {g.name} size", g.size, m->geom_size + 3 * i, g.type == Sphere ? 1 : g.type == Capsule ? 2 : 3);
                Vec($"shape {g.name} pos", g.pos, m->geom_pos + 3 * i, 3);
                if (g.type != Sphere && !SameShapeTurn(g.type, g.quat, m->geom_quat + 4 * i, tol)) d.Add($"shape {g.name}: turned differently");
                Vec($"shape {g.name} friction", g.friction, m->geom_friction + 3 * i, 3);
                Num($"shape {g.name} contype", g.contype, m->geom_contype[i]); Num($"shape {g.name} conaffinity", g.conaffinity, m->geom_conaffinity[i]);
                Num($"shape {g.name} condim", g.condim, m->geom_condim[i]); Num($"shape {g.name} margin", g.margin, m->geom_margin[i]);
                Vec($"shape {g.name} solref", g.solref, m->geom_solref + 2 * i, 2);
                Vec($"shape {g.name} solimp", g.solimp, m->geom_solimp + 5 * i, 5);
            }

            for (int k = 0; k < actuators.Length; k++)
            {
                var a = actuators[k];
                int i = Id(MujocoLib.mjtObj.mjOBJ_ACTUATOR, a.name);
                if (i < 0) { d.Add($"drive {a.name}: not in the model"); continue; }
                // The policy's k-th action goes to the k-th drive: the order is part of the contract.
                if (i != Array.IndexOf(actuator_order, a.name)) d.Add($"drive {a.name}: number {i} here, {Array.IndexOf(actuator_order, a.name)} in training");
                if (m->actuator_trnid[2 * i] != Id(MujocoLib.mjtObj.mjOBJ_JOINT, a.joint)) d.Add($"drive {a.name}: not on joint {a.joint}");
                Num($"drive {a.name} gear", a.gear, m->actuator_gear[NGear * i]);
                Vec($"drive {a.name} gain", a.gainprm, m->actuator_gainprm + NGain * i, 3);
                Vec($"drive {a.name} bias", a.biasprm, m->actuator_biasprm + NBias * i, 3);
                Vec($"drive {a.name} ctrlrange", a.ctrlrange, m->actuator_ctrlrange + 2 * i, 2);
                Vec($"drive {a.name} forcerange", a.forcerange, m->actuator_forcerange + 2 * i, 2);
            }

            var have = new HashSet<int>();
            for (int i = 0; i < m->nexclude; i++) have.Add(m->exclude_signature[i]);
            foreach (var p in excludes)
            {
                int a = Id(MujocoLib.mjtObj.mjOBJ_BODY, p.a), b = Id(MujocoLib.mjtObj.mjOBJ_BODY, p.b);
                if (!have.Contains((a << 16) + b) && !have.Contains((b << 16) + a)) d.Add($"{p.a} and {p.b} are not kept from colliding");
            }
            return d;
        }

        static double[] Matrix(double[] q)
        {
            double w = q[0], x = q[1], y = q[2], z = q[3];
            return new[]
            {
                1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y),
                2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x),
                2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y),
            };
        }

        /// <summary>R diag(I) R^T: the inertia in the body's own frame, whichever way its principal axes are named.</summary>
        static double[] Tensor(double[] inertia, double[] iquat)
        {
            var r = Matrix(iquat);
            var t = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    for (int k = 0; k < 3; k++)
                        t[3 * i + j] += r[3 * i + k] * inertia[k] * r[3 * j + k];
            return t;
        }

        static unsafe bool SameTurn(double[] want, double* got, double tol)
        {
            double dot = 0;
            for (int i = 0; i < 4; i++) dot += want[i] * got[i];
            return Math.Abs(Math.Abs(dot) - 1.0) <= 10 * tol;
        }

        static unsafe bool SameShapeTurn(int type, double[] want, double* got, double tol)
        {
            var a = Matrix(want);
            var b = Matrix(new[] { got[0], got[1], got[2], got[3] });
            for (int i = 0; i < 9; i++)
            {
                if (type == Capsule && i % 3 != 2) continue;                 // only where its axis points
                double x = a[i], y = b[i];
                if (type != Plane) { x = Math.Abs(x); y = Math.Abs(y); }     // either way up, either way round
                if (Math.Abs(x - y) > 100 * tol) return false;
            }
            return true;
        }
    }
}
