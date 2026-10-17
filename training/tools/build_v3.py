"""models/v3 from models/v2: the same eight bodies, with four changes for realism. v2 and the game are left alone.

    python tools/build_v3.py                 rigs/v3, models/v3 (every boxer, every pair), the checks
    python tools/build_v3.py --only matt     one boxer and the pairs it is in

Each rig is rigs/v2's, which rebuilds models/v2 byte for byte (checked 2026-10-16), plus:
  * "human_force": each way's force limit as a person's (rig_to_mjcf.HUMAN_FORCE: ankle dorsiflexion a fifth, knee
    flexion a half, hip rotation a third);
  * "per_joint_action": each joint's action scale is its reach from the guard over the clip (action_scale_j), so a
    clipped action is the end of the joint's range and an elbow can straighten;
  * the head's sphere at 95% of the head's half-width measured from the mesh (DOCS/reports/2026-10-16-collision-fit.html;
    v2's were 100-137%, the head colliding outside the skull);
  * "joint_kv": each drive's damping for a ratio of 0.85, measured standing on the v3 body (tools/pd_sweep.py --loaded).
The configs carry v2's rules (speed limit, fatigue, punch rules) and "fatigue_ceiling": true. Every boxer is checked for
self-contact in the T-pose, the guard and a swing (tools/check_self_collision.py) before its pairs are built.
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
from check_self_collision import check  # noqa: E402

NAMES = ("grandma", "grandpa", "kim", "matt", "nick", "sokol", "trump", "zombie")
# The head's half-width across, m, measured from each mesh by tools/fit_check.py on 2026-10-16.
HEAD_HALF = {"grandma": 0.118, "grandpa": 0.091, "kim": 0.091, "matt": 0.086, "nick": 0.078, "sokol": 0.094,
             "trump": 0.120, "zombie": 0.103}
HEAD_FILL = 0.95
RULES = ("speed_limit", "fatigue_j", "fatigue_rec_s", "fatigue_weak", "hit_push", "stagger_v", "hurt_full", "body_drain", "obs_state")


def with_rules(cfg: dict, v2: dict) -> dict:
    for k in RULES:
        if k in v2:
            cfg[k] = v2[k]
    cfg["fatigue_ceiling"] = True
    return cfg


def save(path: str, obj: dict) -> None:
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f, indent=2)


def build(name: str, v2_models: str, rigs: str, models: str) -> tuple:
    v2 = json.load(open(os.path.join(v2_models, f"{name}_policy_config.json"), encoding="utf-8"))
    rig = json.load(open(os.path.join(HERE, "rigs", "v2", f"{name}.json"), encoding="utf-8"))
    rig["human_force"] = True
    rig["per_joint_action"] = True
    rig["radii"]["head"] = round(HEAD_FILL * HEAD_HALF[name], 4)
    rig.pop("joint_kv", None)
    rig.setdefault("notes", []).append(f"v3 (tools/build_v3.py): human force each way, per-joint action scale, head "
                                       f"{rig['radii']['head']:.3f} m ({HEAD_FILL:.0%} of its half-width), joint_kv standing")
    rig_path = os.path.join(rigs, f"{name}.json")
    save(rig_path, rig)
    cubes = int(v2.get("cubes", 0))

    # Built once to measure the drives standing, then again with the damping that measurement asks for.
    z, cfg = rig_to_mjcf.build_one(rig, rig_path, name, models, cubes)
    save(os.path.join(models, f"{name}_policy_config.json"), with_rules(cfg, v2))
    env = dict(os.environ, PYTHONPATH=os.path.join(HERE, ".mj350"))
    subprocess.run([sys.executable, os.path.join(HERE, "tools", "pd_sweep.py"), "--name", name, "--loaded",
                    "--models", os.path.relpath(models, HERE), "--rig", rig_path,
                    "--write", os.path.join("logs", f"pd_v3_{name}.json")], cwd=HERE, env=env, check=True, stdout=subprocess.DEVNULL)
    rig = json.load(open(rig_path, encoding="utf-8"))
    z, cfg = rig_to_mjcf.build_one(rig, rig_path, name, models, cubes)
    cfg = with_rules(cfg, v2)
    save(os.path.join(models, f"{name}_policy_config.json"), cfg)
    if check(name, models) != 0:
        raise SystemExit(f"{name}: its own parts touch in v3; see the list above")
    return rig, z, cfg


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", action="append", default=[])
    ap.add_argument("--models", default=os.path.join(HERE, "models", "v3"))
    ap.add_argument("--rigs", default=os.path.join(HERE, "rigs", "v3"))
    args = ap.parse_args()
    v2_models = os.path.join(HERE, "models", "v2")
    os.makedirs(args.models, exist_ok=True)
    os.makedirs(args.rigs, exist_ok=True)

    built = {}
    for n in NAMES:
        if args.only and n not in args.only:
            rig = json.load(open(os.path.join(args.rigs, f"{n}.json"), encoding="utf-8"))
            cfg = json.load(open(os.path.join(args.models, f"{n}_policy_config.json"), encoding="utf-8"))
            built[n] = (rig, float(cfg["stand_height"]), cfg)
        else:
            print(f"\n==== {n}", flush=True)
            built[n] = build(n, v2_models, args.rigs, args.models)
    print("\n==== pairs", flush=True)
    for i, a in enumerate(NAMES):
        for b in NAMES[i + 1:]:
            if args.only and a not in args.only and b not in args.only:
                continue
            # The pair's file names as v2 has them, so every tool that finds a_vs_b or b_vs_a finds it here too.
            x, y = (a, b) if os.path.exists(os.path.join(v2_models, f"{a}_vs_{b}_spar.xml")) else (b, a)
            rx, zx, cx = built[x]
            ry, zy, cy = built[y]
            rig_to_mjcf.build_versus(rx, x, zx, cx, ry, y, zy, cy, args.models, int(cx.get("cubes", 0)))
    print("\n==== v3")
    for n, (rig, z, cfg) in built.items():
        print(f"  {n:8s} head {rig['radii']['head']:.3f} m, ankle_y {cfg['force_lo'][13]:.0f}/{cfg['force_hi'][13]:.0f} N m, "
              f"elbow scale {cfg['action_scale_j'][5]:.3f}, kv hip_y {cfg['kv'][11]:.1f} (v2 {rig_to_mjcf.GAINS['hip'][1] * cfg['strength']:.1f})")


if __name__ == "__main__":
    main()
