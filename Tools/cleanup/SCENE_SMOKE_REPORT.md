# Non-training scene smoke test

Generated 2026-09-08 23:25 by `SceneTool_SmokeTest.RunAll`,
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
fighter	Contest_Bot	Bot	joints=14	sensor=121	expected=121	model=(none)	behavior=HeuristicOnly	pelvisHeight=0.133	upright=0.041
fighter	Contest_Grandma	Grandma	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=0.758	upright=0.661
fighter	Contest_Grandpa	Grandpa	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=0.794	upright=-0.425
fighter	Contest_Raptor	Raptor	joints=13	sensor=114	expected=114	model=RaptorBalance01	behavior=InferenceOnly	pelvisHeight=0.478	upright=0.759
fighter	Contest_Standard	Standard	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=0.981	upright=0.98
contestants	1
contestant	Systems_NickContestant	Nick	headAboveGround=1.261	reportsDown=False	position=(-0.59, 1.17, -0.69)
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
fighter	Contest_Bot	Bot	joints=14	sensor=121	expected=121	model=(none)	behavior=HeuristicOnly	pelvisHeight=0.134	upright=0.036
fighter	Contest_Grandma	Grandma	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.208	upright=-0.417
fighter	Contest_Grandpa	Grandpa	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.174	upright=0.801
fighter	Contest_Raptor	Raptor	joints=13	sensor=114	expected=114	model=(none)	behavior=HeuristicOnly	pelvisHeight=0.098	upright=-0.432
fighter	Contest_Standard	Standard	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.145	upright=0.003
contestants	1
contestant	Systems_NickContestant	Nick	headAboveGround=0.271	reportsDown=False	position=(2.97, 0.2, -5.2)
```

