"""Turn the two rules of a living body (envs/boxing.py: the speed limit, fatigue) on or off for boxers, in
every config under models/v2 they appear in. The exam reads them there, and the game from the copy of a
boxer's own config that promoting it makes. A policy must have been trained under a rule before this turns it on.

    python tools/set_rules.py --only matt --speed-limit 1 --fatigue-j 30000
    python tools/set_rules.py --only matt --hit-push 0.6 --stagger-v 5 --hurt-full 400 --body-drain 0.0025 --obs-state 1

The last five are the punch's rules of 2026-10-16 (envs/boxing.py); 0 turns one off.
"""
import argparse
import glob
import json
import os

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ap = argparse.ArgumentParser()
ap.add_argument("--only", nargs="*", default=[], help="boxers; none = all")
ap.add_argument("--speed-limit", type=int, choices=[0, 1])
ap.add_argument("--fatigue-j", type=float, help="0 = no fatigue")
ap.add_argument("--hit-push", type=float)
ap.add_argument("--stagger-v", type=float)
ap.add_argument("--hurt-full", type=float)
ap.add_argument("--body-drain", type=float)
ap.add_argument("--obs-state", type=int, choices=[0, 1])
ap.add_argument("--models", default=os.path.join("models", "v2"), help="the folder of configs to change (models/v3 for the v3 bodies)")
args = ap.parse_args()
for path in glob.glob(os.path.join(HERE, args.models, "*_policy_config.json")):
    with open(path, "r", encoding="utf-8") as f:
        cfg = json.load(f)
    mine = [e for e in cfg.get("fighters", [cfg]) if not args.only or e.get("name") in args.only]
    for e in mine:
        if args.speed_limit is not None:
            e["speed_limit"] = bool(args.speed_limit)
        if args.fatigue_j is not None:
            e["fatigue_j"] = args.fatigue_j
        for key in ("hit_push", "stagger_v", "hurt_full", "body_drain"):
            if getattr(args, key) is not None:
                e[key] = getattr(args, key)
        if args.obs_state is not None:
            e["obs_state"] = bool(args.obs_state)
    if mine:
        with open(path, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2)
        print(f"{os.path.basename(path)}: {', '.join(e.get('name', '?') for e in mine)}")
