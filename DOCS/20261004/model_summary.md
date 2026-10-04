# PoBox: agent and rig inventory

As of 4 October 2026. Every number is from the exam taken in plain MuJoCo 3.5.0 with the brain that is in
the game (`training/logs/exam_*.json`). Which training run each file came from was confirmed by comparing
the files byte for byte.

---

## Tier 1: Quick look

- **Six boxers, three brain files each, 18 files in all.** Every file is 1.7 MB.
- **Three boxers pass the whole exam** (nine lines): Zombie, Nick, Grandma. They are ready for the game.
- **Three are one line short** and are in the game for testing: Matt (carrying on after a knockdown),
  Trump (attack), Grandpa (footing).
- **All six pass the Unity gate**: the game's boxer behaves as the trainer's did (18 checks of 18).
- A seventh boxer, Lil Matt, was taken out of the game on 3 October. His training files remain.

---

## Tier 2: Core mechanics

### Agent model table

Task types: **Footwork** = stand under shoves, walk at the speed asked, turn to face. **Get-up** = rise
from the canvas unaided. **Boxing** = attack, guard, chin, footing, and carrying on after a get-up.

| Agent | Brain model file | File size | Task type | Final success rate | Deployment status |
|---|---|---|---|---|---|
| Matt | `Assets/Boxers/Matt/policy.onnx` | 1.7 MB | Footwork | stand 100%, walk off by 0.04 m/s, turn 100% | Ready for game |
| Matt | `Assets/Boxers/Matt/getup.onnx` | 1.7 MB | Get-up | 30 of 30, in 1.7 s | Ready for game |
| Matt | `Assets/Boxers/Matt/match.onnx` | 1.7 MB | Boxing | 4 of 5 lines; carrying on 26 of 30 (needs 27) | Testing |
| Zombie | `Assets/Boxers/Zombie/policy.onnx` | 1.7 MB | Footwork | stand 100%, walk off by 0.01 m/s, turn 93% | Ready for game |
| Zombie | `Assets/Boxers/Zombie/getup.onnx` | 1.7 MB | Get-up | 30 of 30, in 1.8 s | Ready for game |
| Zombie | `Assets/Boxers/Zombie/match.onnx` | 1.7 MB | Boxing | 5 of 5 lines; 1.30 punches a second | Ready for game |
| Nick | `Assets/Boxers/Nick/policy.onnx` | 1.7 MB | Footwork | stand 100%, walk off by 0.01 m/s, turn 100% | Ready for game |
| Nick | `Assets/Boxers/Nick/getup.onnx` | 1.7 MB | Get-up | 30 of 30, in 1.6 s | Ready for game |
| Nick | `Assets/Boxers/Nick/match.onnx` | 1.7 MB | Boxing | 5 of 5 lines; 1.38 punches a second | Ready for game |
| Trump | `Assets/Boxers/Trump/policy.onnx` | 1.7 MB | Footwork | stand 100%, walk off by 0.02 m/s, turn 100% | Ready for game |
| Trump | `Assets/Boxers/Trump/getup.onnx` | 1.7 MB | Get-up | 30 of 30, in 1.7 s | Ready for game |
| Trump | `Assets/Boxers/Trump/match.onnx` | 1.7 MB | Boxing | 4 of 5 lines; attack 0.37 a second (needs 0.5) | Testing |
| Grandma | `Assets/Boxers/Grandma/policy.onnx` | 1.7 MB | Footwork | stand 98%, walk off by 0.01 m/s, turn 99% | Ready for game |
| Grandma | `Assets/Boxers/Grandma/getup.onnx` | 1.7 MB | Get-up | 30 of 30, in 2.4 s | Ready for game |
| Grandma | `Assets/Boxers/Grandma/match.onnx` | 1.7 MB | Boxing | 5 of 5 lines; 0.82 punches a second | Ready for game |
| Grandpa | `Assets/Boxers/Grandpa/policy.onnx` | 1.7 MB | Footwork | stand 97%, walk off by 0.02 m/s, turn 97% | Ready for game |
| Grandpa | `Assets/Boxers/Grandpa/getup.onnx` | 1.7 MB | Get-up | 30 of 30, in 2.2 s | Ready for game |
| Grandpa | `Assets/Boxers/Grandpa/match.onnx` | 1.7 MB | Boxing | 4 of 5 lines; footing 0.33 falls a minute (needs 0.2) | Testing |

The five boxing lines are footing, attack, guard, chin and carrying on. `Assets/Boxers/zero_policy.onnx`
is a do-nothing brain that holds the guard pose; it is used by the parity tests.

### Physics and rig table

| Character | Physics setup | Movement type | Moving parts | Main objective |
|---|---|---|---|---|
| Matt | MuJoCo plugin: `MjBody` tree, `MjHingeJoint`, position `MjActuator` | Joint position targets, torque-limited | 12 body parts, 21 joints | All-rounder: land punches, stay up |
| Zombie | same | same | 12 body parts, 21 joints | All-rounder |
| Nick | same | same | 12 body parts, 21 joints | Swarmer: always closing, many light fast punches |
| Trump | same, with the speed limit and fatigue on | same | 12 body parts, 21 joints | Brawler: comes forward, big slow head shots |
| Grandma | same, about half strength, three-quarter speed | same | 12 body parts, 21 joints | Counter-puncher: block, then quick body shots |
| Grandpa | same, about half strength, three-quarter speed | same | 12 body parts, 21 joints | Out-boxer: stays at the end of his reach, left hand |

No Unity Rigidbody, ArticulationBody, Configurable Joint or Collider is used. There is no root motion and
no animation clip: walking, punching and getting up all come from the 21 joint targets.

---

## Tier 3: Setup guide

### Bodies

| Character | Mass (kg, gloves on) | Height (m) | Strength factor | Speed factor | Source mesh | Trainer's model |
|---|---|---|---|---|---|---|
| Matt | 85.1 | 1.86 | 1.05 | 1.0 | `Assets/Models/Matt.glb` | `training/models/v2/matt_solo.xml` |
| Zombie | 91.6 | 1.82 | 1.01 | 1.0 | `Assets/Models/Zombie.glb` | `training/models/v2/zombie_solo.xml` |
| Nick | 58.6 | 1.65 | 0.83 | 1.0 | `Assets/Models/Nick.glb` | `training/models/v2/nick_solo.xml` |
| Trump | 109.4 | 1.90 | 1.10 | 1.0 | `Assets/Models/Trump.glb` | `training/models/v2/trump_solo.xml` |
| Grandma | 87.8 | 1.70 | 0.53 | 0.75 | `Assets/Models/Grandma.glb` | `training/models/v2/grandma_solo.xml` |
| Grandpa | 79.8 | 1.62 | 0.48 | 0.75 | `Assets/Models/Grandpa.glb` | `training/models/v2/grandpa_solo.xml` |

Each body is 12 rigid parts (pelvis, torso with the head, two upper arms, two forearms, two thighs, two
shins, two feet) with 17 simple collision shapes fitted inside the mesh. All pairs of parts collide except
the 11 pairs joined by a joint.

### Where each brain came from

| Character | Footwork run | Get-up run | Boxing run |
|---|---|---|---|
| Matt | `r2b_matt` | `u1_matt_zombie` | `m4_matt` |
| Zombie | `f_zombie` | `u1_matt_zombie` | `m1_zombie` |
| Nick | `f_nick` | `u1_nick_lilmatt` | `m2_nick` |
| Trump | `f_trump2` | `u1_trump2_matt` | `m6_trump` |
| Grandma | `f2_grandma` | `u1_trump_grandma` | `m5_grandma` |
| Grandpa | `f_grandpa` | `u1_grandpa_matt` | `m3_grandpa` |

Checkpoints are in `training/checkpoints/<run>` and charts in `training/logs/tb/<run>` (neither is in git).

### Brain file facts

| Item | Value |
|---|---|
| Format | ONNX, opset 17, batch of 1 |
| Inputs | 103 numbers |
| Outputs | `actions` (21 joint targets) and `value` (the brain's confidence, used by the win-probability bar) |
| Shape | two networks (actor and critic), each 512, 256 and 128 units |
| Runs on | Unity Inference Engine 2.6.1, CPU, 50 calls a second per boxer |
| Inspector slots | `MjBoxer.policy` (footwork), `MjBoxer.matchPolicy`, `MjBoxer.getUpPolicy` |

### To replace a brain

1. Train and examine the run (see `architecture_overview.md`, Tier 3).
2. In the editor: `MjRetrofit.Promote("matt", "r2b_matt")` for footwork, or `MjRetrofit.PromoteBoxing`
   for boxing and get-up. This copies the ONNX and the reference recordings into `Assets/Boxers/Matt`.
3. Run `MjGateTests`. All three checks must pass for that boxer before it is played.

### Open items

- Matt: carrying on 26 of 30. Over 119 longer trials he stays up 89% of the time.
- Trump: attack. He lands on Zombie (1.33 a second) and Nick (0.42) and almost nothing on the other three.
  Also open: his hands show past his gloves, and his menu portrait's crop.
- Grandpa: footing, one unprovoked fall in three minutes.
- All but Trump: retraining under the speed limit and fatigue (`tasks.md` D8). The first attempt, on
  4 October, kept nothing.
- Android: the build exists; no measurement on a phone is logged (`tasks.md` D6).
