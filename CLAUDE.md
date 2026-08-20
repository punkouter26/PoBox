# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PoBox is a Unity 6000.5.6f1 (URP, portrait 9:16) active-ragdoll boxing game whose
fighters are driven by ML-Agents policies. Most of the work here is not gameplay
code — it is training brains that can stand and walk, and building the harness
scenes used to evaluate them.

There is no test suite, no linter, and no build script in the repo. The build,
scene generation, and training entry points are all listed below.

## Commands

### Scene and prefab generation (Unity Editor menu)

Scenes and fighter prefabs are **generated artifacts**, not hand-authored. They are
rebuilt from `Tools/ML Boxing/*` menu items backed by `Assets/Scripts/Editor/RigTool_*.cs`.
The numbering is the intended order:

| Menu item | Builds |
|---|---|
| `0. Apply Project Settings` | Locks `Time.fixedDeltaTime = 0.02`, gravity, 16 solver iterations |
| `1. Generate Capsule Biped` / `2. Auto-Rig Selected` / `3. Prepare for Training` | Fighter prefab pipeline |
| `5/5b/5c. Create … Balance Scene` | `SCN_TRAIN_BALANCE`, `_GRANDMA`, `_GRANDPA` |
| `6. Create Walk Training Scene`, `9. Create Locomotion Training Scene` | `SCN_TRAIN_WALK`, `SCN_TRAIN_LOCOMOTION` |
| `7*. Contest Scene` (a–g) | `SCN_TEST_BALANCE_CONTEST`, incrementally |
| `8. Create Walk Contest Scene` | `SCN_TEST_WALK_CONTEST` |
| `1. Create Menu Scene` | `SCN_MENU` (build index 0) |

**Re-running a scene tool overwrites that scene wholesale**, including asset
references you edited by hand. Before running one, check the tool's constants —
e.g. `RigTool_WalkContestScene.LOCOMOTION_BRAIN_PATH` decides which brain the whole
roster gets. A stale constant silently downgrades a scene you just wired up.

### Training

`mlagents-learn` attaches to the **running Editor** on port 5004 — there is no
headless env build in the loop. Start the trainer first, then press Play:

```powershell
.venv\Scripts\activate
mlagents-learn Config\BoxerLocomotion07.yaml --run-id=boxer_locomotion07
```

Run id matches the config name lowercased (`BoxerLocomotion07.yaml` →
`boxer_locomotion07`). Output lands in `results/` (gitignored). If Unity logs
`Couldn't connect to trainer on port 5004 … Will perform inference instead`, no
trainer was listening and the scene just ran the baked brains.

The venv pins are load-bearing — `protobuf 3.20.3`, `torch 2.2.2`, `numpy 1.23.5`,
`mlagents 1.1.0`. Any `pip install` that moves them breaks training; re-pin after.

### WebGL build and deploy

```powershell
Unity.exe -batchmode -quit -projectPath . -buildTarget WebGL `
          -executeMethod PoBox.Editor.Build_WebGL.Build -buildOutput WEB
```

Also available as `Tools/Web/Build WebGL to WEB/`. Output is the committed static
site in `WEB/`, deployed to Azure Static Web Apps by
`.github/workflows/azure-static-web-apps.yml`. See [WEB/README.md](WEB/README.md).
`.github/workflows/build-web.yml` rebuilds on pushes to `main` touching sources.

## Architecture

### The observation-size contract

This is the invariant most likely to bite you, and it has bitten this project
repeatedly. `Agent_FighterBoxing.ComputeObservationCount(jointCount, observeOpponent,
observeFootHeight, observeLocomotionCommand)` is the **single source of truth** for
how wide the observation vector is. Three serialized bool flags on the agent change
it, and each change invalidates every previously trained `.onnx`:

- `_observeOpponent` — +19 (boxing phase; false in balance/walk)
- `_observeFootHeight` — +2
- `_observeLocomotionCommand` — +6 (commanded speed, goal direction, gait clock)

Current fighters: 14 joints → 121 without the locomotion command, 127 with it.

Every place that sizes `BehaviorParameters.VectorObservationSize` must call
`ComputeObservationCount` (or `Agent_FighterBoxing.ExpectedObservationCount`) rather
than restate the flags — `RigTool_PrepareForTraining`, `RigTool_BalanceScene`,
`RigTool_LocomotionScene`, and `Systems_ContestSpawner.Configure` all do.

Two failure modes to know:

1. **Sensor too small.** ML-Agents logs `More observations (N) made than vector
   observation size (M)` per step and truncates. Loud but easy to drown in.
2. **Model doesn't match sensor.** ML-Agents only compares model shape to
   `BrainParameters` from the *BehaviorParameters inspector*
   (`Editor/BehaviorParametersEditor.cs`); its runtime path checks the model version
   and nothing else. A brain assigned from code — which is every contest brain —
   mismatches in **total silence**. `Systems_ContestSpawner.WarnOnObservationMismatch`
   exists to catch this and is Editor/dev-build only.

Brain folder names under `Assets/Agents/` have historically lied about which
generation they contain. Verify with the ONNX input shape before trusting one;
`Locomotion_gen7/SOURCE.txt` is the format for recording provenance.

### Agent / rig / reward split

- `Agent_FighterBoxing` (`Assets/Scripts/Agent/`) — the only `Agent`. Collects
  observations, buffers actions in `OnActionReceived`, and applies them **in
  `FixedUpdate` only**. Also carries the code-driven heuristic PD bot (balance
  strategy + scripted gait) used when `BehaviorType.HeuristicOnly`.
- `Systems_FighterRig` — runtime handle to the ragdoll. Owns the serialized
  `RigJointEntry` list (joints, per-axis ranges, base drive values), maps normalized
  `[-1,1]` actions onto joint target rotations, and probes `GroundY` once in `Awake`.
  Height observations are **ground-relative**, which is what lets the ring sit on a
  1 m platform (`Systems_ContestSpawner.RING_FLOOR_Y`).
- `Reward_*` (`Assets/Scripts/Reward/`) — rewards live in separate components, never
  in the agent. `Reward_Locomotion` is the current line: one brain for both
  mini-games, handed a commanded speed each episode (0 m/s = stand, 1 m/s = walk),
  with the curriculum driving `speed_command_max` through named lessons
  (StandStill → Sway → Shuffle → Step → Stride → Walk).

Execution order is explicit and matters: agent `-100`, rewards `-99`, then the
ML-Agents Academy stepper.

### Training scenes vs contest scenes

- **`SCN_TRAIN_*`** — headless by rule: no cameras, HUD, or audio. N fighter
  instances (16 for balance) on a shared ground box, fully unpacked so training
  components never become prefab overrides. Rewards and shovers attached.
- **`SCN_TEST_*_CONTEST`** — presentation harnesses, explicitly "test-scene harness
  only". Nothing is placed at author time: `Systems_ContestSpawner.SpawnAndBegin`
  instantiates from a serialized roster of `ContestRosterEntry` (prefab + brain +
  tint + `locomotionBrain` flag), then wakes a sleeping systems root holding the
  referee, drama camera, hazards, announcer and FX, which self-discover fighters in
  their own `Start`.
- **`SCN_MENU`** — build index 0. Picks a mini-game and roster, stashes them in a
  `Systems_MiniGameSelection` asset, and loads the contest scene, which skips its own
  setup menu when a selection is present.

**Spawner subtlety:** fighters are instantiated under an *inactive* holder object,
configured, then reparented to the scene root. Reparenting is what fires `Awake`/
`OnEnable`, so `Agent.LazyInitialize` — which snapshots `BrainParameters` to build
the `VectorSensor` — runs *after* `Configure` has corrected the observation size.
Instantiating straight into the scene initializes the agent against the prefab's
stale values instead. Anything that must be set before the sensor exists belongs in
`Configure`.

### Conventions stated in code

These are referred to as "project rules" in comments and are enforced by convention:

- Every app ships one code-driven heuristic bot (here, the PD balance/gait bot).
- No singletons and no `DontDestroyOnLoad` — cross-scene state goes through a
  `ScriptableObject` (`Systems_MiniGameSelection`).
- Opening scene shows a version stamp, top-left, non-pickable.
- Runtime UI is UI Toolkit, portrait 9:16, styled from `Assets/UI/USS_Contest.uss`
  through `PS_Contest` / `TSS_Contest`.
- Observation and action counts are derived from the rig, never hand-typed.

### Naming and assemblies

Type prefixes map to folders under `Assets/Scripts/`: `Agent_`, `Systems_`,
`Reward_`, `Sensor_`, and `RigTool_`/`Build_` for editor-only code. Everything is in
namespace `PoBox` (`PoBox.Editor` for tools), split across two assembly definitions:
`PoBox.Runtime` and `PoBox.Editor`.

### Packages

`Packages/` contains **embedded, locally patched copies** of UniTask, MessagePipe and
VContainer that shadow the git URLs still listed in `Packages/manifest.json` (same
versions). They are patched for Unity 6000.5 — do not "fix" the manifest by removing
the embedded folders.

`com.unity.ml-agents` 4.1.0 already includes what used to be
`com.unity.ml-agents.extensions`; adding that package causes GUID conflicts.
