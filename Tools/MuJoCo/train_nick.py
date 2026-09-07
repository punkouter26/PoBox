"""Train Nick's locomotion brain in MuJoCo Warp with rsl_rl PPO.

    # validate the env only: random-ish steps, no learning
    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/train_nick.py --smoke

    # the real run: TensorBoard started, MuJoCo viewer following the newest
    # checkpoint, per AGENTS.md
    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/train_nick.py --run-name nick01 --num-envs 4096 --max-iterations 3000

THE UI. MuJoCo Warp itself has no window -- it is a batch of thousands of
worlds on the GPU. AGENTS.md wants MuJoCo runs watched, so this script also
launches watch_nick.py --follow, which opens the MuJoCo viewer on the CPU
model and replays the newest checkpoint as they land, cycling BALANCE and
WALK the way the Unity demo does. --no-ui skips it.

PPO SETTINGS are the Isaac line's (Tools/Isaac/train_matt.py), on purpose:
same network, same hyperparameters, same reward design, so the comparison
between the two simulators is not confounded by the trainer.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).parent
REPO = HERE.parent.parent
LOG_ROOT = REPO / "results" / "nick"          # results/ is gitignored

parser = argparse.ArgumentParser()
parser.add_argument("--num-envs", type=int, default=4096)
parser.add_argument("--max-iterations", type=int, default=3000)
parser.add_argument("--run-name", default="nick01")
parser.add_argument("--resume", default=None, help="checkpoint path to continue from")
parser.add_argument("--resume-latest", action="store_true",
                    help="continue from the newest model_*.pt in this run's own directory, if any (crash recovery)")
parser.add_argument("--until-iteration", type=int, default=None,
                    help="absolute iteration to stop at; overrides --max-iterations after a resume")
parser.add_argument("--smoke", action="store_true", help="step the env, do not train")
parser.add_argument("--no-ui", action="store_true", help="do not open the MuJoCo viewer")
parser.add_argument("--no-tensorboard", action="store_true")
parser.add_argument("--tensorboard-port", type=int, default=6007)
parser.add_argument("--seed", type=int, default=1)
parser.add_argument("--save-interval", type=int, default=50)
parser.add_argument("--w-planted", type=float, default=None, help="override NickEnvCfg.w_planted for this run")
args = parser.parse_args()


def prune_stale_runs(root: Path, keep: str) -> None:
    """Drop abandoned run dirs before starting, per AGENTS.md.

    Only runs with no event file, or nothing but an event file under 2 KB (a
    run that died before writing a scalar), are removed. Anything with real
    history stays: this deletes abandoned runs, not results.
    """
    if not root.exists():
        return
    for d in sorted(root.iterdir()):
        if not d.is_dir() or d.name == keep:
            continue
        events = list(d.rglob("events.out.tfevents.*"))
        biggest = max((e.stat().st_size for e in events), default=0)
        if biggest < 2048:
            print("  pruning abandoned run %s (largest event file %d bytes)" % (d.name, biggest))
            shutil.rmtree(d, ignore_errors=True)


def start_tensorboard(root: Path, port: int) -> subprocess.Popen | None:
    exe = Path(sys.executable).parent / "tensorboard.exe"
    cmd = ([str(exe)] if exe.exists() else [sys.executable, "-m", "tensorboard.main"])
    cmd += ["--logdir", str(root), "--port", str(port), "--reload_interval", "30"]
    try:
        proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print("  TensorBoard started on http://localhost:%d (pid %d)" % (port, proc.pid))
        return proc
    except Exception as exc:                                  # noqa: BLE001
        print("  TensorBoard could not start: %s" % exc)
        return None


def start_viewer(run_dir: Path) -> subprocess.Popen | None:
    """MuJoCo viewer following the run's newest checkpoint (AGENTS.md: show the UI)."""
    cmd = [sys.executable, str(HERE / "watch_nick.py"), "--run", run_dir.name, "--follow"]
    try:
        proc = subprocess.Popen(cmd, cwd=str(REPO))
        print("  MuJoCo viewer started, following %s (pid %d)" % (run_dir, proc.pid))
        return proc
    except Exception as exc:                                  # noqa: BLE001
        print("  viewer could not start: %s" % exc)
        return None


sys.path.insert(0, str(HERE))
import torch                                                  # noqa: E402
from nick_env import ACT_DIM, NickEnv, NickEnvCfg, preferred_model_path   # noqa: E402

log_dir = LOG_ROOT / args.run_name
cfg = NickEnvCfg(num_envs=args.num_envs, seed=args.seed, model_path=preferred_model_path())
if args.w_planted is not None:
    cfg.w_planted = args.w_planted

if args.smoke:
    env = NickEnv(cfg)
    obs, _ = env.get_observations()
    print("ENV READY  num_envs=%d  obs=%d  act=%d  control_dt=%.3f" % (env.num_envs, env.num_obs, env.num_actions, env.control_dt))
    total = torch.zeros(env.num_envs, device=env.device)
    for step in range(100):
        action = torch.randn(env.num_envs, ACT_DIM, device=env.device) * 0.3
        obs, rew, done, extras = env.step(action)
        total += rew
        if step % 25 == 0:
            print("  step %3d  rew %7.4f  height %.3f  upright %.2f  done %d"
                  % (step, float(rew.mean()), float(extras["log"]["Metrics/height"]),
                     float(extras["log"]["Metrics/upright_fraction"]), int(done.sum())))
    print("  finite obs: %s   mean return %.3f" % (bool(torch.isfinite(obs).all()), float(total.mean())))
    print("SMOKE_OK")
    sys.exit(0)

print("TensorBoard housekeeping")
LOG_ROOT.mkdir(parents=True, exist_ok=True)
prune_stale_runs(LOG_ROOT, keep=args.run_name)
if not args.no_tensorboard:
    start_tensorboard(LOG_ROOT, args.tensorboard_port)

env = NickEnv(cfg)
print("ENV READY  num_envs=%d  obs=%d  act=%d  control_dt=%.3f  model=%s"
      % (env.num_envs, env.num_obs, env.num_actions, env.control_dt, cfg.model_path))

from rsl_rl.runners import OnPolicyRunner   # noqa: E402

train_cfg = {
    "seed": args.seed,
    "num_steps_per_env": 24,
    "max_iterations": args.max_iterations,
    "save_interval": args.save_interval,
    "experiment_name": args.run_name,
    "empirical_normalization": True,
    "logger": "tensorboard",
    "policy": {
        "class_name": "ActorCritic",
        "init_noise_std": 1.0,
        "actor_hidden_dims": [512, 256, 128],
        "critic_hidden_dims": [512, 256, 128],
        "activation": "elu",
    },
    "algorithm": {
        "class_name": "PPO",
        "value_loss_coef": 1.0,
        "use_clipped_value_loss": True,
        "clip_param": 0.2,
        "entropy_coef": 0.001,
        "num_learning_epochs": 5,
        "num_mini_batches": 4,
        "learning_rate": 1.0e-3,
        "schedule": "adaptive",
        "gamma": 0.99,
        "lam": 0.95,
        "desired_kl": 0.01,
        "max_grad_norm": 1.0,
    },
}

log_dir.mkdir(parents=True, exist_ok=True)
json.dump({"env": env.config_dict(), "train": train_cfg}, open(log_dir / "config.json", "w"), indent=2, default=str)
runner = OnPolicyRunner(env, train_cfg, log_dir=str(log_dir), device=str(env.device))
resume_path = args.resume
if args.resume_latest:
    import re
    own = sorted(log_dir.glob("model_*.pt"), key=lambda p: int(re.search(r"(\d+)", p.stem).group(1)))
    if own:
        resume_path = str(own[-1])
if resume_path:
    runner.load(resume_path)
    print("  resumed from %s (iteration %d)" % (resume_path, runner.current_learning_iteration))
iterations = args.max_iterations
if args.until_iteration is not None:
    iterations = args.until_iteration - runner.current_learning_iteration
    if iterations <= 0:
        print("TRAINING_DONE (already at iteration %d)" % runner.current_learning_iteration)
        os._exit(0)

viewer = None if args.no_ui else start_viewer(log_dir)
print("TRAINING  logs -> %s" % log_dir)
try:
    runner.learn(num_learning_iterations=iterations, init_at_random_ep_len=True)
finally:
    if viewer is not None and viewer.poll() is None:
        viewer.terminate()
print("TRAINING_DONE")
os._exit(0)
