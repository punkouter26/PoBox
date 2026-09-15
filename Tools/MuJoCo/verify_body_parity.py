"""Prove the body Unity will run is the body the trainer trained.

    Tools/MuJoCo/.venv/Scripts/python.exe Tools/MuJoCo/verify_body_parity.py \
        --unity Tools/MuJoCo/grandma_unity.xml --training Tools/MuJoCo/grandma_human.xml

Compiles both MJCFs and compares every physical array that shapes the motion:
body placement, mass and inertia, joint axes/ranges/armature, geometry and
contact flags, and -- the part prepare_human_limits.py adds -- actuator
gains, damping and force limits. A mismatch here is a policy trained for a
creature that does not exist (CLAUDE.md), which is the failure this project has
already shipped once with a timestep.
"""
import argparse
from pathlib import Path

import mujoco
import numpy as np

STRUCTURAL = ("body_pos", "body_quat", "body_mass", "body_inertia", "jnt_axis", "jnt_range",
              "dof_armature", "dof_damping", "geom_pos", "geom_quat", "geom_size", "geom_type",
              "geom_contype", "geom_conaffinity")
ACTUATION = ("actuator_gainprm", "actuator_biasprm", "actuator_ctrlrange", "actuator_forcerange",
             "actuator_forcelimited", "actuator_ctrllimited")


def compare(unity: Path, training: Path, tolerance: float) -> int:
    a = mujoco.MjModel.from_xml_path(str(unity.resolve()))
    b = mujoco.MjModel.from_xml_path(str(training.resolve()))
    failures = 0
    for attr in STRUCTURAL + ACTUATION:
        x, y = np.asarray(getattr(a, attr)), np.asarray(getattr(b, attr))
        if x.shape != y.shape:
            print(f"MISMATCH {attr}: shape {x.shape} vs {y.shape}")
            failures += 1
            continue
        diff = np.abs(x.astype(float) - y.astype(float)).max() if x.size else 0.0
        if diff > tolerance:
            print(f"MISMATCH {attr}: max |diff| {diff:.6g}")
            failures += 1
    print(f"{'PARITY OK' if failures == 0 else 'PARITY FAILED'}: {unity.name} vs {training.name} "
          f"({a.nbody} bodies, {a.nu} actuators, {a.body_mass.sum():.1f} kg, tolerance {tolerance})")
    return failures


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--unity", type=Path, required=True, help="MJCF exported from the Unity scene AFTER limits were applied")
    ap.add_argument("--training", type=Path, required=True, help="the *_human.xml the trainer used")
    ap.add_argument("--tolerance", type=float, default=1e-4)
    args = ap.parse_args()
    raise SystemExit(1 if compare(args.unity, args.training, args.tolerance) else 0)
