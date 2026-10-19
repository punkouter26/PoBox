"""Why do some boxers (Matt, Sokol) land 0-3% to the head? C MuJoCo 3.5.0, the exam's ring and punch rules, the game's roster.

    PYTHONPATH=.mj350 python tools/head_diag.py matt sokol nick zombie [--book logs/book_night_mix.json] [--rounds 2]

Per boxer, over 45 s rounds against every opponent: the pelvis distance it keeps; where its gloves come closest to (head or
body) at punching speed; how far the head was when a punch landed on the body (gap to the head minus gap to the body: how much
further the head is); how many fast gloves came within 10 cm of the head and what became of them (landed, blocked, nothing);
and the glove's height at a body landing against the head's.
"""
from __future__ import annotations

import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(HERE, "tools"))
sys.path.insert(0, HERE)
import exam as E  # noqa: E402
import exam_v2 as V  # noqa: E402


def main() -> None:
    args = sys.argv[1:]
    book_path = os.path.join(HERE, "logs", "book_night_mix.json")
    rounds = 2
    if "--book" in args:
        i = args.index("--book"); book_path = args[i + 1]; del args[i:i + 2]
    if "--rounds" in args:
        i = args.index("--rounds"); rounds = int(args[i + 1]); del args[i:i + 2]
    book = json.load(open(book_path, encoding="utf-8"))
    E.act = V.act
    out = {}
    for me in args:
        acc = dict(steps=0, dist=[], body_land=0, head_land=0, head_extra=[], dz=[], near_head=0, near_body=0, att_land=0, att_blocked=0,
                   reach_head=[], reach_body=[])
        for opp in book:
            if opp == me:
                continue
            stem, x, y = E.pair_model(me, opp)
            xml = stem + ("_ring.xml" if os.path.exists(stem + "_ring.xml") else "_spar.xml")
            ring = E.ExamRing(xml, json.load(open(stem + "_policy_config.json", encoding="utf-8")))
            k = 0 if x == me else 1
            o = 1 - k
            pols = [V.load_policy(os.path.join(HERE, book[n]["match"]), ring.A) for n in (x, y)]
            for i, p in enumerate(pols):
                if p.obs_state:
                    p.state = (lambda i=i: ring.state(i))
            d = ring.d
            for _ in range(rounds):
                ring.at_marks()
                pun = E.Punches(ring)
                prev = np.array([d.geom_xpos[ring.glove[k][i]].copy() for i in range(2)])
                for _ in range(int(45.0 / ring.dt)):
                    ring.drive([E.act(pols[j], ring.observe(j)) for j in range(2)])
                    landed, stopped = pun.step()
                    th, tb = d.geom_xpos[ring.head[o]], d.geom_xpos[ring.torso[o]]
                    ax = d.geom_xmat[ring.torso[o]].reshape(3, 3)[:, 2]
                    pk, po = d.xpos[ring.pelvis[k]], d.xpos[ring.pelvis[o]]
                    acc["steps"] += 1
                    acc["dist"].append(float(np.linalg.norm((pk - po)[:2])))
                    for i in range(2):
                        g = d.geom_xpos[ring.glove[k][i]]
                        v = np.linalg.norm(g - prev[i]) / ring.dt
                        prev[i] = g.copy()
                        gh = np.linalg.norm(th - g) - ring.r_glove[k] - ring.r_head[o]
                        al = float(np.clip((g - tb) @ ax, -ring.half[o], ring.half[o]))
                        gb = np.linalg.norm(tb + ax * al - g) - ring.r_glove[k] - ring.r_torso[o]
                        if v > 2.0:
                            acc["reach_head"].append(gh)
                            acc["reach_body"].append(gb)
                            acc["near_head"] += int(gh < 0.10)
                            acc["near_body"] += int(gb < 0.10)
                    for zone, speed in landed[k]:
                        if zone == 0:
                            acc["head_land"] += 1
                        else:
                            acc["body_land"] += 1
                    # body landings: how much further away the head was, and the glove's height against the head's
                    for zone, speed in landed[k]:
                        if zone == 1:
                            gs = [d.geom_xpos[ring.glove[k][i]] for i in range(2)]
                            g = min(gs, key=lambda q: np.linalg.norm(q - tb))
                            acc["head_extra"].append(float(np.linalg.norm(th - g) - ring.r_head[o] - ring.r_glove[k]))
                            acc["dz"].append(float(th[2] - g[2]))
                    acc["att_blocked"] += stopped[o]
            print(f"  {me} v {opp} done", flush=True)
        n = max(1, acc["head_land"] + acc["body_land"])
        q = lambda a, p: float(np.percentile(a, p)) if a else float("nan")
        row = {"dist_median": q(acc["dist"], 50), "dist_p10": q(acc["dist"], 10), "dist_p90": q(acc["dist"], 90),
               "landed": n, "head_share": acc["head_land"] / n,
               "body_land_head_extra_median_m": q(acc["head_extra"], 50), "body_land_head_above_glove_median_m": q(acc["dz"], 50),
               "fast_glove_near_head_per_s": acc["near_head"] / (acc["steps"] * ring.dt), "fast_glove_near_body_per_s": acc["near_body"] / (acc["steps"] * ring.dt),
               "fast_glove_min_gap_head_p10": q(acc["reach_head"], 10), "fast_glove_min_gap_body_p10": q(acc["reach_body"], 10),
               "its_head_attempts_blocked_per_s": acc["att_blocked"] / (acc["steps"] * ring.dt)}
        out[me] = row
        print(me.upper(), json.dumps(row, indent=1), flush=True)
    with open(os.path.join(HERE, "logs", "head_diag.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, indent=1)


if __name__ == "__main__":
    main()
