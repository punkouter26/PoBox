"""Elo for every boxer from CPU bouts: the game's brains boxing each other in plain C MuJoCo, every pair, both ways.

    PYTHONPATH=.mj350 python tools/elo.py                       4 bouts a pair, the brains the game ships
    PYTHONPATH=.mj350 python tools/elo.py --bouts 8 --workers 4
    PYTHONPATH=.mj350 python tools/elo.py --book logs/book_punch.json --no-write    rate a set of trial brains

A bout is the game's: 3 rounds of 45 s under each boxer's game rules (Assets/Boxers/NAME/config.json laid over
the trainer's pair config, so the punch push, fatigue and speed limit are what the game runs), the exam's punches
and daze (tools/exam.py rounds()). Each round is scored as the game's judges do it, one card instead of three:
a knockdown costs a point, otherwise 10-9 to whoever landed more punch speed, 10-10 within 4%. A bout starts from
the marks with every joint jittered a little (seeded), which is where one bout of a pair differs from the next.

The ratings are a Bradley-Terry fit of all the results (a draw is half a win each), on the Elo scale (400 points
= 10 to 1), centred on 1000: unlike Elo updated bout by bout, it does not depend on the order the bouts ran in.
Written to Assets/Resources/cpu_elo.json, where the game's ladder starts from (LeagueTable), and every bout is
appended to logs/cpu_bouts.ndjson with its numbers.

ponytail: one judge, not three, and the lasting damage starts again each round (exam rounds() resets it at a
bout's first round, and each round here is its own call); a closer copy of Judges.cs if ratings ever hinge on it.
"""
from __future__ import annotations

import argparse
import itertools
import json
import math
import os
import sys
import time
from multiprocessing import Pool

import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(HERE, "tools"))
import exam as E  # noqa: E402
import exam_v2 as V  # noqa: E402

GAME = os.path.join(ROOT, "Assets", "Boxers")
OUT = os.path.join(ROOT, "Assets", "Resources", "cpu_elo.json")
BOUTS_LOG = os.path.join(HERE, "logs", "cpu_bouts.ndjson")
RULES = ("hit_push", "stagger_v", "hurt_full", "body_drain", "fatigue_j", "speed_limit")
ROUNDS, ROUND_S, JITTER = 3, 45.0, 0.05

E.load_policy, E.act = V.load_policy, V.act


def game_cfg(pair_cfg: dict, names) -> dict:
    """The pair's trainer config with each boxer's game rules laid over it."""
    cfg = json.loads(json.dumps(pair_cfg))
    for c, n in zip(cfg["fighters"], names):
        with open(os.path.join(GAME, n.capitalize(), "config.json"), "r", encoding="utf-8") as f:
            g = json.load(f)
        for k in RULES:
            if k in g:
                c[k] = g[k]
            else:
                c.pop(k, None)
    return cfg


def card(r) -> tuple:
    """One round's score from rounds() totals: (red, blue), the ten-point must."""
    downs = [r[0]["falls_hit"], r[1]["falls_hit"]]
    a, b = r[0]["speed"], r[1]["speed"]
    if downs[0] != downs[1]:
        lean = 1 if downs[0] < downs[1] else -1
    elif abs(a - b) <= 0.04 * (a + b) + 1e-4:
        lean = 0
    else:
        lean = 1 if a > b else -1
    red, blue = (10 if lean >= 0 else 9) - downs[0], (10 if lean <= 0 else 9) - downs[1]
    if downs[0] and not downs[1]:
        blue = 10
    if downs[1] and not downs[0]:
        red = 10
    return max(7, red), max(7, blue)


def box_pair(job) -> list:
    """Every bout of one pair; the model's a_ fighter is x."""
    stem, x, y, book, bouts, seed = job
    with open(stem + "_policy_config.json", "r", encoding="utf-8") as f:
        cfg = game_cfg(json.load(f), (x, y))
    ring = E.ExamRing(stem + "_spar.xml", cfg, seed=seed)
    pols = [V.load_policy(os.path.join(HERE, book[n]["match"]), ring.A) for n in (x, y)]
    for i, p in enumerate(pols):
        if getattr(p, "obs_state", False):
            p.state = (lambda i=i: ring.state(i))
    # The marks with a jitter, so the bouts of a pair are not one bout repeated; half of them with the corners swapped.
    marks = [((-0.72, -0.72), (0.72, 0.72)), ((0.72, 0.72), (-0.72, -0.72))]
    out = []
    for n in range(bouts):
        spots = marks[n % 2]
        yaw = math.atan2(spots[1][1] - spots[0][1], spots[1][0] - spots[0][0])
        ring.at_marks = lambda spots=spots, yaw=yaw: ring.place(spots, [yaw, yaw + math.pi], jitter=JITTER)
        ring.hurt = np.zeros(2)
        score, stats = [0, 0], [dict(landed=0, speed=0.0, knockdowns=0) for _ in range(2)]
        for _ in range(ROUNDS):
            r = E.rounds(ring, pols, 1, ROUND_S, True)
            for k, s in enumerate(card(r)):
                score[k] += s
            for k in range(2):
                stats[k]["landed"] += r[k]["hits"]
                stats[k]["speed"] += r[k]["speed"]
                stats[k]["knockdowns"] += r[1 - k]["falls_hit"]
        result = 1.0 if score[0] > score[1] else 0.0 if score[0] < score[1] else 0.5
        out.append(dict(when=time.strftime("%Y-%m-%d %H:%M:%S"), red=x, blue=y, cards=score, result=result,
                        red_brain=book[x]["match"], blue_brain=book[y]["match"], bout=n, seed=seed,
                        red_stats=stats[0], blue_stats=stats[1]))
    return out


def bradley_terry(names, results, iters: int = 2000) -> dict:
    """Strengths by the minorise-maximise fit (Hunter 2004), on the Elo scale, mean 1000. A draw is half a win each;
    every boxer is given half a draw against a 1000 ghost so one who has lost (or won) everything stays finite."""
    i = {n: k for k, n in enumerate(names)}
    n = len(names)
    wins, games = np.full(n, 0.5), np.zeros((n, n))
    for r in results:
        a, b = i[r["red"]], i[r["blue"]]
        wins[a] += r["result"]
        wins[b] += 1.0 - r["result"]
        games[a, b] += 1
        games[b, a] += 1
    p = np.ones(n)
    for _ in range(iters):
        denom = (games / (p[:, None] + p[None, :])).sum(1) + 1.0 / (p + 1.0)
        p = wins / denom
        p /= np.exp(np.log(p).mean())
    elo = 400.0 * np.log10(p)
    return {nm: float(1000.0 + elo[k] - elo.mean()) for nm, k in i.items()}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--bouts", type=int, default=4, help="bouts per pair (half with the corners swapped)")
    ap.add_argument("--workers", type=int, default=3)
    ap.add_argument("--book", default="logs/book_shipped.json", help="the brains: the game's by default")
    ap.add_argument("--only", nargs="*", default=[], help="pairs with at least one of these boxers")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--no-write", action="store_true", help="print the table; write nothing (neither the game's file nor the bout log)")
    args = ap.parse_args()

    with open(os.path.join(HERE, args.book), "r", encoding="utf-8") as f:
        book = json.load(f)
    names = sorted(n for n in book if "match" in book[n])
    jobs = []
    for a, b in itertools.combinations(names, 2):
        if args.only and a not in args.only and b not in args.only:
            continue
        stem, x, y = V.pair_model(a, b)
        if stem:
            jobs.append((stem, x, y, book, args.bouts, args.seed + len(jobs)))
        else:
            print(f"  no model for {a} v {b}")
    t0 = time.time()
    results = []
    with Pool(args.workers) as pool:
        for out in pool.imap_unordered(box_pair, jobs):
            results += out
            r = out[0]
            w = sum(o["result"] for o in out)
            print(f"  {r['red']:8s} v {r['blue']:8s} {w:g}-{len(out) - w:g}  ({time.time() - t0:.0f} s)", flush=True)
    if not args.no_write:
        with open(BOUTS_LOG, "a", encoding="utf-8") as f:
            for r in results:
                f.write(json.dumps(r) + "\n")

    rating = bradley_terry(names, results)
    record = {n: dict(wins=0, losses=0, draws=0) for n in names}
    for r in results:
        for n, s in ((r["red"], r["result"]), (r["blue"], 1.0 - r["result"])):
            record[n]["wins" if s == 1.0 else "losses" if s == 0.0 else "draws"] += 1
    print()
    for n in sorted(names, key=lambda n: -rating[n]):
        rec = record[n]
        print(f"{n.upper():9s} {rating[n]:6.0f}   {rec['wins']}-{rec['losses']}-{rec['draws']}   ({book[n]['match']})")
    print(f"\n{len(results)} bouts, {time.time() - t0:.0f} s.")
    if not args.no_write:
        os.makedirs(os.path.dirname(OUT), exist_ok=True)
        rows = [dict(name=n.upper(), elo=round(rating[n], 1), brain=book[n]["match"], **record[n]) for n in names]
        with open(OUT, "w", encoding="utf-8") as f:
            json.dump({"when": time.strftime("%Y-%m-%d %H:%M:%S"), "bouts": len(results), "bouts_per_pair": args.bouts,
                       "rows": sorted(rows, key=lambda r: -r["elo"])}, f, indent=1)
        print(f"written {os.path.relpath(OUT, ROOT)}")


if __name__ == "__main__":
    main()
