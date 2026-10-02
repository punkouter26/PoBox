"""Put the policies in training/handoff/ back where the trainers and the exam look for them.

    python tools/restore_handoff.py

training/checkpoints and training/logs are not in git; training/handoff/ is. After a fresh clone this
copies each boxer's boxing policy to checkpoints/handoff/latest_NAME.pt, its get-up policy to
checkpoints/getup_handoff/latest_NAME.pt, the hand-over banks to logs/, and writes logs/policies.json so
tools/exam.py finds them. Nothing is exported to the game: see training/handoff/README.md.
"""
from __future__ import annotations

import json
import os
import shutil

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(HERE, "handoff")


def main() -> None:
    with open(os.path.join(SRC, "policies.json"), "r", encoding="utf-8") as f:
        book = json.load(f)
    out = {}
    os.makedirs(os.path.join(HERE, "logs"), exist_ok=True)
    for name in sorted(book):
        out[name] = {}
        for kind, folder in (("match", "handoff"), ("getup", "getup_handoff")):
            src = os.path.join(SRC, f"{kind}_{name}.pt")
            if not os.path.exists(src):
                continue
            dst = os.path.join(HERE, "checkpoints", folder, f"latest_{name}.pt")
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copyfile(src, dst)
            out[name][kind] = f"checkpoints/{folder}/latest_{name}.pt"
        bank = os.path.join(SRC, f"handover_{name}.npz")
        if os.path.exists(bank):
            shutil.copyfile(bank, os.path.join(HERE, "logs", f"handover_{name}.npz"))
        print(f"{name}: " + ", ".join(f"{k} -> {v}" for k, v in out[name].items()))
    with open(os.path.join(HERE, "logs", "policies.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, indent=1)
    print("wrote logs/policies.json; run tools/exam.py to see where each boxer stands")


if __name__ == "__main__":
    main()
