"""The trainer's flags, checked on the CPU: with every new flag off a stage is what it was, number for number.

    python tools/smoke_flags.py --save logs/smoke_baseline.npz      before a change to envs/
    python tools/smoke_flags.py --compare logs/smoke_baseline.npz   after it: fails on any difference
    python tools/smoke_flags.py --flags                             each new flag on, once: runs, finite, and moves its term

Four worlds, thirty control steps of seeded random actions, in each of: footwork (Matt alone), the match plain, the
match under every rule of a living body and a punch, and get-up (Matt and Zombie). Warp on the CPU, MuJoCo as the venv has it.
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from envs.footwork import FootworkEnv  # noqa: E402
from envs.match import GetUpMatchEnv, MatchEnv  # noqa: E402

RULES = dict(daze=True, fatigue_j=8000.0, speed_limit=True, hit_push=0.6, stagger_v=5.0, hurt_full=400.0,
             body_drain=0.0025, block_w=0.5, survivor_bootstrap=True)


def scenarios(models: str, extra: dict) -> dict:
    solo, pair = os.path.join(models, "matt_solo.xml"), os.path.join(models, "matt_vs_zombie_spar.xml")
    return {
        "footwork": lambda: FootworkEnv(solo, 4, device="cpu", seed=0, **extra.get("footwork", {})),
        "match": lambda: MatchEnv(pair, 4, device="cpu", seed=0, **extra.get("match", {})),
        "match_rules": lambda: MatchEnv(pair, 4, device="cpu", seed=0, **dict(RULES, **extra.get("match", {}))),
        "getup": lambda: GetUpMatchEnv(pair, 4, device="cpu", seed=0, **extra.get("getup", {})),
    }


def run(make, steps: int = 30):
    env = make()
    g = torch.Generator().manual_seed(1)
    obs, rew = [env.reset().clone()], []
    for _ in range(steps):
        a = torch.randn(env.N * env.K, env.A, generator=g) * 1.5
        o, r, _, _ = env.step(a)
        obs.append(o.clone())
        rew.append(r.clone())
    return env, torch.stack(obs).numpy(), torch.stack(rew).numpy()


def check_variety() -> int:
    """envs/boxing.py's punch_variety on a made-up sequence for one fighter: jab, jab, jab (a streak of 2), the other hand
    0.4 s later (a combination, the streak broken), then the first hand to the body after a 3 s pause (neither)."""
    from envs.boxing import punch_variety
    seq = [  # (glove, zone, seconds since the last punch landed)
        (0, 0, 99.0), (0, 0, 0.5), (0, 0, 0.6), (1, 0, 0.4), (0, 1, 3.0)]
    want_streak, want_same, want_combo = [0, 1, 2, 0, 0], [False, True, True, False, False], [False, False, False, True, False]
    last, streak = torch.full((1, 1), -1, dtype=torch.long), torch.zeros(1, 1)
    bad = 0
    for i, (g, z, since) in enumerate(seq):
        landed = torch.zeros(1, 1, 2, dtype=torch.bool)
        landed[0, 0, g] = True
        arriving = torch.full((1, 1, 2), 6.0)
        zone = torch.full((1, 1, 2), z, dtype=torch.long)
        _, same, combo, combo_q, streak, last = punch_variety(landed, arriving, zone, last, streak, torch.full((1, 1), since), 0.8)
        ok = (int(streak) == want_streak[i] and bool(same) == want_same[i] and bool(combo) == want_combo[i]
              and float(combo_q) == (6.0 if want_combo[i] else 0.0))
        bad += not ok
        if not ok:
            print(f"punch {i}: streak {int(streak)} same {bool(same)} combo {bool(combo)}, wanted {want_streak[i]} {want_same[i]} {want_combo[i]}")
    print("punch_variety: the made-up sequence counts as it should" if not bad else f"punch_variety: {bad} wrong")
    return bad


def check_ceiling(models: str) -> int:
    """The fatigue ceiling's arithmetic: right after the drive, kp (target - q) - kv qdot of every tired drive lies inside
    its force range times the strength it has left (the targets of wild actions, half the reserve gone)."""
    env = MatchEnv(os.path.join(models, "matt_vs_zombie_spar.xml"), 4, device="cpu", seed=0, **dict(RULES, fatigue_ceiling=True))
    env.reserve.fill_(0.2)
    env.drive_scale.fill_(1.0)
    a = torch.randn(env.N, env.K, env.A, generator=torch.Generator().manual_seed(3)) * 3.0
    env._drive(a.clamp(-3.0, 3.0))
    kp, kv = env._gains()
    q, qd = env.qpos[:, env.jq], env.qvel[:, env.jv]
    force = kp * (env.ctrl.view(env.N, env.K, env.A) - q) - kv * qd
    t = env._tired()[..., None]
    over = (force - t * env.f_hi).clamp_min(0.0).max() + (t * env.f_lo - force).clamp_min(0.0).max()
    capped = ((force - t * env.f_hi).abs() < 1e-3) | ((force - t * env.f_lo).abs() < 1e-3)
    print(f"fatigue ceiling: tired to {float(t.mean()):.2f}, worst excess {float(over):.2e} N m, {float(capped.float().mean()):.0%} of drives at the cap")
    return int(float(over) > 1e-3 or not bool(capped.any()))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--models", default=os.path.join(HERE, "models", "v2"))
    ap.add_argument("--save", default="")
    ap.add_argument("--compare", default="")
    ap.add_argument("--flags", action="store_true")
    args = ap.parse_args()

    if args.flags:
        failed = check_variety() + check_ceiling(args.models)
        # Each new flag alone, on the stage it belongs to: it must run, stay finite, and change something (the two punch
        # flags only where a punch lands, which thirty steps of random actions seldom do: check_variety covers them).
        tries = {
            "selfcontact_w": ("match", {"selfcontact_w": 0.5}),
            "selfcontact_w ": ("footwork", {"selfcontact_w": 0.5}),
            "repeat_decay": ("match_rules", {"repeat_decay": 0.6}),
            "combo_w": ("match_rules", {"combo_w": 0.3}),
            "slow_v": ("match_rules", {"slow_v": 4.0}),
            "hurt_start": ("match_rules", {"hurt_start": 1.0}),
            "fatigue_ceiling": ("match_rules", {"fatigue_ceiling": True}),
        }
        for flag, (stage, kw) in tries.items():
            base = scenarios(args.models, {})[stage]
            on = scenarios(args.models, {stage.split("_")[0]: kw})[stage]
            _, o0, r0 = run(base)
            env, o1, r1 = run(on)
            finite = np.isfinite(o1).all() and np.isfinite(r1).all()
            moved = not (np.array_equal(o0, o1) and np.array_equal(r0, r1))
            print(f"{flag:16s} on {stage:11s}: {'finite' if finite else 'NOT FINITE'}, {'changes the run' if moved else 'CHANGES NOTHING'}"
                  f"  (reward {r0.mean():+.4f} -> {r1.mean():+.4f})")
            already = flag == "fatigue_ceiling" and json.load(open(os.path.join(args.models, "matt_policy_config.json"))).get("fatigue_ceiling", False)
            if already:
                print(f"{'':16s}   (the configs here already turn it on: nothing to change)")
            failed += (not finite) + (not moved and flag.strip() not in ("repeat_decay", "combo_w", "slow_v") and not already)
        print("all flags pass" if not failed else f"{failed} failure(s)")
        raise SystemExit(failed)

    out = {}
    for name, make in scenarios(args.models, {}).items():
        _, o, r = run(make)
        out[f"{name}_obs"], out[f"{name}_rew"] = o, r
        print(f"{name:12s} obs {o.shape} reward mean {r.mean():+.5f}")
    if args.save:
        np.savez_compressed(args.save, **out)
        print(f"saved {args.save}")
    if args.compare:
        ref = np.load(args.compare)
        bad = [k for k in out if not np.array_equal(out[k], ref[k])]
        for k in bad:
            print(f"DIFFERENT: {k}, largest gap {np.abs(out[k] - ref[k]).max():.3g}")
        print("identical to the baseline" if not bad else f"{len(bad)} arrays differ")
        raise SystemExit(1 if bad else 0)


if __name__ == "__main__":
    main()
