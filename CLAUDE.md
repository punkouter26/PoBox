# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

House rules (git, training, physics, reports, TL;DR) are in `AGENTS.md`; follow them. Read before starting:
`git log -8`, the top of `DOCS/README.md` (running history) and `tasks.md` (live plan). `rl_optimization_log.md`
is the run-by-run training log; log training decisions there. The owner often runs several Claude sessions on
this repo at once.

## What this is

A portrait mobile boxing broadcast: two physics-driven boxers (no animation) fight while the game films,
scores and commentates. Unity 6000.6.0f1, URP 17.6, Cinemachine, UI Toolkit, Unity Inference Engine (Sentis)
for the brains, the official MuJoCo Unity plugin 3.5.0 for the fight's physics. **No PhysX in the fight**: the
arena holds no Rigidbody, Collider, Joint or ArticulationBody. The trainer is Python + MuJoCo Warp in `training/`.
No ML-Agents.

## Architecture (the parts that span files)

- **Decision loop, every 0.02 s, red then blue** (`Assets/Scripts/Mj`): `MjBoxer.Observe` reads `mjData` (103
  numbers) -> `MjBoxer.Infer` (Inference Engine worker, CPU) -> `MjBoxer.Drive` writes 21 PD joint targets to
  `mjData.ctrl` -> the plugin's `MjScene` steps 4 x 0.005 s -> `MjRing.AfterPhysics` measures punches and falls.
- **Game layer** (`Assets/Scripts/Runtime`): `Sim/MjBrain.cs` sits between a `Fighter` (damage, daze, down) and
  its `MjBoxer` (drive strength, get-up brain swap, punch naming). `Fighter` publishes on `SimBus` (Hit,
  Knockdown, GotUp); `Bout` + judges run rounds and counts and place boxers; HUD, cameras (`Broadcast`),
  commentary, sound and effects (`Fx`) only listen to `SimBus`.
- **Three brains per boxer** in `Assets/Boxers/NAME/`: `policy.onnx` (footwork), `match.onnx`, `getup.onnx`,
  plus `config.json` and C MuJoCo reference recordings used by the gate tests. `MjRetrofit.Promote(name, run)`
  copies a training run's policy and recordings there.
- **One body file on both sides**: Unity's boxer prefabs compile to exactly the trainer's MJCF
  (`training/models/v2`, rigs in `training/rigs/v2`). Parity is the core invariant: if a boxer stands in C MuJoCo
  and falls in Unity, stop and fix the cause before training more. Check with menu
  `PoBox/Mj/Check Every Boxer Against Training` and the play-mode tests `MjParityTests` / `MjGateTests`.
- **Scenes are authored from prefabs**, not generated at runtime: `Assets/Scenes/Menu.unity` (pick boxers),
  `Arena.unity` (every boxer as switched-off `NAME Red`/`NAME Blue` prefabs; `MatchSetup` enables the picked pair
  before MuJoCo compiles), `Melee.unity` (free-for-all, menu ALL: `FreeForAll` replaces `Bout` + `MatchSetup`,
  gives the eight boxers prefixes `a_`..`h_` before compile; `MjRing` handles more than two), `Testbed.unity`
  (one boxer, shoves, a pool of 8 cubes). Eight boxers: Matt, Zombie, Nick, Trump, Grandma, Grandpa, Kim, Sokol.
- **Plugin quirks**: `Packages/org.mujoco` and `Packages/bin.mujoco` are embedded with local changes marked
  `PoBox:`. The plugin copies each `MjActuator.Control` over `mjData.ctrl` after every step, so targets are
  rewritten before each step; `mj_step` leaves body positions one step behind `qpos`, so `mj_kinematics` runs
  before observing (as the trainer does). The `bin.mujoco` 3.5.0 tag ships a mislabelled 3.3.7 Android `.so`;
  the embedded 3.5.0 arm64 build is documented in `Packages/bin.mujoco/README-PoBox.md`. Don't re-add the git
  entry to `Packages/manifest.json`.
- **Training** (`training/`): envs `envs/footwork.py` (stand/walk/turn, style reward from CMU clips via
  `style.py`), `envs/getup.py`, `envs/match.py`; PPO in `ppo.py`; entry point `train_box.py --stage
  {auto,getup,footwork,v2}`. Mesh to body: `glb_to_rig.py` -> `rig_to_mjcf.py` (roster in `roster.json`,
  `tools/build_roster.py`). A checkpoint folder holding `REJECTED.txt` is skipped by the exams and the importer.

## Commands

Unity editor (open) via the Unity CLI, from the repo root:

```
unity command recompile                 then poll  unity command recompile_status
unity command run_tests --mode PlayMode --async_tests true    then  unity command test_status
unity command run_tests --mode PlayMode --filter PoBox.Tests.MjGateTests --async_tests true   # one fixture
unity command menu --path "PoBox/Dev/Run Script"   # runs Temp/pobox-dev.txt, output in Temp/pobox-dev-out.txt
```

- Run only this project's tests with the filter `PoBox.Tests` (fixtures `MjParityTests`, `MjGateTests`,
  `OneScreenTests` incl. the layout audit, `FreeForAllTests`, `StateFlowTests`). One fixture per `--filter`: a
  semicolon list matches nothing; the plugin's own tests fail when run
  after ours (a leftover scene).
- `unity command eval` has a 5 s main-thread limit; split long editor work. A test run can leave the editor in
  play mode, which silently blocks compiles: check `EditorApplication.isPlaying` and stop play first.
- Interrupted play runs can bake tilted gravity or zero timeScale into ProjectSettings; check the diff.
- Never edit a `.unity` file on disk while the editor has that scene open: Unity raises a modal ("modified externally") that
  blocks its main thread, and every CLI command times out until someone clicks Reload. Edit scenes through `eval`.
- Compile-check C# without touching the editor: `dotnet build PoBox.Mj.csproj -nologo -v q -o "$TEMP/pobox_csbuild"`.
- Dev script commands (`play`, `stop`, `shot-begin 1080 1920` / `shot-end name`, `call MenuView Fight`, ...) are
  listed at the top of `Assets/Scripts/Editor/DevTools.cs`.
- Android: menu `PoBox/Android/Prepare`, then `PoBox/Android/Build APK` (arm64).
- Unity 6.6 writes two-line `.cs.meta` files (fileFormatVersion + guid); that is normal. A new script the compiler
  silently leaves out of its assembly (missing from `Library/Bee/artifacts/*/PoBox.Runtime.rsp`) was fixed once
  only by renaming the file.
- An unfocused editor runs a few frames a second: `delayCall` may never fire, and play-mode intros take longer.

Training (Python 3.11 venv borrowed from a sibling project, no pip). `../PoDecath` is gone; since 2026-10-14
runs use `../PoOlympic/training/.venv` (MuJoCo + Warp 3.11, torch 2.14). Retrofit-era checkpoints are not on
disk: warm starts are rebuilt from shipped ONNX brains with `tools/onnx_to_pt.py` (into `checkpoints/onnx/`).

```
$py = "..\..\PoOlympic	raining\.venv\Scripts\python.exe"     # from training/
& $py train_box.py --stage footwork --xml models/v2/matt_solo.xml --resume checkpoints/<run>/latest_matt.pt
$env:PYTHONPATH=".mj350"; & $py tools/footwork_c.py --exam     # ladder exam R0-R2 in C MuJoCo 3.5.0
$env:PYTHONPATH=".mj350"; & $py tools/exam_v2.py --only matt   # boxing exam R3-R7
& $py tools/queue_runner.py                                     # works through logs/queue.json
```

Moved-not-deleted files from cleanups live in `../PoBox_quarantine_<date>/` (MANIFEST.md inside). Staged, not-yet-run
jobs are `training/queue_*_staged.json`; copy entries into `logs/queue.json` to run them. `logs/queue.hold` pauses the
runner; it also waits while the GPU is over 25% busy (Unity in play mode).

`PYTHONPATH=training/.mj350` selects MuJoCo 3.5.0 (the version Unity runs); without it the venv's 3.11 is
used. Exams and references must use 3.5.0. One GPU job at a time: before training, check `nvidia-smi`, running
`train_*.py` processes and `training/logs/queue.log`. Unity in play mode on the same GPU cuts Warp from ~60k to
~12k samples a second.
