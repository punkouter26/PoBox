"""Standing, walking and turning: the three rungs under the boxing, as one stage.

    python train_box.py --stage footwork --xml models/v2/matt_solo.xml --run-name r0_matt --resume widened.pt

The model is a boxer alone on the floor with the pool of cubes (rig_to_mjcf.py --cubes). An episode is one
of three kinds, drawn at the reset:

    stand   no command; an opponent's stand-in a step or two in front. Stay up, in the guard, facing it.
    walk    a commanded velocity (forward, sideways, turn rate; in the boxer's own heading), changed every
            few seconds. The stand-in is carried along dead ahead, so it says nothing.
    turn    no command; the stand-in anywhere round the boxer. Turn to face it.

The observation is the match's hundred numbers and the command, 103, so a match policy widened with zero
weights (tools/widen_policy.py) is the starting point (house rule: a new skill is trained from a warm start).

What knocks the boxer about, in every kind of episode:
  * shoves: a force on the trunk for 0.1 to 0.2 s, 10 to 30 N s, from any side;
  * cubes from the pool: thrown at the trunk from two or three metres at 3 to 8 m/s, or dropped on it from
    above. A waiting cube is held at its parking place every step; one in play is left to the physics for
    two seconds. The game throws its cubes the same way, by writing qpos and qvel;
  * a body that is not quite the one in the file: each world has its own link masses, friction, contact
    softness (MuJoCo has no restitution; this is what a bounce is made of), joint damping and drive gains,
    each within `randomise` of the model's. They are drawn once, when the worlds are made.
A frail boxer's shoves are scaled by its strength.
"""
from __future__ import annotations

import math
from typing import Dict

import mujoco
import numpy as np
import torch
import warp as wp

from .boxing import BoxingEnv, quat_mul, quat_yaw, to_heading, yaw_quat

STAND, WALK, TURN = 0, 1, 2
CMD = 3                      # numbers of command on the end of the observation


class FootworkEnv(BoxingEnv):
    TERMS = ["alive", "upright", "height", "track", "yaw", "face", "steps", "arms", "legs", "stance", "lin_z", "ang", "act",
             "rate", "energy", "qvel", "limit", "slip", "lean", "fall", "style"]
    MODE = "footwork"

    def __init__(self, xml_path: str, num_envs: int, walk_share: float = 0.5, turn_share: float = 0.25,
                 disturb: float = 1.0, randomise: float = 0.15, shove_every_s: float = 3.0, cube_every_s: float = 3.0,
                 rsi: float = 0.0, **kw):
        self.walk_share, self.turn_share = walk_share, turn_share
        # The share of walks that begin at a moment of a walking clip (style.py), legs and trunk as the clip has
        # them and the arms in the guard, with the clip's own velocity as the command: a boxer that has only ever
        # stood does not find walking by trying things at random, but one put in mid-stride learns to carry on.
        self.rsi = rsi
        self.disturb, self.randomise = disturb, randomise
        self.shove_every_s, self.cube_every_s = shove_every_s, cube_every_s
        kw["push_vel"] = 0.0          # shoves here are forces, not jumps in velocity
        kw["daze"] = False
        kw.setdefault("nconmax", 96)  # cubes on the floor and on the boxer
        kw.setdefault("njmax", 384)
        super().__init__(xml_path, num_envs, **kw)

    # ---- set-up --------------------------------------------------------------------------------
    def _init_extra(self) -> None:
        N, K, dev, m = self.N, self.K, self.device, self.m
        z = lambda *shape: torch.zeros(*shape, device=dev)
        L = lambda x: torch.tensor(x, device=dev, dtype=torch.long)
        self.base_obs_dim = self.obs_dim
        self.obs_dim += CMD
        self.kind = torch.zeros(N, K, dtype=torch.long, device=dev)
        self.cmd = z(N, K, CMD)
        self.cmd_left = z(N, K)                          # seconds until a walk's command changes
        self.opp_xy = z(N, K, 2)                         # where the stand-in opponent stands
        self.faced_at = torch.full((N, K), -1.0, device=dev)   # seconds into the episode it first faced the stand-in
        self.frail = torch.tensor([min(1.0, float(c.get("strength", 1.0))) for c in self.cfgs], device=dev)
        self.quick = torch.tensor([float(c.get("speed", 1.0)) for c in self.cfgs], device=dev)
        order = self.cfg["joint_order"]
        self.arm_idx = L([i for i, n in enumerate(order) if n.split("_")[0] in ("abdomen", "shoulder", "elbow")])
        self.style_idx = L([i for i, n in enumerate(order) if n.split("_")[0] in ("abdomen", "hip", "knee", "ankle")])
        name = lambda k, part: mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, ("a_", "b_")[k] + part)
        self.leg_len = torch.tensor([float(np.linalg.norm(m.body_pos[name(k, "shin_l")]) + np.linalg.norm(m.body_pos[name(k, "foot_l")]))
                                     for k in range(K)], device=dev)

        self.xfrc = wp.to_torch(self.dw.xfrc_applied)    # (N, nbody, 6): force, then torque
        B, J = mujoco.mjtObj.mjOBJ_BODY, mujoco.mjtObj.mjOBJ_JOINT
        self.b_torso = [mujoco.mj_name2id(m, B, p + "torso") for p in ("a_", "b_")[:K]]
        self.shove_left = z(N, K)
        self.shove_force = z(N, K, 3)
        self.air = z(N, K, 2)                            # seconds each foot has been off the floor
        self.was_down = torch.ones(N, K, 2, dtype=torch.bool, device=dev)

        cubes = []
        while mujoco.mj_name2id(m, J, f"cube_{len(cubes)}") >= 0:
            cubes.append(mujoco.mj_name2id(m, J, f"cube_{len(cubes)}"))
        self.C = len(cubes)
        if self.C:
            self.cube_qi = L([[int(m.jnt_qposadr[j]) + i for i in range(7)] for j in cubes])     # (C, 7)
            self.cube_vi = L([[int(m.jnt_dofadr[j]) + i for i in range(6)] for j in cubes])      # (C, 6)
            self.cube_park = self.default_qpos[self.cube_qi]
            self.cube_left = z(N, self.C)                # seconds a cube in play has left
            self.next_cube = torch.zeros(N, dtype=torch.long, device=dev)
        if self.rsi > 0.0 and K == 1:
            from style import clip_features
            feats, states = clip_features(m, order)
            self.clip_q = torch.tensor(np.stack([q for q, _ in states]), device=dev, dtype=torch.float32)
            self.clip_v = torch.tensor(np.stack([v for _, v in states]), device=dev, dtype=torch.float32)
            f = torch.tensor(feats, device=dev)
            leg = float(self.leg_len[0])
            self.clip_cmd = torch.stack([(f[:, 31] * leg).clamp(-0.4, 1.0), (f[:, 32] * leg).clamp(-0.4, 0.4), f[:, 36].clamp(-1.0, 1.0)], -1)
        else:
            self.rsi = 0.0
        self._randomise()

        self._f = {k: torch.zeros((), device=dev) for k in
                   ("steps", "done_n", "ret_sum", "len_sum", "fell_sum", "up_sum", "power_sum", "walk_n", "track_err",
                    "speed", "asked", "yaw_err", "turn_ends", "turn_ok", "turn_t", "turn_n", "shoves", "cubes", "sat_sum")}
        self._ft = {t: torch.zeros((), device=dev) for t in self.TERMS}
        self._fk = {k: torch.zeros(3, device=dev) for k in ("ends", "fell")}     # by kind of episode

    def _randomise(self) -> None:
        """A body for each world. MuJoCo Warp keeps one copy of a model field unless it is given one a world."""
        f, N = self.randomise, self.N
        if f <= 0.0:
            return
        rng = np.random.default_rng(int(torch.randint(0, 2 ** 31 - 1, (1,), generator=self.rng, device=self.device).item()))
        u = lambda *shape: rng.uniform(1.0 - f, 1.0 + f, shape).astype(np.float32)

        def spread(name: str, factor: np.ndarray) -> None:
            field = getattr(self.mw, name)
            setattr(self.mw, name, wp.array(np.repeat(field.numpy(), N, 0) * factor, dtype=field.dtype))

        link = u(N, self.m.nbody)
        spread("body_mass", link)
        spread("body_inertia", link[..., None])
        spread("geom_friction", u(N, 1, 1) * np.ones((1, self.m.ngeom, 3), np.float32))
        spread("geom_solref", np.concatenate([u(N, 1, 1), np.ones((N, 1, 1), np.float32)], -1) * np.ones((1, self.m.ngeom, 1), np.float32))
        spread("dof_damping", u(N, self.m.nv))
        # A position drive is gain kp and bias (0, -kp, -kv): stiffness and damping each get their own factor.
        kp, kv = u(N, self.m.nu), u(N, self.m.nu)
        gain = np.ones((N, self.m.nu, self.mw.actuator_gainprm.numpy().shape[-1]), np.float32)
        bias = gain.copy()
        gain[..., 0], bias[..., 1], bias[..., 2] = kp, kp, kv
        spread("actuator_gainprm", gain)
        spread("actuator_biasprm", bias)

    # ---- reset ---------------------------------------------------------------------------------
    def _draw_cmd(self, shape) -> torch.Tensor:
        """A walk's command: up to 1 m/s forward, 0.4 back or sideways, 1 rad/s of turn; one in five is 'stand'."""
        c = torch.stack([self._u(*shape, lo=-0.4, hi=1.0), self._u(*shape, lo=-0.4, hi=0.4), self._u(*shape, lo=-1.0, hi=1.0)], -1)
        return c * (self._u(*shape) > 0.2).float()[..., None] * self.quick.view(1, self.K, 1)

    def _reset_states(self):
        N, K, A, dev = self.N, self.K, self.A, self.device
        q = self.default_qpos.unsqueeze(0).repeat(N, 1)
        v = torch.zeros(N, self.qvel.shape[1], device=dev)
        pick = self._u(N, K)
        self._new_kind = torch.where(pick < self.walk_share, WALK, torch.where(pick < self.walk_share + self.turn_share, TURN, STAND))
        self._new_cmd = self._draw_cmd((N, K)) * (self._new_kind == WALK).float()[..., None]
        opp = []
        for k in range(K):
            rq, rv = self.root_q[k], self.root_v[k]
            spot = self._u(N, 2, lo=-1.5, hi=1.5)
            yaw = self._u(N, lo=-math.pi, hi=math.pi)
            q[:, rq:rq + 2] = spot
            q[:, rq + 2] = self.stand_height[k] + 0.002
            q[:, rq + 3:rq + 7] = yaw_quat(yaw)
            q[:, self.jq[k]] = self.default_joint[k] + self._u(N, A, lo=-0.06, hi=0.06)
            v[:, rv:rv + 2] = self._u(N, 2, lo=-0.15, hi=0.15)
            # In front for a stand, anywhere for a turn. (A walk's is put ahead of the boxer every step.)
            turn = self._new_kind[:, k] == TURN
            bearing = yaw + torch.where(turn, self._u(N, lo=-math.pi, hi=math.pi), self._u(N, lo=-0.4, hi=0.4))
            dist = torch.where(turn, self._u(N, lo=0.9, hi=2.0), self._u(N, lo=1.0, hi=1.6))
            opp.append(spot + torch.stack([torch.cos(bearing), torch.sin(bearing)], -1) * dist[:, None])
            if self.rsi > 0.0:
                clip = (self._new_kind[:, k] == WALK) & (self._u(N) < self.rsi)
                at = torch.randint(0, self.clip_q.shape[0], (N,), device=dev, generator=self.rng)
                cq, cv = self.clip_q[at], self.clip_v[at]
                turn = yaw - quat_yaw(cq[:, rq + 3:rq + 7])           # the clip turned to face the episode's way
                c1 = clip[:, None]
                q[:, rq + 2] = torch.where(clip, cq[:, rq + 2], q[:, rq + 2])
                q[:, rq + 3:rq + 7] = torch.where(c1, quat_mul(yaw_quat(turn), cq[:, rq + 3:rq + 7]), q[:, rq + 3:rq + 7])
                c, s_ = torch.cos(turn), torch.sin(turn)
                lin = torch.stack([c * cv[:, rv] - s_ * cv[:, rv + 1], s_ * cv[:, rv] + c * cv[:, rv + 1], cv[:, rv + 2]], -1)
                v[:, rv:rv + 3] = torch.where(c1, lin, v[:, rv:rv + 3])
                v[:, rv + 3:rv + 6] = torch.where(c1, cv[:, rv + 3:rv + 6], v[:, rv + 3:rv + 6])
                legs = self.jq[k][self.style_idx]
                q[:, legs] = torch.where(c1, cq[:, legs], q[:, legs])
                v[:, self.jv[k][self.style_idx]] = torch.where(c1, cv[:, self.jv[k][self.style_idx]], v[:, self.jv[k][self.style_idx]])
                self._new_cmd[:, k] = torch.where(c1, self.clip_cmd[at] * self.quick[k], self._new_cmd[:, k])
        self._new_opp = torch.stack(opp, 1)
        return q, v

    def _apply_reset(self, mask: torch.Tensor) -> None:
        super()._apply_reset(mask)          # the keyframe has every cube at its parking place
        m1, m3 = mask[:, None], mask[:, None, None]
        self.kind = torch.where(m1, self._new_kind, self.kind)
        self.cmd = torch.where(m3, self._new_cmd, self.cmd)
        self.cmd_left = torch.where(m1, self._u(self.N, self.K, lo=2.0, hi=4.0), self.cmd_left)
        self.opp_xy = torch.where(m3, self._new_opp, self.opp_xy)
        self.faced_at = torch.where(m1, torch.full_like(self.faced_at, -1.0), self.faced_at)
        self.shove_left = torch.where(m1, torch.zeros_like(self.shove_left), self.shove_left)
        self.air = torch.where(m1[..., None], torch.zeros_like(self.air), self.air)
        self.was_down = torch.where(m1[..., None], torch.ones_like(self.was_down), self.was_down)
        if self.C:
            self.cube_left = torch.where(m1, torch.zeros_like(self.cube_left), self.cube_left)

    # ---- what the boxer is shown ---------------------------------------------------------------
    def _geometry(self) -> Dict[str, torch.Tensor]:
        # ponytail: with no bag in the model the base class reads the last site and geom for one (index -1)
        # and the lines below overwrite all of it. Give boxing.py a "no target" case when it is free to edit.
        g = super()._geometry()
        pos, yaw = g["pos"], g["yaw"]
        ahead = pos[..., :2] + torch.stack([torch.cos(yaw), torch.sin(yaw)], -1) * 1.2
        opp = torch.where((self.kind == WALK)[..., None], ahead, self.opp_xy)
        their_yaw = torch.atan2(pos[..., 1] - opp[..., 1], pos[..., 0] - opp[..., 0])      # it faces the boxer
        c, s = torch.cos(their_yaw), torch.sin(their_yaw)

        def place(local: torch.Tensor, stand: torch.Tensor) -> torch.Tensor:
            """A point given in a guard's own frame, put in the world at the stand-in's spot."""
            x = opp[..., 0] + c * local[..., 0] - s * local[..., 1]
            y = opp[..., 1] + s * local[..., 0] + c * local[..., 1]
            return torch.stack([x, y, (stand + local[..., 2]).expand_as(x)], -1)

        other = lambda t: t.flip(0)        # the stand-in has the other boxer's shape; alone, the boxer's own
        stand = other(self.stand_height).view(1, self.K)
        g["t_head"] = place(other(self.guard_head)[None], stand)
        g["t_body"] = place(other(self.guard_body)[None], stand)
        og = other(self.guard_glove)
        g["opp_glove"] = torch.stack([place(og[None, :, i], stand) for i in range(2)], 2)
        g["opp_yaw"] = their_yaw
        return g

    def _observe(self, g) -> torch.Tensor:
        N, K = self.N, self.K
        self.obs_dim = self.base_obs_dim            # the base class shapes its hundred by this
        obs = super()._observe(g).reshape(N, K, self.base_obs_dim).clone()
        self.obs_dim = self.base_obs_dim + CMD
        # The base class shows a lone boxer zeros for its opponent's gloves and facing, and its place in a
        # ring that is not there. The stand-in has gloves and a facing; the ring is held to its own size.
        pos, yaw = g["pos"], g["yaw"]
        dyaw = g["opp_yaw"] - yaw
        obs[..., 90:96] = to_heading(g["opp_glove"] - pos[:, :, None], yaw[:, :, None]).reshape(N, K, 6)
        obs[..., 96:98] = torch.stack([torch.cos(dyaw), torch.sin(dyaw)], -1)
        obs[..., 98:100] = obs[..., 98:100].clamp(-1.0, 1.0)
        return torch.cat([obs, self.cmd], -1).reshape(N * K, self.obs_dim)

    # ---- what the judge of its walk is shown ----------------------------------------------------
    def style_features(self) -> torch.Tensor:
        """The legs, the pelvis and the trunk as style.py reads them off the walking clips (the arms are
        in the guard, which no clip is): (N * K, style.FEATURES). Lengths are in leg lengths."""
        N, K = self.N, self.K
        g = self._geometry()
        lin_w, _, ang_b, grav_b = self._base(g)
        jp, jv = self.qpos[:, self.jq][..., self.style_idx], self.qvel[:, self.jv][..., self.style_idx]
        leg = self.leg_len.view(1, K, 1)
        return torch.cat([jp, jv, g["pos"][..., 2:3] / leg, to_heading(lin_w, g["yaw"]) / leg, ang_b, grav_b], -1).reshape(N * K, -1)

    def style_mask(self) -> torch.Tensor:
        """Where a walk is being judged: a walk with somewhere to go, or a turn. (N * K,)"""
        moving = (self.kind == TURN) | ((self.kind == WALK) & (self.cmd.abs().sum(-1) > 0.0))
        return moving.reshape(self.N * self.K)

    # ---- what knocks it about ------------------------------------------------------------------
    def _shove(self) -> None:
        N, K = self.N, self.K
        start = (self._u(N, K) < self.dt / self.shove_every_s) & (self.shove_left <= 1e-6) & (self.disturb > 0.0)
        ang, secs = self._u(N, K, lo=-math.pi, hi=math.pi), self._u(N, K, lo=0.1, hi=0.2)
        newtons = self._u(N, K, lo=10.0, hi=30.0) * self.disturb * self.frail.view(1, K) / secs
        force = torch.stack([torch.cos(ang), torch.sin(ang), torch.zeros_like(ang)], -1) * newtons[..., None]
        self.shove_force = torch.where(start[..., None], force, self.shove_force)
        self.shove_left = torch.where(start, secs, (self.shove_left - self.dt).clamp_min(0.0))
        for k in range(K):
            self.xfrc[:, self.b_torso[k], :3] = self.shove_force[:, k] * (self.shove_left[:, k] > 1e-6).float()[:, None]
        self._f["shoves"] += start.float().sum()

    def _cubes(self) -> None:
        if not self.C:
            return
        N, dev = self.N, self.device
        # A waiting cube is put back at its parking place every step: it is not resting on anything.
        self.cube_left = (self.cube_left - self.dt).clamp_min(0.0)
        waiting = (self.cube_left <= 0.0)[..., None]
        self.qpos[:, self.cube_qi] = torch.where(waiting, self.cube_park[None], self.qpos[:, self.cube_qi])
        self.qvel[:, self.cube_vi] = torch.where(waiting, torch.zeros_like(self.qvel[:, self.cube_vi]), self.qvel[:, self.cube_vi])
        if self.disturb <= 0.0:
            return
        c = self.next_cube
        rows = torch.nonzero((self._u(N) < self.dt / self.cube_every_s) & (self.cube_left.gather(1, c[:, None])[:, 0] <= 0.0))[:, 0]
        if rows.numel() == 0:
            return
        n, c = rows.numel(), c[rows]
        k = c % self.K                                          # whose turn it is to be thrown at
        chest = self.xpos[rows, self.pelvis[k]] + torch.tensor([0.0, 0.0, 0.25], device=dev)
        u = lambda lo, hi: self._u(n, lo=lo, hi=hi)
        # Thrown: from two or three metres, any side, on the arc that arrives at the chest.
        b, dist, speed = u(-math.pi, math.pi), u(2.0, 3.0), u(3.0, 8.0) * max(0.3, self.disturb)
        away = torch.stack([torch.cos(b), torch.sin(b)], -1)
        rise = u(-0.2, 0.4)
        t = dist / speed
        start = chest + torch.cat([away * dist[:, None], rise[:, None]], -1)
        vel = torch.cat([-away * speed[:, None], (-rise / t + 0.5 * 9.81 * t)[:, None]], -1)
        # Dropped: let go a metre or two above it.
        drop = (self._u(n) < 0.3)[:, None]
        start = torch.where(drop, chest + torch.stack([u(-0.15, 0.15), u(-0.15, 0.15), u(1.2, 2.0)], -1), start)
        vel = torch.where(drop, torch.zeros_like(vel), vel)
        quat = torch.tensor([1.0, 0.0, 0.0, 0.0], device=dev).expand(n, 4)
        self.qpos[rows[:, None], self.cube_qi[c]] = torch.cat([start, quat], -1)
        self.qvel[rows[:, None], self.cube_vi[c]] = torch.cat([vel, self._u(n, 3, lo=-5.0, hi=5.0)], -1)
        self.cube_left[rows, c] = 2.0
        self.next_cube[rows] = (c + 1) % self.C
        self._f["cubes"] += float(n)

    # ---- step ----------------------------------------------------------------------------------
    def step(self, action: torch.Tensor):
        N, K, A = self.N, self.K, self.A
        action = action.clamp(-self.action_clip, self.action_clip).reshape(N, K, A)
        self.prev_action = self.last_action
        self.last_action = action
        self._drive(action)
        self._shove()
        self._cubes()
        self._physics_step()
        self.step_count = self.step_count + 1.0

        g = self._geometry()
        lin_w, lin_b, ang_b, grav_b = self._base(g)
        pos, yaw = g["pos"], g["yaw"]
        upright = -grav_b[..., 2]
        self.head_vel = (g["t_head"] - self.prev_head) / self.dt

        stand, walk, turn = (self.kind == STAND).float(), (self.kind == WALK).float(), (self.kind == TURN).float()
        still = (self.cmd.abs().sum(-1) == 0.0).float() * (1.0 - turn)        # asked to stand where it is
        vel = to_heading(lin_w, yaw)[..., :2]
        v_err = (vel - self.cmd[..., :2]).norm(dim=-1)
        w_err = (ang_b[..., 2] - self.cmd[..., 2]).abs()
        to_t = g["t_body"] - pos
        off = torch.atan2(to_t[..., 1], to_t[..., 0]) - yaw
        off = torch.atan2(torch.sin(off), torch.cos(off)).abs()             # how far it is from facing the stand-in
        contact = g["foot_contact"]
        moving_now = (1.0 - still) * (1.0 - turn) * (self.cmd[..., :2].norm(dim=-1) > 0.1).float()
        foot_v = (g["foot_xy"] - self.prev_foot_xy) / self.dt
        foot_sep = (g["foot_xy"][:, :, 0] - g["foot_xy"][:, :, 1]).norm(dim=-1)
        jp = self.qpos[:, self.jq]
        jv = self.qvel[:, self.jv]
        tau = self.act_force.reshape(N, K, A)
        away = jp - self.default_joint

        r = {}
        r["alive"] = torch.full((N, K), 0.15, device=self.device)
        r["upright"] = 0.25 * upright.clamp_min(0.0)
        r["height"] = -1.0 * (self.stand_height - 0.08 - pos[..., 2]).clamp_min(0.0)
        # Two widths: the wide one can be felt from a standstill when 1 m/s is asked (the narrow one alone is
        # flat there: exp(-16)), the narrow one is what makes 0.15 m/s of error cost something.
        # Worth twice as much when the boxer is asked to go somewhere: walking has to pay more than standing,
        # and what it costs (below) is charged at less than standing's rate.
        r["track"] = (1.0 + moving_now) * (0.5 * torch.exp(-(v_err / 0.5) ** 2) + 0.5 * torch.exp(-(v_err / 0.2) ** 2))
        r["yaw"] = 0.5 * torch.exp(-(w_err / 0.5) ** 2) * (1.0 - turn)
        # Two widths again: the cosine is felt from behind; the narrow one (0.2 rad) is what makes 20 degrees off
        # cost something. With the cosine alone a policy settled 15 to 30 degrees off (R2's exam: 73%, mark 90%).
        r["face"] = (0.25 * stand + 0.6 * turn) * (torch.cos(off) + torch.exp(-(off / 0.2) ** 2))
        # A step is a foot that leaves the floor and comes down again. Paid when it lands, for every tenth of a
        # second it was up beyond the first quarter, and only when going somewhere: a boxer standing still that
        # learned to stand is slow to learn that walking means lifting a foot, and shuffling is charged (slip).
        self.air = self.air + self.dt * (~contact).float()
        landed = contact & ~self.was_down
        r["steps"] = 1.0 * ((self.air - 0.25) * landed.float()).sum(-1) * moving_now
        self.air = torch.where(contact, torch.zeros_like(self.air), self.air)
        self.was_down = contact
        # The guard stays up whatever the legs are doing; the legs go back to the stance when nothing is asked.
        r["arms"] = -0.2 * (away[..., self.arm_idx] ** 2).mean(-1)
        r["legs"] = -0.03 * still * (away[..., self.leg_idx] ** 2).sum(-1)
        r["stance"] = -0.2 * still * (foot_sep - 0.30).abs()
        r["lin_z"] = -0.3 * lin_b[..., 2] ** 2
        r["ang"] = -0.02 * (ang_b[..., :2] ** 2).sum(-1)
        r["act"] = -0.001 * (action ** 2).sum(-1)
        r["rate"] = -0.01 * ((action - self.prev_action) ** 2).sum(-1)
        # A boxer's charges, for standing and for punching. A walker's legs do work every step: charged at full
        # rate it cost more than walking earned, and the first three walking runs learned to stand (2 October).
        easy = 1.0 - 0.7 * moving_now
        r["energy"] = -2.5e-4 * easy * (tau * jv).abs().clamp_max(2000.0).sum(-1)
        # House rule: joints move no faster than a person's.
        r["qvel"] = -0.1 * (jv.abs() - self.qvel_limit).clamp(0.0, 10.0).pow(2).sum(-1)
        r["limit"] = -0.5 * ((self.joint_lo + 0.05 - jp).clamp_min(0.0) + (jp - self.joint_hi + 0.05).clamp_min(0.0)).sum(-1)
        r["slip"] = -0.2 * easy * ((foot_v ** 2).sum(-1).clamp_max(25.0) * contact.float()).sum(-1)
        r["lean"] = -1.5 * easy * ((g["own_head"][..., :2] - g["foot_xy"].mean(2)).norm(dim=-1) - 0.18).clamp_min(0.0)
        # Moving like a person, judged against the walking and turning clips: paid by the trainer, when it
        # has a judge. Nothing here.
        r["style"] = torch.zeros(N, K, device=self.device)

        fell = (pos[..., 2] < self.stand_height * 0.6) | (upright < 0.4) | ~torch.isfinite(pos).all(-1)
        timeout = self.step_count >= self.max_steps
        any_fell = fell.any(1)
        r["fall"] = -self.fall_penalty * fell.float()
        reward = sum(r.values())
        done = any_fell | timeout

        # ---- bookkeeping
        t_now = (self.episode_len[:, None] + 1.0) * self.dt
        self.faced_at = torch.where((off < math.radians(15.0)) & (self.faced_at < 0.0), t_now.expand(N, K), self.faced_at)
        self.episode_return = self.episode_return + reward
        self.episode_len = self.episode_len + 1.0
        df = done.float()
        a = self._f
        a["steps"] += 1.0
        a["done_n"] += df.sum()
        a["ret_sum"] += (self.episode_return.mean(1) * df).sum()
        a["len_sum"] += (self.episode_len * df).sum()
        a["fell_sum"] += (any_fell.float() * df).sum()
        a["up_sum"] += upright.mean()
        a["power_sum"] += (tau * jv).abs().sum(-1).clamp_max(20000.0).mean()
        a["sat_sum"] += (action.abs() >= 0.99 * self.action_clip).float().mean()
        moving = walk * (1.0 - still)
        a["walk_n"] += moving.sum()
        a["track_err"] += (v_err * moving).sum()
        a["speed"] += (vel.norm(dim=-1) * moving).sum()
        a["asked"] += (self.cmd[..., :2].norm(dim=-1) * moving).sum()
        a["yaw_err"] += (w_err * moving).sum()
        ended = df[:, None] * turn
        a["turn_ends"] += ended.sum()
        a["turn_ok"] += (ended * ((self.faced_at >= 0.0) & (self.faced_at <= 3.0) & ~fell).float()).sum()
        a["turn_t"] += (ended * self.faced_at.clamp_min(0.0)).sum()
        a["turn_n"] += (ended * (self.faced_at >= 0.0).float()).sum()
        for name in self.TERMS:
            self._ft[name] += r[name].mean()
        for kind in range(3):
            of_kind = (self.kind == kind).float() * df[:, None]
            self._fk["ends"][kind] += of_kind.sum()
            self._fk["fell"][kind] += (of_kind * fell.float()).sum()

        # A walk's command changes every few seconds.
        self.cmd_left = self.cmd_left - self.dt
        change = (self.cmd_left <= 0.0) & (self.kind == WALK)
        self.cmd = torch.where(change[..., None], self._draw_cmd((N, K)), self.cmd)
        self.cmd_left = torch.where(change, self._u(N, K, lo=2.0, hi=4.0), self.cmd_left)

        self.prev_glove, self.prev_head, self.prev_body = g["glove"].clone(), g["t_head"].clone(), g["t_body"].clone()
        self.prev_foot_xy = g["foot_xy"].clone()

        self._apply_reset(done)
        self._physics_forward()
        self._reseed_trackers(done)
        self._obs = self._observe(self._geometry())
        expand = lambda x: x[:, None].expand(N, K).reshape(N * K)
        return self._obs, reward.reshape(N * K), expand(done), expand(timeout & ~any_fell)

    def get_stats(self) -> Dict[str, float]:
        a = {k: v.item() for k, v in self._f.items()}
        n, s, w = max(1.0, a["done_n"]), max(1.0, a["steps"]), max(1.0, a["walk_n"])
        seconds = s * self.dt * self.N * self.K
        ends, fell = self._fk["ends"].tolist(), self._fk["fell"].tolist()
        out = {
            "ep_return": a["ret_sum"] / n,
            "ep_len_s": a["len_sum"] / n * self.dt,
            "fall_rate": a["fell_sum"] / n,
            "falls_per_min": a["fell_sum"] / seconds * 60.0,
            "upright": a["up_sum"] / s,
            "power": a["power_sum"] / s,
            "act_sat": a["sat_sum"] / s,
            # Walking: how far the velocity is from the one asked for, m/s; what was asked and what was done.
            "track_err": a["track_err"] / w, "speed": a["speed"] / w, "speed_asked": a["asked"] / w,
            "yaw_err": a["yaw_err"] / w,
            # Turning: the share of turn episodes that faced the stand-in within 15 degrees inside 3 s and stayed up.
            "turn_rate": a["turn_ok"] / max(1.0, a["turn_ends"]),
            "turn_time": a["turn_t"] / max(1.0, a["turn_n"]),
            "stand_fall_rate": fell[STAND] / max(1.0, ends[STAND]),
            "walk_fall_rate": fell[WALK] / max(1.0, ends[WALK]),
            "turn_fall_rate": fell[TURN] / max(1.0, ends[TURN]),
            "shoves_per_min": a["shoves"] / seconds * 60.0,
            "cubes_per_min": a["cubes"] / (s * self.dt * self.N) * 60.0,
            # The match stage's headline numbers, which mean nothing here, so that anything reading a
            # status file finds them.
            "hits_per_s": 0.0, "head_share": 0.0, "hit_speed": 0.0, "hit_speed_max": 0.0, "distance": 0.0,
            "knockdowns_per_min": 0.0, "glove_speed": 0.0, "blocks_per_s": 0.0, "daze": 0.0,
        }
        out.update({f"rt_{t}": self._ft[t].item() / s for t in self.TERMS})
        for group in (self._f, self._ft, self._fk):
            for v in group.values():
                v.zero_()
        return out
