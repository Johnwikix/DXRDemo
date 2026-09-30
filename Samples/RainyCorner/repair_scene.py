"""Physical assembly correction for Rainy Corner, authored in local Blender.

Run begin(), street(), bicycle(), vending(), validate() through Blender MCP.
apply() is also called by refine_scene.export(), so fresh exports keep the fixes.
Dimensions are metres. No downloaded assets or preview-only lights are used.
"""
import bpy
import math
import json
import importlib.util
from pathlib import Path
from mathutils import Vector, Matrix

ROOT = Path(__file__).resolve().parent
REVISION = 2
COLLECTION = '08 - Grounded street furniture and assembled bicycle'
scene = bpy.context.scene
spec = importlib.util.spec_from_file_location('rainy_repair_geometry', ROOT / 'refine_scene.py')
g = importlib.util.module_from_spec(spec)
spec.loader.exec_module(g)
g.scene = scene
road_z = .032
bike_root = None
joints = []


def bounds(obj):
    """Use real evaluated vertices, including rotation, rather than rotated AABB corners."""
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh()
    points = [evaluated.matrix_world @ v.co for v in mesh.vertices]
    evaluated.to_mesh_clear()
    return (Vector(tuple(min(p[i] for p in points) for i in range(3))),
            Vector(tuple(max(p[i] for p in points) for i in range(3))))


def remove(prefixes):
    for obj in list(scene.objects):
        if obj.name.startswith(tuple(prefixes)):
            bpy.data.objects.remove(obj, do_unlink=True)


def material(name, color, roughness=.5, metallic=0, emission=0):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    node = next(n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    for key, value in {'Base Color': (*color, 1), 'Roughness': roughness,
                       'Metallic': metallic, 'Emission Color': (*color, 1),
                       'Emission Strength': emission}.items():
        node.inputs[key].default_value = value
    mat.diffuse_color = (*color, 1)
    return mat


def box(name, pos, size, mat='steel', bevel=.002):
    return g.box(name, pos, size, mat, bevel)


def rod(name, a, b, radius=.008, mat='steel', segments=20):
    return g.rod(name, a, b, radius, mat, segments)


def path(name, points, radius=.003, mat='steel', parent=None):
    # Mesh tubes export directly; bends share ring vertices and have no loose rod caps.
    vertices, faces = [], []
    points = [Vector(p) for p in points]
    for i, point in enumerate(points):
        tangent = points[min(i+1, len(points)-1)] - points[max(0, i-1)]
        tangent.normalize()
        axis = Vector((0, 1, 0))
        if abs(tangent.dot(axis)) > .95:
            axis = Vector((1, 0, 0))
        u = tangent.cross(axis).normalized()
        v = tangent.cross(u).normalized()
        for j in range(8):
            angle = j * math.tau / 8
            vertices.append(tuple(point + radius * (u*math.cos(angle) + v*math.sin(angle))))
    for i in range(len(points)-1):
        for j in range(8):
            a = i*8+j
            b = i*8+(j+1)%8
            faces.append((a, b, b+8, a+8))
    faces += [tuple(reversed(range(8))), tuple((len(points)-1)*8+j for j in range(8))]
    obj = g.mesh_object(name, vertices, faces, mat,
                       key=('path',tuple(tuple(p) for p in points),radius,mat))
    for polygon in obj.data.polygons:
        polygon.use_smooth = len(polygon.vertices) == 4
    obj.parent = parent
    return obj


def begin():
    global road_z, joints
    assert scene.name == 'Rainy Corner - Koyomi Mart', 'Select the Rainy Corner scene first'
    road_z = bounds(scene.objects['Wet asphalt full square'])[1].z
    old = bpy.data.collections.get(COLLECTION)
    if old:
        for obj in list(old.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.collections.remove(old)
    g.detail = bpy.data.collections.new(COLLECTION)
    scene.collection.children.link(g.detail)
    joints = []
    scene.pop('assembly_revision', None)
    remove(('Bicycle', 'Cross-laced bicycle', 'Handlebar', 'Basket inset',
            'RC Bicycle', 'Rainwater puddle', 'Corner guardrail', 'Guardrail',
            'Recycling bin', 'Waste bin', 'Recycling opening', 'Drain grating cross bar'))
    material('RC chain steel', (.25, .28, .29), .32, .85)
    material('RC bicycle enamel', (.035, .36, .34), .27, .25)
    material('RC saddle leather', (.08, .035, .018), .65)
    material('RC vending backing', (.30, .34, .37), .62)
    material('RC vending LED diffuser', (.72, .87, 1), .3, emission=48)
    material('RC ceiling diffuser', (1, .80, .55), .3, emission=75)
    # Wet asphalt is dielectric, with a broad reflection rather than mirror-flat pads.
    road = next(n for n in bpy.data.materials['road'].node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    road.inputs['Metallic'].default_value = 0
    road.inputs['Roughness'].default_value = .38
    print('Correction ready; actual asphalt surface:', round(road_z, 6))


def street():
    for x in [.1, 1.2, 2.3, 3.4]:
        box('RC Guardrail ground plate', (x, -4.43, road_z+.011), (.20, .20, .022))
        rod('RC Guardrail post', (x, -4.43, road_z+.022), (x, -4.43, .66), .045, 'white')
        for dx in [-.063, .063]:
            for dy in [-.063, .063]:
                g.torus('RC Guardrail washer', (x+dx, -4.43+dy, road_z+.024), .012, .002,
                        segments=24, rings=8)
                rod('RC Guardrail embedded bolt', (x+dx, -4.43+dy, road_z+.012),
                    (x+dx, -4.43+dy, road_z+.033), .007, 'metal', 6)
    rod('RC Guardrail continuous top', (.1, -4.43, .625), (3.4, -4.43, .625), .039, 'white')
    for x in [.1, 3.4]:
        box('RC Guardrail reflector mount', (x, -4.477, .57), (.10, .025, .14), 'metal')
        box('RC Guardrail orange reflector', (x, -4.492, .57), (.083, .008, .12), 'orange')

    # Two complete hollow bins stand on the road beyond the side wall and raised sidewalk.
    for index, (y, mat, label) in enumerate([(-.82, 'green', 'BOTTLES'), (-.17, 'blue', 'CANS')]):
        x = 4.04
        low, high = road_z+.03, road_z+.75
        for dx in [-.145, .145]:
            for dy in [-.165, .165]:
                box('RC Bin grounded rubber foot', (x+dx, y+dy, road_z+.015), (.055, .055, .030), 'rubber')
        box('RC Bin bottom', (x, y, low+.015), (.38, .44, .030), mat, .006)
        for dx in [-.18, .18]:
            box('RC Bin side shell', (x+dx, y, (low+high)/2), (.02, .44, high-low), mat, .005)
        for dy in [-.21, .21]:
            box('RC Bin front rear shell', (x, y+dy, (low+high)/2), (.34, .02, high-low), mat, .005)
        # An actual aperture in the lid; the interior remains empty down to the bottom.
        outer = [(-.20,-.23), (.20,-.23), (.20,.23), (-.20,.23)]
        inner = [(-.115,-.08), (.115,-.08), (.115,.08), (-.115,.08)]
        vertices = [(xx, yy, zz) for zz in [0, .04] for ring in [outer, inner] for xx, yy in ring]
        faces = []
        for i in range(4):
            j = (i+1)%4
            faces += [(8+i, 8+j, 12+j, 12+i), (i, 4+i, 4+j, j),
                      (i, j, 8+j, 8+i), (4+i, 12+i, 12+j, 4+j)]
        g.mesh_object('RC Bin lid with open deposit slot', vertices, faces, 'metal', (x, y, high))
        box('RC Bin label panel', (x, y-.224, low+.45), (.28, .008, .15), 'white')
        curve = bpy.data.curves.new('RC Bin embossed label', 'FONT')
        curve.body = label
        curve.size = .045
        curve.extrude = .0005
        curve.materials.append(bpy.data.materials[mat])
        obj = bpy.data.objects.new('RC Bin embossed '+label, curve)
        g.detail.objects.link(obj)
        obj.location = (x-.127, y-.229, low+.443)
        obj.rotation_euler.x = math.pi/2
        for dx in [-.195, .195]:
            path('RC Bin attached side handle', [(x+dx, y-.08, high-.20),
                 (x+dx*1.14, y-.08, high-.23), (x+dx*1.14, y+.08, high-.23),
                 (x+dx, y+.08, high-.20)], .007, 'black')
        for z in [low+.04, high-.04]:
            box('RC Bin rolled front edge', (x, y-.223, z), (.35, .014, .013), 'metal')

    # Grating belongs in the recess, not suspended at sidewalk height.
    for obj in scene.objects:
        if obj.name.startswith('Storm drain recess'):
            obj.location.z = road_z-.004
            obj.scale.z = .010/.028
        elif obj.name.startswith('Drain grate'):
            obj.location.z = road_z+.002
            obj.scale.z = .006/.018
        elif obj.name.startswith(('Parking bay line', 'Reflective zebra crossing')):
            obj.location.z = road_z+.001
            obj.scale.z = .002/(.012 if obj.name.startswith('Parking') else .014)
        elif obj.name.startswith('Sidewalk tile seam'):
            obj.location.z = .210
    for x in [-2.2, .8, 3.15]:
        for dy in [-.127, .127]:
            box('RC Drain recessed frame', (x, -1.90+dy, road_z+.001), (.73, .014, .008), 'metal')

    # The old umbrella stand also sat on the curb. Move its whole contents together.
    rack = scene.objects.get('Umbrella rack')
    if rack:
        for obj in scene.objects:
            if obj.name.startswith(('Folded umbrella', 'Umbrella curved handle')):
                obj.location += Vector((0, -.23, -.184))
        bpy.data.objects.remove(rack, do_unlink=True)
    box('RC Umbrella grounded drip tray', (2.7, -2.00, road_z+.018), (.47, .30, .036), 'metal')
    for dx in [-.21, .21]:
        for dy in [-.12, .12]:
            rod('RC Umbrella rack upright', (2.7+dx, -2+dy, road_z+.035),
                (2.7+dx, -2+dy, .39), .012, 'metal')
    path('RC Umbrella rack upper rim', [(2.49,-2.12,.39),(2.91,-2.12,.39),
         (2.91,-1.88,.39),(2.49,-1.88,.39),(2.49,-2.12,.39)], .012, 'metal')
    for i in range(3):
        rod('RC Umbrella rack divider', (2.565+i*.105, -2.12, .39),
            (2.565+i*.105, -1.88, .39), .006, 'metal')

    # Existing condenser casing retains its position, with real isolation mounts underneath.
    for dx in [-.24, .24]:
        for dy in [-.43, .43]:
            box('RC AC grounded mounting foot', (4.05+dx, 2.18+dy, (road_z+.152)/2),
                (.10, .12, .152-road_z), 'metal')
            box('RC AC isolation pad', (4.05+dx, 2.18+dy, .156), (.11, .13, .008), 'rubber')
    box('RC Streetlamp ground flange', (-4.08,-2.38,(road_z+.05)/2), (.23,.23,.05-road_z), 'metal')
    box('RC Roadsign embedded footing', (-3.25,-3.6,road_z), (.12,.12,.016), 'concrete')
    print('Street furniture rebuilt with ground contact and wall clearance')


def bike_own(obj):
    obj.parent = bike_root
    return obj


def br(name, a, b, radius=.009, mat='steel'):
    obj = bike_own(rod('RC Bicycle '+name, a, b, radius, mat))
    obj['assembly_a'] = list(a)
    obj['assembly_b'] = list(b)
    return obj


def bb(name, pos, size, mat='steel', bevel=.003):
    return bike_own(box('RC Bicycle '+name, pos, size, mat, bevel))


def bt(name, pos, major, minor, mat='steel', segments=96):
    return bike_own(g.torus('RC Bicycle '+name, pos, major, minor, mat,
                           (math.pi/2,0,0), segments, 12))


def gear(name, center, pitch_radius, teeth, inner, thickness=.004):
    # A toothed annulus, including separate sidewalls and a real central opening.
    count = teeth*4
    vertices, faces = [], []
    for y in [-thickness/2, thickness/2]:
        for ring in [0, 1]:
            for i in range(count):
                angle = i*math.tau/count
                radius = inner if ring else pitch_radius + ([-.0035,.001,.003,.001][i%4])
                vertices.append((radius*math.cos(angle), y, radius*math.sin(angle)))
    for i in range(count):
        j = (i+1)%count
        faces += [(i,j,count+j,count+i), (2*count+i,3*count+i,3*count+j,2*count+j),
                  (i,2*count+i,2*count+j,j), (count+i,count+j,3*count+j,3*count+i)]
    return bike_own(g.mesh_object('RC Bicycle '+name, vertices, faces, 'RC chain steel', center,
                                 key=('gear',pitch_radius,teeth,inner,thickness)))


def chain(rear, crank, rear_radius, crank_radius, y):
    c0, c1 = Vector((rear[0],rear[2])), Vector((crank[0],crank[2]))
    delta = c1-c0
    direction = delta.normalized()
    perp = Vector((-direction.y, direction.x))
    ratio = (rear_radius-crank_radius)/delta.length
    top = ratio*direction + math.sqrt(1-ratio*ratio)*perp
    bottom = ratio*direction - math.sqrt(1-ratio*ratio)*perp
    theta0, theta1 = math.atan2(top.y,top.x), math.atan2(bottom.y,bottom.x)
    points = []
    def line(a,b):
        points.extend(a.lerp(b,i/60) for i in range(60))
    def arc(center,radius,start,end):
        sweep = (start-end)%math.tau
        points.extend(center + radius*Vector((math.cos(start-sweep*i/100),
                      math.sin(start-sweep*i/100))) for i in range(100))
    line(c0+rear_radius*top,c1+crank_radius*top)
    arc(c1,crank_radius,theta0,theta1)
    line(c1+crank_radius*bottom,c0+rear_radius*bottom)
    arc(c0,rear_radius,theta1,theta0)
    points.append(points[0])
    distances = [0]
    for a,b in zip(points,points[1:]):
        distances.append(distances[-1]+(b-a).length)
    links = round(distances[-1]/.0127)
    pitch = distances[-1]/links
    samples, cursor = [], 0
    for i in range(links):
        distance = i*pitch
        while distances[cursor+1] < distance:
            cursor += 1
        t = (distance-distances[cursor])/(distances[cursor+1]-distances[cursor])
        samples.append(points[cursor].lerp(points[cursor+1],t))
    # Each pair of plates shares the same roller pins with its neighbours.
    for i,a in enumerate(samples):
        b = samples[(i+1)%links]
        center = (a+b)*.5
        tangent = b-a
        length = tangent.length
        outline = []
        for end, phase in [(1,-math.pi/2),(-1,math.pi/2)]:
            for j in range(9):
                angle = phase+j*math.pi/8
                outline.append((end*(length/2-.001), angle))
        vertices = [(cx+.0034*math.cos(angle), yy, .0034*math.sin(angle))
                    for yy in [-.0007,.0007] for cx,angle in outline]
        n = len(outline)
        faces = [tuple(reversed(range(n))),tuple(range(n,2*n))]
        faces += [(j,(j+1)%n,(j+1)%n+n,j+n) for j in range(n)]
        for side in [-1,1]:
            offset = .0048 if i%2 else .0064
            obj = g.mesh_object('RC Bicycle chain link plate', vertices, faces, 'RC chain steel',
                               (center.x,y+side*offset,center.y), key=('chain plate',round(length,5)))
            obj.rotation_euler.y = -math.atan2(tangent.y,tangent.x)
            bike_own(obj)
        br('chain roller and rivet', (a.x,y-.0078,a.y),(a.x,y+.0078,a.y), .0029, 'RC chain steel')
    bike_root['chain_links'] = links
    bike_root['chain_pitch_m'] = pitch
    bike_root['chainline_m'] = y


def saddle():
    outline = [(.130,0),(.115,.031),(.060,.041),(0,.070),(-.070,.085),
               (-.120,.077),(-.140,.045),(-.145,0),(-.140,-.045),
               (-.120,-.077),(-.070,-.085),(0,-.070),(.060,-.041),(.115,-.031)]
    vertices, faces = [], []
    for scale,z in [(.90,-.020),(1,.008),(.94,.021)]:
        for x,y in outline:
            vertices.append((x*scale,y*scale,z-.009*max(0,x/.13)))
    n = len(outline)
    for ring in range(2):
        for i in range(n):
            j = (i+1)%n
            faces.append((ring*n+i,ring*n+j,(ring+1)*n+j,(ring+1)*n+i))
    faces += [tuple(reversed(range(n))),tuple(2*n+i for i in range(n))]
    obj = g.mesh_object('RC Bicycle contoured leather saddle',vertices,faces,'RC saddle leather',(-1.975,0,.69))
    for polygon in obj.data.polygons:
        polygon.use_smooth = len(polygon.vertices)==4
    return bike_own(obj)


def bicycle():
    global bike_root
    bike_root = bpy.data.objects.new('RC Bicycle complete assembly', None)
    g.detail.objects.link(bike_root)
    bike_root.location = (0,-2.19,road_z+.347)
    bike_root.rotation_euler.x = math.radians(7)
    rear, front = (-2.20,0,0), (-1.04,0,0)
    crank, seat = (-1.77,0,.045), (-1.94,0,.51)
    head_low, head_top = (-1.28,0,.48), (-1.33,0,.64)
    tires = []
    for name, axle in [('rear',rear),('front',front)]:
        tires.append(bt(name+' tire', axle, .320, .027, 'rubber', 128))
        bt(name+' rim', axle, .298, .012)
        bt(name+' rim brake track', axle, .305, .004, 'steel')
        br(name+' hub', (axle[0],-.052,0), (axle[0],.052,0), .022)
        br(name+' axle', (axle[0],-.076,0), (axle[0],.076,0), .009)
        for side in [-1,1]:
            bt(name+' hub flange', (axle[0],side*.028,0), .031, .004, segments=48)
            for i in range(16):
                angle = (i*2+(1 if side>0 else 0))*math.tau/32
                end = (axle[0]+.297*math.cos(angle),0,.297*math.sin(angle))
                start = (axle[0]+.03*math.cos(angle+side*.6),side*.028,.03*math.sin(angle+side*.6))
                br(name+' laced spoke',start,end,.0017)
            br(name+' axle nut', (axle[0],side*.060,0),(axle[0],side*.074,0), .014)
        br(name+' tire valve', (axle[0]+.288,0,0),(axle[0]+.272,0,0), .003, 'black')
        # A curved sheet fender with lateral width, rather than another wire ring.
        vertices, faces = [], []
        angles = [math.radians(-15+210*i/64) for i in range(65)]
        for angle in angles:
            for yy,rr in [(-.039,.354),(-.026,.365),(.026,.365),(.039,.354)]:
                vertices.append((axle[0]+rr*math.cos(angle),yy,rr*math.sin(angle)))
        for i in range(64):
            for j in range(3):
                faces.append((i*4+j,i*4+j+1,(i+1)*4+j+1,(i+1)*4+j))
        fender = g.mesh_object('RC Bicycle '+name+' curved mudguard',vertices,faces,'metal')
        bike_own(fender)
        for side in [-1,1]:
            for angle in [math.radians(20),math.radians(160)]:
                br(name+' mudguard stay',(axle[0],side*.060,0),
                   (axle[0]+.354*math.cos(angle),side*.037,.354*math.sin(angle)),.0035)
    bpy.context.view_layer.update()
    bottom = min(bounds(obj)[0].z for obj in tires)
    bike_root.location.z += road_z-bottom
    bpy.context.view_layer.update()

    enamel = 'RC bicycle enamel'
    for name,a,b,r in [('seat tube',crank,seat,.024),('top tube',seat,head_top,.021),
                       ('down tube',crank,head_low,.026),('head tube',head_low,head_top,.030)]:
        br(name,a,b,r,enamel)
    for side in [-1,1]:
        dropout = (rear[0],side*.064,0)
        br('rear chainstay',crank,dropout,.016,enamel)
        br('rear seatstay',seat,dropout,.014,enamel)
        fork_top = (head_low[0],side*.039,head_low[2])
        path('RC Bicycle curved front fork', [fork_top,(-1.22,side*.053,.29),
             (-1.13,side*.064,.09),(front[0],side*.064,0)], .016, enamel, bike_root)
        bb('front dropout', (front[0],side*.064,.012), (.046,.018,.052), enamel)
        bb('rear dropout', (rear[0]+.009,side*.064,.013), (.062,.018,.042), enamel)
    br('fork crown', (head_low[0],-.052,head_low[2]), (head_low[0],.052,head_low[2]), .022,enamel)
    for z in [head_low[2],head_top[2]]:
        collar = bt('headset bearing',(-1.28 if z==head_low[2] else -1.33,0,z),.033,.004)
        collar.rotation_euler = (0,math.atan2(-.05,.16),0)
    br('seatpost',seat,(-1.965,0,.66),.017)
    collar = bt('seatpost clamp',seat,.028,.004)
    collar.rotation_euler = (0,math.atan2(-.17,.465),0)
    saddle()
    for side in [-1,1]:
        br('saddle supporting rail',(-2.04,side*.028,.661),(-1.87,side*.028,.668),.004)
    br('handlebar quill stem',head_top,(-1.35,0,.81),.017)
    path('RC Bicycle swept handlebar',[(-1.42,-.24,.83),(-1.33,-.19,.835),(-1.30,-.10,.83),
         (-1.30,.10,.83),(-1.33,.19,.835),(-1.42,.24,.83)],.013,'steel',bike_root)
    br('handlebar stem clamp',(-1.35,0,.81),(-1.30,0,.83),.024)
    for side in [-1,1]:
        br('rubber handgrip',(-1.35,side*.19,.835),(-1.44,side*.245,.829),.020,'rubber')
        br('brake lever mount',(-1.32,side*.17,.83),(-1.32,side*.20,.83),.022,'black')
        br('brake lever',(-1.32,side*.185,.83),(-1.39,side*.235,.805),.007)
        for axle in [rear,front]:
            dx = .16 if axle==rear else -.16
            br('caliper brake arm',(axle[0]+dx,side*.045,.280),(axle[0]+dx,side*.030,.259),.006)
            bb('rim brake pad',(axle[0]+dx,side*.018,.259),(.034,.012,.012),'rubber')
    for axle in [rear,front]:
        dx = .16 if axle==rear else -.16
        br('caliper brake bridge',(axle[0]+dx,-.052,.280),(axle[0]+dx,.052,.280),.008)
    path('RC Bicycle front brake cable',[(-1.34,-.18,.816),(-1.15,-.18,.74),
         (-1.20,-.065,.55),(front[0]-.16,-.045,.280)],.003,'black',bike_root)
    path('RC Bicycle rear brake cable',[(-1.34,.18,.816),(-1.48,.08,.75),
         (-1.95,.04,.53),(rear[0]+.16,.045,.280)],.003,'black',bike_root)

    pitch = .0127
    r_big, r_small = pitch/(2*math.sin(math.pi/48)),pitch/(2*math.sin(math.pi/18))
    y_chain = -.091
    gear('48 tooth chainring',(crank[0],y_chain,crank[2]),r_big,48,.065)
    gear('18 tooth rear sprocket',(rear[0],y_chain,0),r_small,18,.012)
    for radius, teeth, yy in [(r_small*.9,16,-.078),(r_small*.8,14,-.067)]:
        gear('rear hub sprocket',(rear[0],yy,0),radius,teeth,.012)
    for i in range(5):
        a = i*math.tau/5
        br('chainring spider',(crank[0],y_chain,crank[2]),
           (crank[0]+.071*math.cos(a),y_chain,crank[2]+.071*math.sin(a)),.007,'RC chain steel')
    br('bottom bracket spindle',(crank[0],-.13,crank[2]),(crank[0],.13,crank[2]),.014)
    for side in [-1,1]:
        endpoint = (crank[0]+side*.105,side*.125,crank[2]-side*.095)
        br('crank arm',(crank[0],side*.125,crank[2]),endpoint,.012,'RC chain steel')
        br('pedal spindle',endpoint,(endpoint[0],side*.21,endpoint[2]),.007)
        pedal = bb('pedal platform',(endpoint[0],side*.19,endpoint[2]),(.10,.08,.025),'black')
        for dx in [-.042,.042]:
            bb('amber pedal reflector',(endpoint[0]+dx,side*.19,endpoint[2]),(.008,.056,.017),'orange')
    chain(rear,crank,r_small,r_big,y_chain)

    # Open tapered wire basket: every wire terminates on a rim, with two independent mounts.
    bottom, top = .54,.80
    corners0 = [(-1.075,-.13,bottom),(-.805,-.13,bottom),(-.805,.13,bottom),(-1.075,.13,bottom)]
    corners1 = [(-1.105,-.175,top),(-.755,-.175,top),(-.755,.175,top),(-1.105,.175,top)]
    for i in range(4):
        a0,b0 = Vector(corners0[i]),Vector(corners0[(i+1)%4])
        a1,b1 = Vector(corners1[i]),Vector(corners1[(i+1)%4])
        br('basket lower rim',a0,b0,.004,'metal')
        br('basket upper rim',a1,b1,.005,'metal')
        for j in range(9):
            br('basket vertical mesh wire',a0.lerp(b0,j/8),a1.lerp(b1,j/8),.0019,'metal')
        for t in [.25,.5,.75]:
            br('basket horizontal mesh wire',a0.lerp(a1,t),b0.lerp(b1,t),.0019,'metal')
    for i in range(10):
        xx = -1.075+i*.030
        br('basket floor wire',(xx,-.13,bottom),(xx,.13,bottom),.002,'metal')
    for side in [-1,1]:
        support = (front[0],side*.064,0)
        rack = (-.94,side*.11,bottom)
        br('basket axle mounted carrier stay',support,rack,.007)
        br('basket supporting platform',(-1.10,side*.11,bottom),(-.78,side*.11,bottom),.007)
        # A collar on the handlebar stem connects to the rear basket wall.
        clamp = (-1.35,side*.035,.725)
        bracket = (-1.075-.030*(.725-bottom)/(top-bottom),side*.12,.725)
        br('basket handlebar fixing bracket',clamp,bracket,.007)
        bb('basket fixing clamp',(clamp[0],clamp[1],clamp[2]),(.045,.016,.04),'metal')
        bb('basket bolted mounting tab',bracket,(.014,.034,.042),'metal')
        joints.append({'joint':'basket carrier to floor','gap_m':abs(rack[2]-bottom)})
    bb('headlamp housing',(-1.05,0,.48),(.09,.075,.065),'black')
    bb('headlamp glass lens',(-.999,0,.48),(.007,.062,.052),'white')
    br('headlamp attached bracket',(-1.16,0,.46),(-1.05,0,.46),.008)
    bb('rear reflector bracket',(-2.30,0,.31),(.050,.024,.047),'metal')
    bb('rear red reflector',(-2.326,0,.31),(.008,.031,.041),'red')

    # Stand is bolted to the chainstay immediately behind the bottom bracket.
    mount = (crank[0]-.08,-.071,crank[2]+.004)
    foot_world = Vector((crank[0]-.22,-2.48,road_z+.008))
    bpy.context.view_layer.update()
    foot = bike_root.matrix_world.inverted() @ foot_world
    bb('kickstand chainstay clamp',mount,(.10,.045,.037),'metal')
    br('kickstand pivot',(mount[0],-.10,mount[2]),(mount[0],-.045,mount[2]),.013)
    path('RC Bicycle deployed side stand',[mount,(mount[0]-.028,mount[1]-.028,mount[2]-.08),
         tuple(foot)],.010,'steel',bike_root)
    obj = box('RC Bicycle kickstand ground shoe',foot_world,(.055,.035,.016),'rubber',.003)
    bpy.context.view_layer.update()
    world = obj.matrix_world.copy()
    obj.parent = bike_root
    obj.matrix_world = world
    # Connected saddle springs, bell and rear rack provide the remaining city-bike hardware.
    bell = bt('bell dome',(-1.31,-.13,.86),.020,.006,'steel',48)
    bell.rotation_euler = (0,0,0)
    br('bell clamp',(-1.31,-.13,.829),(-1.31,-.13,.855),.008,'metal')
    for side in [-1,1]:
        br('rear luggage rack stay',(rear[0],side*.065,0),(-2.22,side*.12,.43),.006)
        br('rear rack upper rail',(-2.48,side*.12,.43),(-2.01,side*.12,.43),.006)
        br('rear rack seatstay mount',(-2.01,side*.12,.43),(-1.95,side*.02,.48),.006)
    for i in range(7):
        br('rear rack crossbar',(-2.45+i*.067,-.12,.43),(-2.45+i*.067,.12,.43),.004)
    bike_root['assembly_note'] = '7 degree lean; two tire contacts and one stand shoe; open basket bolted to stem and front axle carrier'
    bike_root['basket_mounts'] = json.dumps(joints)
    print('Bicycle rebuilt:',bike_root['chain_links'],'chain links; two-sided forks and attached basket')


def emitter(name, vertices, mat):
    center = sum((Vector(p) for p in vertices),Vector())/len(vertices)
    local = [tuple(Vector(p)-center) for p in vertices]
    # Instance a local shape at each fixture, rather than reusing absolute positions.
    obj = g.mesh_object(name,local,[tuple(range(len(vertices)))],mat,center,
                        key=('emitter',tuple(local),mat))
    obj['light_source'] = 'continuous emissive surface, exported as geometry'
    return obj


def vending():
    remove(('Vending machine spill','Interior warm punctual'))
    # Cabinet feet support the cabinet at its new minimum, 43 mm above the road.
    dz = road_z+.043-.03
    prefixes = ('Vending','Can rolled upper rim','Can pull tab','Coin slot','Coin return','Recessed machine fastener')
    if not scene.get('vending_grounded',False):
        for obj in scene.objects:
            if obj.name.startswith(prefixes):
                obj.location.z += dz
        scene['vending_grounded'] = True
    for x in [-3.48,-4.39]:
        for dx in [-.31,.31]:
            for dy in [-.25,.25]:
                box('RC Vending ground foot',(x+dx,1.55+dy,road_z+.0215),(.09,.09,.043),'rubber')
        box('RC Vending display glass',(x,1.066,1.47+dz),(.68,.012,1.28),'glass',0)
        for side in [-1,1]:
            box('RC Vending window side bezel',(x+side*.354,1.122,1.47+dz),(.025,.108,1.33),'metal')
        for z in [.816,2.124]:
            box('RC Vending window header sill',(x,1.122,z+dz),(.72,.108,.025),'metal')
        for side in [-1,1]:
            xx = x+side*.304
            low,high = .882+dz,2.068+dz
            box('RC Vending recessed LED channel',(xx,1.133,(low+high)/2),(.035,.029,high-low+.030),'metal')
            # Front-facing only; a housing blocks back/side emission.
            emitter('RC Vending continuous vertical LED',[(xx-.007,1.116,low),(xx+.007,1.116,low),
                    (xx+.007,1.116,high),(xx-.007,1.116,high)],'RC vending LED diffuser')
        emitter('RC Vending continuous header LED',[(x-.29,1.116,2.079+dz),(x+.29,1.116,2.079+dz),
                (x+.29,1.116,2.091+dz),(x-.29,1.116,2.091+dz)],'RC vending LED diffuser')
        for row in range(4):
            z = .9415+row*.28+dz
            box('RC Vending product tray',(x,1.145,z),(.58,.09,.017),'white')
    for obj in scene.objects:
        if obj.name.startswith('Vending machine display'):
            obj.data = obj.data.copy()
            obj.data.materials.clear()
            obj.data.materials.append(bpy.data.materials['RC vending backing'])
        elif obj.name.startswith('Warm ceiling luminaire'):
            obj.data = obj.data.copy()
            obj.data.materials.clear()
            obj.data.materials.append(bpy.data.materials['metal'])
            x,y,z = obj.location
            emitter('RC Ceiling continuous downward diffuser',[(x-.565,y-.115,z-.024),
                    (x-.565,y+.115,z-.024),(x+.565,y+.115,z-.024),(x+.565,y-.115,z-.024)],
                    'RC ceiling diffuser')
    print('Vending/ceiling strips now emit from continuous geometry; proxy point lights removed')


def validate(write_report=True):
    bpy.context.view_layer.update()
    contacts = []
    prefixes = ('RC Guardrail ground plate','RC Bin grounded rubber foot','RC Bicycle rear tire',
                'RC Bicycle front tire','RC Bicycle kickstand ground shoe','RC Vending ground foot',
                'RC AC grounded mounting foot','RC Umbrella grounded drip tray')
    for obj in scene.objects:
        if any(obj.name==prefix or obj.name.startswith(prefix+'.') for prefix in prefixes):
            gap = bounds(obj)[0].z-road_z
            assert abs(gap)<.0002, f'{obj.name}: ground gap {gap:.6f} m'
            contacts.append({'object':obj.name,'gap_m':round(gap,7)})
    assert len(contacts)==28, f'Incomplete ground supports: {len(contacts)}'
    wall = bounds(scene.objects['Right low wall'])
    shells = [o for o in scene.objects if o.name.startswith('RC Bin side shell')]
    clearance = min(bounds(o)[0].x-wall[1].x for o in shells)
    assert clearance>.20, f'Bins intersect storefront: {clearance}'
    assert not any(o.name.startswith('Rainwater puddle') for o in scene.objects)
    assert not any(o.name.startswith(('Vending machine spill','Interior warm punctual')) for o in scene.objects)
    root = scene.objects['RC Bicycle complete assembly']
    assert root['chain_links']>70
    carriers = [o for o in scene.objects if o.name.startswith('RC Bicycle basket axle mounted carrier stay')]
    platforms = [o for o in scene.objects if o.name.startswith('RC Bicycle basket supporting platform')]
    joint_report = []
    for obj in carriers:
        end = Vector(obj['assembly_b'])
        distances = []
        for rail in platforms:
            a,b = Vector(rail['assembly_a']),Vector(rail['assembly_b'])
            t = max(0,min(1,(end-a).dot(b-a)/(b-a).length_squared))
            distances.append((end-a.lerp(b,t)).length)
        gap = min(distances)
        assert gap<.0001, f'Basket carrier detached: {gap}'
        joint_report.append({'joint':obj.name,'gap_m':round(gap,7)})
    assert len(joint_report)==2
    for obj in scene.objects:
        if obj.name.startswith(('RC Bicycle 48 tooth chainring','RC Bicycle 18 tooth rear sprocket',
                                'RC Bicycle chain roller and rivet')):
            assert abs(obj.location.y-root['chainline_m'])<.00001, 'Misaligned drivetrain'
    led = [o for o in scene.objects if o.name.startswith('RC Vending continuous vertical LED')]
    assert len(led)==4
    strip_centers = []
    for obj in led:
        extent = bounds(obj)[1]-bounds(obj)[0]
        assert extent.z>1.1 and extent.x<.020, f'Invalid strip shape: {obj.name}'
        strip_centers.append(round((bounds(obj)[1].x+bounds(obj)[0].x)/2,3))
    assert sorted(strip_centers)==sorted([-3.784,-3.176,-4.694,-4.086]), 'Overlapping or misplaced LED strips'
    forks = [o for o in scene.objects if o.name.startswith('RC Bicycle curved front fork')]
    assert len(forks)==2 and forks[0].data!=forks[1].data, 'Duplicated fork geometry'
    for obj in forks:
        local_y = [v.co.y for v in obj.data.vertices]
        assert max(local_y)<0 or min(local_y)>0, 'Fork crosses the tire plane'
    ceilings = [o for o in scene.objects if o.name.startswith('RC Ceiling continuous downward diffuser')]
    assert len({round(o.location.x,2) for o in ceilings})==3, 'Overlapping ceiling emitters'
    report = {'revision':REVISION,'ground_surface_m':round(road_z,6),'contact_tolerance_m':.0002,
              'contacts':contacts,'bin_wall_clearance_m':round(clearance,5),
              'chain_links':int(root['chain_links']),'chain_pitch_m':float(root['chain_pitch_m']),
              'chainline_m':float(root['chainline_m']),'basket_mounts':joint_report,
              'puddles_removed':True,'vending_point_lights':0,'vertical_emissive_strips':len(led),
              'embedded_punctual_lights':sum(o.type=='LIGHT' for o in scene.objects)}
    scene['assembly_revision'] = REVISION
    scene['assembly_validation'] = json.dumps(report,separators=(',',':'))
    if write_report:
        (ROOT/'model-audit.json').write_text(json.dumps(report,indent=2,ensure_ascii=False),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k not in {'contacts','basket_mounts'}},ensure_ascii=False))
    return report


def apply():
    if scene.get('assembly_revision')==REVISION:
        return
    begin()
    street()
    bicycle()
    vending()
    validate()


def export():
    validate()
    g.export()


if __name__=='__main__':
    apply()
    export()
