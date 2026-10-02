"""Build every boxer in roster.json from its mesh: the rig, the MuJoCo models, and the house-rule checks.

    python tools/build_roster.py                 every boxer, then every pair model
    python tools/build_roster.py --only nick     one boxer and the pairs it is in

For each boxer: glb_to_rig.py reads the skeleton and fits the collision shapes inside the mesh (a picture of
the fit goes to logs/fit_<name>.png); its strength, speed and style are written into the rig file;
rig_to_mjcf.py generates the bag and spar models; and tools/check_self_collision.py is run on the result.
If a part touches another in the T-pose, the guard or a normal swing, the trunk is slimmed a little and
the body is built again, until nothing does (house rule: verified before training).

Then the match models: every new boxer with every other, and with each veteran (a boxer already trained,
whose own files are left alone).
"""
from __future__ import annotations

import argparse
import itertools
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "tools"))
import rig_to_mjcf  # noqa: E402
from check_self_collision import check  # noqa: E402

REFERENCE_HEIGHT = 1.81   # the height the generator's joint strengths were written for (Matt)


def build_boxer(name: str, spec: dict, models: str, rigs: str, plots: str, cubes: int) -> tuple:
    rig_path = os.path.join(rigs, f"{name}.json")
    cmd = [sys.executable, os.path.join(HERE, "glb_to_rig.py"), "--glb", os.path.normpath(os.path.join(HERE, spec["glb"])),
           "--out", rig_path, "--plot", os.path.join(plots, f"fit_{name}.png")]
    if spec.get("height"):
        cmd += ["--height", str(spec["height"])]
    if spec.get("mass"):
        cmd += ["--mass", str(spec["mass"])]
    print(f"\n==== {name}: {spec['persona']}", flush=True)
    subprocess.check_call(cmd, cwd=HERE)
    with open(rig_path, "r", encoding="utf-8") as f:
        rig = json.load(f)
    # Muscle follows cross-section, so the square of height; age takes its share off that.
    rig["strength"] = round((rig["height_m"] / REFERENCE_HEIGHT) ** 2 * float(spec.get("age", 1.0)), 3)
    rig["speed"] = float(spec.get("speed", 1.0))
    rig["style"] = spec.get("style", {})
    rig["display"] = spec.get("display", name.upper())
    rig["persona"] = spec.get("persona", "")
    rig["says"] = spec.get("says", "")

    for attempt in range(10):
        with open(rig_path, "w", encoding="utf-8") as f:
            json.dump(rig, f, indent=2)
        z, cfg = rig_to_mjcf.build_one(rig, rig_path, name, models, cubes)
        if check(name, models) == 0:
            break
        for part in ("torso", "pelvis"):
            rig["radii"][part] = round(rig["radii"][part] * 0.94, 4)
        rig.setdefault("notes", []).append(
            f"trunk slimmed to torso {rig['radii']['torso']:.3f}, pelvis {rig['radii']['pelvis']:.3f}: its own arms touched it")
        print(f"  {name}: slimming the trunk and building again (try {attempt + 2})", flush=True)
    else:
        raise SystemExit(f"{name}: its own parts still touch after ten tries; see the list above")
    print(f"  {name}: strength {rig['strength']:.2f}, speed {rig['speed']:.2f}, {cfg['total_mass_kg']:.1f} kg, "
          f"{rig['height_m']:.2f} m, radii {rig['radii']}")
    return rig, z, cfg


def veteran(name: str, models: str, rigs: str) -> tuple:
    with open(os.path.join(rigs, f"{name}.json"), "r", encoding="utf-8") as f:
        rig = json.load(f)
    with open(os.path.join(models, f"{name}_policy_config.json"), "r", encoding="utf-8") as f:
        cfg = json.load(f)
    return rig, float(cfg["stand_height"]), cfg


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", action="append", default=[])
    ap.add_argument("--roster", default=os.path.join(HERE, "roster.json"))
    ap.add_argument("--models", default=os.path.join(HERE, "models"))
    ap.add_argument("--rigs", default=os.path.join(HERE, "rigs"))
    ap.add_argument("--plots", default=os.path.join(HERE, "logs"), help="where the pictures of each fit go")
    ap.add_argument("--cubes", type=int, default=0, help="cubes in the pool of every model (rig_to_mjcf.cubes_xml)")
    ap.add_argument("--all", action="store_true", help="derive the veterans from their meshes too")
    args = ap.parse_args()
    with open(args.roster, "r", encoding="utf-8") as f:
        roster = json.load(f)
    for folder in (args.models, args.rigs, args.plots):
        os.makedirs(folder, exist_ok=True)

    veterans = roster.get("veterans", {})
    specs = dict(roster["boxers"], **(veterans if args.all else {}))
    built = {}
    for name, spec in specs.items():
        if args.only and name not in args.only:
            # Already built on an earlier call: its files are read back, not made again.
            built[name] = veteran(name, args.models, args.rigs)
        else:
            built[name] = build_boxer(name, spec, args.models, args.rigs, args.plots, args.cubes)
    old = {name: veteran(name, args.models, args.rigs) for name in veterans if name not in built}

    print("\n==== match models", flush=True)
    new = list(specs)
    pairs = list(itertools.combinations(new, 2)) + [(n, v) for n in new for v in old]
    for a, b in pairs:
        if args.only and a not in args.only and b not in args.only:
            continue
        ra, za, ca = built[a]
        rb, zb, cb = built[b] if b in built else old[b]
        rig_to_mjcf.build_versus(ra, a, za, ca, rb, b, zb, cb, args.models, args.cubes)

    print("\n==== the roster")
    for name, (rig, z, cfg) in built.items():
        print(f"  {rig.get('display', name):9s} {rig['height_m']:.2f} m  {cfg['total_mass_kg']:5.1f} kg  "
              f"strength {cfg.get('strength', 1.0):.2f}  speed {cfg.get('speed', 1.0):.2f}  {rig.get('persona', '')}")


if __name__ == "__main__":
    main()
