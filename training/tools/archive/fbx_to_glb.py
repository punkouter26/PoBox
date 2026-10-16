"""Convert an .fbx to a .glb with Blender, so glb_to_rig.py can read its skeleton.

    blender-launcher --background --python tools/fbx_to_glb.py -- input.fbx output.glb

Blender is only the converter: nothing is edited, the source file is not touched.
"""
import sys

import bpy

args = sys.argv[sys.argv.index("--") + 1:]
src, dst = args[0], args[1]

bpy.ops.wm.read_factory_settings(use_empty=True)
try:
    bpy.ops.wm.fbx_import(filepath=src)          # Blender 4.5 and later
except Exception as first:
    try:
        bpy.ops.import_scene.fbx(filepath=src)   # the older add-on
    except Exception as second:
        print(f"FBX import failed: {first} / {second}")
        sys.exit(1)

armatures = [o for o in bpy.data.objects if o.type == "ARMATURE"]
meshes = [o for o in bpy.data.objects if o.type == "MESH"]
print(f"imported {len(armatures)} armature(s), {len(meshes)} mesh(es)")
for a in armatures:
    print(f"  armature {a.name}: {len(a.data.bones)} bones, scale {tuple(round(s, 4) for s in a.scale)}")

bpy.ops.export_scene.gltf(filepath=dst, export_format="GLB", export_apply=False, export_animations=False)
print(f"wrote {dst}")
