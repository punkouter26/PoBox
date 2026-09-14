"""Render live MuJoCo states in Newton without running a second physics solver."""
from pathlib import Path
import time

import mujoco
import newton
import numpy as np
import warp as wp
from newton.viewer import ViewerGL


class NewtonMujocoViewer:
    def __init__(self, model_path: str, mj_model, mj_data, track_body: int):
        self.data = mj_data
        self.track_body = track_body
        builder = newton.ModelBuilder(up_axis=newton.Axis.Z)
        builder.add_mjcf(str(Path(model_path).resolve()), parse_visuals=True,
                         enable_self_collisions=True)
        self.model = builder.finalize(device="cpu")
        self.state = self.model.state()
        self.body_ids = np.array([
            mujoco.mj_name2id(mj_model, mujoco.mjtObj.mjOBJ_BODY, label.split("/")[-1])
            for label in self.model.body_label
        ], dtype=np.int32)
        if np.any(self.body_ids < 0) or len(set(self.body_ids)) != len(self.body_ids):
            raise ValueError("Newton body names do not map uniquely to the MuJoCo model")
        self.viewer = ViewerGL(width=960, height=720, vsync=True,
                               enable_cuda_interop=ViewerGL.CudaInterop.NONE)
        self.viewer.set_model(self.model)
        self.last_position = self.data.xpos[self.track_body].copy()
        self.viewer.set_camera(wp.vec3(float(self.last_position[0] + 4),
                                      float(self.last_position[1] - 4), 2.8),
                               pitch=-18.0, yaw=135.0)
        self.positions = np.empty((len(self.body_ids), 7), dtype=np.float32)
        self.sync()

    def sync(self):
        if not self.is_running():
            return
        # MuJoCo stores quaternion wxyz; Warp transforms store xyz + xyzw.
        self.positions[:, :3] = self.data.xpos[self.body_ids]
        self.positions[:, 3:6] = self.data.xquat[self.body_ids, 1:]
        self.positions[:, 6] = self.data.xquat[self.body_ids, 0]
        self.state.body_q.assign(self.positions)
        # Follow translation while retaining the user's orbit/zoom adjustments.
        p = self.data.xpos[self.track_body]
        delta = p - self.last_position
        camera = self.viewer.camera
        offset = camera._as_vec3((delta[0], delta[1], 0))
        camera.pos += offset
        camera.pivot += offset
        self.last_position = p.copy()
        self.viewer.begin_frame(float(self.data.time))
        self.viewer.log_state(self.state)
        self.viewer.end_frame()

    def wait_for_step(self):
        while self.is_running() and not self.viewer.should_step():
            self.sync()
            time.sleep(0.01)

    def is_running(self):
        return self.viewer.is_running()

    def close(self):
        self.viewer.close()
