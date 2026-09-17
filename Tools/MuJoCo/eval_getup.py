"""Get-up evaluation: can this checkpoint stand up from the floor?

    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/eval_getup.py --run nick_getup01
    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/eval_getup.py --run nick_getup01 --checkpoint model_300.pt

Every world starts LYING ON THE FLOOR (the same fallen-pose distribution
nick_env's get-up task trains: root tilted past vertical about a random
horizontal axis, hinges near rest, zero velocity) and has the full cap to
produce ONE get-up. Reported:

    success rate   worlds that were up (pelvis above 75% of rest height, up
                   axis past 0.7) and HELD it getup_stable_seconds
    rise time      control seconds from episode start to that moment
    relapse        worlds that, having succeeded, fell again before the cap

Numbers over N different fallen starts -- the lying-pose noise IS the sample,
so there is no --start-noise knob here (the identical-worlds trap from
eval_nick.py cannot recur: every world's start pose is random by design).

HAZARDS ARE OFF for this measurement: shoves disabled, gusts and lean
zeroed. The practice scene judges get-up on its own; eval_nick.py still owns
the balance/walk/ring tables.

STEP PROVENANCE, every run: the control_dt line below states the timestep
and decimation this evaluation actually ran at, resolved from
NICK_TIMESTEP / NICK_DECIMATION. A brain trained at 0.02 x 1 measured at
0.005 x 4 -- or the reverse -- fails in a way that looks like a bad policy.
Judge a checkpoint only against the step its SOURCE.txt states.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

import numpy as np
import torch

HERE = Path(__file__).parent
REPO = HERE.parent.parent
LOG_ROOT = REPO / "results" / "nick"

sys.path.insert(0, str(HERE))
from nick_env import ACT_DIM, OBS_BASE, OBS_COMMAND, NickEnv, NickEnvCfg, preferred_model_path  # noqa: E402

parser = argparse.ArgumentParser()
parser.add_argument("--run", default="nick01")
parser.add_argument("--checkpoint", default=None)
parser.add_argument("--onnx", default=None)
parser.add_argument("--worlds", type=int, default=512)
parser.add_argument("--seconds", type=float, default=20.0, help="cap per world")
parser.add_argument("--repeats", type=int, default=1, help="rerun with fresh fallen-pose seeds")
parser.add_argument("--model", default=None,
                    help="MJCF to evaluate on; default is the body the RUN trained on "
                         "(its config.json), then Nick's Unity export")
parser.add_argument("--tag", default="", help="printed on every row, for the log")
args = parser.parse_args()

OBS_DIM = OBS_BASE + OBS_COMMAND


def model_for_run(run_dir: Path) -> str | None:
    config = run_dir / "config.json"
    if not config.exists():
        return None
    try:
        return json.loads(config.read_text()).get("env", {}).get("model_path") or None
    except (OSError, ValueError):
        return None


def resolve_model_path() -> str:
    """--model wins; else the run's own body; else Nick's Unity export.

    Same rule as eval_nick.py: a checkpoint is measured on the body it
    trained on, read from the run's own config.json.
    """
    if args.model:
        return args.model
    from_run = model_for_run(LOG_ROOT / args.run) if args.run else None
    if from_run:
        path = Path(from_run)
        if not path.exists():
            rebased = REPO / path
            if rebased.exists():
                return str(rebased)
        return from_run
    return preferred_model_path()


def load_policy():
    if args.onnx:
        import onnxruntime as ort
        sess = ort.InferenceSession(args.onnx, providers=["CPUExecutionProvider"])
        name = sess.get_inputs()[0].name

        def onnx_policy(obs):
            return torch.tensor(sess.run(None, {name: obs.cpu().numpy()})[0], device=obs.device)
        return onnx_policy, Path(args.onnx).name

    from rsl_rl.modules import ActorCritic, EmpiricalNormalization
    run_dir = LOG_ROOT / args.run
    if args.checkpoint:
        path = run_dir / args.checkpoint
    else:
        candidates = sorted(run_dir.glob("model_*.pt"), key=lambda p: int(re.search(r"(\d+)", p.stem).group(1)))
        if not candidates:
            raise SystemExit("no checkpoints under %s" % run_dir)
        path = candidates[-1]
    ckpt = torch.load(path, map_location="cpu", weights_only=False)
    net = ActorCritic(OBS_DIM, OBS_DIM, ACT_DIM, [512, 256, 128], [512, 256, 128], "elu").to("cuda:0").eval()
    net.load_state_dict(ckpt["model_state_dict"])
    norm = EmpiricalNormalization(shape=[OBS_DIM]).to("cuda:0").eval()
    norm.load_state_dict(ckpt["obs_norm_state_dict"])

    def torch_policy(obs):
        with torch.no_grad():
            return net.actor(norm(obs))
    return torch_policy, "%s (iteration %s)" % (path.name, ckpt.get("iter", "?"))


def rollout_getup(env: NickEnv, policy) -> dict:
    """One lying start per world; stats freeze at each world's FIRST success."""
    import warp as wp
    n = env.num_envs
    dev = env.device
    cfg = env.cfg
    steps = int(round(args.seconds / env.control_dt))
    env.reset()
    env.episode_length_buf[:] = 0
    env.push_steps_left[:] = 0
    obs = env._observations()

    succeeded = torch.zeros(n, dtype=torch.bool, device=dev)
    rise_time = torch.full((n,), float("nan"), device=dev)
    relapsed = torch.zeros(n, dtype=torch.bool, device=dev)
    up_steps = torch.zeros(n, dtype=torch.long, device=dev)
    ever_rose = torch.zeros(n, dtype=torch.bool, device=dev)

    for step in range(steps):
        action = policy(obs).clamp(-1.0, 1.0)
        target = torch.where(action >= 0.0, action * env.ctrl_high, -action * env.ctrl_low)
        env.ctrl[:] = target.to(env.ctrl.dtype)
        env.xfrc[:] = 0.0                     # hazards are off for this measurement
        torch.cuda.synchronize()
        for _ in range(env.cfg.decimation):
            wp.capture_launch(env._step_graph)
        wp.synchronize()
        env.episode_length_buf += 1

        s = env._state()
        risen = (s["pelvis_z"] > cfg.getup_rise_pelvis_fraction * env.rest_pelvis_z) \
            & (s["up"][:, 2] > cfg.getup_rise_up_z)
        up_steps = torch.where(risen, up_steps + 1, torch.zeros_like(up_steps))
        ever_rose |= risen
        stable = (up_steps.float() * env.control_dt >= cfg.getup_stable_seconds) & ~succeeded
        t_now = (step + 1) * env.control_dt
        rise_time = torch.where(stable, torch.full_like(rise_time, t_now), rise_time)
        succeeded |= stable
        # A relapse is a fall AFTER the success latch: the referee's own rule,
        # so a world that stands and then slumps does not count as healthy.
        fell = (s["pelvis_z"] < cfg.fall_pelvis_fraction * env.rest_pelvis_z) \
            | (s["up"][:, 2] < cfg.fall_up_z)
        relapsed |= fell & succeeded
        obs = env._observations()

    succ = rise_time[succeeded].cpu().numpy()
    pct = lambda q: float(np.nanpercentile(succ, q)) if succ.size else float("nan")  # noqa: E731
    return {
        "worlds": n,
        "success_rate": float(succeeded.float().mean()),
        "rose_rate": float(ever_rose.float().mean()),
        "relapse_rate": float(relapsed.float().mean()),
        "median": pct(50), "p25": pct(25), "p75": pct(75),
        "best": float(np.nanmin(succ)) if succ.size else float("nan"),
    }


def main():
    policy, name = load_policy()
    tag = (" [%s]" % args.tag) if args.tag else ""
    model_path = resolve_model_path()
    print("policy: %s   worlds: %d   repeats: %d   cap: %g s   model: %s"
          % (name, args.worlds, args.repeats, args.seconds, model_path))
    for attempt in range(args.repeats):
        cfg = NickEnvCfg(num_envs=args.worlds, model_path=model_path,
                         gain_scale_range=(1.0, 1.0), friction_scale_range=(1.0, 1.0),
                         getup_fraction=1.0, push_probability=0.0,
                         hazard_wind_newtons=0.0, hazard_lean_degrees=0.0,
                         seed=101 + attempt)
        env = NickEnv(cfg)
        if attempt == 0:
            print("  control_dt %.3f s (timestep %.4f x decimation %d)"
                  % (env.control_dt, env.mjm.opt.timestep, cfg.decimation))
        r = rollout_getup(env, policy)
        print("GETUP_EVAL | rep %d/%d%s | success %.0f%% | rose-but-no-hold %.0f%% | "
              "median rise %.1f s (p25 %.1f / p75 %.1f, best %.1f) | relapsed after success %.0f%%"
              % (attempt + 1, args.repeats, tag, 100 * r["success_rate"],
                 100 * (r["rose_rate"] - r["success_rate"]),
                 r["median"], r["p25"], r["p75"], r["best"], 100 * r["relapse_rate"]))
        del env
        torch.cuda.empty_cache()


if __name__ == "__main__":
    main()
