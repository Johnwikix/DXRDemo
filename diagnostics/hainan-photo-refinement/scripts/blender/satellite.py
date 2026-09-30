"""Detailed payload with local hinge coordinates and separated rigid solar leaves."""
import math
import textures
from scenery_core import Part, surface, TAU

HINGE_X, HINGE_Z = .68, .70
PANEL_COUNT, PANEL_PITCH = 3, 1.18
PANEL_U, PANEL_V = 1.08, 1.40
PANEL_STOWED_X = .69

def declare_materials():
    for n,c,r,m in [('mli','#d4a94d',.36,.78),('structure','#aab8c0',.32,.78),
                     ('dark','#15202b',.55,.3),('white','#e8e9df',.6,.1),
                     ('dish','#dce2e4',.3,.65),('copper','#b18549',.36,.8),
                     ('glass','#1a4275',.16,.65),('cell-frame','#8498aa',.32,.75)]:
        surface('Sat-'+n,c,r,m)
    surface('Sat-cells','#ffffff',.3,.5,texture=textures.solar_cells)


def reflector(p, detail):
    # Two distinct centre vertices, no collapsed rings / degenerate triangles.
    rings, seg=(16,64) if detail==0 else ((9,40) if detail==1 else (4,20))
    verts=[(0,0,1.46),(0,0,1.44)]
    for layer in [0,1]:
        for r in range(1,rings+1):
            radius=.46*r/rings
            z=1.46+.17*(r/rings)**2-layer*.02
            verts.extend((radius*math.cos(i*TAU/seg),radius*math.sin(i*TAU/seg),z) for i in range(seg))
    faces=[]
    for layer in [0,1]:
        start=2+layer*rings*seg
        for i in range(seg):
            j=(i+1)%seg
            tri=(layer,start+i,start+j)
            faces.append(tri if layer==0 else tuple(reversed(tri)))
        for r in range(rings-1):
            for i in range(seg):
                j=(i+1)%seg;a=start+r*seg
                face=(a+i,a+seg+i,a+seg+j,a+j)
                faces.append(face if layer==0 else tuple(reversed(face)))
    a=2+(rings-1)*seg;b=2+(2*rings-1)*seg
    for i in range(seg):
        j=(i+1)%seg;faces.append((a+i,b+i,b+j,a+j))
    p.add('Sat-dish',verts,faces)
    p.tube('Sat-structure',.475,.026,1.615,1.645,segments=seg)
    p.cylinder('Sat-structure',.07,1.28,1.445,segments=20)
    for i in range(3):
        a=i*TAU/3
        p.beam('Sat-structure',(.41*math.cos(a),.41*math.sin(a),1.615),(0,0,1.99),.012,segments=8)
    p.cylinder('Sat-copper',.057,1.94,2.04,segments=24,radius_top=.09)
    if detail<2:
        for i in range(12):
            a=i*TAU/12
            # Curved ribs follow the underside instead of chords through the dish.
            for j in range(5):
                r0=.06+j*.076;r1=r0+.076
                p.beam('Sat-structure',(r0*math.cos(a),r0*math.sin(a),1.424+.17*(r0/.46)**2),
                       (r1*math.cos(a),r1*math.sin(a),1.424+.17*(r1/.46)**2),.008,segments=6)


def satellite_bus(body, mechanism, detail):
    body.bevel_box('Sat-dark',1.06,.96,1.1,(0,0,.7),.035)
    # Quilted insulation: tessellated, gently crumpled surfaces, closed at the back.
    cols,rows=(18,20) if detail==0 else ((10,12) if detail==1 else (4,5))
    for side in [-1,1]:
        for axis in [0,1]:
            verts=[]
            width=1.04 if axis==1 else .94
            for j in range(rows+1):
                for i in range(cols+1):
                    u=-width/2+width*i/cols;v=.17+1.06*j/rows
                    ripple=.009*math.sin(i*2.17+j*1.71)+.006*math.sin(i*.71-j*2.3)
                    outer=side*((.495 if axis==1 else .545)+ripple)
                    verts.append((u,outer,v) if axis==1 else (outer,u,v))
            faces=[]
            for j in range(rows):
                for i in range(cols):
                    a=j*(cols+1)+i;b=a+1;c=a+cols+2;d=c-1
                    faces.extend([(a,b,c),(a,c,d)])
            if (axis == 1 and side > 0) or (axis == 0 and side < 0):
                faces = [tuple(reversed(f)) for f in faces]
            body.add('Sat-mli',verts,faces)
        for y in [-.49,.49]:
            body.bevel_box('Sat-structure',.045,.045,1.18,(side*.55,y,.7),.009)
        for z in [.125,1.275]:
            body.box('Sat-structure',1.14,.035,.035,(0,side*.49,z))
            body.box('Sat-structure',.035,1.02,.035,(side*.55,0,z))
    body.bevel_box('Sat-white',1.02,.94,.045,(0,0,1.27),.012)
    # Recessed radiator, cable channels and optical instruments on fore/aft faces.
    body.bevel_box('Sat-white',.62,.025,.75,(0,-.527,.72),.006)
    if detail<2:
        for i in range(9):
            body.box('Sat-structure',.56,.008,.012,(0,-.544,.4+i*.079))
        for side in [-1,1]:
            body.beam('Sat-copper',(side*.43,.54,.24),(side*.43,.54,1.12),.012)
        for i in range(4):
            body.bevel_box('Sat-dark',.18,.022,.11,(-.36+i*.24,.535,.44),.004)
    for side in [-1,1]:
        body.bevel_box('Sat-dark',.23,.23,.2,(side*.32,.56,1.04),.02)
        body.tube('Sat-structure',.083,.015,0,.06,(side*.32,.7,1.04),(math.pi/2,0,0),segments=24 if detail==0 else 12)
        body.cylinder('Sat-glass',.067,0,.01,(side*.32,.704,1.04),(math.pi/2,0,0),segments=24 if detail==0 else 12)
        for y in [-.32,.32]:
            mechanism.cylinder('Sat-structure',.065,.04,.14,(side*.38,y),segments=16)
    body.tube('Sat-structure',.42,.075,0,.115,segments=(64,40,24)[detail])
    body.tube('Sat-dark',.44,.05,.045,.07,segments=(64,40,24)[detail])
    if detail==0:
        for i in range(24):
            a=i*TAU/24
            body.cylinder('Sat-copper',.015,.115,.137,(.383*math.cos(a),.383*math.sin(a)),segments=6)
    reflector(body,detail)
    for side in [-1,1]:
        # Fixed root brackets terminate exactly at the animated hinge.
        body.beam('Sat-structure',(side*.54,-.22,.7),(side*HINGE_X,-.22,.7),.04)
        body.beam('Sat-structure',(side*.54,.22,.7),(side*HINGE_X,.22,.7),.04)


def solar_wing(p, side, detail):
    for y in [-.22,.22]:
        p.cylinder('Sat-structure',.063,-.065,.065,(0,y,0),(math.pi/2,0,0),segments=(24,16,8)[detail])
        p.beam('Sat-structure',(0,y,0),(.18,y,0),.022)
    p.beam('Sat-structure',(.15,-.28,0),(.15,.28,0),.023)


def solar_panel(p,index,detail):
    p.bevel_box('Sat-dark',PANEL_U,PANEL_V,.028,bevel=.006)
    for side in [-1,1]:
        p.box('Sat-cell-frame',.025,PANEL_V,.041,(side*(PANEL_U/2-.0125),0,0))
        p.box('Sat-cell-frame',PANEL_U-.05,.025,.041,(0,side*(PANEL_V/2-.0125),0))
    # Cell surfaces get one atlas per leaf, no repeat across individual cells.
    p.box('Sat-cells',PANEL_U-.065,PANEL_V-.065,.005,(0,0,.017))
    p.box('Sat-white',PANEL_U-.07,PANEL_V-.07,.004,(0,0,-.017))
    if detail<2:
        for i in range(1,9):
            x=-.5+i*.125
            p.box('Sat-copper',.002,PANEL_V-.085,.0015,(x,0,.0205))
        for y in [-.42,.42]:
            for side in [-1,1]:
                p.cylinder('Sat-structure',.028,-.065,.065,(side*.55,y,0),(math.pi/2,0,0),segments=12)
        for x in [-.35,.35]:
            p.box('Sat-structure',.025,PANEL_V-.06,.014,(x,0,-.026))
    if detail==0:
        for x in [-.52,.52]:
            for y in [-.67,-.33,0,.33,.67]:
                p.cylinder('Sat-structure',.008,.02,.024,(x,y),segments=8)
