"""What a boxer's 103 observation numbers really look like in each stage, and which of them are off.

    python tools/obs_audit.py                                   matt (and zombie in the pair stages), 10 s each, CPU
    python tools/obs_audit.py --name kim --opp matt --seconds 20 --worlds 32 --json logs/obs_audit_kim.json

Runs the footwork, match and get-up stages on the CPU (MuJoCo Warp's CPU device; the GPU is left alone) with the
shipped brains in Assets/Boxers/NAME (policy.onnx, match.onnx, getup.onnx; onnxruntime, at the game's batch of one),
or zero actions where a brain or onnxruntime is missing. For every index it prints min, max, mean and std, and flags:

    big    |mean| > 5            a number the network sees far from zero before its scaling
    wide   std > 10              one that swings wider than the rest put together
    dead   std < 1e-4            one that never moves (the command's three in a bout are meant to be)
    clip   over 1% of samples beyond the brain's own scaling (mean, std inside the ONNX) by more than 10:
           the network sees them flattened at the clip, which is where a policy stops telling states apart

and, for footwork, the C MuJoCo 3.5.0 reference recording the Unity gate replays (reference_trajectory.json),
the indices whose mean in the reference is furthest from training's, in training's standard deviations.
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = os.path.dirname(HERE)
sys.path.insert(0, HERE)
from envs.footwork import FootworkEnv  # noqa: E402
from envs.match import GetUpMatchEnv, MatchEnv  # noqa: E402


def labels(order):
    xyz = lambda p: [f"{p}.{c}" for c in "xyz"]
    out = xyz("lin_vel") + xyz("ang_vel") + xyz("gravity")
    out += [f"jp.{n}" for n in order] + [f"jv.{n}" for n in order] + [f"act.{n}" for n in order]
    out += ["foot.l", "foot.r", "height"] + xyz("t_head") + xyz("t_body") + xyz("t_head_vel")
    out += xyz("glove.l") + xyz("glove.r") + xyz("opp_glove.l") + xyz("opp_glove.r")
    return out + ["facing.cos", "facing.sin", "ring.x", "ring.y", "cmd.vx", "cmd.vy", "cmd.wz"]


class Brain:
    """A shipped ONNX brain, one observation at a time as the game runs it; None acts zeros."""

    def __init__(self, path: str):
        self.sess, self.mean, self.std = None, None, None
        if not os.path.exists(path):
            return
        try:
            import onnx
            import onnxruntime as ort
            self.sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
            init = {i.name: onnx.numpy_helper.to_array(i) for i in onnx.load(path).graph.initializer}
            self.mean, self.std = init.get("mean"), init.get("std")
        except Exception as e:
            print(f"  [{os.path.basename(path)}] not usable ({e}); zero actions")

    def act(self, obs: np.ndarray) -> np.ndarray:
        if self.sess is None:
            return np.zeros((obs.shape[0], 21), np.float32)
        return np.concatenate([self.sess.run(["actions"], {"obs": o[None]})[0] for o in obs.astype(np.float32)])


def audit(stage: str, env, brains, seconds: float, names) -> dict:
    steps = int(seconds / env.dt)
    obs = env.reset()
    K = env.K
    rows = []
    for _ in range(steps):
        o = obs.cpu().numpy()
        rows.append(o)
        a = np.zeros((o.shape[0], env.A), np.float32)
        for k in range(K):
            a[k::K] = brains[k].act(o[k::K])
        obs, _, _, _ = env.step(torch.tensor(a))
    x = np.concatenate(rows)
    st = {"min": x.min(0), "max": x.max(0), "mean": x.mean(0), "std": x.std(0)}
    clip = np.zeros(x.shape[1])
    for k in range(K):
        b = brains[k]
        if b.mean is not None:
            z = np.abs(x[k::K] - b.mean) / b.std           # rows are (world, fighter) pairs, fighter fastest
            clip = np.maximum(clip, (z > 10.0).mean(0))
    flags = []
    for i, name in enumerate(names):
        f = []
        if abs(st["mean"][i]) > 5.0:
            f.append("big")
        if st["std"][i] > 10.0:
            f.append("wide")
        if st["std"][i] < 1e-4:
            f.append("dead")
        if clip[i] > 0.01:
            f.append(f"clip {clip[i]:.0%}")
        flags.append(f)
    print(f"\n== {stage}: {x.shape[0]} observations ({steps} steps of {env.N} worlds x {K})")
    print(f"{'i':>3} {'number':22} {'min':>9} {'max':>9} {'mean':>9} {'std':>9}  flags")
    for i, name in enumerate(names):
        if flags[i]:
            print(f"{i:3d} {name:22} {st['min'][i]:9.3f} {st['max'][i]:9.3f} {st['mean'][i]:9.3f} {st['std'][i]:9.4f}  {', '.join(flags[i])}")
    print(f"    ({sum(1 for f in flags if f)} of {len(names)} flagged; the rest are within bounds)")
    return {"stats": {k: v.tolist() for k, v in st.items()}, "clip": clip.tolist(), "flags": flags, "x": x}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", default="matt")
    ap.add_argument("--opp", default="zombie", help="the other boxer in the match and get-up stages")
    ap.add_argument("--worlds", type=int, default=16)
    ap.add_argument("--seconds", type=float, default=10.0, help="per stage")
    ap.add_argument("--stages", nargs="*", default=["footwork", "match", "getup"])
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--json", default="", help="write the numbers here as well")
    args = ap.parse_args()

    torch.manual_seed(args.seed)
    boxer = lambda n: os.path.join(REPO, "Assets", "Boxers", n.capitalize() if n != "lilmatt" else "LilMatt")
    models = os.path.join(HERE, "models", "v2")
    pair = next((p for p in (f"{args.name}_vs_{args.opp}_spar.xml", f"{args.opp}_vs_{args.name}_spar.xml")
                 if os.path.exists(os.path.join(models, p))), None)
    kw = dict(device="cpu", seed=args.seed, cuda_graph=False, obs_noise=1.0)
    out = {}
    for stage in args.stages:
        if stage == "footwork":
            env = FootworkEnv(os.path.join(models, f"{args.name}_solo.xml"), args.worlds, **kw)
            brains = [Brain(os.path.join(boxer(args.name), "policy.onnx"))]
        elif pair is None:
            print(f"no pair model for {args.name} and {args.opp} in models/v2; {stage} skipped")
            continue
        else:
            env = (MatchEnv if stage == "match" else GetUpMatchEnv)(os.path.join(models, pair), args.worlds, push_vel=0.4 if stage == "match" else 0.0,
                                                                     **({"daze": True} if stage == "match" else {}), **kw)
            file = "match.onnx" if stage == "match" else "getup.onnx"
            brains = [Brain(os.path.join(boxer(n), file)) for n in env.names]
        names = labels(env.cfg["joint_order"])
        res = audit(stage, env, brains, args.seconds, names)
        if stage == "footwork":
            ref = os.path.join(boxer(args.name), "reference_trajectory.json")
            if os.path.exists(ref):
                r = np.array([row["obs"] for row in json.load(open(ref, encoding="utf-8"))["rows"]], np.float32)
                gap = np.abs(r.mean(0) - np.array(res["stats"]["mean"])) / np.maximum(np.array(res["stats"]["std"]), 0.05)
                top = np.argsort(-gap)[:6]
                print(f"   C MuJoCo reference ({len(r)} rows, the Unity gate's): furthest from training, in training's std: "
                      + ", ".join(f"{names[i]} {gap[i]:.1f}" for i in top))
                res["reference_gap"] = gap.tolist()
        res.pop("x")
        out[stage] = res
    if args.json:
        with open(args.json if os.path.isabs(args.json) else os.path.join(HERE, args.json), "w", encoding="utf-8") as fh:
            json.dump({"name": args.name, "opp": args.opp, "labels": labels(env.cfg["joint_order"]), **out}, fh, indent=1)


if __name__ == "__main__":
    main()
