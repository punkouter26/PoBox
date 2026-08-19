# Runtime Pipeline — From Observation to Joint Drive

## Tier 1 — 30-second summary

Every **0.02 s**, the agent writes a flat observation vector, the **Sentis inference engine** runs the policy MLP and returns a 30-float action vector, and `Systems_FighterRig` maps those floats onto **SLERP drives** that PhysX follows until the next tick. Fall detection terminates the episode; otherwise the buffer accumulates for the PPO update.

## Tier 2 — Pipeline overview

```
┌──────────────────────────────────────────────────────────────────────────┐
│  PER FIXEDUPDATE TICK (Δt = 0.02 s)                                       │
│                                                                          │
│  Academy stepper                                                         │
│      │                                                                   │
│      ▼                                                                   │
│  CollectObservations(VectorSensor)                                       │
│      │   root (13) + per-joint (7 × 14) + feet (8) [+ foot-height (2)]  │
│      │   [+ opponent (19)] [+ locomotion command (4)]                    │
│      ▼                                                                   │
│  Unity Inference Engine (Sentis) ONNX forward                            │
│      │   input  [1, N_obs] float32                                       │
│      │   body   Dense(512)+tanh × 3                                      │
│      │   heads  mean, sigma (Gaussian policy)                            │
│      ▼                                                                   │
│  output [1, 30] float32 → sampled action                                 │
│      │                                                                   │
│      ▼                                                                   │
│  OnActionReceived → buffer (mean |Δaction| computed)                     │
│      │                                                                   │
│      ▼                                                                   │
│  Systems_FighterRig.ApplyActions (FixedUpdate-only)                      │
│      │   action 0-centered → pitch/roll/yaw per active axis              │
│      │   optional exponential smoothing                                  │
│      ▼                                                                   │
│  14 × ConfigurableJoint.slerpDrive (PD via baseSpring/baseDamper/baseMax)│
│      │                                                                   │
│      ▼                                                                   │
│  PhysX ArticulationBody step + ground contact sensors                    │
│      │                                                                   │
│      ▼                                                                   │
│  Reward_Balance / Reward_Locomotion / Reward_Walk → AddReward            │
│      │                                                                   │
│      ▼                                                                   │
│  Terminal?  fall sensor grounded OR head < 0.4 * start → EndEpisode       │
└──────────────────────────────────────────────────────────────────────────┘
```

## Tier 3 — Granular pipeline

### Step 1 — `CollectObservations`

Defined in `Agent_FighterBoxing.cs`. The observation layout is built from these constants:

| Constant | Floats | Source |
|---|---|---|
| `ROOT_OBSERVATIONS` | 13 | `pelvis.position.y` + `InverseTransformDirection(linearVelocity)` + `InverseTransformDirection(angularVelocity)/20` + `transform.up` + `transform.forward` |
| `PER_JOINT_OBSERVATIONS` × joint count | 7 × 14 = 98 | Each joint's `localRotation` (4 floats) + `angularVelocity / 20` (3 floats) |
| `FOOT_OBSERVATIONS` | 8 | `_footLeft.IsGrounded` + `ContactNormal` (3) + `_footRight.IsGrounded` + `ContactNormal` (3) |
| `FOOT_HEIGHT_OBSERVATIONS` (optional) | 2 | Two downward `Physics.Raycast` distances, divided by 1 m |
| `LOCOMOTION_OBSERVATIONS` (optional) | 4 | Commanded speed + `InverseTransformDirection(commandedDirection)` |
| `OPPONENT_OBSERVATIONS` (optional) | 19 | Opponent pelvis position/velocity, head, gloves, stamina, ring center |

**Total:** 119 (no foot height, no cmd), 121 (foot height on), 125 (cmd on), or 138 (opponent on).

### Step 2 — Sentis forward

The exported ONNX runs a 3-hidden-layer MLP. The exact shape is observed from `Assets/Agents/Standard/Boxer.onnx`:

- **Input:** `obs_0` shape `batch × 119`
- **Body:** 3 × `Dense(512) + tanh`
- **Output heads:**
  - `continuous_actions` shape `batch × 30`
  - `deterministic_continuous_actions` shape `batch × 30` (mean, for inference determinism)
  - `continuous_action_output_shape` (scalar, used by the inference runtime)
  - `version_number`, `memory_size` (PPO version metadata)

Active `initializer` count: **14** (3 weight + 3 bias for body layers + output head weights + biases + auxiliary constant nodes).

### Step 3 — `OnActionReceived`

```csharp
public override void OnActionReceived(ActionBuffers actions) {
    var continuous = actions.ContinuousActions;          // length == DofCount
    float deltaSum = 0f;
    for (int i = 0; i < _pendingActions.Length; i++) {
        float incoming = continuous[i];
        deltaSum += Mathf.Abs(incoming - _pendingActions[i]);
        _pendingActions[i] = incoming;
    }
    _lastActionDelta01 = _pendingActions.Length > 0
        ? Mathf.Clamp01(deltaSum / (_pendingActions.Length * 2f))
        : 0f;
    _hasPendingActions = true;
}
```

Actions live in [−1, 1]; mean |Δaction| is normalised to [0, 1] by dividing by `2 × DofCount` (the largest possible per-DOF jump).

### Step 4 — `FixedUpdate → Rig.ApplyActions`

```csharp
public void ApplyActions(float[] actions, int offset) {
    float sign = _invertTargetRotation ? -1f : 1f;
    float alpha = _actionSmoothingSeconds > 0f
        ? 1f - Mathf.Exp(-Time.fixedDeltaTime / _actionSmoothingSeconds)
        : 1f;
    for (int j = 0; j < _joints.Count; j++) {
        RigJointEntry entry = _joints[j];
        int base = j * 3;
        float pitch = 0f, roll = 0f, yaw = 0f;
        if (entry.hasPitch) pitch = MapZeroCentered(actions[cursor++], entry.pitchLow, entry.pitchHigh);
        if (entry.hasRoll)  roll  = MapZeroCentered(actions[cursor++], entry.rollLow, entry.rollHigh);
        if (entry.hasYaw)   yaw   = MapZeroCentered(actions[cursor++], entry.yawLow, entry.yawHigh);
        _smoothedTargets[base+0] += (pitch - _smoothedTargets[base+0]) * alpha;
        _smoothedTargets[base+1] += (roll  - _smoothedTargets[base+1]) * alpha;
        _smoothedTargets[base+2] += (yaw   - _smoothedTargets[base+2]) * alpha;
        entry.joint.targetRotation = Quaternion.Euler(
            sign * _smoothedTargets[base+0],
            sign * _smoothedTargets[base+1],
            sign * _smoothedTargets[base+2]);
    }
}

private static float MapZeroCentered(float a, float low, float high) {
    return a >= 0f ? a * high : -a * low;
}
```

The zero-centered mapping is deliberate: action = 0 must always command the rest pose even on asymmetric ranges — a fresh zero-mean policy must not command a squat.

### Step 5 — PhysX step

Each joint's `slerpDrive` is set every FixedUpdate:

```
positionSpring = baseSpring  * springScale * strengthScale
positionDamper = baseDamper
maximumForce   = baseMaxForce * strengthScale
```

`baseSpring`/`baseDamper`/`baseMaxForce` are captured at rig-build time and serialized into the prefab. `springScale` is `0.35 + 0.65 * anaerobic^1.2` from `Systems_Stamina`. `strengthScale` comes from the env parameter `strength_scale` (curriculum).

### Step 6 — Reward and terminal

`Reward_Balance` adds `_stepScale * (balanceReward - smoothnessWeight * LastActionDelta01)` each FixedUpdate, where `_stepScale = 1 / MaxStep` so the per-step reward is commensurate with the −1 fall terminal.

`IsFallen(out int cause)` returns true when any non-foot sensor (torso, head, shins, gloves) is grounded OR when `head.position.y < 0.4 * startHeadY`.

### Diagrams

- **Pipeline simple:** `assets/lifecycle-simple.svg`
- **Pipeline detailed:** `assets/lifecycle-detailed.svg`
