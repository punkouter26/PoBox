# CLAUDE.md - Project Context for AI Assistants

## Project Overview

| Property | Value |
|----------|-------|
| **Project** | PoBox |
| **Unity Version** | 6000.5.6f1 |
| **Render Pipeline** | Universal Render Pipeline (URP) |
| **Input** | Input System (new) |
| **ML** | ML-Agents 4.1.0 (Unity package) + `mlagents` Python in `.venv/` |

### Scenes in Build
0. `Assets/Scenes/SCN_MENU.unity` — opening scene (version stamp lives here).
   Pick a mini-game, then a fighter per slot: 8 for balance, 4 for walk. Picks
   travel to the loaded scene in `Assets/Config/SO_MiniGameSelection.asset`.
1. `Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity` — balance contest. Its own
   setup menu still runs when the scene is opened directly, and defers to
   SCN_MENU whenever a selection has already been made.
2. `Assets/Scenes/SCN_TEST_WALK_CONTEST.unity` — walk race across the ring.

SCN_MENU was removed 2026-08-18 and reinstated 2026-08-19: with two mini-games
to choose between, the menu is no longer contest-specific. Both contest scenes
spawn their fighters at runtime from the menu picks — nothing is baked in.

Training scenes (not in player build; env build passes them explicitly):
- `SCN_TRAIN_LOCOMOTION` — current model line. 16 fighters, `Reward_Locomotion`,
  one brain for standing AND walking driven by a commanded speed that is also an
  observation (125 obs). Curriculum `speed_command_max` 0 → 1.0 m/s. Headless env
  build: `Builds/BoxerLocomotionEnv/`.
- `SCN_TRAIN_BALANCE`, `SCN_TRAIN_BALANCE_GRANDMA`, `SCN_TRAIN_BALANCE_GRANDPA`,
  `SCN_TRAIN_WALK` — superseded older model line (121 obs, `Reward_Balance`).
  Their `.onnx` files are NOT loadable by the locomotion scenes: the command
  observations changed the input layout.

### Key docs
- `CREDITS.md` — third-party asset attribution (crowd audio is CC-BY, must ship in credits)
- `Docs/TRAINING.md` — MISSING as of 2026-08-19; deleted from the working tree by
  something outside this project's tooling. Recover with
  `git restore Docs/TRAINING.md` if the training commands are still wanted.

---

## MCP Integration

- Bridge: **Official Unity Pipeline** (`com.unity.pipeline`), registered in Claude Code as MCP server `unity-editor-mcp` (via `unity mcp`).
- The Unity Editor must be open for MCP tools to work.
- Use MCP tools for scene/prefab manipulation — never text-edit `.unity`/`.prefab` files.
- Do NOT run a second MCP bridge (e.g. Coplay) at the same time.

## ML-Agents Workflow

- Versions: C# `com.unity.ml-agents` 4.1.0 ↔ Python `mlagents` 1.1.0 (keep release pair in sync).
- Activate Python env: `.venv\Scripts\activate`
- Train: `mlagents-learn <config.yaml> --run-id=<name>` then press Play in the Editor.
- ALWAYS start TensorBoard when training starts: `tensorboard --logdir results`
- Config `<Name><Phase><NN>.yaml` pairs 1:1 with `--run-id=<name>_<phase><nn>`.
- Training output goes to `results/` (gitignored).
- Full ML rules: `.claude/rules/mlagents.md`

## Rules

Follow the coding standards in:
- `.claude/rules/csharp-unity.md` — C# style conventions
- `.claude/rules/performance.md` — performance rules (zero-alloc Update)
- `.claude/rules/serialization.md` — serialization safety (FormerlySerializedAs)
- `.claude/rules/architecture.md` — architecture patterns (composition, SO, events)
- `.claude/rules/unity-specifics.md` — Unity-specific rules (Editor/Runtime, threading)
- `.claude/rules/mlagents.md` — ML-Agents rules (naming, physics, display, MLOps)

## Key Conventions

- `[SerializeField] private` — never expose public fields for inspector
- `[FormerlySerializedAs]` — ALWAYS when renaming serialized fields
- Cache `GetComponent` in Awake — never call in Update
- `obj == null` not `obj?.Method()` — Unity overrides == for destroyed objects
- Editor code in `Editor/` folder or `#if UNITY_EDITOR` guard
- Custom 3D models: import `.glb`/`.fbx` into `Assets/Art/`
- Scenes prefix `SCN_` (training: `SCN_TRAIN_<NAME>`); scripts prefix-match their folder (`Agent_`, `Sensor_`, `Reward_`, `Systems_`)
- Runtime UI is UI Toolkit only; Portrait 9:16 @ 60 FPS; version stamp top-left of opening scene
- ML actions applied in `FixedUpdate` only; Δt locked at 0.02 s; actions normalized to [−1, 1]
