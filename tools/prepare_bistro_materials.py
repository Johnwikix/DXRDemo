"""Bake RTXPT's Bistro material overrides into portable standard glTF textures.

Specular/glossiness -> metallic/roughness is approximate (the two BRDFs cannot
represent every identical material). Equations follow Khronos' workflow conversion:
https://github.com/KhronosGroup/glTF/tree/main/extensions/2.0/Archived/KHR_materials_pbrSpecularGlossiness/examples/convert-between-workflows
Geometry and instances remain untouched. This helper uses only CPU decoding.
"""
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image


def linear(value):
    return np.where(value <= .04045, value / 12.92, ((value + .055) / 1.055)**2.4)


def srgb(value):
    value = np.clip(value, 0, 1)
    return np.where(value <= .0031308, value * 12.92, 1.055 * value**(1/2.4) - .055)


def metal_rough(diffuse, specular):
    # Solve metallicity using perceived brightness, then blend the two base-color estimates.
    brightness = lambda x: np.sqrt(np.sum(x*x*np.array([.299, .587, .114]), axis=-1, keepdims=True))
    one_minus = 1 - np.max(specular, axis=-1, keepdims=True)
    d, s = brightness(diffuse), brightness(specular)
    b = d * one_minus / .96 + s - .08
    metallic = np.where(s < .04, 0, np.clip((-b + np.sqrt(np.maximum(b*b - .16*(.04-s), 0))) / .08, 0, 1))
    from_diffuse = diffuse * one_minus / (.96 * np.maximum(1-metallic, 1e-6))
    from_specular = (specular - .04*(1-metallic)) / np.maximum(metallic, 1e-6)
    return np.clip(from_diffuse*(1-metallic*metallic) + from_specular*metallic*metallic, 0, 1), metallic


def prepare(model, source, staging, max_size, hashes):
    assets = source.parents[1]
    staging.mkdir(parents=True, exist_ok=True)
    model["images"], model["textures"] = [], []
    model["extensionsUsed"] = []
    model.pop("extensionsRequired", None)
    prepared, cached = {}, {}

    def record(path):
        key = path.relative_to(assets).as_posix()
        if key not in hashes:
            data = path.read_bytes()
            if data.startswith(b"version https://git-lfs.github.com/spec/"):
                raise RuntimeError(f"Git LFS object not downloaded: {path}")
            hashes[key] = hashlib.sha256(data).hexdigest()

    def decode(description):
        if description is None:
            return np.ones((1, 1, 4), dtype=np.float32)
        path = assets / description["path"].replace("\\", "/")
        record(path)
        with Image.open(path) as image:
            image = image.convert("RGBA")
            image.thumbnail((max_size, max_size), Image.Resampling.BOX)
            data = np.asarray(image, dtype=np.float32) / 255
        if description["sRGB"]:
            data[:, :, :3] = linear(data[:, :, :3])
        return data

    def emit(data, color=False):
        data = data.copy()
        if color:
            data[:, :, :3] = srgb(data[:, :, :3])
        pixels = np.rint(np.clip(data, 0, 1)*255).astype(np.uint8)
        digest = hashlib.sha256(str(pixels.shape).encode() + pixels.tobytes()).hexdigest()
        if digest in cached:
            return {"index": cached[digest]}
        index = len(model["images"])
        path = staging / f"baked-{index:03}.png"
        Image.fromarray(pixels).save(path, compress_level=6)
        model["images"].append({"uri": path.name})
        model["textures"].append({"source": index})
        prepared[index] = path
        cached[digest] = index
        return {"index": index}

    def resize(data, width, height):
        if data.shape[:2] == (height, width):
            return data
        return np.stack([np.asarray(Image.fromarray(data[:, :, i]).resize((width, height), Image.Resampling.BILINEAR))
                         for i in range(4)], axis=-1)

    record(assets / "bistro-programmer-art.scene.json")
    for index, original in enumerate(model["materials"]):
        override_path = assets / "Materials" / f"bistro.{original['name']}.material.json"
        record(override_path)
        material = json.loads(override_path.read_text())
        if material["SkipRender"]:
            raise ValueError("Bistro now contains hidden geometry; update the converter")

        def texture(key):
            return material.get(key) if material.get("Enable" + key, True) else None

        diffuse = decode(texture("BaseTexture"))
        specular = decode(texture("OcclusionRoughnessMetallicTexture"))
        height, width = max(diffuse.shape[0], specular.shape[0]), max(diffuse.shape[1], specular.shape[1])
        diffuse, specular = resize(diffuse, width, height), resize(specular, width, height)
        diffuse[:, :, :3] *= material["BaseOrDiffuseColor"]
        diffuse[:, :, 3] *= material["Opacity"]
        if material["UseSpecularGlossModel"]:
            specular[:, :, :3] *= material["SpecularColor"]
            diffuse[:, :, :3], metallic = metal_rough(diffuse[:, :, :3], specular[:, :, :3])
            roughness = 1 - specular[:, :, 3] * (1-material["Roughness"])
        else:
            metallic = specular[:, :, 0 if material["MetalnessInRedChannel"] else 2, None] * material["Metalness"]
            roughness = specular[:, :, 1] * material["Roughness"]
        orm = np.ones_like(diffuse)
        orm[:, :, 1], orm[:, :, 2] = roughness, metallic[:, :, 0]
        converted = {"name": original["name"], "pbrMetallicRoughness": {
            "baseColorTexture": emit(diffuse, color=True), "metallicRoughnessTexture": emit(orm),
            "metallicFactor": 1, "roughnessFactor": 1},
            "alphaMode": "MASK" if material["EnableAlphaTesting"] else ("BLEND" if material["Opacity"] < 1 else "OPAQUE"),
            "alphaCutoff": material["AlphaCutoff"], "doubleSided": True,
            "extras": {"nestedPriority": material["NestedPriority"]}}
        extensions = converted["extensions"] = {}
        if texture("NormalTexture") and material["NormalTextureScale"] != 0:
            normal = decode(texture("NormalTexture"))
            xy = normal[:, :, :2]*2 - 1
            z = np.where(normal[:, :, 2] <= 0, np.sqrt(np.maximum(1 - np.sum(xy*xy, axis=-1), 0)),
                         np.abs(normal[:, :, 2]*2 - 1))
            normal[:, :, 2] = z*.5 + .5
            converted["normalTexture"] = emit(normal) | {"scale": material["NormalTextureScale"]}
        emission = np.array(material["EmissiveColor"]) * material["EmissiveIntensity"]
        strength = max(1., float(emission.max()))
        converted["emissiveFactor"] = (emission / strength).tolist()
        if strength > 1:
            extensions["KHR_materials_emissive_strength"] = {"emissiveStrength": strength}
        if emission.max() > 0 and texture("EmissiveTexture"):
            converted["emissiveTexture"] = emit(decode(texture("EmissiveTexture")), color=True)
        if material["EnableTransmission"] and material["TransmissionFactor"] > 0:
            extensions["KHR_materials_transmission"] = {"transmissionFactor": material["TransmissionFactor"]}
            extensions["KHR_materials_ior"] = {"ior": material["IoR"]}
            if not material["ThinSurface"]:
                extensions["KHR_materials_volume"] = {"thicknessFactor": 1,
                    "attenuationColor": material["VolumeAttenuationColor"],
                    "attenuationDistance": material["VolumeAttenuationDistance"]}
        model["materials"][index] = converted
        if index % 32 == 0:
            print(f"Bistro materials: {index + 1}/{len(model['materials'])}", flush=True)
    # The original scene uses this panorama as a BC6H cubemap. Package its CC0
    # 1K RGBE original from Poly Haven so the CPU loader preserves HDR radiance.
    model["extras"] = {"environmentMap": "../EnvironmentMaps/shanghai_bund_1k.hdr"}
    return prepared
