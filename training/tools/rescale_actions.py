"""A brain trained at one action scale, made to ask for the same joint targets at another (models/v3's per-joint scale).

    python tools/rescale_actions.py --ckpt checkpoints/onnx/match_matt.pt --config models/v3/matt_policy_config.json \
        --out checkpoints/v3/match_matt.pt

A joint's target is guard + action x scale. With scale s_old (0.5 for every brain before v3) and the config's
action_scale_j s_new, the actor's last layer and its noise are multiplied by r = s_old / s_new joint by joint, and the
"last action" inputs' running mean and spread by r too, so the network sees exactly the numbers it saw before.
The targets then match the old brain's wherever the old target lay inside the joint's range (outside it, both are held
at the limit). The optimiser's state is dropped (its moments were in the old units). --check runs that comparison on
random observations: where the old action was inside its clip, no target may move by more than 1e-3 rad.
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from ppo import PPO, PPOConfig  # noqa: E402


def last_layer(model_sd: dict) -> str:
    idx = max(int(k.split(".")[1]) for k in model_sd if k.startswith("actor.") and k.endswith(".weight"))
    return f"actor.{idx}"


def rescale(ck: dict, ratio: torch.Tensor, A: int) -> dict:
    sd, rms = dict(ck["model"]), dict(ck["obs_rms"])
    w = last_layer(sd)
    sd[w + ".weight"] = sd[w + ".weight"] * ratio[:, None]
    sd[w + ".bias"] = sd[w + ".bias"] * ratio
    sd["log_std"] = sd["log_std"] + torch.log(ratio)
    a0 = 9 + 2 * A                                   # the last action's place in the observation (envs/boxing.py)
    rms["mean"], rms["var"] = rms["mean"].clone(), rms["var"].clone()
    rms["mean"][a0:a0 + A] *= ratio
    rms["var"][a0:a0 + A] *= ratio ** 2
    extra = dict(ck.get("extra", {}))
    extra["action_scale_j"] = None                   # filled in by the caller
    return {"model": sd, "obs_rms": rms, "extra": extra}


def targets(ppo: PPO, obs: torch.Tensor, scale: torch.Tensor, guard, lo, hi, clip: float = 3.0) -> torch.Tensor:
    with torch.no_grad():
        a = ppo.model.actor(ppo.obs_rms.normalize(obs, 10.0)).clamp(-clip, clip)
    return (guard + a * scale).clamp(lo, hi)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ckpt", required=True)
    ap.add_argument("--config", required=True, help="the policy config with action_scale_j (models/v3/NAME_policy_config.json)")
    ap.add_argument("--out", default="")
    ap.add_argument("--old-scale", type=float, default=0.0, help="0 = the checkpoint's own action_scale, else 0.5")
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()

    path = lambda p: p if os.path.isabs(p) else os.path.join(HERE, p)
    ck = torch.load(path(args.ckpt), map_location="cpu", weights_only=False)
    cfg = json.load(open(path(args.config), encoding="utf-8"))
    new = torch.tensor(cfg["action_scale_j"], dtype=torch.float32)
    A = new.numel()
    old_s = args.old_scale or float(ck.get("extra", {}).get("action_scale", 0.5) or 0.5)
    old_j = ck.get("extra", {}).get("action_scale_j")
    old = torch.tensor(old_j, dtype=torch.float32) if old_j else torch.full((A,), old_s)
    ratio = old / new
    out = rescale(ck, ratio, A)
    out["extra"]["action_scale_j"] = new.tolist()
    out["extra"]["rescaled_from"] = {"ckpt": args.ckpt, "action_scale": old.tolist()}
    D = ck["obs_rms"]["mean"].numel()
    print(f"{os.path.basename(args.ckpt)}: {A} joints, ratio {ratio.min():.3f} to {ratio.max():.3f} (old / new scale)")

    if args.check:
        before, after = PPO(D, A, 1, "cpu", PPOConfig()), PPO(D, A, 1, "cpu", PPOConfig())
        before.model.load_state_dict(ck["model"]); before.obs_rms.load_state_dict(ck["obs_rms"])
        after.model.load_state_dict(out["model"]); after.obs_rms.load_state_dict(out["obs_rms"])
        g = torch.Generator().manual_seed(0)
        mean, std = ck["obs_rms"]["mean"], ck["obs_rms"]["var"].sqrt()
        obs = mean + torch.randn(4096, D, generator=g) * std
        a0 = 9 + 2 * A
        obs_new = obs.clone()
        obs_new[:, a0:a0 + A] *= ratio
        guard = torch.tensor(cfg["default_joint_pos"]); lo, hi = torch.tensor(cfg["lower"]), torch.tensor(cfg["upper"])
        t0 = targets(before, obs, old, guard, lo, hi)
        t1 = targets(after, obs_new, new, guard, lo, hi)
        with torch.no_grad():
            raw = before.model.actor(before.obs_rms.normalize(obs, 10.0))
        # Where the old brain asked for more than the clip on a joint whose new scale is larger (r < 1: the elbows,
        # the shoulders), the new one reaches further: that is the point of v3, not a fault of the rescaling.
        pressed = (raw.abs() > 3.0) & (ratio < 1.0)
        gap = (t0 - t1).abs()[~pressed].max().item()
        further = (t1 - t0).abs()[pressed]
        print(f"check: largest change of a joint target where the old action was inside its clip {gap:.2e} rad; "
              f"{pressed.float().mean():.1%} of joint-samples were pressing on the clip, and there reach up to "
              f"{further.max().item() if further.numel() else 0.0:.2f} rad further (mean {further.mean().item() if further.numel() else 0.0:.2f})")
        if gap > 1e-3:              # float32 rounding through two rescaled layers is ~1e-4 rad
            raise SystemExit("the rescaled brain does not ask for the same targets inside the old clip")
    if args.out:
        os.makedirs(os.path.dirname(path(args.out)), exist_ok=True)
        torch.save(out, path(args.out))
        print(f"wrote {args.out}")


if __name__ == "__main__":
    main()
