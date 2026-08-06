"""Headless render of an imported .m3, each mesh (region) a distinct flat colour, from several
angles — ground truth for "is this geoset actually there and where".

    blender --background --factory-startup --python m3render.py -- model.m3 outdir

Reuses m3accept's staged, headless-patched m3studio to import, then renders with the Workbench
engine (flat, per-object colour) so geometry is unambiguous without textures or lighting.
"""
import bpy, sys, os, shutil, tempfile, traceback, math

argv = sys.argv[sys.argv.index("--") + 1:]
m3path = argv[0]
outdir = argv[1] if len(argv) > 1 else os.path.dirname(m3path)
os.makedirs(outdir, exist_ok=True)

# ---- stage + register m3studio exactly as m3accept does ----
addon_src = None
for scripts_dir in bpy.utils.script_paths():
    cand = os.path.join(scripts_dir, "addons", "m3studio-main")
    if os.path.isdir(cand):
        addon_src = cand; break
if addon_src is None:
    roaming = os.path.expandvars(r"%APPDATA%\Blender Foundation\Blender")
    for ver in sorted(os.listdir(roaming), reverse=True) if os.path.isdir(roaming) else []:
        cand = os.path.join(roaming, ver, "scripts", "addons", "m3studio-main")
        if os.path.isdir(cand):
            addon_src = cand; break
stage = os.path.join(tempfile.gettempdir(), "m3verify_addon")
dst = os.path.join(stage, "m3studio")
shutil.rmtree(stage, ignore_errors=True)
shutil.copytree(addon_src, dst, ignore=shutil.ignore_patterns("__pycache__", ".git*"))
for rel, old, new in [
    ("bl_graphics_draw.py",
     "uni_polyline_shader = gpu.shader.from_builtin(POLYLINE_SHADER_ID)",
     "uni_polyline_shader = gpu.shader.from_builtin(POLYLINE_SHADER_ID) if not bpy.app.background else None"),
    ("__init__.py",
     "    M3_SHADER = bpy.types.SpaceView3D.draw_handler_add(bl_graphics_draw.draw, (), 'WINDOW', 'POST_VIEW')",
     "    if not bpy.app.background:\n        M3_SHADER = bpy.types.SpaceView3D.draw_handler_add(bl_graphics_draw.draw, (), 'WINDOW', 'POST_VIEW')"),
]:
    p = os.path.join(dst, rel)
    text = open(p, encoding="utf-8").read()
    open(p, "w", encoding="utf-8").write(text.replace(old, new))

bpy.ops.wm.read_factory_settings(use_empty=True)
sys.path.insert(0, stage)
import m3studio
m3studio.register()
getattr(bpy.ops.m3, "import")(filepath=m3path)

# ---- colour each mesh distinctly ----
meshes = [o for o in bpy.data.objects if o.type == "MESH"]
palette = [(0.85,0.2,0.2,1),(0.2,0.7,0.2,1),(0.2,0.4,0.9,1),(0.9,0.8,0.2,1),(0.8,0.3,0.85,1),
           (0.25,0.8,0.85,1),(0.95,0.55,0.15,1),(0.6,0.85,0.3,1),(0.55,0.3,0.15,1),(0.9,0.6,0.75,1),
           (0.5,0.5,0.55,1),(0.15,0.55,0.4,1),(0.75,0.2,0.4,1),(0.4,0.45,0.85,1),(0.7,0.7,0.2,1),(0.3,0.75,0.6,1)]
co_all = []
for i, o in enumerate(meshes):
    o.color = palette[i % len(palette)]
    mw = o.matrix_world
    for v in o.data.vertices:
        co_all.append(mw @ v.co)

mn = [min(c[i] for c in co_all) for i in range(3)]
mx = [max(c[i] for c in co_all) for i in range(3)]
ctr = [(mn[i] + mx[i]) / 2 for i in range(3)]
size = max(mx[i] - mn[i] for i in range(3))
print(f"bbox min={['%.1f'%v for v in mn]} max={['%.1f'%v for v in mx]} size={size:.1f}  meshes={len(meshes)}")

scene = bpy.context.scene
scene.render.engine = "BLENDER_WORKBENCH"
scene.display.shading.light = "FLAT"
scene.display.shading.color_type = "OBJECT"
# SC2 backface-culls single-sided materials; mirror that so a reversed-winding region shows as
# a see-through hole here the way it would in the game. Toggle via env for an A/B.
scene.display.shading.show_backface_culling = os.environ.get("CULL", "0") == "1"
scene.render.film_transparent = False
scene.render.resolution_x = 480
scene.render.resolution_y = 560
if scene.world is None:
    scene.world = bpy.data.worlds.new("W")
scene.world.color = (0.05, 0.05, 0.06)

cam_data = bpy.data.cameras.new("Cam")
cam_data.type = "ORTHO"
cam_data.ortho_scale = size * 1.15
cam = bpy.data.objects.new("Cam", cam_data)
scene.collection.objects.link(cam)
scene.camera = cam


def look_at(obj, target, eye):
    import mathutils
    d = mathutils.Vector((target[0]-eye[0], target[1]-eye[1], target[2]-eye[2]))
    obj.location = eye
    obj.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()


# Z-up model. Views: front(-Y), back(+Y), 3/4, and a head close-up.
R = size * 2.5
views = {
    "posX": (ctr[0] + R, ctr[1], ctr[2]),
    "negX": (ctr[0] - R, ctr[1], ctr[2]),
    "posY": (ctr[0], ctr[1] + R, ctr[2]),
    "negY": (ctr[0], ctr[1] - R, ctr[2]),
}
head_z = mx[2] - (mx[2]-mn[2]) * 0.20
for name, eye in views.items():
    look_at(cam, ctr, eye)
    cam_data.ortho_scale = size * 1.15
    scene.render.filepath = os.path.join(outdir, f"render_{name}.png")
    bpy.ops.render.render(write_still=True)
    print("wrote", scene.render.filepath)

# head close-ups down each horizontal axis so one is face-on regardless of orientation
for name, eye in {"head_posX": (ctr[0]+R, ctr[1], head_z), "head_negX": (ctr[0]-R, ctr[1], head_z),
                  "head_posY": (ctr[0], ctr[1]+R, head_z), "head_negY": (ctr[0], ctr[1]-R, head_z)}.items():
    look_at(cam, (ctr[0], ctr[1], head_z), eye)
    cam_data.ortho_scale = size * 0.42
    scene.render.filepath = os.path.join(outdir, f"render_{name}.png")
    bpy.ops.render.render(write_still=True)
    print("wrote", scene.render.filepath)
print("RENDER_DONE")
