"""Compare the training configs of several runs, parameter by parameter.

Written for the 2026-09-15 question: Nick's `nick_lB_torque` produced a policy
that stands for the full 30 s in 90 % of worlds, while Grandma's and Grandpa's
runs landed around 20 %. If the recipe differs, this shows where.

Usage: python compare_run_configs.py nick_lB_torque gma_lD_torque ...
"""
import itertools
import json
import pathlib
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
BASE = REPO / "results" / "nick"


def main() -> int:
    runs = sys.argv[1:] or ["nick_lB_torque", "gma_lD_torque"]
    configs = {}
    for run in runs:
        path = BASE / run / "config.json"
        if not path.exists():
            print("missing: %s" % path)
            continue
        configs[run] = json.loads(path.read_text())

    width = 30
    print("%-*s" % (width, "env key") + "".join("%18s" % r for r in configs))
    keys = sorted(set(itertools.chain.from_iterable(c.get("env", {}) for c in configs.values())))
    for key in keys:
        cells = []
        for run in configs:
            value = configs[run].get("env", {}).get(key, "-")
            if isinstance(value, float):
                value = round(value, 5)
            cells.append(str(value))
        if len(set(cells)) > 1:
            print("%-*s" % (width, key) + "".join("%18s" % c for c in cells))

    print()
    for key in sorted(set(itertools.chain.from_iterable(configs.values())) - {"env"}):
        row = {run: configs[run].get(key) for run in configs}
        if len(set(map(str, row.values()))) > 1:
            print(key, row)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
