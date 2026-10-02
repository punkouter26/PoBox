"""A policy that reads 100 numbers, made to read 103, without changing anything it does.

    python tools/widen_policy.py checkpoints/handoff/latest_matt.pt checkpoints/widened/latest_matt.pt

The footwork stage (envs/footwork.py) puts a three-number command on the end of the match's observation.
The first layer of the actor and of the critic gets a column of zeros for each new number, so the widened
policy returns exactly what the old one did whatever the command says; training then teaches it to listen.
The optimiser's state is dropped: its moments are shaped for the old layer.
"""
from __future__ import annotations

import argparse
import os
import sys

import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from ppo import ActorCritic  # noqa: E402


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("dst")
    ap.add_argument("--to", type=int, default=103)
    args = ap.parse_args()
    ck = torch.load(args.src, map_location="cpu", weights_only=False)
    sd, rms = ck["model"], ck["obs_rms"]
    old = sd["actor.0.weight"].shape[1]
    add = args.to - old
    if add <= 0:
        raise SystemExit(f"{args.src} already reads {old} numbers")
    before = ActorCritic(old, sd["log_std"].shape[0])
    before.load_state_dict(sd)
    for net in ("actor", "critic"):
        w = sd[f"{net}.0.weight"]
        sd[f"{net}.0.weight"] = torch.cat([w, torch.zeros(w.shape[0], add)], 1)
    rms["mean"] = torch.cat([rms["mean"].cpu(), torch.zeros(add)])
    rms["var"] = torch.cat([rms["var"].cpu(), torch.ones(add)])
    ck.pop("opt", None)
    ck.setdefault("extra", {})["obs_dim"] = args.to

    after = ActorCritic(args.to, sd["log_std"].shape[0])
    after.load_state_dict(sd)
    x = torch.randn(64, args.to) * 3.0
    with torch.no_grad():
        assert torch.equal(before.actor(x[:, :old]), after.actor(x)) and torch.equal(before.critic(x[:, :old]), after.critic(x)), \
            "the widened policy does not answer as the old one did"
    os.makedirs(os.path.dirname(os.path.abspath(args.dst)), exist_ok=True)
    torch.save(ck, args.dst)
    print(f"wrote {args.dst}: {old} -> {args.to} inputs, same actions and value on 64 random observations")


if __name__ == "__main__":
    main()
