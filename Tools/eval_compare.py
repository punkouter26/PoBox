"""Print eval reports from Systems_EvalHarness side by side.

One column per brain, one block per body, so the question a promotion actually
turns on -- "does this brain beat the one it would replace, on EVERY body" --
is answerable by reading across a row.

Usage:
    python Tools/eval_compare.py eval/gen20_speed0.json eval/gen21_speed0.json
    python Tools/eval_compare.py eval/*.json
"""
import glob
import json
import os
import sys

# (json field, label, higher_is_better)
ROWS = [
    ("stepsBetweenFalls", "steps between falls", True),
    ("bestRunDistance", "best run distance (m)", True),
    ("uprightFraction", "upright fraction", True),
    ("stumbles", "falls per episode", False),
    ("stepsSurvived", "steps to first fall", True),
    ("alternation", "alternation", True),
    ("clearanceMean", "foot clearance", True),
    ("singleSupportMean", "single support", True),
    ("speedMatchMean", "speed match", True),
    ("footLeftGrounded", "left foot grounded", True),
    ("footRightGrounded", "right foot grounded", True),
    ("footLeftLift", "left foot lift (m)", True),
    ("footRightLift", "right foot lift (m)", True),
]
BODY_ORDER = ["ALL", "Capsule", "Grandma", "Grandpa", "Unlabelled"]


def load(path):
    with open(path, encoding="utf-8") as handle:
        report = json.load(handle)
    report["_path"] = path
    report["_bodies"] = {b["body"]: b for b in report["bodies"]}
    return report


def column_name(report):
    name = report.get("brain") or os.path.basename(report["_path"])
    speed = report.get("speedCommandMax", 0.0)
    return f"{name}@{speed:g}"


def main():
    paths = []
    for pattern in sys.argv[1:]:
        paths.extend(sorted(glob.glob(pattern)) or [pattern])
    if not paths:
        print(__doc__)
        return 1

    reports = []
    for path in paths:
        if not os.path.isfile(path):
            print(f"missing: {path}")
            continue
        reports.append(load(path))
    if not reports:
        return 1

    names = [column_name(r) for r in reports]
    width = max(14, max(len(n) for n in names) + 2)

    print()
    header = "{:<24}".format("") + "".join(f"{n:>{width}}" for n in names)
    print(header)
    print("  episodes / body        " + "".join(
        f"{sum(b['episodes'] for b in r['bodies'] if b['body'] != 'ALL'):>{width}}" for r in reports))

    bodies = []
    for body in BODY_ORDER:
        if any(body in r["_bodies"] for r in reports):
            bodies.append(body)

    for body in bodies:
        print()
        print(f"  --- {body} ---")
        for key, label, higher_better in ROWS:
            values = []
            for report in reports:
                entry = report["_bodies"].get(body)
                values.append(None if entry is None else entry.get(key))
            present = [v for v in values if v is not None]
            best = (max(present) if higher_better else min(present)) if present else None
            row = "  {:<22}".format(label)
            for value in values:
                if value is None:
                    row += f"{'--':>{width}}"
                else:
                    mark = " *" if best is not None and value == best and len(present) > 1 else "  "
                    row += f"{value:>{width - 2}.3f}{mark}"
            print(row)
    print()
    print("  * = best in row. 'falls per episode' is scored lower-is-better;")
    print("  every other row is higher-is-better.")
    print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
