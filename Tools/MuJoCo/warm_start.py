"""Recover the exported actor and its normalizer; the critic starts fresh."""
from pathlib import Path

import numpy as np
import onnx
from onnx import numpy_helper
import onnxruntime as ort
import torch


def initialize_from_onnx(runner, path: str):
    graph = onnx.load(path).graph
    weights = {x.name: numpy_helper.to_array(x).copy() for x in graph.initializer}
    sub = next(n for n in graph.node if n.op_type == "Sub" and n.input[0] == "obs_0")
    div = next(n for n in graph.node if n.op_type == "Div" and n.input[0] == sub.output[0])
    mean = weights[sub.input[1]]
    denominator = weights[div.input[1]]
    actor = runner.alg.policy.actor
    actor.load_state_dict({name: torch.from_numpy(weights["actor." + name])
                           for name in actor.state_dict()}, strict=True)
    for norm in (runner.obs_normalizer, runner.privileged_obs_normalizer):
        std = denominator - norm.eps
        if mean.shape != tuple(norm._mean.shape) or np.any(std < 0):
            raise ValueError("Unsupported exported normalizer")
        with torch.no_grad():
            norm._mean.copy_(torch.from_numpy(mean))
            norm._std.copy_(torch.from_numpy(std))
            norm._var.copy_(torch.from_numpy(std * std))
            # Retain the existing scaling while allowing gradual adaptation to
            # the new body. The original sample count is absent from ONNX.
            norm.count.fill_(1_000_000)

    # Compare outputs on varied, nonzero observations before using recovered
    # weights for learning. A width match alone cannot establish equivalence.
    norm = runner.obs_normalizer
    norm.eval()
    rng = np.random.default_rng(20260914)
    observations = (mean + rng.normal(size=(16, mean.shape[1])) * denominator).astype(np.float32)
    session = ort.InferenceSession(str(Path(path).resolve()), providers=["CPUExecutionProvider"])
    reference = np.concatenate([session.run(["continuous_actions"], {"obs_0": x[None]})[0]
                                for x in observations])
    with torch.no_grad():
        recovered = actor(norm(torch.from_numpy(observations).to(runner.device))).cpu().numpy()
    np.testing.assert_allclose(recovered, reference, atol=2e-4, rtol=2e-4)
    norm.train()
    print("WARM_START actor matches exported ONNX; max error %.8f; critic is new" %
          float(np.max(np.abs(recovered - reference))), flush=True)
