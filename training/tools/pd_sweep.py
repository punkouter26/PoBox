"""How each joint's position drive answers a step: rise time, overshoot and damping ratio, joint by joint.

    python tools/pd_sweep.py                          matt, as trained (his config's speed-limit rule)
    python tools/pd_sweep.py --name kim --step 0.3 --write pd_gains_proposal_kim.json

The boxer's own model (models/v2/NAME_solo.xml, C MuJoCo on the CPU) with its pelvis held half a metre off the floor
(the free joint is put back every step, so the base is fixed and the feet touch nothing). From the guard, one
joint's target jumps by --step radians (towards the side of its range with room) while every other target stays;
a second of the joint's angle gives:

    rise     10% to 90% of the way, seconds
    over     overshoot, as a share of the step
    zeta     damping ratio read off the overshoot (a step that does not overshoot is at least 1)
    zeta_a   the same from the model: (kv + joint damping [+ force limit / speed limit]) / (2 sqrt(kp * M)), M the
             joint's own inertia in the guard (armature included) from the mass matrix

--loaded stands the boxer on its feet instead, so the legs carry the body: a balance hand on the pelvis (a spring and
damper on its place across the floor and on its tilt and heading, nothing vertical) keeps it from toppling, the step is
--step-loaded, and each joint's (w_n, zeta) is fitted to its whole trace, which works whether it overshoots or not; the
proposal is the kv that gives --target-zeta at that fitted ratio. The hand steadies the trunk, so an ankle's or a hip's
answer is the joint's with the trunk held level: a standing estimate, not the free body. --rig writes the proposal into a
rig file as "joint_kv" (each side's mean, kept within --kv-band of the drive's own), which rig_to_mjcf.py builds from.

A person's joints settle like a ratio of about 0.7 to 1.0. --write puts a proposal beside the model: the kv that would
give --target-zeta at each joint's kp, per joint group. It changes no model: models/v2 is what Unity compiles, and a
gain changed there is a parity change for MjRetrofit and every shipped brain.
"""
from __future__ import annotations

import argparse
import json
import math
import os

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", default="matt")
    ap.add_argument("--step", type=float, default=0.3, help="size of the target's jump, radians")
    ap.add_argument("--seconds", type=float, default=1.0)
    ap.add_argument("--speed-limit", choices=["config", "on", "off"], default="config",
                    help="add the speed-limit rule's damping (force limit / speed limit), as the trainer does; config = as the boxer's config says")
    ap.add_argument("--target-zeta", type=float, default=0.85)
    ap.add_argument("--write", default="", help="write the proposal here (relative to training/)")
    ap.add_argument("--models", default=os.path.join("models", "v2"))
    ap.add_argument("--loaded", action="store_true", help="standing on its feet, steadied at the pelvis (see above)")
    ap.add_argument("--step-loaded", type=float, default=0.12)
    ap.add_argument("--rig", default="", help="--loaded: write joint_kv into this rig file")
    ap.add_argument("--kv-band", type=float, default=2.5, help="a proposed kv stays within this factor of the drive's own")
    args = ap.parse_args()
    if args.loaded:
        return loaded(args)

    xml = os.path.join(HERE, args.models, f"{args.name}_solo.xml")
    m = mujoco.MjModel.from_xml_path(xml)
    cfg = json.load(open(os.path.join(HERE, args.models, f"{args.name}_policy_config.json"), encoding="utf-8"))
    order = cfg["joint_order"]
    limited = cfg.get("speed_limit", False) if args.speed_limit == "config" else args.speed_limit == "on"
    jid = [mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, "a_" + n) for n in order]
    if limited:
        for j, fl, vl in zip(jid, cfg["force_limit"], cfg["velocity_limit"]):
            m.dof_damping[m.jnt_dofadr[j]] += fl / vl
    root_q = int(m.jnt_qposadr[mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, "a_root")])
    root_v = int(m.jnt_dofadr[mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, "a_root")])
    d = mujoco.MjData(m)
    mujoco.mj_resetDataKeyframe(m, d, 0)
    d.qpos[root_q + 2] += 0.5
    base = d.qpos[root_q:root_q + 7].copy()
    guard = np.array([d.qpos[m.jnt_qposadr[j]] for j in jid])
    start = d.qpos.copy()

    def run(target: np.ndarray, seconds: float, joint: int = -1):
        trace = []
        for _ in range(int(seconds / m.opt.timestep)):
            d.ctrl[:] = target
            mujoco.mj_step(m, d)
            d.qpos[root_q:root_q + 7] = base           # the fixed base
            d.qvel[root_v:root_v + 6] = 0.0
            if joint >= 0:
                trace.append(d.qpos[m.jnt_qposadr[joint]])
        return np.array(trace)

    M = np.zeros((m.nv, m.nv))
    rows, dt = [], m.opt.timestep
    print(f"{args.name}: step {args.step} rad from the guard, base fixed, speed-limit damping {'on' if limited else 'off'}")
    print(f"{'joint':14} {'kp':>7} {'kv':>7} {'damp':>6} {'M':>6} {'rise s':>7} {'over':>6} {'zeta':>6} {'zeta_a':>6} {'w_n Hz':>6}")
    for i, (n, j) in enumerate(zip(order, jid)):
        d.qpos[:], d.qvel[:] = start, 0.0
        run(guard, 0.5)                                 # settle in the guard
        mujoco.mj_fullM(m, M, d.qM)
        dof = int(m.jnt_dofadr[j])
        lo, hi = m.jnt_range[j]
        q0 = d.qpos[m.jnt_qposadr[j]]
        sign = 1.0 if hi - q0 >= q0 - lo else -1.0
        target = guard.copy()
        target[i] = q0 + sign * args.step
        tr = run(target, args.seconds, j) - q0
        final = tr[-int(0.1 / dt):].mean()
        frac = tr / final if abs(final) > 1e-6 else tr * 0.0
        t10 = np.argmax(frac >= 0.1) * dt
        t90 = np.argmax(frac >= 0.9) * dt if (frac >= 0.9).any() else float("nan")
        over = max(0.0, frac.max() - 1.0)
        zeta = -math.log(over) / math.sqrt(math.pi ** 2 + math.log(over) ** 2) if over > 0.005 else float("inf")
        a = m.actuator(i)
        kp, kv = float(a.gainprm[0]), float(-a.biasprm[2])
        damp = float(m.dof_damping[dof])
        mass = float(M[dof, dof])
        zeta_a = (kv + damp) / (2.0 * math.sqrt(kp * mass))
        rows.append({"joint": n, "kp": kp, "kv": kv, "damping": damp, "inertia": mass, "rise_s": t90 - t10, "overshoot": over,
                     "zeta": zeta, "zeta_model": zeta_a, "w_n_hz": math.sqrt(kp / mass) / (2 * math.pi), "reached": float(final / (sign * args.step)),
                     "kv_for_target": max(0.0, 2.0 * args.target_zeta * math.sqrt(kp * mass) - damp)})
        r = rows[-1]
        print(f"{n:14} {kp:7.1f} {kv:7.2f} {damp:6.2f} {mass:6.3f} {r['rise_s']:7.3f} {over:6.1%} {zeta:6.2f} {zeta_a:6.2f} {r['w_n_hz']:6.1f}"
              + ("" if r["reached"] > 0.9 else f"   reached {r['reached']:.0%} of the step"))

    groups = {}
    for r in rows:
        g = r["joint"].rsplit("_", 1)[0] if r["joint"][-2:] in ("_l", "_r") else r["joint"]
        groups.setdefault(g, []).append(r)
    print(f"\nby group (target ratio {args.target_zeta}; the kv that would give it, against the model's):")
    proposal = {}
    for g, rs in groups.items():
        z = float(np.mean([r["zeta_model"] for r in rs]))
        kv_new = float(np.mean([r["kv_for_target"] for r in rs]))
        verdict = "under-damped" if z < 0.7 else "over-damped" if z > 1.0 else "human-like"
        print(f"  {g:12} zeta {z:5.2f} ({verdict}), rise {np.mean([r['rise_s'] for r in rs]):.3f} s, kv {rs[0]['kv']:.2f} -> {kv_new:.2f}")
        proposal[g] = {"kp": rs[0]["kp"], "kv_now": rs[0]["kv"], "kv_proposed": round(kv_new, 3), "zeta_now": round(z, 3), "verdict": verdict}
    if args.write:
        path = args.write if os.path.isabs(args.write) else os.path.join(HERE, args.write)
        with open(path, "w", encoding="utf-8") as fh:
            json.dump({"boxer": args.name, "model": os.path.relpath(xml, HERE), "speed_limit_damping": limited, "target_zeta": args.target_zeta,
                       "note": "A proposal only: models/v2 is unchanged. Adopting it is a parity change (MjRetrofit rebuild, "
                               "every brain of this boxer retrained or re-examined).", "groups": proposal, "joints": rows}, fh, indent=1)
        print(f"proposal written to {os.path.relpath(path, HERE)}")


def fit_second_order(trace: np.ndarray, dt: float):
    """The (w_n, zeta) of a unit step response that best fits a trace (already divided by its final value), by a grid."""
    t = np.arange(1, len(trace) + 1) * dt
    zetas = np.linspace(0.08, 3.0, 147)
    wns = np.geomspace(3.0, 300.0, 140)
    Z, W = np.meshgrid(zetas, wns, indexing="ij")
    z, w = Z[..., None], W[..., None]
    under = z < 1.0
    root = np.sqrt(np.abs(1.0 - z ** 2)) + 1e-9
    wd = w * root
    y_under = 1.0 - np.exp(-z * w * t) * (np.cos(wd * t) + z / root * np.sin(wd * t))
    r1, r2 = -w * (z - root), -w * (z + root)
    y_over = 1.0 + (r2 * np.exp(r1 * t) - r1 * np.exp(r2 * t)) / (r1 - r2 + 1e-9)
    y = np.where(under, y_under, y_over)
    err = ((y - trace) ** 2).mean(-1)
    i = np.unravel_index(np.argmin(err), err.shape)
    fit = 1.0 - err[i] / max(1e-9, ((trace - trace.mean()) ** 2).mean())
    return float(W[i]), float(Z[i]), float(fit)


def loaded(args) -> None:
    xml = os.path.join(HERE, args.models, f"{args.name}_solo.xml")
    m = mujoco.MjModel.from_xml_path(xml)
    cfg = json.load(open(os.path.join(HERE, args.models, f"{args.name}_policy_config.json"), encoding="utf-8"))
    order = cfg["joint_order"]
    limited = cfg.get("speed_limit", False) if args.speed_limit == "config" else args.speed_limit == "on"
    jid = [mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, "a_" + n) for n in order]
    if limited:
        for j, fl, vl in zip(jid, cfg["force_limit"], cfg["velocity_limit"]):
            m.dof_damping[m.jnt_dofadr[j]] += fl / vl
    root_q = int(m.jnt_qposadr[mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, "a_root")])
    root_v = int(m.jnt_dofadr[mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, "a_root")])
    pelvis = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, "a_pelvis")
    mass = float(m.body_subtreemass[pelvis])
    d = mujoco.MjData(m)
    mujoco.mj_resetDataKeyframe(m, d, 0)
    mujoco.mj_forward(m, d)
    xy0, q0 = d.qpos[root_q:root_q + 2].copy(), d.qpos[root_q + 3:root_q + 7].copy()
    guard = np.array([d.qpos[m.jnt_qposadr[j]] for j in jid])
    start_q, dt = d.qpos.copy(), m.opt.timestep
    # The hand: stiff enough across the floor and in tilt to hold an inverted pendulum of the body's weight up (m g h is
    # about 750 N m/rad for 85 kg at the hip's height), and nothing up or down, so the legs take the weight.
    k_xy, c_xy, k_rot, c_rot = 40.0 * mass, 8.0 * mass, 2.5 * mass * 9.81, 0.25 * mass * 9.81
    err = np.zeros(3)

    def run(target: np.ndarray, seconds: float, joint: int = -1):
        trace = []
        for _ in range(int(seconds / dt)):
            d.ctrl[:] = target
            f = np.zeros(6)
            f[:2] = -k_xy * (d.qpos[root_q:root_q + 2] - xy0) - c_xy * d.qvel[root_v:root_v + 2]
            mujoco.mju_subQuat(err, d.qpos[root_q + 3:root_q + 7], q0)
            f[3:] = -k_rot * err - c_rot * d.qvel[root_v + 3:root_v + 6]
            d.xfrc_applied[pelvis] = f
            mujoco.mj_step(m, d)
            if joint >= 0:
                trace.append(d.qpos[m.jnt_qposadr[joint]])
        return np.array(trace)

    print(f"{args.name}: standing, steadied at the pelvis; step {args.step_loaded} rad; speed-limit damping {'on' if limited else 'off'}; target zeta {args.target_zeta}")
    print(f"{'joint':14} {'kp':>7} {'kv':>7} {'damp':>6} {'w_n Hz':>7} {'zeta':>6} {'fit':>5} {'reached':>8} {'kv ->':>7}")
    rows = []
    for i, (n, j) in enumerate(zip(order, jid)):
        d.qpos[:], d.qvel[:] = start_q, 0.0
        run(guard, 0.6)
        lo, hi = m.jnt_range[j]
        qa = d.qpos[m.jnt_qposadr[j]]
        sign = 1.0 if hi - qa >= qa - lo else -1.0
        target = guard.copy()
        target[i] = guard[i] + sign * args.step_loaded
        tr = run(target, 0.8, j) - qa
        final = tr[-int(0.1 / dt):].mean()
        reached = float(final / (sign * args.step_loaded))
        a = m.actuator(i)
        kp, kv = float(a.gainprm[0]), float(-a.biasprm[2])
        damp = float(m.dof_damping[int(m.jnt_dofadr[j])])
        w, z, fit = fit_second_order(tr / final, dt) if abs(final) > 1e-4 else (float("nan"), float("nan"), 0.0)
        ok = 0.5 < reached < 2.0 and fit > 0.9
        # A leg with both feet down and the trunk steadied is a closed chain: one joint's step barely moves it. Those
        # fall back to the model's ratio, from the joint's own inertia in the standing pose (a lower bound on the load).
        M = np.zeros((m.nv, m.nv))
        mujoco.mj_fullM(m, M, d.qM)
        inertia = float(M[int(m.jnt_dofadr[j]), int(m.jnt_dofadr[j])])
        kv_new = (kv + damp) * args.target_zeta / z - damp if ok else 2.0 * args.target_zeta * math.sqrt(kp * inertia) - damp
        kv_new = float(min(max(kv_new, kv / args.kv_band), kv * args.kv_band))
        rows.append({"joint": n, "kp": kp, "kv": kv, "damping": damp, "w_n_hz": w / (2 * math.pi), "zeta": z, "fit": fit,
                     "reached": reached, "source": "standing" if ok else "model", "kv_proposed": round(kv_new, 3)})
        print(f"{n:14} {kp:7.1f} {kv:7.2f} {damp:6.2f} {w / (2 * math.pi):7.2f} {z:6.2f} {fit:5.2f} {reached:8.0%} {kv_new:7.2f}"
              + ("" if ok else "   (closed chain: the model's inertia)"))
    if args.write:
        path = args.write if os.path.isabs(args.write) else os.path.join(HERE, args.write)
        with open(path, "w", encoding="utf-8") as fh:
            json.dump({"boxer": args.name, "model": os.path.relpath(xml, HERE), "loaded": True, "target_zeta": args.target_zeta, "joints": rows}, fh, indent=1)
        print(f"proposal written to {os.path.relpath(path, HERE)}")
    if args.rig:
        path = args.rig if os.path.isabs(args.rig) else os.path.join(HERE, args.rig)
        with open(path, "r", encoding="utf-8") as fh:
            rig = json.load(fh)
        # One damping a joint, both sides: the mean of the two sides' proposals (a body is not built lopsided).
        sides = {}
        for r in rows:
            base = r["joint"][:-2] if r["joint"][-2:] in ("_l", "_r") else r["joint"]
            sides.setdefault(base, []).append(r["kv_proposed"])
        rig["joint_kv"] = {b: round(float(np.mean(v)), 3) for b, v in sides.items()}
        rig.setdefault("notes", []).append(f"joint_kv from tools/pd_sweep.py --loaded (target zeta {args.target_zeta})")
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(rig, fh, indent=2)
        print(f"joint_kv written to {os.path.relpath(path, HERE)}")


if __name__ == "__main__":
    main()
