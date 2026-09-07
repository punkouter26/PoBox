"""Element-wise parity between the C# and Python observation builders.

    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/parity_check.py

Needs the Unity Editor open on this project (it is driven through
Editor_CommandBridge). Writes a random near-standing state to
Temp/nick_parity_in.json, has RigTool_NickMuJoCo.RunParityProbe load it into
the live MjScene and dump CreatureSentisController's observation vector, then
builds the same vector here from the same state on CPU MuJoCo via
nick_env.observe and diffs the two.

Why this exists: ML-Agents and InferenceEngine check tensor SHAPE only. A
quaternion in the wrong order, a velocity in the wrong frame, or a contact
flag on the wrong threshold is the same width as the right thing and trains a
policy for a creature that does not exist. The Isaac line lost gens 1-7 to
exactly that class of bug (Tools/Isaac/README.md, root cause). This is the
check that would have caught it.
"""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path

import mujoco
import numpy as np
import torch

HERE = Path(__file__).parent
REPO = HERE.parent.parent
TEMP = REPO / "Temp"
IN_PATH = TEMP / "nick_parity_in.json"
OUT_PATH = TEMP / "nick_parity_out.json"
COMMAND_PATH = TEMP / "agent-command.txt"
RESULT_PATH = TEMP / "agent-command-result.txt"

sys.path.insert(0, str(HERE))
from nick_env import (FOOT_CONTACT_ABOVE_REST, JOINT_BODIES, PHYSICS_TIMESTEP, observe,   # noqa: E402
                      preferred_model_path, stem)

LEGACY_FOOT_CONTACT_HEIGHT = 0.0525


def bridge(method: str, timeout: float = 180.0) -> str:
    if RESULT_PATH.exists():
        RESULT_PATH.unlink()
    COMMAND_PATH.write_text(method)
    deadline = time.time() + timeout
    while time.time() < deadline:
        if RESULT_PATH.exists():
            time.sleep(0.5)
            return RESULT_PATH.read_text().strip()
        time.sleep(0.5)
    raise SystemExit("Editor did not answer %s within %.0f s -- is it open on this project?" % (method, timeout))


def random_state(model: mujoco.MjModel, rng: np.random.RandomState) -> tuple[np.ndarray, np.ndarray]:
    qpos = model.qpos0.copy()
    qpos[0:3] += rng.normal(0.0, 0.05, 3)
    axis = rng.normal(size=3)
    axis /= np.linalg.norm(axis)
    angle = rng.uniform(0.05, 0.25)
    tilt = np.zeros(4)
    mujoco.mju_axisAngle2Quat(tilt, axis, angle)
    q = np.zeros(4)
    mujoco.mju_mulQuat(q, tilt, qpos[3:7])
    qpos[3:7] = q / np.linalg.norm(q)
    for j in range(model.njnt):
        if model.jnt_type[j] != mujoco.mjtJoint.mjJNT_HINGE:
            continue
        a = model.jnt_qposadr[j]
        lo, hi = model.jnt_range[j]
        qpos[a] = np.clip(qpos[a] + rng.uniform(-0.3, 0.3), lo, hi)
    qvel = rng.normal(0.0, 0.5, model.nv)
    return qpos, qvel


def main():
    rng = np.random.RandomState(7)
    model = mujoco.MjModel.from_xml_path(preferred_model_path())
    model.opt.timestep = PHYSICS_TIMESTEP
    data = mujoco.MjData(model)
    qpos, qvel = random_state(model, rng)
    speed, yaw = 0.8, rng.uniform(-0.5, 0.5)
    direction = np.array([np.sin(yaw), -np.cos(yaw)])

    TEMP.mkdir(exist_ok=True)
    IN_PATH.write_text(json.dumps({"qpos": qpos.tolist(), "qvel": qvel.tolist(),
                                   "speed": speed, "dirX": float(direction[0]), "dirY": float(direction[1])}))
    if OUT_PATH.exists():
        OUT_PATH.unlink()

    print("asking the Editor to run the probe...")
    print("  " + bridge("PoBox.Editor.RigTool_NickMuJoCo.RunParityProbe"))
    deadline = time.time() + 120.0
    while not OUT_PATH.exists() and time.time() < deadline:
        time.sleep(1.0)
    print("  " + bridge("PoBox.Editor.RigTool_NickMuJoCo.StopDemo"))
    if not OUT_PATH.exists():
        raise SystemExit("no %s -- check the Editor console for NICK_PARITY errors" % OUT_PATH)
    unity = json.loads(OUT_PATH.read_text())
    unity_obs = np.array(unity["obs"], dtype=np.float64)
    print("Unity: %d observations, locomotionCommand=%s, decimation=%d, pelvis=(%.3f, %.3f, %.3f)"
          % (unity["observations"], unity["locomotionCommand"], unity["decimation"],
             unity["pelvisX"], unity["pelvisY"], unity["pelvisZ"]))

    # --- the same state here -------------------------------------------------
    data.qpos[:] = qpos
    data.qvel[:] = qvel
    mujoco.mj_forward(model, data)
    by_stem = {}
    for i in range(model.nbody):
        by_stem.setdefault(stem(model.body(i).name), i)
    pelvis_id = by_stem["Pelvis"]
    foot_ids = [by_stem["FootL"], by_stem["FootR"]]
    joint_body_ids = [by_stem[b] for b in JOINT_BODIES]
    joint_parent_ids = [int(model.body_parentid[i]) for i in joint_body_ids]
    if unity["locomotionCommand"]:
        rest = mujoco.MjData(model)
        mujoco.mj_forward(model, rest)
        foot_contact_z = [float(rest.xpos[i, 2]) + FOOT_CONTACT_ABOVE_REST for i in foot_ids]
        commands = torch.tensor([[speed, direction[0], direction[1]]], dtype=torch.float32)
        phase = torch.zeros(1)
    else:
        foot_contact_z = [LEGACY_FOOT_CONTACT_HEIGHT, LEGACY_FOOT_CONTACT_HEIGHT]
        commands, phase = None, None
    python_obs = observe(torch.tensor(data.xpos, dtype=torch.float32).unsqueeze(0),
                         torch.tensor(data.xquat, dtype=torch.float32).unsqueeze(0),
                         torch.tensor(data.cvel, dtype=torch.float32).unsqueeze(0),
                         pelvis_id, foot_ids, joint_body_ids, joint_parent_ids, foot_contact_z,
                         commands, phase)[0].numpy().astype(np.float64)

    print("Python: %d observations, pelvis=(%.3f, %.3f, %.3f)" % (len(python_obs), *data.xpos[pelvis_id]))
    if len(python_obs) != len(unity_obs):
        raise SystemExit("PARITY_FAIL: widths differ, %d vs %d" % (len(python_obs), len(unity_obs)))
    diff = np.abs(python_obs - unity_obs)
    labels = (["pelvis_z", "lin_x", "lin_y", "lin_z", "ang_x", "ang_y", "ang_z",
               "up_x", "up_y", "up_z", "fwd_x", "fwd_y", "fwd_z"]
              + ["%s_%s" % (b, t) for b in JOINT_BODIES for t in ("qw", "qx", "qy", "qz", "wx", "wy", "wz")]
              + ["L_down", "L_nx", "L_ny", "L_nz", "R_down", "R_nx", "R_ny", "R_nz", "L_height", "R_height"]
              + ["cmd_speed", "cmd_dir_x", "cmd_dir_y", "cmd_dir_z", "clock_sin", "clock_cos"])
    worst = np.argsort(diff)[::-1][:8]
    print("max |unity - python| = %.3e   mean %.3e" % (diff.max(), diff.mean()))
    for i in worst:
        print("  %-18s unity %+.5f  python %+.5f  diff %.2e" % (labels[i], unity_obs[i], python_obs[i], diff[i]))
    # Unity computes in float32 and its quaternion products round differently
    # from float64 numpy; anything past 1e-3 is a convention error, not rounding.
    if diff.max() > 1e-3:
        raise SystemExit("PARITY_FAIL")
    print("PARITY_OK")


if __name__ == "__main__":
    main()
