# ONNX & Rig Model Inventory

> Generated 2026-08-19 • Source: `Assets/Agents/`, `results/` • ONNX inspected with `onnx` 1.22.0

This document is the source of truth for every brain currently shipping in PoBox, the runs that produced them, and the rigs they were trained to control. It comes in three tiers:

- **Tier 1 (Very Basic):** "Which brains exist, which fighters use them, and which are healthy?"
- **Tier 2 (Basic):** Per-fighter tensor shapes, run lineage, and rig topology.
- **Tier 3 (Complete):** Full ONNX file matrix (Matrix A) and full Physics/Actuator matrix (Matrix B), including training telemetry snapshots and DOF breakdowns.

---

## Tier 1 — 30-second summary

| Fighter | Brain status | Source run | Healthy? |
|---|---|---|---|
| **Capsule (Standard)** | Production | `boxer_balance04` (lineage) | Yes — promoted `Assets/Agents/Standard/Boxer.onnx` |
| **Grandma** | Production | `grandma_balance04` (lineage) | Stalled at gen 2 — gen 3 (product form) queued |
| **Grandpa** | Production | `grandpa_balance04` (lineage) | Stalled at gen 2 — gen 3 (product form) queued |

Three independent model lines exist because each rig has different bone axes; a brain trained on one rig cannot drive another. All current production brains are sized for **119 observations / 30 actions**. The newest checkpoint in `results/boxer_balance04/` is sized **121 / 30** (foot-height rays on) — that is the layout balance05 will train against.

---

## Tier 2 — Per-fighter overview

### Standard / Capsule

| Property | Value |
|---|---|
| Prefab | `Assets/Prefabs/Fighters/Fighter_Capsule.prefab` |
| Brain | `Assets/Agents/Standard/Boxer.onnx` (2.31 MB) |
| Run lineage | `boxer_balance04` → product-form → `boxer_balance05` (in progress) |
| Obs shape | `batch × 119` (root + per-joint proprio + feet; foot-height rays off) |
| Action shape | `batch × 30` continuous in [−1, 1] |
| Rig topology | 14 driven `ConfigurableJoint`s, 14 `ArticulationBody` mirrors |
| Behavior key | `Boxer` |

### Grandma

| Property | Value |
|---|---|
| Prefab | `Assets/Prefabs/Fighters/Fighter_Grandma.prefab` |
| Brain | `Assets/Agents/Grandma/Boxer.onnx` (2.31 MB) |
| Run lineage | `grandma_balance03` → `grandma_balance04` (stalled at −0.98 mean reward) |
| Obs shape | `batch × 119` |
| Action shape | `batch × 30` continuous |
| Rig topology | 14 driven `ConfigurableJoint`s, imported skeleton bone axes |
| Behavior key | `Boxer` |
| Note | Heuristic `Kp/Kd` signs calibrated negative for this rig's axes |

### Grandpa

| Property | Value |
|---|---|
| Prefab | `Assets/Prefabs/Fighters/Fighter_Grandpa.prefab` |
| Brain | `Assets/Agents/Grandpa/Boxer.onnx` (2.31 MB) |
| Run lineage | `grandpa_balance03` → `grandpa_balance04` (stalled) |
| Obs shape | `batch × 119` |
| Action shape | `batch × 30` continuous |
| Rig topology | 14 driven `ConfigurableJoint`s, imported skeleton bone axes |
| Behavior key | `Boxer` |
| Note | Bone axes differ from Grandma — heuristic `Kp/Kd` may need per-rig retune |

---

## Tier 3 — Complete matrices

### Matrix A — Models

The active parameter count below is what `onnx` reports from `graph.initializer`; for PPO continuous policies this is normally the union of all weight tensors for the body MLP plus the mean/sigma heads.

| # | Prefab name | ONNX path | Size (MB) | Init params | Input tensor | Output tensor(s) | Run ID | Final mean reward | Promotion status |
|---|---|---|---|---|---|---|---|---|---|
| 1 | Fighter_Standard | `Assets/Agents/Standard/Boxer.onnx` | 2.31 | 14 | `obs_0 : batch × 119` | `version_number : 1`, `memory_size : 1`, `continuous_actions : batch × 30`, `continuous_action_output_shape : 1`, `deterministic_continuous_actions : batch × 30` | `boxer_balance04` (lineage) | unknown (older run) | **Production** (Staging candidate for v5) |
| 2 | Fighter_Grandma | `Assets/Agents/Grandma/Boxer.onnx` | 2.31 | 14 | `obs_0 : batch × 119` | same as above | `grandma_balance04` (lineage) | ~ −0.98 (gen 2, stalled) | **Production** (Staging candidate for product-form v3) |
| 3 | Fighter_Grandpa | `Assets/Agents/Grandpa/Boxer.onnx` | 2.31 | 14 | `obs_0 : batch × 119` | same as above | `grandpa_balance04` (lineage) | ~ −0.98 (gen 2, stalled) | **Production** (Staging candidate for product-form v3) |
| 4 | (latest checkpoint) | `results/boxer_balance04/Boxer.onnx` | 2.31 | 14 | `obs_0 : batch × 121` | same | `boxer_balance04` | unknown (latest, ≥ 23 M steps) | **Staging** |
| 5 | (checkpoint) | `results/boxer_balance04/Boxer/Boxer-25000104.onnx` | 2.31 | 14 | `obs_0 : batch × 121` | same | `boxer_balance04` | unknown | Staging |
| 6 | (checkpoint) | `results/boxer_balance04/Boxer/Boxer-24999848.onnx` | 2.31 | 14 | `obs_0 : batch × 121` | same | `boxer_balance04` | unknown | Staging |
| 7 | (checkpoint) | `results/boxer_balance04/Boxer/Boxer-24499906.onnx` | 2.31 | 14 | `obs_0 : batch × 121` | same | `boxer_balance04` | unknown | Staging |
| 8 | (checkpoint) | `results/boxer_balance04/Boxer/Boxer-23999748.onnx` | 2.31 | 14 | `obs_0 : batch × 121` | same | `boxer_balance04` | unknown | Staging |
| 9 | (checkpoint) | `results/boxer_balance04/Boxer/Boxer-23499925.onnx` | 2.31 | 14 | `obs_0 : batch × 121` | same | `boxer_balance04` | unknown | Staging |
| 10 | (checkpoint) | `results/grandma_balance04/Boxer/Boxer-1999991.onnx` | 2.31 | 14 | unknown (gen-3 layout) | same | `grandma_balance04` | ~ −0.98 | Staging (stalled) |
| 11 | (checkpoint) | `results/grandma_balance04/Boxer/Boxer-1999963.onnx` | 2.31 | 14 | unknown | same | `grandma_balance04` | unknown | Staging |
| 12 | (checkpoint) | `results/grandma_balance04/Boxer/Boxer-1499954.onnx` | 2.31 | 14 | unknown | same | `grandma_balance04` | unknown | Staging |
| 13 | (checkpoint) | `results/grandma_balance04/Boxer/Boxer-1499933.onnx` | 2.31 | 14 | unknown | same | `grandma_balance04` | unknown | Staging |
| 14 | (checkpoint) | `results/grandma_balance04/Boxer/Boxer-999965.onnx` | 2.31 | 14 | unknown | same | `grandma_balance04` | unknown | Staging |
| 15 | (checkpoint) | `results/grandma_balance04/Boxer/Boxer-999956.onnx` | 2.31 | 14 | unknown | same | `grandma_balance04` | unknown | Staging |
| 16 | (checkpoint) | `results/grandma_balance04/Boxer/Boxer-499981.onnx` | 2.31 | 14 | unknown | same | `grandma_balance04` | unknown | Staging |
| 17 | (checkpoint) | `results/grandma_balance04/Boxer/Boxer-499959.onnx` | 2.31 | 14 | unknown | same | `grandma_balance04` | unknown | Staging |

> All files are valid ONNX protobuf (magic bytes `08` at offset 0, indicating an `onnx.ModelProto` field-1 byte). The promoted brains in `Assets/Agents/` use the **119-obs** layout (foot-height rays disabled); the `results/boxer_balance04/` series uses the **121-obs** layout (foot-height rays enabled). The locomotion brain (when trained) will use a **125-obs** layout (121 + 4 commanded-speed observations).

#### Training telemetry snapshots

These are taken from the per-run `training_status.json` and `Player-N.log` files in each run's `run_logs/` directory.

| Run ID | Steps observed | Status |
|---|---|---|
| `boxer_balance04` | ≥ 25,000,000 | Stalled under summed reward (gen 2) → product-form (`boxer_balance05`) queued |
| `grandma_balance04` | 1,850,000 | Pinned at mean reward −0.98, 0 spread → product-form queued |
| `grandpa_balance04` | (similar) | Pinned at mean reward −0.98 → product-form queued |
| `boxer_locomotion01` | not started | Fresh model line; command observation changes the input layout |
| `boxer_balance05` | not started | Replaces `boxer_balance04`; identical rig & curricula, product-form reward |
| `boxer_walk01` | not started | Inherits `init_path: results/boxer_balance05/Boxer` |
| `boxer_selfplay01` | not started | Needs opponent-obs re-enabled on prefabs (skeleton only) |

---

### Matrix B — Physics & actuators

| Fighter | Rig component types | Drive mode | DOF count | Action mapping | Behavioral purpose |
|---|---|---|---|---|---|
| **Standard / Capsule** | 14 × `ConfigurableJoint` (parent→child bone chain) + 14 × `ArticulationBody` mirrors + 15 × `Rigidbody` (root + 14 segments) | SLERP (`slerpDrive` on each joint), zero-centered [−1,1] → asymmetric per-axis range | 30 active DOFs (out of 42 possible — yaw axes disabled on most joints) | Order = joints in list order; per joint pitch (X), roll (Y), yaw (Z), skipping inactive axes. `action ≥ 0 ? a*high : −a*low`. Optional exponential smoothing α = 1 − e<sup>−dt/τ</sup> when `_actionSmoothingSeconds > 0`. | Stage-1 balance: stand upright under shoves and contest-grade cube projectiles. |
| **Grandma** | Same rig topology, but bone local axes differ (imported skeleton). 14 × `ConfigurableJoint`, 14 × `ArticulationBody`, 15 × `Rigidbody`. | SLERP, zero-centered, **negative Kp/Kd signs** on the heuristic bot to match the imported bone axes. | 30 active DOFs | Same as Standard. | Stage-1 balance on a Grandma skeleton. Per-rig calibration needed for the heuristic bot to balance. |
| **Grandpa** | Same rig topology, bone axes differ again. 14 × `ConfigurableJoint`, 14 × `ArticulationBody`, 15 × `Rigidbody`. | SLERP, zero-centered, **negative Kp/Kd signs** (calibration pending per rig). | 30 active DOFs | Same as Standard. | Stage-1 balance on a Grandpa skeleton. |
| **Locomotion (planned)** | Same rig as Standard; the locomotion flag is on the agent, not the rig. | SLERP, zero-centered. | 30 active DOFs | Same as Standard. | Unified stand-and-walk: commanded speed = 0 m/s for the balance contest, 1 m/s for the walk race. |
| **Walk (planned)** | Standard rig + 4 pre-placed fighters in `SCN_TRAIN_WALK`. | SLERP, zero-centered. | 30 active DOFs | Same as Standard. | Stage-2 walking: travel 5.6 m along +Z; bonus reward on reach; inherits `init_path` from balance05. |

#### Drive parameter table (per rig)

The exact per-joint `baseSpring` / `baseDamper` / `baseMaxForce` are captured by the rig tool at build time and serialized into the prefab. They are then scaled at runtime:

```
positionSpring  = baseSpring  * staminaSpringScale * strengthScale
positionDamper  = baseDamper                                  (unchanged)
maximumForce    = baseMaxForce *                  strengthScale
```

| Scale source | Field | Default | Effect |
|---|---|---|---|
| Stamina | `_currentSpringScale = 0.35 + 0.65 * anaerobic^1.2` | 1.0 at fresh | Softens joints as the agent tires |
| Strength curriculum | `_strengthScale` (env param `strength_scale`) | 1.0 | Multiplies spring AND max force |
| Realism profile | Captured at Awake; foot=0.5×, shin=0.8×, arm=0.6×, head=0.4×, hips/thighs/torso=1.0× | off | Human strength proportions |
| Foot contact tightening | `collider.contactOffset = 0.01f` | off (only with realism) | Crisper ground contact, less float |

#### Heuristic bot

Every prefab ships a code-driven heuristic bot so that an untrained brain still works at runtime:

- Ankle-pitch and ankle-roll PD on the horizontal center-of-mass offset over the feet
- Counter-hip-pitch / hip-roll at half gain
- `_heuristicKp = -4f`, `_heuristicKd = -1.2f` by calibration (2026-08-17 contest-scene A/B); negative values stabilize the capsule and Grandma rigs

This is the same bot used inside `Systems_ContestSpawner.Configure` (when `entry.forceHeuristic == true`) and during training to bootstrap PPO.
