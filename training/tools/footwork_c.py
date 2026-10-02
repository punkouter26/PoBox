"""The footwork stage in plain C MuJoCo, one world: the exam for standing, walking and turning, the
recording Unity is checked against, and the template for the C# that builds the observation there.

    python tools/footwork_c.py --name matt --exam --policy checkpoints/r1_matt/latest.pt --json logs/exam_r1_matt.json
    python tools/footwork_c.py --name matt --reference logs/reference_trajectory.json --policy checkpoints/r1_matt/latest.pt
    python tools/footwork_c.py --name matt --reference logs/reference_hold.json          the guard held, no policy
    python tools/footwork_c.py --name matt --check-env                                   same numbers as envs/footwork.py?

Run the exam and the recordings with the MuJoCo the game runs (PYTHONPATH=.mj350). --check-env needs the
trainer's own MuJoCo, because it builds the Warp environment beside this one.

The observation is written out again from scratch in numpy, on purpose, as tools/eval_cmujoco.py does for
the match: 100 numbers of the match, then the command. The stand-in opponent, the shoves and the cubes are
envs/footwork.py's, one world at a time.

The exam (pass marks in tasks.md; no exploration noise, the body as the file has it):
    stand   20 s under shoves of 10 to 30 N s and cubes at 5 m/s, thrown and dropped. Up at the end, 95%.
    walk    10 s at a commanded 0.3 to 1.0 m/s forward. Mean velocity within 0.15 m/s; 0.2 falls a minute.
    turn    the stand-in anywhere round it. Facing it within 15 degrees inside 3 s and still up at 5 s, 90%.
    joints  no joint's 99th-percentile speed above its limit, over all of the above.
A frail boxer's shoves and a slow boxer's commands are scaled as in training.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import sys

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
STAND, WALK, TURN = 0, 1, 2


def yaw_of(q) -> float:
    w, x, y, z = q
    return math.atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (y * y + z * z))


def heading(v, yaw: float) -> np.ndarray:
    c, s = math.cos(yaw), math.sin(yaw)
    return np.array([c * v[0] + s * v[1], -s * v[0] + c * v[1], v[2]])


class Solo:
    def __init__(self, xml: str, cfg: dict, seed: int = 1):
        self.m = m = mujoco.MjModel.from_xml_path(xml)
        self.d = mujoco.MjData(m)
        self.cfg, self.rng = cfg, np.random.default_rng(seed)
        self.order = cfg["joint_order"]
        self.A = len(self.order)
        J, G, B = mujoco.mjtObj.mjOBJ_JOINT, mujoco.mjtObj.mjOBJ_GEOM, mujoco.mjtObj.mjOBJ_BODY
        js = [mujoco.mj_name2id(m, J, "a_" + n) for n in self.order]
        self.jq, self.jv = np.array([m.jnt_qposadr[j] for j in js]), np.array([m.jnt_dofadr[j] for j in js])
        self.pelvis, self.torso_body = mujoco.mj_name2id(m, B, "a_pelvis"), mujoco.mj_name2id(m, B, "a_torso")
        self.head, self.torso = mujoco.mj_name2id(m, G, "a_head_geom"), mujoco.mj_name2id(m, G, "a_torso_geom")
        self.glove = [mujoco.mj_name2id(m, G, f"a_glove_{s}") for s in "lr"]
        self.foot = [mujoco.mj_name2id(m, G, f"a_foot_{s}_geom") for s in "lr"]
        self.cube_q, self.cube_v = [], []
        while (j := mujoco.mj_name2id(m, J, f"cube_{len(self.cube_q)}")) >= 0:
            self.cube_q.append(int(m.jnt_qposadr[j]))
            self.cube_v.append(int(m.jnt_dofadr[j]))
        self.key = m.key_qpos[0].copy()
        self.default, self.stand = self.key[self.jq], float(self.key[2])
        self.lo, self.hi = np.array(cfg["lower"]), np.array(cfg["upper"])
        self.limit = np.array(cfg["velocity_limit"])
        self.frail, self.quick = min(1.0, float(cfg.get("strength", 1.0))), float(cfg.get("speed", 1.0))
        self.ring_half = float(cfg.get("ring_half", 3.05))
        self.dt = m.opt.timestep * int(cfg.get("control_decimation", 4))
        # Where the head, the body and the gloves are from the pelvis in the guard: the stand-in's shape.
        d = self.d
        mujoco.mj_resetDataKeyframe(m, d, 0)
        mujoco.mj_forward(m, d)
        rel0 = lambda p: heading(p - d.xpos[self.pelvis], yaw_of(d.xquat[self.pelvis]))
        self.guard = [rel0(d.geom_xpos[g]) for g in (self.head, self.torso, *self.glove)]
        self.events = []                      # what was written into the simulation from outside, for a replay
        self.reset(STAND, noise=False)

    # ---- reset ---------------------------------------------------------------------------------
    def reset(self, kind: int, noise: bool = True, cmd=(0.0, 0.0, 0.0), bearing: float = None) -> None:
        m, d, u = self.m, self.d, self.rng.uniform
        mujoco.mj_resetData(m, d)
        q = self.key.copy()
        spot, yaw = (u(-1.5, 1.5, 2), u(-math.pi, math.pi)) if noise else (np.zeros(2), 0.0)
        q[0:2], q[2] = spot, self.stand + 0.002
        q[3:7] = [math.cos(yaw / 2), 0, 0, math.sin(yaw / 2)]
        d.qvel[:] = 0
        if noise:
            q[self.jq] = self.default + u(-0.06, 0.06, self.A)
            d.qvel[0:2] = u(-0.15, 0.15, 2)
        d.qpos[:] = q
        d.ctrl[:] = q[self.jq]
        mujoco.mj_forward(m, d)
        self.kind, self.cmd, self.t = kind, np.array(cmd, dtype=float), 0.0
        if bearing is None:
            bearing = u(-math.pi, math.pi) if kind == TURN else u(-0.4, 0.4) if noise else 0.0
        dist = u(0.9, 2.0) if kind == TURN else u(1.0, 1.6) if noise else 1.3
        self.opp = spot + np.array([math.cos(yaw + bearing), math.sin(yaw + bearing)]) * dist
        self.last = np.zeros(self.A)
        self.head_vel = np.zeros(3)
        self.prev_head = self.stand_in()[0]
        self.shove_left, self.shove_force = 0.0, np.zeros(3)
        self.cube_left = [0.0] * len(self.cube_q)
        self.next_cube, self.events = 0, []

    # ---- what the boxer is shown ---------------------------------------------------------------
    def stand_in(self):
        """The opponent's stand-in: its head, body and two gloves in the world, and which way it faces."""
        d = self.d
        pos, yaw = d.xpos[self.pelvis], yaw_of(d.xquat[self.pelvis])
        opp = pos[:2] + np.array([math.cos(yaw), math.sin(yaw)]) * 1.2 if self.kind == WALK else self.opp
        theirs = math.atan2(pos[1] - opp[1], pos[0] - opp[0])
        c, s = math.cos(theirs), math.sin(theirs)
        place = lambda p: np.array([opp[0] + c * p[0] - s * p[1], opp[1] + s * p[0] + c * p[1], self.stand + p[2]])
        return [place(p) for p in self.guard] + [theirs]

    def observe(self) -> np.ndarray:
        d = self.d
        pos, quat = d.xpos[self.pelvis], d.xquat[self.pelvis]
        R = d.xmat[self.pelvis].reshape(3, 3)
        yaw = yaw_of(quat)
        lin_w = d.qvel[0:3]
        foot = [1.0 if d.geom_xpos[g][2] - np.abs(d.geom_xmat[g].reshape(3, 3)[2]) @ self.m.geom_size[g] < 0.005 else 0.0 for g in self.foot]
        rel = lambda p: heading(p - pos, yaw)
        t_head, t_body, g_l, g_r, theirs = self.stand_in()
        ring = (heading(np.array([-pos[0], -pos[1], 0.0]), yaw)[:2] / self.ring_half).clip(-1.0, 1.0)
        return np.concatenate([
            R.T @ lin_w, d.qvel[3:6], R.T @ np.array([0.0, 0.0, -1.0]),
            d.qpos[self.jq] - self.default, d.qvel[self.jv], self.last,
            foot, [pos[2]],
            rel(t_head), rel(t_body), heading(self.head_vel - lin_w, yaw),
            rel(d.geom_xpos[self.glove[0]]), rel(d.geom_xpos[self.glove[1]]),
            rel(g_l), rel(g_r),
            [math.cos(theirs - yaw), math.sin(theirs - yaw)], ring,
            self.cmd,
        ]).clip(-100.0, 100.0)

    # ---- what knocks it about ------------------------------------------------------------------
    def shove(self, newton_seconds: float, angle: float, seconds: float) -> None:
        self.shove_force = np.array([math.cos(angle), math.sin(angle), 0.0]) * newton_seconds / seconds
        self.shove_left = seconds
        self.events.append({"t": self.t, "shove": self.shove_force.tolist(), "seconds": seconds})

    def throw(self, speed: float, bearing: float, dist: float, rise: float, drop_from: float = 0.0) -> None:
        """One cube from the pool at the chest, on the arc that arrives there; or let go above it."""
        d, i = self.d, self.next_cube
        if not self.cube_q or self.cube_left[i] > 0.0:
            return
        chest = d.xpos[self.pelvis] + [0.0, 0.0, 0.25]
        away = np.array([math.cos(bearing), math.sin(bearing)])
        if drop_from > 0.0:
            start, vel = chest + [0.0, 0.0, drop_from], np.zeros(3)
        else:
            t = dist / speed
            start = chest + np.array([*(away * dist), rise])
            vel = np.array([*(-away * speed), -rise / t + 0.5 * 9.81 * t])
        q, v = np.array([*start, 1.0, 0.0, 0.0, 0.0]), np.array([*vel, 0.0, 0.0, 0.0])
        d.qpos[self.cube_q[i]:self.cube_q[i] + 7], d.qvel[self.cube_v[i]:self.cube_v[i] + 6] = q, v
        self.cube_left[i] = 2.0
        self.next_cube = (i + 1) % len(self.cube_q)
        self.events.append({"t": self.t, "cube": i, "qpos": q.tolist(), "qvel": v.tolist()})

    # ---- step ----------------------------------------------------------------------------------
    def step(self, action: np.ndarray) -> bool:
        m, d = self.m, self.d
        action = np.asarray(action, dtype=float).clip(-3.0, 3.0)
        self.last = action
        d.ctrl[:] = (self.default + action * 0.5).clip(self.lo, self.hi)
        d.xfrc_applied[self.torso_body, :3] = self.shove_force if self.shove_left > 1e-6 else 0.0
        self.shove_left = max(0.0, self.shove_left - self.dt)
        for i, (q, v) in enumerate(zip(self.cube_q, self.cube_v)):
            self.cube_left[i] = max(0.0, self.cube_left[i] - self.dt)
            if self.cube_left[i] <= 0.0:       # a waiting cube is held at its parking place
                d.qpos[q:q + 7], d.qvel[v:v + 6] = self.key[q:q + 7], 0.0
        for _ in range(int(round(self.dt / m.opt.timestep))):
            mujoco.mj_step(m, d)
        self.t += self.dt
        # mj_step leaves where the bodies are (xpos, geom_xpos) one step behind qpos. The Warp stage measures
        # the stand-in's velocity from them as they are left, and then brings them up to date (its forward
        # after the step) before it observes. So does this, in that order.
        head = self.stand_in()[0]
        self.head_vel, self.prev_head = (head - self.prev_head) / self.dt, head
        mujoco.mj_kinematics(m, d)
        up = -(d.xmat[self.pelvis].reshape(3, 3).T @ np.array([0.0, 0.0, -1.0]))[2]
        return bool(d.xpos[self.pelvis][2] < self.stand * 0.6 or up < 0.4)

    def facing_error(self) -> float:
        t_body = self.stand_in()[1]
        pos = self.d.xpos[self.pelvis]
        off = math.atan2(t_body[1] - pos[1], t_body[0] - pos[0]) - yaw_of(self.d.xquat[self.pelvis])
        return abs(math.atan2(math.sin(off), math.cos(off)))


def load_policy(path: str, A: int):
    """A function from an observation to an action; with no file, the zero action (the guard, held)."""
    if not path:
        return lambda obs: np.zeros(A)
    import torch
    from ppo import PPO, PPOConfig
    ppo = PPO(103, A, 1, "cpu", PPOConfig())
    ppo.load(path)

    def act(obs: np.ndarray) -> np.ndarray:
        with torch.no_grad():
            x = ppo.obs_rms.normalize(torch.tensor(obs, dtype=torch.float32)[None], ppo.cfg.obs_clip)
            return ppo.model.actor(x)[0].numpy().astype(float)
    return act


def exam(solo: Solo, act, seeds: int, episodes: int) -> dict:
    u = solo.rng.uniform
    speeds, stood, walked, v_err, walk_falls, walk_s, turned, turns = [], 0, 0, [], 0, 0.0, 0, 0
    for seed in range(seeds):
        solo.rng = np.random.default_rng(1000 + seed)
        u = solo.rng.uniform
        for _ in range(episodes):
            # stand: 20 s, something every two to four seconds
            solo.reset(STAND)
            nxt, fell = u(1.0, 2.0), False
            while solo.t < 20.0 and not fell:
                if solo.t >= nxt:
                    nxt = solo.t + u(2.0, 4.0)
                    if u() < 0.5:
                        solo.shove(u(10.0, 30.0) * solo.frail, u(-math.pi, math.pi), u(0.1, 0.2))
                    elif u() < 0.3:
                        solo.throw(0.0, 0.0, 0.0, 0.0, drop_from=u(1.2, 2.0))
                    else:
                        solo.throw(5.0, u(-math.pi, math.pi), u(2.0, 3.0), u(-0.2, 0.4))
                fell = solo.step(act(solo.observe()))
                speeds.append(np.abs(solo.d.qvel[solo.jv]) / solo.limit)
            stood += int(not fell)
            # walk: 10 s at one forward speed; the mark is taken from the third second on
            want = u(0.3, 1.0) * solo.quick
            solo.reset(WALK, cmd=(want, 0.0, 0.0))
            vel, fell = [], False
            while solo.t < 10.0 and not fell:
                fell = solo.step(act(solo.observe()))
                if solo.t > 2.0:
                    vel.append(heading(solo.d.qvel[0:3], yaw_of(solo.d.xquat[solo.pelvis]))[:2])
                speeds.append(np.abs(solo.d.qvel[solo.jv]) / solo.limit)
            walk_falls += int(fell)
            walk_s += solo.t
            walked += 1
            v_err.append(float(np.linalg.norm(np.mean(vel, 0) - [want, 0.0])) if vel else want)
            # turn: the stand-in anywhere round it
            solo.reset(TURN)
            faced, fell = -1.0, False
            while solo.t < 5.0 and not fell:
                fell = solo.step(act(solo.observe()))
                if faced < 0.0 and solo.facing_error() < math.radians(15.0):
                    faced = solo.t
                speeds.append(np.abs(solo.d.qvel[solo.jv]) / solo.limit)
            turns += 1
            turned += int(not fell and 0.0 <= faced <= 3.0)
    n = seeds * episodes
    p99 = np.percentile(np.array(speeds), 99, axis=0)
    worst = int(np.argmax(p99))
    out = {
        "stand": {"up_at_20s": stood / n, "pass": stood / n >= 0.95},
        "walk": {"velocity_error": float(np.mean(v_err)), "falls_per_min": walk_falls / walk_s * 60.0,
                 "pass": float(np.mean(v_err)) <= 0.15 and walk_falls / walk_s * 60.0 <= 0.2},
        "turn": {"faced_in_3s": turned / turns, "pass": turned / turns >= 0.90},
        "joints": {"worst": solo.order[worst], "p99_of_limit": float(p99[worst]), "pass": bool(p99[worst] <= 1.0)},
        "episodes_each": n, "mujoco": mujoco.__version__,
    }
    return out


def reference(solo: Solo, act, seconds: float, cube_at: float) -> dict:
    """One run from the keyframe, a row a control step: everything Unity needs to do the same and compare."""
    solo.reset(STAND, noise=False)
    d, rows, thrown = solo.d, [], False
    while solo.t < seconds - 1e-9:
        if not thrown and cube_at > 0.0 and solo.t >= cube_at:
            solo.throw(5.0, math.pi, 2.5, 0.2)      # from in front, 5 m/s
            thrown = True
        obs = solo.observe()
        action = act(obs)
        fell = solo.step(action)
        rows.append({"t": round(solo.t, 4), "obs": obs.tolist(), "action": np.asarray(action).tolist(),
                     "ctrl": d.ctrl.tolist(), "torque": d.actuator_force.tolist(),
                     "root_pos": d.qpos[0:3].tolist(), "root_quat": d.qpos[3:7].tolist(),
                     "root_linvel": d.qvel[0:3].tolist(), "root_angvel": d.qvel[3:6].tolist(),
                     "joint_pos": d.qpos[solo.jq].tolist(), "joint_vel": d.qvel[solo.jv].tolist(),
                     "foot_contact": [bool(x) for x in solo.observe()[72:74]], "fell": fell})
    return {"mujoco": mujoco.__version__, "control_dt": solo.dt, "timestep": float(solo.m.opt.timestep), "joint_order": solo.order,
            "start_qpos": solo.key.tolist(), "stand_in_xy": solo.opp.tolist(), "command": solo.cmd.tolist(),
            "events": solo.events, "rows": rows,
            "note": "obs and action are before the step of that row; everything else is after it. Frames are MuJoCo's (z up)."}


def check_env(xml: str, cfg: dict) -> None:
    """The same body, state and actions in the Warp environment and here: do the 103 numbers agree?"""
    import torch
    from envs.footwork import FootworkEnv
    env = FootworkEnv(xml, 1, device="cpu", seed=3, obs_noise=0.0, disturb=0.0, randomise=0.0, episode_len_s=60.0)
    env.step_count[:] = 0.0          # the episode clock starts anywhere; a time-out in mid-check would move the body
    solo = Solo(xml, cfg)
    rng = np.random.default_rng(0)
    worst = {}
    for kind in (STAND, WALK, TURN):
        env.kind[:] = kind
        env.cmd[:] = torch.tensor([0.6, -0.2, 0.5]) if kind == WALK else 0.0
        env._physics_forward()
        env._reseed_trackers(torch.ones(1, dtype=torch.bool))
        env.head_vel[:] = 0.0
        solo.kind, solo.cmd, solo.opp = kind, env.cmd[0, 0].numpy().astype(float), env.opp_xy[0, 0].numpy().astype(float)
        solo.d.qpos[:], solo.d.qvel[:] = env.qpos[0].numpy(), env.qvel[0].numpy()
        mujoco.mj_forward(solo.m, solo.d)
        solo.last, solo.head_vel, solo.prev_head = env.last_action[0, 0].numpy().astype(float), np.zeros(3), solo.stand_in()[0]
        gap = [float(np.abs(env._observe(env._geometry())[0].numpy() - solo.observe()).max())]
        for _ in range(5):
            a = rng.uniform(-1.0, 1.0, solo.A)
            obs = env.step(torch.tensor(a, dtype=torch.float32)[None])[0][0].numpy()
            solo.step(a)
            gap.append(float(np.abs(obs - solo.observe()).max()))
        worst[("stand", "walk", "turn")[kind]] = gap
        print(f"{('stand', 'walk', 'turn')[kind]:5s} largest difference in the observation: at the same state {gap[0]:.1e}; after 1 to 5 steps of the same actions " + " ".join(f"{g:.1e}" for g in gap[1:]))
    assert all(g[0] < 1e-3 for g in worst.values()), "the two observations are not the same numbers"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", default="matt")
    ap.add_argument("--models", default=os.path.join(HERE, "models", "v2"))
    ap.add_argument("--policy", default="", help="a 103-input checkpoint; none is the zero action")
    ap.add_argument("--exam", action="store_true")
    ap.add_argument("--seeds", type=int, default=10)
    ap.add_argument("--episodes", type=int, default=3, help="of each kind, a seed")
    ap.add_argument("--json", default="")
    ap.add_argument("--reference", default="", help="write the recording Unity is checked against here")
    ap.add_argument("--seconds", type=float, default=5.0)
    ap.add_argument("--cube-at", type=float, default=2.0, help="reference: throw one cube at this time; 0 for none")
    ap.add_argument("--check-env", action="store_true")
    args = ap.parse_args()
    xml = os.path.join(args.models, f"{args.name}_solo.xml")
    cfg = json.load(open(os.path.join(args.models, f"{args.name}_policy_config.json"), encoding="utf-8"))
    if args.check_env:
        return check_env(xml, cfg)
    solo = Solo(xml, cfg)
    act = load_policy(args.policy, solo.A)
    if args.reference:
        ref = reference(solo, act, args.seconds, args.cube_at)
        ref["model"], ref["policy"] = os.path.basename(xml), os.path.basename(args.policy) or "zero action"
        os.makedirs(os.path.dirname(os.path.abspath(args.reference)), exist_ok=True)
        with open(args.reference, "w", encoding="utf-8") as fh:
            json.dump(ref, fh)
        up = sum(1 for r in ref["rows"] if not r["fell"]) * solo.dt
        print(f"wrote {args.reference}: {len(ref['rows'])} control steps, MuJoCo {mujoco.__version__}, up for {up:.2f} s, {len(ref['events'])} event(s)")
    if args.exam:
        out = exam(solo, act, args.seeds, args.episodes)
        out["boxer"], out["policy"] = args.name, args.policy
        mark = lambda b: "pass" if b else "FAIL"
        print(f"{args.name}, C MuJoCo {mujoco.__version__}, {out['episodes_each']} episodes of each kind:")
        print(f"  stand   up at 20 s in {out['stand']['up_at_20s']:.0%} (mark 95%)                                  {mark(out['stand']['pass'])}")
        print(f"  walk    velocity off by {out['walk']['velocity_error']:.2f} m/s (0.15), {out['walk']['falls_per_min']:.2f} falls a minute (0.2)        {mark(out['walk']['pass'])}")
        print(f"  turn    facing in 3 s and still up in {out['turn']['faced_in_3s']:.0%} (90%)                          {mark(out['turn']['pass'])}")
        print(f"  joints  fastest against its limit: {out['joints']['worst']} at {out['joints']['p99_of_limit']:.2f} of it (1.00)          {mark(out['joints']['pass'])}")
        if args.json:
            with open(args.json, "w", encoding="utf-8") as fh:
                json.dump(out, fh, indent=1)


if __name__ == "__main__":
    main()
