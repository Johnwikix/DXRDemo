"""Shared Blender-side building blocks for the WR-1 scene assets.

Geometry builders use (x, scene-depth, height), measured in scene units:
1 unit = 2.46 metres. _passthrough converts to native Blender (x,-depth,height);
standard glTF Y-up export then yields runtime (x,height,depth).
"""

from __future__ import annotations

import math
import os
from collections import OrderedDict

import bmesh
import bpy
from mathutils import Matrix, Vector

TAU = math.pi * 2


# --------------------------------------------------------------------------
# Colour helpers
# --------------------------------------------------------------------------

def srgb_to_linear(value: float) -> float:
    return value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4


def hex_rgba(code: str) -> tuple[float, float, float, float]:
    code = code.lstrip("#")
    channels = [int(code[i:i + 2], 16) / 255.0 for i in (0, 2, 4)]
    return tuple(srgb_to_linear(channel) for channel in channels) + (1.0,)


# --------------------------------------------------------------------------
# Material palette
# --------------------------------------------------------------------------
# ``cost`` is the draw-call weight of one material: the exporter emits one
# primitive per material per joint group, so this is what the runtime pays.
PALETTE: "OrderedDict[str, dict]" = OrderedDict()


def surface(name: str, color: str, roughness: float, metallic: float, *, alpha: float = 1.0,
            emission: tuple[str, float] | None = None, cost: float = 1.0,
            texture=None) -> str:
    """Declare one material in the shared palette and return its name.

    ``texture`` is a zero-argument callable returning a Blender image; it is
    wired to Base Color so the surface tiles a real map instead of a flat tint.
    """
    existing = PALETTE.get(name)
    if existing and existing["color"] != color:
        raise ValueError(f"material {name!r} redeclared with a different colour")
    PALETTE[name] = {
        "color": color,
        "roughness": roughness,
        "metallic": metallic,
        "alpha": alpha,
        "emission": emission,
        "cost": cost,
        "texture": texture,
    }
    return name


def _principled_input(bsdf, *candidates):
    for key in candidates:
        if key in bsdf.inputs:
            return bsdf.inputs[key]
    return None


def make_material(entry: dict, raster: dict | None = None):
    material = bpy.data.materials.new(entry["name"])
    material.use_nodes = True
    bsdf = next(node for node in material.node_tree.nodes if node.type == 'BSDF_PRINCIPLED')
    bsdf.inputs["Base Color"].default_value = hex_rgba(entry["color"])
    bsdf.inputs["Roughness"].default_value = entry["roughness"]
    bsdf.inputs["Metallic"].default_value = entry["metallic"]
    alpha_input = _principled_input(bsdf, "Alpha")
    if alpha_input is not None:
        alpha_input.default_value = entry["alpha"]
    if entry["emission"]:
        colour, strength = entry["emission"]
        emission_colour = _principled_input(bsdf, "Emission Color", "Emission")
        emission_strength = _principled_input(bsdf, "Emission Strength")
        if emission_colour is not None:
            emission_colour.default_value = hex_rgba(colour)
        if emission_strength is not None:
            emission_strength.default_value = strength
    if entry["alpha"] < 1.0:
        for attribute, value in (("blend_method", "BLEND"),
                                 ("surface_render_method", "BLENDED")):
            if hasattr(material, attribute):
                setattr(material, attribute, value)
    if entry.get("texture"):
        bsdf.inputs["Base Color"].default_value = (1, 1, 1, 1)
        image = entry["texture"]()
        image.colorspace_settings.name = "sRGB"
        sampler = material.node_tree.nodes.new("ShaderNodeTexImage")
        sampler.image = image
        sampler.interpolation = "Linear"
        sampler.location = (-420.0, 240.0)
        material.node_tree.links.new(sampler.outputs["Color"], bsdf.inputs["Base Color"])
    if raster:
        material["raster"] = raster
    return material


# --------------------------------------------------------------------------
# Part accumulation
# --------------------------------------------------------------------------

def _location_vector(location) -> Vector:
    """Accept 2- or 3-tuples; a 2-tuple places the part on the z = 0 plane."""
    values = tuple(location)
    if len(values) == 2:
        return Vector((values[0], values[1], 0.0))
    if len(values) == 3:
        return Vector(values)
    raise ValueError(f"location must have 2 or 3 components, got {len(values)}")


def _rotation_matrix(rotation) -> Matrix:
    """Always a 3x3 matrix, so vertex transforms stay three-dimensional."""
    if rotation is None:
        return Matrix.Identity(3)
    if isinstance(rotation, Matrix):
        return rotation.to_3x3()
    if len(rotation) == 3:
        # Euler XYZ in radians, applied X then Y then Z.
        return (Matrix.Rotation(rotation[2], 3, "Z")
                @ Matrix.Rotation(rotation[1], 3, "Y")
                @ Matrix.Rotation(rotation[0], 3, "X"))
    raise ValueError("rotation must be a 3-tuple of radians or a Matrix")


class Part:
    """One object under construction: named material buckets of raw geometry."""

    def __init__(self, name: str, joint: str | None = None):
        self.name = name
        self.joint = joint
        self._buckets: "OrderedDict[str, tuple[list, list]]" = OrderedDict()

    # -- accumulation ------------------------------------------------------
    def add(self, material: str, verts, faces) -> None:
        if material not in PALETTE:
            raise KeyError(f"{self.name}: undeclared material {material!r}")
        bucket = self._buckets.get(material)
        if bucket is None:
            bucket = ([], [])
            self._buckets[material] = bucket
        vertices, polygons = bucket
        offset = len(vertices)
        vertices.extend(verts)
        polygons.extend(tuple(index + offset for index in face) for face in faces)

    def empty(self) -> bool:
        return not self._buckets

    def triangle_count(self) -> int:
        return sum(len(face) - 2 for _, faces in self._buckets.values() for face in faces)

    # -- primitives --------------------------------------------------------
    def box(self, material, width, depth, height, location=(0, 0, 0), rotation=None) -> None:
        x, y, z = location
        hw, hd, hh = width / 2.0, depth / 2.0, height / 2.0
        corners = [(-hw, -hd, -hh), (hw, -hd, -hh), (hw, hd, -hh), (-hw, hd, -hh),
                   (-hw, -hd, hh), (hw, -hd, hh), (hw, hd, hh), (-hw, hd, hh)]
        faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
                 (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
        self._emit(material, corners, faces, location, rotation)

    def prism(self, material, corners_xy, z0, z1, location=(0, 0, 0), rotation=None) -> None:
        """Extrude a closed XZ-agnostic polygon given in the XY plane along Z."""
        count = len(corners_xy)
        verts = [(x, y, z0) for x, y in corners_xy] + [(x, y, z1) for x, y in corners_xy]
        faces = [tuple(range(count - 1, -1, -1)), tuple(range(count, count * 2))]
        for index in range(count):
            nxt = (index + 1) % count
            faces.append((index, nxt, nxt + count, index + count))
        self._emit(material, verts, faces, location, rotation)

    def bevel_box(self, material, width, depth, height, location=(0, 0, 0), bevel=0.04):
        """Closed chamfered plate/cabinet with real edge highlights."""
        b = min(bevel, min(width, depth, height) * .24)
        def outline(w, d):
            return [(-w/2+b,-d/2),(w/2-b,-d/2),(w/2,-d/2+b),(w/2,d/2-b),
                    (w/2-b,d/2),(-w/2+b,d/2),(-w/2,d/2-b),(-w/2,-d/2+b)]
        verts = []
        for w, d, z in [(width-2*b,depth-2*b,-height/2),
                         (width,depth,-height/2+b),(width,depth,height/2-b),
                         (width-2*b,depth-2*b,height/2)]:
            verts.extend((x,y,z) for x,y in outline(w,d))
        faces = [tuple(range(7,-1,-1)),tuple(range(24,32))]
        for ring in range(3):
            for i in range(8):
                j=(i+1)%8
                faces.append((ring*8+i,ring*8+j,(ring+1)*8+j,(ring+1)*8+i))
        self._emit(material, verts, faces, location, None)

    def frustum(self, material, half_bottom, half_top, z0, z1, location=(0, 0, 0), rotation=None) -> None:
        """Square tapered shaft: half_bottom/half_top are (half_x, half_y)."""
        bx, by = half_bottom
        tx, ty = half_top
        verts = [(-bx, -by, z0), (bx, -by, z0), (bx, by, z0), (-bx, by, z0),
                 (-tx, -ty, z1), (tx, -ty, z1), (tx, ty, z1), (-tx, ty, z1)]
        faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
                 (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
        self._emit(material, verts, faces, location, rotation)

    def cylinder(self, material, radius, z0, z1, location=(0, 0, 0), rotation=None,
                 segments=24, radius_top=None, cap_bottom=True, cap_top=True) -> None:
        top = radius if radius_top is None else radius_top
        verts, faces = [], []
        for index in range(segments):
            angle = TAU * index / segments
            verts.append((math.cos(angle) * radius, math.sin(angle) * radius, z0))
        for index in range(segments):
            angle = TAU * index / segments
            verts.append((math.cos(angle) * top, math.sin(angle) * top, z1))
        for index in range(segments):
            nxt = (index + 1) % segments
            faces.append((index, nxt, nxt + segments, index + segments))
        if cap_bottom:
            faces.append(tuple(range(segments - 1, -1, -1)))
        if cap_top:
            faces.append(tuple(range(segments, segments * 2)))
        self._emit(material, verts, faces, location, rotation)

    def tube(self, material, radius, thickness, z0, z1, location=(0, 0, 0), rotation=None,
             segments=24) -> None:
        inner = radius - thickness
        verts, faces = [], []
        for z in (z0, z1):
            for index in range(segments):
                angle = TAU * index / segments
                verts.append((math.cos(angle) * radius, math.sin(angle) * radius, z))
            for index in range(segments):
                angle = TAU * index / segments
                verts.append((math.cos(angle) * inner, math.sin(angle) * inner, z))
        outer_bottom, inner_bottom, outer_top, inner_top = 0, segments, segments * 2, segments * 3
        for index in range(segments):
            nxt = (index + 1) % segments
            faces.append((outer_bottom + index, outer_bottom + nxt, outer_top + nxt, outer_top + index))
            faces.append((inner_bottom + nxt, inner_bottom + index, inner_top + index, inner_top + nxt))
            faces.append((outer_top + index, outer_top + nxt, inner_top + nxt, inner_top + index))
            faces.append((outer_bottom + nxt, outer_bottom + index, inner_bottom + index, inner_bottom + nxt))
        self._emit(material, verts, faces, location, rotation)

    def beam(self, material, start, end, radius, segments=8, caps=True) -> None:
        """Cylinder spanning two points; the structural workhorse."""
        a, b = Vector(start), Vector(end)
        direction = b - a
        length = direction.length
        if length <= 1e-6:
            return
        rotation = Vector((0, 0, 1)).rotation_difference(direction.normalized()).to_matrix()
        self.cylinder(material, radius, 0.0, length, location=tuple(a), rotation=rotation,
                      segments=segments, cap_bottom=caps, cap_top=caps)

    def lattice(self, material, a, b, section, segments, radius, alternate=True) -> None:
        """Square lattice mast between two points with real cross bracing."""
        a, b = Vector(a), Vector(b)
        axis = (b - a)
        length = axis.length
        if length <= 1e-6:
            return
        up = axis.normalized()
        reference = Vector((0, 0, 1)) if abs(up.z) < 0.9 else Vector((1, 0, 0))
        side = up.cross(reference).normalized()
        other = side.cross(up).normalized()
        corners = [side + other, side - other, -side - other, -side + other]
        for level in range(segments + 1):
            t = level / segments
            centre = a + axis * t
            for corner in corners:
                self.beam(material, tuple(centre + corner * section),
                          tuple(a + axis * min(1.0, (level + 1) / segments) + corner * section),
                          radius, segments=6)
        for level in range(segments):
            t0, t1 = level / segments, (level + 1) / segments
            for index in range(4):
                nxt = (index + 1) % 4
                p0 = a + axis * t0 + corners[index] * section
                p1 = a + axis * t1 + corners[nxt] * section
                p2 = a + axis * t0 + corners[nxt] * section
                p3 = a + axis * t1 + corners[index] * section
                self.beam(material, tuple(p0), tuple(p1), radius * 0.45, segments=5)
                if alternate:
                    self.beam(material, tuple(p2), tuple(p3), radius * 0.45, segments=5)

    def instance(self, other: "Part", offset=(0, 0, 0), rotation=None, material_map=None) -> None:
        """Copy another part's buckets in, optionally remapping materials."""
        matrix = _rotation_matrix(rotation)
        shift = _location_vector(offset)
        for material, (vertices, faces) in other._buckets.items():
            target = material_map.get(material, material) if material_map else material
            moved = [tuple(matrix @ Vector(vertex) + shift) for vertex in vertices]
            self.add(target, moved, faces)

    def mirror_x(self, material_map=None) -> None:
        """Duplicate everything mirrored across the YZ plane (port/starboard)."""
        snapshot = [(material, list(vertices), list(faces))
                    for material, (vertices, faces) in self._buckets.items()]
        for material, vertices, faces in snapshot:
            target = material_map.get(material, material) if material_map else material
            flipped = [(-x, y, z) for x, y, z in vertices]
            reversed_faces = [tuple(reversed(face)) for face in faces]
            self.add(target, flipped, reversed_faces)

    def mirror_onto(self) -> "Part":
        """A mirrored copy of this part, with winding reversed for outward faces."""
        mirrored = Part(self.name, self.joint)
        mirrored.mirror_x_from(self)
        return mirrored

    def mirror_x_from(self, other: "Part") -> None:
        """Replace this part's buckets with ``other`` reflected across YZ."""
        self._buckets.clear()
        for material, (vertices, faces) in other._buckets.items():
            flipped = [(-x, y, z) for x, y, z in vertices]
            reversed_faces = [tuple(reversed(face)) for face in faces]
            self.add(material, flipped, reversed_faces)

    def _emit(self, material, verts, faces, location, rotation) -> None:
        matrix = _rotation_matrix(rotation)
        shift = _location_vector(location)
        moved = [tuple(matrix @ Vector(vertex) + shift) for vertex in verts]
        self.add(material, moved, faces)


def _passthrough(part: "Part") -> "Part":
    """Plan (x, depth, height) -> Blender (x, -depth, height).

    Standard glTF Y-up export produces (x, height, depth), matching the terrain
    and flight rig. Reflection requires reversing the polygon winding.
    """
    converted = Part(part.name, part.joint)
    for material, (vertices, faces) in part._buckets.items():
        converted.add(material, [(x, -y, z) for x, y, z in vertices],
                      [tuple(reversed(face)) for face in faces])
    return converted


# --------------------------------------------------------------------------
# Object construction
# --------------------------------------------------------------------------

def build_mesh(part: Part, materials: dict, name: str):
    """Turn a Part into a real Blender object with one primitive per material.

    ``mesh.validate()`` discards material slots that nothing references, so the
    polygon -> material mapping is recorded while the buckets are concatenated
    and applied before validation runs.
    """
    mesh = bpy.data.meshes.new(name)
    vertices, faces, material_slots, face_materials = [], [], [], []
    slot_of: "OrderedDict[str, int]" = OrderedDict()
    for material, (bucket_verts, bucket_faces) in part._buckets.items():
        if material not in slot_of:
            slot_of[material] = len(material_slots)
            material_slots.append(material)
        slot = slot_of[material]
        offset = len(vertices)
        vertices.extend(bucket_verts)
        faces.extend(tuple(index + offset for index in face) for face in bucket_faces)
        face_materials.extend([slot] * len(bucket_faces))
    mesh.from_pydata(vertices, [], faces)
    for material in material_slots:
        mesh.materials.append(materials[material])
    for polygon, slot in zip(mesh.polygons, face_materials):
        polygon.material_index = slot
    mesh.validate(verbose=False)
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    object_ = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(object_)
    return object_


def _buckets_of(mesh):
    """Material name -> list of faces, each face a plain tuple of vertex indices.

    Faces are copied out to immutable index tuples on purpose: holding live
    ``bpy`` polygon elements across a mesh swap dereferences freed memory and
    crashes Blender.
    """
    buckets: "OrderedDict[str, list]" = OrderedDict()
    names = [material.name for material in mesh.materials]
    coordinates = [tuple(vertex.co) for vertex in mesh.vertices]
    for polygon in mesh.polygons:
        key = names[polygon.material_index] if names else ""
        buckets.setdefault(key, []).append(tuple(polygon.vertices))
    return buckets, coordinates


def _mesh_from_faces(coordinates, faces, material_names, name: str):
    """Build a mesh from pre-collected coordinates and index tuples."""
    vertices, rebuilt, face_materials, slot_of = [], [], [], OrderedDict()
    for material_name, polygons in faces:
        if material_name not in slot_of:
            slot_of[material_name] = len(slot_of)
        slot = slot_of[material_name]
        index_map = {}
        for face in polygons:
            mapped = []
            for vertex_index in face:
                if vertex_index not in index_map:
                    index_map[vertex_index] = len(vertices)
                    vertices.append(coordinates[vertex_index])
                mapped.append(index_map[vertex_index])
            rebuilt.append(tuple(mapped))
            face_materials.append(slot)
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], rebuilt)
    for material_name in slot_of:
        mesh.materials.append(bpy.data.materials[material_name])
    for polygon, slot in zip(mesh.polygons, face_materials):
        polygon.material_index = slot
        polygon.use_smooth = False
    mesh.validate(verbose=False)
    return mesh


def merge_by_material(object_, max_span: float = 0.0, keep_whole=None, clear_enough=None):
    """Collapse one object's primitives into one primitive per material.

    With ``max_span`` set, a material is split into spatially compact chunks no
    wider than that many metres - but only when its own plan footprint fails
    ``clear_enough``. Keeping a material whole whenever its bounding box already
    clears the modelled keep-out volume is what holds the draw count down: only
    materials that genuinely straddle the zone pay for being split.
    """
    keep_whole = keep_whole or frozenset()
    mesh = object_.data
    buckets, coordinates = _buckets_of(mesh)
    if max_span <= 0:
        if len(buckets) <= 1 and object_.data.materials:
            return [(object_, None)]
        groups = list(buckets.items())
        object_.data = _mesh_from_faces(coordinates, groups, list(buckets), object_.name + "-merged")
        bpy.data.meshes.remove(mesh)
        return [(object_, None)]

    chunks = []
    for material_name, polygons in buckets.items():
        corners = [coordinates[i] for face in polygons for i in face]
        low_x, high_x = min(c[0] for c in corners), max(c[0] for c in corners)
        low_y, high_y = min(c[1] for c in corners), max(c[1] for c in corners)
        if material_name in keep_whole or (clear_enough and clear_enough(low_x, high_x, low_y, high_y)):
            # Returned with its material name so the caller leaves it un-fused:
            # fusing would rebuild the spanning bounding box it must not have.
            chunks.append((material_name, polygons, material_name))
            continue
        for chunk in _split_by_span(coordinates, polygons, max_span):
            chunks.append((material_name, chunk, None))

    objects = []
    for index, (material_name, chunk, guard) in enumerate(chunks):
        piece_mesh = _mesh_from_faces(coordinates, [(material_name, chunk)],
                                      [material_name], f"{object_.name}-{index}")
        if index == 0:
            object_.data = piece_mesh
            bpy.data.meshes.remove(mesh)
            objects.append((object_, guard))
        else:
            clone = bpy.data.objects.new(f"{object_.name}-{index}", piece_mesh)
            bpy.context.collection.objects.link(clone)
            objects.append((clone, guard))
    return objects or [(object_, None)]


def _split_by_span(coordinates, polygons, max_span: float,
                   cell: float = 1.0, gap: float = 6.0, limit: int = 400):
    """Group faces of one material into spatially compact, connected clusters.

    Faces are bucketed into a ``cell``-metre plan grid and neighbouring cells are
    chained, so port and starboard runs - or a bow cluster and a stern cluster -
    never share a cluster. Clusters are then merged only when they are within
    ``gap`` metres of each other and their combined box still fits ``max_span``,
    so each merged mesh is one physical run of geometry rather than an average
    across the whole deck.
    """
    if not polygons:
        return []
    low = [float("inf"), float("inf")]
    high = [float("-inf"), float("-inf")]
    for face in polygons:
        for vertex_index in face:
            coordinate = coordinates[vertex_index]
            for axis in (0, 1):
                low[axis] = min(low[axis], coordinate[axis])
                high[axis] = max(high[axis], coordinate[axis])
    spans = (high[0] - low[0], high[1] - low[1])
    if spans[0] <= max_span and spans[1] <= max_span:
        return [polygons]

    grid: "dict[tuple[int, int], list]" = {}
    for face in polygons:
        centre_x = sum(coordinates[i][0] for i in face) / len(face)
        centre_y = sum(coordinates[i][1] for i in face) / len(face)
        key = (int(math.floor(centre_x / cell)), int(math.floor(centre_y / cell)))
        grid.setdefault(key, []).append(face)

    cluster_of, clusters = {}, []
    for key in sorted(grid):
        if key in cluster_of:
            continue
        index = len(clusters)
        clusters.append([])
        pending = [key]
        cluster_of[key] = index
        while pending:
            current = pending.pop()
            clusters[index].extend(grid[current])
            # Four-way chaining only: a diagonal step would hop the gap between
            # two parallel runs and fuse them into one sprawling cluster.
            for offset in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                neighbour = (current[0] + offset[0], current[1] + offset[1])
                if neighbour in grid and neighbour not in cluster_of:
                    cluster_of[neighbour] = index
                    pending.append(neighbour)

    boxes = []
    for faces in clusters:
        corners = [coordinates[i] for face in faces for i in face]
        boxes.append([min(c[0] for c in corners), max(c[0] for c in corners),
                      min(c[1] for c in corners), max(c[1] for c in corners)])

    def near(a, b) -> bool:
        return (max(a[0], b[0]) - min(a[1], b[1]) <= gap
                and max(a[2], b[2]) - min(a[3], b[3]) <= gap)

    merged = True
    while merged:
        merged = False
        for i in range(len(boxes)):
            if boxes[i] is None:
                continue
            for j in range(i + 1, len(boxes)):
                if boxes[j] is None or not near(boxes[i], boxes[j]):
                    continue
                combined = [min(boxes[i][0], boxes[j][0]), max(boxes[i][1], boxes[j][1]),
                            min(boxes[i][2], boxes[j][2]), max(boxes[i][3], boxes[j][3])]
                if (combined[1] - combined[0] <= max_span
                        and combined[3] - combined[2] <= max_span):
                    boxes[i] = combined
                    clusters[i].extend(clusters[j])
                    boxes[j] = None
                    merged = True
    return [faces for box, faces in zip(boxes, clusters) if box is not None]


def uv_project(object_, scale: float, offset=(0.0, 0.0, 0.0)) -> None:
    """World-space box projection, so tiling stays uniform across merged parts."""
    mesh = object_.data
    if not mesh.loops:
        return
    if not mesh.uv_layers:
        mesh.uv_layers.new(name="UVMap")
    layer = mesh.uv_layers.active.data
    for polygon in mesh.polygons:
        normal = polygon.normal
        axis = max(range(3), key=lambda index: abs(normal[index]))
        for loop_index in polygon.loop_indices:
            coordinate = mesh.vertices[mesh.loops[loop_index].vertex_index].co
            if axis == 0:
                u, v = coordinate.y, coordinate.z
            elif axis == 1:
                u, v = coordinate.x, coordinate.z
            else:
                u, v = coordinate.x, coordinate.y
            layer[loop_index].uv = ((u + offset[0]) * scale, (v + offset[1]) * scale)


def clean_geometry(object_, weld: float = 0.0, dissolve: float = 0.0) -> None:
    mesh = object_.data
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=weld if weld > 0 else 1e-5)
    if dissolve > 0:
        bmesh.ops.dissolve_degenerate(bm, dist=dissolve, edges=bm.edges)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    # Preserve crisp plate edges while smoothing round pipes and tanks.
    for face in bm.faces:
        face.smooth = True
    for edge in bm.edges:
        edge.smooth = edge.is_manifold and edge.calc_face_angle() < math.radians(35)
    bm.to_mesh(mesh)
    bm.free()


def decimate(object_, ratio: float) -> None:
    """Collapse an applied object to ``ratio`` of its triangles.

    The modifier is applied through ``bpy.ops``, so the object must be linked to
    the view layer and made active. ``use_collapse_triangulate`` is left off: it
    triangulates first, which inflates the face count the ratio is measured
    against and can leave the mesh unchanged.
    """
    if ratio >= 0.999 or not len(object_.data.polygons):
        return
    before = triangle_count(object_)
    modifier = object_.modifiers.new(name="lod", type="DECIMATE")
    modifier.decimate_type = "COLLAPSE"
    modifier.ratio = max(0.01, min(1.0, ratio))
    modifier.use_collapse_triangulate = False
    previous = bpy.context.view_layer.objects.active
    for other in bpy.context.view_layer.objects:
        other.select_set(False)
    object_.select_set(True)
    bpy.context.view_layer.objects.active = object_
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    bpy.context.view_layer.objects.active = previous


def triangle_count(object_) -> int:
    return sum(len(polygon.vertices) - 2 for polygon in object_.data.polygons)


# --------------------------------------------------------------------------
# Scene assembly
# --------------------------------------------------------------------------

def reset_scene() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    PALETTE.clear()


def material_library():
    return {name: make_material({"name": name, **entry}) for name, entry in PALETTE.items()}


def link_parent(child, parent):
    child.parent = parent
    child.matrix_parent_inverse = Matrix.Identity(4)


DECK_RASTER = {
    "polygonOffset": True,
    "polygonOffsetFactor": -1,
    "polygonOffsetUnits": -1,
    "depthWrite": True,
}


def join_objects(target, sources) -> None:
    """Join meshes into ``target``, keeping every material slot.

    The glTF exporter emits one primitive per (mesh, material) pair, so N meshes
    that between them use M materials cost N*M draw calls. Bundling meshes that
    share a material down to one mesh per material group is what keeps the draw
    count proportional to the palette instead of to the part count.
    """
    sources = [object_ for object_ in sources if object_ is not target]
    if not sources:
        return
    previous = bpy.context.view_layer.objects.active
    for object_ in bpy.context.view_layer.objects:
        object_.select_set(False)
    for object_ in sources:
        object_.select_set(True)
    target.select_set(True)
    bpy.context.view_layer.objects.active = target
    result = bpy.ops.object.join()
    bpy.context.view_layer.objects.active = previous
    return result


def bundle_by_material(objects, max_span: float = 0.0, keep_whole=None, uv_scales=None):
    """Group objects so every material is carried by as few meshes as possible.

    The whole plan is computed before any join runs: joining mutates the scene
    collection, so an index taken from the original list would no longer refer to
    the same object mid-loop.

    With ``max_span`` set, a fused group is rejected when its combined bounding
    box would pass that span on either axis - which is what stops a port run and
    a starboard run from being fused into one mesh that bridges the touchdown
    keep-out zone.
    """
    keep_whole = keep_whole or frozenset()
    if not uv_scales:
        # The caller already produced its final meshes, so there is nothing to do.
        return objects

    spans, materials_of = {}, {}
    for index, object_ in enumerate(objects):
        points = [vertex.co for vertex in object_.data.vertices]
        if not points:
            continue
        spans[index] = (min(p.x for p in points), max(p.x for p in points),
                        min(p.y for p in points), max(p.y for p in points))
        materials_of[index] = {material.name for material in object_.data.materials}

    def footprint(index):
        low_x, high_x, low_y, high_y = spans[index]
        return max(high_x - low_x, high_y - low_y)

    def merge_boxes(a, b):
        return (min(a[0], b[0]), max(a[1], b[1]), min(a[2], b[2]), max(a[3], b[3]))

    def fits(box):
        return max(box[1] - box[0], box[3] - box[2]) <= max_span

    guard_present = max_span > 0 or bool(keep_whole)
    plan = []
    if not guard_present:
        # No spatial constraint: fuse everything sharing a UV density, so the
        # exporter emits one primitive per material.
        by_scale = {}
        for index in range(len(objects)):
            by_scale.setdefault(uv_scales[index], []).append(index)
        plan = list(by_scale.values())
    else:
        groups = []
        for index in sorted(range(len(objects)), key=footprint, reverse=True):
            if index not in spans:
                continue
            scale = uv_scales[index]
            if not fits(spans[index]):
                # Already wider than the limit: it keeps its own sealed group.
                groups.append({"members": [index], "scale": scale,
                               "box": spans[index], "materials": materials_of[index],
                               "sealed": True})
                continue
            for group in groups:
                if group["sealed"] or group["scale"] != scale:
                    continue
                if not (materials_of[index] & group["materials"]):
                    continue
                combined = merge_boxes(group["box"], spans[index])
                if not fits(combined):
                    continue
                group["members"].append(index)
                group["materials"] |= materials_of[index]
                group["box"] = combined
                break
            else:
                groups.append({"members": [index], "scale": scale, "box": spans[index],
                               "materials": set(materials_of[index]), "sealed": False})
        plan = [group["members"] for group in groups]

    keepers = []
    for members in plan:
        anchor = objects[members[0]]
        join_objects(anchor, [objects[member] for member in members[1:]])
        keepers.append(anchor)
    return keepers


def assemble(asset_name: str, parts, materials):
    """Build parts into one LOD group.

    Each entry is ``(part, uv_scale, raster, decimate_ratio)`` plus optional
    ``max_span``, ``keep_whole`` and ``clear_enough`` guards:

    - ``max_span`` caps how wide a single merged mesh may grow.
    - ``keep_whole`` names materials exempt from spatial splitting.
    - ``clear_enough(low_x, high_x, low_y, high_y)`` lets a material stay whole
      when its plan footprint already clears a modelled keep-out volume.

    Together these are what keep the recovery ship's deck clear of the touchdown
    zone without paying to split materials that never enter it.

    The returned group is unparented so the caller can place it in the asset
    hierarchy (an LOD level, or a joint pivot node).
    """
    group = bpy.data.objects.new(asset_name, None)
    group.empty_display_size = 4.0
    bpy.context.collection.objects.link(group)
    built, scales = [], []
    handled = set()
    span_limit = 0.0
    guard_set = None
    for entry in parts:
        part, uv_scale, raster, ratio = entry[:4]
        max_span = entry[4] if len(entry) > 4 else 0.0
        keep_whole = entry[5] if len(entry) > 5 else None
        clear_enough = entry[6] if len(entry) > 6 else None
        if max_span > 0:
            span_limit = max_span
        if keep_whole:
            guard_set = keep_whole
        if part.empty():
            continue
        converted = _passthrough(part)
        if raster:
            for material_name in converted._buckets:
                if material_name in materials:
                    materials[material_name]["raster"] = raster
        object_ = build_mesh(converted, materials, f"{asset_name}-{part.name}")
        for piece, guard in merge_by_material(object_, max_span, keep_whole, clear_enough):
            clean_geometry(piece)
            uv_project(piece, uv_scale)
            handled.add(piece)
            # A guarded piece is never fused with anything: fusing is exactly
            # what would rebuild the bounding box the guard exists to avoid.
            # Unguarded pieces are fused only when the result still fits the
            # span limit, which `bundle_by_material` enforces.
            if guard is None and not keep_whole:
                built.append(piece)
                scales.append(uv_scale)
            else:
                link_parent(piece, group)
    for piece in bundle_by_material(built, span_limit, guard_set, scales):
        if ratio < 0.999:
            decimate(piece, ratio)
        link_parent(piece, group)
    # Guarded pieces are excluded from bundling, so the LOD ratio reaches them
    # here instead - otherwise a guarded-only asset would never simplify at all.
    if ratio < 0.999:
        for piece in list(group.children):
            if piece.type == "MESH" and piece not in handled:
                decimate(piece, ratio)
    return group


def asset_root(asset_name: str):
    """The single root node whose children are ``LOD0``/``LOD1``/``LOD2``."""
    root = bpy.data.objects.new(asset_name, None)
    root.empty_display_size = 8.0
    bpy.context.collection.objects.link(root)
    return root


def export_asset(path: str, root) -> int:
    """Export standard Y-up glTF in scene units (1 unit = 2.46 metres)."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        export_extras=True,
        export_yup=True,
        export_apply=True,
        export_normals=True,
        export_texcoords=True,
        export_materials="EXPORT",
        export_cameras=False,
        export_lights=False,
        use_selection=False,
        use_visible=True,
    )
    return os.path.getsize(path)


def summarise(root) -> dict:
    """Per-LOD triangle and draw-call totals, following joint subtrees.

    A draw call is one exported primitive, so the count is the number of
    distinct materials across the LOD's meshes - not the number of meshes.
    """
    levels = []
    for level in root.children:
        triangles = 0
        materials = set()
        stack = list(level.children)
        while stack:
            object_ = stack.pop()
            stack.extend(object_.children)
            if object_.type != "MESH":
                continue
            triangles += triangle_count(object_)
            for material in object_.data.materials:
                materials.add(material.name)
        levels.append({"lod": level.name, "triangles": triangles, "draws": len(materials)})
    return levels
