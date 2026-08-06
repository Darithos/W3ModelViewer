"""Replay a .m3's own animation data and compare against the viewer's rendering.

    <blender>\\3.3\\python\\bin\\python.exe m3sim.py model.ref.json

Reads the reference JSON emitted by `MdxProbe --simref` (viewer-skinned vertex positions at exact
baked frames), then independently computes the same vertices the way a consumer of the .m3 must:

    bone world  W_i = L_i * W_parent        (L from the file's own TRS keys at that frame)
    vertex      p'  = sum_k weight_k * (p * IREF_bone_k * W_bone_k)

Matrices use the row-vector convention (v' = v*M); the file's Matrix44 x/y/z/w fields are the
basis vectors and translation, which is the same layout either way.

A large divergence means the .m3 does not encode what the viewer renders — an exporter bug that
no structural check can see.
"""
import sys, json, os, math, importlib.util

ADDON = r"C:\Users\Darithos\AppData\Roaming\Blender Foundation\Blender\3.3\scripts\addons\m3studio-main"
spec = importlib.util.spec_from_file_location("io_m3", os.path.join(ADDON, "io_m3.py"))
io_m3 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(io_m3)

IDENTITY = [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]]


def mat_mul(a, b):
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)] for r in range(4)]


def xform(v, m):
    return tuple(v[0] * m[0][i] + v[1] * m[1][i] + v[2] * m[2][i] + m[3][i] for i in range(3))


def trs(loc, quat, scale):
    x, y, z, w = quat
    n = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    x, y, z, w = x / n, y / n, z / n, w / n
    # rotation, row-vector convention
    rot = [
        [1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w), 0],
        [2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w), 0],
        [2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y), 0],
        [0, 0, 0, 1],
    ]
    m = [[rot[r][c] * (scale[r] if r < 3 else 1) for c in range(4)] for r in range(4)]
    m[3] = [loc[0], loc[1], loc[2], 1]
    return m


def mat_from_field(f):
    return [[f.x.x, f.x.y, f.x.z, f.x.w],
            [f.y.x, f.y.y, f.y.z, f.y.w],
            [f.z.x, f.z.y, f.z.z, f.z.w],
            [f.w.x, f.w.y, f.w.z, f.w.w]]


def ref(sl, r):
    return sl[r.index] if r.entries else None


def sec_str(sl, r):
    s = ref(sl, r)
    return "".join(chr(c) for c in s.content if c != 0) if s else ""


refpath = sys.argv[1]
with open(refpath) as f:
    R = json.load(f)
m3path = os.path.join(os.path.dirname(refpath), R["m3"])
sl = io_m3.M3SectionList.load(m3path)
m = sl.model

bones = ref(sl, m.bones).content
irefs = ref(sl, m.bone_rests).content
bone_names = [sec_str(sl, b.name) for b in bones]

# locate the STC for the requested sequence: STG name -> stc index
target = R["sequence_m3"]
stgs = ref(sl, m.sequence_transformation_groups)
stcs = ref(sl, m.sequence_transformation_collections)
stc = None
for g in (stgs.content if stgs else []):
    if sec_str(sl, g.name) == target:
        idxs = ref(sl, g.stc_indices)
        if idxs and len(idxs.content):
            stc = stcs.content[idxs.content[0]]
        break
if stc is None:
    for s in (stcs.content if stcs else []):
        if sec_str(sl, s.name).startswith(target):
            stc = s
            break
if stc is None:
    print(f"SIM_FAILED: no STC for sequence '{target}'")
    sys.exit(2)
print(f"using STC '{sec_str(sl, stc.name)}' for sequence '{target}'")

# id -> (kind, track index); kind 2 = SD3V (vec3), 3 = SD4Q (quat)
ids = ref(sl, stc.anim_ids).content
refs = ref(sl, stc.anim_refs).content
track_of = {aid: (r >> 16, r & 0xFFFF) for aid, r in zip(ids, refs)}
sd3v = ref(sl, stc.sd3v)
sd4q = ref(sl, stc.sd4q)


def sample(aid, frame_ms, default):
    """Key value at an exact frame time, else the bone default."""
    if aid not in track_of:
        return None
    kind, ti = track_of[aid]
    sec = sd3v if kind == 2 else sd4q if kind == 3 else None
    if sec is None or ti >= len(sec.content):
        return None
    head = sec.content[ti]
    frames = ref(sl, head.frames)
    keys = ref(sl, head.keys)
    if frames is None or keys is None:
        return None
    fl = list(frames.content)
    # exact match preferred; otherwise nearest (frames are the same grid the viewer sampled)
    if frame_ms in fl:
        k = keys.content[fl.index(frame_ms)]
    else:
        best = min(range(len(fl)), key=lambda i: abs(fl[i] - frame_ms))
        k = keys.content[best]
    if kind == 2:
        return (k.x, k.y, k.z)
    return (k.x, k.y, k.z, k.w)


# region 0 geometry
div = ref(sl, m.divisions).content[0]
regn = ref(sl, div.regions).content[R["region"]]
faces = ref(sl, div.faces).content
lookup = ref(sl, m.bone_lookup).content
vdesc = io_m3.M3StructureDescription.get_vertex_description(m.vertex_flags)
vraw = ref(sl, m.vertices).content
vraw = bytes(vraw) if not isinstance(vraw, (bytes, bytearray)) else vraw
import struct as st
stride = vdesc.size
has_weights = any(f.startswith("weight") for f in vdesc.fields)
print(f"region0: {regn.vertex_count} verts, lookup[{regn.first_bone_lookup_index}:+{regn.bone_lookup_count}], "
      f"stride {stride}, lookups_used {regn.vertex_lookups_used}")

worst = 0.0
worst_where = None
report = []
for smp in R["samples"]:
    fms = smp["frame_ms"]
    # regions renumber (and split) vertices, so match m3 vertices to reference by rest position
    expected_by_rest = {(round(r[0], 3), round(r[1], 3), round(r[2], 3)): p
                        for r, p in zip(R["rest"], smp["positions"])}
    # compose bone worlds at this frame
    worlds = [None] * len(bones)
    for i, b in enumerate(bones):
        loc = sample(b.location.header.id, fms, None) or (b.location.default.x, b.location.default.y, b.location.default.z)
        q = sample(b.rotation.header.id, fms, None)
        if q is None:
            q = (b.rotation.default.x, b.rotation.default.y, b.rotation.default.z, b.rotation.default.w)
        scl = sample(b.scale.header.id, fms, None) or (b.scale.default.x, b.scale.default.y, b.scale.default.z)
        local = trs(loc, q, scl)
        p = b.parent
        worlds[i] = local if p < 0 else mat_mul(local, worlds[p])

    skin = [mat_mul(mat_from_field(irefs[i].matrix), worlds[i]) for i in range(len(bones))]

    dev = 0.0
    dev_v = -1
    matched = 0
    for vi in range(regn.vertex_count):
        o = (regn.first_vertex_index + vi) * stride
        px, py, pz = st.unpack_from("<3f", vraw, o)
        expect = expected_by_rest.get((round(px, 3), round(py, 3), round(pz, 3)))
        if expect is None:
            continue        # vertex outside the reference sample
        matched += 1
        acc = [0.0, 0.0, 0.0]
        total = 0
        for k in range(4):
            w8 = vraw[o + 12 + k]
            if w8 == 0:
                continue
            li = vraw[o + 16 + k]
            bone = lookup[regn.first_bone_lookup_index + li]
            t = xform((px, py, pz), skin[bone])
            for c in range(3):
                acc[c] += t[c] * (w8 / 255.0)
            total += w8
        if total == 0:
            acc = [px, py, pz]
        d = math.dist(acc, expect)
        if d > dev:
            dev, dev_v = d, vi
    report.append((fms, dev, dev_v, matched))
    if dev > worst:
        worst, worst_where = dev, (fms, dev_v)

print(f"\nmodel {R['model']}  sequence '{R['sequence_mdx']}' -> '{target}'  skin={'HD' if R['has_skin'] else 'classic'}")
for fms, dev, vi, matched in report:
    print(f"  frame {fms:>5} ms: max deviation {dev:10.3f} units over {matched} matched vertices (worst {vi})")
print(f"\nWORST {worst:.3f} units at frame {worst_where[0]} ms, vertex {worst_where[1]}")
print("VERDICT:", "MATCH — the .m3 encodes what the viewer renders" if worst < 0.5
      else "MISMATCH — the .m3 does NOT encode what the viewer renders")
