"""Fresh-start evaluation of a Nick checkpoint on a MuJoCo Warp batch.

    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/eval_nick.py --run nick01
    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/eval_nick.py --run nick01 --checkpoint model_1500.pt --worlds 1024

WHY A SEPARATE EVALUATOR. The 2026-08-31 line recorded in-training metrics
disagreeing with fresh-start rollouts by up to 100x, because training
measures a steady-state population dominated by survivors while every real
episode starts cold. This starts every world from the rest pose, exactly as
Unity does, and reads survival against time. Two tables:

    BALANCE  command 0 m/s, shoved every 4 s (150 N for 0.2 s), 30 s cap
    WALK     command 1 m/s straight ahead, 20 s cap

Domain randomisation is OFF (nominal gains and friction) so the numbers
describe the creature Unity runs, not the training distribution.
"""
from __future__ import annotations

import argparse
import math
import re
import sys
from pathlib import Path

import numpy as np
import torch

HERE = Path(__file__).parent
REPO = HERE.parent.parent
LOG_ROOT = REPO / "results" / "nick"

sys.path.insert(0, str(HERE))
from nick_env import ACT_DIM, OBS_BASE, OBS_COMMAND, NickEnv, NickEnvCfg, preferred_model_path   # noqa: E402

parser = argparse.ArgumentParser()
parser.add_argument("--run", default="nick01")
parser.add_argument("--checkpoint", default=None)
parser.add_argument("--onnx", default=None)
parser.add_argument("--worlds", type=int, default=512)
parser.add_argument("--balance-seconds", type=float, default=30.0)
parser.add_argument("--walk-seconds", type=float, default=20.0)
parser.add_argument("--walk-speed", type=float, default=1.0)
parser.add_argument("--shove-newtons", type=float, default=150.0)
parser.add_argument("--shove-interval", type=float, default=4.0)
args = parser.parse_args()

OBS_DIM = OBS_BASE + OBS_COMMAND


def load_policy():
    if args.onnx:
        import onnxruntime as ort
        sess = ort.InferenceSession(args.onnx, providers=["CPUExecutionProvider"])
        name = sess.get_inputs()[0].name

        def onnx_policy(obs):
            return torch.tensor(sess.run(None, {name: obs.cpu().numpy()})[0], device=obs.device)
        return onnx_policy, Path(args.onnx).name

    from rsl_rl.modules import ActorCritic, EmpiricalNormalization
    run_dir = LOG_ROOT / args.run
    if args.checkpoint:
        path = run_dir / args.checkpoint
    else:
        candidates = sorted(run_dir.glob("model_*.pt"), key=lambda p: int(re.search(r"(\d+)", p.stem).group(1)))
        if not candidates:
            raise SystemExit("no checkpoints under %s" % run_dir)
        path = candidates[-1]
    ckpt = torch.load(path, map_location="cpu", weights_only=False)
    net = ActorCritic(OBS_DIM, OBS_DIM, ACT_DIM, [512, 256, 128], [512, 256, 128], "elu").to("cuda:0").eval()
    net.load_state_dict(ckpt["model_state_dict"])
    norm = EmpiricalNormalization(shape=[OBS_DIM]).to("cuda:0").eval()
    norm.load_state_dict(ckpt["obs_norm_state_dict"])

    def torch_policy(obs):
        with torch.no_grad():
            return net.actor(norm(obs))
    return torch_policy, "%s (iteration %s)" % (path.name, ckpt.get("iter", "?"))


def rollout(env: NickEnv, policy, speed: float, seconds: float, shove: bool) -> dict:
    n = env.num_envs
    dev = env.device
    steps = int(round(seconds / env.control_dt))
    env.reset()
    env.commands[:, 0] = speed
    env.commands[:, 1] = 0.0
    env.commands[:, 2] = -1.0        # straight ahead from the rest heading
    env.episode_length_buf[:] = 0
    env.push_steps_left[:] = 0
    obs = env._observations()

    alive = torch.ones(n, dtype=torch.bool, device=dev)
    survival = torch.full((n,), seconds, device=dev)
    upright_steps = torch.zeros(n, device=dev)
    speed_sum = torch.zeros(n, device=dev)
    switches = torch.zeros(n, device=dev)
    last_stance = torch.zeros(n, device=dev)
    start_xy = env.xpos[:, env.pelvis_id, :2].clone()
    shove_every = int(round(args.shove_interval / env.control_dt))
    shove_steps = max(1, int(round(0.2 / env.control_dt)))

    for step in range(steps):
        action = policy(obs).clamp(-1.0, 1.0)
        target = torch.where(action >= 0.0, action * env.ctrl_high, -action * env.ctrl_low)
        env.ctrl[:] = target.to(env.ctrl.dtype)
        if shove and step > 0 and step % shove_every == 0:
            angle = torch.rand(n, device=dev) * 2.0 * math.pi
            env.push_force[:] = torch.stack([torch.cos(angle), torch.sin(angle), torch.zeros(n, device=dev)], dim=1) * args.shove_newtons
            env.push_steps_left[:] = shove_steps
        active = env.push_steps_left > 0
        env.xfrc[:, env.pelvis_id, 0:3] = torch.where(active.unsqueeze(1), env.push_force, torch.zeros_like(env.push_force)).to(env.xfrc.dtype)
        env.push_steps_left = torch.clamp(env.push_steps_left - 1, min=0)
        torch.cuda.synchronize()
        for _ in range(env.cfg.decimation):
            import warp as wp
            wp.capture_launch(env._step_graph)
        wp.synchronize()
        env.episode_length_buf += 1

        s = env._state()
        fell = (s["pelvis_z"] < env.cfg.fall_pelvis_fraction * env.rest_pelvis_z) | (s["up"][:, 2] < env.cfg.fall_up_z)
        newly = fell & alive
        survival[newly] = (step + 1) * env.control_dt
        alive &= ~fell
        upright_steps += (s["up"][:, 2] > 0.7).float() * alive.float()
        v = s["lin_w"][:, :2]
        speed_sum += (-v[:, 1]) * alive.float()          # ahead is -y
        left_down, right_down = s["foot_down"][:, 0], s["foot_down"][:, 1]
        stance = torch.zeros(n, device=dev)
        stance = torch.where(left_down & ~right_down, -torch.ones_like(stance), stance)
        stance = torch.where(right_down & ~left_down, torch.ones_like(stance), stance)
        switched = (stance != 0) & (last_stance != 0) & (stance != last_stance) & alive
        switches += switched.float()
        last_stance = torch.where(stance != 0, stance, last_stance)
        obs = env._observations()

    lived = torch.clamp(survival / env.control_dt, min=1.0)
    distance = torch.norm(env.xpos[:, env.pelvis_id, :2] - start_xy, dim=1)
    return {
        "survival": survival.cpu().numpy(),
        "survived_all": float(alive.float().mean()),
        "upright": float((upright_steps / lived).mean()),
        "speed": float((speed_sum / lived).mean()),
        "alternation": float(torch.clamp((switches / lived) / (1.0 / 35.0), max=1.0).mean()),
        "distance": float(distance.mean()),
    }


def main():
    policy, name = load_policy()
    cfg = NickEnvCfg(num_envs=args.worlds, model_path=preferred_model_path(),
                     gain_scale_range=(1.0, 1.0), friction_scale_range=(1.0, 1.0),
                     exact_start_fraction=1.0, push_probability=0.0)
    env = NickEnv(cfg)
    print("policy: %s   worlds: %d   model: %s" % (name, args.worlds, cfg.model_path))
    b = rollout(env, policy, 0.0, args.balance_seconds, shove=True)
    w = rollout(env, policy, args.walk_speed, args.walk_seconds, shove=False)
    z = rollout(env, lambda obs: torch.zeros(obs.shape[0], ACT_DIM, device=obs.device), 0.0, 10.0, shove=False)

    def row(label, r, cap):
        s = r["survival"]
        return "%-8s median %5.2fs  mean %5.2fs  p25 %5.2fs  full-cap %4.0f%%  upright %.3f  speed %6.3f m/s  alternation %.3f  distance %6.2f m" % (
            label, float(np.median(s)), float(s.mean()), float(np.percentile(s, 25)), 100.0 * r["survived_all"],
            r["upright"], r["speed"], r["alternation"], r["distance"])

    print("NICK_EVAL " + row("BALANCE", b, args.balance_seconds) + "   (shoved %g N every %g s, cap %g s)" % (args.shove_newtons, args.shove_interval, args.balance_seconds))
    print("NICK_EVAL " + row("WALK", w, args.walk_seconds) + "   (command %g m/s, cap %g s)" % (args.walk_speed, args.walk_seconds))
    print("NICK_EVAL " + row("ZERO", z, 10.0) + "   (no policy: the passive baseline)")


if __name__ == "__main__":
    main()
