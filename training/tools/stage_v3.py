"""Writes the two staged queues of 2026-10-16 night (nothing is run; copy entries into logs/queue.json to run them).

    python tools/stage_v3.py            training/queue_flags_staged.json and training/queue_v3_staged.json
    python tools/stage_v3.py --books    only the exam books the v3 queue reads (logs/book_v3_<name>.json); its first job

queue_flags_staged.json, on v2 (the game's bodies), about 2 GPU hours: the benchmark of rings x rollout, Matt's footwork
topped up with the self-contact charge, and rc2_matt: the punch's variety (repeat decay, combinations), hurt starts with the
clinch's pay, the hand-over share up (H8), the self-contact charge and ringcraft on m9_matt, stopping early on --done-when.

queue_v3_staged.json, on models/v3 (tools/build_v3.py), about 2 h a boxer: every boxer's three shipped brains rebuilt from
Assets/Boxers/NAME/*.onnx and rescaled to v3's per-joint action scale; the punch rules into the v3 configs; then a boxer at a
time: footwork topped up (+ exam), get-up topped up against a frozen partner, its hand-over bank, a match round against the
others' newest v3 brains (+ exam on v3). v3's force each way and its action scale fail the shipped footwork brains
(tools/ablate_v3.py), so every boxer is topped up before anything of v3 can reach the game.
"""
from __future__ import annotations

import argparse
import json
import os

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ORDER = ("matt", "sokol", "kim", "zombie", "nick", "trump", "grandpa", "grandma")     # the strongest first: the others meet them
DISPLAY = {n: n.capitalize() for n in ORDER}
NEW = ["--selfcontact-w", "0.02", "--repeat-decay", "0.7", "--combo-w", "0.1", "--hurt-start", "0.2", "--clinch-w", "0.1"]
PUNCH = ["--daze", "--obs-state", "--hit-push", "0.6", "--stagger-v", "5", "--hurt-full", "400", "--body-drain", "0.0025"]
DONE = ["--done-when", "hits_per_s>=1.2,falls<=0.03,repeat_share<=0.6", "--done-window", "40"]
MJ350 = {"PYTHONPATH": ".mj350"}


def recipe(n: str) -> list:
    """Each boxer's own match terms, as the runs that passed or nearly passed had them (rl_optimization_log.md)."""
    if n in ("grandma", "grandpa"):
        return ["--hit-w", "2.0", "--taken-w", "0.3", "--seek-w", "0.3", "--reset-std", "0.35", "--fall-penalty", "12"]
    if n == "trump":
        return ["--hit-w", "1.5", "--taken-w", "1.0", "--gamma", "0.995", "--weights", "matt=2", "grandma=2", "grandpa=2", "--fall-penalty", "8"]
    return ["--hit-w", "1.5", "--taken-w", "1.0", "--block-w", "0.5", "--fall-penalty", "12"]


def match_brain(n: str, upto: int) -> str:
    """A boxer's newest v3 match brain at a point in the queue: its round if it has had one, else its shipped one rescaled."""
    return f"checkpoints/v3m_{n}/latest_{n}.pt" if ORDER.index(n) < upto else f"checkpoints/v3/match_{n}.pt"


def getup_brain(n: str, upto: int) -> str:
    return f"checkpoints/v3u_{n}/latest_{n}.pt" if ORDER.index(n) < upto else f"checkpoints/v3/getup_{n}.pt"


def books() -> None:
    os.makedirs(os.path.join(HERE, "logs"), exist_ok=True)
    for i, n in enumerate(ORDER):
        book = {o: {"match": match_brain(o, i + 1 if o == n else i), "getup": getup_brain(o, i + 1 if o == n else i)} for o in ORDER}
        with open(os.path.join(HERE, "logs", f"book_v3_{n}.json"), "w", encoding="utf-8") as f:
            json.dump(book, f, indent=1)
    print(f"wrote logs/book_v3_<name>.json for {', '.join(ORDER)}")


def flags_queue() -> list:
    q = [{"run": "bench_match", "viewer": False, "cmd": ["tools/bench_match.py", "--write", "logs/bench_match.json"]}]
    q.append({"run": "shipped_matt", "viewer": False, "cmd": ["tools/onnx_to_pt.py", "--onnx", "../Assets/Boxers/Matt/policy.onnx",
                                                              "--out", "checkpoints/shipped/policy_matt.pt"]})
    q.append({"run": "f2_matt", "viewer": True, "cmd": ["tools/train_resilient.py", "--run-name", "f2_matt", "--stage", "footwork",
                                                        "--xml", "models/v2/matt_solo.xml", "--resume", "checkpoints/shipped/policy_matt.pt",
                                                        "--more-iters", "400", "--max-hours", "0.4", "--speed-limit", "--rsi", "0.5",
                                                        "--style-w", "0.3", "--max-std", "0.35", "--selfcontact-w", "0.02", "--keep-old-runs"]})
    q.append({"run": "exam_f2_matt", "viewer": False, "env": MJ350, "cmd": ["tools/footwork_c.py", "--name", "matt", "--exam",
                                                                          "--policy", "checkpoints/f2_matt/latest.pt", "--seeds", "10", "--episodes", "3",
                                                                          "--json", "logs/exam_f2_matt.json"]})
    against = ["zombie=checkpoints/sf1_zombie/latest_zombie.pt", "nick=checkpoints/m1_nick/latest_nick.pt", "sokol=checkpoints/m2_sokol/latest_sokol.pt",
               "kim=checkpoints/m7_kim/latest_kim.pt", "trump=checkpoints/onnx/match_trump.pt", "grandma=checkpoints/m1_grandma/latest_grandma.pt",
               "grandpa=checkpoints/m1_grandpa/latest_grandpa.pt"]
    q.append({"run": "rc2_matt", "viewer": True, "cmd": ["tools/train_resilient.py", "--gauntlet", "--run-name", "rc2_matt", "--stage", "match",
                                                         "--models", "models/v2", "--name", "matt", "--resume", "checkpoints/m9_matt/latest_matt.pt",
                                                         "--against", *against, *PUNCH, *recipe("matt"), *NEW, "--rope-w", "0.05", "--cutoff-w", "0.05",
                                                         "--sep-max", "3.5", "--num-envs", "4608", "--more-iters", "600", "--max-hours", "0.75",
                                                         "--max-std", "0.35", "--handover", "logs/handover_v2_matt.npz", "--handover-share", "0.5", *DONE]})
    q.append({"run": "exam_rc2_matt", "viewer": False, "env": MJ350, "cmd": ["tools/exam_v2.py", "--only", "matt", "--trials", "60", "--rounds", "12",
                                                                           "--json", "logs/exam_rc2_matt.json", "--book", "logs/book_punch.json"]})
    return q


def v3_queue() -> list:
    q = [{"run": "bench_match", "viewer": False, "cmd": ["tools/bench_match.py", "--write", "logs/bench_match.json"]},
         {"run": "books_v3", "viewer": False, "cmd": ["tools/stage_v3.py", "--books"]}]
    for n in ORDER:
        for kind in ("policy", "getup", "match"):
            q.append({"run": f"shipped_{kind}_{n}", "viewer": False, "cmd": ["tools/onnx_to_pt.py", "--onnx", f"../Assets/Boxers/{DISPLAY[n]}/{kind}.onnx",
                                                                            "--out", f"checkpoints/shipped/{kind}_{n}.pt"]})
            q.append({"run": f"rescale_{kind}_{n}", "viewer": False, "env": MJ350,
                      "cmd": ["tools/rescale_actions.py", "--ckpt", f"checkpoints/shipped/{kind}_{n}.pt", "--config", f"models/v3/{n}_policy_config.json",
                              "--out", f"checkpoints/v3/{kind}_{n}.pt", "--check"]})
    q.append({"run": "rules_v3", "viewer": False, "cmd": ["tools/set_rules.py", "--models", "models/v3", "--fatigue-j", "8000", "--hit-push", "0.6",
                                                          "--stagger-v", "5", "--hurt-full", "400", "--body-drain", "0.0025"]})
    for i, n in enumerate(ORDER):
        q.append({"run": f"v3f_{n}", "viewer": True, "cmd": ["tools/train_resilient.py", "--run-name", f"v3f_{n}", "--stage", "footwork",
                                                             "--xml", f"models/v3/{n}_solo.xml", "--resume", f"checkpoints/v3/policy_{n}.pt",
                                                             "--more-iters", "800", "--max-hours", "0.5", "--rsi", "0.5", "--style-w", "0.3",
                                                             "--max-std", "0.35", "--selfcontact-w", "0.02", "--keep-old-runs"]})
        q.append({"run": f"exam_v3f_{n}", "viewer": False, "env": MJ350, "cmd": ["tools/footwork_c.py", "--name", n, "--models", "models/v3", "--exam",
                                                                              "--policy", f"checkpoints/v3f_{n}/latest.pt", "--seeds", "10", "--episodes", "3",
                                                                              "--json", f"logs/exam_v3f_{n}.json"]})
        partner = "zombie" if n == "matt" else "matt"
        q.append({"run": f"v3u_{n}", "viewer": True, "cmd": ["tools/train_resilient.py", "--getup", "--run-name", f"v3u_{n}", "--stage", "v2",
                                                             "--models", "models/v3", "--a", n, "--b", partner,
                                                             "--resume", getup_brain(n, i), getup_brain(partner, i), "--freeze", partner,
                                                             "--reset-std", "0", "--max-std", "0.4", "--more-iters", "600", "--done-when", "0.85"]})
        q.append({"run": f"bank_v3_{n}", "viewer": False, "cmd": ["tools/make_handover_v2.py", "--models", "models/v3", "--name", n,
                                                                  "--ckpt", f"checkpoints/v3u_{n}/latest_{n}.pt", "--out", f"logs/handover_v3_{n}.npz"]})
        against = [f"{o}={match_brain(o, i)}" for o in ORDER if o != n]
        q.append({"run": f"v3m_{n}", "viewer": True, "cmd": ["tools/train_resilient.py", "--gauntlet", "--run-name", f"v3m_{n}", "--stage", "match",
                                                             "--models", "models/v3", "--name", n, "--resume", f"checkpoints/v3/match_{n}.pt",
                                                             "--against", *against, *PUNCH, *recipe(n), *NEW, "--fatigue-ceiling",
                                                             "--sep-max", "3.5", "--num-envs", "4608", "--more-iters", "600", "--max-hours", "0.75",
                                                             "--max-std", "0.35", "--handover", f"logs/handover_v3_{n}.npz", "--handover-share", "0.3", *DONE]})
        q.append({"run": f"exam_v3m_{n}", "viewer": False, "env": MJ350, "cmd": ["tools/exam_v2.py", "--models", "models/v3", "--only", n,
                                                                              "--trials", "60", "--rounds", "12", "--json", f"logs/exam_v3m_{n}.json",
                                                                              "--book", f"logs/book_v3_{n}.json"]})
    return q


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--books", action="store_true")
    args = ap.parse_args()
    if args.books:
        return books()
    for name, q in (("queue_flags_staged.json", flags_queue()), ("queue_v3_staged.json", v3_queue())):
        with open(os.path.join(HERE, name), "w", encoding="utf-8") as f:
            json.dump(q, f, indent=1)
        print(f"wrote {name}: {len(q)} jobs")
    books()


if __name__ == "__main__":
    main()
