"""Derive an authored MuJoCo body from a rigged GLB, the way Nick's was.

    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/make_creature_mjcf.py \
        --glb Assets/Art/2026_GrandmaRigged.glb --name grandma \
        --out Tools/MuJoCo/grandma.xml

THE RULE, LEARNED FROM NICK. Assets/MuJoCoCreature/Model/creature.xml was built
from RIGGED_Nick.glb by placing each of the 15 physics bodies on one bone of the
24-bone armature -- Hips, Spine02, neck, UpLeg, Leg, Foot, Arm, ForeArm, Hand
-- and running each capsule from that bone to the next one down the chain
(the head capsule ends at head_end, the foot capsule at ToeBase). glTF is
Y-up and MuJoCo Z-up, so (x, y, z) becomes (x, -z, y), and the whole body is
lifted so the lowest toe capsule just touches the floor. Re-running this on
Nick's own GLB reproduces creature.xml's body positions to the millimetre
(see --check), which is the evidence the rule is right.

THE REST POSE IS THE BIND POSE. Every joint's zero is the pose the artist
bound the mesh in, because SkinnedRigBinder bakes the physics-to-bone offset
there and a physics rest pose that differed from it would put the collision
capsules somewhere other than the visible limbs. Grandma and Grandpa bind with
their arms hanging and their elbows slightly bent where Nick T-poses, so the
joint ranges and control ranges Nick's file carries are RE-CENTRED per joint:
the angle that turns Nick's segment into the new character's is solved
numerically (and checked against MuJoCo's own joint composition), and the
range is shifted by it so the two bodies cover the same absolute arc. Where
the new bind pose lies outside Nick's arc entirely, the range is widened by a
margin so the rest pose stays legal.

MASS AND SIZE scale with stature: total mass keeps Nick's 75 kg / 1.65 m^2, split
in Nick's segment fractions; capsule radii scale with height. Realism budgets
for joint torque and speed are NOT applied here -- they go on the MJCF Unity
exports, by prepare_human_limits.py, so the game body and the training body
get them from one place.
"""
from __future__ import annotations

import argparse
import json
import math
import struct
import xml.etree.ElementTree as ET
from pathlib import Path

import mujoco
import numpy as np

HERE = Path(__file__).parent
REPO = HERE.parent.parent
NICK_GLB = REPO / "Assets/MuJoCoCreature/Model/RIGGED_Nick.glb"
NICK_XML = REPO / "Assets/MuJoCoCreature/Model/creature.xml"
NICK_HEIGHT = 1.65
NICK_MASS = 75.0

# body -> (bone it sits on, bone its capsule runs to; None for a sphere)
BODY_BONES = {
    "Pelvis": ("Hips", "Spine02"),
    "Torso": ("Spine02", "neck"),
    "Head": ("neck", "head_end"),
    "UpperArmL": ("LeftArm", "LeftForeArm"),
    "ForearmL": ("LeftForeArm", "LeftHand"),
    "GloveL": ("LeftHand", None),
    "UpperArmR": ("RightArm", "RightForeArm"),
    "ForearmR": ("RightForeArm", "RightHand"),
    "GloveR": ("RightHand", None),
    "ThighL": ("LeftUpLeg", "LeftLeg"),
    "ShinL": ("LeftLeg", "LeftFoot"),
    "FootL": ("LeftFoot", "LeftToeBase"),
    "ThighR": ("RightUpLeg", "RightLeg"),
    "ShinR": ("RightLeg", "RightFoot"),
    "FootR": ("RightFoot", "RightToeBase"),
}
TREE = {
    "Pelvis": ["Torso", "ThighL", "ThighR"],
    "Torso": ["Head", "UpperArmL", "UpperArmR"],
    "UpperArmL": ["ForearmL"], "ForearmL": ["GloveL"],
    "UpperArmR": ["ForearmR"], "ForearmR": ["GloveR"],
    "ThighL": ["ShinL"], "ShinL": ["FootL"],
    "ThighR": ["ShinR"], "ShinR": ["FootR"],
}
# Below this, a joint's shift is noise and the range is left as Nick's.
SHIFT_DEADBAND_DEG = 2.0
# How far past the bind pose a range is widened when the bind pose falls
# outside Nick's arc, degrees. Arms bind far from the T-pose; the rest do not.
MARGIN_DEG = {"UpperArm": 20.0, "Forearm": 20.0, "default": 10.0}


# ------------------------------------------------------------------ GLB
def load_glb_json(path: Path) -> dict:
    b = path.read_bytes()
    clen, ctype = struct.unpack_from("<II", b, 12)
    assert ctype == 0x4E4F534A, "first GLB chunk is not JSON"
    return json.loads(b[20:20 + clen])


def quat_to_mat(q):
    x, y, z, w = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def bind_pose(glb: dict) -> tuple[dict, float]:
    """World position (MuJoCo axes, metres) of every skin joint, and the mesh height."""
    nodes = glb["nodes"]
    parent = {c: i for i, n in enumerate(nodes) for c in n.get("children", [])}
    cache = {}

    def world(i):
        if i not in cache:
            n = nodes[i]
            m = np.eye(4)
            m[:3, :3] = quat_to_mat(n.get("rotation", [0, 0, 0, 1])) * np.array(n.get("scale", [1, 1, 1]))
            m[:3, 3] = n.get("translation", [0, 0, 0])
            cache[i] = (world(parent[i]) @ m) if i in parent else m
        return cache[i]

    joints = {}
    for j in glb["skins"][0]["joints"]:
        p = world(j)[:3, 3]
        joints[nodes[j]["name"]] = np.array([p[0], -p[2], p[1]])   # Y-up -> Z-up
    height = max(g["accessors"][prim["attributes"]["POSITION"]]["max"][1]
                 for g in [glb] for m in g["meshes"] for prim in m["primitives"])
    return joints, float(height)


# ------------------------------------------------------------------ template
def parse_template():
    """Nick's authored body: per-body joints/ranges, actuator gains, radii, masses."""
    root = ET.parse(NICK_XML).getroot()
    bodies = {}

    def walk(el):
        for b in el.findall("body"):
            g = b.find("geom")
            bodies[b.get("name")] = {
                "joints": [(j.get("name"), j.get("axis"), [float(v) for v in j.get("range").split()])
                           for j in b.findall("joint")],
                "radius": float(g.get("size")),
                "mass": float(g.get("mass")),
                "sphere": g.get("type") == "sphere",
            }
            walk(b)
    walk(root.find("worldbody"))
    actuators = {a.get("joint"): {"kp": a.get("kp"), "name": a.get("name")}
                 for a in root.find("actuator")}
    return root, bodies, actuators


# ------------------------------------------------------------------ joint re-centring
def rot_x(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])


def rot_y(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


def rot_z(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])


ROT = {"1 0 0": rot_x, "0 1 0": rot_y, "0 0 1": rot_z}


def compose(axes, q):
    """MuJoCo composes several hinges in one body intrinsically, in listed order."""
    m = np.eye(3)
    for axis, angle in zip(axes, q):
        m = m @ ROT[axis](angle)
    return m


def solve_joint_angles(axes, d0, d1):
    """Angles on the given hinge axes that turn direction d0 into d1.

    Only the axes that can move the segment are solved (a yaw about the
    segment itself cannot be seen from its direction and stays 0).
    Coarse grid then coordinate refinement -- two unknowns at most, so a
    library optimiser would be more code than the search.
    """
    d0 = d0 / np.linalg.norm(d0)
    d1 = d1 / np.linalg.norm(d1)
    movable = [i for i, ax in enumerate(axes) if abs(np.dot(np.array([float(v) for v in ax.split()]), d0)) < 0.9]
    if not movable:
        return [0.0] * len(axes)

    def err(q):
        # A pair of ~180 degree turns about two axes leaves a segment that
        # lies along the third axis where it was, so the residual alone has
        # degenerate minima. The small penalty on the angles picks the
        # minimal turn among them without moving a genuine solution.
        return float(np.linalg.norm(compose(axes, q) @ d0 - d1)) + 0.002 * sum(abs(a) for a in q)

    best = [0.0] * len(axes)
    best_e = err(best)
    # Bounded: no bind pose here differs from Nick's by more than an arm's
    # hang (~75 degrees), and beyond +-95 the paired-180 degeneracy above
    # can out-score the honest small turn on a near-vertical segment.
    grid = np.deg2rad(np.arange(-95, 96, 5))
    if len(movable) == 1:
        for a in grid:
            q = [0.0] * len(axes); q[movable[0]] = a
            e = err(q)
            if e < best_e: best, best_e = q, e
    else:
        i, j = movable[:2]
        for a in grid:
            for b in grid:
                q = [0.0] * len(axes); q[i] = a; q[j] = b
                e = err(q)
                if e < best_e: best, best_e = q, e
    step = math.radians(2.5)
    for _ in range(40):
        improved = False
        for k in movable[:2]:
            for sign in (-1, 1):
                q = list(best); q[k] += sign * step
                e = err(q)
                if e < best_e:
                    best, best_e, improved = q, e, True
        if not improved:
            step *= 0.5
    return best


def check_composition(nick_bodies, body, q_deg):
    """Set Nick's joints on `body` to q and confirm MuJoCo turns the segment the same way."""
    m = mujoco.MjModel.from_xml_path(str(NICK_XML))
    d = mujoco.MjData(m)
    axes = [ax for _, ax, _ in nick_bodies[body]["joints"]]
    # The body's own capsule centre lies on its fromto line, so it gives the
    # segment direction for leaves (Head, Foot) as well as for bodies with a
    # child.
    bid = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, body)
    gid = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, "g_" + body)
    mujoco.mj_forward(m, d)
    d0 = d.geom_xpos[gid] - d.xpos[bid]
    for (jname, _, _), qv in zip(nick_bodies[body]["joints"], q_deg):
        jid = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, jname)
        d.qpos[m.jnt_qposadr[jid]] = math.radians(qv)
    mujoco.mj_forward(m, d)
    d1 = d.geom_xpos[gid] - d.xpos[bid]
    predicted = compose(axes, [math.radians(v) for v in q_deg]) @ d0
    return float(np.linalg.norm(predicted - d1))


# ------------------------------------------------------------------ build
def build(glb_path: Path, name: str, out: Path, check: bool):
    nick_root, nick_bodies, nick_actuators = parse_template()
    nick_joints, _ = bind_pose(load_glb_json(NICK_GLB))
    joints, height = bind_pose(load_glb_json(glb_path))

    scale = height / NICK_HEIGHT
    total_mass = NICK_MASS * scale * scale
    # Lift so the lowest toe capsule surface sits on z = 0, as creature.xml does.
    foot_r = nick_bodies["FootL"]["radius"] * scale
    lift = foot_r - min(joints["LeftToeBase"][2], joints["RightToeBase"][2])
    world = {b: joints[bone] + np.array([0, 0, lift]) for b, (bone, _) in BODY_BONES.items()}
    ends = {b: (joints[end] + np.array([0, 0, lift]) if end else None) for b, (_, end) in BODY_BONES.items()}

    report = {"name": name, "glb": str(glb_path), "height_m": round(height, 4),
              "total_mass_kg": round(total_mass, 2), "scale": round(scale, 4), "lift_m": round(lift, 4),
              "joint_shifts_deg": {}}

    parent_of = {c: p for p, cs in TREE.items() for c in cs}

    def body_xml(bname, indent):
        info = nick_bodies[bname]
        pos = world[bname] - (world[parent_of[bname]] if bname in parent_of else 0)
        pad = "  " * indent
        lines = [f'{pad}<body name="{bname}" pos="{pos[0]:.5f} {pos[1]:.5f} {pos[2]:.5f}">']
        if bname == "Pelvis":
            lines.append(f'{pad}  <freejoint name="root"/>')
        radius = info["radius"] * scale
        mass = info["mass"] / NICK_MASS * total_mass
        if info["sphere"]:
            lines.append(f'{pad}  <geom name="g_{bname}" type="sphere" pos="0 0 0" size="{radius:.4f}" mass="{mass:.4f}"/>')
        else:
            v = ends[bname] - world[bname]
            lines.append(f'{pad}  <geom name="g_{bname}" fromto="0.00000 0.00000 0.00000 {v[0]:.5f} {v[1]:.5f} {v[2]:.5f}" '
                         f'size="{radius:.4f}" mass="{mass:.4f}"/>')
        # Re-centre the joint ranges on this character's bind pose.
        shifts = [0.0] * len(info["joints"])
        if not info["sphere"] and info["joints"]:
            d_nick = nick_joints[BODY_BONES[bname][1]] - nick_joints[BODY_BONES[bname][0]]
            d_new = ends[bname] - world[bname]
            axes = [ax for _, ax, _ in info["joints"]]
            q = solve_joint_angles(axes, d_nick, d_new)
            shifts = [math.degrees(a) for a in q]
            if check and any(abs(s) > SHIFT_DEADBAND_DEG for s in shifts):
                mismatch = check_composition(nick_bodies, bname, shifts)
                assert mismatch < 2e-3, f"{bname}: joint composition check failed ({mismatch:.4f})"
        family = bname.rstrip("LR")
        margin = MARGIN_DEG.get(family, MARGIN_DEG["default"])
        for (jname, axis, (lo, hi)), shift in zip(info["joints"], shifts):
            if abs(shift) < SHIFT_DEADBAND_DEG:
                shift = 0.0
            nlo, nhi = lo - shift, hi - shift
            if nlo > -margin: nlo = -margin      # keep the bind pose comfortably legal
            if nhi < margin: nhi = margin
            report["joint_shifts_deg"][jname] = round(shift, 1)
            info.setdefault("new_ranges", {})[jname] = (nlo, nhi)
            lines.append(f'{pad}  <joint name="{jname}" axis="{axis}" range="{nlo:.1f} {nhi:.1f}"/>')
        for child in TREE.get(bname, []):
            lines.extend(body_xml(child, indent + 1))
        lines.append(f"{pad}</body>")
        return lines

    body_lines = body_xml("Pelvis", 2)

    act_lines = []
    for bname, info in nick_bodies.items():
        for jname, _, _ in info["joints"]:
            a = nick_actuators[jname]
            lo, hi = info["new_ranges"][jname]
            act_lines.append(f'    <position name="{a["name"]}" joint="{jname}" kp="{a["kp"]}" '
                             f'ctrlrange="{math.radians(lo):.6f} {math.radians(hi):.6f}"/>')

    xml = f'''<?xml version="1.0" ?>
<mujoco model="{name}">
  <compiler angle="degree" autolimits="true"/>
  <option timestep="0.005" iterations="20" solver="Newton" integrator="implicitfast"/>
  <default>
    <geom type="capsule" condim="3" friction="1.0 0.05 0.05" rgba="0.72 0.60 0.52 1" density="0"/>
    <joint type="hinge" damping="2" stiffness="0" armature="0.2" limited="true"/>
    <position ctrllimited="true" dampratio="1"/>
  </default>
  <asset>
    <texture name="grid" type="2d" builtin="checker" rgb1="0.24 0.26 0.28" rgb2="0.20 0.22 0.24" width="512" height="512"/>
    <material name="grid" texture="grid" texrepeat="8 8" reflectance="0.05"/>
  </asset>
  <worldbody>
    <light pos="0 0 4" dir="0 0 -1" directional="true"/>
    <geom name="floor" type="plane" size="20 20 0.1" material="grid" condim="3" friction="1.0 0.05 0.05"/>
{chr(10).join(body_lines)}
  </worldbody>
  <actuator>
{chr(10).join(act_lines)}
  </actuator>
  <sensor>
    <framepos name="s_FootL_pos" objtype="body" objname="FootL"/>
    <framepos name="s_FootR_pos" objtype="body" objname="FootR"/>
    <framepos name="s_Head_pos" objtype="body" objname="Head"/>
    <framezaxis name="s_Pelvis_up" objtype="body" objname="Pelvis"/>
    <subtreecom name="s_com" body="Pelvis"/>
  </sensor>
</mujoco>
'''
    out.write_text(xml, encoding="utf-8")

    # Prove it compiles and stands where it should.
    m = mujoco.MjModel.from_xml_path(str(out))
    d = mujoco.MjData(m)
    mujoco.mj_forward(m, d)
    report["compiled"] = {"nbody": int(m.nbody), "nu": int(m.nu), "nq": int(m.nq),
                          "mass_kg": round(float(m.body_mass.sum()), 2),
                          "rest_pelvis_z": round(float(d.xpos[mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "Pelvis"), 2]), 4),
                          "rest_head_z": round(float(d.xpos[mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "Head"), 2]), 4)}
    out.with_suffix(".rig.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))
    return report


def check_against_nick():
    """Rebuild Nick from his own GLB and diff body positions with creature.xml."""
    joints, height = bind_pose(load_glb_json(NICK_GLB))
    _, nick_bodies, _ = parse_template()
    root = ET.parse(NICK_XML).getroot()
    authored = {}

    def walk(el, origin):
        for b in el.findall("body"):
            p = origin + np.array([float(v) for v in b.get("pos").split()])
            authored[b.get("name")] = p
            walk(b, p)
    walk(root.find("worldbody"), np.zeros(3))
    foot_r = nick_bodies["FootL"]["radius"]
    lift = foot_r - min(joints["LeftToeBase"][2], joints["RightToeBase"][2])
    worst = 0.0
    for bname, (bone, _) in BODY_BONES.items():
        err = np.linalg.norm(joints[bone] + [0, 0, lift] - authored[bname])
        worst = max(worst, err)
    print(f"Nick self-check: height {height:.3f} m, lift {lift * 1000:.1f} mm, worst body position error {worst * 1000:.2f} mm")
    return worst


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--glb", type=Path)
    ap.add_argument("--name")
    ap.add_argument("--out", type=Path)
    ap.add_argument("--check", action="store_true", help="also rebuild Nick from his GLB and diff against creature.xml")
    args = ap.parse_args()
    if args.check:
        assert check_against_nick() < 0.002, "the derivation rule no longer reproduces Nick"
    if args.glb:
        build(args.glb, args.name or args.glb.stem, args.out or HERE / f"{args.name}.xml", check=True)
