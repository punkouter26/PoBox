# PoBox training

Teaches a fighter to box in MuJoCo (MuJoCo Warp, on the GPU) and exports the result as ONNX for Unity.
Built 2026-10-01 on the pattern of `../../PoDecath/training`; `ppo.py` is PoDecath's, unchanged.

Python is PoDecath's virtual environment, used in place (torch 2.5 + CUDA, mujoco 3.14, mujoco-warp, warp 1.17):

    $py = "..\..\PoDecath\training\.venv\Scripts\python.exe"

## From a rigged mesh to a trained policy

1. **Read the rig out of the owner's mesh** (house rule: the skeleton comes from the skinned model).

       & $py glb_to_rig.py --glb ..\Assets\Models\Fighter.glb --out rigs\fighter.json --mass 75

   Works on a `.glb`. It finds the limbs by bone name (Mixamo, Avaturn and similar), and by the shape of
   the skeleton when the names say nothing (`bone_0`, `bone_1`, ...). It prints which bone it took for
   which limb: read that list. Arms are straightened to a T-pose and the size fixed if the file is not in
   metres; both are reported.

2. **Generate the MuJoCo models.**

       & $py rig_to_mjcf.py --rig rigs\fighter.json

   Writes `models/fighter_bag.xml` (one fighter and a heavy bag), `models/fighter_spar.xml` (two fighters in
   a ring) and `models/fighter_policy_config.json`. It prints the fighter's mass and where its weight sits
   over its feet in the guard; that should be near the middle of the foot.

       & $py tools\render_pose.py --xml models\fighter_spar.xml --out logs\pose.png

3. **Train.** One command for the whole night:

       & $py run_night.py --hours 8

   30% of the time on the bag (stand unaided, hold range, hit hard), then sparring against itself from that
   policy. TensorBoard starts with it: http://localhost:6006. Progress in one line:

       .\tools\status.ps1 bag          .\tools\status.ps1 spar -Terms

4. **Watch it.** MuJoCo's own viewer, following the newest checkpoint of a run as training saves them:

       & $py view_box.py --run spar

   A strip of stills instead: `& $py view_box.py --run spar --sheet logs\spar.png`. `run_night.py` writes one
   every half hour to `logs/sheets/`.

Each save leaves `checkpoints/<run>/latest.pt`, `latest.onnx` and `latest_policy_config.json`.

## What is being taught

Both stages share one observation (100 numbers) and one action (21 joint targets at 50 Hz), so the policy
that learned on the bag is the one that starts sparring. The full list is at the top of `envs/boxing.py`.

A **hit** is a glove arriving at the target's head or body faster than 1 m/s straight into the surface,
having come back at least 30 cm, and a quarter of a second, since its last hit. Its strength is that
closing speed, capped at 9 m/s. It is measured from the positions of the bodies, not from the solver's
contact list, because reading that back would stall the GPU every step.

Reward, per fighter per step: staying up and upright, facing the target, holding punching range, a small
payment for a glove closing on the target, a large one for a hit (head counts 1.5, body 1; paid in full
only 0.6 s after the fighter's last), a charge for being hit, and charges for wasted power, jerky actions,
joints faster than a person's, sliding feet, leaning on the target, and a fall. In sparring, putting the
other fighter down within a second and a half of hitting them pays a bonus.

## The follow-up of 2026-10-01: two things tried, neither kept

The eight-hour match ended with both fighters landing 1.5 hits a second and 0.04 knockdowns a minute. Sixty
minutes more were spent, from its final policies, on two ideas. Charts and numbers:
`DOCS/reports/2026-10-01-training-followup.html`. Both runs are started by `tools\followup.ps1`.

- **A knockdown should not cost the fighter who lands it** (`--survivor-bootstrap --ko-bonus 10`). A fighter
  earns about 0.8 a step just for standing upright in range, and a fall ended the episode for both, so the one
  who put the other down threw away the rest of the episode's income for a bonus of 4. With the option on, the
  fighter left standing has its episode cut short rather than ended (its value is carried over, as at a
  time-out). Run `match3`, 29 minutes: knockdowns 0.045 a minute, hits 1.51 a second. **No change.** The
  incentive was wrong, but it was not what held knockdowns back. With 1.5 hits a second nearly every fall
  follows a hit, so the knockdown count is the fall rate under another name; and nothing in this environment
  weakens a fighter who has been hit, which is what puts one down in the game. The option is harmless and
  stays available; it is off by default.
- **Being hit should cost what hitting pays** (`--taken-w 1.0`, with the above). Run `match2`, stopped at 31
  minutes: landed hits held at 1.1 a second for 20 minutes and then fell to 0.22, glove speed and joint power
  with them, while the reward per episode rose. With every exchange worth nothing and punching still costing
  energy, not punching is the best policy. **Worse.** Its checkpoint folder holds `REJECTED.txt`, and Unity's
  importer passes over any run that has one. Somewhere between 0.5 (they trade for ever) and 1.0 (they stop)
  is untried.

What a next run needs instead: the game's "hurt" rule inside the environment (a heavy shot to the head
weakens the legs for a moment), so that a knockdown is something a fighter can cause and must guard against.

## A roster: five boxers with styles, and the league (2026-10-01 night)

Report, with the charts and what each boxer ended as: `DOCS/reports/2026-10-02-training-roster.html`.

**Who the boxers are is `roster.json`**: the mesh, the height if the file has no scale, how strong and how
quick beside a trained adult (`age`, `speed`), and the `style`. One command builds all of them and every
pair model, and runs the self-collision check on each body:

    & $py tools\build_roster.py                  (or --only nick)

- `glb_to_rig.py` now reads the skin's bind pose (a file with an animation in it stores its nodes in a
  frame of that), makes a lopsided skeleton symmetric, fits each collision shape inside the mesh from the
  limb's outline, and takes mass from the mesh's volume held to a body-mass index of 18.5 to 30. `--plot`
  draws the mesh with the shapes over it; `--skeleton-only` is the old behaviour. Matt and Zombie's rigs
  were not regenerated.
- `rig_to_mjcf.py` takes `radii`, `strength`, `speed` and `style` from the rig file. A rig with none of
  them builds exactly as before.
- **A style is how a boxer is paid**, per fighter, in `envs/boxing.py` (the `s_*` values, read from each
  fighter's config): what the head and the body are worth, each hand, how hard, how often, what being hit
  costs, what a block pays, a bonus for a punch straight after a block, the range it likes, pay for walking
  in, a charge for being crowded. It changes nothing about what a hit is. `tools/restyle.py` writes a
  changed style into the rig and every config without rebuilding a body.

**The league is `tools/league.py`**: each boxer 18 minutes on the bag from `bag_matt` (a warm start), then
five rounds of three ring sessions. `train_box.py` gained `--freeze NAME` (a partner that boxes its best punch
and does not learn) and `--career` (one TensorBoard line per boxer, `boxer_<name>`, through every stage).

What the night taught:

- **A warm start works across bodies.** From Matt's bag policy, a body 20 cm shorter with half the strength
  was landing 1.5 punches a second with a 2% fall rate in 18 minutes. Matt needed 45 from nothing.
- **Two learners can agree not to fight; a learner and a frozen puncher cannot.** Three of fifteen sessions
  ended with one or both boxers not punching (Grandma v Grandpa; Grandpa v Lil Matt; Trump against either
  grandparent). Every session against frozen Matt or Zombie produced a boxer that boxed. A next league
  should have the newcomer learn against frozen opponents at least as often as against another learner.
- **A style that keeps opponents out needs opponents who are paid to come in.** Grandpa (long arms, paid to
  stand at 0.90 m) was out of everyone's reach. Nick, who is paid to walk in, was the only one who landed
  on him (0.96 a second); Trump is paid for it too and did not.
- **When a session leaves a boxer worse, put the earlier policy back**: `tools/restore_policy.py` writes it
  over the checkpoint the league will resume from and keeps the bad one as `passive_<name>.pt`. A rejected
  session's folder has `REJECTED.txt`, or the one bad `.onnx` deleted, so the importer passes it over.
- **The viewer and the editor cost training**: the editor in Play mode halves it; MuJoCo's viewer on the
  RTX costs 10 to 15%, 30% minimised, nothing on the Intel GPU. `tools/viewer_leash.ps1` sees to it.
- **The age limit on joint speed is only a charge.** Grandma's gloves still arrive at 8 m/s. Her lower
  strength is in the model; a hard speed limit would have to be too.

## The exam, and finishing every boxer (2026-10-02)

The owner's goal: every boxer able to do everything a bout needs. The plan, with the audit of the bodies,
the rewards and the hardware behind it: `DOCS/reports/2026-10-02-training-manifest.html`.

**What "able" means is `tools/exam.py`.** Plain C MuJoCo (the game's library), the game's ring where it
has been exported, no exploration noise. Six lines per boxer; the pass marks are at the top of the file.

| Line | Test | Mark |
|---|---|---|
| Footing | rounds of 45 s, nothing weakening anybody | falls nobody caused: at most 0.2 a minute |
| Attack | the same rounds | at least 0.5 punches landed a second, at 4 m/s times the boxer's own speed factor |
| Guard | the same rounds | stops at least 30% of what comes at its head, or takes under 0.3 head shots a second. Marked only for boxers whose style pays for a guard |
| Chin | the same rounds under the daze rule | legs taken at most 1.5 times a minute |
| Getting up | a shove, 1.6 to 2.6 s of slack drives, the other boxer held in its guard | on its feet for half a second, unaided: 80% within 8 s (60% within 9 s for a boxer under 0.7 of an adult's strength) |
| Carrying on | the match policies take over, the other boxer is let go | still up 3 s later, 90% |

```
python tools/exam.py --json logs/exam.json          everybody, against everybody they have a model with
python tools/exam.py --only matt --among --skip getup
python tools/export_getup.py --from-exam logs/exam.json     get-up policies that passed, to the game
```

Which policy a boxer brings: `logs/policies.json` if it exists, else a veteran's newest kept run
(`guard`, `defend`, `match`), a league boxer's entry in `logs/league.state.json`, and the newest
`*getup*` checkpoint with its name on it.

**Get-up has three trainers and one rule about exporting.**

- `train_getup.py --a A --b B --resume CK_A CK_B`: the GPU, two boxers of a pair model, passing through
  each other. `--freeze NAME` for a boxer that is only there to make up the pair; `--done-when 0.9` stops
  when every learner gets up unaided nine times in ten with no help left; `--num-envs 0` takes the size
  `tools/bench_envs.py` measured.
- `train_getup_cpu.py --name A --resume CK`: one boxer in plain C MuJoCo (`envs/cgetup.py`), a few hundred
  worlds on two threads at idle priority. About 2,000 steps a second against the GPU's 30,000 per boxer.
  **It is not free**: two of them took 17% off a GPU run on the same laptop (39,000 against 47,000 steps a
  second), through the cores and the heat they share. Use it when the GPU has nothing on it worth more.
- A checkpoint from either carries on in the other, and both start from a match policy.
- **Neither writes an ONNX file.** The game's importer takes the newest get-up ONNX it finds, so a policy
  that is half way there must not have one. `tools/export_getup.py` writes it, to
  `checkpoints/getup_final/`, for a policy that has passed the exam.

**The observation scaling has to be redone for the floor.** A match policy's running normalisation was
measured on boxers that were always upright: the gravity vector's standard deviation is a few hundredths.
Lying down it is thirty of those away and is clipped flat at ten, with the pelvis height and a dozen
others (13 of 100 observations for Matt, clipped up to 63% of the time). The policy could tell that it was
down and not which way up. Both trainers therefore measure the scaling again on the stage's own states
before training and rewrite the first layer of the actor and the critic so that every state the old
scaling did not clip gives the output it gave before (`renormalise` in `train_getup_cpu.py`).
`train_box.py --stage getup` does not do this.

**`tools/queue_runner.py`** runs GPU jobs one at a time from `logs/queue.json`, which can be added to
while it runs; `logs/queue.hold` makes it wait before the next job (for a Play-mode check, say),
`logs/queue.stop` ends it. It opens the viewer on each run, at idle priority on two logical cores.

**`tools/audit_bodies.py`** prints every boxer's mass, strength, joint torque and speed limits, and what
share of its knees' strength holding a deep squat takes (Grandma 89%, Grandpa 92%: they rise with
momentum or their arms or not at all).

**`models/guard/`** is the Matt and Zombie match model with a style for Matt (a punch taken costs twice
as much, a punch stopped pays three times as much, a counter half as much again), for a rung in which
Matt learns to guard against a frozen Zombie. The bodies are the same; only how Matt is paid differs.

**Three things about the machine found on the way.** The Unity editor left in Play mode took half the GPU
from the run on it (24,000 steps a second, 46,000 the moment Play stopped). A MuJoCo viewer window drawing
on the same GPU costs a tenth, a minimised one a third; Windows is now told to draw that Python's windows
on the integrated GPU. A second training job on the GPU beside a running one is not allowed by the
permission check and was not done.

## The defend rung and the get-up stage (2026-10-01, evening)

Report with the TensorBoard charts: `DOCS/reports/2026-10-01-training-defend.html`. Both are started by
`tools\rungs.ps1` (one after the other, detached, with TensorBoard and the viewer); `-Only defend` or
`-Only getup` runs one.

**What was added to the trainer.**

- **The daze rule** (`--daze`, with `--daze-tau 2.5 --daze-lo 14 --daze-hi 42 --daze-weak 0.45`): the
  consequence for being hit that the follow-up above said was missing. Every landed punch adds its
  closing speed to the daze of the fighter who took it (in full to the head, 0.3 to the body); the daze
  drains with the time constant; above `lo` the joint drives weaken (a weakened fighter is asked for a
  point part of the way from where each joint is to where the policy wants it); at `hi` the drives go
  slack for 0.7 s (the fighter may or may not fall). The game applies the same rule with the same numbers,
  except that there the line is a knockdown outright
  (`Assets/Entrants/<name>/rules.json`, written by the importer from the run's `args.json`).
- **A payment for stopping a punch** (`--block-w 0.05`): per m/s of a glove that is coming at the
  fighter's head at more than 2 m/s, within 45 cm of it, and is touching the fighter's own glove or
  forearm. `blocks_per_s` counts those.
- **Two ceilings for a rung that refines a working policy** (`--max-std`, `--lr-max`): see the first
  attempt below.
- **`--more-iters N`**: stop after N iterations of this run. A rung is given iterations, not minutes: the
  Unity editor in Play mode takes four fifths of the GPU, and a rung timed by the clock loses what it took.
- **The critic goes out with the policy**: `export_onnx` writes two outputs, `actions` and `value`. The
  game's win-probability bar reads the second.
- **`envs/getup.py`, `--stage getup`**: a stage of its own for standing back up, trained from the match
  policies in the match model, with the two fighters passing through each other. An episode is a
  knockdown as the game deals one: a shove and slack drives for 0.15 to 2.6 s, then the policy has the
  rest of ten seconds. Reward for pelvis and head height and an upright trunk and, once standing, for
  being in the guard, still, and facing a stand-in opponent. It starts with an upward force on the trunk
  worth 60% of body weight, which comes down by itself as the fighter succeeds; one world in ten never
  gets it, and the "unaided" numbers come from those.
- **`tools/check_self_collision.py`** (house rule): each fighter's 17 shapes are capsules, spheres and
  boxes; 55 of the 66 pairs of body parts collide, the 11 that do not are joined by a joint; no pair
  touches in the T-pose, the guard, or 23 steps of walking and punching. Checked for Matt and Zombie
  before training. A deliberately folded arm (glove into the head) is reported, so the check can fail.
- **`tools/eval_ring.py`**: the match as the game runs it, in C MuJoCo on the CPU: the ring with its
  ropes, 45-second rounds, the daze rule. For telling a fault of the game from a fault of the policy
  without the game or the GPU.
- **`tools/smoke_env.py`**: twenty seconds of an environment with given policies and the numbers that
  come out, for choosing a threshold before a long run is spent on it.
- **`tools/export_mujoco_layout.py`**: the layout carries the sizes a punch is measured against and
  MuJoCo's state bits (they moved between versions); `--android` writes a second layout for the version
  of MuJoCo this Python is running. The ropes' contact is softer (`solref 0.2 1`), so a body leaning on
  one sinks in and the game can draw the rope bowing.

**Choosing the line.** With the match policies and their own exploration noise, twenty seconds in 512
worlds: at `--daze-hi 36` Matt was down in 92% of episodes, at 42 in 37%, at 48 in 15%; Zombie in 3 to 5%
throughout (its punches go to Matt's head, Matt's to its body, which counts for three tenths). 42 was
chosen: a start with room to get better and worse.

**First attempt (`defend_a`, 39 minutes, rejected).** Started as the follow-up runs were: noise reset to
0.30, the usual entropy bonus, knockdown bonus 10. By iteration 10,500 (11 minutes) the fall rate was
down from 0.68 to 0.11 and blocks were up from 0.2 to 0.4 a second. Then the exploration noise climbed,
0.30 to 0.65, the charges for power and jerky actions doubled, and Zombie's reward per step went from
+0.63 to -0.37. With knockdowns twenty times as frequent as in the match the value loss was about ten
times what it had been; with advantages that noisy nothing pulled the noise down and the entropy bonus
walked it up. Its folder holds `REJECTED.txt`.

**Second attempt (`defend`, from `defend_a`'s iteration 10,500).** `--max-std 0.32 --entropy-coef 0
--lr 3e-4 --lr-max 5e-4 --ko-bonus 6 --survivor-bootstrap`. 2,400 iterations in 88 minutes (21:40 to 23:08), ending at iteration 12,900.
Start of the rung (the match policies under the rule) to its end: a fighter down in 58% of episodes to 7%;
knockdowns 3.0 a minute to 0.31; punches landed 0.7 a second each to 1.3, at 6.6 m/s to 8.3; punches stopped
on the guard 0.20 a second each to 0.76; noise 0.29 to 0.16; power 1,290 W to 690 W. **Accepted, and in the
game.** What it did not do: Matt does not defend. All the blocking is Zombie's (1.50 a second against
Matt's 0.01), because Zombie's punches go to the head and Matt's to the body, which counts three tenths as
much, so only Matt is ever in danger; Matt's answer was to hit harder (4.4 to 8.0 m/s). Matt's legs are
taken 1.26 times a minute at the end, Zombie's never. A rung that pays for body shots less unevenly, or
that trains Matt alone against a frozen Zombie, is what would change that.

**Checked outside the trainer** (`tools/eval_ring.py --run defend --rounds 10`: C MuJoCo on the CPU, the
game's ring with its ropes, 45-second rounds with no reset inside them, the daze rule): neither fighter
fell in 450 s; Matt's legs were taken ten times and Zombie's never; Zombie's punches were all to the head
and Matt's all to the body. With `--head-bias` (every punch counted as a head shot) nobody's legs went at
all. In training, too, a fighter whose legs are taken for 0.7 s keeps its feet about three times in four
(Matt: legs taken 0.25 times an episode, down in 0.07 of them). **The game is harsher**: there the line is
a knockdown and a count, every time. In one full bout in the game Matt went down once a round and Zombie
never, and Zombie won a unanimous decision, 30-24.

**The get-up stage has not been trained.** It was queued behind `defend`; the owner then gave the GPU to
another session's eight-hour run. What is known about it is the smoke test: the match policies, put
through it, end up on the canvas in 85 to 95% of episodes and are back on their feet at the end of about
1% of those. `tools\rungs.ps1 -Only getup` runs it (5,400 iterations, about two and a half hours with the
GPU to itself). The game uses a get-up policy when `Assets/Entrants/<name>/getup.onnx` exists, and the
referee when it does not.

**The phone's MuJoCo.** `Assets/Plugins/Android/arm64-v8a/libmujoco.so` is MuJoCo 3.3.7 (the Windows
library is 3.14). The match policies were run in C MuJoCo 3.3.7 (`pip install --target DIR mujoco==3.3.7`,
then `PYTHONPATH=DIR python tools/eval_cmujoco.py --seconds 200`): 16 episodes, no falls, the same as in
3.14. The same `PYTHONPATH` with `tools/export_mujoco_layout.py --a matt --b zombie --android` writes
`models/matt_vs_zombie_layout_android.json`.

## Rehearsals, 2026-10-01 (on a stand-in rig, PoDecath's Matt; not the owner's mesh)

Four short runs to find out what the rewards actually teach before a night is spent on them. Each rule
above that looks fussy is there because a rehearsal found the cheaper thing it prevents.

| Run | Length | What the fighter learned | What was changed |
|---|---|---|---|
| 1 | 12 min, bag | stood within 5 min; then rubbed a glove up and down the bag: 17.7 "hits"/s at 11 m/s, 3.7 kW | only speed into the surface counts; one hit per landing; power and joint-speed charges raised |
| 2 | 10 min, bag | stood; 5 pats a second at 8 m/s, each glove coming back 15 cm | 30 cm wind-up to score again; quick repeats paid by the square of the shortfall |
| 3 | 7 min bag + 16 min spar | rested both gloves on the bag; sparring, the two propped on each other, feet far back | charge for a head more than 18 cm out from between the feet |
| 4 | 21 min bag + 49 min spar | bag: upright, arm fully out and back to the guard, 1.9 punches/s at 8.6 m/s. Spar: both upright at range, 1.6 punches/s each at 8.7 m/s, fall rate 0.00 | none |

Sheets from run 4: `logs/rehearsal2_bag.png`, `logs/rehearsal2_spar.png`.

Known after run 4, not yet addressed: every punch goes to the head (the head pays more and nothing yet
makes the body the better target); nobody defends, both simply trade; and there were no knockdowns.

Throughput on the RTX 2060 with the Unity editor open: about 70,000 steps/s on the bag (4,096 worlds) and
59,000 fighter-steps/s sparring (2,048 worlds of two).

## Into Unity

After training, from this folder (CPU only, safe beside a running job):

```
python tools/export_reference.py --name matt
python tools/export_reference.py --name zombie
python tools/export_mujoco_layout.py --a matt --b zombie
python tools/eval_cmujoco.py --seconds 300
```

That writes `models/<name>_inertia.json` (each link's mass, centre of mass and inertia as MuJoCo has them)
and `logs/reference_<name>.json` (the fighter held in its guard, and driven by its bag policy, one row per
control step). The third writes `models/<a>_vs_<b>_ring.xml` (the match model with ropes) and
`models/<a>_vs_<b>_layout.json`, which the game needs to step the fight in MuJoCo itself. The fourth runs
the two match policies in plain C MuJoCo with no exploration noise and prints the fall rate; it takes half
a minute where the Warp environment on the CPU takes half an hour. In Unity, menu
`PoBox/Dev/Import Policies And Calibrate Scoring`; the transfer check is
`Assets/Scripts/Editor/TransferProbe.cs` (see `DOCS/README.md`).

## The run of 2026-10-01 (Matt and Zombie, 7.85 h)

| Stage | Length | End state |
|---|---|---|
| `bag_matt` | 45 min | fall rate 0.02, 1.96 hits/s at 8.6 m/s |
| `bag_zombie` | 45 min | fall rate 0.01, 2.09 hits/s at 8.7 m/s |
| `match` | 381 min, 10,193 iterations, 1.0e9 fighter-steps | fall rate 0.00; Matt 1.5 hits/s at 6.2 m/s, Zombie 1.5 at 8.8 m/s; no knockdowns |

Final policies without exploration noise, C MuJoCo, 600 s: 50 episodes, no falls. Charts and what they
mean: `DOCS/reports/2026-10-01-training-matt-v-zombie.html`.

Throughput: about 48,000 fighter-steps/s with other jobs on the CPU, 59,000 with the machine left alone,
11,500 while the Unity editor was in Play mode. The GPU was power-capped throughout (about 1,500 of
2,100 MHz, Windows on Balanced).

Things learned doing it:

- **With a policy each, one fighter can be left behind.** Both lost their punch in the first minutes of
  the match. Zombie had it back by minute 20; Matt stood and took it until minute 67 and only matched
  Zombie's rate by minute 180. The rehearsal, with one policy on both sides, had no such phase.
- **Matt's punches slowed late on** (8.2 to 6.2 m/s over the last hundred minutes, rate unchanged). Cause
  not known.
- **The policies do not survive Unity's physics**: fall rate 0.69 to 0.89 per episode there against 0.00
  here, with the bodies and the observations matched. The game therefore runs the fight in MuJoCo.
- **A match policy presses forward until the other body stops it**, as a bag policy leans on the bag.
  With gloves that touch nothing it still stands (it does not need the reaction of its own punch), but an
  opponent with no body at all it walks straight through.

- **A bag policy leans on the bag.** With the bag made a ghost (nothing touches it), Matt's finished bag
  policy falls forward in 2.1 s: it throws its weight into a target it expects to be there. So a bag
  policy cannot shadow-box, and against an opponent of another height it falls; only the match stage
  produces something that fights.
- **The first minutes of the match stage look like a disaster and are not.** Both fall within 2 s at
  iteration 0 (fall rate 1.00); seven minutes later episodes last 7 s and the fall rate is 0.62.

TensorBoard charts for a report: `tools\tb_shot.ps1 -Tags 'env/fall_rate$' -Out some.png`.

## Not there yet

- No randomisation of friction, mass or motor strength, only sensor noise and shoves. That is why the
  policies need MuJoCo itself at run time, and why the game's trained fighters are Windows-only for now.
- The get-up stage is written but not trained (above).
- An opponent is never behind a fighter in training (starting headings are within 0.6 rad of facing), so
  a policy cannot turn round.
- The head is part of the torso (no neck joint), as in PoDecath's athlete.
- `.fbx` meshes are not read directly; convert to `.glb` first (Blender: File > Export > glTF).
