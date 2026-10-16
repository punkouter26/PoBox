"""Turn the two dental scans (upper and lower jaw, .ply from the scanner) into one .glb the game can use.

    blender --background --python tools/teeth_to_glb.py -- upper.ply lower.ply out.glb [faces_each]

Blender is the converter; the scans are not touched. What it does:
  * works out which way the jaws face from their shape: up is the thin direction (and the upper jaw is
    above the lower), left-right is the direction the arch is its own mirror image in, and the front is
    the closed end of the U;
  * turns them to stand upright facing -Y (Blender's forward, which glTF and Unity read as forward), in metres;
  * puts both jaws' origins on the hinge, behind and above the back teeth where a jaw's joint is, so the
    lower jaw opens by turning about its own X axis;
  * thins each scan to `faces_each` triangles and keeps the scanner's vertex colours.
"""
import sys

import bpy
import numpy as np

args = sys.argv[sys.argv.index("--") + 1:]
upper_path, lower_path, out_path = args[0], args[1], args[2]
faces_each = int(args[3]) if len(args) > 3 else 30000
if len(args) > 4:
    # The launcher of a Store-installed Blender does not hand its output back: say it to a file instead.
    sys.stdout = sys.stderr = open(args[4], "w", buffering=1)

bpy.ops.wm.read_factory_settings(use_empty=True)


def load(path, name):
    bpy.ops.wm.ply_import(filepath=path)
    o = bpy.context.selected_objects[0]
    o.name = name
    o.data.name = name
    return o


def points(o):
    a = np.empty(len(o.data.vertices) * 3, dtype=np.float32)
    o.data.vertices.foreach_get("co", a)
    return a.reshape(-1, 3).astype(np.float64)


upper, lower = load(upper_path, "TeethUpper"), load(lower_path, "TeethLower")
U, L = points(upper), points(lower)
both = np.concatenate([U, L])
centre = both.mean(0)

# Up: the thin direction of the two arches together, pointing from the lower jaw to the upper.
w, v = np.linalg.eigh(np.cov((both - centre).T))
up = v[:, 0]
if np.dot(U.mean(0) - L.mean(0), up) < 0.0:
    up = -up
h1, h2 = v[:, 1], v[:, 2]

# Left-right: the horizontal direction about which the arch mirrors onto itself best.
flat = np.stack([(both - centre) @ h1, (both - centre) @ h2], 1)
span = float(np.abs(flat).max())
best = (-2.0, 0.0)
for deg in np.arange(0.0, 180.0, 1.0):
    t = np.radians(deg)
    a = flat @ np.array([np.cos(t), np.sin(t)])
    b = flat @ np.array([-np.sin(t), np.cos(t)])
    hist, _, _ = np.histogram2d(a, b, bins=40, range=[[-span, span], [-span, span]])
    score = float(np.corrcoef(hist.ravel(), hist[::-1].ravel())[0, 1])
    if score > best[0]:
        best = (score, t)
t = best[1]
side = np.cos(t) * h1 + np.sin(t) * h2
fwd = np.cross(up, side)
fwd /= np.linalg.norm(fwd)
# Front: the closed end of the U. At the back the teeth are out at the sides, at the front on the midline.
ap, lr = (both - centre) @ fwd, (both - centre) @ side
front_spread = float(np.abs(lr[ap > np.percentile(ap, 92)]).mean())
back_spread = float(np.abs(lr[ap < np.percentile(ap, 8)]).mean())
if front_spread > back_spread:
    fwd = -fwd
side = np.cross(fwd, up)          # x = forward x up, so that (x, -forward, up) is right-handed with -Y forward
print(f"mirror score {best[0]:.3f}; spread at the front {min(front_spread, back_spread):.1f}, at the back {max(front_spread, back_spread):.1f} (scanner units)")

# Units: a dental arch is about 60 mm across. Anything near that is millimetres.
width = float(np.ptp((both - centre) @ side))
scale = 0.001 if width > 5.0 else 1.0
ap = (both - centre) @ fwd
z = (both - centre) @ up
bite = 0.5 * (float(((U - centre) @ up).min()) + float(((L - centre) @ up).max()))     # where the teeth meet
hinge = centre + fwd * (float(ap.min()) - 0.20 * float(np.ptp(ap))) + up * (bite + 0.35 * float(np.ptp(z)))
print(f"arch {width * scale * 1000:.0f} mm across, {np.ptp(ap) * scale * 1000:.0f} mm deep, {np.ptp(z) * scale * 1000:.0f} mm tall; "
      f"hinge {0.20 * np.ptp(ap) * scale * 1000:.0f} mm behind the back teeth")

R = np.stack([side, -fwd, up], 0)     # rows: new x, new y (back), new z (up)

mat = bpy.data.materials.new("Teeth")
mat.use_nodes = True
nodes, links = mat.node_tree.nodes, mat.node_tree.links
bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
bsdf.inputs["Roughness"].default_value = 0.3

for o, P in ((upper, U), (lower, L)):
    q = ((P - hinge) @ R.T) * scale
    o.data.vertices.foreach_set("co", q.astype(np.float32).ravel())
    o.data.update()
    o.location = (0.0, 0.0, 0.0)
    o.rotation_euler = (0.0, 0.0, 0.0)
    o.scale = (1.0, 1.0, 1.0)
    bpy.context.view_layer.objects.active = o
    for other in bpy.context.selected_objects:
        other.select_set(False)
    o.select_set(True)
    n = len(o.data.polygons)
    if n > faces_each:
        mod = o.modifiers.new("thin", "DECIMATE")
        mod.ratio = faces_each / n
        bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.ops.object.shade_smooth()
    o.data.materials.clear()
    o.data.materials.append(mat)
    names = [a.name for a in o.data.color_attributes]
    print(f"{o.name}: {n} -> {len(o.data.polygons)} faces, colour attributes {names}")
    if names:
        o.data.color_attributes.active_color = o.data.color_attributes[names[0]]
        o.data.color_attributes.render_color_index = 0

names = [a.name for a in upper.data.color_attributes]
if names:
    col = nodes.new("ShaderNodeVertexColor")
    col.layer_name = names[0]
    links.new(col.outputs["Color"], bsdf.inputs["Base Color"])

for o in (upper, lower):
    o.select_set(True)
kw = dict(filepath=out_path, export_format="GLB", use_selection=True, export_apply=True, export_yup=True)
for extra in (dict(export_vertex_color="MATERIAL"), dict(export_colors=True), dict()):
    try:
        bpy.ops.export_scene.gltf(**kw, **extra)
        print(f"wrote {out_path} with {extra or 'default colour options'}")
        break
    except TypeError as e:
        print(f"exporter did not take {extra}: {e}")
