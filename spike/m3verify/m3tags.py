"""List distinct (tag, version) pairs in .m3 files and check them against m3studio's structures.xml."""
import sys, struct, importlib.util

ADDON = r"C:\Users\Darithos\AppData\Roaming\Blender Foundation\Blender\3.3\scripts\addons\m3studio-main"
spec = importlib.util.spec_from_file_location("io_m3", ADDON + r"\io_m3.py")
io_m3 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(io_m3)

for path in sys.argv[1:]:
    with open(path, "rb") as f:
        data = f.read()
    magic, index_offset, index_size = struct.unpack_from("<4sII", data, 0)
    pairs = {}
    for i in range(index_size):
        raw_tag, offset, reps, ver = struct.unpack_from("<4sIII", data, index_offset + 16 * i)
        name = raw_tag.decode("ascii", "replace").replace("\x00", "")[::-1]
        pairs.setdefault((name, ver, raw_tag), 0)
        pairs[(name, ver, raw_tag)] += 1
    print(f"\n{path}")
    for (name, ver, raw_tag), count in sorted(pairs.items()):
        hist = io_m3.structures.get(name)
        if hist is None:
            verdict = "UNKNOWN TAG"
        elif hist.get_version(ver) is None:
            verdict = f"UNKNOWN VERSION (known: {sorted(hist.version_to_size)})"
        else:
            verdict = "ok"
        raw = " ".join(f"{b:02X}" for b in raw_tag)
        print(f"  {name:<5} v{ver:<3} x{count:<5} [{raw}]  {verdict}")
