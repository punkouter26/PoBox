# Purge manifest — 2026-09-08

Recorded before deletion. results/ is gitignored, so this file is the only record.

## Training runs removed

| run | MB | last checkpoint | config |
|---|---|---|---|
| boxer_locomotion21 | 95 | Boxer-4999000.onnx | Config/BoxerLocomotion21.yaml |
| boxer_locomotion22 | 79 | Boxer-3999000.onnx | Config/BoxerLocomotion22.yaml |
| boxer_locomotion23 | 47 | Boxer-1999000.onnx | Config/BoxerLocomotion23.yaml |
| boxer_locomotion24 | 30 | Boxer-999000.onnx | Config/BoxerLocomotion24.yaml |
| boxer_locomotion26 | 385 | Boxer-22999000.onnx | Config/BoxerLocomotion26.yaml |
| boxer_locomotion27 | 401 | Boxer-23999000.onnx | Config/BoxerLocomotion27.yaml |
| boxer_locomotion28 | 95 | Boxer-4999000.onnx | Config/BoxerLocomotion28.yaml |
| boxer_locomotion29 | 192 | Boxer-10999000.onnx | Config/BoxerLocomotion29.yaml |
| boxer_locomotion30 | 208 | Boxer-11999000.onnx | Config/BoxerLocomotion30.yaml |
| boxer_locomotion31 | 256 | Boxer-14999000.onnx | Config/BoxerLocomotion31.yaml |
| boxer_locomotion32 | 1 | (none — no checkpoint reached) | Config/BoxerLocomotion32.yaml |

## Why these were safe to delete

Final TensorBoard scalars for every locomotion run, read before deletion.
boxer_locomotion25 (the promoted gen25 balance brain) dominates every other
run on every metric: 1629 steps between falls against the next best 119, and
upright fraction 0.99 against 0.83. None of runs 26-32 was an unpromoted winner.
Run 31 has the highest alternation at 0.11, still far under the 0.601 of the
shipping walk brain, on a reward of 0.05 -- a failed run, not a walk candidate.

```
run                              step    reward  metrics
boxer_locomotion21            5250000    0.3846  {'Alternation': 0.01, 'StepsBetweenFalls': 41.35, 'UprightFraction': 0.62}
boxer_locomotion22            3950000    0.3924  {'Alternation': 0.0, 'StepsBetweenFalls': 42.44, 'UprightFraction': 0.62}
boxer_locomotion23            2400000    0.2742  {'Alternation': 0.0, 'StepsBetweenFalls': 37.83, 'UprightFraction': 0.6}
boxer_locomotion24            1900000    0.1920  {'Alternation': 0.0, 'StepsBetweenFalls': 36.06, 'UprightFraction': 0.58}
boxer_locomotion25           21850000    0.9455  {'Alternation': 0.0, 'StepsBetweenFalls': 1628.97, 'UprightFraction': 0.99}
boxer_locomotion26           23850000    0.3119  {'Alternation': 0.0, 'StepsBetweenFalls': 91.01, 'UprightFraction': 0.78}
boxer_locomotion27           24600000    0.6314  {'Alternation': 0.01, 'StepsBetweenFalls': 119.27, 'UprightFraction': 0.83}
boxer_locomotion28            5250000    0.3129  {'Alternation': 0.0, 'StepsBetweenFalls': 92.38, 'UprightFraction': 0.78}
boxer_locomotion29           11700000    0.3638  {'Alternation': 0.01, 'StepsBetweenFalls': 94.79, 'UprightFraction': 0.79}
boxer_locomotion30            8350000    0.3452  {'Alternation': 0.06, 'StepsBetweenFalls': 91.31, 'UprightFraction': 0.78}
boxer_locomotion31           14750000    0.0531  {'Alternation': 0.11, 'StepsBetweenFalls': 54.04, 'UprightFraction': 0.68}
boxer_locomotion32                  -         -  (no events)
```

## Kept

- `results/boxer_locomotion25` -- produced Locomotion_gen25, the shipping balance brain.
- `results/nick` -- the MuJoCo Warp line, and TensorBoard was serving it live on port 6007.

The runs behind Locomotion_gen18_34M and Locomotion_gen20 (boxer_locomotion18,
boxer_locomotion20) were ALREADY absent from results/ before this purge, despite
gen20's SOURCE.txt claiming 'Full run kept at results/boxer_locomotion20/'.

## Brain removed: Locomotion_gen20

Superseded by Locomotion_gen25, which beats it on every body (167.9 steps
between falls against 91.7 under shove). Referenced by no scene -- only by
its own dossier and the spectator kit's dossier list, from which its entry
was removed. Its SOURCE.txt is preserved verbatim below.

```
boxer_locomotion20, checkpoint Boxer-4496768.onnx (step 4,496,768 of a 20M
budget). The run was stopped early, on request, while still improving.

A DEDICATED BALANCE BRAIN. Trained with speed_command_max pinned at 0.0 the
whole way, resumed from boxer_locomotion18. Its gait is deliberately sacrificed
-- catastrophic forgetting at speed 0 was expected and accepted, because the
walk race keeps Locomotion_gen18_34M. Two mini-games, two brains.

SHIPS IN SCN_TEST_BALANCE_CONTEST, replacing gen 9. Measured against gen 9 on
2026-08-23 under two independent protocols, both favouring gen 20:

  SCN_TRAIN_LOCOMOTION, 16 capsules, commanded speed 0, no hazards
    steps between falls    gen 9   68 (1.4 s)
                           gen 20 113 (2.3 s)      +66%

  SCN_TEST_BALANCE_CONTEST, 8 fighters, hazards active, sampled mid-round
    mean aliveTime         gen 9   2.30 s
                           gen 20  ~3.5 s          +52%

CAVEAT ON THE NUMBERS: these are NOT the same metric as the 672/118/61/56 table
in BoxerLocomotion20.yaml's header, which measured steps-to-first-fall in a
fresh episode. _stepsToFirstFall reads 0 for every fighter in the training
scene, because the reset pose topples on the tick it is applied before the
policy catches it, so that metric was unusable here. Steps-between-falls and
contest aliveTime are like-for-like between the two brains, which is what the
shipping decision needed; they are not comparable to the header table.

Gen 20 did NOT reach its config's stated success bar (capsule at or above gen
9's 672 by that other metric) because it was stopped at 4.5M of 20M. It is
shipped anyway on the strength of beating the brain it replaces, measured two
ways, in the configuration it ships in.

127 observations, ground-relative heights.

Full run kept at results/boxer_locomotion20/.
```
