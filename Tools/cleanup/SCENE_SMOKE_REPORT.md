# Non-training scene smoke test

Generated 2026-09-14 10:14 by `SceneTool_SmokeTest.RunAll`,
20 s of play mode per scene in the open Editor.

`sensor` is the width the VectorSensor was built at, `expected` is what
`ComputeObservationCount` derives from the rig. They must agree.

## SCN_MENU

Errors and exceptions: **0**. Warnings: 0.

```
scene	Assets/Scenes/SCN_MENU.unity
timeScale	1
fixedDeltaTime	0.02
gravity	(0, -9.81, 0)
fighters	0
contestants	0
```

## SCN_TEST_BALANCE_CONTEST

Errors and exceptions: **0**. Warnings: 1.

| count | type | message |
|---:|---|---|
| 1 | Warning | Compilation was requested for method `Unity.Jobs.IJobExtensions+JobStruct`1[[Unity.InferenceEngine.CPUBackend+CopyJob`1[[System.Single, mscorlib, Version=4.0.0.... |

```
scene	Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity
timeScale	1
fixedDeltaTime	0.02
gravity	(0, -9.81, 0)
fighters	5
fighter	Contest_Bot	Bot	joints=14	sensor=121	expected=121	model=(none)	behavior=HeuristicOnly	pelvisHeight=1.026	upright=1
fighter	Contest_Grandma	Grandma	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=0.944	upright=0.918
fighter	Contest_Grandpa	Grandpa	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=0.843	upright=-0.326
fighter	Contest_Raptor	Raptor	joints=13	sensor=114	expected=114	model=RaptorBalance01	behavior=InferenceOnly	pelvisHeight=0.689	upright=1
fighter	Contest_Standard	Standard	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=1.026	upright=1
contestants	1
contestant	Systems_NickContestant	Nick	headAboveGround=2.389	reportsDown=False	position=(0.76, 1.93, -0.72)
```

## SCN_TEST_WALK_CONTEST

Errors and exceptions: **0**. Warnings: 1.

| count | type | message |
|---:|---|---|
| 1 | Warning | Compilation was requested for method `Unity.Jobs.IJobExtensions+JobStruct`1[[Unity.InferenceEngine.CPUBackend+CopyJob`1[[System.Single, mscorlib, Version=4.0.0.... |

```
scene	Assets/Scenes/SCN_TEST_WALK_CONTEST.unity
timeScale	1
fixedDeltaTime	0.02
gravity	(0, -9.81, 0)
fighters	5
fighter	Contest_Bot	Bot	joints=14	sensor=121	expected=121	model=(none)	behavior=HeuristicOnly	pelvisHeight=0.139	upright=0.001
fighter	Contest_Grandma	Grandma	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.132	upright=-0.319
fighter	Contest_Grandpa	Grandpa	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.134	upright=0.824
fighter	Contest_Raptor	Raptor	joints=13	sensor=114	expected=114	model=(none)	behavior=HeuristicOnly	pelvisHeight=0.099	upright=-0.427
fighter	Contest_Standard	Standard	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.161	upright=-0.141
contestants	1
contestant	Systems_NickContestant	Nick	headAboveGround=1.419	reportsDown=False	position=(2.64, 0.15, -1.8)
```

