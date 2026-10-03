"""The boxing exam (R3 to R7 in tasks.md) on the retrofit's bodies: tools/exam.py's rounds, punches, daze and
get-up test, unchanged, run on models/v2 with the 103-input policies (the match's 100 numbers and the command's
3, which a bout leaves at zero). Run it with the MuJoCo the game runs:

    PYTHONPATH=.mj350 python tools/exam_v2.py                     every boxer with a match policy
    PYTHONPATH=.mj350 python tools/exam_v2.py --only matt zombie --json logs/exam_v2.json

Which policies: a boxer's match policy is the newest of checkpoints/m<round>_<name>/latest_<name>.pt (m1, m2, ...), then
checkpoints/wide/match_<name>.pt (the old one widened to 103, before any training on the new body); its get-up
policy the newest checkpoints/u1_*/latest_<name>.pt, then checkpoints/wide/getup_<name>.pt. --book FILE
({"matt": {"match": path, "getup": path}}) overrides either.
"""
from __future__ import annotations

import glob
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(HERE, "tools"))
import exam as E  # noqa: E402
from ppo import PPO, PPOConfig  # noqa: E402

MODELS = os.path.join(HERE, "models", "v2")
# Lil Matt was taken out of the game on 2026-10-03 (his models and checkpoints stay in training/). Trump came back the same day on a new mesh.
NAMES = ("grandma", "grandpa", "matt", "nick", "trump", "zombie")
OBS = 103


def load_policy(path: str, A: int) -> PPO:
    ppo = PPO(OBS, A, 1, "cpu", PPOConfig())
    ppo.load(path)
    return ppo


_act = E.act


def act(ppo: PPO, obs: np.ndarray) -> np.ndarray:
    # The ring observes the match's 100 numbers; the command (vx, vy, wz) is zero in a bout.
    return _act(ppo, np.concatenate([obs, np.zeros(OBS - len(obs), obs.dtype)]))


def policies() -> dict:
    ck = os.path.join(HERE, "checkpoints")
    book = {}
    override = {}
    if "--book" in sys.argv:
        with open(sys.argv[sys.argv.index("--book") + 1], "r", encoding="utf-8") as f:
            override = json.load(f)
    for n in NAMES:
        trained = glob.glob(os.path.join(ck, f"m[0-9]_{n}", f"latest_{n}.pt"))
        match = max(trained, key=os.path.getmtime) if trained else os.path.join(ck, "wide", f"match_{n}.pt")
        ups = glob.glob(os.path.join(ck, "u1_*", f"latest_{n}.pt"))
        getup = max(ups, key=os.path.getmtime) if ups else os.path.join(ck, "wide", f"getup_{n}.pt")
        e = {}
        if os.path.exists(match):
            e["match"] = match
        if os.path.exists(getup):
            e["getup"] = getup
        e.update(override.get(n, {}))
        if e:
            book[n] = e
    return book


def pair_model(a: str, b: str):
    for x, y in ((a, b), (b, a)):
        stem = os.path.join(MODELS, f"{x}_vs_{y}")
        if os.path.exists(stem + "_spar.xml"):
            return stem, x, y
    return None, a, b


if __name__ == "__main__":
    if "--book" in sys.argv:
        i = sys.argv.index("--book")
        del sys.argv[i:i + 2]
    # The old exam with the new bodies, configs and policies put in: its rounds and get-up test call these by name.
    E.HERE = HERE
    E.load_policy, E.act, E.policies, E.pair_model = load_policy, act, policies, pair_model
    E.VETERANS = NAMES          # every boxer on the new line is trained with a guard
    _join = os.path.join

    def join(*parts):
        # main() reads each boxer's config from HERE/models; on this line they are in models/v2.
        if len(parts) == 3 and parts[0] == HERE and parts[1] == "models":
            return _join(MODELS, parts[2])
        return _join(*parts)

    E.os.path.join = join
    E.main()
