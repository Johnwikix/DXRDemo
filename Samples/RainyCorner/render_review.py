"""Render the corrected source and geometry close-ups using the installed Blender.

blender --background RainyCorner.blend --python render_review.py
The full preview uses scene lighting. Close-ups add temporary neutral inspection
lighting in this render process only; it is never saved or exported to GLB.
"""
import bpy
import sys
from pathlib import Path
from mathutils import Vector

ROOT = Path(__file__).resolve().parent
scene = bpy.context.scene
prefs = bpy.context.preferences.addons['cycles'].preferences
device_types = prefs.get_device_types(bpy.context)
backend = next((x[0] for x in device_types if x[0]=='OPTIX'),
               next((x[0] for x in device_types if x[0]=='CUDA'),None))
if backend:
    prefs.compute_device_type = backend
    prefs.refresh_devices()
    for device in prefs.devices:
        device.use = device.type==backend
    if any(device.use for device in prefs.devices):
        scene.cycles.device = next(i.identifier for i in scene.cycles.bl_rna.properties['device'].enum_items if i.identifier=='GPU')
scene.cycles.samples = 64
scene.cycles.use_denoising = True
scene.render.resolution_percentage = 100
scene.render.resolution_x = 1440
scene.render.resolution_y = 1200
only = sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else []
scene.render.filepath = str(ROOT/'RainyCorner-preview.png')
if not only or 'full' in only:
    bpy.ops.render.render(write_still=True)

# Review images expose contact points, drivetrain, basket mounts and full strip shape.
camera_data = bpy.data.cameras.new('Temporary review camera')
camera_data.type = next(i.identifier for i in camera_data.bl_rna.properties['type'].enum_items if i.identifier=='ORTHO')
camera = bpy.data.objects.new('Temporary review camera',camera_data)
scene.collection.objects.link(camera)
scene.camera = camera
light_kinds = [i.identifier for i in bpy.data.lights.bl_rna.functions['new'].parameters['type'].enum_items]
area_kind = next(k for k in light_kinds if k=='AREA')
light_data = bpy.data.lights.new('Temporary neutral inspection softbox',area_kind)
light_data.energy = 100
light_data.size = 3
light_data.color = (1,1,1)
fill = bpy.data.objects.new('Temporary neutral inspection softbox',light_data)
scene.collection.objects.link(fill)
scene.render.resolution_x = 1400
scene.render.resolution_y = 1000
scene.cycles.samples = 32
views = [
    ('RainyCorner-bicycle.png',(-1.45,-6.5,1.8),(-1.62,-2.19,.63),2.15,(-1.6,-4,3),80),
    ('RainyCorner-basket.png',(.4,-4.8,2.1),(-1.12,-2.2,1.03),1.16,(-1,-3.8,3),55),
    ('RainyCorner-vending.png',(-3.93,-.85,1.68),(-3.93,1.13,1.19),3.0,(-4,-1.5,3.4),20),
    ('RainyCorner-contacts.png',(7,-4.4,2.5),(3.9,-.5,.50),2.05,(5,-2.2,3),80),
]
for filename,eye,target,scale,light_pos,energy in views:
    if only and filename.removeprefix('RainyCorner-').removesuffix('.png') not in only:
        continue
    camera.location = eye
    camera.rotation_euler = (Vector(target)-camera.location).to_track_quat('-Z','Y').to_euler()
    camera_data.ortho_scale = scale
    fill.location = light_pos
    fill.rotation_euler = (Vector(target)-fill.location).to_track_quat('-Z','Y').to_euler()
    light_data.energy = energy
    scene.render.filepath = str(ROOT/filename)
    bpy.ops.render.render(write_still=True)
    print('REVIEW SAVED',filename,flush=True)
