# ML-Agents Rules (UNITY_RULES)

Base style guide: https://unity.com/resources/c-sharp-style-guide-unity-6

## 1. Naming

- **Trained models:** `Assets/Agents/<Name>_v<NN>/` housing the `.onnx` (+ its `.meta`).
- **Script prefixes** — scripts live under `Assets/Scripts/<Folder>/` and their file name prefix MUST match the folder:
  - `Agent_` — ML-Agents `Agent` subclasses
  - `Sensor_` — observation collectors / custom sensors
  - `Reward_` — reward computation
  - `Systems_` — referees, presentation, persistence, UI
- **Scenes:** prefix `SCN_`. Training scenes: `SCN_TRAIN_<NAME>` (no suffixes).
- **Environment builds:** output to `Builds/<Name>Env/`.
- **Configs & run IDs:** config `<Name><Phase><NN>.yaml` pairs 1:1 with `--run-id=<name>_<phase><nn>`.

## 2. Physics & Biomechanics

- **Simulation defaults:** Earth gravity (−9.81 m/s²), SI units, realistic friction, deterministic execution. Realistic agent sizes and mass. Place 1 m² semi-transparent markers on the ground plane for scale.
- **Fixed timestep:** apply actions strictly in `FixedUpdate`. Lock Δt = 0.02 s and solver iterations — overrides silently change dynamics fitted against trained brains.
- **Action mapping:** normalize actions to [−1, 1], scale directly to real joint limits and DoF.
- **Fatigue mechanics:** read load from **applied torque, not the action vector** — isometric bracing produces near-zero action at near-maximum torque. Clear fatigue on reset *before* restoring motors.
- **Hierarchical anchors:** derive joint anchors from segment lengths so moving a segment naturally shifts downstream joints.
- **Joint range verification:** test joint ranges in **parent-local** space with gravity disabled — verifies body counter-rotation, detects world-space drift, and confirms `jointAngle` sign.

## 3. Display

- **UI:** C# UI Toolkit at runtime exclusively (no UGUI at runtime).
- **Framerate & orientation:** Portrait 9:16 at 60 FPS. Set `QualitySettings.vSyncCount = 0` so `Application.targetFrameRate = 60` is honored.
- **Version stamp:** display `Application.version` anchored top-left of the opening scene — inset layer, non-pickable, outside ScrollViews.
- **Panel scaling:** scale panel UI on width; validate dimensions against a live device capture.

## 4. MLOps

- **Version alignment:** exact release-pair parity between C# `com.unity.ml-agents` and Python `mlagents` — mismatches reject the comms API handshake.
- **Asset integrity:** overwrite `.onnx` files **in place** to preserve the Unity `.meta` GUID references.
- **Headless execution:** pass `--env` with `--no-graphics` and explicit `--base-port` (allocate consecutive ports to avoid collision hangs). Run 4–8 envs to leave CPU cores for PyTorch. Record `--num-envs` — it alters batching behavior.
- **Telemetry:** output via HTTP *and* `StatsRecorder`.
- **TensorBoard:** ALWAYS start TensorBoard when starting training. Kill TensorBoard before any `--force` wipe (Windows file handles silently fail the wipe).
- **Shutdown order (strict):** trainer → envs → TensorBoard.
- **Model evaluation:** evaluate self-play policies with ELO ratings, not mean reward.
- **Heuristic bot:** every app has one code-driven heuristic bot, as efficient as possible — used both for training and in the game.
