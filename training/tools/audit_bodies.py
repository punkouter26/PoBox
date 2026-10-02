"""Every boxer's body on one page: size, mass, joint ranges, drive strength and speed, and whether the
legs are strong enough for what a bout asks of them.

    python tools/audit_bodies.py            a table, and the checks that fail
    python tools/audit_bodies.py --json     the same numbers, for another tool

What is checked, and against what:
  mass for height     a body-mass index between 18 and 32
  joint ranges        the same hinge ranges for every boxer (they come from one template); printed once
  drive strength      peak torque at the knee, hip, shoulder and elbow beside a trained adult's
                      (knee 250, hip 220, shoulder 90, elbow 70 N m: dynamometer figures for a fit man)
  rising              the torque it takes to hold the body in a deep squat (thighs level), a knee each,
                      as a share of the knee's limit. Over 100% the boxer cannot stand up from the canvas
                      however it is trained; over about 80% it has to do it with momentum.
  speed               the joint speed limit at the elbow and the knee, beside a person's (about 20 and 14 rad/s)
  drive gains         the natural frequency and damping ratio of each drive on its own link
"""
from __future__ import annotations

import argparse
import json
import math
import os
import sys

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ADULT = {"knee": 250.0, "hip_y": 220.0, "shoulder_x": 90.0, "elbow": 70.0, "abdomen_y": 200.0, "ankle_y": 150.0}


def boxers() -> list:
    names = []
    for f in sorted(os.listdir(os.path.join(HERE, "models"))):
        if f.endswith("_bag.xml"):
            names.append(f[:-8])
    return names


def audit(name: str) -> dict:
    cfg = json.load(open(os.path.join(HERE, "models", f"{name}_policy_config.json"), encoding="utf-8"))
    m = mujoco.MjModel.from_xml_path(os.path.join(HERE, "models", f"{name}_bag.xml"))
    d = mujoco.MjData(m)
    mujoco.mj_resetDataKeyframe(m, d, 0)
    mujoco.mj_forward(m, d)
    order = cfg["joint_order"]
    idx = {n: i for i, n in enumerate(order)}
    lim = dict(zip(order, cfg["force_limit"]))
    kp, kv = dict(zip(order, cfg["kp"])), dict(zip(order, cfg["kv"]))
    vel = dict(zip(order, cfg["velocity_limit"]))
    B = mujoco.mjtObj.mjOBJ_BODY
    pelvis = mujoco.mj_name2id(m, B, "a_pelvis")
    mass = float(m.body_subtreemass[pelvis])
    height = float(cfg.get("height_m", 0.0))

    def body(n):
        return mujoco.mj_name2id(m, B, "a_" + n)

    # The deep squat, statically: everything above the knees, half on each, at a lever arm of the thigh.
    names = [mujoco.mj_id2name(m, B, i) or "" for i in range(m.nbody)]
    below = [i for i, n in enumerate(names) if n.startswith("a_") and any(t in n for t in ("shin", "foot", "calf", "lower_leg"))]
    thigh = [i for i, n in enumerate(names) if n.startswith("a_") and "thigh" in n]
    m_below = float(sum(m.body_mass[i] for i in below))
    m_thigh = float(sum(m.body_mass[i] for i in thigh))
    knee_j = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, "a_knee_l")
    hip_j = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, "a_hip_y_l")
    thigh_len = float(np.linalg.norm(d.xanchor[knee_j] - d.xanchor[hip_j]))
    above = mass - m_below - m_thigh
    squat = (above * thigh_len + m_thigh * thigh_len * 0.5) * 9.81 / 2.0
    # A drive on its own link: reflected inertia from the mass matrix's diagonal at the guard pose.
    M = np.zeros((m.nv, m.nv))
    for i in range(m.nv):
        e = np.zeros(m.nv); e[i] = 1.0
        mujoco.mj_mulM(m, d, M[i], e)
    gains = {}
    for n in ("knee_l", "hip_y_l", "elbow_l", "shoulder_x_l", "abdomen_y", "ankle_y_l"):
        j = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, "a_" + n)
        inertia = float(M[m.jnt_dofadr[j], m.jnt_dofadr[j]])
        wn = math.sqrt(kp[n] / max(1e-6, inertia))
        gains[n] = {"hz": wn / (2 * math.pi), "zeta": (kv[n] + float(m.dof_damping[m.jnt_dofadr[j]])) / (2.0 * math.sqrt(kp[n] * inertia))}
    return {
        "name": name, "mass": mass, "height": height, "bmi": mass / height ** 2 if height else float("nan"),
        "strength": cfg.get("strength", 1.0), "speed": cfg.get("speed", 1.0), "stand": cfg["stand_height"],
        "knee": lim["knee_l"], "hip": lim["hip_y_l"], "shoulder": lim["shoulder_x_l"], "elbow": lim["elbow_l"],
        "abdomen": lim["abdomen_y"], "ankle": lim["ankle_y_l"],
        "elbow_speed": vel["elbow_l"], "knee_speed": vel["knee_l"],
        "thigh_len": thigh_len, "squat_torque": squat, "squat_share": squat / lim["knee_l"],
        "weight_per_knee": mass * 9.81 / (2.0 * lim["knee_l"]),
        "gains": gains,
        "ranges_deg": {n: [round(math.degrees(cfg["lower"][idx[n]])), round(math.degrees(cfg["upper"][idx[n]]))] for n in order},
        "shapes": int(sum(1 for g in range(m.ngeom) if (mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_GEOM, g) or "").startswith("a_"))),
        "timestep": float(m.opt.timestep), "style": cfg.get("style", {}),
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()
    rows = [audit(n) for n in boxers()]
    if args.json:
        print(json.dumps(rows, indent=1))
        return
    print(f"{'boxer':9s} {'kg':>6s} {'m':>5s} {'BMI':>5s} {'str':>5s} {'spd':>5s} | {'knee':>5s} {'hip':>5s} {'shld':>5s} {'elbw':>5s} {'abd':>5s} {'ankl':>5s} N m "
          f"| {'squat':>6s} {'of knee':>7s} | {'elbow':>5s} {'knee':>5s} rad/s | shapes")
    bad = []
    for r in rows:
        print(f"{r['name']:9s} {r['mass']:6.1f} {r['height']:5.2f} {r['bmi']:5.1f} {r['strength']:5.2f} {r['speed']:5.2f} | "
              f"{r['knee']:5.0f} {r['hip']:5.0f} {r['shoulder']:5.0f} {r['elbow']:5.0f} {r['abdomen']:5.0f} {r['ankle']:5.0f}     "
              f"| {r['squat_torque']:6.0f} {r['squat_share']:6.0%}  | {r['elbow_speed']:5.1f} {r['knee_speed']:5.1f}       | {r['shapes']}")
        if not 18.0 <= r["bmi"] <= 32.0:
            bad.append(f"{r['name']}: body-mass index {r['bmi']:.1f}")
        if r["squat_share"] > 1.0:
            bad.append(f"{r['name']}: holding a deep squat takes {r['squat_share']:.0%} of the knee's limit; it cannot rise from the canvas on its legs alone")
        elif r["squat_share"] > 0.8:
            bad.append(f"{r['name']}: holding a deep squat takes {r['squat_share']:.0%} of the knee's limit; rising will need momentum or the arms")
        for j, key in (("knee", "knee"), ("hip", "hip_y"), ("shoulder", "shoulder_x"), ("elbow", "elbow")):
            share = r[j] / (ADULT[key] * r["strength"])
            if not 0.6 <= share <= 1.6:
                bad.append(f"{r['name']}: {j} limit {r[j]:.0f} N m is {share:.0%} of a person's at its strength")
    print("\ndrives on their own links at the guard pose (natural frequency Hz, damping ratio):")
    for r in rows:
        print(f"  {r['name']:9s} " + "  ".join(f"{n.replace('_l', '')} {g['hz']:.1f}/{g['zeta']:.2f}" for n, g in r["gains"].items()))
    same = all(r["ranges_deg"] == rows[0]["ranges_deg"] for r in rows)
    print(f"\njoint ranges ({'the same for all' if same else 'DIFFER between boxers'}), degrees:")
    print("  " + "  ".join(f"{n} {lo}..{hi}" for n, (lo, hi) in rows[0]["ranges_deg"].items()))
    print(f"\nphysics step {rows[0]['timestep'] * 1000:.0f} ms, control every 4 steps (50 Hz)")
    print("\nchecks: " + ("all pass" if not bad else ""))
    for b in bad:
        print("  - " + b)


if __name__ == "__main__":
    main()
