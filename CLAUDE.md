# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PoBox is a Unity 6000.6.0f1 (URP, portrait 9:16) active-ragdoll boxing game whose
fighters are **MuJoCo bodies driven by policies trained in MuJoCo Warp**. Most
of the work here is not gameplay code — it is training brains that can stand
and walk, and keeping the contest scenes that show them honest.

There is no test suite, no linter, and no build script in the repo. The scene
checks, training entry points and build commands are all listed below.

Binding rules for agents working here are in [AGENTS.md](AGENTS.md): branch
policy and what `git sync` means, **MuJoCo Warp / Newton is the only trainer**,
**ask for the skinned mesh before building a rig**, when to close the Editor
for a long run, TensorBoard, showing the simulator's UI, **authoring scene
objects through MCP instead of from code**, which Unity MCP servers are
available, fighter colours, physical realism including joint speed/force
limits and full collision, and how to write an answer. `DOCS/` holds the
project's own summary of itself; read it for orientation.

**What was removed on 2026-09-14, and must not come back:** the Unity
ML-Agents / PhysX training line (`Agent_FighterBoxing`, the `Reward_*`
components, `com.unity.ml-agents`, the `SCN_TRAIN_*` scenes, `Config/*.yaml`,
the Python eval ladder) and the three PhysX fighters that ran on it (Grandma,
Grandpa, the PhysX Raptor, and their `Locomotion_gen*` / `RaptorBalance01`
brains), together with every Isaac Lab comparison. The cast is now Nick, a
MuJoCo creature, and whatever MuJoCo creatures get trained next.

## User-requested operating rules

- Train new policies in MuJoCo Warp or Newton. Nothing else.
- Keep work on `master` unless the user explicitly requests another branch.
- Read the root `DOCS/` folder first for project context and summary material.
- Start TensorBoard with each training run and remove stale TensorBoard runs that
  are no longer useful.
- Ask for the skinned mesh before training begins, then derive the rig
  structure from that model for MuJoCo/Newton.
- Focus first on the current model and the behaviours it needs, then expand to
  additional creatures or humans later.
- Use the MuJoCo Android build approach from
  https://github.com/joanllobera/mujoco-bin/.
- When training in MuJoCo, keep the simulator UI visible so the motion can be
  observed during and after training; use Newton's viewer if that is the better
  viewing option.
- Keep the motion realistic with Earth gravity, realistic mass, and a joint
  speed and force that resemble a real human's when the agent is a human.
- Create as many prefabs, objects and static scene elements with Unity MCP as
  possible, so the user can adjust their positions in the Inspector instead of
  changing code.
- For MuJoCo RL runs expected to last 30+ minutes, save and close the Unity
  Editor first and tell the user when training is over and it can be reopened.
- Keep collision handling correct across all body parts and prevent
  interpenetration with creatures and the environment.
- When using `git sync`, commit every outstanding change first.
- Use the Unity CLI and the Unity MCP tools as needed to get the best result:
  the CLI command bridge, and whichever of
  https://github.com/AnkleBreaker-Studio/unity-mcp-plugin,
  https://github.com/CoplayDev/unity-mcp or
  https://github.com/IvanMurzak/Unity-MCP suits it best.
- Use non-technical, plain-language answers that explain the practical next
  steps clearly.
- Add a TLDR of about 20 words to any answer longer than about 100 words.

## Commands

### Scenes: authored, not generated

**The three shipping scenes are hand-authored assets.** `SCN_MENU`,
`SCN_TEST_BALANCE_CONTEST` and `SCN_TEST_WALK_CONTEST` are tuned in the Editor
and committed. The tools that used to build them were deleted on 2026-09-08,
deliberately: `RigTool_ContestScene.BuildAll` destroyed the scene it built and
logged "built end to end" anyway. A generator that cannot reproduce the
artifact is not a generator; it is a way to lose one. Edit these scenes by
hand and commit them.

Nick is placed in both contest scenes at author time by
`RigTool_NickMuJoCo` (`PoBox/Nick/...` menu), which also builds his demo and
ring scenes under `Assets/MuJoCoCreature/Scenes/` and exports the MJCF the
trainer reads:

| Entry point | Does |
|---|---|
| `RigTool_NickMuJoCo.BuildDemoScene` | Nick alone on a floor with the demo driver (`Nick_DemoScene`) |
| `RigTool_NickMuJoCo.ExportNickMjcf` | writes the MJCF `MjScene` ACTUALLY generates to `Tools/MuJoCo/nick_unity.xml` |
| `RigTool_NickMuJoCo.AddNickToContest` and the walk variant | places or refreshes Nick in the two contest scenes |

Drive any `PoBox.Editor` static method from the command bridge while the
Editor is open:

```powershell
echo PoBox.Editor.SceneTool_SmokeTest.RunAll > Temp/agent-command.txt
```

or headlessly with the Editor closed:

```powershell
Unity.exe -batchmode -quit -projectPath . `
          -executeMethod PoBox.Editor.SceneTool_Audit.RunBatch
```

Unity allows one process per project, so the headless form exits immediately
while the Editor is open. **Editing a scene file on disk while that scene is
open pops a modal "modified externally" dialog that stalls the Editor's update
loop** — the bridge stops polling until someone presses Reload.

### Checking the shipping scenes

Two tools, and they catch different things:

| Tool | Finds |
|---|---|
| `PoBox/Scene/Audit Shippable Scenes` (`SceneTool_Audit`) | dangling GUID references, missing tints, camera framing -- static, no play mode |
| `PoBox/Scene/Smoke Test Shippable Scenes` (`SceneTool_SmokeTest`) | runtime exceptions, match flow, and every contestant's state after 20 s |

The smoke test plays each of the three scenes for 20 s and writes
`Tools/cleanup/SCENE_SMOKE_REPORT.md`: errors counted by signature, the
referee's round flow, then one row per contestant. It refuses to run if an
open scene has unsaved changes, since it walks scenes with `OpenScene`, which
discards them without prompting.

It reports `IContestFighter` implementers (Nick) separately from any
`Systems_FighterRig`, because the creature is refereed through that interface
from another assembly and a rig sweep cannot see him.

### Training (MuJoCo Warp)

Everything lives in `Tools/MuJoCo/` with its own venv (`Tools/MuJoCo/.venv`,
pins in `requirements.txt`). Read `Tools/MuJoCo/README.md` first.

```powershell
# validate the env only
Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/train_nick.py --smoke
# a real run: TensorBoard started, viewer following the newest checkpoint
Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/train_nick.py --run-name nick13 --num-envs 4096 --max-iterations 3000
# judge a checkpoint -- never the reward curve
Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/eval_nick.py --run nick13 --checkpoint model_3000.pt
# export for Unity, with a SOURCE.txt written from the checkpoint's own name
Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/export_onnx.py --run nick13 --checkpoint model_3000.pt
```

Runs land in `results/nick/<run>/` (gitignored). `train_nick_loop.py`
checkpoints every 50 iterations and relaunches with `--resume-latest`, so a
crash costs at most two minutes; launch long runs through the loop.
`watch_nick.py --follow` opens a viewer on the newest checkpoint, cycling
BALANCE and WALK the way the Unity demo does — the UI AGENTS.md asks for.
`newton_viewer.py` is the Newton alternative.

**The training model is the exported `nick_unity.xml`, never the authored
`creature.xml`.** Training against the authored file is what made every early
attempt collapse in Unity: the trainer and the game have to read one body at
one timestep.

**In-training metrics cannot see whether a policy works.** `Metrics/fall_rate`
read 0.0000 and mean episode length 1000/1000 for a policy whose fresh-start
eval was 0% walk full-cap. Judge a checkpoint with `eval_nick.py`. Two rules
it enforces:

- **Every evaluation sets `NICK_TIMESTEP` and `NICK_DECIMATION` to the values
  the brain was trained at**, which its `SOURCE.txt` states and `eval_nick.py`
  prints on every run. A 0.02 s brain evaluated at 0.005 s fails exactly the
  way a 0.005 s brain fails at 0.02 s, and one such measurement was once
  written into this file as fact.
- `--start-noise` (default on) perturbs the start pose. With the exact rest
  pose, no domain randomisation and a deterministic policy, all N worlds are
  the SAME world and the table has an effective sample size of one. The old
  giveaway was the passive baseline reading median = mean = p25 = 1.52 s for
  every brain ever measured.

**The last checkpoint is not the best one.** PPO on this line oscillates
across a plateau: in run `nick12` the final `model_4699` walks in 32% of
starts where `model_3700`, a thousand iterations earlier, walks in 83%. Sweep
checkpoints against `eval_nick.py` and promote the one that measures best.

### WebGL build and deploy

```powershell
Unity.exe -batchmode -quit -projectPath . -buildTarget WebGL `
          -executeMethod PoBox.Editor.Build_WebGL.Build -buildOutput WEB
```

Also available as `PoBox/Build/WebGL`. Output is the committed static
site in `WEB/`, deployed to Azure Static Web Apps by
`.github/workflows/azure-static-web-apps.yml`. See [WEB/README.md](WEB/README.md).
`.github/workflows/build-web.yml` rebuilds on pushes to `main` touching sources.
The committed `WEB/` player predates the ML-Agents removal; rebuild it before
the next deploy.

## Architecture

### Brains

**Every brain lives under `Assets/Agents/<Name>/`, one folder each, with a
`SOURCE.txt` beside it** that states what it is, its observation/action
contract, the timestep and decimation it was trained at, the run and
checkpoint it came from, and what it measured. `export_onnx.py` writes one
from the checkpoint's own filename rather than from what anyone believed the
run had reached, because brain folder names have historically lied about
which generation they hold. Verify with the ONNX input shape before trusting
one.

| Brain | Body | Step | Role |
|---|---|---|---|
| `Nick_Balance002` (`nick_balance_002.onnx`) | Nick | 0.02 s x decimation 1 | **both contest scenes load this one file.** nick12/model_3700: ring 96%, balance 98%, walk 87% over 10 consecutive 512-world evaluations |
| `Nick_Locomotion` (`nick_locomotion.onnx`) | Nick | 0.005 s x decimation 4 | Nick's own demo scene; scores at the PASSIVE baseline if run at 0.02 s |
| `Creature_Policy` | Nick | 0.005 s | the first MuJoCo Warp balance policy, 2026-08-31; superseded |
| `RaptorMuJoCo_Balance` | MuJoCo raptor (`RaptorRig.prefab`, `RaptorController`) | 0.005 s | balance only, `Raptor_TestScene` / `MuJoCo_TestScene` |

Check the `_onnxModelAsset` guid in a scene before assuming which brain it
loads. A brain is loaded by `CreatureSentisController` through the Inference
Engine (`com.unity.ai.inference`); the observation vector it builds is the
contract, mirrored term for term by `Tools/MuJoCo/nick_env.py`, and
`parity_check.py` proves the two agree. Feeding a different vector of the
same width loads, runs and produces confident nonsense — the engine checks
shape only — so the C# and the Python change in lockstep or not at all.

The vector: 13 root terms, 7 per joint (rotation quaternion + angular
velocity / 20) in canonical order, 8 foot-contact terms, 2 foot heights = 121;
plus the 6-term locomotion command (speed, pelvis-local direction, gait clock
sin/cos) = 127 when `_observeLocomotionCommand` is on. Actions are
zero-centred position targets, `action >= 0 ? action * high : -action * low`.

### The timestep is a body property, not a scene setting

`Time.fixedDeltaTime` is global, so one scene has one step, and a policy only
works at the step its body is stable at. The contest scenes run at 0.02 s.

The position servos are kp=400, so at armature 0.02 `omega*dt` is 2.83 at a
0.02 s step, past the stability limit of 2 — the actuator chatters and the
policy has no authority. Armature 0.2 gives 0.89. It is set on Nick's 30
`MjHingeJoint`s in Unity, NOT the raptor's 21, and exported into
`nick_unity.xml` so the trainer and the game read one body. Any change to
gains, armature or force limits is a BODY change: it invalidates every policy
trained on it (see AGENTS.md on human joint speed and force).

### Contest scenes

- **`SCN_TEST_*_CONTEST`** — presentation harnesses, explicitly "test-scene
  harness only". The contestants stand in the scene at author time;
  `Systems_ContestSpawner.SpawnAndBegin` adopts whichever of them the menu
  picked, stands down the rest, then wakes a sleeping systems root holding the
  referee, drama camera, hazards, announcer, colour commentary and FX, which
  self-discover contestants in their own `Start`. The spawner's roster of
  prefab-spawnable fighters is empty now; the machinery stays so a future
  MuJoCo creature prefab can be fielded from the menu.
- **`SCN_MENU`** — build index 0. Picks a mini-game and roster, stashes them in a
  `Systems_MiniGameSelection` asset, and loads the contest scene, which skips its own
  setup menu when a selection is present.

The referees (`Systems_BalanceContest`, `Systems_WalkContest`, both over
`Systems_ContestReferee`) talk to a contestant through **`IContestFighter`**:
display name, readiness, reset, stand/walk commands, world position, head
height above the floor, and `ReportsDown`. `Systems_NickContestant` implements
it in the `PoBox.MuJoCoCreature` assembly, because the shipping game must not
have to link the MuJoCo plugin from `PoBox.Runtime`. Height is measured
ABOVE THE FLOOR, never raw world Y — the ring canvas sits on a 1 m platform
(`Systems_ContestSpawner.RING_FLOOR_Y`), and an absolute height silently
rescales with altitude.

A contestant is down when it reports so OR when its head drops under 40% of
its standing height. Nick's `ReportsDown` needs both of its tells: the
contest scenes set his controller's `_fallHeight` to -1 so a fallen body stays
fallen rather than snapping back to the rest pose, which also means the
reset counter never moves there, so he additionally reports down when his
pelvis is under `_downPelvisHeight` (0.3 m).

Both contest scenes carry a 3-round `Systems_MatchDirector`, so the referee's
**Restart Rounds Automatically** must be ON; the director logs an error at
startup when it is not, because the alternative was a match that froze after
round one in silence (which both scenes shipped with from 2026-09-08 to
2026-09-14).

**Every spectator system talks to `IContestFighter` and nothing else.** The
drama, race and winner cameras, the announcer's near-fall saves, the match
tally, the device HUD, the fall FX and the hazards were ported off
`Systems_FighterRig` on 2026-09-14, and the interface grew what they need:
`Root`, `PlateColor`, `GroundY`, `PelvisAngularVelocity`, `Shove` and
`SetGravity`. `Systems_Contestants.FindAll` is the one way to enumerate the
field. What could not be ported without a MuJoCo-side equivalent was deleted
rather than left dead: the joint-stress heatmap, stamina, footsteps, impact
sparks, blob shadows, colour commentary, the ground/impact sensors and the
rig itself. BALL RAIN went with them — a PhysX sphere passes through a MuJoCo
body — so the hazards are WIND GUSTS (a pelvis shove) and GRAVITY LEAN
(mirrored into `mjModel.opt.gravity`).

**The booth has two voices.** `Systems_Announcer` is play-by-play and purely
event-driven -- round start, a fall, a hazard, a save. `Systems_ColourCommentary`
is the second voice, a lower third driven by rolling telemetry. Every detector
is a STREAK rather than an instant, streaks accumulate on SCALED time so a
frozen countdown cannot manufacture one, and it yields to the play-by-play via
`Systems_Announcer.CalloutActive`. Every line is also logged as
`COLOUR_COMMENTARY | <tell> | <text>`, because the band is on screen for 4.5 s
and an offline capture cannot reliably sample it.

### Conventions stated in code

These are referred to as "project rules" in comments and are enforced by convention:

- **The cast, and its colours.** A heuristic coded bot, always RED. A reference
  RL fighter on the standard body, always GREEN and untextured. Then zero or
  more custom creatures carrying their own textures and skinned meshes. Colour
  is identity, not decoration.
- **Earth gravity, realistic joints and masses for the creature's size.** A rig
  that stands only because it is unnaturally heavy or hinged past its anatomy
  is not shippable, whatever its reward curve says.
- **Everything collides.** Every limb carries a collision geometry, self-collision
  is on, creature-vs-creature collision is on. Check the generated MJCF and the
  Unity rig both. Two creatures can only collide if they live in the SAME
  simulator: a MuJoCo body and a PhysX body pass straight through each other,
  which is one reason the PhysX cast went.
- No singletons and no `DontDestroyOnLoad` — cross-scene state goes through a
  `ScriptableObject` (`Systems_MiniGameSelection`).
- Opening scene shows a version stamp, top-left, non-pickable.
- Runtime UI is UI Toolkit, portrait 9:16, styled from `Assets/UI/USS_Contest.uss`
  through `PS_Contest` / `TSS_Contest`.

### Naming and assemblies

Type prefixes map to folders under `Assets/Scripts/`: `Systems_`, `Sensor_`.
Everything is in namespace `PoBox` (`PoBox.Editor` for tools), split across
two assembly definitions: `PoBox.Runtime` and `PoBox.Editor`. Nick carries his
own pair, `PoBox.MuJoCoCreature` and `.Editor`, because the shipping game must
not have to link the MuJoCo plugin.

Editor code is grouped by what it acts on, and the prefix follows the folder:

| Folder | Prefix | Acts on |
|---|---|---|
| `Editor/Build/` | `Build_` | produces an artifact: a player, a bundle, WebGL |
| `Editor/Scene/` | `SceneTool_` | authors or inspects a scene |
| `Editor/` | `Editor_` | editor infrastructure: the command bridge, project settings |
| `MuJoCoCreature/Editor/` | `RigTool_` | Nick's MuJoCo rig, scenes and MJCF export |

**One menu root: `PoBox/`.**

### Packages

The dependency list is deliberately short: a package that no script references
does not stay. UniTask, MessagePipe, VContainer, R3, Addressables and friends
were removed on 2026-08-30 with zero references; `com.unity.ml-agents` on
2026-09-14 for the reason at the top of this file.

Three that look unused and are not:

- **`com.unity.pipeline`** is the MCP bridge the Editor is driven through. It
  reads as an unused experimental package and is load-bearing.
- **`com.unity.cloud.gltfast`** imports the `.glb` rigs (`RIGGED_Nick.glb`).
- **`com.unity.ai.inference`** runs every brain (`CreatureSentisController`).

`Packages/org.mujoco` is the MuJoCo Unity plugin, pinned at
`mjVERSION_HEADER = 3012000`; the Android `libmujoco.so` must match it
(see AGENTS.md).

After a manifest change, stale `Library/PackageCache/` folders for the removed
packages keep compiling and fail against their now-missing dependencies. Delete
those folders; the Assets errors will be zero and the PackageCache ones are the
whole list.

---

## Android release & Play internal testing

Ported from the PoRacer pipeline on 2026-08-28; PoSumo is the original, already
shipping on the punkouter27 Play account.

### Identity (permanent — do not change after the first upload)

| Property | Value |
|---|---|
| Application id | `com.punkoutersoftware.pobox` |
| Version / code | `1.0.0` / `1` — bump `VERSION_CODE` in `Build_AndroidSettings` for every upload; Play rejects a reused code |
| min / target SDK | 26 / 36 (Play requires target 36 for new uploads from 2026-08-31) |
| Architecture | ARM64, IL2CPP, Release |
| Orientation | Portrait is locked in `Build_AndroidSettings`. |

### Secrets live OUTSIDE the repo

`C:/Users/punko/Downloads/PoBox-Release/`

- `pobox-upload.jks` — the upload key. **Losing it means losing the ability to
  update the app.** Back it up somewhere other than this machine.
- `pobox-upload.pass` — the store/alias password, one line.
- `upload_certificate.pem` — the public cert, for Play App Signing.
- `play-service-account.json` — NOT created yet; see the SETUP block at the top of
  `Tools/play_publish.py`.

Unity does not serialize keystore passwords into `ProjectSettings`, so both Android
builders read `POBOX_KEYSTORE_PASS` first and fall back to the `.pass` file.
Without either, the build **aborts** rather than producing an unsigned artifact.

### The tools

| Tool | What it does |
|---|---|
| *PoBox → Build → Configure Android Release* (`Build_AndroidSettings`) | One-shot: identity, SDK levels, orientation, and the launcher icons (adaptive + round + legacy, 6 densities) from `Assets/Icons/`. Re-run after changing icon art |
| *PoBox → Build → Android AAB (Play release)* (`Build_Android.Aab`) | Signed bundle → `Builds/Android/PoBox.aab`. Logs `AAB BUILD RESULT:` |
| *PoBox → Build → Android APK* (`Build_Android.Apk`) | Sideloadable APK on the SAME key, so it installs over a Play build → `Builds/Android/PoBox.apk`. Logs `BUILD RESULT:` |
| `Tools/play_publish.py` | Uploads a built AAB. Defaults to the `internal` track as a `draft`; `--dry-run` rehearses and discards |

`Tools/play_publish.py` needs its own venv (`Tools/publish-venv`). Do not install it
into `Tools/MuJoCo/.venv` — that one carries the trainer's pins.

### The shipped scene list is explicit

`Build_Android.SHIP_SCENES` names the player's scenes in boot order:

  0. `Assets/Scenes/SCN_MENU.unity`
  1. `Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity`
  2. `Assets/Scenes/SCN_TEST_WALK_CONTEST.unity`

It is a hardcoded list, not whatever is ticked in Build Settings, so a stray
scene tick can never bloat the bundle or boot a tester into the wrong scene.
A scene named here that is missing on disk **aborts** the build.

### The icons

`Assets/Icons/` holds `AppIcon_Adaptive_Background.png` and
`AppIcon_Adaptive_Foreground.png` (432x432, the API 26+ pair) and
`AppIcon_Legacy.png` (512x512, round and pre-adaptive launchers). The adaptive
FOREGROUND art must stay inside the middle 66% of its canvas — every OEM launcher
masks the outside to a different shape.

The Play STORE icon is a different file, in `StoreAssets/PlayStoreIcon_512.png`:
full-bleed, because Play rounds it itself. Do not swap the two.

### What still needs a human in a browser

1. Play Console → Create app, with the application id above.
2. Store listing, content rating and data-safety forms — drafted in
   `StoreAssets/play-listing.md`.
3. Upload the first bundle by hand; Play refuses an API upload before the app is set up.
4. Create a service account, grant it release permission ON THE APP, and drop its
   JSON key next to the keystore.

After that, `python Tools/play_publish.py --track internal` owns every upload.

### Headless

```
Unity.exe -batchmode -quit -nographics -projectPath <root> -buildTarget Android ^
  -executeMethod PoBox.Editor.Build_Android.Aab -logFile <log>
```

Grep the log for `AAB BUILD RESULT:` — that line is the outcome.
