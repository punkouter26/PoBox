"""A shipped brain back into a training checkpoint: Assets/Boxers/NAME/{policy,match,getup}.onnx -> a .pt that
train_box.py, train_gauntlet.py and train_getup.py can --resume from (or a gauntlet can freeze as an opponent).

    python tools/onnx_to_pt.py --onnx ../Assets/Boxers/Nick/match.onnx --out checkpoints/onnx/match_nick.pt

The ONNX graph (ppo.ExportPolicy) holds the actor, the critic and the observation normaliser's mean and std, so
everything comes back but the action noise, which is not in it: --std sets it (training's own max_std then
holds it). The normaliser's count is set very high (--count): at 1e5 one rollout moved the statistics enough that the first
update tore the policy up (f1_zombie, KL 291 at iteration 0). Written 2026-10-14, when the checkpoints of the retrofit were not on this disk.
"""
from __future__ import annotations

import argparse
import os
import sys

import numpy as np
import onnx
import onnx.numpy_helper
import torch

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from ppo import ActorCritic  # noqa: E402


def convert(src: str, dst: str, std: float, count: float) -> None:
    w = {i.name: torch.from_numpy(onnx.numpy_helper.to_array(i).copy()) for i in onnx.load(src).graph.initializer}
    obs_dim, act_dim = w["actor.0.weight"].shape[1], w["actor.6.weight"].shape[0]
    model = ActorCritic(obs_dim, act_dim, init_std=std)
    sd = model.state_dict()
    for k in sd:
        if k != "log_std":
            sd[k] = w[k].float()
    model.load_state_dict(sd)
    var = w["std"].float() ** 2 - 1e-8
    torch.save({"model": model.state_dict(),
                "obs_rms": {"mean": w["mean"].float(), "var": var.clamp(min=0.0), "count": torch.tensor(count)},
                "opt": {}, "extra": {"iter": 0, "from_onnx": os.path.abspath(src)}}, dst)

    # The check: the rebuilt policy answers as the shipped graph does.
    import onnxruntime as ort
    obs = np.random.default_rng(0).normal(size=(1, obs_dim)).astype(np.float32) * w["std"].numpy() + w["mean"].numpy()
    want_a, want_v = ort.InferenceSession(src, providers=["CPUExecutionProvider"]).run(None, {"obs": obs})
    x = torch.clamp((torch.from_numpy(obs) - w["mean"]) / w["std"], -10.0, 10.0)
    with torch.no_grad():
        got_a, got_v = model.actor(x).numpy(), model.critic(x).numpy()
    assert np.allclose(got_a, want_a, atol=1e-4) and np.allclose(got_v, want_v, atol=1e-3), "rebuilt policy differs from the ONNX graph"
    print(f"{os.path.basename(src)} -> {dst}: obs {obs_dim}, actions {act_dim}, matches the graph", flush=True)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--onnx", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--std", type=float, default=0.3, help="action noise to start from (not in the graph)")
    ap.add_argument("--count", type=float, default=1e9, help="weight of the donor's observation statistics")
    a = ap.parse_args()
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    convert(a.onnx, a.out, a.std, a.count)
