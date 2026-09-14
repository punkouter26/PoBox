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

### `git sync` commits everything first

**A `git sync` is: commit every outstanding change, then push.** Never push
with a dirty working tree, and never leave modified files behind for the next
session to rediscover. If something in the tree should *not* be committed, say
so and ask — do not quietly push around it.

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

## User-requested operating rules

- Use MuJoCo or Newton for all new training, and keep the legacy ML-Agents/
  PhysX line working only as a measured fallback.
- Use only the `master` branch for work unless the user explicitly asks for
  another branch.
- Start TensorBoard whenever training starts, and prune obsolete runs from the
  log directory before launching a new one.
- Ask for a skinned mesh before training begins, and derive the rig structure
  from that model for MuJoCo/Newton training.
- Focus on the current creature or human model and teach it the behaviours it
  needs before broadening the cast.
- Use the MuJoCo Android build method from
  https://github.com/joanllobera/mujoco-bin/ when preparing Android phone builds.
- For MuJoCo or Isaac Lab runs, show the simulator UI so the motion can be
  observed during and after training; use Newton’s viewer if that is the better
  option.
- Keep motion realistic: Earth gravity, sensible joint ranges, realistic mass,
  and a joint speed and force that resemble a real human's when the agent is a
  human.
- Create as many prefabs, objects and static scene elements with Unity MCP as
  possible, so the user can adjust their positions in the Inspector instead of
  editing code.
- For MuJoCo RL runs expected to take 30+ minutes, save and close the Unity
  Editor first, then tell the user when training is over and the Editor can be
  reopened.
- Keep all body parts colliding correctly and prevent creatures from passing
  through each other or the environment.
- When using `git sync`, commit every outstanding change first.
- Keep answers plain-language and non-technical so the user can decide the next
  step quickly.
- Use the Unity CLI and the available Unity MCP tools as needed to get the best
  result for each task: the CLI command bridge, and whichever of
  https://github.com/AnkleBreaker-Studio/unity-mcp-plugin,
  https://github.com/CoplayDev/unity-mcp or
  https://github.com/IvanMurzak/Unity-MCP suits it best.
- Add a brief TLDR of about 20 words at the end of any answer longer than about
  100 words.

## Training: MuJoCo / Newton only

**All new training happens in MuJoCo or Newton.** Not Unity ML-Agents / PhysX.
The `SCN_TRAIN_*` scenes, the `Config/Boxer*.yaml` runs and the
`Locomotion_gen*` brains described in [CLAUDE.md](CLAUDE.md) are the **legacy
PhysX line**: keep them working, keep measuring against them, but do not start
a new generation there. New policies are trained against a MuJoCo/Newton body,
the way Nick is (`Tools/MuJoCo/`, `nick_unity.xml`).

The reason is the one already documented for Nick: the trainer and the game
have to read **one body at one timestep**. An MJCF exported from the same rig
the game loads gives that; a PhysX ragdoll retuned by hand does not.

### A rig starts from a skinned mesh the user supplies

**Ask for the skinned mesh before training anything.** Do not invent a body, do
not reuse the capsule as a stand-in, and do not start a run while waiting for
the asset.

Given the model, the pipeline is: read the **rig structure out of that model**
(bone hierarchy, bone lengths, bind pose), convert it into the MuJoCo/Newton
body definition, and train against that. The mesh is the source of truth for
proportions; the MJCF is generated from it, never hand-guessed to match.

**One creature at a time.** More creature and human models are coming, but
until they arrive the job is to teach the **current** model every behaviour it
needs — stand, balance under shove, walk, turn — rather than to broaden the
cast.

### Close the Unity Editor for long runs

**Any MuJoCo RL run expected to take 30 minutes or more: tell the user to save
and close the Unity Editor first, and tell them explicitly when training is
over and they can reopen it.** A long run and an open Editor compete for the
same cores and the same MuJoCo plugin. Do not start such a run and leave the
user guessing whether the project is theirs again.

## Training runs

These apply to every trainer — MuJoCo, Newton, Isaac Lab, and ML-Agents for
as long as the legacy line is still being measured.

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
  assumed. **Use Newton to show the training if that is the better viewer**
  — the requirement is that the motion is watchable, not which app draws
  it. (Unity ML-Agents training scenes remain headless by rule — they carry
  no cameras — and TensorBoard is the window into those.)

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

### Joints move at human speed and human force

If the agent is a human, its joints move at a **speed and torque a real human
joint produces** — angular velocity limits and actuator force ceilings taken
from human ranges, not whatever number makes the reward curve climb. A policy
that only balances because its hips can snap at 30 rad/s, or because its ankle
can apply a torque a person could not, is not shippable. Scale the same way for
non-human creatures: force and speed follow the anatomy and the mass.

Note the existing tension documented in [CLAUDE.md](CLAUDE.md): the position
servos are kp=400 and stability at a 0.02 s step was bought with armature 0.2.
Any change to gains, armature or force limits is a **body** change — it
invalidates the policies trained on it, and it has to stay inside the realism
budget above rather than escape it.

### Everything collides, and nothing passes through anything

**All body parts of every creature must collide correctly** — with each other,
with other creatures, and with the environment. No creature may pass through
another creature, through the ground, through the ring, or through any static
object.

That means, concretely:

- Every limb carries a collision geometry, not just the ones that happen to
  matter for the current reward.
- Self-collision is **on** unless disabling a specific pair is justified and
  written down. A body that clips its own thigh through its own torso is
  learning from physics the player will not see.
- Creature-vs-creature collision is on. Two fighters that interpenetrate are
  not boxing.
- Check this on the **generated MJCF and the Unity rig both**, since contact
  exclusion is easy to inherit silently from an exporter default.

## Build the scene with MCP, not with code

**Prefer creating GameObjects, prefabs and scene content through the Unity MCP
bridge over writing a script that spawns them at runtime.** Anything static —
ground, ring, platforms, hazards, lights, cameras, props, spawn markers —
should exist as a real object in a real scene that the user can select and drag,
not as coordinates buried in C#.

The reason is direct: the user adjusts positions by hand. A static object placed
by code can only be moved by editing and recompiling code; the same object
placed via MCP is moved in the Inspector in two seconds. Reach for code only for
things that genuinely cannot be authored — the N-fighter training grids, and the
contest spawner's roster-driven instantiation, both of which are already
documented as deliberate in [CLAUDE.md](CLAUDE.md).

This is also the standing reversal recorded in CLAUDE.md: the three shipping
scenes are **hand-authored assets**, because a generator that cannot reproduce
its artifact is a way to lose one.

## Unity tooling

Drive the Editor with whichever of these gives the best result for the task —
they are all fair game, and more than one may be used in a session:

- **Unity CLI / the command bridge** — `Temp/agent-command.txt` and
  `-executeMethod`, documented in [CLAUDE.md](CLAUDE.md). Best for headless
  builds and for invoking a named static method.
- **https://github.com/AnkleBreaker-Studio/unity-mcp-plugin**
- **https://github.com/CoplayDev/unity-mcp**
- **https://github.com/IvanMurzak/Unity-MCP**

The three MCP servers overlap; pick per task rather than per habit, and say
which one was used when it matters to reproducing the result. `com.unity.pipeline`
in the manifest is the existing MCP bridge and is load-bearing — do not remove
it as unreferenced.

## Android builds

**MuJoCo does not currently run on Android in this project, and the obvious fix
does not work.** Measured on a Pixel 9 Pro, 2026-09-09:

- Without a native library the player throws `DllNotFoundException: Unable to
  load DLL 'mujoco'` once per contest scene and then
  `NullReferenceException: Failed to create Mujoco runtime` at
  `MjScene.StepScene` **every physics tick** -- 2,133 of them in one 20 s
  round. Nick does not simulate. He also cannot fall, so the referee declares
  him the winner of every round. The Editor smoke test cannot see any of this,
  because the Editor has `Assets/Plugins/mujoco.dll` and the phone does not.
- **https://github.com/joanllobera/mujoco-bin/** ships an arm64-v8a
  `libmujoco.so` and it loads (`nativeloader: ... ok`, zero DllNotFound), but
  the binary is **MuJoCo 3.3.7** -- not the 3.5.0 its README claims nor the
  3.3.0 in its package.json, both of which were read out of the binary itself.
  This project's `Packages/org.mujoco` bindings are pinned at
  `mjVERSION_HEADER = 3012000`. The app segfaults about a second after the
  library loads: `signal 11 (SIGSEGV), fault addr 0x0 (write)` on the Unity
  main thread, inside managed code. Nine minor versions of `mjModel`/`mjData`
  layout drift is not survivable by struct marshalling.

**RESOLVED 2026-09-09 by building MuJoCo 3.12.0 for Android from source.**
`Assets/Plugins/Android/libmujoco.so` is now arm64-v8a at exactly the version
the bindings pin, so the trainer and the game stay on one engine version and
one MJCF -- the invariant the whole Nick line depends on. It needs two
one-line portability patches (bionic declares `aligned_alloc` only from API 28,
and does not define `_POSIX_C_SOURCE`, so MuJoCo's `localtime_r` guard hits a
hard `#error`). Build recipe, patch and measurements:
[Tools/MuJoCo/android/README.md](Tools/MuJoCo/android/README.md).

On device the three failure signatures went to zero and Nick simulates. **The
cost is frame rate: 60 fps drops to about 20 during a contest**, which is the
open item.

Do not install the 3.3.7 `.so` instead. It turns a working app into a crash on
launch, which is strictly worse than Nick standing still.

**Standing instruction: compile MuJoCo for phones the
https://github.com/joanllobera/mujoco-bin/ way** — that repo is the reference
for the Android/arm64 toolchain and NDK setup, and
[Tools/MuJoCo/android/README.md](Tools/MuJoCo/android/README.md) is this
project's recipe built on it. Take the **method** from mujoco-bin; take the
**version** from `Packages/org.mujoco` (`mjVERSION_HEADER`). Never ship a
prebuilt `.so` whose version does not match the bindings, whatever its README
claims — read the version out of the binary.

## Answering

**Write for a non-technical reader.** Plain language, no jargon where a normal
word works, and say what it means for the game rather than only what the code
does. The point of an answer here is that the user can decide the next step from
it without decoding it first. Keep the precise numbers — they are the evidence —
just explain what they imply.

Any answer longer than ~100 words ends with a **TLDR of about 20 words**.
