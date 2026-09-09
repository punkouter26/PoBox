# Non-training scene smoke test

Generated 2026-09-09 07:47 by `SceneTool_SmokeTest.RunAll`,
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
gravity	(-0.12, -9.8, 0.41)
fighters	5
fighter	Contest_Bot	Bot	joints=14	sensor=121	expected=121	model=(none)	behavior=HeuristicOnly	pelvisHeight=0.134	upright=0.037
fighter	Contest_Grandma	Grandma	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=0.258	upright=0.295
fighter	Contest_Grandpa	Grandpa	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=0.174	upright=-0.651
fighter	Contest_Raptor	Raptor	joints=13	sensor=114	expected=114	model=RaptorBalance01	behavior=InferenceOnly	pelvisHeight=0.133	upright=-0.115
fighter	Contest_Standard	Standard	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=0.149	upright=-0.076
contestants	1
contestant	Systems_NickContestant	Nick	headAboveGround=1.168	reportsDown=False	position=(0.2, 1.13, -0.79)
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
fighter	Contest_Bot	Bot	joints=14	sensor=121	expected=121	model=(none)	behavior=HeuristicOnly	pelvisHeight=0.14	upright=0.001
fighter	Contest_Grandma	Grandma	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.139	upright=-0.3
fighter	Contest_Grandpa	Grandpa	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.133	upright=0.795
fighter	Contest_Raptor	Raptor	joints=13	sensor=114	expected=114	model=(none)	behavior=HeuristicOnly	pelvisHeight=0.102	upright=-0.413
fighter	Contest_Standard	Standard	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.195	upright=-0.268
contestants	1
contestant	Systems_NickContestant	Nick	headAboveGround=0.148	reportsDown=False	position=(2.77, 0.13, -3.64)
```

