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

### Segment 1 (nickgetup01, iters 0→999, warm-started from Nick_Torque002)

- Warm start verified: actor reproduces the exported ONNX to 3.3e-6.
- Ran clean, no crashes, ~2.1–2.5 s/iter with TB + viewer sharing the GPU.
- Training-metric get-up success crawled 0 → ~0.12% of worlds; first
  successes appeared around iteration 84.
- **Episode eval (the judge), 256 fresh fallen starts:**
  - `model_999`: get-up success **0%**, "rose-but-no-hold" 21%, no relapses.
  - `model_500`: identical picture — success 0%, rose 20%.
  - Reading: the policy reaches partway up but cannot hold 4 s; from flat on
    the floor the v1 shaping (gaussian near standing height) had ~no gradient.
- **Regression check on model_999 (the skills that must survive):**
  BALANCE 75% full-cap (median 30 s), WALK 74% (median 20 s, 14.7 m,
  0.73 m/s), RING 70% under hazards, passive baseline 1.3 s. No regression
  from adding the get-up task.

### Change: shaping v2 (applied before segment 2)

1. Linear height ramp (`0.35 * clamp(z / 0.65 rest)`) replaces the gaussian —
   pays from the first centimetre instead of ~0.02 flat signal.
2. One-time milestone bonuses: sit (z > 45% rest) +0.5, crouch (z > 65%) +1.0,
   latched per episode; logged as `getup_milestone_sit/crouch`.
3. Failed get-up attempts end at 60% of the cap (12 s of 20) — a world still
   down at 12 s was learning nothing for 8 more seconds. Timeout bootstraps.
4. `getup_fraction` 0.25 → 0.33; PPO `entropy_coef` 0.001 → 0.005 (more
   flailing = more discovery from the floor).
5. Smoke return improved −0.58 → −0.16 with no code path errors.

### Segment 2 (iters 999→2199) — DONE, first successes

- Shaping v2 moved the climb: sit 22.5% / crouch 6.6% of get-up worlds
  (from unmeasurably small), risen rate peaked ~1.8% mid-run then settled
  0.5% — the policy crouches readily but hesitated at full extension.
- **Episode evals (256 fresh starts):** `model_2199` produced the FIRST
  fresh-start successes — ~0.4–0.8% (1–2 worlds), rise time 5.0 s.
  `model_1800` still 0%. So: real but marginal; still iterating.
- **Regression (model_2199):** BALANCE 75% / WALK 74% (15.1 m) / RING 70% —
  identical to segment 1. The contest skills are simply not being spent.

### Change: curriculum step (applied before segment 3)

1. Hold window trains at **2 s** (was 4) — "stay up" becomes earnable now.
   Evaluations keep measuring the TRUE 4 s standard via `NICK_GETUP_STABLE=4`
   (new env override); the window returns to 4 s once the hold rate exists.
2. `getup_w_hold` 0.30 → 0.60: reward mass moves to staying up.
3. Milestone bonuses halved (sit 0.25, crouch 0.5) — already learned.

### Segment 3 (iters 2199→3399) — DONE, 2 s window alone was not enough

- In-training: risen rate roughly doubled (0.5% → 1.0%), sit 21% — the
  climb continues, but fresh-start holds at the 2 s window did not
  materialise in the eval.
- **Episode evals (256 starts, TRUE 4 s standard via NICK_GETUP_STABLE=4):**
  `model_3399` and `model_2900` both 0% success, 21% rose-but-no-hold.
- Regression (model_3399): BAL 74% / WALK 74% / RING 69% — intact again.
- Diagnosis: the stand-and-hold phase is too rare a slice of experience —
  every episode pays for the whole chain from flat floor before practising
  the last, decisive phase.

### Change: start-height curriculum (applied before segment 4)

A fraction of get-up episodes now START partway up: 25% at crouch height
(60–70% rest pelvis, tilt 10–30 deg), 15% sitting (30–42%, tilt 50–70),
60% still flat on the floor. The stand-and-hold phase trains in isolation
and densely; the flat-floor majority keeps the full chain honest.
Smoke clean.

### Segment 4 (iters 3399→4600) — running

(appended when it lands)
