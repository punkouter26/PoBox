"""The Melee exam (2026-10-19): a boxer against each opponent with N downed bodies on the canvas (envs/boxing.py lying_bodies,
the same shape training uses), C MuJoCo 3.5.0, the exam's ring and punch rules, no daze (falls are then the feet's own).

    PYTHONPATH=.mj350 python tools/melee_exam.py NAME [--book logs/book_night_mix.json] [--bodies 2] [--rounds 4] [--json out.json]

Per boxer: falls nobody caused a minute (the footing line, mark 0.2), how many of them came within 1.5 s of touching a body
(trips), body touches a minute, and its punches landed a second. A 106-input brain sees the nearest body (envs/match.py's
numbers, worked out here again in numpy); a 103 brain only feels it.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import sys

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(HERE, "tools"))
sys.path.insert(0, HERE)
import exam as E  # noqa: E402
import exam_v2 as V  # noqa: E402
from envs.bodies import lying_bodies  # noqa: E402

PELVIS_X, SEE_M = -0.28, 3.0          # envs/match.py BODY_PELVIS_X, BODY_SEE_M


class MeleeRing(E.ExamRing):
    def __init__(self, xml, cfg, n, seed=1):
        super().__init__(xml, cfg, seed, model=lying_bodies(xml, n))
        self.n = n
        self.bid = [mujoco.mj_name2id(self.m, mujoco.mjtObj.mjOBJ_BODY, f"downed_{j}") for j in range(n)]
        self.mocap = [int(self.m.body_mocapid[b]) for b in self.bid]
        self.downed = {g for g in range(self.m.ngeom) if int(self.m.geom_bodyid[g]) in self.bid}
        self.owner = np.array([0 if (mujoco.mj_id2name(self.m, mujoco.mjtObj.mjOBJ_GEOM, g) or "").startswith("a_") else
                               1 if (mujoco.mj_id2name(self.m, mujoco.mjtObj.mjOBJ_GEOM, g) or "").startswith("b_") else -1 for g in range(self.m.ngeom)])

    def _bodies(self):
        d = self.d
        feet = [d.qpos[self.rq[k]:self.rq[k] + 2].copy() for k in range(2)]
        lim, placed = self.ring_half - 0.7, []
        for j in range(self.n):
            for _ in range(50):
                xy = self.rng.uniform(-lim, lim, 2)
                if all(np.linalg.norm(xy - f) > 0.9 for f in feet) and all(np.linalg.norm(xy - p) > 1.2 for p in placed):
                    break
            placed.append(xy)
            yaw = self.rng.uniform(-math.pi, math.pi)
            d.mocap_pos[self.mocap[j]] = [xy[0], xy[1], 0.0]
            d.mocap_quat[self.mocap[j]] = [math.cos(yaw / 2), 0, 0, math.sin(yaw / 2)]
        mujoco.mj_forward(self.m, d)

    def place(self, spots, yaws, jitter=0.0):
        super().place(spots, yaws, jitter)
        self._bodies()

    def reset(self):
        super().reset()
        self._bodies()

    def seen(self, k):
        """envs/match.py's three numbers for fighter k."""
        d = self.d
        pos = d.xpos[self.pelvis[k]]
        q = d.xquat[self.pelvis[k]]
        yaw = math.atan2(2.0 * (q[0] * q[3] + q[1] * q[2]), 1.0 - 2.0 * (q[2] * q[2] + q[3] * q[3]))
        best, bd = None, 1e9
        for j in range(self.n):
            mq, mp = d.mocap_quat[self.mocap[j]], d.mocap_pos[self.mocap[j]]
            yb = math.atan2(2.0 * (mq[0] * mq[3] + mq[1] * mq[2]), 1.0 - 2.0 * (mq[2] * mq[2] + mq[3] * mq[3]))
            p = mp + np.array([math.cos(yb) * PELVIS_X, math.sin(yb) * PELVIS_X, 0.0])
            dist = float(np.linalg.norm((p - pos)[:2]))
            if dist < bd:
                best, bd = p, dist
        if best is None or bd > SEE_M:
            return np.zeros(3)
        c, s = math.cos(yaw), math.sin(yaw)
        v = best - pos
        return np.array([np.clip(c * v[0] + s * v[1], -3, 3), np.clip(-s * v[0] + c * v[1], -3, 3), 1.0])

    def touching(self):
        """Which fighters have a part on a downed body now."""
        out = [False, False]
        for i in range(self.d.ncon):
            c = self.d.contact[i]
            for a, b in ((c.geom1, c.geom2), (c.geom2, c.geom1)):
                if a in self.downed and self.owner[b] >= 0:
                    out[self.owner[b]] = True
        return out


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("name")
    ap.add_argument("--book", default=os.path.join(HERE, "logs", "book_night_mix.json"))
    ap.add_argument("--bodies", type=int, default=2)
    ap.add_argument("--rounds", type=int, default=4)
    ap.add_argument("--json", default="")
    a = ap.parse_args()
    book = json.load(open(a.book, encoding="utf-8"))
    E.act = V.act
    me = a.name
    tot = dict(seconds=0.0, falls=0, trips=0, touch_s=0.0, touches=0, hits=0)
    per = {}
    for opp in book:
        if opp == me:
            continue
        stem, x, y = E.pair_model(me, opp)
        xml = stem + ("_ring.xml" if os.path.exists(stem + "_ring.xml") else "_spar.xml")
        ring = MeleeRing(xml, json.load(open(stem + "_policy_config.json", encoding="utf-8")), a.bodies)
        k = 0 if x == me else 1
        pols = [V.load_policy(os.path.join(HERE, book[n]["match"]), ring.A) for n in (x, y)]
        for i, p in enumerate(pols):
            if p.obs_state:
                p.state = (lambda i=i: ring.state(i))
            p.bodies = (lambda i=i: ring.seen(i))
        r = dict(seconds=0.0, falls=0, trips=0, touch_s=0.0, touches=0, hits=0)
        for _ in range(a.rounds):
            ring.at_marks()
            pun = E.Punches(ring)
            low, since_taken, since_touch, was = np.zeros(2), np.full(2, 99.0), np.full(2, 99.0), [False, False]
            for _ in range(int(45.0 / ring.dt)):
                ring.drive([E.act(pols[j], ring.observe(j)) for j in range(2)])
                landed, _ = pun.step()
                r["seconds"] += ring.dt
                r["hits"] += len(landed[k])
                since_taken += ring.dt
                since_touch += ring.dt
                for j in range(2):
                    if landed[1 - j]:
                        since_taken[j] = 0.0
                now = ring.touching()
                for j in range(2):
                    if now[j]:
                        since_touch[j] = 0.0
                if now[k]:
                    r["touch_s"] += ring.dt
                    r["touches"] += int(not was[k])
                was = now
                down = False
                for j in range(2):
                    h, up = ring.posture(j)
                    low[j] = low[j] + ring.dt if (h < 0.6 or up < 0.4) else 0.0
                    if low[j] > 0.4:
                        down = True
                        if j == k and since_taken[j] >= 1.5:
                            r["falls"] += 1
                            r["trips"] += int(since_touch[j] < 1.5)
                if down:
                    ring.reset()
                    pun = E.Punches(ring)
                    low[:], since_taken[:], since_touch[:] = 0.0, 99.0, 99.0
        per[opp] = {"falls_per_min": r["falls"] / (r["seconds"] / 60), "trips_per_min": r["trips"] / (r["seconds"] / 60),
                    "touches_per_min": r["touches"] / (r["seconds"] / 60), "touch_share": r["touch_s"] / r["seconds"], "hits_per_s": r["hits"] / r["seconds"]}
        for key in tot:
            tot[key] += r[key]
        print(f"  {me} v {opp}: {json.dumps({k2: round(v, 3) for k2, v in per[opp].items()})}", flush=True)
    mins = tot["seconds"] / 60
    out = {"name": me, "match": book[me]["match"], "inputs": int(V.obs_dim_of(os.path.join(HERE, book[me]["match"]))), "bodies": a.bodies,
           "rounds": a.rounds, "falls_per_min": tot["falls"] / mins, "trips_per_min": tot["trips"] / mins, "touches_per_min": tot["touches"] / mins,
           "touch_share": tot["touch_s"] / tot["seconds"], "hits_per_s": tot["hits"] / tot["seconds"],
           "worst_falls_per_min": max(v["falls_per_min"] for v in per.values()), "vs": per}
    print(json.dumps({k2: v for k2, v in out.items() if k2 != "vs"}, indent=1))
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(out, f, indent=1)


if __name__ == "__main__":
    main()
