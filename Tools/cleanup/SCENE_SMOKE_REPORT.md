# Non-training scene smoke test

Generated 2026-09-14 21:19 by `SceneTool_SmokeTest.RunAll`,
20 s of play mode per scene in the open Editor.

`sensor` is the width the VectorSensor was built at, `expected` is what
`ComputeObservationCount` derives from the rig. They must agree.

## SCN_MENU

Errors and exceptions: **0**. Warnings: 0.

State flow: OK. `flow	no-referee`

```
scene	Assets/Scenes/SCN_MENU.unity
timeScale	1
fixedDeltaTime	0.02
gravity	(0, -9.81, 0)
fighters	0
flow	no-referee
contestants	0
```

## SCN_TEST_BALANCE_CONTEST

Errors and exceptions: **0**. Warnings: 1.

| count | type | message |
|---:|---|---|
| 1 | Warning | Compilation was requested for method `Unity.Jobs.IJobExtensions+JobStruct`1[[Unity.InferenceEngine.CPUBackend+CopyJob`1[[System.Single, mscorlib, Version=4.0.0.... |

State flow: OK. `flow	restartsAutomatically=True	roundsStarted=1	roundsEnded=0	matchDirector=yes	matchDecided=False	champion=(none)	verdict=ok`

```
scene	Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity
timeScale	1
fixedDeltaTime	0.02
gravity	(0, -9.81, 0)
fighters	3
flow	restartsAutomatically=True	roundsStarted=1	roundsEnded=0	matchDirector=yes	matchDecided=False	champion=(none)	verdict=ok
fighter	Contest_Grandma	Grandma	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=0.883	upright=0.979
fighter	Contest_Grandpa	Grandpa	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=0.784	upright=-0.317
fighter	Contest_Raptor	Raptor	joints=13	sensor=114	expected=114	model=RaptorBalance01	behavior=InferenceOnly	pelvisHeight=0.506	upright=0.618
contestants	1
contestant	Systems_NickContestant	Nick	headAboveGround=0.11	reportsDown=True	position=(0.96, 1.1, -1.38)
```

## SCN_TEST_WALK_CONTEST

Errors and exceptions: **0**. Warnings: 1.

| count | type | message |
|---:|---|---|
| 1 | Warning | Compilation was requested for method `Unity.Jobs.IJobExtensions+JobStruct`1[[Unity.InferenceEngine.CPUBackend+CopyJob`1[[System.Single, mscorlib, Version=4.0.0.... |

State flow: OK. `flow	restartsAutomatically=True	roundsStarted=1	roundsEnded=0	matchDirector=yes	matchDecided=False	champion=(none)	verdict=ok`

```
scene	Assets/Scenes/SCN_TEST_WALK_CONTEST.unity
timeScale	1
fixedDeltaTime	0.02
gravity	(0, -9.81, 0)
fighters	3
flow	restartsAutomatically=True	roundsStarted=1	roundsEnded=0	matchDirector=yes	matchDecided=False	champion=(none)	verdict=ok
fighter	Contest_Grandma	Grandma	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.172	upright=-0.476
fighter	Contest_Grandpa	Grandpa	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.145	upright=0.59
fighter	Contest_Raptor	Raptor	joints=13	sensor=114	expected=114	model=RaptorBalance01	behavior=InferenceOnly	pelvisHeight=0.509	upright=0.749
contestants	1
contestant	Systems_NickContestant	Nick	headAboveGround=0.131	reportsDown=True	position=(2.67, 0.13, -1.73)
```

