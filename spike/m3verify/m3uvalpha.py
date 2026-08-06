"""Per-region UV coverage vs. diffuse alpha.

For each REGN, rasterises its UV triangles into the diffuse texture and reports what fraction of
the texels the region actually samples are transparent. That is the only honest way to decide
whether a material is a cutout: an atlas can be 95% opaque and still have one geoset that lives
entirely inside its cut-out strip."""
import sys, os, importlib.util, struct

ADDON = r"C:\Users\Darithos\AppData\Roaming\Blender Foundation\Blender\3.3\scripts\addons\m3studio-main"
spec = importlib.util.spec_from_file_location("io_m3", os.path.join(ADDON, "io_m3.py"))
io_m3 = importlib.util.module_from_spec(spec)
sys.modules["io_m3"] = io_m3
spec.loader.exec_module(io_m3)

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ddsalpha import dds_alpha


def res(sl, ref):
    return list(sl[ref.index]) if getattr(ref, "entries", 0) else []


def s_of(sl, ref):
    try:
        v = res(sl, ref)
    except Exception:
        return ""
    if not v:
        return ""
    return v[0].rstrip("\0") if isinstance(v[0], str) else "".join(chr(c) for c in v if c).rstrip("\0")


def main(path):
    root = os.path.dirname(os.path.abspath(path))
    sl = io_m3.M3SectionList.load(path)
    model = res(sl, sl[0][0].model)[0]
    div = res(sl, model.divisions)[0]
    regions = res(sl, div.regions)
    faces = res(sl, div.faces)
    batches = res(sl, div.batches)
    mats = res(sl, model.materials_standard)
    matm = res(sl, model.material_references)

    vbytes = bytes(res(sl, model.vertices))
    vdesc = io_m3.M3StructureDescription.get_vertex_description(model.vertex_flags)
    stride = vdesc.size
    uvoff = None
    o = 0
    for name, f in vdesc.fields.items():
        if name.startswith("uv0"):
            uvoff = o
            break
        o += f.size
    assert uvoff is not None, list(vdesc.fields)

    region_mat = {}
    for b in batches:
        region_mat[b.region_index] = matm[b.material_reference_index].material_index

    cache = {}
    print(f"\n=== {os.path.basename(path)} ===")
    print(f"{'region':>6} {'material':<34} {'tris':>6} {'texels':>8} {'cut%':>7} {'verdict'}")
    for ri, r in enumerate(regions):
        mi = region_mat.get(ri)
        if mi is None:
            continue
        m = mats[mi]
        name = s_of(sl, m.name)
        lay = res(sl, m.layer_diff)
        p = s_of(sl, lay[0].color_bitmap) if lay else ""
        if not p:
            continue
        dpath = os.path.join(root, p.replace("/", os.sep))
        if dpath not in cache:
            cache[dpath] = dds_alpha(dpath) if os.path.isfile(dpath) else (0, 0, None, "?")
        w, h, alpha, _ = cache[dpath]
        if alpha is None:
            continue

        # region-local faces -> global vertex indices
        fs, fn = r.first_face_index, r.face_count
        vs = r.first_vertex_index
        mask = bytearray(w * h)
        ntri = fn // 3
        for t in range(ntri):
            uv = []
            for k in range(3):
                gi = vs + faces[fs + t * 3 + k]
                u, v = struct.unpack_from("<hh", vbytes, gi * stride + uvoff)
                uv.append((u / 2048.0 * w, v / 2048.0 * h))
            # scanline-fill the UV triangle
            ys = sorted(int(p[1]) for p in uv)
            y0, y1 = max(0, ys[0]), min(h - 1, ys[2])
            (ax, ay), (bx, by), (cx, cy) = uv
            den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
            if abs(den) < 1e-9:
                continue
            xs = sorted(int(p[0]) for p in uv)
            for y in range(y0, y1 + 1):
                for x in range(max(0, xs[0]), min(w - 1, xs[2]) + 1):
                    px, py = x + 0.5, y + 0.5
                    l1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den
                    l2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den
                    if l1 >= -0.001 and l2 >= -0.001 and l1 + l2 <= 1.001:
                        mask[y * w + x] = 1
        tot = sum(mask)
        cut = sum(1 for i in range(w * h) if mask[i] and alpha[i] < 128)
        pct = 100.0 * cut / max(1, tot)
        verdict = "CUTOUT" if pct > 1.0 else "opaque"
        print(f"{ri:>6} {name:<34} {ntri:>6} {tot:>8} {pct:>6.2f}% {verdict}   blend={m.blend_mode}")


for p in sys.argv[1:]:
    main(p)
