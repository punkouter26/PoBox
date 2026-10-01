# AGENTS.md

Rules for AI agents working in this Unity project (UNITY_AGENT).

> **ML-Agents exception:** ignore the instructions about using MuJoCo or Isaac Lab if ML-Agents is being used in this app.

## General workflow

- Only use the `master` branch for all work; only use other branches if specifically asked to.
- When doing a git sync, always commit all changes beforehand.
- Check for a `DOCS` folder in the root to get an overall summary of the project.
- At the end of any prompt that has an answer longer than 100 words, add a TLDR 20-word summary.

## Unity / Unreal editor

- Use the following tools to interact with Unity/Unreal as needed, picking whichever provides the best results for the app:
  - Unity CLI pipeline
  - https://github.com/CoplayDev/unity-mcp
  - https://github.com/IvanMurzak/Unity-MCP
- To avoid stalling, set this in Unity via MCP: enable "No Throttling" in editor preferences and "Run In Background" in Player settings, and turn on auto tick so it keeps updating in the background.
- Create as many prefabs/objects in the scene using MCP as possible, so the positions of these static objects can be adjusted easily in the scene rather than having code create them.
- When a change to the UI is made, take an annotated screenshot showing the new and old UI and annotate the changes. Put it in an HTML file.

## Training

- All training should be done with MuJoCo/Newton.
- Ask me for a skinned mesh before attempting to train. Get the rig structure from that model and import it into MuJoCo/Newton for training.
- When training in MuJoCo or Isaac Lab, show the UI of those apps so I can observe how the creature moves during and after training. Use Newton to show training if that is optimal.
- Use https://github.com/joanllobera/mujoco-bin/ to compile for Android phones.
- Always start TensorBoard when training is started so I can view progress.
- When starting training, check that there are no obsolete behaviours on TensorBoard taking up room. If there are, remove them.
- When only doing RL training without changing Unity, close the Unity editor and then open it back up when training is complete.
- When 30+ minutes of MuJoCo RL training is needed, always close the Unreal/Unity editor if it dramatically speeds up training, and let me know when I can open it back up (training is over).
- When training over 30 minutes, take screenshots of the 3 most consequential charts in TensorBoard and explain in simple terms what they describe. Compare the currently running training to previous runs and explain in simple terms if it is doing better or worse and why. Put it in an HTML file and describe the graphs at 3 levels: (1) toddler, (2) child, (3) adult.

## Physics and realism

- Creatures should move realistically, with Earth gravity, realistic joint movement, and mass according to their size.
- Joints should move at a speed and a force that resemble real humans (if the trained agent is the human).
- Make sure all body parts of all creatures accurately collide with each other, and that creatures cannot pass through each other or anything in the environment.
