"""Which of v3's four changes a boxer's shipped footwork brain minds: v2 plus one change at a time, each examined.

    python tools/ablate_v3.py --name matt          (PYTHONPATH=.mj350; C MuJoCo, CPU, a few minutes)

Builds each variant into logs/ablate_v3/<change>/ (models only, nothing under models/ is touched), rescales the shipped
brain where the action scale changes (tools/rescale_actions.py), and runs tools/footwork_c.py's exam, 3 seeds x 2
episodes of each kind. Prints stand / walk / turn pass, walk falls a minute and walk velocity error per variant.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "tools"))
import rig_to_mjcf  # noqa: E402
from build_v3 import HEAD_FILL, HEAD_HALF, with_rules  # noqa: E402


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", default="matt")
    args = ap.parse_args()
    n = args.name
    v2cfg = json.load(open(os.path.join(HERE, "models", "v2", f"{n}_policy_config.json"), encoding="utf-8"))
    v3rig = json.load(open(os.path.join(HERE, "rigs", "v3", f"{n}.json"), encoding="utf-8"))
    base = json.load(open(os.path.join(HERE, "rigs", "v2", f"{n}.json"), encoding="utf-8"))
    variants = {
        "v2": {},
        "force": {"human_force": True},
        "kv": {"joint_kv": v3rig["joint_kv"]},
        "action": {"per_joint_action": True},
        "head": {"radii": dict(base["radii"], head=round(HEAD_FILL * HEAD_HALF[n], 4))},
        "v3": {"human_force": True, "joint_kv": v3rig["joint_kv"], "per_joint_action": True,
               "radii": dict(base["radii"], head=round(HEAD_FILL * HEAD_HALF[n], 4))},
    }
    env = dict(os.environ, PYTHONPATH=os.path.join(HERE, ".mj350"))
    rows = {}
    for key, change in variants.items():
        out = os.path.join(HERE, "logs", "ablate_v3", key)
        os.makedirs(out, exist_ok=True)
        rig = dict(base, **change)
        rig_path = os.path.join(out, f"{n}.json")
        json.dump(rig, open(rig_path, "w", encoding="utf-8"))
        _, cfg = rig_to_mjcf.build_one(rig, rig_path, n, out, int(v2cfg.get("cubes", 0)))
        cfg = with_rules(cfg, v2cfg)
        json.dump(cfg, open(os.path.join(out, f"{n}_policy_config.json"), "w", encoding="utf-8"), indent=1)
        policy = os.path.join(HERE, "checkpoints", "onnx", f"policy_{n}.pt")
        if "action_scale_j" in cfg:
            scaled = os.path.join(out, f"policy_{n}.pt")
            subprocess.run([sys.executable, os.path.join(HERE, "tools", "rescale_actions.py"), "--ckpt", policy,
                            "--config", os.path.join(out, f"{n}_policy_config.json"), "--out", scaled], cwd=HERE, env=env,
                           check=True, stdout=subprocess.DEVNULL)
            policy = scaled
        js = os.path.join(out, "exam.json")
        subprocess.run([sys.executable, os.path.join(HERE, "tools", "footwork_c.py"), "--name", n, "--models", out, "--policy", policy,
                        "--exam", "--seeds", "3", "--episodes", "2", "--json", js], cwd=HERE, env=env, check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        rows[key] = json.load(open(js, encoding="utf-8"))
    print(f"\n{n}: the shipped footwork brain on v2 plus one change at a time (3 seeds x 2 episodes of each kind)")
    print(f"{'variant':8s} {'stand':>6s} {'walk':>6s} {'turn':>6s} {'falls/min':>10s} {'vel err':>8s}")
    for key, r in rows.items():
        w = r.get("walk", {})
        print(f"{key:8s} {str(r.get('stand', {}).get('pass')):>6s} {str(w.get('pass')):>6s} {str(r.get('turn', {}).get('pass')):>6s} "
              f"{w.get('falls_per_min', float('nan')):10.2f} {w.get('velocity_error', float('nan')):8.3f}")


if __name__ == "__main__":
    main()
