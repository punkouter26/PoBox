"""Compare the three creatures' STANDING DIFFICULTY from their own MJCF.

Written 2026-09-15 to answer a measured fact: Nick's body learns to stand in
~200-250 PPO iterations, Grandma's and Grandpa's need ~800, under identical
reward weights and identical hyperparameters. If the difference is physical,
it should show up here.

The classic measures of how hard a body is to balance:

- centre of mass height (a taller inverted pendulum is slower to correct)
- the support polygon: how much foot area the CoM has to move out of before
  the body topples, and where the CoM sits inside it
- CoM height / foot length, the shape factor that says "this body is a
  stilt" versus "this body is a brick"
- total mass, and the ankle torque budget per kg, because a body with a
  weak ankle relative to its height cannot recover a lean

Usage: python standing_difficulty.py nick grandma grandpa
Reads Tools/MuJoCo/<name>_torque.xml (the body the trainer uses).
"""
import pathlib
import sys
import xml.etree.ElementTree as ET

import mujoco
import numpy as np

HERE = pathlib.Path(__file__).parent


def foot_geoms(model):
    """Geoms whose body or geom name marks them as a foot, with their extents."""
    feet = []
    for gid in range(model.ngeom):
        name = mujoco.mj_id2name(model, mujoco.mjtObj.mjOBJ_GEOM, gid) or ""
        if not (name.startswith("g_Foot") or "Foot" in name):
            continue
        gtype = model.geom_type[gid]
        size = model.geom_size[gid]
        body = mujoco.mj_id2name(model, mujoco.mjtObj.mjOBJ_BODY, model.geom_bodyid[gid]) or "?"
        pos = model.geom_pos[gid]
        if gtype == mujoco.mjtGeom.mjGEOM_BOX:
            feet.append((name, body, "box", tuple(size[:3]), tuple(pos)))
        elif gtype == mujoco.mjtGeom.mjGEOM_CAPSULE:
            length = 2.0 * float(size[1]) + 2.0 * float(size[0])
            feet.append((name, body, "capsule", (float(size[0]), length), tuple(pos)))
        elif gtype == mujoco.mjtGeom.mjGEOM_SPHERE:
            feet.append((name, body, "sphere", (float(size[0]),), tuple(pos)))
        else:
            feet.append((name, body, "type%d" % gtype, tuple(size[:3]), tuple(pos)))
    return feet


def report(label, path):
    model = mujoco.MjModel.from_xml_path(str(path))
    data = mujoco.MjData(model)
    mujoco.mj_forward(model, data)

    total_mass = float(model.body_mass.sum())
    com = data.subtree_com[0]          # world CoM in the rest pose
    floor_z = 0.0
    com_height = float(com[2]) - floor_z

    # Support polygon proxy: the foot geoms' extents on the floor.
    feet = foot_geoms(model)
    spans = []
    for name, body, kind, dims, pos in feet:
        if kind == "capsule":
            spans.append(dims[1])      # total capsule length
        elif kind == "box":
            spans.append(2.0 * dims[0])
        elif kind == "sphere":
            spans.append(2.0 * dims[0])
    foot_length = max(spans) if spans else float("nan")

    print("== %s" % label)
    print("   file            %s" % path.name)
    print("   total mass      %.2f kg" % total_mass)
    print("   CoM height      %.3f m above floor (rest pose)" % com_height)
    print("   foot geoms      %d  %s" % (len(feet), ", ".join(
        "%s(%s %.3f)" % (n, k, d[0] if d else 0.0) for n, _, k, d, _ in feet)))
    print("   longest foot    %.3f m" % foot_length)
    if foot_length == foot_length:            # not NaN
        print("   CoM/foot ratio  %.2f   (higher = harder: taller stilt on a shorter base)"
              % (com_height / foot_length))

    # Strongest ankle actuators, per kg of body: what a lean must be answered with.
    best = 0.0
    best_name = ""
    for aid in range(model.nu):
        name = mujoco.mj_id2name(model, mujoco.mjtObj.mjOBJ_ACTUATOR, aid) or ""
        if "Foot" not in name and "Shin" not in name:
            continue
        fmax = float(np.abs(model.actuator_forcerange[aid]).max()) if model.actuator_forcelimited[aid] else float("inf")
        if fmax > best:
            best, best_name = fmax, name
    print("   strongest ankle %.1f Nm  (%s)  = %.2f Nm/kg"
          % (best, best_name, best / total_mass))

    # Hinge ranges, to see whether one body simply has more slack to hold.
    slack = 0.0
    for jid in range(model.njnt):
        if model.jnt_type[jid] != mujoco.mjtJoint.mjJNT_HINGE:
            continue
        if not model.jnt_limited[jid]:
            continue
        lo, hi = model.jnt_range[jid]
        slack += float(hi - lo)
    print("   total joint slack %.1f deg over %d hinges" % (np.degrees(slack), model.njnt - 1))
    print()


def main():
    names = sys.argv[1:] or ["nick", "grandma", "grandpa"]
    for name in names:
        path = HERE / ("%s_torque.xml" % name)
        if not path.exists():
            print("missing %s" % path)
            continue
        report(name, path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
