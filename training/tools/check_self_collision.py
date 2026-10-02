"""House rule, checked before training: a fighter collides with itself, and none of its parts touch each
other in the T-pose, in its guard, or through a normal swing of the arms and legs.

    python tools/check_self_collision.py --name matt --name zombie

Reads models/<name>_bag.xml. Prints which pairs of body parts can collide at all (every pair except the
ones joined by a joint, which overlap by construction and are held apart by their joint limits), then
puts the fighter through the poses and lists any pair found touching. Exits 1 if there is one.
"""
from __future__ import annotations

import argparse
import itertools
import math
import os
import sys

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def touching(m, d, prefix: str):
    """Pairs of the fighter's own shapes in contact (penetrating, not merely within the margin)."""
    out = []
    for i in range(d.ncon):
        c = d.contact[i]
        a = mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_GEOM, c.geom1) or ""
        b = mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_GEOM, c.geom2) or ""
        if a.startswith(prefix) and b.startswith(prefix) and c.dist < 0.0:
            out.append((a[len(prefix):], b[len(prefix):], float(c.dist)))
    return out


def check(name: str) -> int:
    path = os.path.join(HERE, "models", f"{name}_bag.xml")
    m = mujoco.MjModel.from_xml_path(path)
    d = mujoco.MjData(m)
    J = mujoco.mjtObj.mjOBJ_JOINT
    prefix = "a_"

    # Which shapes are the fighter's, what they are, and which pairs the model lets collide.
    kinds = {mujoco.mjtGeom.mjGEOM_CAPSULE: "capsule", mujoco.mjtGeom.mjGEOM_SPHERE: "sphere", mujoco.mjtGeom.mjGEOM_BOX: "box"}
    geoms = [g for g in range(m.ngeom) if (mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_GEOM, g) or "").startswith(prefix)]
    odd = [mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_GEOM, g) for g in geoms if int(m.geom_type[g]) not in kinds]
    bodies = sorted({int(m.geom_bodyid[g]) for g in geoms})
    excluded = {(int(m.exclude_signature[i]) >> 16, int(m.exclude_signature[i]) & 0xFFFF) for i in range(m.nexclude)}
    pairs = [(a, b) for a, b in itertools.combinations(bodies, 2)]
    colliding = [(a, b) for a, b in pairs if (a, b) not in excluded and (b, a) not in excluded]
    parent_child = [(a, b) for a, b in pairs if m.body_parentid[b] == a or m.body_parentid[a] == b]
    not_parent_child_excluded = [(a, b) for a, b in pairs if ((a, b) in excluded or (b, a) in excluded) and (a, b) not in parent_child]
    bn = lambda b: (mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_BODY, b) or "")[len(prefix):]
    print(f"{name}: {len(geoms)} shapes ({', '.join(sorted({kinds.get(int(m.geom_type[g]), 'other') for g in geoms}))}) on {len(bodies)} body parts; "
          f"{len(colliding)} of {len(pairs)} pairs of parts collide, {len(pairs) - len(colliding)} are excluded (joined by a joint)")
    if odd:
        print(f"  NOT simple shapes: {odd}")
    if not_parent_child_excluded:
        print("  excluded although not parent and child: " + ", ".join(f"{bn(a)}-{bn(b)}" for a, b in not_parent_child_excluded))

    def qadr(joint: str) -> int:
        return int(m.jnt_qposadr[mujoco.mj_name2id(m, J, prefix + joint)])

    root = qadr("root")
    key = m.key_qpos[0].copy()

    def pose(joints: dict, base: np.ndarray) -> np.ndarray:
        q = base.copy()
        for j, deg in joints.items():
            q[qadr(j)] = math.radians(deg)
        return q

    tpose = key.copy()
    for j in range(m.njnt):
        jn = mujoco.mj_id2name(m, J, j) or ""
        if jn.startswith(prefix) and jn != prefix + "root":
            tpose[int(m.jnt_qposadr[j])] = 0.0
    tpose[root + 2] += 0.3      # clear of the floor: this is about the body against itself

    poses = [("T-pose", tpose), ("guard", key)]
    # A normal swing: the walk and the punch. Arms swing forward and back with the elbow working; a
    # straight punch with each hand from the guard; legs stride with the knee bending behind.
    for t in np.linspace(0.0, 1.0, 9):
        s = math.sin(t * 2.0 * math.pi)
        poses.append((f"walk {t:.2f}", pose({
            "hip_y_l": -14 - 28 * s, "hip_y_r": -14 + 28 * s, "knee_l": 26 + 30 * max(0.0, s), "knee_r": 26 + 30 * max(0.0, -s),
            "shoulder_x_l": -76, "shoulder_x_r": 76, "shoulder_z_l": -30 * s, "shoulder_z_r": -30 * s,
            "elbow_l": -40 - 30 * max(0.0, -s), "elbow_r": 40 + 30 * max(0.0, s)}, key)))
    for hand, sign in (("l", 1.0), ("r", -1.0)):
        for t in np.linspace(0.0, 1.0, 7):
            poses.append((f"punch {hand} {t:.2f}", pose({
                f"shoulder_x_{hand}": sign * (-76 + 20 * t), f"shoulder_z_{hand}": sign * (-42 - 48 * t),
                f"elbow_{hand}": sign * (-112 + 104 * t), "abdomen_z": sign * -20 * t}, key)))

    found = {}
    for label, q in poses:
        mujoco.mj_resetData(m, d)
        d.qpos[:] = q
        if label != "T-pose" and label != "guard":
            d.qpos[root + 2] = key[root + 2] + 0.3
        mujoco.mj_forward(m, d)
        for a, b, dist in touching(m, d, prefix):
            found.setdefault((a, b), []).append((label, dist))
    if not found:
        print(f"  no part touches another in {len(poses)} poses: T-pose, guard, {len(poses) - 2} steps of walking and punching")
        return 0
    for (a, b), hits in sorted(found.items()):
        worst = min(h[1] for h in hits)
        print(f"  TOUCHING {a} and {b} in {len(hits)} pose(s), deepest {-worst * 1000:.0f} mm: {', '.join(h[0] for h in hits[:4])}{' ...' if len(hits) > 4 else ''}")
    return 1


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", action="append", default=[])
    args = ap.parse_args()
    bad = sum(check(n) for n in (args.name or ["matt", "zombie"]))
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
