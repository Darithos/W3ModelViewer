"""Generate the PAR_ field-offset and default tables that M3ParticleWriter is written against.

PAR_ v24 is 1496 bytes across 141 fields, and its defaults are NOT zero: friction and the mass
multipliers are 1.0, the flipbook fractions are +infinity, and four sentinel indices are -1. Writing
a zero-filled struct therefore produces a wrong particle system, not a neutral one — the same class
of mistake that made every exported model crash the editor until the MAT_ flag pair was bisected out.

So the exporter starts from exactly what m3studio builds for a fresh v24 system, and this script is
where those numbers come from. Run it after changing the target version and paste the output into
src/Wc3ModelViewer.Core/Convert/M3ParticleWriter.cs.

    python m3pardefaults.py [structure] [version]
"""
import importlib.util
import os
import struct
import sys

ADDON = r"C:\Users\Darithos\AppData\Roaming\Blender Foundation\Blender\3.3\scripts\addons\m3studio-main"

FIELDS_OF_INTEREST = (
    "bone", "material_reference_index", "emit_speed", "emit_speed_random", "emit_spread_x",
    "emit_spread_y", "lifespan", "gravity", "size_anim_mid", "color_anim_mid", "alpha_anim_mid",
    "size", "color_init", "color_mid", "color_end", "emit_max", "emit_rate", "emit_shape",
    "emit_shape_size", "uv_flipbook_cols", "uv_flipbook_rows", "particle_type", "flags",
    # ribbons
    "height_above", "height_below", "edge_rate", "edge_lifespan", "color_base", "material",
)


def load_io_m3():
    spec = importlib.util.spec_from_file_location("io_m3", os.path.join(ADDON, "io_m3.py"))
    mod = importlib.util.module_from_spec(spec)
    sys.modules["io_m3"] = mod
    spec.loader.exec_module(mod)
    return mod


def main(name="PAR_", version=24):
    io_m3 = load_io_m3()
    desc = io_m3.structures[name].get_version(version)

    print(f"=== {name} v{version}: {desc.size} bytes, {len(desc.fields)} fields ===\n")

    print("--- field offsets ---")
    offset = 0
    for field_name, field in desc.fields.items():
        if field_name in FIELDS_OF_INTEREST:
            print(f"  private const int Off{to_pascal(field_name):28} = {offset};   // +{field.size}")
        offset += field.size
    assert offset == desc.size, f"field sizes total {offset}, header says {desc.size}"

    # Serialise a default-initialised instance: whatever is non-zero is a default the writer must
    # reproduce, and there is no other place those values are written down.
    blob = desc.instances_to_bytearray([desc.instance()])
    print("\n--- non-zero defaults, as (byte offset, raw uint32) ---")
    names = offsets_by_name(desc)
    for at in range(0, len(blob), 4):
        word = blob[at:at + 4]
        if word == b"\0\0\0\0":
            continue
        raw = struct.unpack("<I", word)[0]
        f32 = struct.unpack("<f", word)[0]
        i32 = struct.unpack("<i", word)[0]
        meaning = f32 if abs(f32) not in (0.0,) and -1e9 < f32 < 1e9 else i32
        print(f"        ({at}, 0x{raw:08X}),   // {names.get(at, '?'):28} {meaning}")


def offsets_by_name(desc):
    out, offset = {}, 0
    for field_name, field in desc.fields.items():
        out[offset] = field_name
        offset += field.size
    return out


def to_pascal(s):
    return "".join(part.capitalize() for part in s.split("_"))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "PAR_",
         int(sys.argv[2]) if len(sys.argv) > 2 else 24)
