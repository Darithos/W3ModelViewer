"""Decode a DDS's alpha channel (BC1/BC2/BC3 or uncompressed BGRA32) and print a histogram
plus a coarse ASCII map so you can see *where* the transparency is."""
import sys, struct, os


def dds_alpha(path):
    d = open(path, "rb").read()
    assert d[:4] == b"DDS ", "not DDS"
    h, w = struct.unpack_from("<II", d, 12)
    pf_flags = struct.unpack_from("<I", d, 80)[0]
    fourcc = d[84:88]
    off = 128
    if fourcc == b"DX10":
        off = 148
    a = bytearray(w * h)
    if pf_flags & 0x4:  # compressed
        if fourcc == b"DXT1":
            return w, h, None, "DXT1 (1-bit alpha)"
        if fourcc not in (b"DXT3", b"DXT5"):
            return w, h, None, fourcc.decode("ascii", "replace")
        bw, bh = (w + 3) // 4, (h + 3) // 4
        for by in range(bh):
            for bx in range(bw):
                blk = off + (by * bw + bx) * 16
                if fourcc == b"DXT5":
                    a0, a1 = d[blk], d[blk + 1]
                    bits = int.from_bytes(d[blk + 2:blk + 8], "little")
                    if a0 > a1:
                        tbl = [a0, a1] + [((7 - i) * a0 + (i + 1) * a1) // 7 for i in range(6)]
                    else:
                        tbl = [a0, a1] + [((5 - i) * a0 + (i + 1) * a1) // 5 for i in range(4)] + [0, 255]
                    for py in range(4):
                        for px in range(4):
                            x, y = bx * 4 + px, by * 4 + py
                            if x < w and y < h:
                                a[y * w + x] = tbl[(bits >> (3 * (py * 4 + px))) & 7]
                else:  # DXT3, 4-bit explicit
                    for py in range(4):
                        for px in range(4):
                            x, y = bx * 4 + px, by * 4 + py
                            if x < w and y < h:
                                n = d[blk + py * 2 + px // 2]
                                v = (n & 0xF) if px % 2 == 0 else (n >> 4)
                                a[y * w + x] = v * 17
        return w, h, a, fourcc.decode()
    for i in range(w * h):
        a[i] = d[off + i * 4 + 3]
    return w, h, a, "BGRA32"


def _cli(argv):
  for p in argv:
      w, h, a, fmt = dds_alpha(p)
      print(f"\n=== {os.path.basename(p)}  {w}x{h}  {fmt} ===")
      if a is None:
          print("  (alpha not decodable in this format)")
          continue
      n = w * h
      zero = sum(1 for v in a if v == 0)
      lo = sum(1 for v in a if v < 32)
      mid = sum(1 for v in a if 32 <= v < 224)
      full = sum(1 for v in a if v >= 250)
      print(f"  a==0 {100*zero/n:5.1f}%   a<32 {100*lo/n:5.1f}%   32<=a<224 {100*mid/n:5.1f}%   a>=250 {100*full/n:5.1f}%")
      # coarse map: 64 cols
      cols = 64
      rows = max(1, int(cols * h / w))
      print("  map (' '=opaque  .=<10% cut  :=<50%  #=>50% cut):")
      for r in range(rows):
          line = []
          for c in range(cols):
              x0, x1 = c * w // cols, max(c * w // cols + 1, (c + 1) * w // cols)
              y0, y1 = r * h // rows, max(r * h // rows + 1, (r + 1) * h // rows)
              cnt = tot = 0
              for y in range(y0, y1, max(1, (y1 - y0) // 8)):
                  for x in range(x0, x1, max(1, (x1 - x0) // 8)):
                      tot += 1
                      if a[y * w + x] < 128:
                          cnt += 1
              f = cnt / max(1, tot)
              line.append(" " if f < 0.02 else "." if f < 0.10 else ":" if f < 0.5 else "#")
          print("   |" + "".join(line) + "|")

if __name__ == "__main__":
    _cli(sys.argv[1:])
