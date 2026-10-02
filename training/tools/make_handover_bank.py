"""The states a boxer's get-up policy leaves its body in, for the match policy to practise taking over from.

    python tools/make_handover_bank.py --name matt --ckpt checkpoints/getup4_matt_zombie/latest_matt.pt
    -> logs/handover_matt.npz

Why. The match policy has only ever started an episode in its guard, joints within a few hundredths of a
radian of it. The get-up policy hands it a body that is standing but not there: arms low, trunk turned,
feet where they ended up. Handed that, Matt's match policy fell over in a third of the trials, whatever the
other boxer did, and no scripted way of easing the change (blending to the guard, cross-fading the two
policies) did anything but make it worse. So the match policy is trained on the real thing: a share of
its episodes begin in a state from this file (envs/boxing.py, handover; train_gauntlet.py --handover).

Each state is the boxer's body HANDOVER_S after it got to its feet (tools/exam.py and the game wait the
same time), in plain C MuJoCo, with the policy's own choice of action: joint angles and speeds, pelvis
height, the pelvis's tilt with its heading taken out, and its velocities in its own heading.
"""
from __future__ import annotations

import argparse
import os
import sys

import numpy as np
import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from envs.cgetup import CGetUp, quat_yaw, to_heading  # noqa: E402
from ppo import PPO, PPOConfig  # noqa: E402

HANDOVER_S = 1.5


def quat_mul(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    aw, ax, ay, az = a[..., 0], a[..., 1], a[..., 2], a[..., 3]
    bw, bx, by, bz = b[..., 0], b[..., 1], b[..., 2], b[..., 3]
    return np.stack([aw * bw - ax * bx - ay * by - az * bz, aw * bx + ax * bw + ay * bz - az * by,
                     aw * by - ax * bz + ay * bw + az * bx, aw * bz + ax * by - ay * bx + az * bw], -1)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", required=True)
    ap.add_argument("--ckpt", required=True)
    ap.add_argument("--n", type=int, default=1500)
    ap.add_argument("--worlds", type=int, default=64)
    ap.add_argument("--threads", type=int, default=4)
    ap.add_argument("--out", default="")
    args = ap.parse_args()
    torch.set_num_threads(2)
    env = CGetUp(args.name, args.worlds, threads=args.threads, assist=0.0, obs_noise=1.0, stand_share=0.0, slack_lo=0.6, seed=7)
    ppo = PPO(100, env.A, 1, "cpu", PPOConfig())
    extra = ppo.load(args.ckpt if os.path.isabs(args.ckpt) else os.path.join(HERE, args.ckpt))
    obs = torch.from_numpy(env.reset())
    env.step_count[:] = 0
    keep = {k: [] for k in ("joint", "jvel", "z", "tilt", "lin", "ang")}
    episodes = got = 0
    while got < args.n and episodes < args.n * 6:
        with torch.no_grad():
            a = ppo.model.actor(ppo.obs_rms.normalize(obs, 10.0))
        o, _, done, _ = env.step(a.numpy())
        obs = torch.from_numpy(o)
        episodes += int(done.sum())
        ready = np.nonzero((env.stood_for >= HANDOVER_S) & (env.step_count < env.max_steps - 1))[0]
        if len(ready):
            yaw = quat_yaw(env.QUAT[ready])
            unyaw = np.stack([np.cos(-yaw / 2), np.zeros_like(yaw), np.zeros_like(yaw), np.sin(-yaw / 2)], -1)
            keep["joint"].append(env.QJ[ready].copy())
            keep["jvel"].append(env.VJ[ready].copy())
            keep["z"].append(env.POS[ready, 2].copy())
            keep["tilt"].append(quat_mul(unyaw, env.QUAT[ready]))
            keep["lin"].append(to_heading(env.ROOTV[ready, :3], yaw))
            keep["ang"].append(env.ROOTV[ready, 3:6].copy())
            got += len(ready)
            env.step_count[ready] = env.max_steps        # that world's episode is over: the next step resets it
            episodes += len(ready)
    bank = {k: np.concatenate(v).astype(np.float32) for k, v in keep.items()}
    out = args.out or os.path.join(HERE, "logs", f"handover_{args.name}.npz")
    np.savez(out, **bank, default=env.default.astype(np.float32), stand=np.float32(env.stand))
    err = np.abs(bank["joint"] - env.default).mean(-1)
    print(f"{args.name}: {len(bank['z'])} hand-over states from {episodes} knockdowns (get-up policy at iteration {extra.get('iter', 0)}) -> "
          f"{os.path.relpath(out, HERE)}; off the guard by {err.mean():.2f} rad a joint (worst {err.max():.2f}), "
          f"pelvis at {bank['z'].mean() / env.stand:.0%} of standing height, moving {np.linalg.norm(bank['lin'][:, :2], axis=-1).mean():.2f} m/s")


if __name__ == "__main__":
    main()
