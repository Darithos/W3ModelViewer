"""How Heroes of the Storm's hero materials build their metal: the environment layer and its mask.

    python envicensus.py [glob] [max-files]

For every standard material: which layers carry a bitmap; for the envi layer its texture, UV
source, channels, multiply/brightness and fresnel; what masks it (the spec map, the diffuse,
a map of its own) and through which channel; and the MAT_ fields that go with it.
"""
import sys, os, glob, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from m3audit import io_m3, res, scalars

UV = ['UV0', 'UV1', 'REFCUBE', 'REFSPHERE', 'PLANARLZ', 'PLANARWZ', 'PARTICLE', 'CUBE', 'SPHERE', 'UV2', 'UV3']
CH = ['RGB', 'ARGB', 'A', 'R', 'G', 'B']
SLOTS = ['diff', 'decal', 'spec', 'gloss', 'emis1', 'emis2', 'envi', 'envi_mask', 'alpha1', 'alpha2', 'norm', 'height', 'light', 'ao']


def bitmap(sl, lay):
    bm = res(sl, getattr(lay, 'color_bitmap', None))
    return bytes(bm).decode('ascii', 'replace').rstrip('\x00') if bm else ''


def main():
    pattern = sys.argv[1] if len(sys.argv) > 1 else r'C:\games\StarCraft II\Mods\HotS.SC2Mod\assets\units\heroes\**\storm_hero_*.m3'
    limit = int(sys.argv[2]) if len(sys.argv) > 2 else 100000
    files = [f for f in sorted(glob.glob(pattern, recursive=True)) if 'ragdoll' not in f.lower()][:limit]
    c = collections.defaultdict(collections.Counter)
    mats_total = envi_total = 0
    examples = []
    for path in files:
        try:
            sl = io_m3.M3SectionList.load(path)
            model = res(sl, sl[0][0].model)[0]
            mats = res(sl, model.materials_standard)
        except Exception as e:
            c['errors'][type(e).__name__] += 1
            continue
        for m in mats:
            mats_total += 1
            lay = {}
            for s in SLOTS:
                L = res(sl, getattr(m, 'layer_' + s, None))
                if L and bitmap(sl, L[0]):
                    lay[s] = L[0]
            c['layer sets'][' '.join(s for s in SLOTS if s in lay)] += 1
            ms = scalars(m)
            if 'envi' not in lay:
                c['no envi: specularity / hdr_spec / gloss'][(ms.get('specularity'), ms.get('hdr_spec'), 'gloss' in lay)] += 1
                continue
            envi_total += 1
            e = lay['envi']; es = scalars(e)
            name = os.path.basename(bitmap(sl, e)).lower()
            c['envi texture'][name] += 1
            c['envi uv_source'][UV[int(es['uv_source'])] if int(es['uv_source']) < len(UV) else es['uv_source']] += 1
            c['envi channels'][CH[int(es['color_channels'])]] += 1
            c['envi multiply x brightness'][(es.get('color_multiply'), es.get('color_brightness'))] += 1
            c['envi fresnel type/exp/min'][(es.get('fresnel_type'), es.get('fresnel_exponent'), es.get('fresnel_min'))] += 1
            c['envi flags'][hex(int(es['flags']))] += 1
            spec = bitmap(sl, lay['spec']) if 'spec' in lay else None
            diff = bitmap(sl, lay['diff']) if 'diff' in lay else None
            if 'envi_mask' in lay:
                mk = lay['envi_mask']; mp = bitmap(sl, mk)
                src = 'spec' if mp == spec else 'diff' if mp == diff else 'own map'
                c['envi_mask source.channel'][f"{src}.{CH[int(scalars(mk)['color_channels'])]}"] += 1
            else:
                c['envi_mask source.channel']['none'] += 1
            if 'gloss' in lay:
                gp = bitmap(sl, lay['gloss'])
                c['gloss source.channel (with envi)'][f"{'spec' if gp == spec else 'own map'}.{CH[int(scalars(lay['gloss'])['color_channels'])]}"] += 1
            c['MAT_ specularity / hdr_spec / gloss / sim (with envi)'][(ms.get('specularity'), ms.get('hdr_spec'), 'gloss' in lay, bool(int(ms['flags']) & 0x800))] += 1
            c['MAT_ hdr_envi const/diff/spec'][(ms.get('hdr_envi_const'), ms.get('hdr_envi_diff'), ms.get('hdr_envi_spec'))] += 1
            if len(examples) < 12 and 'muradin' not in path.lower() and ('arthas' in path.lower() or 'uther' in path.lower() or 'johanna' in path.lower() or 'varian' in path.lower() or 'leoric' in path.lower()):
                examples.append(f"{os.path.basename(path)}: envi={name} x{es.get('color_multiply')} mask={os.path.basename(bitmap(sl, lay['envi_mask'])) if 'envi_mask' in lay else '-'} spec={ms.get('specularity')} hdr={ms.get('hdr_spec')}")
    print(f"{len(files)} files, {mats_total} standard materials, {envi_total} with an envi layer ({100 * envi_total / max(1, mats_total):.0f}%)")
    for key, cnt in c.items():
        print(f"\n{key}:")
        for v, n in cnt.most_common(12):
            print(f"  {n:6d}  {v}")
    print("\nexamples:"); [print("  " + x) for x in examples]


if __name__ == '__main__':
    main()
