"""Scan .m3 files for PAR_ emit_rate / emit_count animation: header flags, STC binding, key shapes.

    python ratescan.py <glob-or-files...> [--examples N] [--verbose]
"""
import sys, os, glob, importlib.util, collections, traceback

ADDON = r"C:\Users\Darithos\AppData\Roaming\Blender Foundation\Blender\3.3\scripts\addons\m3studio-main"
spec = importlib.util.spec_from_file_location("io_m3", os.path.join(ADDON, "io_m3.py"))
io_m3 = importlib.util.module_from_spec(spec)
sys.modules["io_m3"] = io_m3
spec.loader.exec_module(io_m3)

TYPE_NAMES = {0: "sdev", 1: "sd2v", 2: "sd3v", 3: "sd4q", 4: "sdcc", 5: "sdr3", 6: "sdu8", 7: "sds6",
              8: "sdu6", 9: "sds3", 10: "sdu3", 11: "sdfg", 12: "sdmb"}


def ref(sl, r):
    if r.entries == 0:
        return None
    return sl[r.index]


def content(sl, r):
    s = ref(sl, r)
    return list(s.content) if s is not None else []


def scan(path, verbose, examples, stats):
    sl = io_m3.M3SectionList.load(path)
    m = sl.model
    pars = content(sl, m.particle_systems) if hasattr(m, "particle_systems") else []
    if not pars:
        return
    seqs = content(sl, m.sequences)
    stcs = content(sl, m.sequence_transformation_collections)
    stgs = content(sl, m.sequence_transformation_groups)
    stss = content(sl, m.sts)
    seq_names = []
    for s in seqs:
        n = ref(sl, s.name)
        seq_names.append(bytes(n.content).rstrip(b"\0").decode("ascii", "replace") if n else "?")
    # anim id -> list of (stc name, type, frames, keys)
    tracks = collections.defaultdict(list)
    for stc in stcs:
        nsec = ref(sl, stc.name)
        stcname = bytes(nsec.content).rstrip(b"\0").decode("ascii", "replace") if nsec else "?"
        ids = content(sl, stc.anim_ids)
        refs = content(sl, stc.anim_refs)
        sts_ids = set()
        if stc.sts_index < len(stss):
            sts_ids = set(content(sl, stss[stc.sts_index].anim_ids))
        for aid, aref in zip(ids, refs):
            t, idx = aref >> 16, aref & 0xFFFF
            sec = ref(sl, getattr(stc, TYPE_NAMES.get(t, "sdev")))
            if sec is None or idx >= len(sec.content):
                continue
            sd = sec.content[idx]
            frames = content(sl, sd.frames)
            keys = content(sl, sd.keys)
            tracks[aid].append(dict(stc=stcname, concurrent=stc.concurrent, priority=stc.priority,
                                    type=TYPE_NAMES.get(t, t), sdflags=sd.flags, fend=sd.fend,
                                    frames=frames, keys=keys, in_sts=aid in sts_ids))
    for pi, p in enumerate(pars):
        for fname in ("emit_rate", "emit_count"):
            ar = getattr(p, fname)
            h = ar.header
            key = (fname, h.interpolation, h.flags)
            stats["headers"][key] += 1
            tr = tracks.get(h.id, [])
            if tr:
                stats["animated"][fname] += 1
                stats["animated_flags"][(fname, h.flags)] += 1
                # classify shapes
                for t in tr:
                    ks = [k if not hasattr(k, "x") else k.x for k in t["keys"]]
                    if fname == "emit_count":
                        ks = [int(k) for k in ks]
                    shape = "const" if len(set(ks)) <= 1 else ("burst" if ks[0] == 0 and ks[-1] == 0 else "varies")
                    stats["shapes"][(fname, shape)] += 1
                    stats["sdflags"][(fname, t["sdflags"])] += 1
                    stats["in_sts"][(fname, t["in_sts"])] += 1
                    stats["interp"][(fname, h.interpolation)] += 1
                    if shape == "burst" and len(examples[fname]) < examples["max"]:
                        examples[fname].append((os.path.basename(path), pi, ar.default, h.interpolation, h.flags, h.id, t, seq_names))
            if verbose and tr:
                print(f"{os.path.basename(path)} PAR_[{pi}] {fname}: default={ar.default} interp={h.interpolation} flags={h.flags} id=0x{h.id:X}")
                for t in tr:
                    print(f"   STC {t['stc']!r} conc={t['concurrent']} prio={t['priority']} {t['type']} sdflags={t['sdflags']} fend={t['fend']} in_sts={t['in_sts']}")
                    print(f"      frames={t['frames'][:16]}")
                    print(f"      keys  ={[round(k, 3) if isinstance(k, float) else k for k in t['keys'][:16]]}")


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    verbose = "--verbose" in sys.argv
    nex = 8
    for a in sys.argv[1:]:
        if a.startswith("--examples="):
            nex = int(a.split("=")[1])
    files = []
    for a in args:
        if os.path.isdir(a):
            for root, _, fs in os.walk(a):
                files += [os.path.join(root, f) for f in fs if f.lower().endswith(".m3")]
        else:
            files += glob.glob(a) if any(c in a for c in "*?") else [a]
    stats = collections.defaultdict(collections.Counter)
    examples = {"emit_rate": [], "emit_count": [], "max": nex}
    n = 0
    for f in files:
        try:
            scan(f, verbose, examples, stats)
            n += 1
        except Exception as e:
            print(f"!! {os.path.basename(f)}: {type(e).__name__}: {e}")
    print(f"\n==== {n} files ====")
    for k in ("headers", "animated", "animated_flags", "shapes", "sdflags", "in_sts", "interp"):
        print(k)
        for kk, v in sorted(stats[k].items(), key=lambda kv: -kv[1]):
            print(f"   {kk}: {v}")
    for fname in ("emit_rate", "emit_count"):
        print(f"\n==== burst examples: {fname} ====")
        for (fn, pi, default, interp, flags, aid, t, seq_names) in examples[fname]:
            print(f"{fn} PAR_[{pi}] default={default} interp={interp} flags={flags} id=0x{aid:X} seqs={seq_names[:6]}")
            print(f"   STC {t['stc']!r} conc={t['concurrent']} prio={t['priority']} {t['type']} sdflags={t['sdflags']} fend={t['fend']} in_sts={t['in_sts']}")
            print(f"      frames={t['frames'][:20]}")
            print(f"      keys  ={[round(k, 3) if isinstance(k, float) else k for k in t['keys'][:20]]}")


main()
