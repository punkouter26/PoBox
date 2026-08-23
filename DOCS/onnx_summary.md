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
| **Balance ring ships** | `Locomotion_gen20` — 113 steps between falls, +66% on the brain it replaced |
| **Walk race ships** | `Locomotion_gen18_34M` — Alternation 0.766, a deliberately kept mid-run checkpoint |
| **Blocker** | Stability. Every walking brain topples about every 1.7 s |
| **Silent-failure risk** | Observation width. A brain assigned from code whose `obs_0` disagrees with the fighter mismatches **without any error** |

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
