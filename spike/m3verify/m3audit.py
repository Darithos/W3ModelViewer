"""Flag every field where OUR .m3 is an outlier against Blizzard's own files.

    python m3audit.py <ours.m3> [corpus-root] [max-files]

Builds, over Blizzard's models, the distribution of every scalar field of the structures that
carry effects (PAR_, RIB_, and the MAT_/LAYR each one uses), then prints the fields where our
value is rare or never seen there, rarest first. Field-by-field eyeballing gives a dozen
candidate differences and no way to rank them; the corpus ranks them — the `additional_flags`
bug (we wrote 0, Blizzard never does) was found exactly this way.

Values are compared as printed: floats rounded to 4 significant figures, colours as RGBA,
vectors component-wise. Animated fields contribute their default.
"""
import sys, os, glob, importlib.util, collections

ADDON = r"C:\Users\Darithos\AppData\Roaming\Blender Foundation\Blender\3.3\scripts\addons\m3studio-main"
spec = importlib.util.spec_from_file_location("io_m3", os.path.join(ADDON, "io_m3.py"))
io_m3 = importlib.util.module_from_spec(spec)
sys.modules["io_m3"] = io_m3
spec.loader.exec_module(io_m3)


def res(sl, ref):
    if ref is None or getattr(ref, "entries", 0) == 0:
        return []
    return list(sl[ref.index])


def val(v):
    """One field as a comparable string, or None when it is not a scalar we can compare."""
    if hasattr(v, "default"):
        v = v.default
    if isinstance(v, bool):
        return str(v)
    if isinstance(v, int):
        return str(v)
    if isinstance(v, float):
        return f"{v:.4g}"
    if hasattr(v, "r") and hasattr(v, "a"):
        return f"rgba({v.r},{v.g},{v.b},{v.a})"
    if hasattr(v, "x") and hasattr(v, "y"):
        z = f",{v.z:.4g}" if hasattr(v, "z") else ""
        return f"({v.x:.4g},{v.y:.4g}{z})"
    return None


SKIP = {"desc", "name", "structure_description"}


def scalars(obj):
    out = {}
    for f in dir(obj):
        if f.startswith("_") or f in SKIP:
            continue
        try:
            v = getattr(obj, f)
        except Exception:
            continue
        if callable(v):
            continue
        s = val(v)
        if s is not None:
            out[f] = s
    return out


def harvest(path):
    """{structure: [ {field: value} ... ]} for the effect structures of one model."""
    out = collections.defaultdict(list)
    sl = io_m3.M3SectionList.load(path)
    model = res(sl, sl[0][0].model)[0]
    refs = res(sl, model.material_references)
    mats = res(sl, model.materials_standard)

    def material_of(inst, kind):
        mi = inst.material_reference_index
        if not (0 <= mi < len(refs)) or refs[mi].type != 1:
            return
        m = mats[refs[mi].material_index]
        out["MAT_/" + kind].append(scalars(m))
        for lname in ("diff", "emis1", "emis2", "alpha1"):
            lay = res(sl, getattr(m, "layer_" + lname, None))
            if lay:
                out[f"LAYR/{kind}/{lname}"].append(scalars(lay[0]))

    for inst in res(sl, model.particle_systems):
        out["PAR_"].append(scalars(inst))
        material_of(inst, "PAR_")
    for inst in res(sl, model.ribbons):
        out["RIB_"].append(scalars(inst))
        material_of(inst, "RIB_")
    return out


def main():
    ours_path = sys.argv[1]
    root = sys.argv[2] if len(sys.argv) > 2 else r"C:\Games\StarCraft II"
    limit = int(sys.argv[3]) if len(sys.argv) > 3 else 500

    files = [p for p in glob.glob(os.path.join(root, "**", "*.m3"), recursive=True)
             if os.path.abspath(p) != os.path.abspath(ours_path)]
    # Models whose whole job is effects are the closest template for ours.
    files.sort(key=lambda p: (("missile" not in p.lower()), ("effect" not in p.lower() and "_fx_" not in p.lower()), p))
    files = files[:limit]

    corpus = collections.defaultdict(lambda: collections.defaultdict(collections.Counter))
    used = 0
    for p in files:
        try:
            for struct, rows in harvest(p).items():
                for row in rows:
                    for f, v in row.items():
                        corpus[struct][f][v] += 1
            used += 1
        except Exception:
            pass

    ours = harvest(ours_path)
    print(f"corpus: {used} of {len(files)} Blizzard models parsed under {root}")
    print(f"ours:   {os.path.basename(ours_path)}\n")

    for struct in sorted(ours):
        dist = corpus.get(struct)
        if not dist:
            print(f"=== {struct}: no Blizzard counterpart in the corpus\n")
            continue
        for idx, row in enumerate(ours[struct]):
            findings = []
            for f, v in sorted(row.items()):
                c = dist.get(f)
                if not c:
                    continue
                total = sum(c.values())
                seen = c[v]
                share = seen / total
                if share >= 0.02:            # ordinary
                    continue
                top = ", ".join(f"{k}×{n}" for k, n in c.most_common(3))
                findings.append((share, f, v, seen, total, top))
            findings.sort()
            head = f"=== {struct} #{idx}"
            if not findings:
                print(f"{head}: every field is ordinary in the corpus\n")
                continue
            print(f"{head}: {len(findings)} outlier field(s), rarest first")
            for share, f, v, seen, total, top in findings:
                tag = "NEVER" if seen == 0 else f"{share:.1%}"
                print(f"  [{tag:>6}] {f:30} ours={v:<22} corpus: {top}")
            print()


if __name__ == "__main__":
    main()
