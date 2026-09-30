"""Read StarCraft II's specular law off a Terrain-view capture of the MdxProbe gloss spheres.

    python glossfit.py capture.png 2.2[,1.0]

The capture is `MdxProbe --glosscal <map> gcE --fine 0.12` placed as two rows of eight at
x = 8, 10.4 .. 24.8 (y 16: z k l m n o p x; y 12.6: z q r s t u v w), rotation 0, in the normaltest
sandbox under Agria light, default camera, the Terrain window maximised on the 2560x1440 screen.
`rows` holds each sphere's centre in the half-size preview of the viewport; the disc is refitted.

Per sphere: display = 255 * (D(N) + k * max(0, N.H)^n) ^ (1/gamma), with D an order-2 spherical-
harmonic diffuse fitted alongside (so perspective and fill lights need no model) and H free. At
gamma 2.2 the no-gloss ruler reads back n = 17.9 .. 488 for specularity 16 .. 512 at one k; at 1.0
the fit fails with four times the residual, so the display is plain 2.2 gamma at these levels.
"""
import sys, numpy as np
from PIL import Image
from scipy import ndimage, optimize

im = np.asarray(Image.open(sys.argv[1]).convert('RGB')).astype(np.float64)
lum = im.mean(axis=2)
rows = {'z1': (230, 290), 'k': (320, 290), 'l': (411, 290), 'm': (502, 290), 'n': (592, 290), 'o': (683, 290), 'p': (774, 290), 'x': (865, 290),
        'z2': (211, 402), 'q': (307, 402), 'r': (404, 402), 's': (500, 402), 't': (596, 402), 'u': (692, 402), 'v': (788, 402), 'w': (885, 402)}
lab = {'k': 's16', 'l': 's32', 'm': 's64', 'n': 's128', 'o': 's256', 'p': 's512',
       'q': 'g.125', 'r': 'g.25', 's': 'g.375', 't': 'g.5', 'u': 'g.625', 'v': 'g.75', 'w': 'g.875', 'x': 'g1.0', 'z1': 'diffuse', 'z2': 'diffuse'}
S = 100

def disc(v):
    cx, cy = 420 + 2 * rows[v][0], 100 + 2 * rows[v][1]
    L = lum[cy - S:cy + S, cx - S:cx + S]
    smooth = ndimage.generic_filter(L, np.std, size=5) < 2.5
    lbl, _ = ndimage.label(smooth)
    m = ndimage.binary_fill_holes(ndimage.binary_closing(lbl == lbl[S, S], iterations=3))
    edge = m & ~ndimage.binary_erosion(m)
    ey, ex = np.nonzero(edge)
    ys, xs = np.nonzero(m)
    p = optimize.least_squares(lambda p: np.hypot(ex - p[0], ey - p[1]) - p[2],
                               [xs.mean(), ys.mean(), np.sqrt(m.sum() / np.pi)], loss='soft_l1', f_scale=2).x
    return cx - S + p[0], cy - S + p[1], p[2]

def sh(N):
    x, y, z = N[:, 0], N[:, 1], N[:, 2]
    return np.stack([np.ones_like(x), x, y, z, x * y, y * z, x * z, x * x - y * y, 3 * z * z - 1], 1)

geo = {v: disc(v) for v in rows}
print("discs:", " ".join(f"{v}=({g[0]:.0f},{g[1]:.0f},R{g[2]:.1f})" for v, g in geo.items()))
for gamma in [float(g) for g in sys.argv[2].split(',')]:
    print(f"--- gamma {gamma}")
    for v in rows:
        cx, cy, R = geo[v]
        x0, y0 = int(cx - R), int(cy - R)
        L = lum[y0:int(cy + R) + 1, x0:int(cx + R) + 1]
        yy, xx = np.mgrid[0:L.shape[0], 0:L.shape[1]]
        nx = (xx + x0 - cx) / R; ny = -(yy + y0 - cy) / R; r2 = nx * nx + ny * ny
        inside = r2 < 0.9 ** 2
        N = np.stack([nx, ny, np.sqrt(np.clip(1 - r2, 0, 1))], -1)[inside]
        obs = (L[inside] / 255.0) ** gamma
        B = sh(N)
        if v.startswith('z'):
            c, *_ = np.linalg.lstsq(B, obs, rcond=None)
            rms = np.sqrt(np.mean((B @ c - obs) ** 2))
            print(f"{v}: diffuse only, SH rms {rms:.4f}, peak display {L[inside].max():.0f}")
            continue
        def model(q):
            k, ln_n, th, ph = q[:4]
            H = np.array([np.sin(th) * np.cos(ph), np.sin(th) * np.sin(ph), np.cos(th)])
            return B @ q[4:] + k * np.clip(N @ H, 0, 1) ** np.exp(ln_n)
        # Start H at the pixel that stands most above a pure-SH fit.
        c0, *_ = np.linalg.lstsq(B, obs, rcond=None)
        resid = obs - B @ c0
        h0 = N[np.argmax(ndimage.uniform_filter1d(resid, 1))]
        th0, ph0 = np.arccos(h0[2]), np.arctan2(h0[1], h0[0])
        best = None
        for n0 in (8, 32, 128, 512, 2048):
            r = optimize.least_squares(lambda q: model(q) - obs, np.r_[max(resid.max(), 1e-3), np.log(n0), th0, ph0, c0],
                                       bounds=(np.r_[0, np.log(1), 0, -np.pi, [-5] * 9], np.r_[10, np.log(20000), 1.5, np.pi, [5] * 9]))
            if best is None or r.cost < best.cost: best = r
        k, ln_n, th, ph = best.x[:4]
        rms = np.sqrt(np.mean(best.fun ** 2))
        print(f"{v}: {lab[v]:6s} n={np.exp(ln_n):7.1f}  k={k:.4f}  H=({np.sin(th)*np.cos(ph):+.2f},{np.sin(th)*np.sin(ph):+.2f},{np.cos(th):.2f})  rms={rms:.4f}  peak display {L[inside].max():.0f}")
