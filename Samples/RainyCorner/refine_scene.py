"""Detail pass for the authored Rainy Corner scene; execute via Blender MCP after build_scene.py.
Adds shaped product containers, seams, mechanisms and street hardware using shared meshes.
"""
import bpy
import math
import json
import struct
import contextlib
import io
from pathlib import Path
from mathutils import Vector

ROOT = Path(__file__).resolve().parent
scene = bpy.context.scene
detail = None
cache = {}

def start():
    global detail
    assert scene.name == 'Rainy Corner - Koyomi Mart'
    if scene.get('detail_revision', 0):
        raise RuntimeError('Detail pass already applied; use the base scene to rebuild')
    detail = bpy.data.collections.new('07 - Machined hardware and packaged products')
    scene.collection.children.link(detail)

def mesh_object(name, vertices, faces, mat, pos=(0,0,0), key=None):
    key = key or name
    if key not in cache:
        mesh = bpy.data.meshes.new(name)
        mesh.from_pydata(vertices, [], faces); mesh.update()
        mesh.materials.append(bpy.data.materials[mat]); cache[key] = mesh
    obj = bpy.data.objects.new(name, cache[key]); detail.objects.link(obj); obj.location=pos
    return obj

def lathe_mesh(name, profile, mat, segments=96, flutes=0):
    vertices=[]; faces=[]
    for z,radius in profile:
        for j in range(segments):
            angle=j*math.tau/segments
            r=radius*(1+flutes*math.cos(angle*48))
            vertices.append((r*math.cos(angle),r*math.sin(angle),z))
    for k in range(len(profile)-1):
        for j in range(segments):
            a=k*segments+j;b=k*segments+(j+1)%segments
            faces.append((a,b,b+segments,a+segments))
    faces.append(tuple(reversed(range(segments))))
    faces.append(tuple((len(profile)-1)*segments+j for j in range(segments)))
    mesh=bpy.data.meshes.new(name);mesh.from_pydata(vertices,[],faces);mesh.materials.append(bpy.data.materials[mat])
    for p in mesh.polygons:p.use_smooth=len(p.vertices)==4
    return mesh

def box(name,pos,size,mat='steel',bevel=.002):
    key=('box',tuple(size),mat,bevel)
    if key not in cache:
        bpy.ops.mesh.primitive_cube_add(size=1)
        obj=bpy.context.object;obj.dimensions=size;bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
        obj.data.materials.append(bpy.data.materials[mat])
        if bevel:
            mod=obj.modifiers.new('Rounded manufactured edges','BEVEL');mod.width=min(bevel,min(size)*.2);mod.segments=4
            bpy.ops.object.modifier_apply(modifier=mod.name)
        for c in list(obj.users_collection):c.objects.unlink(obj)
        detail.objects.link(obj);cache[key]=obj.data
    else:obj=bpy.data.objects.new(name,cache[key]);detail.objects.link(obj)
    obj.name=name;obj.location=pos;return obj

def rod(name,a,b,radius,mat='steel',segments=24):
    delta=Vector(b)-Vector(a);key=('rod',round(delta.length,6),radius,mat,segments)
    if key not in cache:
        cache[key]=lathe_mesh(name,[(-delta.length/2,radius),(delta.length/2,radius)],mat,segments)
    obj=bpy.data.objects.new(name,cache[key]);detail.objects.link(obj)
    obj.location=(Vector(a)+Vector(b))*.5;obj.rotation_euler=delta.to_track_quat('Z','Y').to_euler();return obj

def torus(name,pos,major,minor,mat='steel',rotate=(0,0,0),segments=96,rings=12):
    key=('torus',major,minor,mat,segments,rings)
    if key not in cache:
        vertices=[];faces=[]
        for i in range(segments):
            a=i*math.tau/segments
            for j in range(rings):
                b=j*math.tau/rings;r=major+minor*math.cos(b)
                vertices.append((r*math.cos(a),r*math.sin(a),minor*math.sin(b)))
        for i in range(segments):
            for j in range(rings):faces.append((i*rings+j,((i+1)%segments)*rings+j,((i+1)%segments)*rings+(j+1)%rings,i*rings+(j+1)%rings))
        obj=mesh_object(name,vertices,faces,mat,pos,key)
        for p in obj.data.polygons:p.use_smooth=True
    else:obj=bpy.data.objects.new(name,cache[key]);detail.objects.link(obj);obj.location=pos
    obj.rotation_euler=rotate;return obj

def products():
    # PET bottles: rounded foot, formed ribs, shoulder, neck and crimped cap.
    bottles=[o for o in scene.objects if o.name.startswith('Drink bottle')]
    profiles=[]
    for i in range(49):
        t=i/48;z=-.095+.19*t
        if t<.06:r=.043+(.055-.043)*math.sin(t/.06*math.pi/2)
        elif t<.67:r=.055-(.0016*(.5+.5*math.cos(t*math.tau*18)) if t<.27 or t>.53 else 0)
        elif t<.88:r=.026+(.055-.026)*(.5+.5*math.cos((t-.67)/.21*math.pi))
        else:r=.026+.0012*math.cos(t*math.tau*38)
        profiles.append((z,r))
    for obj in bottles:
        mat=obj.data.materials[0].name;key=('pet',mat)
        if key not in cache:cache[key]=lathe_mesh('Sculpted PET bottle - ribs shoulder neck',profiles,mat)
        obj.data=cache[key]
        x,y,z=obj.location
        torus('PET bottle base seam',(x,y,z-.081),.052,.0015,mat,segments=64,rings=8)
        box('Printed bottle label',(x,y-.0555,z-.012),(.067,.0015,.057),'paper',.0004)
        for n in range(5):box('Bottle barcode ink',(x-.022+n*.009,y-.0565,z-.012),(.003,.0008,.027),'black',0)
    cap_mesh=lathe_mesh('Fluted screw cap',[(z,.033) for z in [-.0125,-.011,-.009,-.006,-.003,0,.003,.006,.009,.011,.0125]],'white',96,.035)
    for obj in [o for o in scene.objects if o.name.startswith('Bottle cap')]:obj.data=cap_mesh
    for obj in [o for o in scene.objects if o.name.startswith('Vending drink')]:
        mat=obj.data.materials[0].name;key=('can',mat)
        if key not in cache:
            profile=[(-.08,.034),(-.078,.041),(-.074,.042),(-.07,.04)]
            profile += [(-.068+i*.005,.042+math.sin(i*.4)*.0002) for i in range(27)]
            profile += [(.067,.041),(.071,.038),(.075,.039),(.078,.041),(.08,.041)]
            cache[key]=lathe_mesh('Rolled aluminum beverage can',profile,mat)
        obj.data=cache[key];x,y,z=obj.location
        torus('Can rolled upper rim',(x,y,z+.078),.039,.002,segments=64,rings=8)
        tab=torus('Can pull tab',(x,y,z+.083),.012,.0025,segments=48,rings=8);tab.scale.y=.57
    # Folded package lids, sealed edges, barcode strips and colored brand panels.
    for index,obj in enumerate([o for o in scene.objects if o.name.startswith('Snack carton')]):
        x,y,z=obj.location;mat=obj.data.materials[0].name
        box('Carton folded top',(x,y,z+.103),(.195,.173,.009),mat,.002)
        box('Carton lid overlap seam',(x,y,z+.109),(.012,.168,.004),'paper',.0005)
        box('Package side brand block',(x+.102,y,z+.023),(.003,.115,.075),['red','cyan','yellow'][index%3],.0004)
        for j in range(5):box('Package barcode',(x+.104,y-.04+j*.018,z-.045),(.001,.006,.022),'black',0)
    print('Products detailed:',len(bottles),'bottles; objects',len(scene.objects))

def fixtures():
    for shelf in [o for o in scene.objects if o.name.startswith('Gondola shelf')]:
        x,y,z=shelf.location
        for side in [-1,1]:
            box('Shelf rolled edge',(x+side*.557,y,z+.02),(.016,2.2,.035),'steel')
            box('Shelf price rail',(x+side*.569,y,z),(.012,2.18,.048),'white')
            for j in range(7):
                yy=y-.87+j*.29
                box('Shelf price ticket',(x+side*.577,yy,z+.002),(.003,.10,.028),'yellow',.0003)
        for j in range(28):rod('Shelf wire deck',(x-.53,y-1.06+j*.078,z+.029),(x+.53,y-1.06+j*.078,z+.029),.0035)
    for refrigerator in [o for o in scene.objects if o.name.startswith('Drinks refrigerator')]:
        x,y,z=refrigerator.location
        for j in range(21):box('Refrigerator compressor louver',(x-.50+j*.05,2.805,.40),(.019,.019,.20),'metal',.001)
        for xx in [x-.53,x+.53]:
            box('Fridge rubber door seal',(xx,2.448,1.50),(.018,.020,2.10),'rubber')
            for zz in [.54,2.43]:box('Refrigerator hinge',(xx,2.425,zz),(.033,.027,.10),'steel')
        rod('Brushed fridge handle',(x+.42,2.402,1.02),(x+.42,2.402,1.85),.013)
    for machine in [o for o in scene.objects if o.name.startswith('Vending machine body')]:
        x,y,z=machine.location;front=y-.39
        for dx in [-.39,.39]:box('Vending door gasket',(x+dx,front,z),(.014,.013,2.08),'rubber')
        box('Vending payment panel',(x+.19,front-.014,.72),(.26,.025,.30),'metal')
        for row in range(3):
            for col in range(3):box('Vending keypad key',(x+.115+col*.066,front-.03,.79-row*.06),(.04,.01,.036),'white',.006)
        box('Coin slot bevel',(x-.13,front-.018,.79),(.18,.014,.06),'steel')
        box('Coin slot opening',(x-.13,front-.028,.79),(.125,.008,.010),'black',0)
        torus('Coin return button',(x-.13,front-.032,.64),.03,.004,'steel',(math.pi/2,0,0),64,12)
        for row in range(7):box('Vending lower air grille',(x,front-.008,.22+row*.024),(.64,.012,.009),'metal')
        for dx in [-.32,.32]:
            for zz in [.12,2.17]:torus('Recessed machine fastener',(x+dx,front-.008,zz),.009,.002,'steel',(math.pi/2,0,0),32,8)
    # Door track, sensor lens and floor mat weave.
    for z in [.255,2.525]:
        for yy in [-1.13,-1.08,-1.03]:rod('Sliding door guide rail',(.87,yy,z),(2.37,yy,z),.011)
    for x in [1.36,1.9]:
        for z in [1.255,1.545]:torus('Door handle mount',(x,-1.172,z),.019,.005,'steel',(math.pi/2,0,0),48,12)
    for i in range(40):box('Entrance mat rib',(.95+i*.035,-1.51,.28),(.013,.43,.012),'rubber',.002)
    for i in range(7):box('Coffee drip tray channel',(2.41+i*.055,-.038,1.458),(.022,.20,.008),'steel',.001)
    for x in [2.47,2.69]:rod('Coffee nozzle',(x,-.029,1.71),(x,-.029,1.64),.012)
    for i in range(10):box('Register keyboard key',(2.34+(i%5)*.055,-.55+(i//5)*.047,1.507),(.041,.033,.01),'black',.003)
    print('Store hardware detailed; objects',len(scene.objects))

def street():
    for wheel in [o for o in scene.objects if o.name.startswith('Bicycle tire')]:
        x,y,z=wheel.location
        torus('Bicycle machined rim',(x,y,z),.304,.012,'steel',(math.pi/2,0,0),128,16)
        for i in range(32):
            a=i*math.tau/32
            rod('Cross-laced bicycle spoke',(x+.045*math.cos(a+.6),y+(.028 if i%2 else -.028),z+.045*math.sin(a+.6)),
                (x+.301*math.cos(a),y,z+.301*math.sin(a)),.0025,'steel',12)
        rod('Bicycle axle',(x,y-.065,z),(x,y+.065,z),.023)
        torus('Bicycle hub flange',(x,y-.025,z),.042,.005,'steel',(math.pi/2,0,0),64,12)
    for radius in [.063,.077,.091]:torus('Bicycle rear sprocket',(-2.01,-1.966,.56),radius,.003,'steel',(math.pi/2,0,0),96,8)
    # Condenser concentric protection rings, fan blades, folded side fins and service screw heads.
    for radius in [.07,.12,.17,.22,.27,.30]:torus('AC fan concentric safety grille',(4.433,2.18,.59),radius,.004,'steel',(0,math.pi/2,0),128,10)
    for i in range(24):
        a=i*math.tau/24
        rod('AC fan radial grille',(4.434,2.18+.045*math.cos(a),.59+.045*math.sin(a)),(4.434,2.18+.305*math.cos(a),.59+.305*math.sin(a)),.003,'steel',12)
    for i in range(27):box('Condenser folded cooling fin',(4.02,1.619,.25+i*.024),(.51,.012,.009),'steel',.001)
    for i in range(4):
        a=i*math.pi/2
        blade=box('Condenser curved fan blade',(4.418,2.18+.15*math.cos(a),.59+.15*math.sin(a)),(.018,.10,.26),'black',.009);blade.rotation_euler.x=-a+.35
    for x,y in [(-4.47,3.42),(4.18,3.5)]:
        for z in [1.0,2.0,3.0]:torus('Utility pole metal band',(x,y,z),.088,.009,'steel',segments=96,rings=12)
        for dx in [-.32,.32]:
            for z in [4.24,4.28,4.32,4.36]:torus('Ceramic insulator shed',(x+dx,y,z),.062,.012,'white',segments=64,rings=12)
    for x in [.1,1.2,2.3,3.4]:
        box('Guardrail bolted foot',(x,-4.43,.073),(.20,.20,.032),'steel',.009)
        for dx in [-.063,.063]:
            for dy in [-.063,.063]:rod('Guardrail anchor bolt',(x+dx,-4.43+dy,.088),(x+dx,-4.43+dy,.11),.012,'metal',6)
    for i in range(25):box('Drain grating cross bar',(-2.76+i*.22,-1.825,.155),(.016,.25,.023),'metal',.002)
    print('Street hardware detailed; objects',len(scene.objects))

def configure_glass():
    mat=bpy.data.materials['glass'];bsdf=next(n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    bsdf.inputs['Alpha'].default_value=1;bsdf.inputs['Transmission Weight'].default_value=1;bsdf.inputs['IOR'].default_value=1.5
    bsdf.inputs['Roughness'].default_value=.06;bsdf.inputs['Metallic'].default_value=0;bsdf.inputs['Base Color'].default_value=(.97,.99,1,1)
    mat.diffuse_color=(.97,.99,1,1)
    absorption=next((n for n in mat.node_tree.nodes if n.type=='VOLUME_ABSORPTION'),None)
    if absorption is None:absorption=mat.node_tree.nodes.new('ShaderNodeVolumeAbsorption')
    absorption.inputs['Color'].default_value=(.82,.94,1,1);absorption.inputs['Density'].default_value=.1
    output=next(n for n in mat.node_tree.nodes if n.type=='OUTPUT_MATERIAL')
    mat.node_tree.links.new(absorption.outputs[0],output.inputs['Volume'])
    scene['purpose']='PBR, embedded lights, emissive geometry, refractive volume glass and wet-ground validation'
    scene['glass']='KHR transmission/IOR/volume dielectric; closed geometry provides true thickness'

def export():
    configure_glass()
    scene['detail_revision']=1
    # Apply the physical assembly pass on both new builds and subsequent exports.
    import importlib.util
    spec=importlib.util.spec_from_file_location('rainy_assembly',ROOT/'repair_scene.py')
    assembly=importlib.util.module_from_spec(spec);spec.loader.exec_module(assembly)
    assembly.apply()
    depsgraph=bpy.context.evaluated_depsgraph_get();count=0
    for obj in scene.objects:
        if obj.type not in {'MESH','CURVE','FONT'}:continue
        evaluated=obj.evaluated_get(depsgraph);mesh=evaluated.to_mesh();mesh.calc_loop_triangles();count+=len(mesh.loop_triangles);evaluated.to_mesh_clear()
    scene['rendered_triangles']=count
    bpy.ops.wm.save_as_mainfile(filepath=str(ROOT/'RainyCorner.blend'))
    with contextlib.redirect_stdout(io.StringIO()):
        bpy.ops.export_scene.gltf(filepath=str(ROOT/'RainyCorner.glb'),export_format='GLB',use_active_scene=True,
            export_lights=True,export_import_convert_lighting_mode='RAW',export_apply=True,export_animations=False)
    preserve_glass_volume(ROOT/'RainyCorner.glb')
    print('Exported',count,'triangles;',len(scene.objects),'objects; GLB bytes',(ROOT/'RainyCorner.glb').stat().st_size)
    return count

def preserve_glass_volume(path):
    # Blender may prune its unconnected glTF thickness settings group. Preserve the authored
    # volume in JSON without changing the exporter's buffers, nodes or shared mesh instances.
    data=path.read_bytes();length=struct.unpack_from('<I',data,12)[0]
    document=json.loads(data[20:20+length]);used=document.setdefault('extensionsUsed',[])
    for material in document['materials']:
        if material.get('name')!='glass':continue
        material.pop('alphaMode',None);material['doubleSided']=True
        ext=material.setdefault('extensions',{})
        ext['KHR_materials_transmission']={'transmissionFactor':1}
        ext['KHR_materials_ior']={'ior':1.5}
        ext['KHR_materials_volume']={'thicknessFactor':.025,'attenuationColor':[.82,.94,1],'attenuationDistance':10}
        for name in ext:
            if name not in used:used.append(name)
    payload=json.dumps(document,ensure_ascii=False,separators=(',',':')).encode();payload+=b' '*((-len(payload))%4)
    tail=data[20+length:];path.write_bytes(struct.pack('<III',0x46546C67,2,20+len(payload)+len(tail))+struct.pack('<II',len(payload),0x4E4F534A)+payload+tail)

if __name__=='__main__':
    start();products();fixtures();street();export()
