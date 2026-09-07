"""Check a training scene for fall detectors with holes in them.

WHY. Reward_Locomotion dereferences every entry of _fallContacts on every
FixedUpdate. A null there does not crash the run -- it throws on that one
fighter, 50 times a second, so the fighter earns EXACTLY ZERO reward for the
whole run while the trainer keeps reporting a healthy-looking mean over the
fighters that still work. Measured once at ten of sixteen fighters dead for
46,784 consecutive ticks, which from the outside is indistinguishable from
"these rigs are hard to train".

The scene is a GENERATED artifact, so the defect arrives the moment someone
regenerates it -- the documented way to maintain it -- and the committed scene
being healthy proves nothing about the tool that is supposed to produce it.
This reads the scene YAML directly, so it needs no Unity and can run before a
build rather than after a wasted one.

Usage:
    python Tools/verify_train_scene.py [Assets/Scenes/SCN_TRAIN_LOCOMOTION.unity]
Exit code 1 if any fighter has a null or short fall-contact array.
"""
import re
import sys

DEFAULT_SCENE = "Assets/Scenes/SCN_TRAIN_LOCOMOTION.unity"
# Torso, head, both lower legs, both gloves. Mirrors MIN_FALL_CONTACTS in
# RigTool_LocomotionScene.
MIN_FALL_CONTACTS = 6


def parse(path):
    """fileID -> (classId, body), plus GameObject names and component owners."""
    text = open(path, encoding="utf-8", errors="replace").read()
    parts = re.split(r"\n--- !u!(\d+) &(\d+)[^\n]*\n", text)
    objects, names, owner = {}, {}, {}
    for index in range(1, len(parts) - 2, 3):
        class_id, file_id, body = parts[index], parts[index + 1], parts[index + 2]
        objects[file_id] = (class_id, body)
        if class_id == "1":
            match = re.search(r"^  m_Name: (.*)$", body, re.M)
            names[file_id] = match.group(1).strip() if match else "?"
        else:
            match = re.search(r"^  m_GameObject: \{fileID: (\d+)\}", body, re.M)
            if match:
                owner[file_id] = match.group(1)
    return objects, names, owner


def main():
    scene = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SCENE
    objects, names, owner = parse(scene)

    failures = 0
    fighters = 0
    for _file_id, (_class_id, body) in objects.items():
        if "_fallContacts:" not in body:
            continue
        fighters += 1
        block = body.split("_fallContacts:", 1)[1]
        # The array runs until the next key at the same indentation.
        block = re.split(r"\n  [A-Za-z_]", block, 1)[0]
        ids = re.findall(r"fileID: (-?\d+)", block)
        resolved = []
        for value in ids:
            if value == "0":
                resolved.append("NULL")
            else:
                resolved.append(names.get(owner.get(value, ""), "?"))
        bad = [r for r in resolved if r in ("NULL", "?")]
        if bad or len(resolved) < MIN_FALL_CONTACTS:
            failures += 1
            print(f"  FAIL  {len(resolved)} contacts {resolved}")

    print(f"{scene}: {fighters} fighters with a fall detector, {failures} defective")
    if fighters == 0:
        print("  no fighters found - wrong scene, or the component was renamed")
        return 1
    if failures:
        print("  Regenerate with RigTool_LocomotionScene, which resolves fall contacts")
        print("  from the rig rather than by hardcoded joint index.")
        return 1
    print("  OK - every fighter has a complete fall detector")
    return 0


if __name__ == "__main__":
    sys.exit(main())
