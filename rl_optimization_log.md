# RL optimisation log

Runs and decisions of the retrofit (plan: `tasks.md`), newest last. Earlier training is in `training/README.md`.

## 2026-10-02: Phase 0 and Phase A (no training; CPU only)

Another session's queue was training gauntlets on the GPU the whole time (`g1_nick`, then `g1_lilmatt`), so
nothing here used the GPU and nothing it reads was changed: the re-derived bodies are in `training/rigs/v2`
and `training/models/v2`, beside the old ones.

**Versions found.** The trainer's Python is 3.11.9 with MuJoCo 3.14.1 and MuJoCo Warp 3.14.0 (the docs say
3.10 and 3.14.0). It has no `pip`; MuJoCo 3.5.0, the version Unity will run, was installed with the base
Python's pip into `training/.mj350` (`--no-deps --target`). A tool run with `PYTHONPATH=training/.mj350` uses 3.5.0.

**Bodies re-derived** with `tools/build_roster.py --all --rigs rigs/v2 --models models/v2 --cubes 8`. Every
model now has the pool of 8 cubes (0.2 m, 1 kg, free joints, waiting at x 40 to 43.5, y 40, z 0.3), and each
boxer has a `_solo.xml` (alone on the floor). Fit pictures: `DOCS/reports/img/bodies_v2/`.

| Boxer | Old kg | New kg | Old m | New m | Mesh volume as BMI | What changed |
|---|---|---|---|---|---|---|
| Matt | 79.5 | 104.6 | 1.81 | 1.86 | 42.0, held to 30 | shapes fitted to the mesh; mass from mesh volume; trunk slimmed four times |
| Zombie | 80.2 | 91.6 | 1.82 | 1.82 | 27.4 | shapes fitted to the mesh; mass from mesh volume; trunk slimmed twice |
| Nick | 58.6 | 58.6 | 1.65 | 1.65 | 21.1 | nothing: already fitted |
| Lil Matt | 81.5 | 81.5 | 1.64 | 1.64 | 34.0, held to 30 | nothing |
| Trump | 109.4 | 109.4 | 1.90 | 1.90 | 56.3, held to 30 | nothing |
| Grandma | 87.8 | 87.8 | 1.70 | 1.70 | 43.8, held to 30 | nothing |
| Grandpa | 79.8 | 79.8 | 1.62 | 1.62 | 47.4, held to 30 | nothing |

**Open, for the owner: the masses.** The rule (mass from the mesh's volume, held to a body-mass index of 18.5
to 30) puts four of the seven on the ceiling, which means the volume told it nothing for them. Matt's mesh
measures 147 litres although the figure is slim (the shapes fitted inside it hold 85 litres, an index of 24):
his mesh has surfaces inside it. At 104.6 kg he is 25 kg heavier than the body his policy knows.

**Self-collision** (house rule): all seven pass. 17 shapes each (capsules, spheres, boxes), 55 of 66 pairs of
parts collide, the 11 excluded are joined by a joint; no pair touches in the T-pose, the guard, or 23 steps
of walking and punching.

**Fingerprints.** `tools/export_fingerprint.py --models models/v2` wrote `<name>_fingerprint.json` for the
seven solo models with MuJoCo 3.5.0: nq 84, nv 75, nu 21, 21 bodies, 26 shapes, 11 excluded pairs. Keyed by
name, for the Unity test of Phase B.

**How much the warm start lost.** Matt and Zombie's current policies (`checkpoints/handoff`), plain C MuJoCo
3.5.0, no exploration noise, 300 s (25 episodes of 12 s), `tools/eval_cmujoco.py --run handoff`:

| Bodies | Fall rate | Matt down | Zombie down |
|---|---|---|---|
| old (`models`) | 0.04 | 4% | 0% |
| re-derived (`models/v2`: Matt 104.6 kg, Zombie 91.6 kg) | 0.04 | 4% | 0% |
| re-derived, Matt at 84 kg (not kept; a trial for the mass question) | 0.00 | 0% | 0% |

So the two veterans stand on their new bodies as well as on their old ones, and the same policies run in
MuJoCo 3.5.0 as in 3.14. The other five bodies did not change. Punching was not measured here.

**Decision:** nothing trained. Phase B waits for the owner's word on the masses.

### The owner's answers, the same afternoon

- **Masses: Matt at 84 kg in the rig (85.1 kg with his gloves), everybody else as derived.** `roster.json` carries
  it; Matt and his six pair models were built again, his fingerprint exported again, the self-collision check
  passed again (trunk slimmed four times, as before). His and Zombie's policies on the final bodies, C MuJoCo
  3.5.0, 300 s: 25 episodes, no falls. The audit's only remarks are the old ones: holding a deep squat takes
  89% of Grandma's knee strength and 92% of Grandpa's.
- **Phase B starts when the other session's queue has finished** (`g1_trump`, `g1_grandma`, `g1_grandpa` were
  still to run). From Phase B until Phase D the old game's fights do not run on `master`: the plugin's MuJoCo
  3.5.0 replaces the 3.14 library the old binding needs. The tag `pre-retrofit` is the working game.
- Seen in the other session's `logs/queue.log`, not acted on: `g1_nick` and `p1_matt` ended with
  "Warp CUDA error 719: unspecified launch failure" (`g1_nick` at 0.59 of its 0.6 hours).

## 2026-10-02: walk and turn clips (CPU only, while the other session's queue ran)

`tools/retarget_clips.py` fetches 20 trials of the CMU motion-capture database (free for research and
commercial use; B. Hahne's BVH conversion, from a mirror) and writes `training/clips/walk_turn.npz`: 271 s at
50 frames a second, the 21 joint angles and the pelvis's path in leg lengths, one file for every boxer.

| What | Trials | Seconds |
|---|---|---|
| walk, 1.1 to 1.7 m/s on Matt's legs | 07_01, 08_01, 16_15, 35_01 | 12 |
| slow walk, 0.6 to 1.0 m/s; one that stops | 69_01, 07_04, 16_33 | 10 |
| veer left, veer right | 16_11, 16_13 | 8 |
| 90-degree turn, left and right | 16_17, 69_24, 16_19, 69_20 | 16 |
| turn in place, both ways (a full turn each) | 69_16, 69_18 | 18 |
| walk and turn, repeated; walk, turn in place | 69_06, 69_13 | 83 |
| walk backwards and turn | 69_34 | 40 |
| walk sideways and turn, both ways | 69_42, 69_48 | 84 |

Checked on Matt's body with MuJoCo's own kinematics: the thighs, shins and upper arms point where the
capture's do to 0.01 degree (the capture's knee and elbow are hinges, like ours), and the check fails by 11
to 170 degrees when the sign of a hip, knee or shoulder angle is flipped. Up to 1.1% of joint samples were
outside the boxers' ranges and were clipped. The lower foot is 4 degrees toes-down at the median in a walk.

Two things to know when the style reward is written. The capture walks with its knees a little bent (never
straighter than 16 degrees in 16_17), which suits a boxer. Its arms hang and swing; a boxer's are in the
guard, so the reward should look at the legs, the pelvis and the trunk only. The ankle's roll is left at zero.

## 2026-10-02: the footwork stage, written and tried on the CPU (the GPU was still the other session's)

`training/envs/footwork.py` is rungs R0 to R2 as one stage on the solo model: an episode is a stand, a walk
to a commanded velocity, or a turn to face a stand-in opponent anywhere round the boxer. It is a new file
on top of `envs/boxing.py`, which was not touched (the other session's jobs import it). The observation is
the match's 100 numbers plus the command, 103; `tools/widen_policy.py` widens an existing policy with zero
weights and checks that it still answers exactly as before.

Checked on the CPU, 6 to 16 worlds (`device="cpu"`; nothing here has run on the GPU yet):

- **Randomisation is real.** Each world has its own link masses, friction, contact softness, joint damping and
  drive gains, within 15%. The drive torques MuJoCo Warp reports match each world's own gains to 0.0000 N m and
  are up to 16 N m from what the file's gains would give.
- **Cubes.** A waiting cube is never more than 2.5 mm from its parking place (it falls for one control step
  and is put back); cubes in play reach the boxer (nearest 0.27 m from the pelvis centre).
- **Shoves** reach 296 N for 0.1 s, the top of the 10 to 30 N s range.
- **The guard held with a zero action falls in 1.5 s** with nothing knocking it. That is the "zero brain" of
  Phase B: Unity and C MuJoCo can be compared over the first second of it, not over five.
- **Matt's current match policy, widened, as the warm start** (16 worlds, 10 s, no exploration noise):

  | | Stand | Walk | Turn |
  |---|---|---|---|
  | Nothing knocking it, the file's body | 0.00 falls an episode | 0.00; asked 0.53 m/s, did 0.05 | 0.90 falls; faced the stand-in in 3 s in 10% |
  | Shoves, cubes, randomised body | 0.60 | 0.36; asked 0.51, did 0.10 | 1.00; 0% |

  So he stands when left alone, does not yet listen to the command (its weights are zero), falls when the
  opponent is not in front of him (known: he has never had one behind him), and is knocked over by the
  shoves and cubes. These are the numbers R0 to R2 start from. Sixteen worlds is a small sample.

**Not done:** `train_box.py` does not know the stage yet (it is shared with the running queue and waits for
it to end); nothing has been trained; the style reward's judge (the discriminator) is not written, so the
`style` term pays nothing. The reward weights are first guesses.

## 2026-10-02: the footwork exam and the reference recording, in plain C MuJoCo (CPU)

`training/tools/footwork_c.py` is the footwork stage for one world in plain C MuJoCo: the exam for R0 to R2,
the recording Unity will be checked against, and the template for the C# observation.

- **The 103 numbers are the same in both.** `--check-env` puts the Warp stage and the C runner in the same
  state: the observations agree to 2.5e-07. After one to five steps of the same random actions they are 0.01
  to 0.09 apart (and a foot-contact flag flips), which is Warp against C, not the observation.
- **Baseline exam, Matt's widened match policy** (C MuJoCo 3.5.0, 30 episodes of each kind, no noise):

  | Line | Result | Mark | |
  |---|---|---|---|
  | Stand, 20 s under shoves and cubes | up at the end in 40% | 95% | fail |
  | Walk, 0.3 to 1.0 m/s | velocity off by 0.68 m/s; no falls | 0.15 m/s; 0.2 falls a minute | fail |
  | Turn to face, 3 s | 37% | 90% | fail |
  | Joint speed | fastest joint at 0.40 of its limit | 1.00 | pass |

  This is where R0 to R2 start. (The 10-episode run before it gave 50%, 0.60 and 40%.)
- **Zero-action reference for Phase B:** `training/models/v2/matt_reference_hold.json`, 5 s from the keyframe
  in MuJoCo 3.5.0, a row a control step (observation, action, targets, torques, root and joint state, foot
  contacts) and the one cube thrown at 2.0 s as an event. The guard holds itself: pelvis 0.953 m at the start,
  0.946 m at 1 s, 0.944 m at 2 s; the cube knocks it over and it is down at 4.58 s.

## 2026-10-02: the judge of the walk (CPU)

`training/style.py`: a small network that learns to tell the clips' moments from the boxer's own, and pays
the boxer for the ones it takes for a clip's. A moment is the legs and the trunk (15 joint angles and
speeds), the pelvis's height and velocity in leg lengths, its turn rate, and which way is down: 40 numbers,
no arms. The policy gets no new input. `python style.py` is its check:

- 13,544 moments come from the 20 clips on Matt's body. The stage, put in a clip's state, reads the same
  40 numbers to 1.3e-07 (`FootworkEnv.style_features`).
- After 240 updates the judge pays a clip's moment 1.00 and a boxer standing in its guard 0.01.
- A first version could not learn at all (it paid both 0.75): its smoothness penalty was taken in metres and
  radians, where a number that hardly varies, such as which way is down, was the whole penalty. It is now
  taken in the scaled numbers the network reads.

Not yet in the training loop (`train_box.py` waits for the other session's queue), so untried on the GPU and
its weight against the other rewards is not chosen.

## 2026-10-02, 17:00: the other session's queue ended; first run of the footwork stage on the GPU

The other session's last gauntlet (`g1_grandpa`) ended at 16:53 and its queue reported "nothing queued"; it
then started its own exam on the CPU. The GPU was free.

`train_box.py` now knows the stage (`--stage footwork`, with `--walk-share`, `--turn-share`, `--disturb`,
`--randomise`, `--style-w`), runs the judge beside PPO and keeps its state in the checkpoint, and exports a
footwork policy as a fixed-batch graph (`ppo.export_onnx(..., fixed_batch=True)`; other stages as before).

**Smoke run** `smoke_footwork` (deleted afterwards): Matt, from his widened match policy
(`checkpoints/r0_start/latest_matt.pt`), 4,096 worlds, 40 iterations, 48 s, style weight 0.3, noise 0.30.

| | Iteration 10 | Iteration 30 |
|---|---|---|
| Falls an episode: stand / walk / turn | 0.71 / 0.14 / 0.88 | 0.52 / 0.10 / 0.74 |
| Walk: asked, did, off by (m/s) | 0.50, 0.32, 0.59 | 0.48, 0.27, 0.54 |
| Faced the stand-in in 3 s | 12% | 26% |
| Style paid a step | 0.022 | 0.016 |

- **Throughput: about 77,000 steps a second** on the RTX 5070 Ti with the editor open and idle (the old
  laptop did 48,000 to 59,000 fighter-steps on the match).
- It runs, saves, and the numbers move the right way inside a minute. Nothing more is claimed: 40 iterations.
- The first update's KL was 5.5 (the observation scaling meets a new distribution of states); the adaptive
  rate dropped to 2e-05 and recovered. Worth a look in the first real run: a few iterations with the policy
  frozen while the scaling settles would avoid it.
- The exported graph: opset 17, input `obs` 1 x 103, outputs `actions` 1 x 21 and `value` 1 x 1.
- Run folders of the retrofit carry `REJECTED.txt` so the old game's importer passes them over.

**Decision:** the trainer side of C0 is complete. No real training until Phase B's zero-brain parity passes.

## 2026-10-02, 17:09: the gauntlet session's results, and its queue is stopped for good

Written by the gauntlet session (the "other session" of the entries above) for whoever runs C7.

- **The GPU is free.** That session started a second round at 17:04 (`g2_nick`), not knowing of the retrofit,
  and stopped it at 17:09 when it read `tasks.md`. Its queue runner has exited, its viewer is closed, the
  remaining `g2_*` jobs are out of `training/logs/queue.json`. `checkpoints/g2_nick` and `checkpoints/p1_matt`
  carry `REJECTED.txt`. TensorBoard (port 6006, `training/logs/tb`) was left running; it holds `g1_*` and
  `g1_zombie_old_laptop` (Zombie's earlier gauntlet, written in from its CSV for comparison).
- **Warm starts for the boxing rungs:** `training/logs/policies.json` names them: `checkpoints/g1_<name>/latest_<name>.pt`
  for Matt, Nick, Lil Matt, Trump, Grandma and Grandpa, `checkpoints/handoff/latest_zombie.pt` for Zombie. Nick's
  is from iteration 650 of about 690 (a GPU fault ended his run 30 s early).
- **Exam of all seven on the old bodies** (`training/handoff/exam_1.log`, C MuJoCo 3.14.1): 0 of 7 pass. Fails:
  footing Matt (3.11 a minute v Grandpa), Lil Matt, Nick, Zombie (about 1); carrying on for six of seven; guard
  Lil Matt; chin Trump and Lil Matt. Report: `DOCS/reports/2026-10-02-training-gauntlets.html`.
- **Three things measured that a gauntlet on the new bodies should expect.**
  1. A policy can pass in training and fail without noise. Matt against Grandpa, 30 twelve-second episodes in
     C MuJoCo: 0 falls with noise 0.18, 1 at 0.08, 12 at 0.04, 19 at 0. Check without noise before a run is called done.
  2. Resetting the noise to 0.04 on a policy trained at 0.18 gave a first-update KL of 256 and the rate went to
     its floor (1e-05); 100 iterations at that noise changed nothing in the exam.
  3. Doing the boxers one after another moves the opponents under those already measured: Zombie passed all six
     lines against the league's policies and fails footing against the gauntlet-trained Nick (0.89 a minute).
- **Two GPU faults** (nvlddmkm event 13, "SKEDCHECK22_INVALIDATE_ACTIVE_QMD failed") at 14:21:56 and 14:27:27,
  each killing the run on the GPU; none in the 70 minutes before or the 2.5 hours after. The GPU was at 86 to
  89 °C with the thermal-throttle flag set throughout, drawing 40 to 75 W of 140. Cause not established.

## 2026-10-02, evening: Phase B for Matt. The plugin, the testbed, and the zero-brain parity test

No training. Unity 6000.6.0f1, `org.mujoco` 3.5.0 (embedded in `Packages/org.mujoco`), `bin.mujoco` 3.5.0.

**What had to be changed in the plugin** (each marked `PoBox:` in its source):
- `MjMeshFilter.cs`: `Object.GetInstanceID()` is an error in Unity 6000.6 (it was only in a mesh's debug name).
- `MjGlobalSettings.cs`: the plugin wrote a `passive` flag that MuJoCo 3.5.0 refuses, so no scene with global
  settings would load; and it knew neither `ls_iterations` nor the `eulerdamp` flag, both of which the trainer sets.

**The model is the trainer's.** `ModelFingerprint.Differences` (Unity) against `models/v2/matt_fingerprint.json`:
0 differences at 1e-6, by name: sizes, solver options, every body's mass, inertia tensor and centre of mass,
every joint's axis, range, damping and armature, every shape, every drive's gains and limits and its place in
the order, the 11 excluded pairs. Before the plugin was patched there were two (`ls_iterations` 50 for 10, and
the `eulerdamp` flag).

**Zero-brain parity** (`MjParityTests`, play mode; the policy file that always answers zero, a cube at 2 s),
Unity against `matt_reference_hold.json` from C MuJoCo 3.5.0, 250 control steps:

| Second | Pelvis height | A joint | A torque | An observation |
|---|---|---|---|---|
| 1 | 0.000 mm | 5.4e-08 rad | 7.3e-05 N m | 2.5e-06 |
| 2 | 0.000 mm | 9.6e-08 rad | 3.0e-05 N m | 1.1e-07 |
| 3 (cube lands) | 0.000 mm | 2.0e-07 rad | 9.9e-05 N m | 1.4e-06 |
| 4 | 0.000 mm | 6.3e-07 rad | 2.7e-04 N m | 3.6e-06 |
| 5 (down at 4.58 s) | 0.009 mm | 1.8e-05 rad | 3.0e-03 N m | 6.0e-04 |

The first observation differs by 6.0e-08. The testbed scene holds no Rigidbody, ArticulationBody, Collider or
Joint, and Unity's physics is set to step only when a script asks (none does).

**Three things found getting there, each of which would have cost a rung of training:**
1. **The plugin overwrites `mjData.ctrl` after every step** with each `MjActuator`'s own `Control` field. The
   policy's targets lasted one physics step in four and the boxer folded to a T-pose: joints 1.9 rad out inside
   the first control step. `MjTestbed` writes the targets again before every step.
2. **`mj_step` leaves `xpos` and `geom_xpos` one step behind `qpos`.** The Warp stage brings them up to date
   before it observes (its forward after the step); `tools/footwork_c.py`, like `tools/eval_cmujoco.py` before it,
   did not, and neither would Unity have. With `mj_kinematics` after the step (and the stand-in's velocity taken
   before it, as the stage does), the Warp stage and the C runner agree to 4e-06 to 1e-04 after one to five steps
   of the same random actions, where they were 1e-02 to 1e+00 apart. The old game's fall-rate gap between Warp
   and C had this in it.
3. **The config's guard pose was not the keyframe's**: the model's keyframe is written to four places, the
   config's `default_joint_pos` and `stand_height` were not, 5e-05 rad apart. `rig_to_mjcf.py` now writes both
   to four places; the v2 configs were rebuilt (the models themselves did not change).

Also: Unity keeps its fixed step as 0.004999993 s and the plugin hands that to MuJoCo; `MjTestbed` sets it to
1/200 exactly once the model exists.

**Decision:** B1 to B3, B5 to B9 are done for Matt. The HUD (B4), the other six boxers (B10) and the report
(B11) are next; no training before them.

## 2026-10-02, late evening: Phase B complete

- **All seven boxers** are prefabs (`Assets/Boxers/NAME`); `MjRetrofit.CheckAll` puts each in the testbed's world
  in turn: 0 differences from the trainer's fingerprint for every one of them.
- **The HUD** (`Assets/UI/Testbed.uxml`, `TestbedHud`): title, telemetry, behaviour selector, reset, shoves, cube,
  version. Before and after: `DOCS/reports/2026-10-02-retrofit-testbed.html`.
- **Cost in the editor** with the zero policy: 0.12 ms a physics step (MuJoCo and the plugin's copy to the
  transforms), 0.5 ms a control step for the observation and the policy on the CPU, about 220 frames a second.
  At 200 physics steps and 50 control steps a second that is about 50 ms of each second. Not measured on a phone.
- The three parity tests pass with the HUD in the scene.
- Not done: the camera does not follow the boxer; the other six cannot be picked on the testbed's screen.

**Decision:** Phase C starts with R0 and R1 for Matt (`--stage footwork` from `checkpoints/r0_start/latest_matt.pt`),
then the gate C3 in Unity. Notes from the gauntlet session's entry above apply: check a policy without its noise
before calling a rung done, and do not drop the noise sharply on a resume.

## 2026-10-02, 18:11 to 18:41: R0 for Matt, `r0_matt` (stand)

`train_box.py --stage footwork --xml models/v2/matt_solo.xml --resume checkpoints/r0_start/latest_matt.pt
--walk-share 0 --turn-share 0 --disturb 1.0 --randomise 0.15 --episode-s 20 --reset-std 0.3 --max-std 0.35
--lr 5e-4 --lr-max 1e-3 --max-hours 0.5`. 4,096 worlds, 1,193 iterations, 117 million steps. About 95,000
steps a second with the Unity editor closed, 62,000 to 70,000 once the owner had it opened again.

| Iteration | Minutes | Falls an episode (with noise) | Return | Noise | Power |
|---|---|---|---|---|---|
| 20 | 0.3 | 0.77 | 77 | 0.30 | 1,165 W |
| 50 | 0.9 | 0.04 | 554 | 0.29 | 891 W |
| 100 | 2.0 | 0.02 | 828 | 0.28 | 764 W |
| 300 | 6.6 | 0.00 | 1,152 | 0.24 | 490 W |
| 600 | 14.2 | 0.00 | 1,431 | 0.20 | 298 W |
| 1,192 | 30.0 | 0.01 | 1,654 | 0.16 | 136 W |

**Exam, no noise, C MuJoCo 3.5.0** (`tools/footwork_c.py --exam`; stand line: up at 20 s under shoves of 10 to
30 N s and cubes at 5 m/s, mark 95%):

| Checkpoint | Episodes | Stand |
|---|---|---|
| before (widened match policy) | 30 | 40% |
| iteration about 220 | 30 | 83% |
| iteration about 850 | 50 | 100% |
| final, iteration 1,193 | 100 | **94%: one episode short of the mark** |

Joint speed passes (fastest joint at 0.28 of its limit). Walk and turn fail, as they must: neither was trained.

**Reading.** Standing under the knocks is learned in the first minute and the rest is polish; the noise-free
exam lags the training number, as the gauntlet session warned. 94 and 100 of two samples are the same policy
within chance: it is at the mark, not clear of it. The power fell tenfold: it has learned to stand quietly.

**Decision.** Not called passed. R1 keeps 40% of its episodes as stands under the same knocks, so R0 goes on
being trained, and both lines are examined again after it. The tracking reward was changed before R1: one width
of 0.25 m/s was flat at a standstill when 1 m/s is asked (exp(-16)); it is now half at 0.5 m/s and half at 0.2.

## 2026-10-02, 18:43 to 18:52: R1 for Matt, first attempt (`r1_matt_a`, stopped)

From `r0_matt`, 60% walk episodes, style weight 0.3. Two things went wrong.

1. **A GPU driver fault** killed it after half a minute (`CUDA error: unspecified launch failure`; the system log
   has nvlddmkm events 13 and 153 at 18:43:59, the GPU at 85 C). Not the code: the gauntlet session had the
   same event twice. `tools/train_resilient.py` now starts a run again from its last save (every 50 iterations).
2. **It did not learn to walk.** In 120 iterations (3 minutes) it went 0.21 m/s when 0.49 was asked, the error
   flat at 0.53 m/s. The judge of the walk separated the clips from the boxer completely within a minute (+0.98
   against -1.00), so the style reward was zero: it can only help once the boxer moves something like a walker.
   What was missing was a reason to lift a foot: standing is what the boxer knows, and sliding a foot is charged.

**Decision.** Stopped and set aside (`checkpoints/r1_matt_a`, curves in `logs/tb_archive/r1_matt_a`). A step
reward was added to the stage: when a walk is asked for, each foot that lands is paid for the time it was up
beyond a quarter of a second (`steps`). R1 started again from `r0_matt`, to iteration 2,900 (about 40 minutes).

## 2026-10-02, 19:00 to 19:31: R1 for Matt (`r1_matt`, the fourth attempt) and the gate

**Why the first three attempts stood still.** The per-step reward terms (TensorBoard `rt_*`) gave it away: when
a walk began, the energy charge was 0.43 a step and the slip and lean charges 0.1 to 0.2 more, against at most 0.8
to be gained by tracking the command well. A clumsy first walk cost more than it earned, so the policy learned to
stand. Starting a walk mid-stride from the clips (third attempt, `--rsi 0.5`) did not change that on its own.
Fourth attempt: when a walk is asked for, tracking pays double, and energy, slip and lean are charged at 30%.

`train_resilient.py ... --stage footwork --resume checkpoints/r0_matt/latest.pt --run-name r1_matt --walk-share 0.6
--rsi 0.5 --style-w 0.3 --reset-std 0.3 --max-std 0.35`, stopped at iteration 1,950 (20 minutes) once it passed.

| Minutes | Walk asked | Walk done | Error (with noise) |
|---|---|---|---|
| 0 | 0.50 | 0.46 (mid-stride starts) | 0.54 |
| 4 | 0.48 | 0.20 | 0.48 |
| 8 | 0.49 | 0.32 | 0.34 |
| 10 | 0.50 | 0.43 | 0.25 |
| 20 | 0.50 | 0.48 | 0.14 |

**Exam, no noise, C MuJoCo 3.5.0, 100 episodes of each kind:** stand **95%** (mark 95%, pass), walk **off by 0.02
m/s** with **no falls** (marks 0.15 and 0.2 a minute, pass), joints at 0.33 of their limits (pass). Turn 30%: not
yet trained. R0 and R1 are passed.

**The gate (C3), Unity against C MuJoCo 3.5.0** (`MjGateTests`, with `checkpoints/r1_matt/latest.onnx` on Matt's
prefab via `MjRetrofit.Promote`):

| Test | Unity | C MuJoCo | Mark | |
|---|---|---|---|---|
| Replay of 250 recorded observations: largest difference in an action | 6.7e-06 | | 1e-04 | pass |
| Closed loop, 5 s of walking at 0.6 m/s: on its feet | all 5 s | all 5 s | the same | pass |
| cadence | 5.20 footfalls a second | 5.40 | within 10% | pass |
| RMS joint torque | 51.34 N m | 51.16 N m | within 15% | pass |
| ground covered | 3.112 m | 3.109 m | | |
| 25 episodes from C's own starts, a 20 N s shove at 2 s and a 5 m/s cube at 4 s: on the floor | 0% | 0% | within 0.05 | pass |
| the same ending | 25 of 25 | | | |

The joints of the closed-loop runs part by 0.05 rad at 3.2 s: a walking body is chaotic, and the summaries above
are what is compared for that reason.

**Decision:** the gate is passed; later rungs may go ahead (tasks.md, the parity rule). R2 (turn) for Matt started
at 19:31 from `r1_matt`.

## 2026-10-02 evening: R2 for Matt, the first footwork for another body (Zombie), and R2b

**R2 (`r2_matt`, 1950 to 3150 from `r1_matt`, 40% walks, 30% turns).** In training it faced the stand-in within
3 s in 87 to 94% of turn episodes (with exploration noise). **Exam, no noise, C MuJoCo 3.5.0, 30 of each kind:**
stand 100% (pass), walk off by 0.02 m/s with no falls (pass), joints at 0.40 of their limits (pass), **turn 73%
(mark 90%, FAIL).**

Why: 60 turns sorted by their starting angle (scratchpad `turn_diag.py`) show it never falls and nearly always
swings round, but settles 15 to 30 degrees off and drifts there. The facing term was `0.6 * cos(off)`: at 20
degrees off that pays 0.94 of the most it can, so precision was worth almost nothing.

**Change:** `envs/footwork.py`, face = weight x (cos(off) + exp(-(off/0.2)^2)): the cosine is still felt from
behind; the narrow term costs about half of the face reward at 20 degrees off and nothing at 0.

**R2b (`r2b_matt`, 3150 to 3950 from `r2_matt`), queued after `f_zombie`;** the five footwork jobs not yet started
were pointed at it. (Stopping `f_zombie`, already running on the old reward, was not allowed, so it finished.)

**Zombie (`f_zombie`, 1600 iterations from `r2_matt` onto Zombie's body, old reward).** Exam, C MuJoCo 3.5.0:
stand 100%, walk off by 0.01 m/s with no falls, **turn 93%**, joints 0.38: **R0, R1 and R2 passed.** Its old
get-up policy on the new body: 30% (mark 80%): the queued `u1_matt_zombie` is for that.
**The gate in Unity** (promoted with `MjRetrofit.Promote("zombie", "f_zombie")`; the gate tests now run for every
boxer with recordings): replay 6.7e-06; closed loop on its feet the whole 5 s in both, 3.60 against 3.60 footfalls
a second, RMS torque 57.23 against 57.25 N m, 2.980 m against 2.980 m; 25 shove-and-cube episodes 0% against 0%
on the floor, the same ending in 25 of 25. **Passed.**

Also: R2b ran at 12,000 rather than 60,000 samples a second while a game (Age of Empires IV) held the GPU.

**R2b result (22:06).** Exam, C MuJoCo 3.5.0, 30 of each kind: stand 100%, walk off by 0.04 m/s with no falls,
**turn 100%** (from 73%), joints 0.45. **R2 passed for Matt.** Promoted (`MjRetrofit.Promote("matt", "r2b_matt")`,
with new references); the gate in Unity: replay 8.6e-06; closed loop 4.80 against 4.80 footfalls a second, RMS
torque 59.45 against 59.63 N m, 2.859 against 2.853 m; 25 episodes 0% against 0% on the floor, 25 of 25 the same.
19/19 PoBox tests. Report with the charts explained: `DOCS/reports/2026-10-02-training-turning.html`.
`f_nick` started at 22:06 from `r2b_matt`.

**Nick (`f_nick`, 1600 iterations from `r2b_matt`, 22:06 to 22:40).** Exam, C MuJoCo 3.5.0: stand 100%, walk off
by 0.01 m/s with no falls, turn 100%, joints 0.61; his old get-up policy, widened, on the new body: **100%** in 2.7 s
on average (mark 80%). R0 to R3 passed; promoted. The Unity gate is run for the five together once all are in.

**C7 baseline: the old match and get-up policies, widened to 103 inputs, on the new bodies** (`tools/exam_v2.py`,
C MuJoCo 3.5.0, 4 rounds of 45 s against each of the six others, plain and under the daze; 30 knockdowns each;
`logs/exam_v2_baseline.json`). **1 of 7 pass (Nick).**

| Boxer | Footing (falls/min, worst) | Attack (/s at m/s) | Guard (stops) | Chin (legs/min) | Get up | Carrying on |
|---|---|---|---|---|---|---|
| Grandma | **1.33** | 1.03 at 5.1 | 74% | 0.00 | 25/30 | **22/25** |
| Grandpa | **1.67** | 1.13 at 5.4 | 84% | 0.00 | 21/30 | **16/21** |
| Lil Matt | 0.00 | 0.85 at 6.2 | **13%** | 1.11 | 30/30 | **21/30** |
| Matt | **1.67** | 1.19 at 5.7 | 51% | 0.44 | **7/30** | **2/7** |
| Nick | 0.00 | 1.65 at 7.3 | 48% | 0.00 | 30/30 | 29/30 |
| Trump | **1.33** | 0.52 at 6.4 | 38% | **1.78** | 30/30 | **25/30** |
| Zombie | **1.67** | **0.33** at 5.7 | 95% | 0.00 | **22/30** | **18/22** |

Marks: footing 0.2, attack 0.5/s at 4 m/s x speed, guard 30% or under 0.3 head shots/s, chin 1.5, get up 80% in
8 s (60% in 9 s for the grandparents), carrying on 90%. Bold fails. The match runs (`m1_*`, 700 iterations each
against the frozen others) and get-up runs (`u1_*`) in the queue start from these.

**Lil Matt (`f_lilmatt`, 22:40 to 23:10).** Stand 100%, walk off by 0.02 m/s with no falls, turn 100%, joints 0.36;
old get-up policy on the new body 100% in 2.5 s. R0 to R3 passed. (Promotion and the Unity gate wait for the end of
the night's training: the editor is closed while the GPU trains.)

**Trump (`f_trump`, 23:10 to 23:42).** Stand 100%, walk off by 0.02 m/s with no falls, turn 100%, joints 0.38; old
get-up policy 97% in 3.2 s. R0 to R3 passed.

**Grandma (`f_grandma`, 23:44 to 00:14).** Stand 97%, walk off by 0.02 m/s with no falls, joints 0.58, old get-up
policy 77% (her mark 60%): pass. **Turn 83% (mark 90%): FAIL.** Turns alone from 60 starts: 59 faced in time, one
fall. The exam again with 90 episodes of each kind: turn **90%**, exactly the mark: borderline, not luck. Queued
`f2_grandma` after Grandpa: 600 more iterations from `f_grandma` with 45% turns (from 30%).

**Grandpa (`f_grandpa`, 00:16 to 00:47).** Stand 97%, walk off by 0.02 m/s with no falls, turn 97%, joints 0.66:
R0 to R2 passed. His old get-up policy on the new body: **27%** (mark 60%): for `u1_grandpa_matt`.

**Grandma top-up (`f2_grandma`, 1600 to 2200, 45% turns, 00:48 to 00:59).** Exam on 90 episodes of each kind:
stand 98%, walk off by 0.01 m/s with no falls, **turn 99%** (from 90%), joints 0.59, get-up 78% (mark 60%). Passed.

**All seven pass R0 to R2 in C MuJoCo 3.5.0** (Matt `r2b_matt`, Zombie, Nick, Lil Matt, Trump, Grandpa `f_<name>`,
Grandma `f2_grandma`). Promotion and the Unity gate for the five not yet in Unity wait for the end of the night.
The match runs (`m1_*`) began at 00:59.

**Matt, match (`m1_matt`, 700 iterations from the widened old policy against the six others frozen, 01:02 to 01:30).**
Exam (`tools/exam_v2.py`): attack **1.86 a second at 7.3 m/s** (from 1.19 at 5.7), all to the body; guard stops 33%,
0.01 head shots taken a second; chin 0.00. **Footing 1.67 unprovoked falls a minute against Grandma, 1.33 against
Zombie** (none against the other four): FAIL. Getting up 7 of 30 (old get-up policy; `u1_matt_zombie` to come),
carrying on 3 of 7: FAIL. A second match round with a heavier fall charge (`--fall-penalty`, 4 now) is the lever
for footing if it fails across the seven.

**Zombie, match (`m1_zombie`, 01:33 to 02:02).** Footing 0.00 falls a minute; attack **1.30 a second at 6.9 m/s** (from
0.33), 83% to the head; guard stops 80%; chin 0.00: R4 to R6 and footing pass. Getting up 18 of 30 and carrying on
11 of 18 (old get-up policy): for `u1_matt_zombie`. Against the new Matt he lands nothing (Matt lands 1.72 a second on
him, all to the body).

**Lil Matt, match (`m1_lilmatt`, 02:06 to 02:34).** Footing 0.00; attack 0.93 a second at 6.8 m/s; **guard stops 70%**
(from 13%); chin 1.44 legs a minute (mark 1.5; 8.67 against Zombie, who lands 2.22 head shots a second on him); getting
up 30 of 30 in 1.6 s. **Carrying on 21 of 30** (mark 90%): FAIL, the only line.

**Trump, match (`m1_trump`, 02:37 to 03:05).** Attack 0.81 a second at 6.8 m/s, 85% to the head; getting up 30 of 30.
FAIL: **guard stops 26%** (0.85 head shots taken a second; 2.32 against Nick), **chin 1.72** legs a minute (10.33 against
Nick), **footing 0.33** a minute (all against Nick), carrying on 26 of 30. The match line trains with no pay for blocks
(`--block-w 0`, the default); a second round for him should pay for them.

**Grandma, match (`m1_grandma`, 03:07 to 03:35).** Attack 1.03 a second at 5.8 m/s (her mark 3.0); guard stops 69%; chin
0.00; getting up 22 of 30 (mark 60% in 9 s). FAIL: **footing 1.33 a minute, all against Nick**; carrying on 11 of 22.
Nick is the one boxer the others fall against unprovoked (Trump 0.33, Grandma 1.33, all of it against him): he
fights at close quarters, and a fall from being bumped is not a fall from a punch in the exam's count.

**m1_nick did not train (02:02 to 02:03):** a GPU fault (CUDA 719) a minute in, before its first save, and
`train_resilient.py` gave up for want of a save. It now starts such a run again from the beginning; `m1_nick` is back
in the queue, after `m1_grandpa`.

**Grandpa, match (`m1_grandpa`, 03:38 to 04:06).** Footing 0.00 (from 1.67); attack 0.63 a second at 3.7 m/s (his mark
3.0); guard stops 71%; chin 0.44; getting up 18 of 30 (exactly his 60%). **Carrying on 15 of 18**: FAIL, the only line.
`m1_nick` started again at 04:06 and is past its first save.

**Nick, match (`m1_nick`, 04:06 to 04:37, after the restart).** Attack 1.23 a second at 7.2 m/s; guard stops 64%; chin 0.00;
getting up 28 of 30. FAIL: footing 1.33 (all against the new Zombie), carrying on 21 of 28. He passed everything in the
baseline, but against the old policies: each boxer's exam has met whichever of the others were trained by then, so a
full exam of all seven on their new match policies is running (`logs/exam_v2_after_m1.json`).

**Queue extended (04:45):** the get-up runs train at 107,000 samples a second, about 20 minutes each, so the queue
would have emptied by 06:00. Appended: a v2 handover bank for each boxer from tonight's get-up policies
(`tools/make_handover_v2.py`), then a second match round `m2_<name>` (500 iterations against the others' m1 policies,
30% of episodes begun from the handover bank, for carrying on; Trump also paid for blocks, `--block-w 0.3`), in the
order Matt, Trump, Grandma, Nick, Lil Matt, Grandpa, Zombie. The queue is held at 06:50, eight hours after it began.

**All seven on their m1 policies, like for like (04:52, `logs/exam_v2_after_m1.json`, getting up not tested here):**
**2 of 7 pass footing, attack, guard and chin: Grandma and Zombie.** Footing fails for Grandpa (2.00 a minute), Matt
(1.67) and Nick (1.33); chin for Lil Matt (2.72) and Trump (2.28); guard for Trump (24%). Matt stops only 17% but takes
0.03 head shots a second, which passes. The second round now charges 8 for a fall (from 4) for Matt, Nick and Grandpa,
and runs Matt, Trump, Grandpa, Nick, Lil Matt, Grandma, Zombie.

**Get up, Matt and Zombie (`u1_matt_zombie`, 1500 iterations on the v2 get-up stage, 04:37 to 05:01).** Matt **30 of 30
knockdowns** in 1.5 s (from 7 of 30), carrying on 21 of 30: FAIL (for `m2_matt`). Zombie **30 of 30** in 1.8 s (from
18), **carrying on 27 of 30: pass.** With his match exam, **Zombie passes every rung, R0 to R7.**

**Get up, Nick and Lil Matt (`u1_nick_lilmatt`, 05:01 to 05:23).** Nick 30 of 30 in 1.4 s, **carrying on 28 of 30: pass**.
Lil Matt 29 of 30 in 1.5 s, carrying on 23 of 29: FAIL.

**Get up, Trump and Grandma (`u1_trump_grandma`, 05:23 to 05:46).** Trump 30 of 30 in 1.7 s, carrying on 25 of 30; Grandma
30 of 30 in 2.2 s (from 22), carrying on 20 of 30: getting up passes for both, carrying on fails for both.

**Get up, Grandpa (`u1_grandpa_matt`, Matt frozen, 05:46 to 06:05).** 30 of 30 in 2.2 s (from 18), carrying on 26 of 30.

**R3 passes for all seven: 30 of 30 knockdowns each** (29 for Lil Matt), 1.4 to 2.2 s on average. Carrying on (the
match policy taking the body back) passes for Zombie and Nick and fails for the other five (20 to 26 of 30): the
handover banks (`hb_*`, from 06:05) and the second match round are for that.

**Matt, second match round (`m2_matt`, 500 iterations from `m1_matt`, fall charge 8, 30% of episodes from his handover
bank, 06:10 to 06:32).** **Footing 0.00 falls a minute** (from 1.67): pass. Attack 1.99 a second at 7.2 m/s, every punch to
the body; head shots taken 0.06 a second (guard passes on that); chin 0.00; getting up 30 of 30. Carrying on 23 of 30
(from 21): still short of 27.

**Trump, second match round (`m2_trump`, 500 iterations, blocks paid 0.3, from his handover bank, 06:32 to 06:56).** **Footing
0.00** (from 0.33): pass; attack 1.03 a second at 8.4 m/s; getting up 30 of 30. FAIL: **guard 15%** (from 24%, worse with
blocks paid: he trades rather than covers, 2.11 head shots a second taken from Nick), chin 2.50 legs a minute (11.00
against Nick), carrying on 26 of 30. His guard needs more than a payment: next time a stage of its own, or Nick held
back as a sparring partner until it holds.

**The night ends here (06:56): the queue is held** (`logs/queue.hold`); `m2_grandpa`, `m2_nick`, `m2_lilmatt`, `m2_grandma`
and `m2_zombie` are queued behind it.

**Into Unity (07:15 to 07:40).** Footwork promoted for Nick, Lil Matt, Trump, Grandma (`f2_grandma`) and Grandpa; match
(`m2_matt`, `m2_trump`, `m1_*` for the rest) and get-up (`u1_*`, exported at 103 inputs) for all seven
(`MjRetrofit.PromoteBoxing`). **The gate passes for all seven** (33 of 34 tests at first: Nick's 5 s walk had 21 footfalls
against 19, 10.5%; on a 10 s reference 4.10 against 3.80 a second, and the test passes). Arena spot check, Matt v
Zombie on their match policies, two minutes: both up throughout, Matt lands 206 of 227 (all to the body), Zombie lands
none, as in C MuJoCo: a one-sided pairing, for the next round.

**Nick, second match round (`m2_nick`, 400 iterations, fall charge 8, from his handover bank, 10:15 to 10:41).** Exam
against the five left in the game: **footing 0.00** (from 1.33), attack 1.38 a second at 6.8 m/s, guard 66%, chin 0.00,
getting up 30 of 30, carrying on 29 of 30: **every line passes. Nick passes the whole ladder**, the second after Zombie.
Promoted (match.onnx from `m2_nick`).

**Grandma, second match round (`m2_grandma`, 400 iterations, from her handover bank, 10:41 to 11:01).** Carrying on **24 of
29** (from 20 of 30), attack 1.09 a second (from 0.90), guard 73% (from 65%), footing and chin 0.00, getting up 29 of 30:
better on every line, carrying on still short of 90%. Promoted. The queue is held again (11:01); `m2_grandpa` and
`m2_zombie` wait behind it. Abilities grid: https://claude.ai/artifact/1HfxM4Uhtt9LtAikeP197A

## 2026-10-03 afternoon: five changes to the trainer from an audit (no training run yet)

Code only; nothing was trained and nothing was promoted. The queue is still held.

**On for every run from now on (the held `m2_grandpa` and `m2_zombie` included):**

- **The critic's loss is no longer clipped, and the actor and the critic are clipped apart** (`ppo.py`). Rewards are
  about one a step, so a state is worth 50 to 100; the clipped loss held the critic within 0.2 of its last answer, and
  one gradient clip over both networks let a large value gradient shrink the policy's step. Suspected in `defend_a`
  (value loss up tenfold, noise walking up). Not yet measured: compare `value_loss` and `action_std` on the next run.
- **Sensor noise is in each number's own units** (`envs/boxing.py`, `noise_scale`): 0.005 on gravity, 0.01 on joint
  angles, 0.3 rad/s on joint speeds, none on the last action and the feet. It was 0.02 on all hundred.
- **`tools/queue_runner.py` waits while the GPU is 25% busy with something else** (the editor in Play mode, a game).

**Off until asked for, per boxer** (`--speed-limit`, `--fatigue-j 30000` when training; `tools/set_rules.py` writes
them into the configs under `models/v2`, which is where the exam reads them and what promoting copies to the game):

- **Speed limit:** each joint is damped by its force limit over its speed limit, so a drive pulling its hardest has
  nothing left at the limit. In the Warp stages, `tools/footwork_c.py`, `tools/exam.py` and `MjBoxer.Bind`. A first
  version that held the joint target back once a control step was dropped: a forearm passes its limit inside one 20 ms
  step. CPU smoke (every joint thrown end to end, 8 rings, 3 s): 99th-percentile joint speed 2.30 of the limit without,
  1.17 with (what is left is one limb whipping another, and the floor).
  **What the warm start loses** (`r2b_matt`, C MuJoCo 3.5.0, 9 of each kind): stand 100%, walk 0.67 falls a minute
  (was 0.00, FAIL), turn 89% (was 100%, FAIL). So every policy of a boxer needs a top-up under the rule, and the Unity
  gate again, before `set_rules.py` turns it on for that boxer.
- **Fatigue (bouts only):** the drives' output comes off a reserve (30 kJ times the boxer's strength) that fills again
  over 20 s; below half, the drives weaken, to 0.6 with nothing left. Episodes begin at 0.3 to 1.0 of it. In
  `BoxingEnv.step`, `tools/exam.py` and `MjBoxer.Tire` (called by `MjRing`). The reserve is not observed (nor is the
  daze). Smoke: 8 s of thrashing leaves 0.46. Not yet trained with.

## 2026-10-03 14:30: a new Trump (the owner's `TRUMP.glb`), rigged and derived; not yet in Unity, not yet trained

The owner's new mesh had no skeleton. Rigged in Blender 5.2: scaled to the roster's 1.90 m, 24 bones with Mixamo
names placed from the mesh's own outline, automatic weights (on a copy with its seams merged, then carried back),
exported as `RIGGED_Trump.glb`; `roster.json` points at it. `build_roster.py --only trump` (rigs/v2, models/v2, 8
cubes): 109.4 kg, strength 1.10, pelvis 0.836 m in the guard (the old Trump's was near 1.0: this one has a long trunk
and short legs), weight 47% of the way from heel to toe. Self-collision: the forearms touched the chest in the guard
and the walk; clean after four trunk slimmings (torso 0.105, pelvis 0.076): no pair touches in 25 poses. Fit picture
`logs/fit_trump.png`; fingerprint and `trump_reference_hold.json` written.

**The old Trump's policies are not a warm start for this body** (`f_trump` on it, C MuJoCo 3.5.0, 9 of each kind):
stand 0%, walk 17 falls a minute, turn 56%. Footwork starts from `r2b_matt` as the others' did.

**Waiting on the owner:** the editor was in Play mode on the Arena, so nothing was imported and no run was started.
Still to do: `Assets/Models/Trump.glb`, `MjRetrofit.BuildBoxer/BuildCorners`, the arena, menu, portrait and voice;
footwork, get-up and match with `--speed-limit --fatigue-j 30000` from the first run (AGENTS.md); exam; Unity gate.

**Trump, footwork (`f_trump2`, 1600 iterations from `r2b_matt`, speed limit on from the first step, 14:36 to 15:05, about
95,000 samples a second with the editor closed).** Exam, C MuJoCo 3.5.0 under the speed limit, 30 of each kind: stand
100%, walk off by 0.02 m/s with no falls, turn 100%, joints at 0.29 of their limits (the others ran at 0.38 to 0.66
without the rule). **R0 to R2 passed at the first attempt.** In Unity: prefab and both corners built, the compiled
model equals the trainer's (0 differences), in the Arena's MatchSetup and the Testbed's roster. Get-up
(`u1_trump2_matt`, Matt frozen) and the first match round (`m3_trump`) follow. Report with the charts:
`DOCS/reports/2026-10-03-abilities-and-training.html`.

**Three options taken from mujoco_playground's G1 walking task (16:30; all off unless asked for, so the run in progress
is unchanged):**

- `--effort-free-iters N` (train_box, train_gauntlet): no charge for power, action size or action change for N
  iterations, then brought back over N more. Their humanoid is charged nothing for effort; ours, charged from the first
  step, taught Trump on the bag that standing still is cheapest (`bag_trump2`, `bag_trump3`).
- `--shove-max NS` (footwork; default 30, the exam's) and, for bouts, the `--push-vel` there already was: theirs kick
  the body by up to 2 m/s, ours by 0.1 to 0.4.
- `--start-spread X` (footwork, bag, match): episodes begin up to X times the usual 0.06 rad a joint and 0.15 m/s from
  the guard; theirs scale the joints by 0.5 to 1.5. For carrying on, beside the handover bank.

The held `m2_grandpa` and `m2_zombie` now carry `--push-vel 0.8 --start-spread 3` (footing, carrying on). CPU smoke:
joints start up to 0.18 rad out at spread 3; the three effort terms are exactly 0 at scale 0; shoves reach what is asked.

**Trump, get-up, bag and first match round (15:05 to 16:45).**

- **Get-up (`u1_trump2_matt`, 1500 iterations from the old Trump's get-up policy, Matt frozen):** could not rise at first;
  the stage's helping hand came on by itself (to 0.25 of body weight) and was gone by iteration 500. Exam: **30 of 30
  knockdowns in 1.7 s. R3 passed.**
- **`m3_trump` (from the old Trump's match policy), stopped at 415 and rejected:** it could not stand on the new body
  (down in 99% of episodes), and by the time it could it had stopped punching: 0.03 a second at 0.7 m/s, blocks 1.0 a
  second, noise falling. A boxer that relearns to stand while being hit loses its punch on the way.
- **Bag, two failures (`bag_trump2`, `bag_trump3`, from his footwork policy; the second with hits paid five times as
  much):** in two minutes glove speed fell from 1.8 to 0.4 m/s and the effort charge from -0.48 to -0.09 a step, hits to
  nothing. A policy that has never punched, charged for effort from the first step, finds that standing still is best.
  (`--effort-free-iters` was written because of this; not yet tried.)
- **Bag, third (`bag_trump4`, 700 iterations from Zombie's match policy, `--hit-w 2`, fatigue off for the stage):** down
  in every episode for the first minute and punching throughout; at the end 1.69 a second at 8.5 m/s, 1% falls.
  `train_box.py --stage v2` is the bag (or self-sparring) on the 103-number observation.
- **`m5_trump` (700 iterations from `bag_trump4`, fatigue on, 30% of episodes from his handover bank).** Exam
  (`logs/exam_m5_trump.json`): footing 0.00, guard stops 48%, chin 0.00, getting up 30 of 30: pass. **Attack 0.37 a second
  at 3.7 m/s: FAIL** (1.09 at 8.5 m/s on Zombie, 0.71 on Nick, 0.00 to 0.02 on Matt, Grandma and Grandpa). **Carrying on 23
  of 30: FAIL.**

Why the attack fails: shoulder to wrist he measures 0.44 m, the shortest in the roster (the others 0.48 to 0.54), and
his style, written for the old mesh, had him stand at 0.72 m. He lands on the two who come to him. **Changed:** range
0.72 to 0.60, press 0.25 to 0.4 (roster, rig, configs). **`m6_trump` (17:06):** 500 iterations from `m5_trump`, twice the
rings against Matt, Grandma and Grandpa, `--push-vel 0.8 --start-spread 3` (their first use).

**`m6_trump` (17:06 to 18:00; 500 iterations from `m5_trump`, range 0.60, twice the rings against Matt, Grandma and
Grandpa, `--push-vel 0.8 --start-spread 3`).** It ran at 10,000 samples a second for forty minutes while a game held the
GPU, then 40,000. Exam (`logs/exam_m6_trump.json`): footing 0.00, guard stops 55%, chin 0.00, getting up 30 of 30 in
1.7 s, **carrying on 28 of 30 (from 23): pass.** **Attack 0.37 a second at 4.3 m/s: FAIL** (1.33 at 8.1 m/s on Zombie, 0.42
on Nick, 0.02 to 0.04 on Matt, Grandma and Grandpa: no better for the shorter range). **Trump passes 8 of 9.** The wider
starts and harder shoves came with the first pass of carrying on for a boxer in one round, with the handover bank as
before, so the credit is shared; the shoves also put his opponents down in 20 to 40% of episodes, which a bout does not.

**Into Unity (18:05 to 18:30).** Promoted: `f_trump2` (with a 5 s walk reference and 25 falls episodes), `m6_trump`,
`u1_trump2_matt` (exported at 103 inputs), and his config (speed limit, fatigue). **The gate passes for all six, 18 of
18:** Trump's replay 5.7e-06; closed loop 3.60 against 3.60 footfalls a second, 64.33 against 64.30 N m, 2.943 against
2.943 m; 25 episodes 0% against 0%. Played: Testbed (stand under shoves and a cube, walk at 0.59 of 0.60 m/s, turn in
2.6 s, no falls), Arena against Zombie (119 landed to 101, a knockdown on command and up at the count of 6), Menu (his
card; rebuilt with six). Announcer clip and portrait in. Report: `DOCS/reports/2026-10-03-trump-in-the-game.html`.
Open: attack against Matt, Grandma and Grandpa; his hands show past his gloves; the menu portrait's crop.

**Grandpa, second match round, cut short (`m2_grandpa`, 19:00 to 19:09, 247 of 500 iterations: the owner asked for
training to end by 19:15).** From `m1_grandpa` against the others' current policies (Trump's `m6_trump` among them), fall
charge 8, handover bank 30%, `--push-vel 0.8 --start-spread 3`. Exam (`logs/exam_m2_grandpa.json`): **footing 0.00 falls a
minute (from 2.00): pass.** Guard 73%, chin 0.53, getting up 30 of 30. **Attack 0.25 a second at 2.5 m/s (from 0.62 at
3.4; his mark 0.5 at 3.0): now FAILS** (1.02 a second on Nick, 0.04 to 0.09 on the other four). Carrying on 26 of 30,
unchanged, FAIL. Still 7 of 9, with a different line failing: **not promoted; `m1_grandpa` stays in the game.** Half a
round under harder shoves taught him to keep his feet and cost him his punch; whether the second half brings it back
is not known. The queue is still held; released, it carries this run on from iteration 247 to 500. `m2_zombie` not run.

## 2026-10-03 evening: a five-hour session (19:58 to about 01:00), editor closed

**Block 1 (19:58 to 21:17): the wider starts and harder shoves, on three more boxers, and rejected.** Each 400 to 500
iterations against everybody's current policy, handover bank 30%, `--push-vel 0.5 --start-spread 3`.

| Run | Carrying on | Other lines | Verdict |
|---|---|---|---|
| `m3_matt` | 24 of 30 (from 23) | attack 1.36 a second (from 1.99) | no better |
| `m3_grandma` | 19 of 30 (from 24 of 29) | attack 0.57 (from 1.09), chin 0.67 (from 0.00) | worse |
| `m2_zombie` | 22 of 30 (from 27) | footing 2.00 falls a minute (from 0.00) | worse: he had passed everything |

All three, and the half-round `m2_grandpa`, hold `REJECTED.txt`; `tools/exam_v2.py` now passes such runs over.
`m2_grandpa` did not run again here (`train_resilient.py` will not carry on a gauntlet run that has a save).
**`--start-spread` and a harder `--push-vel` are not a fix for carrying on.** Trump's 23 to 28 this afternoon, which I
put down to them, was one result on 30 trials; four boxers since say otherwise.

**What carrying on is (carry_diag, 60 trials each, C MuJoCo 3.5.0).** Every trial was handed over (the get-up policy
stood its 1.5 s every time). Matt then fell in 22 of 60, 1.7 to 2.8 s after the match policy took the body, with
the two pelvises 1.9 to 3.5 m apart when it did (median 2.3 m); and as often with the other boxer a ghost (27) or held
still in its guard (30). Nobody is knocking him over: **a knockdown leaves the boxers further apart than any episode of
training begins (0.85 to 2.2 m), and a match policy that has never had to walk in falls over on the way.** Grandma:
8 of 60, the same distances.

**Block 2 (21:22):** `--sep-max 3.5` (new: the furthest apart an episode begins), handover bank 30%, nothing else
changed: `m4_matt` and `m4_grandma` from their `m2`, `m3_grandpa` from `m1` with the fall charge at 8.

**Block 2 (21:20 to 22:43): episodes begun up to 3.5 m apart (`--sep-max 3.5`), 500 iterations each.**

| Run | Carrying on | Other lines | Verdict |
|---|---|---|---|
| `m4_matt` (from `m2`) | **26 of 30** (from 23; needs 27) | attack 1.96 a second at 6.4 m/s, all else passes | better, one short: kept |
| `m3_grandpa` (from `m1`, fall charge 8) | **30 of 30** (from 26): pass | attack 0.64 at 3.6 m/s: pass; **footing 0.33 a minute** (from 2.00; mark 0.2: one fall in three minutes) | 8 of 9 (from 7): kept |
| `m4_grandma` (from `m2`) | 24 of 29, unchanged | **footing 1.67 a minute, all of it against Zombie** (from 0.00) | rejected |

The wider start is what carrying on wanted for Matt and Grandpa, with their attack kept. Not for Grandma.
**Block 3 (22:46), all with `--sep-max 3.5`:** `m5_matt` and `m4_grandpa` (300 more each, from the runs above), `m7_trump`
(500 from `m6`: his attack fails on the three who stand off, which may be the same not walking in), `m5_grandma` (400
from `m2`, fall charge 8), `m3_zombie` (300 from `m1`: he has not met the current opponents). The human-rules retrain
(D8) does not fit in tonight's five hours.

**Block 3 (22:46 to 00:19) and what 120 trials say.** The 30-trial carrying-on line moves by three either way by
chance, so each candidate was put through 120 knockdowns (carry_diag): falls after the handover, of those handed over.

| Boxer | Before tonight | Block 2 (`--sep-max 3.5`) | Block 3 (300 more) |
|---|---|---|---|
| Matt | `m2`: 53 of 119 | `m4`: **13 of 119** | `m5`: 16 of 119 |
| Grandpa | `m1`: 17 of 117 | `m3`: **8 of 117** | `m4`: 9 of 117 |

**The wider start is a real fix (Matt stays up 89% of the time, from 55%); more of it adds nothing.** Kept: `m4_matt`,
`m3_grandpa`. Rejected: `m5_matt`, `m4_grandpa` (footing 1.00 a minute), `m7_trump` (attack 0.31 a second at 2.5 m/s,
from 0.37 at 4.3: the wider start is not what his attack wants). **`m5_grandma` (400 from `m2`, `--sep-max 3.5`, fall
charge 8): passes every line** (footing 0.00, attack 0.82 at 3.7 m/s, guard 72%, carrying on 29 of 30): kept; she is the
third to pass the whole ladder. Exams from here use 60 knockdowns (`--trials 60`).

**From 00:44, unattended (the owner: eight more hours): tasks.md D8**, a boxer at a time, by `overnight.sh` and `d8.sh`
(session scratchpad; detached, so no tool time limit ends it): rules on in the boxer's configs, footwork 800
iterations, get-up 800, match 600 with `--sep-max 3.5`, an exam after each. Kept if the footwork exam passes and the
bout keeps footing, attack and getting up; otherwise the rules come back off for that boxer and its runs are put
aside. Order: Matt, Nick, Zombie, Grandma, Grandpa. Its log is `training/logs/overnight.log`.
