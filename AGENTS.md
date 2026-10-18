# AGENTS.md

Rules for AI agents working in this Unity project (UNITY_AGENT).

> **ML-Agents exception:** ignore the MuJoCo/Newton/Isaac Lab rules if the project uses ML-Agents (`mlagents` / `com.unity.ml-agents`).

## 1. Project setup and git workflow

- **Project context:** read the root `DOCS` folder for the project summary before starting.
- **Build version:** show the build version `YYMMDD` (set by the build script from the build date or the git commit date) in the bottom-right corner of every screen.
- **Branching and sync:** work only on `master`; use other branches only when asked. Commit all pending changes before any git sync.
- **Editor tools:** drive Unity/Unreal with whichever gives the best result: the Unity CLI pipeline, https://github.com/CoplayDev/unity-mcp or https://github.com/IvanMurzak/Unity-MCP.
- **Scene construction:** create prefabs and static objects in the scene via MCP or the Unity CLI, not at runtime from code, so their positions can be adjusted in the scene.
- **Performance safeguards:** prevent stalling by enabling **No Throttling** (Editor Preferences), **Run In Background** (Player Settings) and continuous auto-tick via MCP/CLI.
- **UI documentation:** for every UI change, store an HTML report with annotated before-and-after screenshots, the changes marked, in `SCREENSHOTS/`.
- **Blender edits:** before editing a Blender scene through MCP, archive the original `.blend` in `Assets/Art/Raw/` and export a `.glb` copy to `Assets/Art/Exports/GLB/` for future reference (older sources stay in `BLENDER/<date>_<name>/`).

## 2. Training environment and execution

- **Engine:** train in MuJoCo/Newton. Compile for Android phones with https://github.com/joanllobera/mujoco-bin/.
- **Model pipeline:** ask for a skinned mesh before training. Validate it, extract its rig hierarchy and import it into MuJoCo/Newton.
- **Inference runtime:** export trained policies to ONNX and run them in Unity with Sentis (Inference Engine).
- **Simulation UI:** show the MuJoCo/Newton/Isaac Lab UI during and after training so the creature's movement can be observed. Use Newton to show training if that is optimal.
- **Editor during training:** close Unity/Unreal during pure RL runs (no Unity changes) or 30+ minute runs when that speeds training up, and say when it can be reopened (training is over).
- **TensorBoard:** start TensorBoard as soon as training starts.
- **Housekeeping:** automatically prune abandoned run folders, dummy checkpoints and TensorBoard logs under 5,000 steps.
- **Graduation:** export a policy only after it passes its graduation threshold: mean reward at or above target across 100 consecutive episodes, with zero NaN gradients.
- **Long-run reports:** for runs over 30 minutes, make an HTML report with screenshots of the 3 most consequential TensorBoard charts, a comparison to previous runs (better or worse, and why), and each graph explained at 3 levels: (1) toddler, (2) child, (3) adult.
- **Telemetry:** stream agent actions, win/loss stats and kinematics to local files or Azure Tables to analyse movement profiles and game results over time and to help debug.

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
