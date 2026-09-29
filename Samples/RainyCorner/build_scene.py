"""Original Rainy Corner diorama. Run stages through Blender MCP, or run this file in Blender.
All geometry and materials are authored here; no downloaded assets are required.
"""
import bpy
import math
import random
from pathlib import Path
from mathutils import Vector

ROOT = Path(__file__).resolve().parent
RNG = random.Random(927)
M = {}
CACHE = {}
scene = None
group = None

def collection(name):
    global group
    group = bpy.data.collections.new(name)
    scene.collection.children.link(group)

def own(obj, name):
    obj.name = name
    for c in list(obj.users_collection): c.objects.unlink(obj)
    group.objects.link(obj)
    return obj

def material(name, color, rough=.5, metal=0, emission=0, alpha=1):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = next(n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    bsdf.inputs['Base Color'].default_value = (*color, 1)
    bsdf.inputs['Metallic'].default_value = metal
    bsdf.inputs['Roughness'].default_value = rough
    bsdf.inputs['Alpha'].default_value = alpha
    bsdf.inputs['Emission Color'].default_value = (*color, 1)
    bsdf.inputs['Emission Strength'].default_value = emission
    mat.diffuse_color = (*color, alpha)
    if alpha < 1:
        modes = [i.identifier for i in mat.bl_rna.properties['surface_render_method'].enum_items]
        mat.surface_render_method = next(i for i in modes if 'BLENDED' in i)
    M[name] = mat
    return mat

def box(name, pos, size, mat, bevel=.015, rotation=0):
    key = ('box', tuple(round(s,5) for s in size), mat, bevel)
    if key not in CACHE:
        bpy.ops.mesh.primitive_cube_add(size=1)
        obj = bpy.context.object
        obj.dimensions = size
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
        obj.data.materials.append(M[mat])
        if bevel:
            mod = obj.modifiers.new('Soft manufactured edges','BEVEL')
            mod.width = min(bevel, min(size)*.2)
            mod.segments = 2
            bpy.ops.object.modifier_apply(modifier=mod.name)
        CACHE[key] = obj.data
    else:
        obj = bpy.data.objects.new(name, CACHE[key]); scene.collection.objects.link(obj)
    own(obj,name); obj.location=pos; obj.rotation_euler.z=rotation
    return obj

def cylinder(name, pos, radius, depth, mat, vertices=20):
    key=('cylinder',radius,round(depth,5),mat,vertices)
    if key not in CACHE:
        bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=depth)
        obj=bpy.context.object;obj.data.materials.append(M[mat]);CACHE[key]=obj.data
        for p in obj.data.polygons: p.use_smooth = len(p.vertices)==4
    else:
        obj=bpy.data.objects.new(name,CACHE[key]);scene.collection.objects.link(obj)
    own(obj,name);obj.location=pos;return obj

def rod(name, a, b, radius, mat, vertices=12):
    delta=Vector(b)-Vector(a)
    obj=cylinder(name,(Vector(a)+Vector(b))*.5,radius,delta.length,mat,vertices)
    obj.rotation_euler=delta.to_track_quat('Z','Y').to_euler();return obj

def text(name, value, pos, size, mat, rotation=(math.pi/2,0,0), align='CENTER'):
    curve=bpy.data.curves.new(name,'FONT');curve.body=value;curve.size=size
    curve.align_x=align;curve.align_y='CENTER';curve.extrude=.0015;curve.bevel_depth=.0008
    curve.materials.append(M[mat]);obj=bpy.data.objects.new(name,curve);group.objects.link(obj)
    obj.location=pos;obj.rotation_euler=rotation;return obj

def light(name, pos, color, watts, kind='POINT', target=None, cone=65):
    kinds=[i.identifier for i in bpy.data.lights.bl_rna.functions['new'].parameters['type'].enum_items]
    assert kind in kinds
    data=bpy.data.lights.new(name,kind);data.color=color;data.energy=watts;data.shadow_soft_size=.06
    if kind=='SPOT': data.spot_size=math.radians(cone);data.spot_blend=.4
    obj=bpy.data.objects.new(name,data);group.objects.link(obj);obj.location=pos
    if target: obj.rotation_euler=(Vector(target)-Vector(pos)).to_track_quat('-Z','Y').to_euler()
    return obj

def tube(name, points, radius, mat):
    curve=bpy.data.curves.new(name,'CURVE');curve.dimensions='3D';curve.resolution_u=12
    curve.bevel_depth=radius;curve.bevel_resolution=2
    spline=curve.splines.new('POLY');spline.points.add(len(points)-1)
    for p,co in zip(spline.points,points):p.co=(*co,1)
    curve.materials.append(M[mat]);obj=bpy.data.objects.new(name,curve);group.objects.link(obj);return obj

def setup():
    global scene
    scene=bpy.data.scenes.new('Rainy Corner - Koyomi Mart');bpy.context.window.scene=scene
    scene.unit_settings.system='METRIC'
    palette={
        'base':((.055,.075,.095),.4,.25), 'road':((.045,.060,.078),.25,.12),
        'puddle':((.025,.042,.052),.055,.30),'concrete':((.36,.40,.40),.7,0),
        'tile':((.64,.62,.55),.35,0),'white':((.83,.84,.78),.32,0),
        'plaster':((.65,.67,.61),.72,0),'metal':((.19,.25,.29),.28,.8),
        'steel':((.54,.60,.62),.22,.9),'black':((.015,.021,.027),.42,.15),
        'green':((.025,.28,.19),.26,.15),'cyan':((.04,.52,.64),.25,.15),
        'orange':((.91,.26,.07),.38,0),'red':((.58,.045,.055),.28,.2),
        'blue':((.08,.20,.42),.28,.25),'yellow':((.93,.67,.16),.45,0),
        'wood':((.36,.18,.085),.55,0),'rubber':((.023,.030,.034),.85,0),
        'leaf':((.08,.20,.075),.8,0),'paper':((.82,.75,.58),.8,0)
    }
    for name,(color,rough,metal) in palette.items():material(name,color,rough,metal)
    glass=material('glass',(.97,.99,1),.06,0)
    bsdf=next(n for n in glass.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    bsdf.inputs['Transmission Weight'].default_value=1;bsdf.inputs['IOR'].default_value=1.5
    material('warm_light',(1,.74,.42),.25,emission=5)
    material('cream_light',(1,.93,.77),.3,emission=3)
    material('cyan_light',(.12,.76,1),.22,emission=4)
    material('pink_light',(.95,.13,.40),.25,emission=3)
    material('green_light',(.16,.8,.40),.25,emission=3)
    collection('01 - Square plinth and wet streets')
    box('Single complete square collectible plinth',(0,0,-.25),(10,10,.5),'base',.12)
    box('Wet asphalt full square',(0,0,.016),(9.96,9.96,.032),'road',.04)
    box('Raised corner sidewalk',(.4,1.05,.11),(6.5,5.2,.20),'concrete',.05)
    # Segmented curb follows both street edges and makes the L-shaped corner explicit.
    for i in range(13):box('Front curb stone',(-2.6+i*.5,-1.58,.17),(.48,.18,.26),'concrete',.018)
    for i in range(10):box('Alley curb stone',(-2.88,-1.2+i*.5,.17),(.18,.48,.26),'concrete',.018)
    for i in range(7):box('Reflective zebra crossing',(-3.88,-3.86+i*.43,.042),(1.65,.21,.014),'white',.01)
    for x in [-1.8,1.0,3.8]:box('Parking bay line',(x,-2.69,.04),(.06,1.8,.012),'white',.001)
    for i in range(18):box('Sidewalk tile seam',(-2.62+i*.35,-1.36,.216),(.014,.25,.006),'metal',0)
    for i,(x,y,sx,sy) in enumerate([(-.8,-3.1,1.3,.5),(2.8,-3.8,.75,.44),(-3.7,.6,.5,1.3),(.9,-1.9,.7,.21),(4.0,2.1,.38,.8)]):
        bpy.ops.mesh.primitive_circle_add(vertices=48,radius=1,fill_type='NGON')
        obj=own(bpy.context.object,'Rainwater puddle '+str(i));obj.location=(x,y,.043);obj.scale=(sx,sy,1);obj.data.materials.append(M['puddle'])
    for x in [-2.2,.8,3.15]:
        box('Storm drain recess',(x,-1.90,.04),(.75,.28,.028),'black',.008)
        for i in range(10):box('Drain grate',(x-.33+i*.073,-1.9,.061),(.028,.25,.018),'steel',.002)
    collection('02 - Koyomi Mart architecture')
    box('Shop tiled foundation',(.5,1.13,.23),(6,4.5,.15),'tile',.03)
    box('Back stucco wall',(.5,3.40,1.7),(6,.16,2.95),'plaster',.02)
    box('Left stucco wall',(-2.48,1.13,1.7),(.16,4.5,2.95),'plaster',.02)
    box('Right low wall',(3.49,1.1,.52),(.14,4.5,.55),'plaster',.02)
    box('Flat roof',(.5,1.13,3.22),(6.35,4.78,.18),'white',.035)
    for x in [-2.56,3.56]:box('Roof edge cap',(x,1.13,3.4),(.13,4.85,.25),'metal',.02)
    box('Roof rear cap',(.5,3.5,3.4),(6.2,.13,.25),'metal',.02)
    box('Front fascia lightbox',(.5,-1.20,2.91),(6.35,.28,.50),'cream_light',.035)
    box('Teal fascia stripe',(.5,-1.353,2.73),(6.25,.018,.065),'cyan_light',.002)
    box('Green fascia stripe',(.5,-1.355,2.79),(6.25,.018,.06),'green',.002)
    text('Koyomi Mart illuminated logo','KOYOMI  MART',(.12,-1.365,2.995),.34,'green')
    text('24 hour sign','24h',(3.06,-1.372,2.98),.25,'pink_light')
    box('Rain canopy',(.5,-1.51,2.62),(6.25,.9,.085),'metal',.022)
    for x in [-2.39,-.65,1.04,2.24,3.38]:box('Front aluminum mullion',(x,-1.075,1.46),(.065,.085,2.32),'steel',.008)
    for z in [.31,2.57]:box('Front glazing rail',(.5,-1.075,z),(5.88,.09,.06),'metal',.006)
    for x,w in [(-1.52,1.66),(.19,1.62),(1.63,1.1),(2.8,1.08)]:
        box('Clear storefront glazing',(x,-1.07,1.45),(w,.016,2.22),'glass',0)
    for x in [1.36,1.9]:box('Automatic sliding door handle',(x,-1.145,1.4),(.028,.025,.35),'steel',.007)
    box('Automatic door sensor',(1.65,-1.16,2.49),(.3,.07,.07),'black',.009)
    for y in [-.99,.48,1.95,3.38]:box('Side window mullion',(3.49,y,1.63),(.07,.06,2.03),'metal',.006)
    for y in [-.24,1.22,2.68]:box('Clear side window',(3.49,y,1.64),(.016,1.4,1.9),'glass',0)
    box('Entrance ribbed mat',(1.65,-1.38,.244),(1.15,.62,.025),'rubber',.018)
    for i in range(14):box('Mat rib',(1.15+i*.077,-1.38,.26),(.018,.57,.01),'metal',0)
    for x in [-1.55,.5,2.55]:
        box('Warm ceiling luminaire',(x,1.15,3.09),(1.20,.28,.045),'warm_light',.009)
        light('Interior warm punctual '+str(x),(x,1.15,2.78),(1,.78,.52),85)
    light('Door canopy pool',(1.5,-1.48,2.5),(1,.65,.33),70,'SPOT',(1.5,-2.1,0),90)
    data=bpy.data.cameras.new('Diorama camera');data.type='ORTHO';data.ortho_scale=15.3
    cam=bpy.data.objects.new('Diorama camera',data);group.objects.link(cam);cam.location=(12,-17,11)
    cam.rotation_euler=(Vector((0,.4,1.1))-cam.location).to_track_quat('-Z','Y').to_euler();scene.camera=cam
    scene.world=bpy.data.worlds.new('Very dark blue night');scene.world.use_nodes=True
    background=next(n for n in scene.world.node_tree.nodes if n.type=='BACKGROUND')
    background.inputs['Color'].default_value=(.055,.085,.15,1);background.inputs['Strength'].default_value=.15
    scene.render.resolution_x=1200;scene.render.resolution_y=1000;scene.render.resolution_percentage=100
    for area in bpy.context.screen.areas:
        if area.type=='VIEW_3D': area.spaces.active.region_3d.view_perspective='CAMERA'
    print('Architecture objects:',len(scene.objects))

def interior():
    collection('03 - Stocked convenience store interior')
    for x in [-1.20,.48]:
        for z in [.40,.82,1.25,1.68]:box('Gondola shelf',(x,1.12,z),(1.12,2.22,.045),'white',.006)
        for y in [.04,2.20]:box('Shelf upright',(x,y,1.05),(1.08,.055,1.40),'metal',.009)
        for side in [-1,1]:
            for row,z in enumerate([.52,.94,1.37,1.80]):
                for col in range(7):
                    y=.22+col*.29;xx=x+side*.36;mat=['red','orange','yellow','blue','green','paper'][(row+col)%6]
                    box('Snack carton',(xx,y,z),(.20,.18,.20),'paper' if row==3 else mat,.012)
                    box('Package paper label',(xx+side*.104,y,z),(.006,.115,.075),'white',.001)
            box('Shelf price strip',(x+side*.56,1.12,1.70),(.016,2.2,.075),'yellow',.002)
    # Back-wall refrigerated drinks, full of individually modeled bottles and cans.
    for x in [-1.62,-.28,1.06]:
        box('Drinks refrigerator',(x,3.08,1.47),(1.2,.52,2.30),'metal',.025)
        box('Fridge luminous back',(x,2.805,1.55),(1.04,.012,1.96),'cream_light',0)
        for row,z in enumerate([.55,.95,1.35,1.75,2.15]):
            box('Refrigerated shelf',(x,2.66,z-.1),(1.08,.3,.027),'white',.004)
            for c in range(6):
                mat=['blue','green','orange','red','yellow','white'][(row+c)%6]
                cylinder('Drink bottle',(x-.44+c*.175,2.60,z),.057,.19,mat,12)
                cylinder('Bottle cap',(x-.44+c*.175,2.60,z+.105),.035,.025,'white',10)
        box('Refrigerator glass door',(x,2.465,1.5),(1.12,.013,2.13),'glass',0)
        box('Fridge door handle',(x+.45,2.426,1.5),(.025,.028,.42),'steel',.008)
        text('Drinks category','DRINKS',(x,2.44,2.52),.13,'blue')
    box('Back-room storage door',(2.59,3.295,1.42),(1.0,.065,2.32),'green',.02)
    text('Staff only sign','STAFF',(2.59,3.251,1.96),.12,'white')
    collection('04 - Service counter and fresh food')
    box('Cash counter',(2.50,.25,.83),(1.22,2.23,1.06),'white',.035)
    box('Cash counter wood top',(2.50,.25,1.39),(1.31,2.32,.09),'wood',.028)
    box('POS screen',(2.30,-.49,1.74),(.30,.065,.25),'black',.018)
    box('POS luminous display',(2.30,-.529,1.74),(.25,.008,.19),'cyan_light',.001)
    cylinder('POS stand',(2.30,-.47,1.51),.035,.20,'metal')
    box('Cash register',(2.48,-.45,1.47),(.33,.33,.055),'black',.006)
    box('Coffee machine',(2.58,.20,1.69),(.44,.40,.54),'metal',.03)
    box('Coffee touch screen',(2.58,-.012,1.78),(.22,.013,.12),'cyan_light',.004)
    for x in [2.46,2.69]:cylinder('Coffee paper cup',(x,-.015,1.50),.047,.1,'paper')
    box('Oden warm display',(2.50,.91,1.60),(.98,.48,.33),'glass',.005)
    box('Oden warming tray',(2.5,.91,1.45),(.98,.48,.07),'steel',.012)
    for x in [2.24,2.48,2.72]:
        for y in [.80,1.02]:cylinder('Oden food',(x,y,1.52),.079,.08,'orange',12)
    box('Bento chilled case',(-1.35,-.40,.76),(1.80,.70,.86),'white',.028)
    for x in [-1.98,-1.57,-1.16,-.75]:
        box('Bento lunch box',(x,-.47,1.22),(.34,.41,.08),'black',.008)
        box('Rice in bento',(x-.075,-.46,1.275),(.13,.29,.032),'white',.004)
        box('Bento side dish',(x+.072,-.46,1.275),(.13,.28,.032),'orange',.007)
    text('Fresh food counter label','BENTO / FRESH',(-1.35,-.762,.94),.13,'green')
    box('Onigiri basket',(-.32,-.47,1.20),(.35,.40,.11),'wood',.012)
    for x in [-.42,-.29,-.16]:
        item=box('Onigiri wrapped rice',(x,-.48,1.33),(.11,.12,.13),'white',.015)
        box('Onigiri seaweed strip',(x,-.548,1.31),(.045,.008,.09),'green',.001)
    box('Magazine rack',(-2.25,-.35,.91),(.23,.9,1.12),'wood',.018)
    for z in [.71,1.03,1.34]:
        for y in [-.62,-.34,-.06]:box('Magazine cover',(-2.10,y,z),(.045,.23,.24),RNG.choice(['red','cyan','yellow']),.003)
    box('Ice cream freezer',(2.63,2.25,.68),(1.08,.85,.79),'white',.028)
    box('Freezer sliding glass top',(2.63,2.25,1.10),(1.02,.78,.025),'glass',.006)
    text('Freezer ICE label','ICE',(2.63,1.81,.80),.20,'blue')
    for x in [-1.7,1.4]:
        box('Interior hanging lightbox',(x,1.6,2.59),(1.15,.07,.29),'cream_light',.015)
        text('Interior department sign','FOOD' if x<0 else 'COFFEE',(x,1.557,2.60),.15,'green')
    for y in [-.3,1.0,2.15]:box('Floor aisle guide',(1.4,y,.315),(.07,.4,.006),'green',0)
    print('Stocked interior objects:',len(scene.objects))

def street():
    collection('05 - Vending machines and corner furniture')
    for x,mat in [(-3.48,'red'),(-4.39,'blue')]:
        box('Vending machine body',(x,1.55,1.15),(.82,.75,2.24),mat,.045)
        box('Vending machine display',(x,1.159,1.47),(.66,.02,1.27),'cream_light',.012)
        for row in range(4):
            for col in range(4):
                xx=x-.23+col*.155;z=1.03+row*.28
                cylinder('Vending drink',(xx,1.12,z),.043,.16,['red','blue','green','orange'][col],12)
                box('Vending illuminated button',(xx,1.087,z-.106),(.084,.017,.033),'cyan_light',.004)
        box('Vending collection hatch',(x,1.151,.43),(.53,.025,.22),'black',.016)
        text('Vending cold drink sign','COLD',(x,1.13,2.16),.12,'white')
        light('Vending machine spill '+mat,(x,1.01,1.55),(.20,.58,1) if mat=='blue' else (1,.20,.35),20)
    # Bicycle with tires, spokes, frame, chain, saddle, pedals and basket.
    for x in [-2.01,-.98]:
        bpy.ops.mesh.primitive_torus_add(major_segments=40,minor_segments=8,major_radius=.32,minor_radius=.027)
        obj=own(bpy.context.object,'Bicycle tire');obj.location=(x,-1.91,.56);obj.rotation_euler.x=math.pi/2;obj.data.materials.append(M['rubber'])
        for i in range(12):
            a=i*math.tau/12;rod('Bicycle spoke',(x,-1.91,.56),(x+.30*math.cos(a),-1.91,.56+.30*math.sin(a)),.004,'steel',6)
    a=(-2.01,-1.91,.56);b=(-1.66,-1.91,.99);c=(-1.50,-1.91,.55);d=(-1.14,-1.91,1.00);e=(-.98,-1.91,.56)
    for p,q in [(a,b),(b,c),(c,a),(b,d),(d,c),(d,e)]:rod('Bicycle turquoise frame',p,q,.027,'cyan')
    rod('Bicycle seat post',b,(-1.66,-1.91,1.14),.021,'steel')
    box('Bicycle saddle',(-1.68,-1.91,1.17),(.25,.17,.055),'rubber',.03)
    rod('Handlebar stem',d,(-1.10,-1.91,1.27),.02,'steel')
    rod('Handlebar',(-1.1,-2.12,1.26),(-1.1,-1.70,1.26),.018,'metal')
    box('Bicycle basket',(-.83,-1.91,1.14),(.28,.30,.26),'metal',.012)
    box('Basket inset',(-.83,-1.91,1.28),(.23,.25,.015),'black',.001)
    rod('Bicycle kickstand',c,(-1.56,-2.16,.23),.015,'steel')
    box('Umbrella rack',(2.70,-1.77,.52),(.47,.30,.56),'metal',.015)
    for i in range(4):
        x=2.54+i*.105;rod('Folded umbrella shaft',(x,-1.78,.31),(x,-1.78,1.22),.012,'steel')
        rod('Folded umbrella canopy',(x,-1.78,.43),(x,-1.78,1.02),.038,['blue','red','yellow','white'][i])
        tube('Umbrella curved handle',[(x+.045*math.sin(t),-1.78,1.22+.045*math.cos(t)) for t in [j*math.pi/10 for j in range(16)]],.012,'wood')
    for x,mat in [(3.55,'green'),(4.02,'blue')]:
        box('Recycling bin',(x,-.79,.65),(.38,.44,.78),mat,.03)
        box('Waste bin slotted lid',(x,-.79,1.065),(.4,.47,.065),'metal',.018)
        box('Recycling opening',(x,-1.019,.91),(.22,.014,.075),'black',.01)
    collection('06 - Street lighting signage and overhead lines')
    rod('Street lamp post',(-4.08,-2.38,.05),(-4.08,-2.38,3.76),.058,'metal')
    rod('Street lamp arm',(-4.08,-2.38,3.70),(-3.63,-2.38,3.70),.04,'metal')
    box('Streetlamp head',(-3.63,-2.38,3.69),(.48,.22,.13),'metal',.03)
    box('Amber streetlamp diffuser',(-3.63,-2.38,3.61),(.4,.16,.012),'warm_light',.004)
    light('Streetlamp warm cone',(-3.63,-2.38,3.57),(1,.61,.29),180,'SPOT',(-3.65,-2.45,0),100)
    for x,y in [(-4.47,3.42),(4.18,3.50)]:
        rod('Concrete utility pole',(x,y,.04),(x,y,4.66),.086,'concrete')
        rod('Utility cross arm',(x-.43,y,4.18),(x+.43,y,4.18),.042,'metal')
        for xx in [x-.32,x+.32]:
            cylinder('Ceramic insulator',(xx,y,4.31),.060,.17,'white',12)
    for offset in [-.28,.28]:
        points=[]
        for i in range(33):
            t=i/32;points.append((-4.47+8.65*t,3.46+offset,4.36-.7*4*t*(1-t)))
        tube('Sagging overhead power cable',points,.015,'black')
    rod('Road sign post',(-3.25,-3.6,.04),(-3.25,-3.6,2.16),.032,'steel')
    box('Neighborhood road sign',(-3.25,-3.6,2.12),(.68,.065,.32),'blue',.025)
    text('Road sign text','KOYOMI  ->',(-3.25,-3.638,2.12),.081,'white')
    for x in [.1,1.2,2.3,3.4]:rod('Corner guardrail post',(x,-4.43,.05),(x,-4.43,.65),.045,'white')
    rod('Corner guardrail top',(.1,-4.43,.61),(3.4,-4.43,.61),.039,'white')
    for x in [.1,3.4]:box('Guardrail orange reflector',(x,-4.479,.56),(.095,.025,.14),'orange',.01)
    box('Alley entry stone',(-3.90,3.77,.10),(1.85,.5,.16),'concrete',.02)
    text('Alley marking','ALLEY',(-3.85,3.60,.193),.23,'white',rotation=(0,0,0))
    box('Outdoor AC condenser',(4.05,2.18,.57),(.66,1.11,.82),'white',.025)
    fan=cylinder('AC fan grill',(4.40,2.18,.59),.31,.024,'metal',32);fan.rotation_euler.y=math.pi/2
    for i in range(9):rod('AC grille slat',(4.42,1.93+i*.059,.35),(4.42,1.93+i*.059,.84),.008,'steel',6)
    tube('AC drain pipe',[(3.55,2.9,1.9),(4.15,2.9,1.9),(4.15,2.9,.13)],.022,'white')
    box('Public notice board',(-2.573,1.1,1.94),(.06,1.17,.84),'wood',.01)
    for y,mat in [(.76,'paper'),(1.13,'cyan'),(1.49,'pink_light')]:box('Community poster',(-2.612,y,1.94),(.009,.30,.61),mat,.001)
    box('Side neon bracket',(3.62,-.74,2.40),(.15,.55,.10),'metal',.006)
    box('Side neon sign',(3.75,-.74,2.15),(.12,.59,.55),'pink_light',.025)
    text('Side neon OPEN','OPEN',(3.822,-.74,2.16),.13,'white',rotation=(math.pi/2,0,math.pi/2))
    light('Side pink neon pool',(4.0,-.73,2.1),(1,.12,.32),35)
    # Roof vents and subtle utilitarian rooftop detail.
    for x in [-1.3,1.7]:
        box('Rooftop service unit',(x,2.3,3.55),(.82,.76,.50),'metal',.028)
        cylinder('Rooftop exhaust',(x,2.3,3.87),.19,.15,'steel')
    print('Completed street objects:',len(scene.objects))

def finish():
    ROOT.mkdir(parents=True,exist_ok=True)
    scene['author']='Original procedural scene authored for DXRDemo through Blender MCP'
    import importlib.util
    spec=importlib.util.spec_from_file_location('rainy_details',ROOT/'refine_scene.py')
    details=importlib.util.module_from_spec(spec);spec.loader.exec_module(details)
    details.configure_glass()
    scene.render.filepath=str(ROOT/'RainyCorner-preview.png')
    # Use local Japanese font outlines. Geometry, rather than fonts, is exported in GLB.
    font_path=Path('C:/Windows/Fonts/YuGothM.ttc')
    if font_path.exists():
        font=bpy.data.fonts.load(str(font_path),check_existing=True)
        for name,value in [('Koyomi Mart illuminated logo','こよみ  KOYOMI MART'),('Road sign text','こよみ通り  →')]:
            obj=scene.objects.get(name)
            if obj and obj.type=='FONT':
                obj.data.font=font;obj.data.body=value
                if name=='Koyomi Mart illuminated logo':obj.data.size=.30
    formats=[i.identifier for i in scene.render.image_settings.bl_rna.properties['file_format'].enum_items]
    scene.render.image_settings.file_format=next(i for i in formats if i=='PNG')
    # Engine enum is dynamically registered; assignment validates the installed engine.
    try: scene.render.engine='CYCLES'
    except TypeError: pass
    if hasattr(scene,'cycles'):
        scene.cycles.samples=64;scene.cycles.use_denoising=True
    bpy.ops.file.pack_all()
    bpy.ops.wm.save_as_mainfile(filepath=str(ROOT/'RainyCorner.blend'))
    bpy.ops.export_scene.gltf(filepath=str(ROOT/'RainyCorner.glb'),export_format='GLB',use_active_scene=True,
        export_lights=True,export_import_convert_lighting_mode='RAW',export_apply=True,export_animations=False)
    details.preserve_glass_volume(ROOT/'RainyCorner.glb')
    print('Saved Blender source:',ROOT/'RainyCorner.blend')

if __name__=='__main__':
    setup();interior();street();finish()
