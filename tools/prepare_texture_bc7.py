"""Prepare optional BC7 GPU sidecars using Microsoft's texconv (CPU only).

PNG remains the portable glTF fallback and CPU light-distribution source.
Usage: python tools/prepare_texture_bc7.py <scene.gltf> --texconv <texconv.exe>
"""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path
from PIL import Image


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("scene", type=Path)
    parser.add_argument("--texconv", type=Path, required=True)
    args = parser.parse_args()
    scene = args.scene.resolve()
    model = json.loads(scene.read_text(encoding="utf-8"))
    manifest_path = scene.parent / "bc7-manifest.json"
    previous = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else {}
    cached = {(r["source"], r["srgb"]): r for r in previous.get("textures", [])}
    uses = set()
    for material in model.get("materials", []):
        pbr = material.get("pbrMetallicRoughness", {})
        extensions = material.get("extensions", {})
        for binding, srgb in [(pbr.get("baseColorTexture"), True),
                              (material.get("emissiveTexture"), True),
                              (pbr.get("metallicRoughnessTexture"), False),
                              (material.get("normalTexture"), False),
                              (material.get("occlusionTexture"), False),
                              (extensions.get("KHR_materials_transmission", {}).get("transmissionTexture"), False),
                              (extensions.get("KHR_materials_volume", {}).get("thicknessTexture"), False)]:
            if binding:
                uses.add((model["textures"][binding["index"]]["source"], srgb))
    records = []
    for number, (index, srgb) in enumerate(sorted(uses), 1):
        source = scene.parent / model["images"][index]["uri"]
        with Image.open(source) as image:
            width, height = image.size
        if width % 4 or height % 4:
            continue  # D3D12 requires block-aligned top-level dimensions.
        suffix = "-bc7srgb" if srgb else "-bc7linear"
        output = source.with_name(source.stem + suffix + ".dds")
        source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
        cache = cached.get((source.name, srgb), {})
        if not (output.exists() and cache.get("sourceSha256") == source_hash and
                cache.get("sha256") == hashlib.sha256(output.read_bytes()).hexdigest()):
            command = [str(args.texconv.resolve()), "-nologo", "-nogpu", "-bc", "q", "-y",
                       "-f", "BC7_UNORM_SRGB" if srgb else "BC7_UNORM", "-if", "BOX",
                       "-sepalpha", "--ignore-srgb", "-sx", suffix, "-o", str(source.parent)]
            if srgb:
                command.append("-srgb")
            command.append(str(source))
            result = subprocess.run(command, capture_output=True, text=True)
            if result.returncode:
                raise RuntimeError(result.stdout + result.stderr)
        records.append({"source": source.name, "sourceSha256": source_hash, "srgb": srgb,
                        "file": output.name, "sha256": hashlib.sha256(output.read_bytes()).hexdigest()})
        if number % 20 == 0:
            print(f"BC7: {number}/{len(uses)}", flush=True)
    manifest = {"tool": "Microsoft DirectXTex texconv may2026", "cpuOnly": True,
                "compression": "BC7 quick (mode 6), BOX mipmaps, separate alpha", "textures": records}
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Prepared {len(records)} BC7 sidecars.", flush=True)


if __name__ == "__main__":
    main()
