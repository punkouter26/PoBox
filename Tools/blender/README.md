# The art round-trip

One Blender file is the source for both of the game's environment meshes, and
until 2026-09-14 nothing in this repository said so.

```
ArtSource/BoxingRing.blend
  ├── collection "BoxingRing"  ->  Assets/Art/BoxingRing.glb     734 tris, 3 materials
  └── collection "Arena"       ->  Assets/Art/Arena.glb       57,464 tris, 27 materials
```

That was worth writing down for the reason CLAUDE.md already records about the
deleted scene generators: **an artifact whose source cannot rebuild it is a way
to lose it.** The `.glb` files were committed, the `.blend` was committed, and
the step between them existed only in somebody's Blender session.

## What is already in the file

The instinct on being asked for "a better ring" is to model a ring. Most of it
is there. Read this before adding anything:

| In the `Arena` collection | |
|---|---|
| Ring furniture | `TurnbucklePad0..3`, `RingStep0..2` |
| Lighting rig | `Truss0..3`, `LEDStrip0..3`, `SpotHousing0..3`, `SpotLens0..3` |
| Big screen | `Jumbotron_Frame`, `_Pole`, `_Screen0..3` |
| Crowd | 300+ `Chair_*`, `Fan_*` and `Sil_*` objects on `Riser_*` tiers |
| Ringside | `Barrier_-4`/`Barrier_4`, `Commentator0/1`, `CommentatorTable`, `Stool0..3`, `Bucket`, `Towel`, `Crate0..2` |
| Building | `Wall_*`, `Ceiling`, `ConcreteGround`, `ArenaFloor`, `ExitSign_*` |

| In the `BoxingRing` collection | |
|---|---|
| The ring | canvas (textured, with a normal map), apron, posts — one 734-triangle mesh |

**The ropes are not modelled and must not be.** `Systems_RingRopes` builds them
at runtime as three rows of sprung capsule segments, so fighters sink into them
and get flung back. A modelled rope would be a rope that does nothing.

## Blender is not installed on this machine

Neither script here has been executed. They are written against the stable parts
of the API and `export_art.py` filters its own arguments against the exporter's
actual properties, so it should survive a version change — but the first run
needs a human to look at the result.

```powershell
winget install BlenderFoundation.Blender
```

## Exporting

```powershell
pwsh -File Tools/export_art.ps1            # finds Blender, exports both collections
```

or by hand:

```powershell
blender --background ArtSource/BoxingRing.blend --python Tools/blender/export_art.py
```

Both collections go every time, to the same two paths, with the same axis
convention (+Y up, metres, scale 1). That last part is the point: a re-export
cannot silently move or resize the ring under the fighters. The script prints a
triangle count per collection and warns past a budget — 4,000 for the ring,
70,000 for the arena. The budgets are the current counts rounded up, so an
accidental tenfold increase is loud; they are not a demand for an optimisation
nobody asked for.

Unity re-imports the `.glb` on focus. `Assets/Art/*.glb.meta` is committed, so
the import settings survive the re-export.

## Adding ring detail

```powershell
blender ArtSource/BoxingRing.blend --python Tools/blender/build_ring.py
```

Adds a canvas logo, an apron sponsor band and four post caps — the three things
genuinely missing from the ring itself. Every object it makes is named
`RingDetail_*` and every run deletes those first, so it is idempotent and touches
nothing else in the file.

**It does not save the `.blend`.** Look at what it made, then save. An artist's
file is not a script's to overwrite, which is the same rule the three
hand-authored Unity scenes live under.

Then re-export, or the change never reaches the game.
