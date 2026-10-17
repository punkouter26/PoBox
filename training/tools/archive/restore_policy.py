"""Put an earlier policy back where a running league will pick a boxer up from.

    python tools/restore_policy.py --name grandma --good checkpoints/r1_grandma_v_lilmatt/latest_grandma.pt \
                                   --over checkpoints/r2_grandma_v_grandpa/latest_grandma.pt

For when a session taught a boxer something worse than it knew (2026-10-02: Grandma and Grandpa, both
charged heavily for a punch taken, settled on not boxing each other at all). The league holds the path of
each boxer's newest checkpoint in memory, so the file at that path is what is replaced: the bad one is kept
beside it as passive_<name>.pt, and the good one is written over it with the bad one's career count, so
the boxer's TensorBoard line carries on instead of doubling back.
"""
from __future__ import annotations

import argparse
import os
import shutil

import torch

ap = argparse.ArgumentParser()
ap.add_argument("--name", required=True)
ap.add_argument("--good", required=True)
ap.add_argument("--over", required=True)
args = ap.parse_args()

bad = torch.load(args.over, map_location="cpu", weights_only=False)
good = torch.load(args.good, map_location="cpu", weights_only=False)
keep = os.path.join(os.path.dirname(args.over), f"passive_{args.name}.pt")
shutil.copyfile(args.over, keep)
good.setdefault("extra", {})["career"] = int(bad.get("extra", {}).get("career", 0))
good["extra"]["restored_from"] = os.path.abspath(args.good)
tmp = args.over + ".tmp"
torch.save(good, tmp)
os.replace(tmp, args.over)
print(f"{args.name}: {args.over} is now {args.good} (iteration {good['extra'].get('iter')}, run {good['extra'].get('run_name')}), "
      f"career count {good['extra']['career']}; the replaced policy is kept as {keep}")
