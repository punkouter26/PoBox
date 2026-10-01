"""Train boxing policies with PPO on MuJoCo Warp, and export them as ONNX for Unity.

    python train_box.py --xml models/matt_bag.xml --max-hours 0.8 --run-name bag_matt
    python train_box.py --xml models/matt_vs_zombie_spar.xml --max-hours 6 --run-name match \
                        --resume checkpoints/bag_matt/latest.pt checkpoints/bag_zombie/latest.pt

The stage is read from the model:
  * a model with a bag             one fighter learns to stand unaided and hit hard;
  * two copies of one fighter      one policy spars against itself;
  * two different fighters         each has its own policy and they learn against each other. This is
                                   the match: --resume then takes one checkpoint per fighter, in order.

House rules applied here:
  * TensorBoard is launched when training starts (http://localhost:6006).
  * Runs left over from earlier sessions are removed from TensorBoard first, unless --keep-old-runs.
  * Every save writes a .pt checkpoint, an .onnx export and a status file other tools can read.
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import shutil
import subprocess
import sys
import time

import torch
from torch.utils.tensorboard import SummaryWriter

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from envs.boxing import BoxingEnv  # noqa: E402
from ppo import PPO, PPOConfig, export_onnx  # noqa: E402


def launch_tensorboard(logdir: str, port: int) -> None:
    try:
        subprocess.Popen([sys.executable, "-m", "tensorboard.main", "--logdir", logdir, "--port", str(port), "--bind_all"],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
        print(f"[tensorboard] http://localhost:{port}  (logdir {logdir})")
    except Exception as e:  # pragma: no cover
        print(f"[tensorboard] failed to launch: {e}")


def clean_old_runs(tb_root: str) -> None:
    if not os.path.isdir(tb_root):
        return
    for name in os.listdir(tb_root):
        p = os.path.join(tb_root, name)
        if os.path.isdir(p):
            shutil.rmtree(p, ignore_errors=True)
            print(f"[tensorboard] removed obsolete run {name}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--xml", required=True)
    ap.add_argument("--num-envs", type=int, default=4096)
    ap.add_argument("--iters", type=int, default=1000000)
    ap.add_argument("--max-hours", type=float, default=0.0, help="stop cleanly after this long, saving first. 0 = no limit")
    ap.add_argument("--steps", type=int, default=24)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--resume", nargs="*", default=[],
                    help="checkpoint to start from; for a match between two different fighters, one each, in the model's order")
    ap.add_argument("--reset-std", type=float, default=0.0,
                    help="when resuming, put the exploration noise back to this. A policy that has converged "
                         "on the bag explores too little to learn anything new against an opponent.")
    ap.add_argument("--run-name", default="")
    ap.add_argument("--save-every", type=int, default=50)
    ap.add_argument("--tb-port", type=int, default=6006)
    ap.add_argument("--no-tensorboard", action="store_true")
    ap.add_argument("--keep-old-runs", action="store_true")
    ap.add_argument("--no-cuda-graph", action="store_true")
    ap.add_argument("--lr", type=float, default=1e-3)
    ap.add_argument("--entropy-coef", type=float, default=0.005)
    ap.add_argument("--desired-kl", type=float, default=0.01)
    ap.add_argument("--lr-adapt", type=float, default=1.2)
    ap.add_argument("--init-std", type=float, default=0.5,
                    help="exploration noise at the start. At 0.8 a humanoid holding a pose is shaken off its "
                         "feet inside a second and a half, and spends the run learning to cancel its own noise.")
    ap.add_argument("--action-scale", type=float, default=0.5)
    ap.add_argument("--episode-s", type=float, default=12.0)
    ap.add_argument("--obs-noise", type=float, default=1.0, help="0 turns sensor noise off")
    ap.add_argument("--push-vel", type=float, default=0.4, help="size of the occasional shove, m/s. 0 turns it off")
    ap.add_argument("--hit-w", type=float, default=0.6)
    ap.add_argument("--taken-w", type=float, default=0.5)
    ap.add_argument("--fall-penalty", type=float, default=4.0)
    ap.add_argument("--ko-bonus", type=float, default=4.0)
    args = ap.parse_args()

    torch.manual_seed(args.seed)
    device = "cuda"
    tb_root = os.path.join(HERE, "logs", "tb")
    if not args.keep_old_runs:
        clean_old_runs(tb_root)

    env = BoxingEnv(args.xml, args.num_envs, device=device, seed=args.seed, action_scale=args.action_scale,
                    episode_len_s=args.episode_s, cuda_graph=not args.no_cuda_graph, obs_noise=args.obs_noise,
                    push_vel=args.push_vel, hit_w=args.hit_w, taken_w=args.taken_w,
                    fall_penalty=args.fall_penalty, ko_bonus=args.ko_bonus)
    N, K, A, D = env.N, env.K, env.A, env.obs_dim
    # One learner per distinct fighter. Two copies of the same fighter share one, and it learns from both.
    names = env.names if env.hetero else [env.names[0]]
    per_learner = N if env.hetero else N * K
    run_name = args.run_name or f"{env.mode}_{time.strftime('%Y%m%d_%H%M%S')}"
    tb_dir = os.path.join(tb_root, run_name)
    writer = SummaryWriter(tb_dir)
    os.makedirs(os.path.join(HERE, "logs"), exist_ok=True)
    with open(os.path.join(HERE, "logs", run_name + ".args.json"), "w", encoding="utf-8") as fh:
        json.dump({"argv": sys.argv[1:], "args": vars(args), "mode": env.mode, "fighters": env.names,
                   "started": time.strftime("%Y-%m-%d %H:%M:%S")}, fh, indent=2, sort_keys=True)
    print("[run] " + " ".join([os.path.basename(sys.argv[0])] + sys.argv[1:]))
    if not args.no_tensorboard:
        launch_tensorboard(tb_root, args.tb_port)

    # Hold the minibatch near 24k samples whatever the population.
    minibatches = max(1, round(args.steps * per_learner / 24576))
    learners = []
    for _ in names:
        cfg = PPOConfig(steps_per_env=args.steps, lr=args.lr, desired_kl=args.desired_kl,
                        entropy_coef=args.entropy_coef, minibatches=minibatches, lr_adapt=args.lr_adapt,
                        init_std=args.init_std)
        learners.append(PPO(D, A, per_learner, device, cfg))

    start_iter = 0
    if args.resume:
        if len(args.resume) != len(learners):
            raise SystemExit(f"this model has {len(learners)} learner(s) ({', '.join(names)}); --resume needs one checkpoint for each")
        for name, ppo, path in zip(names, learners, args.resume):
            extra = ppo.load(path)
            same_stage = extra.get("mode", "") == env.mode and extra.get("fighters", []) == env.names
            if same_stage:
                start_iter = int(extra.get("iter", 0))
            else:
                # A new stage is a new optimisation problem: the old run's learning rate has no business in it.
                ppo.cfg.lr = args.lr
            print(f"{name}: resumed from {path} ({extra.get('mode', 'unknown stage')}, iteration {extra.get('iter', 0)})")
            if args.reset_std > 0.0:
                with torch.no_grad():
                    ppo.model.log_std.fill_(float(torch.log(torch.tensor(args.reset_std))))
        if args.reset_std > 0.0:
            print(f"exploration noise reset to {args.reset_std:g}")

    ck_dir = os.path.join(HERE, "checkpoints", run_name)
    os.makedirs(ck_dir, exist_ok=True)
    csv_path = os.path.join(HERE, "logs", f"{run_name}.csv")
    new_csv = not os.path.exists(csv_path) or os.path.getsize(csv_path) == 0
    csv_f = open(csv_path, "a", newline="", encoding="utf-8")
    csv_w = csv.writer(csv_f)
    stat_keys = None
    status_path = os.path.join(HERE, "logs", f"{run_name}.status.json")
    tag = (lambda name: f"_{name}") if env.hetero else (lambda name: "")

    obs = env.reset()
    total_steps = 0
    t_start = time.time()
    print(f"mode={env.mode} fighters={'+'.join(env.names)} learners={len(learners)} obs_dim={D} act_dim={A} worlds={N} "
          f"control_dt={env.dt:.3f}s batch={args.steps * per_learner:,} per learner in {minibatches} minibatches", flush=True)

    def split(x: torch.Tensor):
        """A flat (N*K, ...) tensor as one piece per learner."""
        if not env.hetero:
            return [x]
        x = x.reshape(N, K, *x.shape[1:])
        return [x[:, k] for k in range(K)]

    for it in range(start_iter, args.iters):
        t0 = time.time()
        with torch.no_grad():
            for _ in range(args.steps):
                acts = [ppo.act(o) for ppo, o in zip(learners, split(obs))]
                act = torch.stack(acts, 1).reshape(N * K, A) if env.hetero else acts[0]
                obs, rew, done, timeout = env.step(act)
                for ppo, r, d, t in zip(learners, split(rew), split(done), split(timeout)):
                    ppo.record(r, d, t)
        all_stats = [ppo.update(o) for ppo, o in zip(learners, split(obs))]
        stats = {k: sum(s[k] for s in all_stats) / len(all_stats) for k in all_stats[0]}
        total_steps += args.steps * N * K
        fps = args.steps * N * K / max(1e-6, time.time() - t0)
        s = env.get_stats()
        if not all(v == v for v in (stats["kl"], s["ep_return"])):   # NaN check
            print(f"[abort] NaN at iteration {it}; the last checkpoint is intact", flush=True)
            break

        if stat_keys is None:
            stat_keys = sorted(s.keys())
            if new_csv:
                csv_w.writerow(["iter", "hours", "steps", "fps", "kl", "lr", "std"] + stat_keys)
        hours = (time.time() - t_start) / 3600.0
        csv_w.writerow([it, f"{hours:.4f}", total_steps, int(fps), f"{stats['kl']:.5f}", f"{stats['lr']:.2e}",
                        f"{stats['action_std']:.4f}"] + [f"{s[k]:.5f}" for k in stat_keys])
        csv_f.flush()
        for k, v in s.items():
            writer.add_scalar(f"env/{k}", v, it)
        for name, st in zip(names, all_stats):
            for k, v in st.items():
                writer.add_scalar(f"ppo{tag(name)}/{k}", v, it)
        writer.add_scalar("perf/fps", fps, it)

        if it % 10 == 0:
            print(f"it {it:6d} | {fps:7.0f} sps | ret {s['ep_return']:7.2f} | len {s['ep_len_s']:5.1f}s | fall {s['fall_rate']:4.2f} "
                  f"| hits/s {s['hits_per_s']:5.2f} (head {s['head_share']:3.0%}) at {s['hit_speed']:4.1f} m/s, max {s['hit_speed_max']:4.1f} "
                  f"| dist {s['distance']:4.2f} | kd/min {s['knockdowns_per_min']:5.2f} "
                  f"| kl {stats['kl']:.4f} lr {stats['lr']:.1e} std {stats['action_std']:.2f} | {hours * 60:6.1f} min", flush=True)
            if env.hetero:
                print("          " + " | ".join(
                    f"{n}: {s[n + '_hits_per_s']:.2f} hits/s at {s[n + '_hit_speed']:.1f} m/s, down in {s[n + '_falls']:.0%} of endings, "
                    f"{s[n + '_knockdowns_per_min']:.2f} knockdowns/min" for n in names), flush=True)
            with open(status_path, "w", encoding="utf-8") as fh:
                json.dump({"run": run_name, "mode": env.mode, "fighters": env.names, "iter": it, "hours": hours, "fps": fps,
                           "max_hours": args.max_hours, "updated": time.strftime("%Y-%m-%d %H:%M:%S"),
                           "ppo": stats, "env": s}, fh, indent=2)

        out_of_time = args.max_hours > 0.0 and (time.time() - t_start) >= args.max_hours * 3600.0
        if (it + 1) % args.save_every == 0 or it + 1 == args.iters or out_of_time:
            for k, (name, ppo) in enumerate(zip(names, learners)):
                ck = os.path.join(ck_dir, f"model_{it + 1:06d}{tag(name)}.pt")
                ppo.save(ck, {"iter": it + 1, "mode": env.mode, "fighters": env.names, "fighter": name, "obs_dim": D,
                              "act_dim": A, "action_scale": env.action_scale, "run_name": run_name,
                              "xml": os.path.abspath(args.xml)})
                shutil.copyfile(ck, os.path.join(ck_dir, f"latest{tag(name)}.pt"))
                try:
                    export_onnx(ppo, os.path.join(ck_dir, f"latest{tag(name)}.onnx"), D)
                except Exception as e:   # an export problem must never cost the run
                    print(f"[onnx] export failed: {e}", flush=True)
                manifest = dict(env.cfgs[k if env.hetero else 0])
                manifest.update({"action_scale": env.action_scale, "observation_size": D,
                                 "trained_by": {"run": run_name, "mode": env.mode, "against": env.names, "iterations": it + 1}})
                with open(os.path.join(ck_dir, f"latest{tag(name)}_policy_config.json"), "w", encoding="utf-8") as fh:
                    json.dump(manifest, fh, indent=2)
            # Keep one save in ten once they are old; a night of saves is otherwise gigabytes.
            saved = sorted(f for f in os.listdir(ck_dir) if f.startswith("model_"))
            keep_from = sorted({f[6:12] for f in saved})[-6:]
            for f in saved:
                if f[6:12] not in keep_from and int(f[6:12]) % (args.save_every * 10) != 0:
                    os.remove(os.path.join(ck_dir, f))
            print(f"saved iteration {it + 1} to {ck_dir}", flush=True)
        if out_of_time:
            print(f"[max-hours] reached {args.max_hours:g} h at iteration {it + 1}; stopped cleanly", flush=True)
            break

    writer.close()
    csv_f.close()


if __name__ == "__main__":
    main()
