"""Export a trained Nick policy to ONNX for CreatureSentisController.

    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/export_onnx.py --run nick01
    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/export_onnx.py --run nick01 --checkpoint model_1500.pt

WHAT GOES IN THE GRAPH.

1. THE OBSERVATION NORMALISER IS BAKED IN, using rsl_rl's own
   EmpiricalNormalization module in eval mode rather than a re-typed formula
   (its epsilon is 1e-2 on the std, which a naive sqrt(var+1e-8) misses).
   Unity feeds raw observations; the graph does the rest.

2. INPUT "obs_0" [batch, 127], OUTPUT "continuous_actions" [batch, 30] -- the
   names Systems_BrainCompatibility.ObservationWidth and the controller's
   _actionOutputName look for.

3. The graph is verified against the torch policy with onnxruntime before
   anything is written next to the game. An export that loads and returns
   different numbers is the worst outcome available, because nothing
   downstream would notice.

Writes Assets/MuJoCoCreature/Policy/nick_locomotion.onnx plus SOURCE_nick_locomotion.txt,
the provenance format this project uses for brains.
"""
from __future__ import annotations

import argparse
import datetime
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).parent
REPO = HERE.parent.parent
LOG_ROOT = REPO / "results" / "nick"
DEFAULT_OUT = REPO / "Assets" / "MuJoCoCreature" / "Policy" / "nick_locomotion.onnx"

parser = argparse.ArgumentParser()
parser.add_argument("--run", default="nick01")
parser.add_argument("--checkpoint", default=None, help="model_N.pt; default is the highest N")
parser.add_argument("--out", default=str(DEFAULT_OUT))
parser.add_argument("--notes", default="", help="measured numbers to record in SOURCE")
args = parser.parse_args()

import torch                                   # noqa: E402
import torch.nn as nn                          # noqa: E402

sys.path.insert(0, str(HERE))
from nick_env import ACT_DIM, OBS_BASE, OBS_COMMAND   # noqa: E402

OBS_DIM = OBS_BASE + OBS_COMMAND

run_dir = LOG_ROOT / args.run
if args.checkpoint is None:
    candidates = sorted(run_dir.glob("model_*.pt"), key=lambda p: int(re.search(r"(\d+)", p.stem).group(1)))
    if not candidates:
        raise SystemExit("no model_*.pt under %s" % run_dir)
    ckpt_path = candidates[-1]
else:
    ckpt_path = run_dir / args.checkpoint
if not ckpt_path.exists():
    raise SystemExit("no checkpoint at %s" % ckpt_path)
ckpt = torch.load(ckpt_path, map_location="cpu", weights_only=False)
print("checkpoint %s (iteration %s)" % (ckpt_path, ckpt.get("iter", "?")))

from rsl_rl.modules import ActorCritic, EmpiricalNormalization   # noqa: E402

actor_critic = ActorCritic(
    num_actor_obs=OBS_DIM, num_critic_obs=OBS_DIM, num_actions=ACT_DIM,
    actor_hidden_dims=[512, 256, 128], critic_hidden_dims=[512, 256, 128], activation="elu",
)
actor_critic.load_state_dict(ckpt["model_state_dict"])
actor_critic.eval()

normalizer = EmpiricalNormalization(shape=[OBS_DIM])
norm_state = ckpt.get("obs_norm_state_dict")
if norm_state is None:
    raise SystemExit("checkpoint has no obs_norm_state_dict; training ran without empirical "
                     "normalization and this exporter assumes it. Refusing to guess.")
normalizer.load_state_dict(norm_state)
normalizer.eval()


class NickPolicy(nn.Module):
    """Normaliser + actor mean, so Unity feeds raw observations and gets actions."""

    def __init__(self, normalizer, actor):
        super().__init__()
        self.normalizer = normalizer
        self.actor = actor

    def forward(self, obs_0):
        return self.actor(self.normalizer(obs_0))


policy = NickPolicy(normalizer, actor_critic.actor).eval()

out = Path(args.out)
out.parent.mkdir(parents=True, exist_ok=True)
dummy = torch.zeros(1, OBS_DIM)
torch.onnx.export(
    policy, (dummy,), str(out),
    input_names=["obs_0"], output_names=["continuous_actions"],
    dynamic_axes={"obs_0": {0: "batch"}, "continuous_actions": {0: "batch"}},
    opset_version=15, do_constant_folding=True, dynamo=False,
)
print("wrote %s" % out)

import onnxruntime as ort   # noqa: E402

sess = ort.InferenceSession(str(out), providers=["CPUExecutionProvider"])
inp = sess.get_inputs()[0]
outp = sess.get_outputs()[0]
print("graph: %s %s -> %s %s" % (inp.name, inp.shape, outp.name, outp.shape))
if inp.name != "obs_0" or outp.name != "continuous_actions" or inp.shape[1] != OBS_DIM or outp.shape[1] != ACT_DIM:
    raise SystemExit("ABORT: exported graph does not carry the expected names/shapes.")
worst = 0.0
for _ in range(8):
    probe = torch.randn(4, OBS_DIM) * 2.0
    got = torch.tensor(sess.run(None, {"obs_0": probe.numpy()})[0])
    with torch.no_grad():
        want = policy(probe)
    worst = max(worst, (got - want).abs().max().item())
print("onnxruntime check: max |onnx - torch| = %.3e over random observations" % worst)
if worst > 1e-4:
    raise SystemExit("ABORT: exported graph does not match the torch policy.")

source = out.parent / ("SOURCE_%s.txt" % out.stem)
source.write_text(
    "%s\n%s\n\n"
    "WHAT      Locomotion brain for Nick (RIGGED_Nick.glb on the MuJoCo Unity plugin),\n"
    "          trained in MUJOCO WARP -- the second policy in this project not trained\n"
    "          by Unity ML-Agents, and the comparison point for the Isaac Lab line.\n"
    "CONTRACT  %d observations / %d continuous actions.\n"
    "          CreatureSentisController.GatherObservations with _observeLocomotionCommand\n"
    "          on: the 121-term balance vector plus commanded speed, pelvis-local\n"
    "          commanded direction (3) and the 1.4 Hz gait clock (sin, cos).\n"
    "          Foot contact is rest-relative (ankle within 0.03 m of rest).\n"
    "          Actions are zero-centred position targets in canonical order.\n"
    "          Control every 4 physics steps of 0.005 s (decimation 4, 50 Hz).\n"
    "          The observation normaliser is BAKED INTO THE GRAPH; feed raw.\n"
    "          input obs_0 [batch,%d], output continuous_actions [batch,%d].\n"
    "MODEL     Tools/MuJoCo/nick_unity.xml -- the MJCF MjScene generates, exported by\n"
    "          RigTool_NickMuJoCo.ExportNickMjcf. NOT the authored creature.xml.\n"
    "TRAINED   %s, results/nick/%s, %s (iteration %s), rsl_rl PPO on mujoco_warp.\n"
    "          Config: results/nick/%s/config.json.\n"
    "MEASURED  %s\n"
    "EXPORT    Tools/MuJoCo/export_onnx.py --run %s --checkpoint %s\n"
    % (out.name, "=" * len(out.name), OBS_DIM, ACT_DIM, OBS_DIM, ACT_DIM,
       datetime.date.today().isoformat(), args.run, ckpt_path.name, ckpt.get("iter", "?"),
       args.run, args.notes or "(run Tools/MuJoCo/eval_nick.py and record the table here)",
       args.run, ckpt_path.name))
json.dump({"observations": OBS_DIM, "actions": ACT_DIM, "decimation": 4, "run": args.run,
           "checkpoint": ckpt_path.name, "iteration": ckpt.get("iter", None),
           "normaliser_baked_in": True},
          open(out.parent / ("%s.contract.json" % out.stem), "w"), indent=2)
print("wrote %s" % source)
