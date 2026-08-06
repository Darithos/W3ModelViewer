"""Internal consistency: for each bone, world rest position (chain of BONE.location defaults,
rotations applied) must equal -IREF.translation (IREF = inverse bind). Reports worst mismatch."""
import sys, importlib.util

ADDON = r"C:\Users\Darithos\AppData\Roaming\Blender Foundation\Blender\3.3\scripts\addons\m3studio-main"
spec = importlib.util.spec_from_file_location("io_m3", ADDON + r"\io_m3.py")
io_m3 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(io_m3)


def quat_mul(a, b):
    aw, ax, ay, az = a
    bw, bx, by, bz = b
    return (aw * bw - ax * bx - ay * by - az * bz,
            aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw)


def quat_rot(q, v):
    w, x, y, z = q
    qv = (x, y, z)
    uv = (qv[1] * v[2] - qv[2] * v[1], qv[2] * v[0] - qv[0] * v[2], qv[0] * v[1] - qv[1] * v[0])
    uuv = (qv[1] * uv[2] - qv[2] * uv[1], qv[2] * uv[0] - qv[0] * uv[2], qv[0] * uv[1] - qv[1] * uv[0])
    return tuple(v[i] + 2 * (w * uv[i] + uuv[i]) for i in range(3))


for path in sys.argv[1:]:
    sl = io_m3.M3SectionList.load(path)
    m = sl.model
    bones = sl[m.bones.index]
    irefs = sl[m.bone_rests.index]
    bad_order = [i for i, bn in enumerate(bones.content) if bn.parent >= i]
    print(f"{path}\n  parent-first: {'OK' if not bad_order else f'VIOLATED at {bad_order[:8]}'}")
    world_pos = [None] * len(bones.content)
    world_rot = [None] * len(bones.content)
    worst = (0.0, -1)
    for i, bn in enumerate(bones.content):
        loc = (bn.location.default.x, bn.location.default.y, bn.location.default.z)
        rot = (bn.rotation.default.w, bn.rotation.default.x, bn.rotation.default.y, bn.rotation.default.z)
        p = bn.parent
        if p < 0:
            world_pos[i] = loc
            world_rot[i] = rot
        else:
            world_pos[i] = tuple(world_pos[p][k] + quat_rot(world_rot[p], loc)[k] for k in range(3))
            world_rot[i] = quat_mul(world_rot[p], rot)
        iref = irefs.content[i]
        # column-major Matrix44: w = translation column; bind = inverse rest → translation of
        # inverse for pure T(p)·R is -(R⁻¹p) … for pure translation it is just -p.
        it = (iref.matrix.w.x, iref.matrix.w.y, iref.matrix.w.z)
        # rest world position transformed by IREF must land at origin:
        # IREF as columns: out = M·v; basis vectors in x/y/z columns, translation w.
        cols = iref.matrix
        v = world_pos[i]
        out = tuple(cols.x.__getattribute__(a) * v[0] + cols.y.__getattribute__(a) * v[1]
                    + cols.z.__getattribute__(a) * v[2] + cols.w.__getattribute__(a) for a in ("x", "y", "z"))
        err = max(abs(c) for c in out)
        if err > worst[0]:
            worst = (err, i)
    print(f"  bones={len(bones.content)}  worst |IREF · restWorld| = {worst[0]:.5f} at bone {worst[1]}")
    if worst[1] >= 0:
        i = worst[1]
        print(f"  bone[{i}] restWorld={tuple(round(c, 3) for c in world_pos[i])}  IREF.w={tuple(round(getattr(irefs.content[i].matrix.w, a), 3) for a in ('x', 'y', 'z'))}")
