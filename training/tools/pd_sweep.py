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
    args = ap.parse_args()

    xml = os.path.join(HERE, "models", "v2", f"{args.name}_solo.xml")
    m = mujoco.MjModel.from_xml_path(xml)
    cfg = json.load(open(os.path.join(HERE, "models", "v2", f"{args.name}_policy_config.json"), encoding="utf-8"))
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


if __name__ == "__main__":
    main()
