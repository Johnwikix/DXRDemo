"""Run the installed Blender MCP in a dedicated, unsaved Blender process."""
import importlib.util
import sys
import bpy

SOURCE = r'D:\code\node\RocketLanuchTest\artifacts\scenery-blend\pad.blend'
ADDON = r'C:\Users\90684\AppData\Roaming\Blender Foundation\Blender\5.2\scripts\addons\blender_mcp.py'
bpy.ops.wm.open_mainfile(filepath=SOURCE)
spec = importlib.util.spec_from_file_location('blender_mcp', ADDON)
addon = importlib.util.module_from_spec(spec)
sys.modules['blender_mcp'] = addon
spec.loader.exec_module(addon)
addon.register()
for scene in bpy.data.scenes:
    scene.blendermcp_port = 9877
server = addon.BlenderMCPServer(host='127.0.0.1', port=9877)
bpy.types.blendermcp_server = server
server.start()
print('ISOLATED_HAINAN_MCP_READY 9877', flush=True)
