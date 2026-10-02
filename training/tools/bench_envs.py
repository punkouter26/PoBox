"""How many rings should a pair of boxers train in on this GPU? Times the get-up stage at a few sizes.

    python tools/bench_envs.py                      2048, 3072 and 4096 rings of matt and zombie
    python tools/bench_envs.py --sizes 2048 4096 --write logs/bench_envs.json

Each size is its own process (the GPU's memory is only really given back when a process ends). Prints
boxer-steps a second, physics and policy together, and the GPU memory it took. The answer is written as
{"best": N, ...}; "best" is the largest size that is at least 8% faster than the one before it and fits.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def one(n: int, xml: str, steps: int) -> None:
    import torch
    sys.path.insert(0, HERE)
    from envs.getup import GetUpEnv
    from ppo import PPO, PPOConfig
    env = GetUpEnv(xml, n, device="cuda")
    ppos = [PPO(env.obs_dim, env.A, n, "cuda", PPOConfig(steps_per_env=24)) for _ in range(env.K)]
    obs = env.reset()
    split = lambda t: [t.reshape(n, env.K, *t.shape[1:])[:, k] for k in range(env.K)]
    for timed in (False, True):
        torch.cuda.synchronize()
        t0 = time.time()
        with torch.no_grad():
            for i in range(steps if timed else 24):
                acts = [p.model.actor(p.obs_rms.normalize(o, 10.0)) for p, o in zip(ppos, split(obs))]
                obs, _, _, _ = env.step(torch.stack(acts, 1).reshape(n * env.K, env.A))
        torch.cuda.synchronize()
        dt = time.time() - t0
    free, total = torch.cuda.mem_get_info()
    print(json.dumps({"n": n, "sps": steps * n * env.K / dt, "gpu_used_mb": (total - free) / 2 ** 20}), flush=True)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--sizes", nargs="*", type=int, default=[2048, 3072, 4096])
    ap.add_argument("--xml", default=os.path.join(HERE, "models", "matt_vs_zombie_spar.xml"))
    ap.add_argument("--steps", type=int, default=96)
    ap.add_argument("--write", default="")
    ap.add_argument("--one", type=int, default=0)
    args = ap.parse_args()
    if args.one:
        one(args.one, args.xml, args.steps)
        return
    rows = []
    for n in args.sizes:
        try:
            out = subprocess.run([sys.executable, os.path.abspath(__file__), "--one", str(n), "--xml", args.xml, "--steps", str(args.steps)],
                                 capture_output=True, text=True, timeout=600, cwd=HERE)
            line = [ln for ln in out.stdout.splitlines() if ln.startswith("{")]
            if line:
                rows.append(json.loads(line[-1]))
                print(f"{n:5d} rings: {rows[-1]['sps']:8.0f} boxer-steps a second, {rows[-1]['gpu_used_mb']:.0f} MB of GPU memory in use", flush=True)
            else:
                print(f"{n:5d} rings: failed ({(out.stderr or out.stdout).strip().splitlines()[-1][:160] if (out.stderr or out.stdout).strip() else 'no output'})", flush=True)
                break
        except Exception as e:
            print(f"{n:5d} rings: failed ({e})", flush=True)
            break
    best = rows[0]["n"] if rows else 2048
    for prev, cur in zip(rows, rows[1:]):
        if cur["sps"] >= prev["sps"] * 1.08:
            best = cur["n"]
        else:
            break
    print(f"best: {best}")
    if args.write:
        with open(args.write if os.path.isabs(args.write) else os.path.join(HERE, args.write), "w", encoding="utf-8") as f:
            json.dump({"best": best, "rows": rows, "when": time.strftime("%Y-%m-%d %H:%M:%S")}, f, indent=1)


if __name__ == "__main__":
    main()
