"""GPU-vectorised boxing on MuJoCo Warp: one environment for both stages of training.

Stage 1, "bag"   one fighter and a hanging heavy bag. Learn to stand unaided, hold range, and hit hard.
Stage 2, "spar"  two fighters in a ring, both driven by the same policy (self-play). Hit and do not be hit.

Which stage it is comes from the model: a file with a second fighter in it is sparring. Everything else
is shared, and in particular the observation is the same 100 numbers in both, so the policy that learned
on the bag is the policy that starts sparring. What the bag stage calls "the target's head" and "the
target's body" are simply the top and the middle of the bag.

Observation, per fighter (all positions relative to its own pelvis, in its own heading frame):
    base_lin_vel(3) base_ang_vel(3) projected_gravity(3)
    joint_pos - default(21) joint_vel(21) last_action(21)
    foot_contact(2) base_height(1)
    target_head(3) target_body(3) target_head_velocity(3)
    own_gloves(2x3) opponent_gloves(2x3, zeros on the bag)
    opponent_facing(cos, sin; zeros on the bag) position_in_ring(2)
Action: PD joint targets, target = guard pose + action * action_scale (radians), 50 Hz.

A hit is measured from geometry, not from the solver's contact list (reading that back would stall the
GPU every step): a glove that arrives at the target's head or body closing faster than 1 m/s along the
surface normal, having been at least 30 cm clear of it, and a quarter of a second, since its last hit.
Its strength is that closing speed, capped at 9 m/s. A glove resting on the target, sliding over it or
bouncing on it scores once; a target that walks into a still glove scores nothing.
"""
from __future__ import annotations

import json
import math
import os
from typing import Dict, Tuple

import mujoco
import mujoco_warp as mjw
import numpy as np
import torch
import warp as wp


def quat_rotate_inverse(q: torch.Tensor, v: torch.Tensor) -> torch.Tensor:
    """q: (..., 4) wxyz, v: (..., 3) world -> body."""
    w, xyz = q[..., :1], q[..., 1:]
    t = 2.0 * torch.cross(xyz, v, dim=-1)
    return v - w * t + torch.cross(xyz, t, dim=-1)


def quat_yaw(q: torch.Tensor) -> torch.Tensor:
    w, x, y, z = q[..., 0], q[..., 1], q[..., 2], q[..., 3]
    return torch.atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (y * y + z * z))


def yaw_quat(yaw: torch.Tensor) -> torch.Tensor:
    z = torch.zeros_like(yaw)
    return torch.stack([torch.cos(yaw * 0.5), z, z, torch.sin(yaw * 0.5)], dim=-1)


def to_heading(v: torch.Tensor, yaw: torch.Tensor) -> torch.Tensor:
    """Rotate world vectors (..., 3) into the frame that has turned `yaw` about the vertical."""
    c, s = torch.cos(yaw), torch.sin(yaw)
    return torch.stack([c * v[..., 0] + s * v[..., 1], -s * v[..., 0] + c * v[..., 1], v[..., 2]], dim=-1)


class BoxingEnv:
    TERMS = ["alive", "upright", "height", "face", "range", "close", "hit", "taken", "lin_z", "ang", "act",
             "rate", "energy", "qvel", "limit", "slip", "stance", "legs", "lean", "fall", "ko"]

    def __init__(self, xml_path: str, num_envs: int, device: str = "cuda", seed: int = 0,
                 control_decimation: int = 4, episode_len_s: float = 12.0, action_scale: float = 0.5,
                 action_clip: float = 3.0, nconmax: int = 0, njmax: int = 0, cuda_graph: bool = True,
                 obs_noise: float = 0.0, push_vel: float = 0.0, hit_w: float = 0.6, taken_w: float = 0.5,
                 fall_penalty: float = 4.0, ko_bonus: float = 4.0, range_m: float = 0.78, hit_cap: float = 9.0,
                 verbose: bool = False):
        wp.init()
        wp.config.verbose_warnings = verbose
        if device == "cpu":
            # The viewer's case: one world on the CPU, so that watching does not take the GPU from training.
            wp.set_device("cpu")
        self.device = device
        self.N = num_envs
        self.decimation = control_decimation
        self.action_scale = action_scale
        self.action_clip = action_clip
        self.obs_noise = obs_noise
        self.push_vel = push_vel
        self.hit_w, self.taken_w = hit_w, taken_w
        self.fall_penalty, self.ko_bonus = fall_penalty, ko_bonus
        self.range_m = range_m
        self.hit_cap = hit_cap
        # How far a glove has to come back off the target before it can score again: a punch has a
        # wind-up, a pat does not.
        self.rearm_m = 0.30
        self.use_cuda_graph = cuda_graph and device.startswith("cuda")
        self.rng = torch.Generator(device=device)
        self.rng.manual_seed(seed)

        self.m = mujoco.MjModel.from_xml_path(xml_path)
        m = self.m
        self.spar = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "b_pelvis") >= 0
        self.K = 2 if self.spar else 1
        self.mode = "spar" if self.spar else "bag"
        self.dt = float(m.opt.timestep) * control_decimation
        self.episode_len_s = float(episode_len_s)
        self.max_steps = int(episode_len_s / self.dt)

        base = os.path.basename(xml_path)
        stem = base[: base.rfind("_")]
        with open(os.path.join(os.path.dirname(xml_path), stem + "_policy_config.json"), "r", encoding="utf-8") as f:
            cfg = json.load(f)
        # A match between two different bodies carries one config per fighter; everything that depends
        # on the body (guard pose, joint limits, standing height, sizes) is therefore held per fighter.
        self.cfgs = cfg["fighters"] if "fighters" in cfg else [cfg] * self.K
        self.names = list(cfg["names"]) if "names" in cfg else [cfg.get("name", stem)] * self.K
        self.hetero = self.K == 2 and self.names[0] != self.names[1]
        self.cfg = self.cfgs[0]
        order = self.cfg["joint_order"]
        self.A = len(order)
        self.obs_dim = 9 + 3 * self.A + 3 + 9 + 6 + 6 + 2 + 2
        self.ring_half = float(cfg.get("ring_half", self.cfg.get("ring_half", 3.05)))

        d0 = mujoco.MjData(m)
        mujoco.mj_resetDataKeyframe(m, d0, 0)
        mujoco.mj_forward(m, d0)
        self.mw = mjw.put_model(m)
        self.mw.opt.warn_overflow = verbose
        # Two bodies touching each other, the floor and themselves need more room than one runner did.
        nconmax = nconmax or (128 if self.spar else 64)
        njmax = njmax or (512 if self.spar else 256)
        self.dw = mjw.put_data(m, d0, nworld=num_envs, nconmax=nconmax, njmax=njmax)

        self.qpos = wp.to_torch(self.dw.qpos)
        self.qvel = wp.to_torch(self.dw.qvel)
        self.ctrl = wp.to_torch(self.dw.ctrl)
        self.xquat = wp.to_torch(self.dw.xquat)
        self.xpos = wp.to_torch(self.dw.xpos)
        self.site_xpos = wp.to_torch(self.dw.site_xpos)
        self.geom_xpos = wp.to_torch(self.dw.geom_xpos)
        self.geom_xmat = wp.to_torch(self.dw.geom_xmat)
        self.act_force = wp.to_torch(self.dw.actuator_force)

        def ids(kind, fmt_name):
            out = []
            for p in ("a_", "b_")[: self.K]:
                i = mujoco.mj_name2id(m, kind, p + fmt_name)
                if i < 0:
                    raise ValueError(f"model has no {p}{fmt_name}")
                out.append(i)
            return out

        B, G, J = mujoco.mjtObj.mjOBJ_BODY, mujoco.mjtObj.mjOBJ_GEOM, mujoco.mjtObj.mjOBJ_JOINT
        L = lambda x: torch.tensor(x, device=device, dtype=torch.long)
        self.pelvis = L(ids(B, "pelvis"))
        self.g_head = L(ids(G, "head_geom"))
        self.g_torso = L(ids(G, "torso_geom"))
        self.g_glove = L([[mujoco.mj_name2id(m, G, p + f"glove_{s}") for s in "lr"] for p in ("a_", "b_")[: self.K]])
        self.g_foot = L([[mujoco.mj_name2id(m, G, p + f"foot_{s}_geom") for s in "lr"] for p in ("a_", "b_")[: self.K]])
        roots = ids(J, "root")
        self.root_q = [int(m.jnt_qposadr[r]) for r in roots]
        self.root_v = [int(m.jnt_dofadr[r]) for r in roots]
        jq, jv = [], []
        for p in ("a_", "b_")[: self.K]:
            js = [mujoco.mj_name2id(m, J, p + n) for n in order]
            jq.append([int(m.jnt_qposadr[j]) for j in js])
            jv.append([int(m.jnt_dofadr[j]) for j in js])
        self.jq, self.jv = L(jq), L(jv)                       # (K, A)
        # Actuators are written fighter by fighter in joint order, so the control vector is just the
        # flattened (K, A) target. Check rather than assume.
        for k, p in enumerate(("a_", "b_")[: self.K]):
            for i, n in enumerate(order):
                assert mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_ACTUATOR, p + n) == k * self.A + i

        F = lambda rows: torch.tensor(np.array(rows), device=device, dtype=torch.float32)
        K = self.K
        size = m.geom_size
        # Sizes straight from the model, per fighter. "Own" ones are shaped to broadcast over (N, K, glove).
        self.r_glove = F([size[int(self.g_glove[k, 0]), 0] for k in range(K)]).view(1, K, 1)
        self.r_head_k = F([size[int(self.g_head[k]), 0] for k in range(K)])
        self.r_torso_k = F([size[int(self.g_torso[k]), 0] for k in range(K)])
        self.torso_half_k = F([size[int(self.g_torso[k]), 1] for k in range(K)])
        self.foot_half = F([size[int(self.g_foot[k, 0])] for k in range(K)]).view(1, K, 1, 3)

        if not self.spar:
            self.bag_q = int(m.jnt_qposadr[mujoco.mj_name2id(m, J, "bag_swing")])
            self.bag_v = int(m.jnt_dofadr[mujoco.mj_name2id(m, J, "bag_swing")])
            self.s_bag_head = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_SITE, "bag_head")
            self.s_bag_body = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_SITE, "bag_body")
            self.g_bag = mujoco.mj_name2id(m, G, "bag_geom")
            self.r_bag = float(m.geom_size[self.g_bag, 0])

        key = torch.tensor(m.key_qpos[0], device=device, dtype=torch.float32)
        self.default_qpos = key
        self.default_joint = key[self.jq].clone()                        # (K, A)
        self.stand_height = torch.stack([key[q + 2] for q in self.root_q])   # (K,)
        self.joint_lo = F([c["lower"] for c in self.cfgs])               # (K, A)
        self.joint_hi = F([c["upper"] for c in self.cfgs])
        self.qvel_limit = torch.tensor(self.cfg["velocity_limit"], device=device, dtype=torch.float32)
        leg = [i for i, n in enumerate(order) if n.split("_")[0] in ("hip", "knee", "ankle")]
        self.leg_idx = L(leg)

        N, K, A = self.N, self.K, self.A
        z = lambda *shape: torch.zeros(*shape, device=device)
        self.last_action = z(N, K, A)
        self.prev_action = z(N, K, A)
        self.step_count = z(N)
        self.episode_return = z(N, K)
        self.episode_len = z(N)
        self.armed = torch.ones(N, K, 2, dtype=torch.bool, device=device)        # per glove
        self.cool = z(N, K, 2)                                                   # seconds before a glove can score again
        self.prev_closing = z(N, K, 2, 2)
        self.since_hit = torch.full((N, K), 99.0, device=device)                 # seconds since this fighter landed one
        self.prev_glove = z(N, K, 2, 3)
        self.prev_head = z(N, K, 3)
        self.prev_body = z(N, K, 3)
        self.head_vel = z(N, K, 3)                                               # target head velocity, world
        self.prev_foot_xy = z(N, K, 2, 2)
        self.gravity_world = torch.tensor([0.0, 0.0, -1.0], device=device)
        self._acc = {k: torch.zeros((), device=device) for k in
                     ("ret_sum", "len_sum", "fell_sum", "done_n", "steps", "upright_sum", "dist_sum",
                      "hits_head", "hits_body", "strength_sum", "strength_max", "glove_speed", "power_sum",
                      "sat_sum", "ko_sum")}
        self._acc.update({f"rt_{t}": torch.zeros((), device=device) for t in self.TERMS})
        # The same counts kept per fighter, for a match between two different ones.
        self._kacc = {k: torch.zeros(self.K, device=device) for k in ("hits", "speed", "fell", "ko", "ret")}

        self._step_graph = None
        self._fwd_graph = None
        self._capture_graphs()
        self.reset()

    # ---- CUDA graphs ---------------------------------------------------------------------------
    def _capture_graphs(self) -> None:
        if not self.use_cuda_graph:
            return
        try:
            for _ in range(self.decimation):
                mjw.step(self.mw, self.dw)
            mjw.forward(self.mw, self.dw)
            wp.synchronize()
            with wp.ScopedCapture() as cap:
                for _ in range(self.decimation):
                    mjw.step(self.mw, self.dw)
            self._step_graph = cap.graph
            with wp.ScopedCapture() as cap:
                mjw.forward(self.mw, self.dw)
            self._fwd_graph = cap.graph
        except Exception as e:   # capture is an optimisation, never a requirement
            print(f"[cuda-graph] capture unavailable ({e}); falling back to direct launches")
            self._step_graph = self._fwd_graph = None

    def _physics_step(self) -> None:
        if self._step_graph is not None:
            wp.capture_launch(self._step_graph)
        else:
            for _ in range(self.decimation):
                mjw.step(self.mw, self.dw)

    def _physics_forward(self) -> None:
        if self._fwd_graph is not None:
            wp.capture_launch(self._fwd_graph)
        else:
            mjw.forward(self.mw, self.dw)

    # ---- random -------------------------------------------------------------------------------
    def _u(self, *shape, lo: float = 0.0, hi: float = 1.0) -> torch.Tensor:
        return lo + torch.rand(*shape, generator=self.rng, device=self.device) * (hi - lo)

    # ---- reset --------------------------------------------------------------------------------
    def _reset_states(self) -> Tuple[torch.Tensor, torch.Tensor]:
        N, A = self.N, self.A
        q = self.default_qpos.unsqueeze(0).repeat(N, 1)
        v = torch.zeros(N, self.qvel.shape[1], device=self.device)

        if self.spar:
            # Somewhere in the ring, some distance apart, roughly facing each other.
            centre = self._u(N, 2, lo=-1.4, hi=1.4)
            bearing = self._u(N, lo=-math.pi, hi=math.pi)
            sep = self._u(N, lo=0.85, hi=2.2)
            u = torch.stack([torch.cos(bearing), torch.sin(bearing)], -1)
            spots = [centre - u * sep[:, None] * 0.5, centre + u * sep[:, None] * 0.5]
            facing = [bearing, bearing + math.pi]
        else:
            # Somewhere round the bag, within a step or two of punching range, roughly facing it.
            bearing = self._u(N, lo=-math.pi, hi=math.pi)
            dist = self._u(N, lo=0.65, hi=1.35)
            u = torch.stack([torch.cos(bearing), torch.sin(bearing)], -1)
            spots = [-u * dist[:, None]]
            facing = [bearing]
            tilt = self._u(N, 3, lo=-0.06, hi=0.06)
            bq = torch.cat([torch.ones(N, 1, device=self.device), tilt[:, :2], torch.zeros(N, 1, device=self.device)], -1)
            q[:, self.bag_q:self.bag_q + 4] = bq / bq.norm(dim=-1, keepdim=True)
            v[:, self.bag_v:self.bag_v + 2] = self._u(N, 2, lo=-0.3, hi=0.3)

        for k in range(self.K):
            rq, rv = self.root_q[k], self.root_v[k]
            yaw = facing[k] + self._u(N, lo=-0.6, hi=0.6)
            q[:, rq:rq + 2] = spots[k]
            q[:, rq + 2] = self.stand_height[k] + 0.002
            q[:, rq + 3:rq + 7] = yaw_quat(yaw)
            q[:, self.jq[k]] = self.default_joint[k] + self._u(N, A, lo=-0.06, hi=0.06)
            v[:, rv:rv + 2] = self._u(N, 2, lo=-0.15, hi=0.15)
        return q, v

    def _apply_reset(self, mask: torch.Tensor) -> None:
        q, v = self._reset_states()
        m1 = mask[:, None]
        self.qpos.copy_(torch.where(m1, q, self.qpos))
        self.qvel.copy_(torch.where(m1, v, self.qvel))
        ctrl = torch.cat([q[:, self.jq[k]] for k in range(self.K)], dim=-1)
        self.ctrl.copy_(torch.where(m1, ctrl, self.ctrl))
        m3 = mask[:, None, None]
        self.last_action = torch.where(m3, torch.zeros_like(self.last_action), self.last_action)
        self.prev_action = torch.where(m3, torch.zeros_like(self.prev_action), self.prev_action)
        self.step_count = torch.where(mask, torch.zeros_like(self.step_count), self.step_count)
        self.episode_len = torch.where(mask, torch.zeros_like(self.episode_len), self.episode_len)
        self.episode_return = torch.where(m1, torch.zeros_like(self.episode_return), self.episode_return)
        self.armed = torch.where(m3, torch.ones_like(self.armed), self.armed)
        self.cool = torch.where(m3, torch.zeros_like(self.cool), self.cool)
        self.prev_closing = torch.where(mask[:, None, None, None], torch.zeros_like(self.prev_closing), self.prev_closing)
        self.since_hit = torch.where(m1, torch.full_like(self.since_hit, 99.0), self.since_hit)
        self.head_vel = torch.where(m3, torch.zeros_like(self.head_vel), self.head_vel)

    def _reseed_trackers(self, mask: torch.Tensor) -> None:
        """Positions remembered for finite differences have to follow a reset, or the first step of
        every episode reads a velocity of (new position - old position) / dt: hundreds of metres a second."""
        g = self._geometry()
        m3, m4 = mask[:, None, None], mask[:, None, None, None]
        self.prev_glove = torch.where(m4, g["glove"], self.prev_glove)
        self.prev_head = torch.where(m3, g["t_head"], self.prev_head)
        self.prev_body = torch.where(m3, g["t_body"], self.prev_body)
        self.prev_foot_xy = torch.where(m4, g["foot_xy"], self.prev_foot_xy)

    def reset(self) -> torch.Tensor:
        all_envs = torch.ones(self.N, dtype=torch.bool, device=self.device)
        self._apply_reset(all_envs)
        self._physics_forward()
        self._reseed_trackers(all_envs)
        # Spread the episode clock, so the population does not time out in lockstep for ever.
        self.step_count = torch.randint(0, max(1, self.max_steps), (self.N,), device=self.device, generator=self.rng).float()
        self._obs = self._observe(self._geometry())
        return self._obs

    # ---- geometry ------------------------------------------------------------------------------
    def _geometry(self) -> Dict[str, torch.Tensor]:
        """Everything the observation and the reward read off the bodies, shaped (N, K, ...)."""
        g: Dict[str, torch.Tensor] = {}
        g["pos"] = self.xpos[:, self.pelvis]                               # (N,K,3)
        g["quat"] = self.xquat[:, self.pelvis]                             # (N,K,4)
        g["yaw"] = quat_yaw(g["quat"])
        g["glove"] = self.geom_xpos[:, self.g_glove]                       # (N,K,2,3)
        head = self.geom_xpos[:, self.g_head]                              # (N,K,3)
        tc = self.geom_xpos[:, self.g_torso]
        tax = self.geom_xmat[:, self.g_torso][..., :, 2]                   # capsule axis, world
        g["own_head"], g["own_body"], g["own_axis"] = head, tc, tax

        if self.spar:
            # Each fighter's target is the other one.
            g["t_head"], g["t_body"], g["t_axis"] = head.flip(1), tc.flip(1), tax.flip(1)
            # The sizes are the other fighter's, which in a match between two bodies are not one's own.
            shape = lambda t: t.flip(0).view(1, self.K, 1)
            g["t_r_head"], g["t_r_body"] = shape(self.r_head_k), shape(self.r_torso_k)
            g["t_half_head"], g["t_half"] = 0.0, shape(self.torso_half_k)   # a head is a ball, a body a capsule
        else:
            axis = self.geom_xmat[:, self.g_bag][:, :, 2]                  # (N,3)
            g["t_head"] = self.site_xpos[:, self.s_bag_head][:, None]
            g["t_body"] = self.site_xpos[:, self.s_bag_body][:, None]
            g["t_axis"] = axis[:, None]
            # The bag is one capsule; its top half stands in for a head and its bottom half for a body.
            g["t_r_head"], g["t_r_body"] = self.r_bag, self.r_bag
            g["t_half_head"], g["t_half"] = 0.2, 0.2

        cz = self.geom_xpos[:, self.g_foot][..., 2]                        # (N,K,2)
        rz = self.geom_xmat[:, self.g_foot][..., 2, :]                     # world-z row of each foot
        g["sole_z"] = cz - (rz.abs() * self.foot_half).sum(-1)
        g["foot_xy"] = self.geom_xpos[:, self.g_foot][..., :2]
        g["foot_contact"] = g["sole_z"] < 0.005
        return g

    def _base(self, g):
        N, K = self.N, self.K
        lin_w = torch.stack([self.qvel[:, rv:rv + 3] for rv in self.root_v], 1)            # (N,K,3)
        ang_b = torch.stack([self.qvel[:, rv + 3:rv + 6] for rv in self.root_v], 1)
        q = g["quat"]
        lin_b = quat_rotate_inverse(q, lin_w)
        grav_b = quat_rotate_inverse(q, self.gravity_world.expand(N, K, 3))
        return lin_w, lin_b, ang_b, grav_b

    def _observe(self, g) -> torch.Tensor:
        N, K = self.N, self.K
        lin_w, lin_b, ang_b, grav_b = self._base(g)
        pos, yaw = g["pos"], g["yaw"]
        rel = lambda p: to_heading(p - pos, yaw)
        head_vel = self.head_vel - lin_w
        own_gloves = to_heading(g["glove"] - pos[:, :, None], yaw[:, :, None]).reshape(N, K, 6)
        if self.spar:
            opp_gloves = to_heading(g["glove"].flip(1) - pos[:, :, None], yaw[:, :, None]).reshape(N, K, 6)
            dyaw = yaw.flip(1) - yaw
            facing = torch.stack([torch.cos(dyaw), torch.sin(dyaw)], -1)
        else:
            opp_gloves = torch.zeros(N, K, 6, device=self.device)
            facing = torch.zeros(N, K, 2, device=self.device)
        ring = to_heading(torch.cat([-pos[..., :2], torch.zeros(N, K, 1, device=self.device)], -1), yaw)[..., :2] / self.ring_half

        jp = self.qpos[:, self.jq] - self.default_joint
        jv = self.qvel[:, self.jv]
        obs = torch.cat([lin_b, ang_b, grav_b, jp, jv, self.last_action,
                         g["foot_contact"].float(), pos[..., 2:3],
                         rel(g["t_head"]), rel(g["t_body"]), to_heading(head_vel, yaw),
                         own_gloves, opp_gloves, facing, ring], dim=-1)
        if self.obs_noise > 0.0:
            obs = obs + torch.randn(obs.shape, generator=self.rng, device=self.device) * (0.02 * self.obs_noise)
        return torch.nan_to_num(obs).clamp(-100.0, 100.0).reshape(N * K, self.obs_dim)

    # ---- step ----------------------------------------------------------------------------------
    def step(self, action: torch.Tensor):
        N, K, A = self.N, self.K, self.A
        action = action.clamp(-self.action_clip, self.action_clip).reshape(N, K, A)
        self.prev_action = self.last_action
        self.last_action = action
        want = self.default_joint + action * self.action_scale
        self.ctrl.copy_(want.clamp(self.joint_lo, self.joint_hi).reshape(N, K * A))
        if self.push_vel > 0.0:
            # An occasional shove, about once every four seconds per fighter.
            for rv in self.root_v:
                shove = (torch.rand(N, generator=self.rng, device=self.device) < self.dt / 4.0).float()[:, None]
                self.qvel[:, rv:rv + 2].add_(shove * self._u(N, 2, lo=-self.push_vel, hi=self.push_vel))
        self._physics_step()
        self.step_count = self.step_count + 1.0

        g = self._geometry()
        lin_w, lin_b, ang_b, grav_b = self._base(g)
        pos, yaw = g["pos"], g["yaw"]
        upright = -grav_b[..., 2]

        # ---- gloves against the target's head and body
        glove = g["glove"]                                                    # (N,K,2,3)
        v_glove = (glove - self.prev_glove) / self.dt
        self.head_vel = (g["t_head"] - self.prev_head) / self.dt
        v_head = self.head_vel[:, :, None]
        v_body = ((g["t_body"] - self.prev_body) / self.dt)[:, :, None]
        ax = g["t_axis"][:, :, None]

        def to_zone(centre, half):
            """Distance and direction from each glove to the nearest point of a zone's axis."""
            c = centre[:, :, None]
            along = ((glove - c) * ax).sum(-1)
            along = torch.minimum(torch.maximum(along, -half * torch.ones_like(along)), half * torch.ones_like(along))
            to = c + ax * along[..., None] - glove
            d = to.norm(dim=-1)
            return d, to / d.clamp_min(1e-4)[..., None]

        d_head, n_head = to_zone(g["t_head"], g["t_half_head"])
        d_body, n_body = to_zone(g["t_body"], g["t_half"])
        gap = torch.stack([d_head - self.r_glove - g["t_r_head"], d_body - self.r_glove - g["t_r_body"]], -1)   # (N,K,2,2)
        # Speed along the surface normal only, so a glove sliding over the target is not a punch; and
        # the smaller of "closing on the target" and "moving at all", so a target that swings or walks
        # into a glove held still is not one either. (The first rehearsal found both: a glove rubbed up
        # and down the bag scored 17 "hits" a second at 11 m/s.)
        rel = torch.stack([((v_glove - v_head) * n_head).sum(-1), ((v_glove - v_body) * n_body).sum(-1)], -1)
        own = torch.stack([(v_glove * n_head).sum(-1), (v_glove * n_body).sum(-1)], -1)
        closing = torch.minimum(rel, own)
        # The step a glove arrives in, the contact has already slowed it: take the faster of this step
        # and the one before. Capped at what a trained boxer's glove does.
        speed = torch.maximum(closing, self.prev_closing).clamp(0.0, self.hit_cap)
        self.prev_closing = closing

        # One hit per landing, on whichever zone the glove is touching. To score again the glove has to
        # come away 30 cm and a quarter of a second has to pass.
        zone = gap.argmin(-1)                                                  # (N,K,2): 0 head, 1 body
        nearest = gap.min(-1).values
        arriving = speed.gather(-1, zone[..., None]).squeeze(-1)
        self.cool = (self.cool - self.dt).clamp_min(0.0)
        landed = (nearest < 0.012) & self.armed & (arriving > 1.0) & (self.cool <= 0.0)   # (N,K,2)
        self.armed = (self.armed & ~landed) | (nearest > self.rearm_m)
        self.cool = torch.where(landed, torch.full_like(self.cool, 0.25), self.cool)
        to_the_head = landed & (zone == 0)
        strength = landed.float() * arriving * torch.where(zone == 0, 1.5, 1.0)
        # Paid in full only for a punch landing at least 0.6 s after the fighter's last, and falling off
        # with the square of anything quicker: five pats a second earn a third of what three punches in
        # two seconds do. (The second rehearsal settled on exactly those pats while the fall-off was linear.)
        pace = (self.since_hit / 0.6).clamp(0.0, 1.0).pow(2).clamp_min(0.05)
        dealt = strength.sum(-1) * pace                                        # (N,K)
        self.since_hit = torch.where(dealt > 0.0, torch.zeros_like(self.since_hit), self.since_hit + self.dt)
        taken = dealt.flip(1) if self.spar else torch.zeros_like(dealt)

        # ---- where the fighter is relative to its target
        to_t = g["t_body"] - pos
        dist = to_t[..., :2].norm(dim=-1)
        bearing = torch.atan2(to_t[..., 1], to_t[..., 0])
        face = torch.cos(bearing - yaw)
        near = (nearest < 0.6).float()                                         # (N,K,2) glove within reach
        reaching = (closing.max(-1).values.clamp(0.0, 8.0) * near).max(-1).values

        # ---- feet
        contact = g["foot_contact"]
        foot_v = (g["foot_xy"] - self.prev_foot_xy) / self.dt
        foot_sep = (g["foot_xy"][:, :, 0] - g["foot_xy"][:, :, 1]).norm(dim=-1)

        jp = self.qpos[:, self.jq]
        jv = self.qvel[:, self.jv]
        tau = self.act_force.reshape(N, K, A)

        # ---- reward
        r = {}
        r["alive"] = torch.full((N, K), 0.15, device=self.device)
        r["upright"] = 0.25 * upright.clamp_min(0.0)
        r["height"] = -1.0 * (self.stand_height - 0.08 - pos[..., 2]).clamp_min(0.0)
        r["face"] = 0.25 * face
        r["range"] = 0.3 * torch.exp(-((dist - self.range_m) / 0.35) ** 2)
        r["close"] = 0.03 * reaching
        r["hit"] = self.hit_w * dealt
        r["taken"] = -self.taken_w * self.hit_w * taken
        r["lin_z"] = -0.3 * lin_b[..., 2] ** 2
        r["ang"] = -0.02 * (ang_b[..., :2] ** 2).sum(-1)
        r["act"] = -0.001 * (action ** 2).sum(-1)
        r["rate"] = -0.01 * ((action - self.prev_action) ** 2).sum(-1)
        r["energy"] = -2.5e-4 * (tau * jv).abs().clamp_max(2000.0).sum(-1)
        # House rule: joints move no faster than a person's.
        r["qvel"] = -0.1 * (jv.abs() - self.qvel_limit).clamp(0.0, 10.0).pow(2).sum(-1)
        r["limit"] = -0.5 * ((self.joint_lo + 0.05 - jp).clamp_min(0.0) + (jp - self.joint_hi + 0.05).clamp_min(0.0)).sum(-1)
        r["slip"] = -0.2 * ((foot_v ** 2).sum(-1).clamp_max(25.0) * contact.float()).sum(-1)
        r["stance"] = -0.2 * (foot_sep - 0.30).abs()
        r["legs"] = -0.03 * ((jp - self.default_joint)[..., self.leg_idx] ** 2).sum(-1)
        # Stand on your own feet. The dress rehearsal's fighters learned to prop themselves on the bag,
        # and then on each other, feet far behind and heads together like two cards. A head more than
        # 18 cm out from between the feet is leaning on something.
        feet_mid = g["foot_xy"].mean(2)
        r["lean"] = -1.5 * ((g["own_head"][..., :2] - feet_mid).norm(dim=-1) - 0.18).clamp_min(0.0)

        # ---- termination
        fell = (pos[..., 2] < self.stand_height * 0.6) | (upright < 0.4) | ~torch.isfinite(pos).all(-1)   # (N,K)
        timeout = self.step_count >= self.max_steps
        any_fell = fell.any(1)
        r["fall"] = -self.fall_penalty * fell.float()
        if self.spar:
            # Putting the other fighter down is the point; being there when they trip is worth little.
            other_fell = fell.flip(1) & ~fell
            r["ko"] = other_fell.float() * torch.where(self.since_hit < 1.5, self.ko_bonus, 0.5)
        else:
            r["ko"] = torch.zeros(N, K, device=self.device)
        reward = sum(r.values())
        done = any_fell | timeout

        # ---- bookkeeping, all on the GPU
        for name in self.TERMS:
            self._acc[f"rt_{name}"] += r[name].mean()
        self.episode_return = self.episode_return + reward
        self.episode_len = self.episode_len + 1.0
        df = done.float()
        a = self._acc
        a["ret_sum"] += (self.episode_return.mean(1) * df).sum()
        a["len_sum"] += (self.episode_len * df).sum()
        a["fell_sum"] += (any_fell.float() * df).sum()
        a["done_n"] += df.sum()
        a["steps"] += 1.0
        a["upright_sum"] += upright.mean()
        a["dist_sum"] += dist.mean()
        a["hits_head"] += to_the_head.float().sum() / (N * K)
        a["hits_body"] += (landed & ~to_the_head).float().sum() / (N * K)
        a["strength_sum"] += (landed.float() * arriving).sum()
        a["strength_max"] = torch.maximum(a["strength_max"], (landed.float() * arriving).max())
        a["glove_speed"] += v_glove.norm(dim=-1).max(-1).values.mean()
        a["power_sum"] += (tau * jv).abs().sum(-1).clamp_max(20000.0).mean()
        a["sat_sum"] += (action.abs() >= 0.99 * self.action_clip).float().mean()
        a["ko_sum"] += (r["ko"] >= self.ko_bonus).float().sum()
        ka = self._kacc
        ka["hits"] += landed.float().sum((0, 2))
        ka["speed"] += (landed.float() * arriving).sum((0, 2))
        ka["fell"] += (fell.float() * df[:, None]).sum(0)
        ka["ko"] += (r["ko"] >= self.ko_bonus).float().sum(0)
        ka["ret"] += reward.sum(0)

        self.prev_glove, self.prev_head, self.prev_body = glove.clone(), g["t_head"].clone(), g["t_body"].clone()
        self.prev_foot_xy = g["foot_xy"].clone()

        self._apply_reset(done)
        self._physics_forward()
        self._reseed_trackers(done)
        self._obs = self._observe(self._geometry())
        expand = lambda x: x[:, None].expand(N, K).reshape(N * K)
        return self._obs, reward.reshape(N * K), expand(done), expand(timeout & ~any_fell)

    def get_stats(self) -> Dict[str, float]:
        a = {k: v.item() for k, v in self._acc.items()}
        n = max(1.0, a["done_n"])
        s = max(1.0, a["steps"])
        hits = (a["hits_head"] + a["hits_body"])
        out = {
            "ep_return": a["ret_sum"] / n,
            "ep_len_s": a["len_sum"] / n * self.dt,
            "fall_rate": a["fell_sum"] / n,
            "upright": a["upright_sum"] / s,
            "distance": a["dist_sum"] / s,
            # Hits per fighter per second, and how hard: closing speed of the glove, m/s.
            "hits_per_s": hits / s / self.dt,
            "head_share": a["hits_head"] / max(1e-9, hits),
            "hit_speed": a["strength_sum"] / max(1.0, hits * self.N * self.K),
            "hit_speed_max": a["strength_max"],
            "glove_speed": a["glove_speed"] / s,
            "power": a["power_sum"] / s,
            "act_sat": a["sat_sum"] / s,
            "knockdowns_per_min": a["ko_sum"] / (self.N * max(1.0, self.K - 1)) / (s * self.dt) * 60.0,
        }
        out.update({f"rt_{t}": a[f"rt_{t}"] / s for t in self.TERMS})
        if self.hetero:
            ka = {k: v.tolist() for k, v in self._kacc.items()}
            seconds = s * self.dt * self.N
            for i, name in enumerate(self.names):
                out[f"{name}_hits_per_s"] = ka["hits"][i] / seconds
                out[f"{name}_hit_speed"] = ka["speed"][i] / max(1.0, ka["hits"][i])
                # Of the episodes that ended, the share that ended with this fighter on the floor.
                out[f"{name}_falls"] = ka["fell"][i] / n
                out[f"{name}_knockdowns_per_min"] = ka["ko"][i] / seconds * 60.0
                out[f"{name}_reward_per_step"] = ka["ret"][i] / (s * self.N)
        for v in list(self._acc.values()) + list(self._kacc.values()):
            v.zero_()
        return out
