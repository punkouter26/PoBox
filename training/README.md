# PoBox training

Teaches a fighter to box in MuJoCo (MuJoCo Warp, on the GPU) and exports the result as ONNX for Unity.
Built 2026-10-01 on the pattern of `../../PoDecath/training`; `ppo.py` is PoDecath's, unchanged.

Python is PoDecath's virtual environment, used in place (torch 2.5 + CUDA, mujoco 3.14, mujoco-warp, warp 1.17):

    $py = "..\..\PoDecath\training\.venv\Scripts\python.exe"

## From a rigged mesh to a trained policy

1. **Read the rig out of the owner's mesh** (house rule: the skeleton comes from the skinned model).

       & $py glb_to_rig.py --glb ..\Assets\Models\Fighter.glb --out rigs\fighter.json --mass 75

   Works on a `.glb`. It finds the limbs by bone name (Mixamo, Avaturn and similar), and by the shape of
   the skeleton when the names say nothing (`bone_0`, `bone_1`, ...). It prints which bone it took for
   which limb: read that list. Arms are straightened to a T-pose and the size fixed if the file is not in
   metres; both are reported.

2. **Generate the MuJoCo models.**

       & $py rig_to_mjcf.py --rig rigs\fighter.json

   Writes `models/fighter_bag.xml` (one fighter and a heavy bag), `models/fighter_spar.xml` (two fighters in
   a ring) and `models/fighter_policy_config.json`. It prints the fighter's mass and where its weight sits
   over its feet in the guard; that should be near the middle of the foot.

       & $py tools\render_pose.py --xml models\fighter_spar.xml --out logs\pose.png

3. **Train.** One command for the whole night:

       & $py run_night.py --hours 8

   30% of the time on the bag (stand unaided, hold range, hit hard), then sparring against itself from that
   policy. TensorBoard starts with it: http://localhost:6006. Progress in one line:

       .\tools\status.ps1 bag          .\tools\status.ps1 spar -Terms

4. **Watch it.** MuJoCo's own viewer, following the newest checkpoint of a run as training saves them:

       & $py view_box.py --run spar

   A strip of stills instead: `& $py view_box.py --run spar --sheet logs\spar.png`. `run_night.py` writes one
   every half hour to `logs/sheets/`.

Each save leaves `checkpoints/<run>/latest.pt`, `latest.onnx` and `latest_policy_config.json`.

## What is being taught

Both stages share one observation (100 numbers) and one action (21 joint targets at 50 Hz), so the policy
that learned on the bag is the one that starts sparring. The full list is at the top of `envs/boxing.py`.

A **hit** is a glove arriving at the target's head or body faster than 1 m/s straight into the surface,
having come back at least 30 cm, and a quarter of a second, since its last hit. Its strength is that
closing speed, capped at 9 m/s. It is measured from the positions of the bodies, not from the solver's
contact list, because reading that back would stall the GPU every step.

Reward, per fighter per step: staying up and upright, facing the target, holding punching range, a small
payment for a glove closing on the target, a large one for a hit (head counts 1.5, body 1; paid in full
only 0.6 s after the fighter's last), a charge for being hit, and charges for wasted power, jerky actions,
joints faster than a person's, sliding feet, leaning on the target, and a fall. In sparring, putting the
other fighter down within a second and a half of hitting them pays a bonus.

## Rehearsals, 2026-10-01 (on a stand-in rig, PoDecath's Matt; not the owner's mesh)

Four short runs to find out what the rewards actually teach before a night is spent on them. Each rule
above that looks fussy is there because a rehearsal found the cheaper thing it prevents.

| Run | Length | What the fighter learned | What was changed |
|---|---|---|---|
| 1 | 12 min, bag | stood within 5 min; then rubbed a glove up and down the bag: 17.7 "hits"/s at 11 m/s, 3.7 kW | only speed into the surface counts; one hit per landing; power and joint-speed charges raised |
| 2 | 10 min, bag | stood; 5 pats a second at 8 m/s, each glove coming back 15 cm | 30 cm wind-up to score again; quick repeats paid by the square of the shortfall |
| 3 | 7 min bag + 16 min spar | rested both gloves on the bag; sparring, the two propped on each other, feet far back | charge for a head more than 18 cm out from between the feet |
| 4 | 21 min bag + 49 min spar | bag: upright, arm fully out and back to the guard, 1.9 punches/s at 8.6 m/s. Spar: both upright at range, 1.6 punches/s each at 8.7 m/s, fall rate 0.00 | none |

Sheets from run 4: `logs/rehearsal2_bag.png`, `logs/rehearsal2_spar.png`.

Known after run 4, not yet addressed: every punch goes to the head (the head pays more and nothing yet
makes the body the better target); nobody defends, both simply trade; and there were no knockdowns.

Throughput on the RTX 2060 with the Unity editor open: about 70,000 steps/s on the bag (4,096 worlds) and
59,000 fighter-steps/s sparring (2,048 worlds of two).

## Into Unity

After training, from this folder (CPU only, safe beside a running job):

```
python tools/export_reference.py --name matt
python tools/export_reference.py --name zombie
```

That writes `models/<name>_inertia.json` (each link's mass, centre of mass and inertia as MuJoCo has them)
and `logs/reference_<name>.json` (the fighter held in its guard, and driven by its bag policy, one row per
control step). In Unity, `PoBox/Import Trained Entrants` then `PoBox/Build Everything`; the transfer check
is `Assets/Scripts/Editor/TransferProbe.cs` (see `DOCS/README.md`).

Two things learned doing it on 2026-10-01:

- **A bag policy leans on the bag.** With the bag made a ghost (nothing touches it), Matt's finished bag
  policy falls forward in 2.1 s: it throws its weight into a target it expects to be there. So a bag
  policy cannot shadow-box, and against an opponent of another height it falls; only the match stage
  produces something that fights.
- **The first minutes of the match stage look like a disaster and are not.** Both fall within 2 s at
  iteration 0 (fall rate 1.00); seven minutes later episodes last 7 s and the fall rate is 0.62.

TensorBoard charts for a report: `tools\tb_shot.ps1 -Tags 'env/fall_rate$' -Out some.png`.

## Not there yet

- No randomisation of friction, mass or motor strength, only sensor noise and shoves. The policies do
  stand and fight in Unity all the same, but the margin has not been measured.
- MuJoCo's joints have rotor inertia (`armature`); Unity's articulation has no such setting, so light
  links (feet, forearms) answer faster there.
- There is no get-up policy.
- The head is part of the torso (no neck joint), as in PoDecath's athlete.
- `.fbx` meshes are not read directly; convert to `.glb` first (Blender: File > Export > glTF).
