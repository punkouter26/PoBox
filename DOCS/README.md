# PoBox — project summary

Two physics-driven fighters box in a ring while the game films it like a broadcast: cameras that cut on
the action, a live scoreboard, three judges, a ladder, a crowd and a commentary line. The app starts on
a screen where the two boxers are chosen. Portrait, one screen, nothing scrolls. Unity 6000.6.0f1, URP
17.6, Cinemachine 6.6, Timeline 6.6, UI Toolkit.

**Current documentation set (2026-10-04): `DOCS/20261004/`** (architecture, boxer dashboard, abilities grid,
model inventory, scene layout, benchmarks, training charts guide). The overview is the root `README.md`.
This file is the running history.

Read `AGENTS.md` for the owner's house rules. The sibling project `../PoDecath` is the older, larger
relative: same engine version, same house style, and the source of the render settings, icons and sound
recordings copied in here.

## The retrofit (started 2026-10-02, afternoon): read this first

The game is being moved onto the official MuJoCo Unity plugin (`org.mujoco` 3.5.0) with no PhysX in the
fight, on bodies re-derived from the meshes, with a ladder of behaviours under the boxing (stand, walk,
turn, get up) trained against shoves, thrown cubes and randomised bodies. **The plan and its ticks are
`tasks.md`; what was measured, run by run, is `rl_optimization_log.md`.** The working game as it was is the
tag `pre-retrofit`; since Phase D the fights on `master` run on the plugin.

- New bodies, each with a pool of 8 cubes: `training/rigs/v2`, `training/models/v2` (beside the old ones
  until the old policies are retired). Matt is 85.1 kg and 1.86 m there, Zombie 91.6 kg.
- New training files: `training/envs/footwork.py` (stand, walk, turn), `training/style.py` (the judge that
  pays for walking like the clips), `training/clips/walk_turn.npz` (free CMU motion capture, retargeted),
  `training/tools/footwork_c.py` (the exam and the Unity reference, in plain C MuJoCo).
- The trainer's Python on this machine is 3.11.9 with MuJoCo 3.14.1 and MuJoCo Warp 3.14.0, and has no
  `pip`. MuJoCo 3.5.0 for Python, the version Unity will run, is in `training/.mj350` (not versioned):
  `PYTHONPATH=training/.mj350` in front of a tool makes it use that.

**Where it stands (2 October, 22:00): the game runs on MuJoCo; the ladder is being trained.**

- **The boxing game is on the plugin (Phase D).** `Assets/Scenes/Arena.unity` is authored from prefabs: the trainer's
  ring (floor, four rope walls, 8 cubes) under `Mj World`, one `MjRing`, and every boxer twice
  (`Assets/Boxers/NAME/NAME Red.prefab` and `NAME Blue.prefab`, switched off; the blue one's objects are named `b_`).
  `MatchSetup` switches on the picked pair and hands it to the ring before MuJoCo compiles. The old PhysX layer,
  the custom MuJoCo binding, the scripted stand-in and the 21 pair layouts are deleted; the arena holds no
  Rigidbody, ArticulationBody, Collider or Joint. Report: `DOCS/reports/2026-10-02-arena-on-mujoco.html`.
- Runtime: `Assets/Scripts/Mj` (`MjBoxer`, `MjRing`: the bout's physics, punches measured as the trainer measures
  them, holding a boxer through a count, ghosting a downed one; `MjCubePool`, `MjTestbed`) and
  `Assets/Scripts/Runtime/Sim/MjBrain.cs` (between a `Fighter` and its `MjBoxer`: drive strength, get-up, punches
  named). Corner prefabs and the arena: `MjRetrofit.BuildCorners`, `MjRetrofit.RetrofitArena` (run once).
- `Assets/Scenes/Testbed.unity` holds all seven (`MjTestbed.roster`; `MjTestbed.Pick` chooses one by name). The
  gate tests (`MjGateTests`) run for every boxer with C MuJoCo recordings beside it; `MjRetrofit.Promote(name, run)`
  puts a run's policy and recordings there.
- **Android:** the `bin.mujoco` 3.5.0 tag ships a MuJoCo **3.3.7** Android library, so `Packages/bin.mujoco` is
  embedded with a 3.5.0 one built here (`Packages/bin.mujoco/README-PoBox.md`). The APK builds
  (`PoBox/Android/Build APK`, 219 MB); it has not been run on a phone yet.
- **Ladder** (exams in plain C MuJoCo 3.5.0, `training/tools/footwork_c.py --exam`): Matt R0 and R1 pass, R2 (turn)
  failed at 73% and is being retrained with a sharper facing reward (`r2b_matt`); Zombie R0 to R2 pass and his gate
  in Unity passes. The other five, the match (R4 to R7) and get-up (R3) runs are in `training/logs/queue.json`.

Earlier in the day (Phase B):

- All seven boxers are prefabs of the MuJoCo plugin's components with their meshes bound (`Assets/Boxers/NAME`),
  and each compiles to the trainer's model with no difference (`PoBox/Mj/Check Every Boxer Against Training`).
- `Assets/Scenes/Testbed.unity` is the scene every rung is checked in: the floor, the pool of 8 cubes,
  a HUD with reset, shoves, a cube and a behaviour selector. Report: `DOCS/reports/2026-10-02-retrofit-testbed.html`.
- The plugin is embedded in `Packages/org.mujoco` with three small changes marked `PoBox:` (it did not compile on
  Unity 6000.6 and its scenes did not load in MuJoCo 3.5.0).
- Play-mode tests `MjParityTests`: no Unity physics in the scene; the compiled model is the trainer's; a
  zero-action run of 5 s with a cube strike matches plain C MuJoCo 3.5.0 to 1.8e-05 rad a joint.
- Two things worth knowing before touching this: the plugin copies each `MjActuator.Control` over `mjData.ctrl`
  after every step, so targets are written again before each one; and `mj_step` leaves body positions one step
  behind `qpos`, so they are brought up to date (`mj_kinematics`) before the boxer observes, as the trainer does.
- Running the whole play-mode suite runs the plugin's own tests after ours, and five of them then fail on a
  scene left behind (raw object names); on their own they pass (11 of 11). Run ours with the assembly filter
  `PoBox.Tests`.

## Sokol in the menu; punches that land, boxers that tire (2026-10-16, morning)

- **Sokol is the eighth card** on the first screen and boxes one-on-one: `m2_sokol` (passes every line of the boxing
  exam on MuJoCo 3.5.0, re-run because the night's exam had run on 3.11) and his own get-up `u1_sokol_matt`
  (exported at 103 inputs, as Kim's), `PromoteBoxing`. His red and blue corners are in `Arena.unity` and
  `MatchSetup`, placed as Kim's; portrait taken in a fight against Matt (Matt's put back). Gate tests 24/24.
- **The owner's report**: a boxer spams one punch, never tires, and takes 50 head shots with no visible effect.
  Causes found: five of the eight had no fatigue in the game at all (their `config.json` had no `fatigue_j`); where
  it was on, 30 kJ refilled faster than a busy boxer spent it (Matt never went under 90%); the head is a ball on
  the trunk (no neck), MuJoCo passes on only the glove's 0.8 kg, the daze drains in a couple of seconds, and a
  punch cost 0.1 health (94 HP left after 194 punches). The spam is body shots, which only fed 30% of the daze.
- **In the game now, for every boxer** (no retraining needed):
  - fatigue for all eight at `fatigue_j` 8000 (Matt gets to 22% by the end of a busy round, back up on the stool);
  - a body shot takes the taker's wind (`Fighter.bodyDrain`, 0.25% of the reserve per m/s);
  - a head shot at 5 m/s or more rocks the boxer: drives held under 55% for 0.3 s (`Fighter.stagger*`);
  - damage that lasts the fight (`Fighter.Hurt`, the daze's input draining over 10 minutes, 15% off on the stool):
    at its full 400 the legs go at 60% of the usual daze and the drives have lost 25%;
  - a landed punch pushes the taker's trunk at the point it landed with 0.6 of its impulse over the next control
    step (`MjRing`, config `hit_push`);
  - the drawn head snaps away from the glove on a spring and comes back, and a spent or dazed boxer's chin sinks
    (`FighterSkin`, the neck bone only: no number of the fight changes);
  - punch damage 2.5x (`damagePerNs` 0.045 on the 16 corner prefabs).
  A full bout Matt v Zombie under all of it: no knockdowns, reserves down to a fifth, Zombie's lasting damage a third
  of the way. Tests: MjGate 24/24, MjParity 3/3, StateFlow 1/1, FreeForAll 1/1, OneScreen 47/47.
- **Not fixed by any of that**: the brains themselves. Matt still lands about three body shots a second and Zombie
  barely punches back. The rules are now in the trainer and the exam too (`envs/boxing.py`, `tools/exam.py`: config
  keys `hit_push`, `stagger_v`, `hurt_full`, `body_drain`, `obs_state`; `set_rules.py` sets them), and a match policy
  can read its reserve, its daze and the opponent's in the command's three numbers (`--obs-state`; the game fills
  them only for a config with `obs_state`). Retraining all eight under them is staged, not run:
  `training/queue_punch_staged.json` (rules, `m9_NAME` + exam for each, then the shipped brains examined under the
  same rules as the baseline). See `rl_optimization_log.md`.

## Mobile UI: zero-scroll and integrity, ten options (2026-10-16 night)

Owner: "do all" of ten UI options. The G4 pass had already done the anchors, the one results card, the tabbed settings,
icon switches, gestures and the phone shapes, so these were the gaps left. Report: `SCREENSHOTS/2026-10-16-ui-integrity/report.html`.

- **Back button** (Android's back is `KeyCode.Escape` under the old Input Manager): `MenuView.Back`, `HudView.Back`,
  `MeleeHud.Back` put away the open sheet first, then a playing replay, then go back (arena and free-for-all to the
  menu; the bare menu quits the app). There was no handler anywhere before.
- **Clock reset**: `MenuView.OnEnable` sets `Time.timeScale = 1`. The free-for-all's bullet time (`ImpactVfx`, no Bout
  to own the clock) set it directly and nothing put it back when the scene was left in the middle of one.
- **LayoutAudit** has two more rules (both in `Passed`): **reach**, the way on (FIGHT, ALL, CUP, NEXT, MENU, REPLAY,
  SHARE, AGAIN) centred in the bottom 40% of the safe area; **wordy**, a button's word (`btn-word`, `thumb-word`,
  `thumb-sub`) one word of at most 8 letters, numbers and dots aside (`LayoutAudit.Wordy`). Four words shortened to
  pass: NEXT BOUT to NEXT, CHANGE BOXERS to BOXERS, RESET LADDER to RESET (SURE? still asks), FREE-FOR-ALL under ALL
  to MELEE. A third rule, **spill**: an in-flow element out of its parent's top or bottom. It found the settings'
  DEBUG tab broken since the cornerman field was added: a 296 px page holding two rows of buttons and two fields
  (384 px), so the rows were squeezed over each other and the TWITCH field was hidden. Now one row of four
  (X-RAY, BRAIN, READINGS, RESET) and the two fields.
- **Free-for-all result**: at the end each boxer's row on the board shows its punches landed/thrown beside WIN or OUT,
  so the card's "MOST LANDED" line went (one line shorter); fastest glove and hardest punch stay.
- **Tests**: `OneScreenTests` runs 16 screen shapes (was 10): adds a home bar alone, a punch-hole with curved edges, a
  3:4 tablet (plain and with a bar) and a foldable's inner screen (plain and with side cutouts); `OneScreenTests.Phone`
  replaces the notch flag. `StateFlowTests` fails on any warning (bar the Inference Engine's Burst cold start, which
  has no stack and is matched by text) and has a second test: back on each screen, CUP > NEW CUP > FIGHT counted for
  the cup, NEXT, a replay cut short by the menu's NEXT, back during a count, back out of the free-for-all in bullet
  time (the owner's `career.json` is kept aside and put back). New `SceneIntegrityTests`: every scene (switched-off
  objects included, so every corner prefab) for missing scripts, references to deleted objects (`SerializedProperty
  .objectReferenceEntityIdValue` valid with no object; `GetInstanceID` is an error in 6000.6) and UIDocuments without
  a layout or panel; a self-check plants a deleted mesh reference.
- **Clean-up**: `Assets/_Recovery/0.unity` (an Arena autosave from the 09:30 crash, older than the venue commits) moved
  to `../PoBox_quarantine_2026-10-16/`. The Android scripting define `APP_UI_EDITOR_ONLY` (now as on Standalone; the
  editor-only App UI package is a dependency of the AI assistant) committed.
- Not done: **a phone run** (no device attached; D6, G10, I8 stay open).
- Tests: OneScreen 72/72, StateFlow 2/2, SceneIntegrity 5/5, FreeForAll 1/1, MjParity 3/3 (MjGate not run: no physics or brain changed).

## Graphics and audio: ten options (2026-10-16 evening)

Owner: "do all 10" of the graphics and audio options. Several overlapped what was there (the report's table says
what); only the missing parts were built. Report: `SCREENSHOTS/2026-10-16-gfx-audio/report.html`. Three commits.

- **Screen ripple** (`Fx/ScreenRipple.cs`, `Shaders/ScreenRipple.shader`): a full-screen pass on both URP renderers
  (`FullScreenPassRendererFeature` "Screen Ripple", off until a clean punch over 14 N s and 1.4 times the usual, then
  0.45 s) bends the picture in a ring from the punch. **Skin**: the fighter overlay runs a ring of light over the skin
  from a clean punch, and glows amber round joints working past half their limit (`FighterSkin`, `FighterOverlay.shader`).
  A boxer on the ropes under pressure gets its corner's rim brighter.
- **Collision fit** (`training/tools/fit_check.py`, `glb_to_rig.py --fit-report`): each trained radius against the
  part's thickness the thin way, plus the self-contact check: body capsules 56-95%, **head spheres 100-138%** of the
  head's half-width (heads collide a little outside the skull; not changed: it would need retraining).
  `DOCS/reports/2026-10-16-collision-fit.html`. A first try measured the live Unity skin and was wrong (the skin is
  bound and scaled at runtime by SkinBinder); removed.
- **Venue** (Blender 5.2 over MCP, `BLENDER/2026-10-16_arena-props`: original scene, working file, GLBs): ring steps
  at the red and blue corners, a press row, two TV cameras, 28 barrier sections at 7 m, a hung screen. Prefabs in
  `Assets/Prefabs/Props`, placed by `PoBox/Do All/Graphics` under `Environment/Venue` in both scenes. Visual only.
- **Light**: a beam through the haze under each ring light (`Shaders/LightBeam.shader`, cone in
  `Assets/Settings/LightBeam.asset`; `ArenaMood.beams`). **Looks** (`Fx/PhaseLook.cs`, `Assets/Settings/Look_*.asset`):
  walk-on (vignette, contrast), replay (Gaussian depth of field past the ring, low motion blur, cooler), result
  (warm, bloom up).
- **Sound**: a planted foot pivoting fast squeaks (`MjRing.FootSpin`); each glove's air follows its measured speed
  (3.5-9 m/s), replacing the fixed whoosh on every throw; half the crowd bed comes from eight 3D sections round the
  hall, the scorer's half up (`ArenaMood.Side`).
- **HUD**: GPU frame time in the debug readings (`PerfTelemetry.GpuMs`, Frame Timing Stats on in Player Settings);
  the three display sizes are theme tokens (`--t-hero`, `--t-banner`, `--t-count`).
- Tests: OneScreen 47/47, StateFlow 1/1, MjParity 3/3, FreeForAll 1/1 (gates 24/24 before this pass; the physics was
  not changed, only measured: `MjRing.FootSpin`, `MjBoxer.GlovePosition`).

## Features pass: punch card, AI cornerman, ringcraft, fight log, CPU Elo (2026-10-16 afternoon)

Owner picked 3, 5, 7 and 9 of ten feature options and asked for an Elo for every boxer from CPU bouts. Report with before/after
shots, the training charts and the Elo table: `SCREENSHOTS/2026-10-16-features/report.html`.

- **Punch card** (`UI/PunchCard.cs`, on the arena's HUD): after a clean punch of 11 N s or more (at most every 12 s) a card bottom
  right shows the half second before it from MuJoCo: hip, shoulder, elbow and glove peak speed and when each came
  (`MjBoxer.Chain`, mj_objectVelocity), IN SEQUENCE or ARMS ONLY, glove km/h, impulse, the floor's push in body weights. It gives
  way to the brain cam, the replay and the results, and rebuilds when the HUD's tree is rebuilt.
- **AI cornerman** (`Broadcast/CornerAI.cs`, `server/corner_proxy.py`, self-check `server/test_corner_proxy.py`): 6 s before the bell
  the round's numbers go to a proxy that holds the key and asks Claude Opus 5.5 (low effort, refusal fallback "default") for one
  line; it is said in the break if back in time, else the old rule-based tip. After a win the winner gets one line. Settings,
  DEBUG, CORNER: the proxy's address (editor default `http://127.0.0.1:8787/corner`; `?token=` if the proxy has `--token`).
  Not tried against the real API: no key on this machine. A phone needs Player Settings' plain-HTTP option or an https proxy.
- **Ringcraft**: commentary calls "trapped on the ropes" (2 s on the ropes under pressure; the HUD's rule, now
  `Fighter.OnTheRopes`) and clinches (pelvises within 0.55 m for 1.5 s). Training: `--clinch-w` and rc1_matt
  (`rl_optimization_log.md`): 37% less time on the ropes, not promoted (fails carrying on, as m9 does).
- **Fight log** (`League/FightLog.cs`, on the arena's Bout): each 1-on-1 bout with 10 s or more of fighting is a line in
  `Application.persistentDataPath/fightlog.ndjson`: result, cards, Elo change, each corner's brain, punches by kind and zone, impulse,
  blocks, knockdowns, metres walked, seconds on the ropes and chest to chest, effort per joint group, lowest reserve, and a sample a
  second (position, speed, reserve, effort). A first full bout: Matt threw 175 crosses and 97 body shots and nothing else.
- **CPU Elo** (`training/tools/elo.py`): 168 C MuJoCo bouts between the game's brains; `Assets/Resources/cpu_elo.json`. The ladder
  (`LeagueTable`) starts from those ratings and re-seeds when the file changes; watched bouts keep moving them. The league tab shows
  each boxer's CPU record. KIM and SOKOL have entrants now and the ladder has 8 rows.
- Tests: OneScreen 47/47 (the label first read CORNER AI and was clipped: 30 failures, renamed CORNER), StateFlow 1/1, FreeForAll 1/1, MjParity 3/3.

## Do-all pass and the overnight queue (2026-10-16)

Owner: run /init, /simplify, /code-review and /security-review, then "do all" of 50 options (features, graphics and
sound, UI, AI, cleanup), then train every behaviour for 10 hours. Report: `SCREENSHOTS/2026-10-16-do-all/ui.html`.

- **Fixes** (from /code-review): the eight Melee prefabs are authored round the ropes, not on one point, and the ring
  waits for the first phase; a recompile keeps who is held and down; a boxer held over a count in a pile stands on the
  getting-up collision layer; a boxer down during the walk-out or while squaring up is not handed to the match
  policy; the stand-in is re-aimed without a speed jump; a new target starts the glove's cool-down (waiting for 30 cm
  clearance instead cut the free-for-all's landed punches to 2 of 8 boxers); `Eliminate` while rising ends the
  get-up; MeleeCamera null slot; voices tool refuses unsaved scenes. Not changed: crowding tooltip now says what
  the formula does.
- **Free-for-all brains are staggered**: boxer k decides on substep k mod 4 (still 50 Hz each, same observe-act
  timing as training), so eight inferences are not paid on one physics step. Pairs are unchanged (parity 3/3).
  Opponent choice also prefers a dazed boxer (`opportunism`).
- **Features**: KO instant replay (`Mj/MjReplay.cs`, 8 s of qpos at 50 Hz, exact state restore), punch stats on the
  result, brain cam (settings), Twitch chat votes and `!cube` (anonymous read-only, `pobox.twitch`), an 8-boxer cup,
  a corner-man tip between rounds, a pick-the-winner game with coins, SHARE (MediaStore + share sheet on Android).
  MjRing now binds the ring's cube pool (parked where the trainer parks them).
- **Graphics/sound**: daze heartbeat on screen, crowd ducks under the bell, ropes bow from `MjRing.RopeLoad` (RopeFlex
  looked for Colliders the arena no longer has), hot-exchange spotlight, face flush, X-ray contact arrows, bullet time
  on clean knockouts (`ArenaMood.TimeScaleK`, settings "Slow-mo"), `CameraJolt` for Melee, a hall reverb zone, physics
  and inference ms in `PerfTelemetry`. Blender MCP could not reach Blender: the arena props were not made.
- **UI**: five anchors on every screen (title, FPS, menu, DEBUG, version), one results card with REPLAY / SHARE / NEXT
  / MENU, settings as SOUND | DISPLAY | DEBUG tabs, icon switches, the menu as a 2x4 grid with CUP | FIGHT | ALL in
  the thumb zone, three-finger tap for the debug panel, names clamped. Tests: OneScreen 47/47 (now 1080x2340 too),
  new StateFlowTests 1/1, FreeForAll 1/1, MjParity 3/3.
- **Cleanup**: about 543 MB moved (not deleted) to `../PoBox_quarantine_2026-10-16/` with a MANIFEST: superseded and
  rejected checkpoints, their TensorBoard runs, the v1 handoff policies, a duplicate `Kim.glb`, a stray MuJoCo 3.15
  `mujoco.dll` in `Packages/org.mujoco`, the report PNGs (now WebP). 12 unused built-in modules removed; one-off tools
  in `training/tools/archive`; `run_night.py`, `run_match.py` and `MjRetrofit.RetrofitArena` removed. The v1 models
  stay: `make_handover_v2.py` finds stand-ins through `models/*_bag.xml`.
- **Training, staged**: off-by-default flags `--action-rate-w`, `--obs-delay-max`, `--seek-w`, `--ropes-start`;
  `tools/obs_audit.py`, `tools/pd_sweep.py` (+ `pd_gains_proposal.json`), `tools/bench_match.py`;
  `queue_doall_staged.json`. Flags off reproduce the old rewards exactly. See `rl_optimization_log.md`.
- **Seen, not fixed**: in the free-for-all several boxers slip in the first 20 s (Grandma, Grandpa, Nick, Sokol in the
  after shot), before and after these changes: their brains were never trained in a crowd (tasks.md Phase F).
- **Overnight** (from 23:39): Sokol's get-up, bank, boxing and exam; Nick's boxing; a seek run for Zombie; D8 for
  Grandma, Grandpa and Matt; Trump's D8 exam. The queue writes `logs/queue.stop` at the end. Nothing is promoted
  into Unity until the exams are read.

## Free-for-all, Sokol, and the boxers' voices (2026-10-15)

- **Free-for-all** (`Assets/Scenes/Melee.unity`, menu button ALL beside FIGHT): all eight in the one ring, boxed
  like the 1-on-1. Owner's rules: 3 rounds of 45 s with the bell; everybody starts at the ropes and meets in the
  middle; a punch knockdown gets its own count of ten while the rest fight on; counted out, a boxer stays where it
  lies; bodies are solid and pile; each boxer faces the nearest boxer on its feet; must run on the phone.
  - `FreeForAll` (Runtime/Sim) replaces `Bout` + `MatchSetup` there: it renames each red-corner prefab to a prefix
    of its own (a_ to h_: the plugin names MuJoCo's objects after the GameObjects) before MuJoCo compiles, colours it,
    retargets every FixedUpdate before the brains run (nearest on its feet; nearer if it just landed on this one;
    further for each boxer already on it), counts, rounds, judges (damage done and knockdowns, `Bout.Score`).
    A fall with no punch behind it is a slip: no count; down 15 s from one and the boxer cannot go on.
  - The opening, found by experiment: handed an opponent 5 m off, or handed over mid-stride, the match policies fell
    within seconds (7 of 8). So at the bell each walks out on its footwork policy (0.6 m/s), stops near another
    boxer, turns square to its opponent on the footwork policy, and only then does the match policy take over
    (`MjBrain.WalkOut/EndWalk`), as a training episode starts.
  - `MjRing` takes `boxers` (more than two) and `solidBodies`: punches and blocks are measured against each boxer's
    own opponent; bodies down are solid (standing 1/1, lying 2/3, getting up 4/5: a boxer rising passes through
    those lying, and stays so until clear). With two it does exactly what it did (all parity and gate tests pass).
    `MjBoxer.Face` swaps an opponent without a jump in the stand-in's head speed. `Fighter.Eliminate`.
  - `MeleeCamera`: close, on the hottest boxer and its opponent (heat from punches thrown, landed, taken, knockdowns),
    everybody still in when it is quiet, the winner from over the ropes at the end. `MeleeHud` + `Assets/UI/Melee.uxml`:
    round and time, FPS, settings, two rows of four, the referee's calls, AGAIN and MENU; in the layout audit and
    `OneScreenTests`. Bells, sting and applause through `SimBus.RaisePhase`; the ring stops at the final bell.
  - Built by `PoBox/Free-for-all/Build Melee Scene` (FreeForAllAuthoring). Known limit: the brains were trained
    one-on-one; in a crowd they slip often and get up from a pile badly. Training plan: `tasks.md`, Phase F.
- **Sokol, the eighth boxer**, is in Unity on his footwork brain (`f_sokol`) and Matt's get-up brain (87% up within
  8 s on his body in C MuJoCo, mark 80%): 0 differences from the trainer's model, gate tests on the testbed, in the
  free-for-all; not in the 1-on-1 menu until his boxing is trained. `MjBrain` now boxes on the footwork policy when a
  boxer has no match policy (after a get-up it had stayed on the get-up policy).
- **Voices**: a sharp exhale on most punches, a grunt on hooks, uppercuts and body shots, an "oof" on clean hits, a groan going down, panting and groaning as a
  boxer tires or is dazed. Made in code and written as WAVs (40) (`PoBox/Audio/Write Voice WAVs`, `VoiceWavs.cs`:
  glottal pulses and breath through three vowel formants) to `Assets/Audio/Synth/voice_*.wav`, a man's and a woman's
  register (`AudioDirector.highVoices`: Kim, Grandma). Whooshes and footsteps turned up. Both scenes.

## Broadcast, sound and portrait pass (2026-10-14)

26 of 30 proposed options (all but features 1, 5, 8, 9). Pictures before and after:
`SCREENSHOTS/2026-10-14/report.html`. 44 of 44 play-mode tests pass (`PoBox.Tests`).

- **New on `SimBus`: `Block(defender, at, speed)`**, raised from `MjRing.PunchStopped` via `MjBrain`; the block
  pop (AudioDirector) and puff (ImpactVfx) listen to it. `MjRing.RopeLoad(boxer, out at)` is the rope force the
  creak and twang read.
- **HUD:** fatigue bar and TIRED badge (boxers with fatigue on), ON THE ROPES, centre control on the round card
  (and a tie-break in `Judges`), damage ghost bar, X-RAY chip, VOICE and BUZZ in Settings (`SettingsPage.cs`,
  `Settings.uxml`, shared by HUD and first screen), one safe-area helper (`SafeArea.cs`), five anchors on the
  first screen and testbed (`TestbedHud.cs` moved to `Runtime/UI`), scene loads in the background.
- **Picture:** shockwave ring (`Shaders/Resources/Shockwave.shader`), the dazed look (a runtime volume on
  ImpactVfx), knockdown spot and desaturation (`ArenaMood.downSpot`), corner props from Blender
  (`Assets/Prefabs/Props`, sources in `BLENDER/2026-10-14_corner-props/` and `Assets/Models/Props/CREDITS.txt`;
  stools and buckets are `ArenaMood.breakProps`), the X-ray (`Fx/Xray/XrayShapes.cs`, its own assembly for
  unsafe code; dev command `xray on|off`). The director cuts to the impact camera ~0.13 s before a punch lands.
- **Phone only, untried:** haptics (`Audio/Haptics.cs`), spoken commentary (`Audio/SpeechCommentary.cs`; the
  TTS query is added to the manifest by `Editor/AndroidTtsQueries.cs`), the thermal governor in `PerfTelemetry`.
- **Training (staged, nothing run):** fatigue reserve now spent in footwork and get-up too (`BoxingEnv._spend`);
  `--gamma`, `--rope-w`, `--cutoff-w` on `train_gauntlet.py`; queues `training/queue_d8_staged.json` and
  `training/queue_ringcraft_staged.json`. See `rl_optimization_log.md`.

The sections below describe the game before the retrofit.

## Portrait compaction and integrity (2026-10-03)

Five of a list of ten; the other five (one screen, safe area, the five anchors, the debug panel on a chip, the
result card docked) were already in the game. Pictures before and after:
`DOCS/reports/2026-10-03-portrait-compaction.html`. 30 of 30 play-mode tests pass (one new).

- **The menu is a bottom sheet.** Docked where the result card sits (`.menu-sheet`), so the scoreboard stays in
  view and the cameras frame the boxers above it (`HudView.ReportPictureBand`). A tap on the picture closes it.
  Menu, debug panel and result card are up one at a time (`HudView.ShowResults`).
- **Icons for words.** The menu button is three bars, a cross while open (`GlyphKind.Menu`, `Close`); FOLLOW RED
  and FOLLOW BLUE are a red and a blue glove at the end of the camera row. All four frame chips are 88 px tall.
- **State flow.** A second tap on FIGHT or BOXERS no longer loads the scene again (`MenuView.Fight`,
  `HudView.ToBoxers`); a bout with an empty corner stops instead of throwing every frame (`Bout.Start`). The test
  `TheLoopFromTheMenuToAResultAndBackLeavesNothingBehind` walks menu, fight, a count cut short by RESTART, the
  result, the next bout and back, and fails on any error in the console.
- **Console.** The MuJoCo plugin's mouse-spring inspector threw at every recompile and Play with a body selected:
  guarded (`Packages/org.mujoco/Editor/Components/MjMouseSpring.cs`, the fourth change marked `PoBox:`).
  Compile warnings 22 to 0.
- **One stylesheet.** Radii, colours and text sizes in `Theme.uss` are tokens (the rule is at the top of the
  file); `Testbed.uxml` had its own inline styles and now uses the HUD's classes. Dev command `play-testbed`.
- **Not checked:** nothing has been on a phone; the tap that closes the menu has no test.

## Physics-driven effects (2026-10-03)

Five of a list of ten; six of the ten were already in the game, so these are the gaps. Pictures before and
after: `DOCS/reports/2026-10-03-physics-driven-fx.html`. 29 of 29 play-mode tests pass (one new).

- **Landings from MuJoCo's contacts.** `MjRing.TrackCanvas` reads `mjData.contact` once a control step and
  raises `BodyLanded` for every shape of a boxer (feet apart) that comes down on the floor, with the impulse
  the solver gave it; `MjBrain` passes it on as `SimBus.FloorImpact`. It replaces one guess a fall from the
  pelvis's speed. One knockdown and get-up: eleven landings, 7 to 166 N s. Dust, thud, scuff and rope shake
  are full at 120 N s; only landings over 30 N s mark the cloth.
- **Scrape.** `MjRing.FootSlide` is how fast a planted foot is sliding; `AudioDirector.Scrape` plays a loop of
  filtered noise made at start-up at each boxer's feet, silent under 0.12 m/s and full at 1.2
  (`scrapeSpeed`). **Not listened to.**
- **Hit pulse.** `Hit Pulse`, a second global volume beside `Look` (`Assets/Settings/Arena_Pulse.asset`, made
  by `AssetBakery.Pulse`: bloom, lens distortion, fringing, motion blur). `ImpactVfx` sets its weight from
  how far a clean punch is above the usual, to full on a knockdown, gone in 0.28 s. No depth of field: the
  owner had that look removed on 1 October. Its cost on a phone is not known.
- **Follow-spots.** In the fight each sweep lamp stays on its own boxer and comes up when that boxer lands a
  clean punch (`ArenaMood.sweepOnPunch`); the walk-on and the winner use them as before.
- **Steam.** `Steam` (a copy of `Canvas Dust`) puffs off a boxer's head as the tank falls: from 97%, full at
  60% (`ImpactVfx.steamTank`). The tank only gets to about 0.8 in a bout, so it is a wisp.
- Left out: Blender models and new shaders (nothing needed one), mesh deformation and heat glow.

## Broadcast telemetry (2026-10-03)

Five additions to the fight screen, picked by the owner from a list of ten, and a steadier announcer. Screens
before and after, annotated: `DOCS/reports/2026-10-03-broadcast-telemetry.html`. 28 of 28 play-mode tests pass.

- **Pods:** under each balance gauge a tank bar (`Fighter.Stamina`) and a badge saying what the boxer is doing:
  GUARD, the punch just thrown, DAZED, DOWN, RISING. `HudView.RefreshStatus`.
- **Round break:** the three judges' cards and the round's own landed-of-thrown and stopped punches, for the
  three seconds between rounds. `HudView.FillBreak`.
- **Camera:** FOLLOW RED and FOLLOW BLUE in the menu lock the close-up on one boxer (`BroadcastDirector.Focus`);
  a sideways drag on the picture takes the orbit camera and turns it (`TurnOrbit`). AUTO gives the cameras back.
  The drag was checked with synthetic pointer events, not on a phone.
- **On the canvas:** each boxer's foot patch and capture point (`Fx/BalanceMarks.cs`, two line renderers a
  boxer, under `Decals` in the arena). A clean punch a quarter harder than usual has its glove speed written
  where it landed (`HudView.flashOverUsual`); between Matt and Zombie few punches are.
- **Count:** an inset of the boxer left standing (`Cameras/PiP Camera`, drawing into `Assets/UI/Pip.renderTexture`
  only during a count). Its cost on a phone is not known.
- **Announcer:** the jaw reads 23 ms of the recording a little ahead of the sound and eases to it
  (`TeethAnnouncer.jawSmoothSeconds`), against the loudest the voice has been. Sharp reversals fell from 11 a
  second to under 1. **Measured, not watched.**
- Left out on purpose: slow motion, the highlight reel and the replay scrubber (removed on 1 October), and
  trajectory ribbons (a policy gives joint targets, not intentions).

## Finishing every boxer, on the new laptop (2026-10-02, afternoon)

Carried on at 13:10 on the new machine (RTX 5070 Ti Laptop GPU; the gauntlet runs at about 35,000 steps a
second here against 13,300 on the old one). Report with the TensorBoard charts explained:
`DOCS/reports/2026-10-02-training-gauntlets.html`.

- **First pass done, 16:53.** Matt, Nick, Lil Matt, Trump, Grandma and Grandpa each had a 36-minute gauntlet
  (`training/checkpoints/g1_<name>`). Full exam afterwards (`training/handoff/exam_1.log`): **0 of 7 pass**,
  one to four lines failed each. Footing fails for four (Matt 3.11 falls a minute against Grandpa, the others
  about 1), carrying on for six, Lil Matt's guard, Trump's and Lil Matt's chin. Zombie, who passed after his
  own gauntlet, now fails footing against the new Nick.
- **No second round.** One was started at 17:04 and stopped at 17:09 (`g2_nick`, marked rejected): the retrofit
  above changes the bodies, so these policies are warm starts for its boxing rungs (C7 in `tasks.md`), not
  entrants. `training/logs/policies.json` points at the six `g1_<name>` policies and Zombie's earlier one. The
  queue runner is stopped and nothing of this session is on the GPU.
- **Matt only stays up with his training noise on** (0 falls in 30 episodes against Grandpa with it, 19 without;
  the exam and the game run without). A low-noise run to cure it (`p1_matt`) is marked rejected.
- **Two GPU faults** at 14:21 and 14:27 (nvlddmkm event 13) each killed a run; cause not established. The
  queue runner carries on with the next job and a run keeps its last checkpoint (every 50 iterations).
- Nothing from today has been imported into the game. The trainer's Python on this machine has mujoco 3.14.1
  and torch 2.11 (the notes below say 3.14.0 and 2.5.1); Zombie's exam gives the same result here as there.

## Finishing every boxer: where it stood at the move (2026-10-02, 11:35, stopped unfinished)

**Stopped at 11:35 at the owner's request, to move the project to another computer.** One boxer of seven
(Zombie) passes the exam; six gauntlets were not run. The policies, the progress files and the steps to
carry on are in `training/handoff/` (start with its `README.md`); the trainer's Python environment is in
the sibling project `../PoDecath/training/.venv` and has to come too.

The owner's goal of 1 October, 23:48: train all boxers until they can do everything a bout needs. What
that means is one exam, `training/tools/exam.py` (footing, attack, guard, chin, getting up, carrying on);
the plan and the audit behind it are `DOCS/reports/2026-10-02-training-manifest.html`, and the tools are
described in `training/README.md` under "The exam, and finishing every boxer".

| | State at 11:30 |
|---|---|
| Exam after the league (07:20) | 0 of 7 pass. Everybody falls over against opponents they did not train with (Zombie: 5.7 falls a minute against Nick, none against Matt) |
| Getting up | all seven pass it in the exam (plain C MuJoCo): Matt 59/60, Zombie 60/60, Nick 59/60, Lil Matt 60/60, Trump 40/40, Grandma 35/40, Grandpa 39/40 |
| **Getting up, in the game** | **does not work yet**: Grandpa and Grandma got up 2 times in 8 and were counted out 6. The policies are therefore held out of the game (`training/checkpoints/getup_final/REJECTED.txt` says how to put them back) and the referee still stands a boxer up. Why the game differs from the exam is not yet known |
| Gauntlet (one boxer learns against all six others, frozen) | Zombie done and **passes all six lines of the exam**; his policy is in the game. Matt, Nick, Lil Matt, Trump, Grandma, Grandpa queued (`training/logs/queue.json`, about 36 minutes each) |
| Carrying on after a get-up | passes for Zombie and Grandma; trained for the others inside their gauntlets |

Also changed in the game on 2 October: the teeth announcer is in the picture only for the introductions
and the winner (`DOCS/reports/2026-10-02-teeth-announcer-intros.html`), and a boxer that gets up by itself
keeps the get-up policy for a second and a half of standing before the match policy has the body back
(`Fighter.handOverSeconds`).

## Five more boxers, and the teeth (2026-10-02)

Two things the owner asked for on the night of 1 October, done by a second session beside the broadcast pass.

**Seven boxers.** Trump, Grandma, Grandpa, Nick and Lil Matt joined Matt and Zombie: five rigged meshes from
the project root (copies in `Assets/Models`), each with a body fitted inside its own mesh and a way of boxing
of its own (brawler, counter-puncher, out-boxer, swarmer, body puncher). The grandparents are age-realistic by
the owner's choice: about half a trained adult's strength. Trained in an eight-hour league, 23:08 to 07:00;
report with the TensorBoard charts explained: `DOCS/reports/2026-10-02-training-roster.html`. How it is done:
`training/README.md`, "A roster".

- All seven are on the boxer menu and all 21 pairings have a MuJoCo ring (`Assets/MuJoCo/<a>_vs_<b>`). Three
  pairings were played from the menu on 2026-10-02 (Trump v Grandma, Grandpa v Nick, Lil Matt v Zombie); the
  other 18 have not been watched.
- **None of the seven passed the exam taken straight after the league** (`training/logs/exam_0.log`, all 21
  pairings in C MuJoCo): every boxer falls unprovoked, 1 to 6 times a minute in its worst pairing, against
  opponents it did not train with. The gauntlet rungs that follow are for that.
- Known: Grandma v Grandpa is probably a stand-off (it was in training); Trump does not close on an opponent
  who keeps out of his range; the new boxers have no get-up policy and little training under the hurt rule,
  so they go down often in the game. In the first seconds of a bout the new meshes drew as red and blue
  shells only (`DOCS/reports/img/39_shells_at_intro.png`); not investigated.
- A boxer's name on screen is the `display` in its rig file ("LIL MATT"); its portrait is taken with the dev
  command `portraits`, which the five have had.

**The ring announcer is the owner's teeth.** Two dental scans (`2025_TeethUpper.ply`, `2025_TeethLower.ply`)
hang over the middle of the ring on a cable, twelve times life size, facing the live camera. They come down
to speak: the corners at the walk-on, the round, "down", the winner, said aloud from recordings made with
Windows' own speech synthesiser; every other commentary line is mouthed. Before and after:
`DOCS/reports/2026-10-01-teeth-announcer.html`. **Nobody has listened to it yet.**

**Changed on 2026-10-02 at the owner's choice** (the paragraph above is how it was first built): the teeth are
now seen only for the two boxers' introductions and for the winner, lowered on the cable to ring centre and
hauled away again; between announcements they are not in the hall at all. The round call and "down" are
still heard, with nothing on screen, and commentary lines are no longer mouthed. Before and after:
`DOCS/reports/2026-10-02-teeth-announcer-intros.html`.

| What | Where |
|---|---|
| The announcer | `Runtime/Announcer/TeethAnnouncer.cs`; listens to `SimBus.Line` and `SimBus.PhaseChanged` |
| Its prefab, model, voice | `Assets/Announcer/` (`TeethAnnouncer.prefab`, `Teeth.glb`, `Voice/*.wav`, `make_voice.ps1`) |
| Putting it in the arena | `Editor/AnnouncerBuilder.cs`, on `PoBoxBuilder.BuildingArena`; menus under `PoBox/Announcer` |
| Scans to model | `training/tools/teeth_to_glb.py` (Blender) |

A new boxer needs its name recorded: add a line to `make_voice.ps1`, run it with Windows PowerShell
(`powershell.exe`, not `pwsh`), then `PoBox/Announcer/Rebuild Prefab`.

## The broadcast pass (2026-10-01, evening)

The owner picked twenty items from three top-ten lists: features 1, 2, 3, 4, 5, 7, 10; all ten graphics and
sound items; interface items 3, 4, 6. Screens before and after, annotated:
`DOCS/reports/2026-10-01-broadcast-pass.html`. The training two of the features needed:
`DOCS/reports/2026-10-01-training-defend.html`.

| Item | State | What it is, and where |
|---|---|---|
| F1 defence and knockdowns | done; **only Zombie defends** | A punch now has a consequence, in training and in the game alike: the **daze rule** (below). `training/envs/boxing.py --daze`, `Sim/Fighter.cs` |
| F2 get-up policy | built, **not trained** | `training/envs/getup.py`, `PolicyBrain.getUpModel`, `Fighter` rising state. The owner gave the GPU to another session's eight-hour run before this rung started. Until it is trained the referee stands a fighter up, as before. To train it: `training\tools\rungs.ps1 -Only getup` |
| F3 critic win-probability bar | done | every policy export now has a second output, the critic's value; `Sim/Excitement.cs` |
| F4 balance gauge | done | capture point against the feet; `Fighter.SampleBalance`, `UI/BalanceGauge.cs` |
| F5 hit map | done | impulse taken by zone; `Fighter.taken`, `UI/HitMap.cs` |
| F7 three judges | done | `Sim/Judges.cs`, on the result card |
| F10 Android | APK built (`Build/Android/PoBox.apk`, final policies), **never run on a phone**: none was attached | `Assets/Plugins/Android/arm64-v8a/libmujoco.so` (MuJoCo 3.3.7, from joanllobera/mujoco-bin), a second layout for it, `Editor/AndroidPrep.cs` |
| G1 mix | done, **not listened to** | `Assets/Audio/PoBox.mixer` (Hits, Crowd, Ring), crowd ducked under a heavy punch, `Audio/AudioMeter.cs` measures and limits the mix |
| G2 layered impacts | done | slap + body thud + crowd gasp, from Kenney's impact pack (CC0) and a synthesised gasp; footsteps on the canvas |
| G3 baked lighting, cookies, HDRI | done, with light probe groups, **not Adaptive Probe Volumes** | `Editor/StageBakery.cs`; bake is on the CPU, 17 to 30 s |
| G4 canvas decals | done | logo, wet spots, scuffs; `Fx/CanvasMarks.cs`; the URP decal feature, in screen space |
| G5 bruise and sweat on the meshes | done | a second skinned renderer over each mesh; marks where punches landed; `Fx/FighterSkin.cs` |
| G6 sweat and dust | done **with the Particle System, not VFX Graph** | a VFX Graph cannot be authored from a script; `Fx/ImpactVfx.cs` |
| G7 ropes that flex | done, **without the Splines package** | one procedural mesh; `Fx/RopeFlex.cs` |
| G8 crowd flashes | done | `Shaders/Crowd.shader`, `Fx/ArenaMood.cs` |
| G9 walk-on and winner shot | done | `Assets/Timeline/WalkOn.playable`, two corner cameras, a winner camera |
| G10 performance pass | done | the "30 kB of garbage a frame" was the editor's; the governor runs |
| UI 3 one scoreboard row | done | `Assets/UI/Hud.uxml`, `Theme.uss` |
| UI 4 three icon chips | done | |
| UI 6 commentary as a caption | done | |

**The daze rule.** Every landed punch adds its closing speed (m/s, capped at 9) to the daze of the fighter
who took it: in full to the head, three tenths to the body, nothing on the arms. The daze drains with a
time constant of 2.5 s. Above 14 the joint drives weaken (to 55% just below the line); at 42 the legs go:
in training the drives go slack for 0.7 s, which a fighter survives on its feet about three times in
four; in the game the fighter is down for a count, every time. The numbers travel with the policy: the importer reads them from the
training run's arguments into `Assets/Entrants/<name>/rules.json`. A policy trained without the rule gets
it in the game with the line at 54. The old rule (a single punch above a threshold staggers or floors)
is kept for the scripted stand-in only.

**A punch is measured as the trainer measures it.** `MujocoRing.TrackGlovesAndFeet` computes, once a
control step and from MuJoCo's own state, each glove's closing speed on the other fighter's head and body
(the faster of this step and the one before, capped at 9 m/s). A hit's impulse is 2.2 kg times that. It
used to be read off Unity's contact on the shadow bodies, which came out about a quarter lower (hardest
punch 14.6 N s then, 19.8 now, which is the cap). `Assets/Entrants/scoring.json` was recalibrated for it
(`damagePerNs` 0.018): in a full bout of the final policies Matt ended on 22 health and Zombie on 60, so a
bout between them goes to the judges.

**While the referee counts**, the fighter left standing is held in its guard where it stands, turned
towards the one on the canvas (`MujocoRing.Hold`, `Release`), and let go from that stance when the other
is up: the first moment of a training episode. It used to be shown a stand-in to box; the policies of
the defend rung fell over doing that (three downs each in one round, where C MuJoCo with the same policies had none).

**Who is in the arena.** `EntrantFactory.Available()` leaves out an entrant whose only policy is from its
bag stage once two match-trained entrants exist. It has to: the importer takes whatever
`training/checkpoints/bag_<name>/latest.onnx` it finds, and at 23:10 it took the other session's
half-trained `trump`, which then walked into the default pairing. `Assets/Entrants/trump` and
`Assets/League/Entrant_TRUMP.asset` are still there (this session was not permitted to delete them);
they are harmless and will be overwritten when that fighter has a match policy.

**Win probability** (`Excitement.RedShare`): the judges' cards as they stand, weighted more the less time
is left; health; daze; who is on the canvas; and the two critics. Each critic is read as a departure from
its own recent level in units of its own recent spread (the two are in different units and sit at
different levels), and the difference moves the bar by at most about a fifth. The debug panel's CRITIC is
that difference alone. Between Matt and Zombie the bar is at 90% or more for Zombie within twenty seconds
and stays there, which is what happened in the bout but makes a dull bar.

**Excitement** no longer sits at 100%. It used to jump for every landed punch, and trained fighters land
three a second between them. It now reads what is out of the ordinary for these two: clean impulse landed
in the last second and a half against the last half minute ("flurry"), a punch a quarter harder than they
usually land, either fighter past half way to losing its legs, balance, and a count.

**Judges.** POWER (impulse above the floor to the power 1.5, head 1.6), VOLUME (clean punches landed, and
a little for being busier), CRAFT (clean impulse, plus credit for punches stopped on the guard). Ten-point
must; a knockdown costs a point; a round within 4% is 10-10. A bout that goes the distance ends
UNANIMOUS, MAJORITY or SPLIT DECISION, or a draw. `Bout.Score` (damage plus 15 a knockdown) is no longer
the result.

**The walk-on** is a new bout phase (`BoutPhase.WalkOn`, five real seconds, round 1 only, nothing
simulated). The fighters stand at `Red Stool` and `Blue Stool`; `Bout` then stands them at their marks.
The Timeline has three tracks: cameras (two Cinemachine shots), house lights (`ArenaMood.houseLights`) and
follow-spots (rotation and intensity of `Sweep Red` and `Sweep Blue`). It is rebuilt by the scene builder;
edits made in the Timeline window are lost on the next build unless they are put in `StageBakery.WalkOn`.

**Sound, measured.** `AudioMeter` sits beside the listener, measures the finished mix and limits it to
-1 dBFS. One round at normal speed: average level -19 to -21 dBFS, loudest sample before the limiter
+1.1 dBFS, 39 samples over full scale, the limiter took off at most 2 dB. A whole bout with the final
policies, which hit harder: loudest +2.0 dBFS, 736 samples over, at most 3 dB taken off. The hits could
come down 2 dB (`AudioDirector.hitsGain`); nobody has listened yet, so they were left. Dev command `sound`.

**Performance, measured** (editor, PC tier, wide shot, lighting baked): 60 FPS, 60 set-pass calls, 312,000
triangles; before this pass 57 and 316,000. Unbaked, the six house lights are live and cast shadows:
120 set-pass calls and 625,000 to 1,080,000 triangles, which is what a scene built with
`pobox.bakeOnBuild` off looks like. Garbage: the profiler's call tree charges the game's own code with
0.11 kB a frame (all of it `HudView.Update`); the engine's "GC Allocated In Frame" counter reads 12 to
70 kB in the editor because it counts the editor's own windows and whatever is driving it. Dev commands
`gc-begin` / `gc-report`. The render-scale governor, which had never run, was run in the editor against a
240 FPS target (dev command `governor on`): four steps down, 1.0 to 0.6, as designed.

**Another session was at work the same evening** (five new boxers, an announcer). For it:
`PoBoxBuilder.BuildingArena` is raised while the arena is built so another editor script can add to it,
and `SimBus.Line` is every commentary line.

## State on 2026-09-30 (first build)

The project was an empty shell that morning. Everything below was built that day, from the owner's pick
out of three top-ten lists: features 1, 2, 3, 4, 6, 7, 9, 10, all ten graphics-and-sound items, all ten
UI-consolidation items. Screens before and after: `DOCS/reports/2026-09-30-first-build.html`.

**The fighters built that day were a stand-in.** Since 2026-10-01 the scene is built with the trained
entrants (next section) whenever there are any; the stand-in below is the fallback when there are none:

- The body is thirteen articulation links built from capsules and spheres (72 kg, human joint ranges and
  torque limits), not a skinned character. `FighterFactory.cs`.
- The brain is hand-written (`ScriptedBoxer.cs`): a guard, five punches, a stepping walk. It stays upright
  with outside help — a spring on the pelvis, a string on the chest — which a trained policy will not need.
  All of that help is in one method, `ScriptedBoxer.Assist`, so it can be deleted in one piece.
- The ladder's six entries (`Assets/League/Profile_*.asset`) are six sets of style numbers for that brain,
  labelled SCRIPTED on screen. Each has an empty `checkpoint` slot for an exported ONNX policy.
- The momentum bar is computed from damage and health. A trained critic's value estimate goes in its place.

What is real: every hit is a contact between a glove collider on a torque-limited arm and the other body,
and the number on screen is the impulse the solver reported, summed over the first 50 ms of the touch.
Joint stress is drive torque over the joint's limit. A knockdown switches the help off and lets the body fall.

## Choosing the boxers; no slow motion, replays or joint glow (2026-10-01)

Asked for by the owner that afternoon. Screens before and after: `DOCS/reports/2026-10-01-boxer-menu.html`.

- **The app starts in `Assets/Scenes/Menu.unity`** (`UI/MenuView.cs`, `Assets/UI/Menu.uxml`): a column for
  each corner with a card for every trained boxer, and FIGHT. The pick travels to the arena in
  `Sim/MatchSelection.cs`. BOXERS on the result card and CHANGE BOXERS in the menu go back to it. The cards
  are drawn in the editor too, without pressing Play (the menu runs in edit mode); the buttons only act in play.
- **The arena holds every boxer twice**, once dressed for each corner, all switched off, and a MuJoCo ring
  for each pair with a match model. `Sim/MatchSetup.cs` switches on the two that were picked before anything
  else in the scene wakes up. Opened on its own (the editor's Play button, a test) the arena fields its
  default pair, the first boxer in red against the second in blue.
- **The two corners always hold different boxers.** A boxer against a copy of itself was built and tried:
  two Zombies fight, but two Matts settle into a perfectly matched stand-off and neither throws a punch
  (0 thrown in 92 s, and again started 1.2 m apart). The policy is running and its output is the same
  for both, step after step; it looks as if Matt only answers an opponent who comes to him, which a copy
  of Matt never does.
- **Gone:** the replay system and its puppets, the highlight reel and scrubber on the result card, the
  slow motion after a heavy hit and its depth-of-field look, the 0.25x and 0.5x speeds, and the amber glow
  on a joint near its torque limit (with the shapes inside the limbs that carried it). Joint stress is
  still measured and shown as a number. Between rounds there is a three-second pause.
- **Portraits** on the cards are `Assets/UI/Portraits/<name>.png`. To take them: play the arena, run the dev
  command `portraits` a few times during the fight, keep the best, then `PoBox/Build Menu Scene`.

## The sixty-minute follow-up of 2026-10-01, and what is in the game

Report with the TensorBoard charts: `DOCS/reports/2026-10-01-training-followup.html`.

- **In the game:** still the eight-hour run's final policies (iteration 10,193). Checked again at 18:21 in the
  editor with 300 s of training's own episodes (0 falls in 25; Matt 1.25 and Zombie 1.48 hits a second,
  against 1.54 each in MuJoCo), and the scorekeeper's numbers re-measured from that.
- **The follow-up** tried two reward changes from those policies. Pricing a hit taken as high as a hit landed
  made both fighters stop punching (`match2`, stopped at 31 minutes and marked rejected). Removing what a
  knockdown cost the fighter who landed it changed nothing (`match3`, 29 minutes, not imported). See
  `training/README.md`.
- **Importing:** `PoBox/Import Trained Entrants` (and so `PoBox/Dev/Import Policies And Calibrate Scoring`)
  takes the newest policy from any run under `training/checkpoints`, skipping a run whose folder holds
  `REJECTED.txt`. As of that evening the newest is `match3`, which is as good as what is in the game.

## Trained entrants: Matt and Zombie (2026-10-01)

The owner's two meshes in the project root, `test_MATT_Avaturn.glb` and `test_ZOMBIE_RiggedAccurig.fbx`,
are the fighters. Each has a physics body generated from its own skeleton (Matt 1.81 m and 79.5 kg, Zombie
80.2 kg with a pelvis 9 cm higher), a policy trained in MuJoCo Warp, and its own mesh skinned onto the body
in Unity. The fight itself is stepped by MuJoCo (`Rl/MujocoRing.cs`, `Assets/Plugins/x86_64/mujoco.dll`):
the bodies in the scene are its shadows. The stand-in described above is still built when
`Assets/Entrants` holds no trained fighter, and the app then starts in the arena with no menu.

**The eight-hour run of 2026-10-01** (09:48 to 17:39; report with the TensorBoard charts explained:
`DOCS/reports/2026-10-01-training-matt-v-zombie.html`). Each fighter 45 minutes on the bag, then 381
minutes in the ring together, a billion fighter-steps. It ended with no falls and each landing about 1.5
punches a second; Matt's glove arrives at 6.2 m/s, Zombie's at 8.8, so Zombie is the better of the two.
First bout in the game with the final policies: Zombie on points, 133 to 26, no knockdowns.

**Why the fight runs on MuJoCo and not on Unity's physics.** It was built on Unity's physics first, and
measured: bodies agreeing with MuJoCo's at rest to the millimetre, the policies' hundred inputs agreeing to
three decimal places, and the same two policies falling in 69%, 79% and 89% of twelve-second episodes
against 0% in MuJoCo. The engines part company over a swinging leg by a few centimetres inside a third of
a second, and a policy trained in one engine only has no margin for that. `MujocoRing` loads the library
the trainer itself uses (3.14.0, checked at start-up), steps the match model in it, and after every step
puts the scene's bodies, gravity and drives off, where the real ones are. The policy reads MuJoCo's state.
Fall rate in the game: 0 in 25 episodes. **The library in the project is the Windows one**: on any other
platform `MujocoRing` reports that it could not load and the fighters fall back to Unity's physics.

How a fighter gets from training into the ring:

1. `training/run_match.py --hours 8 --a matt --b zombie` (removed 2026-10-16 with the v1 pipeline) trains each on a heavy bag (45 min), then both in
   one ring, one policy each. See `training/README.md`.
2. `training/tools/export_reference.py --name matt` (and `zombie`) writes each body's mass properties as
   MuJoCo has them, and a recording of the policy to check Unity against.
3. `training/tools/export_mujoco_layout.py --a matt --b zombie` writes the match model with the ropes in it
   and the layout file `MujocoRing` needs (which numbers are which, and where in MuJoCo's data the body
   positions sit for this build of the library).
4. Menu `PoBox/Dev/Import Policies And Calibrate Scoring` (stop Play first): imports everything, spars the
   two for five minutes on the clock with no damage and no count, sets the scorekeeper's four numbers from
   how hard they turned out to hit (`Assets/Entrants/scoring.json`), and rebuilds the scenes. Or, without
   recalibrating: `PoBox/Import Trained Entrants` then `PoBox/Build Everything`.

In Unity the body is rebuilt from the same MuJoCo file (`Rl/MjcfFighterImporter.cs`), the policy runs in the
Inference Engine at 50 Hz with no balance help at all (`Rl/PolicyBrain.cs`), and the mesh rides on the body
(`Rl/SkinBinder.cs`). There is no get-up policy: a fighter that survives the count is stood back in its
guard where it fell.

**Checking that Unity's body is MuJoCo's body** is `Assets/Scripts/Editor/TransferProbe.cs`, run headless:

```
Unity.exe -batchmode -nographics -projectPath . -executeMethod PoBox.EditorTools.TransferProbe.Run -probeMode replay
```

`hold` and `replay` lay Unity beside the MuJoCo recording step by step; `pair` does the same for the two
in the ring and checks every block of both observations; `spar` runs training's own twelve-second episodes
and prints the fall rate the trainer prints; `static` hangs each fighter in the air to read joint stiffness
off the sag; `match` plays a bout and reports every hit and knockdown; `shots` (without `-nographics`)
writes stills to `Logs/shots`. `training/tools/eval_cmujoco.py` gives MuJoCo's fall rate for the same
policies in half a minute. Measured on 2026-10-01 for Matt (on Unity's physics, before the move to MuJoCo):
gloves at rest in the same place to the millimetre; with the guard held, pelvis height within 3 mm and
every joint within 0.045 rad over the first second; Unity's copy of the policy returns the trainer's
actions to four decimal places; replaying MuJoCo's actions reproduces the first punch (glove reach 0.56 m
in both at 0.4 s) and the two drift apart after about half a second, which an open-loop replay of a
balancing body always does. What is not the same: MuJoCo's joints carry rotor inertia ("armature") and
Unity's articulation has no such setting, so the ankles answer faster in Unity.

## What was built

| Area | Where | Notes |
|---|---|---|
| Bout rules: walk-on, rounds, count, KO, decision, next pairing | `Sim/Bout.cs`, `Sim/Judges.cs` | 3 rounds of 45 s. Also owns time: the 1x and 2x speeds |
| Choosing the boxers | `UI/MenuView.cs`, `Sim/MatchSelection.cs`, `Sim/MatchSetup.cs` | the first screen; the arena switches on the pair that was picked |
| Hit detection, damage, daze, knockdown, getting up | `Sim/Fighter.cs`, `GloveSensor.cs` | only a thrown punch scores, once; zone weights head 1.6, body 1.0, arms 0.25; the daze rule for trained fighters |
| Excitement and win-probability readings | `Sim/Excitement.cs` | cameras, crowd and sound all read the same excitement number |
| Camera director (feature 1) | `Broadcast/BroadcastDirector.cs` | eleven Cinemachine cameras in the scene; hard cuts; frames to the part of the screen the HUD leaves clear; starts the walk-on Timeline |
| Commentary ticker (10) | `Broadcast/Commentary.cs` | only quotes numbers the telemetry holds |
| Scoreboard, chips, pods, caption, result card | `UI/HudView.cs`, `UI/HitMap.cs`, `UI/BalanceGauge.cs`, `Assets/UI/Hud.uxml` | |
| Bruise, sweat and corner-colour rim | `Fx/FighterSkin.cs`, `Shaders/FighterOverlay.shader` | hand-written URP shader, not Shader Graph; on the trained fighters' own meshes since the broadcast pass |
| Impact effects, camera shake (7) | `Fx/ImpactVfx.cs`, Cinemachine Impulse | built-in Particle System, not VFX Graph |
| Ropes, canvas marks | `Fx/RopeFlex.cs`, `Fx/CanvasMarks.cs` | one mesh for sixteen ropes; a pool of URP decal projectors |
| Lighting, cookies, decals, HDRI, mixer, walk-on Timeline | `Editor/StageBakery.cs` | everything the scene builder needs that is staging rather than set |
| Ladder with Elo (9) | `League/LeagueTable.cs` | saved on the device; RESET LADDER is in SETTINGS |
| Crowd | `Shaders/Crowd.shader`, `Fx/ArenaMood.cs` | 1,138 figures, one draw call, bounces with the excitement reading; flashes, phone lights, corner colours |
| Sound | `Audio/AudioDirector.cs`, `Audio/AudioMeter.cs`, `Assets/Audio/PoBox.mixer` | layered 3D one-shots at the contact point; crowd bed follows excitement and is ducked under a heavy punch; the mix is measured and limited. **Not listened to yet** |
| Look | `AssetBakery.Look` | ACES, bloom, vignette |
| Android | `Editor/AndroidPrep.cs` | menus `PoBox/Android/Prepare` and `Build APK` |
| Performance readings | `Diag/PerfTelemetry.cs`, `Sim/PhysicsStepper.cs` | physics is timed with a stopwatch round the step |
| One-screen audit | `UI/LayoutAudit.cs`, `Tests/OneScreenTests.cs` | off-screen, scroll, text under 26 px, clipped text, the five anchors |
| Trained fighter: body, brain, skin | `Rl/MjcfFighterImporter.cs`, `MjcfRig.cs`, `PolicyBrain.cs`, `SkinBinder.cs`; `Editor/EntrantFactory.cs` | body from the MuJoCo file; 100-number observation identical to `training/envs/boxing.py`; punches recognised from glove speed |
| Transfer check | `Editor/TransferProbe.cs`, `training/tools/export_reference.py` | headless; hold, replay, match, shots |

## Measured

- Layout audit, every HUD state (fight, three menu tabs, debug panel, results card) at 720x1280, 1080x1920
  and 1080x2400: off-screen 0, scroll 0, small text 0, clipped 0, anchors in place.
- Play-mode tests: 10 of 10 pass on 2026-10-01, evening (the HUD and the first screen at three phone shapes
  each, both fighters still standing after the walk-on and the intro, the arena fielding the pair that was
  picked, and two for the judges: three judges can disagree, a knockdown costs a point).
- Editor, PC tier, RTX 2060: 60 FPS (the cap), physics about 0.9 ms a frame.
- Five bouts watched through: about one punch thrown a second, roughly 40 to 50% landing, 7 to 27% of
  those clean (the rest on the arms); peak impulses 20 to 43 N·s; one knockout, four points decisions.

## Not done, or not checked

- No get-up policy yet: the stage is written and smoke-tested but not trained (see the broadcast pass
  above), so a trained fighter is still stood back up by the referee.
- An Android APK builds (`PoBox/Android/Build APK`, `Build/Android/PoBox.apk`, arm64, MuJoCo 3.3.7) and has **never been run on a phone**: none was attached. What is known is that the policies stand and box in C MuJoCo 3.3.7 on the PC. Its frame rate, its sound and whether the library loads are not known.
- There is no MuJoCo library in the project for iOS, macOS or Linux.
- The scorekeeper's numbers are measured from the policies, not tuned by watching bouts. One bout has
  been watched to the end with the final ones.
- While the referee counts there is no neutral corner. The fighter left standing shadow-boxes a stand-in
  of its opponent on the spot (see below for why).
- Nothing has been run on a phone. The Mobile quality tier exists but has not been looked at.
- The sound has not been listened to by a person.
- Adaptive Probe Volumes are not used: the baked light reaches the fighters through a light probe group.
- The sound of the broadcast pass (layers, ducking, gasp, footsteps) has been measured, not heard.
- A rope bowing under a fighter has not been seen: in the bouts watched nobody leaned on one. The drawing
  of it is exercised only by its own arithmetic.
- Unity Recorder is not installed. The render-scale governor in `PerfTelemetry` acts on frame time; it is
  off in the editor unless `governInEditor` is set, and has been run that way once.
- The winner's shot is of a body frozen where the bell found it: nothing is simulated once a bout is over.
- The walk is weak: fighters reach about 0.2 m/s against the 0.3 to 1.0 they ask for.
- Training checkpoints and logs are not in git (`training/checkpoints`, `training/logs`); only the policies
  copied to `Assets/Entrants` are. The project before the 2026-09-30 rebuild is under the tag `nick-era`.

## Running it

Open `Assets/Scenes/Menu.unity` and press Play: choose the two boxers, FIGHT. Or open
`Assets/Scenes/Arena.unity` and press Play to go straight to the default pair. Bouts run themselves; the
same two box again until BOXERS is pressed.

- Rebuild both scenes and re-bake the generated assets: menu `PoBox/Build Everything` (it leaves the menu
  scene open). The scenes are generated by `Assets/Scripts/Editor/PoBoxBuilder.cs`; objects can be moved by
  hand afterwards, but a rebuild replaces them, so a change that should last belongs in the builder.
  It also bakes the arena's lighting (17 to 30 s on the CPU; `PoBox/Bake Arena Lighting` does only that).
  Never judge the frame rate of an unbaked arena: its six house lights are then live, with shadows.
- Portrait Game view: menu `PoBox/Dev/Game View 1080x1920 (9:16)`.
- Layout audit of the live HUD: menu `PoBox/Dev/Check Layout` (in play mode).

### Driving the editor from outside

The Unity CLI (`unity command ...`, the client for `com.unity.pipeline`) works while the editor is
unfocused. Run it from the project folder.

```
unity command recompile            then poll   unity command recompile_status
unity command menu --path "PoBox/Build Everything"
unity command run_tests --mode PlayMode --async_tests true    then   unity command test_status
```

For anything in play mode, write commands one per line to `Temp/pobox-dev.txt` and call
`unity command menu --path "PoBox/Dev/Run Script"`; output is appended to `Temp/pobox-dev-out.txt`.
The commands are listed at the top of `Assets/Scripts/Editor/DevTools.cs`:

```
play | play-menu | stop | state | timescale 2 | skip   (skip: end the walk-on now)
down 0                      (the red fighter's legs go, now; 1 for blue)
sound | sound-reset         (the finished mix: peak, average, samples over full scale, limiter)
gc-begin   (play a few seconds)   gc-report     (what allocated garbage, by method)
governor on | off           (run the render-scale governor in the editor against 240 FPS)
portraits                   (in play on the arena: writes Assets/UI/Portraits/<name>.png)
call MenuView Pick 0 1      (corner 0 red or 1 blue, then the boxer's place in the list)
call MenuView Fight
shot-begin 1080 1920        (wait a moment)        shot-end name      -> Build/Shots/name.png
layout
call HudView OpenMenu 1
call BroadcastDirector Pin Impact
```

Headless (`Unity.exe -batchmode -executeMethod PoBox.EditorTools.PoBoxBuilder.BuildFromCommandLine`) only
works with the editor closed; it refuses to start while any editor has the project open.

## Things that cost time, worth knowing

- **Joint drive targets use the same sign as a Unity Euler angle** about the link's own axis (measured with
  `probe` / `probe-read`). Flexing an arm forward is a negative X.
- **A sheet over other interface must be fully opaque.** The panel blends in linear space; 4% of white text
  through a 96% dark card is a readable grey.
- **The engine's physics profiler markers read zero outside the profiler**, and so do the draw-call and
  batch counters. Physics is timed by hand; the debug panel shows set-pass calls and triangles.
- **In portrait, a lens angle is the wrong number to think in.** Cameras solve their distance from the
  horizontal angle the screen has and from the band of screen the HUD leaves clear.
- **Outside cameras have to be above the top rope's sight line**, which depends on how close the fighters
  are to the ropes on the camera's side. `BroadcastDirector.OverTheRopes`.
- Never write two hyphens in a row inside a UXML comment: the importer produces an empty document, silently.
- **The first frame after a scene loads takes seconds, and the wall clock counts them.** A five-second
  walk-on timed with `Time.unscaledTime` was over before the first picture. `Bout.PhaseAge` counts frames,
  none worth more than a tenth of a second, and the Timeline is evaluated from it by hand.
- **A body told where to stand is drawn there only after a physics step.** With nothing simulated (the
  walk-on) the fighters were shown where the last bout left them. `Bout.Place` takes one step of no length.
- **A light marked Baked is a live light until the scene is baked**, shadows and all.
- **Switching the profiler driver off zeroes the engine's render counters** (set-pass calls, triangles).
  `gc-report` puts it back as it found it.
- **A decal box as deep as it is wide marks the boots as well as the canvas.** The projectors are 6 cm deep
  and sit on the cloth. Decals are drawn in screen space: the other way needs every fighter drawn twice.
- **An AudioMixer can be made from a script only through the editor's internal classes** (`StageBakery.Mixer`),
  and a mixer that has never been opened in its window has no view to add a group to.
- **"Baked" is a statement about one build of the scene.** Rebuilding the scene makes new objects and the
  old lighting data no longer fits them; the builder bakes again every time.
- **One component class per file, named after the file.** A second MonoBehaviour in the same file can be
  added in the editor but is not saved with the scene. The list of body-part pairs that must not collide
  lived in such a class; without it the fighter's own overlapping shapes (thigh in pelvis, upper arm in
  chest) threw its shoulders to their stops in a tenth of a second and it folded. `TransferProbe` prints
  every overlapping pair and whether it is ignored.
- **A policy that has only hit the bag must be fed zeros for the opponent's gloves and facing.** Those inputs
  were always zero in training, so their scaling is meaningless and any other value is noise at full
  volume. `PolicyBrain.bagStage`.
- **Unity spreads a link's mass over its colliders by volume; MuJoCo was told each shape's mass.** The
  importer takes mass, centre of mass and inertia from `inertia.json`, exported from MuJoCo itself.
- **Do not press Play while a recompile is pending.** `Build Everything` can queue one; it lands twenty
  seconds into play, empties every static and both policies, and looks like a physics fault. The build now
  sets Script Changes While Playing to Recompile After Finished Playing.
- **Meshes arrive standing whichever way their exporter left them.** The zombie, by way of FBX and Blender,
  arrives lying on its back. `SkinBinder` reads up and left off the skeleton and turns the mesh to match.
- **A bone nothing drives stays where the fighter was built.** Character Creator skeletons hang pelvis and
  spine under a hip bone; the mesh's root now rides on the pelvis so that bone comes along.
- **A policy can only box what is in front of it, within a couple of metres.** Shown its neutral corner
  during a count it spun round and fell. Led there by a stand-in opponent it arrived, and then fell turning
  back to the real one behind it. Given a stand-in that stood still it walked through it, having learned
  to press forward until a body stops it. What works: the stand-in appears where the real opponent stood,
  backs away when crowded, and the real one is stood up on the same line a step further out. `PolicyBrain.Phantom`.
- **Stand a fighter up out of arm's reach (1.3 m).** Nearer, its guard appears with the other's glove
  already inside it and the contact that follows knocks the other one down.
- **A fighter down for a count is moved to a collision layer only the ring is on**, or the one left
  standing trips over it. `MujocoRing.SetGhost`.
- **What Unity's solver says it took to stop a shadow's glove is not a measurement.** A punch's strength
  for a MuJoCo-simulated fighter is the glove's closing speed times 2.2 kg, as training scores it.
- **With the TGS solver an articulation drive is stiffer than it says**, by (position iterations +
  velocity iterations) / position iterations: 1.33 times at 12 and 4. With PGS it is exact. Measured with
  the probe's `static` mode. The project is on PGS.
- **Unity gives articulation joints friction of 0.05 by default**, and a glove moving 4 cm a step is first
  seen already inside what it hit. The importer sets friction to 0, speculative contacts on the forearms
  and a 1 m/s limit on the speed two overlapping shapes are pushed apart at.

## Folder map

```
Assets/Scenes/Menu.unity        the first screen: choose the boxers (generated)
Assets/Scenes/Arena.unity       the ring (generated)
Assets/Scripts/Runtime/Sim      fighter, brain, bout rules, excitement, physics stepper
Assets/Scripts/Runtime/Broadcast  camera director, commentary
Assets/Scripts/Runtime/Fx       fighter skin, impact effects, crowd and lamps, ropes, canvas marks
Assets/Scripts/Runtime/Audio    sound, and the meter and limiter on the listener
Assets/Scripts/Runtime/UI       HUD, graph, icons, hit map, balance gauge, layout audit
Assets/Scripts/Runtime/League   profiles and the ladder
Assets/Scripts/Runtime/Diag     performance readings
Assets/Scripts/Runtime/Rl       trained fighters: body from MuJoCo, policy runner, skin binder
Assets/Entrants/<name>          one trained fighter: model.xml, config.json, rig.json, inertia.json, policy.onnx
Assets/Models                   the owner's meshes: Matt.glb, Zombie.glb
training/                       the MuJoCo Warp trainer (see its README)
Assets/Scripts/Editor           scene builder, asset bakery, stage bakery, fighter factory, Android, dev tools
Assets/Timeline                 the walk-on (generated)
Assets/Plugins                  mujoco.dll (Windows), Android/arm64-v8a/libmujoco.so
Assets/Scripts/Tests            play-mode tests
Assets/UI                       Hud.uxml, Menu.uxml, Theme.uss, panel settings, Kenney icons (CC0), Portraits/
Assets/MuJoCo                   the match model each pair of boxers is simulated in
Assets/Shaders                  FighterOverlay, Crowd
Assets/Audio/Real               CC0 recordings from PoDecath (licences beside them)
Assets/Audio/Kenney             CC0 punch, soft-impact and footstep recordings (Kenney's Impact Sounds)
Assets/Audio/PoBox.mixer        Master > Hits, Crowd, Ring
Assets/Textures/PolyHaven       the hall's ambient light: a CC0 HDRI of a boxing gym
Assets/Textures/AmbientCG       the canvas's weave: a CC0 fabric normal map
Assets/Audio/Synth              generated punch sounds
Assets/Settings                 URP assets (from PoDecath), volume profiles
Assets/League                   the six profiles
DOCS/                           this file, and reports/
```
