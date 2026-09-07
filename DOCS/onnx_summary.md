# ONNX & Rig Model Inventory

Generated from the repository on 2026-08-23. Model facts are parsed from the ONNX
graphs themselves (`onnx` 1.15.0), telemetry from TensorBoard event files under
`results/`, physics from the live Unity Editor.

---

## Tier 1 — Executive summary (30 seconds)

Seventeen policies on disk, two in production. Every one is the same architecture:
a 3 × 512 fully-connected network, ~606k parameters, 30 continuous outputs. What
differs between them is **the width of the input** and **what behaviour they learned**.

| | |
|---|---|
| **Balance ring ships** | `Locomotion_gen25` — 167.9 steps between falls under shove, +83% on `gen20`, and ahead on every body |
| **Walk race ships** | `Locomotion_gen18_34M` — Alternation 0.766, a deliberately kept mid-run checkpoint |
| **Blocker** | ~~Stability~~ — that was three faults in the instrumentation; see the 2026-09-07 addendum. The open blocker is **gait**: every walk brain slides instead of stepping |
| **Silent-failure risk** | Observation width. A brain assigned from code whose `obs_0` disagrees with the fighter mismatches **without any error** |

---

## Addendum — 2026-09-07: the blocker was instrumentation, not stability

Everything above this section was written on 2026-08-23 and its **Blocker** line
reads *"Stability. Every walking brain topples about every 1.7 s."* That reading
survived twenty generations of reward tuning. It was measuring three faults in
the instrumentation underneath the policies.

### What was actually wrong

**Ground contact was a tally that drifted.** `Sensor_GroundContact` reported
grounded while a running total of `OnCollisionEnter` minus `OnCollisionExit` was
positive. On the imported character rigs that total desynchronises and never
recovers, because `ResetContacts()` zeroes it while the foot is still resting on
the floor and no new `Enter` is ever generated for a pair that never separated.
Measured on fighters standing still:

```
Capsule/Fighter_00  L[on 'FootL',     grounded=True,  minY=-0.0024]
Grandma/Fighter_13  L[on 'LeftFoot',  grounded=False, minY=-0.0043]
                    R[on 'RightFoot', grounded=False, minY=-0.0083]
```

Grandma's soles are millimetres *below* the floor plane while both sensors
report airborne. `singleSupport`, the clearance gate, the whole support term,
fall detection, and eight of the agent's own observations are computed from
those flags — so ten of sixteen fighters trained against a corrupted objective
*and* read a corrupted vector. The per-body ordering the project read as a
difficulty gradient tracks sensor health, not rig difficulty:

| body | feet grounded (heuristic bot) | steps between falls |
|---|---|---|
| Capsule | 0.97 / 0.98 | 104 |
| Grandpa | 0.61 / 1.00 | 66 |
| Grandma | 0.41 / 0.41 | 38 |

**The scene tool silently disabled ten fighters.** Regenerating
`SCN_TRAIN_LOCOMOTION` — the documented way to maintain it — produced null fall
contacts on the character rigs, because the lower-leg sensors were looked up by
joint indices 3 and 9, positions in the *capsule's* joint list. The reward
dereferences them every `FixedUpdate`, so those fighters threw
`NullReferenceException` 50 times a second and earned zero reward while the
trainer reported a healthy mean over the six that still worked. The committed
scene did not have the defect, so nothing had ever caught it.

**Nothing measured a finished brain.** Every metric reached only TensorBoard,
which only records while a trainer is attached, so promotions were argued from
one run's training curve against another generation's. `Systems_EvalHarness` now
runs a baked `.onnx` through the training scene with no trainer and reports the
same numbers, per body, at a chosen commanded speed and with or without shoves.

### What the corrected measurements say

Like-for-like, same scene, same three rigs, same reward code.

| commanded speed 0 | steps between falls | upright | notes |
|---|---:|---:|---|
| heuristic PD bot | 91.6 | 0.776 | stands on two feet (`singleSupport` 0.013) |
| `Locomotion_gen20` | 110.0 | 0.803 | stands on **one** foot, 0.159 m up |
| heuristic, +shove | 82.4 | 0.760 | −10% under perturbation |
| `Locomotion_gen20`, +shove | 92.3 | 0.777 | −17% under perturbation |

Two things fall out of that table.

**The hand-written bot out-stands the shipping balance brain on the capsule**,
112.1 against 80.2, at a higher upright fraction. A PD controller beating twenty
generations of policy is not a statement about RL; it is a statement about what
the reward asked for. At `gaitBlend` 0 the support term and the clearance term
both saturate whether the fighter is planted or holding one foot in the air, so
a speed-0 balance run optimises neither — and the alternation term added in
gen 15 to stop exactly this is gated by `gaitBlend`, so it is switched off at
the one commanded speed a balance brain trains at.

**Gen 20 gives back nearly twice what the bot does when shoved**, which is the
signature of a policy fitted to still air. `SCN_TRAIN_LOCOMOTION` disables the
shovers; `SCN_TEST_BALANCE_CONTEST` runs hazards and shoves.

### The walk race, quantified

`Systems_WalkContest.MIN_WIN_DISTANCE` is 0.75 m. Measured at commanded speed 1,
furthest reached in one unbroken upright run:

| | best run distance | alternation |
|---|---:|---:|
| heuristic PD bot | 0.863 m | 0.102 |
| `Locomotion_gen18_34M` (ships) | **0.767 m** | 0.579 |

The shipping walk brain averages two centimetres over the pass mark. That is the
whole explanation for "11 of 13 rounds were no contest" — the field sits exactly
on the line, so roughly half of all rounds fall short of it.

### What shipped

**`Locomotion_gen25` replaces `Locomotion_gen20` in the balance ring.** Trained
from scratch, 21.85M steps, final mean reward 0.945. Measured under shove --
the condition the ring presents, which no previous generation was ever measured
in -- 4 episodes per fighter:

| steps between falls | heuristic | `gen20` | **`gen25`** |
|---|---:|---:|---:|
| ALL | 82.2 | 91.7 | **167.9** |
| Capsule | 94.8 | 70.1 | **167.9** |
| Grandma | 64.8 | 106.5 | **172.4** |
| Grandpa | 84.5 | 103.0 | **163.3** |
| upright fraction | 0.760 | 0.776 | **0.870** |
| falls per episode | 27.9 | 25.9 | **15.1** |

It wins on every body, so it does not make the trade the shipping rule forbids.
Undisturbed it effectively stops falling: during training, steps between falls
saturated the 3000-step episode on the capsule (2889) and Grandpa (2754).

**The walk race keeps `Locomotion_gen18_34M`**, and this is a deliberate refusal
rather than an absence of candidates. At commanded speed 1:

| | best run distance | alternation | steps between falls |
|---|---:|---:|---:|
| `gen18_34M` (ships) | 0.781 m | **0.601** | 57.4 |
| `gen29` | **0.912 m** | 0.018 | 95.7 |
| `gen30` | 0.869 m | 0.037 | 100.1 |

`gen29` travels 17% further than the brain that ships and clears
`MIN_WIN_DISTANCE` comfortably, which would turn "no contest" rounds back into
races. It gets there by **sliding**: alternation 0.018 against gen 18's 0.601,
clearance 0.066 against 0.411. This project's standard, set by gens 13 to 15, is
that distance bought by not stepping does not count -- and in an active-ragdoll
game the gait is the thing the player watches. Shipping it would buy a rules
outcome with a visible regression.

### The open lead on gait

Four walk runs this session all slid rather than stepped, and the suspect is the
entropy bonus. Gen 21's horizon fix bundled `beta` 0.01 -> 0.005, borrowed from
ML-Agents' Walker example. That was right for balance -- standing still is
low-entropy and gen 25 is the proof -- and may be exactly wrong for gait.
`Agent_FighterBoxing`'s gait-clock comment already recorded the diagnosis in
2026-08-19: single support 0.005 and foot lift 0.09 mm "at every reward
weighting tried across three generations -- an exploration problem, not an
incentive one".

Gen 30 raised it to 0.02 and, at matched lesson and fewer steps, showed more
alternation than gen 29 (0.054 against 0.011 at ~8M) -- weakly positive, and
then it stalled, for a reason worth recording on its own.

**The curriculum gates were carried across a reward change and stopped meaning
what they meant.** 0.55 / 0.60 / 0.45 / 0.40 / 0.38 were calibrated against
gen 18's reward function. This session changed three terms in it, so the same
NUMBER now marks a different level of competence -- and gen 30 sat at 0.337
against the Shuffle gate's 0.45 and crept, so it would never have reached the
commanded speeds where sliding stops working. The same mistake bit gen 27's
shove ladder, which was gated on a reward the shoving itself depressed. **Gate a
curriculum on `progress` whenever the reward function has moved.**

`gen31` re-ran the ladder on progress gates over a 25M budget. It walked the
rungs on schedule and the gait terms climbed monotonically as the commanded pace
rose -- which is the first time in this session that they moved at all:

| rung | commanded | alternation | clearance |
|---|---:|---:|---:|
| Shuffle | 0.32 | 0.015 | 0.023 |
| Step | 0.49 | 0.037 | 0.070 |
| Stride | 0.64 | 0.044 | 0.100 |
| Walk | 0.68 | 0.049 | **0.118** |

Still an order of magnitude short of gen 18's 0.766 alternation and 0.411
clearance, so it is not a walk brain. But the direction is right and the run was
stopped by the clock, not by a plateau. **The next run is `gen31`'s config with
a budget past 30M**, and its checkpoints under `results/boxer_locomotion31/` are
a legitimate `--initialize-from` starting point.

### Contest code measured height against the wrong zero

Both contests, the announcer, the drama camera, the fall-impact FX and the race
camera all decided "has this fighter collapsed" with a fraction of a **raw world
Y**. The ring canvas sits at `RING_FLOOR_Y` = 1 m, so 45% of a 2.6 m head height
is 1.17 m — 17 cm above the canvas. A fighter counted as standing until its head
was practically on the floor, and the balance contest's round-ranking score gave
a fighter lying flat 0.385 per second instead of ~0. All are now measured above
`rig.GroundY`, which is probed per fighter and correct at any altitude.

---

---

## Tier 2 — What the inventory means

### The observation-width contract

`obs_0` width is the compatibility key. It is computed, never hand-typed:

```
13 (root) + 7 × 14 (joints) + 8 (foot)          = 119
                              + 2 (foot height) = 121
                              + 6 (locomotion)  = 127
```

Three serialized bool flags on `Agent_FighterBoxing` move it, and each change
invalidates every previously trained `.onnx`. The three widths present on disk are
exactly the three the formula produces.

ML-Agents only compares model shape to `BrainParameters` from the **inspector**.
Its runtime path checks the model version and nothing else — so a brain assigned
from code, which is every contest brain, mismatches in total silence. The spawner
therefore refuses a mismatched brain and falls back to the heuristic PD bot rather
than shipping a policy reading a shifted vector.

### Promotion states

| State | Meaning |
|---|---|
| **Production** | Referenced by a shipping scene's roster |
| **Staging** | Trained, banked, benchmarked, not currently wired to a scene |
| **Archive** | Superseded; kept because its `SOURCE.txt` records a measurement that shaped a later generation |
| **Obsolete** | Incompatible observation contract — cannot load against current rigs |

---

## Tier 3 — Complete specifications

### Matrix A — Models

All models: `producer = pytorch`, `opset = 9`, single input `obs_0`, outputs
`continuous_actions`, `deterministic_continuous_actions`, `continuous_action_output_shape`,
`version_number`, `memory_size`. Graph ops: `Gemm ×4`, `Sigmoid ×3`, `Clip ×3`,
`Div ×3`, `Mul ×5`, `Add ×2`, `Sub`, `Concat`, `Exp`, `RandomNormalLike`, `Identity ×3`.

| Prefab / consumer | ONNX path | KB | `obs_0` | `continuous_actions` | Params | Run ID | Final mean reward | Status |
|---|---|---:|---:|---:|---:|---|---:|---|
| Contest roster — balance | `Assets/Agents/Locomotion_gen20/Locomotion_gen20.onnx` | 2375 | `[batch,127]` | `[batch,30]` | 606,525 | `boxer_locomotion20` | 0.643 | **Production** |
| Contest roster — walk | `Assets/Agents/Locomotion_gen18_34M/Locomotion_gen18_34M.onnx` | 2375 | `[batch,127]` | `[batch,30]` | 606,525 | `boxer_locomotion18` @34M | 0.337 (42M final) | **Production** |
| — | `Assets/Agents/Locomotion_gen18/Locomotion_gen18.onnx` | 2375 | `[batch,127]` | `[batch,30]` | 606,525 | `boxer_locomotion18` | 0.337 | Staging |
| — | `Assets/Agents/Locomotion_gen17/Locomotion_gen17.onnx` | 2375 | `[batch,127]` | `[batch,30]` | 606,525 | `boxer_locomotion17` | 0.574 | Staging |
| — | `Assets/Agents/Locomotion_gen16/Locomotion_gen16.onnx` | 2375 | `[batch,127]` | `[batch,30]` | 606,525 | `boxer_locomotion16` | 0.187 | Staging |
| — | `Assets/Agents/Locomotion_gen15/Locomotion_gen15.onnx` | 2375 | `[batch,127]` | `[batch,30]` | 606,525 | `boxer_locomotion15` | 0.350 | Staging |
| — | `Assets/Agents/Locomotion_gen13/Locomotion_gen13.onnx` | 2375 | `[batch,127]` | `[batch,30]` | 606,525 | `boxer_locomotion13` | 0.613 | Archive |
| — | `Assets/Agents/Locomotion_gen12/Locomotion_gen12.onnx` | 2375 | `[batch,127]` | `[batch,30]` | 606,525 | `boxer_locomotion12` | 0.356 | Archive |
| — | `Assets/Agents/Locomotion_gen9/Locomotion_gen9.onnx` | 2375 | `[batch,127]` | `[batch,30]` | 606,525 | `boxer_locomotion09` | 0.086 | Archive |
| — | `Assets/Agents/Locomotion_gen8/Boxer.onnx` | 2375 | `[batch,127]` | `[batch,30]` | 606,525 | `boxer_locomotion08` | 0.053 | Archive |
| — | `Assets/Agents/Locomotion_gen7/Boxer.onnx` | 2375 | `[batch,127]` | `[batch,30]` | 606,525 | `boxer_locomotion07` (rescued) | 0.075 | Archive |
| — | `Assets/Agents/Locomotion_v03/Boxer.onnx` | 2375 | `[batch,127]` | `[batch,30]` | 606,525 | gen 5 — folder name lies | — | Archive |
| — | `Assets/Agents/Locomotion_v02/Boxer.onnx` | 2375 | `[batch,127]` | `[batch,30]` | 606,525 | — | — | Archive |
| `Fighter_Capsule` | `Assets/Agents/Standard/Boxer.onnx` | 2359 | `[batch,119]` | `[batch,30]` | 602,413 | balance era | — | Archive |
| `Fighter_Grandma` | `Assets/Agents/Grandma/Boxer.onnx` | 2359 | `[batch,119]` | `[batch,30]` | 602,413 | `GrandmaBalance04` | — | Archive |
| `Fighter_Grandpa` | `Assets/Agents/Grandpa/Boxer.onnx` | 2359 | `[batch,119]` | `[batch,30]` | 602,413 | `GrandpaBalance04` | — | Archive |
| — | `Assets/Agents/_obsolete_125obs/Locomotion_v01/Boxer.onnx` | 2371 | `[batch,125]` | `[batch,30]` | 605,497 | — | — | **Obsolete** |

**Parameter counts explained.** 606,525 = 127×512 + 512 + (512×512 + 512)×2 + 512×60 + 60,
plus the normalizer's running mean/variance buffers. The 119-obs models are 602,413 —
the whole difference is the narrower first layer (8 × 512 = 4,096 weights, plus 16
normalizer entries).

### Matrix B — Physics & actuators

One rig shared by all fighters: `Systems_FighterRig` driving 14 `ConfigurableJoint`s,
15 rigidbodies, **75.0 kg** total. There are **no `ArticulationBody` components in this
project** — every joint is a `ConfigurableJoint` in **Slerp** rotation-drive mode, which
is why the drive is specified once as a `slerpDrive` rather than per-axis.

Actions map to joint targets as `a ∈ [−1,1] → Lerp(low, high)` per enabled axis, then
through a 0.10 s low-pass, then to `ConfigurableJoint.targetRotation`.

| Joint | Body | Component | Drive mode | Mass kg | Pitch° | Roll° | Yaw° | DOF | Kp (spring) | Kd (damper) | Force max | Behavioural purpose |
|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| `Torso` | Capsule / Grandma / Grandpa | ConfigurableJoint | Slerp | 24.26 | −30..30 | −20..20 | −35..35 | 3 | 3500 | 250 | 5000 | Carries the upright term; heaviest segment |
| `Head` | ″ | ConfigurableJoint | Slerp | 5.15 | −40..40 | −30..30 | −45..45 | 3 | 1200 | 90 | 2000 | Height is the collapse terminal (40% of standing) |
| `ThighL` | ″ | ConfigurableJoint | Slerp | 8.46 | −110..20 | −20..45 | −30..30 | 3 | 4500 | 350 | 7000 | Hip flexion — the prime mover of a step |
| `ShinL` | ″ | ConfigurableJoint | Slerp | 3.68 | 0..130 | — | — | 1 | 4000 | 300 | 6000 | Knee; single axis, swing clearance |
| `FootL` | ″ | ConfigurableJoint | Slerp | 1.10 | −40..25 | −20..20 | — | 2 | 2500 | 180 | 3500 | Ankle. **65° pitch range is what enabled the gen 10 toe-pivot exploit** |
| `UpperArmL` | ″ | ConfigurableJoint | Slerp | 2.21 | −60..120 | −30..90 | −60..60 | 3 | 2000 | 150 | 3000 | Counterweight; boxing guard later |
| `ForearmL` | ″ | ConfigurableJoint | Slerp | 1.47 | 0..140 | — | — | 1 | 1800 | 120 | 2500 | Elbow |
| `GloveL` | ″ | ConfigurableJoint | Slerp | 0.74 | −60..60 | — | −25..25 | 2 | 1000 | 80 | 1500 | Fall sensor mount |
| `ThighR` | ″ | ConfigurableJoint | Slerp | 8.46 | −110..20 | −45..20 | −30..30 | 3 | 4500 | 350 | 7000 | Mirror of ThighL (roll range mirrored) |
| `ShinR` | ″ | ConfigurableJoint | Slerp | 3.68 | 0..130 | — | — | 1 | 4000 | 300 | 6000 | Mirror |
| `FootR` | ″ | ConfigurableJoint | Slerp | 1.10 | −40..25 | −20..20 | — | 2 | 2500 | 180 | 3500 | Mirror |
| `UpperArmR` | ″ | ConfigurableJoint | Slerp | 2.21 | −60..120 | −90..30 | −60..60 | 3 | 2000 | 150 | 3000 | Mirror |
| `ForearmR` | ″ | ConfigurableJoint | Slerp | 1.47 | 0..140 | — | — | 1 | 1800 | 120 | 2500 | Mirror |
| `GloveR` | ″ | ConfigurableJoint | Slerp | 0.74 | −60..60 | — | −25..25 | 2 | 1000 | 80 | 1500 | Mirror |
| | | | | **75.00** | | | | **30** | | | | |

**Drive authority is not the limiting factor.** Hip 7,000 N·m and knee 6,000 N·m
against a 75 kg / 736 N body is roughly two orders of magnitude of headroom. This was
measured and eliminated as a hypothesis at gen 11, alongside sensor integrity and
action range — the joint limits above are anatomically generous and a foot lift sits
well inside them.

**Runtime differs from these base values.** `Systems_StrengthCurriculum` scales spring
and force during training; the values above are the authored baseline in
`Fighter_Capsule.prefab`.

### Sensors

| Sensor | Count | Attached to | Role |
|---|---:|---|---|
| `Sensor_GroundContact` | 8 | torso, head, shin L/R, glove L/R, foot L/R | `_contactCount > 0` against static colliders. Feet feed the gait terms; the other six are the fall terminal |

`ResetContacts()` exists because `OnCollisionExit` only fires on the next physics step —
after a teleport-reset the stale contacts would otherwise persist for a frame.

---

## Provenance

Every banked model carries a `SOURCE.txt` recording the run, the checkpoint step, the
measurement that justified keeping it, and its known failure mode. Folder names have
historically lied about which generation they contain (`Locomotion_v03` is really
gen 5) — **verify against the ONNX input shape and the SOURCE file, not the folder name.**
