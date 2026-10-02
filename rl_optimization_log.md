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
