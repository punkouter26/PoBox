"""Elo ladder over every brain this project has measured.

WHAT A DUEL IS HERE, AND WHAT IT IS NOT. These are METRIC DUELS, not ring
fights. Two brains never meet: Systems_EvalHarness measures each one alone, in
the same scene, on the same bodies, at the same commanded speed, for the same
number of episodes. A duel is one of those like-for-like comparisons resolved
-- "gen25 beat gen20 on the Capsule's steps-between-falls at speed 0 with shove
on" -- and the ladder is the accumulation of them. A rating gap is a statement
about how CONSISTENTLY one brain outmeasures another across bodies and
criteria. It is not a win probability in a contest.

WHY THAT IS STILL WORTH HAVING. Every promotion in this project's history has
been argued from one number in one condition, and the table that number came
from is thrown away the moment the next run starts. eval_compare.py shows two
reports side by side and nothing remembers the comparison. This does: the duels
are appended to a log and the ratings are recomputed from the whole log every
time, so gen18 is still on the board months after the last time anyone ran it,
and a new checkpoint arrives into a field rather than into an empty room.

THE AGGREGATE ROW IS EXCLUDED ON PURPOSE. CLAUDE.md records that a single mean
cannot see "improved the characters by wrecking the capsule", and that an ALL
column matching the Capsule column exactly is the signature of the character
rigs contributing nothing. So every duel is fought per BODY, and a brain that
wins on the capsule while losing on Grandma and Grandpa comes out of this
behind -- which is the ordering the project's own shipping rule already implies.

METRICS DEPEND ON THE COMMANDED SPEED, because the shipping criteria do. At
speed 0 the question is standing; alternation and foot clearance are noise from
a policy that is supposed to hold still. Above 0 the question is walking, and
the gait terms are most of the answer.

DETERMINISTIC BY CONSTRUCTION. Elo is order-dependent, so the ratings are
recomputed from the entire log in a fixed sort order rather than updated in
place. Re-running this on the same reports changes nothing: a duel's id is a
hash of the two reports' CONTENT, so ingesting the same file twice is a no-op
while a genuine re-measurement (different numbers, same brain) is a new duel.

Usage:
    python Tools/ladder.py eval/*.json      # ingest those reports, then print
    python Tools/ladder.py                  # just print the standing ladder
    python Tools/ladder.py --json           # machine-readable, for the dossiers
"""
import glob
import hashlib
import json
import os
import sys
from datetime import date

LADDER_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ladder")
DUELS_PATH = os.path.join(LADDER_DIR, "duels.jsonl")
LADDER_PATH = os.path.join(LADDER_DIR, "ladder.json")

# (json field, higher_is_better). Mirrors eval_compare.py's ROWS, split by the
# question the commanded speed is asking.
BALANCE_METRICS = [
    ("stepsBetweenFalls", True),
    ("uprightFraction", True),
    ("stepsSurvived", True),
    ("stumbles", False),
]
WALK_METRICS = [
    ("bestRunDistance", True),
    ("alternation", True),
    ("speedMatchMean", True),
    ("clearanceMean", True),
    ("singleSupportMean", True),
    ("stepsBetweenFalls", True),
    ("uprightFraction", True),
]

# Rated a draw when the two values are within this fraction of the larger one.
# Without it, floating-point noise on a metric both brains have saturated (an
# upright fraction of 0.9987 against 0.9986) is scored as a win.
DRAW_MARGIN = 0.02

# Low K because one meet produces many CORRELATED duels -- three bodies times
# four metrics is twelve results from a single pair of measurements, and at a
# tournament K the first meet would decide the ladder outright.
K_FACTOR = 12.0
START_RATING = 1500.0


def condition_of(report):
    """The comparison a report belongs to. Only brains measured the same way duel."""
    speed = report.get("speedCommandMax", 0.0)
    shove = "+shove" if report.get("shove") else ""
    scene = report.get("scene", "?")
    return f"{scene}@speed{speed:g}{shove}"


def metrics_for(report):
    return WALK_METRICS if report.get("speedCommandMax", 0.0) > 0.0 else BALANCE_METRICS


def load_report(path):
    with open(path, encoding="utf-8") as handle:
        raw = handle.read()
    report = json.loads(raw)
    if not report.get("brain") or not report.get("bodies"):
        return None
    # Hashed from the CONTENT, not the path: eval_candidates.ps1 overwrites
    # eval/<brain>_speed0.json on every run, so the filename is the same for a
    # measurement taken today and one taken in August.
    report["_hash"] = hashlib.sha1(raw.encode("utf-8")).hexdigest()[:12]
    report["_bodies"] = {
        body["body"]: body
        for body in report["bodies"]
        # ALL is excluded -- see the module docstring. A body with no completed
        # episodes is excluded because its metrics are all zero, which would
        # score as a loss on every row rather than as the absence it is.
        if body["body"] != "ALL" and body.get("episodes", 0) > 0
    }
    return report


def duels_between(left, right):
    """Every resolved comparison between two reports measured the same way."""
    if condition_of(left) != condition_of(right):
        return []
    # OBSERVATION WIDTH IS PART OF "MEASURED THE SAME WAY", and it is the one
    # part that is invisible in the numbers. The width comes from the model's
    # own obs_0 input (Systems_BrainCompatibility, via the Inference Engine),
    # and a brain of a different width was measured on fighters configured with
    # a different sensor -- so the two reports are not like-for-like however
    # identical their scene, speed and episode count look. The eval harness
    # refuses a mismatched brain at measurement time; nothing until now stopped
    # a LADDER from quietly putting a 121-observation generation and a
    # 127-observation one on the same board months apart.
    width_a = left.get("observationWidth", -1)
    width_b = right.get("observationWidth", -1)
    if width_a >= 0 and width_b >= 0 and width_a != width_b:
        print(f"  skipped {left['brain']} vs {right['brain']}: "
              f"{width_a} observations against {width_b} -- different bodies, not a duel",
              file=sys.stderr)
        return []
    # Ordered by brain name so the same pair always produces the same duel id
    # regardless of the order the files arrived in.
    if left["brain"] > right["brain"]:
        left, right = right, left
    if left["brain"] == right["brain"]:
        return []

    condition = condition_of(left)
    out = []
    for body in sorted(set(left["_bodies"]) & set(right["_bodies"])):
        for field, higher_better in metrics_for(left):
            value_a = left["_bodies"][body].get(field)
            value_b = right["_bodies"][body].get(field)
            if value_a is None or value_b is None:
                continue
            scale = max(abs(value_a), abs(value_b))
            if scale == 0.0 or abs(value_a - value_b) <= DRAW_MARGIN * scale:
                score_a = 0.5
            elif (value_a > value_b) == higher_better:
                score_a = 1.0
            else:
                score_a = 0.0
            key = f"{left['_hash']}:{right['_hash']}:{condition}:{body}:{field}"
            out.append({
                "id": hashlib.sha1(key.encode("utf-8")).hexdigest()[:16],
                "a": left["brain"],
                "b": right["brain"],
                "score_a": score_a,
                "condition": condition,
                "body": body,
                "metric": field,
                "va": round(float(value_a), 6),
                "vb": round(float(value_b), 6),
                "obs": width_a,
                "recorded": date.today().isoformat(),
            })
    return out


def read_log():
    if not os.path.isfile(DUELS_PATH):
        return []
    duels = []
    with open(DUELS_PATH, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                duels.append(json.loads(line))
    return duels


def append_log(new_duels):
    os.makedirs(LADDER_DIR, exist_ok=True)
    with open(DUELS_PATH, "a", encoding="utf-8") as handle:
        for duel in new_duels:
            handle.write(json.dumps(duel, sort_keys=True) + "\n")


def rate(duels):
    """Ratings recomputed from scratch, in a fixed order. See the docstring."""
    ratings = {}
    record = {}
    for duel in sorted(duels, key=lambda d: d["id"]):
        for brain in (duel["a"], duel["b"]):
            ratings.setdefault(brain, START_RATING)
            record.setdefault(brain, {
                "wins": 0, "losses": 0, "draws": 0,
                "bodies": set(), "conditions": set(), "widths": set(),
            })
        rating_a = ratings[duel["a"]]
        rating_b = ratings[duel["b"]]
        expected_a = 1.0 / (1.0 + 10.0 ** ((rating_b - rating_a) / 400.0))
        score_a = duel["score_a"]
        ratings[duel["a"]] = rating_a + K_FACTOR * (score_a - expected_a)
        ratings[duel["b"]] = rating_b + K_FACTOR * ((1.0 - score_a) - (1.0 - expected_a))

        if score_a == 0.5:
            record[duel["a"]]["draws"] += 1
            record[duel["b"]]["draws"] += 1
        else:
            winner, loser = (duel["a"], duel["b"]) if score_a == 1.0 else (duel["b"], duel["a"])
            record[winner]["wins"] += 1
            record[loser]["losses"] += 1
        for brain in (duel["a"], duel["b"]):
            record[brain]["bodies"].add(duel["body"])
            record[brain]["conditions"].add(duel["condition"])
            if duel.get("obs", -1) >= 0:
                record[brain]["widths"].add(duel["obs"])

    table = []
    for brain, rating in ratings.items():
        entry = record[brain]
        widths = sorted(entry["widths"])
        table.append({
            "brain": brain,
            "elo": round(rating, 1),
            "wins": entry["wins"],
            "losses": entry["losses"],
            "draws": entry["draws"],
            "duels": entry["wins"] + entry["losses"] + entry["draws"],
            "bodies": sorted(entry["bodies"]),
            "conditions": sorted(entry["conditions"]),
            # -1 for the heuristic bot, which reads no model at all, and for a
            # brain whose duels disagree about its width -- which would mean the
            # folder it was measured from stopped containing the same thing.
            "observationWidth": widths[0] if len(widths) == 1 else -1,
        })
    # Ties broken by name so the printed order and the ranks written into the
    # dossiers cannot shuffle between runs.
    table.sort(key=lambda row: (-row["elo"], row["brain"]))
    for rank, row in enumerate(table, start=1):
        row["rank"] = rank
        row["entrants"] = len(table)
    return table


def write_ladder(table, duel_count):
    os.makedirs(LADDER_DIR, exist_ok=True)
    payload = {
        "generated": date.today().isoformat(),
        "duels": duel_count,
        "note": "metric duels from Systems_EvalHarness reports, not ring fights",
        "brains": table,
    }
    with open(LADDER_PATH, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, indent=2)
    return payload


def print_table(table, duel_count):
    if not table:
        print("\n  ladder is empty -- run eval_candidates.ps1, then "
              "python Tools/ladder.py eval/*.json\n")
        return
    width = max(18, max(len(row["brain"]) for row in table) + 2)
    print()
    print(f"  {'#':>2}  {'brain':<{width}}{'elo':>8}{'W':>6}{'L':>6}{'D':>6}  bodies")
    print("  " + "-" * (width + 32))
    for row in table:
        print(f"  {row['rank']:>2}  {row['brain']:<{width}}{row['elo']:>8.1f}"
              f"{row['wins']:>6}{row['losses']:>6}{row['draws']:>6}  "
              f"{', '.join(row['bodies'])}")
    print()
    print(f"  {duel_count} duels on the board. A duel is one metric, on one body, in one")
    print("  condition -- not a fight. Ratings are recomputed from the whole log.")
    print()


def main():
    args = [a for a in sys.argv[1:] if a != "--json"]
    as_json = "--json" in sys.argv[1:]

    paths = []
    for pattern in args:
        paths.extend(sorted(glob.glob(pattern)) or [pattern])

    duels = read_log()
    known = {duel["id"] for duel in duels}

    if paths:
        reports = []
        for path in paths:
            if not os.path.isfile(path):
                print(f"  missing: {path}", file=sys.stderr)
                continue
            report = load_report(path)
            if report is None:
                print(f"  not an eval report: {path}", file=sys.stderr)
                continue
            reports.append(report)

        fresh = []
        for i in range(len(reports)):
            for j in range(i + 1, len(reports)):
                for duel in duels_between(reports[i], reports[j]):
                    if duel["id"] not in known:
                        known.add(duel["id"])
                        fresh.append(duel)
        if fresh:
            append_log(fresh)
            duels.extend(fresh)
        if not as_json:
            print(f"  ingested {len(reports)} report(s), {len(fresh)} new duel(s)")

    table = rate(duels)
    payload = write_ladder(table, len(duels))
    if as_json:
        print(json.dumps(payload, indent=2))
    else:
        print_table(table, len(duels))
        print(f"  wrote {os.path.relpath(LADDER_PATH)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
