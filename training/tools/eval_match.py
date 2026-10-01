"""How two match policies do in MuJoCo when they act without exploration noise: the number to lay beside
Unity's (TransferProbe, -probeMode spar), which runs the same policies the same way.

    python tools/eval_match.py --a matt --b zombie [--run match] [--seconds 300] [--sample]

Runs on the CPU, one world, beside a training job. Training's own fall rate is not this number: there the
policies act with noise, the sensors are noisy and the fighters are shoved.
"""
from __future__ import annotations

import argparse
import os
import sys

import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--a", default="matt")
    ap.add_argument("--b", default="zombie")
    ap.add_argument("--run", default="match")
    ap.add_argument("--seconds", type=float, default=300.0)
    ap.add_argument("--sample", action="store_true", help="act with exploration noise, as in training")
    ap.add_argument("--ghost", action="store_true", help="gloves touch nothing: does the policy stand up to its own punches without a target to stop them?")
    ap.add_argument("--glove-friction", type=float, default=-1.0, help="gloves this slippery, whatever they touch")
    ap.add_argument("--solref", type=float, default=0.0, help="contact time constant for every shape, seconds (MuJoCo's default is 0.02)")
    args = ap.parse_args()

    from view_box import Player
    xml = os.path.join(HERE, "models", f"{args.a}_vs_{args.b}_spar.xml")
    if args.ghost or args.glove_friction >= 0.0 or args.solref > 0.0:
        import re
        import shutil
        import tempfile
        text = open(xml, "r", encoding="utf-8").read()
        extra = ""
        if args.ghost:
            extra += ' gap="1"'             # a contact further out than margin - gap is seen and then ignored
        if args.glove_friction >= 0.0:
            extra += f' priority="1" friction="{args.glove_friction} 0.005 0.0001"'
        if extra:
            text, n = re.subn(r'(<geom name="[ab]_glove_[lr]")', r"\1" + extra, text)
            assert n == 4, f"expected four gloves, found {n}"
        if args.solref > 0.0:
            text = text.replace('<geom contype="1"', f'<geom solref="{args.solref} 1" contype="1"', 1)
        tmp = tempfile.mkdtemp(prefix="pobox_eval_")
        shutil.copy(xml[: -len("_spar.xml")] + "_policy_config.json", tmp)
        xml = os.path.join(tmp, os.path.basename(xml))
        open(xml, "w", encoding="utf-8").write(text)
    ckpts = [os.path.join(HERE, "checkpoints", args.run, f"latest_{n}.pt") for n in (args.a, args.b)]
    player = Player(ckpts, xml, seed=1, deterministic=not args.sample, device="cpu")
    env = player.env
    env.get_stats()
    steps = int(args.seconds / env.dt)
    with torch.no_grad():
        for _ in range(steps):
            player.step()
    s = env.get_stats()
    what = ("ghost gloves, " if args.ghost else "") + (f"glove friction {args.glove_friction:g}, " if args.glove_friction >= 0 else "") + (f"contact time constant {args.solref:g} s, " if args.solref > 0 else "")
    print(f"mujoco, {what}iteration {player.iters}, {'with' if args.sample else 'without'} exploration noise, {args.seconds:g} s:")
    print(f"  stays up {s['ep_len_s']:.1f} s of an episode; fall rate {s['fall_rate']:.2f}; "
          f"{args.a} down in {s.get(args.a + '_falls', float('nan')):.0%} of endings, {args.b} in {s.get(args.b + '_falls', float('nan')):.0%}")
    print(f"  hits {s['hits_per_s']:.2f}/s per fighter at {s['hit_speed']:.1f} m/s "
          f"({args.a} {s.get(args.a + '_hits_per_s', float('nan')):.2f}/s, {args.b} {s.get(args.b + '_hits_per_s', float('nan')):.2f}/s); "
          f"knockdowns {s['knockdowns_per_min']:.2f}/min")


if __name__ == "__main__":
    main()
