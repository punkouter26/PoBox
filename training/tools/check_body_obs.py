"""Check: envs/match.py's three body numbers (torch, training) equal tools/melee_exam.py's formula (numpy, the exams and the
game's spec) on the same states, and are zero with no body within 3 m. CPU (Warp on the CPU), a few rings.

    CUDA_VISIBLE_DEVICES=-1 python tools/check_body_obs.py
"""
from __future__ import annotations

import math
import os
import sys

import numpy as np
import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from envs.match import MatchEnv, BODY_PELVIS_X  # noqa: E402


def spec(pos, q, mp, mq):
    """The spec, written out plainly for one fighter."""
    yaw = math.atan2(2 * (q[0] * q[3] + q[1] * q[2]), 1 - 2 * (q[2] ** 2 + q[3] ** 2))
    best, bd = None, 1e9
    for p, r in zip(mp, mq):
        yb = math.atan2(2 * (r[0] * r[3] + r[1] * r[2]), 1 - 2 * (r[2] ** 2 + r[3] ** 2))
        pel = p + np.array([math.cos(yb) * BODY_PELVIS_X, math.sin(yb) * BODY_PELVIS_X, 0.0])
        dist = np.linalg.norm((pel - pos)[:2])
        if dist < bd:
            best, bd = pel, dist
    if bd > 3.0:
        return np.zeros(3)
    v = best - pos
    c, s = math.cos(yaw), math.sin(yaw)
    return np.array([np.clip(c * v[0] + s * v[1], -3, 3), np.clip(-s * v[0] + c * v[1], -3, 3), 1.0])


def main() -> None:
    env = MatchEnv(os.path.join(HERE, "models", "v2", "matt_vs_zombie_spar.xml"), 16, device="cpu", cuda_graph=False, bodies=2, obs_bodies=True)
    assert env.obs_dim == 106
    seen = 0
    for _ in range(3):
        obs = env.reset().reshape(env.N, 2, 106).numpy()
        pos, quat = env.xpos[:, env.pelvis].numpy(), env.xquat[:, env.pelvis].numpy()
        mp, mq = env.mocap_pos.numpy(), env.mocap_quat.numpy()
        for n in range(env.N):
            for k in range(2):
                want = spec(pos[n, k], quat[n, k], mp[n], mq[n])
                assert np.allclose(obs[n, k, 103:], want, atol=1e-4), (obs[n, k, 103:], want)
                seen += int(want[2] > 0)
    # Every body parked away: all zeros.
    env.mocap_pos[:] = torch.tensor([30.0, 30.0, 0.0])
    o = env._observe(env._geometry()).reshape(env.N, 2, 106)
    assert float(o[..., 103:].abs().max()) == 0.0
    print(f"body numbers match the spec on {env.N * 2 * 3} fighter states ({seen} with a body in sight); zeros with none")


if __name__ == "__main__":
    main()
