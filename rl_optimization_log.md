# RL Optimization Log — Nick get-up line (autonomous loop, 2026-09-16)

The previous optimization log (PhysX/ML-Agents era + early MuJoCo loops) was
removed in commit `1b15750` and lives in git history. This file is the live
log of the 8-hour autonomous loop authorised today. Goal: teach Nick to get
up after falling — as a THIRD behaviour inside the one brain — while
maximising throughput on this machine, and leave the ring/walk tables
unregressed.

## Task being optimised

`nick_env.py` gained a GET-UP task (this session):

- `getup_fraction` (default 0.25) of episodes start LYING: root tilted past
  vertical (65–115 deg) about a random horizontal axis, random yaw, hinges at
  rest +-0.3 rad, pelvis z 0.10–0.24 m, zero velocity.
- Reward while down: additive shape `0.30 * exp(-((pelvis_z - rest)^2)/0.15)`
  `+ 0.20 * up_z`; after rising: `0.30 * up_z * both-feet-planted`; one-time
  `+3.0` bonus for holding the rise 4 s (the practice scene's success rule).
- Termination: a get-up world only terminates on a fall AFTER it has risen;
  before that the floor is the start state, not a failure.
- Observation contract UNCHANGED (127 terms; get-up worlds command 0 m/s like
  standing worlds). No C# change; same brain file for all three behaviours.

Game side: `Assets/MuJoCoCreature/Scenes/Nick_GetUpPractice.unity` +
`Systems_NickGetUpRing` (automatic 1800 N knock-down, 45 s rise window,
success = head > 75% standing held 4 s, `GETUP_RESULT` log lines). Verified
live: knock-down, attempt timing, FAIL + auto-stand all work. The current
brain (Nick_Torque002) fails every attempt — that is the baseline.

## Phase 1 — audit & baseline (done, ~40 min)

### KPI definitions (all already logged per iteration to TensorBoard + stdout)

| KPI | Metric (extras["log"]) | Baseline value (getup_base, iter 120) |
|---|---|---|
| Survival step ratio | `fall_rate_standing` / `fall_rate_moving` / eval survival | see caveat below |
| Target velocity error | `track_err_frac`, `track_within_10pct` | 0.296 / 0.192 (mid-train means; eval owns this number) |
| Actuator control cost | `action_cost`, `actuator_force_abs/max` | 0.78 / 31.1 / 148.8 N·m |
| Joint jerk | `joint_accel_abs`, `action_rate`, `hinge_speed_max` | 70.7 / 0.53 / 11.9 rad/s |
| Torso pitch/roll deviation | `tilt_deg` | 23.7 deg (pulled up by lying worlds) |
| GET-UP (new) | `getup_success_rate`, `getup_risen_rate`, `getup_time_mean`, `getup_progress` | **0.0%** / 0.28% / – / 0.019 |

Caveat recorded honestly: with get-up worlds in the mix, the standing fall
rate is polluted (lying worlds are command-0 and count as "standing"). The
in-training numbers are for CURVE WATCHING; the judge is always
`eval_nick.py` (balance/ring/walk tables) and the new `eval_getup.py`
(success rate / time-to-stand over N random fallen starts), never the
training curve — per the project's own rule.

### Instrumentation

- TensorBoard: ON for real runs (train_nick starts it, port 6007).
- Per-iteration stdout: every `Metrics/*` scalar prints each iteration and is
  redirected to a run log file — the CSV-equivalent record.
- New `Tools/MuJoCo/eval_getup.py`: success rate, rise-time quartiles,
  relapse rate, on its own body + step provenance line.

### Physics audit of the trained body (nick_unity.xml) — AUDIT ONLY

House rule: the body is parity-locked with Unity (`verify_body_parity.py`);
any change to limits/damping/forcerange invalidates every shipped policy.
So this loop does NOT edit the MJCF. Verified state:

- gravity 9.81, timestep 0.005; env pins integrator `implicitfast`, solver
  iterations 20 (both measured 2026-09-07/08, recorded in nick_env.py).
- 30 hinges, ALL with explicit `range` limits; damping 2 per joint.
- Position servos kp=400, kv=70.17 (dampratio-resolved), forcerange ±150,
  ctrlrange ±0.52 rad — the human-limits budget, in-PHYSICS-limit ✓.
- 0 explicit `<exclude>` tags: MuJoCo's default parent-child filtering
  applies; non-adjacent segments DO collide — matches "everything collides".
- **Fixed this session**: constraint-arena overflow on lying starts
  (`nefc overflow`, need 69 rows vs 64-row default heuristic — dropped
  constraints would let a body sink through the floor and poison the task).
  `put_data` now pins `nconmax=96, njmax=192`. Smoke clean after.

### Throughput baseline (RTX 2060 6 GB, Editor CLOSED — the long-run rule)

| Config | Iteration time | Control steps/s |
|---|---|---|
| 4096 envs | 1.90 s | ~51.7k (393k physics steps/s) |
| 8192 envs (probe) | 3.42 s | ~57.5k (+11%) |

Decision: **4096** — the +11% is not worth leaving the proven env count that
all comparable runs (nick12/13 lineage) used; 6 GB VRAM stays comfortable
with the Newton viewer open.

### Convergence thresholds / exit criteria (quantified)

1. **Get-up success**: eval_getup success rate > 90% across 10 consecutive
   evaluation episodes (fresh fallen starts), rise time median < 10 s.
2. **No regression**: eval_nick BALANCE ring >= 90% full-cap and WALK >= 80%
   clean-walk survival at the trained step (0.02 s control), or better than
   the warm-start checkpoint's own baseline, whichever is lower.
3. **Effort stays human**: `hinge_speed_max` within the per-family budgets
   (K11); actuator force within ±150 N·m.
4. **Plateau**: get-up success change < 3 points across 3 consecutive
   validations -> stop early.
5. **Clock**: 8 hours hard stop.

## Phase 2 — log

(appended below as segments complete)
