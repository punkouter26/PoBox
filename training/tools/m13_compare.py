"""Tonight's match brains against the game's: per boxer, the exam's lines (logs/exam_m13_NAME.json, NAME's m13 against the
game's roster) against the shipped brain's (logs/exam_shipped_1018.json, the same roster and rules). Exports
checkpoints/m13_NAME/latest_NAME.onnx (103 inputs, one observation, as train_gauntlet.py writes a match brain) only where
the m13 passes at least as many lines as the shipped brain. Promoting is left to the editor.

    python tools/m13_compare.py [--only kim matt] [--prefix m13]
"""
from __future__ import annotations

import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from ppo import PPO, PPOConfig, export_onnx  # noqa: E402

NAMES = ("matt", "zombie", "nick", "sokol", "kim", "trump", "grandma", "grandpa")
BOX = ("footing", "attack", "guard", "chin")      # the match brain's own lines (getting up is the get-up brain's)


def lines(path: str, name: str):
    with open(path, encoding="utf-8") as f:
        row = json.load(f)["boxers"][name]
    return {k: v for k, v in row["lines"].items() if k in BOX}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", nargs="*", default=list(NAMES))
    ap.add_argument("--prefix", default="m13")
    ap.add_argument("--base", default="logs/exam_shipped_1018.json")
    a = ap.parse_args()
    out = {}
    for n in a.only:
        new_exam = os.path.join(HERE, "logs", f"exam_{a.prefix}_{n}.json")
        ck = os.path.join(HERE, "checkpoints", f"{a.prefix}_{n}", f"latest_{n}.pt")
        if not (os.path.exists(new_exam) and os.path.exists(ck)):
            print(f"{n}: no {a.prefix} exam or checkpoint")
            continue
        old, new = lines(os.path.join(HERE, a.base), n), lines(new_exam, n)
        p_old = sum(1 for v in old.values() if v["ok"])
        p_new = sum(1 for v in new.values() if v["ok"])
        ok = p_new >= p_old
        row = {"shipped": p_old, a.prefix: p_new, "export": ok, "lines": {k: [old[k]["text"], new[k]["text"], old[k]["ok"], new[k]["ok"]] for k in new}}
        if ok:
            ppo = PPO(103, 21, 1, "cpu", PPOConfig())
            ppo.load(ck)
            path = os.path.join(os.path.dirname(ck), f"latest_{n}.onnx")
            export_onnx(ppo, path, 103, fixed_batch=True)
            row["onnx"] = os.path.relpath(path, HERE)
        out[n] = row
        print(f"{n}: lines shipped {p_old} -> {a.prefix} {p_new}: {'exported ' + row['onnx'] if ok else 'not exported (worse)'}")
        for k, (t0, t1, o0, o1) in row["lines"].items():
            print(f"    {k:8s} {'pass' if o0 else 'FAIL' if o0 is False else ' -  '} {t0}\n             -> {'pass' if o1 else 'FAIL' if o1 is False else ' -  '} {t1}")
    with open(os.path.join(HERE, "logs", f"{a.prefix}_compare.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, indent=1)


if __name__ == "__main__":
    main()
