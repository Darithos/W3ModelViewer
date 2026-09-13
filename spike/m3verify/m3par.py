"""Dump every PAR_ in an .m3 with the fields that differ from a default-initialised instance.

    python m3par.py model.m3 [more.m3 ...]

Uses m3studio's structures.xml (the same description M3ParticleWriter's defaults were generated
from), so a Blizzard emitter and one of ours print through the same lens and can be diffed by eye.
Animation references print as their default value only; the per-sequence keys live in STC_.
"""
import sys, os, struct, importlib.util

ADDON = r"C:\Users\Darithos\AppData\Roaming\Blender Foundation\Blender\3.3\scripts\addons\m3studio-main"
spec = importlib.util.spec_from_file_location("io_m3", os.path.join(ADDON, "io_m3.py"))
io_m3 = importlib.util.module_from_spec(spec)
sys.modules["io_m3"] = io_m3
spec.loader.exec_module(io_m3)


def sections(data):
    magic, index_offset, index_size = struct.unpack_from("<4sII", data, 0)
    out = []
    for i in range(index_size):
        raw_tag, offset, reps, ver = struct.unpack_from("<4sIII", data, index_offset + 16 * i)
        out.append((raw_tag.decode("ascii", "replace").replace("\x00", "")[::-1], offset, reps, ver))
    return out


def fmt(v):
    if isinstance(v, float):
        return f"{v:.5g}"
    if hasattr(v, "__dict__"):
        d = vars(v)
        # animation refs and small structs
        keys = [k for k in d if not k.startswith("_")]
        return "{" + ", ".join(f"{k}={fmt(d[k])}" for k in keys) + "}"
    if isinstance(v, (list, tuple)):
        return "[" + ", ".join(fmt(x) for x in v) + "]"
    return str(v)


def dump(path, tag="PAR_"):
    data = open(path, "rb").read()
    secs = sections(data)
    print(f"=== {os.path.basename(path)} ===")
    for name, offset, reps, ver in secs:
        if name != tag:
            continue
        desc = io_m3.structures[name].get_version(ver)
        default = desc.instance()
        default_blob = desc.instances_to_bytearray([default])
        for i in range(reps):
            blob = data[offset + i * desc.size: offset + (i + 1) * desc.size]
            inst = desc.instances_from_bytearray(blob, 1)[0] if hasattr(desc, "instances_from_bytearray") else None
            print(f"\n--- {name} v{ver} #{i} ---")
            off = 0
            for fname, field in desc.fields.items():
                seg = blob[off:off + field.size]
                dseg = default_blob[off:off + field.size]
                if seg != dseg:
                    val = getattr(inst, fname, None) if inst is not None else None
                    shown = fmt(val) if val is not None else seg.hex(" ")
                    print(f"  +{off:<5} {fname:<34} {shown}")
                off += field.size


if __name__ == "__main__":
    tag = "PAR_"
    for p in sys.argv[1:]:
        if p.startswith("--tag="):
            tag = p[6:]
            continue
        dump(p, tag)
