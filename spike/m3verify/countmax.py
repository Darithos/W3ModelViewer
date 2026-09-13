"""For every PAR_ with an emit_count burst: peak count, hold length, emit_max, lifespan, emit_rate.
If emit_count were per-frame, emit_max would need to exceed peak * frames_held; if it is an
edge-triggered burst, emit_max ~ peak suffices."""
import sys, os, importlib.util, collections, statistics
ADDON = r"C:\Users\Darithos\AppData\Roaming\Blender Foundation\Blender\3.3\scripts\addons\m3studio-main"
spec = importlib.util.spec_from_file_location("io_m3", os.path.join(ADDON, "io_m3.py"))
io_m3 = importlib.util.module_from_spec(spec); sys.modules["io_m3"] = io_m3; spec.loader.exec_module(io_m3)

def ref(sl, r): return sl[r.index] if r.entries else None
def content(sl, r):
    s = ref(sl, r); return list(s.content) if s is not None else []

rows = []
for root, _, fs in os.walk(sys.argv[1]):
    for f in fs:
        if not f.lower().endswith(".m3"): continue
        try:
            sl = io_m3.M3SectionList.load(os.path.join(root, f)); m = sl.model
        except Exception: continue
        pars = content(sl, m.particle_systems)
        if not pars: continue
        stcs = content(sl, m.sequence_transformation_collections)
        tracks = collections.defaultdict(list)
        for stc in stcs:
            for aid, aref in zip(content(sl, stc.anim_ids), content(sl, stc.anim_refs)):
                t, idx = aref >> 16, aref & 0xFFFF
                if t != 7: continue
                sec = ref(sl, stc.sds6)
                if sec is None or idx >= len(sec.content): continue
                sd = sec.content[idx]
                tracks[aid].append((content(sl, sd.frames), content(sl, sd.keys)))
        for p in pars:
            for frames, keys in tracks.get(p.emit_count.header.id, []):
                ks = [int(k) for k in keys]
                peak = max(ks)
                if peak <= 0 or ks[0] != 0: continue
                # hold: ms from first non-zero key to the next zero key
                i = next(i for i, k in enumerate(ks) if k > 0)
                j = next((j for j in range(i + 1, len(ks)) if ks[j] == 0), None)
                hold = frames[j] - frames[i] if j is not None else -1
                rows.append((f, peak, hold, p.emit_max, p.lifespan.default, p.emit_rate.default, len([k for k in ks if k > 0])))

print(f"{len(rows)} emit_count bursts")
holds = [r[2] for r in rows if r[2] >= 0]
print("hold ms: median", statistics.median(holds), "min", min(holds), "max", max(holds))
ratio = [r[3] / r[1] for r in rows if r[3] > 0]
print("emit_max / peak: median", round(statistics.median(ratio), 2), "p10", round(sorted(ratio)[len(ratio)//10], 2), "p90", round(sorted(ratio)[len(ratio)*9//10], 2))
below = sum(1 for r in rows if 0 < r[3] < r[1]); print("emit_max < peak:", below, " emit_max == peak:", sum(1 for r in rows if r[3] == r[1]), " emit_max==0:", sum(1 for r in rows if r[3] == 0))
print("emit_rate default 0 alongside:", sum(1 for r in rows if r[5] == 0), "/", len(rows))
c = collections.Counter(r[2] for r in rows); print("hold histogram (top):", c.most_common(8))
for r in rows[:12]: print("  ", r)
