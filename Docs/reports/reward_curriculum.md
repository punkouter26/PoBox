# Sensors, Reward, and Curriculum

## Tier 1 — 30-second summary

The agent earns per-step reward for **staying upright**, **keeping the head high**, keeping the **center of mass over the feet**, and (with locomotion on) **matching a commanded speed**. A fall triggers the −1 terminal and ends the episode. Three **curricula** gate difficulty: drive strength, shove force, and projectile cube speed — each lesson raises the bar only after the agent sustains a reward threshold.

## Tier 2 — Reward & curriculum overview

```
Per-step reward
   ├─ uprightWeight       (0.3)  torso.up · world.up, squared
   ├─ heightWeight        (0.3)  exp(−20 · ΔheadY²)
   ├─ comWeight           (0.2)  COM over feet projection
   ├─ legUprightWeight    (0.2)  thigh + shin vertical
   ├─ speedMatchWeight    (0.4)  exp(−speedError²/0.36)   ← locomotion only
   ├─ energyWeight        (0)    torque·angularSpeed clamp ← optional
   └─ smoothnessWeight    (0.05) mean |Δaction|

Form: weighted sum  OR  product (geometric mean, weights become exponents)

Terminal:  AddReward(−1) + EndEpisode()   ← balance/walk only
           EndEpisode() only              ← locomotion (no penalty)
```

Curricula (raise threshold to advance):

| Param | Lessons |
|---|---|
| `strength_scale` | WeakMotors 0.6 → MediumMotors 0.8 → FullMotors 1.0 |
| `shove_force_max` | CalmAir 0 → GentleShoves 60 → LightShoves 120 → FullShoves 300 |
| `cube_speed_max` | NoCubes 0 → SoftCubes 6 → ContestCubes 12 |
| `speed_command_max` (locomotion only) | StandStill 0 → Shuffle 0.3 → Step 0.7 → Walk 1.0 |

## Tier 3 — Granular specification

### Sensors

`Sensor_GroundContact` is attached by the rig tool to every foot segment. It uses **PhysX collision callbacks**:

```csharp
private void OnCollisionEnter(Collision collision) {
    if (collision.rigidbody == null) {
        _contactCount++;
        if (collision.contactCount > 0)
            _lastNormal = collision.GetContact(0).normal;
    }
}
```

"Ground" = any static collider (no attached rigidbody). This means the canvas, ring apron, and cube projectiles do **not** count — only the static ring floor does.

Fall sensors are added at spawn time by `Systems_ContestSpawner.Configure`:

```csharp
rig.Torso.gameObject.AddComponent<Sensor_GroundContact>();
rig.Head.gameObject.AddComponent<Sensor_GroundContact>();
rig.Joints[JOINT_INDEX_SHIN_L].body.gameObject.AddComponent<Sensor_GroundContact>();
rig.Joints[JOINT_INDEX_SHIN_R].body.gameObject.AddComponent<Sensor_GroundContact>();
rig.GloveLeft.gameObject.AddComponent<Sensor_GroundContact>();
rig.GloveRight.gameObject.AddComponent<Sensor_GroundContact>();
```

The fall sensors are wired into `Reward_Balance._fallContacts`. Foot sensors (left + right) are wired separately into the agent for the contact observation, not the fall list — so a planted foot never triggers a fall.

### Reward form: weighted sum vs product

`Reward_Balance` has a boolean `_productReward` field. When **false** (gen 2, default for old scenes), the reward is:

```
r = uprightWeight * uprightReward
  + heightWeight * heightReward
  + comWeight * comReward
  + legUprightWeight * legUprightReward
```

When **true** (gen 3, default for scenes built after 2026-08-18), the reward becomes a weighted geometric mean:

```
r = uprightReward^uprightWeight
  · heightReward^heightWeight
  · comReward^comWeight
  · legUprightReward^legUprightWeight
```

The product form comes from the WalkerAgent reference project: every criterion must hold at once, and per-step reward is positive-definite, so "fell slower" always scores better than "fell fast". The implementation guards against a single zero factor wiping the whole step:

```csharp
private static float ProductFactor(float value, float weight) {
    return Mathf.Pow(Mathf.Max(PRODUCT_FACTOR_FLOOR, value), weight);
}
```

### Locomotion reward (no terminal penalty)

`Reward_Locomotion` deliberately **does not** add a −1 fall penalty. The reasoning (verbatim from the script header):

> Per-step reward is positive-definite and ending early already forfeits every remaining step, which is punishment enough. The old −1 terminal was ~40× anything a 1.5 s episode could earn and drowned the signal (observed 2026-08-19: mean reward pinned at −0.98, episodes stuck at 76 steps).

The locomotion reward is:

```
r = upright^0.3 · height^0.3 · speedMatch^0.4   (product)
    − smoothnessWeight * LastActionDelta01
```

`speedMatch = exp(−(speedError)² / 0.36)` where `speedError = pelvis.vel · commandedDirection − commandedSpeed`. The signed dot product means travelling **backwards** scores worse than standing still.

### Walk bonus (stage 2)

`Reward_Walk` is layered on top of `Reward_Balance` (`[DefaultExecutionOrder(-98)]`). It pays per-step for travel along the goal direction:

```
speedTowardGoal = pelvis.vel · goalDirection
speedReward     = clamp01(speedTowardGoal / TARGET_SPEED)   // 1.0 m/s target pace
r += _stepScale * progressWeight * speedReward
```

…and adds `_goalBonus = 1.0` when the fighter reaches `_goalDistance = 5.6 m`, then ends the episode.

### Curricula (full YAML)

```yaml
environment_parameters:
  strength_scale:
    curriculum:
      - name: WeakMotors
        completion_criteria:
          measure: reward
          behavior: Boxer
          signal_smoothing: true
          min_lesson_length: 100
          threshold: 0.4
        value: 0.6
      - name: MediumMotors
        completion_criteria:
          measure: reward
          behavior: Boxer
          signal_smoothing: true
          min_lesson_length: 100
          threshold: 0.55
        value: 0.8
      - name: FullMotors
        value: 1.0

  shove_force_max:
    curriculum:
      - name: CalmAir
        completion_criteria: { measure: reward, behavior: Boxer, signal_smoothing: true, min_lesson_length: 100, threshold: 0.5 }
        value: 0.0
      - name: GentleShoves
        completion_criteria: { measure: reward, behavior: Boxer, signal_smoothing: true, min_lesson_length: 100, threshold: 0.5 }
        value: 60.0
      - name: LightShoves
        completion_criteria: { measure: reward, behavior: Boxer, signal_smoothing: true, min_lesson_length: 100, threshold: 0.6 }
        value: 120.0
      - name: FullShoves
        value: 300.0

  cube_speed_max:
    curriculum:
      - name: NoCubes
        completion_criteria: { measure: reward, behavior: Boxer, signal_smoothing: true, min_lesson_length: 150, threshold: 0.65 }
        value: 0.0
      - name: SoftCubes
        completion_criteria: { measure: reward, behavior: Boxer, signal_smoothing: true, min_lesson_length: 150, threshold: 0.7 }
        value: 6.0
      - name: ContestCubes
        value: 12.0
```

For locomotion, the only curriculum is `speed_command_max`:

```yaml
  speed_command_max:
    curriculum:
      - { name: StandStill, value: 0.0,  completion_criteria: { ... threshold: 0.25 } }
      - { name: Shuffle,    value: 0.3,  completion_criteria: { ... threshold: 0.30 } }
      - { name: Step,       value: 0.7,  completion_criteria: { ... threshold: 0.35 } }
      - { name: Walk,       value: 1.0 }
```

### Self-play curriculum

`Config/BoxerSelfPlay01.yaml` adds a separate self-play block. **It is a skeleton** — the boxing scene and opponent observations need to be re-enabled before first use.

```yaml
self_play:
  save_steps: 100000
  team_change: 200000
  swap_steps: 20000
  window: 10
  play_against_latest_model_ratio: 0.5
  initial_elo: 1200.0
```

The model line includes an LSTM memory cell:

```yaml
network_settings:
  hidden_units: 512
  num_layers: 3
  memory:
    memory_size: 128
    sequence_length: 64
```

Project rule: evaluate self-play with ELO, not mean reward.

### Diagrams

- **Sensor / reward simple:** `assets/sensor-reward-simple.svg`
- **Sensor / reward detailed:** `assets/sensor-reward-detailed.svg`
