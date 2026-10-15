# AGENTS.md

Rules for AI agents working in this Unity project (UNITY_AGENT).

> **ML-Agents exception:** ignore the MuJoCo/Newton/Isaac Lab rules if the project uses ML-Agents (`mlagents` / `com.unity.ml-agents`).

## 1. Project setup and git workflow

- **Project context:** read the root `DOCS` folder for the project summary before starting.
- **Branching and sync:** work only on `master`; use other branches only when asked. Commit all pending changes before any git sync.
- **Editor tools:** drive Unity/Unreal with whichever gives the best result: the Unity CLI pipeline, https://github.com/CoplayDev/unity-mcp or https://github.com/IvanMurzak/Unity-MCP.
- **Scene construction:** create prefabs and static objects in the scene via MCP or the Unity CLI, not at runtime from code, so their positions can be adjusted in the scene.
- **Performance safeguards:** prevent stalling by enabling **No Throttling** (Editor Preferences), **Run In Background** (Player Settings) and continuous auto-tick via MCP/CLI.
- **UI documentation:** for every UI change, store an HTML report with annotated before-and-after screenshots, the changes marked, in `SCREENSHOTS/`.
- **Blender edits:** before editing a Blender scene through MCP, save the original `.blend` and export it as `.glb` into `BLENDER/<date>_<name>/` for future reference.

## 2. Training environment and execution

- **Engine:** train in MuJoCo/Newton. Compile for Android phones with https://github.com/joanllobera/mujoco-bin/.
- **Model pipeline:** ask for a skinned mesh before training. Extract its rig structure and import it into MuJoCo/Newton.
- **Simulation UI:** show the MuJoCo/Newton/Isaac Lab UI during and after training so the creature's movement can be observed. Use Newton to show training if that is optimal.
- **Editor during training:** always close Unity/Unreal during pure RL runs (no Unity changes) and reopen it when training is complete. For 30+ minute runs, close it if that dramatically speeds training up, and say when it can be reopened (training is over).
- **TensorBoard:** start TensorBoard as soon as training starts. Remove obsolete runs that are taking up room first.
- **Long-run reports:** for runs over 30 minutes, make an HTML report with screenshots of the 3 most consequential TensorBoard charts, a comparison to previous runs (better or worse, and why), and each graph explained at 3 levels: (1) toddler, (2) child, (3) adult.
- **Data logging:** log agent actions to local files or Azure Tables to analyse movement profiles, win/loss metrics and game results over time.

## 3. Physics, biomechanics and self-collision

- **Physical realism:** Earth gravity, mass according to size, realistic joint movement. Human joints move at human speed and force.
- **Muscle rules:** every new creature trains from the start with a muscle-like speed limit (joint torque falls to nothing at the joint's speed limit) and fatigue (drives weaken as energy is spent and recover with rest). The game runs it under the same rules.
- **World collision:** all body parts collide accurately; creatures cannot pass through each other or anything in the environment.
- **Primitive colliders:** self-collision uses capsules, boxes and spheres fitted inside the skinned mesh. Never use the visual mesh or the bones as colliders.
- **Collision pairs:** all body-part pairs collide except parent–child pairs and pairs that overlap in the default standing pose; joint limits handle those.
- **Validation:** before training, verify no pair touches in the T-pose, the default stance and a normal arm and leg swing.
- **Self-contact:** never ends an episode. If the policy leans on it, add a small self-contact force penalty.
- **Warm start:** train a new skill from a warm start (a brain trained without self-collision, or the previous rung), not from scratch.

## 4. Response formatting

- **TL;DR:** end any response longer than 100 words with a 20-word TL;DR.
