"""Dump every MAT_ in an .m3: name, flags, blend_mode, alpha_test_threshold, and each layer's
bitmap path with whether that path resolves to a file on disk relative to the .m3."""
import sys, os, importlib.util

ADDON = r"C:\Users\Darithos\AppData\Roaming\Blender Foundation\Blender\3.3\scripts\addons\m3studio-main"
spec = importlib.util.spec_from_file_location("io_m3", os.path.join(ADDON, "io_m3.py"))
io_m3 = importlib.util.module_from_spec(spec)
sys.modules["io_m3"] = io_m3
spec.loader.exec_module(io_m3)

FLAGS = [
    (0x1, "vertex_color"), (0x2, "vertex_alpha"), (0x4, "unfogged"),
    (0x8, "two_sided"), (0x10, "unshaded"), (0x20, "no_shadows_cast"),
    (0x40, "no_hittest"), (0x80, "no_shadows_receive"), (0x100, "depth_prepass"),
    (0x1000, "pixel_forward_lighting"), (0x2000, "depth_fog"),
    (0x4000, "transparent_shadows"), (0x8000, "decal_lighting"),
    (0x10000, "transparent_depth_effects"), (0x20000, "transparent_local_lights"),
    (0x40000, "disable_soft"), (0x80000, "double_lambert"),
    (0x100000, "hair_layer_sorting"), (0x10000000, "depth_prepass_low_required"),
    (0x80000000, "geometry_visible"),
]
BLEND = {0: "opaque", 1: "alpha-blend", 2: "add", 3: "alpha-add", 4: "mod", 5: "mod2x"}
LAYERS = ["diff", "decal", "spec", "gloss", "emis1", "emis2", "envi", "envi_mask",
          "alpha1", "alpha2", "norm", "height", "light", "ao"]


def res(sl, ref):
    """Resolve a Reference/SmallReference to its section's instance list."""
    if getattr(ref, "entries", 0) == 0:
        return []
    return list(sl[ref.index])


def s_of(sl, ref):
    try:
        v = res(sl, ref)
    except Exception:
        return ""
    if not v:
        return ""
    if isinstance(v[0], str):
        return v[0].rstrip("\0")
    try:
        return "".join(chr(c) for c in v if c).rstrip("\0")
    except Exception:
        return ""


def main(path, checkdisk=True):
    root = os.path.dirname(os.path.abspath(path))
    sl = io_m3.M3SectionList.load(path)
    model = res(sl, sl[0][0].model)[0]
    mats = res(sl, model.materials_standard)
    print(f"\n=== {os.path.basename(path)} — {len(mats)} standard materials ===")
    missing = []
    for m in mats:
        name = s_of(sl, m.name)
        fl = m.flags & 0xFFFFFFFF
        names = "|".join(n for b, n in FLAGS if fl & b) or "-"
        print(f"\n  {name}")
        print(f"    blend={m.blend_mode} ({BLEND.get(m.blend_mode, '?')})  "
              f"alpha_test={m.alpha_test_threshold}  prio={m.priority}  spec={m.specularity:g}")
        print(f"    flags=0x{fl:08X}  {names}")
        for tag in LAYERS:
            ref = getattr(m, "layer_" + tag, None)
            if ref is None:
                continue
            lay = res(sl, ref)
            if not lay:
                continue
            p = s_of(sl, lay[0].color_bitmap)
            if not p:
                continue
            disk = os.path.join(root, p.replace("/", os.sep))
            ok = os.path.isfile(disk)
            if not ok and checkdisk:
                missing.append((name, tag, p))
            mark = "OK" if ok else ("*** MISSING ***" if checkdisk else "")
            print(f"      {tag:<10} {p:<50} {mark}")
    if checkdisk:
        if missing:
            print(f"\n  !! {len(missing)} unresolved texture reference(s)")
            for n, t, p in missing:
                print(f"     {n}.{t} -> {p}")
        else:
            print("\n  all texture references resolve on disk")


for p in sys.argv[1:]:
    main(p, checkdisk="--nodisk" not in sys.argv)
