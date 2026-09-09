"""Fresh-start evaluation of a Nick checkpoint on a MuJoCo Warp batch.

    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/eval_nick.py --run nick01
    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/eval_nick.py --run nick01 --checkpoint model_1500.pt --worlds 1024

WHY A SEPARATE EVALUATOR. The 2026-08-31 line recorded in-training metrics
disagreeing with fresh-start rollouts by up to 100x, because training
measures a steady-state population dominated by survivors while every real
episode starts cold. This starts every world from the rest pose, exactly as
Unity does, and reads survival against time. Two tables:

    BALANCE  command 0 m/s, shoved every 4 s (150 N for 0.2 s), 30 s cap
    WALK     command 1 m/s straight ahead, 20 s cap
    RING     command 0 m/s, the same shoves PLUS the contest ring hazards --
             55 N wind gusts held 2 s, and a 2.5 deg gravity lean rotating
             once every 13 s. nick08 measured 95% full-cap in the shove-only
             BALANCE table and a 4.4 s median in the actual ring; the
             difference is this table, which did not exist.
             ONE HAZARD PER WORLD, because Systems_HazardDirector "rolls one
             random hazard per round" -- a third of the worlds get shoves
             only, a third gusts, a third the lean. nick_env samples the same
             three modes per episode. Running all three at once measures a
             ring that does not exist and reads about 20 points low.

Every table also reports the physical-stability KPIs the reward curve cannot
see: torso tilt, delivered actuator effort, action rate, hinge acceleration,
and for WALK the fraction of steps tracking the command within 10%.

Domain randomisation is OFF (nominal gains and friction) so the numbers
describe the creature Unity runs, not the training distribution.

THE START POSE IS PERTURBED, and it has to be. With `exact_start_fraction=1.0`,
no gain or friction spread and a deterministic policy, all N worlds are the
SAME world: the WALK and ZERO tables then have an effective sample size of one
trajectory, spread only by whatever numerical divergence the batched solver
happens to produce. The tell was the ZERO row reading median = mean = p25 =
1.52 s exactly, every time, on every brain. `--start-noise` (default 1.0)
gives each world the training reset noise -- 0.05 rad on the hinges, 0.1 on
the velocities -- so a percentage is a percentage of 512 different starts.
Pass `--start-noise 0` for the old single-trajectory behaviour.
"""
from __future__ import annotations

import argparse
import math
import re
import sys
from pathlib import Path

import numpy as np
import torch

HERE = Path(__file__).parent
REPO = HERE.parent.parent
LOG_ROOT = REPO / "results" / "nick"

sys.path.insert(0, str(HERE))
from nick_env import ACT_DIM, OBS_BASE, OBS_COMMAND, NickEnv, NickEnvCfg, preferred_model_path   # noqa: E402

parser = argparse.ArgumentParser()
parser.add_argument("--run", default="nick01")
parser.add_argument("--checkpoint", default=None)
parser.add_argument("--onnx", default=None)
parser.add_argument("--worlds", type=int, default=512)
parser.add_argument("--balance-seconds", type=float, default=30.0)
parser.add_argument("--walk-seconds", type=float, default=20.0)
parser.add_argument("--walk-speed", type=float, default=1.0)
parser.add_argument("--shove-newtons", type=float, default=150.0)
parser.add_argument("--shove-interval", type=float, default=4.0)
parser.add_argument("--repeats", type=int, default=1,
                    help="run the whole matrix this many times with different seeds; "
                         "the exit criterion wants 10 consecutive passing evaluations")
parser.add_argument("--no-ring", action="store_true", help="skip the RING hazard table")
parser.add_argument("--tag", default="", help="printed on every row, for the log")
parser.add_argument("--start-noise", type=float, default=1.0,
                    help="fraction of worlds that start from a perturbed pose rather than the "
                         "exact rest pose (1.0 = all, the default; 0 = the old identical-worlds "
                         "behaviour, effective sample size one)")
args = parser.parse_args()

OBS_DIM = OBS_BASE + OBS_COMMAND


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


def rollout(env: NickEnv, policy, speed: float, seconds: float, shove: bool,
            wind: bool = False, lean: bool = False) -> dict:
    n = env.num_envs
    dev = env.device
    steps = int(round(seconds / env.control_dt))
    env.reset()
    env.commands[:, 0] = speed
    env.commands[:, 1] = 0.0
    env.commands[:, 2] = -1.0        # straight ahead from the rest heading
    env.episode_length_buf[:] = 0
    env.push_steps_left[:] = 0
    obs = env._observations()

    alive = torch.ones(n, dtype=torch.bool, device=dev)
    survival = torch.full((n,), seconds, device=dev)
    upright_steps = torch.zeros(n, device=dev)
    speed_sum = torch.zeros(n, device=dev)
    switches = torch.zeros(n, device=dev)
    last_stance = torch.zeros(n, device=dev)
    start_xy = env.xpos[:, env.pelvis_id, :2].clone()
    shove_every = int(round(args.shove_interval / env.control_dt))
    shove_steps = max(1, int(round(0.2 / env.control_dt)))

    tilt_sum = torch.zeros(n, device=dev)
    force_sum = torch.zeros(n, device=dev)
    accel_sum = torch.zeros(n, device=dev)
    rate_sum = torch.zeros(n, device=dev)
    tracked = torch.zeros(n, device=dev)
    prev_action = torch.zeros(n, ACT_DIM, device=dev)

    # RING HAZARDS, the shapes Systems_HazardDirector runs and the ones
    # nick_env models in training: a 55 N gust held 2 s with a pause between,
    # and a 2.5 deg gravity lean as mass * g * sin(angle), rotating once per
    # cycle. Ball rain is not modelled; falling bodies are not a pelvis force.
    hz = env.cfg
    wind_steps = max(1, int(round(hz.hazard_wind_seconds / env.control_dt)))
    wind_gap = max(1, int(round(sum(hz.hazard_wind_interval_seconds) / 2.0 / env.control_dt)))
    wind_period = wind_steps + wind_gap
    lean_newtons = env.total_mass * 9.81 * math.sin(math.radians(hz.hazard_lean_degrees))
    wind_angle = torch.rand(n, device=dev) * 2.0 * math.pi
    # 0 = shoves only, 1 = + gusts, 2 = + lean, one per world for the rollout.
    mode = torch.randint(0, 3, (n,), device=dev) if (wind or lean) else torch.zeros(n, dtype=torch.long, device=dev)
    wind_mask = (mode == 1).float().unsqueeze(1) if wind else torch.zeros(n, 1, device=dev)
    lean_mask = (mode == 2).float().unsqueeze(1) if lean else torch.zeros(n, 1, device=dev)

    for step in range(steps):
        action = policy(obs).clamp(-1.0, 1.0)
        target = torch.where(action >= 0.0, action * env.ctrl_high, -action * env.ctrl_low)
        env.ctrl[:] = target.to(env.ctrl.dtype)
        if shove and step > 0 and step % shove_every == 0:
            angle = torch.rand(n, device=dev) * 2.0 * math.pi
            env.push_force[:] = torch.stack([torch.cos(angle), torch.sin(angle), torch.zeros(n, device=dev)], dim=1) * args.shove_newtons
            env.push_steps_left[:] = shove_steps
        active = env.push_steps_left > 0
        force = torch.where(active.unsqueeze(1), env.push_force, torch.zeros_like(env.push_force))
        if wind:
            if step % wind_period == 0:
                wind_angle = torch.rand(n, device=dev) * 2.0 * math.pi
            if (step % wind_period) < wind_steps:
                gust = torch.stack([torch.cos(wind_angle), torch.sin(wind_angle),
                                    torch.zeros(n, device=dev)], dim=1) * hz.hazard_wind_newtons
                force = force + gust * wind_mask
        if lean:
            phase = 2.0 * math.pi * step * env.control_dt / hz.hazard_lean_cycle_seconds
            force = force + torch.tensor([math.cos(phase) * lean_newtons,
                                          math.sin(phase) * lean_newtons, 0.0], device=dev) * lean_mask
        env.xfrc[:, env.pelvis_id, 0:3] = force.to(env.xfrc.dtype)
        env.push_steps_left = torch.clamp(env.push_steps_left - 1, min=0)
        torch.cuda.synchronize()
        for _ in range(env.cfg.decimation):
            import warp as wp
            wp.capture_launch(env._step_graph)
        wp.synchronize()
        env.episode_length_buf += 1

        s = env._state()
        fell = (s["pelvis_z"] < env.cfg.fall_pelvis_fraction * env.rest_pelvis_z) | (s["up"][:, 2] < env.cfg.fall_up_z)
        newly = fell & alive
        survival[newly] = (step + 1) * env.control_dt
        alive &= ~fell
        upright_steps += (s["up"][:, 2] > 0.7).float() * alive.float()
        a = alive.float()
        tilt_sum += torch.rad2deg(torch.acos(torch.clamp(s["up"][:, 2], -1.0, 1.0))) * a
        force_sum += env.actuator_force.abs().float().mean(dim=1) * a
        accel_sum += env.qacc[:, 6:].abs().float().mean(dim=1) * a
        rate_sum += ((action - prev_action) ** 2).mean(dim=1) * a
        prev_action = action
        v = s["lin_w"][:, :2]
        speed_sum += (-v[:, 1]) * a          # ahead is -y
        if speed > 0.1:
            tracked += (((-v[:, 1]) - speed).abs() <= 0.1 * speed).float() * a
        left_down, right_down = s["foot_down"][:, 0], s["foot_down"][:, 1]
        stance = torch.zeros(n, device=dev)
        stance = torch.where(left_down & ~right_down, -torch.ones_like(stance), stance)
        stance = torch.where(right_down & ~left_down, torch.ones_like(stance), stance)
        switched = (stance != 0) & (last_stance != 0) & (stance != last_stance) & alive
        switches += switched.float()
        last_stance = torch.where(stance != 0, stance, last_stance)
        obs = env._observations()

    lived = torch.clamp(survival / env.control_dt, min=1.0)
    distance = torch.norm(env.xpos[:, env.pelvis_id, :2] - start_xy, dim=1)
    return {
        "survival": survival.cpu().numpy(),
        "survived_all": float(alive.float().mean()),
        "upright": float((upright_steps / lived).mean()),
        "speed": float((speed_sum / lived).mean()),
        "alternation": float(torch.clamp((switches / lived) / (1.0 / 35.0), max=1.0).mean()),
        "distance": float(distance.mean()),
        "tilt_deg": float((tilt_sum / lived).mean()),
        "force": float((force_sum / lived).mean()),
        "accel": float((accel_sum / lived).mean()),
        "action_rate": float((rate_sum / lived).mean()),
        "within10": float((tracked / lived).mean()),
        "by_mode": [float((survival * (mode == k).float()).sum()
                          / max(1.0, float((mode == k).sum())))
                    for k in range(3)],
        "cap_by_mode": [float((alive.float() * (mode == k).float()).sum()
                              / max(1.0, float((mode == k).sum())))
                        for k in range(3)],
    }


def row(label, r, tag):
    s = r["survival"]
    return "%-8s%s median %5.2fs  mean %5.2fs  p25 %5.2fs  full-cap %4.0f%%  upright %.3f  speed %6.3f m/s  alternation %.3f  distance %6.2f m" % (
        label, tag, float(np.median(s)), float(s.mean()), float(np.percentile(s, 25)), 100.0 * r["survived_all"],
        r["upright"], r["speed"], r["alternation"], r["distance"])


def kpi(label, r, tag):
    return "%-8s%s tilt %5.2f deg  actuator |f| %6.2f Nm  hinge |qacc| %7.1f  action rate %.4f  within10 %.3f" % (
        label, tag, r["tilt_deg"], r["force"], r["accel"], r["action_rate"], r["within10"])


def main():
    policy, name = load_policy()
    tag = (" [%s]" % args.tag) if args.tag else ""
    print("policy: %s   worlds: %d   repeats: %d   start-noise %.2f   model: %s"
          % (name, args.worlds, args.repeats, args.start_noise, preferred_model_path()))
    for attempt in range(args.repeats):
        cfg = NickEnvCfg(num_envs=args.worlds, model_path=preferred_model_path(),
                         gain_scale_range=(1.0, 1.0), friction_scale_range=(1.0, 1.0),
                         exact_start_fraction=1.0 - args.start_noise,
                         push_probability=0.0, seed=1 + attempt)
        env = NickEnv(cfg)
        if attempt == 0:
            print("  control_dt %.3f s (timestep %.4f x decimation %d)"
                  % (env.control_dt, env.mjm.opt.timestep, cfg.decimation))
        suffix = tag if args.repeats == 1 else "%s rep %d/%d" % (tag, attempt + 1, args.repeats)
        b = rollout(env, policy, 0.0, args.balance_seconds, shove=True)
        w = rollout(env, policy, args.walk_speed, args.walk_seconds, shove=False)
        r = None if args.no_ring else rollout(env, policy, 0.0, args.balance_seconds,
                                              shove=True, wind=True, lean=True)
        z = rollout(env, lambda obs: torch.zeros(obs.shape[0], ACT_DIM, device=obs.device),
                    0.0, 10.0, shove=False)

        print("NICK_EVAL " + row("BALANCE", b, suffix) + "   (shoved %g N every %g s, cap %g s)"
              % (args.shove_newtons, args.shove_interval, args.balance_seconds))
        print("NICK_KPI  " + kpi("BALANCE", b, suffix))
        print("NICK_EVAL " + row("WALK", w, suffix) + "   (command %g m/s, cap %g s)"
              % (args.walk_speed, args.walk_seconds))
        print("NICK_KPI  " + kpi("WALK", w, suffix))
        if r is not None:
            print("NICK_EVAL " + row("RING", r, suffix) + "   (shoves + %g N gusts + %g deg lean, cap %g s)"
                  % (cfg.hazard_wind_newtons, cfg.hazard_lean_degrees, args.balance_seconds))
            print("NICK_KPI  " + kpi("RING", r, suffix))
            print("NICK_RING %s per hazard: shoves-only %.2fs/%.0f%%  +gusts %.2fs/%.0f%%  +lean %.2fs/%.0f%%"
                  % (suffix.strip() or "-", r["by_mode"][0], 100 * r["cap_by_mode"][0],
                     r["by_mode"][1], 100 * r["cap_by_mode"][1],
                     r["by_mode"][2], 100 * r["cap_by_mode"][2]))
        print("NICK_EVAL " + row("ZERO", z, suffix) + "   (no policy: the passive baseline)")
        del env
        torch.cuda.empty_cache()


if __name__ == "__main__":
    main()
