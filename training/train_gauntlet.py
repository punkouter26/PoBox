"""One boxer learns against several opponents at once, none of whom is learning.

    python train_gauntlet.py --name matt --resume checkpoints/defend/latest_matt.pt \
        --against zombie=checkpoints/defend/latest_zombie.pt nick=checkpoints/r5_nick_v_lilmatt/latest_nick.pt ... \
        --daze --max-hours 0.6

Why this exists. The league of 2026-10-02 put two learners in a ring and three times they agreed a truce:
both stopped punching, because for the weaker one not boxing was the best deal on offer and the stronger
one could not reach it. And a boxer that learns against one opponent at a time forgets the last one (Matt
and Zombie had only ever boxed each other; against Nick, Matt landed a third of what he lands on Zombie,
against Lil Matt almost nothing). Here the opponents are frozen, so there is nobody to agree a truce with,
and they are all in front of the learner in the same batch, so there is nothing to forget.

Each opponent is its own set of rings (envs/boxing.py, the match model of that pair); the learner has one
policy and one PPO batch across all of them. The opponents play their best punch (no exploration noise).
The numbers are logged per opponent: `v_NICK/hits_per_s` and so on in TensorBoard, one column each in the
log line.

--models DIR      look for the pair models here first (models/guard holds Matt's guard style)
--weights         relative share of rings per opponent, e.g. zombie=2 (default 1 each)
Everything else is train_box.py's (the daze rule, the block payment, the ceilings on noise and learning rate).
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
from envs.boxing import BoxingEnv  # noqa: E402
from envs.match import MatchEnv  # noqa: E402
from ppo import PPO, PPOConfig, export_onnx  # noqa: E402


def pair_xml(a: str, b: str, first: str) -> str:
    for folder in ([first] if first else []) + [os.path.join(HERE, "models")]:
        for x, y in ((a, b), (b, a)):
            p = os.path.join(folder, f"{x}_vs_{y}_spar.xml")
            if os.path.exists(p):
                return p
    raise SystemExit(f"no match model for {a} and {b}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", required=True, help="the boxer that learns")
    ap.add_argument("--resume", required=True)
    ap.add_argument("--against", nargs="+", required=True, help="opponent=checkpoint, one or more")
    ap.add_argument("--weights", nargs="*", default=[])
    ap.add_argument("--models", default="")
    ap.add_argument("--run-name", default="")
    ap.add_argument("--num-envs", type=int, default=2304, help="rings in all, shared out among the opponents")
    ap.add_argument("--steps", type=int, default=24)
    ap.add_argument("--max-hours", type=float, default=0.6)
    ap.add_argument("--more-iters", type=int, default=0)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--save-every", type=int, default=50)
    ap.add_argument("--device", default="cuda")
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--lr-max", type=float, default=5e-4)
    ap.add_argument("--entropy-coef", type=float, default=0.0)
    ap.add_argument("--reset-std", type=float, default=0.0)
    ap.add_argument("--max-std", type=float, default=0.35)
    ap.add_argument("--episode-s", type=float, default=12.0)
    ap.add_argument("--obs-noise", type=float, default=1.0)
    ap.add_argument("--push-vel", type=float, default=0.4)
    ap.add_argument("--hit-w", type=float, default=0.6)
    ap.add_argument("--taken-w", type=float, default=0.5)
    ap.add_argument("--fall-penalty", type=float, default=4.0)
    ap.add_argument("--ko-bonus", type=float, default=6.0)
    ap.add_argument("--no-survivor-bootstrap", action="store_true")
    ap.add_argument("--daze", action="store_true")
    ap.add_argument("--daze-tau", type=float, default=2.5)
    ap.add_argument("--daze-lo", type=float, default=14.0)
    ap.add_argument("--daze-hi", type=float, default=42.0)
    ap.add_argument("--daze-weak", type=float, default=0.45)
    ap.add_argument("--block-w", type=float, default=0.0)
    ap.add_argument("--speed-limit", action="store_true",
                    help="a drive's torque falls to nothing as its joint nears its speed limit (envs/boxing.py)")
    ap.add_argument("--fatigue-j", type=float, default=0.0,
                    help="a bout: joules a full-strength boxer's drives can put out before they tire; 0 = no fatigue. Try 30000")
    ap.add_argument("--start-spread", type=float, default=1.0,
                    help="how far from the guard an episode may begin, as a multiple of the usual 0.06 rad and 0.15 m/s")
    ap.add_argument("--effort-free-iters", type=int, default=0,
                    help="no charge for power or for action size and change for this many iterations of the run, then "
                         "brought back over as many again: for a stage in which a skill has first to be found")
    ap.add_argument("--sep-max", type=float, default=2.2, help="the furthest apart the two boxers begin an episode, m")
    ap.add_argument("--no-onnx", action="store_true")
    ap.add_argument("--handover", default="", help="states the learner's get-up policy leaves it in (tools/make_handover_bank.py); "
                                                   "a share of its episodes begin in one")
    ap.add_argument("--handover-share", type=float, default=0.15)
    ap.add_argument("--stage", default="spar", choices=["spar", "match"],
                    help="match: the retrofit's bodies and its 103-number observation (envs/match.py); give --models models/v2")
    ap.add_argument("--randomise", type=float, default=0.15, help="match: each world's body is within this of the file's")
    args = ap.parse_args()

    torch.manual_seed(args.seed)
    dev = args.device
    opponents = [a.split("=", 1) for a in args.against]
    weights = dict((w.split("=")[0], float(w.split("=")[1])) for w in args.weights)
    share = np.array([weights.get(o, 1.0) for o, _ in opponents])
    counts = np.maximum(16, np.round(args.num_envs * share / share.sum() / 16).astype(int) * 16)
    run = args.run_name or f"gauntlet_{args.name}"

    envs, side, foes = [], [], []
    for i, ((opp, ck), n) in enumerate(zip(opponents, counts)):
        xml = pair_xml(args.name, opp, args.models)
        extra_env = {"randomise": args.randomise} if args.stage == "match" else {}
        env = (MatchEnv if args.stage == "match" else BoxingEnv)(xml, int(n), device=dev, seed=args.seed + i, episode_len_s=args.episode_s, obs_noise=args.obs_noise,
                        push_vel=args.push_vel, hit_w=args.hit_w, taken_w=args.taken_w, fall_penalty=args.fall_penalty,
                        ko_bonus=args.ko_bonus, survivor_bootstrap=not args.no_survivor_bootstrap, daze=args.daze,
                        daze_tau=args.daze_tau, daze_lo=args.daze_lo, daze_hi=args.daze_hi, daze_weak=args.daze_weak, block_w=args.block_w,
                        handover={args.name: args.handover if os.path.isabs(args.handover) else os.path.join(HERE, args.handover)} if args.handover else None,
                        handover_share=args.handover_share, start_spread=args.start_spread, sep_hi=args.sep_max, speed_limit=args.speed_limit, fatigue_j=args.fatigue_j, **extra_env)
        assert env.hetero and args.name in env.names and opp in env.names, f"{xml} holds {env.names}"
        envs.append(env)
        side.append(env.names.index(args.name))
        foe = PPO(env.obs_dim, env.A, 1, dev, PPOConfig())
        foe.load(ck if os.path.isabs(ck) else os.path.join(HERE, ck))
        foes.append(foe)
        print(f"  against {opp}: {int(n)} rings of {os.path.relpath(xml, HERE)}, its policy {ck}", flush=True)
    D, A = envs[0].obs_dim, envs[0].A
    total_n = int(sum(e.N for e in envs))
    minibatches = max(1, round(args.steps * total_n / 24576))
    cfg = PPOConfig(steps_per_env=args.steps, lr=args.lr, entropy_coef=args.entropy_coef, minibatches=minibatches, lr_adapt=1.2,
                    init_std=0.5, max_std=args.max_std, lr_max=args.lr_max)
    ppo = PPO(D, A, total_n, dev, cfg)
    extra = ppo.load(args.resume if os.path.isabs(args.resume) else os.path.join(HERE, args.resume))
    cfg.lr = min(args.lr, args.lr_max)
    if args.reset_std > 0.0:
        with torch.no_grad():
            ppo.model.log_std.fill_(float(np.log(args.reset_std)))
    if args.max_std > 0.0:
        with torch.no_grad():
            ppo.model.log_std.clamp_(max=float(np.log(args.max_std)))
    career = int(extra.get("career", 0))
    start = int(extra.get("iter", 0)) if extra.get("run_name") == run else 0
    iters = start + args.more_iters if args.more_iters > 0 else 10 ** 9
    print(f"[run] {run}: {args.name} from {args.resume} ({extra.get('mode', '?')}, iteration {extra.get('iter', 0)}) against "
          f"{', '.join(o for o, _ in opponents)}; {total_n} rings, batch {args.steps * total_n:,} in {minibatches} minibatches", flush=True)

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
        json.dump({"argv": sys.argv[1:], "args": vars(args), "mode": args.stage, "fighters": [args.name], "against": [o for o, _ in opponents],
                   "started": time.strftime("%Y-%m-%d %H:%M:%S")}, fh, indent=2, sort_keys=True)

    def mine(env, k, t):       # the learner's rows of a flat (N*2, ...) tensor
        return t.reshape(env.N, 2, *t.shape[1:])[:, k]

    obs = [e.reset() for e in envs]
    per = ("hits_per_s", "hit_speed", "head_share", "falls", "blocks_per_s", "legs_gone_per_min", "knockdowns_per_min", "reward_per_step")
    t_start = time.time()
    total = 0
    for it in range(start, iters):
        t0 = time.time()
        if args.effort_free_iters > 0:
            share = min(1.0, max(0.0, (it - start - args.effort_free_iters) / args.effort_free_iters))
            for e in envs:
                e.effort_scale = share
        with torch.no_grad():
            for _ in range(args.steps):
                a_me = ppo.act(torch.cat([mine(e, k, o) for e, k, o in zip(envs, side, obs)]))
                rews, dones, cuts = [], [], []
                at = 0
                for i, (e, k) in enumerate(zip(envs, side)):
                    theirs = mine(e, 1 - k, obs[i])
                    a_foe = foes[i].model.actor(foes[i].obs_rms.normalize(theirs, 10.0))
                    pair = [None, None]
                    pair[k], pair[1 - k] = a_me[at:at + e.N], a_foe
                    at += e.N
                    obs[i], rew, done, cut = e.step(torch.stack(pair, 1).reshape(e.N * 2, A))
                    rews.append(mine(e, k, rew)); dones.append(mine(e, k, done)); cuts.append(mine(e, k, cut))
                ppo.record(torch.cat(rews), torch.cat(dones), torch.cat(cuts))
        stats = ppo.update(torch.cat([mine(e, k, o) for e, k, o in zip(envs, side, obs)]))
        total += args.steps * total_n
        sps = args.steps * total_n / max(1e-6, time.time() - t0)
        rows = {}
        for (opp, _), e in zip(opponents, envs):
            s = e.get_stats()
            rows[opp] = {"fall_rate": s["fall_rate"], "ep_return": s["ep_return"], "their_hits_per_s": s[f"{opp}_hits_per_s"],
                         "their_falls": s[f"{opp}_falls"], "their_head_share": s[f"{opp}_head_share"]}
            rows[opp].update({k: s[f"{args.name}_{k}"] for k in per})
        w = {o: e.N / total_n for (o, _), e in zip(opponents, envs)}
        mean = {k: sum(rows[o][k] * w[o] for o in rows) for k in next(iter(rows.values()))}
        if not (stats["kl"] == stats["kl"] and mean["ep_return"] == mean["ep_return"]):
            print(f"[abort] NaN at iteration {it}; the last checkpoint is intact", flush=True)
            break
        hours = (time.time() - t_start) / 3600.0
        career += 1
        if keys is None:
            keys = sorted(mean.keys())
            if new_csv:
                csv_w.writerow(["iter", "hours", "steps", "fps", "kl", "lr", "std", "value_loss"] + keys + [f"{o}:{k}" for o in rows for k in keys])
        csv_w.writerow([it, f"{hours:.4f}", total, int(sps), f"{stats['kl']:.5f}", f"{stats['lr']:.2e}", f"{stats['action_std']:.4f}",
                        f"{stats['value_loss']:.4f}"] + [f"{mean[k]:.5f}" for k in keys] + [f"{rows[o][k]:.5f}" for o in rows for k in keys])
        csv_f.flush()
        for k, v in mean.items():
            writer.add_scalar(f"env/{k}", v, it)
        for o in rows:
            for k, v in rows[o].items():
                writer.add_scalar(f"v_{o.upper()}/{k}", v, it)
        for k, v in stats.items():
            writer.add_scalar(f"ppo_{args.name}/{k}", v, it)
        writer.add_scalar("perf/fps", sps, it)
        if it % 10 == 0:
            print(f"it {it:6d} | {sps:7.0f} sps | {args.name}: {mean['hits_per_s']:.2f} hits/s at {mean['hit_speed']:.1f} m/s, down {mean['falls']:.0%}, "
                  f"{mean['blocks_per_s']:.2f} blocks/s, legs {mean['legs_gone_per_min']:.2f}/min | "
                  + " | ".join(f"{o}: {rows[o]['hits_per_s']:.2f}/s v {rows[o]['their_hits_per_s']:.2f}/s, down {rows[o]['falls']:.0%} v {rows[o]['their_falls']:.0%}" for o in rows)
                  + f" | kl {stats['kl']:.4f} lr {stats['lr']:.1e} std {stats['action_std']:.2f} | {hours * 60:5.1f} min", flush=True)
            with open(os.path.join(HERE, "logs", f"{run}.status.json"), "w", encoding="utf-8") as fh:
                json.dump({"run": run, "mode": args.stage, "fighters": [args.name], "iter": it, "hours": hours, "fps": sps, "max_hours": args.max_hours,
                           "updated": time.strftime("%Y-%m-%d %H:%M:%S"), "ppo": stats, "env": mean, "against": rows}, fh, indent=2)
        stopping = os.path.exists(stop_file) or (args.max_hours > 0.0 and hours >= args.max_hours)
        if (it + 1) % args.save_every == 0 or it + 1 == iters or stopping:
            ck = os.path.join(ck_dir, f"model_{it + 1:06d}_{args.name}.pt")
            # The first opponent's policy is kept beside it, so the viewer has a pair to show (as train_box.py
            # does for a frozen partner); there is no latest_* for it, so nothing takes it for a new policy.
            foes[0].save(os.path.join(ck_dir, f"model_{it + 1:06d}_{opponents[0][0]}.pt"),
                         {"iter": it + 1, "mode": args.stage, "fighters": envs[0].names, "fighter": opponents[0][0], "run_name": run,
                          "xml": os.path.abspath(pair_xml(args.name, opponents[0][0], args.models))})
            ppo.save(ck, {"iter": it + 1, "mode": args.stage, "fighters": envs[0].names, "fighter": args.name, "obs_dim": D, "act_dim": A,
                          "action_scale": envs[0].action_scale, "run_name": run, "career": career, "against": [o for o, _ in opponents],
                          "xml": os.path.abspath(pair_xml(args.name, opponents[0][0], args.models)),
                          "env": {"daze": args.daze, "daze_tau": args.daze_tau, "daze_lo": args.daze_lo, "daze_hi": args.daze_hi,
                                  "daze_weak": args.daze_weak, "block_w": args.block_w, "speed_limit": envs[0].speed_limit, "fatigue_j": args.fatigue_j}})
            shutil.copyfile(ck, os.path.join(ck_dir, f"latest_{args.name}.pt"))
            if not args.no_onnx:
                try:
                    export_onnx(ppo, os.path.join(ck_dir, f"latest_{args.name}.onnx"), D, fixed_batch=args.stage == "match")
                    manifest = dict(envs[0].cfgs[side[0]])
                    manifest.update({"action_scale": envs[0].action_scale, "observation_size": D, "speed_limit": envs[0].speed_limit, "fatigue_j": args.fatigue_j,
                                     "trained_by": {"run": run, "mode": args.stage, "against": [o for o, _ in opponents], "iterations": it + 1}})
                    with open(os.path.join(ck_dir, f"latest_{args.name}_policy_config.json"), "w", encoding="utf-8") as fh:
                        json.dump(manifest, fh, indent=2)
                except Exception as e:
                    print(f"[onnx] export failed: {e}", flush=True)
            saved = sorted(f for f in os.listdir(ck_dir) if f.startswith("model_"))
            keep = sorted({f[6:12] for f in saved})[-3:]
            for f in saved:
                if f[6:12] not in keep and int(f[6:12]) % (args.save_every * 10) != 0:
                    os.remove(os.path.join(ck_dir, f))
            print(f"saved iteration {it + 1} to {ck_dir}", flush=True)
        if stopping:
            print(f"[stop] after iteration {it + 1}, {hours:.2f} h", flush=True)
            break
    writer.close()
    csv_f.close()
    if os.path.exists(stop_file):
        os.remove(stop_file)


if __name__ == "__main__":
    main()
