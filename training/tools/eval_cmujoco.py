"""The match policies run in plain C MuJoCo (the library a game can load), not MuJoCo Warp (the GPU
reimplementation they were trained in).

    python tools/eval_cmujoco.py [--a matt --b zombie --run match --seconds 300]

Two things this is for. It is fast: one world on one CPU core does 300 simulated seconds in well under a
minute, where the Warp environment on the CPU takes half an hour. And it answers a question that has to
be answered before the fighters are put in Unity on MuJoCo's own physics: is the C library close enough
to Warp that a policy trained in one stands up in the other?

The observation here is written out again from scratch in numpy, on purpose. It is the same hundred
numbers as envs/boxing.py builds, and it is the template for the C# that builds them in Unity.
"""
from __future__ import annotations

import argparse
import math
import os
import sys

import mujoco
import numpy as np
import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from ppo import PPO, PPOConfig  # noqa: E402


def yaw_of(q):
    w, x, y, z = q
    return math.atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (y * y + z * z))


def heading(v, yaw):
    c, s = math.cos(yaw), math.sin(yaw)
    return np.array([c * v[0] + s * v[1], -s * v[0] + c * v[1], v[2]])


class Ring:
    def __init__(self, xml: str, cfg: dict, seed: int = 1):
        self.m = mujoco.MjModel.from_xml_path(xml)
        self.d = mujoco.MjData(self.m)
        m = self.m
        self.rng = np.random.default_rng(seed)
        self.cfgs = cfg["fighters"]
        self.order = self.cfgs[0]["joint_order"]
        self.A = len(self.order)
        self.ring_half = float(cfg.get("ring_half", 3.05))
        J, G, B = mujoco.mjtObj.mjOBJ_JOINT, mujoco.mjtObj.mjOBJ_GEOM, mujoco.mjtObj.mjOBJ_BODY
        self.rq, self.rv, self.jq, self.jv, self.pelvis, self.head, self.torso, self.glove, self.foot = [], [], [], [], [], [], [], [], []
        for p in ("a_", "b_"):
            r = mujoco.mj_name2id(m, J, p + "root")
            self.rq.append(int(m.jnt_qposadr[r]))
            self.rv.append(int(m.jnt_dofadr[r]))
            js = [mujoco.mj_name2id(m, J, p + n) for n in self.order]
            self.jq.append(np.array([m.jnt_qposadr[j] for j in js]))
            self.jv.append(np.array([m.jnt_dofadr[j] for j in js]))
            self.pelvis.append(mujoco.mj_name2id(m, B, p + "pelvis"))
            self.head.append(mujoco.mj_name2id(m, G, p + "head_geom"))
            self.torso.append(mujoco.mj_name2id(m, G, p + "torso_geom"))
            self.glove.append([mujoco.mj_name2id(m, G, p + f"glove_{s}") for s in "lr"])
            self.foot.append([mujoco.mj_name2id(m, G, p + f"foot_{s}_geom") for s in "lr"])
        key = m.key_qpos[0].copy()
        self.key = key
        self.default = [key[self.jq[k]] for k in range(2)]
        self.stand = [key[self.rq[k] + 2] for k in range(2)]
        self.lo = [np.array(c["lower"]) for c in self.cfgs]
        self.hi = [np.array(c["upper"]) for c in self.cfgs]
        self.dt = m.opt.timestep * 4
        self.last = np.zeros((2, self.A))
        self.prev_head = np.zeros((2, 3))
        self.head_vel = np.zeros((2, 3))

    def reset(self):
        m, d = self.m, self.d
        mujoco.mj_resetData(m, d)
        q = self.key.copy()
        centre = self.rng.uniform(-1.4, 1.4, 2)
        bearing = self.rng.uniform(-math.pi, math.pi)
        sep = self.rng.uniform(0.85, 2.2)
        u = np.array([math.cos(bearing), math.sin(bearing)])
        for k in range(2):
            yaw = bearing + (0.0 if k == 0 else math.pi) + self.rng.uniform(-0.6, 0.6)
            rq = self.rq[k]
            q[rq:rq + 2] = centre + u * sep * (-0.5 if k == 0 else 0.5)
            q[rq + 2] = self.stand[k] + 0.002
            q[rq + 3:rq + 7] = [math.cos(yaw / 2), 0, 0, math.sin(yaw / 2)]
            q[self.jq[k]] = self.default[k] + self.rng.uniform(-0.06, 0.06, self.A)
        d.qpos[:] = q
        d.qvel[:] = 0
        d.ctrl[:] = np.concatenate([q[self.jq[k]] for k in range(2)])
        mujoco.mj_forward(m, d)
        self.last[:] = 0
        self.head_vel[:] = 0
        for k in range(2):
            self.prev_head[k] = d.geom_xpos[self.head[1 - k]]

    def observe(self, k: int) -> np.ndarray:
        d = self.d
        o = 1 - k
        pos = d.xpos[self.pelvis[k]]
        quat = d.xquat[self.pelvis[k]]
        R = d.xmat[self.pelvis[k]].reshape(3, 3)
        yaw = yaw_of(quat)
        rv = self.rv[k]
        lin_w = d.qvel[rv:rv + 3]
        foot = []
        for g in self.foot[k]:
            sole = d.geom_xpos[g][2] - np.abs(d.geom_xmat[g].reshape(3, 3)[2]) @ self.m.geom_size[g]
            foot.append(1.0 if sole < 0.005 else 0.0)
        rel = lambda p: heading(p - pos, yaw)
        dyaw = yaw_of(d.xquat[self.pelvis[o]]) - yaw
        ring = heading(np.array([-pos[0], -pos[1], 0.0]), yaw)[:2] / self.ring_half
        return np.concatenate([
            R.T @ lin_w, d.qvel[rv + 3:rv + 6], R.T @ np.array([0.0, 0.0, -1.0]),
            d.qpos[self.jq[k]] - self.default[k], d.qvel[self.jv[k]], self.last[k],
            foot, [pos[2]],
            rel(d.geom_xpos[self.head[o]]), rel(d.geom_xpos[self.torso[o]]), heading(self.head_vel[k] - lin_w, yaw),
            rel(d.geom_xpos[self.glove[k][0]]), rel(d.geom_xpos[self.glove[k][1]]),
            rel(d.geom_xpos[self.glove[o][0]]), rel(d.geom_xpos[self.glove[o][1]]),
            [math.cos(dyaw), math.sin(dyaw)], ring,
        ]).clip(-100.0, 100.0)

    def step(self, action: np.ndarray) -> list:
        d = self.d
        action = action.clip(-3.0, 3.0)
        self.last = action
        d.ctrl[:] = np.concatenate([(self.default[k] + action[k] * 0.5).clip(self.lo[k], self.hi[k]) for k in range(2)])
        for _ in range(4):
            mujoco.mj_step(self.m, d)
        fell = []
        for k in range(2):
            head = d.geom_xpos[self.head[1 - k]]
            self.head_vel[k] = (head - self.prev_head[k]) / self.dt
            self.prev_head[k] = head
            up = -(d.xmat[self.pelvis[k]].reshape(3, 3).T @ np.array([0.0, 0.0, -1.0]))[2]
            fell.append(d.xpos[self.pelvis[k]][2] < self.stand[k] * 0.6 or up < 0.4)
        return fell


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--a", default="matt")
    ap.add_argument("--b", default="zombie")
    ap.add_argument("--run", default="match")
    ap.add_argument("--seconds", type=float, default=300.0)
    args = ap.parse_args()
    import json
    stem = os.path.join(HERE, "models", f"{args.a}_vs_{args.b}")
    ring = Ring(stem + "_spar.xml", json.load(open(stem + "_policy_config.json", encoding="utf-8")))
    ppos, iters = [], 0
    for n in (args.a, args.b):
        ppo = PPO(100, ring.A, 1, "cpu", PPOConfig())
        iters = int(ppo.load(os.path.join(HERE, "checkpoints", args.run, f"latest_{n}.pt")).get("iter", 0))
        ppos.append(ppo)

    ring.reset()
    episodes = falls = steps_in = 0
    down = [0, 0]
    lengths = []
    with torch.no_grad():
        for _ in range(int(args.seconds / ring.dt)):
            act = np.stack([ppos[k].model.actor(ppos[k].obs_rms.normalize(torch.tensor(ring.observe(k), dtype=torch.float32)[None], ppos[k].cfg.obs_clip))[0].numpy() for k in range(2)])
            fell = ring.step(act)
            steps_in += 1
            if any(fell) or steps_in >= 600:
                episodes += 1
                falls += int(any(fell))
                for k in range(2):
                    down[k] += int(fell[k])
                lengths.append(steps_in * ring.dt)
                steps_in = 0
                ring.reset()
    n = max(1, episodes)
    print(f"C MuJoCo {mujoco.__version__}, iteration {iters}, without exploration noise, {args.seconds:g} s:")
    print(f"  {episodes} episodes; stays up {np.mean(lengths) if lengths else args.seconds:.1f} s of an episode; fall rate {falls / n:.2f}; "
          f"{args.a} down in {down[0] / n:.0%} of endings, {args.b} in {down[1] / n:.0%}")


if __name__ == "__main__":
    main()
