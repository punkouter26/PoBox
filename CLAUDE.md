# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PoBox is a Unity 6000.5.6f1 (URP, portrait 9:16) active-ragdoll boxing game whose
fighters are driven by ML-Agents policies. Most of the work here is not gameplay
code — it is training brains that can stand and walk, and building the harness
scenes used to evaluate them.

There is no test suite, no linter, and no build script in the repo. The build,
scene generation, and training entry points are all listed below.

Binding rules for agents working here — branch policy, when pushing is allowed,
TensorBoard, fighter colours, physical realism — are in [AGENTS.md](AGENTS.md).
`DOCS/` holds the project's own summary of itself; read it for orientation.

## Commands

### Scenes: authored, not generated

**The three shipping scenes are hand-authored assets.** `SCN_MENU`,
`SCN_TEST_BALANCE_CONTEST` and `SCN_TEST_WALK_CONTEST` are tuned in the Editor
and committed. The tools that used to build them
(`RigTool_MenuScene`, `RigTool_ContestScene`, `RigTool_WalkContestScene`,
1,622 lines) were deleted on 2026-09-08.

That is a deliberate reversal. `RigTool_ContestScene.BuildAll` destroyed the
scene it built -- `Create()` opened a new empty scene, removing the
`Systems_ContestSpawner` an earlier step had added and that was never
reimplemented, so six of its nine steps bailed with "run 7c first" and it logged
"built end to end" anyway. A generator that cannot reproduce the artifact is not
a generator; it is a way to lose one. Edit these scenes by hand and commit them.

**Training scenes are still generated**, because sixteen fighters on a shared
ground box is not something to tune by hand. Those tools kept their
`public static` entry points and LOST their menu items, so a regeneration that
discards hand tuning cannot happen by a misclick:

| Entry point | Builds |
|---|---|
| `SceneTool_BalanceTraining.Create` / `.CreateGrandma` / `.CreateGrandpa` / `.CreateRaptor` | `SCN_TRAIN_BALANCE`, `_GRANDMA`, `_GRANDPA`, `_RAPTOR` |
| `SceneTool_WalkTraining.Create` | `SCN_TRAIN_WALK` |
| `SceneTool_LocomotionTraining.Create` | `SCN_TRAIN_LOCOMOTION` |

Drive one from the command bridge while the Editor is open:

```powershell
echo PoBox.Editor.SceneTool_LocomotionTraining.Create > Temp/agent-command.txt
```

or headlessly with the Editor closed:

```powershell
Unity.exe -batchmode -quit -projectPath . `
          -executeMethod PoBox.Editor.SceneTool_LocomotionTraining.Create
```

**Re-running a scene tool overwrites that scene wholesale**, and the committed
scene being healthy is no evidence that the tool still produces a healthy one.
Regenerating `SCN_TRAIN_LOCOMOTION` on 2026-09-07 produced a scene in which ten
of sixteen fighters threw `NullReferenceException` every physics tick and earned
zero reward, while the trainer reported a plausible mean over the six that still
worked. Run `python Tools/verify_train_scene.py` afterwards; it reads the YAML
directly and needs no Unity.

Fighter prefabs are still generated too, from `PoBox/Fighter/*`.

### Checking the shipping scenes

Two tools, and they catch different things:

| Tool | Finds |
|---|---|
| `PoBox/Scene/Audit Shippable Scenes` (`SceneTool_Audit`) | dangling GUID references, missing tints, camera framing -- static, no play mode |
| `PoBox/Scene/Smoke Test Shippable Scenes` (`SceneTool_SmokeTest`) | runtime exceptions, and what each fighter's brain actually resolved to |

The smoke test plays each of the three scenes for 20 s and writes
`Tools/cleanup/SCENE_SMOKE_REPORT.md`: errors counted by signature, then one row
per fighter with its sensor width beside the width the rig derives. It reports
per fighter on purpose, because an aggregate cannot see ten broken bodies behind
six working ones. It refuses to run if an open scene has unsaved changes, since
it walks scenes with `OpenScene`, which discards them without prompting.

It reports `IContestFighter` implementers separately from `Systems_FighterRig`,
because Nick is refereed through that interface from another assembly and the
rig sweep cannot see him.

### Training

Two ways in, and the headless one is the default now.

**Headless (preferred).** Build a player once, then run as many trainers as the
machine will take. Nothing needs the Editor open, so several generations can run
side by side and the project stays free for builds:

```powershell
Unity.exe -batchmode -quit -nographics -projectPath . -buildTarget Win64 `
          -executeMethod PoBox.Editor.Build_TrainingEnv.Build -buildOutput EnvBuild
.venv\Scripts\mlagents-learn Config\BoxerLocomotion21.yaml `
          --run-id=boxer_locomotion21 --env=EnvBuild\PoBoxTrain.exe `
          --no-graphics --num-envs 6
```

Concurrent runs need **their own `--base-port` and their own env directory**:
Windows holds a running `.exe` open, so a second generation that changes reward
code must build to `EnvBuild2/`, `EnvBuild3/` and so on rather than over the top
of a run in flight. Measured on a 24-core box: 6 envs sustain ~2,500 steps/s per
run and four concurrent runs sit at ~35% CPU, so the limit is RAM (~250 MB per
env player), not cores.

**Attached to the Editor.** `mlagents-learn` with no `--env` waits on port 5004;
start the trainer first, then press Play. If Unity logs `Couldn't connect to
trainer on port 5004 ... Will perform inference instead`, nothing was listening
and the scene just ran its baked brains.

**Start TensorBoard whenever training starts**, and prune dead runs from the
log directory first so the live one is readable. When training in MuJoCo or
Isaac Lab, run those with their UI visible rather than headless — watching how
the creature moves is part of judging the policy. See [AGENTS.md](AGENTS.md).

Run id matches the config name lowercased (`BoxerLocomotion21.yaml` ->
`boxer_locomotion21`). Output lands in `results/` (gitignored), with a `.onnx`
exported at every `checkpoint_interval` -- so a run stopped early still leaves
usable brains behind and does not need a graceful shutdown to be salvaged.

The venv pins are load-bearing -- `protobuf 3.20.3`, `torch 2.2.2`,
`numpy 1.23.5`, `mlagents 1.1.0`, on **Python 3.10**. Any `pip install` that
moves them breaks training; re-pin after. `Tools/requirements-training.txt`
records the whole stack and how to rebuild it, including installing torch from
the CPU index first -- the default index pulls the CUDA build, ten times the
download for no benefit on 3x512 nets whose trainer is bound by environment
throughput rather than matrix multiplies.

### Measuring a brain

Mean reward is not the shipping criterion for either mini-game, and the criteria
that are -- steps between falls, upright fraction, alternation, distance reached
before falling -- reach only TensorBoard, which only records while a trainer is
attached. Three tools close that gap:

| Tool | Answers |
|---|---|
| `python Tools/train_report.py <run-id>` | how a **live run** is doing, per body |
| `pwsh -Command "& ./Tools/eval_candidates.ps1 -Runs @('<run-id>')"` | how a **finished brain** compares to the ones that ship |
| `python Tools/verify_train_scene.py` | whether a regenerated scene has a hole in its fall detector |

`eval_candidates.ps1` stages a run's latest checkpoint under `Assets/Agents`,
rebuilds `EvalBuild/` and runs the matrix; it is safe to run while training
continues. Pass array arguments with `-Command`, never `-File` -- under `-File`
every argument arrives as a plain string, so `-Runs a,b` silently becomes one run
named `a,b` and the script measures only the baselines.

**Every measurement includes the heuristic PD bot.** It is the floor a policy has
to clear, and it is not a soft one: measured 2026-09-07, it out-stands the
shipping balance brain on the capsule by 40%.

**Statistics are written per body** (`Locomotion/Grandma/StepsBetweenFalls`) as
well as in aggregate. Sixteen fighters train one shared brain across three rigs,
and the standing rule is that improving the characters by wrecking the capsule is
not shippable -- which a single mean cannot see. An aggregate that matches the
capsule's column exactly is the signature of the character rigs contributing
nothing at all; see the fall-detector note under **Scene and prefab generation**.

### WebGL build and deploy

```powershell
Unity.exe -batchmode -quit -projectPath . -buildTarget WebGL `
          -executeMethod PoBox.Editor.Build_WebGL.Build -buildOutput WEB
```

Also available as `PoBox/Build/WebGL`. Output is the committed static
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
than restate the flags — `RigTool_PrepareForTraining`, `SceneTool_BalanceTraining`,
`SceneTool_LocomotionTraining`, and `Systems_ContestSpawner.Configure` all do.

Two failure modes to know:

1. **Sensor too small.** ML-Agents logs `More observations (N) made than vector
   observation size (M)` per step and truncates. Loud but easy to drown in.
2. **Model doesn't match sensor.** ML-Agents only compares model shape to
   `BrainParameters` from the *BehaviorParameters inspector*
   (`Editor/BehaviorParametersEditor.cs`); its runtime path checks the model version
   and nothing else. A brain assigned from code — which is every contest brain —
   mismatches in **total silence**. `Systems_BrainCompatibility.Accept` catches
   it and REFUSES the brain rather than merely reporting it, falling back to the
   heuristic PD bot — a worse fighter but an honest one. It is deliberately not
   `[Conditional]`: a player build has to make the same call an Editor run does.
   Both the contest spawner and the offline evaluation harness go through it, so
   the evaluator cannot benchmark a brain the game would refuse.

**Every brain lives under `Assets/Agents/<Name>/`, one folder each, with a
`SOURCE.txt` beside it.** Nick's two and the MuJoCo raptor's used to sit in
`Assets/MuJoCoCreature/Policy/` instead, which put the brain the balance ring
loads in a different tree from the brain the walk race loads.

Brain folder names under `Assets/Agents/` have historically lied about which
generation they contain. Verify with the ONNX input shape before trusting one;
`Locomotion_gen25/SOURCE.txt` is the format for recording provenance, and
`Tools/promote_brain.ps1` writes one from the checkpoint's own filename rather
than from what anyone believed the run had reached.

**The last checkpoint is not the best one.** PPO on this line oscillates
across a plateau rather than settling on it: in run `nick12` the final
`model_4699` walks in 32% of starts where `model_3700`, a thousand iterations
earlier, walks in 83% -- with balance and ring healthy at both, so no
aggregate flags it. Sweep checkpoints against `eval_nick.py` and promote the
one that measures best.

**What ships, as of 2026-09-08:**

| Mini-game | Brain | Why |
|---|---|---|
| Balance ring | `Locomotion_gen25` | 167.9 steps between falls under shove against `gen20`'s 91.7, and ahead on every body |
| Walk race | `Locomotion_gen18_34M` | still the only brain that actually WALKS — alternation 0.601; the faster candidates slide |
| Raptor | `RaptorBalance01` | its own model line, 13-joint rig; the shared 127-observation brain cannot load on it |
| Balance ring AND walk race (Nick) | `nick_balance_002.onnx` | MuJoCo Warp at the contest step, 0.02 s x decimation 1. Both scenes load this one file. nick12/model_3700, 2026-09-09: ring 96%, balance 98%, walk 87% over 10 consecutive 512-world evaluations |
| Nick's own ring / demo | `nick_locomotion.onnx` | MuJoCo Warp at 0.005 s. 99% full-cap at 250 N, walks at 0.980 m/s |

Two mini-games, two brains, and that is the architecture rather than an
accident: `gen25` was trained with the commanded speed pinned at 0 and cannot
walk, `gen18` walks and cannot stand still.

Nick's `nick_locomotion` walks at 0.005 s and scores at the PASSIVE baseline
if run at 0.02, so it stays in his own demo scene. **Both contest scenes load
`nick_balance_002`** -- check the `_onnxModelAsset` guid in either scene
before assuming otherwise.

The line that used to sit here, that `nick_balance_002` "CANNOT WALK (0%
full-cap, 2.22 s median)", was **a measurement taken at the wrong timestep**.
`eval_nick.py` defaults to 0.005 s, and a 0.02 s brain evaluated there fails
exactly the way this project documents in the other direction. Measured at its
own step the old brain walked at 64% and the current one walks at 87%.
Two rules came out of that, and both are enforced in code now:

- **Every evaluation of a Nick brain sets `NICK_TIMESTEP` and
  `NICK_DECIMATION` to the values it was trained at**, which its `SOURCE.txt`
  states and `eval_nick.py` prints on every run.
- `eval_nick.py` perturbs the start pose (`--start-noise`, default on). With
  the exact rest pose, no domain randomisation and a deterministic policy,
  all N worlds are the SAME world and the walk table has an effective sample
  size of one. The old giveaway was the passive baseline reading median =
  mean = p25 = 1.52 s for every brain ever measured.

### The timestep is a body property, not a scene setting

`Time.fixedDeltaTime` is global, so one scene has one step, and a policy only
works at the step its body is stable at. Measured 2026-09-08, both directions:

| scene runs at | Nick | the PhysX cast |
|---|---|---|
| 0.02 s | 1.06 s median — the PASSIVE baseline is 1.08 | 30.0 s |
| 0.005 s | 30.0 s | Standard 30.0 -> **2.9 s** |

Neither survives the other's step, and DecisionPeriod compensation does not
rescue the PhysX brains — the heuristic bot got BETTER at 0.005 s (2.8 -> 4.0),
which is the tell that the physics is fine and the learned policies are simply
out of distribution.

What made a shared ring possible was the BODY. The position servos are kp=400,
so at armature 0.02 `omega*dt` is 2.83 at a 0.02 s step, past the stability
limit of 2 — the actuator chatters and the policy has no authority. Armature
0.2 gives 0.89. It is set on Nick's 30 `MjHingeJoint`s in Unity, NOT the
Raptor's 21 in the same scene, and exported into `nick_unity.xml` so the
trainer and the game read one body.

**In-training metrics cannot see any of this.** `Metrics/fall_rate` read
0.0000 and mean episode length 1000/1000 for a policy whose fresh-start eval
was 0% walk full-cap and a 3.57 s median. Judge a checkpoint with
`Tools/MuJoCo/eval_nick.py`, never the reward curve.

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
- **The cast, and its colours.** A heuristic coded bot, always RED. A reference
  RL fighter on the standard body, always GREEN and untextured. Then zero or
  more custom creatures carrying their own textures and skinned meshes. Colour
  is identity, not decoration: red means "no brain, hand-written", green means
  "the standard policy on the standard body".
- **Earth gravity, realistic joints and masses for the creature's size.** A rig
  that stands only because it is unnaturally heavy or hinged past its anatomy
  is not shippable, whatever its reward curve says.
- No singletons and no `DontDestroyOnLoad` — cross-scene state goes through a
  `ScriptableObject` (`Systems_MiniGameSelection`).
- Opening scene shows a version stamp, top-left, non-pickable.
- Runtime UI is UI Toolkit, portrait 9:16, styled from `Assets/UI/USS_Contest.uss`
  through `PS_Contest` / `TSS_Contest`.
- Observation and action counts are derived from the rig, never hand-typed.

### Naming and assemblies

Type prefixes map to folders under `Assets/Scripts/`: `Agent_`, `Systems_`,
`Reward_`, `Sensor_`. Everything is in namespace `PoBox` (`PoBox.Editor` for
tools), split across two assembly definitions: `PoBox.Runtime` and `PoBox.Editor`.
Nick carries his own pair, `PoBox.MuJoCoCreature` and `.Editor`, because the
shipping game must not have to link the MuJoCo plugin.

Editor code is grouped by what it acts on, and the prefix follows the folder:

| Folder | Prefix | Acts on |
|---|---|---|
| `Editor/Build/` | `Build_` | produces an artifact: a player, a bundle, WebGL |
| `Editor/Rig/` | `RigTool_` | the fighter prefab and rig pipeline |
| `Editor/Scene/` | `SceneTool_` | authors or inspects a scene |
| `Editor/` | `Editor_` | editor infrastructure: the command bridge, project settings |

The scene tools were named `RigTool_*` and built no rigs. `Editor_BuildAndroid`
and `Editor_BuildAndroidAAB` were one class each for two artifacts off one key,
and only the bundle path applied the SDK levels, ARM64 and IL2CPP -- so an APK
built for testing could differ from the bundle that shipped, under a comment
claiming it could not. They are now `Build_Android.Aab` and `.Apk` over one
`Configure`.

**One menu root: `PoBox/`.** It was three (`PoBox/`, `Tools/ML Boxing/`,
`Tools/Web/`) with numbered items whose numbers had holes in them and two
different items numbered 17.

### Packages

The dependency list is deliberately short: a package that no script references
does not stay. UniTask, MessagePipe, VContainer and R3 were removed on
2026-08-30 — they had **zero references** across all runtime and editor scripts,
yet carried embedded patched copies in `Packages/`, a NuGet restore under
`Assets/Packages/`, git-URL manifest entries and three `PoBox.Runtime.asmdef`
references. Addressables, AI Navigation, Animation Rigging, Memory Profiler,
Recorder, Android Logcat, Graphy, In-Game Debug Console, Asset Usage Detector
and NuGetForUnity went the same way.

Two that look unused and are not:

- **`com.unity.pipeline`** is the MCP bridge the Editor is driven through. It
  reads as an unused experimental package and is load-bearing.
- **`com.unity.cloud.gltfast`** imports the `.glb` fighter rigs.

`com.unity.ml-agents` 4.1.0 already includes what used to be
`com.unity.ml-agents.extensions`; adding that package causes GUID conflicts.

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
into `.venv` — that one carries load-bearing ml-agents/torch pins, and the C#/Python
ml-agents versions must stay in exact parity.

### The shipped scene list is explicit

`Build_Android.SHIP_SCENES` names the player's scenes in boot order:

  0. `Assets/Scenes/SCN_MENU.unity`
  1. `Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity`
  2. `Assets/Scenes/SCN_TEST_WALK_CONTEST.unity`

It is a hardcoded list, not whatever is ticked in Build Settings, so a stray
`SCN_TRAIN_*` tick can never bloat the bundle or — depending on order — boot a
tester straight into a training rig. Build Settings happens to agree with it
right now (verified 2026-08-30); the point is that the build does not depend on
that staying true. A scene named here that is missing on disk **aborts** the build.

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
