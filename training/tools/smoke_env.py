"""A few hundred steps of an environment with given policies, and the numbers that come out: the quick look
before a long run is started on a changed reward.

    python tools/smoke_env.py --run match --daze --daze-hi 36 --seconds 20
    python tools/smoke_env.py --run match --stage getup --seconds 20

Acts with the exploration noise the run would start with (--std), because that is what training sees.
"""
from __future__ import annotations

import argparse
import os
import sys

import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from envs.boxing import BoxingEnv  # noqa: E402
from envs.getup import GetUpEnv  # noqa: E402
from ppo import PPO, PPOConfig  # noqa: E402


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--a", default="matt")
    ap.add_argument("--b", default="zombie")
    ap.add_argument("--run", default="match")
    ap.add_argument("--stage", default="auto")
    ap.add_argument("--envs", type=int, default=512)
    ap.add_argument("--seconds", type=float, default=20.0)
    ap.add_argument("--std", type=float, default=-1.0, help="exploration noise; below zero, the checkpoint's own")
    ap.add_argument("--daze", action="store_true")
    ap.add_argument("--daze-lo", type=float, default=14.0)
    ap.add_argument("--daze-hi", type=float, default=36.0)
    ap.add_argument("--daze-weak", type=float, default=0.45)
    ap.add_argument("--daze-tau", type=float, default=2.5)
    ap.add_argument("--block-w", type=float, default=0.05)
    ap.add_argument("--assist", type=float, default=0.6)
    args = ap.parse_args()

    xml = os.path.join(HERE, "models", f"{args.a}_vs_{args.b}_spar.xml")
    if args.stage == "getup":
        env = GetUpEnv(xml, args.envs, obs_noise=1.0, assist=args.assist)
    else:
        env = BoxingEnv(xml, args.envs, obs_noise=1.0, push_vel=0.4, ko_bonus=10.0, survivor_bootstrap=True,
                        daze=args.daze, daze_lo=args.daze_lo, daze_hi=args.daze_hi, daze_weak=args.daze_weak,
                        daze_tau=args.daze_tau, block_w=args.block_w)
    N, K, A = env.N, env.K, env.A
    ppos = []
    for n in (args.a, args.b):
        ppo = PPO(env.obs_dim, A, N, "cuda", PPOConfig())
        ppo.load(os.path.join(HERE, "checkpoints", args.run, f"latest_{n}.pt"))
        ppos.append(ppo)
    obs = env.reset()
    steps = int(args.seconds / env.dt)
    with torch.no_grad():
        for i in range(steps):
            o = obs.reshape(N, K, -1)
            acts = []
            for k in range(K):
                x = ppos[k].obs_rms.normalize(o[:, k], 10.0)
                mean = ppos[k].model.actor(x)
                std = args.std if args.std >= 0.0 else ppos[k].model.log_std.exp()
                acts.append(mean + torch.randn_like(mean) * std)
            obs, rew, done, cut = env.step(torch.stack(acts, 1).reshape(N * K, A))
            if (i + 1) % (steps // 4) == 0:
                s = env.get_stats()
                keys = [k for k in s if not k.startswith("rt_") and s[k] != 0.0]
                print(f"t={(i + 1) * env.dt:5.1f}s  " + "  ".join(f"{k}={s[k]:.3g}" for k in keys))
                print("        reward/step: " + "  ".join(f"{k[3:]}={s[k]:.3f}" for k in s if k.startswith("rt_")))


if __name__ == "__main__":
    main()
