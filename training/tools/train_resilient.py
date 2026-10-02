"""train_box.py, started again from its last save whenever the GPU faults under it.

    python tools/train_resilient.py --run-name r1_matt --resume checkpoints/r0_matt/latest.pt --iters 2400 \
        --stage footwork --xml models/v2/matt_solo.xml ... (anything else train_box.py takes)

This laptop's GPU driver kills a run now and then ("CUDA error: unspecified launch failure", nvlddmkm event
13 in the system log; four times on 2 October 2026, at 85 to 89 C). A run saves every 50 iterations, so a
fault costs a minute or two if somebody starts it again; this does. Give the length with --iters, which is
counted from the start of the stage and so means the same after a restart (--max-hours would start again).
--reset-std is applied on the first start only.

ponytail: one learner (a solo or self-play model, whose save is latest.pt). A match of two different boxers
saves one file each; teach this their names when a match is run through it.
"""
from __future__ import annotations

import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main() -> None:
    args = sys.argv[1:]
    # --gauntlet: train_gauntlet.py, whose save is latest_NAME.pt and whose length is --more-iters, counted
    # from the run's own save; what is left of it is worked out again at every restart.
    gauntlet = "--gauntlet" in args
    if gauntlet:
        args.remove("--gauntlet")
    run = args[args.index("--run-name") + 1]
    latest = os.path.join(HERE, "checkpoints", run, f"latest_{args[args.index('--name') + 1]}.pt" if gauntlet else "latest.pt")
    script = "train_gauntlet.py" if gauntlet else "train_box.py"
    if gauntlet and os.path.exists(latest):
        raise SystemExit(f"[resilient] {run} already has a save; start a gauntlet under a new name")
    total = int(args[args.index("--more-iters") + 1]) if gauntlet and "--more-iters" in args else 0
    for attempt in range(1, 9):
        code = subprocess.call([sys.executable, os.path.join(HERE, script)] + args, cwd=HERE)
        if code == 0:
            return
        print(f"[resilient] attempt {attempt} of {run} ended with exit code {code} at {time.strftime('%H:%M:%S')}", flush=True)
        if not os.path.exists(latest):
            raise SystemExit(f"[resilient] {run} has no save to start again from")
        # From its own last save, and without what only a first start does.
        i = args.index("--resume")
        j = next((k for k in range(i + 1, len(args)) if args[k].startswith("--")), len(args))
        args[i + 1:j] = [latest]
        if "--reset-std" in args:
            k = args.index("--reset-std")
            del args[k:k + 2]
        if total:
            import torch
            done = int(torch.load(latest, map_location="cpu", weights_only=False).get("extra", {}).get("iter", 0))
            args[args.index("--more-iters") + 1] = str(max(1, total - done))
        time.sleep(30)        # let the driver come back
    raise SystemExit(f"[resilient] {run} faulted eight times; giving up")


if __name__ == "__main__":
    main()
