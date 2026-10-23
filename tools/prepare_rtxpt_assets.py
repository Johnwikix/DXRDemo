"""Prepare the pinned RTXPT Kitchen, ConvergenceTest and Bistro assets on the CPU.

python tools/prepare_rtxpt_assets.py G:/SoftwareProject/Nvidia/RTXPT
Requires Pillow (DDS/BC7 support) and NumPy. No GPU or runtime checkout is needed.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import shutil
import struct
import subprocess
from urllib.parse import unquote
from urllib.request import urlopen

from PIL import Image

RTXPT_COMMIT = "f08d1c739071e0faad0c7c274d861124c511abab"
ASSETS_COMMIT = "2a38b92df0fb43ebf6c6e6603893052dacf5a4c8"
SHANGHAI_URL = "https://dl.polyhaven.org/file/ph-assets/HDRIs/hdr/1k/shanghai_bund_1k.hdr"
SHANGHAI_HASH = "77276a6b67cbf09611efc46e55871eead34e1fc6fa107db412207b91cf74697e"
ROOT = Path(__file__).resolve().parents[1]


def verify_revision(checkout, expected):
    actual = subprocess.check_output(["git", "-C", str(checkout), "rev-parse", "HEAD"], text=True).strip()
    if actual != expected:
        raise ValueError(f"Expected {checkout} at {expected}, found {actual}")


def checked_bytes(path):
    data = path.read_bytes()
    if data.startswith(b"version https://git-lfs.github.com/spec/"):
        raise RuntimeError(f"Git LFS object not downloaded: {path}")
    return data


def convert(source, destination, name, max_size):
    source_file = source / f"{name}.gltf"
    model = json.loads(checked_bytes(source_file))
    destination.mkdir(parents=True, exist_ok=True)
    sources = {source_file.name: hashlib.sha256(checked_bytes(source_file)).hexdigest()}
    prepared_images = {}
    if name == "bistro":
        from prepare_bistro_materials import prepare
        prepared_images = prepare(model, source, ROOT / "output/rtxpt-bistro-materials", max_size, sources)
    # Some upstream Blender exports contain zero/invalid tangents. Keep strict
    # glTF validation and reconstruct only these primitive frames from triangle UVs.
    buffers = [bytearray(checked_bytes(source / unquote(b["uri"]))) for b in model.get("buffers", [])]
    repaired_normals = set()

    def read_accessor(index):
        accessor = model["accessors"][index]
        view = model["bufferViews"][accessor["bufferView"]]
        component = {5121: "B", 5123: "H", 5125: "I", 5126: "f"}[accessor["componentType"]]
        count = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[accessor["type"]]
        fmt = "<" + component*count
        offset = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
        stride = view.get("byteStride", struct.calcsize(fmt))
        return [struct.unpack_from(fmt, buffers[view["buffer"]], offset+i*stride) for i in range(accessor["count"])]

    for mesh in model.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            normal_index = primitive.get("attributes", {}).get("NORMAL")
            if normal_index is None:
                continue
            normals = read_accessor(normal_index)
            bad = [i for i, n in enumerate(normals) if not all(math.isfinite(v) for v in n) or abs(sum(v*v for v in n)-1) > .001]
            if not bad:
                continue
            repaired_normals.add(normal_index)
            positions = read_accessor(primitive["attributes"]["POSITION"])
            indices = [i[0] for i in read_accessor(primitive["indices"])] if "indices" in primitive else list(range(len(positions)))
            if primitive.get("mode", 4) != 4:
                raise ValueError("Normal repair expects triangle-list source geometry")
            generated = [[0., 0., 0.] for _ in positions]
            for k in range(0, len(indices), 3):
                a, b, c = indices[k:k+3]
                e1 = [positions[b][j]-positions[a][j] for j in range(3)]
                e2 = [positions[c][j]-positions[a][j] for j in range(3)]
                face = [e1[1]*e2[2]-e1[2]*e2[1], e1[2]*e2[0]-e1[0]*e2[2], e1[0]*e2[1]-e1[1]*e2[0]]
                for i in (a, b, c):
                    for j in range(3):
                        generated[i][j] += face[j]
            accessor = model["accessors"][normal_index]
            view = model["bufferViews"][accessor["bufferView"]]
            offset = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
            stride = view.get("byteStride", 12)
            for i in bad:
                n = normals[i]
                if not all(math.isfinite(v) for v in n) or sum(v*v for v in n) < 1e-20:
                    n = generated[i]
                length = math.sqrt(sum(v*v for v in n))
                n = [v/length for v in n] if length > 1e-10 else [0., 1., 0.]
                struct.pack_into("<3f", buffers[view["buffer"]], offset+i*stride, *n)
    invalid_tangents = set()
    for mesh in model.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            index = primitive.get("attributes", {}).get("TANGENT")
            if index is None:
                continue
            accessor = model["accessors"][index]
            view = model["bufferViews"][accessor["bufferView"]]
            offset = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
            stride = view.get("byteStride", 16)
            for vertex in range(accessor["count"]):
                x, y, z, w = struct.unpack_from("<4f", buffers[view["buffer"]], offset + vertex*stride)
                if not all(math.isfinite(v) for v in (x, y, z, w)) or abs(x*x+y*y+z*z-1) > .001 or w not in (-1, 1):
                    invalid_tangents.add(index)
                    break
    for mesh in model.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            if primitive.get("attributes", {}).get("TANGENT") in invalid_tangents:
                del primitive["attributes"]["TANGENT"]
    # The Kitchen checkout does not contain the Image*.png files referenced only
    # by this optional extension. Donut uses the metallic-roughness fallback.
    # Remove it explicitly instead of inventing the absent specular textures.
    for material in model.get("materials", []):
        material.get("extensions", {}).pop("KHR_materials_specular", None)
    extensions = set(e for e in model.get("extensionsUsed", []) if e != "KHR_materials_specular")
    def collect_extensions(value):
        if isinstance(value, dict):
            extensions.update(value.get("extensions", {}).keys())
            for item in value.values():
                collect_extensions(item)
        elif isinstance(value, list):
            for item in value:
                collect_extensions(item)
    collect_extensions(model)
    model["extensionsUsed"] = sorted(extensions)
    texture_refs = []

    def visit(value):
        if isinstance(value, dict):
            for key, item in value.items():
                if key.endswith("Texture") and isinstance(item, dict) and "index" in item:
                    texture_refs.append(item)
                visit(item)
        elif isinstance(value, list):
            for item in value:
                visit(item)

    visit(model.get("materials", []))
    old_textures = model.get("textures", [])
    old_images = model.get("images", [])
    texture_map, image_map, textures, images = {}, {}, [], []
    for ref in texture_refs:
        old_index = ref["index"]
        if old_index not in texture_map:
            texture = dict(old_textures[old_index])
            texture.get("extensions", {}).pop("MSFT_texture_dds", None)
            image_index = texture["source"]
            if image_index not in image_map:
                original = prepared_images.get(image_index, source / unquote(old_images[image_index]["uri"]))
                image_path = original if original.exists() else original.with_suffix(".dds")
                data = checked_bytes(image_path)
                if image_index not in prepared_images:
                    sources[image_path.relative_to(source).as_posix()] = hashlib.sha256(data).hexdigest()
                output_name = f"texture-{len(images):03}.png"
                with Image.open(image_path) as decoded:
                    rgba = decoded.convert("RGBA")
                    # Keep source transfer function. Runtime mip generation decodes
                    # sRGB according to material usage; normal/roughness maps stay linear.
                    if max(rgba.size) > max_size:
                        scale = max_size / max(rgba.size)
                        rgba = rgba.resize((max(1, round(rgba.width*scale)), max(1, round(rgba.height*scale))), Image.Resampling.BOX)
                    rgba.save(destination / output_name, compress_level=6)
                image_map[image_index] = len(images)
                images.append({"uri": output_name, "mimeType": "image/png", "name": original.stem})
            texture["source"] = image_map[image_index]
            texture_map[old_index] = len(textures)
            textures.append(texture)
        ref["index"] = texture_map[old_index]
    if textures:
        model["textures"], model["images"] = textures, images
    else:
        model.pop("textures", None)
        model.pop("images", None)
    for buffer_index, buffer in enumerate(model.get("buffers", [])):
        path = source / unquote(buffer["uri"])
        data = checked_bytes(path)
        sources[path.name] = hashlib.sha256(data).hexdigest()
        (destination / path.name).write_bytes(buffers[buffer_index])
        buffer["uri"] = path.name
    (destination / f"{name}.gltf").write_text(json.dumps(model, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    return {"source": str(source_file.relative_to(source.parents[1])).replace("\\", "/"),
            "textures": len(images), "maxTextureSize": max_size, "sourceSha256": sources,
            "invalidTangentAccessorsReconstructed": sorted(invalid_tangents),
            "invalidNormalAccessorsRepaired": sorted(repaired_normals),
            "changes": ["DDS decoded to PNG", "textures capped to configured size", "missing optional specular extension removed", "invalid tangent frames reconstructed from UVs", "invalid normals repaired from adjacent faces"] +
                (["254 RTXPT material overrides applied", "specular/glossiness baked to approximate metallic/roughness", "normal Z reconstructed", "diffuse transmission omitted (foliage approximation)"] if name == "bistro" else [])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("checkout", type=Path)
    parser.add_argument("--max-texture-size", type=int, default=1024)
    parser.add_argument("--scene", choices=("Kitchen", "ConvergenceTest", "Bistro"), help="Regenerate only one scene; preserve the other manifest entries")
    args = parser.parse_args()
    if not 1 <= args.max_texture_size <= 16384:
        parser.error("max texture size must be between 1 and 16384")
    assets = args.checkout / "Assets"
    verify_revision(args.checkout, RTXPT_COMMIT)
    verify_revision(assets, ASSETS_COMMIT)
    target = ROOT / "DXRDemo/Assets/Models/RTXPT"
    manifest = {"rtxptCommit": RTXPT_COMMIT, "assetsCommit": ASSETS_COMMIT, "assets": {}}
    if args.scene and (target / "manifest.json").exists():
        manifest = json.loads((target / "manifest.json").read_text(encoding="utf-8"))
        if manifest["rtxptCommit"] != RTXPT_COMMIT or manifest["assetsCommit"] != ASSETS_COMMIT:
            raise ValueError("Existing manifest revisions differ; regenerate all scenes")
    for directory, name in [("Kitchen", "kitchen"), ("ConvergenceTest", "ConvergenceTest"), ("Bistro", "bistro")]:
        if args.scene and args.scene != directory:
            continue
        manifest["assets"][directory] = convert(assets / "Models" / directory, target / directory, name, args.max_texture_size)
        print(f"Prepared {directory}", flush=True)
    shutil.copyfile(assets / "ArtLicenses.txt", target / "ArtLicenses.txt")
    shutil.copyfile(args.checkout / "LICENSE.txt", target / "RTXPT-LICENSE.txt")
    environment = target / "EnvironmentMaps"
    environment.mkdir(parents=True, exist_ok=True)
    for name in ("20060807_wells6_hd.hdr", "readme.txt"):
        data = checked_bytes(assets / "EnvironmentMaps" / name)
        (environment / name).write_bytes(data)
    manifest["environment"] = {"source": "EnvironmentMaps/20060807_wells6_hd.hdr",
                               "sha256": hashlib.sha256((environment / "20060807_wells6_hd.hdr").read_bytes()).hexdigest()}
    if "Bistro" in manifest["assets"]:
        shanghai = environment / "shanghai_bund_1k.hdr"
        if not shanghai.exists():
            with urlopen(SHANGHAI_URL, timeout=60) as response:
                data = response.read()
            if hashlib.sha256(data).hexdigest() != SHANGHAI_HASH:
                raise ValueError("Shanghai Bund download differs from the pinned input")
            shanghai.write_bytes(data)
        if hashlib.sha256(checked_bytes(shanghai)).hexdigest() != SHANGHAI_HASH:
            raise ValueError("Shanghai Bund HDR checksum mismatch")
        manifest["bistroEnvironment"] = {"source": SHANGHAI_URL, "sha256": SHANGHAI_HASH,
                                         "author": "Greg Zaal / Poly Haven", "license": "CC0"}
    (target / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
