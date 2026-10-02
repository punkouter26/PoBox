"""What a compiled model is, in numbers: the thing Unity's own compile of the same body is checked against.

    python tools/export_fingerprint.py --models models/v2        every <name>_solo.xml in the folder

Writes <name>_fingerprint.json beside each model. Everything carries its name and is matched by it, not by index: the MuJoCo
Unity plugin writes its model out of the scene's hierarchy, and nothing says it keeps the trainer's order.
Run it with the MuJoCo the game runs (PYTHONPATH=.mj350), so both sides are compiled by the same library.
"""
from __future__ import annotations

import argparse
import glob
import json
import os

import mujoco

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
O = mujoco.mjtObj


def fingerprint(path: str) -> dict:
    m = mujoco.MjModel.from_xml_path(path)
    name = lambda kind, i: mujoco.mj_id2name(m, kind, i)
    f = lambda a: [float(x) for x in a]
    joints = []          # lists, not dictionaries: Unity's JsonUtility reads the one and not the other
    for j in range(m.njnt):
        q, v = int(m.jnt_qposadr[j]), int(m.jnt_dofadr[j])
        nq = 7 if m.jnt_type[j] == mujoco.mjtJoint.mjJNT_FREE else 1
        joints.append({
            "name": name(O.mjOBJ_JOINT, j), "type": int(m.jnt_type[j]), "body": name(O.mjOBJ_BODY, m.jnt_bodyid[j]), "pos": f(m.jnt_pos[j]),
            "axis": f(m.jnt_axis[j]), "limited": bool(m.jnt_limited[j]), "range": f(m.jnt_range[j]),
            "stiffness": float(m.jnt_stiffness[j]), "damping": float(m.dof_damping[v]),
            "armature": float(m.dof_armature[v]), "frictionloss": float(m.dof_frictionloss[v]),
            "key_qpos": f(m.key_qpos[0][q:q + nq])})
    out = {
        "model": os.path.basename(path), "mujoco": mujoco.__version__,
        "sizes": {k: int(getattr(m, k)) for k in ("nq", "nv", "nu", "nbody", "njnt", "ngeom", "nexclude")},
        "option": {"timestep": float(m.opt.timestep), "integrator": int(m.opt.integrator), "solver": int(m.opt.solver),
                   "cone": int(m.opt.cone), "iterations": int(m.opt.iterations), "ls_iterations": int(m.opt.ls_iterations),
                   "tolerance": float(m.opt.tolerance), "impratio": float(m.opt.impratio), "gravity": f(m.opt.gravity),
                   "disableflags": int(m.opt.disableflags), "enableflags": int(m.opt.enableflags)},
        "bodies": [{
            "name": name(O.mjOBJ_BODY, b), "parent": name(O.mjOBJ_BODY, m.body_parentid[b]), "pos": f(m.body_pos[b]), "quat": f(m.body_quat[b]),
            "mass": float(m.body_mass[b]), "inertia": f(m.body_inertia[b]), "ipos": f(m.body_ipos[b]),
            "iquat": f(m.body_iquat[b])} for b in range(m.nbody)],
        "joints": joints,
        "geoms": [{
            "name": name(O.mjOBJ_GEOM, g), "type": int(m.geom_type[g]), "body": name(O.mjOBJ_BODY, m.geom_bodyid[g]), "size": f(m.geom_size[g]),
            "pos": f(m.geom_pos[g]), "quat": f(m.geom_quat[g]), "friction": f(m.geom_friction[g]),
            "contype": int(m.geom_contype[g]), "conaffinity": int(m.geom_conaffinity[g]), "condim": int(m.geom_condim[g]),
            "margin": float(m.geom_margin[g]), "solref": f(m.geom_solref[g]), "solimp": f(m.geom_solimp[g])}
            for g in range(m.ngeom)],
        "actuators": [{
            "name": name(O.mjOBJ_ACTUATOR, a), "joint": name(O.mjOBJ_JOINT, m.actuator_trnid[a][0]), "gear": float(m.actuator_gear[a][0]),
            "gainprm": f(m.actuator_gainprm[a][:3]), "biasprm": f(m.actuator_biasprm[a][:3]),
            "ctrlrange": f(m.actuator_ctrlrange[a]), "forcerange": f(m.actuator_forcerange[a])} for a in range(m.nu)],
        # The order the policy's actions are written to ctrl in.
        "actuator_order": [name(O.mjOBJ_ACTUATOR, a) for a in range(m.nu)],
        "excludes": [{"a": a, "b": b} for a, b in sorted(sorted([name(O.mjOBJ_BODY, int(s) >> 16), name(O.mjOBJ_BODY, int(s) & 0xFFFF)])
                                                         for s in m.exclude_signature)],
    }
    # Matching by name only works if everything has one, and only one.
    for kind in ("bodies", "joints", "geoms", "actuators"):
        names = [x["name"] for x in out[kind]]
        assert None not in names and len(set(names)) == len(names), f"{path}: unnamed or repeated {kind}"
    assert len(out["excludes"]) == m.nexclude
    return out


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--models", default=os.path.join(HERE, "models"))
    args = ap.parse_args()
    for path in sorted(glob.glob(os.path.join(args.models, "*_solo.xml"))):
        fp = fingerprint(path)
        out = path[:-len("_solo.xml")] + "_fingerprint.json"
        with open(out, "w", encoding="utf-8") as fh:
            json.dump(fp, fh, indent=1)
        mass = sum(b["mass"] for b in fp["bodies"] if b["name"].startswith("a_"))
        print(f"wrote {out}: MuJoCo {fp['mujoco']}, {fp['sizes']}, fighter {mass:.1f} kg, {len(fp['excludes'])} excluded pairs")


if __name__ == "__main__":
    main()
