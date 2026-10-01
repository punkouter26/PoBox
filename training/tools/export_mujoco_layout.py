"""Everything Unity needs to run the match on MuJoCo itself rather than on its own physics.

    python tools/export_mujoco_layout.py --a matt --b zombie

Why: the policies are trained in MuJoCo, and in Unity's physics the same policies, fed the same
observations to three decimal places, fall over in most episodes (measured 2026-10-01: fall rate 0.7 to
0.9 against 0.00 in MuJoCo). Rather than chase the differences between two engines, the game loads
mujoco.dll and steps the very model the fighters were trained in. Unity draws it.

Writes, and training reads none of it:

models/<a>_vs_<b>_ring.xml
    The match model with the ring's ropes and corner posts added, which training does without.

models/<a>_vs_<b>_layout.json
    Where things are: which slots of the state vector are which fighter's joints, which body and shape
    numbers are its pelvis, head, gloves and feet, its guard pose, limits and gains. And four numbers a
    C program cannot get from MuJoCo's API: how far into the mjData structure the pointers to the body
    and shape positions sit. Those are found here by looking, and they belong to one build of the library
    (the version is recorded, and Unity checks a known position at start-up before trusting them).

The library itself is mujoco.dll from this Python environment's mujoco package; Unity's importer copies it.
"""
from __future__ import annotations

import argparse
import ctypes
import json
import os
import re
import sys

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

ROPE_HEIGHTS = (0.45, 0.75, 1.05, 1.35)      # as PoBoxBuilder builds them


def with_ropes(text: str, half: float) -> str:
    """Unity's x, y, z are MuJoCo's x, z, y; the ring is square, so the ropes look the same in both."""
    g = []
    for i, h in enumerate(ROPE_HEIGHTS):
        for name, a, b in (("n", (-half, half), (half, half)), ("s", (-half, -half), (half, -half)),
                           ("e", (half, -half), (half, half)), ("w", (-half, -half), (-half, half))):
            # A rope gives: a slower contact than the default, so a body leaning on it is let down, not bounced.
            g.append(f'    <geom name="rope_{name}_{i}" type="capsule" fromto="{a[0]} {a[1]} {h} {b[0]} {b[1]} {h}" size="0.03" '
                     f'solref="0.05 1" contype="3" conaffinity="3" rgba="0.86 0.86 0.84 1"/>')
    for name, x, y in (("ne", half, half), ("nw", -half, half), ("se", half, -half), ("sw", -half, -half)):
        g.append(f'    <geom name="post_{name}" type="capsule" fromto="{x} {y} 0 {x} {y} 1.45" size="0.07" contype="3" conaffinity="3" rgba="0.3 0.3 0.35 1"/>')
    assert "</worldbody>" in text
    # The canvas, ropes and posts collide with layer 1 and layer 2. A fighter is on layer 1; the game moves
    # one that is down for a count to layer 2, where it still lies on the canvas but the fighter left
    # standing cannot trip over it on the way to a neutral corner.
    assert '<geom name="floor" type="plane"' in text
    text = text.replace('<geom name="floor" type="plane"', '<geom name="floor" type="plane" contype="3" conaffinity="3"', 1)
    return text.replace("</worldbody>", "\n".join(g) + "\n  </worldbody>", 1)


def pointer_offset(d, array: np.ndarray) -> int:
    """How many bytes into mjData (or mjModel) the pointer to this array is stored."""
    want = array.ctypes.data
    base = d._address
    step = 4096
    for start in range(0, 8 << 20, step):
        try:
            words = (ctypes.c_uint64 * (step // 8)).from_address(base + start)
            for i in range(step // 8):
                if words[i] == want:
                    return start + i * 8
        except OSError:
            break
    raise RuntimeError("pointer not found in mjData")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--a", default="matt")
    ap.add_argument("--b", default="zombie")
    args = ap.parse_args()

    stem = os.path.join(HERE, "models", f"{args.a}_vs_{args.b}")
    cfg = json.load(open(stem + "_policy_config.json", encoding="utf-8"))
    half = float(cfg.get("ring_half", 3.05))
    xml = with_ropes(open(stem + "_spar.xml", encoding="utf-8").read(), half)
    open(stem + "_ring.xml", "w", encoding="utf-8").write(xml)

    m = mujoco.MjModel.from_xml_string(xml)
    d = mujoco.MjData(m)
    mujoco.mj_resetDataKeyframe(m, d, 0)
    mujoco.mj_forward(m, d)

    J, G, B, A = mujoco.mjtObj.mjOBJ_JOINT, mujoco.mjtObj.mjOBJ_GEOM, mujoco.mjtObj.mjOBJ_BODY, mujoco.mjtObj.mjOBJ_ACTUATOR
    fighters = []
    for k, p in enumerate(("a_", "b_")):
        c = cfg["fighters"][k]
        order = c["joint_order"]
        root = mujoco.mj_name2id(m, J, p + "root")
        js = [mujoco.mj_name2id(m, J, p + n) for n in order]
        acts = [mujoco.mj_name2id(m, A, p + n) for n in order]
        assert acts == list(range(k * len(order), (k + 1) * len(order))), "actuators are not in joint order"
        gid = lambda n: mujoco.mj_name2id(m, G, p + n)
        feet = [gid("foot_l_geom"), gid("foot_r_geom")]
        fighters.append({
            "name": cfg["names"][k],
            "root_q": int(m.jnt_qposadr[root]), "root_v": int(m.jnt_dofadr[root]),
            "jq": [int(m.jnt_qposadr[j]) for j in js], "jv": [int(m.jnt_dofadr[j]) for j in js],
            "ctrl": k * len(order),
            "pelvis": mujoco.mj_name2id(m, B, p + "pelvis"),
            "head": gid("head_geom"), "torso": gid("torso_geom"),
            "glove_l": gid("glove_l"), "glove_r": gid("glove_r"), "foot_l": feet[0], "foot_r": feet[1],
            "geoms": [g for g in range(m.ngeom) if (mujoco.mj_id2name(m, G, g) or "").startswith(p)],
            "foot_half_l": [float(x) for x in m.geom_size[feet[0]]], "foot_half_r": [float(x) for x in m.geom_size[feet[1]]],
            "stand": float(m.key_qpos[0][m.jnt_qposadr[root] + 2]),
            "default_pos": [float(m.key_qpos[0][m.jnt_qposadr[j]]) for j in js],
            "lower": [float(x) for x in c["lower"]], "upper": [float(x) for x in c["upper"]],
            "kp": [float(m.actuator_gainprm[a, 0]) for a in acts],
            "kv": [float(-m.actuator_biasprm[a, 2]) for a in acts],
            "limit": [float(m.actuator_forcerange[a, 1]) for a in acts],
        })

    pelvis = fighters[0]["pelvis"]
    layout = {
        "mujoco": mujoco.__version__, "version_number": int(mujoco.mj_version()),
        "nq": int(m.nq), "nv": int(m.nv), "nu": int(m.nu), "nbody": int(m.nbody), "ngeom": int(m.ngeom),
        "timestep": float(m.opt.timestep), "ring_half": half,
        "off_xpos": pointer_offset(d, d.xpos), "off_xquat": pointer_offset(d, d.xquat),
        "off_geom_xpos": pointer_offset(d, d.geom_xpos), "off_geom_xmat": pointer_offset(d, d.geom_xmat),
        "off_actuator_force": pointer_offset(d, d.actuator_force),
        # In mjModel, not mjData: which layer each shape is on and which layers it collides with.
        "off_geom_contype": pointer_offset(m, m.geom_contype), "off_geom_conaffinity": pointer_offset(m, m.geom_conaffinity),
        "fighters": fighters,
        "key_qpos": [float(x) for x in m.key_qpos[0]],
        # A position Unity can check the offsets against before it trusts them: fighter A's left glove in the keyframe.
        "check_geom": fighters[0]["glove_l"], "check_xpos": [float(x) for x in d.geom_xpos[fighters[0]["glove_l"]]],
        "check_body_xpos": [float(x) for x in d.xpos[pelvis]],
    }
    json.dump(layout, open(stem + "_layout.json", "w", encoding="utf-8"), indent=1)
    print(f"wrote {stem}_ring.xml: {m.ngeom} shapes, {m.nbody} bodies")
    print(f"wrote {stem}_layout.json: MuJoCo {layout['mujoco']} (mj_version {layout['version_number']}), nq {m.nq} nv {m.nv} nu {m.nu}; "
          f"pointers at xpos {layout['off_xpos']}, xquat {layout['off_xquat']}, geom_xpos {layout['off_geom_xpos']}, geom_xmat {layout['off_geom_xmat']}, actuator_force {layout['off_actuator_force']}")
    print(f"library: {os.path.join(os.path.dirname(mujoco.__file__), 'mujoco.dll')}")


if __name__ == "__main__":
    main()
