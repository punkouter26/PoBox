"""tools/make_handover_bank.py on the retrofit's bodies (models/v2) with a 103-input get-up policy: the states the
get-up policy leaves a boxer in, for its match policy to practise taking over from (train_gauntlet.py --handover).

    python tools/make_handover_v2.py --name matt --ckpt checkpoints/u1_matt_zombie/latest_matt.pt
    -> logs/handover_v2_matt.npz
"""
from __future__ import annotations

import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "tools"))
import envs.cgetup as C  # noqa: E402
import make_handover_bank as B  # noqa: E402
from ppo import PPO, PPOConfig  # noqa: E402

V2 = os.path.join(HERE, "models", "v2")
_join = os.path.join


def join(*parts):
    # envs/cgetup.py reads HERE/models/NAME_bag.xml and its config; this line's are in models/v2.
    if len(parts) == 3 and parts[0] == C.HERE and parts[1] == "models":
        return _join(V2, parts[2])
    return _join(*parts)


class PPO103(PPO):
    def __init__(self, obs_dim, *a, **k):
        super().__init__(103, *a, **k)


_observe = C.CGetUp._observe


def observe(self, g):
    # The match's 100 numbers and the command's 3, zero.
    obs = _observe(self, g)
    return np.concatenate([obs, np.zeros((obs.shape[0], 3), obs.dtype)], -1)


if __name__ == "__main__":
    C.os.path.join = join
    C.CGetUp._observe = observe
    B.PPO = PPO103
    if "--out" not in sys.argv:
        name = sys.argv[sys.argv.index("--name") + 1]
        sys.argv += ["--out", _join(HERE, "logs", f"handover_v2_{name}.npz")]
    B.main()
