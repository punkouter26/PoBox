"""The match on the retrofit's bodies (models/v2): envs/boxing.py's ring, seen through the footwork stage's
observation, so that one policy goes up the whole ladder (house rule: every rung from the one before).

    python train_gauntlet.py --stage match --name matt --resume checkpoints/r2_matt/latest.pt --against ...

The observation is the match's hundred numbers with the three of the command on the end, always zero: a
boxer in a bout is told nothing, it boxes. The pool of cubes is in every v2 model; here the cubes wait where
they are parked (held there every step, as in the footwork stage) and are never thrown: the opponent is
what knocks a boxer about. Each world's body is randomised as in the footwork stage.
"""
from __future__ import annotations

import mujoco
import torch

from .boxing import BoxingEnv
from .footwork import CMD, FootworkEnv


class MatchEnv(BoxingEnv):
    def __init__(self, xml_path: str, num_envs: int, randomise: float = 0.15, **kw):
        self.randomise = randomise
        super().__init__(xml_path, num_envs, **kw)

    def _init_extra(self) -> None:
        m, dev = self.m, self.device
        self.base_obs_dim = self.obs_dim
        self.obs_dim += CMD
        cubes = []
        while mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, f"cube_{len(cubes)}") >= 0:
            cubes.append(mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, f"cube_{len(cubes)}"))
        L = lambda x: torch.tensor(x, device=dev, dtype=torch.long)
        self.C = len(cubes)
        if self.C:
            self.cube_qi = L([[int(m.jnt_qposadr[j]) + i for i in range(7)] for j in cubes])
            self.cube_vi = L([[int(m.jnt_dofadr[j]) + i for i in range(6)] for j in cubes])
            self.cube_park = self.default_qpos[self.cube_qi]
        FootworkEnv._randomise(self)          # the same bodies a world, drawn the same way

    def _observe(self, g) -> torch.Tensor:
        self.obs_dim = self.base_obs_dim
        obs = super()._observe(g)
        self.obs_dim = self.base_obs_dim + CMD
        return torch.cat([obs, torch.zeros(obs.shape[0], CMD, device=self.device)], -1)

    def step(self, action: torch.Tensor):
        if self.C:
            self.qpos[:, self.cube_qi] = self.cube_park[None].expand(self.N, -1, -1)
            self.qvel[:, self.cube_vi] = 0.0
        return super().step(action)
