"""Rank every field of OUR surface materials (MAT_ and its diff/spec/norm layers) against Blizzard's.

    python m3specaudit.py <ours.m3> [corpus-root] [max-files]

m3audit.py does this for the materials particles and ribbons draw; this is the same census for
the opaque surfaces of units, to find why an exported spec map lights nothing under the sun.
Only Blizzard materials that carry a spec layer are counted.

Its answer (2026-09-30): nothing in the material. On the gloss calibration sphere only hdr_spec and
specularity stood out, both set on purpose; the sphere was wound inside out (MdxProbe --winding:
1.5% of its triangles counter-clockwise about their normals against the HD knight's 99.8%), so SC2
drew the inside of its far half, lit from behind.
"""
import sys, os, glob, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from m3audit import io_m3, res, scalars


def harvest(path):
    out = collections.defaultdict(list)
    sl = io_m3.M3SectionList.load(path)
    model = res(sl, sl[0][0].model)[0]
    for m in res(sl, model.materials_standard):
        if not res(sl, getattr(m, "layer_spec", None)):
            continue
        out["MAT_"].append(scalars(m))
        for lname in ("diff", "spec", "norm"):
            lay = res(sl, getattr(m, "layer_" + lname, None))
            if lay:
                out["LAYR/" + lname].append(scalars(lay[0]))
    return out


def main():
    ours_path = sys.argv[1]
    root = sys.argv[2] if len(sys.argv) > 2 else r"C:\games\StarCraft II\Mods\HotS.SC2Mod\assets\units"
    limit = int(sys.argv[3]) if len(sys.argv) > 3 else 300
    files = sorted(glob.glob(os.path.join(root, "**", "storm_*.m3"), recursive=True))[:limit]
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
    print(f"corpus: {used} of {len(files)} Blizzard models under {root}")
    ours = harvest(ours_path)
    print(f"ours: {os.path.basename(ours_path)}\n")
    for struct in sorted(ours):
        dist = corpus.get(struct, {})
        row = ours[struct][0]
        found = []
        for f, v in sorted(row.items()):
            c = dist.get(f)
            if not c:
                continue
            total = sum(c.values()); seen = c[v]
            if seen / total >= 0.02:
                continue
            found.append((seen / total, f, v, ", ".join(f"{k}x{n}" for k, n in c.most_common(3))))
        found.sort()
        print(f"=== {struct} (first of {len(ours[struct])}): {len(found)} outlier(s)")
        for share, f, v, top in found:
            print(f"  [{'NEVER' if share == 0 else f'{share:.1%}':>6}] {f:30} ours={v:<24} corpus: {top}")
        print()


if __name__ == "__main__":
    main()
