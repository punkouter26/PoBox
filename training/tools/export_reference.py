"""What Unity needs from MuJoCo to be the same body, and a recording to check that it is.

    python tools/export_reference.py --name matt [--ckpt checkpoints/bag_matt/latest.pt] [--seconds 3]

Writes two files, neither of which training reads or writes, so this is safe to run beside a training job
(it runs on the CPU):

models/<name>_inertia.json
    Each body's mass, centre of mass and inertia exactly as MuJoCo computed them from the shapes. Unity
    left to itself spreads a link's mass over its colliders by volume; MuJoCo was told each shape's mass.
    On a torso that carries a 6 kg head and a half-kilo collarbone the two disagree about where the
    weight is, and a policy that balances one of them falls over in the other.

logs/reference_<name>.json
    The fighter started from its guard at a fixed spot, with the bag made a ghost (nothing touches it,
    so Unity needs no bag to compare against). Two recordings, each one row per control step:
      hold    every joint target held at the guard pose, no policy
      policy  the checkpoint's policy, deterministic
    Each row is the 100-number observation and the 21-number action. The observation carries the joint
    angles, the pelvis height and the tilt, which is everything needed to lay Unity's run beside it.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import tempfile

import mujoco
import numpy as np
import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)


def inertia(xml: str, prefix: str = "a_") -> dict:
    m = mujoco.MjModel.from_xml_path(xml)
    bodies = []
    for b in range(m.nbody):
        name = mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_BODY, b) or ""
        if not name.startswith(prefix):
            continue
        bodies.append({
            "name": name[len(prefix):],
            "mass": float(m.body_mass[b]),
            "com": [float(x) for x in m.body_ipos[b]],          # in the body's frame
            "quat": [float(x) for x in m.body_iquat[b]],        # w x y z: principal axes in the body's frame
            "inertia": [float(x) for x in m.body_inertia[b]],   # about those axes
        })
    return {"bodies": bodies, "total_mass": float(sum(b["mass"] for b in bodies))}


def ghost_model(xml: str, folder: str) -> str:
    """A copy of the bag model in which nothing collides with the bag."""
    text = open(xml, "r", encoding="utf-8").read()
    needle = '<geom name="bag_geom"'
    assert needle in text, "no bag in this model"
    text = text.replace(needle, needle + ' contype="0" conaffinity="0"')
    base = os.path.basename(xml)
    stem = base[: base.rfind("_")]
    out = os.path.join(folder, stem + "_ghost.xml")
    open(out, "w", encoding="utf-8").write(text)
    shutil.copy(os.path.join(os.path.dirname(xml), stem + "_policy_config.json"), folder)
    return out


@torch.no_grad()
def record(player, steps: int, use_policy: bool) -> dict:
    env = player.env
    # The same start every time: the guard, at the model's own spot, facing the bag, nothing moving.
    q = env.default_qpos.clone()[None]
    q[:, env.root_q[0] + 2] = env.stand_height[0] + 0.002
    env.qpos.copy_(q)
    env.qvel.zero_()
    env.ctrl.copy_(q[:, env.jq[0]])
    everyone = torch.ones(env.N, dtype=torch.bool, device=env.device)
    for t in (env.last_action, env.prev_action, env.step_count, env.head_vel, env.cool, env.prev_closing):
        t.zero_()
    env._physics_forward()
    env._reseed_trackers(everyone)
    obs = env._observe(env._geometry())

    rows_obs, rows_act = [], []
    for _ in range(steps):
        if use_policy:
            act = player._act(player.ppos[0], obs).clamp(-env.action_clip, env.action_clip)
        else:
            act = torch.zeros(1, env.A)
        rows_obs.append(obs[0].cpu().numpy().copy())
        rows_act.append(act[0].cpu().numpy().copy())
        obs, _, done, _ = env.step(act)
        if bool(done.any()):
            break
    o = np.array(rows_obs)
    height, upright = o[:, 9 + 3 * env.A + 2], -o[:, 8]
    return {
        "steps": len(rows_obs),
        "obs": [round(float(x), 5) for x in o.reshape(-1)],
        "act": [round(float(x), 5) for x in np.array(rows_act).reshape(-1)],
        "summary": f"{len(rows_obs)} steps ({len(rows_obs) * env.dt:.1f} s), pelvis {height.min():.3f}..{height.max():.3f} m, "
                   f"ends at {height[-1]:.3f}; upright min {upright.min():.3f}",
    }


@torch.no_grad()
def record_match(a: str, b: str, run: str, seconds: float, ghost: bool = False) -> None:
    """The two fighters in one ring from the model's own starting spots, each driven by its own policy.

    What this is for is the half of the observation a bag recording cannot check: where the opponent's
    head, body and gloves are, which way they face, and where the ring is. Rows are fighter A then
    fighter B for each control step.
    """
    from view_box import Player
    xml = os.path.join(HERE, "models", f"{a}_vs_{b}_spar.xml")
    ckpts = [os.path.join(HERE, "checkpoints", run, f"latest_{n}.pt") for n in (a, b)]
    if not all(os.path.exists(c) for c in ckpts):
        print(f"no match checkpoints in checkpoints/{run}; skipped")
        return
    if ghost:
        # Gloves that touch nothing, so that what is compared with Unity is each body moving itself and
        # nothing else: no two engines agree about a collision, and they do not need to for this.
        import re
        text, n = re.subn(r'(<geom name="[ab]_glove_[lr]")', r'\1 gap="1"', open(xml, "r", encoding="utf-8").read())
        assert n == 4
        tmp = tempfile.mkdtemp(prefix="pobox_ref_")
        shutil.copy(xml[: -len("_spar.xml")] + "_policy_config.json", tmp)
        xml = os.path.join(tmp, os.path.basename(xml))
        open(xml, "w", encoding="utf-8").write(text)
    player = Player(ckpts, xml, seed=0, deterministic=True, device="cpu")
    env = player.env
    q = env.default_qpos.clone()[None]
    for k in range(env.K):
        q[:, env.root_q[k] + 2] = env.stand_height[k] + 0.002
    env.qpos.copy_(q)
    env.qvel.zero_()
    env.ctrl.copy_(torch.cat([q[:, env.jq[k]] for k in range(env.K)], dim=-1))
    everyone = torch.ones(env.N, dtype=torch.bool, device=env.device)
    for t in (env.last_action, env.prev_action, env.step_count, env.head_vel, env.cool, env.prev_closing):
        t.zero_()
    env._physics_forward()
    env._reseed_trackers(everyone)
    obs = env._observe(env._geometry())

    rows_obs, rows_act, rows_where = [], [], []
    for _ in range(int(seconds / env.dt)):
        act = torch.cat([player._act(player.ppos[k], obs[k:k + 1]) for k in range(2)], 0).clamp(-env.action_clip, env.action_clip)
        rows_obs.append(obs.cpu().numpy().copy())
        rows_act.append(act.cpu().numpy().copy())
        # Where each fighter is in the ring: pelvis x, y, heading, then the centre of each foot. Per fighter, 7 numbers.
        g = env._geometry()
        rows_where.append(torch.cat([g["pos"][0, :, :2], g["yaw"][0, :, None], g["foot_xy"][0].reshape(env.K, 4)], dim=-1).cpu().numpy().copy())
        obs, _, done, _ = env.step(act)
        if bool(done.any()):
            break
    starts = []
    for k in range(env.K):
        rq = env.root_q[k]
        w, z = float(q[0, rq + 3]), float(q[0, rq + 6])
        starts.append({"x": float(q[0, rq]), "y": float(q[0, rq + 1]), "yaw": 2.0 * float(np.arctan2(z, w))})
    ref = {
        "name": f"{a}_vs_{b}", "fighters": [a, b], "iteration": player.iters, "obs_size": env.obs_dim,
        "act_size": env.A, "dt": env.dt, "starts": starts, "steps": len(rows_obs),
        "obs": [round(float(x), 5) for x in np.array(rows_obs).reshape(-1)],
        "act": [round(float(x), 5) for x in np.array(rows_act).reshape(-1)],
        "where": [round(float(x), 4) for x in np.array(rows_where).reshape(-1)],
        "ghost": ghost,
    }
    out = os.path.join(HERE, "logs", f"reference_{a}_vs_{b}{'_ghost' if ghost else ''}.json")
    json.dump(ref, open(out, "w", encoding="utf-8"))
    print(f"wrote {out}: {ref['steps']} steps ({ref['steps'] * env.dt:.1f} s) at iteration {player.iters}, starts {starts}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", default="")
    ap.add_argument("--ckpt", default="")
    ap.add_argument("--seconds", type=float, default=3.0)
    ap.add_argument("--match", nargs=2, default=[], metavar=("A", "B"), help="record the two in the ring together instead")
    ap.add_argument("--run", default="match")
    ap.add_argument("--ghost", action="store_true", help="with --match: gloves that touch nothing")
    args = ap.parse_args()
    if args.match:
        record_match(args.match[0], args.match[1], args.run, args.seconds, args.ghost)
        return
    if not args.name:
        raise SystemExit("--name or --match is needed")

    xml = os.path.join(HERE, "models", f"{args.name}_bag.xml")
    out = os.path.join(HERE, "models", f"{args.name}_inertia.json")
    data = inertia(xml)
    json.dump(data, open(out, "w", encoding="utf-8"), indent=1)
    print(f"wrote {out}: {len(data['bodies'])} bodies, {data['total_mass']:.2f} kg")

    ckpt = args.ckpt or os.path.join(HERE, "checkpoints", f"bag_{args.name}", "latest.pt")
    if not os.path.exists(ckpt):
        print(f"no checkpoint at {ckpt}; inertia only")
        return
    from view_box import Player
    with tempfile.TemporaryDirectory() as tmp:
        player = Player(ckpt, ghost_model(xml, tmp), seed=0, deterministic=True, device="cpu")
        steps = int(args.seconds / player.env.dt)
        ref = {
            "name": args.name,
            "checkpoint": os.path.relpath(ckpt, HERE).replace("\\", "/"),
            "iteration": player.iters,
            "obs_size": player.env.obs_dim,
            "act_size": player.env.A,
            "dt": player.env.dt,
            "start": [float(x) for x in player.env.default_qpos[:3]],
            "hold": record(player, steps, use_policy=False),
            "policy": record(player, steps, use_policy=True),
        }
    out = os.path.join(HERE, "logs", f"reference_{args.name}.json")
    json.dump(ref, open(out, "w", encoding="utf-8"))
    print(f"wrote {out}")
    print(f"  hold:   {ref['hold']['summary']}")
    print(f"  policy: {ref['policy']['summary']}")


if __name__ == "__main__":
    main()
