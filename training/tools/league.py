"""The long run for a roster of new boxers: each learns on the bag, then they box each other in turn.

    python tools/league.py --hours 8
    python tools/league.py --hours 8 --wait-pid 28380      start when that process has gone (a run already on the GPU)
    python tools/league.py --hours 8 --resume              carry on a league that stopped (logs/league.state.json)

Meant to be started and left; nothing here needs the terminal that started it. Every stage is its own
train_box.py process, so a crash in one keeps the others' results.

    bag_<name>               each new boxer alone with the heavy bag, starting from a brain that can already
                             box (house rule: a warm start, not from scratch): stand in this body, hit hard
    r<N>_<a>_v_<b>           five rounds. In each, every new boxer is in the ring once: against another new
                             boxer (both learn), or against a veteran, whose policy is frozen and only spars

A boxer therefore meets every other new boxer and one veteran, and carries one policy through all of it.
What makes the boxers different from each other is not here: it is the style in each one's rig file, which
envs/boxing.py pays by. The last rounds turn on the game's rule that being hit weakens the legs (--daze).

It keeps MuJoCo's viewer open on whichever stage is running, writes a contact sheet every half hour to
logs/sheets/, charts every boxer in TensorBoard as boxer_<name>, and logs what it did in logs/league.log.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import shutil
import socket
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOG = os.path.join(HERE, "logs", "league.log")
STATE = os.path.join(HERE, "logs", "league.state.json")

# Round by round, who is in the ring with whom. "vet" is the veteran named for that boxer below.
ROUNDS = [
    [("trump", "vet"), ("grandma", "lilmatt"), ("grandpa", "nick")],
    [("trump", "lilmatt"), ("nick", "vet"), ("grandma", "grandpa")],
    [("trump", "nick"), ("grandpa", "lilmatt"), ("grandma", "vet")],
    [("trump", "grandpa"), ("grandma", "nick"), ("lilmatt", "vet")],
    [("trump", "grandma"), ("grandpa", "vet"), ("nick", "lilmatt")],
]
VETERAN_FOR = {"trump": "zombie", "nick": "matt", "grandma": "zombie", "lilmatt": "matt", "grandpa": "zombie"}
DAZE_FROM_ROUND = 4
DAZE = ["--daze", "--daze-hi", "42", "--ko-bonus", "6", "--max-std", "0.35", "--entropy-coef", "0",
        "--lr", "3e-4", "--lr-max", "5e-4"]


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


def alive(pid: int) -> bool:
    out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True, text=True).stdout
    return str(pid) in out


def train(xml: str, run: str, minutes: float, extra: list) -> int:
    cmd = [sys.executable, os.path.join(HERE, "train_box.py"), "--xml", xml, "--run-name", run,
           "--max-hours", f"{minutes / 60.0:.4f}", "--keep-old-runs", "--no-tensorboard", "--career"] + extra
    log("start  " + " ".join(os.path.relpath(c, HERE) if os.path.isabs(c) else c for c in cmd[1:]))
    with open(os.path.join(HERE, "logs", f"{run}.log"), "a", encoding="utf-8") as out:
        return subprocess.call(cmd, cwd=HERE, stdout=out, stderr=subprocess.STDOUT)


class Side:
    """What runs beside training: the live viewer and the half-hourly contact sheets."""

    def __init__(self, viewer: bool):
        self.run = None
        self.viewer_on = viewer
        self.viewer = None
        self.viewer_run = None
        self.stop = threading.Event()
        os.makedirs(os.path.join(HERE, "logs", "sheets"), exist_ok=True)
        threading.Thread(target=self._loop, daemon=True).start()

    def _loop(self) -> None:
        last_sheet = time.time()
        while not self.stop.wait(20.0):
            run = self.run
            if not run or not glob.glob(os.path.join(HERE, "checkpoints", run, "model_*.pt")):
                continue
            if self.viewer_on and self.viewer_run != run:
                if self.viewer is not None and self.viewer.poll() is None:
                    self.viewer.terminate()
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


def veteran_checkpoint(name: str) -> str:
    """The newest policy a veteran has that was kept: the defend rung's, or else the match's."""
    for run in ("defend", "match"):
        ck = os.path.join(HERE, "checkpoints", run, f"latest_{name}.pt")
        if os.path.exists(ck) and not os.path.exists(os.path.join(HERE, "checkpoints", run, "REJECTED.txt")):
            return ck
    raise SystemExit(f"no kept policy for the veteran {name}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--hours", type=float, default=8.0)
    ap.add_argument("--bag-min", type=float, default=18.0, help="bag time for each new boxer")
    ap.add_argument("--warm", default=os.path.join(HERE, "checkpoints", "bag_matt", "latest.pt"),
                    help="the brain every new boxer starts its bag stage from")
    ap.add_argument("--wait-pid", type=int, default=0, help="do nothing until this process has exited")
    ap.add_argument("--resume", action="store_true", help="skip the stages logs/league.state.json says are done")
    ap.add_argument("--no-viewer", action="store_true")
    ap.add_argument("--plan", action="store_true", help="print the schedule and stop")
    args = ap.parse_args()
    os.makedirs(os.path.join(HERE, "logs"), exist_ok=True)

    with open(os.path.join(HERE, "roster.json"), "r", encoding="utf-8") as f:
        roster = json.load(f)
    boxers = list(roster["boxers"])
    models = os.path.join(HERE, "models")

    def pair_xml(a: str, b: str) -> tuple:
        """The match model for two boxers, and the order it holds them in."""
        for x, y in ((a, b), (b, a)):
            p = os.path.join(models, f"{x}_vs_{y}_spar.xml")
            if os.path.exists(p):
                return p, x, y
        raise SystemExit(f"no match model for {a} and {b}. Run tools/build_roster.py.")

    sessions = []
    for n, pairs in enumerate(ROUNDS, 1):
        for a, b in pairs:
            b = VETERAN_FOR[a] if b == "vet" else b
            xml, x, y = pair_xml(a, b)
            sessions.append({"round": n, "run": f"r{n}_{x}_v_{y}", "xml": xml, "a": x, "b": y})
    for n in boxers:
        if not os.path.exists(os.path.join(models, f"{n}_bag.xml")):
            raise SystemExit(f"models/{n}_bag.xml does not exist. Run tools/build_roster.py.")
    if args.plan:
        ring = (args.hours * 60.0 - 8.0 - args.bag_min * len(boxers)) / len(sessions)
        print(f"{args.hours:g} h: {args.bag_min:g} min on the bag each for {', '.join(boxers)} (from {os.path.relpath(args.warm, HERE)}), "
              f"then {len(sessions)} sessions of about {ring:.0f} min")
        for s in sessions:
            vet = [n for n in (s["a"], s["b"]) if n not in boxers]
            print(f"  round {s['round']}: {s['run']:28s} {os.path.basename(s['xml'])}"
                  + (f"   frozen: {vet[0]} ({os.path.relpath(veteran_checkpoint(vet[0]), HERE)})" if vet else "")
                  + ("   hurt rule on" if s["round"] >= DAZE_FROM_ROUND else ""))
        for n in boxers:
            print(f"  {n}: {sum(1 for s in sessions if n in (s['a'], s['b']))} ring sessions")
        return

    if args.wait_pid:
        log(f"wait   for process {args.wait_pid} to leave the GPU")
        while alive(args.wait_pid):
            time.sleep(10.0)

    state = {"ck": {}, "done": [], "started": time.time()}
    if args.resume and os.path.exists(STATE):
        with open(STATE, "r", encoding="utf-8") as f:
            state = json.load(f)

    def save_state() -> None:
        with open(STATE, "w", encoding="utf-8") as f:
            json.dump(state, f, indent=2)

    # House rules at the start of training: nothing obsolete left in TensorBoard, and TensorBoard up.
    tb, archive = os.path.join(HERE, "logs", "tb"), os.path.join(HERE, "logs", "tb_archive")
    os.makedirs(archive, exist_ok=True)
    if not args.resume:
        for old in os.listdir(tb) if os.path.isdir(tb) else []:
            # Only what an earlier league or a smoke test left: another run's charts are its owner's to move.
            if old.startswith(("smoke", "boxer_", "bag_")) or old[:1] == "r" and "_v_" in old:
                shutil.rmtree(os.path.join(archive, old), ignore_errors=True)
                shutil.move(os.path.join(tb, old), os.path.join(archive, old))
                log(f"tensorboard: archived {old}")
    with socket.socket() as s:
        s.settimeout(1.0)
        up = s.connect_ex(("127.0.0.1", 6006)) == 0
    if not up:
        subprocess.Popen([sys.executable, "-m", "tensorboard.main", "--logdir", tb, "--port", "6006", "--bind_all"],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
    log(f"tensorboard: {'already up' if up else 'started'} on http://localhost:6006")

    t0 = state["started"]
    budget = args.hours * 60.0 - 8.0      # minutes; start-up, saving and the sheets take a few of them
    left = lambda: budget - (time.time() - t0) / 60.0
    log(f"league {args.hours:g} h: {args.bag_min:g} min on the bag for each of {', '.join(boxers)}, then {len(sessions)} "
        f"ring sessions over {len(ROUNDS)} rounds; the hurt rule from round {DAZE_FROM_ROUND}")
    side = Side(not args.no_viewer)

    for n in boxers:
        run = f"bag_{n}"
        if run in state["done"]:
            continue
        side.run = run
        code = train(os.path.join(models, f"{n}_bag.xml"), run, args.bag_min,
                     ["--num-envs", "4096", "--resume", args.warm, "--reset-std", "0.3"])
        ck = os.path.join(HERE, "checkpoints", run, "latest.pt")
        e = status(run).get("env", {})
        log(f"end    {run}: exit {code}, fall rate {e.get('fall_rate', float('nan')):.2f}, "
            f"{e.get('hits_per_s', float('nan')):.2f} hits/s at {e.get('hit_speed', float('nan')):.1f} m/s")
        if not os.path.exists(ck):
            log(f"stop   {run} produced no checkpoint; see logs/{run}.log. {n} starts the ring from the warm brain")
            ck = args.warm
        elif e.get("fall_rate", 1.0) > 0.5:
            log(f"again  {n} still falls in {e.get('fall_rate', 1.0):.0%} of episodes on the bag; 8 minutes more")
            train(os.path.join(models, f"{n}_bag.xml"), run, 8.0, ["--num-envs", "4096", "--resume", ck])
        state["ck"][n] = ck
        state["done"].append(run)
        save_state()

    first_ring = set(boxers)
    for i, s in enumerate(sessions):
        if s["run"] in state["done"]:
            first_ring -= {s["a"], s["b"]}
            continue
        todo = len([x for x in sessions[i:] if x["run"] not in state["done"]])
        minutes = left() / todo
        if minutes < 3.0:
            log(f"skip   {s['run']}: {left():.0f} minutes left for {todo} sessions")
            continue
        vets = [n for n in (s["a"], s["b"]) if n not in boxers]
        cks = [veteran_checkpoint(n) if n in vets else state["ck"][n] for n in (s["a"], s["b"])]
        extra = ["--num-envs", "2048", "--survivor-bootstrap", "--resume"] + cks
        if vets:
            extra += ["--freeze"] + vets
        if {s["a"], s["b"]} & first_ring:
            # A brain that has converged on the bag explores too little to learn anything against an opponent.
            extra += ["--reset-std", "0.35"]
        if s["round"] >= DAZE_FROM_ROUND:
            extra += DAZE
        side.run = s["run"]
        code = train(s["xml"], s["run"], minutes, extra)
        e = status(s["run"]).get("env", {})
        for n in (s["a"], s["b"]):
            if n in vets:
                continue
            ck = os.path.join(HERE, "checkpoints", s["run"], f"latest_{n}.pt")
            if os.path.exists(ck):
                state["ck"][n] = ck
            else:
                log(f"keep   {n}: {s['run']} left no checkpoint for it; it goes on from {os.path.relpath(state['ck'][n], HERE)}")
        log(f"end    {s['run']}: exit {code}, fall rate {e.get('fall_rate', float('nan')):.2f} | " + " | ".join(
            f"{n}: {e.get(n + '_hits_per_s', float('nan')):.2f} hits/s at {e.get(n + '_hit_speed', float('nan')):.1f} m/s, "
            f"head {e.get(n + '_head_share', float('nan')):.0%}, down in {e.get(n + '_falls', float('nan')):.0%}, "
            f"{e.get(n + '_blocks_per_s', float('nan')):.2f} blocks/s" for n in (s["a"], s["b"])))
        first_ring -= {s["a"], s["b"]}
        state["done"].append(s["run"])
        save_state()

    side.stop.set()     # the last viewer is left open: the owner asked to be able to watch after training too
    log(f"done   {(time.time() - t0) / 3600.0:.2f} h. Final policies: " + ", ".join(
        f"{n} {os.path.relpath(state['ck'].get(n, ''), HERE)}" for n in boxers))
    with open(os.path.join(HERE, "logs", "league.done"), "w", encoding="utf-8") as f:
        f.write(time.strftime("%Y-%m-%d %H:%M:%S"))


if __name__ == "__main__":
    main()
