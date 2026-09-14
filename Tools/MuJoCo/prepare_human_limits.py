"""Apply documented actuator budgets to an exported, mesh-derived Nick body.

Only actuator properties change. Bone positions, proportions, mass, collision
geometry and joint ranges remain those exported from the supplied skin.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

import mujoco
import numpy as np

# (negative torque Nm, positive torque Nm, driven angular speed rad/s).
# Engineering budgets, not subject-specific strength measurements; see HUMAN_LIMITS.md.
BUDGETS = {
    "Torso_pitch": (-150, 150, 4), "Torso_roll": (-100, 100, 4), "Torso_yaw": (-60, 60, 4),
    "Head_pitch": (-15, 15, 4), "Head_roll": (-10, 10, 4), "Head_yaw": (-8, 8, 4),
    "Thigh_pitch": (-140, 200, 6), "Thigh_roll": (-80, 80, 6), "Thigh_yaw": (-40, 40, 6),
    "Shin_pitch": (-200, 100, 8), "Foot_pitch": (-45, 120, 6), "Foot_roll": (-20, 20, 4),
    "UpperArm_pitch": (-60, 60, 8), "UpperArm_roll": (-60, 60, 8), "UpperArm_yaw": (-25, 25, 6),
    "Forearm_pitch": (-40, 60, 8), "Glove_pitch": (-10, 10, 6), "Glove_yaw": (-5, 5, 6),
}


def prepare(source: Path, output: Path):
    model = mujoco.MjModel.from_xml_path(str(source.resolve()))
    if model.nu != 30 or not np.isclose(model.body_mass.sum(), 75, atol=0.01):
        raise ValueError("These budgets are for the supplied 75 kg Nick rig only")
    tree = ET.parse(source)
    edits = []
    for actuator in tree.getroot().find("actuator"):
        if actuator.tag != "position":
            raise ValueError("Expected position actuators")
        name = actuator.get("name")
        canonical = re.sub(r"_\d+$", "", name)[2:]
        family = re.sub(r"[LR]_", "_", canonical)
        lower, upper, max_speed = BUDGETS[family]
        idx = mujoco.mj_name2id(model, mujoco.mjtObj.mjOBJ_ACTUATOR, name)
        joint_id = model.actuator_trnid[idx, 0]
        if not model.jnt_limited[joint_id] or not model.actuator_ctrllimited[idx]:
            raise ValueError("Speed envelope needs bounded angles and controls")
        q_lo, q_hi = model.jnt_range[joint_id]
        u_lo, u_hi = model.actuator_ctrlrange[idx]
        kp = model.actuator_gainprm[idx, 0]
        # At |qdot| >= max_speed, kp*(u-q)-kv*qdot cannot drive the
        # joint faster for ANY legal angle/target. Braking is also torque
        # limited by MuJoCo. No velocity or pose teleportation is used.
        span = max(u_hi - q_lo, q_hi - u_lo)
        kv = max(-model.actuator_biasprm[idx, 2], kp * span / max_speed)
        actuator.set("forcelimited", "true")
        actuator.set("forcerange", f"{lower} {upper}")
        actuator.set("kv", f"{kv:.9g}")
        edits.append({"name": "a_" + canonical, "force_range_nm": [lower, upper],
                      "max_driven_speed_rad_s": max_speed, "kp": float(kp), "kv": float(kv)})
    output.parent.mkdir(parents=True, exist_ok=True)
    tree.write(output, encoding="utf-8", xml_declaration=True)
    manifest = {"source": str(source), "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                "body": "RIGGED_Nick.glb, 75 kg, unchanged exported proportions", "actuators": edits}
    output.with_suffix(".limits.json").write_text(json.dumps(manifest, indent=2) + "\n")
    candidate = mujoco.MjModel.from_xml_path(str(output.resolve()))
    for attr in ("body_pos", "body_quat", "body_mass", "body_inertia", "jnt_range", "jnt_axis",
                 "geom_pos", "geom_quat", "geom_size", "geom_contype", "geom_conaffinity", "dof_armature"):
        np.testing.assert_array_equal(getattr(model, attr), getattr(candidate, attr), err_msg=attr)
    assert candidate.actuator_forcelimited.all()
    for idx, edit in enumerate(edits):
        j = candidate.actuator_trnid[idx, 0]
        for q in candidate.jnt_range[j]:
            for u in candidate.actuator_ctrlrange[idx]:
                for sign in (-1, 1):
                    velocity = sign * edit["max_driven_speed_rad_s"]
                    force = edit["kp"] * (u - q) - edit["kv"] * velocity
                    assert sign * force <= 1e-5, (edit["name"], force, velocity)
    print(f"Validated {len(edits)} force/speed envelopes; source body unchanged: {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, default=Path(__file__).with_name("nick_unity.xml"))
    parser.add_argument("--output", type=Path, default=Path(__file__).with_name("nick_human.xml"))
    args = parser.parse_args()
    prepare(args.source, args.output)
