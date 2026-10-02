"""The match as the game runs it, in plain C MuJoCo on the CPU: the ring model with its ropes, rounds of
45 seconds with no reset inside them, and the daze rule. Counts who goes down, why, and where.

    python tools/eval_ring.py --run defend --rounds 12
    python tools/eval_ring.py --run defend --rounds 12 --no-daze
    python tools/eval_ring.py --run defend --rounds 12 --rope-solref 0.05
    python tools/eval_ring.py --run defend --rounds 12 --head-bias      (every clean punch counts as a head shot)

What it is for: the trainer's episodes are twelve seconds long, start in the middle of the ring and stand
inside four walls. A round in the game is 45 seconds, nobody is put back in the middle, and the ring has
ropes. When fighters fall in the game more often than they did in training, this says whether the ropes,
the length of a round or the daze rule is the reason, without the game, the GPU or Unity.

A hit, the daze and the weakened drives are the trainer's (envs/boxing.py), written out again in numpy.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys

import mujoco
import numpy as np
import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "tools"))
from ppo import PPO, PPOConfig  # noqa: E402
from eval_cmujoco import Ring  # noqa: E402


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--a", default="matt")
    ap.add_argument("--b", default="zombie")
    ap.add_argument("--run", default="defend")
    ap.add_argument("--rounds", type=int, default=12)
    ap.add_argument("--round-s", type=float, default=45.0)
    ap.add_argument("--no-daze", action="store_true")
    ap.add_argument("--daze-hi", type=float, default=42.0)
    ap.add_argument("--daze-lo", type=float, default=14.0)
    ap.add_argument("--daze-weak", type=float, default=0.45)
    ap.add_argument("--daze-tau", type=float, default=2.5)
    ap.add_argument("--head-bias", action="store_true", help="count every landed punch as a head shot, as the game did before 2026-10-01 23:30")
    ap.add_argument("--rope-solref", type=float, default=0.0, help="override the ropes' contact time constant. 0 = as the model has it")
    ap.add_argument("--walls", action="store_true", help="the trainer's model (walls) instead of the game's (ropes)")
    args = ap.parse_args()

    stem = os.path.join(HERE, "models", f"{args.a}_vs_{args.b}")
    path = stem + ("_spar.xml" if args.walls else "_ring.xml")
    if args.rope_solref > 0.0 and not args.walls:
        text = re.sub(r'(<geom name="rope_[a-z]_\d"[^>]*solref=")[0-9.]+ ', lambda m: m.group(1) + f"{args.rope_solref:g} ", open(path, encoding="utf-8").read())
        path = stem + "_ring_tmp.xml"
        open(path, "w", encoding="utf-8").write(text)
    ring = Ring(path, json.load(open(stem + "_policy_config.json", encoding="utf-8")))
    m, d = ring.m, ring.d
    ppos = []
    for n in (args.a, args.b):
        ppo = PPO(100, ring.A, 1, "cpu", PPOConfig())
        ppo.load(os.path.join(HERE, "checkpoints", args.run, f"latest_{n}.pt"))
        ppos.append(ppo)

    r_glove = [m.geom_size[ring.glove[k][0]][0] for k in range(2)]
    r_head = [m.geom_size[ring.head[k]][0] for k in range(2)]
    r_torso = [m.geom_size[ring.torso[k]][0] for k in range(2)]
    half = [m.geom_size[ring.torso[k]][1] for k in range(2)]
    dt = ring.dt

    falls = [0, 0]
    legs = [0, 0]
    hits = [0, 0]
    head_hits = [0, 0]
    where = []
    seconds = 0.0
    with torch.no_grad():
        for rnd in range(args.rounds):
            ring.reset()
            # As the game starts a round: at the marks, facing each other, in the guard.
            q = ring.key.copy()
            for k, (x, y, yaw) in enumerate(((-0.72, -0.72, math.pi / 4), (0.72, 0.72, math.pi / 4 + math.pi))):
                rq = ring.rq[k]
                q[rq:rq + 2] = (x, y)
                q[rq + 2] = ring.stand[k] + 0.002
                q[rq + 3:rq + 7] = [math.cos(yaw / 2), 0, 0, math.sin(yaw / 2)]
            d.qpos[:] = q
            d.qvel[:] = 0
            mujoco.mj_forward(m, d)
            for k in range(2):
                ring.prev_head[k] = d.geom_xpos[ring.head[1 - k]]
            daze = np.zeros(2)
            legs_out = np.zeros(2)
            scale = np.ones(2)
            armed = np.ones((2, 2), bool)
            cool = np.zeros((2, 2))
            prev_closing = np.zeros((2, 2, 2))
            prev_glove = np.array([[d.geom_xpos[ring.glove[k][i]].copy() for i in range(2)] for k in range(2)])
            prev_tb = np.array([d.geom_xpos[ring.torso[1 - k]].copy() for k in range(2)])
            prev_th = np.array([d.geom_xpos[ring.head[1 - k]].copy() for k in range(2)])
            low = np.zeros(2)
            for step in range(int(args.round_s / dt)):
                act = np.stack([ppos[k].model.actor(ppos[k].obs_rms.normalize(torch.tensor(ring.observe(k), dtype=torch.float32)[None], 10.0))[0].numpy() for k in range(2)])
                act = act.clip(-3.0, 3.0)
                ring.last = act
                want = [(ring.default[k] + act[k] * 0.5).clip(ring.lo[k], ring.hi[k]) for k in range(2)]
                for k in range(2):
                    if scale[k] < 0.999:
                        qk = d.qpos[ring.jq[k]]
                        want[k] = qk + scale[k] * (want[k] - qk)
                d.ctrl[:] = np.concatenate(want)
                for _ in range(4):
                    mujoco.mj_step(m, d)
                seconds += dt
                received = np.zeros(2)
                for k in range(2):
                    o = 1 - k
                    th, tb = d.geom_xpos[ring.head[o]].copy(), d.geom_xpos[ring.torso[o]].copy()
                    ax = d.geom_xmat[ring.torso[o]].reshape(3, 3)[:, 2]
                    ring.head_vel[k] = (th - ring.prev_head[k]) / dt
                    ring.prev_head[k] = th
                    vh, vb = (th - prev_th[k]) / dt, (tb - prev_tb[k]) / dt
                    prev_th[k], prev_tb[k] = th, tb
                    for i in range(2):
                        g = d.geom_xpos[ring.glove[k][i]].copy()
                        vg = (g - prev_glove[k, i]) / dt
                        prev_glove[k, i] = g
                        to_h = th - g
                        dh = np.linalg.norm(to_h)
                        along = float(np.clip((g - tb) @ ax, -half[o], half[o]))
                        to_b = tb + ax * along - g
                        db = np.linalg.norm(to_b)
                        gap = np.array([dh - r_glove[k] - r_head[o], db - r_glove[k] - r_torso[o]])
                        nh, nb = to_h / max(dh, 1e-4), to_b / max(db, 1e-4)
                        closing = np.array([min((vg - vh) @ nh, vg @ nh), min((vg - vb) @ nb, vg @ nb)])
                        speed = np.clip(np.maximum(closing, prev_closing[k, i]), 0.0, 9.0)
                        prev_closing[k, i] = closing
                        zone = int(gap.argmin())
                        cool[k, i] = max(0.0, cool[k, i] - dt)
                        landed = gap[zone] < 0.012 and armed[k, i] and speed[zone] > 1.0 and cool[k, i] <= 0.0
                        armed[k, i] = (armed[k, i] and not landed) or gap[zone] > 0.30
                        if landed:
                            cool[k, i] = 0.25
                            hits[k] += 1
                            head_hits[k] += int(zone == 0)
                            received[o] += speed[zone] * (1.0 if zone == 0 or args.head_bias else 0.3)
                if not args.no_daze:
                    daze = daze * math.exp(-dt / args.daze_tau) + received
                    for k in range(2):
                        if daze[k] >= args.daze_hi and legs_out[k] <= 0.0:
                            legs_out[k] = 0.7
                            daze[k] = 0.0
                            legs[k] += 1
                        else:
                            legs_out[k] = max(0.0, legs_out[k] - dt)
                        weak = 1.0 - args.daze_weak * float(np.clip((daze[k] - args.daze_lo) / (args.daze_hi - args.daze_lo), 0.0, 1.0))
                        scale[k] = 0.04 if legs_out[k] > 0.0 else weak
                down = False
                for k in range(2):
                    p = d.xpos[ring.pelvis[k]]
                    up = -(d.xmat[ring.pelvis[k]].reshape(3, 3).T @ np.array([0.0, 0.0, -1.0]))[2]
                    low[k] = low[k] + dt if (p[2] < ring.stand[k] * 0.6 or up < 0.4) else 0.0
                    if low[k] > 0.4:
                        falls[k] += 1
                        where.append((k, step * dt, max(abs(p[0]), abs(p[1])), legs_out[k] > 0.0 or daze[k] < 1.0))
                        down = True
                if down:
                    break   # the game counts and stands them up; here the round simply ends
    minutes = seconds / 60.0
    names = (args.a, args.b)
    print(f"C MuJoCo {mujoco.__version__}, run {args.run}, {'walls' if args.walls else 'ropes'}"
          f"{'' if args.rope_solref <= 0 else f' (solref {args.rope_solref:g})'}, daze {'off' if args.no_daze else f'on (line {args.daze_hi:g})'}"
          f"{', every punch a head shot' if args.head_bias else ''}: {args.rounds} rounds, {seconds:.0f} s boxed")
    for k in range(2):
        print(f"  {names[k]}: down {falls[k]} times ({falls[k] / max(minutes, 1e-6):.2f} a minute), legs taken by the daze {legs[k]} times; "
              f"landed {hits[k] / max(seconds, 1e-6):.2f} a second, {head_hits[k] / max(1, hits[k]):.0%} to the head")
    near = sum(1 for w in where if w[2] > 2.6)
    print(f"  of {len(where)} falls, {near} were within 45 cm of the ropes; first falls at " + ", ".join(f"{names[w[0]]} {w[1]:.0f}s" for w in where[:8]))


if __name__ == "__main__":
    main()
