"""The exam: can a boxer do what a bout needs? Behaviour by behaviour, in plain C MuJoCo (the library the
game steps the fighters with), without exploration noise.

    python tools/exam.py                         every boxer that has a policy, against every opponent it has a model with
    python tools/exam.py --only matt zombie      only these boxers (each still meets all its opponents unless --among)
    python tools/exam.py --among                 only pairings inside --only
    python tools/exam.py --rounds 4 --trials 30 --json logs/exam.json

Which policy a boxer brings is read from logs/policies.json if it is there ({"matt": {"match": path,
"getup": path}}), and otherwise worked out: a veteran's newest kept run, a league boxer's entry in
logs/league.state.json, and the newest get-up checkpoint with the boxer's name on it.

    Footing       rounds of 45 s with nothing weakening anybody: falls nobody caused, per minute
    Attack        the same rounds: punches landed per second, and how fast
    Guard         the same rounds: of the punches that reached its head or were stopped on a glove or a
                  forearm, the share stopped; and head shots taken per second
    Chin          the same rounds under the daze rule: how often its legs are taken, per minute
    Getting up    a knockdown as the game deals one (a shove, two seconds of slack drives, the other boxer
                  held in its guard and passed through): on its feet for half a second, unaided, in time
    Carrying on   after a second and a half on its feet the match policies take over and the other boxer is
                  let go: still up 3 s later

A hit, a block and the daze are envs/boxing.py's, written out again in numpy. The pass marks are at the top.
"""
from __future__ import annotations

import argparse
import glob
import json
import math
import os
import sys
import time

import mujoco
import numpy as np
import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "tools"))
from ppo import PPO, PPOConfig  # noqa: E402
from eval_cmujoco import Ring  # noqa: E402

VETERANS = ("matt", "zombie")
MARKS = {
    "footing_falls_per_min": 0.2,       # at most
    "attack_hits_per_s": 0.5,           # at least
    "attack_speed": 4.0,                # at least, times the boxer's own speed factor
    "guard_block_share": 0.3,           # at least, or ...
    "guard_head_taken_per_s": 0.3,      # ... at most this many head shots taken a second
    "chin_legs_per_min": 1.5,           # at most
    "getup_rate": 0.8, "getup_within_s": 8.0,
    "getup_rate_frail": 0.6, "getup_within_s_frail": 9.0,     # a boxer with under 0.7 of an adult's strength
    "carry_on_rate": 0.9,
}
HANDOVER_S = 1.5        # seconds on its feet before the match policy takes the body back from the get-up policy
DAZE = {"tau": 2.5, "lo": 14.0, "hi": 42.0, "weak": 0.45, "body": 0.3, "legs_out": 0.7}


def load_policy(path: str, A: int) -> PPO:
    ppo = PPO(100, A, 1, "cpu", PPOConfig())
    ppo.load(path)
    return ppo


def act(ppo: PPO, obs: np.ndarray) -> np.ndarray:
    with torch.no_grad():
        return ppo.model.actor(ppo.obs_rms.normalize(torch.from_numpy(obs.astype(np.float32))[None], 10.0))[0].numpy()


class ExamRing(Ring):
    def __init__(self, xml: str, cfg: dict, seed: int = 1):
        super().__init__(xml, cfg, seed)
        m = self.m
        G = mujoco.mjtObj.mjOBJ_GEOM
        self.forearm = [[mujoco.mj_name2id(m, G, p + f"forearm_{s}_geom") for s in "lr"] for p in ("a_", "b_")]
        self.r_glove = [m.geom_size[self.glove[k][0]][0] for k in range(2)]
        self.r_head = [m.geom_size[self.head[k]][0] for k in range(2)]
        self.r_torso = [m.geom_size[self.torso[k]][0] for k in range(2)]
        self.half = [m.geom_size[self.torso[k]][1] for k in range(2)]
        self.r_arm = [m.geom_size[self.forearm[k][0]][0] for k in range(2)]
        self.arm_half = [m.geom_size[self.forearm[k][0]][1] for k in range(2)]
        self.layers = (m.geom_contype.copy(), m.geom_conaffinity.copy())
        self.own_geoms = [[g for g in range(m.ngeom) if (mujoco.mj_id2name(m, G, g) or "").startswith(p)] for p in ("a_", "b_")]
        self.scale = np.ones(2)

    def place(self, spots, yaws, jitter: float = 0.0):
        m, d = self.m, self.d
        mujoco.mj_resetData(m, d)
        q = self.key.copy()
        for k in range(2):
            rq = self.rq[k]
            q[rq:rq + 2] = spots[k]
            q[rq + 2] = self.stand[k] + 0.002
            q[rq + 3:rq + 7] = [math.cos(yaws[k] / 2), 0, 0, math.sin(yaws[k] / 2)]
            q[self.jq[k]] = self.default[k] + self.rng.uniform(-jitter, jitter, self.A)
        d.qpos[:] = q
        d.qvel[:] = 0
        d.ctrl[:] = np.concatenate([q[self.jq[k]] for k in range(2)])
        mujoco.mj_forward(m, d)
        self.last[:] = 0
        self.head_vel[:] = 0
        self.scale[:] = 1.0
        for k in range(2):
            self.prev_head[k] = d.geom_xpos[self.head[1 - k]]

    def at_marks(self):
        self.place([(-0.72, -0.72), (0.72, 0.72)], [math.pi / 4, math.pi / 4 + math.pi])

    def ghost(self, k: int, on: bool) -> None:
        """Fighter k passes through the other one (and still stands on the canvas)."""
        m = self.m
        m.geom_contype[:], m.geom_conaffinity[:] = self.layers
        if on:
            for g in range(m.ngeom):
                layer = 1 if g in self.own_geoms[1 - k] else 2 if g in self.own_geoms[k] else 3
                m.geom_contype[g] = layer
                m.geom_conaffinity[g] = layer

    def pin(self, k: int, spot, yaw: float) -> None:
        d = self.d
        rq, rv = self.rq[k], self.rv[k]
        d.qpos[rq:rq + 2] = spot
        d.qpos[rq + 2] = self.stand[k] + 0.002
        d.qpos[rq + 3:rq + 7] = [math.cos(yaw / 2), 0, 0, math.sin(yaw / 2)]
        d.qvel[rv:rv + 6] = 0
        d.qpos[self.jq[k]] = self.default[k]
        d.qvel[self.jv[k]] = 0

    def drive(self, actions) -> None:
        """One control step. actions[k] None = hold the guard pose."""
        d = self.d
        want = []
        for k in range(2):
            if actions[k] is None:
                self.last[k] = 0
                want.append(self.default[k].copy())
                continue
            a = np.asarray(actions[k]).clip(-3.0, 3.0)
            self.last[k] = a
            w = (self.default[k] + a * 0.5).clip(self.lo[k], self.hi[k])
            if self.scale[k] < 0.999:
                q = d.qpos[self.jq[k]]
                w = q + self.scale[k] * (w - q)
            want.append(w)
        d.ctrl[:] = np.concatenate(want)
        mujoco.mj_step(self.m, d, 4)
        for k in range(2):
            head = d.geom_xpos[self.head[1 - k]]
            self.head_vel[k] = (head - self.prev_head[k]) / self.dt
            self.prev_head[k] = head.copy()

    def posture(self, k: int):
        d = self.d
        p = d.xpos[self.pelvis[k]]
        up = d.xmat[self.pelvis[k]].reshape(3, 3)[2, 2]
        return p[2] / self.stand[k], up


class Punches:
    """The trainer's hit and block, one control step at a time, for both fighters."""

    def __init__(self, ring: ExamRing):
        self.r = ring
        d = ring.d
        self.armed = np.ones((2, 2), bool)
        self.cool = np.zeros((2, 2))
        self.prev_closing = np.zeros((2, 2, 2))
        self.prev_glove = np.array([[d.geom_xpos[ring.glove[k][i]].copy() for i in range(2)] for k in range(2)])
        self.prev_tb = np.array([d.geom_xpos[ring.torso[1 - k]].copy() for k in range(2)])
        self.prev_th = np.array([d.geom_xpos[ring.head[1 - k]].copy() for k in range(2)])
        self.prev_block = np.zeros((2, 2), bool)

    def step(self):
        """Returns per fighter: hits landed [(zone, speed)], and the punches it stopped (count)."""
        r, d, dt = self.r, self.r.d, self.r.dt
        landed = [[], []]
        stopped = [0, 0]
        for k in range(2):
            o = 1 - k
            th, tb = d.geom_xpos[r.head[o]].copy(), d.geom_xpos[r.torso[o]].copy()
            ax = d.geom_xmat[r.torso[o]].reshape(3, 3)[:, 2]
            vh, vb = (th - self.prev_th[k]) / dt, (tb - self.prev_tb[k]) / dt
            self.prev_th[k], self.prev_tb[k] = th, tb
            for i in range(2):
                g = d.geom_xpos[r.glove[k][i]].copy()
                vg = (g - self.prev_glove[k, i]) / dt
                self.prev_glove[k, i] = g
                to_h = th - g
                dh = np.linalg.norm(to_h)
                along = float(np.clip((g - tb) @ ax, -r.half[o], r.half[o]))
                to_b = tb + ax * along - g
                db = np.linalg.norm(to_b)
                gap = np.array([dh - r.r_glove[k] - r.r_head[o], db - r.r_glove[k] - r.r_torso[o]])
                nh, nb = to_h / max(dh, 1e-4), to_b / max(db, 1e-4)
                closing = np.array([min((vg - vh) @ nh, vg @ nh), min((vg - vb) @ nb, vg @ nb)])
                speed = np.clip(np.maximum(closing, self.prev_closing[k, i]), 0.0, 9.0)
                self.prev_closing[k, i] = closing
                zone = int(gap.argmin())
                self.cool[k, i] = max(0.0, self.cool[k, i] - dt)
                hit = gap[zone] < 0.012 and self.armed[k, i] and speed[zone] > 1.0 and self.cool[k, i] <= 0.0
                self.armed[k, i] = (self.armed[k, i] and not hit) or gap[zone] > 0.30
                if hit:
                    self.cool[k, i] = 0.25
                    landed[k].append((zone, float(speed[zone])))
                # Is this glove, coming at the other one's head, met by a glove or a forearm of theirs?
                near_head = (dh - r.r_head[o]) < 0.45
                touching = False
                for j in range(2):
                    if np.linalg.norm(g - d.geom_xpos[r.glove[o][j]]) < r.r_glove[k] + r.r_glove[o] + 0.03:
                        touching = True
                    fc = d.geom_xpos[r.forearm[o][j]]
                    fax = d.geom_xmat[r.forearm[o][j]].reshape(3, 3)[:, 2]
                    off = g - fc
                    al = float(np.clip(off @ fax, -r.arm_half[o], r.arm_half[o]))
                    if np.linalg.norm(off - fax * al) < r.r_glove[k] + r.r_arm[o] + 0.03:
                        touching = True
                blocked = touching and speed[0] > 2.0 and near_head
                if blocked and not self.prev_block[k, i]:
                    stopped[o] += 1
                self.prev_block[k, i] = blocked
        return landed, stopped


def rounds(ring: ExamRing, pols, n_rounds: int, round_s: float, daze_on: bool) -> list:
    """Box. Returns per fighter a dict of counts over all the rounds."""
    out = [dict(hits=0, head=0, speed=0.0, blocks=0, head_taken=0, falls_own=0, falls_hit=0, legs=0, seconds=0.0) for _ in range(2)]
    dt = ring.dt
    for _ in range(n_rounds):
        ring.at_marks()
        p = Punches(ring)
        daze, legs_out, low, since_taken = np.zeros(2), np.zeros(2), np.zeros(2), np.full(2, 99.0)
        for _ in range(int(round_s / dt)):
            ring.drive([act(pols[k], ring.observe(k)) for k in range(2)])
            landed, stopped = p.step()
            received = np.zeros(2)
            since_taken += dt
            for k in range(2):
                o = 1 - k
                out[k]["seconds"] += dt
                out[k]["blocks"] += stopped[k]
                for zone, speed in landed[k]:
                    out[k]["hits"] += 1
                    out[k]["speed"] += speed
                    out[k]["head"] += int(zone == 0)
                    out[o]["head_taken"] += int(zone == 0)
                    received[o] += speed * (1.0 if zone == 0 else DAZE["body"])
                    since_taken[o] = 0.0
            if daze_on:
                daze = daze * math.exp(-dt / DAZE["tau"]) + received
                for k in range(2):
                    if daze[k] >= DAZE["hi"] and legs_out[k] <= 0.0:
                        legs_out[k], daze[k] = DAZE["legs_out"], 0.0
                        out[k]["legs"] += 1
                    else:
                        legs_out[k] = max(0.0, legs_out[k] - dt)
                    weak = 1.0 - DAZE["weak"] * float(np.clip((daze[k] - DAZE["lo"]) / (DAZE["hi"] - DAZE["lo"]), 0.0, 1.0))
                    ring.scale[k] = 0.04 if legs_out[k] > 0.0 else weak
            down = False
            for k in range(2):
                h, up = ring.posture(k)
                low[k] = low[k] + dt if (h < 0.6 or up < 0.4) else 0.0
                if low[k] > 0.4:
                    out[k]["falls_hit" if since_taken[k] < 1.5 or legs_out[k] > 0.0 else "falls_own"] += 1
                    down = True
            if down:
                # The game counts and stands them up; here they are put back and the round goes on.
                ring.reset()
                p = Punches(ring)
                daze[:], legs_out[:], low[:] = 0.0, 0.0, 0.0
                since_taken[:] = 99.0
    return out


def getting_up(ring: ExamRing, k: int, getup: PPO, match, trials: int, within_s: float) -> dict:
    """Fighter k is knocked down; the other is held in its guard. Then both box on."""
    o = 1 - k
    rng, dt = ring.rng, ring.dt
    res = dict(trials=0, floored=0, up=0, t_up=0.0, carried=0)
    for _ in range(trials):
        spot = rng.uniform(-1.6, 1.6, 2)
        yaw = rng.uniform(-math.pi, math.pi)
        bearing = yaw + rng.uniform(-0.6, 0.6)
        their = (spot + np.array([math.cos(bearing), math.sin(bearing)]) * rng.uniform(0.9, 2.0)).clip(-2.6, 2.6)
        spots, yaws = [None, None], [0.0, 0.0]
        spots[k], yaws[k] = spot, yaw
        spots[o] = their
        ring.ghost(k, True)
        ring.place(spots, yaws, jitter=0.1)
        d = ring.d
        slack = rng.uniform(1.6, 2.6)
        hard = 0.2 + 0.8 * slack / 2.6
        rv = ring.rv[k]
        d.qvel[rv:rv + 2] = rng.uniform(-2.5, 2.5, 2) * hard
        d.qvel[rv + 3:rv + 6] = rng.uniform(-2.5, 2.5, 3) * hard
        floored, stood_for, t, up_at = False, 0.0, 0.0, -1.0
        while t < slack + within_s + (HANDOVER_S if up_at >= 0.0 else 0.0):
            here = d.xpos[ring.pelvis[k]]
            face = math.atan2(here[1] - their[1], here[0] - their[0])
            ring.pin(o, their, face)
            mujoco.mj_forward(ring.m, d)
            actions = [None, None]
            ring.scale[k] = 0.04 if t < slack else 1.0
            actions[k] = act(getup, ring.observe(k))
            ring.drive(actions)
            t += dt
            h, up = ring.posture(k)
            floored = floored or h < 0.6 or up < 0.4
            standing = t >= slack and h > 0.88 and up > 0.85
            stood_for = stood_for + dt if standing else 0.0
            if stood_for >= 0.5 and up_at < 0.0:
                up_at = t - slack
            # On its feet is half a second standing. The get-up policy keeps the body for HANDOVER_S of
            # standing before the match policy has it: handed over at half a second, with the body still
            # settling, Matt's match policy fell in a third of the trials whatever the other boxer did;
            # at a second and a half, in one in fifteen. The game waits the same time (Sim/Fighter.cs).
            if stood_for >= HANDOVER_S:
                break
        res["trials"] += 1
        if not floored:
            continue
        res["floored"] += 1
        if up_at < 0.0:
            continue
        res["up"] += 1
        res["t_up"] += up_at
        if stood_for < HANDOVER_S:
            continue            # got up, and was down again before the match could be handed the body
        # The match takes over: the other boxer is let go, bodies touch again, three seconds of boxing.
        ring.ghost(k, False)
        ring.scale[:] = 1.0
        ring.last[:] = 0
        low, fell = 0.0, False
        for _ in range(int(3.0 / dt)):
            ring.drive([act(match[j], ring.observe(j)) for j in range(2)])
            h, up = ring.posture(k)
            low = low + dt if (h < 0.6 or up < 0.4) else 0.0
            if low > 0.4:
                fell = True
                break
        res["carried"] += int(not fell)
    ring.ghost(k, False)
    return res


def policies() -> dict:
    book = {}
    path = os.path.join(HERE, "logs", "policies.json")
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as f:
            book = json.load(f)
    ck = os.path.join(HERE, "checkpoints")
    for n in VETERANS:
        if "match" not in book.get(n, {}):
            for run in ("guard", "defend", "match"):
                p = os.path.join(ck, run, f"latest_{n}.pt")
                if os.path.exists(p) and not os.path.exists(os.path.join(ck, run, "REJECTED.txt")):
                    book.setdefault(n, {})["match"] = p
                    break
    state = os.path.join(HERE, "logs", "league.state.json")
    if os.path.exists(state):
        with open(state, "r", encoding="utf-8") as f:
            for n, p in json.load(f).get("ck", {}).items():
                if "match" not in book.get(n, {}) and os.path.basename(os.path.dirname(p)).startswith("r"):
                    book.setdefault(n, {})["match"] = p
    for n in list(book):
        if "getup" not in book[n]:
            found = [p for p in glob.glob(os.path.join(ck, "*getup*", f"latest_{n}.pt"))
                     if not os.path.exists(os.path.join(os.path.dirname(p), "REJECTED.txt"))]
            if found:
                book[n]["getup"] = max(found, key=os.path.getmtime)
    return book


def pair_model(a: str, b: str):
    for x, y in ((a, b), (b, a)):
        stem = os.path.join(HERE, "models", f"{x}_vs_{y}")
        if os.path.exists(stem + "_spar.xml"):
            return stem, x, y
    return None, a, b


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", nargs="*", default=[])
    ap.add_argument("--among", action="store_true")
    ap.add_argument("--rounds", type=int, default=4)
    ap.add_argument("--round-s", type=float, default=45.0)
    ap.add_argument("--trials", type=int, default=30, help="knockdowns per boxer in the get-up test")
    ap.add_argument("--skip", nargs="*", default=[], choices=["box", "chin", "getup"])
    ap.add_argument("--json", default="")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--walls", action="store_true", help="always the trainer's ring (walls), even where the game's (ropes) exists")
    args = ap.parse_args()

    book = policies()
    names = sorted(n for n in book if "match" in book[n])
    subjects = [n for n in names if not args.only or n in args.only]
    if not subjects:
        raise SystemExit("no boxer with a match policy to examine")
    pairs = []
    for i, a in enumerate(names):
        for b in names[i + 1:]:
            if not (a in subjects or b in subjects) or (args.among and not (a in subjects and b in subjects)):
                continue
            stem, x, y = pair_model(a, b)
            if stem:
                pairs.append((stem, x, y))
    cfgs = {n: json.load(open(os.path.join(HERE, "models", f"{n}_policy_config.json"), encoding="utf-8")) for n in names}
    rows = {n: {"vs": {}, "getup": None} for n in subjects}
    t0 = time.time()
    for stem, x, y in pairs:
        # The game's ring, with its ropes, where it has been exported; otherwise the trainer's, with walls.
        xml = stem + ("_ring.xml" if os.path.exists(stem + "_ring.xml") and not args.walls else "_spar.xml")
        ring = ExamRing(xml, json.load(open(stem + "_policy_config.json", encoding="utf-8")), seed=args.seed)
        pols = [load_policy(book[n]["match"], ring.A) for n in (x, y)]
        plain = rounds(ring, pols, args.rounds, args.round_s, False) if "box" not in args.skip else None
        dazed = rounds(ring, pols, args.rounds, args.round_s, True) if "chin" not in args.skip else None
        for k, n in enumerate((x, y)):
            if n not in subjects:
                continue
            e = {}
            if plain:
                p, minutes = plain[k], plain[k]["seconds"] / 60.0
                e.update(falls_own_per_min=p["falls_own"] / minutes, falls_hit_per_min=p["falls_hit"] / minutes,
                         hits_per_s=p["hits"] / p["seconds"], speed=p["speed"] / max(1, p["hits"]),
                         head_share=p["head"] / max(1, p["hits"]), blocks_per_s=p["blocks"] / p["seconds"],
                         head_taken_per_s=p["head_taken"] / p["seconds"],
                         block_share=p["blocks"] / max(1, p["blocks"] + p["head_taken"]))
            if dazed:
                q = dazed[k]
                e.update(legs_per_min=q["legs"] / (q["seconds"] / 60.0), falls_dazed_per_min=(q["falls_own"] + q["falls_hit"]) / (q["seconds"] / 60.0))
            rows[n]["vs"][y if k == 0 else x] = e
        # Getting up is tested once per boxer, against the first opponent it has a model with.
        if "getup" not in args.skip:
            for k, n in enumerate((x, y)):
                if n in subjects and rows[n]["getup"] is None and "getup" in book[n]:
                    frail = float(cfgs[n].get("strength", 1.0)) < 0.7
                    within = MARKS["getup_within_s_frail" if frail else "getup_within_s"]
                    g = getting_up(ring, k, load_policy(book[n]["getup"], ring.A), pols, args.trials, within)
                    g.update(within_s=within, frail=frail, against=y if k == 0 else x, policy=os.path.relpath(book[n]["getup"], HERE))
                    rows[n]["getup"] = g
        print(f"  {x} v {y}: done ({time.time() - t0:.0f} s)", flush=True)

    # ---- verdicts
    report = {}
    print()
    for n in subjects:
        vs, cfg = rows[n]["vs"], cfgs[n]
        style = cfg.get("style", {})
        guards = n in VETERANS or float(style.get("block_w", 0.0)) > 0.0
        speed_mark = MARKS["attack_speed"] * float(cfg.get("speed", 1.0))
        mean = lambda key: float(np.mean([v[key] for v in vs.values() if key in v])) if any(key in v for v in vs.values()) else float("nan")
        worst = lambda key: float(np.max([v[key] for v in vs.values() if key in v])) if any(key in v for v in vs.values()) else float("nan")
        lines = {}
        if "box" not in args.skip and vs:
            lines["footing"] = (worst("falls_own_per_min") <= MARKS["footing_falls_per_min"], f"{worst('falls_own_per_min'):.2f} falls a minute at worst")
            lines["attack"] = (mean("hits_per_s") >= MARKS["attack_hits_per_s"] and mean("speed") >= speed_mark,
                               f"{mean('hits_per_s'):.2f} a second at {mean('speed'):.1f} m/s (mark {speed_mark:.1f}), {mean('head_share'):.0%} to the head")
            ok = mean("block_share") >= MARKS["guard_block_share"] or mean("head_taken_per_s") <= MARKS["guard_head_taken_per_s"]
            lines["guard"] = (ok if guards else None, f"stops {mean('block_share'):.0%} of what comes at its head ({mean('blocks_per_s'):.2f} blocks a second), "
                                                      f"takes {mean('head_taken_per_s'):.2f} head shots a second" + ("" if guards else "; built without a guard"))
        if "chin" not in args.skip and vs:
            lines["chin"] = (mean("legs_per_min") <= MARKS["chin_legs_per_min"], f"legs taken {mean('legs_per_min'):.2f} a minute (worst {worst('legs_per_min'):.2f})")
        if "getup" not in args.skip:
            g = rows[n]["getup"]
            if g is None:
                lines["getting up"] = (False, "no get-up policy")
                lines["carrying on"] = (False, "no get-up policy")
            else:
                rate = g["up"] / max(1, g["floored"])
                need = MARKS["getup_rate_frail" if g["frail"] else "getup_rate"]
                lines["getting up"] = (rate >= need and g["floored"] > 0, f"{g['up']} of {g['floored']} knockdowns, in {g['t_up'] / max(1, g['up']):.1f} s on average "
                                                                           f"(mark {need:.0%} within {g['within_s']:g} s; against {g['against']})")
                carried = g["carried"] / max(1, g["up"])
                lines["carrying on"] = (carried >= MARKS["carry_on_rate"] and g["up"] > 0, f"{g['carried']} of {g['up']} still up 3 s after the match policy took over")
        passed = all(ok for ok, _ in lines.values() if ok is not None)
        report[n] = {"passed": passed, "lines": {k: {"ok": ok, "text": text} for k, (ok, text) in lines.items()}, "vs": vs, "getup": rows[n]["getup"],
                     "match": os.path.relpath(book[n]["match"], HERE)}
        print(f"{n.upper():9s} {'PASS' if passed else 'FAIL'}   ({os.path.relpath(book[n]['match'], HERE)})")
        for k, (ok, text) in lines.items():
            print(f"    {k:12s} {'pass' if ok else '  - ' if ok is None else 'FAIL'}  {text}")
        for opp, v in vs.items():
            if "hits_per_s" in v:
                print(f"        v {opp:8s} lands {v['hits_per_s']:.2f}/s at {v['speed']:.1f} m/s, {v['head_share']:.0%} head | blocks {v['blocks_per_s']:.2f}/s, "
                      f"head shots taken {v['head_taken_per_s']:.2f}/s | falls {v['falls_own_per_min']:.2f}/min"
                      + (f" | dazed: legs {v['legs_per_min']:.2f}/min" if "legs_per_min" in v else ""))
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump({"when": time.strftime("%Y-%m-%d %H:%M:%S"), "marks": MARKS, "rounds": args.rounds, "round_s": args.round_s,
                       "trials": args.trials, "boxers": report}, f, indent=1)
    print(f"\n{sum(1 for r in report.values() if r['passed'])} of {len(report)} boxers pass. {time.time() - t0:.0f} s.")


if __name__ == "__main__":
    main()
