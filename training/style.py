"""A judge of how a boxer walks: paid for moving like the walking and turning clips.

The judge is a small network shown two piles of moments: the clips' (clips/walk_turn.npz, put on the boxer's
own body) and the boxer's own while it trains. It learns to tell them apart, and the boxer is paid for the
moments the judge takes for a clip's. The policy never sees a clip and gets no new input: the ONNX file is
the same with or without this. (Adversarial motion priors, Peng et al. 2021, cut down.)

A moment is the legs and the trunk only: 15 joint angles and their speeds (abdomen, hips, knees, ankles),
the pelvis's height and velocity in leg lengths, its turn rate, and which way is down. The arms are left
out: a clip's hang and swing, a boxer's are in the guard.

    python style.py        the check: the clips' numbers are the stage's numbers, and the judge can learn

ponytail: one moment at a time, with its velocities, where the paper judges a pair of moments. Judge pairs
if the gait comes out stiff or the judge is fooled by poses that do not follow each other.
"""
from __future__ import annotations

import os
import sys

import mujoco
import numpy as np
import torch

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "tools"))
from ppo import mlp  # noqa: E402
from retarget_clips import place  # noqa: E402

PARTS = ("abdomen", "hip", "knee", "ankle")
FEATURES = 2 * 15 + 1 + 3 + 3 + 3


def clip_features(m: mujoco.MjModel, order: list, path: str = os.path.join(HERE, "clips", "walk_turn.npz"), prefix: str = "a_"):
    """Every moment of every clip on this body: (rows, FEATURES), and the qpos and qvel each row came from."""
    z = np.load(path)
    fps = float(z["fps"])
    J, B = mujoco.mjtObj.mjOBJ_JOINT, mujoco.mjtObj.mjOBJ_BODY
    root_q = int(m.jnt_qposadr[mujoco.mj_name2id(m, J, prefix + "root")])
    root_v = int(m.jnt_dofadr[mujoco.mj_name2id(m, J, prefix + "root")])
    jq = np.array([m.jnt_qposadr[mujoco.mj_name2id(m, J, prefix + n)] for n in order])
    jv = np.array([m.jnt_dofadr[mujoco.mj_name2id(m, J, prefix + n)] for n in order])
    idx = [i for i, n in enumerate(order) if n.split("_")[0] in PARTS]
    leg = sum(np.linalg.norm(m.body_pos[mujoco.mj_name2id(m, B, prefix + n)]) for n in ("shin_l", "foot_l"))
    feats, states = [], []
    q0, q1, v, R = m.qpos0.copy(), m.qpos0.copy(), np.zeros(m.nv), np.zeros(9)
    for name in z.files:
        if name in ("joint_order", "fps", "descriptions"):
            continue
        rows = place(m, z[name], order, prefix)
        for a, b in zip(rows[:-1], rows[1:]):
            q0[root_q:root_q + 7], q0[jq] = a[:7], a[7:]
            q1[root_q:root_q + 7], q1[jq] = b[:7], b[7:]
            mujoco.mj_differentiatePos(m, v, 1.0 / fps, q0, q1)
            quat = a[3:7] / np.linalg.norm(a[3:7])
            mujoco.mju_quat2Mat(R, quat)
            Rm = R.reshape(3, 3)
            yaw = np.arctan2(2.0 * (quat[0] * quat[3] + quat[1] * quat[2]), 1.0 - 2.0 * (quat[2] ** 2 + quat[3] ** 2))
            c, s = np.cos(yaw), np.sin(yaw)
            lin = v[root_v:root_v + 3]
            feats.append(np.concatenate([a[7:][idx], v[jv][idx], [a[2] / leg],
                                         np.array([c * lin[0] + s * lin[1], -s * lin[0] + c * lin[1], lin[2]]) / leg,
                                         v[root_v + 3:root_v + 6], Rm.T @ [0.0, 0.0, -1.0]]))
            states.append((q0.copy(), v.copy()))
    return np.array(feats, dtype=np.float32), states


class Judge:
    def __init__(self, real: np.ndarray, device: str, lr: float = 1e-4, keep: int = 200_000):
        self.device = device
        self.real = torch.tensor(real, device=device)
        self.mean, self.std = self.real.mean(0), self.real.std(0) + 1e-3
        self.real_n = self._n(self.real)
        self.net = mlp(real.shape[1], (256, 128), 1).to(device)
        self.opt = torch.optim.Adam(self.net.parameters(), lr=lr)
        self.keep = keep
        self.seen = []

    def _n(self, x: torch.Tensor) -> torch.Tensor:
        return ((x - self.mean) / self.std).clamp(-10.0, 10.0)

    def _d(self, x: torch.Tensor) -> torch.Tensor:
        return self.net(self._n(x)).squeeze(-1)

    @torch.no_grad()
    def reward(self, feat: torch.Tensor) -> torch.Tensor:
        """1 for a moment the judge takes for a clip's, 0 for one it is sure is not."""
        return (1.0 - 0.25 * (self._d(feat) - 1.0) ** 2).clamp_min(0.0)

    def show(self, feat: torch.Tensor) -> None:
        """The boxer's own moments from this stretch of training, for the next update."""
        self.seen.append(feat.detach())

    def update(self, rounds: int = 4, batch: int = 4096) -> dict:
        fake = torch.cat(self.seen)[-self.keep:] if self.seen else None
        self.seen = []
        if fake is None or len(fake) == 0:
            return {}
        out = {}
        for _ in range(rounds):
            r = self.real_n[torch.randint(0, len(self.real_n), (batch,), device=self.device)].requires_grad_(True)
            f = fake[torch.randint(0, len(fake), (batch,), device=self.device)]
            d_r, d_f = self.net(r).squeeze(-1), self._d(f)
            # Least squares: a clip is 1, the boxer is -1; and the judge is kept smooth round the clips
            # (its slope taken in the scaled numbers it reads: in metres and radians, a number that hardly
            # varies, like which way is down, would be all of the penalty).
            slope = torch.autograd.grad(d_r.sum(), r, create_graph=True)[0]
            loss = ((d_r - 1.0) ** 2).mean() + ((d_f + 1.0) ** 2).mean() + 5.0 * (slope ** 2).sum(-1).mean()
            self.opt.zero_grad(set_to_none=True)
            loss.backward()
            self.opt.step()
            out = {"judge_loss": loss.item(), "judge_on_clips": d_r.mean().item(), "judge_on_boxer": d_f.mean().item()}
        return out

    def state_dict(self) -> dict:
        return {"net": self.net.state_dict(), "opt": self.opt.state_dict()}

    def load_state_dict(self, sd: dict) -> None:
        self.net.load_state_dict(sd["net"])
        self.opt.load_state_dict(sd["opt"])


def demo() -> None:
    import json
    from envs.footwork import FootworkEnv
    xml = os.path.join(HERE, "models", "v2", "matt_solo.xml")
    cfg = json.load(open(os.path.join(HERE, "models", "v2", "matt_policy_config.json"), encoding="utf-8"))
    env = FootworkEnv(xml, 16, device="cpu", seed=1, obs_noise=0.0, disturb=0.0, randomise=0.0)
    real, states = clip_features(env.m, cfg["joint_order"])
    assert real.shape[1] == FEATURES
    # 1. The stage, put in a clip's state, reads the clip's numbers.
    worst = 0.0
    for i in range(0, len(states), max(1, len(states) // 16))[:16]:
        q, v = states[i]
        env.qpos[0] = torch.tensor(q, dtype=torch.float32)
        env.qvel[0] = torch.tensor(v, dtype=torch.float32)
        env._physics_forward()
        worst = max(worst, float((env.style_features()[0] - torch.tensor(real[i])).abs().max()))
    print(f"{len(real)} moments from the clips; the stage's reading of 16 of them differs by at most {worst:.1e}")
    assert worst < 1e-3
    # 2. The judge learns to tell the clips from a boxer standing in its guard being pushed about by noise.
    env.reset()
    judge = Judge(real, "cpu", lr=1e-3)
    own = []
    for _ in range(40):
        env.step(torch.randn(16, env.A) * 0.3)
        own.append(env.style_features())
    for _ in range(60):
        judge.show(torch.cat(own))
        stats = judge.update(batch=1024)
    on_clips, on_boxer = judge.reward(judge.real).mean().item(), judge.reward(torch.cat(own)).mean().item()
    print(f"after 240 updates the judge pays a clip's moment {on_clips:.2f} and the standing boxer's {on_boxer:.2f}; {stats}")
    assert on_clips > 0.6 > on_boxer + 0.3


if __name__ == "__main__":
    demo()
