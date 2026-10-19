"""How long a boxer's reserve lasts at a given fatigue_j, with the brains the game ships, in C MuJoCo (the exam's ring,
punches, daze and fatigue: tools/exam.py), boxing 45 s from the game's marks (joints jittered 0.05 rad).

    PYTHONPATH=.mj350 python tools/fatigue_sweep.py --joules 8000 16000 24000 --seeds 3 --json logs/fatigue_sweep.json

Every pair with a model in models/v2 boxes once per fatigue_j and seed; each boxer's reserve is read every 0.5 s.
A fall puts both back in the ring with their reserve kept (the game's get-up drains nothing). Owner's aim (2026-10-18):
the median boxer near 0.3-0.4 at 40 s, the busiest not empty before ~25 s.
"""
from __future__ import annotations

import argparse
import itertools
import json
import os
import sys
import time
from multiprocessing import Pool

import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(HERE, "tools"))
import exam as E  # noqa: E402
import exam_v2 as V  # noqa: E402

E.load_policy, E.act = V.load_policy, V.act
NAMES = V.NAMES


def bout(job):
    stem, x, y, joules, seed, book = job
    xml = stem + ("_ring.xml" if os.path.exists(stem + "_ring.xml") else "_spar.xml")
    cfg = json.load(open(stem + "_policy_config.json", encoding="utf-8"))
    for e in cfg.get("fighters", [cfg]):
        e["fatigue_j"] = float(joules)
    ring = E.ExamRing(xml, cfg, seed=seed)
    pols = [V.load_policy(os.path.join(HERE, book[n]), ring.A) for n in (x, y)]
    for i, p in enumerate(pols):
        p.obs_state = True
        p.state = (lambda i=i: ring.state(i))
    place = ring.place
    ring.at_marks = lambda: place([(-0.72, -0.72), (0.72, 0.72)], [np.pi / 4, np.pi / 4 + np.pi], jitter=0.05)
    trace, watts, steps = [], np.zeros(2), [0]
    drive = ring.drive

    def logged(actions):
        drive(actions)
        d, A = ring.d, ring.A
        for k in range(2):
            watts[k] += np.abs(d.actuator_force[k * A:(k + 1) * A] * d.qvel[ring.jv[k]]).sum()
        steps[0] += 1
        if steps[0] % 25 == 0:
            trace.append(ring.reserve.copy().tolist())

    ring.drive = logged
    out = E.rounds(ring, pols, 1, 45.0, True)
    return dict(pair=[x, y], joules=joules, seed=seed, trace=trace, watts=(watts / steps[0]).tolist(),
                hits=[o["hits"] for o in out], falls=[o["falls_own"] + o["falls_hit"] for o in out])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--joules", nargs="+", type=float, default=[8000, 12000, 16000, 20000, 24000, 32000])
    ap.add_argument("--seeds", type=int, default=3)
    ap.add_argument("--workers", type=int, default=8)
    ap.add_argument("--book", default="", help='{"matt": "checkpoints/.../match_matt.pt"}; default checkpoints/onnx_1018 (the game\'s)')
    ap.add_argument("--json", default=os.path.join(HERE, "logs", "fatigue_sweep.json"))
    a = ap.parse_args()
    book = json.load(open(a.book)) if a.book else {n: f"checkpoints/onnx_1018/match_{n}.pt" for n in NAMES}
    pairs = []
    for i, x in enumerate(NAMES):
        for y in NAMES[i + 1:]:
            stem, p, q = E.pair_model(x, y)
            if stem:
                pairs.append((stem, p, q))
    jobs = [(s, p, q, j, seed, book) for (s, p, q), j, seed in itertools.product(pairs, a.joules, range(1, a.seeds + 1))]
    t0 = time.time()
    with Pool(a.workers) as pool:
        res = pool.map(bout, jobs, chunksize=1)
    json.dump(res, open(a.json, "w"), indent=0)
    # Per boxer and fatigue_j: median reserve at 25 s and 40 s over its bouts, and when it first reads empty (< 0.05).
    print(f"{len(jobs)} bouts, {time.time() - t0:.0f} s")
    for j in a.joules:
        row = []
        for n in NAMES:
            tr = [np.array(r["trace"])[:, r["pair"].index(n)] for r in res if r["joules"] == j and n in r["pair"]]
            if not tr:
                continue
            at = lambda s: float(np.median([t[min(len(t) - 1, int(s * 2) - 1)] for t in tr]))
            empty = [((t < 0.05).argmax() + 1) / 2 if (t < 0.05).any() else 99 for t in tr]
            row.append((n, at(25), at(40), float(np.median(empty)), min(empty)))
        r40 = np.median([r[2] for r in row])
        print(f"fatigue_j {j:6.0f}: median boxer at 40 s {r40:.2f} | " + "  ".join(f"{n} {a25:.2f}/{a40:.2f} empty {e:.0f}s(min {m:.0f})" for n, a25, a40, e, m in row))


if __name__ == "__main__":
    main()
