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

Pending fresh complete-round measurements. The earlier smoke test is only
historical context and was recorded before the preserved capsule removal.
