"""Getting up off the canvas in plain C MuJoCo, on the CPU: the stage envs/getup.py describes, written out
again in numpy for one boxer at a time.

Why there are two. envs/getup.py runs on MuJoCo Warp and needs the GPU. This one needs none of it: a few
hundred worlds, each an ordinary MjData, stepped on a couple of threads. It is about a tenth as fast, and
it is what there is while the GPU is busy with something else; it is also the library the game itself
steps the fighters with, so what is learned or measured here is measured in the game's own physics.
tools/exam.py uses it to test a boxer; train_getup_cpu.py trains in it.

One boxer, from models/NAME_bag.xml with the bag made untouchable. Everything else is envs/getup.py's:
an episode starts with the boxer in its guard, shoved, with slack joint drives for a while (a short while
is a stumble, a long one puts it flat on the canvas); then the drives come back and the rest of ten
seconds is the policy's. The observation is the match's hundred numbers; the opponent in it is a stand-in
standing in its guard a step or two away, which is what the game shows a fighter that is getting up.
Reward, the helping hand and the statistics have the same names and the same numbers as in envs/getup.py.
"""
from __future__ import annotations

import json
import math
import os
from concurrent.futures import ThreadPoolExecutor
from typing import Dict, List, Optional

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TERMS = ["height", "head", "upright", "stood", "pose", "still", "face", "feet", "act", "rate", "energy", "qvel", "limit"]


def quat_yaw(q: np.ndarray) -> np.ndarray:
    w, x, y, z = q[..., 0], q[..., 1], q[..., 2], q[..., 3]
    return np.arctan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (y * y + z * z))


def rotate_inverse(q: np.ndarray, v: np.ndarray) -> np.ndarray:
    """q (..., 4) wxyz, v (..., 3) world -> body."""
    w, xyz = q[..., :1], q[..., 1:]
    t = 2.0 * np.cross(xyz, v)
    return v - w * t + np.cross(xyz, t)


def to_heading(v: np.ndarray, yaw: np.ndarray) -> np.ndarray:
    c, s = np.cos(yaw), np.sin(yaw)
    return np.stack([c * v[..., 0] + s * v[..., 1], -s * v[..., 0] + c * v[..., 1], v[..., 2]], -1)


def boxer_names() -> List[str]:
    return sorted(f[:-8] for f in os.listdir(os.path.join(HERE, "models")) if f.endswith("_bag.xml") and f[:-8] != "dev")


def load_alone(name: str):
    """A boxer by itself: its bag model with the bag turned to air. Returns the model and its config."""
    m = mujoco.MjModel.from_xml_path(os.path.join(HERE, "models", f"{name}_bag.xml"))
    for g in range(m.ngeom):
        if (mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_GEOM, g) or "").startswith("bag"):
            m.geom_contype[g] = 0
            m.geom_conaffinity[g] = 0
            m.geom_rgba[g, 3] = 0.0
    with open(os.path.join(HERE, "models", f"{name}_policy_config.json"), "r", encoding="utf-8") as f:
        cfg = json.load(f)
    return m, cfg


def guard_of(name: str) -> Dict[str, np.ndarray]:
    """Where a boxer's head, body and gloves are from its pelvis when it stands in its guard, in its own
    heading, and how high its pelvis stands: what a stand-in for it looks like to another boxer."""
    m, _ = load_alone(name)
    d = mujoco.MjData(m)
    mujoco.mj_resetDataKeyframe(m, d, 0)
    mujoco.mj_forward(m, d)
    G, B = mujoco.mjtObj.mjOBJ_GEOM, mujoco.mjtObj.mjOBJ_BODY
    pel = mujoco.mj_name2id(m, B, "a_pelvis")
    yaw = float(quat_yaw(d.xquat[pel]))
    rel = lambda p: to_heading(np.asarray(p) - d.xpos[pel], np.asarray(yaw))
    gid = lambda n: mujoco.mj_name2id(m, G, "a_" + n)
    return {"head": rel(d.geom_xpos[gid("head_geom")]), "body": rel(d.geom_xpos[gid("torso_geom")]),
            "glove": np.stack([rel(d.geom_xpos[gid("glove_l")]), rel(d.geom_xpos[gid("glove_r")])]),
            "stand": float(d.xpos[pel][2])}


class CGetUp:
    def __init__(self, name: str, num_envs: int, threads: int = 2, seed: int = 0, slack_lo: float = 0.15,
                 slack_hi: float = 2.6, shove: float = 2.5, stand_share: float = 0.12, assist: float = 0.6,
                 episode_len_s: float = 10.0, obs_noise: float = 1.0, action_scale: float = 0.5,
                 action_clip: float = 3.0, unaided_share: float = 0.1, stand_ins: Optional[List[str]] = None):
        self.name, self.N = name, num_envs
        self.m, self.cfg = load_alone(name)
        m, cfg, N = self.m, self.cfg, num_envs
        self.rng = np.random.default_rng(seed)
        self.slack_lo, self.slack_hi, self.shove, self.stand_share = slack_lo, slack_hi, shove, stand_share
        self.obs_noise, self.action_scale, self.action_clip = obs_noise, action_scale, action_clip
        self.dt = float(m.opt.timestep) * 4
        self.episode_len_s = float(episode_len_s)
        self.max_steps = int(episode_len_s / self.dt)
        self.ring_half = float(cfg.get("ring_half", 3.05))
        order = cfg["joint_order"]
        self.A = A = len(order)
        self.obs_dim = 9 + 3 * A + 3 + 9 + 6 + 6 + 2 + 2

        J, G, B, U = mujoco.mjtObj.mjOBJ_JOINT, mujoco.mjtObj.mjOBJ_GEOM, mujoco.mjtObj.mjOBJ_BODY, mujoco.mjtObj.mjOBJ_ACTUATOR
        root = mujoco.mj_name2id(m, J, "a_root")
        self.rq, self.rv = int(m.jnt_qposadr[root]), int(m.jnt_dofadr[root])
        js = [mujoco.mj_name2id(m, J, "a_" + n) for n in order]
        self.jq = np.array([m.jnt_qposadr[j] for j in js])
        self.jv = np.array([m.jnt_dofadr[j] for j in js])
        self.act = np.array([mujoco.mj_name2id(m, U, "a_" + n) for n in order])
        assert (self.act >= 0).all() and m.nu == A
        self.pelvis = mujoco.mj_name2id(m, B, "a_pelvis")
        self.torso = mujoco.mj_name2id(m, B, "a_torso")
        self.g_head = mujoco.mj_name2id(m, G, "a_head_geom")
        self.g_glove = [mujoco.mj_name2id(m, G, f"a_glove_{s}") for s in "lr"]
        self.g_foot = [mujoco.mj_name2id(m, G, f"a_foot_{s}_geom") for s in "lr"]
        self.foot_half = m.geom_size[self.g_foot[0]].copy()
        self.key = m.key_qpos[0].copy()
        self.default = self.key[self.jq].copy()
        self.stand = float(self.key[self.rq + 2])
        self.lo, self.hi = np.array(cfg["lower"]), np.array(cfg["upper"])
        self.qvel_limit = np.array(cfg["velocity_limit"])
        self.weight = float(m.body_subtreemass[self.pelvis]) * 9.81
        own = guard_of(name)
        self.head_stand = self.stand + float(own["head"][2])

        # The stand-in opponent is any boxer of the roster: the one getting up should not care which.
        ins = [guard_of(n) for n in (stand_ins or boxer_names())]
        self._ins = {k: np.stack([g[k] for g in ins]) for k in ("head", "body", "glove")}
        self._ins["stand"] = np.array([g["stand"] for g in ins])

        self.datas = [mujoco.MjData(m) for _ in range(N)]
        self.pool = ThreadPoolExecutor(max_workers=max(1, threads))
        self.threads = max(1, threads)
        z = lambda *s: np.zeros(s)
        self.QJ, self.VJ, self.TAU = z(N, A), z(N, A), z(N, A)
        self.ROOTV, self.POS, self.QUAT = z(N, 6), z(N, 3), z(N, 4)
        self.HEAD, self.GLOVE, self.FOOT, self.FOOTZ = z(N, 3), z(N, 2, 3), z(N, 2, 3), z(N, 2, 3)
        self.CTRL, self.LIFT = z(N, A), z(N)
        self.last_action, self.prev_action = z(N, A), z(N, A)
        self.step_count, self.slack, self.drive_scale = z(N), z(N), np.ones(N)
        self.opp_xy, self.opp_who = z(N, 2), np.zeros(N, dtype=np.int64)
        self.stood_for, self.first_up, self.since_drives = z(N), np.full(N, -1.0), z(N)
        self.was_floored = np.zeros(N, dtype=bool)
        self.prev_t_head, self.head_vel, self.prev_foot_xy = z(N, 3), z(N, 3), z(N, 2, 2)
        self.episode_return = z(N)
        self.assist0 = assist
        self.assist = float(assist)
        self.up_ema = 0.0
        n_raw = max(1, int(round(N * unaided_share))) if assist > 0.0 else N
        self.aided = (np.arange(N) >= n_raw).astype(np.float64)        # the first tenth go without
        self._zero_stats()
        self._floor = {"up_end": 0.0, "ends": 0.0}
        self.reset()

    # ---- the worlds --------------------------------------------------------------------------------
    def _gather(self, which=None) -> None:
        jq, jv, act, rv = self.jq, self.jv, self.act, self.rv
        for i in (range(self.N) if which is None else which):
            d = self.datas[i]
            self.QJ[i] = d.qpos[jq]
            self.VJ[i] = d.qvel[jv]
            self.TAU[i] = d.actuator_force[act]
            self.ROOTV[i] = d.qvel[rv:rv + 6]
            self.POS[i] = d.xpos[self.pelvis]
            self.QUAT[i] = d.xquat[self.pelvis]
            self.HEAD[i] = d.geom_xpos[self.g_head]
            for s in range(2):
                self.GLOVE[i, s] = d.geom_xpos[self.g_glove[s]]
                self.FOOT[i, s] = d.geom_xpos[self.g_foot[s]]
                self.FOOTZ[i, s] = d.geom_xmat[self.g_foot[s]][6:9]

    def _work(self, lo: int, hi: int) -> None:
        m, act, torso = self.m, self.act, self.torso
        for i in range(lo, hi):
            d = self.datas[i]
            d.ctrl[act] = self.CTRL[i]
            d.xfrc_applied[torso, 2] = self.LIFT[i]
            mujoco.mj_step(m, d, 4)

    def _physics(self) -> None:
        n, t = self.N, self.threads
        if t == 1:
            self._work(0, n)
            return
        cuts = [n * k // t for k in range(t + 1)]
        list(self.pool.map(lambda k: self._work(cuts[k], cuts[k + 1]), range(t)))

    def _reset(self, which) -> None:
        rng, m, A = self.rng, self.m, self.A
        for i in which:
            d = self.datas[i]
            q = self.key.copy()
            v = np.zeros(m.nv)
            spot = rng.uniform(-2.0, 2.0, 2)
            yaw = rng.uniform(-math.pi, math.pi)
            q[self.rq:self.rq + 2] = spot
            q[self.rq + 2] = self.stand + 0.002
            q[self.rq + 3:self.rq + 7] = [math.cos(yaw / 2), 0.0, 0.0, math.sin(yaw / 2)]
            q[self.jq] = self.default + rng.uniform(-0.1, 0.1, A)
            standing = rng.uniform() < self.stand_share
            slack = 0.0 if standing else rng.uniform(self.slack_lo, self.slack_hi)
            hard = 0.2 + 0.8 * slack / self.slack_hi
            v[self.rv:self.rv + 2] = rng.uniform(-0.3, 0.3, 2) if standing else rng.uniform(-self.shove, self.shove, 2) * hard
            if not standing:
                v[self.rv + 3:self.rv + 6] = rng.uniform(-2.5, 2.5, 3) * hard
            bearing = yaw + rng.uniform(-0.6, 0.6)
            dist = rng.uniform(0.9, 2.0)
            opp = spot + np.array([math.cos(bearing), math.sin(bearing)]) * dist
            self.opp_xy[i] = opp.clip(-self.ring_half + 0.4, self.ring_half - 0.4)
            self.opp_who[i] = rng.integers(len(self._ins["stand"]))
            mujoco.mj_resetData(m, d)
            d.qpos[:] = q
            d.qvel[:] = v
            d.ctrl[self.act] = q[self.jq]
            mujoco.mj_forward(m, d)
            self.slack[i] = slack
            self.drive_scale[i] = 0.04 if slack > 0.0 else 1.0
            self.last_action[i] = 0.0
            self.prev_action[i] = 0.0
            self.step_count[i] = 0.0
            self.stood_for[i] = 0.0
            self.first_up[i] = -1.0
            self.since_drives[i] = 0.0
            self.was_floored[i] = False
            self.episode_return[i] = 0.0
            self.head_vel[i] = 0.0
        self._gather(which)
        g = self._phantom()
        idx = np.asarray(list(which), dtype=np.int64)
        self.prev_t_head[idx] = g["t_head"][idx]
        self.prev_foot_xy[idx] = self.FOOT[idx][..., :2]

    def reset(self) -> np.ndarray:
        self._reset(range(self.N))
        # Spread the episode clock, so the population does not time out in lockstep for ever.
        self.step_count = self.rng.integers(0, max(1, self.max_steps), self.N).astype(np.float64)
        return self._observe(self._phantom())

    # ---- what the boxer is shown -------------------------------------------------------------------
    def _phantom(self) -> Dict[str, np.ndarray]:
        pos = self.POS
        their_yaw = np.arctan2(pos[:, 1] - self.opp_xy[:, 1], pos[:, 0] - self.opp_xy[:, 0])
        c, s = np.cos(their_yaw), np.sin(their_yaw)
        stand = self._ins["stand"][self.opp_who]

        def place(local: np.ndarray) -> np.ndarray:
            return np.stack([self.opp_xy[:, 0] + c * local[:, 0] - s * local[:, 1],
                             self.opp_xy[:, 1] + s * local[:, 0] + c * local[:, 1], stand + local[:, 2]], -1)

        glove = self._ins["glove"][self.opp_who]                      # (N,2,3)
        return {"t_head": place(self._ins["head"][self.opp_who]), "t_body": place(self._ins["body"][self.opp_who]),
                "opp_glove": np.stack([place(glove[:, 0]), place(glove[:, 1])], 1), "opp_yaw": their_yaw}

    def _feet(self):
        sole = self.FOOT[..., 2] - (np.abs(self.FOOTZ) * self.foot_half).sum(-1)
        return sole < 0.005, self.FOOT[..., :2]

    def _observe(self, g: Dict[str, np.ndarray]) -> np.ndarray:
        N = self.N
        pos, quat = self.POS, self.QUAT
        yaw = quat_yaw(quat)
        lin_w = self.ROOTV[:, :3]
        lin_b = rotate_inverse(quat, lin_w)
        grav_b = rotate_inverse(quat, np.broadcast_to(np.array([0.0, 0.0, -1.0]), (N, 3)))
        rel = lambda p: to_heading(p - pos, yaw)
        contact, _ = self._feet()
        own_gloves = to_heading(self.GLOVE - pos[:, None], yaw[:, None]).reshape(N, 6)
        opp_gloves = to_heading(g["opp_glove"] - pos[:, None], yaw[:, None]).reshape(N, 6)
        dyaw = g["opp_yaw"] - yaw
        ring = to_heading(np.concatenate([-pos[:, :2], np.zeros((N, 1))], -1), yaw)[:, :2] / self.ring_half
        obs = np.concatenate([lin_b, self.ROOTV[:, 3:6], grav_b, self.QJ - self.default, self.VJ, self.last_action,
                              contact.astype(np.float64), pos[:, 2:3],
                              rel(g["t_head"]), rel(g["t_body"]), to_heading(self.head_vel - lin_w, yaw),
                              own_gloves, opp_gloves, np.stack([np.cos(dyaw), np.sin(dyaw)], -1), ring], -1)
        if self.obs_noise > 0.0:
            obs = obs + self.rng.standard_normal(obs.shape) * (0.02 * self.obs_noise)
        return np.nan_to_num(obs).clip(-100.0, 100.0).astype(np.float32)

    # ---- step --------------------------------------------------------------------------------------
    def step(self, action: np.ndarray):
        N = self.N
        action = np.asarray(action, dtype=np.float64).clip(-self.action_clip, self.action_clip)
        self.prev_action = self.last_action
        self.last_action = action
        want = (self.default + action * self.action_scale).clip(self.lo, self.hi)
        self.CTRL = self.QJ + self.drive_scale[:, None] * (want - self.QJ)
        self.LIFT = self.assist * self.weight * self.aided * (self.slack <= 0.0)
        self._physics()
        self._gather()
        self.step_count += 1.0

        g = self._phantom()
        pos, quat = self.POS, self.QUAT
        yaw = quat_yaw(quat)
        lin_w, ang_b = self.ROOTV[:, :3], self.ROOTV[:, 3:6]
        upright = 1.0 - 2.0 * (quat[:, 1] ** 2 + quat[:, 2] ** 2)
        self.head_vel = (g["t_head"] - self.prev_t_head) / self.dt

        active = self.slack <= 0.0
        h = (pos[:, 2] / self.stand).clip(0.0, 1.05)
        head_h = (self.HEAD[:, 2] / self.head_stand).clip(0.0, 1.05)
        standing = (h > 0.88) & (upright > 0.85) & active
        st = standing.astype(np.float64)
        self.was_floored |= (h < 0.6) | (upright < 0.4)

        jp, jv, tau = self.QJ, self.VJ, self.TAU
        to_t = g["t_body"] - pos
        face = np.cos(np.arctan2(to_t[:, 1], to_t[:, 0]) - yaw)
        contact, foot_xy = self._feet()
        foot_v = (foot_xy - self.prev_foot_xy) / self.dt

        r = {}
        r["height"] = 1.0 * np.minimum(h, 1.0)
        r["head"] = 0.6 * np.minimum(head_h, 1.0)
        r["upright"] = 0.4 * (upright + 1.0) * 0.5
        r["stood"] = 0.5 * st
        pose_err = np.abs(jp - self.default).mean(-1)
        r["pose"] = st * (1.5 * np.exp(-4.0 * ((jp - self.default) ** 2).mean(-1)) - 2.5 * pose_err)
        r["still"] = 0.4 * st * np.exp(-((lin_w[:, :2] ** 2).sum(-1) + 0.1 * (ang_b ** 2).sum(-1)))
        r["face"] = 0.4 * st * face
        r["feet"] = -0.1 * st * (np.minimum((foot_v ** 2).sum(-1), 25.0) * contact).sum(-1)
        r["act"] = -0.0005 * (action ** 2).sum(-1)
        r["rate"] = -0.005 * ((action - self.prev_action) ** 2).sum(-1)
        r["energy"] = -1.0e-4 * np.minimum(np.abs(tau * jv), 2000.0).sum(-1)
        r["qvel"] = -0.1 * ((np.abs(jv) - self.qvel_limit).clip(0.0, 10.0) ** 2).sum(-1)
        r["limit"] = -0.5 * (np.maximum(self.lo + 0.05 - jp, 0.0) + np.maximum(jp - self.hi + 0.05, 0.0)).sum(-1)
        reward = sum(r.values()) * active

        broken = ~np.isfinite(pos).all(-1)
        timeout = self.step_count >= self.max_steps
        done = timeout | broken

        # ---- bookkeeping
        self.since_drives = np.where(active, self.since_drives + self.dt, self.since_drives)
        self.stood_for = np.where(standing, self.stood_for + self.dt, 0.0)
        just_up = (self.stood_for >= 0.5) & (self.first_up < 0.0)
        self.first_up = np.where(just_up, self.since_drives, self.first_up)
        self.episode_return += reward
        df = done.astype(np.float64)
        a = self._s
        a["steps"] += 1.0
        a["done_n"] += df.sum()
        a["ret_sum"] += (self.episode_return * df).sum()
        a["h_sum"] += h.mean()
        a["up_sum"] += upright.mean()
        a["power_sum"] += np.minimum(np.abs(tau * jv).sum(-1), 20000.0).mean()
        a["rew_sum"] += reward.sum()
        a["pose_err"] += (pose_err * st).sum()
        a["st_n"] += st.sum()
        for name in TERMS:
            a["rt_" + name] += r[name].mean()
        ended_up = (self.stood_for >= 0.5) * df
        floored = self.was_floored * df
        got = (self.first_up >= 0.0).astype(np.float64)
        a["up_end"] += ended_up.sum()
        a["t_up"] += (np.maximum(self.first_up, 0.0) * got * floored).sum()
        a["n_up"] += (got * floored).sum()
        self._floor["up_end"] += (ended_up * floored * self.aided).sum()
        self._floor["ends"] += (floored * self.aided).sum()
        a["floor_up_end"] += (ended_up * floored * self.aided).sum()
        a["floor_ends"] += (floored * self.aided).sum()
        raw = floored * (1.0 - self.aided)
        a["raw_up_end"] += (ended_up * raw).sum()
        a["raw_ends"] += raw.sum()
        a["raw_t_up"] += (np.maximum(self.first_up, 0.0) * got * raw).sum()
        a["raw_n_up"] += (got * raw).sum()

        self.slack = np.maximum(self.slack - self.dt, 0.0)
        self.drive_scale = np.where(self.slack > 0.0, 0.04, 1.0)
        self.prev_t_head = g["t_head"].copy()
        self.prev_foot_xy = foot_xy.copy()

        ends = np.nonzero(done)[0]
        if len(ends):
            self._reset(ends)
        obs = self._observe(self._phantom())
        return obs, reward.astype(np.float32), done, timeout & ~broken

    # ---- numbers -----------------------------------------------------------------------------------
    def _zero_stats(self) -> None:
        keys = ["steps", "done_n", "ret_sum", "h_sum", "up_sum", "power_sum", "rew_sum", "up_end", "t_up", "n_up", "pose_err", "st_n",
                "floor_up_end", "floor_ends", "raw_up_end", "raw_ends", "raw_t_up", "raw_n_up"] + ["rt_" + t for t in TERMS]
        self._s = {k: 0.0 for k in keys}

    def get_stats(self) -> Dict[str, float]:
        a = self._s
        n, s = max(1.0, a["done_n"]), max(1.0, a["steps"])
        # The helping hand follows how often the boxer gets up with it. With a few hundred worlds an
        # iteration ends too few episodes to judge by, so the count is carried until there are enough.
        if self._floor["ends"] >= 16.0 and self.assist0 > 0.0:
            rate = self._floor["up_end"] / self._floor["ends"]
            self.up_ema = 0.9 * self.up_ema + 0.1 * rate
            self.assist = float(np.clip(self.assist + (-0.004 if self.up_ema > 0.6 else 0.004 if self.up_ema < 0.3 else 0.0), 0.0, 0.75))
            self._floor = {"up_end": 0.0, "ends": 0.0}
        out = {
            "ep_return": a["ret_sum"] / n,
            "up_rate": a["up_end"] / n,
            "up_rate_from_floor": a["floor_up_end"] / max(1.0, a["floor_ends"]),
            "floored_share": (a["floor_ends"] + a["raw_ends"]) / n,
            "up_rate_unaided": a["raw_up_end"] / max(1.0, a["raw_ends"]),
            "unaided_ends": a["raw_ends"],
            "time_to_stand_unaided": a["raw_t_up"] / max(1.0, a["raw_n_up"]),
            "time_to_stand": a["t_up"] / max(1.0, a["n_up"]),
            "assist": self.assist,
            "up_ema": self.up_ema,
            "height": a["h_sum"] / s,
            "upright": a["up_sum"] / s,
            "power": a["power_sum"] / s,
            "reward_per_step": a["rew_sum"] / (s * self.N),
            "pose_err": a["pose_err"] / max(1.0, a["st_n"]),
        }
        out.update({f"rt_{t}": a["rt_" + t] / s for t in TERMS})
        self._zero_stats()
        return out
