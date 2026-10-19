"""One m14 round (2026-10-19 day): NAME from the brain the game ships against the game's roster (logs/book_night_mix.json,
the brains in Assets/Boxers since e218acc), under the game's daze line (Fighter: hi 26, lo 9, tau 3.5, body 0.45).

    python tools/m14_job.py grandpa [--run-name mel1_grandpa --bodies 2 --obs-bodies ...]   the rest goes to train_gauntlet.py
    python tools/m14_job.py grandpa --from newest      start from m14_NAME if it was exported (it passed its exam), else the game's

m13's recipe (guard and footing kept: block 0.5, fall penalty, arms to the guard, low first lr) with landing paid more
(hit 3.0, seek 0.5): m13's attack failed for 7 of 8 because everyone guards now. RECIPE adds each boxer's own.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BOOK = os.path.join(HERE, "logs", "book_night_mix.json")
BANK = {"grandma": "logs/handover_d9_grandma.npz", "grandpa": "logs/handover_d9_grandpa.npz"}
GAME_DAZE = ["--daze", "--daze-hi", "26", "--daze-lo", "9", "--daze-tau", "3.5", "--daze-body", "0.45"]
COMMON = GAME_DAZE + [
    "--obs-state", "--foe-state", "--block-w", "0.5", "--sep-max", "3.5", "--num-envs", "3072", "--more-iters", "700",
    "--max-hours", "0.67", "--max-std", "0.3", "--lr", "3e-5", "--hit-push", "0.6", "--stagger-v", "5", "--hurt-full", "400",
    "--body-drain", "0.0025", "--selfcontact-w", "0.02", "--repeat-decay", "0.7", "--combo-w", "0.1", "--hurt-start", "0.2",
    "--clinch-w", "0.1", "--rope-w", "0.05", "--cutoff-w", "0.05", "--taken-w", "1.0", "--hit-w", "3.0", "--seek-w", "0.5",
    "--handover-share", "0.5", "--fall-penalty", "12", "--slow-v", "4.0", "--head-mult", "2.0", "--arms-w", "0.01",
    "--done-when", "hits_per_s>=1.2,falls<=0.03,repeat_share<=0.6", "--done-window", "40", "--no-onnx"]
# Later flags win (argparse keeps the last). Weights: the opponents each lands least on in exam_nightmix.
RECIPE = {
    # m14_grandpa (this recipe without --style): 125 iterations, landed 0.05 -> 0.06/s. His own style holds him out of reach: range_m
    # 0.9 is paid and crowd 0.5 charges every step inside 0.78 m, while punches land from 0.71-0.76 m (tools/head_diag.py). m14b: the
    # range of the boxers who land (0.70), no crowd charge.
    "grandpa": ["--hit-w", "3.5", "--slow-v", "3.0", "--seek-w", "0.6", "--reset-std", "0.3", "--style", "range_m=0.7", "crowd=0",
                "--weights", "matt=2", "zombie=2", "grandma=2", "kim=2"],
    "kim": ["--fall-penalty", "20", "--slow-v", "4.4", "--weights", "grandpa=2", "zombie=2", "matt=2", "sokol=2"],
    "grandma": ["--fall-penalty", "16", "--slow-v", "3.0", "--style", "range_m=0.72", "--weights", "grandpa=2", "trump=2", "zombie=2"],
    # Head shots (tools/head_diag.py, logs/head_diag_*.out): same range as Nick, gloves 21-28 cm under the head; lead them up.
    "sokol": ["--fall-penalty", "16", "--head-aim-w", "0.25", "--body-mult", "0.5", "--weights", "zombie=2", "trump=2", "grandma=2", "grandpa=2"],
    "matt": ["--block-w", "1.0", "--handover-share", "0.7", "--head-aim-w", "0.25", "--body-mult", "0.6",
             "--weights", "grandpa=2", "zombie=2", "sokol=2"],
    "zombie": ["--weights", "grandma=2", "grandpa=2", "kim=2", "nick=2"],
    "nick": ["--fall-penalty", "16", "--weights", "sokol=2", "grandpa=2"],
    "trump": ["--weights", "grandma=2", "matt=2", "sokol=2"],
}


def main() -> None:
    name, rest = sys.argv[1], sys.argv[2:]
    run = rest[rest.index("--run-name") + 1] if "--run-name" in rest else f"m14_{name}"
    book = json.load(open(BOOK, encoding="utf-8"))
    start = book[name]["match"]
    if "--from" in rest:
        i = rest.index("--from")
        if rest[i + 1] == "newest":
            # The newest m14 that passed its exam (tools/day_exam.py exported its ONNX), else the game's.
            for run in (f"m14b_{name}", f"m14_{name}"):
                if os.path.exists(os.path.join(HERE, "checkpoints", run, f"latest_{name}.onnx")):
                    start = f"checkpoints/{run}/latest_{name}.pt"
                    break
        elif rest[i + 1].endswith(".pt"):
            start = rest[i + 1]          # a run carried on from its own save (m14_kim, stopped with the session at 11:45)
        del rest[i:i + 2]
    against = [f"{n}={e['match']}" for n, e in book.items() if n != name]
    cmd = [sys.executable, os.path.join(HERE, "tools", "train_resilient.py"), "--gauntlet", "--run-name", run, "--stage", "match",
           "--models", "models/v2", "--name", name, "--resume", start, "--against", *against,
           "--handover", BANK.get(name, f"logs/handover_v2_{name}.npz"), *COMMON, *RECIPE[name],
           *[a for a in rest if a not in ("--run-name", run)]]
    print(" ".join(cmd[1:]), flush=True)
    sys.exit(subprocess.call(cmd, cwd=HERE))


if __name__ == "__main__":
    main()
