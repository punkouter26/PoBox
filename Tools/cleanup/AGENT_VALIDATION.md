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
