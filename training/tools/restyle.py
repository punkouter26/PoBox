"""Write the styles in roster.json into the files training reads, without building any body again.

    python tools/restyle.py                 every boxer in the roster
    python tools/restyle.py --only grandpa

A boxer's style lives in three places: roster.json (where it is decided), its rig file, and the "style" of
its entry in every models/*_policy_config.json it appears in (its own, and each pair's), which is what
envs/boxing.py reads when a session starts. tools/build_roster.py writes all three but also rebuilds the
bodies; this changes only how a boxer is paid, so it is safe between two sessions of a running league.
"""
from __future__ import annotations

import argparse
import glob
import json
import os

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

ap = argparse.ArgumentParser()
ap.add_argument("--only", action="append", default=[])
args = ap.parse_args()

with open(os.path.join(HERE, "roster.json"), "r", encoding="utf-8") as f:
    roster = json.load(f)["boxers"]

for name, spec in roster.items():
    if args.only and name not in args.only:
        continue
    style = spec.get("style", {})
    rig_path = os.path.join(HERE, "rigs", f"{name}.json")
    with open(rig_path, "r", encoding="utf-8") as f:
        rig = json.load(f)
    rig["style"] = style
    with open(rig_path, "w", encoding="utf-8") as f:
        json.dump(rig, f, indent=2)
    touched = 0
    for path in glob.glob(os.path.join(HERE, "models", "*_policy_config.json")):
        with open(path, "r", encoding="utf-8") as f:
            cfg = json.load(f)
        entries = cfg["fighters"] if "fighters" in cfg else [cfg]
        mine = [e for e in entries if e.get("name") == name]
        if not mine:
            continue
        for e in mine:
            e["style"] = style
        with open(path, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2)
        touched += 1
    print(f"{name}: style {style} written to its rig and {touched} config files")
