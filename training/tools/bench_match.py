"""How many rings, and how long a rollout, for the boxing stage on this GPU? Times whole PPO iterations.

    python tools/bench_match.py                                   4096, 8192, 16384 rings x rollouts of 24 and 32
    python tools/bench_match.py --sizes 4096 8192 --steps 24 --write logs/bench_match.json

tools/bench_envs.py times the get-up stage's physics and policy only; this times what a gauntlet run spends: the
match stage (envs/match.py, a v2 pair, daze on), a rollout of --steps per ring and the PPO update over it, with
the minibatch held near 24k samples as train_gauntlet.py holds it. Each (rings, steps) is its own process, so the
GPU's memory is given back between them. Prints boxer-steps a second over three timed iterations, after one to
warm up, and the GPU memory in use. GPU only: one job at a time (CLAUDE.md), nothing else on the card.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def one(n: int, steps: int, xml: str) -> None:
    import torch
    sys.path.insert(0, HERE)
    from envs.match import MatchEnv
    from ppo import PPO, PPOConfig
    env = MatchEnv(xml, n, device="cuda", daze=True, push_vel=0.4)
    ppo = PPO(env.obs_dim, env.A, n * env.K, "cuda", PPOConfig(steps_per_env=steps, minibatches=max(1, round(steps * n * env.K / 24576))))
    obs = env.reset()
    for it in range(4):
        if it == 1:
            torch.cuda.synchronize()
            t0 = time.time()
        with torch.no_grad():
            for _ in range(steps):
                obs, r, d, t = env.step(ppo.act(obs))
                ppo.record(r, d, t)
        ppo.update(obs)
    torch.cuda.synchronize()
    dt = (time.time() - t0) / 3
    free, total = torch.cuda.mem_get_info()
    print(json.dumps({"n": n, "steps": steps, "sps": steps * n * env.K / dt, "iter_s": dt, "gpu_used_mb": (total - free) / 2 ** 20}), flush=True)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--sizes", nargs="*", type=int, default=[4096, 8192, 16384])
    ap.add_argument("--steps", nargs="*", type=int, default=[24, 32], help="rollout lengths, control steps per ring")
    ap.add_argument("--xml", default=os.path.join(HERE, "models", "v2", "matt_vs_zombie_spar.xml"))
    ap.add_argument("--write", default="")
    ap.add_argument("--one", nargs=2, type=int, default=None)
    args = ap.parse_args()
    if args.one:
        one(args.one[0], args.one[1], args.xml)
        return
    rows = []
    for steps in args.steps:
        for n in args.sizes:
            try:
                out = subprocess.run([sys.executable, os.path.abspath(__file__), "--one", str(n), str(steps), "--xml", args.xml],
                                     capture_output=True, text=True, timeout=1200, cwd=HERE)
                line = [ln for ln in out.stdout.splitlines() if ln.startswith("{")]
                if not line:
                    tail = (out.stderr or out.stdout).strip().splitlines()
                    print(f"{n:6d} rings x {steps}: failed ({tail[-1][:160] if tail else 'no output'})", flush=True)
                    break                                   # a bigger one will not fit either
                rows.append(json.loads(line[-1]))
                r = rows[-1]
                print(f"{n:6d} rings x {steps}: {r['sps']:9.0f} boxer-steps a second, {r['iter_s']:5.2f} s an iteration, "
                      f"{r['gpu_used_mb']:.0f} MB of GPU memory", flush=True)
            except Exception as e:
                print(f"{n:6d} rings x {steps}: failed ({e})", flush=True)
                break
    best = max(rows, key=lambda r: r["sps"]) if rows else None
    if best:
        print(f"fastest: {best['n']} rings x {best['steps']} ({best['sps']:.0f} a second). More rings per update is also a "
              f"bigger batch: the learning rate and the iteration counts in a queue were set at 2304 to 4608 rings.")
    if args.write:
        with open(args.write if os.path.isabs(args.write) else os.path.join(HERE, args.write), "w", encoding="utf-8") as f:
            json.dump({"best": best, "rows": rows, "when": time.strftime("%Y-%m-%d %H:%M:%S")}, f, indent=1)


if __name__ == "__main__":
    main()
