"""Acceptance test: import a .m3 through the m3studio Blender add-on, headless.

    blender --background --factory-startup --python m3accept.py -- model.m3

m3studio's loader enforces every expected-value in structures.xml while parsing, so a clean
import means the file is field-level conformant with the format SC2 accepts. Requires the
m3studio add-on (folder `m3studio-main`) in the user's Blender addons directory; a patched
copy is staged to a temp dir because the original both has an invalid module name (dash) and
creates GPU shaders at import time, which background mode cannot do.
"""
import bpy, sys, json, os, shutil, tempfile, traceback

m3path = sys.argv[sys.argv.index("--") + 1]

addon_src = None
for scripts_dir in bpy.utils.script_paths():
    cand = os.path.join(scripts_dir, "addons", "m3studio-main")
    if os.path.isdir(cand):
        addon_src = cand
        break
if addon_src is None:
    roaming = os.path.expandvars(r"%APPDATA%\Blender Foundation\Blender")
    for ver in sorted(os.listdir(roaming), reverse=True) if os.path.isdir(roaming) else []:
        cand = os.path.join(roaming, ver, "scripts", "addons", "m3studio-main")
        if os.path.isdir(cand):
            addon_src = cand
            break
if addon_src is None:
    print("REGISTER_FAILED: m3studio-main not found in any Blender addons directory")
    sys.exit(3)

stage = os.path.join(tempfile.gettempdir(), "m3verify_addon")
dst = os.path.join(stage, "m3studio")
shutil.rmtree(stage, ignore_errors=True)
shutil.copytree(addon_src, dst, ignore=shutil.ignore_patterns("__pycache__", ".git*"))

# headless patches: no GPU in background mode
for rel, old, new in [
    ("bl_graphics_draw.py",
     "uni_polyline_shader = gpu.shader.from_builtin(POLYLINE_SHADER_ID)",
     "uni_polyline_shader = gpu.shader.from_builtin(POLYLINE_SHADER_ID) if not bpy.app.background else None"),
    ("__init__.py",
     "    M3_SHADER = bpy.types.SpaceView3D.draw_handler_add(bl_graphics_draw.draw, (), 'WINDOW', 'POST_VIEW')",
     "    if not bpy.app.background:\n        M3_SHADER = bpy.types.SpaceView3D.draw_handler_add(bl_graphics_draw.draw, (), 'WINDOW', 'POST_VIEW')"),
]:
    p = os.path.join(dst, rel)
    with open(p, encoding="utf-8") as f:
        text = f.read()
    if old not in text and new.splitlines()[0] not in text:
        print(f"REGISTER_FAILED: patch anchor missing in {rel} (add-on version changed?)")
        sys.exit(3)
    with open(p, "w", encoding="utf-8") as f:
        f.write(text.replace(old, new))

bpy.ops.wm.read_factory_settings(use_empty=True)
sys.path.insert(0, stage)
try:
    import m3studio
    m3studio.register()
except Exception as e:
    print("REGISTER_FAILED:", e)
    traceback.print_exc()
    sys.exit(3)

try:
    getattr(bpy.ops.m3, "import")(filepath=m3path)
except Exception as e:
    print("IMPORT_FAILED:", e)
    traceback.print_exc()
    sys.exit(2)

report = {"objects": [], "meshes": [], "actions": len(bpy.data.actions)}
for o in bpy.data.objects:
    entry = {"name": o.name, "type": o.type}
    if o.type == "ARMATURE":
        entry["bones"] = len(o.data.bones)
        try:
            entry["anim_groups"] = len(o.m3_animation_groups)
            entry["m3_materials"] = len(o.m3_materialrefs)
        except Exception:
            pass
    report["objects"].append(entry)
    if o.type == "MESH":
        m = o.data
        report["meshes"].append({
            "name": o.name, "verts": len(m.vertices), "polys": len(m.polygons),
            "materials": len(m.materials), "vertex_groups": len(o.vertex_groups),
            "uvs": len(m.uv_layers),
        })
report["actions"] = len(bpy.data.actions)

print("M3_ACCEPT_BEGIN")
print(json.dumps(report, indent=1))
print("M3_ACCEPT_END")
sys.exit(0)
