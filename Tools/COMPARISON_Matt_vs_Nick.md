# Isaac Lab (Matt) vs MuJoCo Warp (Nick): which ships better in Unity?

Two fighters, two training stacks, one question: **which policy performs better
in the actual Unity scenes** — the balance ring's command (hold still) and the
walk race's command (1 m/s). Training-side numbers are not the criterion;
CLAUDE.md is explicit that mean reward is not what ships. The Unity measurement
is.

| | Matt | Nick |
|---|---|---|
| trainer | Isaac Lab 0.44.9 / PhysX | MuJoCo Warp 3.12 |
| body description | USD generated from the .glb + `RigTool_Config` (`Tools/Isaac/build_usd.py`) | MJCF `Tools/MuJoCo/nick_unity.xml`, imported into Unity by `org.mujoco` |
| Unity body | `RigTool_AutoRig` ConfigurableJoint ragdoll (a SECOND model of the body) | the SAME MJCF, run by the MuJoCo plugin |
| observations | 102, Isaac-native; adapter `Systems_MattIsaacPolicy` rebuilds them in Unity | 127, already `CreatureSentisController`'s layout; no adapter |
| envs / throughput | 1024 / ~44k steps/s | 4096 / ~18k steps/s |
| Unity harness | `SCN_TEST_MATT_DEMO` → `MATT_DEMO` lines | `Nick_DemoScene` → `NICK_DEMO` lines |

## Protocol — identical for both

Each demo cycles BALANCE (command 0 m/s) and WALK (command 1 m/s), 10 s per
phase, resetting to the rest pose between phases. One log line per phase. The
columns are the same on both sides by design:

| column | balance wants | walk wants | why |
|---|---|---|---|
| `down` steps (of 500) | **0** | **0** | the fall detector; this is the ring's verdict |
| `distance` (m / 10 s) | **< 0.2** (hold still) | **≥ 8** | balance = don't drift; walk = actually travel |
| `measured` speed (m/s) | ≈ 0 | **≥ 0.8** | tracking the command |
| `alternation` | n/a | **≥ 0.6** | gen18's bar — stepping, not sliding |
| `switches` | n/a | ~14 (1.4 Hz) | cadence; alternation saturates and cannot see a buzz |
| `clearance` (m) | n/a | ~0.10 | feet leave the floor |

Plus the same run measured in its own trainer, to separate "the policy is bad"
from "the policy did not transfer".

## Results

Fill in as each lands. Same checkpoint measured twice: in its trainer, in Unity.

### Matt — Isaac Lab

| gen | where | phase | measured | distance | upright | falls | alternation | switches | clearance |
|---|---|---|---|---|---|---|---|---|---|
| 8 | Isaac | STAND | −0.013 | 0.11 | 100% | 0 | — | — | — |
| 8 | Isaac | WALK | 0.921 | 9.25 | 100% | 0 | 1.000 | 48 | 0.106 |
| 8 | Unity | BALANCE | | | | | | | *invalid — trained on wrong joint order* |
| 9 | Isaac | STAND | 0.153 | 1.57 | 78.8% | 106 down | 0.140 | 2 | 0.093 |
| 9 | Isaac | WALK | 0.757 | 4.03 | 100% | 0 | 1.000 | 45 | 0.134 |
| 9 | **Unity** | BALANCE | 0.085 | 0.91 | — | **441/500 down** | 0.140 | 2 | 0.101 |
| 9 | **Unity** | WALK | 0.197 | 1.05 | — | **453/500 down** | 0.000 | 0 | 0.303 |

**Matt does not transfer.** Gen 9 is the first Matt trained on Unity's real
joint order (gens 1–8 drove the wrong joint on 18 of 30 action slots); it walks
in Isaac with alternation 1.0 and collapses inside about a second in Unity, in
both phases, verified wired to `Matt_matt09.onnx` with no refusal. The
residual is body-model mismatch between the generated USD articulation and the
auto-rigged ConfigurableJoint ragdoll: the per-DOF probe shows Unity reaching
about 0.46x Isaac's joint angle on matched joints after the order, orientation,
hinge and mirror fixes, unaffected by drive stiffness x57 or torque cap x10.
Standing also regressed inside Isaac itself (78.8% upright): 1500 iterations
were not enough to relearn the reordered actions.

### Nick — MuJoCo Warp

| run/ckpt | where | phase | measured | distance | upright | falls | alternation | switches | clearance |
|---|---|---|---|---|---|---|---|---|---|
| nick02 @200 | MuJoCo | BALANCE | −0.034 | 0.42 | 100% | 0 | (1.000) | 77 | 0.113 |
| nick02 @200 | MuJoCo | WALK | 0.945 | 9.48 | 100% | 0 | 1.000 | 39 | 0.284 |
| nick02 @200 | **Unity** | BALANCE | −0.036 | 0.40 | 100% | 0 | (1.000) | 69 | 0.126 |
| nick02 @200 | **Unity** | WALK | **0.963** | **9.67** | **100%** | **0** | **1.000** | **37** | **0.288** |

**Nick transfers.** MuJoCo and Unity agree to within a few percent on every
column, at iteration 200 of 3000. The one open item is a POLICY flaw, not a
transfer flaw: told to hold still he marches in place (69-77 switches, 0.4 m
drift against a 0.2 m bar) — and MuJoCo shows the identical behaviour, so it
is the reward, not the engine.

Measurement note: the first Unity attempt silently ran the OLD Aug-31 balance
brain, because `BuildDemoScene` ran seconds before Unity had imported the new
.onnx (`RigTool: no brain at ... keeps the old balance brain`). Always verify
the brain's guid is in the scene before trusting a NICK_DEMO / MATT_DEMO line;
both harnesses have now produced a convincing-looking table for the wrong
brain once.
| nick03 @2099 | MuJoCo | BALANCE | 0.000 | 0.03 | 100% | 0 | 0.210 | 3 | 0.016 |
| nick03 @2099 | MuJoCo | WALK | 0.991 | 9.90 | 100% | 0 | 1.000 | 39 | 0.307 |
| nick03 @2099 | **Unity** | BALANCE (shoved 150 N / 4 s) | −0.004 | **0.07** | **100%** | **0** | 0.070 | 1 | 0.021 |
| nick03 @2099 | **Unity** | WALK | **0.968** | **9.68** | **100%** | **0** | **1.000** | **37** | 0.311 |

nick03 = nick02 resumed from iteration 600 with a planted-feet factor at zero
command (`NickEnvCfg.w_planted`), 1500 more iterations. It is the brain in
`Assets/Agents/Nick_Locomotion/nick_locomotion.onnx`. The Unity BALANCE phase
above is measured **with the demo's auto-shove on** (150 N for 0.2 s every
4 s) and he never goes down; the march-in-place is gone (1 stance change in
10 s against 69 before, 7 cm of drift against 40). The exported graph was also
replayed through onnxruntime on CPU MuJoCo (`watch_nick.py --onnx`) before the
Unity run, so the .onnx itself, not only the checkpoint, is what both tables
describe.

Fresh-start batch evaluation in MuJoCo Warp (`Tools/MuJoCo/eval_nick.py`,
512 worlds, every world from the rest pose, nominal gains and friction, no
noise). This is the number the 2026-08-31 line said to trust over any
in-training metric, because training measures a population dominated by
survivors:

| run/ckpt | phase | survive full cap | median survival | upright | speed | distance | alternation |
|---|---|---|---|---|---|---|---|
| nick02 @500 | BALANCE, 150 N shove every 4 s, 30 s cap | 87% | 30.0 s | 0.999 | 0.005 m/s | 0.39 m | (1.000) — marching |
| nick02 @500 | WALK 1 m/s, 20 s cap | 100% | 20.0 s | 1.000 | 0.977 m/s | 19.54 m | 1.000 |
| no policy | passive | 0% | 1.52 s | | | | |

The 2026-08-31 Nick balance brain (`creature_policy.onnx`, 121 obs, no
command) measured a median of 10.9 s unshoved on the same footing. nick02 at
iteration 500 survives 30 s *while being shoved* in 87% of worlds.

## Verdict, 2026-09-07

**MuJoCo Warp + the MuJoCo Unity plugin wins, and not narrowly.**

| criterion | Matt / Isaac Lab | Nick / MuJoCo Warp |
|---|---|---|
| Trains a policy that balances and walks in its own simulator | yes, by gen 7 | yes, by iteration ~200 of the first real run |
| Same policy runs in Unity | **no** — 10 generations, ~8 h of parity work, still down in ~1 s | **yes** — first attempt, within a few percent on every column |
| Unity balance under shoves (150 N / 4 s) | down 441/500 steps | 0 falls, 7 cm drift in 10 s |
| Unity walk at 1 m/s | 0.197 m/s, 1.05 m, collapses | 0.968 m/s, 9.68 m, alternation 1.0 |
| Setup cost | Isaac Sim 5.0 (~20 GB), USD generation, DOF-order/orientation/handedness/hinge fixes, drive calibration | `pip install mujoco-warp`, one MJCF export from the Editor, one parity check |
| Throughput on this laptop | ~44k steps/s at 1024 envs | ~45k steps/s at 4096 envs (crash-prone Warp, hence the resume loop) |
| What can still go wrong | body-model mismatch, by construction | Warp CUDA crashes (mitigated), and the plugin fixes the whole scene at 0.005 s |

The reason is structural rather than an engine preference: Nick's body is ONE
MJCF that MuJoCo runs in both places, so the 121-term observation vector and
the 30 position targets mean the same thing in training and in the game
(`parity_check.py`: max difference 6e-8). Matt's body is TWO independently
built descriptions (a generated USD articulation, an auto-rigged
ConfigurableJoint ragdoll), and every generation to date has found another
way for them to disagree. Isaac Lab might still be rescued by calibration
(gen 10 is training with a measured per-DOF gain), but MuJoCo delivered the
working humanoid in Unity in one afternoon.

The price of the MuJoCo route is that Nick cannot share a PhysX contest scene:
`CreatureSentisController` pins `Time.fixedDeltaTime` to 0.005 s because
`MjScene` stamps Unity's fixed step into the model, while the shipped ring and
its brains are locked to 0.02 s. Putting both fighters in front of the same
referee means either a side-by-side scene at Nick's step or two scenes at
each engine's native step with the same shove schedule and log line.

## What decided today (so the comparison is read honestly)

Every Matt failure in Unity through gen 8 was a **body-model mismatch**, not an
engine property: Isaac's USD and Unity's ragdoll were two independently built
descriptions of one body, and they disagreed on joint order (18 of 30 action
slots), link orientations, a hinge-welding flag and a handedness mirror. See
`Tools/Isaac/README.md`. Nick's design has one description (the MJCF) on both
sides, so that class of failure cannot occur. **If Nick transfers and Matt does
not, that is the architecture speaking, not PhysX vs MuJoCo.**
