"""MD34 section dumper: tag/offset/entries/version table plus MODL field hexdump."""
import struct, sys

def dump(path):
    with open(path, "rb") as f:
        data = f.read()
    magic, index_offset, index_size, m_count, m_index, m_flags = struct.unpack_from("<4sIIIII", data, 0)
    print(f"FILE {path}  ({len(data)} bytes)")
    print(f"magic={magic[::-1].decode()} index_offset=0x{index_offset:X} sections={index_size} modelRef=(count={m_count}, index={m_index}, flags=0x{m_flags:X})")
    entries = []
    for i in range(index_size):
        tag, offset, reps, ver = struct.unpack_from("<4sIII", data, index_offset + 16 * i)
        entries.append((tag[::-1].decode("ascii", "replace"), offset, reps, ver))
    print(f"{'#':>3} {'tag':<5} {'ver':>3} {'reps':>7} {'offset':>9} {'sizeToNext':>10}")
    for i, (tag, offset, reps, ver) in enumerate(entries):
        nxt = entries[i + 1][1] if i + 1 < len(entries) else index_offset
        print(f"{i:>3} {tag:<5} {ver:>3} {reps:>7} 0x{offset:>7X} {nxt - offset:>10}")
    # MODL is entry m_index; hexdump its first bytes for reference-level diffing
    tag, offset, reps, ver = entries[m_index]
    print(f"\nMODL entry #{m_index}: version={ver} reps={reps} offset=0x{offset:X}")
    end = entries[m_index + 1][1] if m_index + 1 < len(entries) else index_offset
    blob = data[offset:end]
    print(f"MODL byte size (to next section, incl padding): {len(blob)}")
    for row in range(0, min(len(blob), 800), 16):
        chunk = blob[row:row + 16]
        hexs = " ".join(f"{b:02X}" for b in chunk)
        u32s = " ".join(f"{struct.unpack_from('<I', chunk, o)[0]:>10}" for o in range(0, len(chunk) - 3, 4))
        print(f"  +{row:>4} {hexs:<48} | {u32s}")

for p in sys.argv[1:]:
    dump(p)
    print("=" * 100)
