"""The Melee's downed bodies, plain mujoco (no Warp), so the C MuJoCo exams (tools/melee_exam.py) build the same model."""
from __future__ import annotations

import mujoco


def lying_bodies(xml_path: str, n: int):
    """The model with n boxers down on the canvas: mocap bodies (static, solid) of a lying adult's capsules, along x."""
    spec = mujoco.MjSpec.from_file(xml_path)
    C, S = mujoco.mjtGeom.mjGEOM_CAPSULE, mujoco.mjtGeom.mjGEOM_SPHERE
    parts = [(C, 0.13, (-0.25, 0, 0.13, 0.25, 0, 0.13)), (S, 0.10, (0.45, 0, 0.10)),
             (C, 0.07, (-0.30, 0.10, 0.07, -1.10, 0.14, 0.07)), (C, 0.07, (-0.30, -0.10, 0.07, -1.10, -0.14, 0.07)),
             (C, 0.05, (0.20, 0.22, 0.05, -0.30, 0.35, 0.05)), (C, 0.05, (0.20, -0.22, 0.05, -0.30, -0.35, 0.05))]
    for j in range(n):
        b = spec.worldbody.add_body(name=f"downed_{j}", mocap=True, pos=[30.0 + 5.0 * j, 30.0, 0.0])
        for i, (kind, r, at) in enumerate(parts):
            g = b.add_geom(name=f"downed_{j}_{i}", type=kind, size=[r, 0, 0], rgba=[0.5, 0.4, 0.35, 1])
            if kind == C:
                g.fromto = list(at)
            else:
                g.pos = list(at)
    return spec.compile()
