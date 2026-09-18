# Generic Overnight MuJoCo RL Training Prompt

Drop-in for any long training run on an existing project where you want the
GPU used productively while you step away. Reads `rl_optimization_log.md`
and the most recent committed loop state to discover what's being worked
on and what brain you inherited, so it auto-tunes without you restating
the target. Use it ONLY for long unattended runs. For first-time project
exploration use your project's dedicated setup prompt instead.

---

**/goal Run an autonomous N-hour MuJoCo RL training loop on the *current
in-progress behaviours documented in `rl_optimization_log.md` of this
project*. Spend the entire budget on training + honest evaluation. Keep
the Unity / Unreal Editor closed the entire time. Append every segment's
results to `rl_optimization_log.md`. Stop only when N hours elapse, when
the documented success bar is hit, when the curve is verifiably flat for
two consecutive segments, or when a regression in a previously-working
behaviour forces a rollback decision.

**Discovery (first 5–10 min, before any training):**

1. Read `rl_optimization_log.md` end-to-end. Identify:
   * WHICH behaviours are in scope (often a ladder: stand, balance under
     shove, walk, get-up, etc.).
   * The CURRENT quantitative target for each (e.g. "≥10% get-up
     success on the 4-second standard across 512 fresh fallen starts").
   * The HARD REGRESSION CEILINGS (which behaviours must not regress
     during training of the new one — e.g. balance ≥70% full-cap, walk
     ≥12 m). These are non-negotiable; a segment that hits the new bar
     but breaks an old one is a regression, not a win.
   * The LATEST best checkpoint and the warm-start brain (often the
     last shipped ONNX in `Assets/Agents/`).
2. Probe throughput ONCE: `train_nick.py --num-envs N --max-iterations 20
   --no-ui --no-tensorboard` for N in {4096, 8192}, pick the higher SPS,
   use that `num_envs` for the whole loop.
3. Confirm TensorBoard is serving and a Newton/mujoco-py viewer is
   replaying the newest checkpoint. From this point on, you should be
   able to walk away.

**Looping (the rest of the time):**

A "segment" = one `train_nick_loop.py` invocation that resumes to a new
iteration target. Pick segment length from SPS and time remaining (~3,000
iters ≈ ~50 min on a 4096-env RTX-2060-class card; scale to your hardware).

After every segment, in this order:

1. **Snapshot:** the current best checkpoint (the last one may not be
   best — sweep `model_{N-2000, N-1000, N}.pt` against the exam if the
   head number didn't improve; older checkpoints sometimes win on
   PPO-plateau lines).
2. **Evaluate fairly:** set the time/discount env vars (e.g.
   `NICK_TIMESTEP=0.005 NICK_DECIMATION=4`) so the body matches the
   brain, run the behaviour's exam at 256–1024 worlds and the
   regression-check on the SAME checkpoint. Report BOTH.
3. **Decide.** Then write one paragraph to `rl_optimization_log.md`
   with the numbers table + your reasoning.

**Decision rule (every segment, no exceptions):**

| Last segment did what to the target? | What to do |
|---|---|
| Crossed the success bar AND no regression ceilings tripped | STOP. Export the winner. Hand back. |
| Curve still climbing (≥Δ bars over the previous segment) AND time remains | Another segment; bump target by ~3,000 iters; same recipe unless a clearer lever is obvious. |
| Crossed the bar BUT tripped a regression ceiling | STOP. Report. That is not a win, it is a tradeoff the user has to weigh. |
| Flat across two consecutive segments (<Δ bars each) | STOP. Export the best measured brain, report honestly. Do not keep grinding on a flat curve. |
| Under-budget but not progressing (one flat segment not yet doubled) | Make ONE curriculum/recipe change, then another segment. |

For "made a change": change the *mix* of starts (curriculum) or the
*handover target* (what the reward hands the agent once it crosses a
milestone) BEFORE touching PPO constants. Hyperparameter roulette is
the slow way down on a known task; curriculum shaping is the lever
that has the best odds of working in 3,000 iterations.

**Things this loop does NOT do:**

* Does not introduce new behaviours or new tasks.
* Does not edit the trained body (the parity-locked MJCF) or the
  physical-realism limits. Audit only.
* Does not edit the shipped scenes, brain folders, or existing ONNX
  weights. New brains go in NEW folders (`Assets/Agents/<Name>_*/`).
* Does not push to remote, merge branches, or commit unless explicitly
  asked.
* Does not decide what's "good enough" for the user.
* Does not run the Editor, the practice scene, or any human-in-loop
  tooling while training — they compete for the same cores.

**Hand-back deliverable (when time is up, hit the bar, or flat across two):**

1. Export the best-measured checkpoint via `export_onnx.py --out
   Assets/Agents/<Name>_NNN/<key>.onnx` so it lands in a NEW folder with
   a generated SOURCE.txt (never overwrite an existing brain).
2. Append a final block to `rl_optimization_log.md`:
   * What was hit / what wasn't, with the numbers (NOT the curve).
   * The regression table on the same checkpoint.
   * One sentence on what's still blocking the next bar (e.g. "this body
     learns stand-up to ~K% with curriculum shaping alone; reaching the
     next ≥20% likely needs imitation-learning follow-up").
3. Verify nothing else is left dirty (no modified ProjectSettings, no
   stray processes), then tell the user the machine is theirs again.

**Why this prompt is generic:** the only project-specific things are the
behaviour list, the bars, and the ceilings — all of which are read from
`rl_optimization_log.md` on every run. The protocol stays the same whether
you're training get-up, walk, balance-under-wind, or a four-behaviour
ladder.
