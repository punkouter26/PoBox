"""Read the skeleton out of a rigged .glb and write the rig description the MJCF generator takes.

    python glb_to_rig.py --glb ../Assets/Models/Fighter.glb --out rigs/fighter.json --mass 75

House rule: the fighter's rig comes from the owner's skinned mesh, not from an invented skeleton. This
is the step that reads it. No Blender needed for .glb: a glTF file is JSON plus a binary blob, and the
skeleton is entirely in the JSON (node transforms and the skin's joint list).

Output frame is the one `rig_to_mjcf.py` expects (and PoDecath's rigs use): Blender Z-up, +x = the
character's left, -y = forward. glTF is Y-up with +z forward, so   blender = (x, -z, y).

Two things are normalised on the way through, and both are reported:
  * scale, so the figure is `--height` metres tall if the file is in centimetres or some other unit;
  * the arms, to a T-pose. The generator's shoulder and elbow axes assume the arm lies along the
    shoulder line at rest; an A-pose rig would put every arm joint's zero in the wrong place. Segment
    lengths are kept, only the direction is straightened.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import struct
from typing import Dict, List, Optional

import numpy as np


def read_glb(path: str) -> dict:
    with open(path, "rb") as f:
        data = f.read()
    magic, version, _length = struct.unpack_from("<4sII", data, 0)
    if magic != b"glTF":
        raise ValueError(f"{path} is not a binary glTF (.glb) file")
    offset = 12
    while offset < len(data):
        chunk_len, chunk_type = struct.unpack_from("<I4s", data, offset)
        offset += 8
        if chunk_type == b"JSON":
            return json.loads(data[offset:offset + chunk_len].decode("utf-8"))
        offset += chunk_len
    raise ValueError(f"{path} has no JSON chunk")


def quat_to_mat(q) -> np.ndarray:
    x, y, z, w = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def local_matrix(node: dict) -> np.ndarray:
    if "matrix" in node:
        return np.array(node["matrix"], dtype=float).reshape(4, 4).T   # glTF is column-major
    m = np.eye(4)
    r = quat_to_mat(node.get("rotation", [0, 0, 0, 1]))
    s = np.array(node.get("scale", [1, 1, 1]), dtype=float)
    m[:3, :3] = r * s
    m[:3, 3] = node.get("translation", [0, 0, 0])
    return m


def world_matrices(gltf: dict) -> List[np.ndarray]:
    nodes = gltf["nodes"]
    parent = [-1] * len(nodes)
    for i, n in enumerate(nodes):
        for c in n.get("children", []):
            parent[c] = i
    world: List[Optional[np.ndarray]] = [None] * len(nodes)

    def solve(i: int) -> np.ndarray:
        if world[i] is None:
            m = local_matrix(nodes[i])
            world[i] = m if parent[i] < 0 else solve(parent[i]) @ m
        return world[i]

    return [solve(i) for i in range(len(nodes))]


def clean(name: str) -> str:
    n = name.lower()
    n = re.sub(r"^(mixamorig\d*[:_]?|cc_base_|bip0?1[_ ]?|def[-_]|rig[:_])", "", n)
    return re.sub(r"[^a-z0-9]", "", n)


def side_of(name: str) -> str:
    raw = name.lower()
    c = clean(name)
    if c.startswith("left") or re.search(r"(^|[_.\- ])l([_.\- ]|$)", raw) or raw.endswith((".l", "_l")) or c.startswith("l") and c[1:2].isupper():
        return "l"
    if c.startswith("right") or re.search(r"(^|[_.\- ])r([_.\- ]|$)", raw) or raw.endswith((".r", "_r")):
        return "r"
    return ""


def strip_side(c: str) -> str:
    for p in ("left", "right"):
        if c.startswith(p):
            return c[len(p):]
    return re.sub(r"^(l|r)(?=(thigh|calf|foot|toe|upperarm|forearm|hand|clavicle|shoulder|arm|leg|upleg))", "", c)


# canonical key -> acceptable (side-stripped, cleaned) bone names, most specific first
PATTERNS = {
    "pelvis": ["hips", "pelvis", "hip", "root"],
    "neck": ["neck", "neck1", "necktwist01"],
    "head": ["head"],
    "thigh": ["upleg", "thigh", "upperleg"],
    "shin": ["leg", "calf", "shin", "lowerleg"],
    "foot": ["foot"],
    "toe": ["toebase", "toe", "ball", "toes"],
    "upper_arm": ["arm", "upperarm"],
    "forearm": ["forearm", "lowerarm"],
    "hand": ["hand"],
    "hand_tip": ["handmiddle3", "middle3", "handmiddle2", "middle2", "handmiddle1", "middle1", "middle03", "middle02", "mid3"],
}


def build_map(bones: List[dict]) -> Dict[str, str]:
    by_key: Dict[str, str] = {}
    cleaned = [(b["name"], strip_side(clean(b["name"])), side_of(b["name"])) for b in bones]

    def find(key: str, side: str) -> Optional[str]:
        for want in PATTERNS[key]:
            for name, c, s in cleaned:
                if c == want and s == side:
                    return name
        return None

    for key in ("pelvis", "neck", "head"):
        hit = find(key, "")
        if hit:
            by_key[key] = hit
    # Lowest spine bone and highest, by where they are rather than by how they are numbered: rigs count
    # the spine upwards, downwards, or call the top one just "Spine".
    height_of = {b["name"]: float(b["head"][2]) for b in bones}
    spines = sorted((name for name, c, s in cleaned
                     if re.fullmatch(r"(spine|chest|upperchest|waist)\d*", c) and s == ""),
                    key=lambda n: height_of[n])
    if spines:
        by_key["spine"] = spines[0]
        by_key["torso"] = spines[-1]
    for side in ("l", "r"):
        for key in ("thigh", "shin", "foot", "toe", "upper_arm", "forearm", "hand", "hand_tip"):
            hit = find(key, side)
            if hit:
                by_key[f"{key}_{side}"] = hit
    return by_key


# How far out to the side, as a fraction of height, a limb hanging off the spine has to reach to be an
# arm. Low enough to catch arms that rest by the sides; a neck reaches nowhere.
ARM_REACH = 0.10


def build_map_by_shape(bones: List[dict]) -> Dict[str, str]:
    """The same mapping worked out from the skeleton's shape, for rigs whose bones are called bone_0..n.

    A humanoid has one joint that two legs and a spine hang off (the pelvis), one further up that two
    arms and a neck hang off (the chest), and a joint in each arm that the fingers fan out from (the
    hand). Everything else is found by walking between those.
    """
    by_name = {b["name"]: b for b in bones}
    kids: Dict[str, List[str]] = {b["name"]: [] for b in bones}
    for b in bones:
        if b["parent"] in kids:
            kids[b["parent"]].append(b["name"])

    def subtree(n: str) -> List[str]:
        out, stack = [], [n]
        while stack:
            k = stack.pop()
            out.append(k)
            stack.extend(kids[k])
        return out

    z = {n: float(by_name[n]["head"][2]) for n in by_name}
    x = {n: float(by_name[n]["head"][0]) for n in by_name}
    lo = min(z.values())
    hi = max(z.values())
    h = hi - lo
    low = {n: min(z[k] for k in subtree(n)) for n in by_name}
    high = {n: max(z[k] for k in subtree(n)) for n in by_name}
    reach = {n: max(abs(x[k]) for k in subtree(n)) for n in by_name}

    order = [b["name"] for b in bones]
    pelvis = next((n for n in order
                   if sum(1 for c in kids[n] if low[c] < lo + 0.25 * h and high[c] < lo + 0.65 * h) >= 2
                   and any(high[c] > lo + 0.8 * h for c in kids[n])), None)
    if pelvis is None:
        return {}
    out = {"pelvis": pelvis}

    legs = sorted((c for c in kids[pelvis] if low[c] < lo + 0.25 * h and high[c] < lo + 0.65 * h), key=lambda c: low[c])[:2]
    for leg in legs:
        side = "l" if np.mean([x[k] for k in subtree(leg)]) > 0 else "r"
        path = [leg]
        while kids[path[-1]]:
            path.append(min(kids[path[-1]], key=lambda c: low[c]))
        ankle = min((n for n in path if z[n] > lo + 0.03 * h), key=lambda n: abs(z[n] - (lo + 0.05 * h)))
        knee = min(path[:path.index(ankle)] or [leg], key=lambda n: abs(z[n] - (z[leg] + z[ankle]) * 0.5))
        out[f"thigh_{side}"], out[f"shin_{side}"], out[f"foot_{side}"] = leg, knee, ankle
        if path.index(ankle) + 1 < len(path):
            out[f"toe_{side}"] = path[path.index(ankle) + 1]

    spine = max((c for c in kids[pelvis] if c not in legs), key=lambda c: high[c])
    path = [spine]
    while sum(1 for c in kids[path[-1]] if reach[c] > ARM_REACH * h) < 2 and kids[path[-1]]:
        path.append(max(kids[path[-1]], key=lambda c: high[c]))
    chest = path[-1]
    out["spine"], out["torso"] = spine, chest

    arms = sorted((c for c in kids[chest] if reach[c] > ARM_REACH * h), key=lambda c: -reach[c])[:2]
    for arm in arms:
        side = "l" if np.mean([x[k] for k in subtree(arm)]) > 0 else "r"
        path = [arm]
        while kids[path[-1]] and len(kids[path[-1]]) < 3:
            path.append(max(kids[path[-1]], key=lambda c: len(subtree(c))))
        if len(path) < 3:
            continue
        if len(kids[path[-1]]) >= 3:
            # Fingers fan out from the last joint: that is the hand.
            hand = path[-1]
            upper, fore = path[-3], path[-2]
        else:
            # No fingers in the rig. The upper arm and forearm are the two longest bones in a row.
            pos = [np.array(by_name[n]["head"], dtype=float) for n in path]
            seg = [float(np.linalg.norm(pos[i + 1] - pos[i])) for i in range(len(path) - 1)]
            i = max(range(len(seg) - 1), key=lambda k: seg[k] + seg[k + 1])
            upper, fore, hand = path[i], path[i + 1], path[i + 2]
        out[f"hand_{side}"], out[f"forearm_{side}"], out[f"upper_arm_{side}"] = hand, fore, upper
        fingers = sorted(kids[hand], key=lambda c: -len(subtree(c)))
        if fingers:
            tip = fingers[len(fingers) // 2]
            while kids[tip]:
                tip = kids[tip][0]
            out[f"hand_tip_{side}"] = tip

    up = [c for c in kids[chest] if c not in arms]
    if up:
        neck = max(up, key=lambda c: high[c])
        path = [neck]
        while kids[path[-1]]:
            path.append(max(kids[path[-1]], key=lambda c: high[c]))
        out["neck"] = neck
        # The head bone is the last one that still has something above it; a lone "head end" leaf is not it.
        out["head"] = path[-2] if len(path) >= 2 else path[-1]
    return out


REQUIRED = ["pelvis", "spine", "torso", "head",
            "thigh_l", "shin_l", "foot_l", "thigh_r", "shin_r", "foot_r",
            "upper_arm_l", "forearm_l", "hand_l", "upper_arm_r", "forearm_r", "hand_r"]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--glb", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--mass", type=float, default=0.0,
                    help="body mass in kg. 0 works it out from the height at a body-mass index of 24, "
                         "so a bigger fighter is a heavier one (house rule: mass according to size).")
    ap.add_argument("--height", type=float, default=0.0,
                    help="force this height in metres. 0 keeps the file's own size if it is plausibly "
                         "a person in metres (1.2 to 2.3 m), and otherwise fixes the unit.")
    args = ap.parse_args()

    gltf = read_glb(args.glb)
    if not gltf.get("skins"):
        raise SystemExit(f"{args.glb} has no skin: it is a mesh without a skeleton, and there is no rig to read.")
    skin = max(gltf["skins"], key=lambda s: len(s["joints"]))
    joints = skin["joints"]
    nodes = gltf["nodes"]
    world = world_matrices(gltf)
    joint_set = set(joints)
    parent_of = {}
    for i, n in enumerate(nodes):
        for c in n.get("children", []):
            parent_of[c] = i

    def to_blender(p: np.ndarray) -> np.ndarray:
        return np.array([p[0], -p[2], p[1]])

    heads = {j: to_blender(world[j][:3, 3]) for j in joints}
    bones = []
    for j in joints:
        name = nodes[j].get("name", f"joint{j}")
        p = parent_of.get(j)
        while p is not None and p not in joint_set:
            p = parent_of.get(p)
        kids = [c for c in nodes[j].get("children", []) if c in joint_set]
        if kids:
            # The child that continues the chain: the one furthest from this joint. On the centre line
            # (spine, neck, head) that means furthest up, or an eye or a clavicle would drag the bone sideways.
            if abs(heads[j][0] - heads[joints[0]][0]) < 0.02 * max(1e-6, np.ptp([h[2] for h in heads.values()])):
                tail = max((heads[c] for c in kids), key=lambda h: float(h[2] - 2.0 * abs(h[0] - heads[j][0])))
            else:
                tail = max((heads[c] for c in kids), key=lambda h: float(np.linalg.norm(h - heads[j])))
        elif p is not None:
            d = heads[j] - heads[p]
            tail = heads[j] + d * 0.3
        else:
            tail = heads[j] + np.array([0, 0, 0.1])
        bones.append({"name": name, "parent": nodes[p].get("name") if p is not None else None,
                      "head": heads[j].copy(), "tail": np.array(tail, dtype=float)})

    mapping = build_map(bones)
    missing = [k for k in REQUIRED if k not in mapping]
    by_shape = False
    if missing:
        # The names did not say; ask the shape. Sides here are from raw x and are settled below.
        mapping = build_map_by_shape(bones)
        by_shape = True
        missing = [k for k in REQUIRED if k not in mapping]
    if missing:
        names = ", ".join(b["name"] for b in bones)
        raise SystemExit(f"could not find bones for: {', '.join(missing)}.\nBones in the file: {names}\n"
                         f"Add the names to PATTERNS in glb_to_rig.py.")
    by_name = {b["name"]: b for b in bones}

    # ---- orientation: feet at z = 0, toes pointing -y, the character's left at +x
    pts = np.array([b["head"] for b in bones] + [b["tail"] for b in bones])
    floor = pts[:, 2].min()
    top = pts[:, 2].max()
    foot = by_name[mapping["foot_l"]]
    toe_dir = (by_name[mapping["toe_l"]]["head"] if "toe_l" in mapping else foot["tail"]) - foot["head"]
    flip = toe_dir[1] > 0.0
    mirror = (by_name[mapping["upper_arm_l"]]["head"][0] < 0.0) != flip
    notes = []
    if by_shape:
        notes.append("bone names were not recognisable; limbs were identified from the skeleton's shape. Check the list below")
    if flip:
        notes.append("turned 180 degrees about the vertical: the file's toes pointed along +y")
    if mirror:
        notes.append("left and right are swapped relative to the bone names; kept the names, mirrored x")

    # ---- scale
    skeleton_height = top - floor
    # The skeleton stops at the top of the head bone; a person is a few per cent taller than that.
    if args.height > 0.0:
        scale = args.height / (skeleton_height * 1.02)
    elif 1.2 <= skeleton_height <= 2.3:
        scale = 1.0
    else:
        scale = 1.74 / (skeleton_height * 1.02)
        notes.append(f"rescaled x{scale:.4g}: the skeleton was {skeleton_height:.3g} units tall, which is not metres")

    def fix(p: np.ndarray) -> np.ndarray:
        q = np.array([p[0], p[1], p[2] - floor]) * scale
        if flip:
            q[0], q[1] = -q[0], -q[1]
        if mirror:
            q[0] = -q[0]
        return q

    for b in bones:
        b["head"], b["tail"] = fix(b["head"]), fix(b["tail"])

    # centre the pelvis on x = 0
    cx = by_name[mapping["pelvis"]]["head"][0]
    for b in bones:
        b["head"][0] -= cx
        b["tail"][0] -= cx

    # ---- arms to a T-pose, keeping segment lengths
    for side, sign in (("l", 1.0), ("r", -1.0)):
        chain = [mapping[f"upper_arm_{side}"], mapping[f"forearm_{side}"], mapping[f"hand_{side}"]]
        tip_key = f"hand_tip_{side}"
        upper, fore, hand = (by_name[n] for n in chain)
        direction = fore["head"] - upper["head"]
        angle = float(np.degrees(np.arctan2(-direction[2], abs(direction[0]) + 1e-9)))
        lengths = [np.linalg.norm(fore["head"] - upper["head"]), np.linalg.norm(hand["head"] - fore["head"])]
        tip = by_name[mapping[tip_key]]["tail"] if tip_key in mapping else hand["tail"]
        hand_len = float(np.linalg.norm(tip - hand["head"]))
        if abs(angle) > 8.0 and side == "l":
            notes.append(f"arms straightened to a T-pose: they rested {angle:.0f} degrees below the shoulder line")
        axis = np.array([sign, 0.0, 0.0])
        elbow = upper["head"] + axis * lengths[0]
        wrist = elbow + axis * lengths[1]
        end = wrist + axis * max(hand_len, 0.08)
        upper["tail"] = elbow
        fore["head"], fore["tail"] = elbow, wrist
        hand["head"], hand["tail"] = wrist, end
        if tip_key in mapping:
            t = by_name[mapping[tip_key]]
            t["head"], t["tail"] = end - axis * 0.02, end

    # ---- the head bone ends at the crown. Rigs often end it at an eye or a jaw instead, which would put
    # the head's centre off to one side and too low.
    figure = float(max(b["head"][2] for b in bones))
    hb = by_name[mapping["head"]]
    rise = hb["tail"][2] - hb["head"][2]
    if abs(hb["tail"][0] - hb["head"][0]) > 0.01 or rise < 0.05 * figure or rise > 0.16 * figure:
        hb["tail"] = hb["head"] + np.array([0.0, 0.0, 0.075 * figure])
        notes.append("head bone had no crown end; its top was placed 7.5% of the figure's height above the neck")

    keep = set(mapping.values())
    out_bones = [{"name": b["name"], "parent": b["parent"],
                  "head": [round(float(x), 4) for x in b["head"]],
                  "tail": [round(float(x), 4) for x in b["tail"]]} for b in bones if b["name"] in keep]
    height = float(max(b["tail"][2] for b in out_bones))
    rig = {
        "source": os.path.abspath(args.glb).replace("\\", "/"),
        "frame": "blender_z_up (x = character left, -y = forward, z = up)",
        "height_m": round(height * 1.02, 3),
        "mass_kg": args.mass if args.mass > 0.0 else round(24.0 * (height * 1.02) ** 2, 1),
        "notes": notes,
        "bones": out_bones,
        "map": mapping,
        "all_bones": len(bones),
    }
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(rig, f, indent=2)

    print(f"read {len(bones)} bones from {os.path.basename(args.glb)}; kept the {len(out_bones)} the fighter uses")
    print(f"height {rig['height_m']:.2f} m, mass {rig['mass_kg']:g} kg")
    for n in notes:
        print("note: " + n)
    for key in REQUIRED + [k for k in mapping if k not in REQUIRED]:
        b = by_name[mapping[key]]
        print(f"  {key:14s} <- {mapping[key]:24s} head {np.round(b['head'], 3)}")
    print(f"wrote {args.out}")


if __name__ == "__main__":
    main()
