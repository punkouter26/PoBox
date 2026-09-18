# Generic Overnight MuJoCo RL Training Prompt

Drop-in for any long training run on an existing project where you want the
GPU used productively while you step away. Reads `rl_optimization_log.md`
and the most recent committed loop state to discover what's being worked
on and what brain you inherited, so it auto-tunes without you restating
the target. Use it ONLY for long unattended runs. For first-time project
exploration use your project's dedicated setup prompt instead.

---

**/goal** Run an autonomous N-hour MuJoCo RL training loop on the behaviours documented in `rl_optimization_log.md`. Editor stays closed the entire time. Append every result to that log. Stop when N hours elapse, the success bar is hit, or the curve is flat two segments running.

**Discovery (~10 min).** Read the log: pull the in-scope behaviours and their numeric targets, the regression ceilings that must not drop, and the latest best checkpoint and its shipped ONNX. Probe `num_envs` ∈ {4096, 8192} once with `--max-iterations 20 --no-ui --no-tensorboard`; keep the higher SPS. Confirm TensorBoard is up and the Newton viewer is replaying new checkpoints.

**Every segment = one `train_nick_loop.py` to a new iteration target (~3000 iters ≈ 50 min on this hardware).** After each: (1) sweep `model_{N-2000, N-1000, N}.pt` against the exam — older checkpoints sometimes win on PPO plateaus; (2) with `NICK_TIMESTEP=0.005 NICK_DECIMATION=4` (plus `NICK_GETUP_STABLE=4` for get-up), run the behaviour's exam AND a regression exam on the SAME checkpoint; (3) write a numbers table + one paragraph of reasoning to the log; (4) decide.

**Decision tree (every segment, no exceptions).**
- Hit the bar, no ceiling tripped → STOP, export, hand back.
- Still climbing AND time left → another segment, same recipe.
- Hit the bar BUT tripped a ceiling → STOP, report the tradeoff, do not call it a win.
- Flat across two segments → STOP, export best, hand back honestly.
- One flat segment only → ONE curriculum change, then another segment.

Change the mix of starts or the handover target before touching PPO constants.

**Does NOT:** introduce new behaviours, edit the parity-locked MJCF, touch shipped ONNX (new brains get new `Assets/Agents/<Name>_*/` folders), push branches, decide what's "good enough", or open the Editor mid-run.

**Hand-back.** `export_onnx.py --out Assets/Agents/<Name>_NNN/<key>.onnx`, write the final log block (what hit, regression table, one-sentence blocker for the next bar), tell the user the machine is theirs.
