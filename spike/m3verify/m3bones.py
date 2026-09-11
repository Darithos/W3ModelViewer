"""Bone flags and BBSC billboard entries of an .m3 — how a model makes a bone face the camera.

Usage: python m3bones.py model.m3 [--all]
Prints every BBSC entry (bone, billboard_type, camera_look_at, up/forward quaternions) and every
bone that carries a billboard flag. --all lists every bone.
"""
import struct, sys

BONE_FLAGS = [(0x1, "inhT"), (0x2, "inhS"), (0x4, "inhR"), (0x10, "billboard1"), (0x40, "billboard2"),
              (0x100, "project_2d"), (0x200, "animated"), (0x400, "ik"), (0x800, "skinned"),
              (0x2000, "real"), (0x4000, "batch1"), (0x8000, "batch2")]


def load(path):
    data = open(path, "rb").read()
    magic, idx_off, idx_n = struct.unpack_from("<4sII", data, 0)
    sections = []
    for i in range(idx_n):
        tag, off, reps, ver = struct.unpack_from("<4sIII", data, idx_off + 16 * i)
        sections.append((tag[::-1].rstrip(b"\0").decode("ascii", "replace"), off, reps, ver))
    return data, sections


def string(data, sections, ref_count, ref_index):
    if ref_count == 0:
        return ""
    tag, off, reps, ver = sections[ref_index]
    return data[off:off + ref_count].split(b"\0")[0].decode("ascii", "replace")


def main(path, show_all):
    data, sections = load(path)
    print(f"== {path}")
    bones = [s for s in sections if s[0] == "BONE"]
    bbsc = [s for s in sections if s[0] == "BBSC"]
    names = []
    for tag, off, reps, ver in bones:
        for i in range(reps):
            p = off + 160 * i
            _, ncount, nindex, _, flags, parent = struct.unpack_from("<iIIIIh", data, p)
            name = string(data, sections, ncount, nindex)
            names.append(name)
            fl = " ".join(n for m, n in BONE_FLAGS if flags & m)
            if show_all or flags & 0x150:
                print(f"  bone {i:3} {name:<40} flags=0x{flags:05X} [{fl}] parent={parent}")
    print(f"  {len(names)} bones, {sum(s[2] for s in bbsc)} BBSC entries")
    for tag, off, reps, ver in bbsc:
        for i in range(reps):
            p = off + 48 * i
            dcount, dindex, dflags, bone, btype, look = struct.unpack_from("<IIIHBB", data, p)
            up = struct.unpack_from("<4f", data, p + 16)
            fwd = struct.unpack_from("<4f", data, p + 32)
            deps = ""
            if dcount:
                dtag, doff, dreps, dver = sections[dindex]
                deps = f" dependents={dtag}x{dcount}"
            bname = names[bone] if bone < len(names) else "?"
            print(f"  BBSC {i}: bone={bone} '{bname}' type={btype} look_at={look} "
                  f"up=({up[0]:.3f},{up[1]:.3f},{up[2]:.3f},{up[3]:.3f}) "
                  f"fwd=({fwd[0]:.3f},{fwd[1]:.3f},{fwd[2]:.3f},{fwd[3]:.3f}){deps}")


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    for a in args:
        main(a, "--all" in sys.argv)
