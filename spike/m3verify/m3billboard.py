"""Census of BBSC billboards: what SC2 art writes, and which bone-local axis the card lies flat on.

    <blender>\\3.3\\python\\bin\\python.exe m3billboard.py <file.m3 | folder> [...] [--limit N]

For every BBSC entry prints billboard_type, camera_look_at, the up/forward quaternions, the bone's
flags, and the bone-local extents of the geometry whose dominant weight is that bone (vertex *
IREF). A flat card shows one ~0 extent: that is the axis SC2 turns toward the camera. Folders are
pre-filtered on the raw section index, so only files that carry a BBSC pay for a full parse.
"""
import sys, os, struct, importlib.util
from collections import Counter

ADDON = r"C:\Users\Darithos\AppData\Roaming\Blender Foundation\Blender\3.3\scripts\addons\m3studio-main"
spec = importlib.util.spec_from_file_location("io_m3", os.path.join(ADDON, "io_m3.py"))
io_m3 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(io_m3)


def has_bbsc(path):
    with open(path, "rb") as f:
        head = f.read(12)
        if head[:4] not in (b"43DM", b"33DM"):
            return False
        idx_off, idx_n = struct.unpack_from("<II", head, 4)
        f.seek(idx_off)
        table = f.read(16 * idx_n)
    return any(table[16 * i:16 * i + 4] == b"CSBB" for i in range(idx_n))


def ref(sl, r):
    return sl[r.index] if r.entries else None


def sec_str(sl, r):
    s = ref(sl, r)
    return "".join(chr(c) for c in s.content if c != 0) if s else ""


def xform(v, m):
    return tuple(v[0] * m[0][i] + v[1] * m[1][i] + v[2] * m[2][i] + m[3][i] for i in range(3))


def mat_from_field(f):
    return [[f.x.x, f.x.y, f.x.z, f.x.w], [f.y.x, f.y.y, f.y.z, f.y.w],
            [f.z.x, f.z.y, f.z.z, f.z.w], [f.w.x, f.w.y, f.w.z, f.w.w]]


def analyse(path, stats):
    sl = io_m3.M3SectionList.load(path)
    m = sl.model
    bbs = ref(sl, m.billboards)
    if bbs is None:
        return
    bones = ref(sl, m.bones).content
    irefs = ref(sl, m.bone_rests).content
    names = [sec_str(sl, b.name) for b in bones]

    # dominant-bone vertex positions, per bone
    per_bone = {}
    verts = ref(sl, m.vertices)
    divs = ref(sl, m.divisions)
    if verts is not None and divs is not None:
        vdesc = io_m3.M3StructureDescription.get_vertex_description(m.vertex_flags)
        off, o = {}, 0
        for name, fd in vdesc.fields.items():
            off[name] = o
            o += fd.size
        raw = bytes(verts.content)
        stride = vdesc.size
        lookup = ref(sl, m.bone_lookup).content if m.bone_lookup.entries else []
        wkeys = [k for k in off if k.startswith("weight")]
        lkeys = [k for k in off if k.startswith("lookup")]
        for div in divs.content:
            for regn in (ref(sl, div.regions).content if div.regions.entries else []):
                for vi in range(regn.vertex_count):
                    base = (regn.first_vertex_index + vi) * stride
                    p = struct.unpack_from("<3f", raw, base + off["pos"])
                    if "uv0" in off:
                        p = p + struct.unpack_from("<2h", raw, base + off["uv0"])
                    best, bw = -1, -1
                    for wk, lk in zip(wkeys, lkeys):
                        w = raw[base + off[wk]]
                        if w > bw:
                            bw, best = w, raw[base + off[lk]]
                    if bw <= 0:
                        continue
                    gi = regn.first_bone_lookup_index + best
                    if gi < len(lookup):
                        per_bone.setdefault(lookup[gi], []).append(p)

    print(f"== {path}")
    for b in bbs.content:
        bone = b.bone
        flags = bones[bone].flags if bone < len(bones) else 0
        pts = per_bone.get(bone, [])
        ext = ""
        flat = "-"
        small = []
        if pts and bone < len(irefs):
            M = mat_from_field(irefs[bone].matrix)
            loc = [xform(p, M) for p in pts]
            e = [max(q[c] for q in loc) - min(q[c] for q in loc) for c in range(3)]
            ext = f"local extents x={e[0]:.3f} y={e[1]:.3f} z={e[2]:.3f} ({len(pts)} verts)"
            big = max(e)
            if big > 0:
                small = [c for c in range(3) if e[c] < FLAT * big]
                flat = "".join("xyz"[c] for c in small) or "none"
            # Texture handedness: the local axis u grows along is screen-right, the one v shrinks
            # along is screen-up (v is top-down). Their cross product is the face the art is drawn
            # on, i.e. the side SC2 must turn toward the camera.
            if len(small) == 1 and len(pts[0]) == 5:
                n = len(pts)
                mu = [sum(q[c] for q in loc) / n for c in range(3)]
                mu_u = sum(p[3] for p in pts) / n
                mu_v = sum(p[4] for p in pts) / n
                cu = [sum((loc[i][c] - mu[c]) * (pts[i][3] - mu_u) for i in range(n)) for c in range(3)]
                cv = [-sum((loc[i][c] - mu[c]) * (pts[i][4] - mu_v) for i in range(n)) for c in range(3)]
                ra = max(range(3), key=lambda c: abs(cu[c]))
                ua = max(range(3), key=lambda c: abs(cv[c]))
                if ra != ua and cu[ra] != 0 and cv[ua] != 0:
                    right = [0, 0, 0]; right[ra] = 1 if cu[ra] > 0 else -1
                    up = [0, 0, 0]; up[ua] = 1 if cv[ua] > 0 else -1
                    front = (right[1] * up[2] - right[2] * up[1], right[2] * up[0] - right[0] * up[2],
                             right[0] * up[1] - right[1] * up[0])
                    sgn = lambda vec: next(("+" if vec[c] > 0 else "-") + "xyz"[c] for c in range(3) if vec[c])
                    face = f"right={sgn(right)} up={sgn(up)} front={sgn(front)}"
                    flat += " " + face
                    ident = all(abs(a - c) < 1e-4 for a, c in zip(
                        (b.up.x, b.up.y, b.up.z, b.up.w, b.forward.x, b.forward.y, b.forward.z, b.forward.w),
                        (0, 0, 0, 1, 0, 0, 0, 1)))
                    stats["face"][(b.billboard_type, b.camera_look_at, "I" if ident else "Q", face)] += 1
        up = (b.up.x, b.up.y, b.up.z, b.up.w)
        fw = (b.forward.x, b.forward.y, b.forward.z, b.forward.w)
        ident = all(abs(a - c) < 1e-4 for a, c in zip(up + fw, (0, 0, 0, 1, 0, 0, 0, 1)))
        stats["type"][b.billboard_type] += 1
        stats["look"][b.camera_look_at] += 1
        stats["quat"]["identity" if ident else "other"] += 1
        stats["flat"][(b.billboard_type, flat)] += 1
        stats["flags"][flags & 0xFF7] += 1
        print(f"  bone {bone:3} {names[bone] if bone < len(names) else '?':<32} type={b.billboard_type} "
              f"look={b.camera_look_at} flags=0x{flags:05X} up={tuple(round(v, 3) for v in up)} "
              f"fwd={tuple(round(v, 3) for v in fw)} flat={flat} {ext}")


FLAT = 0.02


def main():
    global FLAT
    args = sys.argv[1:]
    limit = 10 ** 9
    if "--flat" in args:
        i = args.index("--flat")
        FLAT = float(args[i + 1])
        del args[i:i + 2]
    if "--limit" in args:
        i = args.index("--limit")
        limit = int(args[i + 1])
        del args[i:i + 2]
    files = []
    for a in args:
        if os.path.isdir(a):
            for root, _, fs in os.walk(a):
                files += [os.path.join(root, f) for f in fs if f.lower().endswith(".m3")]
        else:
            files.append(a)
    stats = {k: Counter() for k in ("type", "look", "quat", "flat", "flags", "face")}
    scanned = carriers = 0
    for f in files:
        scanned += 1
        try:
            if not has_bbsc(f):
                continue
        except OSError:
            continue
        carriers += 1
        if carriers > limit:
            break
        try:
            analyse(f, stats)
        except Exception as ex:
            print(f"== {f}: parse failed: {ex}")
    print(f"\n--- {carriers} of {scanned} files carry BBSC ---")
    for k, c in stats.items():
        print(f"  {k}: {dict(c.most_common(12))}")


main()
