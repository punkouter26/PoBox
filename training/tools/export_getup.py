"""Hand a boxer's get-up policy to the game: the ONNX file and the manifest Unity's importer looks for.

    python tools/export_getup.py --name matt --ckpt checkpoints/getup_matt_zombie/latest_matt.pt
    python tools/export_getup.py --from-exam logs/exam.json          every boxer whose exam passed "getting up"

The get-up trainers (train_getup.py, train_getup_cpu.py) write checkpoints only, so that a policy that is
half way there is never taken for a finished one. This writes checkpoints/getup_final/latest_NAME.onnx and
latest_NAME_policy_config.json (with "mode": "getup"), which is what PoBox/Import Trained Entrants copies
to Assets/Entrants/NAME/getup.onnx. To take a policy back out of the game, delete its two files here.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from ppo import PPO, PPOConfig, export_onnx  # noqa: E402

OUT = os.path.join(HERE, "checkpoints", "getup_final")


def export(name: str, ckpt: str, note: str = "") -> None:
    with open(os.path.join(HERE, "models", f"{name}_policy_config.json"), "r", encoding="utf-8") as f:
        cfg = json.load(f)
    A = len(cfg["joint_order"])
    ppo = PPO(100, A, 1, "cpu", PPOConfig())
    extra = ppo.load(ckpt if os.path.isabs(ckpt) else os.path.join(HERE, ckpt))
    if extra.get("mode") != "getup":
        raise SystemExit(f"{ckpt} is a '{extra.get('mode')}' policy, not a get-up policy")
    os.makedirs(OUT, exist_ok=True)
    export_onnx(ppo, os.path.join(OUT, f"latest_{name}.onnx"), 100)
    manifest = dict(cfg)
    manifest.update({"action_scale": float(extra.get("action_scale", 0.5)), "observation_size": 100,
                     "trained_by": {"run": extra.get("run_name", ""), "mode": "getup", "against": extra.get("fighters", [name]),
                                    "iterations": int(extra.get("iter", 0)), "trainer": extra.get("trainer", "gpu"),
                                    "exported": time.strftime("%Y-%m-%d %H:%M:%S"), "note": note}})
    with open(os.path.join(OUT, f"latest_{name}_policy_config.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
    print(f"{name}: {os.path.relpath(ckpt, HERE) if os.path.isabs(ckpt) else ckpt} (iteration {extra.get('iter', 0)}) -> checkpoints/getup_final/latest_{name}.onnx")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", default="")
    ap.add_argument("--ckpt", default="")
    ap.add_argument("--from-exam", default="")
    args = ap.parse_args()
    if args.from_exam:
        with open(args.from_exam if os.path.isabs(args.from_exam) else os.path.join(HERE, args.from_exam), "r", encoding="utf-8") as f:
            exam = json.load(f)
        for name, r in exam["boxers"].items():
            line = r["lines"].get("getting up", {})
            if line.get("ok") and r.get("getup"):
                export(name, r["getup"]["policy"], note=line.get("text", ""))
            else:
                print(f"{name}: not exported ({line.get('text', 'no get-up result')})")
    elif args.name and args.ckpt:
        export(args.name, args.ckpt)
    else:
        raise SystemExit("--name and --ckpt, or --from-exam")


if __name__ == "__main__":
    main()
