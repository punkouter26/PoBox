# PoBox — project summary

Two physics-driven fighters box in a ring while the game films it like a broadcast: cameras that cut on
the action, slow motion and replays, a live scoreboard, a ladder, a crowd and a commentary line. Portrait,
one screen, nothing scrolls. Unity 6000.6.0f1, URP 17.6, Cinemachine 6.6, UI Toolkit.

Read `AGENTS.md` for the owner's house rules. The sibling project `../PoDecath` is the older, larger
relative: same engine version, same house style, and the source of the render settings, icons and sound
recordings copied in here.

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

## Trained entrants: Matt and Zombie (2026-10-01)

The owner's two meshes in the project root, `test_MATT_Avaturn.glb` and `test_ZOMBIE_RiggedAccurig.fbx`,
are the fighters. Each has a physics body generated from its own skeleton (Matt 1.81 m and 79.5 kg, Zombie
80.2 kg with a pelvis 9 cm higher), a policy trained in MuJoCo Warp, and its own mesh skinned onto the body
in Unity. The stand-in described above is still built when `Assets/Entrants` holds no trained fighter.

How a fighter gets from training into the ring:

1. `training/run_match.py --hours 8 --a matt --b zombie` trains each on a heavy bag (45 min), then both in
   one ring, one policy each. See `training/README.md`.
2. `training/tools/export_reference.py --name matt` (and `zombie`) writes each body's mass properties as
   MuJoCo has them, and a recording of the policy to check Unity against.
3. Menu `PoBox/Import Trained Entrants` copies model, config, rig, inertia and the newest policy into
   `Assets/Entrants/<name>/`. Then `PoBox/Build Everything`.

In Unity the body is rebuilt from the same MuJoCo file (`Rl/MjcfFighterImporter.cs`), the policy runs in the
Inference Engine at 50 Hz with no balance help at all (`Rl/PolicyBrain.cs`), and the mesh rides on the body
(`Rl/SkinBinder.cs`). There is no get-up policy: a fighter that survives the count is stood back in its
guard where it fell.

**Checking that Unity's body is MuJoCo's body** is `Assets/Scripts/Editor/TransferProbe.cs`, run headless:

```
Unity.exe -batchmode -nographics -projectPath . -executeMethod PoBox.EditorTools.TransferProbe.Run -probeMode replay
```

`hold` and `replay` lay Unity beside the MuJoCo recording step by step; `match` plays a bout and reports
every hit; `shots` (without `-nographics`) writes stills to `Logs/shots`. Measured on 2026-10-01 for Matt:
gloves at rest in the same place to the millimetre; with the guard held, pelvis height within 3 mm and
every joint within 0.045 rad over the first second; Unity's copy of the policy returns the trainer's
actions to four decimal places; replaying MuJoCo's actions reproduces the first punch (glove reach 0.56 m
in both at 0.4 s) and the two drift apart after about half a second, which an open-loop replay of a
balancing body always does. What is not the same: MuJoCo's joints carry rotor inertia ("armature") and
Unity's articulation has no such setting, so the ankles answer faster in Unity.

## What was built

| Area | Where | Notes |
|---|---|---|
| Bout rules: rounds, count, KO, points, next pairing | `Sim/Bout.cs` | 3 rounds of 45 s. Also owns time: slow motion and the freeze during a replay |
| Hit detection and damage | `Sim/Fighter.cs`, `GloveSensor.cs` | only a thrown punch scores, once; zone weights head 1.6, body 1.0, arms 0.25 |
| Excitement and momentum readings | `Sim/Excitement.cs` | cameras, crowd, sound and highlights all read the same excitement number |
| Camera director (feature 1) | `Broadcast/BroadcastDirector.cs` | nine Cinemachine cameras in the scene; hard cuts; frames to the part of the screen the HUD leaves clear |
| Replay and highlight reel (2, 10) | `Broadcast/ReplaySystem.cs` | records poses, not video; plays them back on two puppets; scrubbable |
| Commentary ticker (10) | `Broadcast/Commentary.cs` | only quotes numbers the telemetry holds |
| Tale of the tape, momentum bar (3, 6) | `UI/HudView.cs`, `Assets/UI/Hud.uxml` | |
| Joint-stress heat map (4) | `Fx/FighterSkin.cs`, `Shaders/FighterOverlay.shader` | hand-written URP shader, not Shader Graph |
| Impact effects, camera shake (7) | `Fx/ImpactVfx.cs`, Cinemachine Impulse | built-in Particle System, not VFX Graph |
| Ladder with Elo (9) | `League/LeagueTable.cs` | saved on the device; RESET LADDER is in SETTINGS |
| Crowd | `Shaders/Crowd.shader`, `Fx/ArenaMood.cs` | 1,138 figures, one draw call, bounces with the excitement reading |
| Sound | `Audio/AudioDirector.cs` | 3D one-shots at the contact point; crowd bed follows excitement; low-pass in slow motion. **Not listened to yet** |
| Look | `AssetBakery.Look`, `ReplayLook` | ACES, bloom, vignette; depth of field and motion blur only in slow motion and replays |
| Performance readings | `Diag/PerfTelemetry.cs`, `Sim/PhysicsStepper.cs` | physics is timed with a stopwatch round the step |
| One-screen audit | `UI/LayoutAudit.cs`, `Tests/OneScreenTests.cs` | off-screen, scroll, text under 26 px, clipped text, the five anchors |
| Trained fighter: body, brain, skin | `Rl/MjcfFighterImporter.cs`, `MjcfRig.cs`, `PolicyBrain.cs`, `SkinBinder.cs`; `Editor/EntrantFactory.cs` | body from the MuJoCo file; 100-number observation identical to `training/envs/boxing.py`; punches recognised from glove speed |
| Transfer check | `Editor/TransferProbe.cs`, `training/tools/export_reference.py` | headless; hold, replay, match, shots |

## Measured

- Layout audit, every HUD state (fight, three menu tabs, debug panel, results card) at 720x1280, 1080x1920
  and 1080x2400: off-screen 0, scroll 0, small text 0, clipped 0, anchors in place.
- Play-mode tests: 4 of 4 pass (three phone shapes, plus both fighters still standing after the intro).
- Editor, PC tier, RTX 2060: 60 FPS (the cap), physics about 0.9 ms a frame.
- Five bouts watched through: about one punch thrown a second, roughly 40 to 50% landing, 7 to 27% of
  those clean (the rest on the arms); peak impulses 20 to 43 N·s; one knockout, four points decisions.

## Not done, or not checked

- No get-up policy: a trained fighter is stood back up by the referee, so to speak.
- The scorekeeper's numbers for trained fighters (`EntrantFactory.Build`: damage per newton-second, stagger
  and knockdown thresholds) were set from a handful of headless bouts, not tuned by watching.
- While the referee counts, the standing fighter is sent to a neutral corner by moving its target there.
  A policy that has only ever walked a step or two may fall over on the way.
- Nothing has been run on a phone. The Mobile quality tier exists but has not been looked at.
- The sound has not been listened to by a person.
- Lighting is real-time only: no baked lighting, no light cookies.
- The canvas logo is painted into a generated texture, not a URP decal. No ambientCG or Poly Haven assets.
- Unity Recorder and Adaptive Performance are not installed. The render-scale governor in
  `PerfTelemetry` acts on frame time and is switched off in the editor, so it has never run.
- The walk is weak: fighters reach about 0.2 m/s against the 0.3 to 1.0 they ask for.
- About 30 kB of garbage a frame in the editor; not investigated.
- Training checkpoints and logs are not in git (`training/checkpoints`, `training/logs`); only the policies
  copied to `Assets/Entrants` are. The project before the 2026-09-30 rebuild is under the tag `nick-era`.

## Running it

Open `Assets/Scenes/Arena.unity` and press Play. Bouts run themselves, one after another.

- Rebuild the scene and re-bake the generated assets: menu `PoBox/Build Everything`. The scene is generated
  by `Assets/Scripts/Editor/PoBoxBuilder.cs`; objects can be moved by hand afterwards, but a rebuild
  replaces them, so a change that should last belongs in the builder.
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
play | stop | state | timescale 3
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

## Folder map

```
Assets/Scenes/Arena.unity       the one scene (generated)
Assets/Scripts/Runtime/Sim      fighter, brain, bout rules, excitement, physics stepper
Assets/Scripts/Runtime/Broadcast  camera director, replay, commentary
Assets/Scripts/Runtime/Fx       heat map, impact effects, crowd and lamps
Assets/Scripts/Runtime/Audio    sound
Assets/Scripts/Runtime/UI       HUD, graph, icons, scrubber, layout audit
Assets/Scripts/Runtime/League   profiles and the ladder
Assets/Scripts/Runtime/Diag     performance readings
Assets/Scripts/Runtime/Rl       trained fighters: body from MuJoCo, policy runner, skin binder
Assets/Entrants/<name>          one trained fighter: model.xml, config.json, rig.json, inertia.json, policy.onnx
Assets/Models                   the owner's meshes: Matt.glb, Zombie.glb
training/                       the MuJoCo Warp trainer (see its README)
Assets/Scripts/Editor           scene builder, asset bakery, fighter factory, dev tools
Assets/Scripts/Tests            play-mode tests
Assets/UI                       Hud.uxml, Theme.uss, panel settings, Kenney icons (CC0)
Assets/Shaders                  FighterOverlay, Crowd
Assets/Audio/Real               CC0 recordings from PoDecath (licences beside them)
Assets/Audio/Synth              generated punch sounds
Assets/Settings                 URP assets (from PoDecath), volume profiles
Assets/League                   the six profiles
DOCS/                           this file, and reports/
```
