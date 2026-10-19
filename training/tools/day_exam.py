"""The day's exam for one new match brain (2026-10-19): waits for its run to finish, then examines NAME's new brain against the
game's roster (logs/book_night_mix.json with only NAME's match brain swapped) exactly as exam_nightmix_NAME.json examined the
game's own brain (12 rounds, 30 knockdowns, the game's get-up rule, MuJoCo 3.5.0, on the CPU), and exports the ONNX
(at the brain's own input size, 103 or 106) only if it passes at least as many lines.

    python tools/day_exam.py NAME RUN            e.g. grandpa m14_grandpa; mel1_grandpa
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
L = lambda *p: os.path.join(HERE, "logs", *p)


def passed(path: str, name: str) -> tuple:
    lines = json.load(open(path, encoding="utf-8"))["boxers"][name]["lines"]
    return sum(1 for v in lines.values() if v["ok"]), lines


def main() -> None:
    name, run = sys.argv[1], sys.argv[2]
    ck = f"checkpoints/{run}/latest_{name}.pt"
    while not os.path.exists(L(f"{run}.done")):
        time.sleep(30)
    if not os.path.exists(os.path.join(HERE, ck)):
        raise SystemExit(f"{run}: no checkpoint")
    book = json.load(open(L("book_night_mix.json"), encoding="utf-8"))
    book[name]["match"] = ck
    with open(L(f"book_{run}.json"), "w", encoding="utf-8") as f:
        json.dump(book, f, indent=1)
    env = {**os.environ, "CUDA_VISIBLE_DEVICES": "-1", "PYTHONPATH": os.path.join(HERE, ".mj350"), "OMP_NUM_THREADS": "1", "MKL_NUM_THREADS": "1"}
    out = L(f"exam_{run}.json")
    if "--melee" in sys.argv:
        # The Melee exam too (tools/melee_exam.py, 2 bodies), started beside the 1v1 exam.
        melee = subprocess.Popen([sys.executable, "tools/melee_exam.py", name, "--book", L(f"book_{run}.json"), "--rounds", "2",
                                  "--json", L(f"melee_{run}.json")], cwd=HERE, env=env, stdout=open(L(f"melee_{run}.out"), "w"), stderr=subprocess.STDOUT)
    with open(L(f"exam_{run}.log"), "w", encoding="utf-8") as log:
        subprocess.call([sys.executable, "tools/exam_v2.py", "--only", name, "--book", L(f"book_{run}.json"), "--rounds", "12", "--trials", "30",
                         "--getup-no-drain", "--json", out], cwd=HERE, env=env, stdout=log, stderr=subprocess.STDOUT)
    p_old, old = passed(L(f"exam_nightmix_{name}.json"), name)
    p_new, new = passed(out, name)
    row = {"run": run, "game": p_old, "new": p_new, "export": p_new >= p_old,
           "lines": {k: [old[k]["text"], new[k]["text"], old[k]["ok"], new[k]["ok"]] for k in new}}
    if row["export"]:
        import torch
        from ppo import PPO, PPOConfig, export_onnx, obs_dim_of
        D = obs_dim_of(os.path.join(HERE, ck))
        ppo = PPO(D, 21, 1, "cpu", PPOConfig())
        ppo.load(os.path.join(HERE, ck))
        onnx = os.path.join(HERE, "checkpoints", run, f"latest_{name}.onnx")
        export_onnx(ppo, onnx, D, fixed_batch=True)
        row.update(onnx=os.path.relpath(onnx, HERE), inputs=D)
    with open(L(f"compare_{run}.json"), "w", encoding="utf-8") as f:
        json.dump(row, f, indent=1)
    print(f"{name} {run}: lines game {p_old} -> {p_new}: {'exported ' + row['onnx'] if row['export'] else 'not exported (worse)'}")
    for k, (t0, t1, o0, o1) in row["lines"].items():
        print(f"    {k:12s} {'pass' if o0 else 'FAIL'} {t0}\n                 -> {'pass' if o1 else 'FAIL'} {t1}")


if __name__ == "__main__":
    main()
