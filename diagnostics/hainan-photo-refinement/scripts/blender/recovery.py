"""Closed recovery platform. Scene units: 1 unit = 2.46 m; deck matches flight rig."""
import math
import textures
from scenery_core import Part, surface, TAU

LENGTH, BEAM = 48.0, 36.0
DECK_Z = 3.2 / 2.46
KEEL_Z = DECK_Z - 2.4
LANDING_RADIUS = 10.0
SPLIT_SPAN = 9.0
KEEP_WHOLE = frozenset({'Barge-hull','Barge-oxide','Barge-deck-paint','Barge-edge'})

def declare_materials():
    for name, color, rough, metal in [
        ('hull','#233b46',.62,.45),('oxide','#723d34',.82,.2),
        ('edge','#768489',.48,.65),('machinery','#bec8c5',.58,.25),
        ('rubber','#171f25',.91,.02),('steel','#85979e',.39,.75),
        ('ochre','#c79748',.58,.25),('window','#162d3e',.18,.6),
        ('safety','#ce5a2b',.55,.15),('copper','#a37242',.5,.75)]:
        surface('Barge-'+name,color,rough,metal)
    surface('Barge-deck-paint','#ffffff',.86,.18,texture=textures.deck_paint)
    surface('Barge-light','#fff0cd',.3,0,emission=('#fff0cd',2))

def outline(inset=0):
    w,d=BEAM/2-inset,LENGTH/2-inset
    return [(-w+2,-d),(w-2,-d),(w,-d+3),(w,d-3),(w-2,d),
            (-w+2,d),(-w,d-3),(-w,-d+3)]

def footprint_clearance(lx,hx,ly,hy):
    return math.hypot(max(lx,min(0,hx)),max(ly,min(0,hy)))

def recovery_ship(hull, deck, bridge, fittings, detail, **edges):
    # Closed shell: both sides share an ordered outline and matching stations.
    verts=[]
    for inset,z in [(1.8,KEEL_Z),(.55,KEEL_Z+.55),(0,DECK_Z-.24)]:
        verts.extend((x,y,z) for x,y in outline(inset))
    # Keep the shell in one material so manifold checks see a sealed volume.
    faces=[tuple(range(7,-1,-1)),tuple(range(16,24))]
    for ring in range(2):
        faces += [(ring*8+i,ring*8+(i+1)%8,(ring+1)*8+(i+1)%8,(ring+1)*8+i) for i in range(8)]
    hull.add('Barge-hull',verts,faces)
    hull.prism('Barge-oxide',outline(.62),KEEL_Z+.15,KEEL_Z+.5)
    hull.prism('Barge-edge',outline(.06),DECK_Z-.23,DECK_Z-.09)
    deck.prism('Barge-deck-paint',outline(.12),DECK_Z-.085,DECK_Z)
    z=DECK_Z
    bridge.bevel_box('Barge-machinery',7.4,3.7,2.5,(0,-20.6,z+1.25),.12)
    bridge.bevel_box('Barge-edge',7.9,4.1,.24,(0,-20.6,z+2.62),.06)
    for i in range(6):
        bridge.bevel_box('Barge-window',.93,.04,.8,(-2.85+i*1.14,-18.73,z+1.77),.008)
    for side in [-1,1]:
        for i in range(2):
            bridge.box('Barge-window',.04,1.04,.8,(side*3.71,-20.9+i*1.2,z+1.77))
        bridge.bevel_box('Barge-steel',.035,.83,1.7,(side*3.72,-21.82,z+.9),.005)
    bridge.beam('Barge-steel',(0,-21,z+2.74),(0,-21,z+7.3),.085,segments=12)
    bridge.beam('Barge-steel',(-1.5,-21,z+6.2),(1.5,-21,z+6.2),.05)
    bridge.bevel_box('Barge-machinery',2.0,.35,.2,(0,-21,z+7.35))
    bridge.cylinder('Barge-light',.11,z+7.5,z+7.68,location=(0,-21),segments=12)
    for side in [-1,1]:
        bridge.bevel_box('Barge-machinery',4.0,6.0,2.15,(side*13.6,-18.5,z+1.075),.1)
        bridge.bevel_box('Barge-edge',4.2,6.2,.20,(side*13.6,-18.5,z+2.23))
        bridge.cylinder('Barge-steel',.45,z+2.3,z+3.6,location=(side*13.6,-20),segments=24 if detail==0 else 12)
        bridge.cylinder('Barge-rubber',.54,z+3.6,z+3.74,location=(side*13.6,-20),segments=24 if detail==0 else 12)
        bridge.bevel_box('Barge-ochre',3.4,4.0,1.55,(side*13.7,18.0,z+.775),.08)
        if detail<2:
            for i in range(8):
                bridge.box('Barge-rubber',.022,2.7,.085,(side*11.59,-18.3,z+.45+i*.17))
                bridge.box('Barge-steel',.035,3.8,.05,(side*15.42,18,z+.2+i*.17))
        # Folded crane stays over its side service lane.
        fittings.cylinder('Barge-steel',.62,z,z+1.35,location=(side*13.7,12.5),segments=24 if detail==0 else 12)
        fittings.bevel_box('Barge-ochre',.85,1.4,1.2,(side*13.7,12.5,z+1.65))
        fittings.beam('Barge-ochre',(side*13.7,12.5,z+2.1),(side*13.7,7.9,z+2.6),.25,segments=8)
        fittings.beam('Barge-steel',(side*13.7,12.4,z+1.6),(side*13.7,8.9,z+2.45),.08)
        fittings.beam('Barge-rubber',(side*13.7,7.9,z+2.6),(side*13.7,7.9,z+1.7),.03)
        fittings.tube('Barge-steel',.16,.055,z+1.45,z+1.7,location=(side*13.7,7.9),segments=12)
        for y in [-21,21]:
            fittings.bevel_box('Barge-steel',1.5,1.1,.14,(side*14.0,y,z+.07))
            for dx in [-.43,.43]:
                fittings.cylinder('Barge-steel',.16,z+.14,z+.65,location=(side*14+dx,y),segments=16 if detail==0 else 8)
            fittings.box('Barge-steel',1.35,.32,.12,(side*14,y,z+.65))
        for i in range((12,9,5)[detail]):
            y=-19+i*38/((12,9,5)[detail]-1)
            hull.bevel_box('Barge-rubber',.48,1.12,.95,(side*18.06,y,z-.78),.08)
        if detail<2:
            rail=edges['port' if side<0 else 'starboard']
            for i in range((22,12)[detail]):
                y=-20.8+i*41.6/((22,12)[detail]-1)
                rail.beam('Barge-steel',(side*17.3,y,z),(side*17.3,y,z+.95),.035,segments=6)
            for h in [.48,.95]:
                rail.beam('Barge-steel',(side*17.3,-21,z+h),(side*17.3,21,z+h),.027,segments=6)
            for y in [-21,21]:
                fittings.beam('Barge-steel',(side*16.2,y,z),(side*16.2,y,z+3.8),.055)
                fittings.bevel_box('Barge-light',.32,.24,.18,(side*16.2,y,z+3.86))
        if detail==0:
            for y in range(-18,20,3):
                fittings.tube('Barge-steel',.095,.032,z+.006,z+.035,location=(side*15.7,y),segments=12)
            fittings.tube('Barge-safety',.38,.12,0,.16,location=(side*16.3,-15.2,z+1.05),rotation=(math.pi/2,0,0),segments=32)
            for y in [-13,16]:
                fittings.bevel_box('Barge-machinery',1.25,2.1,.8,(side*15.5,y,z+.4),.12)
    if detail<2:
        for y,key in [(-23.1,'stern'),(23.1,'bow')]:
            edge=edges[key]
            for i in range(13):
                x=-14.4+i*2.4
                edge.beam('Barge-steel',(x,y,z),(x,y,z+.95),.035,segments=6)
            for h in [.48,.95]:
                edge.beam('Barge-steel',(-14.4,y,z+h),(14.4,y,z+h),.027,segments=6)
