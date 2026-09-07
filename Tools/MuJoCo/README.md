# MuJoCo Warp line: Nick

Training RIGGED_Nick to balance and walk in **MuJoCo Warp**, for deployment
into Unity through the **MuJoCo Unity plugin** (`Packages/org.mujoco`,
3.12.0), and the comparison point for the Isaac Lab line in `Tools/Isaac/`.

Two custom creatures, two trainers, one question: which route gets a working
humanoid into Unity?

| | Matt (Tools/Isaac) | Nick (this folder) |
|---|---|---|
| Trainer | Isaac Lab 2.2 / Isaac Sim 5.0, rsl_rl PPO | mujoco_warp 3.12.0, rsl_rl PPO (same version, same hyperparameters) |
| Training physics | PhysX articulations | MuJoCo (Warp port) |
| Unity physics | Unity PhysX `ConfigurableJoint` ragdoll | MuJoCo 3.12.0 itself, via the plugin's `MjScene` |
| Sim-to-sim gap | PhysX → PhysX, but articulation vs. ragdoll, D6 vs. slerp drives, USD vs. bone frames | MuJoCo → MuJoCo, same version, same MJCF |
| Observation contract | Isaac-native 102, adapter on the Unity side | The project's own 121 (+6 command) built identically in C# and Python |

The whole difference is the last two rows. Matt's policy has to survive a
translation between two physics engines and two rig conventions; Nick's runs
on the same engine and the same model file in both places.

## What ships, as of 2026-09-07

`Assets/MuJoCoCreature/Policy/nick_locomotion.onnx` = `results/nick/nick03/model_2099.pt`
(nick02 to iteration 600, then resumed with the planted-feet term for 1500
more). Fresh-start on 512 worlds: 93% survive 30 s of 150 N shoves every 4 s,
100% walk 20 s at 1.008 m/s. In Unity (`Nick_DemoScene`, shoves on): 0 falls,
7 cm drift standing, 0.968 m/s walking. The full tables and the Isaac
comparison are in `Tools/COMPARISON_Matt_vs_Nick.md`.

Known cosmetic flaw: he lifts his feet ~0.3 m when walking, a high march. The
clearance factor saturates at 0.10 m and nothing penalises going higher; a
follow-up run should add an over-lift kernel. Change one thing at a time.

## Layout

| Path | What |
|---|---|
| `nick_unity.xml` | **The model.** The MJCF `MjScene` generates from the demo scene, exported by `RigTool_NickMuJoCo.ExportNickMjcf`. Train on this, never on the authored `Assets/MuJoCoCreature/Model/creature.xml` |
| `nick_env.py` | MuJoCo Warp batch env, rsl_rl `VecEnv`. Observation builder `observe()` is shared with the viewer |
| `train_nick.py` | PPO run: TensorBoard on :6007, MuJoCo viewer following the newest checkpoint |
| `watch_nick.py` | MuJoCo viewer replay of a checkpoint or an exported `.onnx`, BALANCE/WALK phases, `NICK_WATCH` lines |
| `eval_nick.py` | Fresh-start survival/speed/alternation tables on a Warp batch, no domain randomisation |
| `export_onnx.py` | Checkpoint → `Assets/MuJoCoCreature/Policy/nick_locomotion.onnx` + `SOURCE_nick_locomotion.txt` |
| `parity_check.py` | Element-wise diff of the C# and Python observation builders through the live Editor |
| `.venv/` | Python 3.11, `mujoco==3.12.0`, `mujoco-warp==3.12.0`, `warp-lang 1.17`, `torch 2.14.0+cu130`, `rsl-rl-lib==2.3.3` (gitignored) |

Unity side, in `Assets/MuJoCoCreature/`:

| Path | What |
|---|---|
| `Scripts/CreatureSentisController.cs` | Inference. Builds the observation vector, maps actions to `ctrl`. `_observeLocomotionCommand` adds the 6-term command; `_decimation` holds targets between decisions |
| `Scripts/Systems_NickDemo.cs` | Cycles BALANCE (with shoves) and WALK, logs `NICK_DEMO` lines with the `MATT_DEMO` columns, HUD, camera |
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
  the 1.4 Hz gait clock (sin, cos) — `Agent_FighterBoxing`'s
  `_observeLocomotionCommand` block, term for term.
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
- **Check the GPU before judging throughput.** An Isaac run from another
  session was drawing 87% of the GPU while the first profile ran.
- **`mujoco_warp` returns quaternions wxyz** (it converts from Warp's xyzw on
  the way out) and `cvel` as `[angular, linear]`, both matching CPU MuJoCo;
  `NickEnv.self_check` measures this rather than assuming it.
- **Nick has no prefab.** The importer built him straight into
  `MuJoCo_TestScene`; `BuildDemoScene` clones that root wholesale so bodies,
  gains and bone bindings stay the ones the 2026-08-31 brain was validated on.
- **`Editor_CommandBridge` only refreshes assets after a successful call**, so
  a brand-new script type is reachable only after some existing method has
  been invoked once (any harmless `PoBox.Editor.*` static will do).
