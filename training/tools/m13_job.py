"""One m13 match round (2026-10-19 night): NAME from the brain the game ships (checkpoints/onnx_1018, rebuilt from
Assets/Boxers/NAME/match.onnx) against the game's roster, each opponent its m13 if that has landed tonight, else its
shipped brain. Worked out when the job starts, so later runs meet the earlier ones' m13.

    python tools/m13_job.py kim [--bodies 2 --run-name mel1_kim ...]    anything after the name goes to train_gauntlet.py

The common recipe is m12_zombie's (guard 1% -> 57%), with tonight's terms: repeat decay 0.7 (log, 2026-10-18), the head
priced 2x (--head-mult; Matt lands 0% to the head), the arms pulled to the guard (--arms-w 0.01), the opponents reading
their state (--foe-state), and a low first learning rate (KL fix for ONNX-rebuilt brains). RECIPE adds each boxer's own.
"""
from __future__ import annotations

import os
import subprocess
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NAMES = ("matt", "zombie", "nick", "sokol", "kim", "trump", "grandma", "grandpa")
BANK = {"grandma": "logs/handover_d9_grandma.npz", "grandpa": "logs/handover_d9_grandpa.npz"}
COMMON = ["--daze", "--obs-state", "--foe-state", "--block-w", "0.5", "--sep-max", "3.5", "--num-envs", "3072", "--more-iters", "700",
          "--max-hours", "0.5", "--max-std", "0.3", "--lr", "3e-5", "--hit-push", "0.6", "--stagger-v", "5", "--hurt-full", "400",
          "--body-drain", "0.0025", "--selfcontact-w", "0.02", "--repeat-decay", "0.7", "--combo-w", "0.1", "--hurt-start", "0.2",
          "--clinch-w", "0.1", "--rope-w", "0.05", "--cutoff-w", "0.05", "--taken-w", "1.0", "--hit-w", "2.0", "--seek-w", "0.3",
          "--handover-share", "0.5", "--fall-penalty", "12", "--slow-v", "4.0", "--head-mult", "2.0", "--arms-w", "0.01",
          "--done-when", "hits_per_s>=1.2,falls<=0.03,repeat_share<=0.6", "--done-window", "40", "--no-onnx"]
# Later flags win (argparse keeps the last).
RECIPE = {
    "matt": ["--block-w", "1.0", "--head-mult", "2.5", "--handover-share", "0.7", "--weights", "grandma=2", "grandpa=2"],
    "zombie": ["--weights", "grandma=2", "grandpa=2", "kim=2", "nick=2"],
    "nick": ["--fall-penalty", "16", "--weights", "sokol=3"],
    "sokol": ["--fall-penalty", "16", "--weights", "kim=2", "trump=2"],
    "kim": ["--fall-penalty", "20", "--slow-v", "4.4", "--weights", "nick=3", "sokol=3"],
    "trump": ["--weights", "grandma=2", "matt=2"],
    "grandma": ["--fall-penalty", "16", "--weights", "trump=3"],
    "grandpa": ["--hit-w", "2.5", "--slow-v", "3.0", "--reset-std", "0.3", "--weights", "trump=3"],
}


def main() -> None:
    name, rest = sys.argv[1], sys.argv[2:]
    run = rest[rest.index("--run-name") + 1] if "--run-name" in rest else f"m13_{name}"
    start = None
    if "--from" in rest:
        # Another warm start: "newest" = tonight's m13 if it was exported (it passed its exam), else the shipped brain.
        i = rest.index("--from")
        start = rest[i + 1]
        del rest[i:i + 2]
        if start == "newest":
            m13 = f"checkpoints/m13_{name}/latest_{name}"
            start = m13 + ".pt" if os.path.exists(os.path.join(HERE, m13 + ".onnx")) else None
    shipped = lambda n: f"checkpoints/onnx_1018/match_{n}.pt"
    # m13b_NAME: an m13 carried on after a fault (Matt's, 2026-10-19 01:40) is the newer one.
    tonight = lambda n: next((p for p in (f"checkpoints/m13b_{n}/latest_{n}.pt", f"checkpoints/m13_{n}/latest_{n}.pt")
                              if os.path.exists(os.path.join(HERE, p))), f"checkpoints/m13_{n}/latest_{n}.pt")
    against = [f"{n}={tonight(n) if os.path.exists(os.path.join(HERE, tonight(n))) else shipped(n)}" for n in NAMES if n != name]
    cmd = [sys.executable, os.path.join(HERE, "tools", "train_resilient.py"), "--gauntlet", "--run-name", run, "--stage", "match",
           "--models", "models/v2", "--name", name, "--resume", start or shipped(name), "--against", *against,
           "--handover", BANK.get(name, f"logs/handover_v2_{name}.npz"), *COMMON, *RECIPE[name], *[a for a in rest if a not in ("--run-name", run)]]
    print(" ".join(cmd[1:]), flush=True)
    sys.exit(subprocess.call(cmd, cwd=HERE))


if __name__ == "__main__":
    main()
