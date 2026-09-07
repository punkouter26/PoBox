"""Watch a Nick checkpoint move in the MuJoCo viewer, and measure it.

    # newest checkpoint of a run, live viewer, BALANCE/WALK phases like the Unity demo
    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/watch_nick.py --run nick01

    # keep reloading the newest checkpoint as training writes them (train_nick.py does this)
    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/watch_nick.py --run nick01 --follow

    # the EXPORTED graph, through onnxruntime: proves the .onnx, not just the .pt
    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/watch_nick.py --onnx Assets/MuJoCoCreature/Policy/nick_locomotion.onnx

    # no window, many phases, for a table
    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/watch_nick.py --run nick01 --headless --phases 10

Runs on CPU MuJoCo (the same 3.12.0 the Unity plugin embeds), on the same
nick_unity.xml, with the same observation builder the Warp env trains on
(nick_env.observe) -- so what moves here is the creature the game will run,
one physics engine removed from Unity's rather than two.

Prints one NICK_WATCH line per phase with the columns Systems_NickDemo logs
as NICK_DEMO and Systems_MattDemo logs as MATT_DEMO:

    measured speed, distance, alternation, switches, clearance, down steps, falls
"""
from __future__ import annotations

import argparse
import math
import re
import sys
import time
from pathlib import Path

import mujoco
import numpy as np
import torch

HERE = Path(__file__).parent
REPO = HERE.parent.parent
LOG_ROOT = REPO / "results" / "nick"

sys.path.insert(0, str(HERE))
from nick_env import (ACT_DIM, FOOT_CONTACT_ABOVE_REST, GAIT_CLOCK_HZ, JOINT_BODIES, OBS_BASE,   # noqa: E402
                      OBS_COMMAND, PHYSICS_TIMESTEP, REST_FORWARD, observe, preferred_model_path, stem)

parser = argparse.ArgumentParser()
parser.add_argument("--run", default="nick01")
parser.add_argument("--checkpoint", default=None)
parser.add_argument("--onnx", default=None, help="run this exported graph instead of a checkpoint")
parser.add_argument("--follow", action="store_true", help="reload the newest checkpoint between phases")
parser.add_argument("--headless", action="store_true")
parser.add_argument("--phases", type=int, default=0, help="stop after this many phases (0 = forever)")
parser.add_argument("--phase-seconds", type=float, default=10.0)
parser.add_argument("--walk-speed", type=float, default=1.0)
parser.add_argument("--decimation", type=int, default=4)
parser.add_argument("--shove-newtons", type=float, default=150.0)
parser.add_argument("--shove-seconds", type=float, default=0.2)
parser.add_argument("--shove-interval", type=float, default=4.0)
parser.add_argument("--seed", type=int, default=0)
args = parser.parse_args()

OBS_DIM = OBS_BASE + OBS_COMMAND
TARGET_SWITCHES_PER_STEP = 1.0 / 35.0


# --------------------------------------------------------------- policies
def newest_checkpoint(run_dir: Path) -> Path | None:
    candidates = sorted(run_dir.glob("model_*.pt"), key=lambda p: int(re.search(r"(\d+)", p.stem).group(1)))
    return candidates[-1] if candidates else None


class TorchPolicy:
    def __init__(self, path: Path):
        from rsl_rl.modules import ActorCritic, EmpiricalNormalization
        ckpt = torch.load(path, map_location="cpu", weights_only=False)
        self.net = ActorCritic(OBS_DIM, OBS_DIM, ACT_DIM, [512, 256, 128], [512, 256, 128], "elu").eval()
        self.net.load_state_dict(ckpt["model_state_dict"])
        self.norm = EmpiricalNormalization(shape=[OBS_DIM]).eval()
        if ckpt.get("obs_norm_state_dict") is not None:
            self.norm.load_state_dict(ckpt["obs_norm_state_dict"])
        self.name = "%s (iteration %s)" % (path.name, ckpt.get("iter", "?"))

    def __call__(self, obs: np.ndarray) -> np.ndarray:
        with torch.no_grad():
            x = torch.tensor(obs, dtype=torch.float32).unsqueeze(0)
            return self.net.actor(self.norm(x))[0].numpy()


class OnnxPolicy:
    def __init__(self, path: Path):
        import onnxruntime as ort
        self.sess = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
        self.input = self.sess.get_inputs()[0].name
        self.name = path.name

    def __call__(self, obs: np.ndarray) -> np.ndarray:
        return self.sess.run(None, {self.input: obs[None].astype(np.float32)})[0][0]


def load_policy(current=None):
    if args.onnx:
        return current or OnnxPolicy(Path(args.onnx))
    ckpt = newest_checkpoint(LOG_ROOT / args.run)
    if ckpt is None:
        return current
    if args.checkpoint:
        ckpt = LOG_ROOT / args.run / args.checkpoint
    if current is not None and current.name.startswith(ckpt.name):
        return current
    return TorchPolicy(ckpt)


# ------------------------------------------------------------------ sim
model = mujoco.MjModel.from_xml_path(preferred_model_path())
model.opt.timestep = PHYSICS_TIMESTEP
data = mujoco.MjData(model)
by_stem = {}
for i in range(model.nbody):
    by_stem.setdefault(stem(model.body(i).name), i)
pelvis_id = by_stem["Pelvis"]
foot_ids = [by_stem["FootL"], by_stem["FootR"]]
head_id = by_stem["Head"]
joint_body_ids = [by_stem[b] for b in JOINT_BODIES]
joint_parent_ids = [int(model.body_parentid[i]) for i in joint_body_ids]
ctrl_low = model.actuator_ctrlrange[:, 0]
ctrl_high = model.actuator_ctrlrange[:, 1]
control_dt = PHYSICS_TIMESTEP * args.decimation

mujoco.mj_forward(model, data)
rest_foot_z = [float(data.xpos[i, 2]) for i in foot_ids]
foot_contact_z = [z + FOOT_CONTACT_ABOVE_REST for z in rest_foot_z]
rest_head_z = float(data.xpos[head_id, 2])
rng = np.random.RandomState(args.seed)


def reset():
    mujoco.mj_resetData(model, data)
    data.qpos[:] = model.qpos0
    data.qvel[:] = 0.0
    data.ctrl[:] = 0.0
    data.xfrc_applied[:] = 0.0
    mujoco.mj_forward(model, data)


def pelvis_forward() -> np.ndarray:
    q = data.xquat[pelvis_id]
    v = np.zeros(3)
    mujoco.mju_rotVecQuat(v, np.array(REST_FORWARD), q)
    return v


def gather(commands: np.ndarray, step: int) -> np.ndarray:
    phase = torch.tensor([GAIT_CLOCK_HZ * 2.0 * math.pi * step * control_dt], dtype=torch.float32)
    obs = observe(torch.tensor(data.xpos, dtype=torch.float32).unsqueeze(0),
                  torch.tensor(data.xquat, dtype=torch.float32).unsqueeze(0),
                  torch.tensor(data.cvel, dtype=torch.float32).unsqueeze(0),
                  pelvis_id, foot_ids, joint_body_ids, joint_parent_ids, foot_contact_z,
                  torch.tensor(commands, dtype=torch.float32).unsqueeze(0), phase)
    return obs[0].numpy()


def run_phase(policy, walking: bool, viewer) -> dict:
    reset()
    fwd = pelvis_forward()
    direction = np.array([fwd[0], fwd[1]])
    direction /= max(np.linalg.norm(direction), 1e-6)
    speed = args.walk_speed if walking else 0.0
    commands = np.array([speed, direction[0], direction[1]], dtype=np.float32)
    start = data.xpos[pelvis_id, :2].copy()

    steps = int(round(args.phase_seconds / control_dt))
    shove_every = int(round(args.shove_interval / control_dt)) if args.shove_interval > 0 else 0
    shove_steps = max(1, int(round(args.shove_seconds / PHYSICS_TIMESTEP)))
    shove_left = 0
    speed_sum = 0.0
    switches = 0
    stance = 0
    clearance_sum = 0.0
    down = 0
    falls = 0
    wall = time.time()
    for step in range(steps):
        obs = gather(commands, step)
        action = np.clip(policy(obs), -1.0, 1.0)
        data.ctrl[:] = np.where(action >= 0.0, action * ctrl_high, -action * ctrl_low)
        if not walking and shove_every and step > 0 and step % shove_every == 0:
            angle = rng.uniform(0.0, 2.0 * math.pi)
            data.xfrc_applied[pelvis_id, :3] = np.array([math.cos(angle), math.sin(angle), 0.0]) * args.shove_newtons
            shove_left = shove_steps
        for _ in range(args.decimation):
            if shove_left > 0:
                shove_left -= 1
                if shove_left == 0:
                    data.xfrc_applied[pelvis_id, :] = 0.0
            mujoco.mj_step(model, data)

        v = data.cvel[pelvis_id, 3:5]
        speed_sum += float(np.dot(v, direction))
        foot_z = data.xpos[foot_ids, 2]
        left_down, right_down = foot_z[0] < foot_contact_z[0], foot_z[1] < foot_contact_z[1]
        clearance_sum += max(0.0, float(np.max(foot_z - np.array(rest_foot_z))))
        s = -1 if (left_down and not right_down) else (1 if (right_down and not left_down) else 0)
        if s != 0 and stance != 0 and s != stance:
            switches += 1
        if s != 0:
            stance = s
        if data.xpos[head_id, 2] < 0.55 * rest_head_z:
            down += 1
        if data.xpos[pelvis_id, 2] < 0.3:
            falls += 1
            reset()
            start = data.xpos[pelvis_id, :2].copy()

        if viewer is not None:
            viewer.sync()
            if not viewer.is_running():
                break
            lag = wall + (step + 1) * control_dt - time.time()
            if lag > 0:
                time.sleep(lag)
    distance = float(np.linalg.norm(data.xpos[pelvis_id, :2] - start))
    n = max(1, step + 1)
    return {
        "mode": "WALK" if walking else "BALANCE", "cmd": speed, "measured": speed_sum / n,
        "distance": distance, "alternation": min(1.0, (switches / n) / TARGET_SWITCHES_PER_STEP),
        "switches": switches, "clearance": clearance_sum / n, "down": down, "steps": n, "falls": falls,
    }


def main():
    policy = load_policy()
    if policy is None:
        print("no checkpoint yet under %s; waiting..." % (LOG_ROOT / args.run))
        while policy is None:
            time.sleep(10)
            policy = load_policy()
    print("policy: %s" % policy.name)

    viewer = None
    if not args.headless:
        import mujoco.viewer
        viewer = mujoco.viewer.launch_passive(model, data)
        viewer.cam.distance = 3.5
        viewer.cam.elevation = -15
        viewer.cam.azimuth = 135
        viewer.cam.lookat[:] = [0.0, 0.0, 0.9]

    walking = False
    phase = 0
    while args.phases <= 0 or phase < args.phases:
        if viewer is not None and not viewer.is_running():
            break
        if args.follow:
            reloaded = load_policy(policy)
            if reloaded is not policy:
                policy = reloaded
                print("policy: %s" % policy.name)
        r = run_phase(policy, walking, viewer)
        print("NICK_WATCH %d | %s | cmd=%.2f measured=%.3f m/s distance=%.3f m alternation=%.3f "
              "switches=%d clearance=%.3f m down=%d/%d steps falls=%d"
              % (phase, r["mode"], r["cmd"], r["measured"], r["distance"], r["alternation"],
                 r["switches"], r["clearance"], r["down"], r["steps"], r["falls"]), flush=True)
        walking = not walking
        phase += 1
    if viewer is not None:
        viewer.close()


if __name__ == "__main__":
    main()
