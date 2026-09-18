/goal 8-hour autonomous MuJoCo RL loop for the behaviours required in this app. Editor stays closed. Append every result to the log. Stop when 8 hours elapse, the bar is hit, or the curve is flat two segments.
**Discovery (~10 min).** Read the app for behaviours + targets, regression ceilings, latest checkpoint + shipped ONNX. Probe `num_envs` in {4096, 8192} once with `--max-iterations 20 --no-ui --no-tensorboard`; keep the higher SPS. Confirm TensorBoard + Newton viewer are alive.
**Each segment = one .py to a new iteration target (~3000 iters ≈ 50 min here).** After: sweep `model_{N-2000, N-1000, N}.pt` against the exam (older checkpoints sometimes win); with `IMESTEP=0.005 DECIMATION=4`, run behaviour + regression exams on the SAME checkpoint; write numbers table + one paragraph to the log; decide.

**Decide (every segment, no exceptions):** bar hit + no ceiling → STOP, export, hand back. Climbing + time → another segment, same recipe. Bar hit BUT ceiling tripped → STOP, report tradeoff. Flat twice → STOP, export best. One flat only → ONE curriculum change, then another segment.

Change start mix or handover target before touching PPO constants.

**Does NOT:** new behaviours, edit the parity-locked MJCF, touch shipped ONNX (new brains get `Assets/Agents/<Name>_*/` folders), push branches, decide "good enough", open Editor mid-run.

**Hand-back.** `export_onnx.py --out Assets/Agents/<Name>_NNN/<key>.onnx`, append a final block (hit/miss, regression table, one-sentence blocker), tell the user the machine is theirs.
