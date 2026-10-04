# PoBox: training charts explained in plain English

As of 4 October 2026. The charts are in TensorBoard (`http://localhost:6006`, reading `training/logs/tb`).
Each line on a chart is one training run. The bottom axis is training rounds: in one round the boxer
practises in thousands of rings at once for about half a second of ring time, then learns from it.

Annotated screenshots of three of these charts: `abilities_and_training.html` in this folder.

---

## Tier 1: Quick look

Four charts tell you most of what you need:

| Chart | Nickname | Good sign |
|---|---|---|
| Cumulative reward | The scoreboard | climbs, then levels off high |
| Episode length | Survival time | climbs to the full 12 seconds |
| Policy loss | Confusion level | wobbles near zero and stays calm |
| Entropy / action noise | Curiosity | starts high, comes down slowly |

A boxer is ready when the scoreboard is flat and high, it survives the full practice bout, the confusion
is calm and the curiosity has come down. **But the charts never decide alone.** A boxer goes into the game
when it passes the exam (`training/tools/exam_v2.py`), which is run separately with no luck added.

---

## Tier 2: Core mechanics

### Cumulative reward: "the scoreboard"

- **TensorBoard name:** `env/ep_return`
- **What it is:** the points a boxer collects in one practice bout, averaged over all the rings. Points
  come from landing punches, staying up and facing the opponent; they are lost for being hit, falling and
  wasting effort.
- **Is the boxer generally winning more often?** If the line climbs, yes. In a 12-second boxing practice
  a boxer that falls at once scores near 0 and one that stands and trades scores 300 to 450.
- **Watch for:** a line that climbs and then sags. Zombie's peaked at 425 and ended at 374 as his falls
  crept back. A sudden drop to near zero means the boxer has started falling at the bell.
- **Do not compare boxers by it.** Each style is paid differently, so 378 for Grandpa and 291 for Nick
  does not mean Grandpa is the better boxer. Compare a boxer with its own earlier runs.

### Episode length: "survival time"

- **TensorBoard name:** `env/ep_len_s` (footwork and get-up runs). In boxing runs read `env/fall_rate`
  instead: the share of practice bouts that ended with a fall.
- **What it is:** how many seconds a practice bout lasted before the boxer fell. The most it can be is 12.
- **Is it staying alive longer?** In footwork every boxer goes from 2 to 6 seconds at the start to about
  11.9 at the end. That is the shape you want: a quick climb, then flat at the top.
- **For this game longer is better.** Nothing is "finished faster" except getting up, where
  `env/time_to_stand_unaided` should fall (the boxers end at 1.6 to 2.4 seconds).
- **Watch for:** survival time falling while the scoreboard rises. The boxer is then buying punches with
  its balance, which is how Nick's boxing run reads (falls up to 0.55 of practice bouts, punches up too).

### Policy loss: "confusion level"

- **TensorBoard name:** `ppo/policy_loss`, or `ppo_<name>/policy_loss` when two boxers learn at once
- **What it is:** how much the boxer is changing its mind in each round of learning.
- **Still figuring it out, or settled?** At the start of a run it jumps about (0.02 to 0.03 here). Once
  the boxer has a strategy it sits just under zero and barely moves (about -0.007 for all six).
- **Watch for:** big swings late in a run, or a sudden spike. Something in the practice changed, or the
  learning pace is too high. Its partner chart `ppo/kl` should stay near 0.01; the trainer slows itself
  down when it goes above that.
- **A calm line does not mean a good boxer.** Trump's is as calm as anyone's and his attack still fails:
  he has settled on a strategy that does not land.

### Entropy: "curiosity"

- **TensorBoard name:** `ppo/entropy`, and its easier twin `ppo/action_std`
- **What it is:** how much random wobble the boxer adds to its joint commands in order to try new things.
  `action_std` is that wobble in plain units: 0.5 is a wide swing on every joint, 0.12 is a small tremor.
- **Trying new things, or repeating habits?** High means it is still experimenting. Low means it repeats
  what it knows. A healthy run starts high and comes down slowly as the boxer commits.
- **The entropy number is negative here and goes down.** That is normal for this kind of trainer: more
  negative means less exploring. Read `action_std` if the sign is confusing.
- **Watch for:** curiosity that collapses in the first minutes (the boxer stops improving early), or that
  never comes down (it is still flailing). In the game the wobble is switched off entirely, so a boxer
  that only stays up with the wobble on will fall in the game. That happened to an earlier Matt.

### Other charts worth a look

| Chart | Plain meaning | Good sign |
|---|---|---|
| `env/hits_per_s` | punches landed a second | above 0.5 |
| `env/their_hits_per_s` | punches taken a second | falling |
| `env/hit_speed` | how fast the glove arrives (m/s) | above 4 (3 for the grandparents) |
| `env/turn_rate` | share of turns that faced the opponent in 3 s | above 0.9 |
| `env/track_err` | how far off the asked walking speed (m/s) | under 0.15 |
| `env/up_rate_unaided` | share that got up without help | above 0.8 |
| `env/power` | effort spent (watts) | falling, then flat |
| `perf/fps` | practice steps a second | 26,000 to 127,000 on this laptop, by skill |

---

## Tier 3: Setup guide

### Agent comparison grid

Read from the runs whose brains are in the game. Scoreboard and curiosity are from each boxer's boxing
run; survival time is from its footwork run.

| Boxer | Scoreboard (cumulative reward) | Survival time | Confusion (policy loss) | Curiosity (action noise) | Ready for the game? |
|---|---|---|---|---|---|
| Matt | **Healthy**: 376, flat | **High**: 11.9 of 12 s | **Low**: settled | **Low**: 0.12 | Nearly. Exam 8 of 9: carrying on 26 of 30 |
| Zombie | **Medium**: 374, down from a peak of 425 | **High**: 12.0 of 12 s | **Low**: settled | **Medium**: 0.18 | Yes. Exam 9 of 9 |
| Nick | **Medium**: 291, lowest and flat | **High**: 11.9 of 12 s | **Low**: settled | **Medium**: 0.17 | Yes. Exam 9 of 9 |
| Trump | **Medium**: 332, flat | **High**: 11.9 of 12 s | **Low**: settled | **Medium**: 0.16 | Not yet. Exam 8 of 9: attack 0.37 a second |
| Grandma | **Healthy**: 339, still rising | **High**: 11.8 of 12 s | **Low**: settled | **Low**: 0.13 | Yes. Exam 9 of 9 |
| Grandpa | **Healthy**: 378, flat | **High**: 11.8 of 12 s | **Low**: settled | **Low**: 0.13 | Nearly. Exam 8 of 9: footing 0.33 falls a minute |

How to read the ratings:

- **Scoreboard.** Healthy = flat or rising at the end. Medium = flat but low for that boxer, or sagging.
- **Survival time.** High = within a quarter of a second of the full 12.
- **Confusion.** Low = the line is calm near zero. All six are.
- **Curiosity.** Low (under 0.14) = committed to its habits, little left to learn from more of the same
  practice. Medium (0.14 to 0.2) = still trying things; more rounds may still change it.

What the grid says: the charts look finished for all six, and more of the same training will not close
the three open exam lines. Matt, Trump and Grandpa need a change to the practice itself, as the wider
starting distance was for Matt's and Grandpa's carrying on.

### Open TensorBoard

1. `python -m tensorboard.main --logdir training/logs/tb --port 6006` (it is usually already running).
2. Open `http://localhost:6006`. In "Filter runs" type the runs to compare, for example
   `m4_matt|m3_grandpa|m5_grandma`.
3. In "Filter tags" type the chart name, for example `env/hits_per_s`. Set Smoothing to 0.6.
4. Pin the charts you want to keep at the top.

### Run names

| Prefix | Skill | Example |
|---|---|---|
| `r0_`, `r1_`, `r2_`, `f_`, `f2_` | footwork (stand, walk, turn) | `f_nick` |
| `u1_` | get-up (two boxers learn in one run) | `u1_matt_zombie` |
| `m1_` to `m7_` | boxing against the frozen others; the number is the attempt | `m4_matt` |
| `bag_` | punching a heavy bag | `bag_trump4` |
| `fh_`, `u1_h_` | the 4 October attempt under the human rules; nothing from it was kept | `fh_matt` |

A run folder in `training/checkpoints` that holds `REJECTED.txt` was examined and found worse. When a new
run starts, remove obsolete runs from `training/logs/tb` so the charts stay readable (older ones are in
`training/logs/tb_archive`).

### Every chart has the same numbers elsewhere

`training/logs/<run>.csv` holds every round, and `training/logs/<run>.status.json` holds the latest
values, including the split by opponent in boxing runs (`v_NICK/hits_per_s` in TensorBoard).
