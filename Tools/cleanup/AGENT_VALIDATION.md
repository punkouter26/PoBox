# Skinned character validation

Approved scope: every existing skinned character completes a balance round and
walks from one side of the ring to the other. Basic acceptance is one successful
trial per task, not a statistical reliability claim. No current passes yet.

## Verified inventory (2026-09-14)

GLB JSON skin tables and live Unity SkinnedMeshRenderer searches in both contest
scenes agree on three distinct characters. Each source has one skin, 24 joints,
one skinned node (`char1`) and one animation (not yet assessed as training data).

| Character | Source | Live scene root | Existing simulation |
|---|---|---|---|
| Nick | Assets/MuJoCoCreature/Model/RIGGED_Nick.glb | Nick | MuJoCo, CreatureSentisController |
| Grandma | Assets/Art/2026_GrandmaRigged.glb | Contest_Grandma | Legacy PhysX, Agent_FighterBoxing |
| Grandpa | Assets/Art/2026_GrandpaRigged.glb | Contest_Grandpa | Legacy PhysX, Agent_FighterBoxing |

Root RIGGED_Nick.glb is byte-identical to the imported Nick source (SHA256
da38b1e7d522570bc480507b8f4c195e6fe3e6ffcf3754d45e863e9192c3631f), so it is
not a fourth character. Arena.glb and BoxingRing.glb have no skins. The existing
Raptor is assembled from primitive segments by RigTool_RaptorRig and has no
skinned renderer or supplied skinned source. Preserve it as an existing legacy
contestant; do not invent a new rig or train it as part of the skinned scope.

Source hashes:
- Grandma: 3958ce7593fbfc1f3fbd9b09914d9ed238471901fb75b404aa45557efa670bee
- Grandpa: 9b5bb09e9459c2f04c60b947a2d5d51ff81b1e159ef11b8ae4bc7723c2be399b

Nick's walking controller references Nick_Balance002/nick_balance_002.onnx,
locomotion observations enabled, decimation 1, scene step 0.02 seconds. A name
containing Balance is not evidence that this policy cannot walk.

## Measurement status

Fresh baseline on 2026-09-14, before repairs, with the full current roster:

| Character | Balance trial | Walking trial (5.6 m required) | Verdict |
|---|---|---|---|
| Grandma | Fell at 5.4 s | 0.84 m, fell | Fails both |
| Grandpa | Fell at 6.9 s | 0.64 m, fell | Fails both |
| Nick | Declared winner at 6.9 s | 0.00 m, incorrectly reported upright | Invalid fall detection; no pass |

Balance used Locomotion_gen25 for Grandma/Grandpa; walking used
Locomotion_gen18_34M with 127 observations, confirmed against the running agents
by SceneTool_AgentProbe. Nick successfully bound Nick_Balance002 with 127
observations and 30 actuators at decimation 1. Raptor remains legacy, with
RaptorBalance01 in balance and a heuristic (no brain) in walking.

The runtime probe reproduced Nick's **startHeadHeight=0** while his controller
was later bound and his head was only 0.173 m high. His reset count stayed zero
because contest auto-reset is disabled. Comparing head height against 40% of
zero can never detect an ordinary collapse. Both referees sample before the
controller's first FixedUpdate. Fix readiness and retest before accepting wins.

Reproduction: play either contest, then run the Unity menu
`PoBox/Scene/Probe Active Contest`. Read Temp/agent-probes/<scene>.txt. The probe
reads actual referee scores and model assignments without editing the scene.

## Actual contest rules

- Balance: maximum 30 seconds; ends earlier when all fall or only one of multiple
  entrants remains. Ranking is upright survival time, then mean standing height.
  For acceptance, record whether a character remained upright through round end
  and the actual duration; a short last-standing win does not establish 30-second
  robustness.
- Walking: 5.6 metres along the scene's goal direction, commanded at 1 m/s,
  maximum 60 seconds. A race stalls after 12 seconds without at least 0.25 m of
  additional field progress. Finishers rank by finish time, others by upright
  distance. A 0.75 m partial-distance win is NOT a completed crossing.
- Falls: non-foot ground contacts or head height below 40% of starting height;
  external contestants also expose a down/reset signal.
- Balance hazards: one random wind, gravity lean or ball-rain condition per
  round, plus existing shovers. Their actual reach across physics engines must
  be checked before claiming comparable exposure.
- Current scene configuration disables automatic restarts while a match
  director expects three wins. Initial round-start events are also absent in
  both referees. Record these as flow defects, not training failures.
- Existing menu/contest UI and input remain the presentation baseline. No new
  shove, start, pause or reset controls are requested.

## Physics audit

- Grandma/Grandpa each have 15 collision shapes and 75 kg total mass, but the
  live probe reports **all 105 internal collision pairs ignored**. This comes
  from Systems_FighterRig.DisableIntraRigCollisions. Their current legacy bodies
  do not satisfy the approved self-collision requirement.
- Those rigs allow 50 rad/s angular velocity; this is not a validated human
  movement limit. Keep legacy bodies for comparison while preparing realistic
  MuJoCo bodies for the skinned cast.
- Nick has zero PhysX colliders. The PhysX hazards and other contestants cannot
  physically contact his MuJoCo-only body through those components. Shared
  collision geometry and hazard exposure must be implemented and tested.
- The saved Nick MJCF enables contact and carries a geometry for every segment,
  with parent filtering enabled. All 30 position actuators are force-unlimited.
  Finite human-scale force limits and measured movement speeds are prerequisites
  for a realistic new body; changing them requires reevaluating the policy.
- Embedded clips in the three GLBs contain only one timestamp (0.0333 seconds).
  They supply a pose, not walking motion capture.
- This machine has an RTX 2060 with 6 GB, not the training machine documented
  in older notes. The MuJoCo Python environment and local training checkpoints
  are absent. Restore a compatible environment before starting training.

## Measurement repairs verified

Both referees now wait for the external controller to bind, reset it before
sampling height, and issue the initial round-start event. A 10-second readiness
timeout reports a simulator failure instead of awarding a win. Updates do not
score an uninitialized round.

The balance floor is raised by 1 m. Nick's previous head measurement included
that metre, allowing a collapsed head at world height 1.157 m to count as
upright. Ground references were assigned directly in both scenes through Unity
Pipeline. Head, pelvis observation, foot-height observations and auto-fall
thresholds now use floor-relative heights. World travel uses current MuJoCo
state rather than a transform that may lag behind a reset. Walking directions
use the plugin's own coordinate conversion; the previous extra minus sign
commanded movement toward the wrong end of the ring.

Fresh regression trials: standing head height **1.38933 m in both scenes**;
roundsStarted=1; Nick correctly marked fallen in both. Walking reached 0.109 m;
balance survived 2.22 s. These are failures requiring further work, not passes.
Grandma also survived one 30-second legacy balance trial under a different
random hazard, but its self-collision defect remains and walking still fails.

Unity compilation: no errors. Shippable scene reference audit: all clear.
Scene saves regenerate MuJoCo display mesh IDs; serialized mesh references were
checked separately from code diffs. No character hierarchy was removed.

## Control timing and heading repairs verified

Unity's MuJoCo component copies actuator controls after stepping. The controller
now also writes the native control buffer before that step, matching training
and removing an unintended 20 ms action delay. World-vector observations are
expressed in the authored creature's starting frame, so placing a rig facing
the other way does not change what its policy sees. Nick was turned toward the
walking finish through Unity Pipeline; the controls and appearance are unchanged.

With the existing Nick_Balance002 policy, Nick survived a 6.84-second balance
round and remained upright for approximately 58 seconds in that trial. In the
walking scene on 2026-09-14 at 20:43 UTC, the referee measured a **full 5.6 m
crossing in 6.34 seconds**, finished=True and fallen=False. Grandma reached
0.849 m and Grandpa 0.677 m before falling. These results validate the runtime
repairs, but are not final shipping acceptance: finite actuator forces, shared
collisions and equal hazard exposure remain unresolved.

The local Python 3.11 environment now loads MuJoCo/Warp 3.12.0, Newton 1.6.0,
PyTorch 2.8.0 CUDA 12.8 and rsl-rl 2.3.3. CUDA detects the RTX 2060. Newton can
import Nick's 15-body MJCF. No new training run has started.
