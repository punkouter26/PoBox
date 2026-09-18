# Generic Overnight MuJoCo RL Training Prompt

Drop-in for any long training run on an existing project where you want the
GPU used productively while you step away. Reads `rl_optimization_log.md`
and the most recent committed loop state to discover what's being worked
on and what brain you inherited, so it auto-tunes without you restating
the target. Use it ONLY for long unattended runs. For first-time project
exploration use your project's dedicated setup prompt instead.

---

**/goal** N-hour autonomous MuJoCo RL loop on the behaviours in `rl_optimization_log.md`. Editor stays closed. Append every result to the log. Stop when N hours elapse, the bar is hit, or the curve is flat two segments.

**Discovery (~10 min).** Read the log: behaviours + numeric targets, regression ceilings, latest best checkpoint + shipped ONNX. Probe `num_envs` in {4096, 8192} once with `--max-iterations 20 --no-ui --no-tensorboard`; keep the higher SPS. Confirm TensorBoard + Newton viewer are alive.

**Each segment = one `train_nick_loop.py` to a new iteration target (~3000 iters ≈ 50 min here).** After: sweep `model_{N-2000, N-1000, N}.pt` against the exam (older checkpoints sometimes win); with `NICK_TIMESTEP=0.005 NICK_DECIMATION=4` (add `NICK_GETUP_STABLE=4` for get-up), run behaviour + regression exams on the SAME checkpoint; write numbers table + one paragraph to the log; decide.

**Decide (every segment, no exceptions):** bar hit + no ceiling → STOP, export, hand back. Climbing + time → another segment, same recipe. Bar hit BUT ceiling tripped → STOP, report tradeoff. Flat twice → STOP, export best. One flat only → ONE curriculum change, then another segment.

Change start mix or handover target before touching PPO constants.

**Does NOT:** new behaviours, edit parity-locked MJCF, touch shipped ONNX (new brains get `Assets/Agents/<Name>_*/` folders), push branches, decide "good enough", open Editor mid-run.

**Hand-back.** `export_onnx.py --out Assets/Agents/<Name>_NNN/<key>.onnx`, append a final block (hit/miss, regression table, one-sentence blocker), tell the user the machine is theirs.
