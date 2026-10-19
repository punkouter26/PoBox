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

## 2026-10-04, 00:44 to 04:39: the human-rules retrain (D8), unattended; nothing kept, by a fault in the script

**`m3_zombie` (300 from `m1`, `--sep-max 3.5`): rejected.** Attack 0.24 a second, carrying on 40 of 59. `m1_zombie` stands.

**D8 for Matt, Nick, Zombie, Grandma and Grandpa: every boxer reverted, rules off again, earlier policies standing.**
The fault was mine: `d8.sh` asked for the footwork top-up with `--iters 800`, and a footwork checkpoint carried on in
its own stage counts from where it stopped (1,600 to 3,950), so **no footwork run trained at all** (each ended in under
a minute with no save). With no footwork exam the script's rule read every boxer as failed. The get-up and match
stages did train under the rules, without a footwork policy to go with them. Their exams (60 knockdowns), for what
they say about boxing under the speed limit and fatigue, each from the boxer's own current policy:

| Run | Result under the rules |
|---|---|
| `m4_zombie` | **passes every line**: attack 0.59 a second at 4.2 m/s, guard 81%, carrying on 55 of 58 |
| `m6_matt` | footing 1.33 falls a minute FAILS; carrying on 60 of 60, attack 1.35 at 4.2 m/s |
| `m3_nick` | carrying on 47 of 60 FAILS; all else passes, attack 1.33 at 4.8 m/s |
| `m5_grandpa` | attack 0.58 at 2.6 m/s (his mark 3.0) and carrying on 51 of 59 FAIL |
| `m6_grandma` | footing 4.33 a minute, attack 0.58 at 3.0, carrying on 32 of 51 FAIL |

What the rules cost the present footwork policies, untrained (5 seeds): Matt stand 93% and walk 0.40 falls a minute
(both fail), Grandpa stand 87%, Grandma turn 80%. All five match runs hold `REJECTED.txt`; the get-up runs are
`checkpoints/x_u1_h_*`. Punches slow under the rules: 4.2 to 4.8 m/s where the same boxers had 5.2 to 6.8.
**The GPU then stood idle from 04:39** (the owner had asked for training to about 08:00). To do it again: footwork with
the iteration count taken from the checkpoint plus 800, and Zombie first, since his bout already passes.

**Into Unity, 2026-10-04 11:00.** Promoted (`MjRetrofit.PromoteBoxing`): Grandma `m5_grandma`, Matt `m4_matt`, Grandpa
`m3_grandpa`, each with the get-up policy it had. The gate: 18 of 18 (a first run hung in play on the Menu scene and
was cancelled; run again from the Testbed). Arena, a minute each: Matt v Grandma 100 landed to 87, Matt put down on
command and up; Grandpa v Nick 58 landed to 0 of 117 thrown, Grandpa put down and up at the count of 4, boxing on.

**On a phone, 2026-10-04 (tasks.md D6).** `PoBox/Android/Build APK` (237 MB, IL2CPP, arm64, MuJoCo 3.5.0), installed on
a Pixel 9 Pro (Android 17) as `com.po.pobox`. Menu with six boxers; Trump v Zombie, two rounds: **60 FPS, frame 16.7 ms
(worst 16.9), physics 0.46 ms a frame, render scale 0.70, 1,209 MB, no garbage.**

The first build (three minutes, on data left from 2 October) drew the ring's lit materials magenta and the boxers'
glTF materials not at all (only the outline shader showed), and logged the post-processing shaders as stripped. A build
with the pipeline's variant stripping switched off was abandoned after an hour (55,296 variants of one unused terrain
shader). **What cured it was the next ordinary build, stripping on as before, after that attempt had thrown the stale
shader data away:** ring, ropes, boxers and post-processing all draw. The settings file is as committed. Left: the
depth-of-field and Panini shaders are logged as stripped (unused effects). The editor is still on the Android target.

## 2026-10-14: audit and D8 staged (no training run)

- **Fatigue froze in footwork and get-up.** Only `BoxingEnv.step` spent the reserve; `FootworkEnv.step` and
  `GetUpEnv.step` never did, so a boxer with `fatigue_j` in its config trained at whatever reserve the reset drew
  (0.3-1.0) for the whole episode. Now one `BoxingEnv._spend(tau, jv)` is called by all three steps.
- **`--speed-limit` / `--fatigue-j` on the command line reach every fighter**, frozen opponents included
  (`boxing.py:175, :303`). The staged get-up jobs leave the flag off; each learner gets the rules from its config
  (`tools/set_rules.py`) just before its own runs.
- `train_gauntlet.py --gamma` (default 0.99, unchanged); `queue_runner.py` passes a job's `"env"` (exams need
  `PYTHONPATH=.mj350`).
- **Staged, not queued: `training/queue_d8_staged.json`** (38 jobs, ~330 GPU minutes): per boxer set_rules ->
  footwork -> exam -> get-up -> handover bank -> gauntlet -> exam (12 rounds, 60 trials), Zombie, Nick, Grandma,
  Grandpa, Matt, then Trump (`--gamma 0.995`, double rings against Matt and the grandparents).
- **Blocked:** this checkout has no `training/logs`, no `.mj350`, only `r0_matt`/`r1_matt` in `checkpoints`, and
  the `../PoDecath` venv is gone. Restore those before copying the file to `logs/queue.json`.
- Open for the owner: fatigue as a torque ceiling rather than a weaker spring (a tired drive still reaches full
  torque on a big command); asymmetric human force ranges (ankle dorsiflexion ~5x, hip_z ~3-4x, knee flexion ~2x
  too strong); footing exam over 12 rounds (4 rounds: one fall already fails).

## 2026-10-14: ringcraft reward terms, staged behind D8 (no training run)

Boxers stand where they are put and get walked onto the ropes. Two reward terms, **off by default** (weights 0.0,
so every existing run and job is unchanged), on the match line (`envs/boxing.py`, used by `envs/match.py`):

- **`rope` (charge, `train_gauntlet.py --rope-w`).** How far into the last metre before each rope the pelvis is (0 a
  metre or more off, about 0.7 against a rope; a corner adds both ropes, about 1.4), times "under pressure": the
  opponent within range + 0.8 m and nearer the centre than me (full at 0.5 m nearer).
- **`cutoff` (payment, `--cutoff-w`).** Paid only facing the opponent (cos > 0.7) within range + 0.3 m: how squarely
  I stand in its way to the centre (cosine at the opponent between the way to the centre and the way to me) times
  how roped it is (the same last-metre measure), times 1 + its speed out from the centre (-1 to 1 m/s). Standing at
  the centre pays nothing unless the opponent is on the ropes and in range; stepping in and out pays as standing.
- **Suggested 0.05 each:** about 0.035 a step on a rope, at most about 0.07 (rope, corner) and 0.14 (cutoff, corner,
  driving in). Standing in range pays up to 0.95 a step and a 7 m/s head shot 6.3 once; both new terms stay under
  the 0.4 that alive + upright pay, so neither makes ending an episode or not punching worth it.
- **Logged:** the unweighted measures per fighter (`v_NAME/rope`, `v_NAME/cutoff`, `env/rope`, `env/cutoff`, so a
  D8 run is the baseline) and, new to the gauntlet, every reward term weighted (`reward/<term>`, both fighters'
  mean, as `train_box.py` logs them). The gauntlet CSV gains two columns: a run begun before this change and
  resumed after it appends rows one wider than its header.
- `tools/exam_v2.py` now also takes `checkpoints/rc<round>_<name>` as match rounds (newest kept wins, as for `m*`).
  No rope-time line in the exam (`tools/exam.py` untouched); TensorBoard's `rope`/`cutoff` are the measure.
- CPU smoke (PoOlympic venv, MuJoCo 3.11, 4 worlds): obs 103, actions 21; Matt 0.25 m off a rope with Zombie 0.75 m
  nearer the centre, facing: Matt's `rope` 0.70, Zombie's `cutoff` 0.72, the other two 0; weights 0 give 0.
- **Staged, not queued: `training/queue_ringcraft_staged.json`** (12 jobs, ~120 GPU minutes): per boxer `rc1_NAME`
  (400 iterations, `--rope-w 0.05 --cutoff-w 0.05`, warm start from its D8 match run, the D8 job's handover and flags,
  against the others' D8 match runs) then a 60-trial, 12-round exam. Order Zombie, Nick, Grandma, Grandpa, Matt,
  Trump. Run after `queue_d8_staged.json`; before queuing, swap any D8 run that holds `REJECTED.txt` (and `m8_trump`
  if it was skipped) for the boxer's standing checkpoint. Kept if R4-R7 all hold (no line worse than the D8 exam)
  and `rope` falls / `cutoff` rises against the D8 run.

## 2026-10-14 (evening): Kim, the seventh boxer

- **Body:** `Kim.glb` (the owner's; not in metres, 1.06 units tall) built with `build_roster.py --only kim --rigs rigs/v2
  --models models/v2 --cubes 8`: 1.65 m (chosen), 55.1 kg (mass given as 54, PoOlympic's; the mesh's loose top reads
  106 litres), strength 0.71, speed 1.10. 17 shapes on 12 parts; no pair touches in the 25 check poses. Pair models
  with every boxer. Speed limit and fatigue (30 kJ) on from the first run (`set_rules.py --only kim`).
- **Style, "hit-and-move"** (owner: "a fast and agile woman"): head 1.3, body 0.9, power 0.6, pace 0.25 s, taken 1.5,
  counter 1.4, range 0.78 m, press 0.1, crowd 0.6, energy 0.7, and the gauntlet's rope charge `--rope-w 0.08`, which
  no other boxer trains with.
- **Trainer on this machine:** `../PoDecath` is gone; runs use `../PoOlympic/training/.venv` (Python 3.11.9, MuJoCo
  Warp 3.11.0, torch 2.14). `training/.mj350` re-installed (MuJoCo 3.5.0, `pip --no-deps --target`). The retrofit's
  checkpoints are not on this disk: warm starts and frozen opponents are rebuilt from the shipped ONNX brains by the
  new `tools/onnx_to_pt.py` (into `checkpoints/onnx/`; each checked against its graph), action noise reset to 0.3.
- **Queue** (`logs/queue.json`): `f_kim` footwork 1600 iterations from Nick's footwork brain -> exam -> `u1_kim_matt`
  get-up from Nick's (Matt frozen, assisted at first) -> handover bank -> `m1_kim` gauntlet 800 iterations from Nick's
  match brain against all six -> exam (60 trials, 12 rounds). Unity closed for the run.
- **22:40, the owner: "train and improve all behaviours for next 10 hours".** After Kim's queue, D8 for the old
  boxers (36 jobs appended to `logs/queue.json`, built by a one-off script): per boxer, rules on -> footwork 800
  iterations from its shipped footwork brain -> exam -> get-up 600 (Matt frozen; Matt's own with Zombie) -> handover
  bank -> gauntlet 600 from its shipped match brain against the other six, Kim included (each opponent the newest
  of its shipped brain and its D8 run) -> exam. Order Zombie, Nick, Grandma, Grandpa, Matt; then Trump's exam (he is
  already under the rules). Kim's follow-ups go in front of D8 as her exams come in.
- Footwork no longer runs fatigue (`FootworkEnv` sets `fatigue_on = False`): the footwork exam has none and no bout
  runs that brain. `f_kim` itself started before this change and drains the reserve.
- **23:40, two faults, fixed.** (1) `m1_kim` stopped at iteration 359: `train_gauntlet.py`'s default `--max-hours 0.6`.
  Kim continues as `m2_kim` (600 more, up to 1 h) before D8 resumes; the D8 gauntlets now carry `--max-hours 0.75`.
  (2) `exam_v2.py` knew six names (Kim added) and found opponents only among this disk's runs; it now falls back to
  the shipped brains in `checkpoints/onnx`. (3) `f1_zombie` collapsed at the first update (KL 291, every episode a
  fall in 1.2 s): `onnx_to_pt.py` gave the shipped observation statistics a weight of 1e5 samples, and one rollout of
  98k moved them. Rebuilt at 1e9; `f1_zombie` stopped after 2 minutes, re-queued as `f1b_zombie`. Kim's runs began
  from the 1e5 files and survived it (KL 36 at iteration 0).
- **00:00, Kim's first boxing exam** (`m1_kim`, 359 iterations; 60 trials, 12 rounds): footing 0.00, guard 71%, chin
  0.00, getting up 59 of 60 in 1.5 s, carrying on 56 of 59: pass. **Attack 0.14 a second at 1.1 m/s (mark 0.5 at
  4.4): FAIL**; she landed nothing on Grandma, Grandpa or Matt. Restyled (roster note) and `restyle.py` (now
  `--rigs/--models`) wrote it to models/v2; `m2_kim` stopped after minutes (REJECTED); `m2b_kim` from `m1_kim`,
  600 iterations up to 1 h, `--hit-w 1.2`, twice the rings against Grandma, Grandpa and Matt.
- **02:50.** `bag_kim` (700 iterations from `m2b_kim`, `--hit-w 2`, noise reset to 0.35): 0.13 -> 3.5 hits a second at
  8.3 m/s on the bag. `m3_kim` (600 from the bag): **footing, guard 62%, chin 0.09, getting up 59/60, carrying on 59/59
  pass; attack 1.11 a second at 3.4 m/s (mark 4.4): FAIL**, the average dragged down by Grandma 0.04, Grandpa 0 and
  Matt 0 (Nick 2.67 at 7.0, Trump 3.07 at 6.4, Zombie 0.87 at 5.3): she does not walk in on the three who stand off.
  Next `m4_kim` from `m3_kim`: press 0.3 -> 0.6, `--cutoff-w 0.05`, three times the rings against those three.
  Zombie under the rules (`f1b_zombie`, 800 from his shipped brain): stand 100%, walk 0.01 m/s off and no falls, turn
  93%, joints 0.32: R0 to R2 pass. (`exam_v2.py --book` was being dropped before it was read; fixed.)
- **04:00, `m4_kim`** (press 0.6, cutoff 0.05, three times the stand-off three): attack 1.03 a second at 3.8 m/s (mark
  4.4): FAIL, the other five pass. The exam averages speed over opponents, and on Grandma, Grandpa and Matt she lands
  0.02 a second at 1.0 to 1.8 m/s; in training Matt lands 1.44 a second on her and she 0.03 on him. Noise had fallen
  to 0.14. Next `m5_kim` (after `m1_zombie`): noise reset 0.35, `--hit-w 2 --taken-w 0.3 --effort-free-iters 50
  --sep-max 2.5`, four times the rings against those three.
- **05:40, `m5_kim`** (noise reset, hit 2, taken 0.3, starts within 2.5 m, four times Grandma/Grandpa/Matt): **attack
  1.43 a second at 5.2 m/s: pass** (Matt 2.60 at 6.3, Grandpa 0.71 at 3.2, Grandma 0.22 at 3.9); guard 80%, chin, getting
  up pass; **footing 1.00 falls a minute (against Zombie) and carrying on 43 of 59: FAIL**. Next `m6_kim` from it:
  fall penalty 12, taken back to 0.5, hit 1.5, starts to 3.5 m, half from the handover bank, Zombie x3, Matt/Grandpa x2.
- **06:45, `m6_kim`**: attack 1.52 a second at 5.2 m/s, guard 85%, chin 0.00, getting up 59/60, carrying on 56/59: pass;
  **footing 0.33 falls a minute, all against Zombie: FAIL** (mark 0.2). But the exam's Zombie was `m1_zombie`, his
  D8 run, not the shipped brain Kim trained against. **`m1_zombie` (D8): footing 15.00 falls a minute (against Kim),
  attack 0.54 at 3.3 m/s (mark 4.0): FAIL**; guard, chin, getting up 60/60 in 2.2 s, carrying on 59/60 pass. Marked
  REJECTED (the shipped Zombie passes 9 of 9); Kim's m6 re-examined against the shipped Zombie.
- **11:00, Kim in the game.** Re-examined against the shipped Zombie (`m1_zombie` REJECTED), **`m6_kim` passes all six
  boxing lines** (footing 0.00, attack 1.55 a second at 5.2 m/s, guard 80%, chin 0.00, getting up 59/60, carrying on
  56/59); with the footwork exam (R0-R2 and joints, `f_kim`) every line. Unity: `MjRetrofit.BuildBoxer/BuildCorners`
  (0 differences from the trainer's model), `Promote("kim", "f_kim")`, `PromoteBoxing("kim", "m6_kim", "u1_kim_matt")`
  (get-up exported at 103 inputs with `ppo.export_onnx`), testbed roster and both arena corners, menu card and portrait.
  **Gate: 3 of 3** (policy answers, same run, fall rate). First screen with seven cards: one screen at 720x1280,
  1080x1920 and 1080x2520. In the arena against Matt: 55 punches landed in 19 s.
- **The lost morning:** a full play-mode run started 07:13 with the editor in the background ran at a tenth of real
  time and was stopped at 10:58; the queue was held (07:07) for the editor work and stayed held. D8 done so far:
  Zombie footwork passes under the rules (`f1b_zombie`), his match (`m1_zombie`) REJECTED; Nick's footwork (`f1_nick`)
  trained, not examined. Grandma, Grandpa, Matt not started.
- **11:30, `m6_kim` again with `--seed 2`: all six boxing lines pass** (footing 0.11 a minute at worst, attack 1.55 at 5.2 m/s, guard 78%, chin 0.00, getting up 60/60 in 1.7 s, carrying on 59/60). Kim: 9 of 9 exam lines and the Unity gate.
- **12:00, finished up at the owner's word.** `m7_kim` (400 more from `m6_kim`, 60% of episodes from the handover bank,
  fall penalty 12, noise at most 0.25) trained to the end; its first exam was left running (`logs/exam_m7_kim.log`) and
  `logs/queue.stop` + `logs/queue.hold` end the queue after it. Why it was run: a stand-alone get-up check of `m6_kim`
  (seed 1) gave getting up 60/60 but carrying on 52 of 60, under the 54 mark; the two full exams had 56/59 and 59/60.
  Kim plays on `m6_kim` until `m7_kim` passes two seeds and is promoted. D8 left: Nick's footwork passes under the rules
  (`f1_nick`: joints 0.41); his get-up and match, and all of Grandma, Grandpa and Matt, not run.
- **12:30, `m7_kim` promoted** (`PromoteBoxing("kim", "m7_kim", "u1_kim_matt")`): its exam (seed 1) passes every line, carrying on 54 of 58 (93%) where `m6_kim` had given 52/60 on a stand-alone check; footing 0, attack and guard pass, getting up 58/60. Its seed-2 exam was still running at the commit (`logs/exam_m7_kim_seed2.log`). The gate tests run the footwork brain (`f_kim`), unchanged. Also fixed: the arena drew black (only the HUD) after a `shot-begin` without `shot-end` left `PoBoxPanel.asset` clearing the screen; `DevTools` now restores the panel when play stops.
- **12:40, `m7_kim` seed 2: every line passes** (footing 0.00, attack 1.49 a second at 5.5 m/s, guard pass on head shots taken 0.40/s, chin 0.09, getting up 60/60 in 1.7 s, carrying on 58 of 60). With seed 1 (54/58): Kim passes on both seeds with the brain the game now runs.
- **12:50, stand-alone get-up check of the shipped pair (`m7_kim` + `u1_kim_matt`), three seeds:** getting up 60/60, 60/60, 58/60 (1.6 to 1.8 s); carrying on 56/60, 55/60, 55/58 (mark 90%): pass on every seed, where `m6_kim` had failed seed 1 at 52/60. `Assets/Boxers/Kim/match.onnx` is byte-identical to `m7_kim/latest_kim.onnx`.

## 2026-10-15 (afternoon): Sokol, the eighth boxer

- **Body:** the owner's `Sokol_TPose` character (`Character_TPose.glb` -> `Assets/Models/Sokol.glb`; plaid shirt, jeans;
  in metres, 19 Blender bones `thigh.L`...). `glb_to_rig.py` read no names off it: a side given as a suffix (`.L`) was
  never stripped, so the shape fallback took over and put the shins on the thighs. Fixed where names are cleaned; the
  other seven rigs are not rebuilt. `build_roster.py --only sokol --rigs rigs/v2 --models models/v2 --cubes 8`: 1.83 m,
  83.1 kg (mass given as 82: the mesh's volume reads 123 litres, BMI 36), strength 1.03, speed 1.00; 17 shapes on 12
  parts, no pair touches in the 25 check poses; pair models with every boxer. `set_rules.py --only sokol
  --speed-limit 1 --fatigue-j 30000` before the first run. `exam_v2.NAMES` gains `sokol`.
- **Style, "boxer-puncher"** (chosen to fill the roster's gap; the owner can change it): head 1.3, body 1.0, power 1.3,
  pace 0.5 s, taken 0.8, block 0.03, range 0.72 m, press 0.5, energy 1.0. Kim's lessons from the start: power and press
  up and taken under 1, so standing off does not pay; gauntlet `--hit-w 1.5 --cutoff-w 0.05`, `--max-hours 1.5`.
- **Warm starts from Matt** (1.86 m, 84 kg, the nearest body). Queue, in front of the D8 jobs: `f_sokol` footwork 1600
  -> exam -> `u1_sokol_matt` get-up 800 (Matt frozen, assisted at first) -> handover bank -> `m1_sokol` gauntlet 800
  against all seven (Kim's `m7_kim`) -> exam. The queue had died at 12:59 inside `m1_nick` (iteration 50; its paths
  still pointed at `Downloads/PoBox`, the repo is now `Downloads/CODE_MATT/PoBox`); `m1_nick` reruns after Sokol.
- **14:13, `f_sokol` cut at iteration 1050 of 1600** (the owner gave training 30 minutes; queue stopped with
  `logs/queue.stop`, run killed right after its 1050 save). Footwork exam in C MuJoCo 3.5.0, 30 episodes each:
  **stand 97% (mark 95%), walk off by 0.01 m/s with 0 falls a minute, turn 100%, joints at 0.38 of the speed limit:
  every line passes** (`logs/exam_f_sokol.json`). Not yet done: get-up, handover bank, boxing gauntlet, Unity.
- **16:40, Sokol in Unity on his footwork brain.** Fingerprint and C MuJoCo 3.5.0 recordings written
  (`export_fingerprint.py`, `footwork_c.py --reference`, `--falls 25`: on the floor at the end of 0 of 25). Unity:
  `BuildBoxer`/`BuildCorners` (0 differences from the trainer's model), `Promote("sokol", "f_sokol")`, testbed roster.
  No match or get-up brain yet: in the free-for-all he stands and turns to face, and the referee stands him up.
  Still to train: `u1_sokol_matt`, the handover bank, `m1_sokol` and its exam (queued; the queue is stopped).

## 2026-10-16, staged (not run): the do-all pass on the trainer

Nothing trained; every new behaviour is a flag that is off by default, and with all of them off a fixed-seed CPU smoke
of the three stages (10 steps, 4 worlds) gives the same rewards and observations, number for number, as before the
change. Observation (103) and action (21) sizes, models/v2 and the Unity side are unchanged.

- **`--action-rate-w`** (train_box, train_gauntlet, train_getup; default 0): an extra charge per squared change in
  action between control steps. Every stage already had one (0.01 a step; get-up 0.005); this adds to it, for smoother
  drives. Try 0.01, which doubles it.
- **`--obs-delay-max K`** (the same three; default 0): the observation is 0 to K control steps late, drawn per fighter
  per episode; the last action is never late. Training only: the game's observation is not delayed, so a brain
  trained with it plays in Unity as any other. Try 2 (up to 40 ms).
- **`--seek-w`** (train_box, train_gauntlet; default 0): Phase F2, paid per m/s of closing on the opponent while
  outside punching range, for every boxer whatever its style (the style's `press`, at a weight of its own). Logged as
  `rt_seek` / `reward/seek` only when on. Why: the stand-off. Standing in range facing the opponent pays about 0.95 a
  step (alive, upright, face, range); m1_zombie's gauntlet earned 0.79 a step with 0.54 hits a second.
- **`--ropes-start`** (same; default 0): Phase F3, a share of episodes begin as the free-for-all's rounds do
  (FreeForAll.ToTheRopes): 16 spots round the square of the ropes, half a metre in; the other boxer two to eight
  spots round; both facing the middle.
- **Not done:** F1 (crowd mode, K frozen opponents, `--crowd-schedule`): it needs 3-to-8-body MJCFs and boxing.py
  taken from its two-fighter `flip(1)` to a gathered target; noted as a TODO in the staged queue. Action masking in
  get-up: not needed, since get-up pays nothing for a punch, the arms are what it pushes up with, and `_drive` already
  clamps every target to the joint's range in every stage. A fatigue/daze coupling: they already compose
  (`drive_scale * _tired()`), as the game does.
- **New tools:** `tools/obs_audit.py` (each stage on the CPU with the shipped ONNX brains: per-index statistics,
  flags for big, wide, dead and clipped numbers, and the footwork reference recording's gap); `tools/pd_sweep.py`
  (step response of each joint's drive, base fixed: rise, overshoot, damping ratio; `--write` a proposal, models
  untouched; `pd_gains_proposal.json` is Matt's with the speed-limit damping); `tools/bench_match.py` (whole PPO
  iterations of the match stage at 4096/8192/16384 rings x 24/32 steps).
- **Findings.** Observations (Matt and Zombie, 10 s a stage): no index has |mean| > 5 or std > 10; the widest are joint
  speeds (std up to 3.9); less than 1% of samples beyond the brains' own clip of 10 anywhere (get-up's worst 0.95%,
  a shoulder speed); the command's three are dead in a bout and on the floor, as meant; the C MuJoCo reference sits
  within 1.4 training std of training on every index. But the footwork brain holds its arm actions at the clip (+-3:
  shoulder_x_l and elbow_r mean -2.99), with shoulder_x_l a radian short of its target: drives at their force limit,
  probably the guard pressed against itself (AGENTS: a small self-contact force penalty if so). PD (Matt, fixed base):
  without the speed-limit rule the abdomen (zeta 0.19 to 0.53) and hip_x/hip_y (0.36) are under-damped and the
  ankles over-damped (1.3 to 2.6); with it, everything but the abdomen's bend and the hips' swing is over-damped. Fixed
  base understates the legs' load in stance; a proposal only.
- **Reward-hacking audit (envs/boxing.py):** rope-hugging: measured 0.02 to 0.06 a step unweighted, already charged by
  `--rope-w`; no new guard. Survivor bootstrap: sound; the knockdown bonus needs only a landed punch inside 1.5 s, so
  a pat then a fall counts in full; watch, not changed. Hit against taken: taken is 0.5 (Kim 0.3) of hit, so trading
  always pays, by design; the daze is the only cost. Block farming: paid a step for at most two steps a punch, 0.03 a
  m/s; against frozen opponents it cannot be farmed. The real exploit is passivity (above), which `--seek-w` is for.
- **Removed:** `run_night.py` and `run_match.py`, the v1 bag-then-spar drivers (v1 models only; queue_runner and
  train_gauntlet have replaced them). Kept: `train_getup.py` (v2 get-up, renormalising, the helping hand carried
  over, `--done-when`), `train_getup_cpu.py` (C MuJoCo; `renormalise` lives there), `train_gauntlet.py` (frozen
  opponents): none of that is in `train_box.py --stage`.
- **Staged:** `training/queue_doall_staged.json`: bench_match, then sf1_kim (seek 0.3, ropes 0.3, from m7_kim),
  sr1_kim (rate 0.01, delay 2), sf1_zombie and sf1_nick from the shipped brains, each followed by exam_v2.

## 2026-10-16 00:00, overnight (owner: "train and improve all behaviours ... for next 10 hours")

Decision: finish the live queue (`logs/queue.json`, stopped since 15 Oct 14:13) in priority order, with one run of
the new seek reward added, and stop after it (a last job writes `logs/queue.stop`). Estimated 9.7 h.

1. Sokol has no boxing brain (he boxes on his footwork policy): `exam_f_sokol`, `u1_sokol_matt` (get-up from
   Matt's, 800 iterations), `bank_sokol`, `m1_sokol` (gauntlet from Matt's match brain against all seven, 1.5 h
   cap), `exam_m1_sokol`.
2. Nick: `m1_nick` (get-up and hand-over bank already done; the killed partial run was moved to quarantine so the
   job can start), `exam_m1_nick`.
3. Zombie: `sf1_zombie` + exam, from `queue_doall_staged.json`: `--seek-w 0.3 --ropes-start 0.3` (first GPU use of
   both flags). Why: `m1_zombie` was rejected, and the reward audit found standing at range pays ~0.95 a step;
   `--seek-w` pays for closing on an opponent outside punching range.
4. D8 for Grandma, Grandpa and Matt as already queued (rules on, footwork 800 + exam, get-up 600, bank, gauntlet
   600 at 0.75 h, exam), then `exam_trump_d8`.

Left out for time: `bench_match`, `sf1_kim`/`sr1_kim` (Kim passes with m7), `sf1_nick` (m1_nick first). Nothing
is promoted into Unity overnight: exams decide, promotion is done with the editor open. Unity was in use by a UI
pass when the queue started; the runner waits at job boundaries while the GPU is busy with Play mode.

- 01:53 `exam_m1_sokol`: FAIL on footing only (1.33 falls a minute, all against Nick); attack 1.84/s, guard 21%,
  chin 0, get-up 60/60 in 1.6 s, carrying on 59/60. Added `m2_sokol` after `exam_m1_nick`: from m1, `--weights
  nick=3 --fall-penalty 12 --max-std 0.3`, 400 iterations (0.6 h cap), then `exam_m2_sokol` (Kim's m-series passed
  this way). Adds ~50 min; the night runs to about 10:15.

## 2026-10-16 09:00, the night's results and a 10-hour day queue (owner: "do 10 hours of training")

Night (23:39 to 09:08, 9.5 h): footwork exams pass for Sokol, Grandma, Grandpa and Matt under the rules. Boxing:
**m2_sokol PASS** (all six lines; 2.07 landed/s, 60/60 get-ups, 0.11 falls/min). m1_nick fails footing only (2.89
falls/min, all v Sokol, whom it never trained against). m1_matt fails carrying on only (49/58, mark 90%). m1_grandpa
fails attack (0.02/s: it learned to block, 1.4/s, and not to punch). m1_grandma fails attack (0.35/s) and getting up
0/60: her D8 get-up never stood unaided (help stalled at 0.23 of her weight under the speed limit and 30 kJ fatigue).
`exam_sf1_zombie` examined the SHIPPED brain: exam_v2 only finds runs named m<N>_ (sf1 is not); re-examined with
`--book`.

Day queue (09:08 on, ~9.4 h), each run then its exam; opponents are the newest brain of each boxer (Sokol m2):
- Wave 1: `exam_sf1_zombie_book`; `m2_nick` (+Sokol, weighted 3, fall penalty 12); `m2_matt` (hand-over share 0.5,
  fall penalty 12); `u1_d8b_grandma_matt` (1500 more get-up iterations, stops at 80% unaided) + new bank;
  `m2_grandma` and `m2_grandpa` (Kim's attack recipe: hit 2.0, taken 0.3, seek 0.3, std reset 0.35); `m2_zombie`
  from sf1 (seek, ropes start, hit 1.5, fall penalty 12).
- Wave 2: m3 of the same five from their m2, against the m2 brains, same terms, max std 0.25, 400 iterations.
- If Grandma still never stands unaided, the cause is the rules on her body, not training time: owner's call.
- 09:10 `exam_trump_d8`: FAIL attack (0.29/s) and guard (17%); Nick takes his legs 7.8/min. Added after `m2_zombie`:
  `bank_trump` (from his shipped get-up, 60/60), `m1_trump` (attack recipe as the grandparents', Nick weighted 2,
  600 iterations, 0.75 h), exam. The day queue now ends near 19:30.

## 2026-10-16 10:30, punch rules and the state input (owner: "boxing more realistic"; tasks G12/G13)

- `exam_m2_sokol` had no `PYTHONPATH=.mj350` in the queue (MuJoCo 3.11); re-run on 3.5.0
  (`logs/exam_m2_sokol_mj350.json`): pass, every line. Promoted.
- New rules, each the taker's, from its config or a `train_gauntlet.py` flag, off by default, so old runs reproduce:
  `hit_push` (0.6: a landed punch pushes the trunk at the contact with that share of 2.2 kg x speed over the next
  control step; peak 594 N), `stagger_v` (5 m/s head shot: drives under 0.55 for 0.3 s), `hurt_full` (400: lasting
  damage, drains over 600 s; legs go at up to 40% less daze, drives up to 25% weaker; episodes start anywhere up to
  80% of it), `body_drain` (0.0025 of the reserve per m/s of a body shot). `--obs-state`: the learner's three
  command numbers become reserve, own daze / legs line, opponent's; on a warm start those inputs get a scale and
  zero first-layer weights, so iteration 0 acts exactly as the brain it came from.
- Smoke test, 256 rings, Grandma (shipped) v Sokol (m2), 12 s, falls Grandma/Sokol: rules off 2/0; push only 6/0;
  fatigue 8000 only 8/5; all rules 63-76/16-34. The falls come from lasting damage and body drain (34-35 each alone),
  not from the push or the stagger: these brains fall when their drives weaken, because they never trained weak.
- In Unity (all rules on, all eight, game-only today): a full bout Matt v Zombie, no knockdowns; Matt 22% reserve at
  the end of round 1; Zombie's lasting damage 145. The exam's C MuJoCo ring has the same rules (`tools/exam.py`),
  checked with an in-memory config: 127 pushes a minute, both reserves empty by 30 s.
- Staged, not run (`training/queue_punch_staged.json`, ~8 x (0.75 h + 13 min) + a baseline exam, ~8.5 h): the world
  rules into every v2 config (fatigue 8000 included), then for each boxer `m9_NAME` from its newest brain (Matt,
  Nick, Grandma, Grandpa from their D8 m1, Zombie sf1, Trump shipped, Kim m7, Sokol m2), against the same opponents
  as the D8 day queue, `--obs-state --taken-w 1.0 --block-w 0.5 --hit-w 1.5 --fall-penalty 12`, 600 iterations,
  each examined on 3.5.0 with `logs/book_punch.json`; last, `exam_punch_baseline` (the shipped brains,
  `logs/book_shipped.json`). It trains under the speed limit, as every v2 config now says: the game turns that on for
  the five without it only when their retrained footwork and boxing are promoted together (D8).
- 13:20-13:38 `m9_matt` (owner: "30 minutes"): 196 iterations from m1_matt, all punch rules as flags (configs untouched),
  `--obs-state --taken-w 1.0 --block-w 0.5 --hit-w 1.5`. The obs-state warm start did not fire: m1_matt's variance on
  the three zeros was 0.77, not near zero; the check now reads the checkpoint's own `env.obs_state`. Against Zombie
  (sf1) under the rules, 512 rings x 30 s: m1 lands 0.80/s, m9 1.49/s; both 0% to the head and 0 blocks; reserve
  0.60 v 0.56; Zombie lands 0.18/s v 0.13/s; no falls. Busier, not more varied, and no pacing yet: the spam is
  rewarded (hit pay), and nothing pays for the head or for a mix. Not promoted, not examined.

## 2026-10-16 14:26, rc1_matt: ringcraft and a clinch term on m9; the CPU Elo (owner: features 3 5 7 9 + "an ELO of each fighter from cpu fights")

- **New term, `--clinch-w`** (envs/boxing.py, train_gauntlet.py; off by default, old runs unchanged): pays a hurt boxer (daze past half
  of where its legs go, needs `--daze`) for both gloves resting on the opponent's trunk (within 5 cm, under 1 m/s) with the pelvises
  within 0.55 m. Logged per fighter as `v_NAME/clinch` and `env/clinch`. Smoke-tested 2 iterations, 256 rings.
- **14:26-14:57 `rc1_matt`**: from m9_matt, m9's exact flags and opponents plus `--rope-w 0.05 --cutoff-w 0.05 --clinch-w 0.1`,
  `--max-hours 0.5`, 271 iterations. Unity was open in edit mode (no play) through the run; 8 CPU workers of the Elo round-robin ran
  alongside from 14:43 and took the trainer from ~19k to ~13k samples a second.
  Last 20 iterations, m9 then rc1: on the ropes 0.182 -> 0.115 (-37%), cutting off 0.031 -> 0.034, landed 1.11 -> 1.11/s, taken
  0.435 -> 0.348/s, falls 0.018 -> 0.016. **Clinch 0.000 throughout**: Matt is never dazed that far (legs 0.00/min), so the term never paid.
- Exams, 3.5.0, 60 trials, 12 rounds (`logs/exam_m9_matt.json`, `logs/exam_rc1_matt.json`; m9 had never been examined): both pass
  footing, attack (m9 1.55/s at 4.5 m/s, rc1 1.42/s at 5.0), guard (m9 stops 0% / takes 0.04 head shots a second, rc1 14% / 0.08),
  chin, getting up (58/60); both FAIL carrying on at 52/58 (mark 90%). **Neither promoted.** Carrying on is the get-up hand-over, not
  ringcraft. Report: `SCREENSHOTS/2026-10-16-features/report.html`.
- Next, if wanted: the clinch needs hurt starts (episodes beginning dazed); cut-off needs more episodes with the opponent on the ropes
  (`--ropes-start`); carrying on needs `--handover-share` up from 0.3 (m2_matt's recipe).
- **CPU Elo** (`tools/elo.py`, self-check `tools/test_elo.py`): every pair of the game's brains (`logs/book_shipped.json`), 6 bouts a
  pair (half with the corners swapped, joints jittered 0.05 rad at the marks), 3 x 45 s under each boxer's game rules
  (Assets/Boxers/NAME/config.json laid over the pair config: hit_push, fatigue 8000, no speed limit), exam punches and daze, one judge
  a round. Bradley-Terry fit, Elo scale, mean 1000. 168 bouts in 803 s on 8 workers: MATT 1547 (40-0-2), SOKOL 1147, KIM 1072,
  GRANDPA 939, TRUMP 912, ZOMBIE 912, NICK 787, GRANDMA 685. Written to `Assets/Resources/cpu_elo.json` (the game's ladder starts
  there) and `logs/cpu_bouts.ndjson`. Re-run after every promotion.

## 2026-10-16 night, staged (not run): ten trainer options, and models/v3 (owner: "do all")

Nothing trained or promoted; nothing put in `logs/queue.json`. Every new behaviour is a flag or a config key that is off by
default: with all of them off, `tools/smoke_flags.py --compare` gives footwork, the match, the match under every rule and
get-up identical to a baseline recorded before the change (fixed seed, 4 worlds x 30 steps, CPU), number for number.

- **Self-contact charge** (`--selfcontact-w`, footwork and the bout): per cm of a boxer's own parts pressed into each other,
  read off MuJoCo Warp's contact list on the GPU (no sync). Why, measured with the shipped brains (16 worlds x 5 s, CPU):
  Matt's footwork brain presses 0.93 cm a step, mostly the right thigh into the torso (0.44) and the forearms into it
  (0.2 each); the match brain 0.15. Its arm actions sit at the clip: shoulder_x_l and elbow_r 100% of the time. Suggested 0.02.
- **Variety** (`--repeat-decay`, `--combo-w`; G14): a punch is its hand and its zone; the same one again pays repeat_decay
  times the last (a 2 s pause starts afresh); the other hand within 0.8 s pays combo_w per m/s. Counted always and logged
  per boxer (`repeat_share`, `combos_per_s`, `self_cm`). `punch_variety()` is checked on a made-up sequence.
- **Hurt starts** (`--hurt-start`; H7): a share of episodes begin dazed, 55-90% of the way to the legs going, so the clinch
  pay is ever earned. **`--done-when`** on the gauntlet ("hits_per_s>=1.2,falls<=0.03,repeat_share<=0.6" for 40 iterations).
- **Fatigue ceiling** (`--fatigue-ceiling` or config `fatigue_ceiling`; the open question of 2026-10-14): a tired drive's
  force, kp (target - q) - kv qdot, is held inside its force range times the strength left (per side, from the model);
  the spring keeps only the daze's weakening. Checked: worst excess 3e-5 N m, 64-70% of tired drives at the cap. The same
  in `tools/exam.py` and `MjBoxer.Drive` (Unity), both inert without the key.
- **Per-joint action scale** (config `action_scale_j`): each joint's reach from the guard over the clip. One 0.5 for all
  could not straighten an elbow (1.5 rad from the guard; 1.95 needed) nor reach the shoulder's range: the saturation
  above. In every stage, `tools/exam.py`, `footwork_c.py`, `eval_cmujoco.py`, `envs/cgetup.py` and `MjBoxer.Drive`.
  `tools/rescale_actions.py` moves a brain to it (last layer, noise and the last-action inputs' statistics times
  old/new): targets agree within 1.3e-4 rad wherever the old action was inside its clip; 10% of joint-samples pressed on
  the clip and now reach up to 0.45 rad further, which is the point.
- **models/v3** (`tools/build_v3.py`; `rigs/v2` rebuilds `models/v2` byte for byte, checked under 3.5.0 and 3.11, so v3
  differs by exactly these): human force each way (`rig_to_mjcf.HUMAN_FORCE`: ankle dorsiflexion 0.2, knee flexion 0.5,
  hip rotation 0.3; Matt's ankle_y -46/+232 N m); per-joint action scale; head spheres at 95% of the measured half-width
  (were 100-137%); damping for zeta 0.85 measured standing (`tools/pd_sweep.py --loaded`: a balance hand on the pelvis,
  nothing vertical, (w_n, zeta) fitted to the whole trace). Standing, legs are a closed chain a single joint's step barely
  moves (31-39% reached), so hips, knees and ankles take the model's inertia; arms and trunk are measured. Matt: hips kv
  31.6 -> 58.8, knees -> 21, ankles -> 12.6, shoulders 8.4 -> 3.4-6.3, elbows 5.8 -> 3.1. All eight pass the self-contact
  check in 25 poses; 28 pairs. Configs carry v2's rules and `fatigue_ceiling: true`.
- **What v3 costs the shipped brains** (`tools/ablate_v3.py`, Matt's footwork, 3 seeds x 2 episodes, C MuJoCo 3.5.0):
  v2 stand/walk pass, 0 falls/min, vel err 0.031; +damping pass (turn passes too), +head pass; +force each way FAIL
  (2.19 falls/min); +action scale FAIL (1.15, vel err 0.29); all of v3 FAIL (Matt 1.15 falls/min, Grandma 8.05). So
  every boxer's brains are topped up on v3 before any of it can reach the game.
- **Cleanup (G9)**: `envs/cgetup.py` reads `MODELS` (models/v2); the v1 models, rigs, `models/dev`, `models/guard`, five
  v1-only tools and the spent `queue_d8_staged.json` / `queue_doall_staged.json` moved to `../PoBox_quarantine_2026-10-16/training/`.
- **Staged** (`tools/stage_v3.py` writes both): `training/queue_flags_staged.json` (v2, ~2 h: bench_match; Matt's
  footwork with the self-contact charge + exam; `rc2_matt` from m9 with variety, hurt starts and the clinch, hand-over
  0.5 (H8), self-contact, ringcraft, --done-when + exam). `training/queue_v3_staged.json` (99 jobs, ~2 h a boxer, ~16 h):
  bench_match; the exam books; every shipped brain rebuilt from Assets and rescaled; the punch rules into v3's configs;
  then per boxer (Matt, Sokol, Kim, Zombie, Nick, Trump, Grandpa, Grandma) footwork top-up + exam, get-up top-up against a
  frozen partner, hand-over bank, a match round on v3 against the others' newest v3 brains + exam on v3.
- **Promoting v3 into the game** (not done): `MjRetrofit` reads `training/models/v2` and `rigs/v2`; it would read v3, the
  prefabs rebuilt, `PoBox/Mj/Check Every Boxer Against Training`, fingerprints and C MuJoCo recordings from v3, the gate
  and parity tests; one boxer's v3 brains go in together (footwork, get-up, match), never mixed with v2 bodies.
- Bench (`bench_match`) is staged, not run: it times whole PPO iterations on the GPU, which is training's work.
- Checks: `tools/smoke_flags.py --compare` identical; `--flags` all pass on v2 and v3; one CPU iteration each of a v3
  gauntlet (every new flag), get-up (`--models models/v3`), hand-over bank and exam_v2 (`--models models/v3`) ran clean
  (throwaway output deleted). Unity with the new `MjBoxer.Drive`: MjParity 3/3, MjGate 24/24 (v2 unchanged).

## 2026-10-16 23:00, overnight (owner: "train and improve all behaviours ... for next 10 hours")

Decision: one match round for all eight on v2 with every improvement staged so far, then exams; the old day queue (m2/m3
under the pre-rules terms, 26 jobs never run) is superseded and backed up in `logs/queue.before-night-2026-10-17.json`.
The queue is `training/queue_night_staged.json` (copied into `logs/queue.json`). v3 (~16 h) does not fit in 10 h.

1. `rules_punch` (the world rules into every v2 config, fatigue 8000 included, so the exams box under them), `bank_trump`
   (Trump had no hand-over bank).
2. `m10_NAME` for Matt (from m9, 196 iterations under the rules), Zombie (sf1), Nick, Grandpa, Grandma (m1), Trump
   (shipped), Kim (m7), Sokol (m2): `queue_punch_staged.json`'s m9 recipe plus rc2_matt's terms: punch rules as flags,
   self-contact 0.02, repeat decay 0.7 and combinations 0.1 (the one-punch spam), hurt starts 0.2 with the clinch 0.1,
   ropes 0.05 and cut-off 0.05, hand-over share 0.3 -> 0.5 (carrying on failed m9 and rc1 at 52/58), 600 iterations
   capped at 0.75 h, stopping early once landed >= 1.2/s, falls <= 0.03 and repeat share <= 0.6 hold 40 iterations.
   Grandma and Grandpa (attack 0.35/s and 0.02/s in D8) on Kim's attack recipe: hit 2.0, taken 0.3, seek 0.3.
3. `exam_m10_NAME` after each (3.5.0, 60 trials, 12 rounds, `logs/book_punch.json`); last `exam_punch_baseline`
   (the shipped brains under the same rules), then `queue.stop`. Estimate ~8 h + 1.7 h baseline.

Smoke first: the m10_matt command, 2 iterations at 512 rings, ran clean (`checkpoints/smoke_m10`, throwaway). Unity stays
open in edit mode (a clean-up pass is editing scripts; no play mode, so the runner is not held). Nothing is promoted
overnight: the exams decide and promotion is done with the editor.
- **Results (23:01-05:07, then the exams 05:51-06:59).** All eight m10 runs ran their 0.75 h (398-480 iterations each, ~19k samples a
  second; the first half hour of m10_matt ran at ~8k while a UI capture held play mode). **The night's exams were void**: `logs/book_punch.json`
  named the m9 runs (staged, never trained), so every exam stopped on a missing file in 10 s. A new book, `logs/book_m10.json` (each boxer's
  m10), and the eight exams again (`exam2_m10_NAME`, 3.5.0, 60 trials, 12 rounds, ~8 min each). Lesson: a queue that trains new runs needs
  the exam book written for those runs, checked by one two-trial exam before the night starts.
- **No boxer passes all six lines; the shipped brains pass none of eight under the same rules** (`exam_punch_baseline`, shipped against
  shipped; the m10 exam is m10 against m10). Lines passed, shipped -> m10: Matt 4 -> 5, Zombie 3 -> 5, Nick 3 -> 5, Grandpa 2 -> 2,
  Grandma 2 -> 3, Trump 3 -> 5, Kim 3 -> 4, Sokol 4 -> 5. **No line lost anywhere.** Gained: footing for Matt, Zombie, Nick and Trump
  (falls 1.1-8.1 a minute -> 0.0-0.1); carrying on for Zombie, Nick, Trump, Kim and Sokol (hand-over share 0.5); attack for Grandma
  (0.19 -> 0.64 a second). Still failing: Matt carrying on (51/57, mark 90%); attack for Zombie (0.72/s), Nick (0.79), Trump (0.44),
  Kim (0.40), Grandpa (0.02: it blocks, 0.66 a second); footing for Grandpa (2.7), Grandma (7.0), Kim (2.0), Sokol (1.6); Grandma's
  get-up 0/60 and Grandpa's 24/60 (the D8 get-up problem, untouched by a match round).
- **The punch variety terms did not work at these weights.** Training's repeat share rose for every boxer (first 20 -> last 20
  iterations: Matt 0.66 -> 0.76, Sokol 0.76 -> 0.89, Trump 0.17 -> 0.60), combinations stayed at ~0.001 a second and the clinch at 0:
  with repeat decay 0.7 and combo 0.1 the landing pay still wins, and hurt starts at 0.2 never dazed anyone far enough to clinch.
  Matt and Sokol still land 0-1% to the head. Next, if wanted: repeat decay 0.4 with the head weighted up (`--head-w`), and combo 0.3.
- **Not promoted.** m10 beats the shipped brain on every line for seven of eight, under the rules the game now plays by, so promotion is
  recommended, but it is more than copying the ONNX: these brains read their reserve and daze (`--obs-state`, the config needs
  `obs_state`), and Matt, Nick, Grandma and Grandpa trained under the speed limit, which the game turns on only with their retrained
  footwork (D8). It needs the editor (`MjRetrofit.PromoteBoxing`, gate and parity tests), which was held all night by a dialog.
  Report: `SCREENSHOTS/2026-10-17-night/report.html`.

## 2026-10-17 23:35, overnight (owner: "train and improve all behaviours ... for next 10 hours")

Decision: one more match round for all eight, `m11_NAME` from m10 against the others' m10, each aimed at the lines its
`exam2_m10` failed; the grandparents' get-up first. Queue `training/queue_m11_staged.json` (22 jobs, copied into
`logs/queue.json`; the old one backed up as `logs/queue.before-night-2026-10-18.json`). Estimate ~9.2 h.

- **New flag `--slow-v`** (envs/boxing.py, train_gauntlet.py; off by default): a landed punch slower than this pays
  (speed / slow_v)^2 of its worth. Why: Zombie (3.6 m/s), Nick (3.3), Kim (2.7, mark 4.4) and Grandpa (1.3, mark 3.0) fail
  attack on the speed of what lands, not only the count, and nothing paid for speed above the style's `power`.
  `tools/smoke_flags.py --compare` against a baseline saved just before: identical; `--flags` all pass.
- **Get-up**: `u1_d9_grandpa_matt` (D8's recipe from his shipped get-up, 600 iterations: D8 reached 83-90% unaided in
  training, but that checkpoint is gone, so the exam used the shipped one, 24/60); `u1_d9_grandma_matt` (from D8's, 1500
  more: D8 stalled at 0% unaided, help 0.22). New banks `logs/handover_d9_NAME.npz` (the v2 banks are kept).
- **m11, common**: m10's recipe with variety stronger (repeat decay 0.7 -> 0.5, combo 0.1 -> 0.25: at m10's weights repeat
  share rose for every boxer), 700 iterations capped at 0.8 h, same early stop. Per boxer:
  Matt hand-over share 0.7 (carrying on 51/57), Grandpa and Trump weighted 2 (0 and 0.42 landed/s against them).
  Zombie, Nick: hit 2.0, seek 0.3, slow-v 4, weight 2 on the opponents they land ~0 on. Sokol: fall penalty 16, Kim 2,
  Trump 3 (his falls). Kim: hit 2.0, seek 0.3, slow-v 4.4, fall penalty 16, Nick and Sokol 3. Trump: hit 2.0, seek 0.3,
  slow-v 4, Grandma and Matt 2. Grandma: fall penalty 16, Trump 3 (8.2 falls/min), the new bank. Grandpa: hit 2.5,
  seek 0.3, slow-v 3, std reset 0.3 (std was 0.11 and he lands 0.02/s), Trump 3, the new bank.
- **Exams**: a `book_m11` job writes `logs/book_m11.json` just before them (m11 where it trained, else m10; the d9
  get-up where it trained), so a failed run cannot void the exams as `book_punch` did; then the eight exams on 3.5.0.
- Smoke first: `m11_grandpa`'s command, 2 iterations at 512 rings, ran clean (throwaway, deleted); the book job ran.
  Unity is open in edit mode (opened 23:25 by someone else; not closed): the runner holds at job boundaries if Play
  mode loads the GPU. Nothing is promoted overnight.
- **Results (23:35-08:15; all 22 jobs exit 0).** Get-ups: `u1_d9_grandpa_matt` 600 iterations (bank 1500 states); `u1_d9_grandma_matt` 1500
  (bank only 304 states from 9000 knockdowns: she still seldom stands unaided). m11: all eight ran the 0.8 h cap (471-524 iterations).
  Exams, m11 against m11 (`logs/exam_m11_NAME.json`), lines m10 -> m11: Matt 5 -> 5 (carrying on now passes 53/57, attack lost: 0.65/s at
  3.5 m/s), Zombie 5 -> 5 (attack 0.72 -> 0.42/s), Nick 5 -> 4 (footing lost, 2.33/min), Sokol 5 -> 5 (footing 1.56 -> 0.89), Kim 4 -> 3
  (carrying on lost, falls 5.4/min), Trump 5 -> 4 (footing lost, 1.33), Grandma 3 -> 3 (footing passes 7.0 -> 0.11, attack lost 0.24/s;
  get-up 0/60), **Grandpa 2 -> 4** (footing 0.0; get-up 49/60 in 3.3 s with d9; attack still 0.02/s). 33 lines against m10's 34.
- **Why attack fell**: the training field was the m10 brains, which land far more (on Matt at iteration 0: 0.68/s against 0.45/s for m10's
  field), so taking less paid more than landing (Matt taken 0.68 -> 0.24/s, landed 0.73 -> 0.55). Repeat decay 0.5 / combo 0.25 cut a landed
  punch's worth and still did not mix the punches (repeat share rose for 7 of 8, combinations ~0). `--slow-v` raised the speed of what lands
  in training (Zombie 3.4 -> 4.3, Nick 3.6 -> 3.9, Grandpa 1.9 -> 2.7) but not in the exam (Zombie 3.4-3.6, Nick 2.9-3.2). Clinch 0 again.
- **Mixed roster exam** (`logs/book_mix.json`, `logs/exam_mix_NAME.json`, run 8 in parallel on the CPU, ~15 min): Matt, Zombie, Nick, Kim,
  Trump m10; Sokol, Grandma, Grandpa m11; grandparents on d9 get-ups. Lines: Matt 5 (guard fails: 0.35 head shots/s), Zombie 5, Nick 4
  (falls 6.3/min, all against Sokol m11), Sokol 5 (footing 0.33, mark 0.2), Kim 3, Trump 5, Grandma 3, Grandpa 4: 34. Each exam's field
  differs, so a line near its mark flips on the opponents alone.
- **Recommendation (not done; needs the editor, task M2)**: promote that mix. Grandma's get-up is her body under the rules, not training
  time (2100 iterations, never stands unaided): owner's call. Next: repeat decay back to 0.7 with a head term instead; a bag stage for
  Grandpa's punching; Kim's falls need her footwork under the rules, not a bigger penalty. Report: `SCREENSHOTS/2026-10-18-night/report.html`.
  TensorBoard's throwaway `smoke_m11` run deleted.

## 2026-10-18 10:55-12:45, promotion and m12_zombie (owner: "do all", then "finish in 1 hour")

- **In the game now** (M2): Matt, Nick, Kim, Trump box with m10, Zombie with `m12_zombie`, Sokol, Grandma and Grandpa with m11. Every
  `Assets/Boxers/NAME/config.json` is the trainer's `models/v2` config (speed limit on for all eight) plus `obs_state: true`. Footwork
  under the speed limit: f1 for Matt, Nick, Grandma; f1b for Zombie; `f2_grandpa` for Grandpa (f1_grandpa and u1_d8_grandpa_matt were
  gone from disk), each with its C MuJoCo 3.5.0 recordings. Grandpa gets up with `u1_d9_grandpa_matt` (exported at 103 inputs).
  **Grandma has no get-up brain**: she never stands unaided under the rules, so the referee stands her up, as Sokol was before his.
- **Tests**: gate 24/24, parity 3/3 (the fingerprint check now expects the speed limit's damping, force / speed limit a joint, added
  by `MjBoxer.Bind`; Matt's `reference_hold.json` recorded again with it), state flow 2/2, free-for-all 1/1.
- **`m12_zombie`** (from m10 against the game's roster, m10's variety weights 0.7 / 0.1 with `--slow-v 4`, hit 2.0, seek 0.3; 400
  iterations in 0.8 h, slowed by the Unity tests): exam against that roster 5 of 6, attack 0.61 -> 1.14/s at 4.0 m/s (just under the
  mark), guard stops 57% (was 1%), no falls. Promoted. m12 for Nick, Trump, Kim dropped for time (still in `queue_m12_staged.json`).
- **`f2_grandpa`**: D8's footwork recipe again, 800 iterations: walk, turn, joints pass; stand 93% (mark 95%, 2 of 30 down under shoves;
  f1 had 100%). Promoted anyway: his match brain needs the speed limit, and the footwork brain only stands and walks between exchanges.
- Not done: the CPU Elo re-run (13 min, CPU), skipped for the hour.

## 2026-10-18 afternoon, Grandma's get-up: the speed limit, not her mass (owner: "train" Grandma to get up)

- **Body, measured (C MuJoCo 3.5.0, `models/v2`).** Grandma 87.8 kg / 1.70 m (BMI 30.4, the roster's BMI-30 ceiling; her mesh
  measures 43.8) and Grandpa 79.8 kg / 1.62 m (also the ceiling) have the same strength per kilogram: knee 1.57 N m/kg (Matt
  3.22, Nick 3.69), the same speed limits (knee 15 rad/s, hip 11.25), the same fatigue reserve per kilogram (48.2 J/kg; Matt 99).
  Static squat, feet flat, centre of mass over the feet (gravity bias minus the floor's push through the foot Jacobians): the
  hip is the limit, 67% of hers (Grandpa 63%, Matt 30%, Nick 29%); knee 9-23%. With a balance hand on the pelvis (horizontal
  and turning only, nothing vertical) and the speed limit's damping on, she rises from a deep squat (knee 2.0 rad) to 0.88 of her
  height in 0.35 s, 0.38 s at 60% drive (Grandpa 0.38 / 0.42). Her body can stand; her mass is not what stops her.
- **The muscle rules, one at a time** (getting-up exam only, 30 knockdowns, configs edited in a scratch copy of models/v2):
  her shipped get-up (`checkpoints/onnx/getup_grandma.pt`) stands 30/30 in 2.2 s with no rules, 29/30 with fatigue only, **0/30
  with the speed limit** (with or without fatigue). Grandpa's d9 get-up on her body: 0/30 under every variant (the bodies differ:
  her legs are shorter, thigh 0.365 v 0.377 m, shin 0.315 v 0.336, so her trunk is higher). D9's own brain: 0-2/30 under every
  variant: 2100 iterations with the lifting hand left it unable to stand even without the rules.
- **Fix, recipe only:** ease the speed limit in rather than lifting her. New flag `train_getup.py --speed-ramp S0` (envs/getup.py,
  off by default, old runs unchanged): the speed limit's damping (force / speed limit a joint) is on at a share that starts at S0
  and moves 0.005 an iteration, up while the learners get up from the floor more than 60% of the time, down under 40%; saved in
  the checkpoint; `--done-when` also needs all of it. Checked: knee damping 9.67 at share 1, 0.50 at 0, 5.08 at a half.
- **Run** `u1_d10_grandma_matt`: from her shipped get-up, Matt's frozen, `--assist 0 --speed-ramp 0 --reset-std 0.3 --max-std 0.4`,
  2000 iterations capped at 1.5 h, then `exam_d10_grandma` (getting up, 60 knockdowns, 3.5.0, `logs/book_d10_grandma.json`).
  Mark (R3, frail): 60% on her feet within 9 s.
- **17:14-17:18 `u1_d10_grandma_matt` (REJECTED, stopped at 78).** From the floor 0% within 20 iterations, help creeping up from 0.
  Why, measured on the GPU env without learning (`checkpoints/onnx/getup_grandma.pt`, speed share 0): it is up at some point after
  40% of falls, still up at the end after 27%, against 30/30 in the C MuJoCo exam. Two causes: (1) **the first PPO update from an
  ONNX-rebuilt brain tears it up** (KL 61-404 at iteration 0 in D8, d9 and here: Adam's first step moves every weight by the full
  learning rate, and the smaller the action noise the bigger the KL); with the helping hand on (D8, d9) the brain survived it,
  without it it does not. (2) Action noise of 0.3-0.4 a joint is a lot for a body near its limits. Fixes: `--lr 3e-5` on an ONNX
  start (it adapts up), noise 0.15 (max 0.2), and `--assist-max 0` (new, train_getup.py: the helping hand's ceiling, 0 = never),
  so the speed limit is the only thing eased. The speed controller now counts the exam's measure (up at any time after a real
  fall) instead of still up at the end. `u1_d10b_grandma_matt` (same, before the lr fix: KL 405 at iteration 0, from the floor 1%
  at 10) was killed by a CUDA out-of-memory with Unity in Play mode (GPU 99%, 9.4 of 12 GB) before a save.
- **Every shipped get-up under the game's rules** (current `Assets/Boxers/*/getup.onnx` rebuilt to .pt, the exam's get-up line,
  3.5.0, 30 knockdowns; the Unity session reported nearly every in-game knockdown is a KO): Zombie 6/30, Nick 14/30, Grandma 0/30
  FAIL; Kim 29/30, Sokol 30/30, Trump 28/30, Grandpa 25/30, Matt 24/30 (exactly the mark) pass. Carrying on fails for Matt 18/24,
  Kim 26/29, Nick 8/14, Grandpa 12/25. Kim's in-game KOs were her reserve (empty when she went down: the game now starts a get-up
  at reserve >= 0.5). Parity check of that: two match brains boxing from the marks in the exam ring (C MuJoCo, 10 s, |tau*qdot|):
  Kim 773 W (reserve 1.0 -> 0.29 at 5 s -> 0.0 at 10 s), Nick 646 W (-> 0.25), Zombie 601 W (-> 0.41), Matt 327-568 W (-> 0.47-0.69).
  So the trainer's brains empty a frail-to-average reserve in 7-15 s of boxing too: not game-only (the game's ~1.2 kW is about
  1.5-2x the exam's). The get-up env already starts each knockdown at reserve 0.3-1.0 (BoxingEnv's reset).
- **Staged** (`logs/queue.json`, held by the Unity session's `logs/queue.hold` until its Play mode is done): `u1_d10c_grandma_matt`
  (the d10 recipe + `--lr 3e-5 --assist-max 0`, 2000 iterations, 1.5 h), then get-up retrains on Grandpa's d9 recipe (from the
  shipped get-up, partner frozen, max std 0.4, 600 iterations, 0.5 h) + `--lr 3e-5` for Matt (v Zombie), Zombie and Nick (v Matt).
  Each is followed by its get-up exam (60 knockdowns, 3.5.0, carrying on kept) and `tools/export_getup_v2.py` (new: writes
  `latest_NAME.onnx` at 103 inputs next to the checkpoint, as d9 Grandpa's, only if getting up passed; Matt also has to beat the
  shipped 80%). Kim, Trump, Sokol skipped (they pass; the game's reserve fix covers them).
- **Kim v Sokol parity check** (Unity: Kim down 1-3 s after the bell in 30/30, drives at their limit). C MuJoCo 3.5.0, shipped
  Assets match brains (obs_state on), exam marks, models/v2 configs, punch rules, 20 x 10 s: down within 3 s 0/20 (pelvis 0.74 m
  at 0.3 s against her 0.76 standing; mean |torque|/limit 0.23), so the early falls are a Unity parity break (the Unity session is
  on it). But down within 10 s 20/20 (3.8-9.5 s, median ~5 s) as her reserve runs out (0.00 at the end, Sokol 0.65); her shipped
  get-up then stands 5/20. Her m10 brain spends her 8 kJ x strength in ~5 s: a training problem for her match brain (pacing).
- **Get-up observation parity** (the Unity session's dump `Temp/getup_dump_a_.json`, Trump red v Matt blue, first get-up step):
  loaded into `models/v2/trump_vs_matt_spar.xml` (nq 112 / nv 102 / nu 42 as the dump), C MuJoCo 3.5.0, the exam ring's observation:
  102 of 103 match within 1e-3 (index 82, the opponent's head velocity, 0.001: the dump has no previous head position); Trump's
  shipped get-up gives the same actions on both (max 0.001). The game's dumped ctrl is up to 3.3 rad from q and 2.8 rad from
  default + 0.5 x action: the break is after the brain, in what the game drives with (handed to the Unity session).
- **Reserve while rising.** The game now takes nothing from the reserve while the get-up brain has the body (the Unity session
  measured ~12 kW in-game while rising, reserve 0.50 -> 0.39 in 4 control steps). The C exam does drain: Trump's shipped get-up
  (trump_vs_matt, 10 trials) puts out 2.0 kW on average while rising, reserve to 0.00 (mean 0.26), and still stands 10/10 in 1.7 s
  (carrying on 4/10): the game's 6x higher power is a separate drive-parity question, handed back. New flag `train_getup.py
  --no-drain` (envs/getup.py: the reserve drawn at the reset, 0.3-1, kept for the episode), added to the four staged d10 runs
  to match the game. The exam keeps draining, so it stays the harder test.
- Correction (Unity session): the dump's ctrl interleaves the two boxers (a_ even, b_ odd); Trump's ctrl[::2] after Drive equals
  clip(default + 0.5 x clip(action, 3)) exactly, so the drive is at parity. The "12 kW" was the first 4 control steps' slope as
  the drives come back from 0.04. The real game/exam gap was the starting reserve (exam 1.0; game what was left, 0.39 in the test).
  Game rule kept: a get-up starts at reserve >= 0.5 and nothing drains while rising, as the --no-drain retrains.
- **21:10 `u1_d10c_grandma_matt` started** (queue released by the Unity session). Iteration-0 jump 0.03 (was 61-404) with `--lr 3e-5`. First 80
  iterations: from the floor 80-100%, unaided 62-100%, 1.7-2.2 s to stand, no help; the speed limit eased in to 32% (+0.5% an iteration,
  full at ~215 if she keeps up). ~50-85k samples/s. Matt, Zombie, Nick get-ups, exams and exports follow in the queue.

## 2026-10-18 23:00, overnight (owner: "do all" to the 10-hour plan: fatigue, Grandma at half the speed limit, Kim, m13, exams, Melee prep)

Running status, one line a milestone: `training/logs/night_status.md`. Nothing promoted (the Unity session promotes).

- **Shipped brains rebuilt** from Assets/Boxers/*/{match,getup}.onnx into `checkpoints/onnx_1018/` (`tools/onnx_to_pt.py`, new
  `--obs-state`: the match brains are marked as reading their state, or `train_gauntlet.py --obs-state` would zero those inputs' weights).
- **Incident (mine)**: a 10-worker CPU sweep started 22:55 coincided with a CUDA out-of-memory in `u1_d10_zombie_matt` (iteration ~270);
  its process hung in Warp's teardown and I may not kill it, so the old runner is stuck. CPU tools now run with CUDA_VISIBLE_DEVICES=-1
  (the trainer kept 56k samples/s beside 8 such workers). `logs/queue.stop` and `logs/u1_d10_zombie_matt.stop` written; a second runner
  (`tools/queue_runner.py --queue logs/queue_night.json`, new option: own state and stop file, same queue.log) took over: Zombie from
  its iteration-250 save as `u1_d10b_zombie_matt` (350 more), Nick's d10 as staged, then tonight's jobs.
- **Fatigue recalibrated: fatigue_j 8000 -> 20000** (x strength, rec_s 20). `tools/fatigue_sweep.py`: the shipped match brains, all 28
  pairs with a model, 45 s from the marks under the punch rules and daze, 3 seeds, C MuJoCo 3.5.0, 588 bouts in 16 min on 8 CPU workers.
  Median boxer's reserve at 40 s: 8000 0.20, 16000 0.41, 20000 0.47, 24000 0.52, 32000 0.54. At 16000 Kim (the busiest) is empty at
  18 s (earliest 8 s); at 20000 at 30 s (earliest 12 s), Nick 0.23 and Trump 0.31 at 40 s, the grandparents and Zombie hardly tire.
  The game spends ~1.5-2x the exam's power, so the larger of the two. Kim's brain is the outlier (its own job below).
- **Exam option `--getup-no-drain`** (tools/exam.py): the get-up test as the game runs it (reserve 0.5 when the get-up starts,
  nothing drained while rising). And the config key **`getup_speed_share`** is read by the exam's get-up test: while the get-up brain
  has the body a dof's damping is base + share x force / speed limit (envs/getup.py's speed_share), full again for the match.
  Checked on Grandma (20 knockdowns): shipped get-up 20/20 at share 0, 0/20 at 1 and 0/20 at 0.5; d10c 5/20 at 0 and 0/20 at 1 with
  the drain, 12/20 at 0 and 8/20 at 0.5 (5.3 s) without it. So the exam's drain (her 8 kJ x 0.53 reserve gone in ~2 s at 2 kW) was
  half of why d10c failed 0/60, and training's "from the floor 50% at 92% of the speed limit" does not carry to the C exam at 100%.
- **Grandma's get-up at a fixed half speed limit** (`train_getup.py --speed-share 0.5`, new: held, not ramped, not read from the
  checkpoint): `u1_d11_grandma_matt` from d10c, `--assist-max 0 --no-drain`, lr from d10c (6e-5), std 0.2, 1600 iterations capped at
  0.8 h. Exams (60 knockdowns, 3.5.0) with the game's rule (`--getup-no-drain`) and with the old drain; exported only if the game-rule
  exam passes the frail mark (60% within 9 s). `"getup_speed_share": 0.5` is in her 8 models/v2 configs.
- **Kim**: her shipped footwork brain passes its ladder under the speed limit (stand 97%, walk 0.00 falls/min, turn 100%;
  `logs/exam_fw_kim_shipped.json`), so no footwork top-up: straight to her m13 round, fall penalty 20, Nick and Sokol weighted 3.
- **m13** (`tools/m13_job.py NAME`): from the shipped brain against the game's roster (each opponent its m13 once landed tonight, else
  shipped), under fatigue 20000. m12_zombie's recipe (guard 1% -> 57%) plus: repeat decay 0.7, combo 0.1 (the log's advice) and
  **`--head-mult 2`** (new: the head's style price 1.5 -> 3; Matt lands 0% to the head), **`--arms-w 0.01`** (new, small: r["legs"]'s
  pull to the guard pose for shoulders and elbows, so the hands come back to the chin; no such term existed), **`--foe-state`** (new:
  opponents whose brains read their reserve and daze get them, as in the game; until now every opponent saw zeros there, an empty
  reserve), `--lr 3e-5` (the first-update KL fix), std 0.3, 700 iterations capped at 0.6 h, `--no-onnx` (exported after the exam only).
  Per boxer: Matt block 1.0, head 2.5, hand-over 0.7; Nick and Sokol fall penalty 16; Grandma 16; Grandpa hit 2.5, slow-v 3, std 0.3.
  CPU smoke (32 rings, 2 iterations, every new flag, `--bodies 2`): KL 0.02 at iteration 0; throwaway deleted.
- **Melee prep** (`--bodies N`, envs/boxing.py `lying_bodies`): N boxers down on the canvas as solid mocap bodies (a lying adult's
  capsules), each episode placed clear of both fighters (0.9 m) and each other, a quarter of the time taken away. ponytail: a fixed
  shape, not a ragdoll, and not in the observation (a brain feels a body, it does not see one; seeing needs new inputs and the game's
  observation changed with them). CPU check: 70-77% of episodes have each body in the ring, none nearer a fighter than 0.90 m.
- **Two hangs.** 01:40 `m13_matt` (iteration ~157) hit a CUDA out-of-memory with nothing of mine on the GPU (5.6 GB held elsewhere) and
  hung in Warp's teardown as 22:55's had. So the 22:55 fault may not have been the sweep either. Fix for the future:
  `tools/train_resilient.py` now ends its own run when the run's CSV stops growing for 15 min (exit 99, restarted from its save).
  Matt carried on as `m13b_matt` from the iteration-150 save; the remaining m13 runs use 3072 rings (from 4608) and 0.5 h.
- **Grandma's get-up graduated**: `u1_d11b_grandma_matt` (d10c -> d11 218 iterations -> d11b 1400 on hard falls only, new
  `train_getup.py --slack-lo 1.2`). Why hard falls: at d11's iteration 150 training read 82-88% up from the floor at the 0.5 share,
  the C exam 9/20; training counts every episode that touched the floor (slack from 0.15 s, mostly stumbles), the exam only 1.6-2.6 s
  slack after a hard shove. The trainer's env evaluated deterministically on hard falls only (MuJoCo Warp on the CPU, 63 falls):
  70%, so most of the gap was the mix of falls, not physics. Exam, 60 knockdowns, share 0.5, game rule: 46/60 (77%) in 4.3 s, pass
  (frail mark 60% in 9 s); with the old drain 26/60. Carrying on 34/46 (her m11 match brain). Exported at 103 inputs.
- **Get-ups exported from the d10 queue**: Matt 60/60 (1.7 s), `u1_d10b_zombie_matt` 56/60 (2.1 s; shipped 6/30 under the game's
  rules), `u1_d10_nick_matt` 56/60 (1.7 s; shipped 14/30); carrying on 56/56 and 54/56. Exams at fatigue 8000 with the drain.
- **Shipped baseline at fatigue 20000** (`logs/exam_shipped_1018.json`, the game's roster against itself, 12 rounds, match lines
  footing/attack/guard/chin): Matt 2, Zombie 4, Nick 2, Sokol 3, Kim 2, Trump 4, Grandma 4, Grandpa 3 = 24 of 32.
- **m13_kim** (350 iterations, 0.6 h): training falls 0.50 -> 0.02 of episodes, landed 0.27 -> 0.77/s. Exam vs the shipped roster:
  2 -> 3 lines; footing 2.78 -> 0.11 falls/min, attack 0.35/s at 3.7 -> 0.99/s at 4.1 m/s (mark 4.4: fails on speed), guard 67%,
  chin 0. Reserve at 20000: 0.12 at 40 s and never empty in the median bout (shipped: empty at ~30 s). Exported.
- **m13 results** (each against the shipped seven, fatigue 20000, 12 rounds; `logs/exam_m13_NAME.json`, `logs/compare_m13_NAME.log`).
  Match lines (footing, attack, guard, chin) shipped -> m13: Matt 2 -> 4 (m13b; guard 0% -> 40%, head shots taken 0.56 -> 0.01/s, chin
  1.65 -> 0.02; landed 1.31 -> 0.63/s), Zombie 4 -> 4 (guard 72 -> 86%), Nick 2 -> 4 (footing 1.11 -> 0, attack 0.78/s at 3.3 -> 1.19/s at
  4.5 m/s), Sokol 3 -> 4 (head shots taken 0.34 -> 0.03/s; landed 0.87 -> 0.53), Kim 2 -> 3, Trump 4 -> 4 (all a little better),
  Grandma 4 -> 3 (attack 0.54/s at 4.6 -> 0.41 at 3.2: not exported), Grandpa 3 -> 3 (attack 0.02 -> 0.14/s, still fails). 24 -> 29.
  Seven exported (`tools/m13_compare.py`: not fewer lines than shipped) as `checkpoints/m13[b]_NAME/latest_NAME.onnx`, obs_state brains.
- **What the recipe did**: the guard terms worked again (Matt, Sokol, Zombie, Grandpa take almost no head shots), at the price of
  landing less for Matt and Sokol. The head price (x2, Matt x2.5) did not move Matt (0 -> 3% to the head) or Sokol (2 -> 0%): their
  punches reach only the trunk from the range they keep; a stance or range term would be next, not more head price. Repeat share
  rose with the punching (Kim 0.25 -> 0.52, Nick 0.27 -> 0.43) even at repeat decay 0.7; Nick's combinations 0.07 -> 0.25/s, the
  only boxer to combine. The arm pull (`--arms-w 0.01`) is untested by any exam line (hands at the chin are not measured).
- Report: `SCREENSHOTS/2026-10-19-night/report.html`. Melee jobs staged in `training/queue_melee_staged.json`, not run.
- **Mixed exam, the roster the game would get** (`logs/book_night_mix.json`, against itself, 12 rounds, 30 knockdowns, game get-up
  rule): 39 of 48 (last night's mix 34 under fatigue 8000). Nick passes all six lines, a first. Footing, guard, chin, getting up pass
  for all eight; attack fails for seven (0.04-0.50/s: when every boxer guards, less lands); carrying on fails for the grandparents.
  Next: a range/stance term for attack; the grandparents' hand-over. Nothing promoted; GPU idle from 05:43.
- Check: with every new flag off, tonight's envs/boxing.py and envs/getup.py give numbers identical to the committed ones
  (`tools/smoke_flags.py`, same models at fatigue 8000). `logs/smoke_baseline.npz` (2026-10-16) no longer matches either: the
  configs changed since (match obs/reward, getup obs 0.002); re-save it before the next flag change.
