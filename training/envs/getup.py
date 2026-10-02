"""Getting up off the canvas: a stage of its own, trained from the match policies.

    python train_box.py --stage getup --xml models/matt_vs_zombie_spar.xml --run-name getup \
                        --resume checkpoints/match/latest_matt.pt checkpoints/match/latest_zombie.pt

The model is the match model, and both fighters are in it, but here they do not fight: each learns, with
its own policy, to stand back up, and they pass through each other (the second is moved to a collision
layer of its own, as the game moves a fighter that is down for a count).

An episode is a knockdown as the game deals one (Sim/Fighter.cs): the fighter stands in its guard, is
shoved, and its joint drives go slack for a while. A short while, and it is a stumble to recover from;
a long one, and it is flat on the canvas. Then the drives come back and the rest of the episode is the
policy's. One number, the length of the slack, therefore runs the whole range from "catch yourself" to
"get up off the floor", and the policy that begins this stage already knows the easy end of it.

The observation is the match's hundred numbers, so the match policy is the starting point (house rule: a
new skill is trained from the previous rung, not from scratch). The opponent in it is a stand-in: the
other fighter's guard pose at a fixed spot a step or two away, which is exactly what the game shows a
fighter that is getting up (MujocoRing.Observe with a phantom).

Reward: how high the pelvis and the head are, how upright the trunk is, and, once standing, being in the
guard, still, and facing the stand-in, because the match policy takes over from there and has only ever
seen an opponent in front of it. Nothing ends an episode but the clock (house rule: self-contact and
contact with the floor are not failures here; they are how getting up is done).

Getting up from flat on the canvas is hard to stumble on by trial and error, so the stage starts with a
helping hand: an upward force on the trunk worth a share of the fighter's weight, which makes any
half-right movement succeed. The share comes down whenever the fighter is getting up most of the time and
goes back up when it is not, until there is none. One world in ten never gets the help, and the numbers
called "unaided" come only from those: they are what the game will see.
"""
from __future__ import annotations

import math
from typing import Dict

import mujoco
import torch
import warp as wp

from .boxing import BoxingEnv, quat_yaw, yaw_quat, to_heading


class GetUpEnv(BoxingEnv):
    TERMS = ["height", "head", "upright", "stood", "pose", "still", "face", "feet", "act", "rate", "energy", "qvel", "limit"]
    MODE = "getup"

    def __init__(self, xml_path: str, num_envs: int, slack_lo: float = 0.15, slack_hi: float = 2.6,
                 shove: float = 2.5, stand_share: float = 0.12, assist: float = 0.6, **kw):
        self.slack_lo, self.slack_hi, self.shove, self.stand_share = slack_lo, slack_hi, shove, stand_share
        self.assist0 = assist
        kw.setdefault("episode_len_s", 10.0)
        kw["push_vel"] = 0.0          # the shove is given at the start of the episode, by the reset
        kw["daze"] = False
        super().__init__(xml_path, num_envs, **kw)

    # ---- set-up --------------------------------------------------------------------------------
    def _prepare_model(self, m) -> None:
        if not self.spar:
            return
        # Fighter A stays on layer 1, fighter B goes to layer 2, and everything else (canvas, walls) is
        # on both: each fighter touches the ring and itself, and neither touches the other.
        for g in range(m.ngeom):
            name = mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_GEOM, g) or ""
            layer = 1 if name.startswith("a_") else 2 if name.startswith("b_") else 3
            m.geom_contype[g] = layer
            m.geom_conaffinity[g] = layer

    def _init_extra(self) -> None:
        N, K, dev = self.N, self.K, self.device
        z = lambda *shape: torch.zeros(*shape, device=dev)
        self.slack = z(N, K)                      # seconds of slack drives left
        self.opp_xy = z(N, K, 2)                  # where each fighter's stand-in opponent stands
        self.stood_for = z(N, K)                  # seconds it has been standing without a break
        self.first_up = torch.full((N, K), -1.0, device=dev)   # seconds after the drives came back that it first stood
        self.since_drives = z(N, K)
        self.head_stand = (self.stand_height + self.guard_head[:, 2]).view(1, K)
        self._g = {k: torch.zeros((), device=dev) for k in ("steps", "done_n", "ret_sum", "h_sum", "up_sum", "power_sum")}
        self._gk = {k: torch.zeros(K, device=dev) for k in ("up_end", "ends", "t_up", "n_up", "ret", "pose_err", "st_n")}
        self._gk_floor = {k: torch.zeros(K, device=dev) for k in ("up_end", "ends")}     # the episodes that began with a real fall
        self._gt = {t: torch.zeros((), device=dev) for t in self.TERMS}
        self.was_floored = torch.zeros(N, K, dtype=torch.bool, device=dev)
        self._gk_raw = {k: torch.zeros(K, device=dev) for k in ("up_end", "ends", "t_up", "n_up")}   # the unaided worlds
        # The helping hand: an upward force on the trunk, as a share of body weight, per fighter.
        self.xfrc = wp.to_torch(self.dw.xfrc_applied)                       # (N, nbody, 6): force, then torque
        self.b_torso = [mujoco.mj_name2id(self.m, mujoco.mjtObj.mjOBJ_BODY, p + "torso") for p in ("a_", "b_")[:K]]
        self.weight = torch.tensor([float(self.m.body_subtreemass[int(self.pelvis[k])]) * 9.81 for k in range(K)], device=dev)
        self.assist = torch.full((K,), float(self.assist0), device=dev)
        self.up_ema = torch.zeros(K, device=dev)
        self.aided = (torch.arange(N, device=dev) >= max(1, N // 10)).float()[:, None]     # (N,1); the first tenth go without

    # ---- reset ---------------------------------------------------------------------------------
    def _reset_states(self):
        N, A, dev = self.N, self.A, self.device
        q = self.default_qpos.unsqueeze(0).repeat(N, 1)
        v = torch.zeros(N, self.qvel.shape[1], device=dev)
        self._new_slack, self._new_opp = [], []
        for k in range(self.K):
            rq, rv = self.root_q[k], self.root_v[k]
            spot = self._u(N, 2, lo=-2.0, hi=2.0)
            yaw = self._u(N, lo=-math.pi, hi=math.pi)
            q[:, rq:rq + 2] = spot
            q[:, rq + 2] = self.stand_height[k] + 0.002
            q[:, rq + 3:rq + 7] = yaw_quat(yaw)
            q[:, self.jq[k]] = self.default_joint[k] + self._u(N, A, lo=-0.1, hi=0.1)
            # Some episodes are simply standing in the guard: what the end of every other episode looks
            # like, so that it is not forgotten while the floor is being learned.
            standing = self._u(N) < self.stand_share
            slack = torch.where(standing, torch.zeros(N, device=dev), self._u(N, lo=self.slack_lo, hi=self.slack_hi))
            # A long slack comes with a hard shove and a short one with a nudge, so the short ones are
            # stumbles a standing policy can already save.
            hard = (0.2 + 0.8 * slack / self.slack_hi)[:, None]
            push = torch.where(standing[:, None], self._u(N, 2, lo=-0.3, hi=0.3), self._u(N, 2, lo=-self.shove, hi=self.shove) * hard)
            v[:, rv:rv + 2] = push
            v[:, rv + 3:rv + 6] = torch.where(standing[:, None], torch.zeros(N, 3, device=dev), self._u(N, 3, lo=-2.5, hi=2.5) * hard)
            # The stand-in opponent: a step or two away, in front or to the side. The game puts it where
            # the real opponent stands, which is where the punch came from.
            bearing = yaw + self._u(N, lo=-0.6, hi=0.6)
            dist = self._u(N, lo=0.9, hi=2.0)
            opp = spot + torch.stack([torch.cos(bearing), torch.sin(bearing)], -1) * dist[:, None]
            self._new_slack.append(slack)
            self._new_opp.append(opp.clamp(-self.ring_half + 0.4, self.ring_half - 0.4))
        return q, v

    def _apply_reset(self, mask: torch.Tensor) -> None:
        super()._apply_reset(mask)
        m1 = mask[:, None]
        slack = torch.stack(self._new_slack, 1)
        self.slack = torch.where(m1, slack, self.slack)
        self.opp_xy = torch.where(mask[:, None, None], torch.stack(self._new_opp, 1), self.opp_xy)
        self.drive_scale = torch.where(self.slack > 0.0, torch.full_like(self.slack, 0.04), torch.ones_like(self.slack))
        self.stood_for = torch.where(m1, torch.zeros_like(self.stood_for), self.stood_for)
        self.first_up = torch.where(m1, torch.full_like(self.first_up, -1.0), self.first_up)
        self.since_drives = torch.where(m1, torch.zeros_like(self.since_drives), self.since_drives)
        self.was_floored = torch.where(m1, torch.zeros_like(self.was_floored), self.was_floored)

    # ---- what the fighter is shown -------------------------------------------------------------
    def _geometry(self) -> Dict[str, torch.Tensor]:
        g = super()._geometry()
        if not self.spar:
            return g
        pos = g["pos"]
        # The stand-in faces the fighter, wherever the fighter has got to.
        their_yaw = torch.atan2(pos[..., 1] - self.opp_xy[..., 1], pos[..., 0] - self.opp_xy[..., 0])   # (N,K)
        c, s = torch.cos(their_yaw), torch.sin(their_yaw)

        def place(local: torch.Tensor, stand: torch.Tensor) -> torch.Tensor:
            """A point given in the other fighter's guard frame, put in the world at the stand-in's spot."""
            x = self.opp_xy[..., 0] + c * local[..., 0] - s * local[..., 1]
            y = self.opp_xy[..., 1] + s * local[..., 0] + c * local[..., 1]
            return torch.stack([x, y, (stand + local[..., 2]).expand_as(x)], -1)

        other = lambda t: t.flip(0)                # the other fighter's numbers, indexed by this fighter
        stand = other(self.stand_height).view(1, self.K)
        g["t_head"] = place(other(self.guard_head)[None], stand)
        g["t_body"] = place(other(self.guard_body)[None], stand)
        og = other(self.guard_glove)                                                    # (K,2,3)
        g["opp_glove"] = torch.stack([place(og[None, :, i], stand) for i in range(2)], 2)   # (N,K,2,3)
        g["opp_yaw"] = their_yaw
        return g

    # ---- step ----------------------------------------------------------------------------------
    def step(self, action: torch.Tensor):
        N, K, A = self.N, self.K, self.A
        action = action.clamp(-self.action_clip, self.action_clip).reshape(N, K, A)
        self.prev_action = self.last_action
        self.last_action = action
        self._drive(action)
        lift = self.assist.view(1, K) * self.weight.view(1, K) * self.aided * (self.slack <= 0.0).float()
        for k in range(K):
            self.xfrc[:, self.b_torso[k], 2] = lift[:, k]
        self._physics_step()
        self.step_count = self.step_count + 1.0

        g = self._geometry()
        lin_w, lin_b, ang_b, grav_b = self._base(g)
        pos, yaw = g["pos"], g["yaw"]
        upright = -grav_b[..., 2]
        self.head_vel = (g["t_head"] - self.prev_head) / self.dt

        active = self.slack <= 0.0                                   # the drives are back: the policy's time
        h = (pos[..., 2] / self.stand_height).clamp(0.0, 1.05)
        head_h = (g["own_head"][..., 2] / self.head_stand).clamp(0.0, 1.05)
        standing = (h > 0.88) & (upright > 0.85) & active
        st = standing.float()
        self.was_floored = self.was_floored | (h < 0.6) | (upright < 0.4)

        jp = self.qpos[:, self.jq]
        jv = self.qvel[:, self.jv]
        tau = self.act_force.reshape(N, K, A)
        to_t = g["t_body"] - pos
        face = torch.cos(torch.atan2(to_t[..., 1], to_t[..., 0]) - yaw)
        contact = g["foot_contact"]
        foot_v = (g["foot_xy"] - self.prev_foot_xy) / self.dt

        r = {}
        r["height"] = 1.0 * h.clamp_max(1.0)
        r["head"] = 0.6 * head_h.clamp_max(1.0)
        r["upright"] = 0.4 * (upright + 1.0) * 0.5
        r["stood"] = 0.5 * st
        # Once up: in the guard, still, and facing the opponent, which is where the match policy takes over.
        # The pull back to the guard has to be felt from anywhere. As first written (0.8 * exp(-2 * mean
        # square)) it was nearly flat a long way from the guard, and Matt learned to stand up into a pose
        # of his own, arms straight and trunk twisted, 0.5 rad a joint off his guard, and hold it for ever:
        # the match policy, handed that body, fell over in a third of the trials (2026-10-02). So: a part
        # that falls off in a straight line with the distance, and a part that pays for being exact.
        pose_err = (jp - self.default_joint).abs().mean(-1)
        # (0.8 a radian was still too gentle: 0.04 a joint a step, lost in the noise of the returns; Matt's
        # stance moved 0.47 to 0.40 rad in forty minutes. 2.5 is 0.12 a joint a step.)
        r["pose"] = st * (1.5 * torch.exp(-4.0 * ((jp - self.default_joint) ** 2).mean(-1)) - 2.5 * pose_err)
        r["still"] = 0.4 * st * torch.exp(-((lin_w[..., :2] ** 2).sum(-1) + 0.1 * (ang_b ** 2).sum(-1)))
        r["face"] = 0.4 * st * face
        r["feet"] = -0.1 * st * ((foot_v ** 2).sum(-1).clamp_max(25.0) * contact.float()).sum(-1)
        r["act"] = -0.0005 * (action ** 2).sum(-1)
        r["rate"] = -0.005 * ((action - self.prev_action) ** 2).sum(-1)
        r["energy"] = -1.0e-4 * (tau * jv).abs().clamp_max(2000.0).sum(-1)
        # House rule: joints move no faster than a person's.
        r["qvel"] = -0.1 * (jv.abs() - self.qvel_limit).clamp(0.0, 10.0).pow(2).sum(-1)
        r["limit"] = -0.5 * ((self.joint_lo + 0.05 - jp).clamp_min(0.0) + (jp - self.joint_hi + 0.05).clamp_min(0.0)).sum(-1)
        # While the drives are slack nothing the policy does changes anything, so nothing is paid for it.
        reward = sum(r.values()) * active.float()

        broken = ~torch.isfinite(pos).all(-1).all(-1)
        timeout = self.step_count >= self.max_steps
        done = timeout | broken

        # ---- bookkeeping
        self.since_drives = torch.where(active, self.since_drives + self.dt, self.since_drives)
        self.stood_for = torch.where(standing, self.stood_for + self.dt, torch.zeros_like(self.stood_for))
        just_up = (self.stood_for >= 0.5) & (self.first_up < 0.0)
        self.first_up = torch.where(just_up, self.since_drives, self.first_up)
        self.episode_return = self.episode_return + reward
        self.episode_len = self.episode_len + 1.0
        df = done.float()
        a = self._g
        a["steps"] += 1.0
        a["done_n"] += df.sum()
        a["ret_sum"] += (self.episode_return.mean(1) * df).sum()
        a["h_sum"] += h.mean()
        a["up_sum"] += upright.mean()
        a["power_sum"] += (tau * jv).abs().sum(-1).clamp_max(20000.0).mean()
        for name in self.TERMS:
            self._gt[name] += r[name].mean()
        ended_up = (self.stood_for >= 0.5).float() * df[:, None]
        floored = self.was_floored.float() * df[:, None]
        gk = self._gk
        gk["up_end"] += ended_up.sum(0)
        gk["ends"] += df.sum().expand(K)
        gk["t_up"] += (self.first_up.clamp_min(0.0) * (self.first_up >= 0.0).float() * floored).sum(0)
        gk["n_up"] += ((self.first_up >= 0.0).float() * floored).sum(0)
        gk["ret"] += reward.sum(0)
        gk["pose_err"] += (pose_err * st).sum(0)
        gk["st_n"] += st.sum(0)
        self._gk_floor["up_end"] += (ended_up * floored * self.aided).sum(0)
        self._gk_floor["ends"] += (floored * self.aided).sum(0)
        raw = floored * (1.0 - self.aided)
        self._gk_raw["up_end"] += (ended_up * raw).sum(0)
        self._gk_raw["ends"] += raw.sum(0)
        got_up = (self.first_up >= 0.0).float() * raw
        self._gk_raw["t_up"] += (self.first_up.clamp_min(0.0) * got_up).sum(0)
        self._gk_raw["n_up"] += got_up.sum(0)

        self.slack = (self.slack - self.dt).clamp_min(0.0)
        self.drive_scale = torch.where(self.slack > 0.0, torch.full_like(self.slack, 0.04), torch.ones_like(self.slack))
        self.prev_glove, self.prev_head, self.prev_body = g["glove"].clone(), g["t_head"].clone(), g["t_body"].clone()
        self.prev_foot_xy = g["foot_xy"].clone()

        self._apply_reset(done)
        self._physics_forward()
        self._reseed_trackers(done)
        self._obs = self._observe(self._geometry())
        expand = lambda x: x[:, None].expand(N, K).reshape(N * K)
        return self._obs, reward.reshape(N * K), expand(done), expand(timeout & ~broken)

    def get_stats(self) -> Dict[str, float]:
        a = {k: v.item() for k, v in self._g.items()}
        n, s = max(1.0, a["done_n"]), max(1.0, a["steps"])
        floor_ends = self._gk_floor["ends"].sum().item()
        # The helping hand follows how often each fighter gets up with it.
        ends_k = self._gk_floor["ends"]
        if float(ends_k.min().item()) >= 8.0:
            rate_k = self._gk_floor["up_end"] / ends_k
            self.up_ema = 0.9 * self.up_ema + 0.1 * rate_k
            step = torch.where(self.up_ema > 0.6, -0.004, torch.where(self.up_ema < 0.3, 0.004, 0.0))
            self.assist = (self.assist + step).clamp(0.0, 0.75)
        raw_ends = self._gk_raw["ends"].sum().item()
        out = {
            "ep_return": a["ret_sum"] / n,
            "ep_len_s": self.episode_len_s,
            # Of the episodes that ended, the share that ended with the fighter on its feet; and the same
            # for only those episodes in which it really went down.
            "up_rate": self._gk["up_end"].sum().item() / max(1.0, self._gk["ends"].sum().item()),
            "up_rate_from_floor": self._gk_floor["up_end"].sum().item() / max(1.0, floor_ends),
            "floored_share": floor_ends / max(1.0, self._gk["ends"].sum().item() * 0.9),
            "up_rate_unaided": self._gk_raw["up_end"].sum().item() / max(1.0, raw_ends),
            "time_to_stand_unaided": self._gk_raw["t_up"].sum().item() / max(1.0, self._gk_raw["n_up"].sum().item()),
            "assist": self.assist.mean().item(),
            "time_to_stand": self._gk["t_up"].sum().item() / max(1.0, self._gk["n_up"].sum().item()),
            "height": a["h_sum"] / s,
            "upright": a["up_sum"] / s,
            "power": a["power_sum"] / s,
            # The match stage's headline numbers, which mean nothing here, so that anything reading a
            # status file finds them.
            "fall_rate": 0.0, "hits_per_s": 0.0, "head_share": 0.0, "hit_speed": 0.0, "hit_speed_max": 0.0,
            "distance": 0.0, "knockdowns_per_min": 0.0, "glove_speed": 0.0, "act_sat": 0.0,
        }
        out.update({f"rt_{t}": self._gt[t].item() / s for t in self.TERMS})
        for i, name in enumerate(self.names):
            out[f"{name}_up_rate"] = self._gk["up_end"][i].item() / max(1.0, self._gk["ends"][i].item())
            out[f"{name}_up_rate_from_floor"] = self._gk_floor["up_end"][i].item() / max(1.0, self._gk_floor["ends"][i].item())
            out[f"{name}_time_to_stand"] = self._gk["t_up"][i].item() / max(1.0, self._gk["n_up"][i].item())
            out[f"{name}_reward_per_step"] = self._gk["ret"][i].item() / (s * self.N)
            out[f"{name}_up_rate_unaided"] = self._gk_raw["up_end"][i].item() / max(1.0, self._gk_raw["ends"][i].item())
            out[f"{name}_assist"] = self.assist[i].item()
            # How far from its guard the boxer is while it stands, radians a joint: what the match policy is handed.
            out[f"{name}_pose_err"] = self._gk["pose_err"][i].item() / max(1.0, self._gk["st_n"][i].item())
        for group in (self._g, self._gk, self._gk_floor, self._gk_raw, self._gt):
            for v in group.values():
                v.zero_()
        return out
