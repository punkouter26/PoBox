"""The boxing line's stages on the retrofit's bodies (models/v2), seen through the footwork stage's
observation, so that every policy of a boxer reads the same 103 numbers (house rule: every rung from the one
before; and one observation for the game to build).

    python train_gauntlet.py --stage match --models models/v2 --name matt --resume ... --against ...
    python train_getup.py --stage v2 --a matt --b zombie --resume ...

    MatchEnv       envs/boxing.py's match
    GetUpMatchEnv  envs/getup.py's knockdown and getting up

The observation is the stage's own hundred numbers with the three of the command on the end, always zero: a
boxer in a bout, or on the canvas, is told nothing. The pool of cubes is in every v2 model; here the cubes
wait where they are parked (held there every step, as in the footwork stage) and are never thrown: the
opponent is what knocks a boxer about. Each world's body is randomised as in the footwork stage.
"""
from __future__ import annotations

import mujoco
import torch

from .boxing import BoxingEnv, quat_yaw, to_heading
from .footwork import CMD, FootworkEnv
from .getup import GetUpEnv


class _Retrofit:
    """What the retrofit adds to a stage of the boxing line: the command's zeros, the cubes held, a body a world."""

    randomise = 0.15

    def _retrofit(self) -> None:
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
        self.obs_dim = self.base_obs_dim      # the stage shapes its hundred by this
        obs = super()._observe(g)
        self.obs_dim = self.base_obs_dim + CMD
        return torch.cat([obs, torch.zeros(obs.shape[0], CMD, device=self.device)], -1)

    def step(self, action: torch.Tensor):
        if self.C:
            self.qpos[:, self.cube_qi] = self.cube_park[None].expand(self.N, -1, -1)
            self.qvel[:, self.cube_vi] = 0.0
        return super().step(action)


BODY_OBS = 3                 # the Melee (2026-10-19): the nearest downed body, after the 103
BODY_SEE_M = 3.0
# Where a lying body's pelvis is in its own frame (envs/boxing.py lying_bodies: trunk -0.25..0.25 along x, legs from -0.30).
BODY_PELVIS_X = -0.28


class MatchEnv(_Retrofit, BoxingEnv):
    def __init__(self, xml_path: str, num_envs: int, randomise: float = 0.15, obs_bodies: bool = False, **kw):
        self.randomise = randomise
        self.obs_bodies = obs_bodies
        super().__init__(xml_path, num_envs, **kw)

    def _init_extra(self) -> None:
        self._retrofit()

    def _bodies_seen(self, g) -> torch.Tensor:
        """(N, K, 3), the game's exact numbers: [x, y] = to_heading(p - pos, yaw)[:2], each clamped to [-3, 3], p the pelvis
        of the nearest downed body by horizontal distance (never the opponent), and 1.0 if it is within 3 m; else all 0."""
        N, K = self.N, self.K
        if not self.n_bodies:
            return torch.zeros(N, K, BODY_OBS, device=self.device)
        yb = quat_yaw(self.mocap_quat)                                                  # (N, B)
        p = self.mocap_pos.clone()
        p[..., 0] += torch.cos(yb) * BODY_PELVIS_X
        p[..., 1] += torch.sin(yb) * BODY_PELVIS_X
        d = p[:, None] - g["pos"][:, :, None]                                           # (N, K, B, 3)
        dist = d[..., :2].norm(dim=-1)
        j = dist.argmin(-1)                                                             # (N, K)
        near = d.gather(2, j[..., None, None].expand(N, K, 1, 3)).squeeze(2)
        xy = to_heading(near, g["yaw"])[..., :2].clamp(-3.0, 3.0)
        seen = (dist.gather(2, j[..., None]).squeeze(2) <= BODY_SEE_M).float()[..., None]
        return torch.cat([xy, torch.ones_like(seen)], -1) * seen

    def _observe(self, g) -> torch.Tensor:
        """A boxer with obs_state (envs/boxing.py) reads its reserve, its daze and the opponent's in the command's place."""
        obs = super()._observe(g)
        if self.obs_state.any():
            share = self.daze / self.legs_at.clamp_min(1.0)
            state = torch.stack([self.reserve, share, share.flip(1)], -1) * self.obs_state.view(1, self.K, 1)
            obs[:, self.base_obs_dim:] = state.reshape(-1, CMD)
        if self.obs_bodies:
            obs = torch.cat([obs, self._bodies_seen(g).reshape(-1, BODY_OBS)], -1)
            self.obs_dim = self.base_obs_dim + CMD + BODY_OBS
        return obs


class GetUpMatchEnv(_Retrofit, GetUpEnv):
    def __init__(self, xml_path: str, num_envs: int, randomise: float = 0.15, **kw):
        self.randomise = randomise
        super().__init__(xml_path, num_envs, **kw)

    def _init_extra(self) -> None:
        GetUpEnv._init_extra(self)
        self._retrofit()
