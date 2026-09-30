"""Headless Blender build for the WR-1 scene assets.

Run through ``scripts/build-scenery.mjs`` or directly:

    blender.exe -b --factory-startup --python scripts/blender/build.py

Each asset exports standard Y-up glTF in scene units (1 unit = 2.46 metres).
Builder input is (x, scene-depth, height); core reflects depth into Blender's
right-handed Z-up coordinates before the normal Y-up glTF export. Joint meshes
are local to their translated hinges. Runtime animation uses Y for the service
arm and Z for the solar-wing swing.
"""

from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy
import scenery_core as core

import recovery
import satellite
import sites

OUTPUT_DIR = os.path.join(os.getcwd(), "public", "models", "scenery")

# LODs omit detail explicitly. Do not collapse thin plates, rings or trusses.
PAD_RATIOS = BARGE_RATIOS = SATELLITE_RATIOS = (1.0, 1.0, 1.0)


def joint_node(name: str, kind: str, **extras):
    """A pivot node carrying ``extras.joint`` for the runtime's lookups."""
    node = bpy.data.objects.new(name, None)
    node.empty_display_size = 1.0
    bpy.context.collection.objects.link(node)
    node["joint"] = kind
    for key, value in extras.items():
        node[key] = value
    return node


def reparent(objects, parent):
    for object_ in objects:
        core.link_parent(object_, parent)


def report(name, levels):
    print(f"ASSET {name}")
    for level in levels:
        print(f"  {level['lod']:<8} triangles={level['triangles']:<8} draws={level['draws']}")


def _world_bounds(object_):
    points = [object_.matrix_world @ vertex.co for vertex in object_.data.vertices]
    return points


def check_asset(name: str, root) -> None:
    """Fail the build on a contract break instead of shipping it to the browser."""
    problems = []
    levels = list(root.children)
    if len(levels) != 3:
        problems.append(f"expected 3 LOD groups, found {len(levels)}")

    joints = {}
    for object_ in bpy.data.objects:
        kind = object_.get("joint")
        if kind:
            joints.setdefault(kind, []).append(object_)
    expected = {"pad": {"arm": 3}, "barge": {}, "satellite": {"wing": 6, "panel": 18}}[name]
    for kind, count in expected.items():
        found = len(joints.get(kind, []))
        if found != count:
            problems.append(f"joint {kind!r}: expected {count}, found {found}")
    for kind in joints:
        if kind not in expected:
            problems.append(f"unexpected joint kind {kind!r}")

    if name == "barge":
        deck_height = recovery.DECK_Z
        for level in levels:
            stack = list(level.children)
            while stack:
                object_ = stack.pop()
                stack.extend(object_.children)
                if object_.type != "MESH" or not len(object_.data.vertices):
                    continue
                points = _world_bounds(object_)
                if max(point.z for point in points) <= deck_height + 0.1:
                    continue
                low_x, high_x = min(p.x for p in points), max(p.x for p in points)
                low_y, high_y = min(p.y for p in points), max(p.y for p in points)
                nearest_x = max(low_x, min(0.0, high_x))
                nearest_y = max(low_y, min(0.0, high_y))
                clearance = (nearest_x ** 2 + nearest_y ** 2) ** 0.5
                if clearance <= recovery.LANDING_RADIUS:
                    problems.append(
                        f"{object_.name}: rises above the deck only {clearance:.2f} m from the "
                        f"touchdown centre (needs > {recovery.LANDING_RADIUS} m)")

    if problems:
        raise SystemExit(f"CONTRACT FAILED for {name}:\n  " + "\n  ".join(problems))
    print(f"  contract OK ({sum(len(v) for v in joints.values())} joints)")


# --------------------------------------------------------------------------
# Pad: launch complex and surrounding facilities
# --------------------------------------------------------------------------

def build_pad(root, detail: int, ratio: float, materials: dict):
    parts = [
        (core.Part("Mount"), 0.35, None, ratio),
        (core.Part("Trench"), 0.30, None, ratio),
        (core.Part("Duct"), 0.30, None, ratio),
        (core.Part("Tower"), 0.40, None, ratio),
        (core.Part("Masts"), 0.35, None, ratio),
        (core.Part("Deluge"), 0.40, None, ratio),
        (core.Part("Cryo"), 0.45, None, ratio),
        (core.Part("Roads"), 0.0625, None, ratio),
        (core.Part("Buildings"), 0.35, None, ratio),
        (core.Part("Utilities"), 0.45, None, ratio),
    ]
    by_name = {part.name: part for part, _, _, _ in parts}

    sites.launch_mount(by_name["Mount"], detail)
    sites.flame_trench(by_name["Trench"], detail)
    sites.exhaust_duct(by_name["Duct"], detail)
    sites.service_tower(by_name["Tower"], detail)
    sites.lightning_masts(by_name["Masts"], detail)
    sites.deluge_system(by_name["Deluge"], detail)
    sites.cooling_and_gas(by_name["Cryo"], detail)
    sites.transfer_roads(by_name["Roads"], detail)
    sites.site_buildings(by_name["Buildings"], detail)
    sites.utilities(by_name["Utilities"], detail)

    level = core.assemble(f"LOD{detail}", parts, materials)
    core.link_parent(level, root)

    # The arm mesh is local to its real tower-side pivot.
    arm = core.Part("Arm")
    sites.service_arm(arm, detail)
    arm_group = core.assemble(f"LOD{detail}-Arm", [(arm, 0.5, None, ratio)], materials)
    pivot = joint_node("ServiceArm", "arm")
    x, depth, height = sites.ARM_PIVOT
    pivot.location = (x, -depth, height)
    core.link_parent(arm_group, pivot)
    core.link_parent(arm_group.parent, level)
    return level


# --------------------------------------------------------------------------
# Recovery ship
# --------------------------------------------------------------------------

def build_barge(root, detail: int, ratio: float, materials: dict):
    hull = core.Part("Hull")
    port = core.Part("BulwarkPort")
    starboard = core.Part("BulwarkStarboard")
    bow = core.Part("BulwarkBow")
    stern = core.Part("BulwarkStern")
    deck = core.Part("Deck")
    superstructure = core.Part("Superstructure")
    fittings = core.Part("Fittings")
    recovery.recovery_ship(hull, deck, superstructure, fittings, detail,
                           port=port, starboard=starboard, bow=bow, stern=stern)
    # A material is only split when its own plan footprint reaches into the
    # touchdown keep-out zone the runtime asserts; anything already clear stays
    # whole, which is what keeps the ship's draw count proportional to its
    # palette instead of to its part count.
    def clears(low_x, high_x, low_y, high_y):
        return recovery.footprint_clearance(low_x, high_x, low_y, high_y) > recovery.LANDING_RADIUS

    guard = (recovery.SPLIT_SPAN, recovery.KEEP_WHOLE, clears)
    parts = [
        (hull, 0.22, None, ratio, *guard),
        (deck, 0.026, core.DECK_RASTER, ratio, *guard),
        (superstructure, 0.30, None, ratio, *guard),
        (fittings, 0.45, None, ratio, *guard),
        (port, 0.30, None, ratio, *guard),
        (starboard, 0.30, None, ratio, *guard),
        (bow, 0.30, None, ratio, *guard),
        (stern, 0.30, None, ratio, *guard),
    ]
    level = core.assemble(f"LOD{detail}", parts, materials)
    core.link_parent(level, root)
    return level


# --------------------------------------------------------------------------
# Satellite
# --------------------------------------------------------------------------

def build_satellite(root, detail: int, ratio: float, materials: dict):
    body = core.Part("Bus")
    mechanism = core.Part("Mechanism")
    satellite.satellite_bus(body, mechanism, detail)

    level = core.assemble(f"LOD{detail}",
                          [(body, 0.55, None, ratio), (mechanism, 0.8, None, ratio)],
                          materials)
    core.link_parent(level, root)

    # Separate local leaves under a translated hinge; the native master opens
    # fully deployed and the runtime applies the stowed pose before first paint.
    for side, label in ((-1, "SolarWingLeft"), (1, "SolarWingRight")):
        wing = core.Part(label)
        satellite.solar_wing(wing, side, detail)
        if side < 0:
            wing = wing.mirror_onto()
        wing_group = core.assemble(f"LOD{detail}-{label}",
                                   [(wing, 1.1, None, ratio)], materials)
        hinge = joint_node(label, "wing", side=side)
        hinge.location = (side * satellite.HINGE_X, 0, satellite.HINGE_Z)
        core.link_parent(hinge, level)
        core.link_parent(wing_group, hinge)
        for index in range(satellite.PANEL_COUNT):
            leaf = core.Part(f"{label}-{index}")
            satellite.solar_panel(leaf, index, detail)
            if side < 0:
                leaf = leaf.mirror_onto()
            leaf_group = core.assemble(f"LOD{detail}-{label}-{index}",
                                       [(leaf, 1.1, None, ratio)], materials)
            panel_joint = joint_node(f"{label}-panel-{index}", "panel", side=side, index=index)
            panel_joint.location = (side * (satellite.PANEL_STOWED_X + index * satellite.PANEL_PITCH), 0, 0)
            core.link_parent(panel_joint, hinge)
            core.link_parent(leaf_group, panel_joint)
    return level


BUILDERS = {
    "pad": (build_pad, sites.declare_materials, PAD_RATIOS),
    "barge": (build_barge, recovery.declare_materials, BARGE_RATIOS),
    "satellite": (build_satellite, satellite.declare_materials, SATELLITE_RATIOS),
}


def build_asset(name: str) -> dict:
    builder, declare, ratios = BUILDERS[name]
    core.reset_scene()
    declare()
    materials = core.material_library()
    root = core.asset_root(name)
    for detail, ratio in enumerate(ratios):
        builder(root, detail, ratio, materials)
    levels = core.summarise(root)
    report(name, levels)
    check_asset(name, root)
    # Nonrepeating maps: a complete deck / solar atlas on each physical surface.
    for obj in bpy.data.objects:
        if obj.type != 'MESH' or not obj.data.uv_layers:
            continue
        mesh = obj.data
        for poly in mesh.polygons:
            material = mesh.materials[poly.material_index].name
            if material not in {'Barge-deck-paint', 'Sat-cells'}:
                continue
            width, depth = (recovery.BEAM, recovery.LENGTH) if name == 'barge' else (satellite.PANEL_U-.065, satellite.PANEL_V-.065)
            for loop in poly.loop_indices:
                v = mesh.vertices[mesh.loops[loop].vertex_index].co
                mesh.uv_layers.active.data[loop].uv = (v.x / width + .5, -v.y / depth + .5)
    root['coordinateSystem'] = 'Y_UP'
    root['metresPerUnit'] = 2.46
    # Save an editable native master before exporting. All LODs remain in the file.
    master = os.path.join(os.getcwd(), 'artifacts', 'scenery-blend', name + '.blend')
    os.makedirs(os.path.dirname(master), exist_ok=True)
    hidden = []
    for level in root.children:
        if level.name != 'LOD0':
            hidden.extend([level, *level.children_recursive])
    for obj in hidden:
        obj.hide_viewport = obj.hide_render = True
    bpy.ops.wm.save_as_mainfile(filepath=master)
    for obj in hidden:
        obj.hide_viewport = obj.hide_render = False
    path = os.path.join(OUTPUT_DIR, f"{name}.glb")
    size = core.export_asset(path, root)
    return {"name": name, "bytes": size, "levels": levels}


def main() -> None:
    selected = sys.argv[sys.argv.index("--assets") + 1].split(",") \
        if "--assets" in sys.argv else list(BUILDERS)
    results = [build_asset(name) for name in selected]
    # Written to artifacts, not the shipped directory: validate-scenery.mjs owns
    # the manifest that the runtime and tests actually read.
    destination = os.path.join(os.getcwd(), "artifacts", "blender-build.json")
    os.makedirs(os.path.dirname(destination), exist_ok=True)
    with open(destination, "w", encoding="utf-8") as handle:
        json.dump({"assets": results}, handle, indent=2)
    print("BUILD-OK", json.dumps(results))


main()
