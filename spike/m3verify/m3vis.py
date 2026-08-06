"""Raw-parse every LAYR in an .m3 and report the colour_value default alpha (the layer's
rest visibility) plus color_channels. A geoset whose diffuse layer defaults to alpha 0 is
invisible in any sequence that carries no visibility key — that renders as a missing geoset."""
import sys, os, struct

LAYR = 464
CHANNELS = {0: "RGB", 1: "ARGB", 2: "A", 3: "R", 4: "G", 5: "B"}


def sections(d):
    off = struct.unpack_from("<I", d, 4)[0]
    n = struct.unpack_from("<I", d, 8)[0]
    out = []
    for i in range(n):
        e = off + i * 16
        tag = struct.unpack_from("<I", d, e)[0]
        name = bytes([(tag >> 24) & 255, (tag >> 16) & 255, (tag >> 8) & 255, tag & 255]).decode("latin-1")
        out.append((name, struct.unpack_from("<I", d, e + 4)[0], struct.unpack_from("<I", d, e + 8)[0]))
    return out


def main(p):
    d = open(p, "rb").read()
    secs = sections(d)
    print(f"\n=== {os.path.basename(p)} ===")
    print(f"{'bitmap':<52} {'restAlpha':>9} {'channels':>9}")
    hidden = []
    for si, (name, off, cnt) in enumerate(secs):
        if name != "LAYR":
            continue
        for k in range(cnt):
            e = off + k * LAYR
            entries = struct.unpack_from("<I", d, e + 4)[0]
            idx = struct.unpack_from("<I", d, e + 8)[0]
            path = ""
            if entries and idx < len(secs) and secs[idx][0] == "CHAR":
                so, sc = secs[idx][1], secs[idx][2]
                path = d[so:so + min(entries, sc)].decode("utf-8", "replace").rstrip("\0")
            a = d[e + 27]
            ch = struct.unpack_from("<I", d, e + 44)[0]
            if not path:
                continue
            print(f"{path:<52} {a:>9} {CHANNELS.get(ch, ch):>9}")
            if a == 0 and "_diff" in path:
                hidden.append(path)
    if hidden:
        print(f"\n  !! {len(hidden)} diffuse layer(s) default to INVISIBLE (restAlpha 0):")
        for h in hidden:
            print(f"     {h}")


for p in sys.argv[1:]:
    main(p)
