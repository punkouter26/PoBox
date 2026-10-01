"""The long run: stage 1 on the bag, then stage 2 sparring, inside one wall-clock budget.

    python run_night.py --hours 8 --name fighter

Meant to be started and left. It runs the two stages one after the other as separate processes (so a crash
in one does not take the other's results with it), prints a contact sheet of the current policy every half
hour into logs/sheets/, and keeps a plain-text log of what it did and when in logs/night.log. Nothing here
needs the terminal that started it to stay open.

The split is 30% of the time on the bag and the rest sparring. If the bag stage ends with the fighter still
falling over more often than not, sparring is not started: two fighters who cannot stand teach each other
nothing, and the time is better spent on the bag. That decision is logged.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(HERE, "logs", "night.log")


def log(msg: str) -> None:
    line = f"{time.strftime('%Y-%m-%d %H:%M:%S')}  {msg}"
    print(line, flush=True)
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def status(run: str) -> dict:
    try:
        with open(os.path.join(HERE, "logs", f"{run}.status.json"), "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def train(xml: str, run: str, hours: float, extra: list) -> int:
    cmd = [sys.executable, os.path.join(HERE, "train_box.py"), "--xml", xml, "--run-name", run,
           "--max-hours", f"{hours:.3f}"] + extra
    log("start  " + " ".join(cmd[1:]))
    with open(os.path.join(HERE, "logs", f"{run}.log"), "a", encoding="utf-8") as out:
        code = subprocess.call(cmd, cwd=HERE, stdout=out, stderr=subprocess.STDOUT)
    s = status(run).get("env", {})
    log(f"end    {run}: exit {code}, fall rate {s.get('fall_rate', float('nan')):.2f}, "
        f"hits/s {s.get('hits_per_s', float('nan')):.2f} at {s.get('hit_speed', float('nan')):.1f} m/s")
    return code


def sheets(stop: threading.Event, current: dict, every_s: float) -> None:
    os.makedirs(os.path.join(HERE, "logs", "sheets"), exist_ok=True)
    while not stop.wait(every_s):
        run = current.get("run")
        if not run or not os.path.isdir(os.path.join(HERE, "checkpoints", run)):
            continue
        out = os.path.join(HERE, "logs", "sheets", f"{run}_{time.strftime('%H%M')}.png")
        try:
            subprocess.call([sys.executable, os.path.join(HERE, "view_box.py"), "--run", run, "--sheet", out, "--seconds", "8"],
                            cwd=HERE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=600)
            log(f"sheet  {os.path.relpath(out, HERE)}")
        except Exception as e:
            log(f"sheet  failed: {e}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--hours", type=float, default=8.0)
    ap.add_argument("--name", default="fighter", help="model name: models/<name>_bag.xml and models/<name>_spar.xml")
    ap.add_argument("--models", default=os.path.join(HERE, "models"))
    ap.add_argument("--bag-share", type=float, default=0.30)
    ap.add_argument("--num-envs", type=int, default=4096)
    ap.add_argument("--spar-envs", type=int, default=2048, help="worlds for sparring; each holds two fighters")
    ap.add_argument("--tag", default="", help="suffix for the run names, to keep two nights apart")
    args = ap.parse_args()

    os.makedirs(os.path.join(HERE, "logs"), exist_ok=True)
    bag_xml = os.path.join(args.models, f"{args.name}_bag.xml")
    spar_xml = os.path.join(args.models, f"{args.name}_spar.xml")
    for p in (bag_xml, spar_xml):
        if not os.path.exists(p):
            raise SystemExit(f"{p} does not exist. Run rig_to_mjcf.py first.")

    bag_run, spar_run = "bag" + args.tag, "spar" + args.tag
    t0 = time.time()
    # A few minutes of every hour go on start-up, saving and the sheets; leave room for them.
    budget = args.hours - 0.12
    bag_hours = budget * args.bag_share
    log(f"night  {args.hours:g} h: {bag_hours:.2f} h on the bag, then sparring. Models: {args.name}")

    current = {"run": bag_run}
    stop = threading.Event()
    threading.Thread(target=sheets, args=(stop, current, 1800.0), daemon=True).start()

    try:
        code = train(bag_xml, bag_run, bag_hours, ["--num-envs", str(args.num_envs)])
        bag_ck = os.path.join(HERE, "checkpoints", bag_run, "latest.pt")
        if code != 0 or not os.path.exists(bag_ck):
            log("stop   the bag stage did not produce a checkpoint; see logs/" + bag_run + ".log")
            return

        left = budget - (time.time() - t0) / 3600.0
        fall = status(bag_run).get("env", {}).get("fall_rate", 1.0)
        if fall > 0.5:
            log(f"stay   fall rate on the bag is still {fall:.2f}: the fighter cannot stand yet, so the rest of "
                f"the night ({left:.2f} h) stays on the bag instead of sparring")
            train(bag_xml, bag_run, left, ["--num-envs", str(args.num_envs), "--resume", bag_ck,
                                           "--keep-old-runs", "--no-tensorboard"])
            return

        current["run"] = spar_run
        train(spar_xml, spar_run, left, ["--num-envs", str(args.spar_envs), "--resume", bag_ck, "--reset-std", "0.35",
                                        "--keep-old-runs", "--no-tensorboard"])
    finally:
        stop.set()
        log(f"done   {(time.time() - t0) / 3600.0:.2f} h")


if __name__ == "__main__":
    main()
