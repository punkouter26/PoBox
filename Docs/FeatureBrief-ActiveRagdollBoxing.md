# Feature Brief: Autonomous Active Ragdoll AI Boxing (PoBox)

Source: user PRD "Autonomous Active Ragdoll AI Combat Simulation Engine" + /unity-interview decisions (2026-08-17).
This brief OVERRIDES the PRD where they conflict. Project rules (`.claude/rules/*.md`) win over PRD naming and physics.

---

## Locked Decisions (from interview)

| Decision | Value | Consequence |
|---|---|---|
| Physics timestep | **0.02 s (50 Hz)** — NOT the PRD's 0.01 s | Locked forever; retraining required if changed. Solver iterations locked at 16/16 (from PRD). |
| Orientation | **Portrait 9:16 @ 60 FPS** (`vSyncCount = 0`, `targetFrameRate = 60`) | Cameras + HUD framed for tall screen. |
| First roster | **2 fighters** (one rig, two reward/motor profiles) | Full pipeline proves out end-to-end; remaining 6 archetypes added later using the same pipeline. |
| Heuristic bot | **Scripted puncher bot** (code-only: stabilized stance, target tracking, timed punches) | Stage-3 training opponent + playable fallback fighter. Satisfies the heuristic-bot rule. |
| Fighter body | **Capsule biped generated in code** — no FBX in v1 | Rig tool builds the capsule boxer directly; FBX path stays supported for later meshes. |
| First archetypes | **F-03 Pocket Swarmer vs F-04 Slick Counter** | Opposite styles prove the reward profiles shape behavior. |
| Training budget | **Overnight runs OK (8+ h)** | Full-size stage configs; stages complete in a few nights. |

## Scope

**Does:**
- Editor auto-rig pipeline: FBX in → configured active-ragdoll fighter prefab out (colliders, masses, ConfigurableJoints, drives, agent components).
- 100% physics-driven fighters. No animations, no mocap, no kinematic assist.
- 1v1 spectator match: 3 × 60 s rounds, 10 s rests, 10-point-must scoring, canvas-contact KO (> 0.5 s), no overtime.
- Dual-pool stamina (anaerobic/aerobic) driving joint spring attenuation; heavy-hit stun (0.4 s neck/knee motor collapse).
- Broadcast layer: camera director, UI Toolkit HUD, procedural impact audio, 0.25× 3-angle KO replay.
- 4-stage training pipeline (Balance → Locomotion → Striking → Self-Play) with TensorBoard always started.

**Does NOT (v1):**
- No human player input during matches (spectator only).
- No 8-man tournament (data interfaces only, per PRD).
- No arbitrary-creature rigs in v1 (humanoid path first; RegEx fallback is scaffolded but not acceptance-tested).
- No networking, no saves beyond win/loss records (JSON, local).
- No fighters 3–8 (pipeline must support them without code changes).

**Trigger:** user presses Play in `SCN_MAIN` (match) or runs `mlagents-learn` against a `SCN_TRAIN_*` scene.
**Output:** on-screen match + scorecard result; trained `.onnx` models; telemetry via HTTP + `StatsRecorder`.

---

## Technical Requirements

- **Unity:** 6000.5.6f1 | **Pipeline:** URP 17.5.0 | **Platform:** Windows standalone + in-editor.
- **ML:** com.unity.ml-agents 4.1.0 ↔ Python mlagents 1.1.0 (verify handshake before Stage 1).
- **Physics:** Δt = 0.02 s, solver 16/16, gravity −9.81, `projectionMode = PositionAndRotation` on all joints. Decision period: 1 action per fixed step (50 Hz), `DecisionRequester` period 1.
- **New packages (add via UPM git):** VContainer, MessagePipe, UniTask, R3 (for `ReactiveProperty`). Required by architecture rules.
- **UI:** UI Toolkit only. Version stamp (`Application.version`) top-left of `SCN_MAIN`, non-pickable, inset layer.
- **Performance budget:** 60 FPS in broadcast match (2 fighters, 24 rigidbodies); zero GC alloc in FixedUpdate/Update steady state; training scene strips ALL presentation (no cameras/HUD/audio in headless).
- **Persistence:** fighter win/loss records + ELO as JSON in `Application.persistentDataPath`.

### Corrected Spec Math (PRD numbers were wrong)

- **Action vector: 30 floats**, not 26. DOF sum: torso 3 + head 3 + hips 2×3 + knees 2×1 + ankles 2×2 + shoulders 2×3 + elbows 2×1 + wrists 2×2 = **30**.
- **Body count: 15 rigidbodies, 14 joints** (PRD's "12 major joints" was wrong; its own segment table lists 14 driven segments plus the pelvis root).
- **Observation vector: computed by the rig tool at "Prepare for Training" time**, written into `BehaviorParameters` programmatically — never hand-typed. Composition: root state (13) + per-joint quat(4)+angvel(3) × 14 joints (98) + ground contact (2 flags + 2×3 normals = 8) + opponent-relative (3+3+9+1+3 ring-center vector = 19) = **138**. It must match between training and inference or the ONNX handshake fails.

### Naming (project rules override PRD)

| PRD name | Actual name |
|---|---|
| `Setup_RigAndTrain.unity` | `Assets/Scenes/SCN_RIGSTAGE.unity` |
| training scenes | `SCN_TRAIN_BALANCE`, `SCN_TRAIN_LOCO`, `SCN_TRAIN_STRIKE`, `SCN_TRAIN_SELFPLAY` |
| match scene | `Assets/Scenes/SCN_MATCH.unity`; menu/opening: `SCN_MAIN` |
| `BipedBoxingAgent.cs` | `Assets/Scripts/Agent/Agent_FighterBoxing.cs` |
| `BiomechanicalStamina.cs` | `Assets/Scripts/Systems/Systems_Stamina.cs` |
| heuristic bot | `Assets/Scripts/Agent/Agent_HeuristicBoxer.cs` |
| sensors | `Assets/Scripts/Sensor/Sensor_GroundContact.cs`, `Sensor_Proprioception.cs`, `Sensor_OpponentRelative.cs` |
| rewards | `Assets/Scripts/Reward/Reward_Balance.cs`, `Reward_Punch.cs`, `Reward_Energy.cs` |
| match core | `Assets/Scripts/Systems/Systems_RoundManager.cs`, `Systems_Scoring.cs`, `Systems_ImpactEvaluator.cs` |
| broadcast | `Systems_CameraDirector.cs`, `Systems_Hud.cs`, `Systems_ImpactAudio.cs`, `Systems_Replay.cs` |
| editor tool | `Assets/Scripts/Editor/RigTool*.cs` (menu `Tools → ML Boxing → …`) |
| trained models | `Assets/Agents/<FighterName>_v<NN>/<FighterName>.onnx` (overwrite in place, keep GUID) |
| prefabs | `Assets/Prefabs/Fighters/<ModelName>.prefab` |
| configs ↔ run IDs | `BoxerBalance01.yaml` ↔ `--run-id=boxer_balance01`, etc. |
| env builds | `Builds/BoxerEnv/` |

---

## Edge Cases

| Case | Expected behavior |
|---|---|
| Rig tool: mesh missing required bones | Abort with a list of missing segments. Create nothing. No partial rigs. |
| Rig tool: run twice on same mesh | Detect existing rig; prompt re-rig (destroy + rebuild) or cancel. Idempotent. |
| Double KO (both down > 0.5 s) | Earlier trigger wins; same physics step → draw. |
| Round metric exact tie | 10–10 round. |
| Fighter falls during 10 s rest | No KO during rest; motors stabilized; KO watchdog paused. |
| Stamina fully drained | Spring floor = 35% of base (formula). Never 0, never NaN. |
| ONNX missing / obs-size mismatch at match load | Validation error before the bell; affected corner falls back to `Agent_HeuristicBoxer` and HUD shows a persistent "HEURISTIC BOT" tag. |
| Physics explosion (NaN pose or velocity > 50 m/s) | Training: watchdog ends episode. Match: round void, auto-reset, logged. |
| Fighter exits ring bounds | Ring ropes are colliders; escape beyond apron → training penalty + episode end; match → reposition at nearest corner. |
| Timescale 0.25× replay | Replay only in match builds; never in training; timescale restored on replay end even if interrupted. |
| Headless `--no-graphics` | Training scenes contain zero cameras/HUD/audio; presentation assembly not referenced by training assembly. |
| Simultaneous mutual clean hits in one step | Both counted; both impulses scored. |

---

## Integration Points (all-new project; MVS + VContainer + MessagePipe)

```
Editor RigTool ──(prefab)──► SCN_TRAIN_* ──(.onnx)──► Assets/Agents/ ──► SCN_MATCH

SCN_MATCH runtime:
Agent_FighterBoxing (View-ish adapter, MonoBehaviour)
   │ actions→joints            ▲ observations
   ▼                           │
Physics ──► Systems_ImpactEvaluator ──► publishes PunchLandedMessage, KnockdownMessage
Systems_RoundManager ──► RoundStarted/RoundEnded/MatchEndedMessage
Systems_Stamina ──► StaminaChangedMessage (mutates StaminaModel)
Systems_Scoring ◄── subscribes PunchLanded + RoundEnded → ScorecardModel
Broadcast (subscribers only): Systems_CameraDirector, Systems_Hud (UI Toolkit views
   observe Models via ReactiveProperty), Systems_ImpactAudio, Systems_Replay
```

| System | Direction | Messages |
|---|---|---|
| Systems_RoundManager | owns MatchModel | pub: RoundStarted, RoundEnded, RestStarted, MatchEnded |
| Systems_ImpactEvaluator | reads physics | pub: PunchLanded, HeavyHit, Knockdown, Knockout |
| Systems_Stamina | owns StaminaModel ×2 | pub: StaminaChanged; sub: RoundEnded (rest recovery) |
| Systems_Scoring | owns ScorecardModel | sub: PunchLanded, RoundEnded; pub: RoundScored |
| Systems_CameraDirector / Hud / ImpactAudio / Replay | read-only | sub: all of the above |
| Agent_FighterBoxing / Agent_HeuristicBoxer | writes joint targets | none (physics is the medium) |

Messages are `readonly struct`; brokers registered in `MatchLifetimeScope`. All timers via UniTask with CancellationToken. No coroutines, no singletons, no legacy Input.

**Assembly placement (new asmdefs):**
- `PoBox.Runtime` — Agent/Sensor/Reward/Systems (match core)
- `PoBox.Presentation` — cameras, HUD, audio, replay (references Runtime; Runtime NEVER references it)
- `PoBox.Editor` — rig tool (Editor folder)
- `PoBox.Tests.EditMode` / `PoBox.Tests.PlayMode`

---

## Acceptance Criteria

1. [ ] Rig tool on the capsule biped (or a humanoid FBX) outputs a prefab with 15 rigidbodies, 14 ConfigurableJoints, total mass within ±5% of the 75 kg baseline table, zero manual edits.
2. [ ] Rig tool on a mesh missing bones creates nothing and lists every missing segment (negative test).
3. [ ] "Prepare for Training" writes observation/action sizes into BehaviorParameters programmatically; a hand-edited mismatch is detected and reported at load.
4. [ ] Project asserts Δt = 0.02 s and solver 16/16 on play; any drift logs an error and refuses to start a match.
5. [ ] Stage-1 brain stands 60 s under random 50 N shoves in ≥ 90% of evaluation episodes.
6. [ ] A full unattended match runs 3 × 60 s + 2 × 10 s rests and ends with a scorecard (e.g. 29–28) with no human input.
7. [ ] Continuous canvas contact > 0.5 s by head/torso/pelvis/upper-arm triggers KO within 0.1 s; 0.4 s contact or glove/foot contact does NOT (negative test).
8. [ ] Judge output is deterministic: identical telemetry in → identical 10–9/10–8 card out.
9. [ ] Joint spring never drops below 35% of base at zero stamina; rest interval restores 80% of each pool's deficit.
10. [ ] `Agent_HeuristicBoxer` runs with zero ONNX inference, stays standing, and lands punches on the calibration dummy.
11. [ ] Broadcast match holds 60 FPS in Portrait 9:16 on the dev laptop; profiler shows 0 B GC alloc per frame in steady-state Update/FixedUpdate.
12. [ ] KO triggers 0.25× slow-motion, a 3-angle replay, then the victory screen; `Time.timeScale` returns to 1.0 afterward.
13. [ ] 16 parallel rings train headless (`--no-graphics`, explicit `--base-port`); TensorBoard is started automatically with every training run.
14. [ ] Self-play evaluation reports ELO per brain; win/loss + ELO persist as JSON and reload correctly.
15. [ ] Version stamp shows `Application.version` top-left of `SCN_MAIN`, non-pickable.

---

## Estimated Complexity

**Complex.** Active-ragdoll RL is the hardest common ML-Agents task: reward shaping for balance alone typically takes multiple config iterations, and every physics or observation change invalidates trained brains. The broadcast layer is conventional Unity work; the risk is concentrated in Stages 1–2 of training.

## Recommended Approach

Build in dependency order, each step verified before the next:
1. Packages + asmdefs + physics settings + `SCN_RIGSTAGE` (rig tool on one humanoid FBX).
2. `SCN_TRAIN_BALANCE` + Stage-1 training until the stand-up criterion passes (this de-risks everything).
3. Heuristic bot + impact evaluator + `SCN_TRAIN_STRIKE`.
4. Match core (rounds/scoring/stamina) in `SCN_MATCH` using the heuristic bot for both corners (no ML needed to test the match engine).
5. Broadcast layer. 6. Self-play + ELO + second fighter profile.

Use `/unity-workflow` per step; `unity-prototyper` for scene scaffolding, `unity-coder` for systems, `unity-test-runner` for the EditMode judge/stamina tests.
