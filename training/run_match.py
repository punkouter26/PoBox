"""The long run for two entrants: each learns on the bag, then they learn against each other.

    python run_match.py --hours 8 --a matt --b zombie

Meant to be started and left. Three stages, each its own process so a crash in one keeps the others' results:

    bag_<a>      fighter A alone with the heavy bag: stand unaided, hold range, hit hard
    bag_<b>      the same for fighter B
    match        the two in one ring, each with its own policy, each learning from the other

It keeps MuJoCo's viewer open on whichever stage is running (following the newest checkpoint), prints a
contact sheet of the current policy every half hour into logs/sheets/, and logs what it did and when in
logs/match.log. A fighter that still cannot stand at the end of its bag stage gets a second, shorter one
before the match; that decision is logged. Nothing here needs the terminal that started it to stay open.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(HERE, "logs", "match.log")


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
    log("start  " + " ".join(os.path.relpath(c, HERE) if os.path.isabs(c) else c for c in cmd[1:]))
    with open(os.path.join(HERE, "logs", f"{run}.log"), "a", encoding="utf-8") as out:
        code = subprocess.call(cmd, cwd=HERE, stdout=out, stderr=subprocess.STDOUT)
    s = status(run).get("env", {})
    log(f"end    {run}: exit {code}, fall rate {s.get('fall_rate', float('nan')):.2f}, "
        f"hits/s {s.get('hits_per_s', float('nan')):.2f} at {s.get('hit_speed', float('nan')):.1f} m/s")
    return code


class Side:
    """The things that run beside training: the live viewer and the half-hourly contact sheets."""

    def __init__(self, viewer: bool):
        self.run = None
        self.viewer_on = viewer
        self.viewer = None
        self.viewer_run = None
        self.stop = threading.Event()
        os.makedirs(os.path.join(HERE, "logs", "sheets"), exist_ok=True)
        threading.Thread(target=self._loop, daemon=True).start()

    def _has_checkpoint(self, run: str) -> bool:
        return bool(glob.glob(os.path.join(HERE, "checkpoints", run, "model_*.pt")))

    def _loop(self) -> None:
        last_sheet = time.time()
        while not self.stop.wait(20.0):
            run = self.run
            if not run or not self._has_checkpoint(run):
                continue
            if self.viewer_on and (self.viewer_run != run or (self.viewer is not None and self.viewer.poll() is not None and self.viewer_run != run)):
                self._close_viewer()
                try:
                    self.viewer = subprocess.Popen([sys.executable, os.path.join(HERE, "view_box.py"), "--run", run], cwd=HERE,
                                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    self.viewer_run = run
                    log(f"viewer on {run}")
                except Exception as e:
                    log(f"viewer failed: {e}")
            if time.time() - last_sheet >= 1800.0:
                last_sheet = time.time()
                out = os.path.join(HERE, "logs", "sheets", f"{run}_{time.strftime('%H%M')}.png")
                try:
                    subprocess.call([sys.executable, os.path.join(HERE, "view_box.py"), "--run", run, "--sheet", out, "--seconds", "8"],
                                    cwd=HERE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=600)
                    log(f"sheet  {os.path.relpath(out, HERE)}")
                except Exception as e:
                    log(f"sheet  failed: {e}")

    def _close_viewer(self) -> None:
        if self.viewer is not None and self.viewer.poll() is None:
            self.viewer.terminate()
        self.viewer = None

    def close(self) -> None:
        self.stop.set()
        # The last viewer is left open on purpose: the owner asked to be able to watch after training too.


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--hours", type=float, default=8.0)
    ap.add_argument("--a", default="matt")
    ap.add_argument("--b", default="zombie")
    ap.add_argument("--models", default=os.path.join(HERE, "models"))
    ap.add_argument("--bag-hours", type=float, default=0.75, help="bag time for each fighter")
    ap.add_argument("--num-envs", type=int, default=4096)
    ap.add_argument("--match-envs", type=int, default=2048, help="worlds for the match; each holds both fighters")
    ap.add_argument("--no-viewer", action="store_true")
    ap.add_argument("--tag", default="")
    args = ap.parse_args()

    os.makedirs(os.path.join(HERE, "logs"), exist_ok=True)
    xml = {n: os.path.join(args.models, f"{n}_bag.xml") for n in (args.a, args.b)}
    match_xml = os.path.join(args.models, f"{args.a}_vs_{args.b}_spar.xml")
    for p in list(xml.values()) + [match_xml]:
        if not os.path.exists(p):
            raise SystemExit(f"{p} does not exist. Run rig_to_mjcf.py first.")

    t0 = time.time()
    budget = args.hours - 0.15   # start-up, saving and the sheets take a few minutes of every hour
    log(f"night  {args.hours:g} h: {args.bag_hours:g} h on the bag for {args.a}, the same for {args.b}, then the match")
    side = Side(not args.no_viewer)
    bag_ck = {}
    try:
        first = True
        for n in (args.a, args.b):
            run = f"bag_{n}{args.tag}"
            side.run = run
            flags = ["--num-envs", str(args.num_envs)] + ([] if first else ["--keep-old-runs", "--no-tensorboard"])
            first = False
            code = train(xml[n], run, args.bag_hours, flags)
            ck = os.path.join(HERE, "checkpoints", run, "latest.pt")
            if code != 0 or not os.path.exists(ck):
                log(f"stop   {run} did not produce a checkpoint; see logs/{run}.log")
                return
            fall = status(run).get("env", {}).get("fall_rate", 1.0)
            if fall > 0.5:
                more = min(0.5, args.bag_hours)
                log(f"again  {n} still falls in {fall:.0%} of episodes on the bag; {more:g} h more before the match")
                train(xml[n], run, more, ["--num-envs", str(args.num_envs), "--resume", ck, "--keep-old-runs", "--no-tensorboard"])
            bag_ck[n] = ck

        left = budget - (time.time() - t0) / 3600.0
        run = f"match{args.tag}"
        side.run = run
        train(match_xml, run, left, ["--num-envs", str(args.match_envs), "--resume", bag_ck[args.a], bag_ck[args.b],
                                     "--reset-std", "0.35", "--keep-old-runs", "--no-tensorboard"])
    finally:
        side.close()
        log(f"done   {(time.time() - t0) / 3600.0:.2f} h")


if __name__ == "__main__":
    main()
