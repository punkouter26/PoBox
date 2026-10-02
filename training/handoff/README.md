# Hand-off, 2 October 2026, 11:35

Everything was stopped here at the owner's request so the project could move to another computer.
`training/checkpoints` and `training/logs` are not in git, so the files a new machine needs to carry on
are copied into this folder.

## What is here

| File | What it is |
|---|---|
| `match_NAME.pt` | each boxer's current boxing policy (PPO checkpoint: network, observation scaling, optimiser) |
| `getup_NAME.pt` | each boxer's get-up policy |
| `getup_NAME.onnx`, `getup_NAME_policy_config.json` | the same get-up policies exported for the game (held out of it, see below) |
| `handover_NAME.npz` | the states each get-up policy leaves its body in, for the gauntlet's `--handover` |
| `policies.json` | which file is whose, and which run each came from |
| `exam_0.json`, `exam_0.log` | the exam of all seven after the league, before any gauntlet: 0 of 7 pass |
| `exam_zombie_g1.json` | Zombie after his gauntlet: passes all six lines |
| `queue.json`, `queue.log` | the job list as it stood, and what ran |
| `*.csv` | the training curves of the get-up runs and Zombie's gauntlet |

## Where each boxer stands

| Boxer | Boxing policy | Exam, boxing lines | Gets up (exam) | Gauntlet |
|---|---|---|---|---|
| Zombie | gauntlet `g1_zombie` | **passes all** | 38 of 40, 1.6 s; carries on 35 of 38 | done, 36 min |
| Matt | `defend` | fails footing and attack | 59 of 60, 1.3 s; carries on 88% | not run (a 3-minute start was thrown away) |
| Nick | league round 5 | fails footing | 59 of 60, 1.7 s; carries on 86% | not run |
| Lil Matt | league round 5 | fails footing and guard | 60 of 60, 1.5 s; carries on 75% | not run |
| Trump | league round 3 | fails footing | 40 of 40, 2.2 s; carries on 58% | not run |
| Grandma | league round 5 | fails footing | 35 of 40, 3.5 s; carries on 33 of 35 | not run |
| Grandpa | league round 5 | fails footing | 39 of 40, 3.3 s; carries on 69% | not run |

## To carry on, on the new machine

1. **Python.** The trainer's environment is not in this repository: it is `..\PoDecath\training\.venv`
   (Python 3.10.11, torch 2.5.1+cu121, mujoco 3.14.0, warp 1.17.0, mujoco_warp 3.14.0, tensorboard).
   Bring the PoDecath project too, or make an environment with those versions and use its `python`.
2. **Put the policies back where the tools look.** From `training/`:
   `python tools/restore_handoff.py` copies them to `checkpoints/handoff/latest_NAME.pt` and
   `checkpoints/getup_handoff/latest_NAME.pt`, the banks to `logs/`, and writes `logs/policies.json`.
3. **Run the six gauntlets that are left** (about 36 minutes each on an RTX 2060; Zombie's is the pattern):

   ```
   python train_gauntlet.py --name matt --resume checkpoints/handoff/latest_matt.pt \
     --against zombie=checkpoints/handoff/latest_zombie.pt nick=checkpoints/handoff/latest_nick.pt \
               lilmatt=checkpoints/handoff/latest_lilmatt.pt trump=checkpoints/handoff/latest_trump.pt \
               grandma=checkpoints/handoff/latest_grandma.pt grandpa=checkpoints/handoff/latest_grandpa.pt \
     --run-name g1_matt --models models/guard --block-w 0.05 --weights zombie=2 \
     --daze --daze-hi 42 --reset-std 0.3 --max-std 0.35 --max-hours 0.6 \
     --num-envs 4608 --lr 5e-4 --lr-max 1e-3 --handover logs/handover_matt.npz
   ```

   The others are the same without `--models`, `--block-w` and `--weights`. `queue.json` here has all
   seven command lines as they were queued (their paths point at this machine's `checkpoints/`).
4. **Exam:** `python tools/exam.py --json logs/exam.json`. A boxer is done when every line passes.
5. **Into the game:** `PoBox/Import Trained Entrants`, then `PoBox/Build Everything`.

## The open problem: getting up in the game

All seven pass "getting up" in the exam. In the game, Grandpa and Grandma got up 2 times in 8 and were
counted out 6, so `checkpoints/getup_final` carries a `REJECTED.txt` on the old machine and the game has
no get-up policies: the referee stands a boxer up, as before. To try them in the game, copy
`getup_NAME.onnx` and `getup_NAME_policy_config.json` to `checkpoints/getup_final/latest_NAME.*` and
import.

What is known about why. The game hands the body back only after 1.5 s of standing, where the exam's
"getting up" asks for 0.5 s. Held to the game's test in plain C MuJoCo, Grandpa manages 24 of 30 and
Grandma 18 of 30 even in the exam's own conditions. None of the game's other differences, tried one at
a time (no shove, the other boxer held 0.5 to 0.9 m away instead of 0.9 to 2.0 m, shorter slack, the legs
going in mid-movement, not passing through the other boxer), moved those numbers much (16 to 23 of 30).
So roughly two thirds is what these two policies can do against the game's test, and 2 of 8 is low even
for that: something in the game itself is still unexplained. Only the two frail boxers were tried in
the game; the five adults were not.

Two things that were tried for the hand-over and made it worse, so are not worth trying again: blending
the get-up policy's joint targets into the guard pose, and cross-fading the get-up and match policies.
