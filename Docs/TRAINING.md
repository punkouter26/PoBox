# Training Handbook (updated 2026-08-18)

## Balance-training upgrades (2026-08-18, from WalkerAgent reference study)

New knobs — all inert for existing scenes/brains until a rebuild opts in:

| Knob | Where | Default | Effect |
|------|-------|---------|--------|
| `_legUprightWeight` | `Reward_Balance` | 0 in old scenes, 0.2 in newly built ones | Rewards vertical thighs/shins (calibrated per-rig from start pose) so legs act as a column |
| `strength_scale` env param | `Systems_StrengthCurriculum` (auto-added by balance scene builder) | 1.0 | Curriculum-scalable joint spring + force cap; learn weak → strong, or assist a weak rig early |
| `_observeFootHeight` | `Agent_FighterBoxing` | false | +2 foot-ground raycast obs. **Changes obs size — breaks all existing .onnx.** Only for new model lines; re-run Prepare for Training |

Stuck-run rescue playbook (agent flatlines at ≈ −1, zero variance):
1. Zero all penalties (energy weight, shove force) until reward > 0.
2. Enable `_legUprightWeight` and rebuild scene + env.
3. Add `strength_scale` curriculum: hold 0.5 until standing, anneal to 1.0.
4. Verify drives can hold the start pose passively; if not, raise spring/force.
5. Only then restore shoves and energy penalty.


## What changed in run 02

| Change | Effect |
|--------|--------|
| `_invertTargetRotation = true` on ALL rigs (prefabs + scenes) | Joint drives now move the commanded direction. **Every model trained before this flag is invalid** (grandma_balance01, grandpa_balance01, boxer_balance01). |
| Opponent observations trimmed for balance phase | 138 → 119 observations. Boxing phase flips `_observeOpponent` back on and re-runs Tools > ML Boxing > 3. |
| Energy penalty enabled (`_energyWeight = 0.03`) | Rewards calm posture; reads torque-based power from Systems_Stamina. |
| Shove curriculum in `*Balance02.yaml` | Lesson 1 calm air → lesson 2 shoves ≤120 N → lesson 3 shoves ≤300 N. Advances on smoothed reward. |
| Body variation widened (mass ±12%, strength ±15%) | More robust policies across the 16-fighter grid. |
| Heuristic bot in `Agent_FighterBoxing.Heuristic()` | Ankle+hip PD on COM offset. Runs whenever no trainer and no model is attached. Gains are NEGATIVE by calibration; Grandpa's skeleton may need per-rig tuning. |
| Foot sole friction material `PM_FootSole` | static 0.9 / dynamic 0.85, combine = Maximum. |

## Balance training — editor (1 env)

```powershell
.venv\Scripts\activate
mlagents-learn Config/BoxerBalance02.yaml --run-id=boxer_balance02
# then press Play in SCN_TRAIN_BALANCE
tensorboard --logdir results   # ALWAYS start with training
```

Grandma: `Config/GrandmaBalance02.yaml` + `--run-id=grandma_balance02` in SCN_TRAIN_BALANCE_GRANDMA.
Grandpa: `Config/GrandpaBalance02.yaml` + `--run-id=grandpa_balance02` in SCN_TRAIN_BALANCE_GRANDPA.

## Balance training — headless (4 envs, much faster)

Environment build: `Builds/BoxerBalanceEnv/BoxerBalanceEnv.exe` (scene 0 = SCN_TRAIN_BALANCE).

```powershell
.venv\Scripts\activate
mlagents-learn Config/BoxerBalance02.yaml --run-id=boxer_balance02 `
  --env Builds/BoxerBalanceEnv/BoxerBalanceEnv.exe `
  --num-envs 4 --no-graphics --base-port 5005
tensorboard --logdir results
```

- 4–8 envs max — leave CPU cores for PyTorch (project rule).
- Record `--num-envs` used; it alters batching.
- Shutdown order: trainer → envs → TensorBoard. Kill TensorBoard before any `--force` wipe.

## Watch in TensorBoard

- `Environment/Cumulative Reward` — should clear 0.5 (lesson 1 gate) then dip when shoves start.
- `Environment/Lesson Number/shove_force_max` — curriculum progress.
- `Balance/UprightMean`, `Balance/FallCause` (histogram), `Balance/HeadHeightAtEnd` — custom stats.

## Boxing phase (later)

`Config/BoxerSelfPlay01.yaml` is a ready skeleton: flip `_observeOpponent` on,
re-run Prepare for Training, build the boxing scene, and judge progress by
**Self-Play/ELO**, never mean reward (project rule).
