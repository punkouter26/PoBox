# Non-training scene smoke test

Generated 2026-09-14 13:38 by `SceneTool_SmokeTest.RunAll`,
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

Errors and exceptions: **2**. Warnings: 1.

| count | type | message |
|---:|---|---|
| 1 | Error | Systems_MatchDirector: the referee is set not to start another round, so a 3-round match can never be decided — no champion will be crowned and this scene will ... |
| 1 | Warning | Compilation was requested for method `Unity.Jobs.IJobExtensions+JobStruct`1[[Unity.InferenceEngine.CPUBackend+CopyJob`1[[System.Single, mscorlib, Version=4.0.0.... |
| 1 | Error | SceneTool_SmokeTest: state flow BROKEN — the referee will not start another round and a match director needs three, so no champion can be crowned and this scene... |

**State flow: BROKEN.** `flow	restartsAutomatically=False	roundsStarted=0	roundsEnded=0	matchDirector=yes	matchDecided=False	champion=(none)	verdict=BROKEN`

```
scene	Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity
timeScale	1
fixedDeltaTime	0.02
gravity	(-0.16, -9.81, -0.27)
fighters	5
flow	restartsAutomatically=False	roundsStarted=0	roundsEnded=0	matchDirector=yes	matchDecided=False	champion=(none)	verdict=BROKEN
fighter	Contest_Bot	Bot	joints=14	sensor=121	expected=121	model=(none)	behavior=HeuristicOnly	pelvisHeight=0.161	upright=0.085
fighter	Contest_Grandma	Grandma	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=0.885	upright=0.985
fighter	Contest_Grandpa	Grandpa	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=0.797	upright=-0.476
fighter	Contest_Raptor	Raptor	joints=13	sensor=114	expected=114	model=RaptorBalance01	behavior=InferenceOnly	pelvisHeight=0.499	upright=0.715
fighter	Contest_Standard	Standard	joints=14	sensor=127	expected=127	model=Locomotion_gen25	behavior=InferenceOnly	pelvisHeight=0.143	upright=-0.016
contestants	1
contestant	Systems_NickContestant	Nick	headAboveGround=1.199	reportsDown=False	position=(0.11, 1.17, -0.66)
```

## SCN_TEST_WALK_CONTEST

Errors and exceptions: **2**. Warnings: 2.

| count | type | message |
|---:|---|---|
| 1 | Warning | Systems_RaceCamera: the start line is 5.50 m across but this shot holds 4.40 m, so the outermost racers are outside the frame. Narrow the line (Systems_ContestS... |
| 1 | Error | Systems_MatchDirector: the referee is set not to start another round, so a 3-round match can never be decided — no champion will be crowned and this scene will ... |
| 1 | Warning | Compilation was requested for method `Unity.Jobs.IJobExtensions+JobStruct`1[[Unity.InferenceEngine.CPUBackend+CopyJob`1[[System.Single, mscorlib, Version=4.0.0.... |
| 1 | Error | SceneTool_SmokeTest: state flow BROKEN — the referee will not start another round and a match director needs three, so no champion can be crowned and this scene... |

**State flow: BROKEN.** `flow	restartsAutomatically=False	roundsStarted=0	roundsEnded=0	matchDirector=yes	matchDecided=False	champion=(none)	verdict=BROKEN`

```
scene	Assets/Scenes/SCN_TEST_WALK_CONTEST.unity
timeScale	1
fixedDeltaTime	0.02
gravity	(0, -9.81, 0)
fighters	5
flow	restartsAutomatically=False	roundsStarted=0	roundsEnded=0	matchDirector=yes	matchDecided=False	champion=(none)	verdict=BROKEN
fighter	Contest_Bot	Bot	joints=14	sensor=121	expected=121	model=(none)	behavior=HeuristicOnly	pelvisHeight=0.123	upright=0.109
fighter	Contest_Grandma	Grandma	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.209	upright=-0.575
fighter	Contest_Grandpa	Grandpa	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.135	upright=0.755
fighter	Contest_Raptor	Raptor	joints=13	sensor=114	expected=114	model=(none)	behavior=HeuristicOnly	pelvisHeight=0.105	upright=-0.331
fighter	Contest_Standard	Standard	joints=14	sensor=127	expected=127	model=Locomotion_gen18_34M	behavior=InferenceOnly	pelvisHeight=0.149	upright=-0.025
contestants	1
contestant	Systems_NickContestant	Nick	headAboveGround=0.161	reportsDown=False	position=(2.24, 0.13, -4.4)
```

