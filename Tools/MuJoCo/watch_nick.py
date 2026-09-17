"""Watch a Nick checkpoint move in the MuJoCo viewer, and measure it.

    # newest checkpoint of a run, live viewer, BALANCE/WALK phases like the Unity demo
    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/watch_nick.py --run nick01

    # keep reloading the newest checkpoint as training writes them (train_nick.py does this)
    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/watch_nick.py --run nick01 --follow

    # the EXPORTED graph, through onnxruntime: proves the .onnx, not just the .pt
    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/watch_nick.py --onnx Assets/Agents/Nick_Locomotion/nick_locomotion.onnx

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
import json
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
parser.add_argument("--decimation", type=int, default=None)
parser.add_argument("--timestep", type=float, default=None)
parser.add_argument("--model", default=None, help="MJCF matching the policy's supplied rig")
parser.add_argument("--viewer", choices=("mujoco", "newton"), default="newton")
parser.add_argument("--shove-newtons", type=float, default=150.0)
parser.add_argument("--shove-seconds", type=float, default=0.2)
parser.add_argument("--shove-interval", type=float, default=4.0)
parser.add_argument("--seed", type=int, default=0)
args = parser.parse_args()

# A following viewer must use the run's actual body and control rate. In
# particular, a 0.02 x 1 policy must never silently replay at 0.005 x 4.
saved_env = {}
config_path = LOG_ROOT / args.run / "config.json"
if not args.onnx and config_path.exists():
    saved_env = json.loads(config_path.read_text())["env"]
args.model = args.model or saved_env.get("model_path") or preferred_model_path()
args.decimation = saved_env.get("decimation", 4) if args.decimation is None else args.decimation
saved_physics = saved_env.get("physics", {})
args.timestep = saved_physics.get("timestep", PHYSICS_TIMESTEP) if args.timestep is None else args.timestep
if args.decimation < 1 or args.timestep <= 0 or args.phase_seconds <= 0:
    parser.error("timestep, decimation and phase duration must be positive")

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
model = mujoco.MjModel.from_xml_path(args.model)
model.opt.timestep = args.timestep
if saved_physics:
    integrator_name = saved_physics.get("integrator")
    if integrator_name:
        model.opt.integrator = getattr(mujoco.mjtIntegrator, integrator_name.split(".")[-1])
    model.opt.iterations = saved_physics.get("solver_iterations", model.opt.iterations)
    model.dof_armature[:] *= saved_physics.get("armature_scale", 1.0)
    solref = saved_physics.get("solref_timeconst", 0.0)
    if solref > 0.0:
        model.geom_solref[:, 0] = solref
        model.opt.o_solref[0] = solref
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
control_dt = args.timestep * args.decimation

mujoco.mj_forward(model, data)
rest_foot_z = [float(data.xpos[i, 2]) for i in foot_ids]
foot_contact_z = [z + FOOT_CONTACT_ABOVE_REST for z in rest_foot_z]
rest_head_z = float(data.xpos[head_id, 2])
rest_pelvis_z = float(data.xpos[pelvis_id, 2])
# The hold window the run itself trained with (nickgetup01 trains at 2 s);
# fall back to the 4 s standard for older configs.
getup_stable = float(saved_env.get("getup_stable_seconds", 4.0))
rng = np.random.RandomState(args.seed)


def reset(getup: bool = False):
    mujoco.mj_resetData(model, data)
    data.qpos[:] = model.qpos0
    data.qvel[:] = 0.0
    data.ctrl[:] = 0.0
    data.xfrc_applied[:] = 0.0
    if getup:
        # The same fallen-start distribution nick_env's get-up task trains
        # from: root tilted past vertical about a random horizontal axis,
        # random yaw, hinges near rest, zero velocity, pelvis low.
        yaw = rng.uniform(0.0, 2.0 * math.pi)
        tilt = rng.uniform(65.0, 115.0) * math.pi / 180.0
        axis = rng.uniform(0.0, 2.0 * math.pi)
        half = tilt * 0.5
        tilt_q = np.array([math.cos(half), math.sin(half) * math.cos(axis),
                           math.sin(half) * math.sin(axis), 0.0])
        yh = yaw * 0.5
        yaw_q = np.array([math.cos(yh), 0.0, 0.0, math.sin(yh)])
        yt = np.zeros(4)
        mujoco.mju_mulQuat(yt, yaw_q, tilt_q)
        mujoco.mju_mulQuat(data.qpos[3:7], yt, model.qpos0[3:7].copy())
        data.qpos[2] = rng.uniform(0.10, 0.24)
        data.qpos[7:] = model.qpos0[7:] + rng.uniform(-0.3, 0.3, model.nq - 7)
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


def run_phase(policy, walking: bool, viewer, getup: bool = False) -> dict:
    reset(getup)
    fwd = pelvis_forward()
    direction = np.array([fwd[0], fwd[1]])
    direction /= max(np.linalg.norm(direction), 1e-6)
    speed = args.walk_speed if walking else 0.0
    commands = np.array([speed, direction[0], direction[1]], dtype=np.float32)
    start = data.xpos[pelvis_id, :2].copy()

    steps = int(round(args.phase_seconds / control_dt))
    shove_every = int(round(args.shove_interval / control_dt)) if args.shove_interval > 0 else 0
    shove_steps = max(1, int(round(args.shove_seconds / args.timestep)))
    shove_left = 0
    speed_sum = 0.0
    switches = 0
    stance = 0
    clearance_sum = 0.0
    down = 0
    falls = 0
    risen_steps = 0
    got_up = False
    rose_at = -1.0
    max_torque = np.zeros(model.nu)
    max_joint_speed = np.zeros(model.nu)
    actuator_dofs = model.jnt_dofadr[model.actuator_trnid[:, 0]]
    wall = time.time()
    up_vec = np.zeros(3)
    for step in range(steps):
        if viewer is not None and hasattr(viewer, "wait_for_step"):
            paused_at = time.time()
            viewer.wait_for_step()
            wall += time.time() - paused_at
            if not viewer.is_running():
                break
        obs = gather(commands, step)
        action = np.clip(policy(obs), -1.0, 1.0)
        data.ctrl[:] = np.where(action >= 0.0, action * ctrl_high, -action * ctrl_low)
        if not walking and not getup and shove_every and step > 0 and step % shove_every == 0:
            angle = rng.uniform(0.0, 2.0 * math.pi)
            data.xfrc_applied[pelvis_id, :3] = np.array([math.cos(angle), math.sin(angle), 0.0]) * args.shove_newtons
            shove_left = shove_steps
        for _ in range(args.decimation):
            mujoco.mj_step(model, data)
            if shove_left > 0:
                shove_left -= 1
                if shove_left == 0:
                    data.xfrc_applied[pelvis_id, :] = 0.0
            max_torque = np.maximum(max_torque, np.abs(data.actuator_force))
            max_joint_speed = np.maximum(max_joint_speed, np.abs(data.qvel[actuator_dofs]))

        v = data.cvel[pelvis_id, 3:5]
        speed_sum += float(np.dot(v, direction))
        if getup:
            # The training task's own rise rule: pelvis above 75% of rest
            # height AND pelvis up-axis past 0.7, held getup_stable seconds.
            mujoco.mju_rotVecQuat(up_vec, np.array([0.0, 0.0, 1.0]), data.xquat[pelvis_id])
            risen = (data.xpos[pelvis_id, 2] > 0.75 * rest_pelvis_z) and (up_vec[2] > 0.7)
            risen_steps = risen_steps + 1 if risen else 0
            if not got_up and risen_steps * control_dt >= getup_stable:
                got_up = True
                rose_at = (step + 1) * control_dt
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
            if not getup:
                # A get-up attempt must stay down and keep trying — resetting
                # him out of the floor would erase the very thing we watch.
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
        "mode": "GETUP" if getup else ("WALK" if walking else "BALANCE"),
        "cmd": speed, "measured": speed_sum / n,
        "distance": distance, "alternation": min(1.0, (switches / n) / TARGET_SWITCHES_PER_STEP),
        "switches": switches, "clearance": clearance_sum / n, "down": down, "steps": n, "falls": falls,
        "got_up": got_up, "rose_at": rose_at, "up_fraction": risen_steps / n,
        "max_torque": max_torque.tolist(), "max_joint_speed": max_joint_speed.tolist(),
    }


def main():
    viewer = None
    if not args.headless and args.viewer == "newton":
        from newton_viewer import NewtonMujocoViewer
        viewer = NewtonMujocoViewer(args.model, model, data, pelvis_id)
    elif not args.headless:
        import mujoco.viewer
        viewer = mujoco.viewer.launch_passive(model, data)
        viewer.cam.distance = 6.0
        viewer.cam.elevation = -20
        viewer.cam.azimuth = 135
        viewer.cam.lookat[:] = [0.0, 0.0, 0.9]
        # TRACK the pelvis instead of staring at the origin. A fixed lookat is
        # fine for BALANCE, where he holds station, and useless for WALK, where
        # he covers ~10 m in a 10 s phase and leaves the frame in the first two
        # seconds -- so the phase this tool exists to judge was the one phase
        # nobody could see.
        viewer.cam.type = mujoco.mjtCamera.mjCAMERA_TRACKING
        viewer.cam.trackbodyid = pelvis_id

    policy = load_policy()
    if policy is None:
        print("no checkpoint yet under %s; waiting..." % (LOG_ROOT / args.run), flush=True)
        while policy is None:
            for _ in range(100):
                if viewer is not None:
                    if not viewer.is_running():
                        viewer.close()
                        return
                    viewer.sync()
                time.sleep(0.1)
            policy = load_policy()
    print("policy: %s | model=%s | timestep=%.6f decimation=%d" %
          (policy.name, args.model, args.timestep, args.decimation), flush=True)

    walking = False
    getup = False
    # BALANCE -> WALK -> GETUP: the get-up behaviour the nickgetup01 line
    # trains is part of what this tool exists to let a human judge, so every
    # third phase starts him on the floor.
    cycle = ["BALANCE", "WALK", "GETUP"]
    phase = 0
    while args.phases <= 0 or phase < args.phases:
        if viewer is not None and not viewer.is_running():
            break
        if args.follow:
            reloaded = load_policy(policy)
            if reloaded is not policy:
                policy = reloaded
                print("policy: %s" % policy.name)
        r = run_phase(policy, walking, viewer, getup)
        extra = ""
        if r["mode"] == "GETUP":
            extra = " got_up=%s rise_at=%.1f s up_fraction=%.2f" % (r["got_up"], r["rose_at"], r["up_fraction"])
        print("NICK_WATCH %d | %s | cmd=%.2f measured=%.3f m/s distance=%.3f m alternation=%.3f "
              "switches=%d clearance=%.3f m down=%d/%d steps falls=%d%s"
              % (phase, r["mode"], r["cmd"], r["measured"], r["distance"], r["alternation"],
                 r["switches"], r["clearance"], r["down"], r["steps"], r["falls"], extra), flush=True)
        print("NICK_PHYSICS " + json.dumps({
            "mode": r["mode"], "actuators": [model.actuator(i).name for i in range(model.nu)],
            "max_torque_nm": r["max_torque"], "max_joint_speed_rad_s": r["max_joint_speed"],
        }), flush=True)
        mode_next = cycle[(phase + 1) % len(cycle)]
        walking = mode_next == "WALK"
        getup = mode_next == "GETUP"
        phase += 1
    if viewer is not None:
        viewer.close()


if __name__ == "__main__":
    main()
