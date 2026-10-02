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
from typing import Dict, List, Optional, Tuple

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


def read_glb_bin(path: str) -> Optional[bytes]:
    """The binary chunk: vertex positions, skin weights and the skin's bind matrices."""
    with open(path, "rb") as f:
        data = f.read()
    offset = 12
    while offset < len(data):
        chunk_len, chunk_type = struct.unpack_from("<I4s", data, offset)
        offset += 8
        if chunk_type == b"BIN\x00":
            return data[offset:offset + chunk_len]
        offset += chunk_len
    return None


COMPONENT = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
WIDTH = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def accessor(gltf: dict, blob: bytes, index: int) -> np.ndarray:
    a = gltf["accessors"][index]
    view = gltf["bufferViews"][a["bufferView"]]
    dtype = np.dtype(COMPONENT[a["componentType"]])
    width = WIDTH[a["type"]]
    start = view.get("byteOffset", 0) + a.get("byteOffset", 0)
    stride = view.get("byteStride", 0) or dtype.itemsize * width
    return np.ndarray((a["count"], width), dtype=dtype, buffer=blob, offset=start,
                      strides=(stride, dtype.itemsize)).copy()


def bind_matrices(gltf: dict, blob: Optional[bytes], skin: dict) -> Optional[Dict[int, np.ndarray]]:
    """Where each joint is in the pose the mesh was modelled in. A file that carries an animation often
    stores its nodes in a frame of it; the skin's inverse bind matrices are what the vertices belong to."""
    if blob is None or "inverseBindMatrices" not in skin:
        return None
    ibm = accessor(gltf, blob, skin["inverseBindMatrices"]).astype(float).reshape(-1, 4, 4).transpose(0, 2, 1)
    return {j: np.linalg.inv(ibm[i]) for i, j in enumerate(skin["joints"])}


def skinned_mesh(gltf: dict, blob: Optional[bytes], skin_index: int):
    """Vertices of every mesh the skin drives (in the bind pose), the joint each follows most, and the
    triangles. None if the file's layout is not one this reads."""
    if blob is None:
        return None
    verts, dom, tris, base = [], [], [], 0
    for node in gltf["nodes"]:
        if node.get("skin") != skin_index or "mesh" not in node:
            continue
        for prim in gltf["meshes"][node["mesh"]]["primitives"]:
            at = prim["attributes"]
            if "JOINTS_0" not in at or "WEIGHTS_0" not in at or prim.get("mode", 4) != 4 or "extensions" in prim:
                continue
            v = accessor(gltf, blob, at["POSITION"]).astype(float)
            j = accessor(gltf, blob, at["JOINTS_0"]).astype(int)
            w = accessor(gltf, blob, at["WEIGHTS_0"]).astype(float)
            verts.append(v)
            dom.append(j[np.arange(len(j)), w.argmax(1)])
            if "indices" in prim:
                tris.append(accessor(gltf, blob, prim["indices"]).astype(int).reshape(-1, 3) + base)
            base += len(v)
    if not verts:
        return None
    return np.concatenate(verts), np.concatenate(dom), (np.concatenate(tris) if tris else np.zeros((0, 3), dtype=int))


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

# House rule: a fighter's collision shapes are fitted inside its own skinned mesh. The band each
# radius is held to [m], so that a mesh in a coat or with a bad weight map cannot produce a body no
# person has.
RADIUS_BAND = {"pelvis": (0.070, 0.135), "torso": (0.070, 0.135), "head": (0.085, 0.125), "thigh": (0.045, 0.090),
               "shin": (0.035, 0.062), "upper_arm": (0.030, 0.058), "forearm": (0.028, 0.048)}


def fit_radii(verts: np.ndarray, dom: np.ndarray, bones: List[dict], mapping: Dict[str, str]) -> Tuple[Dict[str, float], float]:
    """Capsule radii that sit inside the mesh, and the height of the crown above the head joint.

    Measured from the outline, not from how far the skin is from the bone: generated meshes carry
    surfaces inside themselves, and a bone is seldom in the middle of its limb. A limb's radius is a
    little under half its thickness the thin way, over the middle half of its length (so not the bulge
    of a joint). The trunk is wider than it is deep and a capsule has to fit the depth.
    """
    def half_extent(x: np.ndarray) -> float:
        return 0.5 * float(np.percentile(x, 96) - np.percentile(x, 4))

    names = [b["name"] for b in bones]
    by_name = {b["name"]: b for b in bones}
    kids: Dict[str, List[str]] = {n: [] for n in names}
    for b in bones:
        if b["parent"] in kids:
            kids[b["parent"]].append(b["name"])
    mapped = set(mapping.values())

    def owned(bone: str) -> List[int]:
        """The bone and whatever hangs off it that the fighter does not use: twist bones, fingers, a jaw."""
        out, stack = [], [bone]
        while stack:
            n = stack.pop()
            out.append(names.index(n))
            stack.extend(c for c in kids[n] if c not in mapped)
        return out

    def skin_of(*keys: str) -> np.ndarray:
        idx = [i for k in keys if k in mapping for i in owned(mapping[k])]
        return verts[np.isin(dom, idx)]

    def limb(key: str, to: str) -> Optional[float]:
        out = []
        for s in "lr":
            a, c = by_name[mapping[f"{key}_{s}"]]["head"], by_name[mapping[f"{to}_{s}"]]["head"]
            v = skin_of(f"{key}_{s}")
            axis = c - a
            length = float(np.linalg.norm(axis))
            if length < 1e-6 or len(v) < 30:
                continue
            axis = axis / length
            t = (v - a) @ axis
            mid = v[(t > 0.25 * length) & (t < 0.75 * length)]
            if len(mid) < 20:
                continue
            off = (mid - a) - np.outer((mid - a) @ axis, axis)
            u = np.cross(axis, [0.0, 1.0, 0.0] if abs(axis[1]) < 0.9 else [1.0, 0.0, 0.0])
            u = u / np.linalg.norm(u)
            w = np.cross(axis, u)
            out.append(0.92 * min(half_extent(off @ u), half_extent(off @ w)))
        return float(np.mean(out)) if out else None

    def trunk(keys: Tuple[str, ...], lo: float, hi: float, x_max: float) -> Optional[float]:
        v = skin_of(*keys)
        v = v[(v[:, 2] > lo) & (v[:, 2] < hi) & (np.abs(v[:, 0]) < x_max)]
        if len(v) < 40:
            return None
        return 0.9 * min(half_extent(v[:, 0]), half_extent(v[:, 1]))

    fit: Dict[str, Optional[float]] = {
        "thigh": limb("thigh", "shin"), "shin": limb("shin", "foot"),
        "upper_arm": limb("upper_arm", "forearm"), "forearm": limb("forearm", "hand"),
    }
    pelvis_z = float(by_name[mapping["pelvis"]]["head"][2])
    spine_z = float(by_name[mapping["spine"]]["head"][2])
    neck_z = float(by_name[mapping["neck"]]["head"][2]) if "neck" in mapping else float(by_name[mapping["torso"]]["tail"][2])
    shoulder_x = 0.5 * (abs(by_name[mapping["upper_arm_l"]]["head"][0]) + abs(by_name[mapping["upper_arm_r"]]["head"][0]))
    fit["torso"] = trunk(("spine", "torso"), spine_z, neck_z, shoulder_x)
    fit["pelvis"] = trunk(("pelvis", "spine"), pelvis_z - 0.10, max(spine_z, pelvis_z + 0.08), shoulder_x)

    head = by_name[mapping["head"]]
    v = skin_of("head")
    v = v[v[:, 2] > head["head"][2]]
    crown = float(v[:, 2].max() - head["head"][2]) if len(v) >= 40 else 0.0
    if crown > 0.0:
        fit["head"] = 0.5 * crown

    radii = {}
    for key, r in fit.items():
        if r is not None:
            lo, hi = RADIUS_BAND[key]
            radii[key] = round(float(np.clip(r, lo, hi)), 4)
    # Thighs hang side by side: each has to fit in its own half of the hips.
    if "thigh" in radii:
        hip_x = 0.5 * (abs(by_name[mapping["thigh_l"]]["head"][0]) + abs(by_name[mapping["thigh_r"]]["head"][0]))
        radii["thigh"] = round(min(radii["thigh"], max(RADIUS_BAND["thigh"][0], hip_x - 0.012)), 4)
    if "shin" in radii and "thigh" in radii:
        radii["shin"] = min(radii["shin"], radii["thigh"])
    return radii, crown


def plot_fit(path: str, verts: np.ndarray, by_name: Dict[str, dict], mapping: Dict[str, str],
             radii: Dict[str, float], crown: float) -> None:
    """The mesh as dots, front and side, with the skeleton and the fitted shapes drawn over it."""
    from PIL import Image, ImageDraw

    head = lambda k: by_name[mapping[k]]["head"]
    segs = [("torso", head("spine"), head("neck") if "neck" in mapping else by_name[mapping["torso"]]["tail"]),
            ("pelvis", head("pelvis"), head("spine"))]
    for s in "lr":
        segs += [("thigh", head(f"thigh_{s}"), head(f"shin_{s}")), ("shin", head(f"shin_{s}"), head(f"foot_{s}")),
                 ("upper_arm", head(f"upper_arm_{s}"), head(f"forearm_{s}")), ("forearm", head(f"forearm_{s}"), head(f"hand_{s}"))]
    px = 360.0                                   # pixels to the metre
    top = float(verts[:, 2].max()) + 0.08
    panel_w, panel_h = int(2.1 * px), int((top + 0.06) * px)
    img = Image.new("RGB", (2 * panel_w, panel_h), (16, 18, 24))
    draw = ImageDraw.Draw(img)
    for n, (i, label) in enumerate(((0, "front (x = the character's left)"), (1, "side (-y = forward)"))):
        at = lambda p: (n * panel_w + panel_w * 0.5 + float(p[i]) * px, (top - float(p[2])) * px)
        for v in verts[:: max(1, len(verts) // 14000)]:
            x, y = at(v)
            draw.point((x, y), fill=(120, 132, 150))
        for metre in range(0, int(top) + 1):
            draw.line([at((-1.0, -1.0, metre)), at((1.0, 1.0, metre))], fill=(52, 58, 70))
        for key, a, b in segs:
            draw.line([at(a), at(b)], fill=(230, 70, 70), width=2)
            r = radii.get(key)
            if r:
                (ax, ay), (bx, by) = at(a), at(b)
                d = np.array([bx - ax, by - ay])
                nrm = np.array([-d[1], d[0]]) / max(1e-9, float(np.linalg.norm(d))) * r * px
                for sign in (1.0, -1.0):
                    draw.line([(ax + sign * nrm[0], ay + sign * nrm[1]), (bx + sign * nrm[0], by + sign * nrm[1])], fill=(90, 170, 255), width=2)
        if crown > 0.0 and "head" in radii:
            cx, cy = at(head("head") + np.array([0.0, 0.0, 0.5 * crown]))
            r = radii["head"] * px
            draw.ellipse([cx - r, cy - r, cx + r, cy + r], outline=(90, 170, 255), width=2)
        draw.text((n * panel_w + 10, 8), label, fill=(230, 230, 230))
    draw.text((10, panel_h - 18), "red: bones   blue: fitted shapes   grey: the mesh   lines: every metre", fill=(230, 230, 230))
    img.save(path)


def mesh_volume(verts: np.ndarray, tris: np.ndarray) -> float:
    """Volume enclosed by the mesh [units cubed]; meaningful only if it is closed."""
    if len(tris) == 0:
        return 0.0
    a, b, c = verts[tris[:, 0]], verts[tris[:, 1]], verts[tris[:, 2]]
    return float(abs(np.einsum("ij,ij->i", a, np.cross(b, c)).sum()) / 6.0)


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
    ap.add_argument("--plot", default="", help="write a picture of the mesh with the fitted shapes over it, front and side")
    ap.add_argument("--skeleton-only", action="store_true",
                    help="read the node transforms and nothing else, as this did before 2026-10-01: no bind pose, "
                         "no sizes from the mesh, no symmetry. For regenerating a rig exactly as it was")
    args = ap.parse_args()

    gltf = read_glb(args.glb)
    if not gltf.get("skins"):
        raise SystemExit(f"{args.glb} has no skin: it is a mesh without a skeleton, and there is no rig to read.")
    skin = max(gltf["skins"], key=lambda s: len(s["joints"]))
    joints = skin["joints"]
    nodes = gltf["nodes"]
    world = world_matrices(gltf)
    notes = []
    blob = None if args.skeleton_only else read_glb_bin(args.glb)
    bind = bind_matrices(gltf, blob, skin)
    if bind is not None:
        span = max(1e-6, float(np.ptp([bind[j][1, 3] for j in joints])))
        drift = max(float(np.linalg.norm(bind[j][:3, 3] - world[j][:3, 3])) for j in joints) / span
        if drift > 0.01:
            notes.append(f"the file stores its skeleton in a pose (a joint is {drift:.0%} of the figure's height from where "
                         f"the mesh was modelled); read the bind pose from the skin instead")
        world = [bind.get(i, w) for i, w in enumerate(world)]
    mesh = skinned_mesh(gltf, blob, gltf["skins"].index(skin))
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
    verts = None
    if mesh is not None:
        # The mesh says where the floor and the crown are; a skeleton stops at the toe bone and the head bone.
        verts = np.stack([mesh[0][:, 0], -mesh[0][:, 2], mesh[0][:, 1]], 1)
        floor, top = float(verts[:, 2].min()), float(verts[:, 2].max())
    if by_shape:
        notes.append("bone names were not recognisable; limbs were identified from the skeleton's shape. Check the list below")
    if flip:
        notes.append("turned 180 degrees about the vertical: the file's toes pointed along +y")
    if mirror:
        notes.append("left and right are swapped relative to the bone names; kept the names, mirrored x")

    # ---- scale
    skeleton_height = top - floor
    # The skeleton stops at the top of the head bone; a person is a few per cent taller than that.
    figure = skeleton_height * (1.0 if verts is not None else 1.02)
    if args.height > 0.0:
        scale = args.height / figure
    elif 1.2 <= skeleton_height <= 2.3:
        scale = 1.0
    else:
        scale = 1.74 / figure
        notes.append(f"rescaled x{scale:.4g}: the {'figure' if verts is not None else 'skeleton'} was {skeleton_height:.3g} units tall, which is not metres")

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

    # ---- sizes from the mesh, measured before anything is straightened
    radii, crown, mesh_info = {}, 0.0, {}
    if verts is not None:
        verts = (verts - np.array([0.0, 0.0, floor])) * scale
        if flip:
            verts[:, :2] *= -1.0
        if mirror:
            verts[:, 0] *= -1.0
        verts[:, 0] -= cx
        radii, crown = fit_radii(verts, mesh[1], bones, mapping)
        if args.plot:
            plot_fit(args.plot, verts, by_name, mapping, radii, crown)
        litres = mesh_volume(verts, mesh[2]) * 1000.0
        mesh_info = {"vertices": int(len(verts)), "height_m": round(float(verts[:, 2].max()), 3), "volume_l": round(litres, 1)}

    # ---- a person is the same on both sides. A rig placed by an automatic rigger is often not, by a
    # few centimetres, and a body with one leg longer than the other stands crooked.
    if verts is not None:
        worst = 0.0
        flip_x = np.array([-1.0, 1.0, 1.0])
        for key in ("thigh", "shin", "foot", "toe", "upper_arm", "forearm", "hand", "hand_tip"):
            if f"{key}_l" in mapping and f"{key}_r" in mapping:
                left, right = by_name[mapping[f"{key}_l"]], by_name[mapping[f"{key}_r"]]
                for part in ("head", "tail"):
                    worst = max(worst, float(np.linalg.norm(left[part] - right[part] * flip_x)))
        centre = [by_name[mapping[k]] for k in ("pelvis", "spine", "torso", "neck", "head") if k in mapping]
        worst = max([worst] + [abs(float(b["head"][0])) for b in centre])
        if worst > 0.005:
            for key in ("thigh", "shin", "foot", "toe", "upper_arm", "forearm", "hand", "hand_tip"):
                if f"{key}_l" in mapping and f"{key}_r" in mapping:
                    left, right = by_name[mapping[f"{key}_l"]], by_name[mapping[f"{key}_r"]]
                    for part in ("head", "tail"):
                        mean = 0.5 * (left[part] + right[part] * flip_x)
                        left[part], right[part] = mean, mean * flip_x
            for b in centre:
                b["head"][0] = 0.0
                b["tail"][0] = 0.0
            notes.append(f"made symmetric: left and right differed by up to {worst * 100:.1f} cm")

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
    if 0.10 < crown < 0.30:
        # The mesh has a crown: the head is the ball between the head joint and the top of the skull.
        hb["tail"] = hb["head"] + np.array([0.0, 0.0, crown])
    elif abs(hb["tail"][0] - hb["head"][0]) > 0.01 or rise < 0.05 * figure or rise > 0.16 * figure:
        hb["tail"] = hb["head"] + np.array([0.0, 0.0, 0.075 * figure])
        notes.append("head bone had no crown end; its top was placed 7.5% of the figure's height above the neck")

    keep = set(mapping.values())
    out_bones = [{"name": b["name"], "parent": b["parent"],
                  "head": [round(float(x), 4) for x in b["head"]],
                  "tail": [round(float(x), 4) for x in b["tail"]]} for b in bones if b["name"] in keep]
    height = float(max(b["tail"][2] for b in out_bones))
    height_m = mesh_info["height_m"] if mesh_info else height * 1.02
    # House rule: mass according to size. A closed mesh has a volume, and a body is about as dense as
    # water. Clothes, hair and surfaces inside the mesh all add to it, so the figure is held to what a
    # person can be: a body-mass index between 18.5 and 30. With no mesh it is the height at an index of 24.
    mass = round(24.0 * height_m ** 2, 1)
    if mesh_info and mesh_info["volume_l"] > 0.0:
        bmi = 0.985 * mesh_info["volume_l"] / height_m ** 2
        mesh_info["bmi_from_volume"] = round(bmi, 1)
        used = float(np.clip(bmi, 18.5, 30.0))
        mass = round(used * height_m ** 2, 1)
        if args.mass <= 0.0:
            notes.append(f"mass from the mesh's volume: {mesh_info['volume_l']:.0f} litres is a body-mass index of {bmi:.1f}"
                         + ("" if used == bmi else f", held to {used:g}"))
    rig = {
        "source": os.path.abspath(args.glb).replace("\\", "/"),
        "frame": "blender_z_up (x = character left, -y = forward, z = up)",
        "height_m": round(height_m, 3),
        "mass_kg": args.mass if args.mass > 0.0 else mass,
        "notes": notes,
        "bones": out_bones,
        "map": mapping,
        "all_bones": len(bones),
    }
    if radii:
        rig["radii"] = radii
        rig["mesh"] = mesh_info
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(rig, f, indent=2)

    print(f"read {len(bones)} bones from {os.path.basename(args.glb)}; kept the {len(out_bones)} the fighter uses")
    print(f"height {rig['height_m']:.2f} m, mass {rig['mass_kg']:g} kg")
    if radii:
        print("shapes fitted inside the mesh [m]: " + ", ".join(f"{k} {v:.3f}" for k, v in radii.items()))
    for n in notes:
        print("note: " + n)
    for key in REQUIRED + [k for k in mapping if k not in REQUIRED]:
        b = by_name[mapping[key]]
        print(f"  {key:14s} <- {mapping[key]:24s} head {np.round(b['head'], 3)}")
    print(f"wrote {args.out}")


if __name__ == "__main__":
    main()
