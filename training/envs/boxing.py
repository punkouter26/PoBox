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

# The mass behind a glove: a punch's impulse is this times its closing speed (the game's Fighter.PunchMass).
PUNCH_MASS = 2.2


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


def quat_mul(a: torch.Tensor, b: torch.Tensor) -> torch.Tensor:
    aw, ax, ay, az = a[..., 0], a[..., 1], a[..., 2], a[..., 3]
    bw, bx, by, bz = b[..., 0], b[..., 1], b[..., 2], b[..., 3]
    return torch.stack([aw * bw - ax * bx - ay * by - az * bz, aw * bx + ax * bw + ay * bz - az * by,
                        aw * by - ax * bz + ay * bw + az * bx, aw * bz + ax * by - ay * bx + az * bw], -1)


def to_heading(v: torch.Tensor, yaw: torch.Tensor) -> torch.Tensor:
    """Rotate world vectors (..., 3) into the frame that has turned `yaw` about the vertical."""
    c, s = torch.cos(yaw), torch.sin(yaw)
    return torch.stack([c * v[..., 0] + s * v[..., 1], -s * v[..., 0] + c * v[..., 1], v[..., 2]], dim=-1)


from .bodies import lying_bodies  # noqa: E402,F401  (the Melee's downed bodies; plain mujoco, shared with the C exams)


def punch_variety(landed, arriving, zone, last_kind, streak, since_hit, combo_s: float, pause_s: float = 2.0):
    """Which punch each fighter landed this step and how it follows the last one. landed, arriving, zone: (N,K,2), per
    glove; last_kind: (N,K) hand x 2 + zone of the last punch landed (-1 none); streak: how many in a row before this were
    that punch; since_hit: seconds since the last one. A punch is its hand and where it went (the faster glove's if both
    land); a pause of pause_s starts the count afresh; a combination is the other hand within combo_s.
    Returns any_land, same (this one repeats the last), combo, combo_q (its speed, m/s), the new streak and last_kind."""
    any_land = landed.any(-1)
    hand = (landed.float() * arriving).argmax(-1)
    kind = hand * 2 + zone.gather(-1, hand[..., None]).squeeze(-1)
    same = any_land & (kind == last_kind) & (since_hit <= pause_s)
    streak = torch.where(any_land, torch.where(same, streak + 1.0, torch.zeros_like(streak)), streak)
    combo = any_land & (last_kind >= 0) & (hand != torch.div(last_kind, 2, rounding_mode="floor")) & (since_hit < combo_s)
    combo_q = combo.float() * (landed.float() * arriving).amax(-1)
    return any_land, same, combo, combo_q, streak, torch.where(any_land, kind, last_kind)


class BoxingEnv:
    TERMS = ["alive", "upright", "height", "face", "range", "close", "hit", "taken", "block", "lin_z", "ang", "act",
             "rate", "energy", "qvel", "limit", "slip", "stance", "legs", "lean", "fall", "ko", "style", "rope", "cutoff", "clinch"]
    MODE = ""          # a subclass that is its own stage of training names it here

    def __init__(self, xml_path: str, num_envs: int, device: str = "cuda", seed: int = 0,
                 control_decimation: int = 4, episode_len_s: float = 12.0, action_scale: float = 0.5,
                 action_clip: float = 3.0, nconmax: int = 0, njmax: int = 0, cuda_graph: bool = True,
                 obs_noise: float = 0.0, push_vel: float = 0.0, hit_w: float = 0.6, taken_w: float = 0.5,
                 fall_penalty: float = 4.0, ko_bonus: float = 4.0, range_m: float = 0.78, hit_cap: float = 9.0,
                 survivor_bootstrap: bool = False, daze: bool = False, daze_tau: float = 2.5,
                 daze_lo: float = 14.0, daze_hi: float = 36.0, daze_weak: float = 0.45, daze_body: float = 0.3,
                 legs_out_s: float = 0.7, block_w: float = 0.0, verbose: bool = False,
                 handover: dict = None, handover_share: float = 0.15,
                 speed_limit: bool = False, fatigue_j: float = 0.0, fatigue_rec_s: float = 20.0, fatigue_weak: float = 0.4,
                 start_spread: float = 1.0, sep_hi: float = 2.2, rope_w: float = 0.0, cutoff_w: float = 0.0,
                 action_rate_w: float = 0.0, obs_delay_max: int = 0, seek_w: float = 0.0, ropes_start: float = 0.0,
                 hit_push: float = 0.0, stagger_v: float = 0.0, stagger_s: float = 0.3, stagger_drive: float = 0.55,
                 hurt_full: float = 0.0, hurt_hi: float = 0.4, hurt_weak: float = 0.25, hurt_tau: float = 600.0,
                 obs_state: tuple = (), body_drain: float = 0.0, clinch_w: float = 0.0,
                 selfcontact_w: float = 0.0, repeat_decay: float = 1.0, combo_w: float = 0.0, combo_s: float = 0.8,
                 hurt_start: float = 0.0, fatigue_ceiling: bool = False, slow_v: float = 0.0,
                 head_mult: float = 1.0, arms_w: float = 0.0, bodies: int = 0, body_mult: float = 1.0, head_aim_w: float = 0.0):
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
        # What the end of an episode means to the fighter left standing when the other one falls. Off, it
        # is an ending like any other, and that turned out to be a reason never to put anybody down: a
        # fighter earns about 0.8 a step just for standing in range, an episode has hundreds of steps
        # left in it, and the knockdown that ends it early pays 4. The eight-hour match of 2026-10-01
        # learned exactly that: 1.7 hits a second each, and 0.03 knockdowns a minute. On, the survivor's
        # episode is cut short rather than ended (its value is carried over, as at a time-out), so a
        # knockdown costs it nothing and the bonus is all profit.
        self.survivor_bootstrap = survivor_bootstrap
        # Being hit has a consequence, the one it has in the game (Sim/Fighter.cs). Every landed punch adds
        # its closing speed to the daze of the fighter who took it (in full to the head, a share to the
        # body), and the daze drains away with a time constant of a couple of seconds. Above daze_lo the
        # joint drives weaken, down to (1 - daze_weak) of their strength at daze_hi; at daze_hi the legs go
        # altogether for legs_out_s, which puts the fighter on the canvas. Without this a fighter that
        # stands and trades for ever loses nothing by it, and that is what eight hours of training found.
        self.daze_on = daze
        self.daze_tau, self.daze_lo, self.daze_hi = daze_tau, daze_lo, daze_hi
        self.daze_weak, self.daze_body, self.legs_out_s = daze_weak, daze_body, legs_out_s
        # Paid to a fighter whose glove or forearm is in the way of a punch coming at its head.
        self.block_w = block_w
        # How far from the guard an episode may begin, as a multiple of the usual 0.06 rad a joint and 0.15 m/s:
        # a boxer that has only ever started in its guard is lost when it is handed a body in any other state.
        self.start_spread = start_spread
        # The furthest apart two boxers begin. At 2.2 m a match policy never has to walk in: put 2.5 to 3.5 m from
        # its opponent, as a boxer is after a knockdown, Matt's fell over on the way in 22 of 60 trials, whether
        # the opponent boxed, stood still or was not there (carry_diag, 2026-10-03). That was "carrying on".
        self.sep_hi = sep_hi
        # Ringcraft, both off unless asked for: a charge for being walked onto the ropes by an opponent that holds
        # the middle of the ring, and a payment for doing that to the opponent (cutting the ring off). See step().
        self.rope_w, self.cutoff_w = rope_w, cutoff_w
        # Tying up, off unless asked for: paid to a hurt boxer (daze past half of where its legs go) for both gloves
        # resting on the opponent's trunk at no punching speed, chest to chest. Needs --daze; see step().
        self.clinch_w = clinch_w
        # Four more, all off unless asked for (2026-10-16, staged). action_rate_w: a charge on the squared change in
        # action between control steps on top of every stage's own small one, for smoother drives. obs_delay_max: the
        # observation is 0 to this many control steps late, drawn per fighter per episode (the last action, which
        # the boxer knows, is never late); the game's numbers are not delayed, so this only makes a brain robust
        # to lag. seek_w: paid per m/s of closing on the opponent while outside punching range, whatever the boxer's
        # style (Phase F2: the owner's "seek out the closest opponent and move forward to attack"; in a pair the
        # nearest on its feet is the one opponent). ropes_start: the share of episodes that begin as the free-for-
        # all's rounds do, both boxers at the ropes facing the middle (Phase F3).
        self.action_rate_w, self.obs_delay_max = action_rate_w, int(obs_delay_max)
        self.seek_w, self.ropes_start = seek_w, ropes_start
        if seek_w and not self.MODE:          # the bout's step only; footwork and get-up have steps of their own
            self.TERMS = self.TERMS + ["seek"]
        # Five more, all off unless asked for (2026-10-16 night, staged). selfcontact_w: a charge per centimetre of
        # a boxer's own parts pressed into each other (MuJoCo's soft contacts: the harder they press, the deeper), for
        # the guard the footwork brain held against itself, arm actions at the clip (AGENTS: a small self-contact
        # penalty when a policy leans on it); the bout's and footwork's steps. repeat_decay: the third jab in a row is
        # worth repeat_decay^2 of the first (a punch is its hand and where it lands; a pause of 2 s starts afresh), for
        # the spam (tasks.md G14). combo_w: paid per m/s of a punch landing within combo_s of one from the other hand.
        # hurt_start: the share of episodes that begin dazed, 55-90% of the way to the legs going, so the clinch's pay
        # is ever earned (H7; needs --daze). fatigue_ceiling: a tired drive's torque is capped at what is left of its
        # force range, instead of its spring being weakened (a weak spring still reaches full torque on a big ask).
        self.selfcontact_w, self.repeat_decay, self.combo_w, self.combo_s = selfcontact_w, repeat_decay, combo_w, combo_s
        self.hurt_start = hurt_start
        # slow_v: a landed punch slower than this (m/s) is worth (speed / slow_v)^2 of its pay, for the exam's attack line
        # (a mean landing speed of 4 m/s times the boxer's speed factor): pats stop paying. 0 = off.
        self.slow_v = slow_v
        # 2026-10-19: head_mult scales every fighter's head price (style "head", 1.5 by default): Matt landed 0% to the head.
        # arms_w: r["legs"]'s pull to the guard pose for the arms (shoulders, elbows), so the hands come back to the chin.
        self.head_mult, self.arms_w = head_mult, arms_w
        if selfcontact_w and self.MODE != "getup":   # getting up leans on its own arms by design
            self.TERMS = self.TERMS + ["self"]
        if combo_w and not self.MODE:
            self.TERMS = self.TERMS + ["combo"]
        if arms_w and not self.MODE:          # the bout's step only
            self.TERMS = self.TERMS + ["arms"]
        # 2026-10-19 day (tools/head_diag.py): Matt and Sokol keep Nick's range but their fast gloves pass 21-28 cm below the
        # head (Nick, Zombie 6-9 cm), so the head's price is never met. head_aim_w pays per m/s a glove closes on the head
        # while within 25 cm of it (a dense lead to head height); body_mult scales the body's price as head_mult the head's.
        self.head_aim_w = head_aim_w
        if head_aim_w and not self.MODE:
            self.TERMS = self.TERMS + ["aim"]
        # The share of the charges for effort (power, action size, action change) in force. The trainer holds it
        # at 0 while a skill is being found and brings it back (--effort-free-iters): charged from the first
        # step, a boxer that cannot punch yet learns that standing still is cheapest (bag_trump2, 2026-10-03).
        self.effort_scale = 1.0
        self.range_m = range_m
        self.hit_cap = hit_cap
        # How far a glove has to come back off the target before it can score again: a punch has a
        # wind-up, a pat does not.
        self.rearm_m = 0.30
        self.use_cuda_graph = cuda_graph and device.startswith("cuda")
        self.rng = torch.Generator(device=device)
        self.rng.manual_seed(seed)

        # bodies (2026-10-19, the Melee): that many boxers down on the canvas, solid, as the free-for-all leaves them; each
        # episode each lies somewhere in the ring clear of the two fighters (or, a quarter of the time, is taken away).
        # ponytail: a fixed lying shape on a mocap body, not a ragdoll, and not in the observation; a brain only feels it.
        self.n_bodies = int(bodies)
        self.m = lying_bodies(xml_path, self.n_bodies) if self.n_bodies else mujoco.MjModel.from_xml_path(xml_path)
        m = self.m
        self.spar = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "b_pelvis") >= 0
        self.K = 2 if self.spar else 1
        self.mode = self.MODE or ("spar" if self.spar else "bag")
        self._prepare_model(m)
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
        # Sensor noise in each number's own units (times obs_noise): one size for all of them was the whole
        # signal of the gravity vector, which moves a few hundredths, and nothing to a joint speed of 20 rad/s.
        A_ = self.A
        self.noise_scale = torch.tensor(
            [0.05] * 3 + [0.1] * 3 + [0.005] * 3              # base velocity, turn rate, gravity
            + [0.01] * A_ + [0.3] * A_ + [0.0] * A_           # joint angles, joint speeds, last action (known exactly)
            + [0.0] * 2 + [0.01]                              # feet down, height
            + [0.02] * 6 + [0.1] * 3                          # target head and body, target head velocity
            + [0.02] * 12 + [0.02] * 2 + [0.01] * 2,          # gloves, facing, place in the ring
            device=device)
        self.ring_half = float(cfg.get("ring_half", self.cfg.get("ring_half", 3.05)))

        # Fighter by fighter: a boxer trained under the rule can meet one that was not.
        limited = [speed_limit or bool(c.get("speed_limit", False)) for c in self.cfgs]
        self.speed_limit = any(limited)
        for p, c, on in zip(("a_", "b_"), self.cfgs, limited):
            if on:
                for n, fl, vl in zip(order, c["force_limit"], c["velocity_limit"]):
                    m.dof_damping[m.jnt_dofadr[mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, p + n)]] += fl / vl
        d0 = mujoco.MjData(m)
        mujoco.mj_resetDataKeyframe(m, d0, 0)
        mujoco.mj_forward(m, d0)
        self.mw = mjw.put_model(m)
        self.mw.opt.warn_overflow = verbose
        # Two bodies touching each other, the floor and themselves need more room than one runner did.
        nconmax = (nconmax or (128 if self.spar else 64)) + 32 * self.n_bodies
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
        if self.n_bodies:
            self.mocap_pos, self.mocap_quat = wp.to_torch(self.dw.mocap_pos), wp.to_torch(self.dw.mocap_quat)

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
        self.g_forearm = L([[mujoco.mj_name2id(m, G, p + f"forearm_{s}_geom") for s in "lr"] for p in ("a_", "b_")[: self.K]])
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
        self.r_forearm = F([size[int(self.g_forearm[k, 0]), 0] for k in range(K)]).view(1, K, 1, 1)
        self.forearm_half = F([size[int(self.g_forearm[k, 0]), 1] for k in range(K)]).view(1, K, 1, 1)

        # A fighter's style: how it is paid, fighter by fighter, from its own config (rig_to_mjcf.py copies
        # it out of the rig file). Nothing here changes what a hit is or what it does to the one who takes
        # it: two fighters in one ring are paid differently for the same exchange, and that is what makes
        # one a brawler and the other a counter-puncher. A config with no style is the plain boxer.
        def style(key: str, default: float) -> torch.Tensor:
            return F([float(c.get("style", {}).get(key, default)) for c in self.cfgs])      # (K,)

        self.s_hit = style("hit", 1.0)             # what landing a punch is worth, as a multiple
        self.s_head, self.s_body = style("head", 1.5) * head_mult, style("body", 1.0) * body_mult
        self.s_hand = torch.stack([style("hand_l", 1.0), style("hand_r", 1.0)], -1)          # (K, 2)
        self.s_power = style("power", 1.0)         # above 1 a hard punch is worth more than its speed; below 1, less
        self.s_pace = style("pace_s", 0.6)         # the gap between punches that is paid in full
        self.s_taken = style("taken", 1.0)         # what being hit costs, as a multiple
        self.s_block = style("block_w", block_w)
        self.s_counter = style("counter", 1.0)     # a punch landed within a second of stopping one
        self.s_range = style("range_m", range_m)
        self.s_press = style("press", 0.0)         # paid for walking in on the target from outside its range
        self.s_crowd = style("crowd", 0.0)         # charged for standing closer than its range
        self.s_energy = style("energy", 1.0)
        # Where each fighter's head, body and gloves are from its pelvis when it stands in its guard, in
        # its own heading: what a stand-in for it looks like to the other one (the game's MujocoRing keeps
        # the same four offsets for the same purpose).
        self.guard_head, self.guard_body, self.guard_glove = [], [], []
        for k in range(K):
            pel = int(self.pelvis[k])
            yaw0 = float(quat_yaw(torch.tensor(d0.xquat[pel], dtype=torch.float32)))
            c0, s0 = math.cos(yaw0), math.sin(yaw0)
            rel0 = lambda p: [c0 * (p[0] - d0.xpos[pel][0]) + s0 * (p[1] - d0.xpos[pel][1]),
                              -s0 * (p[0] - d0.xpos[pel][0]) + c0 * (p[1] - d0.xpos[pel][1]), p[2] - d0.xpos[pel][2]]
            self.guard_head.append(rel0(d0.geom_xpos[int(self.g_head[k])]))
            self.guard_body.append(rel0(d0.geom_xpos[int(self.g_torso[k])]))
            self.guard_glove.append([rel0(d0.geom_xpos[int(self.g_glove[k, i])]) for i in range(2)])
        self.guard_head, self.guard_body, self.guard_glove = F(self.guard_head), F(self.guard_body), F(self.guard_glove)

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
        # Radians per unit of action, joint by joint: the config's action_scale_j where it has one (models/v3, each joint's
        # reach from the guard over the clip), else the one number for all of them, as every brain before v3 was trained.
        self.scale_ka = F([c.get("action_scale_j", [action_scale] * len(order)) for c in self.cfgs])     # (K, A)
        # The ceiling's numbers: each drive's force range from the model, per side (models/v3's are not symmetric).
        self.ceiling = torch.tensor([fatigue_ceiling or bool(c.get("fatigue_ceiling", False)) for c in self.cfgs], device=device)
        fr = m.actuator_forcerange[: self.K * len(order)].reshape(self.K, len(order), 2)
        self.f_lo, self.f_hi = F(fr[..., 0]), F(fr[..., 1])
        self._kp = self._kv = None
        self.qvel_limit = F([c["velocity_limit"] for c in self.cfgs])    # (K, A): an old fighter's joints are slower
        # Two rules of a living body, each off unless asked for here or in the fighter's own config (which is
        # what the exam and the game read, so a policy trained under a rule is examined and played under it).
        # A muscle's strength falls with its speed: each joint is damped by its force limit over its speed
        # limit, so a drive pulling its hardest has nothing left over at the limit, which is then the body's
        # and not only a charge (the physics applies it every solver step). And a body tires: every
        # joule its drives put out is taken from a reserve that fills again over fatigue_rec_s; below half, the
        # drives weaken, down to (1 - fatigue_weak) with nothing left.
        # ponytail: the reserve is not in the observation (nor is the daze); a boxer cannot pace itself by it.
        # Give it an input with tools/widen_policy.py when pacing is wanted.
        self.fatigue_j = F([fatigue_j or float(c.get("fatigue_j", 0.0)) for c in self.cfgs])             * F([float(c.get("strength", 1.0)) for c in self.cfgs])              # (K,) a frail body has less
        self.fatigue_on = bool((self.fatigue_j > 0.0).any())
        self.tires = self.fatigue_j > 0.0                                        # (K,) which fighters tire
        self.fatigue_j = torch.where(self.tires, self.fatigue_j, torch.full_like(self.fatigue_j, float("inf")))
        self.fatigue_rec_s, self.fatigue_weak = fatigue_rec_s, fatigue_weak
        self.ceiling_on = self.fatigue_on and bool((self.ceiling & self.tires).any())
        # Four rules of a punch (2026-10-16, the owner's "50 head shots and no visible effect"), each the taker's
        # own, from its config or the argument, off unless asked for; the game's Fighter and MjRing do the same.
        # hit_push: a landed punch also pushes the taker's trunk, at the point it landed, with this share of the
        # punch's impulse (PUNCH_MASS x closing speed) over the next control step: MuJoCo's contact only passes on
        # the glove's own 0.8 kg, and the head is a ball on the trunk, so a head shot rocks the trunk back.
        # stagger_v: a head shot at least this fast holds the drives under stagger_drive for stagger_s.
        # hurt_full: punishment that lasts (the daze's input, draining over hurt_tau); at hurt_full the legs go at
        # (1 - hurt_hi) of daze_hi and the drives have lost hurt_weak. An episode begins anywhere up to 80% of it.
        # body_drain: a body shot takes the wind, this much of a tiring taker's reserve per m/s of the punch.
        # obs_state: the boxers (by name) whose last three numbers are their reserve, their own daze and the
        # opponent's as shares of where the legs go, in place of the command's zeros (the match stage only).
        rule = lambda arg, key: F([arg or float(c.get(key, 0.0)) for c in self.cfgs])
        self.hit_push, self.stagger_v, self.hurt_full = rule(hit_push, "hit_push"), rule(stagger_v, "stagger_v"), rule(hurt_full, "hurt_full")
        self.body_drain = rule(body_drain, "body_drain")
        self.stagger_s, self.stagger_drive = stagger_s, stagger_drive
        self.hurt_hi, self.hurt_weak, self.hurt_tau = hurt_hi, hurt_weak, hurt_tau
        self.obs_state = torch.tensor([n in obs_state or bool(c.get("obs_state", False)) for n, c in zip(self.names, self.cfgs)], device=device)
        self.push_on = bool((self.hit_push > 0.0).any())
        if self.push_on:
            self.xfrc = wp.to_torch(self.dw.xfrc_applied)
            self.xipos = wp.to_torch(self.dw.xipos)
            self.b_torso = L(ids(B, "torso"))
        leg =[i for i, n in enumerate(order) if n.split("_")[0] in ("hip", "knee", "ankle")]
        self.leg_idx = L(leg)
        self.arm_idx = L([i for i, n in enumerate(order) if n.split("_")[0] in ("shoulder", "elbow")])

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
        self.daze = z(N, K)
        self.legs_out = z(N, K)                                                  # seconds the legs stay gone
        self.drive_scale = torch.ones(N, K, device=device)                       # share of joint drive strength left
        self.reserve = torch.ones(N, K, device=device)                           # what a tiring body has left, 0 to 1
        self.hurt = z(N, K)                                                      # punishment that lasts, m/s of punches
        self.stagger = z(N, K)                                                   # seconds a head shot holds the drives down
        self.legs_at = torch.full((N, K), float(daze_hi), device=device)         # the daze the legs go at, after the hurt
        self.push = z(N, K, 6)                                                   # force and torque on the trunk next step
        self.prev_block = torch.zeros(N, K, 2, dtype=torch.bool, device=device)
        self.since_block = torch.full((N, K), 99.0, device=device)               # seconds since this fighter stopped a punch
        # The last punch each fighter landed, as hand x 2 + zone (-1 none), and how many in a row were that punch.
        self.last_kind = torch.full((N, K), -1, dtype=torch.long, device=device)
        self.streak = z(N, K)
        self.gravity_world = torch.tensor([0.0, 0.0, -1.0], device=device)
        self._acc = {k: torch.zeros((), device=device) for k in
                     ("ret_sum", "len_sum", "fell_sum", "done_n", "steps", "upright_sum", "dist_sum",
                      "hits_head", "hits_body", "strength_sum", "strength_max", "glove_speed", "power_sum",
                      "sat_sum", "ko_sum", "daze_sum", "weak_sum", "reserve_sum")}
        self._acc.update({f"rt_{t}": torch.zeros((), device=device) for t in self.TERMS})
        # The same counts kept per fighter, for a match between two different ones.
        self._kacc = {k: torch.zeros(self.K, device=device) for k in ("hits", "speed", "fell", "ko", "ret", "blocks", "legs", "head", "rope", "cutoff", "clinch",
                                                                      "repeats", "combos", "self")}
        if selfcontact_w:
            # Which fighter each shape belongs to (0 a_, 1 b_, -1 the ring, the floor, a cube or the bag).
            names = [mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_BODY, int(b)) or "" for b in m.geom_bodyid]
            self.geom_owner = torch.tensor([0 if n.startswith("a_") else 1 if (n.startswith("b_") and self.K == 2) else -1 for n in names],
                                           device=device, dtype=torch.long)
        # Carrying on after a knockdown: a share of a fighter's episodes begin in a state its own get-up
        # policy left its body in (tools/make_handover_bank.py), not in its guard. `handover` maps a
        # fighter's name to that file; a fighter with no file starts every episode in its guard, as before.
        self.handover_share = handover_share
        self.bank = [None] * self.K
        for k, name in enumerate(self.names):
            path = (handover or {}).get(name)
            if path:
                z = np.load(path)
                self.bank[k] = {key: torch.tensor(z[key], device=device, dtype=torch.float32) for key in ("joint", "jvel", "z", "tilt", "lin", "ang")}
        self._init_extra()
        if self.obs_delay_max > 0:
            # Every caller of _observe (reset, each stage's step) gets the late observation; a subclass's own
            # _observe still reaches its base class's through super(), undelayed.
            self._obs_hist = None
            self._rows = torch.arange(N * K, device=device)
            on_time = self._observe
            self._observe = lambda g: self._delayed(on_time(g))

        self._step_graph = None
        self._fwd_graph = None
        self._capture_graphs()
        self.reset()

    # ---- for a subclass that is another stage of training ---------------------------------------
    def _prepare_model(self, m) -> None:
        """Changes to the model before it goes to the GPU."""

    def _init_extra(self) -> None:
        """State of its own, made before the first reset."""

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
            sep = self._u(N, lo=0.85, hi=self.sep_hi)
            u = torch.stack([torch.cos(bearing), torch.sin(bearing)], -1)
            spots = [centre - u * sep[:, None] * 0.5, centre + u * sep[:, None] * 0.5]
            facing = [bearing, bearing + math.pi]
            if self.ropes_start > 0.0:
                # As the free-for-all's rounds begin (FreeForAll.ToTheRopes): spots round the square of the ropes, half a
                # metre inside, 16 of them; the other boxer two to eight spots round from this one; both facing the middle.
                def rope_spot(i):
                    a = i.float() * (2.0 * math.pi / 16.0)
                    c, s = torch.cos(a), torch.sin(a)
                    return torch.stack([c, s], -1) / torch.maximum(c.abs(), s.abs())[:, None] * (self.ring_half - 0.5)
                i0 = torch.randint(0, 16, (N,), device=self.device, generator=self.rng)
                i1 = (i0 + torch.randint(2, 9, (N,), device=self.device, generator=self.rng)) % 16
                use = self._u(N) < self.ropes_start
                for k, i in enumerate((i0, i1)):
                    p = rope_spot(i)
                    spots[k] = torch.where(use[:, None], p, spots[k])
                    facing[k] = torch.where(use, torch.atan2(-p[:, 1], -p[:, 0]), facing[k])
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
            s = self.start_spread
            q[:, self.jq[k]] = (self.default_joint[k] + self._u(N, A, lo=-0.06 * s, hi=0.06 * s)).clamp(self.joint_lo[k], self.joint_hi[k])
            v[:, rv:rv + 2] = self._u(N, 2, lo=-0.15 * s, hi=0.15 * s)
            b = self.bank[k]
            if b is not None:
                pick = torch.randint(0, b["z"].shape[0], (N,), device=self.device, generator=self.rng)
                use = self._u(N) < self.handover_share
                u1 = use[:, None]
                q[:, self.jq[k]] = torch.where(u1, b["joint"][pick], q[:, self.jq[k]])
                q[:, rq + 2] = torch.where(use, b["z"][pick] + 0.002, q[:, rq + 2])
                q[:, rq + 3:rq + 7] = torch.where(u1, quat_mul(yaw_quat(yaw), b["tilt"][pick]), q[:, rq + 3:rq + 7])
                c, s = torch.cos(yaw), torch.sin(yaw)
                lin = b["lin"][pick]
                world = torch.stack([c * lin[:, 0] - s * lin[:, 1], s * lin[:, 0] + c * lin[:, 1], lin[:, 2]], -1)
                v[:, rv:rv + 3] = torch.where(u1, world, v[:, rv:rv + 3])
                v[:, rv + 3:rv + 6] = torch.where(u1, b["ang"][pick], v[:, rv + 3:rv + 6])
                v[:, self.jv[k]] = torch.where(u1, b["jvel"][pick], v[:, self.jv[k]])
        return q, v

    def _apply_reset(self, mask: torch.Tensor) -> None:
        q, v = self._reset_states()
        m1 = mask[:, None]
        self.qpos.copy_(torch.where(m1, q, self.qpos))
        self.qvel.copy_(torch.where(m1, v, self.qvel))
        if self.n_bodies:
            self._place_bodies(mask, q)
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
        self.daze = torch.where(m1, torch.zeros_like(self.daze), self.daze)
        self.legs_out = torch.where(m1, torch.zeros_like(self.legs_out), self.legs_out)
        self.drive_scale = torch.where(m1, torch.ones_like(self.drive_scale), self.drive_scale)
        self.prev_block = torch.where(m3, torch.zeros_like(self.prev_block), self.prev_block)
        self.since_block = torch.where(m1, torch.full_like(self.since_block, 99.0), self.since_block)
        if self.fatigue_on:
            # An episode is twelve seconds and a round is forty-five: it begins anywhere in a round's tiredness.
            self.reserve = torch.where(m1 & self.tires, self._u(self.N, self.K, lo=0.3, hi=1.0), self.reserve)
        # And anywhere in a fight's punishment.
        self.hurt = torch.where(m1, self._u(self.N, self.K, lo=0.0, hi=0.8) * self.hurt_full, self.hurt)
        self.stagger = torch.where(m1, torch.zeros_like(self.stagger), self.stagger)
        self.push = torch.where(m1[..., None], torch.zeros_like(self.push), self.push)
        self.legs_at = torch.where(m1, self.daze_hi * (1.0 - self.hurt_hi * self._hurt01()), self.legs_at)
        self.last_kind = torch.where(m1, torch.full_like(self.last_kind, -1), self.last_kind)
        self.streak = torch.where(m1, torch.zeros_like(self.streak), self.streak)
        if self.hurt_start > 0.0 and self.daze_on:
            # Begun hurt: most of the way to the legs going, so a clinch (or a careful round) is worth something.
            hurt = m1 & (self._u(self.N, self.K) < self.hurt_start)
            self.daze = torch.where(hurt, self._u(self.N, self.K, lo=0.55, hi=0.9) * self.legs_at, self.daze)

    def _place_bodies(self, mask: torch.Tensor, q: torch.Tensor) -> None:
        """Each downed body somewhere in the ring at least 0.9 m from both fighters' spots and 1.2 m from the other
        bodies (the first of 8 tries that is), turned any way; a quarter of the time, or with no try clear, parked away."""
        N, dev = self.N, self.device
        lim = self.ring_half - 0.7
        feet = [q[:, self.root_q[k]:self.root_q[k] + 2] for k in range(self.K)]
        placed = []
        for j in range(self.n_bodies):
            tries = self._u(N, 8, 2, lo=-lim, hi=lim)
            ok = torch.ones(N, 8, dtype=torch.bool, device=dev)
            for f in feet:
                ok &= (tries - f[:, None]).norm(dim=-1) > 0.9
            for p in placed:
                ok &= (tries - p[:, None]).norm(dim=-1) > 1.2
            pick = ok.float().argmax(-1)
            xy = tries[torch.arange(N, device=dev), pick]
            there = ok.any(-1) & (self._u(N) > 0.25)
            away = torch.tensor([30.0 + 5.0 * j, 30.0], device=dev).expand(N, 2)
            xy = torch.where(there[:, None], xy, away)
            placed.append(torch.where(there[:, None], xy, torch.full_like(xy, 1e3)))
            pos = torch.cat([xy, torch.zeros(N, 1, device=dev)], -1)
            self.mocap_pos[:, j] = torch.where(mask[:, None], pos, self.mocap_pos[:, j])
            self.mocap_quat[:, j] = torch.where(mask[:, None], yaw_quat(self._u(N, lo=-math.pi, hi=math.pi)), self.mocap_quat[:, j])

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
            g["opp_glove"], g["opp_yaw"] = g["glove"].flip(1), g["yaw"].flip(1)
            # The sizes are the other fighter's, which in a match between two bodies are not one's own.
            shape = lambda t: t.flip(0).view(1, self.K, 1)
            g["t_r_head"], g["t_r_body"] = shape(self.r_head_k), shape(self.r_torso_k)
            g["own_r_head"] = self.r_head_k.view(1, self.K, 1)
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
            opp_gloves = to_heading(g["opp_glove"] - pos[:, :, None], yaw[:, :, None]).reshape(N, K, 6)
            dyaw = g["opp_yaw"] - yaw
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
            obs = obs + torch.randn(obs.shape, generator=self.rng, device=self.device) * (self.noise_scale * self.obs_noise)
        return torch.nan_to_num(obs).clamp(-100.0, 100.0).reshape(N * K, self.obs_dim)

    def _delayed(self, obs: torch.Tensor) -> torch.Tensor:
        """The observation as it was 0 to obs_delay_max control steps ago, the lag drawn per row when its episode
        begins (an episode's first steps see its first observation, never the last episode's)."""
        H = self.obs_delay_max + 1
        fresh = (self.episode_len == 0.0)[:, None].expand(self.N, self.K).reshape(-1)
        if self._obs_hist is None:
            self._obs_hist = obs[None].repeat(H, 1, 1)
            self._obs_lag = torch.zeros(obs.shape[0], dtype=torch.long, device=self.device)
        hist = torch.cat([obs[None], self._obs_hist[:-1]], 0)
        self._obs_hist = torch.where(fresh[None, :, None], obs[None].expand_as(hist), hist)
        lag = torch.randint(0, H, (obs.shape[0],), generator=self.rng, device=self.device)
        self._obs_lag = torch.where(fresh, lag, self._obs_lag)
        out = self._obs_hist[self._obs_lag, self._rows].clone()
        a0 = 9 + 2 * self.A                                    # the last action: the boxer's own, known at once
        out[:, a0:a0 + self.A] = obs[:, a0:a0 + self.A]
        return out

    # ---- step ----------------------------------------------------------------------------------
    def _drive(self, action: torch.Tensor) -> None:
        """Joint targets from an action (N, K, A). A weakened fighter is asked for a point part of the way
        from where each joint is to where the policy wants it: the spring is weaker, the damping is not.
        The game's MujocoRing does exactly this with the same number."""
        N, K, A = self.N, self.K, self.A
        want = (self.default_joint + action * self.scale_ka).clamp(self.joint_lo, self.joint_hi)
        q = self.qpos[:, self.jq]
        if self.ceiling_on:
            # A tired drive under the ceiling: the spring keeps the daze's weakening only, and the target is held where
            # the drive's force, kp (target - q) - kv qdot, stays inside what is left of its force range on each side.
            tired = self._tired()
            ceil = self.ceiling.view(1, K)
            want = q + (self.drive_scale * torch.where(ceil, torch.ones_like(tired), tired))[..., None] * (want - q)
            kp, kv = self._gains()
            share = torch.where(ceil, tired, torch.full_like(tired, 1e6))[..., None]
            base = q + kv * self.qvel[:, self.jv] / kp
            want = torch.minimum(torch.maximum(want, base + share * self.f_lo / kp), base + share * self.f_hi / kp)
        elif self.daze_on or self.MODE or self.fatigue_on:
            scale = self.drive_scale * self._tired() if self.fatigue_on else self.drive_scale
            want = q + scale[..., None] * (want - q)
        self.ctrl.copy_(want.reshape(N, K * A))

    def _gains(self):
        """Each world's kp and kv, (worlds, K, A): the bodies are randomised a world (FootworkEnv._randomise) after __init__."""
        if self._kp is None:
            g, b = wp.to_torch(self.mw.actuator_gainprm), wp.to_torch(self.mw.actuator_biasprm)
            g, b = (g[None], b[None]) if g.dim() == 2 else (g, b)
            n = self.K * self.A
            self._kp = g[:, :n, 0].reshape(-1, self.K, self.A).clone()
            self._kv = (-b[:, :n, 2]).reshape(-1, self.K, self.A).clone()
        return self._kp, self._kv

    def _selfcontact(self) -> torch.Tensor:
        """Metres of each fighter's own parts pressed into each other at the last substep, (N, K): the soft contacts'
        overlap, which grows with how hard they press. Read off the contact list on the GPU, without a sync."""
        c = self.dw.contact
        geom, dist, world = wp.to_torch(c.geom).long(), wp.to_torch(c.dist), wp.to_torch(c.worldid).long()
        live = torch.arange(dist.shape[0], device=self.device) < wp.to_torch(self.dw.nacon)[0]
        own = self.geom_owner[geom]                                            # (C, 2)
        mine = live & (own[:, 0] == own[:, 1]) & (own[:, 0] >= 0)
        pen = (-dist).clamp_min(0.0) * mine.float()
        out = torch.zeros(self.N * self.K, device=self.device)
        out.index_add_(0, (world.clamp(0, self.N - 1) * self.K + own[:, 0].clamp_min(0)), pen)
        return out.view(self.N, self.K)

    def _spend(self, tau: torch.Tensor, jv: torch.Tensor) -> None:
        """Takes the step's joules from the reserve and lets it fill again. Every stage's step calls this: footwork
        and get-up once skipped it, and their reserve stayed where the reset drew it."""
        if self.fatigue_on:
            spent = (tau * jv).abs().sum(-1) / self.fatigue_j
            self.reserve = (self.reserve + self.dt * ((1.0 - self.reserve) / self.fatigue_rec_s - spent)).clamp(0.0, 1.0)

    def _tired(self) -> torch.Tensor:
        """Share of drive strength a tiring body has: all of it down to half its reserve, then less."""
        return 1.0 - self.fatigue_weak * (1.0 - self.reserve / 0.5).clamp(0.0, 1.0)

    def _hurt01(self) -> torch.Tensor:
        """How much of hurt_full a fighter has taken, 0 to 1 (0 where the rule is off)."""
        return torch.where(self.hurt_full > 0.0, self.hurt / self.hurt_full.clamp_min(1e-6), torch.zeros_like(self.hurt)).clamp(0.0, 1.0)

    def step(self, action: torch.Tensor):
        N, K, A = self.N, self.K, self.A
        action = action.clamp(-self.action_clip, self.action_clip).reshape(N, K, A)
        self.prev_action = self.last_action
        self.last_action = action
        self._drive(action)
        if self.push_on:
            # Last step's punches, on the trunks of those who took them, for this whole control step.
            for k in range(K):
                self.xfrc[:, int(self.b_torso[k])] = self.push[:, k]
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
        plain = strength.sum(-1) * pace                                        # (N,K) what they are worth to anybody
        # The same punches as this fighter's own style prices them: which zone, which hand, how hard, how
        # often, and whether it has just stopped one. With no style this is `plain` exactly.
        k1 = lambda t: t.view(1, K, 1)
        worth = landed.float() * arriving * (arriving / 6.0).clamp_min(0.1).pow(k1(self.s_power) - 1.0) \
            * torch.where(zone == 0, k1(self.s_head), k1(self.s_body)) * self.s_hand.view(1, K, 2)
        if self.slow_v:
            worth = worth * (arriving / self.slow_v).clamp(max=1.0).pow(2)
        own_pace = (self.since_hit / self.s_pace).clamp(0.0, 1.0).pow(2).clamp_min(0.05)
        dealt = worth.sum(-1) * own_pace * torch.where(self.since_block < 1.0, self.s_counter, torch.ones_like(self.s_counter))
        # Which punch it was (the faster glove's, if both land): its hand and where it went. Counted always (a run with
        # the flags off is the baseline); paid only with repeat_decay below 1 and combo_w above 0.
        any_land, same, combo, combo_q, self.streak, self.last_kind = punch_variety(
            landed, arriving, zone, self.last_kind, self.streak, self.since_hit, self.combo_s)
        if self.repeat_decay < 1.0:
            dealt = dealt * torch.where(any_land, self.repeat_decay ** self.streak, torch.ones_like(dealt))
        self.since_hit = torch.where(plain > 0.0, torch.zeros_like(self.since_hit), self.since_hit + self.dt)
        taken = plain.flip(1) if self.spar else torch.zeros_like(plain)

        # ---- what being hit does to a fighter, and what stopping a punch is worth
        legs_went = torch.zeros(N, K, dtype=torch.bool, device=self.device)
        blocked = torch.zeros(N, K, 2, dtype=torch.bool, device=self.device)
        stopped = torch.zeros(N, K, device=self.device)
        if self.spar:
            if self.push_on:
                # What each glove's punch pushes with, along the line from the glove to the zone, at the zone's surface.
                n = torch.where((zone == 0)[..., None], n_head, n_body)                                    # (N,K,2,3)
                reach = torch.where(zone == 0, d_head - g["t_r_head"], d_body - g["t_r_body"])
                at = glove + n * reach[..., None]
                j = landed.float() * arriving * PUNCH_MASS * self.hit_push.flip(0).view(1, K, 1)            # the taker's rule
                f = n * (j / self.dt)[..., None]
                arm = at - self.xipos[:, self.b_torso.flip(0)][:, :, None]                                 # from the taker's trunk
                self.push = torch.cat([f.sum(2), torch.cross(arm, f, dim=-1).sum(2)], -1).flip(1)
            if self.daze_on:
                received = (landed.float() * arriving * torch.where(zone == 0, 1.0, self.daze_body)).sum(-1).flip(1)
                self.daze = self.daze * math.exp(-self.dt / self.daze_tau) + received
                self.hurt = self.hurt * math.exp(-self.dt / self.hurt_tau) + received * (self.hurt_full > 0.0)
                h = self._hurt01()
                self.legs_at = self.daze_hi * (1.0 - self.hurt_hi * h)
                legs_went = (self.daze >= self.legs_at) & (self.legs_out <= 0.0)
                self.legs_out = torch.where(legs_went, torch.full_like(self.legs_out, self.legs_out_s),
                                            (self.legs_out - self.dt).clamp_min(0.0))
                self.daze = torch.where(legs_went, torch.zeros_like(self.daze), self.daze)
                weak = 1.0 - self.daze_weak * ((self.daze - self.daze_lo) / (self.legs_at - self.daze_lo).clamp_min(1.0)).clamp(0.0, 1.0)
                weak = weak * (1.0 - self.hurt_weak * h)
                if self.fatigue_on:
                    body_in = ((landed & (zone == 1)).float() * arriving).sum(-1).flip(1)
                    self.reserve = torch.where(self.tires, (self.reserve - self.body_drain * body_in).clamp_min(0.0), self.reserve)
                head_shot = (landed & (zone == 0)).float().mul(arriving).amax(-1).flip(1)                  # the fastest that hit my head
                rocked = (self.stagger_v > 0.0) & (head_shot >= self.stagger_v)
                self.stagger = torch.where(rocked, torch.full_like(self.stagger, self.stagger_s), (self.stagger - self.dt).clamp_min(0.0))
                weak = torch.where(self.stagger > 0.0, torch.minimum(weak, torch.full_like(weak, self.stagger_drive)), weak)
                self.drive_scale = torch.where(self.legs_out > 0.0, torch.full_like(weak, 0.04), weak)
            # A block: the other fighter's glove, coming at this one's head at punching speed, is met by a
            # glove or a forearm before it gets there.
            theirs = glove.flip(1)                                             # (N,K,2,3) the gloves coming at me
            coming = speed[..., 0].flip(1)                                     # their closing speed on my head
            near_head = (d_head.flip(1) - g["own_r_head"]) < 0.45
            to_glove = (theirs[:, :, :, None] - glove[:, :, None]).norm(dim=-1).min(-1).values       # (N,K,2)
            fc = self.geom_xpos[:, self.g_forearm]                             # (N,K,2,3) my forearms
            fax = self.geom_xmat[:, self.g_forearm][..., :, 2]
            off = theirs[:, :, :, None] - fc[:, :, None]                       # (N,K,their glove,my forearm,3)
            along = (off * fax[:, :, None]).sum(-1)
            along = torch.maximum(torch.minimum(along, self.forearm_half), -self.forearm_half)
            to_arm = (off - fax[:, :, None] * along[..., None]).norm(dim=-1).min(-1).values
            r_their = self.r_glove.flip(1)
            touching = (to_glove < r_their + self.r_glove + 0.03) | (to_arm < r_their + self.r_forearm[..., 0] + 0.03)
            blocked = touching & (coming > 2.0) & near_head
            stopped = (coming * blocked.float()).sum(-1)
        new_block = blocked & ~self.prev_block
        self.prev_block = blocked
        self.since_block = torch.where(new_block.any(-1), torch.zeros_like(self.since_block), self.since_block + self.dt)

        # ---- where the fighter is relative to its target
        to_t = g["t_body"] - pos
        dist = to_t[..., :2].norm(dim=-1)
        bearing = torch.atan2(to_t[..., 1], to_t[..., 0])
        face = torch.cos(bearing - yaw)
        near = (nearest < 0.6).float()                                         # (N,K,2) glove within reach
        reaching = (closing.max(-1).values.clamp(0.0, 8.0) * near).max(-1).values

        # ---- ringcraft: who holds the middle of the ring. Measured always (logged per fighter, so a run with the
        # weights at 0 is the baseline for one with them on); paid only with --rope-w / --cutoff-w.
        rope_q = cut_q = torch.zeros(N, K, device=self.device)
        if self.spar:
            xy = pos[..., :2]                                                  # the ring's centre is (0, 0)
            # How far into the last metre before each rope: 0 a metre or more off, about 0.7 against it (a pelvis
            # gets no nearer), so a corner, two ropes at once, counts twice.
            pinned = (xy.abs() - (self.ring_half - 1.0)).clamp(0.0, 1.0).sum(-1)                     # (N,K)
            # Under pressure: the opponent is close and nearer the centre than I am, so it has the middle.
            from_centre = xy.norm(dim=-1)
            pressed = (dist < self.s_range + 0.8).float() * ((from_centre - from_centre.flip(1)) / 0.5).clamp(0.0, 1.0)
            rope_q = pinned * pressed
            # Cutting off: standing in the opponent's way to the centre (at the opponent, the cosine between the
            # way to the centre and the way to me; 1 squarely in it) while it is near the ropes, doubled while its
            # way out closes (its speed out from the centre, -1 to 1 m/s, so stepping in and out pays as standing).
            # Only facing it within reach: at the centre the cosine is 1 against anybody, and pays nothing unless
            # the opponent is also on the ropes and in range, so it cannot be farmed from the middle.
            unit = lambda v: v / v.norm(dim=-1, keepdim=True).clamp_min(1e-3)
            opp_xy = xy.flip(1)
            in_way = (unit(-opp_xy) * unit(xy - opp_xy)).sum(-1).clamp_min(0.0)
            out_v = (lin_w.flip(1)[..., :2] * unit(opp_xy)).sum(-1).clamp(-1.0, 1.0)
            engaged = ((dist < self.s_range + 0.3) & (face > 0.7)).float()
            cut_q = engaged * in_way * pinned.flip(1) * (1.0 + out_v)

        # ---- the clinch: how much of a hug this is (0..1, half a glove each), weighted by how hurt the hugger is.
        # Measured always, paid only with --clinch-w. A glove counts resting on the trunk (within 5 cm) and not
        # punching (under 1 m/s), with the pelvises within 0.55 m. ponytail: a glove on the chest is the stand-in for
        # an arm round the back; a reach-round measure if the brains learn to pat instead of tie up.
        clinch_q = torch.zeros(N, K, device=self.device)
        if self.spar and self.daze_on:
            resting = ((gap[..., 1] < 0.05) & (speed[..., 1] < 1.0)).float().mean(-1)
            hurt = (self.daze / self.legs_at.clamp_min(1.0) * 2.0 - 1.0).clamp(0.0, 1.0)
            clinch_q = resting * (dist < 0.55).float() * hurt

        # ---- feet
        contact = g["foot_contact"]
        foot_v = (g["foot_xy"] - self.prev_foot_xy) / self.dt
        foot_sep = (g["foot_xy"][:, :, 0] - g["foot_xy"][:, :, 1]).norm(dim=-1)

        jp = self.qpos[:, self.jq]
        jv = self.qvel[:, self.jv]
        tau = self.act_force.reshape(N, K, A)
        self._spend(tau, jv)

        # ---- reward
        r = {}
        r["alive"] = torch.full((N, K), 0.15, device=self.device)
        r["upright"] = 0.25 * upright.clamp_min(0.0)
        r["height"] = -1.0 * (self.stand_height - 0.08 - pos[..., 2]).clamp_min(0.0)
        r["face"] = 0.25 * face
        r["range"] = 0.3 * torch.exp(-((dist - self.s_range) / 0.35) ** 2)
        r["close"] = 0.03 * reaching
        r["hit"] = self.hit_w * self.s_hit * dealt
        r["taken"] = -self.taken_w * self.s_taken * self.hit_w * taken
        r["block"] = self.s_block * stopped
        # Footwork that belongs to a style: one fighter is paid to walk in, another charged for being crowded.
        toward = (lin_w[..., :2] * to_t[..., :2]).sum(-1) / dist.clamp_min(0.1)
        r["style"] = self.s_press * toward.clamp(-1.0, 1.0) * (dist > self.s_range).float() \
            - self.s_crowd * (self.s_range - 0.12 - dist).clamp_min(0.0)
        if self.seek_w:
            # The style's press, for every boxer and at its own weight (Phase F2). ponytail: in a pair the nearest
            # opponent on its feet is the one opponent; a crowd mode re-picks it every step (tasks.md F1).
            r["seek"] = self.seek_w * toward.clamp(-1.0, 1.0) * (dist > self.s_range).float()
        # At 0.05 each: about 0.035 a step on a rope, at most about 0.07 and 0.14 in a corner, all under the 0.4 that
        # alive and upright pay, so neither is worth ending an episode for, nor worth more than landing punches.
        none = torch.zeros(N, K, device=self.device)
        r["rope"] = -self.rope_w * rope_q if self.rope_w else none
        r["cutoff"] = self.cutoff_w * cut_q if self.cutoff_w else none
        r["clinch"] = self.clinch_w * clinch_q if self.clinch_w else none
        if self.combo_w:
            r["combo"] = self.combo_w * combo_q
        if self.head_aim_w:
            r["aim"] = self.head_aim_w * (closing[..., 0].clamp(0.0, 8.0) * (gap[..., 0] < 0.25).float()).max(-1).values
        self_q = self._selfcontact() if self.selfcontact_w else none
        if self.selfcontact_w:
            r["self"] = -self.selfcontact_w * 100.0 * self_q                  # per centimetre pressed in
        r["lin_z"] = -0.3 * lin_b[..., 2] ** 2
        r["ang"] = -0.02 * (ang_b[..., :2] ** 2).sum(-1)
        r["act"] = -0.001 * self.effort_scale * (action ** 2).sum(-1)
        jerk = ((action - self.prev_action) ** 2).sum(-1)
        r["rate"] = -0.01 * self.effort_scale * jerk - self.action_rate_w * jerk
        r["energy"] = -2.5e-4 * self.effort_scale * self.s_energy * (tau * jv).abs().clamp_max(2000.0).sum(-1)
        # House rule: joints move no faster than a person's.
        r["qvel"] = -0.1 * (jv.abs() - self.qvel_limit).clamp(0.0, 10.0).pow(2).sum(-1)
        r["limit"] = -0.5 * ((self.joint_lo + 0.05 - jp).clamp_min(0.0) + (jp - self.joint_hi + 0.05).clamp_min(0.0)).sum(-1)
        r["slip"] = -0.2 * ((foot_v ** 2).sum(-1).clamp_max(25.0) * contact.float()).sum(-1)
        r["stance"] = -0.2 * (foot_sep - 0.30).abs()
        r["legs"] = -0.03 * ((jp - self.default_joint)[..., self.leg_idx] ** 2).sum(-1)
        if self.arms_w:
            r["arms"] = -self.arms_w * ((jp - self.default_joint)[..., self.arm_idx] ** 2).sum(-1)
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
        a["daze_sum"] += self.daze.mean()
        a["weak_sum"] += (self.drive_scale < 0.999).float().mean()
        a["reserve_sum"] += self.reserve.mean()
        ka = self._kacc
        ka["blocks"] += new_block.float().sum((0, 2))
        ka["legs"] += legs_went.float().sum(0)
        ka["hits"] += landed.float().sum((0, 2))
        ka["head"] += to_the_head.float().sum((0, 2))
        ka["speed"] += (landed.float() * arriving).sum((0, 2))
        ka["fell"] += (fell.float() * df[:, None]).sum(0)
        ka["ko"] += (r["ko"] >= self.ko_bonus).float().sum(0)
        ka["ret"] += reward.sum(0)
        ka["rope"] += rope_q.sum(0)
        ka["cutoff"] += cut_q.sum(0)
        ka["clinch"] += clinch_q.sum(0)
        ka["repeats"] += same.float().sum(0)
        ka["combos"] += combo.float().sum(0)
        ka["self"] += self_q.sum(0)

        self.prev_glove, self.prev_head, self.prev_body = glove.clone(), g["t_head"].clone(), g["t_body"].clone()
        self.prev_foot_xy = g["foot_xy"].clone()

        self._apply_reset(done)
        self._physics_forward()
        self._reseed_trackers(done)
        self._obs = self._observe(self._geometry())
        expand = lambda x: x[:, None].expand(N, K).reshape(N * K)
        cut_short = expand(timeout & ~any_fell)
        if self.spar and self.survivor_bootstrap:
            cut_short = cut_short | (any_fell[:, None] & ~fell).reshape(N * K)
        return self._obs, reward.reshape(N * K), expand(done), cut_short

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
            # Punches stopped on a glove or a forearm, per fighter per second; how dazed a fighter is on
            # average; and the share of the time a fighter's drives are weakened by it.
            "blocks_per_s": float(self._kacc["blocks"].sum().item()) / (s * self.dt * self.N * self.K),
            "legs_gone_per_min": float(self._kacc["legs"].sum().item()) / (s * self.dt * self.N * self.K) * 60.0,
            "daze": a["daze_sum"] / s,
            "weak_share": a["weak_sum"] / s,
            "reserve": a["reserve_sum"] / s,
        }
        out.update({f"rt_{t}": a[f"rt_{t}"] / s for t in self.TERMS})
        if self.hetero:
            ka = {k: v.tolist() for k, v in self._kacc.items()}
            seconds = s * self.dt * self.N
            for i, name in enumerate(self.names):
                out[f"{name}_hits_per_s"] = ka["hits"][i] / seconds
                out[f"{name}_hit_speed"] = ka["speed"][i] / max(1.0, ka["hits"][i])
                out[f"{name}_head_share"] = ka["head"][i] / max(1.0, ka["hits"][i])
                # Of the episodes that ended, the share that ended with this fighter on the floor.
                out[f"{name}_falls"] = ka["fell"][i] / n
                out[f"{name}_knockdowns_per_min"] = ka["ko"][i] / seconds * 60.0
                out[f"{name}_blocks_per_s"] = ka["blocks"][i] / seconds
                out[f"{name}_legs_gone_per_min"] = ka["legs"][i] / seconds * 60.0
                out[f"{name}_reward_per_step"] = ka["ret"][i] / (s * self.N)
                # Ringcraft, unweighted, a step: on the ropes under pressure (charged by --rope-w) and cutting off (paid by --cutoff-w).
                out[f"{name}_rope"] = ka["rope"][i] / (s * self.N)
                out[f"{name}_cutoff"] = ka["cutoff"][i] / (s * self.N)
                out[f"{name}_clinch"] = ka["clinch"][i] / (s * self.N)
                # Variety: the share of landed punches that were the last one again (same hand, same zone), and
                # combinations (the other hand within combo_s) a second; centimetres of its own parts pressed in.
                out[f"{name}_repeat_share"] = ka["repeats"][i] / max(1.0, ka["hits"][i])
                out[f"{name}_combos_per_s"] = ka["combos"][i] / seconds
                out[f"{name}_self_cm"] = ka["self"][i] * 100.0 / (s * self.N)
        for v in list(self._acc.values()) + list(self._kacc.values()):
            v.zero_()
        return out
