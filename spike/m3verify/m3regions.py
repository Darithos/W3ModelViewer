"""Per-region bone-palette statistics: how many bones does one region reference?"""
import sys, glob, importlib.util, os

ADDON = r"C:\Users\Darithos\AppData\Roaming\Blender Foundation\Blender\3.3\scripts\addons\m3studio-main"
spec = importlib.util.spec_from_file_location("io_m3", os.path.join(ADDON, "io_m3.py"))
io_m3 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(io_m3)
io_m3.structures["COL_"] = io_m3.structures["COL"]


def ref(sl, r):
    return sl[r.index] if r.entries else None


paths = []
for a in sys.argv[1:]:
    paths.extend(glob.glob(a))

print(f"{'file':<34} {'bones':>6} {'regions':>8} {'maxPalette':>11} {'maxLookupsUsed':>15} {'skin_bone_count':>16}")
overall = 0
for p in sorted(paths):
    try:
        sl = io_m3.M3SectionList.load(p)
        m = sl.model
        bones = ref(sl, m.bones)
        div = ref(sl, m.divisions)
        if div is None:
            continue
        regs = ref(sl, div.content[0].regions)
        counts = [r.bone_lookup_count for r in regs.content] if regs else [0]
        used = [r.vertex_lookups_used for r in regs.content] if regs else [0]
        mx = max(counts) if counts else 0
        overall = max(overall, mx)
        print(f"{os.path.basename(p):<34} {len(bones.content):>6} {len(counts):>8} {mx:>11} {max(used):>15} {m.skin_bone_count:>16}")
    except Exception as e:
        print(f"{os.path.basename(p):<34} ERROR {e}")
print(f"\nlargest single-region bone palette seen: {overall}")
