# Observation & Action Tensor Blueprint

## Tier 1 — 30-second summary

The agent emits a flat **observation vector** of **119 / 121 / 125 / 138 floats** depending on which optional flags are enabled. The MLP returns **30 continuous actions** in [−1, 1], one per active DOF. The mapping from action to joint is **zero-centered** so a fresh zero-mean policy always commands the rest pose.

## Tier 2 — Layout overview

### Observation vector (default 121)

| Slice | Floats | Contents |
|---|---|---|
| 0..12 | 13 | Pelvis root state (height, linear vel, angular vel/20, up, forward) |
| 13..110 | 98 | Per-joint proprio (7 × 14): localRotation (4) + angularVelocity/20 (3) |
| 111..118 | 8 | Foot contact state (left grounded + normal, right grounded + normal) |
| 119..120 | 2 | Foot-to-ground ray distances (when enabled) |
| Optional +19 | — | Opponent-relative (position, velocity, head, gloves, stamina, ring center) |
| Optional +4 | — | Commanded locomotion (speed + direction in pelvis-local) |

### Action vector (30 floats)

Action `i` is mapped to joint `j`, axis `a` in the rig's joint list order. Inactive axes are skipped, so the action cursor and DOF count match the union of active axes across the 14 joints. Asymmetric per-axis ranges are captured at rig-build time:

```
action 0..30   →   pitch / roll / yaw   →   targetRotation   →   SLERP drive
```

### Network architecture

```
Input [1, N_obs]
   ↓
Dense(N_obs → 512) + tanh
   ↓
Dense(512 → 512) + tanh
   ↓
Dense(512 → 512) + tanh
   ↓          ↓
Mean head   Sigma head
   ↓          ↓
Output action tensor [1, 30]  (sampled from Gaussian during training;
                                deterministic (= mean) at inference)
```

Hidden layers: **3**, units: **512**, normalize: **true**. Constant learning rate schedule; linear decay for balance/walk/locomotion.

## Tier 3 — Granular specification

### Observation layout (index-level)

Index breakdown for the 121-float default (foot-height rays on, opponent off, locomotion off):

```
[0]      pelvis.position.y
[1..3]   InverseTransformDirection(pelvis.linearVelocity)
[4..6]   InverseTransformDirection(pelvis.angularVelocity) / 20
[7..9]   pelvis.transform.up
[10..12] pelvis.transform.forward

For joint j in 0..13 (stride = 7, base = 13 + 7*j):
[base+0..3] body.transform.localRotation (quat = 4 floats)
[base+4..6] body.angularVelocity / 20

[111]    _footLeft.IsGrounded
[112..114] _footLeft.ContactNormal
[115]    _footRight.IsGrounded
[116..118] _footRight.ContactNormal

[119]    FootGroundDistance01(_footLeft)     (Physics.Raycast, max 1 m)
[120]    FootGroundDistance01(_footRight)    (Physics.Raycast, max 1 m)
```

### Foot-height ray

```csharp
private static float FootGroundDistance01(Sensor_GroundContact foot) {
    if (foot == null) return 1f;
    return Physics.Raycast(foot.transform.position, Vector3.down, out RaycastHit hit, FOOT_RAY_MAX_METERS)
        ? hit.distance / FOOT_RAY_MAX_METERS
        : 1f;
}
```

WalkerAgent standing-phase trick: gives the policy an explicit "am I about to fall" signal without forcing it to learn inverse kinematics.

### Action → joint mapping

Order: joints in `Systems_FighterRig._joints` list order; per joint pitch (X), roll (Y), yaw (Z), skipping inactive axes.

| Axis | Map | Comment |
|---|---|---|
| pitch (X) | `a ≥ 0 ? a*high : −a*low` | Asymmetric: foot can hyperextend more than flex |
| roll (Y) | same | |
| yaw (Z) | same | Disabled on most joints in current rigs (30 active DOFs out of 42 possible) |

After mapping, the target is smoothed exponentially:

```csharp
float alpha = _actionSmoothingSeconds > 0f
    ? 1f - Mathf.Exp(-Time.fixedDeltaTime / _actionSmoothingSeconds)
    : 1f;
_smoothedTargets[i] += (target - _smoothedTargets[i]) * alpha;
```

When `_actionSmoothingSeconds == 0` (default), α = 1 and smoothing is a no-op (targets snap to commanded). When `> 0`, the brain sees a muscle-like lag that the rig was trained with.

### Joint drive parameterization

```csharp
drive.positionSpring  = baseSpring  * springScale * strengthScale;
drive.positionDamper  = baseDamper;
drive.maximumForce    = baseMaxForce * strengthScale;
joint.slerpDrive      = drive;
joint.targetRotation  = Quaternion.Euler(sign*pitch, sign*roll, sign*yaw);
```

`baseSpring`, `baseDamper`, `baseMaxForce` are captured at rig-build time from a PD calibration sweep and stored on the `RigJointEntry`. `springScale` (0.35..1.0) is from `Systems_Stamina` and falls with motor power. `strengthScale` (0.6, 0.8, 1.0) is from the env parameter `strength_scale`.

### Diagrams

- **Tensor simple:** `assets/tensor-simple.svg`
- **Tensor detailed:** `assets/tensor-detailed.svg`
