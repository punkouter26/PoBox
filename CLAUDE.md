# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PoBox is a Unity 6000.5.6f1 (URP, portrait 9:16) active-ragdoll boxing game whose
fighters are driven by ML-Agents policies. Most of the work here is not gameplay
code — it is training brains that can stand and walk, and building the harness
scenes used to evaluate them.

There is no test suite, no linter, and no build script in the repo. The build,
scene generation, and training entry points are all listed below.

## Commands

### Scene and prefab generation (Unity Editor menu)

Scenes and fighter prefabs are **generated artifacts**, not hand-authored. They are
rebuilt from `Tools/ML Boxing/*` menu items backed by `Assets/Scripts/Editor/RigTool_*.cs`.
The numbering is the intended order:

| Menu item | Builds |
|---|---|
| `0. Apply Project Settings` | Locks `Time.fixedDeltaTime = 0.02`, gravity, 16 solver iterations |
| `1. Generate Capsule Biped` / `2. Auto-Rig Selected` / `3. Prepare for Training` | Fighter prefab pipeline |
| `4. Create Menu Scene` | `SCN_MENU` (build index 0) |
| `5/5b/5c. Create … Balance Scene` | `SCN_TRAIN_BALANCE`, `_GRANDMA`, `_GRANDPA` |
| `6. Create Walk Training Scene`, `9. Create Locomotion Training Scene` | `SCN_TRAIN_WALK`, `SCN_TRAIN_LOCOMOTION` |
| `7. Build Balance Contest Scene` | `SCN_TEST_BALANCE_CONTEST`, end to end |
| `8. Create Walk Contest Scene` | `SCN_TEST_WALK_CONTEST` |

The balance contest used to be eight menu items (`7`, `7b`, `7d`..`7i`, with `7c`
already lost) that had to be clicked in order — each one a migration bolted onto
the last. They are now one idempotent `BuildAll`. The individual steps are still
`public static` on `RigTool_ContestScene`, so any one of them can be driven from
the CLI or over MCP without a menu:

```powershell
Unity.exe -batchmode -quit -projectPath . `
          -executeMethod PoBox.Editor.RigTool_ContestScene.BuildAll
```

**Re-running a scene tool overwrites that scene wholesale**, including asset
references you edited by hand. Before running one, check the tool's constants —
e.g. `RigTool_WalkContestScene.LOCOMOTION_BRAIN_PATH` decides which brain the whole
roster gets. A stale constant silently downgrades a scene you just wired up.

### Training

`mlagents-learn` attaches to the **running Editor** on port 5004 — there is no
headless env build in the loop. Start the trainer first, then press Play:

```powershell
.venv\Scripts\activate
mlagents-learn Config\BoxerLocomotion20.yaml --run-id=boxer_locomotion20
```

Run id matches the config name lowercased (`BoxerLocomotion20.yaml` →
`boxer_locomotion20`). Output lands in `results/` (gitignored). If Unity logs
`Couldn't connect to trainer on port 5004 … Will perform inference instead`, no
trainer was listening and the scene just ran the baked brains.

The venv pins are load-bearing — `protobuf 3.20.3`, `torch 2.2.2`, `numpy 1.23.5`,
`mlagents 1.1.0`. Any `pip install` that moves them breaks training; re-pin after.

### WebGL build and deploy

```powershell
Unity.exe -batchmode -quit -projectPath . -buildTarget WebGL `
          -executeMethod PoBox.Editor.Build_WebGL.Build -buildOutput WEB
```

Also available as `Tools/Web/Build WebGL to WEB/`. Output is the committed static
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
than restate the flags — `RigTool_PrepareForTraining`, `RigTool_BalanceScene`,
`RigTool_LocomotionScene`, and `Systems_ContestSpawner.Configure` all do.

Two failure modes to know:

1. **Sensor too small.** ML-Agents logs `More observations (N) made than vector
   observation size (M)` per step and truncates. Loud but easy to drown in.
2. **Model doesn't match sensor.** ML-Agents only compares model shape to
   `BrainParameters` from the *BehaviorParameters inspector*
   (`Editor/BehaviorParametersEditor.cs`); its runtime path checks the model version
   and nothing else. A brain assigned from code — which is every contest brain —
   mismatches in **total silence**. `Systems_ContestSpawner.WarnOnObservationMismatch`
   exists to catch this and is Editor/dev-build only.

Brain folder names under `Assets/Agents/` have historically lied about which
generation they contain. Verify with the ONNX input shape before trusting one;
`Locomotion_gen20/SOURCE.txt` is the format for recording provenance.

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
- No singletons and no `DontDestroyOnLoad` — cross-scene state goes through a
  `ScriptableObject` (`Systems_MiniGameSelection`).
- Opening scene shows a version stamp, top-left, non-pickable.
- Runtime UI is UI Toolkit, portrait 9:16, styled from `Assets/UI/USS_Contest.uss`
  through `PS_Contest` / `TSS_Contest`.
- Observation and action counts are derived from the rig, never hand-typed.

### Naming and assemblies

Type prefixes map to folders under `Assets/Scripts/`: `Agent_`, `Systems_`,
`Reward_`, `Sensor_`, and `RigTool_`/`Build_` for editor-only code. Everything is in
namespace `PoBox` (`PoBox.Editor` for tools), split across two assembly definitions:
`PoBox.Runtime` and `PoBox.Editor`.

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
| Version / code | `1.0.0` / `1` — bump `VERSION_CODE` in `Editor_ConfigureAndroidRelease` for every upload; Play rejects a reused code |
| min / target SDK | 26 / 36 (Play requires target 36 for new uploads from 2026-08-31) |
| Architecture | ARM64, IL2CPP, Release |
| Orientation | Portrait is locked in `Editor_ConfigureAndroidRelease`. |

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
| *PoBox → Configure Android Release Settings* | One-shot: identity, SDK levels, orientation, and the launcher icons (adaptive + round + legacy, 6 densities) from `Assets/Icons/`. Re-run after changing icon art |
| *PoBox → Build Android AAB (Play release)* | Signed bundle → `Builds/Android/PoBox.aab`. Logs `AAB BUILD RESULT:` |
| *PoBox → Build Android APK* | Sideloadable APK on the SAME key, so it installs over a Play build → `Builds/Android/PoBox.apk`. Logs `BUILD RESULT:` |
| `Tools/play_publish.py` | Uploads a built AAB. Defaults to the `internal` track as a `draft`; `--dry-run` rehearses and discards |

`Tools/play_publish.py` needs its own venv (`Tools/publish-venv`). Do not install it
into `.venv` — that one carries load-bearing ml-agents/torch pins, and the C#/Python
ml-agents versions must stay in exact parity.

### The shipped scene list is explicit

`Editor_BuildAndroidAAB.SHIP_SCENES` names the player's scenes in boot order:

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
  -executeMethod PoBox.Editor.Editor_BuildAndroidAAB.Build -logFile <log>
```

Grep the log for `AAB BUILD RESULT:` — that line is the outcome.
