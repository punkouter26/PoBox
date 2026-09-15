# Non-training scene smoke test

Generated 2026-09-14 21:47 by `SceneTool_SmokeTest.RunAll`,
20 s of play mode per scene in the open Editor.

`fighter` rows are PhysX rigs (none since the cast was removed on 2026-09-14);
`contestant` rows are IContestFighter implementers such as Nick.

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

State flow: OK. `flow	restartsAutomatically=True	roundsStarted=2	roundsEnded=1	matchDirector=yes	matchDecided=False	champion=(none)	verdict=ok`

```
scene	Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity
timeScale	0
fixedDeltaTime	0.02
gravity	(0, -9.81, 0)
fighters	0
flow	restartsAutomatically=True	roundsStarted=2	roundsEnded=1	matchDirector=yes	matchDecided=False	champion=(none)	verdict=ok
contestants	1
contestant	Systems_NickContestant	Nick	headAboveGround=1.389	reportsDown=False	position=(0.76, 1.93, -0.72)
```

## SCN_TEST_WALK_CONTEST

Errors and exceptions: **0**. Warnings: 1.

| count | type | message |
|---:|---|---|
| 1 | Warning | Compilation was requested for method `Unity.Jobs.IJobExtensions+JobStruct`1[[Unity.InferenceEngine.CPUBackend+CopyJob`1[[System.Single, mscorlib, Version=4.0.0.... |

State flow: OK. `flow	restartsAutomatically=True	roundsStarted=2	roundsEnded=2	matchDirector=yes	matchDecided=False	champion=(none)	verdict=ok`

```
scene	Assets/Scenes/SCN_TEST_WALK_CONTEST.unity
timeScale	1
fixedDeltaTime	0.02
gravity	(0, -9.81, 0)
fighters	0
flow	restartsAutomatically=True	roundsStarted=2	roundsEnded=2	matchDirector=yes	matchDecided=False	champion=(none)	verdict=ok
contestants	1
contestant	Systems_NickContestant	Nick	headAboveGround=0.129	reportsDown=True	position=(2.67, 0.13, -1.73)
```

