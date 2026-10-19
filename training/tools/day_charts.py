"""The day report's three charts (2026-10-19), drawn from the runs' CSVs (the same numbers TensorBoard shows under env/),
in TensorBoard's look: one line per run, smoothed 0.6, against iteration.

    python tools/day_charts.py OUT_DIR
"""
from __future__ import annotations

import csv
import os
import sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
COLORS = ["#ff7043", "#0077bb", "#009988", "#ee3377", "#33bbee", "#cc3311", "#bbbbbb", "#ee7733", "#555555"]


def series(run: str, key: str):
    path = os.path.join(HERE, "logs", f"{run}.csv")
    if not os.path.exists(path):
        return [], []
    with open(path, encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    xs, ys = [], []
    for r in rows:
        try:
            xs.append(int(r["iter"]))
            ys.append(float(r[key]))
        except (KeyError, ValueError):
            pass
    return xs, ys


def smooth(ys, w=0.6):
    out, last = [], None
    for y in ys:
        last = y if last is None else last * w + y * (1 - w)
        out.append(last)
    return out


def chart(path: str, title: str, key: str, runs, ylabel: str) -> None:
    fig, ax = plt.subplots(figsize=(9, 4.2), dpi=120)
    for i, run in enumerate(runs):
        xs, ys = series(run, key)
        if not xs:
            continue
        x0 = xs[0]
        c = COLORS[i % len(COLORS)]
        ax.plot([x - x0 for x in xs], ys, color=c, alpha=0.18, lw=1)
        ax.plot([x - x0 for x in xs], smooth(ys), color=c, lw=1.8, label=run)
    ax.set_title(title, loc="left", fontsize=11)
    ax.set_xlabel("iteration of the run")
    ax.set_ylabel(ylabel)
    ax.grid(color="#e0e0e0", lw=0.6)
    for s in ("top", "right"):
        ax.spines[s].set_visible(False)
    ax.legend(fontsize=8, ncol=3, frameon=False)
    fig.tight_layout()
    fig.savefig(path)
    plt.close(fig)
    print("wrote", path)


def main() -> None:
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    m14 = ["m14_grandpa", "m14_kim", "m14_grandma", "m14_sokol", "m14_matt"]
    chart(os.path.join(out, "tb_hits.png"), "env/hits_per_s: punches landed a second, the m14 runs", "hits_per_s", m14, "landed / s")
    chart(os.path.join(out, "tb_head.png"), "env/head_share: share of landed punches to the head, Matt and Sokol (m13 against m14)", "head_share",
          ["m13b_matt", "m14_matt", "m13_sokol", "m14_sokol"], "head share")
    mel = [f"mel1_{n}" for n in ("zombie", "nick", "trump", "grandpa", "kim", "grandma", "sokol", "matt")]
    chart(os.path.join(out, "tb_falls.png"), "env/falls: share of episodes ending in the learner's fall, the Melee runs (2 bodies down)", "falls", mel, "falls / episode")


if __name__ == "__main__":
    main()
