"""Add the detail the ring is missing, in Blender, reproducibly.

READ THIS BEFORE RUNNING IT
---------------------------
Most of what a boxing scene needs is ALREADY in
`ArtSource/BoxingRing.blend`, and it is worth knowing what, because the obvious
instinct is to model it again:

    already in the `Arena` collection   TurnbucklePad0..3, RingStep0..2,
                                        Barrier_-4/4, Truss0..3, LEDStrip0..3,
                                        SpotHousing/SpotLens0..3, Jumbotron
                                        (frame, pole, 4 screens), 300+ crowd
                                        silhouettes and chairs, Commentator0/1
                                        and their table, stools, bucket, towel,
                                        crates, exit signs, walls, ceiling
    already in the `BoxingRing`         canvas (textured, with a normal map),
    collection                          apron, posts -- 734 triangles total
    already at RUNTIME, in Unity        the ropes, which are simulated, not
                                        modelled: Systems_RingRopes builds three
                                        rows of sprung capsule segments so
                                        fighters sink into them and get flung
                                        back

So this script does NOT rebuild the ring.  It adds the three things that are
genuinely absent, all of them on the ring itself:

    RingDetail_CanvasLogo    a logo disc inset into the canvas centre
    RingDetail_ApronBand     a sponsor band around the apron face
    RingDetail_PostCap0..3   a domed cap on each corner post

IDEMPOTENT.  Every object it makes is named `RingDetail_*` and every run deletes
those before rebuilding.  Nothing else in the file is touched, which is what
makes it safe to run against a .blend somebody has since hand-tuned -- the
failure this project already recorded for its scene generators.

    blender ArtSource/BoxingRing.blend --python Tools/blender/build_ring.py
    # then, to make the change reach Unity:
    blender --background ArtSource/BoxingRing.blend \
            --python Tools/blender/export_art.py

It does NOT save the .blend.  Look at what it made, then save -- an artist's
file is not a script's to overwrite.

NOT RUN HERE.  Blender is not installed on the machine this was written on.  The
dimensions below are read off the exported mesh (`Assets/Art/BoxingRing.glb`:
bounds -3.5..3.5 on X and Z, 0..2.545 on Y) and off Systems_RingRopes, which
pins its rope anchors at +/-2.95 and says in its own comment that the post
centres sit at +/-3.05 around a 6.1 m canvas.
"""

import math

import bpy

PREFIX = "RingDetail_"
COLLECTION = "BoxingRing"

# Read off the exported ring; see the module docstring.
POST_HALF_EXTENT = 3.05     # post centres, metres from ring centre
CANVAS_HALF = 3.05          # canvas half-width
CANVAS_TOP = 0.0            # the canvas plane in the .blend's local space
APRON_TOP = CANVAS_TOP      # apron face hangs below the canvas
APRON_DROP = 0.55           # how far down the apron face runs

LOGO_RADIUS = 1.15
LOGO_LIFT = 0.004           # clear of the canvas without floating
LOGO_SEGMENTS = 24          # 24 -> 24 triangles as a fan; plenty at this size

BAND_HEIGHT = 0.28
BAND_LIFT = 0.16            # down from the canvas edge, centred on the apron
BAND_BULGE = 0.012          # stands proud of the apron so it cannot z-fight

CAP_RADIUS = 0.085
CAP_SEGMENTS = 12
POST_TOP = 1.55             # top of the corner posts


def _collection():
    existing = bpy.data.collections.get(COLLECTION)
    if existing is None:
        raise RuntimeError(
            "no collection named '%s' — is this ArtSource/BoxingRing.blend?" % COLLECTION)
    return existing


def _clear_previous():
    """Delete everything this script has ever made, and nothing else."""
    removed = 0
    for obj in [o for o in bpy.data.objects if o.name.startswith(PREFIX)]:
        bpy.data.objects.remove(obj, do_unlink=True)
        removed += 1
    # Orphaned meshes would otherwise accumulate one set per run and quietly
    # grow the .blend for ever.
    for mesh in [m for m in bpy.data.meshes if m.name.startswith(PREFIX) and m.users == 0]:
        bpy.data.meshes.remove(mesh)
    if removed:
        print("cleared %d previous %s object(s)" % (removed, PREFIX))


def _material(name, colour, roughness=0.6, metallic=0.0):
    """Reuse the ring's own materials where they exist; make one where they do not."""
    existing = bpy.data.materials.get(name)
    if existing is not None:
        return existing
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    principled = material.node_tree.nodes.get("Principled BSDF")
    if principled is not None:
        principled.inputs["Base Color"].default_value = colour
        principled.inputs["Roughness"].default_value = roughness
        principled.inputs["Metallic"].default_value = metallic
    return material


def _new_mesh(name, verts, faces, material):
    mesh = bpy.data.meshes.new(PREFIX + name)
    mesh.from_pydata(verts, [], faces)
    mesh.validate()
    mesh.update()
    if material is not None:
        mesh.materials.append(material)
    obj = bpy.data.objects.new(PREFIX + name, mesh)
    _collection().objects.link(obj)
    return obj


def build_canvas_logo():
    """A flat disc in the middle of the canvas.

    A fan rather than a textured quad: the canvas already carries a texture and
    a normal map, and painting a logo into them would mean re-exporting two
    images to change it.  Geometry keeps the logo a thing you can select, move
    and recolour.
    """
    material = _material("Ring_Logo", (0.72, 0.13, 0.13, 1.0), roughness=0.75)
    verts = [(0.0, 0.0, CANVAS_TOP + LOGO_LIFT)]
    for index in range(LOGO_SEGMENTS):
        angle = (index / LOGO_SEGMENTS) * math.tau
        verts.append((math.cos(angle) * LOGO_RADIUS,
                      math.sin(angle) * LOGO_RADIUS,
                      CANVAS_TOP + LOGO_LIFT))
    faces = [(0, i + 1, ((i + 1) % LOGO_SEGMENTS) + 1) for i in range(LOGO_SEGMENTS)]
    obj = _new_mesh("CanvasLogo", verts, faces, material)
    print("  CanvasLogo      %d tris" % len(faces))
    return obj


def build_apron_band():
    """A sponsor band around the four apron faces.

    One object, four quads, eight triangles.  Modelled slightly proud of the
    apron rather than coplanar with it: two surfaces at the same depth flicker
    against each other at distance, and the drama camera works at distance.
    """
    material = _material("Ring_ApronBand", (0.95, 0.78, 0.22, 1.0), roughness=0.45)
    reach = CANVAS_HALF + BAND_BULGE
    top = APRON_TOP - BAND_LIFT
    bottom = top - BAND_HEIGHT

    verts = []
    faces = []
    # Four sides, each a quad: +X, -X, +Y, -Y.
    for axis, sign in ((0, 1), (0, -1), (1, 1), (1, -1)):
        base = len(verts)
        for far, height in ((-reach, top), (reach, top), (reach, bottom), (-reach, bottom)):
            if axis == 0:
                verts.append((sign * reach, far, height))
            else:
                verts.append((far, sign * reach, height))
        faces.append((base, base + 1, base + 2, base + 3))
    obj = _new_mesh("ApronBand", verts, faces, material)
    print("  ApronBand       %d tris" % (len(faces) * 2))
    return obj


def build_post_caps():
    """A dome on top of each corner post.

    The posts are flat-topped cylinders, which reads as unfinished from the
    winner camera's low angle — the one shot that ever looks up at them.
    """
    material = _material("Post_Metal", (0.55, 0.55, 0.58, 1.0),
                         roughness=0.35, metallic=0.9)
    made = []
    for index, (x_sign, y_sign) in enumerate(((1, 1), (1, -1), (-1, 1), (-1, -1))):
        centre_x = x_sign * POST_HALF_EXTENT
        centre_y = y_sign * POST_HALF_EXTENT
        verts = [(centre_x, centre_y, POST_TOP + CAP_RADIUS)]
        for segment in range(CAP_SEGMENTS):
            angle = (segment / CAP_SEGMENTS) * math.tau
            verts.append((centre_x + math.cos(angle) * CAP_RADIUS,
                          centre_y + math.sin(angle) * CAP_RADIUS,
                          POST_TOP))
        faces = [(0, i + 1, ((i + 1) % CAP_SEGMENTS) + 1) for i in range(CAP_SEGMENTS)]
        made.append(_new_mesh("PostCap%d" % index, verts, faces, material))
    print("  PostCap0..3     %d tris" % (CAP_SEGMENTS * 4))
    return made


def main():
    print("build_ring: adding detail to the '%s' collection" % COLLECTION)
    _clear_previous()
    build_canvas_logo()
    build_apron_band()
    build_post_caps()
    total = LOGO_SEGMENTS + 8 + CAP_SEGMENTS * 4
    print("build_ring: added %d triangles. The .blend is NOT saved — "
          "look at it, then save." % total)


if __name__ == "__main__":
    main()
