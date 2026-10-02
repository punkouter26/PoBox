"""Generate the boxing MuJoCo models from a rig description (rigs/*.json, written by glb_to_rig.py).

    python rig_to_mjcf.py --rig rigs/fighter.json

    python rig_to_mjcf.py --rig rigs/matt.json --name matt --versus rigs/zombie.json --versus-name zombie

Writes into models/, for each fighter:
    <name>_bag.xml             the fighter and a hanging heavy bag        (stage 1: stand and punch)
    <name>_spar.xml            two copies of it in a roped ring           (sparring against itself)
    <name>_policy_config.json  joint order, default pose, limits, gains   (the contract Unity reads)
and, with --versus, the match itself:
    <a>_vs_<b>_spar.xml        the two different bodies in one ring, with a config holding both

The body is PoDecath's athlete, generated the same way from the same kind of rig file, so the two games
share one skeleton layout: 21 hinge joints driven by PD position actuators.
    abdomen_z, abdomen_y, abdomen_x                 pelvis -> torso
    shoulder_x, shoulder_z, elbow                   per arm (l, r)
    hip_x, hip_z, hip_y, knee, ankle_y, ankle_x     per leg (l, r)

What is different for boxing:
  * the hands are gloves (a 7.5 cm sphere, 10 oz heavier than a hand);
  * the arms are given a boxer's strength rather than a runner's: 100 N m at the shoulder and 70 at the
    elbow, which is about what a trained adult produces, and joint speed limits to match;
  * the pose the policy's zero action means is a guard with soft knees, not a T-pose with locked legs.

Frames: rig JSON is Blender Z-up (x = character left, -y = forward). MJCF is x forward, y left, z up.
"""
from __future__ import annotations

import argparse
import json
import math
import os
from typing import Dict, List, Tuple

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))

GAINS = {  # kp [N m / rad], kv [N m s / rad], force limit [N m]
    "abdomen": (450.0, 21.0, 170.0),
    "hip": (600.0, 30.0, 240.0),
    "knee": (600.0, 30.0, 260.0),
    "ankle": (750.0, 30.0, 220.0),
    "shoulder": (160.0, 8.0, 100.0),
    "elbow": (110.0, 5.5, 70.0),
}
JOINT_GAINS = {
    # Only plantarflexion needs the big ankle torque; rolling the foot over does not.
    "ankle_x": (80.0, 4.0, 60.0),
}
RANGES_DEG = {
    "abdomen_z": (-45, 45), "abdomen_y": (-60, 30), "abdomen_x": (-35, 35),
    "hip_x": (-40, 40), "hip_z": (-35, 35), "hip_y": (-120, 30),
    "knee": (0, 150),
    "ankle_y": (-20, 50), "ankle_x": (-25, 25),
    "shoulder_x": (-170, 30),   # left arm: negative = arm down; mirrored on the right
    "shoulder_z": (-110, 110),  # with the arm down this is the swing forward and back
    "elbow": (-150, 0),         # left; mirrored on the right
}
ARMATURE = {"abdomen": 0.08, "hip": 0.10, "knee": 0.10, "ankle": 0.03, "shoulder": 0.03, "elbow": 0.02}
# Winter / Dempster segment masses as a fraction of body mass.
SEGMENT_MASS_FRAC = {
    "pelvis": 0.142, "torso": 0.355, "head": 0.081, "upper_arm": 0.028, "forearm": 0.016,
    "hand": 0.006, "thigh": 0.100, "shin": 0.0465, "foot": 0.0145,
}
GLOVE_KG = 0.283   # a 10 oz glove
# Peak joint speeds a person reaches [rad/s]. A punch turns the shoulder at about 20 and snaps the
# elbow open at about 25; the rest are the sprinter's figures PoDecath uses.
VELOCITY_LIMIT = {"abdomen": 8.0, "hip": 15.0, "knee": 20.0, "ankle": 14.0, "shoulder": 20.0, "elbow": 25.0}
# The pose a zero action asks for: gloves up by the chin, knees soft, weight over the middle of the feet.
# Arm signs are for the left arm; the generator mirrors them. With the arm down, shoulder_z swings it
# forward (negative on the left) and the elbow folds the forearm up towards the face.
GUARD_DEG = {
    "abdomen_z": 0, "abdomen_y": 0, "abdomen_x": 0,
    "hip_x": 0, "hip_z": 0, "hip_y": -14, "knee": 26, "ankle_y": -12, "ankle_x": 0,
    "shoulder_x": -76, "shoulder_z": -42, "elbow": -112,
}
MIRRORED = ("shoulder_x", "shoulder_z", "elbow")
RADII = {"pelvis": 0.085, "torso": 0.085, "head": 0.10, "thigh": 0.06, "shin": 0.045,
         "upper_arm": 0.04, "forearm": 0.035, "glove": 0.075}
RING_HALF = 3.05


def b2m(v) -> np.ndarray:
    """Blender (x left, -y forward, z up) -> MJCF (x forward, y left, z up)."""
    return np.array([-v[1], v[0], v[2]], dtype=float)


def fmt(v) -> str:
    return " ".join(f"{float(x):.4f}" for x in v)


class Fighter:
    """One fighter's bodies, joints and actuators, with every name carrying a prefix (a_ or b_)."""

    def __init__(self, rig: Dict, prefix: str, lean: float = 0.0):
        self.rig = rig
        self.p = prefix
        # Degrees the whole body is tipped forward at the ankle, with the hip taking it back out so the
        # trunk stays upright. Solved per rig so the weight sits over the middle of the feet: a body with
        # its head carried forward needs less of it than one that stands straight.
        self.lean = lean
        self.bones = {b["name"]: b for b in rig["bones"]}
        self.map = rig["map"]
        self.gains = dict(GAINS, **{k: tuple(v) for k, v in rig.get("boxing_gains", {}).items()})
        # What makes one body not another, all optional and all from the rig file: shapes fitted inside
        # the fighter's own mesh, and how strong and how quick its muscles are beside a trained adult's
        # (1.0). A rig with none of them is built exactly as before.
        self.radii = dict(RADII, **rig.get("radii", {}))
        self.strength = float(rig.get("strength", 1.0))
        self.speed = float(rig.get("speed", 1.0))
        self.mass_kg = float(rig.get("mass_kg", 75.0))
        self.joints: List[Dict] = []
        self.actuators: List[str] = []
        self.lines: List[str] = []

    def has(self, key: str) -> bool:
        return key in self.map and self.map[key] in self.bones

    def head(self, key: str) -> np.ndarray:
        return b2m(self.bones[self.map[key]]["head"])

    def tail(self, key: str) -> np.ndarray:
        return b2m(self.bones[self.map[key]]["tail"])

    def mass(self, segment: str, share: float = 1.0, extra: float = 0.0) -> str:
        return f'mass="{self.mass_kg * SEGMENT_MASS_FRAC[segment] * share + extra:.4f}" '

    @staticmethod
    def capsule_volume(a: np.ndarray, b: np.ndarray, radius: float) -> float:
        return float(math.pi * radius ** 2 * np.linalg.norm(b - a) + (4.0 / 3.0) * math.pi * radius ** 3)

    def capsule(self, name: str, a: np.ndarray, b: np.ndarray, radius: float, mass: str, extra: str = "") -> str:
        return (f'<geom name="{self.p}{name}" type="capsule" fromto="{fmt(a)} {fmt(b)}" '
                f'size="{radius:.4f}" {mass}{extra}/>')

    def joint(self, name: str, axis: str, group: str, side: str = "") -> str:
        lo, hi = RANGES_DEG[name]
        default = GUARD_DEG[name]
        if name == "hip_y":
            default += self.lean
        elif name == "ankle_y":
            default -= self.lean
        if side == "r" and name in MIRRORED:
            lo, hi = -hi, -lo
            default = -default
        full = f"{name}_{side}" if side else name
        kp, kv, fl = (round(x * self.strength, 3) for x in JOINT_GAINS.get(name, self.gains[group]))
        arm = ARMATURE[group]
        self.joints.append({
            "name": full, "group": group, "axis": axis,
            "lower": math.radians(lo), "upper": math.radians(hi), "default": math.radians(default),
            "kp": kp, "kv": kv, "force_limit": fl, "armature": arm,
            "velocity_limit": round(VELOCITY_LIMIT[group] * self.speed, 3),
        })
        self.actuators.append(
            f'<position name="{self.p}{full}" joint="{self.p}{full}" kp="{kp}" kv="{kv}" forcerange="-{fl} {fl}" '
            f'ctrlrange="{math.radians(lo):.4f} {math.radians(hi):.4f}"/>')
        return (f'<joint name="{self.p}{full}" type="hinge" axis="{axis}" range="{lo} {hi}" '
                f'damping="0.5" armature="{arm}" stiffness="0"/>')

    def build(self, pos: Tuple[float, float], yaw: float) -> str:
        L, p = self.lines, self.p
        RADII = self.radii
        pelvis = self.head("pelvis")
        spine = self.head("spine") if self.has("spine") else self.tail("pelvis")
        neck = self.head("neck") if self.has("neck") else self.tail("torso")
        head_c = 0.5 * (self.head("head") + self.tail("head"))
        quat = f"{math.cos(yaw / 2):.5f} 0 0 {math.sin(yaw / 2):.5f}"

        L.append(f'<body name="{p}pelvis" pos="{pos[0]:.4f} {pos[1]:.4f} {pelvis[2]:.4f}" quat="{quat}">')
        L.append(f'  <freejoint name="{p}root"/>')
        p_a, p_b = np.array([0, -0.07, 0.0]), np.array([0, 0.07, 0.0])
        p_up_b = spine - pelvis - np.array([0, 0, 0.02])
        v_lo = self.capsule_volume(p_a, p_b, RADII["pelvis"])
        v_up = self.capsule_volume(np.zeros(3), p_up_b, RADII["pelvis"] * 0.9)
        f_lo = v_lo / (v_lo + v_up)
        L.append('  ' + self.capsule("pelvis_geom", p_a, p_b, RADII["pelvis"], self.mass("pelvis", f_lo)))
        L.append('  ' + self.capsule("pelvis_up", np.zeros(3), p_up_b, RADII["pelvis"] * 0.9, self.mass("pelvis", 1.0 - f_lo)))

        L.append(f'  <body name="{p}torso" pos="{fmt(spine - pelvis)}">')
        L.append('    ' + self.joint("abdomen_z", "0 0 1", "abdomen"))
        L.append('    ' + self.joint("abdomen_y", "0 1 0", "abdomen"))
        L.append('    ' + self.joint("abdomen_x", "1 0 0", "abdomen"))
        L.append('    ' + self.capsule("torso_geom", np.array([0, 0, 0.02]), neck - spine - np.array([0, 0, 0.01]),
                                       RADII["torso"], self.mass("torso")))
        # Shoulders: a bar across the top of the chest, so a body shot has a chest to land on and the
        # arms hang off something as wide as a person.
        sh_l, sh_r = self.head("upper_arm_l") - spine, self.head("upper_arm_r") - spine
        L.append(f'    <geom name="{p}chest_geom" type="capsule" fromto="{fmt(sh_r * 0.8)} {fmt(sh_l * 0.8)}" '
                 f'size="{RADII["torso"] * 0.9:.4f}" mass="0.5"/>')
        L.append(f'    <geom name="{p}head_geom" type="sphere" pos="{fmt(head_c - spine)}" '
                 f'size="{RADII["head"]:.4f}" {self.mass("head")}/>')
        for side in ("l", "r"):
            sh = self.head(f"upper_arm_{side}"); el = self.head(f"forearm_{side}"); wr = self.head(f"hand_{side}")
            tip = self.tail(f"hand_tip_{side}") if self.has(f"hand_tip_{side}") else self.tail(f"hand_{side}")
            reach = (tip - wr) / max(1e-6, float(np.linalg.norm(tip - wr)))
            glove = wr + reach * RADII["glove"] * 0.9
            L.append(f'    <body name="{p}upper_arm_{side}" pos="{fmt(sh - spine)}">')
            L.append('      ' + self.joint("shoulder_x", "1 0 0", "shoulder", side))
            L.append('      ' + self.joint("shoulder_z", "0 0 1", "shoulder", side))
            L.append('      ' + self.capsule(f"upper_arm_{side}_geom", np.zeros(3), el - sh, RADII["upper_arm"], self.mass("upper_arm")))
            L.append(f'      <body name="{p}forearm_{side}" pos="{fmt(el - sh)}">')
            L.append('        ' + self.joint("elbow", "0 0 1", "elbow", side))
            L.append('        ' + self.capsule(f"forearm_{side}_geom", np.zeros(3), wr - el, RADII["forearm"], self.mass("forearm")))
            L.append(f'        <geom name="{p}glove_{side}" type="sphere" pos="{fmt(glove - el)}" size="{RADII["glove"]:.4f}" '
                     f'{self.mass("hand", 1.0, GLOVE_KG)}rgba="{"0.85 0.2 0.2 1" if p == "a_" else "0.2 0.45 0.9 1"}"/>')
            L.append('      </body>')
            L.append('    </body>')
        L.append('  </body>')

        for side in ("l", "r"):
            hip = self.head(f"thigh_{side}"); knee = self.head(f"shin_{side}"); ankle = self.head(f"foot_{side}")
            toe = self.tail(f"toe_{side}") if self.has(f"toe_{side}") else self.tail(f"foot_{side}")
            # Legs straight down from the hip. A rig modelled with its feet apart would otherwise put
            # every hip joint's zero in a straddle.
            thigh_len = float(np.linalg.norm(knee - hip)); shin_len = float(np.linalg.norm(ankle - knee))
            knee_rel = np.array([0.0, 0.0, -thigh_len]); ankle_rel = np.array([0.0, 0.0, -shin_len])
            ankle_h = max(0.05, float(ankle[2]))
            L.append(f'  <body name="{p}thigh_{side}" pos="{fmt(hip - pelvis)}">')
            L.append('    ' + self.joint("hip_x", "1 0 0", "hip", side))
            L.append('    ' + self.joint("hip_z", "0 0 1", "hip", side))
            L.append('    ' + self.joint("hip_y", "0 1 0", "hip", side))
            L.append('    ' + self.capsule(f"thigh_{side}_geom", np.zeros(3), knee_rel, RADII["thigh"], self.mass("thigh")))
            L.append(f'    <body name="{p}shin_{side}" pos="{fmt(knee_rel)}">')
            L.append('      ' + self.joint("knee", "0 1 0", "knee", side))
            L.append('      ' + self.capsule(f"shin_{side}_geom", np.zeros(3), ankle_rel, RADII["shin"], self.mass("shin")))
            L.append(f'      <body name="{p}foot_{side}" pos="{fmt(ankle_rel)}">')
            L.append('        ' + self.joint("ankle_y", "0 1 0", "ankle", side))
            L.append('        ' + self.joint("ankle_x", "1 0 0", "ankle", side))
            foot_len = float(np.clip(abs(float(toe[0] - ankle[0])) + 0.07, 0.22, 0.30))
            half = np.array([foot_len * 0.5, 0.045, 0.028])
            # A person's heel is about a quarter of the foot behind the ankle.
            center = np.array([foot_len * 0.5 - foot_len * 0.27, 0.0, -ankle_h + half[2]])
            L.append(f'        <geom name="{p}foot_{side}_geom" type="box" pos="{fmt(center)}" size="{fmt(half)}" '
                     f'{self.mass("foot")}friction="1.0 0.005 0.0001"/>')
            L.append(f'        <site name="{p}foot_{side}_site" pos="{fmt(center)}" size="0.01"/>')
            L.append('      </body>')
            L.append('    </body>')
            L.append('  </body>')
        L.append('</body>')
        return "\n    ".join(L)

    def exclusions(self) -> List[Tuple[str, str]]:
        """Body pairs joined by a joint, which overlap by construction. Everything else collides."""
        p = self.p
        pairs = [(f"{p}pelvis", f"{p}torso")]
        for s in ("l", "r"):
            pairs += [(f"{p}pelvis", f"{p}thigh_{s}"), (f"{p}thigh_{s}", f"{p}shin_{s}"), (f"{p}shin_{s}", f"{p}foot_{s}"),
                      (f"{p}torso", f"{p}upper_arm_{s}"), (f"{p}upper_arm_{s}", f"{p}forearm_{s}")]
        return pairs


def bag_xml() -> str:
    """A 35 kg heavy bag on a ball joint, hanging at the origin: head height at the top, belt at the bottom."""
    return ('<body name="bag" pos="0 0 2.45">\n'
            '      <joint name="bag_swing" type="ball" damping="6"/>\n'
            '      <geom name="bag_geom" type="capsule" fromto="0 0 -0.62 0 0 -1.42" size="0.17" mass="35" rgba="0.5 0.3 0.2 1"/>\n'
            '      <site name="bag_head" pos="0 0 -0.82" size="0.02"/>\n'
            '      <site name="bag_body" pos="0 0 -1.22" size="0.02"/>\n'
            '    </body>')


def ring_xml() -> str:
    """Four walls where the ropes are. A wall, not ropes: all the trainer needs is that nobody leaves."""
    h, t, r = 0.75, 0.05, RING_HALF
    walls = [("n", (0, r + t, h), (r + 2 * t, t, h)), ("s", (0, -r - t, h), (r + 2 * t, t, h)),
             ("e", (r + t, 0, h), (t, r + 2 * t, h)), ("w", (-r - t, 0, h), (t, r + 2 * t, h))]
    return "\n    ".join(f'<geom name="rope_{n}" type="box" pos="{fmt(pos)}" size="{fmt(size)}" rgba="0.8 0.8 0.8 0.15"/>'
                         for n, pos, size in walls)


def assemble(name: str, fighters: List[Fighter], bodies: List[str], extra_world: str, keyframe_tail: str) -> str:
    excl = "\n    ".join(f'<exclude body1="{a}" body2="{b}"/>' for f in fighters for a, b in f.exclusions())
    act = "\n    ".join(a for f in fighters for a in f.actuators)
    body = "\n    ".join(bodies)
    return f"""<mujoco model="{name}">
  <compiler angle="degree" inertiafromgeom="true" autolimits="true"/>
  <option timestep="0.005" iterations="10" ls_iterations="10" integrator="implicitfast">
    <flag eulerdamp="disable"/>
  </option>
  <default>
    <geom contype="1" conaffinity="1" condim="3" friction="1.0 0.005 0.0001" density="1000" margin="0"/>
    <joint limited="true"/>
    <position ctrllimited="true"/>
  </default>
  <asset>
    <texture name="grid" type="2d" builtin="checker" rgb1=".18 .28 .48" rgb2=".14 .22 .40" width="300" height="300"/>
    <material name="grid" texture="grid" texrepeat="6 6" reflectance="0"/>
  </asset>
  <worldbody>
    <light pos="0 0 5" dir="0 0 -1" directional="true"/>
    <geom name="floor" type="plane" size="0 0 0.05" material="grid" friction="1.0 0.005 0.0001"/>
    {extra_world}
    {body}
  </worldbody>
  <contact>
    {excl}
  </contact>
  <actuator>
    {act}
  </actuator>
  <keyframe>
    <key name="stand" qpos="{keyframe_tail}"/>
  </keyframe>
</mujoco>
"""


def settle_height(xml: str, fighters: int, joints: int) -> Tuple[float, Dict]:
    """Root height that puts the soles on the floor in the guard pose, and a report on the balance."""
    m = mujoco.MjModel.from_xml_string(xml)
    d = mujoco.MjData(m)
    mujoco.mj_resetDataKeyframe(m, d, 0)
    mujoco.mj_forward(m, d)
    info = {}
    lowest = 1e9
    for side in "lr":
        g = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, f"a_foot_{side}_geom")
        half = m.geom_size[g]
        rz = d.geom_xmat[g].reshape(3, 3)[2]
        lowest = min(lowest, float(d.geom_xpos[g][2] - np.abs(rz) @ half))
    root_z = float(d.qpos[2] - lowest + 0.002)

    # Where the weight is over the feet. 0 is the heel, 1 the toe; a standing person is near 0.45.
    d.qpos[2] = root_z
    mujoco.mj_forward(m, d)
    pelvis = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "a_pelvis")
    com = d.subtree_com[pelvis]
    g = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, "a_foot_l_geom")
    heel = d.geom_xpos[g][0] - m.geom_size[g][0]
    toe = d.geom_xpos[g][0] + m.geom_size[g][0]
    info["com_over_foot"] = float((com[0] - heel) / (toe - heel))
    info["mass_kg"] = float(m.body_subtreemass[pelvis])
    info["pelvis_height"] = root_z
    for side in "lr":
        gg = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, f"a_glove_{side}")
        info[f"glove_{side}"] = [round(float(x), 3) for x in (d.geom_xpos[gg] - d.xpos[pelvis])]
    hg = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, "a_head_geom")
    info["head"] = [round(float(x), 3) for x in (d.geom_xpos[hg] - d.xpos[pelvis])]
    return root_z, info


def fighter_config(rig: Dict, name: str, rig_path: str, a: "Fighter", root_z: float, report: Dict) -> Dict:
    return {
        "name": name,
        "rig": os.path.basename(rig_path),
        "source_mesh": rig.get("source", ""),
        "joint_order": [j["name"] for j in a.joints],
        "default_joint_pos": [j["default"] for j in a.joints],
        "lower": [j["lower"] for j in a.joints],
        "upper": [j["upper"] for j in a.joints],
        "kp": [j["kp"] for j in a.joints],
        "kv": [j["kv"] for j in a.joints],
        "force_limit": [j["force_limit"] for j in a.joints],
        "armature": [j["armature"] for j in a.joints],
        "velocity_limit": [j["velocity_limit"] for j in a.joints],
        "total_mass_kg": report["mass_kg"],
        "height_m": rig.get("height_m", 0.0),
        "stand_height": root_z,
        "action_scale": 0.5,
        "action_clip": 3.0,
        "physics_hz": 200,
        "control_decimation": 4,
        "ring_half": RING_HALF,
        "radii": {"head": a.radii["head"], "torso": a.radii["torso"], "glove": a.radii["glove"]},
        # How this fighter is paid in training (envs/boxing.py reads it); empty for the plain boxer.
        "style": rig.get("style", {}),
        "strength": a.strength,
        "speed": a.speed,
    }


def key_for(fighter: Fighter, x: float, y: float, yaw: float, root_z: float) -> str:
    joint_q = " ".join(f"{j['default']:.4f}" for j in fighter.joints)
    return f"{x:.4f} {y:.4f} {root_z:.4f} {math.cos(yaw / 2):.5f} 0 0 {math.sin(yaw / 2):.5f} {joint_q}"


def build_one(rig: Dict, rig_path: str, name: str, out_dir: str) -> Tuple[float, Dict]:
    """One fighter's own files: the bag model, a spar against a copy of itself, and its config."""

    lean = 0.0

    def make(mode: str, root_z: float) -> Tuple[str, Fighter]:
        a = Fighter(rig, "a_", lean)
        if mode == "bag":
            body = [a.build((-0.75, 0.0), 0.0), bag_xml()]
            key = key_for(a, -0.75, 0.0, 0.0, root_z) + " 1 0 0 0"   # the bag's ball joint
            return assemble(f"{name}_bag", [a], body, "", key), a
        b = Fighter(rig, "b_", lean)
        body = [a.build((-0.6, 0.0), 0.0), b.build((0.6, 0.0), math.pi)]
        key = key_for(a, -0.6, 0.0, 0.0, root_z) + " " + key_for(b, 0.6, 0.0, math.pi, root_z)
        return assemble(f"{name}_spar", [a, b], body, ring_xml(), key), a

    # Find the lean that puts the weight 47% of the way from heel to toe, by bisection: more lean moves
    # the weight forward. Each try builds the model and measures it.
    lo, hi = -10.0, 14.0
    for _ in range(14):
        lean = 0.5 * (lo + hi)
        xml, a = make("bag", 1.0)
        if settle_height(xml, 1, len(a.joints))[1]["com_over_foot"] < 0.47:
            lo = lean
        else:
            hi = lean
    lean = 0.5 * (lo + hi)

    # Build once to measure where the soles are in the guard pose, then again at that height.
    xml, a = make("bag", 1.0)
    root_z, _ = settle_height(xml, 1, len(a.joints))
    report = {}
    for mode in ("bag", "spar"):
        xml, a = make(mode, root_z)
        path = os.path.join(out_dir, f"{name}_{mode}.xml")
        with open(path, "w", encoding="utf-8") as f:
            f.write(xml)
        m = mujoco.MjModel.from_xml_path(path)   # fails loudly here if the model is malformed
        _, report = settle_height(xml, 1, len(a.joints))
        print(f"wrote {path}: {m.nbody} bodies, {m.nu} actuators, {m.ngeom} geoms")

    cfg = fighter_config(rig, name, rig_path, a, root_z, report)
    cfg["stance_lean_deg"] = round(lean, 2)
    cfg["models"] = {"bag": f"{name}_bag.xml", "spar": f"{name}_spar.xml"}
    cfg_path = os.path.join(out_dir, f"{name}_policy_config.json")
    with open(cfg_path, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2)
    print(f"wrote {cfg_path}")
    print(f"  {name}: {report['mass_kg']:.1f} kg, pelvis {root_z:.3f} m above the floor in the guard, "
          f"weight {report['com_over_foot']:.0%} of the way from heel to toe (a standing person: about 45%)")
    print(f"  gloves relative to the pelvis (x fwd, y left, z up): L {report['glove_l']}  R {report['glove_r']}  head {report['head']}")
    print(f"  stance lean {lean:+.1f} degrees")
    return root_z, cfg


def build_versus(rig_a: Dict, name_a: str, z_a: float, cfg_a: Dict,
                 rig_b: Dict, name_b: str, z_b: float, cfg_b: Dict, out_dir: str) -> None:
    """The match: two different bodies in one ring, each at its own standing height."""
    a, b = Fighter(rig_a, "a_", cfg_a["stance_lean_deg"]), Fighter(rig_b, "b_", cfg_b["stance_lean_deg"])
    body = [a.build((-0.6, 0.0), 0.0), b.build((0.6, 0.0), math.pi)]
    key = key_for(a, -0.6, 0.0, 0.0, z_a) + " " + key_for(b, 0.6, 0.0, math.pi, z_b)
    stem = f"{name_a}_vs_{name_b}"
    path = os.path.join(out_dir, f"{stem}_spar.xml")
    with open(path, "w", encoding="utf-8") as f:
        f.write(assemble(f"{stem}_spar", [a, b], body, ring_xml(), key))
    m = mujoco.MjModel.from_xml_path(path)
    cfg_path = os.path.join(out_dir, f"{stem}_policy_config.json")
    with open(cfg_path, "w", encoding="utf-8") as f:
        json.dump({"names": [name_a, name_b], "fighters": [cfg_a, cfg_b], "ring_half": RING_HALF}, f, indent=2)
    print(f"wrote {path}: {m.nbody} bodies, {m.nu} actuators, {m.ngeom} geoms; and {cfg_path}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--rig", required=True)
    ap.add_argument("--name", default="fighter")
    ap.add_argument("--versus", default="", help="a second rig: also writes <name>_vs_<versus-name>_spar.xml, the two in one ring")
    ap.add_argument("--versus-name", default="")
    ap.add_argument("--out-dir", default=os.path.join(HERE, "models"))
    args = ap.parse_args()
    os.makedirs(args.out_dir, exist_ok=True)

    with open(args.rig, "r", encoding="utf-8") as f:
        rig_a = json.load(f)
    z_a, cfg_a = build_one(rig_a, args.rig, args.name, args.out_dir)
    if args.versus:
        if not args.versus_name:
            raise SystemExit("--versus needs --versus-name")
        with open(args.versus, "r", encoding="utf-8") as f:
            rig_b = json.load(f)
        z_b, cfg_b = build_one(rig_b, args.versus, args.versus_name, args.out_dir)
        build_versus(rig_a, args.name, z_a, cfg_a, rig_b, args.versus_name, z_b, cfg_b, args.out_dir)


if __name__ == "__main__":
    main()
