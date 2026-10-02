"""Train one boxer to get up off the canvas, on the CPU, in plain C MuJoCo (envs/cgetup.py).

    python train_getup_cpu.py --name matt --resume checkpoints/defend/latest_matt.pt --hours 6

For when the GPU is taken. A few hundred worlds on a couple of threads, at idle priority so that whatever
has the machine keeps it: about a tenth of the GPU trainer's speed. The policy, the observation and the
checkpoint are the GPU trainer's own, so a run begun here is carried on there (train_box.py --stage getup
--resume ...), and the other way round.

It writes checkpoints and TensorBoard charts and nothing else: no ONNX and no policy manifest, so the
game's importer does not take a half-trained get-up for a finished one. tools/export_getup.py does that
when a policy has passed the exam.

Two things it does that the GPU stage does not do yet:
  --renorm (on by default)   The policy arrives with the match's observation scaling, measured on boxers
      that were always upright: lying down, the gravity vector and the pelvis height are thirty or more of
      those standard deviations out and are clipped flat at ten. The policy could not tell its back from
      its front. So before training the scaling is measured again on this stage's own states, and the
      first layer of both networks is rewritten so that every state the old scaling did not clip gives
      exactly the outputs it gave before. Nothing the boxer knew is lost and the floor becomes legible.
  the helping hand's count is carried between iterations (see CGetUp.get_stats).
"""
from __future__ import annotations

import argparse
import csv
import ctypes
import json
import os
import sys
import time

import numpy as np
import torch
from torch.utils.tensorboard import SummaryWriter

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from envs.cgetup import CGetUp  # noqa: E402
from ppo import PPO, PPOConfig  # noqa: E402


def renormalise(ppo: PPO, mean: torch.Tensor, var: torch.Tensor, count: float) -> None:
    """Give the policy a new observation scaling without changing what it does where the old one was not clipping."""
    m0, s0 = ppo.obs_rms.mean.clone(), torch.sqrt(ppo.obs_rms.var + 1e-8)
    s1 = torch.sqrt(var + 1e-8)
    with torch.no_grad():
        for net in (ppo.model.actor, ppo.model.critic):
            first = net[0]
            first.bias.add_(first.weight @ ((mean - m0) / s0))
            first.weight.mul_((s1 / s0)[None, :])
    ppo.obs_rms.mean, ppo.obs_rms.var, ppo.obs_rms.count = mean.clone(), var.clone(), float(count)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", required=True)
    ap.add_argument("--resume", required=True, help="the boxer's match policy, or a get-up checkpoint to carry on from")
    ap.add_argument("--run-name", default="")
    ap.add_argument("--num-envs", type=int, default=256)
    ap.add_argument("--threads", type=int, default=2)
    ap.add_argument("--steps", type=int, default=32)
    ap.add_argument("--hours", type=float, default=6.0)
    ap.add_argument("--iters", type=int, default=1000000)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--lr", type=float, default=1e-3)
    ap.add_argument("--lr-max", type=float, default=1e-2)
    ap.add_argument("--entropy-coef", type=float, default=0.005)
    ap.add_argument("--reset-std", type=float, default=0.5)
    ap.add_argument("--max-std", type=float, default=0.6)
    ap.add_argument("--slack-hi", type=float, default=2.6)
    ap.add_argument("--shove", type=float, default=2.5)
    ap.add_argument("--assist", type=float, default=0.6)
    ap.add_argument("--no-renorm", action="store_true")
    ap.add_argument("--save-every", type=int, default=25)
    ap.add_argument("--stop-file", default="", help="stop cleanly when this file appears (default logs/<run>.stop)")
    ap.add_argument("--normal-priority", action="store_true")
    args = ap.parse_args()

    if os.name == "nt" and not args.normal_priority:
        ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x00000040)   # idle
    torch.set_num_threads(max(1, args.threads))
    torch.manual_seed(args.seed)
    run = args.run_name or f"cgetup_{args.name}"
    env = CGetUp(args.name, args.num_envs, threads=args.threads, seed=args.seed, slack_hi=args.slack_hi,
                 shove=args.shove, assist=args.assist)
    N, A, D = env.N, env.A, env.obs_dim
    cfg = PPOConfig(steps_per_env=args.steps, lr=args.lr, entropy_coef=args.entropy_coef, lr_adapt=1.2,
                    minibatches=max(1, round(args.steps * N / 8192)), init_std=0.5, max_std=args.max_std, lr_max=args.lr_max)
    ppo = PPO(D, A, N, "cpu", cfg)
    extra = ppo.load(args.resume)
    carried = extra.get("mode") == "getup"
    start = int(extra.get("iter", 0)) if carried and extra.get("run_name") == run else 0
    if carried:
        env.assist = float(extra.get("assist", env.assist))
        env.up_ema = float(extra.get("up_ema", 0.0))
        cfg.lr = min(float(extra.get("lr", args.lr)), args.lr_max)
    else:
        cfg.lr = args.lr
        with torch.no_grad():
            ppo.model.log_std.fill_(float(np.log(args.reset_std)))
    print(f"[run] {run}: {args.name} from {args.resume} ({extra.get('mode', 'unknown stage')}, iteration {extra.get('iter', 0)}); "
          f"{N} worlds on {args.threads} threads, {args.steps} steps an iteration, batch {args.steps * N:,} in {cfg.minibatches} minibatches", flush=True)

    obs = torch.from_numpy(env.reset())
    if not carried and not args.no_renorm:
        seen = []
        with torch.no_grad():
            for _ in range(300):
                a = ppo.model.actor(ppo.obs_rms.normalize(obs, cfg.obs_clip)) + 0.3 * torch.randn(N, A)
                o, _, _, _ = env.step(a.numpy())
                obs = torch.from_numpy(o)
                seen.append(obs)
        seen = torch.cat(seen)
        old_std = torch.sqrt(ppo.obs_rms.var + 1e-8)
        out = ((seen - ppo.obs_rms.mean).abs() / old_std > cfg.obs_clip).float().mean(0)
        # Never narrower than the match's scaling: what the boxer knows about standing must stay in range.
        new_var = torch.maximum(((seen - ppo.obs_rms.mean) ** 2).mean(0), ppo.obs_rms.var)
        new_mean = ppo.obs_rms.mean.clone()
        renormalise(ppo, new_mean, new_var, 2.0e6)
        print(f"[renorm] {int((out > 0.05).sum())} of {D} observations were clipped more than 5% of the time under the match's scaling "
              f"(worst {out.max():.0%}); rescaled, the policy's outputs kept", flush=True)
        env.get_stats()
        obs = torch.from_numpy(env.reset())

    os.makedirs(os.path.join(HERE, "logs"), exist_ok=True)
    writer = SummaryWriter(os.path.join(HERE, "logs", "tb", run))
    ck_dir = os.path.join(HERE, "checkpoints", run)
    os.makedirs(ck_dir, exist_ok=True)
    csv_path = os.path.join(HERE, "logs", f"{run}.csv")
    new_csv = not os.path.exists(csv_path) or os.path.getsize(csv_path) == 0
    csv_f = open(csv_path, "a", newline="", encoding="utf-8")
    csv_w = csv.writer(csv_f)
    keys = None
    stop_file = args.stop_file or os.path.join(HERE, "logs", f"{run}.stop")
    with open(os.path.join(HERE, "logs", f"{run}.args.json"), "w", encoding="utf-8") as fh:
        json.dump({"argv": sys.argv[1:], "args": vars(args), "mode": "getup", "fighters": [args.name], "trainer": "cpu",
                   "started": time.strftime("%Y-%m-%d %H:%M:%S")}, fh, indent=2, sort_keys=True)

    t_start = time.time()
    total = 0
    for it in range(start, args.iters):
        t0 = time.time()
        with torch.no_grad():
            for _ in range(args.steps):
                act = ppo.act(obs)
                o, rew, done, timeout = env.step(act.numpy())
                obs = torch.from_numpy(o)
                ppo.record(torch.from_numpy(rew), torch.from_numpy(done), torch.from_numpy(timeout))
        stats = ppo.update(obs)
        total += args.steps * N
        sps = args.steps * N / max(1e-6, time.time() - t0)
        s = env.get_stats()
        if not (stats["kl"] == stats["kl"] and s["ep_return"] == s["ep_return"]):
            print(f"[abort] NaN at iteration {it}; the last checkpoint is intact", flush=True)
            break
        hours = (time.time() - t_start) / 3600.0
        if keys is None:
            keys = sorted(s.keys())
            if new_csv:
                csv_w.writerow(["iter", "hours", "steps", "sps", "kl", "lr", "std", "value_loss"] + keys)
        csv_w.writerow([it, f"{hours:.4f}", total, int(sps), f"{stats['kl']:.5f}", f"{stats['lr']:.2e}", f"{stats['action_std']:.4f}",
                        f"{stats['value_loss']:.4f}"] + [f"{s[k]:.5f}" for k in keys])
        csv_f.flush()
        for k, v in s.items():
            writer.add_scalar(f"env/{k}", v, it)
        for k, v in stats.items():
            writer.add_scalar(f"ppo/{k}", v, it)
        writer.add_scalar("perf/fps", sps, it)
        if it % 5 == 0:
            print(f"it {it:6d} | {sps:6.0f} sps | ret {s['ep_return']:7.2f} | on its feet at the end {s['up_rate']:4.0%}, "
                  f"from the floor {s['up_rate_from_floor']:4.0%}, unaided {s['up_rate_unaided']:4.0%} in {s['time_to_stand_unaided']:4.1f} s, "
                  f"help {s['assist']:.3f} | height {s['height']:4.2f} upright {s['upright']:4.2f} "
                  f"| kl {stats['kl']:.4f} lr {stats['lr']:.1e} std {stats['action_std']:.2f} | {hours * 60:6.1f} min", flush=True)
            with open(os.path.join(HERE, "logs", f"{run}.status.json"), "w", encoding="utf-8") as fh:
                json.dump({"run": run, "mode": "getup", "fighters": [args.name], "iter": it, "hours": hours, "fps": sps,
                           "updated": time.strftime("%Y-%m-%d %H:%M:%S"), "ppo": stats, "env": s}, fh, indent=2)
        stopping = os.path.exists(stop_file) or (args.hours > 0.0 and hours >= args.hours)
        if (it + 1) % args.save_every == 0 or it + 1 == args.iters or stopping:
            ck = os.path.join(ck_dir, f"model_{it + 1:06d}_{args.name}.pt")
            ppo.save(ck, {"iter": it + 1, "mode": "getup", "fighters": [args.name], "fighter": args.name, "obs_dim": D, "act_dim": A,
                          "action_scale": env.action_scale, "run_name": run, "trainer": "cpu", "assist": env.assist,
                          "up_ema": env.up_ema, "lr": cfg.lr, "career": int(extra.get("career", 0)), "renormed": True})
            import shutil
            shutil.copyfile(ck, os.path.join(ck_dir, f"latest_{args.name}.pt"))
            saved = sorted(f for f in os.listdir(ck_dir) if f.startswith("model_"))
            for f in saved[:-4]:
                if int(f[6:12]) % (args.save_every * 20) != 0:
                    os.remove(os.path.join(ck_dir, f))
        if stopping:
            print(f"[stop] after iteration {it + 1}, {hours:.2f} h", flush=True)
            break
    writer.close()
    csv_f.close()
    if os.path.exists(stop_file):
        os.remove(stop_file)


if __name__ == "__main__":
    main()
