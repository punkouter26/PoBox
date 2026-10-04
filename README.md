# PoBox

A boxing broadcast for phones. Two physics-driven boxers fight in a ring while the game films it with
cameras that cut on the action, scores it with three judges, and commentates. Nobody animates the boxers:
each is a body with 21 joints and a trained brain that decides, 50 times a second, where every joint goes.

Portrait, one screen, nothing scrolls. Unity 6000.6 (URP, Cinemachine, UI Toolkit), the official MuJoCo
Unity plugin 3.5.0 for the fight's physics, and Unity Inference Engine for the brains. No PhysX in the fight.

## What is in it

- **Six boxers**: Matt, Zombie, Nick, Trump, Grandma and Grandpa, each built from its own rigged mesh,
  with mass and strength to match its size. The grandparents have about half an adult's strength.
- **Three trained skills each**: footwork (stand under shoves, walk, turn), getting up from the canvas,
  and boxing. All trained in MuJoCo Warp on the GPU (`training/`).
- **A bout**: a walk-on, three rounds of 45 seconds, knockdowns with a referee's count, and a knockout or
  a judges' decision. A ladder keeps Elo ratings on the device.
- **A broadcast**: eleven cameras, a scoreboard, balance gauges, a hit map, commentary, a crowd, and a
  ring announcer.

## Where the boxers stand (4 October 2026)

| Boxer | Exam lines passed (of 9) | Open line |
|---|---|---|
| Zombie | 9 | |
| Nick | 9 | |
| Grandma | 9 | |
| Matt | 8 | carrying on after a knockdown: 26 of 30 (needs 27) |
| Trump | 8 | attack: 0.37 punches landed a second (needs 0.5) |
| Grandpa | 8 | footing: 0.33 unprovoked falls a minute (at most 0.2) |

All six pass the Unity gate: the game's boxer behaves as the trainer's did.

## Run it

1. Open the project in Unity 6000.6.0f1.
2. Open `Assets/Scenes/Menu.unity` and press Play. Pick a boxer for each corner, then FIGHT.
3. `Assets/Scenes/Arena.unity` goes straight to the default pair. `Assets/Scenes/Testbed.unity` checks one
   boxer alone with shoves and thrown cubes.

Android: menu `PoBox/Android/Prepare`, then `PoBox/Android/Build APK` (arm64, Android 12 or later).

## Documentation

The current set is in [DOCS/20261004](DOCS/20261004):

| Document | What it covers |
|---|---|
| [architecture_overview.md](DOCS/20261004/architecture_overview.md) | The decision loop, what a boxer senses and controls, scoring, tuning settings |
| [architecture_dashboard.html](DOCS/20261004/architecture_dashboard.html) | Component map, step-by-step sequence diagrams, data model |
| [creatures_dashboard.html](DOCS/20261004/creatures_dashboard.html) | Scorecards per boxer, with an Executive and a Technical view |
| [abilities_and_training.html](DOCS/20261004/abilities_and_training.html) | The abilities grid and three annotated TensorBoard charts |
| [model_summary.md](DOCS/20261004/model_summary.md) | Every brain file and every body |
| [scene_layout.html](DOCS/20261004/scene_layout.html) | The ring and testbed to scale, spawn points, engine setup |
| [creature_benchmarks.html](DOCS/20261004/creature_benchmarks.html) | Learning speed, energy and smoothness, running cost |
| [training_metrics_guide.md](DOCS/20261004/training_metrics_guide.md) | The training charts in plain English |

Also: [DOCS/README.md](DOCS/README.md) is the running project history, [tasks.md](tasks.md) is the live
plan, [rl_optimization_log.md](rl_optimization_log.md) is the training log run by run, and
[AGENTS.md](AGENTS.md) holds the house rules for AI agents.

## Folder map

```
Assets/Scenes        Menu, Arena, Testbed
Assets/Boxers/NAME   one boxer: prefabs, config.json, three ONNX brains, test recordings
Assets/Scripts/Mj    the fight's physics loop on the MuJoCo plugin (MjRing, MjBoxer)
Assets/Scripts/Runtime  bout rules, judges, cameras, commentary, HUD, sound, effects
Assets/Scripts/Editor   importer (MjRetrofit), scene and asset tools, Android build
Assets/Scripts/Tests    play-mode tests, including the training-to-Unity gate
Packages/org.mujoco, Packages/bin.mujoco   the MuJoCo plugin and its libraries (embedded)
training/            the trainer: environments, PPO, exams, body models
DOCS/                documentation and dated reports
```
