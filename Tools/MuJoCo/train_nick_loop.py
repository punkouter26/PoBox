"""Relaunch train_nick.py after a crash, resuming from its newest checkpoint.

    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/train_nick_loop.py --run-name nick03 --until-iteration 2100 --resume results/nick/nick02/model_600.pt

nick02 died at iteration 699 of "CUDA error: unspecified launch failure" out
of wp.synchronize() -- Warp on driver 610.78 is fragile (it prints CUDA error
36 at every init) and the failure can land at any iteration. Every argument
here is passed straight through to train_nick.py; --resume applies to the
first attempt only, later attempts add --resume-latest so the run continues
from the newest model_*.pt in its own directory. Stops on TRAINING_DONE or
after --attempts failures.
"""
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).parent
argv = sys.argv[1:]
attempts = 5
if "--attempts" in argv:
    i = argv.index("--attempts")
    attempts = int(argv[i + 1])
    del argv[i:i + 2]

for attempt in range(1, attempts + 1):
    cmd = [sys.executable, str(HERE / "train_nick.py")] + argv
    if attempt > 1:
        cmd = [a for a in cmd if a not in ("--resume",) and not a.endswith(".pt")] + ["--resume-latest"]
    print("TRAIN_LOOP attempt %d/%d: %s" % (attempt, attempts, " ".join(cmd)), flush=True)
    code = subprocess.call(cmd)
    print("TRAIN_LOOP attempt %d exited %d" % (attempt, code), flush=True)
    if code == 0:
        break
