"""Render a model's stand keyframe to a PNG, from the side and the front, so a pose can be looked at.

    python tools/render_pose.py --xml models/fighter_spar.xml --out logs/pose.png
"""
from __future__ import annotations

import argparse
import os

import mujoco
import numpy as np
from PIL import Image


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--xml", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--settle", type=float, default=0.0, help="seconds to simulate holding the keyframe pose first")
    args = ap.parse_args()

    m = mujoco.MjModel.from_xml_path(args.xml)
    d = mujoco.MjData(m)
    mujoco.mj_resetDataKeyframe(m, d, 0)
    # Hold the pose: the actuators are position servos, so the keyframe's joint angles are the targets.
    nq_joint = m.nu // max(1, sum(1 for i in range(m.njnt) if m.jnt_type[i] == mujoco.mjtJoint.mjJNT_FREE))
    for i in range(m.nu):
        d.ctrl[i] = d.qpos[m.jnt_qposadr[m.actuator_trnid[i, 0]]]
    mujoco.mj_forward(m, d)
    for _ in range(int(args.settle / m.opt.timestep)):
        mujoco.mj_step(m, d)

    r = mujoco.Renderer(m, height=480, width=640)
    cam = mujoco.MjvCamera()
    shots = []
    for azimuth, elevation in ((90, -8), (180, -8), (135, -25)):
        cam.lookat[:] = [0.0, 0.0, 1.0]
        cam.distance = 4.2
        cam.azimuth = azimuth
        cam.elevation = elevation
        r.update_scene(d, cam)
        shots.append(r.render())
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    Image.fromarray(np.concatenate(shots, axis=1)).save(args.out)
    pelvis = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "a_pelvis")
    print(f"wrote {args.out}; a_pelvis height {d.xpos[pelvis][2]:.3f} m after {args.settle:g} s, ncon {d.ncon}")


if __name__ == "__main__":
    main()
