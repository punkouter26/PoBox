"""The collision-fit check (AGENTS.md: primitives fitted inside the skinned mesh), for every boxer on the v2 line:

    python tools/fit_check.py                    writes DOCS/reports/DATE-collision-fit.html

For each boxer, glb_to_rig.py is run again on its mesh into a scratch folder (the rigs are not touched), with
--fit-report: how thick each part of the mesh is, the thin way (half of the 4th-to-96th percentile spread of
the part's own vertices across its axis; the head across its width and depth). The radius the boxer actually
trains with (rigs/v2/NAME.json, after any slimming) is put against it:

    under 100%   the capsule is inside the skin, by what is left over (the fitter aims for 92%, 90% on the trunk)
    over 100%    it is fatter than the part is thin-way, so it shows through the skin on the narrow sides

Beside it, the self-contact check (tools/check_self_collision.py: no pair of a boxer's own parts touching in
the T-pose, the guard and 23 steps of walking and punching), run now, and the fitter's picture of each mesh
with its shapes (--plot).
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ROOT = os.path.dirname(HERE)
NAMES = ("grandma", "grandpa", "kim", "matt", "nick", "sokol", "trump", "zombie")


def main() -> None:
    date = time.strftime("%Y-%m-%d")
    with open(os.path.join(HERE, "roster.json"), "r", encoding="utf-8") as f:
        roster = json.load(f)
    specs = dict(roster["boxers"], **roster["veterans"])
    img_dir = os.path.join(ROOT, "DOCS", "reports", "img")
    os.makedirs(img_dir, exist_ok=True)
    rows = {}
    with tempfile.TemporaryDirectory() as tmp:
        for n in NAMES:
            spec = specs.get(n)
            if spec is None:
                print(f"{n}: not in roster.json")
                continue
            report = os.path.join(tmp, f"{n}_fit.json")
            cmd = [sys.executable, os.path.join(HERE, "glb_to_rig.py"), "--glb", os.path.normpath(os.path.join(HERE, spec["glb"])),
                   "--out", os.path.join(tmp, f"{n}.json"), "--plot", os.path.join(img_dir, f"{date}_fit_{n}.png"), "--fit-report", report]
            if spec.get("height"):
                cmd += ["--height", str(spec["height"])]
            if spec.get("mass"):
                cmd += ["--mass", str(spec["mass"])]
            subprocess.run(cmd, cwd=HERE, check=True, stdout=subprocess.DEVNULL)
            with open(report, "r", encoding="utf-8") as f:
                thick = json.load(f)["half_thickness"]
            with open(os.path.join(HERE, "rigs", "v2", f"{n}.json"), "r", encoding="utf-8") as f:
                final = json.load(f)["radii"]
            rows[n] = {k: (final[k], thick[k], final[k] / thick[k]) for k in final if k in thick and thick[k] > 0}
            worst = max(rows[n].items(), key=lambda kv: kv[1][2])
            print(f"{n:8s} fullest: {worst[0]} at {worst[1][2]:.0%} of its thickness", flush=True)

    env = dict(os.environ, PYTHONPATH=os.path.join(HERE, ".mj350"))
    self_check = subprocess.run([sys.executable, os.path.join(HERE, "tools", "check_self_collision.py"), "--models", os.path.join(HERE, "models", "v2")]
                                + [a for n in NAMES for a in ("--name", n)], cwd=HERE, env=env, capture_output=True, text=True)
    write(date, rows, self_check.stdout + self_check.stderr, self_check.returncode)


def write(date: str, rows: dict, self_text: str, self_code: int) -> None:
    import html
    parts = ("head", "torso", "pelvis", "upper_arm", "forearm", "thigh", "shin")
    head = "".join(f"<th>{p.replace('_', ' ')}</th>" for p in parts)
    body = []
    for n, r in rows.items():
        cells = []
        for p in parts:
            if p not in r:
                cells.append("<td>-</td>")
                continue
            radius, thick, ratio = r[p]
            cls = "in" if ratio <= 0.95 else "edge" if ratio <= 1.0 else "out"
            cells.append(f'<td class="{cls}">{ratio:.0%}<small>{radius * 100:.1f} / {thick * 100:.1f} cm</small></td>')
        body.append(f"<tr><th>{n.upper()}</th>{''.join(cells)}</tr>")
    pics = "".join(f'<figure><img src="img/{date}_fit_{n}.png" alt="{n} mesh with its fitted shapes"><figcaption>{n.upper()}</figcaption></figure>' for n in rows)
    page = f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Collision fit</title>
<style>
  body {{ margin: 0; background: #0b0d12; color: #eef2f7; font: 15px/1.5 system-ui, Segoe UI, sans-serif; }}
  main {{ max-width: 1100px; margin: 0 auto; padding: 24px 16px 64px; }}
  p, figcaption {{ color: #a8b2c0; }}
  .wrap {{ overflow-x: auto; }}
  table {{ width: 100%; border-collapse: collapse; background: #141821; border-radius: 8px; overflow: hidden; }}
  th, td {{ text-align: left; padding: 6px 10px; border-bottom: 1px solid rgba(255,255,255,.08); font-variant-numeric: tabular-nums; }}
  td small {{ display: block; color: #a8b2c0; font-size: 12px; }}
  .in {{ color: #2ebf8c; font-weight: 700; }} .edge {{ color: #ffc654; font-weight: 700; }} .out {{ color: #ff6c5c; font-weight: 700; }}
  pre {{ background: #141821; padding: 12px; border-radius: 8px; white-space: pre-wrap; font-size: 13px; }}
  .pics {{ display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 12px; }}
  figure {{ margin: 0; background: #141821; border-radius: 8px; overflow: hidden; }} figure img {{ width: 100%; display: block; }}
  figcaption {{ padding: 4px 10px; }}
</style></head><body><main>
<h1>Collision fit, {date}</h1>
<p>Each boxer's capsule radius (what it trains with, rigs/v2) as a share of how thick that part of its mesh is the thin way
(training/tools/fit_check.py, from glb_to_rig.py's own measure). Green: inside the skin with at least 5% to spare. Amber: within 5%
of the surface. Red: thicker than the part, so the shape shows through the skin on its narrow sides. The fitter aims for 92% on a limb
and 90% on the trunk; a radius clamped to the part's lower bound, or a head (a sphere on a head that is longer than it is wide), can
come out over.</p>
<div class="wrap"><table><tr><th></th>{head}</tr>{''.join(body)}</table></div>
<h2>Self-contact ({'pass' if self_code == 0 else 'FAIL'}, MuJoCo 3.5.0)</h2>
<pre>{html.escape(self_text.strip())}</pre>
<h2>The meshes with their fitted shapes (the fitter's radii, before any slimming)</h2>
<div class="pics">{pics}</div>
</main></body></html>"""
    out = os.path.join(ROOT, "DOCS", "reports", f"{date}-collision-fit.html")
    with open(out, "w", encoding="utf-8") as f:
        f.write(page)
    print(f"self-contact {'pass' if self_code == 0 else 'FAIL'}; written {os.path.relpath(out, ROOT)}")


if __name__ == "__main__":
    main()
