# PoBox ML-Agents — Architecture Reference

> Three-tier progressive reference. Read tier 1 for context, tier 2 to understand the system, tier 3 to implement or audit it.

## Tier 1 — 30-second summary

PoBox trains **active-ragdoll fighters** to stay upright under shoves and projectile cubes. Each fighter has **14 driven joints**; PPO controls them through a **3 × 512 MLP**; physics runs on **PhysX ArticulationBody** at a locked **0.02 s** fixed step. Three model lines exist (Capsule, Grandma, Grandpa) because each rig has different bone axes — a brain trained on one rig cannot drive another. The whole stack is reproducible end-to-end: `Assets/Agents/<Name>/Boxer.onnx` is the shipped brain, `Config/<Name>Balance0X.yaml` is the recipe.

## Tier 2 — Component & flow overview

```
Scene (SCN_*) ─┬─ Fighter prefabs (×16 training, ×0–8 contest)
               ├─ Ground (static collider; "ground" = rigidbody==null)
               ├─ Lights / Cameras / FX / UI
               └─ Academy / DecisionRequester (50 Hz policy tick)
                       │
                       ▼
   ┌─────────── Agent_FighterBoxing ───────────────┐
   │ CollectObservations → VectorSensor (121/125)  │
   │ OnActionReceived   → buffer (30 actions)     │
   │ FixedUpdate        → ApplyActions on rig     │
   └─────────────┬─────────────────────────────────┘
                 │ per-step reward
                 ▼
   Reward_Balance | Reward_Locomotion | Reward_Walk
                 │
                 ▼
   ┌─────────── Systems_FighterRig ────────────────┐
   │ 14 ConfigurableJoints, SLERP drives, PD       │
   │ Optional muscle-lag smoothing                 │
   └─────────────┬─────────────────────────────────┘
                 │
                 ▼
   ┌─────────── PhysX ArticulationBody ────────────┐
   │ Step at 0.02 s, gravity −9.81 m/s²            │
   │ Sensors: ground contact (foot/head/torso/...) │
   └─────────────┬─────────────────────────────────┘
                 │ fall detected?
                 ▼
   ┌─────────── Terminal → EndEpisode ─────────────┐
   │ Buffer push to PPO; reset next episode        │
   └───────────────────────────────────────────────┘
```

The **Inference Engine (Sentis)** sits between `CollectObservations` and `OnActionReceived`. The exported ONNX holds a 3-hidden-layer MLP with tanh, a mean head, and a sigma head. The ONNX file is shipped inside the player build (`Assets/Agents/<Name>/Boxer.onnx`) and is bound to `BehaviorParameters.Model` by `Systems_ContestSpawner.Configure`.

## Tier 3 — Granular implementation

### File layout

```
Assets/
├── Agents/
│   ├── Grandma/Boxer.onnx          # promoted brain — promoted from grandma_balance04
│   ├── Grandpa/Boxer.onnx          # promoted brain — promoted from grandpa_balance04
│   └── Standard/Boxer.onnx         # promoted brain — promoted from boxer_balance04
├── Scripts/
│   ├── Agent/Agent_FighterBoxing.cs    # observation/action/heuristic, FixedUpdate-only action apply
│   ├── Sensor/Sensor_GroundContact.cs  # foot/torso/head/shin/glove fall sensors
│   ├── Reward/
│   │   ├── Reward_Balance.cs           # product or sum, head/feet/leg upright + smoothness
│   │   ├── Reward_Locomotion.cs        # unified stand+walk; no fall penalty
│   │   └── Reward_Walk.cs              # stage 2 walking bonus
│   └── Systems/
│       ├── Systems_FighterRig.cs       # 14-joint handle, action mapping, drive scaling
│       ├── Systems_Stamina.cs          # motor-power estimate, spring attenuation
│       ├── Systems_BalanceContest.cs   # referee (test scene)
│       ├── Systems_WalkContest.cs      # walk referee (test scene)
│       ├── Systems_ContestSpawner.cs   # menu → spawn pipeline
│       ├── Systems_ContestSetupMenu.cs # opening menu (built into balance contest)
│       ├── Systems_Announcer.cs        # commentary audio cues
│       ├── Systems_CubeThrower.cs      # projectile cubes (curriculum cube_speed_max)
│       ├── Systems_Shover.cs           # horizontal impulses (curriculum shove_force_max)
│       ├── Systems_HazardDirector.cs   # orchestrates shoves + cubes
│       ├── Systems_MatchDirector.cs    # best-of-N match flow
│       ├── Systems_DramaCamera.cs      # cinematic orbiting camera
│       ├── Systems_WinnerCamera.cs     # final-shot camera
│       ├── Systems_WinnerBanner.cs     # winner banner UI
│       ├── Systems_RingRopes.cs        # visual ropes
│       ├── Systems_CrowdAudio.cs       # ambient loop
│       ├── Systems_CubeImpactFx.cs     # cube hit effects
│       ├── Systems_FallImpactFx.cs     # fall dust
│       ├── Systems_KnockoutFx.cs       # knockout flash
│       ├── Systems_RoundCountdown.cs   # countdown timer
│       ├── Systems_DebugOverlay.cs     # editor-only stats
│       ├── Systems_FpsCounter.cs       # FPS counter
│       ├── Systems_MenuOrbitCamera.cs  # menu camera
│       ├── Systems_PhysicsGuard.cs     # anti-tunnelling safety
│       ├── Systems_JointRangeTester.cs # editor rig verification
│       ├── Systems_FighterVariation.cs # cosmetic tint
│       ├── Systems_StrengthCurriculum.cs # strength_scale env-param bridge
│       ├── Systems_TrainingCubes.cs    # training-time cube spawner
│       └── Systems_UiTheme.cs          # font/colour theming
└── Prefabs/Fighters/
    ├── Fighter_Capsule.prefab         # 14-joint capsule rig, ConfigurableJoints
    ├── Fighter_Grandma.prefab         # imported skeleton
    └── Fighter_Grandpa.prefab         # imported skeleton

Config/
├── BoxerBalance04.yaml                # sum-form balance, gen 2
├── BoxerBalance05.yaml                # product-form balance, gen 3 (queued)
├── BoxerLocomotion01.yaml             # unified stand+walk, fresh model line
├── BoxerSelfPlay01.yaml               # skeleton: needs opponent obs re-enabled
├── BoxerWalk01.yaml                   # stage 2 walking, init_path = balance05
├── GrandmaBalance03.yaml              # gen 2 (sum)
├── GrandmaBalance04.yaml              # gen 3 (product) — queued
├── GrandpaBalance03.yaml              # gen 2 (sum)
└── GrandpaBalance04.yaml              # gen 3 (product) — queued
```

### Lifecycle ordering

`[DefaultExecutionOrder]` guarantees the right tick order without `Update` polling:

| Order | Script | Why |
|---|---|---|
| −100 | `Agent_FighterBoxing` | Must run before the Academy stepper so `OnActionReceived` is up to date when the academy reads actions. |
| −99 | `Reward_Balance` | After the agent, before the academy. |
| −98 | `Reward_Walk` | After `Reward_Balance` so a fall wins the tie. |
| −95 | `Systems_Stamina` | After the agent, before the academy stepper (so spring scales are current). |

### Buffer / tensor flow per tick

| Step | Where | Memory | Notes |
|---|---|---|---|
| 1 | Academy stepper | — | Triggered at 50 Hz (`DecisionPeriod = 1 / 50`) |
| 2 | `CollectObservations` | Writes 119/121/125 floats into a managed `float[]` inside `VectorSensor` | Hot path: `for` loops over joints, no LINQ |
| 3 | Sentis ONNX forward | Input tensor `[1, N]` float32 on the inference backend | Body: 3 × Dense(512) + tanh; heads: mean, sigma |
| 4 | Output tensors | `[1, 30]` float32 for `continuous_actions` and `deterministic_continuous_actions` | Sampled from Gaussian during training; deterministic at inference |
| 5 | `OnActionReceived` | Copies actions into `_pendingActions[30]` and computes mean \|Δaction\| | Smoothness penalty reads `_lastActionDelta01` |
| 6 | `FixedUpdate` → `Rig.ApplyActions` | Maps zero-centered [−1, 1] → per-joint target rotations | Optional exponential smoothing α = 1 − e<sup>−dt/τ</sup> |
| 7 | PhysX | 14 `ConfigurableJoint` SLERP drives per body, 1 × `Rigidbody` root per fighter | All at locked 0.02 s |
| 8 | `Reward_*` | `AddReward(_stepScale * …)` where `_stepScale = 1 / MaxStep` | |
| 9 | Terminal check | If any fall sensor is grounded or head collapsed: `AddReward(-1)` then `EndEpisode()` | Locomotion skips the −1 |
| 10 | Buffer push | Episode segment pushed into the PPO rollout buffer; trainer steps async | |

### Mermaid diagrams

- **Lifecycle (simple):** `assets/lifecycle-simple.svg`
- **Lifecycle (detailed):** `assets/lifecycle-detailed.svg`

### Hot-path allocation discipline

The agent is the hottest path. Rules enforced in code:

- `GetComponentsInChildren` only in `Initialize()` (one-time)
- `new float[]` only in `Initialize()` and `Rig.Awake()` (one-time)
- `for (int i …)` over joints — no LINQ
- `Vector3`, `Quaternion` are structs — stack only
- `AddObservation` writes to the sensor's internal `float[]` — no per-tick allocation
- `Sensor_GroundContact.IsGrounded` is an `int _contactCount`; no allocation per contact
- The `applyDriveScales` in `Systems_Stamina` is gated by a `> 0.005` delta to skip the 14 slerpDrive writes when nothing changed
