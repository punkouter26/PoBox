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
    # --gauntlet: train_gauntlet.py, whose save is latest_NAME.pt; --getup: train_getup.py, which saves
    # latest_A.pt and latest_B.pt. Both are given their length as --more-iters, counted from the run's own
    # save, so what is left of it is worked out again at every restart.
    mode = "gauntlet" if "--gauntlet" in args else "getup" if "--getup" in args else "box"
    for flag in ("--gauntlet", "--getup"):
        if flag in args:
            args.remove(flag)
    run = args[args.index("--run-name") + 1]
    ck = lambda name: os.path.join(HERE, "checkpoints", run, f"latest_{name}.pt")
    if mode == "gauntlet":
        names = [args[args.index("--name") + 1]]
    elif mode == "getup":
        names = [args[args.index("--a") + 1], args[args.index("--b") + 1]]
    else:
        names = []
    latest = [ck(n) for n in names] or [os.path.join(HERE, "checkpoints", run, "latest.pt")]
    script = {"gauntlet": "train_gauntlet.py", "getup": "train_getup.py", "box": "train_box.py"}[mode]
    if mode != "box" and any(os.path.exists(p) for p in latest):
        raise SystemExit(f"[resilient] {run} already has a save; start it under a new name")
    total = int(args[args.index("--more-iters") + 1]) if mode != "box" and "--more-iters" in args else 0
    for attempt in range(1, 9):
        code = subprocess.call([sys.executable, os.path.join(HERE, script)] + args, cwd=HERE)
        if code == 0:
            return
        print(f"[resilient] attempt {attempt} of {run} ended with exit code {code} at {time.strftime('%H:%M:%S')}", flush=True)
        if not any(os.path.exists(p) for p in latest):
            # Faulted before its first save (m1_nick, 2026-10-03: CUDA 719 a minute in): nothing was learned, so
            # it simply starts again as it started.
            print(f"[resilient] {run} had no save yet; starting it again from the beginning", flush=True)
            time.sleep(30)
            continue
        # From its own last save (a boxer that is only company keeps the file it came with), and without what
        # only a first start does.
        i = args.index("--resume")
        j = next((k for k in range(i + 1, len(args)) if args[k].startswith("--")), len(args))
        args[i + 1:j] = [p if os.path.exists(p) else args[i + 1 + n] for n, p in enumerate(latest)]
        if "--reset-std" in args:
            k = args.index("--reset-std")
            del args[k:k + 2]
        if total:
            import torch
            done = max(int(torch.load(p, map_location="cpu", weights_only=False).get("extra", {}).get("iter", 0)) for p in latest if os.path.exists(p))
            args[args.index("--more-iters") + 1] = str(max(1, total - done))
        time.sleep(30)        # let the driver come back
    raise SystemExit(f"[resilient] {run} faulted eight times; giving up")


if __name__ == "__main__":
    main()
