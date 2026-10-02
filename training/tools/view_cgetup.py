"""Watch a boxer getting up, in MuJoCo's own viewer, as the CPU trainer (train_getup_cpu.py) or the exam sees it.

    python tools/view_cgetup.py --run cgetup_matt             follows the run: the newest checkpoint, reloaded as it changes
    python tools/view_cgetup.py --name matt --ckpt checkpoints/getup_matt_zombie/latest_matt.pt

One world of envs/cgetup.py in plain C MuJoCo, in real time, with no helping hand and the policy's own
choice of action (no exploration noise): what the game will get. It costs a few per cent of one core.
"""
from __future__ import annotations

import argparse
import ctypes
import glob
import os
import sys
import time

import mujoco
import mujoco.viewer
import numpy as np
import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from envs.cgetup import CGetUp  # noqa: E402
from ppo import PPO, PPOConfig  # noqa: E402


def newest(run: str) -> str:
    files = glob.glob(os.path.join(HERE, "checkpoints", run, "latest_*.pt"))
    return max(files, key=os.path.getmtime) if files else ""


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", default="")
    ap.add_argument("--name", default="")
    ap.add_argument("--ckpt", default="")
    ap.add_argument("--seed", type=int, default=3)
    args = ap.parse_args()
    if os.name == "nt":
        ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x00000040)   # idle
    torch.set_num_threads(1)
    ckpt = args.ckpt or newest(args.run)
    if not ckpt:
        raise SystemExit("no checkpoint yet")
    name = args.name or os.path.basename(ckpt)[len("latest_"):-3]
    env = CGetUp(name, 1, threads=1, seed=args.seed, assist=0.0, obs_noise=0.0, stand_share=0.0, slack_lo=1.2)
    ppo = PPO(100, env.A, 1, "cpu", PPOConfig())
    extra = ppo.load(ckpt)
    stamp = os.path.getmtime(ckpt)
    obs = torch.from_numpy(env.reset())
    env.step_count[:] = 0
    d = env.datas[0]
    checked = time.time()
    with mujoco.viewer.launch_passive(env.m, d) as v:
        v.cam.distance, v.cam.elevation, v.cam.azimuth = 4.2, -14.0, 130.0
        while v.is_running():
            t0 = time.time()
            with torch.no_grad():
                a = ppo.model.actor(ppo.obs_rms.normalize(obs, 10.0))
            o, _, _, _ = env.step(a.numpy())
            obs = torch.from_numpy(o)
            v.cam.lookat[:] = [env.POS[0, 0], env.POS[0, 1], 0.7]
            v.sync()
            if args.run and time.time() - checked > 30.0:
                checked = time.time()
                latest = newest(args.run)
                if latest and os.path.getmtime(latest) != stamp:
                    try:
                        extra = ppo.load(latest)
                        stamp = os.path.getmtime(latest)
                        print(f"iteration {extra.get('iter', 0)}, help in training {extra.get('assist', 0.0):.2f}", flush=True)
                    except Exception as e:
                        print(f"could not load yet: {e}", flush=True)
            time.sleep(max(0.0, env.dt - (time.time() - t0)))


if __name__ == "__main__":
    main()
