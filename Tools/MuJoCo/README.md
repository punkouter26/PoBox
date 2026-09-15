# MuJoCo Warp line: Nick

Training RIGGED_Nick to balance and walk in **MuJoCo Warp**, for deployment
into Unity through the **MuJoCo Unity plugin** (`Packages/org.mujoco`,
3.12.0). This is the project's only training line: the policy runs on the
same engine and the same model file in the trainer and in the game, which is
what makes a policy trained here survive the trip into Unity.

## What ships, as of 2026-09-09

### Local verification, 2026-09-14

The current machine has an RTX 2060 with 6 GB. Its restored Python 3.11
environment uses PyTorch 2.8.0 CUDA 12.8, MuJoCo/Warp 3.12.0 and Newton 1.6.0.
Install the CUDA wheels with
`uv pip install --python Tools/MuJoCo/.venv/Scripts/python.exe torch==2.8.0 torchvision==0.23.0 --index-url https://download.pytorch.org/whl/cu128`,
then install `Tools/MuJoCo/requirements.txt` with the same Python environment.

`watch_nick.py` now defaults to Newton's visible viewer, rendering the actual
MuJoCo body states. Newton does not integrate a second copy of the physics.
The viewer follows the pelvis and supports orbit, zoom and pause/step. Use
`--viewer mujoco` for the original MuJoCo viewer. Following a training run loads
its saved MJCF, timestep, decimation and solver settings. For a standalone ONNX,
specify its settings explicitly:

```powershell
Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/watch_nick.py --onnx Assets/Agents/Nick_Balance002/nick_balance_002.onnx --timestep 0.02 --decimation 1 --viewer newton --phases 2
```

Fresh Unity trials after fixing control timing and heading: Nick completed a
balance round and the full 5.6 m crossing in 6.34 s. **Provisional only:** the
body's actuators still have unlimited force. Newton replay measured knee peaks
around 966 Nm and 27.6 rad/s during walking. Finite strength and speed limits,
collision/hazard parity and the other skinned characters remain unfinished.
See `Tools/cleanup/AGENT_VALIDATION.md`; the historical statistics below are not
fresh acceptance evidence for those unfinished requirements.

`Assets/Agents/Nick_Balance002/nick_balance_002.onnx` = `results/nick/nick12/model_3700.pt`,
and **both contest scenes load it** -- `SCN_TEST_BALANCE_CONTEST` and
`SCN_TEST_WALK_CONTEST` carry the same guid at 0.02 s x decimation 1. Ten
consecutive 512-world evaluations at that step: ring (shoves + one hazard per
world) **96.2%**, balance **97.9%**, clean walk **86.5%** at 0.895 m/s. The
brain it replaces measured 76 / 84 / 64. Full derivation, and the three
measurement errors it corrected, in `rl_optimization_log.md` in the repo root.

Two things about this folder's name and history:

- The folder is called `Nick_Balance002` and the brain is not balance-only.
  Renaming it would rewrite the guid both hand-authored scenes reference, so
  the name stays and `SOURCE.txt` says what it actually is.
- **Evaluate at the step the brain was trained at.** `eval_nick.py` defaults
  to 0.005 s; every 0.02 s brain needs `NICK_TIMESTEP=0.02 NICK_DECIMATION=1`
  in the environment. The tool prints its control step on every run now,
  because getting this wrong is what put "CANNOT WALK, 0% full-cap" into this
  project's docs about a brain that walks.

## What shipped as of 2026-09-07 (superseded)

`Assets/Agents/Nick_Locomotion/nick_locomotion.onnx` = `results/nick/nick03/model_2099.pt`
(nick02 to iteration 600, then resumed with the planted-feet term for 1500
more). Fresh-start on 512 worlds: 93% survive 30 s of 150 N shoves every 4 s,
100% walk 20 s at 1.008 m/s. In Unity (`Nick_DemoScene`, shoves on): 0 falls,
7 cm drift standing, 0.968 m/s walking. The full tables lived in a
comparison document removed with the Isaac Lab line on 2026-09-14.

Known cosmetic flaw: he lifts his feet ~0.3 m when walking, a high march. The
clearance factor saturates at 0.10 m and nothing penalises going higher; a
follow-up run should add an over-lift kernel. Change one thing at a time.
(`NickEnvCfg.w_overlift` now exists for this, default 0 and therefore inert;
it has not been run.)

## Layout

| Path | What |
|---|---|
| `nick_unity.xml` | **The model.** The MJCF `MjScene` generates from the demo scene, exported by `RigTool_NickMuJoCo.ExportNickMjcf`. Train on this, never on the authored `Assets/MuJoCoCreature/Model/creature.xml` |
| `nick_env.py` | MuJoCo Warp batch env, rsl_rl `VecEnv`. Observation builder `observe()` is shared with the viewer |
| `train_nick.py` | PPO run: TensorBoard on :6007, MuJoCo viewer following the newest checkpoint |
| `watch_nick.py` | MuJoCo viewer replay of a checkpoint or an exported `.onnx`, BALANCE/WALK phases, `NICK_WATCH` lines |
| `eval_nick.py` | Fresh-start survival/speed/alternation tables on a Warp batch, no domain randomisation |
| `export_onnx.py` | Checkpoint → `Assets/Agents/Nick_Locomotion/nick_locomotion.onnx` + `SOURCE.txt` |
| `parity_check.py` | Element-wise diff of the C# and Python observation builders through the live Editor |
| `.venv/` | Python 3.11, `mujoco==3.12.0`, `mujoco-warp==3.12.0`, `warp-lang 1.17`, `torch 2.14.0+cu130`, `rsl-rl-lib==2.3.3` (gitignored) |

Unity side, in `Assets/MuJoCoCreature/`:

| Path | What |
|---|---|
| `Scripts/CreatureSentisController.cs` | Inference. Builds the observation vector, maps actions to `ctrl`. `_observeLocomotionCommand` adds the 6-term command; `_decimation` holds targets between decisions |
| `Scripts/Systems_NickDemo.cs` | Cycles BALANCE (with shoves) and WALK, logs `NICK_DEMO` lines, HUD, camera |
| `Scripts/Systems_NickParityProbe.cs` | C# half of `parity_check.py` |
| `Editor/RigTool_NickMuJoCo.cs` | `BuildDemoScene`, `ExportNickMjcf`, `PlayDemo`, `StopDemo`, `RunParityProbe` |
| `Scenes/Nick_DemoScene.unity` | Generated. Nick alone, skinned mesh bound to the MuJoCo bodies |

## The pipeline, in order

```powershell
# 1. Demo scene and the model Unity actually runs (Editor open; via Editor_CommandBridge)
echo PoBox.Editor.RigTool_NickMuJoCo.BuildDemoScene > Temp/agent-command.txt
echo PoBox.Editor.RigTool_NickMuJoCo.ExportNickMjcf > Temp/agent-command.txt

# 2. Prove the two observation builders agree (they did: max diff 6e-8 over 121 terms, 2026-09-07)
Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/parity_check.py

# 3. Train. UI up (MuJoCo viewer), TensorBoard on :6007, per AGENTS.md
Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/train_nick.py --run-name nick01 --num-envs 4096 --max-iterations 3000

# 4. Measure fresh-start, then export
Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/eval_nick.py --run nick01
Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/export_onnx.py --run nick01 --notes "<the eval table>"

# 5. Rebuild the demo scene so it picks up the brain, then play it and read NICK_DEMO
echo PoBox.Editor.RigTool_NickMuJoCo.BuildDemoScene > Temp/agent-command.txt
echo PoBox.Editor.RigTool_NickMuJoCo.PlayDemo > Temp/agent-command.txt
```

## Contract

- **Observations: 127** = the 121-term balance vector `CreatureSentisController`
  always built, plus commanded speed, pelvis-local commanded direction (3), and
  the 1.4 Hz gait clock (sin, cos).
- **Foot contact is rest-relative** under the locomotion contract: an ankle
  body within 0.03 m of its rest height. The legacy absolute 0.0525 m threshold
  is kept for the 2026-08-31 balance brain and never fires while standing (the
  ankle rests 15–17 cm up).
- **Actions: 30** zero-centred position targets, canonical order, applied every
  4 physics steps of 0.005 s and held between (50 Hz control).
- **Normaliser baked into the ONNX.** Input `obs_0`, output `continuous_actions`.

## Things learned, 2026-09-07

- **`rsl-rl-lib` drags in `torchvision` from PyPI, which replaces a CUDA torch
  with a CPU one.** Install rsl-rl first, then force-reinstall torch and
  torchvision from the PyTorch index with `--no-deps`. The RTX 5070 Ti
  (Blackwell) needs cu128 or cu130; torch 2.14 only ships on cu130.
- **The Warp step is not the bottleneck; Python is.** At 4096 worlds the four
  physics substeps cost ~11 ms uncontended; the first env version spent 150 ms
  on top in torch, almost all of it device syncs: `x[mask]`, `if mask.any()`,
  `nonzero()`, fresh `torch.tensor(...)` constants. Masked means and
  `where()` everywhere took the step from 183 ms to 113 ms with the GPU shared;
  see `_reset_mask` and `_rewards`.
- **Warp dies at random with `CUDA error: unspecified launch failure`** out of
  the synchronize after the step graph, on driver 610.78 with an RTX 5070 Ti
  at 87 °C and throttled to ~940 MHz. nick02 lost at iteration 699, nick03
  twice inside its first fifty. Nothing in the run precedes it. Hence
  `train_nick_loop.py`: checkpoints every 50 iterations, relaunch with
  `--resume-latest` and an absolute `--until-iteration`, so a crash costs at
  most two minutes. Launch runs through the loop, not `train_nick.py` directly.
- **Check the GPU before judging throughput.** A run from another session
  was drawing 87% of the GPU while the first profile ran.
- **`mujoco_warp` returns quaternions wxyz** (it converts from Warp's xyzw on
  the way out) and `cvel` as `[angular, linear]`, both matching CPU MuJoCo;
  `NickEnv.self_check` measures this rather than assuming it.
- **Nick has no prefab.** The importer built him straight into
  `MuJoCo_TestScene`; `BuildDemoScene` clones that root wholesale so bodies,
  gains and bone bindings stay the ones the 2026-08-31 brain was validated on.
- **`Editor_CommandBridge` only refreshes assets after a successful call**, so
  a brand-new script type is reachable only after some existing method has
  been invoked once (any harmless `PoBox.Editor.*` static will do).
