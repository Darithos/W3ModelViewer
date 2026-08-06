"""Headless bridge: (viewer .m3) -> m3studio import -> m3studio export -> final .m3.

Run:  blender --background --factory-startup --python blender_m3studio_export.py -- src.m3 dst.m3

The viewer's own writer produces a .m3 that m3studio imports losslessly (bones, skinning, baked
animations, materials with Assets\\Textures\\ paths, attachments, cameras, GEOA visibility).
m3studio then serialises the final file — the same writer that produced every .m3 known to load
in the SC2 editor. Afterwards the per-sequence bounds and movement speeds, which m3studio does
not round-trip, are copied back from the source file with m3studio's own io_m3 library.

The add-on is staged to a temp folder because the installed folder name (m3studio-main) is not a
valid Python module name, and two GPU calls must be guarded to run in background mode.
"""
import bpy
import os
import shutil
import sys
import tempfile
import traceback

argv = sys.argv[sys.argv.index("--") + 1:]
SRC, DST = argv[0], argv[1]


def fail(stage, err):
    print(f"M3PIPELINE_FAILED at {stage}: {err}")
    traceback.print_exc()
    sys.exit(2)


# ---- locate the installed m3studio add-on --------------------------------------------------
addon_src = None
for scripts_dir in bpy.utils.script_paths():
    cand = os.path.join(scripts_dir, "addons", "m3studio-main")
    if os.path.isdir(cand):
        addon_src = cand
        break
if addon_src is None:
    roaming = os.path.expandvars(r"%APPDATA%\Blender Foundation\Blender")
    if os.path.isdir(roaming):
        for ver in sorted(os.listdir(roaming), reverse=True):
            cand = os.path.join(roaming, ver, "scripts", "addons", "m3studio-main")
            if os.path.isdir(cand):
                addon_src = cand
                break
if addon_src is None:
    print("M3PIPELINE_FAILED at locate: m3studio-main not installed in any Blender addons folder")
    sys.exit(3)

# ---- stage a headless-safe copy under a valid module name ----------------------------------
stage = os.path.join(tempfile.gettempdir(), "wc3viewer_m3studio")
dst_mod = os.path.join(stage, "m3studio")
try:
    shutil.rmtree(stage, ignore_errors=True)
    shutil.copytree(addon_src, dst_mod, ignore=shutil.ignore_patterns("__pycache__", ".git*"))
    for rel, old, new in [
        ("bl_graphics_draw.py",
         "uni_polyline_shader = gpu.shader.from_builtin(POLYLINE_SHADER_ID)",
         "uni_polyline_shader = gpu.shader.from_builtin(POLYLINE_SHADER_ID) if not bpy.app.background else None"),
        ("__init__.py",
         "    M3_SHADER = bpy.types.SpaceView3D.draw_handler_add(bl_graphics_draw.draw, (), 'WINDOW', 'POST_VIEW')",
         "    if not bpy.app.background:\n        M3_SHADER = bpy.types.SpaceView3D.draw_handler_add(bl_graphics_draw.draw, (), 'WINDOW', 'POST_VIEW')"),
    ]:
        p = os.path.join(dst_mod, rel)
        with open(p, encoding="utf-8") as f:
            text = f.read()
        if old in text:
            with open(p, "w", encoding="utf-8") as f:
                f.write(text.replace(old, new))
        elif new.splitlines()[0].strip() not in text:
            print(f"M3PIPELINE_FAILED at patch: anchor missing in {rel} (m3studio version changed?)")
            sys.exit(3)
except Exception as e:
    fail("stage", e)

# ---- import src, export dst ----------------------------------------------------------------
try:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sys.path.insert(0, stage)
    import m3studio
    m3studio.register()
except Exception as e:
    fail("register", e)

try:
    getattr(bpy.ops.m3, "import")(filepath=SRC)
    arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    bpy.context.view_layer.objects.active = arm
    arm.select_set(True)
except Exception as e:
    fail("import", e)

try:
    bpy.ops.m3.export(filepath=DST)
except Exception as e:
    fail("export", e)

# ---- copy back what m3studio does not round-trip -------------------------------------------
try:
    import importlib.util
    spec = importlib.util.spec_from_file_location("io_m3", os.path.join(dst_mod, "io_m3.py"))
    io_m3 = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(io_m3)

    def seq_map(sl):
        seqs = sl[sl.model.sequences.index] if sl.model.sequences.entries else None
        out = {}
        for s in (seqs.content if seqs else []):
            name = "".join(chr(c) for c in sl[s.name.index].content if c != 0)
            out[name] = s
        return out

    sl_src = io_m3.M3SectionList.load(SRC)
    sl_dst = io_m3.M3SectionList.load(DST)
    src_seqs = seq_map(sl_src)
    patched = 0
    for name, d in seq_map(sl_dst).items():
        s = src_seqs.get(name)
        if s is None:
            continue
        for a in ("x", "y", "z"):
            setattr(d.bounding_sphere.min, a, getattr(s.bounding_sphere.min, a))
            setattr(d.bounding_sphere.max, a, getattr(s.bounding_sphere.max, a))
        d.bounding_sphere.radius = s.bounding_sphere.radius
        d.movement_speed = s.movement_speed
        d.flags = s.flags
        patched += 1
    sl_dst.save(DST)
    print(f"M3PIPELINE_PATCHED {patched} sequences")
except Exception as e:
    fail("patch", e)

print("M3PIPELINE_OK")
sys.exit(0)
