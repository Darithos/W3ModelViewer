"""Field-level diff of two .m3 files using m3studio's own parser (the spec SC2 accepts).

Usage: python m3compare.py good.m3 mine.m3
"""
import sys, importlib.util, traceback

ADDON = r"C:\Users\Darithos\AppData\Roaming\Blender Foundation\Blender\3.3\scripts\addons\m3studio-main"
spec = importlib.util.spec_from_file_location("io_m3", ADDON + r"\io_m3.py")
io_m3 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(io_m3)
io_m3.structures["COL_"] = io_m3.structures["COL"]  # tolerate my writer's wrong tag so the diff can proceed


def load(path):
    try:
        sl = io_m3.M3SectionList.load(path)
        return sl, None
    except Exception as e:
        return None, f"{type(e).__name__}: {e}\n{traceback.format_exc()}"


def validate(sl, label):
    print(f"--- validate({label}) ---")
    issues = 0
    for ii, section in enumerate(sl):
        try:
            for instance in section:
                section.desc.instance_validate(instance, section.desc.history.name)
        except Exception as e:
            issues += 1
            if issues <= 30:
                print(f"  section #{ii} {section.desc.history.name}V{section.desc.version}: {e}")
    print(f"  {issues} sections with validation issues" if issues else "  clean")


def refstr(sl, ref):
    if ref.entries == 0:
        return "(null)"
    try:
        sec = sl[ref.index]
        return f"-> #{ref.index} {sec.desc.history.name}V{sec.desc.version} x{ref.entries}"
    except Exception:
        return f"-> #{ref.index} x{ref.entries} (unresolvable)"


def field_repr(sl, data, field):
    val = getattr(data, field.name)
    if type(field) is io_m3.M3FieldStructure:
        if field.desc.history.name in ("Reference", "SmallReference"):
            return refstr(sl, val)
        if field.desc.history.name in ("VEC3", "VEC2", "VEC4", "QUAT"):
            parts = [f"{getattr(val, a):.4g}" for a in ("x", "y", "z", "w") if hasattr(val, a)]
            return "(" + ", ".join(parts) + ")"
        if field.desc.history.name == "BNDS":
            return f"min({val.min.x:.4g},{val.min.y:.4g},{val.min.z:.4g}) max({val.max.x:.4g},{val.max.y:.4g},{val.max.z:.4g}) r={val.radius:.4g}"
        return str(val)
    if type(field) is io_m3.M3FieldInt:
        return f"{val} (0x{val:X})" if val > 9 else str(val)
    if type(field) is io_m3.M3FieldFloat:
        return f"{val:.6g}"
    if type(field) is io_m3.M3FieldBytes:
        return val.hex() if any(val) else f"zero[{len(val)}]"
    return str(val)


def diff_struct(sl_a, a, sl_b, b, label, only_diff=False):
    print(f"--- {label}: {a.desc.history.name}V{a.desc.version} vs V{b.desc.version} ---")
    names = list(a.desc.fields)
    for n in names:
        fa = a.desc.fields[n]
        fb = b.desc.fields.get(n)
        ra = field_repr(sl_a, a, fa)
        rb = field_repr(sl_b, b, fb) if fb else "(absent)"
        mark = " " if ra == rb else "*"
        if only_diff and ra == rb:
            continue
        print(f" {mark} {n:<34} A: {ra:<44} B: {rb}")
    for n in b.desc.fields:
        if n not in a.desc.fields:
            print(f" * {n:<34} A: (absent){'':<36} B: {field_repr(sl_b, b, b.desc.fields[n])}")


def get_ref(sl, data, field):
    ref = getattr(data, field)
    if ref.entries == 0:
        return None
    return sl[ref.index]


a_path, b_path = sys.argv[1], sys.argv[2]
sl_a, err_a = load(a_path)
sl_b, err_b = load(b_path)
print(f"A = {a_path}\n  load: {'OK, ' + str(len(sl_a)) + ' sections' if sl_a else 'FAILED ' + err_a}")
print(f"B = {b_path}\n  load: {'OK, ' + str(len(sl_b)) + ' sections' if sl_b else 'FAILED ' + err_b}")
if not sl_a or not sl_b:
    sys.exit(1)

validate(sl_a, "A")
validate(sl_b, "B")

ma, mb = sl_a.model, sl_b.model
diff_struct(sl_a, ma, sl_b, mb, "MODL")

# drill into structure-bearing chains
for side, sl, m in (("A", sl_a, ma), ("B", sl_b, mb)):
    print(f"\n=== {side} drilldown ===")
    div = get_ref(sl, m, "divisions")
    if div:
        d = div[0]
        print(f" DIV_ V{d.desc.version}: faces {refstr(sl, d.faces)}, regions {refstr(sl, d.regions)}, batches {refstr(sl, d.batches)}, msec {refstr(sl, d.msec)}")
        regn = get_ref(sl, d, "regions")
        if regn:
            for i, r in enumerate(regn.content[:3]):
                fields = {n: field_repr(sl, r, r.desc.fields[n]) for n in r.desc.fields}
                print(f"  REGN[{i}] V{r.desc.version}: {fields}")
        bat = get_ref(sl, d, "batches")
        if bat:
            for i, bt in enumerate(bat.content[:3]):
                fields = {n: field_repr(sl, bt, bt.desc.fields[n]) for n in bt.desc.fields}
                print(f"  BAT_[{i}] V{bt.desc.version}: {fields}")
        msec = get_ref(sl, d, "msec")
        if msec:
            for i, ms in enumerate(msec.content[:1]):
                fields = {n: field_repr(sl, ms, ms.desc.fields[n]) for n in ms.desc.fields}
                print(f"  MSEC[{i}] V{ms.desc.version}: {fields}")
    vflags = m.vertex_flags
    vsec = get_ref(sl, m, "vertices")
    if vsec:
        vdesc = io_m3.M3StructureDescription.get_vertex_description(vflags)
        n = len(vsec.content)
        print(f" vertices: {n} bytes, vertex_flags=0x{vflags:X} -> stride {vdesc.size}, divisible: {n % vdesc.size == 0} ({n // vdesc.size} verts)")
    bones = get_ref(sl, m, "bones")
    if bones:
        for i, bn in enumerate(bones.content[:2]):
            fields = {n: field_repr(sl, bn, bn.desc.fields[n]) for n in bn.desc.fields}
            print(f"  BONE[{i}] V{bn.desc.version}: {fields}")
    bl = get_ref(sl, m, "bone_lookup")
    print(f" bone_lookup: {'x' + str(len(bl.content)) if bl else '(null)'}")
    matref = get_ref(sl, m, "material_references")
    if matref:
        for i, mr in enumerate(matref.content[:4]):
            print(f"  MATM[{i}]: type={mr.type} index={mr.material_index}")
    mats = get_ref(sl, m, "materials_standard")
    if mats:
        mt = mats[0]
        fields = {n: field_repr(sl, mt, mt.desc.fields[n]) for n in mt.desc.fields}
        print(f"  MAT_[0] V{mt.desc.version}: {fields}")
    seqs = get_ref(sl, m, "sequences")
    if seqs:
        sq = seqs[0]
        fields = {n: field_repr(sl, sq, sq.desc.fields[n]) for n in sq.desc.fields}
        print(f"  SEQS[0] V{sq.desc.version}: {fields}")
    stc = get_ref(sl, m, "sequence_transformation_collections")
    if stc:
        st = stc[0]
        fields = {n: field_repr(sl, st, st.desc.fields[n]) for n in st.desc.fields}
        print(f"  STC_[0] V{st.desc.version}: {fields}")
    sts = get_ref(sl, m, "sts")
    if sts:
        st = sts[0]
        fields = {n: field_repr(sl, st, st.desc.fields[n]) for n in st.desc.fields}
        print(f"  STS_[0] V{st.desc.version}: {fields}")
