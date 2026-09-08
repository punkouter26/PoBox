# AGENTS.md

Rules for coding agents working in this repository. These are binding.
See [CLAUDE.md](CLAUDE.md) for how the project itself is built and run.

## Git: master only

**This repository uses exactly one branch: `master`. Commit directly to it.**

- Do **not** create feature, topic, or work branches.
- Do **not** open pull requests against this repo.
- If any other branch exists, it is a leftover — delete it, locally and on the
  remote, once its work is on `master`.
- Work on another branch **only when explicitly asked to**.

### Never push unprompted

**Do not push to the remote unless the user asks for it, or types `git sync`.**

Committing to local `master` is expected and needs no permission. Pushing is a
separate, outward-facing act and needs its own. `git sync` is the standing
phrase that authorises it; absent that phrase or an explicit request, commits
stay local no matter how finished they look.

This overrides the usual "branch before committing to the default branch"
default. It is a solo repository; a branch here only adds a merge step and a
chance for work to sit unmerged.

### Merging a leftover branch safely

Checking out `master` reverts the working tree to master's contents, which can
leave files on disk that then block the merge as "untracked working tree files
would be overwritten". Move the branch pointer instead of moving the tree, or
verify the blocking file is byte-identical to the committed one before removing
it. Never delete an untracked file to unblock a merge without checking it is
already in the commit you are merging.


## Orientation: read DOCS/ first

`DOCS/` in the repo root holds the project's own summary of itself —
`index.html`, `scenes.html`, `creatures_dashboard.html`,
`training_motivation.html`, `onnx_summary.md` and the diagrams. Read it before
starting work to get the overall picture, rather than reconstructing it from
the source every time.

## Training runs

These apply to every trainer — ML-Agents, MuJoCo, Isaac Lab.

- **Always start TensorBoard when training starts**, so progress is visible
  without being asked for it.
- **Prune obsolete behaviours from TensorBoard first.** Before a run begins,
  check what is already in the log directory and remove dead runs that are
  only taking up room. A picker crowded with abandoned generations makes the
  live run harder to read, which defeats the point of starting it.
- **When training in MuJoCo or Isaac Lab, show that application's UI.** These
  runs are not to be launched headless by default: the user watches how the
  creature moves during and after training, and that observation is part of
  how a policy gets judged. Headless is an optimisation to be asked for, not
  assumed. (Unity ML-Agents training scenes remain headless by rule — they
  carry no cameras — and TensorBoard is the window into those.)

## Fighter conventions

Every RL app in this line has the same cast:

- **A heuristic coded bot — always RED.** Code-driven, never loads a brain.
  It is the floor a policy has to clear.
- **A reference RL fighter — always GREEN, untextured.** The standard body,
  before any per-creature variation.
- **Zero or more custom creatures**, which carry custom textures and custom
  skinned meshes supplied by the user.

Colour is identity here, not decoration: red means "no brain, hand-written",
green means "the standard policy on the standard body". Do not tint a fighter
outside that scheme without being asked.

## Physical realism

Creatures move under **Earth gravity**, with joint ranges and masses that are
realistic **for the size of the creature being modelled**. A rig that stands
only because it is unnaturally heavy, unnaturally light, or hinged past what
the anatomy allows is not shippable, however good its reward curve looks.

## Android builds

Compile for Android phones using **https://github.com/joanllobera/mujoco-bin/**.

## Answering

Any answer longer than ~100 words ends with a **TLDR of about 20 words**.
