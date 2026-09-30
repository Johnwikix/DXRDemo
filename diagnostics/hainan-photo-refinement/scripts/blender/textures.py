"""Deterministic procedural textures baked into the exported GLBs.

Generating these offline keeps the runtime free of canvas work at load time and
gives every tiling surface a real texel density instead of a flat base colour.
No network access and no random seeds that vary between runs.
"""

from __future__ import annotations

import os

import bpy
import numpy as np

CACHE: "dict[str, bpy.types.Image]" = {}
OUTPUT_DIR = os.path.join(os.getcwd(), "artifacts", "scenery-textures")

# Images are embedded in the GLB, so the encoder settings are informational.


class Rng:
    """Deterministic value source.

    numpy's legacy ``RandomState`` is seeded independently of the global state
    and its stream is documented as frozen, so a texture stays byte-identical
    across runs and machines.
    """

    def __init__(self, seed: int):
        self._random = np.random.RandomState(seed & 0x7FFFFFFF)

    def next(self, count: int) -> np.ndarray:
        return self._random.random_sample(count)


def _grid(size: int) -> tuple[np.ndarray, np.ndarray]:
    axis = np.arange(size, dtype=np.float64)
    return np.meshgrid(axis, axis, indexing="xy")


def _value_noise(size: int, frequency: int, rng: Rng) -> np.ndarray:
    """Bilinear value noise on a wrapped lattice, so the tile stays seamless."""
    lattice = rng.next(frequency * frequency).reshape(frequency, frequency)
    x, y = _grid(size)
    u = x * frequency / size
    v = y * frequency / size
    x0 = np.floor(u).astype(int) % frequency
    y0 = np.floor(v).astype(int) % frequency
    x1 = (x0 + 1) % frequency
    y1 = (y0 + 1) % frequency
    fx = u - np.floor(u)
    fy = v - np.floor(v)
    fx = fx * fx * (3 - 2 * fx)
    fy = fy * fy * (3 - 2 * fy)
    top = lattice[y0, x0] * (1 - fx) + lattice[y0, x1] * fx
    bottom = lattice[y1, x0] * (1 - fx) + lattice[y1, x1] * fx
    return top * (1 - fy) + bottom * fy


def _octaves(size: int, rng: Rng, frequency: int, count: int = 3) -> np.ndarray:
    """Fractal sum of value noise, normalised back to 0..1."""
    total = np.zeros((size, size))
    weight = 0.0
    current = frequency
    for octave in range(count):
        total += _value_noise(size, current, rng) * (0.5 ** octave)
        weight += 0.5 ** octave
        current *= 2
    return total / weight


def _mix(base: np.ndarray, overlay: np.ndarray, amount: np.ndarray) -> np.ndarray:
    return base * (1.0 - amount[..., None]) + overlay * amount[..., None]


def _tint(size: int, colour: str) -> np.ndarray:
    code = colour.lstrip("#")
    channels = [int(code[i:i + 2], 16) / 255.0 for i in (0, 2, 4)]
    return np.tile(np.array(channels, dtype=np.float64), (size, size, 1))


def _to_image(name: str, pixels: np.ndarray, size: int) -> bpy.types.Image:
    image = bpy.data.images.new(name, width=size, height=size, alpha=False)
    rgba = np.empty((size, size, 4), dtype=np.float32)
    rgba[..., :3] = np.clip(pixels, 0.0, 1.0)
    rgba[..., 3] = 1.0
    image.pixels.foreach_set(rgba.reshape(-1))
    image.pack()
    return image


# --------------------------------------------------------------------------
# Deck paint: the landing surface of the recovery ship
# --------------------------------------------------------------------------

def deck_paint() -> bpy.types.Image:
    if "DeckPaint" in CACHE:
        return CACHE["DeckPaint"]
    size=1024
    x,y=_grid(size); rng=Rng(713)
    mx=(x/(size-1)-.5)*36; my=(y/(size-1)-.5)*48
    pixels=_tint(size,"#3a5057")
    grain=_value_noise(size,256,rng)
    pixels *= (.9+.17*grain[...,None])
    seams=((abs((mx+1.5)%3-1.5)<.025)|(abs((my+2)%4-2)<.025))
    pixels[seams]*=.7
    radius=np.hypot(mx,my)
    paint=(abs(radius-7.7)<.1)|(abs(radius-8.15)<.045)
    paint|=((abs(mx)<.07)&(abs(my)<5))|((abs(my)<.07)&(abs(mx)<5))
    border=((abs(mx)>16.65)&(abs(mx)<16.78)&(abs(my)<21))|((abs(my)>22.55)&(abs(my)<22.7)&(abs(mx)<15.3))
    paint|=border
    # Hatched edge lanes; single texture means no coplanar decal flicker.
    paint|=(abs(mx)>16.05)&(abs(mx)<16.45)&(abs(my)<20.5)&(((my+mx)*2)%1<.48)
    pixels[paint]=[.87,.79,.53]
    scorch=np.clip(1-radius/3.3,0,1)**2
    pixels*=1-.36*scorch[...,None]
    sockets=(abs((mx+1)%3-1.5)<.045)&(abs((my+1)%3-1.5)<.045)&(radius>9)
    pixels[sockets]=[.07,.11,.13]
    # Small stencilled identification at the forward end of the clear area.
    font={'W':['10101','10101','10101','10101','11111','11011','10001'],
          'R':['11110','10001','10001','11110','10100','10010','10001'],
          '-':['00000','00000','00000','11111','00000','00000','00000'],
          '1':['00100','01100','00100','00100','00100','00100','01110']}
    for k,ch in enumerate('WR-1'):
        for row,line in enumerate(font[ch]):
            for col,bit in enumerate(line):
                if bit=='1':
                    xx=420+k*48+col*7; yy=805+(6-row)*7
                    pixels[yy:yy+7,xx:xx+7]=[.87,.79,.53]
    image=_to_image('DeckPaint',pixels,size)
    CACHE['DeckPaint']=image
    return image


# --------------------------------------------------------------------------
# Site surfaces
# --------------------------------------------------------------------------

def apron_concrete() -> bpy.types.Image:
    """Broomed apron concrete with pour joints and stains."""
    if "ApronConcrete" in CACHE:
        return CACHE["ApronConcrete"]
    size = 512
    rng = Rng(4021)
    x, y = _grid(size)
    pixels = _tint(size, "#aaa99b")
    pixels = _mix(pixels, np.tile(np.array([0.78, 0.77, 0.71]), (size, size, 1)),
                  _value_noise(size, 128, rng) * 0.22)
    pixels = _mix(pixels, np.tile(np.array([0.55, 0.54, 0.49]), (size, size, 1)),
                  _value_noise(size, 32, rng) * 0.18)
    broom = (np.sin(x * 1.7) * 0.5 + 0.5) * 0.05
    pixels = _mix(pixels, np.tile(np.array([0.6, 0.6, 0.56]), (size, size, 1)), broom)
    joint = np.arange(size) % (size // 4)
    lines = ((joint[:, None] < 2) | (joint[None, :] < 2)).astype(np.float64)
    pixels = _mix(pixels, np.tile(np.array([0.42, 0.42, 0.38]), (size, size, 1)), lines * 0.8)
    stain = _value_noise(size, 16, rng) ** 4
    pixels = _mix(pixels, np.tile(np.array([0.30, 0.30, 0.27]), (size, size, 1)), stain * 0.35)
    image = _to_image("ApronConcrete", pixels, size)
    CACHE["ApronConcrete"] = image
    return image


def road_asphalt() -> bpy.types.Image:
    """Asphalt with a faded centre line, for the transfer roads."""
    if "RoadAsphalt" in CACHE:
        return CACHE["RoadAsphalt"]
    size = 512
    rng = Rng(8817)
    x, _ = _grid(size)
    pixels = _tint(size, "#333c3d")
    pixels = _mix(pixels, np.tile(np.array([0.30, 0.31, 0.31]), (size, size, 1)),
                  _value_noise(size, 192, rng) * 0.35)
    chips = (_value_noise(size, 384, rng) > 0.86).astype(np.float64)
    pixels = _mix(pixels, np.tile(np.array([0.62, 0.61, 0.57]), (size, size, 1)), chips * 0.5)
    centre_line = (np.abs(x - size / 2) < 5).astype(np.float64)
    wear = _value_noise(size, 24, rng) > 0.35
    pixels = _mix(pixels, np.tile(np.array([0.78, 0.77, 0.70]), (size, size, 1)),
                  centre_line * wear)
    image = _to_image("RoadAsphalt", pixels, size)
    CACHE["RoadAsphalt"] = image
    return image


def facility_cladding() -> bpy.types.Image:
    """Profiled metal cladding for the assembly and payload buildings."""
    if "FacilityCladding" in CACHE:
        return CACHE["FacilityCladding"]
    size = 256
    rng = Rng(2299)
    x, _ = _grid(size)
    pixels = _tint(size, "#cfd2ca")
    profile = (np.sin(x * np.pi / 4) * 0.5 + 0.5)
    pixels = _mix(pixels, np.tile(np.array([0.62, 0.65, 0.63]), (size, size, 1)), profile * 0.30)
    pixels = _mix(pixels, np.tile(np.array([0.86, 0.87, 0.84]), (size, size, 1)),
                  _value_noise(size, 48, rng) * 0.12)
    streak = np.clip(_value_noise(size, 8, rng) - 0.5, 0.0, 1.0) ** 2
    pixels = _mix(pixels, np.tile(np.array([0.52, 0.53, 0.50]), (size, size, 1)), streak * 0.9)
    image = _to_image("FacilityCladding", pixels, size)
    CACHE["FacilityCladding"] = image
    return image


def launch_atlas() -> bpy.types.Image:
    """Refractory brick for the trench and deflector."""
    if "LaunchAtlas" in CACHE:
        return CACHE["LaunchAtlas"]
    size = 512
    rng = Rng(5150)
    x, y = _grid(size)
    pixels = _tint(size, "#4a3d36")
    brick_h = size // 8
    brick_w = size // 4
    course = (y // brick_h).astype(int)
    offset = (course % 2) * (brick_w // 2)
    bx = (x + offset) % brick_w
    by = y % brick_h
    mortar = ((bx < 3) | (by < 3)).astype(np.float64)
    shade = _value_noise(size, 64, rng)
    pixels = _mix(pixels, np.tile(np.array([0.42, 0.33, 0.28]), (size, size, 1)), shade * 0.4)
    pixels = _mix(pixels, np.tile(np.array([0.24, 0.20, 0.18]), (size, size, 1)), mortar * 0.9)
    soot = np.clip(1.0 - np.hypot(x - size / 2, y - size / 2) / (size * 0.7), 0.0, 1.0) ** 2
    pixels = _mix(pixels, np.tile(np.array([0.10, 0.09, 0.09]), (size, size, 1)), soot * 0.55)
    image = _to_image("LaunchAtlas", pixels, size)
    CACHE["LaunchAtlas"] = image
    return image


def solar_cells() -> bpy.types.Image:
    """Solar array blanket: cells, busbars and interconnect ribbons."""
    if "SolarCells" in CACHE:
        return CACHE["SolarCells"]
    size = 512
    rng = Rng(3311)
    x, y = _grid(size)
    pixels = _tint(size, "#13265a")
    cell_w, cell_h = size // 16, size // 8
    column = (x // cell_w).astype(int)
    row = (y // cell_h).astype(int)
    variation = (((column * 7 + row * 13) % 5) / 5.0)
    pixels = _mix(pixels, np.tile(np.array([0.10, 0.20, 0.46]), (size, size, 1)),
                  variation * 0.5)
    gap = (((x % cell_w) < 2) | ((y % cell_h) < 2)).astype(np.float64)
    pixels = _mix(pixels, np.tile(np.array([0.42, 0.46, 0.30]), (size, size, 1)), gap * 0.85)
    busbar = (np.abs((x % cell_w) - cell_w / 2) < 1.5).astype(np.float64)
    pixels = _mix(pixels, np.tile(np.array([0.72, 0.74, 0.78]), (size, size, 1)), busbar * 0.9)
    sheen = _octaves(size, rng, 8, 2) * 0.10
    pixels = _mix(pixels, np.tile(np.array([0.30, 0.45, 0.80]), (size, size, 1)), sheen)
    image = _to_image("SolarCells", pixels, size)
    CACHE["SolarCells"] = image
    return image


def write_previews() -> None:
    """Drop PNG previews next to the build for visual inspection."""
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    for name, factory in (("deck-paint", deck_paint), ("apron-concrete", apron_concrete),
                          ("road-asphalt", road_asphalt), ("facility-cladding", facility_cladding),
                          ("launch-atlas", launch_atlas), ("solar-cells", solar_cells)):
        image = factory()
        image.filepath_raw = os.path.join(OUTPUT_DIR, f"{name}.png")
        image.file_format = "PNG"
        image.save()
