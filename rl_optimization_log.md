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
