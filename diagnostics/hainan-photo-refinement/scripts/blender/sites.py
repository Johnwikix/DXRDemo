"""Launch complex geometry for the WR-1 demonstration site.

Layout is a local reconstruction from public imagery and Pad 2 reporting, not a
surveyed plan. Axes follow the glTF scene: +X is the flight azimuth, +Y runs
alongshore, +Z is up with z = 0 on the apron. The vehicle sits at the origin.
"""

from __future__ import annotations

import math

import textures
from scenery_core import Part, surface, TAU

# --------------------------------------------------------------------------
# Materials
# --------------------------------------------------------------------------

A = "Pad-apron-concrete"
B = "Pad-refractory"
C = "Pad-steel"
D = "Pad-dark-steel"
E = "Pad-painted-white"
F = "Pad-painted-blue"
G = "Pad-painted-orange"
H = "Pad-safety-yellow"
I = "Pad-inconel"
J = "Pad-cryo-insulation"
K = "Pad-hazard-stripe"
L = "Pad-glass"
M = "Pad-rubber"
N = "Facility-cladding"
O = "Facility-road"
P = "Facility-steel"
Q = "Pad-light"
R = "Facility-soil-cap"
S = "Pad-copper"


def declare_materials() -> None:
    surface(A, "#aaa99b", 0.95, 0.02, texture=textures.apron_concrete)
    surface(B, "#4a3d36", 0.92, 0.04, texture=textures.launch_atlas)
    surface(C, "#8d949a", 0.40, 0.85)
    surface(D, "#3a4650", 0.55, 0.70)
    surface(E, "#dde0d7", 0.62, 0.10)
    surface(F, "#2878a1", 0.55, 0.08)
    surface(G, "#d24c27", 0.58, 0.06)
    surface(H, "#d8b23c", 0.58, 0.06)
    surface(I, "#9fa7ab", 0.34, 0.92)
    surface(J, "#d6d9dc", 0.72, 0.02)
    surface(K, "#c8452a", 0.62, 0.04)
    surface(L, "#16394d", 0.18, 0.25)
    surface(M, "#1d2a2e", 0.93, 0.02)
    surface(N, "#cfd2ca", 0.74, 0.03, texture=textures.facility_cladding)
    surface(O, "#333c3d", 0.97, 0.02, texture=textures.road_asphalt)
    surface(P, "#99a09b", 0.45, 0.60)
    surface(Q, "#ffd9a0", 0.30, 0.00, emission=("#ffd9a0", 6.0))
    surface(R, "#7d7a63", 1.00, 0.02)
    surface(S, "#8a5a34", 0.42, 0.90)


# --------------------------------------------------------------------------
# 1. Launch mount and flame trench
# --------------------------------------------------------------------------

MOUNT_APERTURE = 0.75
MOUNT_OUTER = 1.65
TRENCH_HALF = 2.90
TRENCH_FLOOR = -1.80
TRENCH_END = 38.0


def launch_mount(p, detail):
    """Open engine well with four supports meeting the actual CAD skirt datum."""
    segments=(96,64,32)[detail]
    p.tube(C,MOUNT_OUTER,.22,.05,.62,segments=segments)
    p.tube(D,MOUNT_APERTURE+.16,.16,.05,.62,segments=segments)
    p.tube(P,MOUNT_OUTER+.06,.1,.46,.55,segments=segments)
    for i in range((24,16,8)[detail]):
        a=i*TAU/(24,16,8)[detail]
        r=(MOUNT_APERTURE+MOUNT_OUTER)/2
        p.box(C,MOUNT_OUTER-MOUNT_APERTURE-.04,.055,.38,
              (r*math.cos(a),r*math.sin(a),.31),(0,0,a))
    contact=3.425/2.46  # supplied CAD first-stage skirt lower plane
    for i in range(4):
        a=TAU*i/4+math.pi/4
        x,y=1.12*math.cos(a),1.12*math.sin(a)
        p.frustum(C,(.19,.19),(.14,.14),.55,contact-.075,(x,y),(0,0,a))
        p.box(D,.75,.34,.075,(.98*math.cos(a),.98*math.sin(a),contact-.0375),(0,0,a))
        p.beam(C,(1.52*math.cos(a),1.52*math.sin(a),.25),(x,y,contact-.14),.055,segments=8)
        p.bevel_box(A,.7,.7,.24,(1.38*math.cos(a),1.38*math.sin(a),-.1),.04)
        if detail<2:
            for offset in [-.1,.1]:
                p.cylinder(I,.035,contact-.1,contact-.02,
                           location=(x+offset*math.sin(a),y-offset*math.cos(a)),segments=12)
    if detail==0:
        for i in range(48):
            a=i*TAU/48
            p.cylinder(I,.022,.62,.66,location=(1.53*math.cos(a),1.53*math.sin(a)),segments=8)


def flame_trench(p: Part, detail: int) -> None:
    """Open trench with refractory lining, a deflector wedge and curb rails."""
    half = TRENCH_HALF
    inner = half - 0.34
    length = TRENCH_END - 2.2
    centre = 2.2 + length / 2
    # Trench walls, floor slab and the apron strips either side.
    for sign in (-1, 1):
        p.box(A, 0.42, length, 2.60, location=(sign * (half + 0.21), centre, -1.10))
        p.box(B, 0.34, length, 2.40, location=(sign * inner, centre, -1.05))
        p.box(D, 0.62, length, 0.14, location=(sign * (half + 0.21), centre, 0.27))
        p.box(A, 7.0, length, 0.90, location=(sign * (half + 3.92), centre, -0.55))
    p.box(B, inner * 2, length, 0.40, location=(0, centre, TRENCH_FLOOR))
    # Sloped deflector at the far end turns the exhaust plume upward.
    for step in range(6):
        t = step / 5.0
        p.box(B, inner * 2 - 0.05, 2.9, 2.6 * (0.30 + 0.70 * t),
              location=(0, TRENCH_END - 5.4 + step * 1.6, TRENCH_FLOOR + 1.3 * t),
              rotation=(math.radians(-34.0 * (0.2 + 0.8 * t)), 0, 0))
    if detail < 2:
        # Water-cooled curb rails and internal deluge rings along the trench.
        for sign in (-1, 1):
            for index in range(9):
                y = 4.0 + index * 4.0
                p.cylinder(C, 0.14, 0.20, 0.46, location=(sign * (half + 0.05), y), segments=12)
                p.box(D, 0.30, 0.60, 0.14, location=(sign * (half + 0.05), y, 0.20))
                if index % 2 == 0:
                    p.beam(S, (sign * (half + 0.05), y, 0.4), (sign * inner, y + 0.6, 0.9), 0.07)
    if detail == 0:
        for sign in (-1, 1):
            for index in range(12):
                y = 3.0 + index * 2.9
                p.box(C, 0.10, 0.10, 2.30, location=(sign * (inner - 0.06), y, -1.04))
                p.box(C, 0.30, 0.06, 0.06, location=(sign * (inner - 0.22), y, 0.16))


def exhaust_duct(p: Part, detail: int) -> None:
    """Acoustic suppression duct and the apron strip beyond the deflector.

    The duct is a lined acoustic box: splitter baffles, perforated facing panels
    and a cooling-water header running along the crown.
    """
    p.box(A, 14.0, 12.0, 1.00, location=(0, TRENCH_END + 5.0, -0.50))
    for sign in (-1, 1):
        p.box(A, 3.0, 12.0, 3.60, location=(sign * 5.5, TRENCH_END + 5.0, 1.30))
        p.box(B, 0.30, 12.0, 3.30, location=(sign * 4.0, TRENCH_END + 5.0, 1.30))
    p.box(B, 14.0, 0.40, 3.30, location=(0, TRENCH_END - 0.9, 1.30))
    if detail < 2:
        # Splitter baffles with perforated facing and a header pipe.
        for index in range(5):
            x = -4.0 + index * 2.0
            p.box(D, 0.24, 11.0, 2.60, location=(x, TRENCH_END + 5.0, 1.30))
            for face in (-1, 1):
                p.box(I, 0.03, 11.0, 2.60, location=(x + face * 0.14, TRENCH_END + 5.0, 1.30))
        p.beam(C, (-6.6, TRENCH_END + 5.0, 3.20), (6.6, TRENCH_END + 5.0, 3.20), 0.18)
        for index in range(9):
            p.cylinder(C, 0.10, 3.20, 3.50,
                       location=(-6.4 + index * 1.6, TRENCH_END + 5.0), segments=10)
        p.box(K, 13.0, 0.30, 0.40, location=(0, TRENCH_END + 10.4, 3.05))
    p.box(R, 14.0, 12.0, 0.30, location=(0, TRENCH_END + 5.0, -1.05))
    # Access stair and handrail down the duct flank.
    for step in range(9):
        p.box(P, 1.10, 0.28, 0.06, location=(7.2, TRENCH_END + 0.4 + step * 0.30, 0.10 + step * 0.32))
    if detail < 2:
        p.beam(G, (7.7, TRENCH_END + 0.2, 1.1), (7.7, TRENCH_END + 3.2, 4.0), 0.026, segments=4)


# --------------------------------------------------------------------------
# 2. Service tower
# --------------------------------------------------------------------------

TOWER_ORIGIN = (-5.2, -1.5)
TOWER_HEIGHT = 25.8
ARM_PIVOT = (-3.8, -0.25, 18.2)

def service_tower(p, detail):
    ox,oy=TOWER_ORIGIN
    floors=9
    for sx in [-1,1]:
        for sy in [-1,1]:
            p.beam(E,(ox+sx*1.35,oy+sy*1.35,0),(ox+sx*1.16,oy+sy*1.16,TOWER_HEIGHT),.115,segments=12 if detail==0 else 8)
            p.bevel_box(A,.85,.85,.4,(ox+sx*1.35,oy+sy*1.35,-.1),.06)
    for floor in range(floors):
        z=.8+floor*2.8; half=1.35-.19*z/TOWER_HEIGHT
        p.bevel_box(D,3.15,3.15,.15,(ox,oy,z),.025)
        for side in [-1,1]:
            p.beam(C,(ox-half,oy+side*half,z),(ox+half,oy+side*half,z+2.8),.046,segments=6)
            p.beam(C,(ox+half,oy+side*half,z),(ox-half,oy+side*half,z+2.8),.046,segments=6)
            p.beam(C,(ox+side*half,oy-half,z),(ox+side*half,oy+half,z+2.8),.046,segments=6)
            if detail<2:
                for h in [.5,.95]:
                    p.beam(G,(ox-1.55,oy+side*1.55,z+h),(ox+1.55,oy+side*1.55,z+h),.02,segments=6)
                    p.beam(G,(ox+side*1.55,oy-1.55,z+h),(ox+side*1.55,oy+1.55,z+h),.02,segments=6)
                for i in range(5):
                    v=-1.5+i*.75
                    p.beam(G,(ox+v,oy+side*1.55,z),(ox+v,oy+side*1.55,z+.95),.02,segments=6)
                    p.beam(G,(ox+side*1.55,oy+v,z),(ox+side*1.55,oy+v,z+.95),.02,segments=6)
        if detail<2 and floor<floors-1:
            # Treads run between landings, with real stringers and handrails.
            for i in range(14):
                p.box(P,.85,.23,.045,(ox-2.0,oy-1.3+i*.2,z+i*.2))
            for x in [ox-2.4,ox-1.6]:
                p.beam(D,(x,oy-1.4,z-.06),(x,oy+1.3,z+2.74),.04,segments=6)
                p.beam(G,(x,oy-1.4,z+.85),(x,oy+1.3,z+3.65),.022,segments=6)
        if detail==0:
            for i in range(18):
                p.box(C,2.95,.018,.018,(ox,oy-1.4+i*.165,z+.085))
    p.bevel_box(E,.85,.85,25,(ox-.4,oy-.4,12.5),.04)
    p.bevel_box(F,3.35,3.35,.22,(ox,oy,25.9),.05)
    p.bevel_box(E,1.1,1.25,1.2,(ox-.4,oy-.4,26.6),.05)
    p.beam(C,(ox,oy,26),(ox,oy,28.7),.065,segments=12)
    p.cylinder(Q,.11,28.7,28.9,location=(ox,oy),segments=12)
    for x in [ox-.95,ox-.65]:
        p.beam(C,(x,oy+1.42,.1),(x,oy+1.42,24.9),.05,segments=10)
    if detail<2:
        for z in [5,11,17,23]:
            p.bevel_box(Q,.26,.2,.18,(ox+1.65,oy+1.55,z))
    p.bevel_box(F,.045,1.55,1.3,(ox+1.65,oy,20),.008)
    p.box(E,.012,1.2,.18,(ox+1.68,oy,20.25))


def service_arm(p, detail):
    # All vertices relative to the hinge, never an absolute world-space offset.
    length=2.55
    p.tube(C,.22,.09,-.35,.4,segments=32 if detail==0 else 16)
    for y,z in [(-.2,.2),(.2,.2),(0,-.2)]:
        p.beam(C,(0,y,z),(length,y,z),.045,segments=10)
    for i in range(6):
        x=i*length/5
        p.beam(D,(x,-.2,.2),(x,.2,.2),.022,segments=6)
        p.beam(D,(x,-.2,.2),(x,0,-.2),.025,segments=6)
        p.beam(D,(x,.2,.2),(x,0,-.2),.025,segments=6)
        if i<5:
            p.beam(D,(x,-.2,.2),(x+length/5,.2,.2),.02,segments=6)
    p.bevel_box(E,.38,.46,.42,(length-.1,0,.08),.04)
    p.bevel_box(M,.12,.3,.3,(length+.14,0,.08),.015)
    if detail<2:
        for y in [-.12,0,.12]:
            p.beam(S,(.1,y,.28),(length-.25,y,.28),.017,segments=8)
    p.box(H,length,.028,.1,(length/2,-.24,.16))


# --------------------------------------------------------------------------
# 3. Lightning protection
# --------------------------------------------------------------------------

MAST_FOOTPRINT = ((-36.0, -48.0), (34.0, -48.0), (-36.0, 44.0), (34.0, 44.0))
MAST_HEIGHT = 125.0 / 2.46 - 9.3


def lightning_masts(p: Part, detail: int) -> None:
    segments = (26, 18, 12)[detail]
    for cx, cy in MAST_FOOTPRINT:
        p.box(A, 7.0, 7.0, 0.80, location=(cx, cy, 0.0))
        p.box(B, 5.4, 5.4, 1.20, location=(cx, cy, 0.9))
        if detail < 2:
            for index in range(4):
                angle = TAU * index / 4 + math.radians(45)
                p.cylinder(E, 0.20, 1.60, 3.10,
                           location=(cx + math.cos(angle) * 1.9, cy + math.sin(angle) * 1.9),
                           segments=10)
                p.cylinder(C, 0.30, 3.10, 3.40,
                           location=(cx + math.cos(angle) * 1.9, cy + math.sin(angle) * 1.9),
                           segments=10)
        half_base, half_top = 1.70, 0.42
        for level in range(segments):
            t0, t1 = level / segments, (level + 1) / segments
            z0, z1 = 2.0 + t0 * MAST_HEIGHT, 2.0 + t1 * MAST_HEIGHT
            a = half_base + (half_top - half_base) * t0
            b = half_base + (half_top - half_base) * t1
            corners = ((-1, -1), (1, -1), (1, 1), (-1, 1))
            for sign_x, sign_y in corners:
                p.beam(C, (cx + sign_x * a, cy + sign_y * a, z0),
                       (cx + sign_x * b, cy + sign_y * b, z1), 0.115, segments=6)
                if detail < 2:
                    p.beam(D, (cx + sign_x * a, cy + sign_y * a, z0),
                           (cx + sign_x * b, cy + sign_y * b, z0), 0.06, segments=5)
            if level < segments:
                for index in range(4):
                    sx0, sy0 = corners[index]
                    sx1, sy1 = corners[(index + 1) % 4]
                    p.beam(D, (cx + sx0 * a, cy + sy0 * a, z0),
                           (cx + sx1 * b, cy + sy1 * b, z1), 0.045, segments=5)
                    p.beam(D, (cx + sx1 * a, cy + sy1 * a, z0),
                           (cx + sx0 * b, cy + sy0 * b, z1), 0.045, segments=5)
        top = MAST_HEIGHT + 2.0
        p.frustum(C, (0.42, 0.42), (0.16, 0.16), top, top + 3.0, location=(cx, cy))
        p.beam(I, (cx, cy, top + 3.0), (cx, cy, top + 6.5), 0.075, segments=8)
        p.box(G, 0.55, 0.55, 0.35, location=(cx, cy, top + 6.9))
        if detail == 0:
            p.box(Q, 0.30, 0.30, 0.20, location=(cx, cy, top + 7.2))
        # Guy anchors and the upper stays.
        if detail < 2:
            for index in range(4):
                angle = TAU * index / 4 + math.radians(45)
                ax = cx + math.cos(angle) * 22.0
                ay = cy + math.sin(angle) * 22.0
                p.box(A, 2.2, 2.2, 0.60, location=(ax, ay, 0.1))
                p.beam(D, (cx + math.cos(angle) * 1.2, cy + math.sin(angle) * 1.2, 2.0 + MAST_HEIGHT * 0.52),
                       (ax, ay, 0.5), 0.035, segments=4)
                p.beam(D, (cx + math.cos(angle) * 0.7, cy + math.sin(angle) * 0.7, 2.0 + MAST_HEIGHT * 0.86),
                       (ax, ay, 0.5), 0.030, segments=4)


# --------------------------------------------------------------------------
# 4. Deluge, cooling and pad services
# --------------------------------------------------------------------------

def deluge_system(p: Part, detail: int) -> None:
    """Rainbird manifold, spray risers and the elevated deluge reservoir."""
    for sign in (-1, 1):
        p.beam(C, (sign * 3.8, -6.0, 0.75), (sign * 3.8, 12.0, 0.75), 0.155)
        for index in range(10):
            y = -6.0 + index * 2.0
            p.tube(P, 0.21, 0.07, 0.62, 0.88, location=(sign * 3.8, y), segments=12)
            p.box(A, 0.70, 0.42, 0.62, location=(sign * 3.8, y, 0.31))
            p.beam(C, (sign * 3.8, y, 0.80), (sign * 3.0, y + 0.18, 1.05), 0.055)
            p.cylinder(I, 0.085, 1.05, 1.16, location=(sign * 2.95, y + 0.20), segments=10)
            if detail == 0:
                p.cylinder(D, 0.13, 1.16, 1.24, location=(sign * 2.95, y + 0.20), segments=10)
    # Elevated reservoir on a braced tower.
    wx, wy, wh = -46.0, -58.0, 32.0
    for sign_x in (-1, 1):
        for sign_y in (-1, 1):
            p.beam(C, (wx + sign_x * 2.0, wy + sign_y * 2.0, 0.0),
                   (wx + sign_x * 2.0, wy + sign_y * 2.0, wh), 0.20, segments=8)
            for level in range(8):
                z0, z1 = level * wh / 8, (level + 1) * wh / 8
                p.beam(D, (wx + sign_x * 2.0, wy + sign_y * 2.0, z0),
                       (wx - sign_x * 2.0, wy + sign_y * 2.0, z1), 0.055, segments=5)
                p.beam(D, (wx + sign_x * 2.0, wy + sign_y * 2.0, z0),
                       (wx + sign_x * 2.0, wy - sign_y * 2.0, z1), 0.055, segments=5)
    p.cylinder(E, 2.9, wh, wh + 5.4, location=(wx, wy), segments=24)
    p.cylinder(E, 2.9, wh + 5.4, wh + 5.9, location=(wx, wy), segments=24, radius_top=2.4)
    p.tube(P, 3.05, 0.20, wh + 1.0, wh + 1.4, location=(wx, wy), segments=24)
    p.tube(P, 3.05, 0.20, wh + 4.4, wh + 4.8, location=(wx, wy), segments=24)
    if detail < 2:
        p.cylinder(C, 0.30, wh - 0.4, wh + 0.2, location=(wx + 2.2, wy), segments=12)
        p.beam(C, (wx, wy + 2.9, wh + 1.2), (wx, wy + 2.9, 0.4), 0.14)
        for index in range(14):
            p.box(P, 0.30, 0.05, 0.05,
                  location=(wx - 2.6 + index * 0.40, wy - 2.95, wh + 2.0))


def cooling_and_gas(p: Part, detail: int) -> None:
    """Gaseous nitrogen bank, helium trailer and the pad junction station."""
    for index in range(4):
        x = -20.0 - index * 3.4
        p.cylinder(J, 1.55, 0.0, 6.4, location=(x, -14.0), segments=(28, 20, 12)[detail])
        p.cylinder(J, 1.55, 6.4, 7.1, location=(x, -14.0), segments=(28, 20, 12)[detail],
                   radius_top=1.30)
        p.tube(P, 1.62, 0.09, 1.2, 1.5, location=(x, -14.0), segments=20)
        p.tube(P, 1.62, 0.09, 4.9, 5.2, location=(x, -14.0), segments=20)
        p.beam(C, (x, -14.0, 0.0), (x, -6.0, 0.0), 0.085, segments=6)
        p.box(A, 2.2, 2.2, 0.50, location=(x, -14.0, -0.25))
        if detail < 2:
            for rung in range(18):
                p.box(P, 0.70, 0.06, 0.05, location=(x + 1.58, -14.0, 0.4 + rung * 0.34))
    # Sphere farm for liquid oxygen.
    for cx, cy in ((-32.0, -26.0), (-32.0, -36.0)):
        p.cylinder(J, 2.6, 4.0, 4.6, location=(cx, cy), segments=8)
        p.cylinder(J, 2.6, 9.0, 9.6, location=(cx, cy), segments=8)
        p.cylinder(J, 2.6, 4.6, 9.0, location=(cx, cy), segments=(30, 22, 14)[detail])
        for index in range(6):
            angle = TAU * index / 6
            p.beam(C, (cx + math.cos(angle) * 1.7, cy + math.sin(angle) * 1.7, 0.0),
                   (cx + math.cos(angle) * 1.7, cy + math.sin(angle) * 1.7, 4.0), 0.16)
        p.box(A, 6.4, 6.4, 0.60, location=(cx, cy, -0.1))
    # Horizontal propellant tanks on saddles.
    for index in range(2):
        cy = -46.0 - index * 9.0
        p.cylinder(J, 2.3, -4.0, 4.0, location=(-24.0, cy, 3.6), rotation=(math.pi / 2, 0, 0), segments=20)
        p.cylinder(J, 2.3, 4.0, 4.5, location=(-24.0, cy, 3.6), rotation=(math.pi / 2, 0, 0),
                   segments=20, radius_top=1.9)
        for offset in (-2.8, 2.8):
            p.box(P, 3.0, 0.8, 2.0, location=(-24.0, cy + offset, 1.0))
        p.beam(C, (-24.0, cy - 5.0, 1.2), (-24.0, cy - 5.0, 0.0), 0.09)
    p.box(A, 12.0, 14.0, 0.35, location=(-24.0, -50.0, -0.18))


# --------------------------------------------------------------------------
# 5. Site buildings, roads, fencing and utilities
# --------------------------------------------------------------------------

def transfer_roads(p: Part, detail: int) -> None:
    p.box(O, 20.0, 760.0, 0.14, location=(-48.0, -320.0, -0.34))
    p.box(O, 310.0, 13.0, 0.14, location=(-174.0, -95.0, -0.33))
    p.box(O, 18.0, 310.0, 0.14, location=(-305.0, -240.0, -0.35))
    p.box(O, 360.0, 14.0, 0.14, location=(-80.0, 54.0, -0.33))
    # Aprons around the mount, leaving the trench and engine well clear.
    p.box(A, 59.0, 76.0, 0.12, location=(-45.5, -31.0, -0.37))
    p.box(A, 27.0, 76.0, 0.12, location=(29.5, -31.0, -0.37))
    p.box(A, 32.0, 56.0, 0.12, location=(0.0, -41.0, -0.37))
    p.box(A, 30.0, 40.0, 0.12, location=(64.0, 20.0, -0.37))
    if detail < 2:
        for index in range(64):
            p.box(N, 0.15, 3.8, 0.02, location=(-48.0, 48.0 - index * 11.0, -0.27))
        for index in range(22):
            p.box(E, 1.4, 0.22, 0.02, location=(9.0, -40.0 + index * 4.0, -0.22))
    p.box(R, 26.0, 30.0, 0.20, location=(74.0, -40.0, -0.24))


def site_buildings(p, detail):
    """Industrial halls with sealed pitched roofs and offset facade details."""
    for bx,by,w,d,h in [(-93,-147,24,82,10),(-151,-305,50,27,7),
                         (-274,-125,34,18,5),(-230,-450,62,28,8)]:
        p.bevel_box(A,w+3,d+3,.4,(bx,by,-.3),.06)
        p.bevel_box(N,w,d,h,(bx,by,h/2-.1),.06)
        # One closed gabled roof. No rods pass through a flat roof slab.
        roof=Part('roof')
        roof.prism(F,[(-w/2-.3,h-.1),(w/2+.3,h-.1),(w/2+.3,h+.18),
                      (0,h+1.3),(-w/2-.3,h+.18)],-d/2-.3,d/2+.3,
                   rotation=(math.pi/2,0,0))
        p.instance(roof,(bx,by,0))
        # Facade channels and blue lower protection strip face the road.
        p.box(F,.08,d,.38,(bx+w/2+.035,by,.5))
        ribs=(int(d/2),int(d/4),int(d/8))[detail]
        for i in range(ribs):
            y=by-d/2+.5+i*(d-1)/max(1,ribs-1)
            p.box(P,.08,.075,h-.6,(bx+w/2+.045,y,h/2))
        p.bevel_box(D,.12,min(14,d*.45),h*.74,(bx+w/2+.13,by,h*.37),.02)
        for i in range(1,10 if detail<2 else 5):
            height=h*.74*i/(10 if detail<2 else 5)
            p.box(P,.035,min(14,d*.45)-.2,.045,(bx+w/2+.205,by,height))
        p.box(H,.3,min(14,d*.45)+.5,.2,(bx+w/2+.12,by,h*.75))
        for side in [-1,1]:
            p.bevel_box(L,.045,2.6,1.0,(bx+w/2+.115,by+side*d*.34,h*.63),.006)
            p.bevel_box(N,2.4,2.4,.65,(bx,by+side*d*.3,h+1.35),.04)
        if detail==0:
            # Gutter downpipes, roof vents, bollards and a personnel canopy.
            for side in [-1,1]:
                p.beam(P,(bx+w/2+.18,by+side*(d/2-.6),.1),
                           (bx+w/2+.18,by+side*(d/2-.6),h),.05,segments=10)
            p.box(F,1.6,3.2,.14,(bx+w/2+.8,by+d*.32,2.5))
            p.box(L,.07,1.2,2.2,(bx+w/2+.1,by+d*.32,1.1))
            for y in [-1,1]:
                p.cylinder(H,.09,0,1.1,location=(bx+w/2+2,by+y*(min(14,d*.45)/2+.5)),segments=12)
    p.bevel_box(F,6,6,26,(-56,-310,12.7),.12)
    p.bevel_box(G,9,8,.6,(-56,-310,26),.06)


def utilities(p: Part, detail: int) -> None:
    """Pipe racks, cable trays, perimeter fence and site lighting."""
    # Elevated pipe rack running to the mount.
    for bay in range(6):
        y = -4.0 - bay * 5.0
        p.beam(P, (-16.0, y, 0.0), (-16.0, y, 4.2), 0.11, segments=6)
        p.beam(P, (-8.0, y, 0.0), (-8.0, y, 4.2), 0.11, segments=6)
        p.beam(P, (-16.0, y, 4.2), (-8.0, y, 4.2), 0.09, segments=6)
        for index in range(4):
            p.beam(C, (-16.0, y, 0.65 + index * 0.85), (-8.0, y, 0.65 + index * 0.85), 0.135, segments=8)
        if detail < 2:
            for index in range(4):
                p.tube(C, 0.19, 0.05, 0.65 + index * 0.85 - 0.05, 0.65 + index * 0.85 + 0.05,
                       location=(-12.0, y), rotation=(0, math.pi / 2, 0), segments=12)
            p.box(D, 8.0, 0.30, 0.16, location=(-12.0, y, 4.35))
    # Perimeter fence along the site edge with posts, rails and gates.
    for index in range(30):
        x = -70.0 + index * 4.0
        if -62 <= x <= -34:
            continue
        p.beam(P, (x, -78.0, 0.0), (x, -78.0, 2.4), 0.055, segments=5)
        p.beam(P, (x, -78.0, 2.3), (x + 4.0, -78.0, 2.3), 0.026, segments=4)
        p.beam(P, (x, -78.0, 1.4), (x + 4.0, -78.0, 1.4), 0.020, segments=4)
        p.beam(P, (x, -78.0, 0.5), (x + 4.0, -78.0, 0.5), 0.020, segments=4)
        if detail < 2:
            for mesh_index in range(9):
                p.beam(D, (x + mesh_index * 0.44, -78.0, 0.2), (x + mesh_index * 0.44, -78.0, 2.3),
                       0.012, segments=3)
    # Site lighting masts on the apron.
    for cx, cz in ((-40.0, 0.0), (40.0, 0.0), (-40.0, 34.0), (40.0, 34.0)):
        p.beam(D, (cx, cz, 0.0), (cx, cz, 26.0), 0.16, segments=8)
        p.box(A, 1.1, 1.1, 0.5, location=(cx, cz, 0.2))
        p.box(D, 1.4, 0.5, 0.35, location=(cx, cz, 26.2))
        for index in range(3):
            p.box(Q, 0.34, 0.30, 0.16, location=(cx - 0.4 + index * 0.4, cz, 26.35))
        if detail < 2:
            for index in range(24):
                p.box(P, 0.05, 0.05, 0.30, location=(cx + 0.16, cz, 0.6 + index * 1.0))
    # Standby generator house and switchyard.
    p.box(N, 14.0, 9.0, 5.0, location=(52.0, -18.0, 2.3))
    p.box(F, 14.4, 9.4, 0.5, location=(52.0, -18.0, 4.9))
    p.box(D, 3.0, 0.30, 2.4, location=(52.0, -13.3, 1.5))
    for index in range(4):
        p.box(P, 1.2, 1.2, 4.6, location=(62.0 + index * 5.0, -30.0, 2.3))
        p.box(D, 0.30, 0.30, 5.4, location=(62.0 + index * 5.0, -30.0, 5.4))
        p.beam(C, (62.0 + index * 5.0, -30.0, 5.6), (62.0 + index * 5.0, -26.0, 6.4), 0.06)
