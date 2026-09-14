# Nick's bounded actuation candidate

The source is the existing Unity export of the supplied RIGGED_Nick.glb rig.
`prepare_human_limits.py` changes only position-actuator force limits and
velocity feedback. It asserts unchanged proportions, mass (75 kg), inertia,
collision geometry, joint ranges and armature. `nick_human.limits.json` lists
every editable actuator property applied through Unity Pipeline.

These are conservative engineering budgets for a game humanoid, not a fitted
biomechanical model of a measured individual. Lower-limb torque directions use
the exported hinge axes: positive hip pitch extends, positive knee pitch flexes,
positive ankle pitch plantarflexes. Hip pitch is -140/+200 Nm; knee -200/+100 Nm;
ankle -45/+120 Nm. Upper-arm axes are capped at 60 Nm (axial rotation 25 Nm),
elbow at -40/+60 Nm, wrist at 10 Nm (deviation 5 Nm), neck at 8–15 Nm. Torso
limits are 150/100/60 Nm for pitch/roll/yaw. These deliberately replace unlimited
effort and are not increased simply to rescue a failing policy.

Reference context: [Anderson et al., lower-limb torque as a function of angle
and speed](https://stacks.cdc.gov/view/cdc/188754),
[Holzbaur et al., measured upper-limb strength](https://pubmed.ncbi.nlm.nih.gov/17250841/),
and [Choi and Vanderby, cervical loading](https://pubmed.ncbi.nlm.nih.gov/10776903/).
Published strength varies with posture, movement direction, age and individual;
the complete per-axis values here are implementation choices within that context.

## Speed envelope

Driven speed budgets are 4 rad/s for torso/neck, 6 for hips, 8 for knees,
4–6 for ankles, 6–8 for shoulders, 8 for elbows and 6 for wrists. These are
task budgets for standing/walking, not claims about universal human maxima.
For each position servo, choose `kv >= kp * largest legal angle error / vmax`.
For every allowed target and joint angle, actuator torque then opposes motion
at or above vmax. MuJoCo clamps both driving and braking effort to the torque
budget. External impacts and coupled-body motion can exceed the driven speed;
actual speeds still need measurement. No qpos/qvel clipping hides a physics
failure. This is a simple torque-limited motor approximation, not a muscle model.

Changing these actuators invalidates previous policy acceptance. Test the old
brain, train as needed, and verify Unity/exported-body parity before deployment.
Parent-child collision filtering currently avoids overlapping anatomy at the
shared joint; other self contacts remain on. Shared hazards and other contestants
remain separate open work in the validation report.
