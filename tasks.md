# All existing skinned agents: approved implementation plan

Approved by the user on 2026-09-14. Work sequentially, validate and explain each
task, then commit its changes on local master. Do not push without an explicit
request or `git sync`. Preserve pre-existing edits.

## Agreed scope and acceptance

- Include all existing skinned characters, using the supplied rigs. Separate
  clothing/body renderers and duplicate imports are not additional characters.
- Preserve the two contest scenes, appearance, controls and competition rules.
- Basic acceptance per character: survive one full balance round and walk from
  one side of the ring to the other once. A smoke test alone is not a pass.
- Test current policies first. New training uses MuJoCo, never legacy PhysX.
- Check Newton visualization before training, with TensorBoard and visible
  simulator motion throughout training. Before runs expected to last 30 minutes
  or more, save and close Unity; explicitly report when it can be reopened.
- Use Unity CLI/MCP to author scene changes; preserve the portrait presentation.

## Sequential checklist

- [x] 1. Record the starting state: review/preserve existing edits and verify Unity opens.
- [x] 2. Verify Git setup: check Unity/training exclusions and local master workflow.
- [ ] 3. Inventory all skinned characters, rigs, controllers and scene entries.
- [ ] 4. Record actual round duration, hazards, crossing distance, scoring and controls.
- [ ] 5. Test each current character in both contests and record the active brain and outcomes.
- [ ] 6. Check body proportions, joint limits, floor contact, self-collision and cross-engine contestant collisions.

Repeat steps 7-12 per character needing work, finishing that character before
moving to the next. Track the individual status in the results table below.

- [ ] 7. Prepare/correct the MuJoCo body from its supplied rig; connect the matching Unity body through editor tooling.
- [ ] 8. Verify training/game parity: body, timestep, control timing, hazards and crossing distance.
- [ ] 9. Verify Newton visualization displays the relevant body and motion correctly.
- [ ] 10. Prepare training: review obsolete logs, launch TensorBoard and live viewer, close Unity for long runs.
- [ ] 11. Train missing abilities in MuJoCo and evaluate saved checkpoints.
- [ ] 12. Validate one full balance round and one full walking crossing in Unity; record selected brain and evidence.
- [ ] 13. Verify the full roster competes correctly with existing controls, appearance and scoring.
- [ ] 14. Verify automatic recovery for falls, stalls, leaving bounds and round completion without disrupting winner selection.
- [ ] 15. Check portrait framing and adjust cameras through Unity CLI/MCP where necessary.
- [ ] 16. Test on the phone: responsive controls, collisions, resets, crashes and frame pacing; target stable 60 fps.
- [ ] 17. Finish the results report, documenting every character and any unresolved limitation.

## Results

Pending verified inventory and fresh runtime measurements. Historical reports
are context only and must not be marked as current acceptance passes.

## Starting state

- Branch: master. Existing Unity `.gitignore`; no existing tasks.md.
- Unity 6000.6.0f1 is running with SCN_MENU open.
- Pre-existing modified files: two FX materials, menu scene, both contest scenes,
  and Tools/cleanup/SCENE_SMOKE_REPORT.md.
- Pre-existing untracked files: SceneTool_RemoveFighters.cs and its .meta.
- Existing removal tool removes Standard/Bot. Preserve this work; the approved
  scope concerns skinned characters and does not request restoring capsules.
- The pre-existing smoke report describes broken multi-round progression and
  walking camera framing. Recheck against the actual current scene contents.
- Existing work preserved in local commit fced622. Unity Pipeline reports ready,
  stopped, SCN_MENU loaded and clean. Coplay MCP is not connected; use the
  existing com.unity.pipeline 0.5.0 HTTP command bridge (the installed newer CLI
  cannot submit commands to this version directly).
- Git check-ignore confirms Library, Temp, results and nested .venv directories
  are ignored; Assets/Scripts/Editor/Build remains eligible for version control.
  No .gitignore change is needed. No Android device is connected yet.
