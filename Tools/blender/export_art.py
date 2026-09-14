"""Export the arena and the ring from ArtSource/BoxingRing.blend.

WHY THIS FILE EXISTS
--------------------
Both of the game's environment meshes come out of ONE Blender file, and until
now nothing in the repository recorded that.  `ArtSource/BoxingRing.blend` holds
two collections -- `BoxingRing` (the ring itself, 734 triangles, three
materials) and `Arena` (the building around it: 57,464 triangles, 27 materials,
396 objects including the crowd, the trusses, the jumbotron and the
commentators) -- and `Assets/Art/BoxingRing.glb` and `Assets/Art/Arena.glb` are
exports of those two collections.  Neither the split nor the export settings
were written down anywhere, which is the same failure this project already
recorded once for its scene generators: an artifact whose source cannot rebuild
it is a way to lose it.

Run it:

    blender --background ArtSource/BoxingRing.blend \
            --python Tools/blender/export_art.py

or, from the repository root on Windows, through the wrapper that finds Blender
for you:

    pwsh -File Tools/export_art.ps1

Blender is NOT installed on the machine this was written on, so this script has
not been executed here.  It is written against the stable parts of the glTF
exporter and filters its own arguments against the operator's actual properties
(see `_supported`), which is what lets it survive the exporter's habit of
renaming options between Blender versions.

WHAT IT GUARANTEES
------------------
* One export per collection, always to the same two paths.
* The same axis convention and scale every time (+Y up, metres, scale 1), so a
  re-export can never silently move or resize the ring under the fighters.
* Modifiers applied, animation excluded, textures embedded in the .glb.
* A triangle count per collection, printed and checked against a budget, because
  the arena is already 57k triangles and this game has a phone at 20 fps as its
  open performance item.
"""

import os
import sys

import bpy

# Paths are resolved from the .blend's own location, so the script does not care
# what the working directory is when Blender is launched.
_BLEND_DIR = os.path.dirname(bpy.data.filepath) or os.getcwd()
_REPO_ROOT = os.path.abspath(os.path.join(_BLEND_DIR, ".."))
_ART_DIR = os.path.join(_REPO_ROOT, "Assets", "Art")

# collection name -> (output file, triangle budget)
#
# The budgets are the CURRENT counts rounded up, not aspirations: their job is
# to make an accidental tenfold increase loud, not to demand an optimisation
# nobody asked for.  Raise one deliberately when the art genuinely grows.
TARGETS = {
    "BoxingRing": ("BoxingRing.glb", 4000),
    "Arena": ("Arena.glb", 70000),
}


def _supported(operator_rna, kwargs):
    """Drop any keyword this Blender's exporter does not have.

    The glTF exporter renames and retires properties between releases
    (`export_selected` became `use_selection`, and so on).  Passing an unknown
    keyword is a hard error, so a script that names them all breaks on the next
    upgrade.  Filtering against the operator's own RNA means an option that has
    gone away is simply not sent, and the export still runs.
    """
    known = set(operator_rna.properties.keys())
    dropped = sorted(set(kwargs) - known)
    if dropped:
        print("  note: this Blender's glTF exporter has no " + ", ".join(dropped))
    return {key: value for key, value in kwargs.items() if key in known}


def _objects_in(collection):
    """Every object in a collection, including its child collections."""
    found = list(collection.objects)
    for child in collection.children:
        found.extend(_objects_in(child))
    return found


def _triangles(objects):
    """Triangle count after modifiers, evaluated the way the exporter sees it."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    total = 0
    for obj in objects:
        if obj.type != "MESH":
            continue
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        try:
            mesh.calc_loop_triangles()
            total += len(mesh.loop_triangles)
        finally:
            evaluated.to_mesh_clear()
    return total


def _select_only(objects):
    bpy.ops.object.select_all(action="DESELECT")
    selected = 0
    for obj in objects:
        # A hidden object cannot be selected, and the crowd is often hidden
        # while working on the ring.  Unhiding is safe: nothing is saved back to
        # the .blend by this script.
        obj.hide_set(False)
        obj.hide_viewport = False
        obj.select_set(True)
        selected += 1
    if selected:
        bpy.context.view_layer.objects.active = objects[0]
    return selected


def export_collection(name, filename, budget):
    collection = bpy.data.collections.get(name)
    if collection is None:
        print("FAIL: no collection named '%s' in %s" % (name, bpy.data.filepath))
        return False

    objects = _objects_in(collection)
    if not objects:
        print("FAIL: collection '%s' is empty" % name)
        return False

    count = _select_only(objects)
    tris = _triangles(objects)
    out_path = os.path.join(_ART_DIR, filename)

    print("exporting %-12s %4d objects  %7d tris -> %s" % (name, count, tris, out_path))
    if tris > budget:
        # Loud, and not fatal.  The export is still the artist's to make; this
        # is here so the growth is noticed at the moment it happens rather than
        # on a phone three weeks later.
        print("  WARNING: %d triangles is over the %d budget for %s" % (tris, budget, name))

    kwargs = _supported(
        bpy.ops.export_scene.gltf.get_rna_type(),
        dict(
            filepath=out_path,
            export_format="GLB",
            use_selection=True,
            export_apply=True,          # modifiers applied
            export_yup=True,            # Blender Z-up -> Unity/glTF Y-up
            export_materials="EXPORT",
            export_image_format="AUTO",  # textures embedded in the .glb
            export_animations=False,
            export_cameras=False,
            export_lights=False,
            export_extras=False,
            export_skins=False,
            export_morph=False,
        ),
    )
    bpy.ops.export_scene.gltf(**kwargs)

    if not os.path.exists(out_path):
        print("FAIL: exporter reported success but %s does not exist" % out_path)
        return False
    print("  wrote %.1f KB" % (os.path.getsize(out_path) / 1024.0))
    return True


def main():
    if not bpy.data.filepath:
        print("FAIL: run this with the .blend open, e.g.\n"
              "  blender --background ArtSource/BoxingRing.blend "
              "--python Tools/blender/export_art.py")
        return 2

    if not os.path.isdir(_ART_DIR):
        print("FAIL: %s does not exist — is this the PoBox repository?" % _ART_DIR)
        return 2

    # Object mode, or the exporter refuses.  Edit mode left over from a save is
    # the single most common reason a headless export fails.
    if bpy.context.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")

    ok = True
    for name, (filename, budget) in TARGETS.items():
        ok &= export_collection(name, filename, budget)

    print("EXPORT RESULT: %s" % ("ok" if ok else "FAILED"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
