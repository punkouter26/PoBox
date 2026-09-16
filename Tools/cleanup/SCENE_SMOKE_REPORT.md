# Non-training scene smoke test

Generated 2026-09-16 17:24 by `SceneTool_SmokeTest.RunAll`,
20 s of play mode per scene in the open Editor.

One `contestant` row per IContestFighter implementer (active or stood down).

## SCN_MENU

Errors and exceptions: **0**. Warnings: 0.

State flow: OK. `flow	no-referee`

```
scene	Assets/Scenes/SCN_MENU.unity
timeScale	1
fixedDeltaTime	0.02
gravity	(0, -9.81, 0)
flow	no-referee
contestants	0
```

## SCN_TEST_BALANCE_CONTEST

Errors and exceptions: **0**. Warnings: 1.

| count | type | message |
|---:|---|---|
| 1 | Warning | Compilation was requested for method `Unity.Jobs.IJobExtensions+JobStruct`1[[Unity.InferenceEngine.CPUBackend+CopyJob`1[[System.Single, mscorlib, Version=4.0.0.... |

State flow: OK. `flow	restartsAutomatically=True	roundsStarted=0	roundsEnded=0	matchDirector=yes	matchDecided=False	champion=(none)	verdict=ok`

```
scene	Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity
timeScale	1
fixedDeltaTime	0.02
gravity	(0, -9.81, 0)
flow	restartsAutomatically=True	roundsStarted=0	roundsEnded=0	matchDirector=yes	matchDecided=False	champion=(none)	verdict=ok
contestants	1
contestant	Systems_NickContestant	Nick	active=True	headAboveGround=2.389	reportsDown=False	position=(0.76, 1.93, -0.72)
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
flow	restartsAutomatically=True	roundsStarted=1	roundsEnded=0	matchDirector=yes	matchDecided=False	champion=(none)	verdict=ok
contestants	1
contestant	Systems_NickContestant	Nick	active=True	headAboveGround=0.867	reportsDown=False	position=(0.2, 0.62, 2.28)
```

