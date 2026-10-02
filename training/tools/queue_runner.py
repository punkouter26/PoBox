"""Runs training jobs one after another on the GPU, from a list that can be edited while it runs.

    python tools/queue_runner.py --after logs/league.done      wait for that file, then work through logs/queue.json
    python tools/queue_runner.py                               start at once

Meant to be started detached and left. logs/queue.json is a list of jobs, read again before every job:

    [{"run": "getup_matt_zombie", "cmd": ["train_getup.py", "--a", "matt", ...], "viewer": true,
      "before": ["cgetup_matt"]}]

  run      the job's name: its log is logs/<run>.log, and logs/queue.state.json records it as done
  cmd      what to run with this Python, from the training folder
  viewer   open MuJoCo's viewer on the run once it has a checkpoint (at idle priority, on two logical cores)
  before   CPU trainers to stop first (their stop file is written and they are waited for)

A job already recorded as done is skipped, so the list can simply be added to. When nothing is left the
runner waits for more; logs/queue.stop ends it. Only one GPU job at a time; it never starts while
logs/queue.hold exists (the other session's Play-mode checks, say).

House rules kept here: TensorBoard is started if it is not up; the viewer is opened on every run.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import socket
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOGS = os.path.join(HERE, "logs")
QUEUE, STATE, LOG = os.path.join(LOGS, "queue.json"), os.path.join(LOGS, "queue.state.json"), os.path.join(LOGS, "queue.log")


def log(msg: str) -> None:
    line = f"{time.strftime('%Y-%m-%d %H:%M:%S')}  {msg}"
    print(line, flush=True)
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def read(path: str, default):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return default


def leash(pid: int) -> None:
    """Idle priority and two logical cores: a viewer left to itself takes most of the machine."""
    try:
        subprocess.run(["powershell", "-NoProfile", "-Command",
                        f"$p = Get-Process -Id {pid}; $p.PriorityClass = 'Idle'; $p.ProcessorAffinity = 0x300;"
                        f"Get-CimInstance Win32_Process -Filter 'ParentProcessId={pid}' | ForEach-Object {{ $c = Get-Process -Id $_.ProcessId; "
                        f"$c.PriorityClass = 'Idle'; $c.ProcessorAffinity = 0x300 }}"], capture_output=True, timeout=30)
    except Exception as e:
        log(f"viewer leash failed: {e}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--after", default="", help="do nothing until this file exists")
    args = ap.parse_args()
    os.makedirs(LOGS, exist_ok=True)
    if args.after:
        path = args.after if os.path.isabs(args.after) else os.path.join(HERE, args.after)
        log(f"waiting for {os.path.relpath(path, HERE)}")
        while not os.path.exists(path):
            if os.path.exists(os.path.join(LOGS, "queue.stop")):
                log("stopped before it began")
                return
            time.sleep(15.0)
        log("it is there")
    with socket.socket() as s:
        s.settimeout(1.0)
        up = s.connect_ex(("127.0.0.1", 6006)) == 0
    if not up:
        subprocess.Popen([sys.executable, "-m", "tensorboard.main", "--logdir", os.path.join(LOGS, "tb"), "--port", "6006", "--bind_all"],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
    log(f"tensorboard: {'already up' if up else 'started'} on http://localhost:6006")

    viewer = None
    idle_said = False
    while not os.path.exists(os.path.join(LOGS, "queue.stop")):
        state = read(STATE, {"done": []})
        todo = [j for j in read(QUEUE, []) if j.get("run") and j["run"] not in state["done"]]
        if not todo or os.path.exists(os.path.join(LOGS, "queue.hold")):
            if not idle_said:
                log("held (logs/queue.hold)" if todo else "nothing queued; waiting")
                idle_said = True
            time.sleep(15.0)
            continue
        idle_said = False
        job = todo[0]
        run = job["run"]
        for cpu in job.get("before", []):
            if glob.glob(os.path.join(LOGS, f"{cpu}.status.json")):
                open(os.path.join(LOGS, f"{cpu}.stop"), "w").close()
                t0 = time.time()
                while os.path.exists(os.path.join(LOGS, f"{cpu}.stop")) and time.time() - t0 < 240.0:
                    time.sleep(3.0)
                log(f"stopped the CPU trainer {cpu}" if not os.path.exists(os.path.join(LOGS, f"{cpu}.stop")) else f"{cpu} did not answer its stop file")
        cmd = [sys.executable] + [str(c) for c in job["cmd"]]
        log(f"start  {run}: {' '.join(str(c) for c in job['cmd'])}")
        with open(os.path.join(LOGS, f"{run}.log"), "a", encoding="utf-8") as out:
            p = subprocess.Popen(cmd, cwd=HERE, stdout=out, stderr=subprocess.STDOUT)
            with open(os.path.join(LOGS, f"{run}.pid"), "w") as f:
                f.write(str(p.pid))
            shown = False
            while p.poll() is None:
                time.sleep(10.0)
                if job.get("viewer", True) and not shown and glob.glob(os.path.join(HERE, "checkpoints", run, "model_*.pt")):
                    shown = True
                    if viewer is not None and viewer.poll() is None:
                        viewer.terminate()
                    try:
                        viewer = subprocess.Popen([sys.executable, os.path.join(HERE, "view_box.py"), "--run", run], cwd=HERE,
                                                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                        time.sleep(20.0)
                        leash(viewer.pid)
                        log(f"viewer on {run}")
                    except Exception as e:
                        log(f"viewer failed: {e}")
        tail = ""
        try:
            with open(os.path.join(LOGS, f"{run}.log"), "r", encoding="utf-8", errors="replace") as f:
                lines = [ln.strip() for ln in f.readlines() if ln.strip()]
            tail = lines[-1][:260] if lines else ""
        except Exception:
            pass
        log(f"end    {run}: exit {p.returncode} | {tail}")
        state = read(STATE, {"done": []})
        state["done"].append(run)
        state.setdefault("exit", {})[run] = p.returncode
        with open(STATE, "w", encoding="utf-8") as f:
            json.dump(state, f, indent=1)
        with open(os.path.join(LOGS, f"{run}.done"), "w") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S"))
    log("stopped (logs/queue.stop)")
    os.remove(os.path.join(LOGS, "queue.stop"))


if __name__ == "__main__":
    main()
