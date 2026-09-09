"""Read a training run's TensorBoard event files and print the metrics that
decide whether a brain ships.

WHY THIS EXISTS. mlagents-learn's stdout gives one number per summary -- mean
reward -- and mean reward is not the shipping criterion for either mini-game.
The criteria are in Reward_Locomotion's StatsRecorder keys, which only ever
reach TensorBoard, so every generation up to gen 20 was judged either by
eyeballing a TensorBoard tab or by re-measuring by hand in a separate scene.

Since gen 21 those keys are also written per body ("Locomotion/Capsule/...").
That split is the whole point: the population is three rigs training ONE shared
brain, and the standing rule is that improving Grandma and Grandpa by wrecking
the capsule is not shippable. The aggregate cannot see that happen.

Usage:
    python Tools/train_report.py boxer_locomotion21 [boxer_locomotion22 ...]
    python Tools/train_report.py --all
    python Tools/train_report.py boxer_locomotion21 --window 10
"""
import argparse
import os
import sys

from tensorboard.backend.event_processing import event_accumulator

RESULTS = "results"

# Printed in this order. The first block is the shipping criteria; the second is
# the diagnostic detail that says HOW a number was earned, which matters because
# several generations bought a headline metric by giving away a hidden one.
SHIPPING = [
    ("StepsBetweenFalls", "steps between falls  (gen20 capsule: 113)"),
    ("UprightFraction", "fraction of round upright"),
    ("BestRunDistance", "best run distance, m  (race needs 0.75)"),
    ("Alternation", "alternation  (walking, not hopping; gen18: 0.766)"),
]
DIAGNOSTIC = [
    ("StepsSurvived", "steps to FIRST fall  (gen18: 88.6)"),
    ("SingleSupportMean", "single support  (gen18: 0.691)"),
    ("ClearanceMean", "foot clearance  (gen18: 0.428)"),
    ("SpeedMatchMean", "speed match  (gen18: 0.844)"),
    ("CommandedSpeed", "commanded speed this episode"),
    ("Stumbles", "falls per episode"),
]
BODIES = ["Capsule", "Grandma", "Grandpa"]


def event_dir(run_dir):
    """Directory holding the run's tfevents file.

    mlagents-learn writes it one level down, under the BEHAVIOR name
    (results/<run-id>/Boxer/), not at the run root -- and EventAccumulator does
    not recurse, so pointing it at the run root silently reports a run with no
    metrics at all rather than an error.
    """
    for base, _dirs, files in os.walk(run_dir):
        if any(f.startswith("events.out.tfevents") for f in files):
            return base
    return None


def load(run_dir):
    """EventAccumulator over the run's event file."""
    base = event_dir(run_dir)
    if base is None:
        return None
    acc = event_accumulator.EventAccumulator(
        base, size_guidance={event_accumulator.SCALARS: 0})
    acc.Reload()
    return acc


def tail_mean(acc, tag, window):
    """Mean of the last `window` points of a scalar, and its step."""
    try:
        events = acc.Scalars(tag)
    except KeyError:
        return None, None
    if not events:
        return None, None
    tail = events[-window:]
    return sum(e.value for e in tail) / len(tail), events[-1].step


def fmt(value):
    return "       --" if value is None else f"{value:9.3f}"


def report_run(run_id, window):
    run_dir = os.path.join(RESULTS, run_id)
    if not os.path.isdir(run_dir):
        print(f"  no such run: {run_dir}")
        return
    acc = load(run_dir)
    if acc is None:
        print()
        print(f"=== {run_id} ===")
        print("  no tfevents file yet (run just started?)")
        return
    tags = set(acc.Tags().get("scalars", []))

    reward, step = tail_mean(acc, "Environment/Cumulative Reward", window)
    lesson, _ = tail_mean(acc, "Environment/Lesson Number/speed_command_max", window)
    length, _ = tail_mean(acc, "Environment/Episode Length", window)

    print(f"\n=== {run_id} ===")
    print(f"  step {step if step is not None else '?'}"
          f"   reward {fmt(reward).strip()}"
          f"   episode length {fmt(length).strip()}"
          f"   lesson {'-' if lesson is None else int(round(lesson))}"
          f"   (mean of last {window} summaries)")

    present = [b for b in BODIES if f"Locomotion/{b}/StepsBetweenFalls" in tags]
    header = "  {:<44}{:>9}".format("", "ALL")
    for body in present:
        header += "{:>9}".format(body)
    print(header)

    for group in (SHIPPING, DIAGNOSTIC):
        print("  " + "-" * (44 + 9 * (1 + len(present))))
        for key, label in group:
            value, _ = tail_mean(acc, f"Locomotion/{key}", window)
            row = "  {:<44}{}".format(label[:44], fmt(value))
            for body in present:
                body_value, _ = tail_mean(acc, f"Locomotion/{body}/{key}", window)
                row += fmt(body_value)
            print(row)

    if not present:
        print("  (no per-body stats: this run predates the body split, or the "
              "scene was not rebuilt by SceneTool_LocomotionTraining)")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("runs", nargs="*")
    parser.add_argument("--all", action="store_true", help="every run under results/")
    parser.add_argument("--window", type=int, default=5,
                        help="how many trailing summaries to average (default 5)")
    args = parser.parse_args()

    runs = args.runs
    if args.all or not runs:
        runs = sorted(d for d in os.listdir(RESULTS)
                      if os.path.isdir(os.path.join(RESULTS, d))) if os.path.isdir(RESULTS) else []
    if not runs:
        print("no runs found under results/")
        return 1
    for run_id in runs:
        report_run(run_id, args.window)
    print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
