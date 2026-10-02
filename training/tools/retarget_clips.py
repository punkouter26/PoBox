"""Walking and turning from the CMU motion-capture database, on the boxers' 21 joints.

    python tools/retarget_clips.py          fetch what is missing, retarget, check against a body, write the file

Writes clips/walk_turn.npz: one array a clip, 50 frames a second, each row
    root x, y, z in leg lengths (thigh + shin)   root quaternion (w x y z)   the 21 joint angles
The angles do not depend on whose body it is, so there is one file for every boxer. What does depend on
the body is how high the pelvis is: place() puts a clip on a model, scaled by its legs, with the lower
foot on the floor in every frame (true of walking and turning; it would not be of running).

The data is the BVH conversion of the CMU database by B. Hahne (cgspeed), fetched from a mirror into
clips/cmu/, which is not versioned. CMU: "This data is free for use in research and commercial projects
worldwide." The database was created with funding from NSF EIA-0196217; mocap.cs.cmu.edu.

How the angles are found. The pelvis and the chest take their turn from the capture's own hip and chest
rotations. The limbs are solved from where the joints are, not from the capture's joint rotations: its
skeleton rests with the legs apart, so its rotations are not ours. A thigh's direction and the capture's
knee axis make the three hip angles; the shin's direction in that frame is the knee; the upper arm's
direction is the two shoulder angles (ours has no twist) and the bend at the elbow is the elbow. The
ankle is the foot's pitch against the shin; its roll is left at zero.
"""
from __future__ import annotations

import argparse
import json
import os
import urllib.request

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
URL = "https://raw.githubusercontent.com/una-dinosauria/cmu-mocap/master/data/{subject:03d}/{name}.bvh"
CLIPS = {  # CMU trial: its description in the database's index
    "07_01": "walk", "08_01": "walk", "16_15": "walk", "35_01": "walk", "69_01": "walk forward",
    "07_04": "slow walk", "16_33": "slow walk, stop",
    "16_11": "walk, veer left", "16_13": "walk, veer right",
    "16_17": "walk, 90-degree left turn", "16_19": "walk, 90-degree right turn",
    "69_24": "walk forward 90 degree left turn", "69_20": "walk forward 90 degree right turn",
    "69_16": "turn in place", "69_18": "turn in place (opposite direction)",
    "69_13": "walk, turn in place (repeated)", "69_06": "walk and turn (repeated)",
    "69_34": "walk backwards and turn", "69_42": "walk sideways and turn (repeated)",
    "69_48": "walk sideways and turn (opposite direction)",
}
FPS = 50
# BVH is x left, y up, z forward; the models are x forward, y left, z up.
M = np.array([[0.0, 0.0, 1.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0]])


def parse_bvh(path: str):
    names, parents, offsets, chans, stack, dt = [], [], [], [], [], 0.0
    lines = open(path, encoding="utf-8").read().split("\n")
    for i, line in enumerate(lines):
        t = line.split()
        if not t:
            continue
        if t[0] in ("ROOT", "JOINT", "End"):
            names.append(t[1] if t[0] != "End" else names[stack[-1]] + "_end")
            parents.append(stack[-1] if stack else -1)
            stack.append(len(names) - 1)
            chans.append([])
        elif t[0] == "OFFSET":
            offsets.append([float(x) for x in t[1:4]])
        elif t[0] == "CHANNELS":
            chans[stack[-1]] = t[2:]
        elif t[0] == "}":
            stack.pop()
        elif t[0] == "Frame":
            dt = float(t[2])
            break
    data = np.array([l.split() for l in lines[i + 1:] if l.strip()], dtype=float)
    return names, parents, np.array(offsets), chans, data[1:], dt   # the first frame is a T-pose, not the motion


def rot(axis: int, a: np.ndarray) -> np.ndarray:
    c, s = np.cos(a), np.sin(a)
    R = np.zeros(a.shape + (3, 3))
    i, j = (axis + 1) % 3, (axis + 2) % 3
    R[..., axis, axis] = 1.0
    R[..., i, i], R[..., i, j], R[..., j, i], R[..., j, j] = c, -s, s, c
    return R


def forward(parents, offsets, chans, data):
    """Every joint's rotation and position in the world, every frame, in the models' axes."""
    T, col = len(data), 0
    R = np.zeros((len(parents), T, 3, 3))
    P = np.zeros((len(parents), T, 3))
    for j, p in enumerate(parents):
        local, t = np.broadcast_to(np.eye(3), (T, 3, 3)), np.zeros((T, 3))
        for c in chans[j]:
            v, col = data[:, col], col + 1
            if c.endswith("position"):
                t[:, "XYZ".index(c[0])] = v
            else:
                local = local @ rot("XYZ".index(c[0]), np.radians(v))
        R[j] = local if p < 0 else R[p] @ local
        P[j] = t if p < 0 else P[p] + R[p] @ offsets[j]
    return M @ R @ M.T, P @ M.T


def unit(v: np.ndarray) -> np.ndarray:
    return v / (np.linalg.norm(v, axis=-1, keepdims=True) + 1e-9)


def retarget(path: str, order: list, lower: np.ndarray, upper: np.ndarray):
    names, parents, offsets, chans, data, dt = parse_bvh(path)
    R, P = forward(parents, offsets, chans, data)
    J = {n: i for i, n in enumerate(names)}
    pel, tor = R[J["Hips"]], R[J["Spine1"]]
    into = lambda frame, v: np.einsum("tji,tj->ti", frame, v)   # a world vector, seen from a frame
    q, dirs = {}, {}

    # pelvis to chest: R = Rz Ry Rx
    A = np.swapaxes(pel, 1, 2) @ tor
    q["abdomen_z"], q["abdomen_y"], q["abdomen_x"] = np.arctan2(A[:, 1, 0], A[:, 0, 0]), -np.arcsin(A[:, 2, 0].clip(-1, 1)), np.arctan2(A[:, 2, 1], A[:, 2, 2])

    leg = 0.0
    for s, side, sign in (("l", "Left", 1.0), ("r", "Right", -1.0)):
        hip, knee, ankle, toe = (P[J[side + n]] for n in ("UpLeg", "Leg", "Foot", "ToeBase"))
        leg += 0.5 * (np.linalg.norm(offsets[J[side + "Leg"]]) + np.linalg.norm(offsets[J[side + "Foot"]]))
        d, shin, foot = unit(into(pel, knee - hip)), unit(into(pel, ankle - knee)), unit(into(pel, toe - ankle))
        # The thigh's frame: z up along it, y the knee's axis (the capture's, squared up to the thigh), x forward.
        z = -d
        y = into(pel, R[J[side + "UpLeg"]][:, :, 1])
        y = unit(y - np.sum(y * z, -1, keepdims=True) * z)
        x = np.cross(y, z)
        # R = Rx(hip_x) Rz(hip_z) Ry(hip_y), columns x y z
        q[f"hip_x_{s}"], q[f"hip_z_{s}"], q[f"hip_y_{s}"] = np.arctan2(y[:, 2], y[:, 1]), -np.arcsin(y[:, 0].clip(-1, 1)), np.arctan2(z[:, 0], x[:, 0])
        k = np.arctan2(-np.sum(shin * x, -1), -np.sum(shin * z, -1)).clip(0.0, None)
        q[f"knee_{s}"] = k
        th = np.stack([x, y, z], -1)
        f = into(th @ rot(1, k), foot)
        rest = M @ offsets[J[side + "ToeBase"]]
        q[f"ankle_y_{s}"] = np.arctan2(-f[:, 2], np.hypot(f[:, 0], f[:, 1])) - np.arctan2(-rest[2], np.hypot(rest[0], rest[1]))
        q[f"ankle_x_{s}"] = np.zeros(len(data))

        sh, el, wr = (P[J[side + n]] for n in ("Arm", "ForeArm", "Hand"))
        u, w = unit(into(tor, el - sh)), unit(into(tor, wr - el))
        # R = Rx(shoulder_x) Rz(shoulder_z), taking (0, sign, 0) to the upper arm's direction
        q[f"shoulder_x_{s}"], q[f"shoulder_z_{s}"] = np.arctan2(sign * u[:, 2], sign * u[:, 1]), np.arcsin((-sign * u[:, 0]).clip(-1, 1))
        q[f"elbow_{s}"] = -sign * np.arccos(np.sum(u * w, -1).clip(-1, 1))
        dirs.update({f"thigh_{s}": d, f"shin_{s}": shin, f"upper_arm_{s}": into(pel, el - sh)})

    joints = np.stack([np.unwrap(q[n]) for n in order], 1)
    clipped = float(np.mean((joints < lower - 1e-6) | (joints > upper + 1e-6)))
    joints = joints.clip(lower, upper)

    # Start at the origin, facing +x.
    yaw = np.arctan2(pel[0, 1, 0], pel[0, 0, 0])
    turn = rot(2, np.array(-yaw))
    pos = (P[J["Hips"]] - P[J["Hips"]][0] * [1, 1, 0]) @ turn.T / leg
    pel = turn @ pel
    quat = np.zeros((len(data), 4))
    for i in range(len(data)):
        mujoco.mju_mat2Quat(quat[i], pel[i].ravel())
        if i and quat[i] @ quat[i - 1] < 0.0:
            quat[i] *= -1.0

    # 120 frames a second to 50.
    src = np.arange(len(data)) * dt
    dst = np.arange(0.0, src[-1], 1.0 / FPS)
    re = lambda a: np.stack([np.interp(dst, src, a[:, c]) for c in range(a.shape[1])], 1)
    row = np.concatenate([re(pos), unit(re(quat)), re(joints)], 1).astype(np.float32)
    return row, {k: unit(re(unit(v))) for k, v in dirs.items()}, clipped


def place(m: mujoco.MjModel, clip: np.ndarray, order: list, prefix: str = "a_") -> np.ndarray:
    """A clip on a body: qpos of that fighter (7 + 21 numbers a frame), scaled by its legs, lower sole on the floor."""
    d = mujoco.MjData(m)
    B, Jn, G = mujoco.mjtObj.mjOBJ_BODY, mujoco.mjtObj.mjOBJ_JOINT, mujoco.mjtObj.mjOBJ_GEOM
    leg = sum(np.linalg.norm(m.body_pos[mujoco.mj_name2id(m, B, prefix + n)]) for n in ("shin_l", "foot_l"))
    root = int(m.jnt_qposadr[mujoco.mj_name2id(m, Jn, prefix + "root")])
    adr = [int(m.jnt_qposadr[mujoco.mj_name2id(m, Jn, prefix + n)]) for n in order]
    feet = [mujoco.mj_name2id(m, G, prefix + f"foot_{s}_geom") for s in "lr"]
    out = clip.astype(np.float64).copy()
    out[:, :3] *= leg
    for i, row in enumerate(out):
        d.qpos[root:root + 7], d.qpos[adr] = row[:7], row[7:]
        mujoco.mj_kinematics(m, d)
        out[i, 2] -= min(d.geom_xpos[g][2] - np.abs(d.geom_xmat[g].reshape(3, 3)[2]) @ m.geom_size[g] for g in feet)
    return out


def check(m: mujoco.MjModel, qpos: np.ndarray, dirs: dict, order: list) -> dict:
    """Degrees between where the capture's limbs point and where the body's do, seen from the pelvis."""
    d = mujoco.MjData(m)
    B, Jn = mujoco.mjtObj.mjOBJ_BODY, mujoco.mjtObj.mjOBJ_JOINT
    body = lambda n: mujoco.mj_name2id(m, B, "a_" + n)
    adr = [int(m.jnt_qposadr[mujoco.mj_name2id(m, Jn, "a_" + n)]) for n in order]
    ends = {"thigh": ("thigh", "shin"), "shin": ("shin", "foot"), "upper_arm": ("upper_arm", "forearm")}
    err = {k: [] for k in dirs}
    for i, row in enumerate(qpos):
        d.qpos[:7], d.qpos[adr] = row[:7], row[7:]
        mujoco.mj_kinematics(m, d)
        pel = d.xmat[body("pelvis")].reshape(3, 3)
        for k in dirs:
            part, s = k.rsplit("_", 1)
            a, b = ends[part]
            v = unit(pel.T @ (d.xpos[body(f"{b}_{s}")] - d.xpos[body(f"{a}_{s}")]))
            err[k].append(np.degrees(np.arccos(np.clip(v @ dirs[k][i], -1, 1))))
    return {k: float(np.mean(v)) for k, v in err.items()}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--models", default=os.path.join(HERE, "models", "v2"))
    ap.add_argument("--body", default="matt", help="the body the result is checked on")
    args = ap.parse_args()
    cfg = json.load(open(os.path.join(args.models, f"{args.body}_policy_config.json"), encoding="utf-8"))
    order, lower, upper = cfg["joint_order"], np.array(cfg["lower"]), np.array(cfg["upper"])
    m = mujoco.MjModel.from_xml_path(os.path.join(args.models, f"{args.body}_solo.xml"))
    cache = os.path.join(HERE, "clips", "cmu")
    os.makedirs(cache, exist_ok=True)

    out, worst = {}, {}
    print(f"{'clip':6s} {'s':>5s} {'m/s':>5s} {'turn':>5s} {'out of range':>12s}   degrees off: thigh shin upper arm   ({args.body})")
    for name, what in CLIPS.items():
        path = os.path.join(cache, name + ".bvh")
        if not os.path.exists(path):
            urllib.request.urlretrieve(URL.format(subject=int(name.split("_")[0]), name=name), path)
        clip, dirs, clipped = retarget(path, order, lower, upper)
        qpos = place(m, clip, order)
        e = check(m, qpos, dirs, order)
        e = {part: 0.5 * (e[part + "_l"] + e[part + "_r"]) for part in ("thigh", "shin", "upper_arm")}
        yaw = np.unwrap(2.0 * np.arctan2(qpos[:, 6], qpos[:, 3]))
        speed = np.linalg.norm(np.diff(qpos[:, :2], axis=0), axis=1).sum() / (len(qpos) / FPS)
        print(f"{name:6s} {len(clip) / FPS:5.1f} {speed:5.2f} {np.degrees(yaw[-1] - yaw[0]):5.0f} {clipped:12.1%}   "
              f"{e['thigh']:17.1f} {e['shin']:4.1f} {e['upper_arm']:9.1f}   {what}")
        out[name] = clip
        worst = {k: max(v, worst.get(k, 0.0)) for k, v in e.items()}
    # A wrong sign or a wrong order of the hip's three angles shows as tens of degrees here.
    assert worst["thigh"] < 3.0 and worst["upper_arm"] < 3.0 and worst["shin"] < 10.0, f"the body does not follow the capture: {worst}"
    path = os.path.join(HERE, "clips", "walk_turn.npz")
    np.savez_compressed(path, joint_order=np.array(order), fps=FPS, descriptions=json.dumps(CLIPS), **out)
    print(f"wrote {path}: {len(out)} clips, {sum(len(c) for c in out.values()) / FPS:.0f} s, {os.path.getsize(path) / 1e6:.2f} MB")


if __name__ == "__main__":
    main()
