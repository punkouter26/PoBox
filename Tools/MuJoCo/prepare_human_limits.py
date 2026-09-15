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


def prepare(source: Path, output: Path, speed_envelope: bool = True, critical_from: Path | None = None):
    model = mujoco.MjModel.from_xml_path(str(source.resolve()))
    # Critical damping per actuator. A Unity export that already carries the
    # envelope has lost it; the authored MJCF (dampratio=1) compiled by the
    # same MuJoCo the importer used resolves the identical value.
    critical = None
    if critical_from is not None:
        cm = mujoco.MjModel.from_xml_path(str(critical_from.resolve()))
        critical = {re.sub(r"_\d+$", "", mujoco.mj_id2name(cm, mujoco.mjtObj.mjOBJ_ACTUATOR, i)): -cm.actuator_biasprm[i, 2]
                    for i in range(cm.nu)}
    if model.nu != 30:
        raise ValueError("These budgets are for the 30-actuator PoBox humanoid layout")
    total_mass = float(model.body_mass.sum())
    if not 50 <= total_mass <= 110:
        raise ValueError("Body mass %.1f kg is outside the human range these budgets assume" % total_mass)
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
        # The speed envelope is EXTRA DAMPING, and damping throttles every
        # motion, not only the fastest one: hip pitch ends up with kv/kp of
        # 0.38 s. Measured 2026-09-14 with Nick's shipping brain on his own
        # body -- 98 % balance unlimited, 5.1 s median with torque limits
        # only, 1.7 s (the passive baseline) with the envelope. So the torque
        # budget stays the physical constraint here and the speed budget is
        # measured as a KPI (hinge |qvel| in eval_nick.py) unless asked for.
        kv = -model.actuator_biasprm[idx, 2]
        if critical is not None:
            kv = critical[re.sub(r"_\d+$", "", name)]
        if speed_envelope:
            kv = max(kv, kp * span / max_speed)
        actuator.set("forcelimited", "true")
        actuator.set("forcerange", f"{lower} {upper}")
        actuator.set("kv", f"{kv:.9g}")
        edits.append({"name": "a_" + canonical, "force_range_nm": [lower, upper],
                      "max_driven_speed_rad_s": max_speed, "kp": float(kp), "kv": float(kv)})
    output.parent.mkdir(parents=True, exist_ok=True)
    tree.write(output, encoding="utf-8", xml_declaration=True)
    manifest = {"source": str(source), "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                "body": "%s, %.1f kg, unchanged exported proportions" % (source.stem, total_mass), "actuators": edits}
    output.with_suffix(".limits.json").write_text(json.dumps(manifest, indent=2) + "\n")
    candidate = mujoco.MjModel.from_xml_path(str(output.resolve()))
    for attr in ("body_pos", "body_quat", "body_mass", "body_inertia", "jnt_range", "jnt_axis",
                 "geom_pos", "geom_quat", "geom_size", "geom_contype", "geom_conaffinity", "dof_armature"):
        np.testing.assert_array_equal(getattr(model, attr), getattr(candidate, attr), err_msg=attr)
    assert candidate.actuator_forcelimited.all()
    for idx, edit in enumerate(edits):
        if not speed_envelope:
            break   # torque budgets only: the speed budget is a measured KPI, not a servo property
        j = candidate.actuator_trnid[idx, 0]
        for q in candidate.jnt_range[j]:
            for u in candidate.actuator_ctrlrange[idx]:
                for sign in (-1, 1):
                    velocity = sign * edit["max_driven_speed_rad_s"]
                    force = edit["kp"] * (u - q) - edit["kv"] * velocity
                    assert sign * force <= 1e-5, (edit["name"], force, velocity)
    print(f"Validated {len(edits)} {'force/speed envelopes' if speed_envelope else 'torque budgets (critical damping kept)'}; source body unchanged: {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, default=Path(__file__).with_name("nick_unity.xml"))
    parser.add_argument("--output", type=Path, default=Path(__file__).with_name("nick_human.xml"))
    parser.add_argument("--no-speed-envelope", action="store_true",
                        help="torque budgets only; keep the critical damping the export resolved")
    parser.add_argument("--critical-from", type=Path, default=None,
                        help="authored MJCF (dampratio=1) to take critical damping from, when the export already carries an envelope")
    args = parser.parse_args()
    prepare(args.source, args.output, speed_envelope=not args.no_speed_envelope, critical_from=args.critical_from)
