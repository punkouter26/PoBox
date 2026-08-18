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
0. `Assets/Scenes/SCN_MENU.unity` — opening menu (version stamp lives here)
1. `Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity` — balance contest

Training scenes (not in player build; env build passes them explicitly):
`SCN_TRAIN_BALANCE`, `SCN_TRAIN_BALANCE_GRANDMA`, `SCN_TRAIN_BALANCE_GRANDPA` —
each has 16 fighters. Headless env build: `Builds/BoxerBalanceEnv/`.

### Key docs
- `Docs/TRAINING.md` — training commands, run-02 changes, curriculum
- `CREDITS.md` — third-party asset attribution (crowd audio is CC-BY, must ship in credits)

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
