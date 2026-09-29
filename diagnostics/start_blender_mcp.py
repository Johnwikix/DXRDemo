"""Start the installed MCP add-on in a dedicated Blender session without changing saved preferences."""
import addon_utils
import bpy

if "blender_mcp" not in bpy.context.preferences.addons:
    addon_utils.enable("blender_mcp", default_set=False, persistent=False)

def connect():
    if not getattr(bpy.types, "blendermcp_server", None):
        bpy.ops.blendermcp.start_server()
    return None

bpy.app.timers.register(connect, first_interval=1)
