"""Self-check of tools/elo.py's scoring and rating fit (no MuJoCo run):  python tools/test_elo.py"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import elo  # noqa: E402


def r(hits, speed, downs):
    return dict(hits=hits, speed=speed, falls_hit=downs)


# The ten-point must: more punch speed 10-9, level within 4% 10-10, a knockdown costs a point and gives the round.
assert elo.card([r(5, 30.0, 0), r(4, 20.0, 0)]) == (10, 9)
assert elo.card([r(5, 30.0, 0), r(5, 30.5, 0)]) == (10, 10)
assert elo.card([r(9, 90.0, 1), r(2, 10.0, 0)]) == (8, 10)          # 10-8, as Judges.cs scores it

bout = lambda a, b, res: dict(red=a, blue=b, result=res)
# A beats everyone, C loses to everyone; B and D draw all theirs.
results = [bout("a", "b", 1.0)] * 4 + [bout("a", "c", 1.0)] * 4 + [bout("b", "c", 1.0)] * 4 + [bout("b", "d", 0.5)] * 4 + [bout("d", "c", 1.0)] * 4
rt = elo.bradley_terry(["a", "b", "c", "d"], results)
assert rt["a"] > rt["b"] > rt["c"] and rt["a"] > rt["d"] > rt["c"], rt
assert abs(sum(rt.values()) / 4 - 1000.0) < 1e-6
assert abs(elo.bradley_terry(["x", "y"], [bout("x", "y", 0.5)] * 6)["x"] - 1000.0) < 1e-6
print("elo: all checks pass", {k: round(v) for k, v in rt.items()})
