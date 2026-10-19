"""Export a v2 get-up checkpoint (103 inputs) next to itself as latest_NAME.onnx, only if its exam passed getting up.

    python tools/export_getup_v2.py --name zombie --ckpt checkpoints/u1_d10_zombie_matt/latest_zombie.pt --exam logs/exam_d10_zombie.json
    python tools/export_getup_v2.py ... --min-rate 0.97        also beat this share of knockdowns got up from (the shipped brain's)

The same file u1_d9_grandpa_matt's latest_grandpa.onnx is (ppo.export_onnx at 103); MjRetrofit.Promote copies it into Assets.
"""
from __future__ import annotations

import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from ppo import PPO, PPOConfig, export_onnx  # noqa: E402


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", required=True)
    ap.add_argument("--ckpt", required=True)
    ap.add_argument("--exam", required=True)
    ap.add_argument("--min-rate", type=float, default=0.0)
    a = ap.parse_args()
    row = json.load(open(os.path.join(HERE, a.exam), encoding="utf-8"))["boxers"][a.name]
    g, ok = row["getup"], row["lines"]["getting up"]["ok"]
    rate = g["up"] / max(1, g["floored"])
    if not ok or rate < a.min_rate:
        print(f"{a.name}: not exported (getting up {g['up']} of {g['floored']}, exam {'pass' if ok else 'FAIL'}, needs {a.min_rate:.0%})")
        return
    ppo = PPO(103, 21, 1, "cpu", PPOConfig())
    extra = ppo.load(os.path.join(HERE, a.ckpt))
    assert extra.get("mode") == "getup", extra.get("mode")
    out = os.path.join(os.path.dirname(os.path.join(HERE, a.ckpt)), f"latest_{a.name}.onnx")
    export_onnx(ppo, out, 103)
    print(f"{a.name}: exported {out} (getting up {g['up']} of {g['floored']}, {g['t_up'] / max(1, g['up']):.1f} s)")


if __name__ == "__main__":
    main()
