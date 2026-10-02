"""Train the two boxers of a pair model to get up off the canvas, on the GPU (envs/getup.py).

    python train_getup.py --a matt --b zombie --resume checkpoints/cgetup_matt/latest_matt.pt checkpoints/defend/latest_zombie.pt
    python train_getup.py --a trump --b nick --resume <trump's match policy> <nick's> --more-iters 2500

The stage is train_box.py --stage getup; this is its own driver because it needs three things that one
does not do, and train_box.py was in use by another run on the night this was written:

  * A checkpoint may come from the match (the boxer starts the stage here), from the CPU trainer
    (train_getup_cpu.py) or from this trainer; one of each in the same run is fine.
  * A policy that arrives from the match has its observation scaling measured again on this stage's
    states, with the first layer rewritten so its behaviour is kept (see train_getup_cpu.py, --renorm).
  * The helping hand each boxer has reached is saved in its checkpoint and picked up again.
  * --freeze NAME: that boxer only keeps the other one company (a seventh boxer has no eighth to pair with).

It writes checkpoints, TensorBoard charts, a CSV and a status file, and no ONNX: tools/export_getup.py
makes the game's file once the policy has passed tools/exam.py.
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import shutil
import sys
import time

import numpy as np
import torch
from torch.utils.tensorboard import SummaryWriter

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from envs.getup import GetUpEnv  # noqa: E402
from envs.match import GetUpMatchEnv  # noqa: E402
from ppo import PPO, PPOConfig  # noqa: E402
from train_getup_cpu import renormalise  # noqa: E402


def pair_xml(a: str, b: str, models: str = "models"):
    for x, y in ((a, b), (b, a)):
        p = os.path.join(HERE, models, f"{x}_vs_{y}_spar.xml")
        if os.path.exists(p):
            return p, x, y
    raise SystemExit(f"no match model for {a} and {b}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--a", required=True)
    ap.add_argument("--b", required=True)
    ap.add_argument("--resume", nargs=2, required=True, help="one checkpoint for --a and one for --b, in that order")
    ap.add_argument("--freeze", nargs="*", default=[])
    ap.add_argument("--run-name", default="")
    ap.add_argument("--num-envs", type=int, default=2048, help="0 = the size tools/bench_envs.py found best (logs/bench_envs.json), else 2048")
    ap.add_argument("--steps", type=int, default=24)
    ap.add_argument("--max-hours", type=float, default=3.0)
    ap.add_argument("--more-iters", type=int, default=0)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--lr", type=float, default=1e-3)
    ap.add_argument("--lr-max", type=float, default=1e-2)
    ap.add_argument("--entropy-coef", type=float, default=0.005)
    ap.add_argument("--reset-std", type=float, default=0.5)
    ap.add_argument("--max-std", type=float, default=0.6)
    ap.add_argument("--slack-hi", type=float, default=2.6)
    ap.add_argument("--shove", type=float, default=2.5)
    ap.add_argument("--assist", type=float, default=0.6)
    ap.add_argument("--fresh-assist", nargs="*", default=[],
                    help="boxers that start at --assist whatever their checkpoint says: for a get-up policy borrowed from "
                         "another body, which has learned the movement and not yet this body's weight")
    ap.add_argument("--save-every", type=int, default=50)
    ap.add_argument("--device", default="cuda")
    ap.add_argument("--done-when", type=float, default=0.0,
                    help="stop when every learning boxer gets up unaided this often (e.g. 0.9) with no help left and stands "
                         "within --done-pose of its guard, for 40 iterations running")
    ap.add_argument("--done-pose", type=float, default=0.15, help="radians a joint from the guard, while standing")
    ap.add_argument("--stage", default="getup", choices=["getup", "v2"],
                    help="v2: the retrofit's bodies (models/v2) and its 103-number observation (envs/match.py)")
    args = ap.parse_args()

    torch.manual_seed(args.seed)
    if args.num_envs <= 0:
        try:
            with open(os.path.join(HERE, "logs", "bench_envs.json"), "r", encoding="utf-8") as fh:
                args.num_envs = int(json.load(fh)["best"])
        except Exception:
            args.num_envs = 2048
    xml, x, y = pair_xml(args.a, args.b, os.path.join("models", "v2") if args.stage == "v2" else "models")
    resume = dict(zip((args.a, args.b), args.resume))
    env = (GetUpMatchEnv if args.stage == "v2" else GetUpEnv)(xml, args.num_envs, device=args.device, seed=args.seed, slack_hi=args.slack_hi, shove=args.shove, assist=args.assist)
    N, K, A, D = env.N, env.K, env.A, env.obs_dim
    names = env.names
    assert names == [x, y], f"{xml} holds {names}"
    run = args.run_name or f"getup_{x}_{y}"
    frozen = [n in args.freeze for n in names]
    minibatches = max(1, round(args.steps * N / 24576))
    learners, extras = [], []
    for k, n in enumerate(names):
        cfg = PPOConfig(steps_per_env=args.steps, lr=args.lr, entropy_coef=args.entropy_coef, minibatches=minibatches, lr_adapt=1.2,
                        init_std=0.5, max_std=args.max_std, lr_max=args.lr_max)
        ppo = PPO(D, A, N, args.device, cfg)
        extra = ppo.load(resume[n])
        extras.append(extra)
        if extra.get("mode") == "getup":
            cfg.lr = min(float(extra.get("lr", args.lr)), args.lr_max)
            if "assist" in extra and n not in args.fresh_assist:
                env.assist[k] = float(extra["assist"])
                env.up_ema[k] = float(extra.get("up_ema", 0.0))
        else:
            cfg.lr = args.lr
            with torch.no_grad():
                ppo.model.log_std.fill_(float(np.log(args.reset_std)))
        learners.append(ppo)
        print(f"{n}: from {resume[n]} ({extra.get('mode', 'unknown stage')}, iteration {extra.get('iter', 0)}, by {extra.get('trainer', 'gpu')})"
              + (", frozen" if frozen[k] else ""), flush=True)

    split = lambda t: [t.reshape(N, K, *t.shape[1:])[:, k] for k in range(K)]
    obs = env.reset()

    # A policy straight from the match cannot read the floor: rescale, keeping its behaviour.
    fresh = [k for k in range(K) if extras[k].get("mode") != "getup" and not extras[k].get("renormed")]
    if fresh:
        seen = [[] for _ in range(K)]
        with torch.no_grad():
            for _ in range(300):
                acts = [p.model.actor(p.obs_rms.normalize(o, p.cfg.obs_clip)) + 0.3 * torch.randn(N, A, device=args.device)
                        for p, o in zip(learners, split(obs))]
                obs, _, _, _ = env.step(torch.stack(acts, 1).reshape(N * K, A))
                for k, o in enumerate(split(obs)):
                    if k in fresh and len(seen[k]) < 60:
                        seen[k].append(o.clone())
        for k in fresh:
            s = torch.cat(seen[k])
            p = learners[k]
            clipped = ((s - p.obs_rms.mean).abs() / torch.sqrt(p.obs_rms.var + 1e-8) > p.cfg.obs_clip).float().mean(0)
            renormalise(p, p.obs_rms.mean.clone(), torch.maximum(((s - p.obs_rms.mean) ** 2).mean(0), p.obs_rms.var), 2.0e6)
            print(f"[renorm] {names[k]}: {int((clipped > 0.05).sum())} of {D} observations were clipped more than 5% of the time "
                  f"under the match's scaling; rescaled, the policy's outputs kept", flush=True)
        env.get_stats()
        obs = env.reset()

    start = 0
    for k in range(K):
        if extras[k].get("run_name") == run:
            start = max(start, int(extras[k].get("iter", 0)))
    iters = start + args.more_iters if args.more_iters > 0 else 10 ** 9

    os.makedirs(os.path.join(HERE, "logs"), exist_ok=True)
    writer = SummaryWriter(os.path.join(HERE, "logs", "tb", run))
    ck_dir = os.path.join(HERE, "checkpoints", run)
    os.makedirs(ck_dir, exist_ok=True)
    csv_path = os.path.join(HERE, "logs", f"{run}.csv")
    new_csv = not os.path.exists(csv_path) or os.path.getsize(csv_path) == 0
    csv_f = open(csv_path, "a", newline="", encoding="utf-8")
    csv_w = csv.writer(csv_f)
    keys = None
    stop_file = os.path.join(HERE, "logs", f"{run}.stop")
    with open(os.path.join(HERE, "logs", f"{run}.args.json"), "w", encoding="utf-8") as fh:
        json.dump({"argv": sys.argv[1:], "args": vars(args), "mode": "getup", "fighters": names, "trainer": "gpu",
                   "started": time.strftime("%Y-%m-%d %H:%M:%S")}, fh, indent=2, sort_keys=True)
    print(f"[run] {run}: {'+'.join(names)}, {N} worlds, batch {args.steps * N:,} per boxer in {minibatches} minibatches, from iteration {start}", flush=True)

    t_start = time.time()
    good_for = 0
    total = 0
    for it in range(start, iters):
        t0 = time.time()
        with torch.no_grad():
            for _ in range(args.steps):
                acts = [p.model.actor(p.obs_rms.normalize(o, p.cfg.obs_clip)) if fz else p.act(o)
                        for p, o, fz in zip(learners, split(obs), frozen)]
                obs, rew, done, timeout = env.step(torch.stack(acts, 1).reshape(N * K, A))
                for p, r, d, t, fz in zip(learners, split(rew), split(done), split(timeout), frozen):
                    if not fz:
                        p.record(r, d, t)
        stats_k = [None if fz else p.update(o) for p, o, fz in zip(learners, split(obs), frozen)]
        live = [s for s in stats_k if s is not None]
        stats = {k: sum(s[k] for s in live) / len(live) for k in live[0]}
        total += args.steps * N * K
        sps = args.steps * N * K / max(1e-6, time.time() - t0)
        s = env.get_stats()
        if not (stats["kl"] == stats["kl"] and s["ep_return"] == s["ep_return"]):
            print(f"[abort] NaN at iteration {it}; the last checkpoint is intact", flush=True)
            break
        hours = (time.time() - t_start) / 3600.0
        if keys is None:
            keys = sorted(s.keys())
            if new_csv:
                csv_w.writerow(["iter", "hours", "steps", "fps", "kl", "lr", "std", "value_loss"] + keys)
        csv_w.writerow([it, f"{hours:.4f}", total, int(sps), f"{stats['kl']:.5f}", f"{stats['lr']:.2e}", f"{stats['action_std']:.4f}",
                        f"{stats['value_loss']:.4f}"] + [f"{s[k]:.5f}" for k in keys])
        csv_f.flush()
        for k, v in s.items():
            writer.add_scalar(f"env/{k}", v, it)
        for n, st in zip(names, stats_k):
            if st is not None:
                for k, v in st.items():
                    writer.add_scalar(f"ppo_{n}/{k}", v, it)
        writer.add_scalar("perf/fps", sps, it)
        if it % 10 == 0:
            print(f"it {it:6d} | {sps:7.0f} sps | ret {s['ep_return']:7.2f} | on its feet at the end {s['up_rate']:4.0%} | "
                  + " | ".join(f"{n}: from the floor {s[n + '_up_rate_from_floor']:.0%}, unaided {s[n + '_up_rate_unaided']:.0%}, help {s[n + '_assist']:.3f}, off guard {s[n + '_pose_err']:.2f}" for n in names)
                  + f" | {s['time_to_stand_unaided']:4.1f} s | kl {stats['kl']:.4f} lr {stats['lr']:.1e} std {stats['action_std']:.2f} | {hours * 60:6.1f} min", flush=True)
            with open(os.path.join(HERE, "logs", f"{run}.status.json"), "w", encoding="utf-8") as fh:
                json.dump({"run": run, "mode": "getup", "fighters": names, "iter": it, "hours": hours, "fps": sps, "max_hours": args.max_hours,
                           "updated": time.strftime("%Y-%m-%d %H:%M:%S"), "ppo": stats, "env": s}, fh, indent=2)
        if args.done_when > 0.0:
            ok = all(fz or (s[n + "_up_rate_unaided"] >= args.done_when and s[n + "_assist"] <= 0.001 and s[n + "_pose_err"] <= args.done_pose)
                     for n, fz in zip(names, frozen))
            good_for = good_for + 1 if ok else 0
        stopping = os.path.exists(stop_file) or (args.max_hours > 0.0 and hours >= args.max_hours) or (args.done_when > 0.0 and good_for >= 40)
        if (it + 1) % args.save_every == 0 or it + 1 == iters or stopping:
            for k, (n, p) in enumerate(zip(names, learners)):
                if frozen[k]:
                    continue
                ck = os.path.join(ck_dir, f"model_{it + 1:06d}_{n}.pt")
                p.save(ck, {"iter": it + 1, "mode": "getup", "fighters": names, "fighter": n, "obs_dim": D, "act_dim": A,
                            "action_scale": env.action_scale, "run_name": run, "trainer": "gpu", "assist": float(env.assist[k].item()),
                            "up_ema": float(env.up_ema[k].item()), "lr": p.cfg.lr, "career": int(extras[k].get("career", 0)),
                            "renormed": True, "xml": os.path.abspath(xml)})
                shutil.copyfile(ck, os.path.join(ck_dir, f"latest_{n}.pt"))
            saved = sorted(f for f in os.listdir(ck_dir) if f.startswith("model_"))
            keep = sorted({f[6:12] for f in saved})[-4:]
            for f in saved:
                if f[6:12] not in keep and int(f[6:12]) % (args.save_every * 10) != 0:
                    os.remove(os.path.join(ck_dir, f))
            print(f"saved iteration {it + 1} to {ck_dir}", flush=True)
        if stopping:
            why = "stop file" if os.path.exists(stop_file) else "every boxer gets up unaided" if good_for >= 40 else f"{args.max_hours:g} h"
            print(f"[stop] {why}, after iteration {it + 1}, {hours:.2f} h", flush=True)
            break
    writer.close()
    csv_f.close()
    if os.path.exists(stop_file):
        os.remove(stop_file)


if __name__ == "__main__":
    main()
