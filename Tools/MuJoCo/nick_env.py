"""MuJoCo Warp velocity-command locomotion environment for Nick.

ONE POLICY, TWO MINI-GAMES, same decision as the Isaac line (Tools/Isaac):
the commanded forward speed is an observation, so a single brain covers the
balance ring (command 0 m/s) and the walk race (command ~1 m/s). A quarter of
episodes command a dead stop, so standing cannot be traded away for walking.

THE MODEL IS THE ONE UNITY GENERATES. Tools/MuJoCo/nick_unity.xml is what
MjScene compiles at play time, exported by RigTool_NickMuJoCo.ExportNickMjcf --
NOT the authored Assets/MuJoCoCreature/Model/creature.xml. The 2026-08-31 line
found that training on the authored file gives a policy that collapses into a
crouch in Unity, because the plugin re-derives inertias, resolves dampratio
into an explicit kv per actuator, and stamps its own solver options. Both
files load; only one of them is the creature the game runs.

THE OBSERVATION VECTOR IS CreatureSentisController.GatherObservations,
term for term (see the layout at the top of that file). 121 terms shared with
the ML-Agents balance line, plus the 6-term locomotion command:

    commanded speed, pelvis-local commanded direction (3), gait clock sin/cos

Anything here that disagrees with the C# builder is a policy trained for a
creature that does not exist. `self_check()` diffs the two builders' inputs
(quaternion layout, cvel layout) against CPU MuJoCo at construction, and the
Unity side exposes DebugGatherObservations for an element-wise diff.

ACTIONS are 30 zero-centred position targets, in the model's actuator order
(canonical PoBox order: Torso, Head, ThighL, ShinL, FootL, ThighR, ShinR,
FootR, UpperArmL, ForearmL, GloveL, UpperArmR, ForearmR, GloveR; pitch, roll,
yaw within each). ctrl = a >= 0 ? a * high : -a * low, as Systems_FighterRig
and the controller both do. The policy decides every `decimation` physics
steps and its targets are held in between -- the controller's decimation
field must match.

REWARD is the Isaac line's (Tools/Isaac/matt_env.py) weighted geometric mean
so the two simulators are compared on the same objective: upright, height,
speed match, facing, and -- fading in with the commanded speed -- single
support, foot clearance and alternation. Falling ends the episode.
"""
from __future__ import annotations

import math
import re
from dataclasses import asdict, dataclass, field
from pathlib import Path

import mujoco
import mujoco_warp as mjw
import numpy as np
import torch
import warp as wp

HERE = Path(__file__).parent
UNITY_MJCF = HERE / "nick_unity.xml"
AUTHORED_MJCF = HERE.parent.parent / "Assets" / "MuJoCoCreature" / "Model" / "creature.xml"

# Canonical PoBox joint order (RigSegment minus the pelvis root).
JOINT_BODIES = [
    "Torso", "Head",
    "ThighL", "ShinL", "FootL",
    "ThighR", "ShinR", "FootR",
    "UpperArmL", "ForearmL", "GloveL",
    "UpperArmR", "ForearmR", "GloveR",
]
# Expected actuator stems, in order. Anything else is a different creature.
ACTUATOR_STEMS = [
    "a_Torso_pitch", "a_Torso_roll", "a_Torso_yaw",
    "a_Head_pitch", "a_Head_roll", "a_Head_yaw",
    "a_ThighL_pitch", "a_ThighL_roll", "a_ThighL_yaw", "a_ShinL_pitch", "a_FootL_pitch", "a_FootL_roll",
    "a_ThighR_pitch", "a_ThighR_roll", "a_ThighR_yaw", "a_ShinR_pitch", "a_FootR_pitch", "a_FootR_roll",
    "a_UpperArmL_pitch", "a_UpperArmL_roll", "a_UpperArmL_yaw", "a_ForearmL_pitch", "a_GloveL_pitch", "a_GloveL_yaw",
    "a_UpperArmR_pitch", "a_UpperArmR_roll", "a_UpperArmR_yaw", "a_ForearmR_pitch", "a_GloveR_pitch", "a_GloveR_yaw",
]

OBS_BASE = 121
OBS_COMMAND = 6
ACT_DIM = 30
ANGULAR_VELOCITY_SCALE = 20.0
FOOT_RAY_MAX = 1.0
# CreatureSentisController: with the locomotion command on, a foot is "down"
# when its ankle body is within this of its rest height. (The legacy 0.0525 m
# absolute threshold never fires while standing -- the ankle rests 15 cm up.)
FOOT_CONTACT_ABOVE_REST = 0.03
GAIT_CLOCK_HZ = 1.4          # Agent_FighterBoxing.GAIT_CLOCK_FREQUENCY
REST_FORWARD = (0.0, -1.0, 0.0)   # the controller's "forward" in pelvis frame
PHYSICS_TIMESTEP = 0.005


def stem(name: str) -> str:
    """Unity's MJCF generator suffixes every name: Torso -> Torso_3."""
    return re.sub(r"_\d+$", "", name)


@dataclass
class NickEnvCfg:
    model_path: str = str(UNITY_MJCF)
    num_envs: int = 4096
    decimation: int = 4
    episode_seconds: float = 20.0
    seed: int = 1
    observe_command: bool = True

    # --- command sampling ---------------------------------------------------
    stand_still_fraction: float = 0.25
    speed_range: tuple = (0.3, 1.2)
    # Commanded direction relative to the rest heading. The Unity demo
    # commands straight ahead; a spread here keeps the brain steerable.
    heading_range_deg: tuple = (-30.0, 30.0)

    # --- reward (Tools/Isaac/matt_env.py, gen 8 weights) --------------------
    w_upright: float = 0.15
    w_height: float = 0.15
    w_speed_match: float = 0.5
    w_facing: float = 0.3
    w_support: float = 0.3
    w_clearance: float = 0.35
    w_alternation: float = 0.5
    w_smoothness: float = 0.05
    w_planted: float = 0.3          # both feet down at zero command (nick03+)
    product_floor: float = 0.01
    reward_scale: float = 5.0
    gait_blend_speed: float = 0.5
    track_kernel: float = 0.5
    stand_kernel: float = 0.05
    foot_clearance_target: float = 0.10
    alternation_ema_steps: int = 100
    alternation_target_steps: int = 35
    pen_termination: float = -2.0

    # --- termination --------------------------------------------------------
    fall_pelvis_fraction: float = 0.55   # of rest pelvis height
    fall_up_z: float = 0.4               # pelvis up axis z below this = over

    # --- perturbations ------------------------------------------------------
    push_probability: float = 1.0 / 150.0   # per control step, after push_start
    push_force_range: tuple = (50.0, 200.0)  # newtons, horizontal
    push_steps: int = 8                      # control steps a push lasts (0.16 s)
    push_start_seconds: float = 1.0
    init_joint_noise: float = 0.05           # rad, hinge dofs
    init_vel_noise: float = 0.1
    exact_start_fraction: float = 0.3        # resets from the exact rest pose, as Unity does

    # --- domain randomisation, fixed per world for the run ------------------
    gain_scale_range: tuple = (0.85, 1.15)   # kp and kv together, keeps the damping ratio
    friction_scale_range: tuple = (0.7, 1.3)


# --- quaternion helpers, wxyz, batched on the last axis ----------------------

def quat_conj(q: torch.Tensor) -> torch.Tensor:
    return q * torch.tensor([1.0, -1.0, -1.0, -1.0], device=q.device, dtype=q.dtype)


def quat_mul(a: torch.Tensor, b: torch.Tensor) -> torch.Tensor:
    """Hamilton product a*b: rotate by b, then by a (Unity's `a * b`, MuJoCo's mju_mulQuat)."""
    aw, ax, ay, az = a.unbind(-1)
    bw, bx, by, bz = b.unbind(-1)
    return torch.stack([
        aw * bw - ax * bx - ay * by - az * bz,
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
    ], dim=-1)


def quat_rotate(q: torch.Tensor, v: torch.Tensor) -> torch.Tensor:
    """Rotate v by q (Unity's `Quaternion * Vector3`)."""
    w = q[..., :1]
    xyz = q[..., 1:]
    t = 2.0 * torch.cross(xyz, v, dim=-1)
    return v + w * t + torch.cross(xyz, t, dim=-1)


_CONSTANTS: dict = {}


def _constants(device) -> tuple:
    """Unit vectors cached per device: a fresh torch.tensor(...) per call is a
    host-to-device copy and a sync, and the reward path runs thousands of times."""
    key = str(device)
    if key not in _CONSTANTS:
        _CONSTANTS[key] = (torch.tensor([0.0, 0.0, 1.0], device=device),
                           torch.tensor(REST_FORWARD, device=device))
    return _CONSTANTS[key]


def observe(xpos: torch.Tensor, xquat: torch.Tensor, cvel: torch.Tensor,
            pelvis_id: int, foot_ids: list, joint_body_ids: list, joint_parent_ids: list,
            foot_contact_z: list, commands: torch.Tensor | None, phase: torch.Tensor | None) -> torch.Tensor:
    """CreatureSentisController.GatherObservations, batched.

    xpos (n, nbody, 3), xquat (n, nbody, 4) as wxyz, cvel (n, nbody, 6) as
    [angular, linear] -- MuJoCo's own layouts. commands (n, 3) is speed and
    horizontal direction in world XY; phase (n,) the gait clock in radians.
    Pass commands=None for the 121-term legacy contract. Shared by the Warp
    env and by watch_nick.py on CPU MuJoCo, so there is exactly one builder.
    """
    n = xpos.shape[0]
    dev = xpos.device
    unit_z, rest_forward = _constants(dev)
    q = xquat[:, pelvis_id]
    qc = quat_conj(q)
    up = quat_rotate(q, unit_z.expand(n, 3))
    fwd = quat_rotate(q, rest_forward.expand(n, 3))
    lin = quat_rotate(qc, cvel[:, pelvis_id, 3:6])
    ang = quat_rotate(qc, cvel[:, pelvis_id, 0:3]) / ANGULAR_VELOCITY_SCALE
    root = torch.cat([xpos[:, pelvis_id, 2:3], lin, ang, up, fwd], dim=1)      # 13

    child = xquat[:, joint_body_ids]                                          # (n, 14, 4)
    parent = xquat[:, joint_parent_ids]
    local = quat_mul(quat_conj(parent), child)                                # w x y z
    w = cvel[:, joint_body_ids, 0:3] / ANGULAR_VELOCITY_SCALE
    joints = torch.cat([local, w], dim=-1).reshape(n, -1)                     # 98

    foot_z = xpos[:, foot_ids, 2]
    lg = (foot_z[:, 0] < foot_contact_z[0]).float()
    rg = (foot_z[:, 1] < foot_contact_z[1]).float()
    zero = torch.zeros_like(lg)
    feet = torch.stack([lg, zero, zero, lg, rg, zero, zero, rg], dim=1)       # 8
    heights = torch.clamp(foot_z / FOOT_RAY_MAX, 0.0, 1.0)                    # 2

    parts = [root, joints, feet, heights]
    if commands is not None:
        direction = torch.stack([commands[:, 1], commands[:, 2], torch.zeros(n, device=dev)], dim=1)
        local_dir = quat_rotate(qc, direction)
        parts.append(torch.stack([commands[:, 0], local_dir[:, 0], local_dir[:, 1], local_dir[:, 2],
                                  torch.sin(phase), torch.cos(phase)], dim=1))   # 6
    return torch.cat(parts, dim=1)


class NickEnv:
    """rsl_rl VecEnv over a MuJoCo Warp batch of Nicks."""

    def __init__(self, cfg: NickEnvCfg, device: str = "cuda:0"):
        self.cfg = cfg
        self.device = torch.device(device)
        torch.manual_seed(cfg.seed)
        np.random.seed(cfg.seed)
        wp.config.quiet = True
        wp.init()

        self.num_envs = cfg.num_envs
        self.num_actions = ACT_DIM
        self.num_obs = OBS_BASE + (OBS_COMMAND if cfg.observe_command else 0)
        self.control_dt = PHYSICS_TIMESTEP * cfg.decimation
        self.max_episode_length = int(round(cfg.episode_seconds / self.control_dt))

        # --- CPU model: ids, rest pose, contract checks ---------------------
        self.mjm = mujoco.MjModel.from_xml_path(cfg.model_path)
        self.mjm.opt.timestep = PHYSICS_TIMESTEP
        self.mjd = mujoco.MjData(self.mjm)
        mujoco.mj_forward(self.mjm, self.mjd)
        self._resolve_ids()
        self._resolve_rest_pose()

        # --- Warp batch -----------------------------------------------------
        n = self.num_envs
        self.m = mjw.put_model(self.mjm, batch_sizes={
            "actuator_gainprm": n, "actuator_biasprm": n, "geom_friction": n})
        self.d = mjw.put_data(self.mjm, self.mjd, nworld=n)
        self.qpos = wp.to_torch(self.d.qpos)
        self.qvel = wp.to_torch(self.d.qvel)
        self.ctrl = wp.to_torch(self.d.ctrl)
        self.xfrc = wp.to_torch(self.d.xfrc_applied)
        self.xpos = wp.to_torch(self.d.xpos)
        self.xquat_raw = wp.to_torch(self.d.xquat)
        self.cvel = wp.to_torch(self.d.cvel)
        self._quat_perm = self.self_check()
        self._randomise_worlds()

        with wp.ScopedCapture() as capture:
            mjw.step(self.m, self.d)
        self._step_graph = capture.graph
        with wp.ScopedCapture() as capture:
            mjw.forward(self.m, self.d)
        self._forward_graph = capture.graph

        # --- buffers ----------------------------------------------------------
        dev = self.device
        self.actions = torch.zeros(n, ACT_DIM, device=dev)
        self.prev_actions = torch.zeros(n, ACT_DIM, device=dev)
        self.commands = torch.zeros(n, 3, device=dev)         # speed, dir x, dir y
        self.episode_length_buf = torch.zeros(n, dtype=torch.long, device=dev)
        self.last_stance = torch.zeros(n, device=dev)
        self.switch_rate = torch.zeros(n, device=dev)
        self.push_steps_left = torch.zeros(n, dtype=torch.long, device=dev)
        self.push_force = torch.zeros(n, 3, device=dev)
        self.fell = torch.zeros(n, dtype=torch.bool, device=dev)
        self.qpos0 = torch.tensor(self.mjm.qpos0, device=dev, dtype=self.qpos.dtype)
        self.ctrl_low = torch.tensor(self.mjm.actuator_ctrlrange[:, 0], device=dev, dtype=torch.float32)
        self.ctrl_high = torch.tensor(self.mjm.actuator_ctrlrange[:, 1], device=dev, dtype=torch.float32)
        self.rest_forward = torch.tensor(REST_FORWARD, device=dev)
        self.rest_foot_z_t = torch.tensor(self.rest_foot_z, device=dev)
        self._hinge_qpos_mask = torch.zeros(self.mjm.nq, device=dev)
        self._hinge_qpos_mask[7:] = 1.0
        self.extras: dict = {}
        self.reset()

    # ------------------------------------------------------------------ setup
    def _resolve_ids(self):
        m = self.mjm
        by_stem = {}
        for i in range(m.nbody):
            by_stem.setdefault(stem(m.body(i).name), i)
        missing = [b for b in ["Pelvis", "FootL", "FootR"] + JOINT_BODIES if b not in by_stem]
        if missing:
            raise RuntimeError("model %s lacks bodies %s" % (self.cfg.model_path, missing))
        self.pelvis_id = by_stem["Pelvis"]
        self.foot_ids = [by_stem["FootL"], by_stem["FootR"]]
        self.head_id = by_stem["Head"]
        self.joint_body_ids = [by_stem[b] for b in JOINT_BODIES]
        self.joint_parent_ids = [int(m.body_parentid[i]) for i in self.joint_body_ids]

        stems = [stem(m.actuator(i).name) for i in range(m.nu)]
        if stems != ACTUATOR_STEMS:
            raise RuntimeError("actuator order differs from the PoBox canonical order:\n  got %s" % stems)
        # Hinge dofs (everything after the free joint) for reset noise.
        self.hinge_qpos_index = torch.arange(7, m.nq)
        self.hinge_dof_index = torch.arange(6, m.nv)

    def _resolve_rest_pose(self):
        d = self.mjd
        self.rest_pelvis_z = float(d.xpos[self.pelvis_id, 2])
        self.rest_foot_z = [float(d.xpos[i, 2]) for i in self.foot_ids]
        self.rest_head_z = float(d.xpos[self.head_id, 2])
        self.foot_contact_z = [z + FOOT_CONTACT_ABOVE_REST for z in self.rest_foot_z]

    def self_check(self) -> list[int]:
        """Pin down Warp's storage layouts against CPU MuJoCo.

        Warp quaternions are xyzw in memory while MuJoCo's are wxyz, and
        mujoco_warp may or may not convert on the way in; cvel could likewise
        be stored [ang, lin] or otherwise. Guessing either silently corrupts
        every rotation observation, so both are measured on a random state.
        """
        m = self.mjm
        probe = mujoco.MjData(m)
        probe.qpos[:] = m.qpos0
        probe.qvel[:] = np.random.RandomState(0).randn(m.nv) * 0.3
        mujoco.mj_forward(m, probe)
        d1 = mjw.put_data(m, probe, nworld=1)
        wp.synchronize()
        xq = wp.to_torch(d1.xquat)[0].cpu().numpy()
        cv = wp.to_torch(d1.cvel)[0].cpu().numpy()
        body = self.joint_body_ids[0]
        if np.allclose(xq[body], probe.xquat[body], atol=1e-5):
            perm = [0, 1, 2, 3]
        elif np.allclose(xq[body][[3, 0, 1, 2]], probe.xquat[body], atol=1e-5):
            perm = [3, 0, 1, 2]
        else:
            raise RuntimeError("cannot match Warp xquat layout to MuJoCo: %s vs %s" % (xq[body], probe.xquat[body]))
        if not np.allclose(cv[body], probe.cvel[body], atol=1e-4):
            raise RuntimeError("Warp cvel differs from MuJoCo: %s vs %s" % (cv[body], probe.cvel[body]))
        return perm

    def _randomise_worlds(self):
        """Per-world actuator gain and friction scales, fixed for the run.

        kp and kv are scaled together so the damping ratio the plugin resolved
        survives; the raptor line (SOURCE_raptor.txt) transferred cleanly to
        Unity with this much spread and no more.
        """
        n = self.num_envs
        g = torch.empty(n, 1, device=self.device).uniform_(*self.cfg.gain_scale_range)
        gain = wp.to_torch(self.m.actuator_gainprm)      # (n, nu, ...)
        bias = wp.to_torch(self.m.actuator_biasprm)
        gain[:, :, 0] *= g
        bias[:, :, 1] *= g
        bias[:, :, 2] *= g
        f = torch.empty(n, 1, device=self.device).uniform_(*self.cfg.friction_scale_range)
        friction = wp.to_torch(self.m.geom_friction)      # (n, ngeom, 3)
        friction[:, :, 0] *= f
        torch.cuda.synchronize()
        wp.synchronize()

    # ------------------------------------------------------------- interface
    def get_observations(self):
        return self.obs_buf, {"observations": {}}

    def reset(self):
        self._reset_idx(torch.arange(self.num_envs, device=self.device))
        self.obs_buf = self._observations()
        return self.obs_buf, {"observations": {}}

    def step(self, actions: torch.Tensor):
        cfg = self.cfg
        self.prev_actions = self.actions
        self.actions = actions.clamp(-1.0, 1.0)
        target = torch.where(self.actions >= 0.0,
                             self.actions * self.ctrl_high,
                             -self.actions * self.ctrl_low)
        self.ctrl[:] = target.to(self.ctrl.dtype)

        self._apply_pushes()
        torch.cuda.synchronize()
        for _ in range(cfg.decimation):
            wp.capture_launch(self._step_graph)
        wp.synchronize()
        self.episode_length_buf += 1

        state = self._state()
        reward = self._rewards(state)
        terminated, timeout = self._dones(state)
        dones = terminated | timeout
        # Mask-based reset every step: no nonzero() sync, and the forward
        # kinematics graph costs about one physics substep.
        self._reset_mask(dones)
        self.obs_buf = self._observations()
        self.extras["time_outs"] = timeout
        return self.obs_buf, reward, dones, self.extras

    # ----------------------------------------------------------------- state
    def _xquat(self) -> torch.Tensor:
        return self.xquat_raw[..., self._quat_perm]

    def _state(self) -> dict:
        xquat = self._xquat()
        q = xquat[:, self.pelvis_id]
        qc = quat_conj(q)
        unit_z, rest_forward = _constants(self.device)
        up = quat_rotate(q, unit_z.expand(self.num_envs, 3))
        fwd = quat_rotate(q, rest_forward.expand(self.num_envs, 3))
        lin_w = self.cvel[:, self.pelvis_id, 3:6].float()
        foot_z = self.xpos[:, self.foot_ids, 2].float()
        return {
            "xquat": xquat, "q": q, "qc": qc, "up": up, "fwd": fwd,
            "pelvis_z": self.xpos[:, self.pelvis_id, 2].float(),
            "lin_w": lin_w,
            "foot_z": foot_z,
            "foot_down": torch.stack([foot_z[:, 0] < self.foot_contact_z[0],
                                      foot_z[:, 1] < self.foot_contact_z[1]], dim=1),
        }

    def _observations(self) -> torch.Tensor:
        phase = GAIT_CLOCK_HZ * 2.0 * math.pi * self.episode_length_buf.float() * self.control_dt
        obs = observe(self.xpos.float(), self._xquat().float(), self.cvel.float(),
                      self.pelvis_id, self.foot_ids, self.joint_body_ids, self.joint_parent_ids,
                      self.foot_contact_z,
                      self.commands if self.cfg.observe_command else None,
                      phase if self.cfg.observe_command else None)
        if obs.shape[1] != self.num_obs:
            raise RuntimeError("observation is %d wide, expected %d" % (obs.shape[1], self.num_obs))
        return obs

    # ---------------------------------------------------------------- reward
    def _rewards(self, s: dict) -> torch.Tensor:
        cfg = self.cfg
        n = self.num_envs
        speed = self.commands[:, 0]
        direction = self.commands[:, 1:3]
        v_xy = s["lin_w"][:, :2]
        commanded_xy = speed.unsqueeze(1) * direction

        lin_err = torch.sum((v_xy - commanded_xy) ** 2, dim=1)
        standing = speed <= 0.1
        kernel = torch.where(standing, torch.full_like(lin_err, cfg.stand_kernel),
                             torch.full_like(lin_err, cfg.track_kernel))
        speed_match = torch.exp(-lin_err / kernel)

        fwd_xy = s["fwd"][:, :2]
        fwd_xy = fwd_xy / torch.clamp(fwd_xy.norm(dim=1, keepdim=True), min=1e-6)
        facing = torch.clamp(torch.sum(fwd_xy * direction, dim=1), 0.0, 1.0)

        upright = torch.clamp(s["up"][:, 2], min=0.0)
        height = torch.exp(-((s["pelvis_z"] - self.rest_pelvis_z) ** 2) / 0.02)

        left_down, right_down = s["foot_down"][:, 0], s["foot_down"][:, 1]
        support = (left_down ^ right_down).float()

        swing_height = torch.clamp(s["foot_z"] - self.rest_foot_z_t, min=0.0)
        clearance = torch.clamp(swing_height.max(dim=1)[0] / cfg.foot_clearance_target, max=1.0)

        stance = torch.zeros(n, device=self.device)
        stance = torch.where(left_down & ~right_down, -torch.ones_like(stance), stance)
        stance = torch.where(right_down & ~left_down, torch.ones_like(stance), stance)
        switched = (stance != 0) & (self.last_stance != 0) & (stance != self.last_stance)
        self.last_stance = torch.where(stance != 0, stance, self.last_stance)
        ema = 1.0 / cfg.alternation_ema_steps
        self.switch_rate += (switched.float() - self.switch_rate) * ema
        alternation = torch.clamp(self.switch_rate * cfg.alternation_target_steps, max=1.0)

        blend = torch.clamp(speed / cfg.gait_blend_speed, max=1.0)

        # Told to hold still, keep both feet on the floor. nick02 (no such
        # term) stood up fine but marched in place at command 0 -- 70 stance
        # changes and 0.4 m of drift per 10 s, identical in MuJoCo and Unity.
        # The gait factors fade OUT as the command drops; this one fades IN.
        planted = (left_down & right_down).float()

        def factor(value, weight):
            return torch.clamp(value, min=cfg.product_floor) ** weight

        locomotion = (
            factor(upright, cfg.w_upright)
            * factor(height, cfg.w_height)
            * factor(speed_match, cfg.w_speed_match)
            * factor(facing, cfg.w_facing * blend)
            * factor(support, cfg.w_support * blend)
            * factor(clearance, cfg.w_clearance * blend)
            * factor(alternation, cfg.w_alternation * blend)
            * factor(planted, cfg.w_planted * (1.0 - blend))
        )
        smoothness = torch.mean((self.actions - self.prev_actions) ** 2, dim=1)
        reward = cfg.reward_scale * (locomotion - cfg.w_smoothness * smoothness) * self.control_dt

        self.fell = (s["pelvis_z"] < cfg.fall_pelvis_fraction * self.rest_pelvis_z) | (s["up"][:, 2] < cfg.fall_up_z)
        reward = reward + cfg.pen_termination * self.fell.float()

        # Masked means, never boolean indexing: every `x[mask]` and every
        # `if mask.any()` is a device sync, and this runs every control step.
        moving = (speed > 0.1).float()
        standing_f = 1.0 - moving
        n_moving = torch.clamp(moving.sum(), min=1.0)
        n_standing = torch.clamp(standing_f.sum(), min=1.0)
        measured = torch.sum(v_xy * direction, dim=1)
        self.extras["log"] = {
            "Metrics/speed_ratio": torch.sum(measured / torch.clamp(speed, min=0.1) * moving) / n_moving,
            "Metrics/drift_when_standing": torch.sum(v_xy.norm(dim=1) * standing_f) / n_standing,
            "Metrics/upright_fraction": (upright > 0.7).float().mean(),
            "Metrics/alternation": torch.sum(alternation * moving) / n_moving,
            "Metrics/single_support": torch.sum(support * moving) / n_moving,
            "Metrics/facing": torch.sum(facing * moving) / n_moving,
            "Metrics/planted_when_standing": torch.sum(planted * standing_f) / n_standing,
            "Metrics/foot_clearance": swing_height.max(dim=1)[0].mean(),
            "Metrics/height": s["pelvis_z"].mean(),
            "Metrics/fall_rate": self.fell.float().mean(),
        }
        return reward

    def _dones(self, s: dict):
        timeout = self.episode_length_buf >= self.max_episode_length
        return self.fell.clone(), timeout & ~self.fell

    # ------------------------------------------------------------ transitions
    def _apply_pushes(self):
        cfg = self.cfg
        n = self.num_envs
        eligible = self.episode_length_buf.float() * self.control_dt > cfg.push_start_seconds
        start = eligible & (self.push_steps_left <= 0) & (torch.rand(n, device=self.device) < cfg.push_probability)
        # Sampled for every world and selected with where(): no data-dependent
        # shapes, so no device sync.
        angle = torch.rand(n, device=self.device) * 2.0 * math.pi
        magnitude = torch.empty(n, device=self.device).uniform_(*cfg.push_force_range)
        fresh = torch.stack([torch.cos(angle) * magnitude, torch.sin(angle) * magnitude,
                             torch.zeros(n, device=self.device)], dim=1)
        self.push_force = torch.where(start.unsqueeze(1), fresh, self.push_force)
        self.push_steps_left = torch.where(start, torch.full_like(self.push_steps_left, cfg.push_steps), self.push_steps_left)
        active = self.push_steps_left > 0
        force = torch.where(active.unsqueeze(1), self.push_force, torch.zeros_like(self.push_force))
        self.xfrc[:, self.pelvis_id, 0:3] = force.to(self.xfrc.dtype)
        self.push_steps_left = torch.clamp(self.push_steps_left - 1, min=0)

    def _reset_idx(self, ids: torch.Tensor):
        mask = torch.zeros(self.num_envs, dtype=torch.bool, device=self.device)
        mask[ids] = True
        self._reset_mask(mask)

    def _reset_mask(self, mask: torch.Tensor):
        """Reset the worlds where mask is true. Fully vectorised over the
        batch -- fresh states are sampled for every world and selected with
        where(), so nothing here depends on how many reset."""
        cfg = self.cfg
        n = self.num_envs
        dev = self.device
        dtype = self.qpos.dtype
        m1 = mask.unsqueeze(1)

        exact = (torch.rand(n, device=dev) < cfg.exact_start_fraction).float().unsqueeze(1)
        noise = (torch.rand(n, self.mjm.nq, device=dev) * 2.0 - 1.0) * cfg.init_joint_noise * self._hinge_qpos_mask
        qpos = self.qpos0.unsqueeze(0) + (noise * (1.0 - exact)).to(dtype)
        qvel = (torch.randn(n, self.mjm.nv, device=dev) * cfg.init_vel_noise * (1.0 - exact)).to(dtype)

        self.qpos[:] = torch.where(m1, qpos, self.qpos)
        self.qvel[:] = torch.where(m1, qvel, self.qvel)
        self.ctrl[:] = torch.where(m1, torch.zeros_like(self.ctrl), self.ctrl)
        self.xfrc[:] = torch.where(mask.view(n, 1, 1), torch.zeros_like(self.xfrc), self.xfrc)
        self.actions = torch.where(m1, torch.zeros_like(self.actions), self.actions)
        self.prev_actions = torch.where(m1, torch.zeros_like(self.prev_actions), self.prev_actions)
        self.episode_length_buf = torch.where(mask, torch.zeros_like(self.episode_length_buf), self.episode_length_buf)
        self.last_stance = torch.where(mask, torch.zeros_like(self.last_stance), self.last_stance)
        self.switch_rate = torch.where(mask, torch.zeros_like(self.switch_rate), self.switch_rate)
        self.push_steps_left = torch.where(mask, torch.zeros_like(self.push_steps_left), self.push_steps_left)
        self.fell = self.fell & ~mask

        speed = torch.empty(n, device=dev).uniform_(*cfg.speed_range)
        stand = torch.rand(n, device=dev) < cfg.stand_still_fraction
        speed = torch.where(stand, torch.zeros_like(speed), speed)
        yaw = torch.empty(n, device=dev).uniform_(*cfg.heading_range_deg) * (math.pi / 180.0)
        # Rest heading is (0, -1, 0); rotated about +z by yaw it is (sin, -cos).
        fresh = torch.stack([speed, torch.sin(yaw), -torch.cos(yaw)], dim=1)
        self.commands = torch.where(m1, fresh, self.commands)

        # Kinematics for the new states, so the first observation of the new
        # episode describes the new episode.
        torch.cuda.synchronize()
        wp.capture_launch(self._forward_graph)
        wp.synchronize()

    # ------------------------------------------------------------- reporting
    def config_dict(self) -> dict:
        return asdict(self.cfg)


def preferred_model_path() -> str:
    """The Unity-generated MJCF if it has been exported, else the authored one, loudly."""
    if UNITY_MJCF.exists():
        return str(UNITY_MJCF)
    print("WARNING: %s missing -- run RigTool_NickMuJoCo.ExportNickMjcf. Falling back to the AUTHORED "
          "creature.xml, which is NOT the creature Unity runs." % UNITY_MJCF)
    return str(AUTHORED_MJCF)


if __name__ == "__main__":
    # Smoke test: random actions, a few hundred steps, report throughput.
    import argparse
    import time

    parser = argparse.ArgumentParser()
    parser.add_argument("--num-envs", type=int, default=1024)
    parser.add_argument("--steps", type=int, default=200)
    args = parser.parse_args()
    cfg = NickEnvCfg(num_envs=args.num_envs, model_path=preferred_model_path())
    env = NickEnv(cfg)
    print("ENV READY  num_envs=%d  obs=%d  act=%d  control_dt=%.3f  quat_perm=%s"
          % (env.num_envs, env.num_obs, env.num_actions, env.control_dt, env._quat_perm))
    print("  rest pelvis z %.3f  feet %s  head %.3f" % (env.rest_pelvis_z, env.rest_foot_z, env.rest_head_z))
    obs, _ = env.get_observations()
    print("  obs[0][:13] =", np.round(obs[0, :13].cpu().numpy(), 3))
    print("  obs[0][-6:] =", np.round(obs[0, -6:].cpu().numpy(), 3))
    total = torch.zeros(env.num_envs, device=env.device)
    falls = 0
    t0 = time.time()
    for step in range(args.steps):
        action = torch.zeros(env.num_envs, ACT_DIM, device=env.device)
        obs, rew, done, extras = env.step(action)
        total += rew
        falls += int(done.sum())
        if step % 50 == 0:
            print("  step %3d  rew %7.4f  height %.3f  upright %.2f  done %d"
                  % (step, float(rew.mean()), float(extras["log"]["Metrics/height"]),
                     float(extras["log"]["Metrics/upright_fraction"]), int(done.sum())))
    dt = time.time() - t0
    print("  %d control steps x %d envs in %.1fs -> %.0f env-steps/s" % (args.steps, env.num_envs, dt, args.steps * env.num_envs / dt))
    print("  finite obs: %s   falls: %d   mean return %.3f" % (bool(torch.isfinite(obs).all()), falls, float(total.mean())))
    print("SMOKE_OK")
