# IsoRim Waylith - Combat Extended Pack: non-generic ammo models (batch "ammo2", 2026-10-30, Blender preview round 1), and the
# ammo of "The Generic Ammo Experience for Combat Extended" (Workshop 3284460811; batch "gae", 2026-11-13, looks G1..G5).
# CE ammo without a generic counterpart (rockets, recoilless, advanced cells, medieval, neolithic, flamethrower fuel,
# flares, nails, toxic grenades): one look per CE texture folder, bound by texture path (IsoRim Api 16 graphicPath).
# Analysis: scratchpad ce_ammo2/ce_ammo2_analysis.md (shapes N1..N18 + the Tox colour set on the generic M5 grenade).
# Conventions copied from tools/make_ce_ammo.py (generic batch, WT4): geometry helpers and writer copied unchanged, one shape
# per model and stack sprite (a = 1, b = more, c = full stack), one colour set per CE texture folder with its own .mtl
# (same material names) and its own copy of the geometry: ce_ammo2_<model>_<set>_<a|b|c>.obj + ce_ammo2_<model>_<set>.mtl.
# Colours sampled from CE's textures, lifted 1.08x; Metal / Wood swatches on crates, cases, frames and wooden boxes.
# Shapes follow CE's item art read as a TOP view (1 cell unless CE sets a drawSize: Tomahawk 4 -> 3.9 cells long, drawn
# over the neighbouring cells as CE draws it; boulder 0.8; the 50mm rocket's drawSize 0.7 is the ModelDef scale, not here).
# Game axes while building: x east, y up, z north, one cell per unit, origin = cell centre, nothing below y = 0. The OBJ
# stores z flipped (file z = -game z); every part is its own closed shell, triangulated.
# GAE looks (key GAE, section "GAE looks" below): written as ce_ammo_gae_<model>_<set>_<a|b|c>.obj + .mtl, bound by thingDef
# to that mod's own defs (tools/ce_ammo_gae_binds.json -> Defs/CE_Ammo_GAE.xml). For them lathe/seg/body take an optional
# 'phase' (ring turn; default 0 = the original rings) and a part may carry one material per triangle (Mesh.add with a list);
# the ammo2 looks are written exactly as before these additions.
# usage: <Blender>\5.2\python\bin\python.exe -I make_ce_ammo2.py <abs Models dir> [<abs report.json>] [model keys...]
#          the ammo2 looks (all, or the keys given)
#        <Blender>\5.2\python\bin\python.exe -I make_ce_ammo2.py <abs Models dir> [<abs report.json>] GAE [gae model keys...]
#          the GAE looks
import json, math, os, sys
from collections import defaultdict

OUT = sys.argv[1]
REPORT = sys.argv[2] if len(sys.argv) > 2 else None
os.makedirs(OUT, exist_ok=True)
LIFT = 1.08

# ---------------------------------------------------------------- geometry
class Mesh:
    def __init__(self):
        self.parts = []
    def add(self, mat, vt):
        v, t = vt
        self.parts.append((mat, [tuple(map(float, p)) for p in v], list(t)))

def ear_clip(poly):
    idx = list(range(len(poly)))
    def a2(a, b, c):
        return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
    s = sum(poly[i][0] * poly[(i + 1) % len(poly)][1] - poly[(i + 1) % len(poly)][0] * poly[i][1] for i in range(len(poly)))
    if s < 0:
        idx.reverse()
    tris, guard = [], 0
    while len(idx) > 3 and guard < 1000:
        guard += 1
        n = len(idx)
        for k in range(n):
            a, b, c = idx[(k - 1) % n], idx[k], idx[(k + 1) % n]
            if a2(poly[a], poly[b], poly[c]) <= 1e-12:
                continue
            if any(m not in (a, b, c) and a2(poly[a], poly[b], poly[m]) >= 0 and a2(poly[b], poly[c], poly[m]) >= 0
                   and a2(poly[c], poly[a], poly[m]) >= 0 for m in idx):
                continue
            tris.append((a, b, c)); idx.pop(k); break
    tris.append(tuple(idx))
    return tris

def prism(poly, y0, y1):
    n = len(poly)
    v = [(x, y0, z) for x, z in poly] + [(x, y1, z) for x, z in poly]
    t = list(ear_clip(poly)) + [(a + n, b + n, c + n) for a, b, c in ear_clip(poly)]
    for i in range(n):
        j = (i + 1) % n
        t += [(i, j, j + n), (i, j + n, i + n)]
    return v, t

def box(cx, cz, w, d, y0, h, yaw=0.0):
    """Box centred (cx, cz), w along x, d along z, from y0 up h; yaw degrees clockwise seen from above."""
    pts = [(-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, d / 2), (-w / 2, d / 2)]
    return place(prism(pts, y0, y0 + h), yaw=yaw, at=(cx, 0, cz))

def lathe(profile, n, phase=0.0):
    """profile [(r, t)] along the local +y axis from one end to the other; r = 0 makes a pole. Closed solid.
    (gae) phase turns the rings by phase / n of a turn (0 = the original ring; -0.5 puts a corner on the local x axis)."""
    v, rings = [], []
    for r, h in profile:
        if r < 1e-9:
            rings.append([len(v)]); v.append((0.0, h, 0.0))
        else:
            ring = []
            for i in range(n):
                a = 2 * math.pi * (i + 0.5 + phase) / n
                ring.append(len(v)); v.append((r * math.cos(a), h, r * math.sin(a)))
            rings.append(ring)
    t = []
    if len(rings[0]) > 1:   # flat start cap
        t += [(rings[0][0], rings[0][i + 1], rings[0][i]) for i in range(1, n - 1)]
    if len(rings[-1]) > 1:
        t += [(rings[-1][0], rings[-1][i], rings[-1][i + 1]) for i in range(1, n - 1)]
    for A, B in zip(rings, rings[1:]):
        if len(A) == 1:
            t += [(A[0], B[(i + 1) % n], B[i]) for i in range(n)]
        elif len(B) == 1:
            t += [(A[i], A[(i + 1) % n], B[0]) for i in range(n)]
        else:
            for i in range(n):
                j = (i + 1) % n
                t += [(A[i], A[j], B[j]), (A[i], B[j], B[i])]
    return v, t

def place(vt, axis='y', yaw=0.0, tilt=0.0, at=(0.0, 0.0, 0.0)):
    """Local part (axis = +y) laid along 'z' (north) or 'x' (east) or kept upright 'y'; tilt (deg) tips +y toward -z
    before laying; then yaw (deg, clockwise from above) and move."""
    v, t = vt
    out = []
    ct, st = math.cos(math.radians(tilt)), math.sin(math.radians(tilt))
    cy, sy = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    for x, y, z in v:
        y, z = y * ct + z * st, -y * st + z * ct
        if axis == 'z':
            x, y, z = x, z, y
        elif axis == 'x':
            x, y, z = y, x, z
        x, z = x * cy + z * sy, -x * sy + z * cy
        out.append((x + at[0], y + at[1], z + at[2]))
    return out, t

def seg(m, mat, r0, r1, t0, t1, n, axis, cx, cy, cz, yaw=0.0, pivot=None, phase=0.0, prof=None):
    """One coloured section of a round body: frustum r0 -> r1 from t0 to t1 along the axis through (cx, cy, cz).
    (gae) mat = ('stripes', A, B): the side faces alternate A / B round the ring (caps A), still one closed shell;
    prof = [(r, t), ...] replaces the frustum by a whole profile (one shell, e.g. a rod with its pointed nose)."""
    if prof is None:
        prof = [(r0, t0), (r1, t1)] if r1 > 1e-9 else [(r0, t0), (0.0, t1)]
        if r0 < 1e-9:
            prof = [(0.0, t0), (r1, t1)]
    v, t = lathe(prof, n, phase)
    if isinstance(mat, tuple) and mat[0] == 'stripes':
        caps = (n - 2) * sum(1 for r, _ in (prof[0], prof[-1]) if r > 1e-9)
        mat = [mat[1]] * caps + [mat[1 + (k // 2) % 2] for k in range(len(t) - caps)]
    if axis == 'z':
        v = [(x + cx, z + cy, y + cz) for x, y, z in v]   # local y -> north, local z -> up
    elif axis == 'x':
        v = [(y + cx, z + cy, x + cz) for x, y, z in v]
    else:
        v = [(x + cx, y + cy, z + cz) for x, y, z in v]
    if yaw:
        px, pz = pivot if pivot else (0.0, 0.0)
        c, s = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
        v = [((x - px) * c + (z - pz) * s + px, y, -(x - px) * s + (z - pz) * c + pz) for x, y, z in v]
    m.add(mat, (v, t))

def body(m, sections, n, axis, cx, cy, cz, yaw=0.0, pivot=None, phase=0.0):
    """sections: [(mat, r0, r1, t0, t1)] along the axis (t in cells from (cx, cy, cz)).
    (gae) a section may be (mat, profile) for one multi-ring shell; phase as lathe()."""
    start = len(m.parts)
    for sec in sections:
        if len(sec) == 2:
            seg(m, sec[0], 0, 0, 0, 0, n, axis, cx, cy, cz, yaw, pivot, phase, prof=sec[1])
        else:
            mat, r0, r1, t0, t1 = sec
            seg(m, mat, r0, r1, t0, t1, n, axis, cx, cy, cz, yaw, pivot, phase)
    low = min(p[1] for _, v, _ in m.parts[start:] for p in v)
    if low < 0.0:   # a slightly wider band on a polygon corner would dip under the ground: lift the whole body
        m.parts[start:] = [(mat, [(x, y - low, z) for x, y, z in v], t) for mat, v, t in m.parts[start:]]

def ogive(r, t0, t1, k=3):
    """Rounded nose as k frustums: [(r_i, r_i+1, t_i, t_i+1)]."""
    out = []
    for i in range(k):
        a0, a1 = math.pi / 2 * i / k, math.pi / 2 * (i + 1) / k
        out.append((r * math.cos(a0), r * math.cos(a1) if i < k - 1 else 0.0, t0 + (t1 - t0) * math.sin(a0), t0 + (t1 - t0) * math.sin(a1)))
    return out

# ---------------------------------------------------------------- writer
def orient(verts, tris):
    key, weld = {}, []
    for v in verts:
        weld.append(key.setdefault((round(v[0], 5), round(v[1], 5), round(v[2], 5)), len(key)))
    T = [tuple(weld[i] for i in t) for t in tris]
    et = defaultdict(list)
    for ti, t in enumerate(T):
        for e in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
            et[frozenset(e)].append(ti)
    flip = [None] * len(T)
    pos = {weld[i]: verts[i] for i in range(len(verts))}
    for seed in range(len(T)):
        if flip[seed] is not None:
            continue
        flip[seed] = False
        comp, stack = [seed], [seed]
        while stack:
            ti = stack.pop()
            t = T[ti] if not flip[ti] else (T[ti][0], T[ti][2], T[ti][1])
            for a, b in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
                for nj in et[frozenset((a, b))]:
                    if nj == ti or flip[nj] is not None:
                        continue
                    u = T[nj]
                    flip[nj] = any((u[k], u[(k + 1) % 3]) == (a, b) for k in range(3))
                    comp.append(nj); stack.append(nj)
        vol = 0.0
        for ti in comp:
            t = T[ti] if not flip[ti] else (T[ti][0], T[ti][2], T[ti][1])
            p = [(pos[i][0], pos[i][1], -pos[i][2]) for i in t]
            vol += (p[0][0] * (p[1][1] * p[2][2] - p[1][2] * p[2][1]) - p[0][1] * (p[1][0] * p[2][2] - p[1][2] * p[2][0])
                    + p[0][2] * (p[1][0] * p[2][1] - p[1][1] * p[2][0])) / 6.0
        if vol < 0:
            for ti in comp:
                flip[ti] = not flip[ti]
    return [(t[0], t[2], t[1]) if flip[i] else t for i, t in enumerate(tris)]

def check(verts, tris):
    key, weld = {}, []
    for v in verts:
        weld.append(key.setdefault((round(v[0], 5), round(v[1], 5), round(v[2], 5)), len(key)))
    E = defaultdict(int)
    degen = 0
    for t in tris:
        a, b, c = (weld[i] for i in t)
        if len({a, b, c}) < 3:
            degen += 1
        for e in ((a, b), (b, c), (c, a)):
            E[e] += 1
    return sum(1 for (a, b), n in E.items() if E.get((b, a), 0) != n), degen

GEN = 'tools/make_ce_ammo2.py'
def write_obj(mesh, name, mtl, header):
    lines = ['# IsoRim model: %s' % name, '# ' + header,
             '# Generated by %s. One unit = one map cell, +Y up, file -Z = game north.' % GEN, 'mtllib ' + mtl]
    vb, rep, allv = 1, {'name': name, 'tris': 0, 'open_edges': 0, 'degenerate': 0}, []
    for pi, (mat, v, t) in enumerate(mesh.parts):
        t = orient(v, t)
        oe, dg = check(v, t)
        rep['tris'] += len(t); rep['open_edges'] += oe; rep['degenerate'] += dg
        if isinstance(mat, list):   # (gae) one material per triangle: one object, a usemtl at each change
            lines += ['o %s_%d' % (name, pi)]
            lines += ['v %.4f %.4f %.4f' % (x, y, -z) for x, y, z in v]
            cur = None
            for mt, (a, b, c) in zip(mat, t):
                if mt != cur:
                    lines.append('usemtl ' + mt); cur = mt
                lines.append('f %d %d %d' % (a + vb, b + vb, c + vb))
        else:
            lines += ['o %s_%d' % (name, pi), 'usemtl ' + mat]
            lines += ['v %.4f %.4f %.4f' % (x, y, -z) for x, y, z in v]
            lines += ['f %d %d %d' % (a + vb, b + vb, c + vb) for a, b, c in t]
        vb += len(v); allv += v
    rep['bounds'] = {k: [round(min(p[i] for p in allv), 3), round(max(p[i] for p in allv), 3)] for i, k in enumerate('xyz')}
    with open(os.path.join(OUT, name + '.obj'), 'w', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    return rep


SWATCH = {'can': 'Metal', 'canDark': 'Metal', 'canLight': 'Metal', 'link': 'Metal', 'crate': 'Metal', 'crateDark': 'Metal',
          'case': 'Metal', 'caseDark': 'Metal', 'caseStripe': 'Metal', 'block': 'Metal', 'frame': 'Metal', 'tank': 'Metal',
          'holder': 'Metal', 'tube': 'Metal', 'wood': 'Wood', 'woodDark': 'Wood'}
SWATCH.update({'mag': 'Metal', 'woodTrim': 'Wood'})   # (gae) mags as make_ce_ammo.py; the lead-slug box trim is wood
PREFIX = 'ce_ammo2'   # ce_ammo_gae for the GAE looks
SOURCE = "Combat Extended's textures"
def write_mtl(model, typ, cols):
    lines = ['# IsoRim Waylith - CE Pack: ammo colours for %s / %s, sampled from %s (x%.2f lift).' % (model, typ, SOURCE, LIFT)]
    for zone, hexc in cols.items():
        r, g, b = (min(1.0, int(hexc[i:i + 2], 16) / 255.0 * LIFT) for i in (1, 3, 5))
        lines += ['newmtl ceammo_' + zone, 'Kd %.3f %.3f %.3f' % (r, g, b)]
        if SWATCH.get(zone):
            lines.append('map_Kd IsoRim/Swatches/' + SWATCH[zone])
    name = '%s_%s_%s.mtl' % (PREFIX, model, typ)
    with open(os.path.join(OUT, name), 'w', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    return name

# ---------------------------------------------------------------- clearance checks (SOP: parts that meet keep >= 0.01)
CHECKS = []
def gap(label, a_max, b_min, need=0.01):
    CHECKS.append({'check': label, 'gap': round(b_min - a_max, 4), 'ok': b_min - a_max >= need - 1e-9})

# ---------------------------------------------------------------- shared pieces
M = 'ceammo_'
def can(m, w=0.82, d=0.50, h=0.40, cz=0.08):
    m.add(M + 'can', box(0, cz, w, d, 0.0, h))
    m.add(M + 'canDark', box(0, cz, w - 0.05, d - 0.07, h, 0.04))                  # lid
    m.add(M + 'canDark', box(0, cz, 0.30, 0.07, h + 0.04, 0.035))                  # handle
    m.add(M + 'canLight', box(w / 2 + 0.02, cz, 0.04, 0.16, h * 0.42, 0.14))      # latch on the east face
    return cz - d / 2                                                              # south face z

def white_case(m, w=0.86, d=0.55, h=0.34, cz=0.08):
    m.add(M + 'case2', box(0, cz, w, d, 0.0, h))
    m.add(M + 'band', box(0, cz, 0.22, d + 0.012, 0.0, h + 0.006))                # type stripe over the lid
    m.add(M + 'case2Dark', box(0, cz, w + 0.012, d + 0.012, h * 0.62, 0.03))       # lid seam
    m.add(M + 'case2Dark', box(-w / 2 - 0.02, cz, 0.04, 0.18, h * 0.30, 0.10))     # side handles
    m.add(M + 'case2Dark', box(w / 2 + 0.02, cz, 0.04, 0.18, h * 0.30, 0.10))
    return cz - d / 2 - 0.006

def standing_round(m, x, z, r, hc, ht, case='case', tip='tip', n=6, y0=0.0):
    body(m, [(M + case, r, r, 0.0, hc), (M + tip, r * 0.85, 0.0, hc, hc + ht)], n, 'y', x, y0, z)

# ---------------------------------------------------------------- models
def roff(dx, dz, yaw):
    """An offset in a part's own frame turned by yaw (degrees, clockwise from above), as place() turns."""
    c, s = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    return dx * c + dz * s, -dx * s + dz * c

# ---------------------------------------------------------------- M5 grenade (copied unchanged from make_ce_ammo.py, for the Tox colour set)
def gren(m, x, z, r, L, y=None, axis='z', n=8):
    cy = r if y is None else y
    t0 = -L / 2
    nose = [(M + 'nose', a, b, c, d) for a, b, c, d in ogive(r, t0 + 0.72 * L, L / 2, 3 if n > 6 else 2)]
    body(m, [(M + 'body', r, r, t0, t0 + 0.58 * L), (M + 'band', r * 1.005, r * 1.005, t0 + 0.58 * L, t0 + 0.72 * L)] + nose, n, axis, x, cy, z)

def grenade(stk):
    m = Mesh()
    if stk == 'a':
        gren(m, 0, 0, 0.13, 0.56)
    elif stk == 'b':
        for i in range(5):
            gren(m, -0.32 + i * 0.16, -0.04, 0.075, 0.36, n=6)
        for i in range(4):
            gren(m, -0.24 + i * 0.16, 0.10, 0.075, 0.36, y=0.075 + 0.14, n=6)
        m.add(M + 'link', box(0, -0.18, 0.86, 0.05, 0.0, 0.16))   # belt wraps the bottom row
        gap('grenade b: side by side', -0.32 + 0.075, -0.16 - 0.075)
        gap('grenade b: top row vs bottom row (axis distance vs 2r)', 0.15, math.hypot(0.08, 0.14))
        gap('grenade b: link vs top row (z)', -0.18 + 0.025, 0.10 - 0.18)
    else:
        s = can(m, 0.82, 0.52, 0.40, 0.06)
        for i in range(6):
            x = -0.30 + i * 0.12
            body(m, [(M + 'body', 0.05, 0.05, 0.44, 0.48)] + [(M + 'nose', a, b, c, d) for a, b, c, d in ogive(0.05, 0.48, 0.54, 2)], 6, 'y', x, 0, 0.22)
        gap('grenade c: noses side by side', -0.30 + 0.05, -0.18 - 0.05)
        gap('grenade c: noses vs handle (z)', 0.06 + 0.035, 0.22 - 0.05)
    return m


# IsoRim's boulder_1.obj (Models/, file coordinates: -Z = north), welded
BOULDER_V = [(0.2881,0.0000,-0.0655),(0.2467,0.1505,-0.0908),(0.1505,0.1583,-0.3214),(0.1272,0.0000,-0.2995),(-0.1080,0.1506,-0.2802),(-0.0800,0.0000,-0.2353),(-0.2922,0.1608,-0.0918),(-0.2922,0.0000,-0.0321),(-0.2364,0.1547,0.1559),(-0.1808,0.0000,0.1695),(-0.0025,0.1504,0.2858),(-0.0042,0.0000,0.3177),(0.2707,0.1640,0.1665),(0.2520,0.0000,0.2335),(0.2396,0.3469,-0.0301),(0.1047,0.3407,-0.1616),(-0.1184,0.3272,-0.2023),(-0.2174,0.3219,-0.0243),(-0.1779,0.3463,0.1489),(0.0013,0.3098,0.2093),(0.1664,0.3269,0.1528),(-0.0307,0.4469,-0.0045),]
BOULDER_F = [(0,1,2),(0,2,3),(3,2,4),(3,4,5),(5,4,6),(5,6,7),(7,6,8),(7,8,9),(9,8,10),(9,10,11),(11,10,12),(11,12,13),(13,12,1),(13,1,0),(1,14,15),(1,15,2),(2,15,16),(2,16,4),(4,16,17),(4,17,6),(6,17,18),(6,18,8),(8,18,19),(8,19,10),(10,19,20),(10,20,12),(12,20,14),(12,14,1),(14,21,15),(15,21,16),(16,21,17),(17,21,18),(18,21,19),(19,21,20),(20,21,14),]

# ---------------------------------------------------------------- batch-2 pieces
def rod(m, secs, n, x, y, z, yaw=0.0):
    """Round body along local +t (north before yaw); yaw clockwise from above (45 = nose NE, as CE's art)."""
    body(m, secs, n, 'z', x, y, z, yaw, pivot=(x, z))

def at(x, z, dt, yaw, dp=0.0):
    """Point dt along a body's axis and dp across it (east before yaw) from (x, z)."""
    ox, oz = roff(dp, dt, yaw)
    return x + ox, z + oz

def sphere(m, mat, x, y, z, r, n=8, k=3, sx=1.0, sy=1.0, sz=1.0):
    prof = [(0.0, -r)] + [(r * math.cos(math.pi * (i / k - 0.5)), r * math.sin(math.pi * (i / k - 0.5))) for i in range(1, k)] + [(0.0, r)]
    v, t = lathe(prof, n)
    m.add(mat, ([(px * sx + x, py * sy + y, pz * sz + z) for px, py, pz in v], t))

def cyl(m, mat, x, z, y0, h, r, n=8, r1=None):
    v, t = lathe([(r, 0.0), (r if r1 is None else r1, h)], n)
    m.add(mat, ([(px + x, py + y0, pz + z) for px, py, pz in v], t))

def crate(m, w, d, h, cx=0.0, cz=0.10, mat='crate', dark='crateDark'):
    """CE ammo crate as the generic batch's: body, darker lid, darker band round the middle; returns the south face z."""
    m.add(M + mat, box(cx, cz, w, d, 0.0, h))
    m.add(M + dark, box(cx, cz, w - 0.04, d - 0.04, h, 0.03))
    m.add(M + dark, box(cx, cz, w + 0.006, d + 0.006, h * 0.45, 0.05))
    return cz - d / 2 - 0.003

def fin_cross(m, x, y, z, yaw, span, length, mat, t_c, thick=0.02, up_only=False):
    """Two crossed plates (horizontal + vertical) centred on the axis at t_c: fins / fletching."""
    cx, cz = at(x, z, t_c, yaw)
    m.add(M + mat, box(cx, cz, span, length, y - thick / 2, thick, yaw))
    lo = y if up_only else max(0.0, y - span / 2)
    m.add(M + mat, box(cx, cz, thick, length, lo, y + span / 2 - lo, yaw))

# ---------------------------------------------------------------- N1 rocket grenades
def rpg_round(m, x, z, y, yaw, s=1.0, n=8):
    og = ogive(0.075 * s, 0.30 * s, 0.45 * s, 3 if n > 6 else 2)
    rod(m, [(M + 'band', 0.036 * s, 0.036 * s, -0.45 * s, -0.33 * s), (M + 'body', 0.03 * s, 0.03 * s, -0.33 * s, 0.10 * s),
            (M + 'body', 0.03 * s, 0.072 * s, 0.10 * s, 0.17 * s), (M + 'warhead', 0.075 * s, 0.075 * s, 0.17 * s, 0.22 * s),
            (M + 'mark', 0.077 * s, 0.077 * s, 0.22 * s, 0.25 * s), (M + 'warhead', 0.075 * s, 0.075 * s, 0.25 * s, 0.30 * s)]
        + [(M + 'warhead', a, b, c, d) for a, b, c, d in og], n, x, y, z, yaw)

def spg_round(m, x, z, y, yaw, s=1.0, n=8):
    og = ogive(0.058 * s, 0.30 * s, 0.46 * s, 3 if n > 6 else 2)
    rod(m, [(M + 'body', 0.034 * s, 0.034 * s, -0.46 * s, -0.34 * s), (M + 'band', 0.046 * s, 0.046 * s, -0.34 * s, -0.20 * s),
            (M + 'body', 0.034 * s, 0.034 * s, -0.20 * s, 0.08 * s), (M + 'body', 0.034 * s, 0.058 * s, 0.08 * s, 0.14 * s),
            (M + 'warhead', 0.058 * s, 0.058 * s, 0.14 * s, 0.20 * s), (M + 'mark', 0.06 * s, 0.06 * s, 0.20 * s, 0.24 * s),
            (M + 'warhead', 0.058 * s, 0.058 * s, 0.24 * s, 0.30 * s)] + [(M + 'warhead', a, b, c, d) for a, b, c, d in og], n, x, y, z, yaw)

def rocketgren(stk, spg=False):
    m = Mesh()
    rnd, r = (spg_round, 0.058) if spg else (rpg_round, 0.075)
    if stk == 'a':
        rnd(m, 0, 0, r, 0.0 if spg else 45.0)
    elif stk == 'b':
        if spg:
            for x in (-0.14, 0.0, 0.14):
                rnd(m, x, 0.0, r, 0.0, n=6)
            gap('spg9 b: rounds side by side', -0.14 + 0.06, 0.0 - 0.06)
        else:
            for p in (-0.16, 0.16):
                x, z = at(0, 0, 0, 45.0, p)
                rnd(m, x, z, r, 45.0, n=6)
            rnd(m, 0, 0, r + 0.13, 45.0, n=6)
            gap('rpg b: side rockets apart', -0.16 + 0.075, 0.16 - 0.075)
            gap('rpg b: top vs bottom (axis distance vs 2r)', 0.15, math.hypot(0.16, 0.13))
    else:
        s = crate(m, 0.86, 0.50, 0.36, 0.0, 0.08)
        top = 0.36 + 0.03
        rnd(m, 0.0, 0.18, top + r * 0.9, 90.0, s=0.9, n=6)
        rnd(m, 0.0, -0.04, top + r * 0.9, 90.0, s=0.9, n=6)
        gap('rocket c: lid rockets apart (z)', -0.04 + r * 0.9 * 1.03, 0.18 - r * 0.9 * 1.03)
        gap('rocket c: lid rocket inside lid edge (z)', 0.18 + r * 0.9, 0.08 + 0.25)
    return m

# ---------------------------------------------------------------- N2 sealed launch tubes
TUBE = {'rpg32': (0.70, 0.08, 'flat'), 'smaw': (0.55, 0.09, 'dome'), 'm74': (0.45, 0.05, 'flat'), 'tow': (0.85, 0.09, 'front')}
def tube_round(m, x, z, y, yaw, L, r, ends, n=8, bands=True):
    t0, t1 = -L / 2, L / 2
    secs = []
    if ends == 'dome':
        secs += [(M + 'cap', 0.0, r * 0.95, t0, t0 + 0.05), (M + 'cap', r * 0.95, r, t0 + 0.05, t0 + 0.09)]
    else:
        secs += [(M + 'cap', r * 1.06, r * 1.06, t0, t0 + 0.07)]
    body_t = [(t0 + 0.09 if ends == 'dome' else t0 + 0.07, t1 - 0.09 if ends in ('dome', 'front') else t1 - 0.07)]
    a, b = body_t[0]
    if bands == 'main':   # bundles: only the type band (keeps the triangle count near the generic batch's)
        k1, k2 = a + (b - a) * 0.70, a + (b - a) * 0.76
        secs += [(M + 'tube', r, r, a, k1), (M + 'band', r * 1.01, r * 1.01, k1, k2), (M + 'tube', r, r, k2, b)]
    elif bands:
        m1, m2 = a + (b - a) * 0.40, a + (b - a) * 0.52
        k1, k2 = a + (b - a) * 0.70, a + (b - a) * 0.76
        secs += [(M + 'tube', r, r, a, m1), (M + 'band2', r * 1.01, r * 1.01, m1, m2), (M + 'tube', r, r, m2, k1),
                 (M + 'band', r * 1.01, r * 1.01, k1, k2), (M + 'tube', r, r, k2, b)]
    else:
        secs += [(M + 'tube', r, r, a, b)]
    if ends == 'dome':
        secs += [(M + 'cap', r, r * 0.95, b, b + 0.04), (M + 'cap', r * 0.95, 0.0, b + 0.04, t1)]
    elif ends == 'front':
        secs += [(M + 'cap', r, r * 0.8, b, b + 0.05), (M + 'cap', r * 0.8, 0.0, b + 0.05, t1)]
    else:
        secs += [(M + 'cap', r * 1.06, r * 1.06, t1 - 0.07, t1)]
    rod(m, secs, n, x, y, z, yaw)

def tubes(stk, kind):
    L, r, ends = TUBE[kind]
    m = Mesh()
    ry = r * 1.06
    if stk == 'a':
        tube_round(m, 0, 0, ry, 45.0, L, r, ends)
    elif stk == 'b':
        for p in (-(ry + 0.008), ry + 0.008):
            x, z = at(0, 0, 0, 45.0, p)
            tube_round(m, x, z, ry, 45.0, L * (0.96 if p < 0 else 1.0), r, ends, n=6)
        gap('%s b: tubes apart' % kind, ry, 2 * ry + 0.016 - ry)
    else:
        step = 2 * ry + 0.012
        rows = {'m74': ([-0.5, 0.5], [-0.5, 0.5]), 'tow': ([-0.5, 0.5], [0.0])}.get(kind, ([-1, 0, 1], [-0.5, 0.5]))
        for p in rows[0]:
            x, z = at(0, 0, 0, 45.0, p * step)
            tube_round(m, x, z, ry, 45.0, L, r, ends, n=6, bands=('main' if kind != 'm74' else False))
        for p in rows[1]:
            x, z = at(0, 0, 0, 45.0, p * step)
            tube_round(m, x, z, ry + step * 0.87, 45.0, L, r, ends, n=6, bands=('main' if kind != 'm74' else False))
        gap('%s c: tubes apart (axis distance vs 2r)' % kind, 2 * ry, step)
        gap('%s c: top row vs bottom row (axis distance vs 2r)' % kind, 2 * ry, math.hypot(step / 2, step * 0.87))
    return m

# ---------------------------------------------------------------- N3 recoilless cartridges
def rcl_round(m, x, z, y, yaw, s=1.0, nose='ogive', n=8, axis='z'):
    r = 0.09 * s
    secs = [(M + 'case', r, r, -0.42 * s, 0.02 * s), (M + 'proj', r * 0.94, r * 0.94, 0.02 * s, 0.05 * s),
            (M + 'band', r * 0.96, r * 0.96, 0.05 * s, 0.08 * s), (M + 'proj', r * 0.94, r * 0.94, 0.08 * s, 0.12 * s),
            (M + 'band', r * 0.96, r * 0.96, 0.12 * s, 0.15 * s), (M + 'proj', r * 0.94, r * 0.94, 0.15 * s, 0.20 * s)]
    if nose == 'ogive':
        secs += [(M + 'proj', a, b, c, d) for a, b, c, d in ogive(r * 0.94, 0.20 * s, 0.42 * s, 3 if n > 6 else 2)]
    elif nose == 'probe':
        secs += [(M + 'proj', r * 0.94, r * 0.35, 0.20 * s, 0.32 * s), (M + 'proj', r * 0.22, r * 0.22, 0.32 * s, 0.42 * s)]
    else:   # canister: flat end
        secs += [(M + 'proj', r * 0.94, r * 0.94, 0.20 * s, 0.40 * s)]
    if axis == 'y':
        body(m, [(mat, a, b, c + 0.42 * s, d + 0.42 * s) for mat, a, b, c, d in secs], n, 'y', x, 0.0, z)
    else:
        rod(m, secs, n, x, y, z, yaw)

def rcl84(stk, nose='ogive'):
    m = Mesh()
    if stk == 'a':
        rcl_round(m, 0, 0, 0.09, 45.0, nose=nose)
    elif stk == 'b':
        for p in (-0.10, 0.10):
            x, z = at(0, 0, 0, 45.0, p)
            rcl_round(m, x, z, 0.09, 45.0, s=0.95, nose=nose, n=6)
        rcl_round(m, 0, 0, 0.09 + 0.155, 45.0, s=0.95, nose=nose, n=6)
        gap('rcl b: side by side', 0.0855, 0.20 - 0.0855)
        gap('rcl b: top vs bottom (axis distance vs 2r)', 0.171, math.hypot(0.10, 0.155))
    else:
        s = 0.65
        cx, cz, w, d = -0.06, 0.16, 0.78, 0.42
        crate(m, w, d, 0.34, cx, cz)
        rcl_round(m, 0.405, 0.16, 0, 0, s=s, nose=nose, n=6, axis='y')
        rcl_round(m, -0.02, -0.25, 0.09 * s, 90.0, s=s, nose=nose, n=6)
        gap('rcl c: standing round vs crate (x)', cx + w / 2 + 0.003, 0.405 - 0.09 * s)
        gap('rcl c: lying round vs crate (z)', -0.25 + 0.09 * s, cz - d / 2 - 0.003)
    return m

def rcl90_round(m, x, z, y, yaw, can=False, n=8, axis='z', s=1.0):
    r = 0.075 * s
    if can:
        secs = [(M + 'case', r, r, -0.40 * s, 0.24 * s), (M + 'band', r * 1.02, r * 1.02, 0.24 * s, 0.28 * s), (M + 'proj', r, r * 0.9, 0.28 * s, 0.36 * s)]
    else:
        secs = [(M + 'case', r, r, -0.40 * s, 0.06 * s), (M + 'band', r * 1.02, r * 1.02, 0.06 * s, 0.10 * s),
                (M + 'proj', r, r * 0.55, 0.10 * s, 0.24 * s), (M + 'proj', r * 0.3, r * 0.3, 0.24 * s, 0.38 * s)]
    if axis == 'y':
        body(m, [(mat, a, b, c + 0.40 * s, d + 0.40 * s) for mat, a, b, c, d in secs], n, 'y', x, 0.0, z)
    else:
        rod(m, secs, n, x, y, z, yaw)

def rcl90(stk, can=False):
    m = Mesh()
    if stk == 'a':
        rcl90_round(m, 0, 0, 0.077, 45.0, can)
    elif stk == 'b':   # standing as CE's art (user 2026-10-30)
        for x, z in ((-0.17, 0.05), (0.0, 0.10), (0.17, 0.05)):
            rcl90_round(m, x, z, 0, 0, can, n=6, axis='y')
        gap('rcl90 b: standing rounds apart', -0.17 + 0.077, 0.0 - 0.077)
    else:
        for x, z in ((-0.17, 0.08), (0.0, 0.10), (0.17, 0.08), (-0.085, -0.08), (0.085, -0.08)):
            rcl90_round(m, x, z, 0, 0, can, n=6, axis='y')
        gap('rcl90 c: back rounds apart', -0.17 + 0.077, 0.0 - 0.077)
        gap('rcl90 c: front vs back row (axis distance vs 2r)', 2 * 0.077, math.hypot(0.085, 0.16))
    return m

# ---------------------------------------------------------------- N4 aircraft rockets
AIR = {'s5': 0.035, 's8': 0.042, 'apkws': 0.04}
def air_round(m, x, z, y, yaw, kind, s=1.0, n=8):
    r = AIR[kind] * s
    if kind == 'apkws':
        secs = [(M + 'nozzle', r, r, -0.47 * s, -0.42 * s), (M + 'body', r, r, -0.42 * s, 0.10 * s),
                (M + 'warhead', r, r, 0.10 * s, 0.38 * s)] + [(M + 'nose', a, b, c, d) for a, b, c, d in ogive(r, 0.38 * s, 0.47 * s, 2)]
    else:
        secs = [(M + 'nozzle', r * 1.1, r * 1.1, -0.47 * s, -0.42 * s), (M + 'body', r, r, -0.42 * s, 0.24 * s),
                (M + 'warhead', r * 1.12, r * 1.12, 0.24 * s, 0.38 * s), (M + 'nose', r * 1.12, 0.0, 0.38 * s, 0.47 * s)]
    rod(m, secs, n, x, y, z, yaw)
    if kind == 'apkws':   # white canards behind the guidance section: one thin cross plate
        fin_cross(m, x, y, z, yaw, 0.15 * s, 0.04 * s, 'body', 0.28 * s, thick=0.012)

def airrocket(stk, kind):
    m = Mesh()
    r = AIR[kind] * 1.12
    if stk == 'a':
        air_round(m, 0, 0, r, 45.0, kind)
    elif stk == 'b':
        ps = (-0.085, 0.085) if kind == 'apkws' else (-0.105, 0.0, 0.105)
        half = 0.075 if kind == 'apkws' else r
        for p in ps:
            x, z = at(0, 0, 0, 45.0, p)
            air_round(m, x, z, r, 45.0, kind, n=6)
        gap('%s b: side by side (canards / bodies)' % kind, ps[0] + half, ps[1] - half)
    else:
        s = crate(m, 0.86, 0.30, 0.24, 0.0, 0.10)
        air_round(m, 0.0, 0.10, 0.27 + r, 90.0, kind, s=0.92, n=6)
        air_round(m, 0.0, -0.20, r, 80.0, kind, s=0.92, n=6)
        gap('%s c: ground rocket vs crate (z)' % kind, -0.20 + 0.46 * math.cos(math.radians(80)) + r, s)
    return m

# ---------------------------------------------------------------- N5 artillery rockets
def art_round(m, x, z, y, yaw, kind, s=1.0, n=8):
    r = (0.085 if kind == 'm13' else 0.10) * s
    nose_k = 3 if n > 6 else 2
    secs = [(M + 'tail', r * 0.85, r * 0.9, -0.45 * s, -0.40 * s), (M + 'tail', r * 0.9, r * 0.9, -0.40 * s, -0.20 * s),
            (M + 'body', r, r, -0.20 * s, 0.04 * s), (M + 'band', r * 1.02, r * 1.02, 0.04 * s, 0.08 * s), (M + 'body', r, r, 0.08 * s, 0.18 * s)]
    secs += [(M + 'nose', a, b, c, d) for a, b, c, d in ogive(r, 0.18 * s, 0.45 * s, nose_k)]
    rod(m, secs, n, x, y, z, yaw)
    if kind == 'm13':
        fin_cross(m, x, y, z, yaw, 0.30 * s, 0.14 * s, 'fin', -0.36 * s, thick=0.018)

def artrocket(stk, kind):
    m = Mesh()
    r = 0.15 if kind == 'm13' else 0.10   # axis height (M13: its fins reach the ground)
    if stk == 'a':
        art_round(m, 0, 0, r, 45.0, kind)
    elif stk == 'b':
        sp, o, s = (0.155, 0.04, 0.9) if kind == 'm13' else (0.115, 0.0, 0.95)
        for p in (-sp, sp):
            x, z = at(o, o, 0, 45.0, p)
            art_round(m, x, z, r, 45.0, kind, s=s, n=6)
        half = 0.15 * s if kind == 'm13' else 0.102 * s
        gap('%s b: side by side (fins / bodies)' % kind, -sp + half, sp - half)
    else:
        for p in (-0.155, 0.155):
            x, z = at(0.04, 0.04, 0, 45.0, p)
            art_round(m, x, z, r, 45.0, kind, s=0.9, n=6)
        x, z = at(0.04, 0.04, -0.06, 45.0, 0.0)
        art_round(m, x, z, r + 0.20, 45.0, kind, s=0.9, n=6)
        gap('m13 c: side by side (fins)', -0.155 + 0.135, 0.155 - 0.135)
        gap('m13 c: top vs bottom bodies (axis distance vs 2r)', 2 * 0.0765, math.hypot(0.155, 0.20))
    return m

# ---------------------------------------------------------------- N6 Tomahawk (CE drawSize 4: 3.9 cells long)
def tomahawk(stk):
    m = Mesh()
    r = 0.21
    secs = [(M + 'band', r * 0.8, r * 0.9, -1.95, -1.85), (M + 'body', r * 0.9, r, -1.85, -1.65), (M + 'body', r, r, -1.65, -0.60),
            (M + 'band', r * 1.01, r * 1.01, -0.60, -0.57), (M + 'body', r, r, -0.57, 0.90), (M + 'band', r * 1.01, r * 1.01, 0.90, 0.93),
            (M + 'body', r, r, 0.93, 1.62)] + [(M + 'nose', a, b, c, d) for a, b, c, d in ogive(r, 1.62, 1.95, 3)]
    rod(m, secs, 10, 0, r, 0, 90.0)
    fin_cross(m, 0, r, 0, 90.0, 0.62, 0.20, 'fin', -1.72, thick=0.03)
    return m

# ---------------------------------------------------------------- N7 charged mini-rockets
def mini_round(m, x, z, y, s=1.0, n=8, k=3, bands=2):
    r = 0.13 * s
    secs = [(M + 'collar', r * 1.15, r * 1.15, -0.40 * s, -0.33 * s), (M + 'body', r, r, -0.33 * s, -0.10 * s)]
    if bands == 2:
        secs += [(M + 'band', r * 1.01, r * 1.01, -0.10 * s, -0.06 * s), (M + 'body', r, r, -0.06 * s, 0.04 * s),
                 (M + 'band', r * 1.01, r * 1.01, 0.04 * s, 0.08 * s), (M + 'body', r, r, 0.08 * s, 0.12 * s)]
    else:
        secs += [(M + 'band', r * 1.01, r * 1.01, -0.10 * s, -0.04 * s), (M + 'body', r, r, -0.04 * s, 0.12 * s)]
    secs += [(M + 'nose', a, b, c, d) for a, b, c, d in ogive(r, 0.12 * s, 0.42 * s, k)]
    rod(m, secs, n, x, y, z, 0.0)
    fin_cross(m, x, y, z, 0.0, 0.42 * s, 0.14 * s, 'fin', -0.30 * s, thick=0.02, up_only=True)

def minirocket(stk):
    m = Mesh()
    if stk == 'a':
        mini_round(m, 0, 0, 0.15)
    elif stk == 'b':
        s = 0.7
        for x in (-0.31, 0.0, 0.31):
            mini_round(m, x, 0.06, 0.15 * s, s=s, n=6, k=2, bands=1)
        mini_round(m, -0.155, -0.06, 0.15 * s + 0.17, s=s, n=6, k=2, bands=1)
        gap('mini b: fins of neighbours (x)', -0.31 + 0.21 * s, 0.0 - 0.21 * s)
        gap('mini b: top vs bottom collars (axis distance vs 2r)', 2 * 0.15 * s, math.hypot(0.155, 0.17))
    else:
        s = crate(m, 0.72, 0.45, 0.55, 0.08, 0.16, 'case', 'caseDark')
        m.add(M + 'caseDark', box(0.08, 0.20, 0.40, 0.14, 0.58, 0.012))   # top slot
        for x in (-0.18, 0.12):
            mini_round(m, x, -0.285, 0.15 * 0.45, s=0.45, n=6, k=2, bands=1)
        gap('mini c: rockets vs case (z)', -0.285 + 0.42 * 0.45, s)
        gap('mini c: rockets apart (x)', -0.18 + 0.21 * 0.45 + 0.01, 0.12 - 0.21 * 0.45)
    return m

# ---------------------------------------------------------------- N8 spacer cells
CELL = {'railp': (0.38, 0.14, 0.30), 'railr': (0.52, 0.16, 0.28), 'railhc': (0.62, 0.18, 0.40),
        'plasmap': (0.48, 0.30, 0.18), 'plasmar': (0.85, 0.25, 0.15), 'plasmah': (0.62, 0.40, 0.22)}
def cell_block(m, kind, x, z, s=1.0):
    w, d, h = (v * s for v in CELL[kind])
    if kind == 'plasmah':   # wedge: sloped south face (extruded along x)
        prof = [(0.0, -d / 2), (0.0, d / 2), (h, d / 2), (h, -d * 0.12), (h * 0.45, -d / 2)]
        m.add(M + 'block', place(prism(prof, -w / 2, w / 2), axis='x', at=(x, 0, z)))
        m.add(M + 'grille', box(x, z - d / 2 - 0.004, w * 0.86, 0.01, 0.02 * s, h * 0.35))
        for lx in (-0.08, 0.08):
            m.add(M + 'light', box(x + lx * s, z + d * 0.18, 0.04 * s, 0.12 * s, h, 0.012))
        return
    m.add(M + 'block', box(x, z, w, d, 0.0, h))
    if kind.startswith('rail'):
        m.add(M + 'stripe', box(x, z - d * 0.25, w * 0.22, 0.05 * s, h, 0.012))
        m.add(M + 'grille', box(x, z - d / 2 - 0.004, w * 0.70, 0.01, 0.03 * s, h * 0.35))
        if kind == 'railhc':
            for lx in (-0.12, 0.12):
                m.add(M + 'light', box(x + lx * s, z - d / 2 - 0.004, 0.035 * s, 0.01, h * 0.45, h * 0.25))
    else:
        m.add(M + 'grille', box(x, z - d / 2 - 0.004, w * 0.92, 0.01, 0.02 * s, h * 0.40))
        m.add(M + 'stripe', box(x, z, w + 0.006, d * 0.25, h * 0.55, h * 0.12))
        for lx in (-0.10, 0.0, 0.10) if kind == 'plasmar' else (-0.06, 0.06):
            m.add(M + 'light', box(x - w * 0.18 + lx * s, z + d * 0.10, 0.035 * s, d * 0.45, h, 0.012))

def spacercell(stk, kind):
    m = Mesh()
    w, d, h = CELL[kind]
    rail = kind.startswith('rail')
    if stk == 'a':
        cell_block(m, kind, 0, 0)
    elif stk == 'b':
        dz = d / 2 + 0.02
        cell_block(m, kind, 0.06, dz, s=0.95)
        cell_block(m, kind, -0.04, -dz, s=0.95)
        gap('%s b: blocks apart (z)' % kind, -dz + d * 0.95 / 2, dz - d * 0.95 / 2 - 0.004)
    else:
        if rail:
            s = crate(m, 0.86, 0.50, 0.42, 0.0, 0.14, 'case', 'caseDark')
            for rx in (-0.30, -0.10, 0.10, 0.30):   # ribs on the lid (CE's ribbed green case)
                m.add(M + 'caseDark', box(rx, 0.14, 0.03, 0.46, 0.45, 0.012))
        else:
            m.add(M + 'case', box(0.0, 0.14, 0.86, 0.50, 0.0, 0.40))
            m.add(M + 'caseStripe', box(0.0, 0.14, 0.866, 0.506, 0.30, 0.045))
            m.add(M + 'caseDark', box(0.0, 0.14 - 0.253, 0.56, 0.01, 0.08, 0.16))
            s = 0.14 - 0.25 - 0.008
        sc = min(0.6, 0.22 / max(d, 0.001), 0.40 / w)
        cell_block(m, kind, 0.12, -0.36 + d * sc / 2 + 0.005, s=sc)
        if w * sc < 0.30:
            cell_block(m, kind, -0.20, -0.36 + d * sc / 2 + 0.005, s=sc)
        gap('%s c: cell vs case (z)' % kind, -0.36 + d * sc + 0.009, s)
    return m

# ---------------------------------------------------------------- N9 30x64mm fuel cells
def fuel_cell_lying(m, x, z, y, yaw, s=1.0, n=8):
    r = 0.07 * s
    rod(m, [(M + 'cap', r * 0.93, r * 0.93, -0.20 * s, -0.14 * s), (M + 'cell', r, r, -0.14 * s, -0.05 * s),
            (M + 'band', r * 1.02, r * 1.02, -0.05 * s, 0.05 * s), (M + 'cell', r, r, 0.05 * s, 0.14 * s),
            (M + 'cap', r * 0.93, r * 0.93, 0.14 * s, 0.20 * s)], n, x, y, z, yaw)

def fuel_cell_standing(m, x, z, y0, s=1.0, n=6):
    r = 0.07 * s
    body(m, [(M + 'cell', r, r, 0.0, 0.08 * s), (M + 'band', r * 1.02, r * 1.02, 0.08 * s, 0.14 * s),
             (M + 'cell', r, r, 0.14 * s, 0.22 * s), (M + 'cap', r * 0.93, r * 0.93, 0.22 * s, 0.28 * s)], n, 'y', x, y0, z)

def fuel30(stk):
    m = Mesh()
    if stk == 'a':
        fuel_cell_lying(m, 0, 0, 0.0714, 0.0)
    elif stk == 'b':
        for x in (-0.20, 0.0, 0.20):
            cyl(m, M + 'cap', x, 0.0, 0.0, 0.04, 0.05, 6)
        m.add(M + 'holder', box(0, 0, 0.62, 0.20, 0.04, 0.10))
        for x in (-0.20, 0.0, 0.20):
            fuel_cell_standing(m, x, 0.0, 0.14)
        gap('fuel30 b: cells apart', -0.20 + 0.0714, 0.0 - 0.0714)
    else:
        s = crate(m, 0.80, 0.55, 0.50, 0.06, 0.14, 'case', 'caseDark')
        m.add(M + 'holder', box(-0.20, -0.30, 0.40, 0.12, 0.0, 0.06))
        for x in (-0.32, -0.20, -0.08):
            fuel_cell_standing(m, x, -0.30, 0.06, s=0.5)
        fuel_cell_lying(m, 0.27, -0.36, 0.036, 90.0, s=0.5)
        gap('fuel30 c: holder vs case (z)', -0.30 + 0.06, s)
        gap('fuel30 c: loose cell vs holder (x)', 0.0, 0.27 - 0.10)
    return m

# ---------------------------------------------------------------- N10 black-powder cannon charges
def powder_bag(m, x, z, y, yaw, grape=False, n=8, s=1.0):
    r = 0.12 * s
    rod(m, [(M + 'bag', 0.025 * s, 0.06 * s, -0.42 * s, -0.33 * s), (M + 'bag', 0.06 * s, r * 0.9, -0.33 * s, -0.30 * s),
            (M + 'bag', r * 0.9, r, -0.30 * s, -0.24 * s), (M + 'cord', r * 1.01, r * 1.01, -0.24 * s, -0.21 * s),
            (M + 'bag', r, r, -0.21 * s, 0.05 * s), (M + 'cord', r * 1.01, r * 1.01, 0.05 * s, 0.08 * s),
            (M + 'bag', r, r * 0.75, 0.08 * s, 0.15 * s)], n, x, y, z, yaw)
    if grape:   # cluster of 4 balls in a net
        for dt, dp, dy in ((0.24, -0.055, -0.03), (0.24, 0.055, -0.03), (0.33, 0.0, -0.03), (0.27, 0.0, 0.065)):
            bx, bz = at(x, z, dt * s, yaw, dp * s)
            sphere(m, M + 'shot', bx, y + dy * s, bz, 0.058 * s, 5, 3)
    else:
        bx, bz = at(x, z, 0.27 * s, yaw)
        sphere(m, M + 'shot', bx, y - 0.01 * s, bz, 0.11 * s, n, 3)

def powder(stk, grape=False):
    m = Mesh()
    if stk == 'a':
        powder_bag(m, 0, 0, 0.12, 45.0, grape)
    else:
        for p in (-0.135, 0.135):
            x, z = at(0, 0, 0, 45.0, p)
            powder_bag(m, x, z, 0.12, 45.0, grape, n=6)
        powder_bag(m, 0, 0, 0.12 + 0.215, 45.0, grape, n=6, s=0.95)
        gap('powder b: side bags apart', -0.135 + 0.1212, 0.135 - 0.1212)
        gap('powder b: top bag vs bottom (axis distance vs 2r)', 0.24, math.hypot(0.135, 0.215))
    return m

# ---------------------------------------------------------------- N11 ball grenade
def bomb(m, x, z, r=0.11, n=8):
    sphere(m, M + 'ball', x, r, z, r, n, 3)
    m.add(M + 'fuse', place(lathe([(0.012, 0.0), (0.010, 0.07)], 5), tilt=-20, at=(x, 2 * r - 0.012, z)))

def ballgren(stk):
    m = Mesh()
    if stk == 'a':
        bomb(m, 0, 0)
    elif stk == 'b':
        for x, z in ((-0.12, -0.05), (0.12, -0.05), (0.0, 0.15)):
            bomb(m, x, z, n=7)
        gap('ballgren b: balls apart', -0.12 + 0.11, 0.12 - 0.11)
    else:
        s = crate(m, 0.80, 0.50, 0.45, 0.04, 0.14, 'wood', 'woodDark')
        for x in (-0.20, 0.04):
            bomb(m, x, -0.26, n=7)
        gap('ballgren c: balls vs crate (z)', -0.26 + 0.11, s)
    return m

# ---------------------------------------------------------------- N12 paper cartridges
def cartridge(m, x, z, s=1.0):
    body(m, [(M + 'paper', 0.05 * s, 0.05 * s, 0.0, 0.08 * s), (M + 'ring', 0.052 * s, 0.052 * s, 0.08 * s, 0.11 * s),
             (M + 'paper', 0.05 * s, 0.05 * s, 0.11 * s, 0.15 * s), (M + 'ring', 0.052 * s, 0.052 * s, 0.15 * s, 0.18 * s),
             (M + 'paper', 0.05 * s, 0.05 * s, 0.18 * s, 0.22 * s), (M + 'paper', 0.05 * s, 0.03 * s, 0.22 * s, 0.27 * s)], 7, 'y', x, 0.0, z)

def loose(m, x, z):
    sphere(m, M + 'ball', x - 0.12, 0.04, z - 0.03, 0.04, 6, 3)
    m.add(M + 'powder', place(lathe([(0.075, 0.0), (0.04, 0.025), (0.0, 0.04)], 6), at=(x + 0.13, 0.0, z - 0.04)))

def paper(stk):
    m = Mesh()
    if stk == 'a':
        cartridge(m, 0, 0.02); loose(m, 0, 0.0)
        gap('paper a: ball vs cartridge (x)', -0.12 + 0.04, -0.05)
    elif stk == 'b':
        m.add(M + 'pouch', box(0.02, 0.10, 0.60, 0.32, 0.0, 0.28))
        m.add(M + 'pouchDark', box(0.02, 0.10 - 0.16 - 0.012, 0.54, 0.024, 0.12, 0.17))   # flap over the front
        cartridge(m, 0.02, -0.24, 0.9); loose(m, 0.02, -0.24)
        gap('paper b: cartridge vs pouch flap (z)', -0.24 + 0.045, 0.10 - 0.16 - 0.024)
    else:
        s = crate(m, 0.90, 0.55, 0.55, 0.0, 0.12, 'wood', 'woodDark')
        cartridge(m, 0.12, -0.28, 0.9); loose(m, 0.12, -0.28)
        gap('paper c: cartridge vs box (z)', -0.28 + 0.045, s)
    return m

# ---------------------------------------------------------------- N13 boulder (IsoRim's boulder_1 facets, light grey)
BOULDER_SCALE = (0.62 / 0.58, 1.0, 0.66 / 0.64)
def boulder(stk):
    m = Mesh()
    v = [(x * BOULDER_SCALE[0], y * BOULDER_SCALE[1], -z * BOULDER_SCALE[2]) for x, y, z in BOULDER_V]
    f = list(BOULDER_F)
    edges = {}
    for a, b, c in f:
        for e in ((a, b), (b, c), (c, a)):
            edges[e] = edges.get(e, 0) + 1
    nxt = {a: b for (a, b) in edges if (b, a) not in edges}   # open rim: IsoRim's boulder has no bottom face, close it
    if nxt:
        start = next(iter(nxt)); loop = [start]
        while nxt[loop[-1]] != start:
            loop.append(nxt[loop[-1]])
        f += [(loop[0], loop[i + 1], loop[i]) for i in range(1, len(loop) - 1)]
    m.add(M + 'stone', (v, f))
    return m

# ---------------------------------------------------------------- N14 / N15 bolts, arrows, darts
PROJ = {'bolt': (0.75, 0.014, 0.10, 0.16), 'arrow': (0.95, 0.012, 0.08, 0.15), 'great': (1.00, 0.018, 0.12, 0.19), 'dart': (0.45, 0.010, 0.06, 0.10)}
def shaft(m, kind, x, z, y, yaw, s=1.0, n=5):
    L, r, fw, fl = PROJ[kind]
    L, fw, fl = L * s, fw * s, fl * s
    hl = 0.08 * s
    rod(m, [(M + 'shaft', r, r, -L / 2, L / 2 - hl), (M + 'head', r * 2.0, 0.0, L / 2 - hl, L / 2)], n, x, y, z, yaw)
    fin_cross(m, x, y, z, yaw, fw, fl, 'fletch', -L / 2 + fl / 2 + 0.01, thick=0.012)

def tuft(m, x, z, y0, w=0.08, h=0.15):
    m.add(M + 'fletch', box(x, z, w, 0.012, y0, h))
    m.add(M + 'fletch', box(x, z, 0.012, w, y0, h))

def jar(m, x, z, gourd=False):
    if gourd:
        sphere(m, M + 'jar', x, 0.07, z, 0.07, 7, 3)
        cyl(m, M + 'jar', x, z, 0.135, 0.05, 0.025, 6)
    else:
        cyl(m, M + 'jar', x, z, 0.0, 0.14, 0.07, 8)

def bolts(stk, withjar=False, spiker=False):
    m = Mesh()
    if stk == 'a':
        shaft(m, 'bolt', 0, 0, 0.05, 45.0)
        return m
    if spiker:   # nerve spiker: round ribbed mug quiver
        cyl(m, M + 'quiver', 0.0, 0.06, 0.0, 0.45, 0.17, 10)
        cyl(m, M + 'quiverDark', 0.0, 0.06, 0.36, 0.06, 0.175, 10)
        qz, qs = 0.06, -0.11
    else:
        m.add(M + 'quiver', box(0, 0.04, 0.42, 0.20, 0.0, 0.50))
        m.add(M + 'quiverDark', box(0, 0.04, 0.426, 0.206, 0.40, 0.06))
        qz, qs = 0.04, -0.06
    xs = (-0.09, 0.09) if stk == 'b' else (-0.16, -0.08, 0.0, 0.08, 0.16)
    for i, x in enumerate(xs):
        tuft(m, x, qz + (0.03 if i % 2 else -0.02), 0.45, w=0.065, h=0.18)
    if withjar:
        jar(m, 0.33, -0.10)
        gap('bolt: jar vs quiver (x)', 0.21 + 0.003, 0.33 - 0.07)
    if stk == 'c':
        shaft(m, 'bolt', -0.02, -0.33, 0.05, 90.0, s=0.8)
        gap('bolt c: lying bolt vs quiver (z)', -0.33 + 0.05, qs)
    return m

def arrows(stk, kind='arrow', withjar=False):
    m = Mesh()
    L, r, fw, fl = PROJ[kind]
    if stk == 'a':
        shaft(m, kind, 0, 0, fw / 2, 45.0)
        return m
    if stk == 'b':
        if kind == 'great':
            for i, p in enumerate((-0.14, 0.0, 0.14)):
                x, z = at(0, 0, 0, 45.0, p)
                shaft(m, kind, x, z, fw / 2, 45.0, s=0.95)
            gap('great b: arrows apart (fletch)', -0.14 + fw / 2, 0.0 - fw / 2)
        else:
            for i, yaw in enumerate((30.0, 45.0, 60.0)):
                shaft(m, kind, 0, 0, fw / 2 + 0.035 * i, yaw, s=0.9)
            gap('%s b: crossing shafts (y step vs 2r)' % kind, 2 * r * 0.9, 0.035)
        return m
    if kind == 'arrow':
        v, t = lathe([(0.13, 0.0), (0.13, 0.45), (0.135, 0.45), (0.135, 0.51), (0.13, 0.51), (0.13, 0.55)], 10)
        quiv = place((v, t), tilt=15, at=(0, 0.035, 0.14))
        m.add(M + 'quiver', quiv)
        m.add(M + 'quiverDark', place(lathe([(0.137, 0.44), (0.137, 0.50)], 10), tilt=15, at=(0, 0.035, 0.14)))
        m.add(M + 'fletch', place(lathe([(0.10, 0.55), (0.06, 0.70)], 7), tilt=15, at=(0, 0.035, 0.14)))
        shaft(m, 'arrow', 0.0, -0.34, fw / 2, 90.0, s=0.82)
        if withjar:
            jar(m, 0.31, 0.10)
        gap('arrow c: lying arrow vs quiver base (z)', -0.34 + fw / 2, 0.14 - 0.13)
    elif kind == 'great':   # bottle-shaped bag lying NW-SE, fletchings out of its NW mouth
        rod(m, [(M + 'quiverDark', 0.06, 0.06, -0.36, -0.30), (M + 'quiver', 0.08, 0.16, -0.30, -0.10), (M + 'quiver', 0.16, 0.14, -0.10, 0.12),
                (M + 'quiver', 0.14, 0.09, 0.12, 0.24), (M + 'quiverDark', 0.095, 0.095, 0.24, 0.29)], 8, -0.02, 0.16, -0.02, -45.0)
        fx, fz = at(-0.02, -0.02, 0.29, -45.0)
        m.add(M + 'fletch', place(lathe([(0.13, 0.0), (0.08, 0.15)], 7), axis='z', yaw=-45.0, at=(fx, 0.16, fz)))
        x, z = at(0, 0, 0, -45.0, -0.22)
        shaft(m, 'great', x, z, fw / 2, 135.0, s=0.60)
        if withjar:
            jar(m, -0.30, -0.32, gourd=True)
        gap('great c: lying arrow vs bag (across)', 0.16, 0.22 - fw / 2 * 0.60)
    else:   # dart: small bag with fletchings
        cyl(m, M + 'quiver', 0.0, 0.04, 0.0, 0.30, 0.15, 9, r1=0.12)
        cyl(m, M + 'quiverDark', 0.0, 0.04, 0.24, 0.05, 0.135, 9)
        for x, z in ((-0.05, 0.02), (0.04, 0.07), (0.03, -0.03)):
            tuft(m, x, z, 0.27, w=0.05, h=0.10)
    return m

# ---------------------------------------------------------------- N16 sling bullets
def pebble(m, x, y, z, yaw=0.0):
    v, t = lathe([(0.0, -1.0)] + [(math.cos(math.pi * (i / 3 - 0.5)), math.sin(math.pi * (i / 3 - 0.5))) for i in (1, 2)] + [(0.0, 1.0)], 7)
    c, s = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    v = [(px * 0.10, py * 0.03, pz * 0.06) for px, py, pz in v]
    v = [(px * c + pz * s + x, py + y + 0.03, -px * s + pz * c + z) for px, py, pz in v]
    m.add(M + 'pebble', (v, t))

def sling(stk):
    m = Mesh()
    if stk == 'a':
        pebble(m, 0, 0, 0)
    elif stk == 'b':
        for x, z, yw in ((-0.22, -0.06, 5), (0.0, -0.09, -8), (0.22, -0.06, 10), (-0.11, 0.07, -4), (0.11, 0.07, 6)):
            pebble(m, x, 0.0, z, yw)
        pebble(m, 0.0, 0.062, -0.01, 0)
        gap('sling b: pebbles apart (x)', -0.22 + 0.10, 0.0 - 0.10)
    else:
        m.add(M + 'sack', lathe([(0.24, 0.0), (0.30, 0.10), (0.30, 0.24), (0.25, 0.38), (0.20, 0.46), (0.21, 0.48)], 10))
        cyl(m, M + 'sackDark', 0.0, 0.0, 0.43, 0.06, 0.215, 10)
        cyl(m, M + 'pebble', 0.0, 0.0, 0.47, 0.04, 0.18, 10)
    return m

# ---------------------------------------------------------------- N17 flamethrower fuel tanks
def tank_box(m, x, z, w, d, h, y0=0.0, front='s', marks=True):
    m.add(M + 'tank', box(x, z, w, d, y0, h))
    if marks:
        if front == 's':
            for i in range(2):
                m.add(M + 'mark', box(x, z - d / 2 - 0.004, w * 0.40, 0.01, y0 + h * (0.62 + 0.09 * i), h * 0.045))
        else:
            for i in range(2):
                m.add(M + 'mark', box(x + w * (0.10 + 0.08 * i), z - d / 2 - 0.004, w * 0.035, 0.01, y0 + h * 0.25, h * 0.5))

def flame(stk):
    m = Mesh()
    if stk == 'a':
        tank_box(m, 0, 0, 0.30, 0.22, 0.55)
        m.add(M + 'valve', box(0.17, 0.0, 0.04, 0.10, 0.28, 0.10))
        cyl(m, M + 'hose', 0.09, 0.06, 0.55, 0.10, 0.022, 6)
        cyl(m, M + 'hose', -0.09, 0.06, 0.55, 0.10, 0.022, 6)
        m.add(M + 'hose', place(lathe([(0.022, -0.11), (0.022, 0.11)], 6), axis='x', at=(0.0, 0.65, 0.06)))
    elif stk == 'b':
        tank_box(m, 0.05, 0.13, 0.60, 0.32, 0.30, marks=False)
        m.add(M + 'valve', box(0.36, 0.13, 0.02, 0.10, 0.12, 0.08))
        tank_box(m, -0.10, -0.21, 0.38, 0.18, 0.18, front='e')
        m.add(M + 'hose', place(lathe([(0.02, -0.12), (0.02, 0.12)], 6), axis='x', at=(0.22, 0.06, -0.21)))
        gap('flame b: small tank vs big tank (z)', -0.21 + 0.09, 0.13 - 0.16)
    else:
        cz = 0.12
        m.add(M + 'frame', box(0, cz, 0.80, 0.48, 0.0, 0.03))
        for x in (-0.38, 0.38):
            for z in (cz - 0.22, cz + 0.22):
                m.add(M + 'frame', box(x, z, 0.04, 0.04, 0.03, 0.67))
        for y in (0.36, 0.67):
            for z in (cz - 0.22, cz + 0.22):
                m.add(M + 'frame', box(0, z, 0.72, 0.03, y, 0.03))
        for x in (-0.18, 0.18):
            tank_box(m, x, cz, 0.32, 0.36, 0.62, y0=0.03)
        tank_box(m, -0.08, -0.29, 0.40, 0.16, 0.16, front='e')
        gap('flame c: tanks vs posts (x)', 0.18 + 0.16, 0.38 - 0.02)
        gap('flame c: tanks vs front rail (z)', cz - 0.22 + 0.015, cz - 0.18 - 0.004)
        gap('flame c: small tank vs rack (z)', -0.29 + 0.08, cz - 0.24)
    return m

# ---------------------------------------------------------------- N18 flares and nails
def flare_round(m, x, z, s=1.0, n=7):
    body(m, [(M + 'shell', 0.07 * s, 0.07 * s, 0.0, 0.03 * s), (M + 'band', 0.072 * s, 0.072 * s, 0.03 * s, 0.06 * s),
             (M + 'shell', 0.07 * s, 0.07 * s, 0.06 * s, 0.18 * s), (M + 'shell', 0.07 * s, 0.05 * s, 0.18 * s, 0.20 * s),
             (M + 'shell', 0.05 * s, 0.05 * s, 0.20 * s, 0.30 * s)], n, 'y', x, 0.0, z)

def flare(stk):
    m = Mesh()
    if stk == 'a':
        flare_round(m, 0, 0)
    elif stk == 'b':
        for x, z, s in ((-0.15, 0.06, 1.25), (0.15, 0.10, 1.25), (0.0, -0.11, 1.1)):
            flare_round(m, x, z, s)
        gap('flare b: back pair apart', -0.15 + 0.0875, 0.15 - 0.0875)
        gap('flare b: front vs back', 0.0875 + 0.077, math.hypot(0.155, 0.17))
    else:
        m.add(M + 'box', box(0, 0.12, 0.86, 0.46, 0.0, 0.42))
        m.add(M + 'lid', box(0, 0.12, 0.866, 0.466, 0.42, 0.06))
        for x in (-0.10, 0.04, 0.18):
            flare_round(m, x, -0.25, 0.62, 6)
        gap('flare c: small flares vs box (z)', -0.25 + 0.045, 0.12 - 0.23)
    return m

def nail_lying(m, x, z, s=1.0):
    rod(m, [(M + 'shell', 0.004, 0.012 * s, -0.15 * s, -0.12 * s), (M + 'shell', 0.012 * s, 0.012 * s, -0.12 * s, 0.13 * s),
            (M + 'band', 0.035 * s, 0.035 * s, 0.13 * s, 0.145 * s)], 6, x, 0.035 * s, z, 0.0)

def nail(stk):
    m = Mesh()
    if stk == 'a':
        nail_lying(m, 0, 0, 1.6)
    elif stk == 'b':
        for i in range(6):
            nail_lying(m, -0.10 + 0.04 * i, 0.04 * (i % 2), 1.0)
        m.add(M + 'lid', box(0.0, -0.03, 0.27, 0.07, 0.0, 0.05))
        gap('nail b: staggered heads (z)', 0.145, 0.13 + 0.04)
    else:
        m.add(M + 'box', box(0, 0.14, 0.70, 0.45, 0.0, 0.45))
        m.add(M + 'lid', box(0, 0.14, 0.706, 0.456, 0.33, 0.03))
        for i in range(5):
            nail_lying(m, -0.08 + 0.04 * i, -0.24, 0.8)
        m.add(M + 'lid', box(0.0, -0.26, 0.22, 0.06, 0.0, 0.04))
        gap('nail c: strip vs carton (z)', -0.24 + 0.145 * 0.8, 0.14 - 0.225)
    return m

# ---------------------------------------------------------------- colour sets (hex from CE's art; analysis section 5)
MODELS = {}
def model(key, fn, sets, stacks='abc', label=''):
    MODELS[key] = dict(fn=fn, sets=sets, stacks=stacks, label=label)

model('rpg', rocketgren, {
    'heat': dict(warhead='#98A060', body='#808848', band='#303030', mark='#98A060', crate='#404040', crateDark='#202020'),
    'frag': dict(warhead='#808850', body='#788048', band='#285848', mark='#808850', crate='#404030', crateDark='#303020'),
    'thermo': dict(warhead='#98A060', body='#788048', band='#802850', mark='#98A060', crate='#802050', crateDark='#601040')}, label='N1 RPG-7 type rockets')
model('spg9', lambda s: rocketgren(s, spg=True), {
    'heat': dict(warhead='#686830', body='#585828', band='#A89040', mark='#686830', crate='#404060', crateDark='#203040'),
    'frag': dict(warhead='#686830', body='#A8A8B0', band='#A89040', mark='#686830', crate='#405030', crateDark='#303020'),
    'thermo': dict(warhead='#903018', body='#506030', band='#A89040', mark='#903018', crate='#801000', crateDark='#601000'),
    'emp': dict(warhead='#888888', body='#787878', band='#A89040', mark='#00C0F8', crate='#708080', crateDark='#506060')}, label='N1 SPG-9 rockets')
model('rpg32', lambda s: tubes(s, 'rpg32'), {
    'heat': dict(tube='#607038', cap='#303030', band='#F0D838', band2='#303030'),
    'thermo': dict(tube='#607038', cap='#303030', band='#303030', band2='#303030')}, label='N2 RPG-32 / M41 tubes')
model('smaw', lambda s: tubes(s, 'smaw'), {
    'heat': dict(tube='#687840', cap='#485028', band='#F0D838', band2='#687840'),
    'thermo': dict(tube='#687840', cap='#485028', band='#485028', band2='#687840')}, label='N2 SMAW tubes')
model('m74', lambda s: tubes(s, 'm74'), {'inc': dict(tube='#C06020', cap='#801010', band='#902020', band2='#C06020')}, label='N2 M74 rockets')
model('tow', lambda s: tubes(s, 'tow'), {'heat': dict(tube='#808080', cap='#A0B0C0', band='#A09888', band2='#282828')}, label='N2 TOW tube')
RC = dict(case='#606060')
model('rcl84', rcl84, {
    'he': dict(proj='#405030', band='#F0D030', crate='#405030', crateDark='#304020', **RC),
    'hedp': dict(proj='#908060', band='#F0D030', crate='#504030', crateDark='#403020', **RC),
    'heab': dict(proj='#404028', band='#F09000', crate='#404030', crateDark='#303020', **RC),
    'frag': dict(proj='#505040', band='#C0C080', crate='#404030', crateDark='#303020', **RC),
    'inc': dict(proj='#C03050', band='#F0F0B0', crate='#C03050', crateDark='#A03040', **RC),
    'thermo': dict(proj='#802050', band='#F03030', crate='#802050', crateDark='#501030', **RC),
    'smk': dict(proj='#88A088', band='#C0D8C0', crate='#A0C0A0', crateDark='#708070', **RC),
    'emp': dict(proj='#8090A0', band='#00C0F8', crate='#708080', crateDark='#506060', **RC)}, label='N3 84x246mmR (ogive nose)')
model('rcl84heat', lambda s: rcl84(s, 'probe'), {'heat': dict(proj='#303030', band='#F8E040', crate='#404040', crateDark='#202020', **RC)}, label='N3 84x246mmR HEAT (probe nose)')
model('rcl84can', lambda s: rcl84(s, 'flat'), {'can': dict(proj='#808078', band='#C0C0B0', crate='#505050', crateDark='#404040', **RC)}, label='N3 84x246mmR canister (flat end)')
model('rcl90', rcl90, {'heat': dict(case='#303030', proj='#A0A0A0', band='#707070')}, label='N3 90mm RCR HEAT')
model('rcl90can', lambda s: rcl90(s, can=True), {'can': dict(case='#909090', proj='#A0A0A0', band='#706070')}, label='N3 90mm RCR canister')
S5 = dict(crate='#405030', crateDark='#304020'); S8 = dict(crate='#607040', crateDark='#405030')
model('s5', lambda s: airrocket(s, 's5'), {
    'he': dict(body='#B0B0B0', warhead='#A08050', nose='#A08050', nozzle='#C05020', **S5),
    'heat': dict(body='#C0C0C0', warhead='#B0A080', nose='#B0B0A0', nozzle='#E03020', **S5),
    'thermo': dict(body='#B0B0B0', warhead='#606050', nose='#606050', nozzle='#C05020', **S5)}, label='N4 S-5 57mm rockets')
model('s8', lambda s: airrocket(s, 's8'), {
    'he': dict(body='#607080', warhead='#C8C0D8', nose='#F0F0F0', nozzle='#304040', **S8),
    'heat': dict(body='#808888', warhead='#405050', nose='#F0F0F0', nozzle='#405050', **S8),
    'thermo': dict(body='#605060', warhead='#706090', nose='#C0C0D0', nozzle='#706090', **S8)}, label='N4 S-8 80mm rockets')
model('apkws', lambda s: airrocket(s, 'apkws'), {'heat': dict(body='#D8D8D8', warhead='#485038', nose='#485038', nozzle='#B05818')}, stacks='ab', label='N4 APKWS (50mm rocket: same OBJs, scale 0.7)')
model('m13', lambda s: artrocket(s, 'm13'), {'he': dict(body='#C0C0C0', nose='#D0D0D0', tail='#A0A0A0', band='#909090', fin='#B8B8B8')}, label='N5 132mm M13')
model('type63', lambda s: artrocket(s, 'type63'), {'he': dict(body='#C0B878', nose='#A8A068', tail='#707048', band='#404028')}, stacks='ab', label='N5 130mm Type 63')
model('nbw', lambda s: artrocket(s, 'nbw'), {'he': dict(body='#B8A888', nose='#505050', tail='#404040', band='#A05050')}, stacks='ab', label='N5 15cm Nebelwerfer')
model('tomahawk', tomahawk, {'lam': dict(body='#E0E0E0', nose='#C0C0C0', band='#808080', fin='#B0B0B0')}, stacks='a', label='N6 Tomahawk (one sprite, drawSize 4)')
model('minirocket', minirocket, {
    'hec': dict(body='#707070', nose='#585858', band='#303030', collar='#C06050', fin='#989898', case='#808080', caseDark='#505050'),
    'icp': dict(body='#A04040', nose='#C06050', band='#F0F0F0', collar='#C06050', fin='#989898', case='#803030', caseDark='#603020'),
    'bb': dict(body='#808080', nose='#909090', band='#606060', collar='#B06050', fin='#989898', case='#808080', caseDark='#505050')}, label='N7 Charged mini-rockets')
RAIL = dict(stripe='#C04030', case='#607030', caseDark='#405020')
model('railp', lambda s: spacercell(s, 'railp'), {'sabot': dict(block='#787070', grille='#505050', **RAIL)}, label='N8 Railgun 8mm (pistol)')
model('railr', lambda s: spacercell(s, 'railr'), {'sabot': dict(block='#787070', grille='#505050', **RAIL)}, label='N8 Railgun 6mm (rifle)')
model('railhc', lambda s: spacercell(s, 'railhc'), {'sabot': dict(block='#303030', grille='#101010', light='#00B8E8', **RAIL)}, label='N8 Railgun 12mm (high calibre)')
PL = dict(block='#505050', stripe='#303030', light='#00B0E0', grille='#202020', case='#D0D0D0', caseDark='#909090', caseStripe='#D04030')
model('plasmap', lambda s: spacercell(s, 'plasmap'), {'std': dict(PL)}, label='N8 Plasma pistol cell')
model('plasmar', lambda s: spacercell(s, 'plasmar'), {'std': dict(PL)}, label='N8 Plasma rifle cell')
model('plasmah', lambda s: spacercell(s, 'plasmah'), {'std': dict(PL)}, label='N8 Plasma heavy cell = laser pack (same art)')
model('fuel30', fuel30, {
    'foam': dict(cell='#E8E8E8', cap='#40A040', band='#E0B8A0', holder='#304030', case='#D0D0D0', caseDark='#A0A0A0'),
    'inc': dict(cell='#381818', cap='#683030', band='#C03810', holder='#403030', case='#705050', caseDark='#403030'),
    'thermo': dict(cell='#282828', cap='#484848', band='#A088A8', holder='#787038', case='#303030', caseDark='#202020')}, label='N9 30x64mm fuel cells')
PB = dict(bag='#D8C8B0', shot='#404040')
model('powder', powder, {'round': dict(cord='#584840', **PB), 'burst': dict(cord='#A84038', **PB), 'inc': dict(cord='#387060', **PB)}, stacks='ab', label='N10 Powder charges (round shot)')
model('grape', lambda s: powder(s, grape=True), {'grape': dict(bag='#D8C8B0', cord='#584840', shot='#988878')}, stacks='ab', label='N10 Powder charges (grape shot)')
model('ballgren', ballgren, {'std': dict(ball='#282028', fuse='#707050', wood='#806040', woodDark='#503010')}, label='N11 Mortar ball grenade')
PW = dict(paper='#C0C0B0', ball='#909080', powder='#303030', pouch='#906830', pouchDark='#604020', wood='#805040', woodDark='#604030')
model('paper', paper, {'musket': dict(ring='#C0C0B0', **PW), 'blunder': dict(ring='#909080', **PW)}, label='N12 Paper cartridges')
model('boulder', boulder, {'std': dict(stone='#B0B0B0')}, stacks='a', label='N13 Catapult boulder (IsoRim boulder_1 facets)')
model('bolt', bolts, {
    'steel': dict(shaft='#A08050', head='#C0C0C0', fletch='#904040', quiver='#706050', quiverDark='#303030'),
    'stone': dict(shaft='#A08050', head='#808080', fletch='#D0D0A0', quiver='#E0C0A8', quiverDark='#C0A090'),
    'plasteel': dict(shaft='#707060', head='#88B8C0', fletch='#F0F0F0', quiver='#484848', quiverDark='#88B8C0')}, label='N14 Crossbow bolts')
model('boltjar', lambda s: bolts(s, withjar=(s != 'a')), {
    'flame': dict(shaft='#C0A090', head='#707070', fletch='#E06000', quiver='#705050', quiverDark='#403030', jar='#803030'),
    'venom': dict(shaft='#606040', head='#606040', fletch='#509030', quiver='#708060', quiverDark='#404040', jar='#509030')}, label='N14 Crossbow bolts with jar')
model('spiker', lambda s: bolts(s, spiker=True), {'bio': dict(shaft='#505050', head='#404040', fletch='#805050', quiver='#806060', quiverDark='#705050')}, label='N14 Nerve spiker bolts (mug quiver)')
model('arrow', arrows, {
    'steel': dict(shaft='#A07050', head='#C0C0C0', fletch='#B05040', quiver='#706050', quiverDark='#B09080'),
    'stone': dict(shaft='#A07050', head='#808080', fletch='#D0D0A0', quiver='#C0A090', quiverDark='#F0D0B0'),
    'plasteel': dict(shaft='#706060', head='#88B8C0', fletch='#F0F0F0', quiver='#303030', quiverDark='#88B8C0')}, label='N15 Arrows')
model('arrowjar', lambda s: arrows(s, 'arrow', withjar=True), {
    'flame': dict(shaft='#C0A090', head='#707070', fletch='#E06000', quiver='#705050', quiverDark='#504040', jar='#803030'),
    'venom': dict(shaft='#A07050', head='#606060', fletch='#509030', quiver='#708060', quiverDark='#A0B0A0', jar='#509030')}, label='N15 Arrows with jar')
model('great', lambda s: arrows(s, 'great'), {
    'steel': dict(shaft='#907040', head='#C0C0C0', fletch='#B05040', quiver='#B09080', quiverDark='#706050'),
    'stone': dict(shaft='#907040', head='#808080', fletch='#D0D0A0', quiver='#F0D0B0', quiverDark='#C0A090'),
    'plasteel': dict(shaft='#505050', head='#88B8C0', fletch='#F0F0F0', quiver='#303030', quiverDark='#88B8C0')}, label='N15 Great arrows')
model('greatjar', lambda s: arrows(s, 'great', withjar=True), {
    'flame': dict(shaft='#B09080', head='#505050', fletch='#E06000', quiver='#705050', quiverDark='#504040', jar='#A04030'),
    'venom': dict(shaft='#A07040', head='#606060', fletch='#509030', quiver='#A0B0A0', quiverDark='#508040', jar='#509030')}, label='N15 Great arrows with flask')
model('dart', lambda s: arrows(s, 'dart'), {'venom': dict(shaft='#303030', head='#606060', fletch='#509030', quiver='#907060', quiverDark='#806050')}, label='N15 Venom darts')
model('sling', sling, {'steel': dict(pebble='#606060', sack='#505030', sackDark='#404030'),
                       'stone': dict(pebble='#706050', sack='#806040', sackDark='#605030')}, label='N16 Sling bullets')
FT = dict(hose='#303030', valve='#808080', frame='#404040')
model('flame', flame, {'napalm': dict(tank='#D05050', mark='#F0F0F0', **FT), 'prom': dict(tank='#703030', mark='#F08000', **FT),
                       'bio': dict(tank='#705050', mark='#906070', **FT)}, label='N17 Flamethrower fuel tanks')
model('flare', flare, {'chem': dict(shell='#C05020', band='#C05020', box='#A08880', lid='#F86840'),
                       'bio': dict(shell='#986060', band='#E09090', box='#605050', lid='#403030')}, label='N18 Flare cartridges')
model('nail', nail, {'std': dict(shell='#505050', band='#404040', box='#A89890', lid='#807868')}, label='N18 Nails')
model('grenade', grenade, {'tox': dict(nose='#A8B870', band='#306050', body='#506060', link='#383838', can='#588848', canDark='#385028', canLight='#90A060')},
      label='M5 grenade geometry (copied unchanged from make_ce_ammo.py) + Tox colours')

# CE texture folder -> look (model_set); the pack binds these by graphicPath (Api 16)
LOOKS = {
    'RPG/HEAT': 'rpg_heat', 'RPG/Frag': 'rpg_frag', 'RPG/Thermobaric': 'rpg_thermo',
    'Rocket/SPG9/HEAT': 'spg9_heat', 'Rocket/SPG9/Frag': 'spg9_frag', 'Rocket/SPG9/Thermobaric': 'spg9_thermo', 'Rocket/SPG9/EMP': 'spg9_emp',
    'RPG/RPG32_HEAT': 'rpg32_heat', 'RPG/RPG32_Thermobaric': 'rpg32_thermo', 'Rocket/SMAW_HEAT': 'smaw_heat', 'Rocket/SMAW_Thermobaric': 'smaw_thermo',
    'Rocket/M74': 'm74_inc', 'Missile/TOW2A': 'tow_heat',
    'Recoilless/HEAT': 'rcl84heat_heat', 'Recoilless/HE': 'rcl84_he', 'Recoilless/HEDP': 'rcl84_hedp', 'Recoilless/HE-AB': 'rcl84_heab',
    'Recoilless/FRAG': 'rcl84_frag', 'Recoilless/CANISTER': 'rcl84can_can', 'Recoilless/INC': 'rcl84_inc', 'Recoilless/THERMO': 'rcl84_thermo',
    'Recoilless/SMK': 'rcl84_smk', 'Recoilless/EMP': 'rcl84_emp', 'Rocket/90mmHEAT': 'rcl90_heat', 'Rocket/90mmCanister': 'rcl90can_can',
    'Rocket/57mmS5/HE': 's5_he', 'Rocket/57mmS5/HEAT': 's5_heat', 'Rocket/57mmS5/Thermobaric': 's5_thermo',
    'Rocket/80mmS8/HE': 's8_he', 'Rocket/80mmS8/HEAT': 's8_heat', 'Rocket/80mmS8/Thermobaric': 's8_thermo', 'Rocket/70mmAPKWS': 'apkws_heat',
    'Rocket/132mmM13': 'm13_he', 'Rocket/130mmType63': 'type63_he', 'Cannon/15cmNbW': 'nbw_he', 'Missile/TomahawkLAM': 'tomahawk_lam',
    'Rocket/MiniRocket/HEAT': 'minirocket_hec', 'Rocket/MiniRocket/Incendiary': 'minirocket_icp', 'Rocket/MiniRocket/Standard': 'minirocket_bb',
    'Railgun/Pistol': 'railp_sabot', 'Railgun/Rifle': 'railr_sabot', 'Railgun/HighCaliber': 'railhc_sabot',
    'PlasmaCell/Pistol': 'plasmap_std', 'PlasmaCell/Rifle': 'plasmar_std', 'PlasmaCell/Heavy': 'plasmah_std', 'LaserPack': 'plasmah_std',
    'FuelCell/Foam': 'fuel30_foam', 'FuelCell/Incendiary': 'fuel30_inc', 'FuelCell/Thermobaric': 'fuel30_thermo',
    'Cannon/BlackPowder/Roundshot': 'powder_round', 'Cannon/BlackPowder/Grapeshot': 'grape_grape', 'Cannon/BlackPowder/Bursting': 'powder_burst',
    'Cannon/BlackPowder/Incendiary': 'powder_inc', 'Medieval/BallGrenade': 'ballgren_std', 'Medieval/Musket/Paper': 'paper_musket',
    'Medieval/Blunderbuss/Paper': 'paper_blunder', 'Medieval/Catapult/CatapultStone_a': 'boulder_std',
    'Medieval/Bolt/Steel': 'bolt_steel', 'Medieval/Bolt/Stone': 'bolt_stone', 'Medieval/Bolt/Plasteel': 'bolt_plasteel',
    'Medieval/Bolt/Flame': 'boltjar_flame', 'Medieval/Bolt/Venom': 'boltjar_venom', 'Medieval/Bolt/Bioferrite': 'spiker_bio',
    'Neolithic/Arrow/Steel': 'arrow_steel', 'Neolithic/Arrow/Stone': 'arrow_stone', 'Neolithic/Arrow/Plasteel': 'arrow_plasteel',
    'Neolithic/Arrow/Flame': 'arrowjar_flame', 'Neolithic/Arrow/Venom': 'arrowjar_venom',
    'Neolithic/GreatArrow/Steel': 'great_steel', 'Neolithic/GreatArrow/Stone': 'great_stone', 'Neolithic/GreatArrow/Plasteel': 'great_plasteel',
    'Neolithic/GreatArrow/Flame': 'greatjar_flame', 'Neolithic/GreatArrow/Venom': 'greatjar_venom', 'Neolithic/Dart': 'dart_venom',
    'Neolithic/SlingBullet/Steel': 'sling_steel', 'Neolithic/SlingBullet/Stone': 'sling_stone',
    'FuelTank/Incendiary': 'flame_napalm', 'FuelTank/Prometheum': 'flame_prom', 'FuelTank/Bioferrite': 'flame_bio',
    'Flare/Chemfuel': 'flare_chem', 'Flare/Bioferrite': 'flare_bio', 'Nail': 'nail_std', 'GrenadeLauncher/Tox': 'grenade_tox',
}
LOOKS = {'Things/Ammo/' + k: v for k, v in LOOKS.items()}
EXTRA = {'Ammo_50mmRocket_HE': 'apkws_heat at scale 0.7 (CE drawSize 0.7), thingDef entry'}
OVERSIZE = {'tomahawk'}   # drawn over neighbouring cells as CE draws it (drawSize 4)

# ================================================================ GAE looks (2026-11-13)
# "The Generic Ammo Experience for Combat Extended" (Workshop 3284460811) ammo defs without a 3D look in the pack. Bound
# later by the mod's thingDefs (not by texture path), so nothing changes without that mod. Same conventions as above:
# a = 1, b = more, c = full stack (IsoRim ModelDef.stackModels), the art read as a TOP view at 1 cell; a def's drawSize
# below 1 is the ModelDef scale (as the 50mm rocket), not built in. Lying round bodies rest exactly on y = 0 (gbody), so
# their outline closes on the ground (outline lesson 2026-11-01). Art -> model map per look in GAE_MODELS[...]['art'].
GAE_MODELS = {}
def gmodel(key, fn, sets, stacks='abc', label='', art='', scale=1.0, binds=()):
    GAE_MODELS[key] = dict(fn=fn, sets=sets, stacks=stacks, label=label, art=art, scale=scale, binds=list(binds))

S60 = math.sin(math.radians(60))

def gbody(m, sections, n, x, z, yaw=0.0, y0=0.0, phase=0.0):
    """body() lying along 'z' (turned by yaw about its own centre), then moved so its lowest point is exactly y0."""
    start = len(m.parts)
    body(m, sections, n, 'z', x, 1.0, z, yaw, pivot=(x, z), phase=phase)
    low = min(p[1] for _, v, _ in m.parts[start:] for p in v)
    m.parts[start:] = [(mat, [(px, py - low + y0, pz) for px, py, pz in v], t) for mat, v, t in m.parts[start:]]

def tube(m, mat, pts, r, n=6):
    """A closed round tube along a polyline [(x, y, z)] lying flat (rings square to the path, sides from the world up)."""
    v, t, rings = [], [], []
    for k, p in enumerate(pts):
        a, b = pts[max(k - 1, 0)], pts[min(k + 1, len(pts) - 1)]
        tx, tz = b[0] - a[0], b[2] - a[2]
        ln = math.hypot(tx, tz)
        sx, sz = tz / ln, -tx / ln
        ring = []
        for i in range(n):
            ang = 2 * math.pi * (i + 0.5) / n
            ring.append(len(v)); v.append((p[0] + r * math.cos(ang) * sx, p[1] + r * math.sin(ang), p[2] + r * math.cos(ang) * sz))
        rings.append(ring)
    t += [(rings[0][0], rings[0][i + 1], rings[0][i]) for i in range(1, n - 1)]
    t += [(rings[-1][0], rings[-1][i], rings[-1][i + 1]) for i in range(1, n - 1)]
    for A, B in zip(rings, rings[1:]):
        for i in range(n):
            j = (i + 1) % n
            t += [(A[i], A[j], B[j]), (A[i], B[j], B[i])]
    m.add(mat, (v, t))

def grid_box(m, x0, x1, y0, y1, z0, z1, nx, ny, nz, mA, mB, off=0):
    """One closed box, every face split into a grid with checker colours mA / mB (the art's packed rod ends)."""
    xs = [x0 + (x1 - x0) * i / nx for i in range(nx + 1)]
    ys = [y0 + (y1 - y0) * j / ny for j in range(ny + 1)]
    zs = [z0 + (z1 - z0) * k / nz for k in range(nz + 1)]
    vid, v, t, mats = {}, [], [], []
    def V(i, j, k):
        if (i, j, k) not in vid:
            vid[(i, j, k)] = len(v); v.append((xs[i], ys[j], zs[k]))
        return vid[(i, j, k)]
    def quad(a, b, c, d, par):
        mm = mA if par % 2 == 0 else mB
        t.extend([(a, b, c), (a, c, d)]); mats.extend([mm, mm])
    for i in (0, nx):
        for j in range(ny):
            for k in range(nz):
                quad(V(i, j, k), V(i, j + 1, k), V(i, j + 1, k + 1), V(i, j, k + 1), j + k + min(i, nx - 1) + off)
    for j in (0, ny):
        for i in range(nx):
            for k in range(nz):
                quad(V(i, j, k), V(i + 1, j, k), V(i + 1, j, k + 1), V(i, j, k + 1), i + k + min(j, ny - 1) + off)
    for k in (0, nz):
        for i in range(nx):
            for j in range(ny):
                quad(V(i, j, k), V(i + 1, j, k), V(i + 1, j + 1, k), V(i, j + 1, k), i + j + min(k, nz - 1) + off)
    m.add(mats, (v, t))

# ---------------------------------------------------------------- G1 charged intermediate rifle (GAE art, 6 sprites a..f)
def ch_round(m, x, z, s=1.0, yaw=0.0, n=8, y0=0.0):
    """Cartridge lying, tip to +axis (north before yaw): dark base rim, pale case, dark seam, type-coloured head with a
    lighter shoulder, black cone tip. Lengths from the art (0.53 inside the outline); the head's 3 slots left out."""
    r = 0.065 * s
    t = -0.265 * s
    secs = []
    for mat, ln, k0, k1 in (('base', 0.05, 0.94, 0.94), ('case', 0.19, 1.0, 1.0), ('base', 0.02, 0.93, 0.93),
                            ('head', 0.14, 1.0, 1.0), ('headLight', 0.03, 1.0, 0.72), ('tip', 0.10, 0.48, 0.0)):
        secs.append((M + mat, r * k0, r * k1, t, t + ln * s)); t += ln * s
    gbody(m, secs, n, x, z, yaw, y0)

def gmag(m, x, z, yaw, s=1.0):
    """Box mag lying flat, feed end north: grey body, type-coloured label over the south 58 % (top + east side; the west
    side stays grey as in the art), the top round lying E-W on the north end, tip east. Returns its size (w, d, h)."""
    w, d, h = 0.34 * s, 0.60 * s, 0.11 * s
    m.add(M + 'mag', box(x, z, w, d, 0.0, h, yaw))
    lw, ld = 0.78 * w, 0.58 * d
    ox, oz = roff(w / 2 - lw / 2 + 0.0015, -d / 2 + 0.08 * d + ld / 2, yaw)
    m.add(M + 'label', box(x + ox, z + oz, lw + 0.003, ld, 0.0, h + 0.004, yaw))
    ox, oz = roff(-0.02 * s, d / 2 - 0.07 * s, yaw)
    ch_round(m, x + ox, z + oz, s=0.5 * s, yaw=yaw + 90.0, n=6, y0=h + 0.01)
    return w, d, h

def standing_mag(m, x, z, tilt=-10.0):
    """Mag standing on its base, leaning back (north), label over its lower part (east 78 %, as gmag)."""
    sec = [(-0.08, -0.035), (0.08, -0.035), (0.08, 0.035), (-0.08, 0.035)]
    m.add(M + 'mag', place(prism(sec, 0.0, 0.30), tilt=tilt, at=(x, 0.0062, z)))
    lab = [(-0.08 + 0.0352, -0.0385), (0.0835, -0.0385), (0.0835, 0.0385), (-0.08 + 0.0352, 0.0385)]
    m.add(M + 'label', place(prism(lab, 0.025, 0.20), tilt=tilt, at=(x, 0.0062, z)))

def chrifle(stk):
    m = Mesh()
    if stk == 'a':
        ch_round(m, 0.0, 0.0)
    elif stk == 'b':      # art e: two mags side by side, turned 14 deg as drawn, the east one a bit north
        s, yaw = 0.9, 14.0
        w, d, h = gmag(m, -0.175, -0.03, yaw, s)
        gmag(m, 0.175, 0.04, yaw, s)
        dx, dz = 0.35, 0.07
        across = dx * math.cos(math.radians(yaw)) - dz * math.sin(math.radians(yaw))
        gap('chrifle b: mags apart (across, incl. label wrap)', w + 0.003, across)
        gap('chrifle b: top round inside the mag width', 0.5 * s * 0.265 + 0.02 * s, w / 2)
    else:                 # art f: type-coloured crate, ribbed lid, white panel front-west, 2 mags standing front-east
        w, d, h, cz = 0.86, 0.46, 0.36, 0.13
        m.add(M + 'crate', box(0, cz, w, d, 0.0, h))
        for rx in (-0.27, 0.0, 0.27):
            m.add(M + 'crateDark', box(rx, cz, 0.12, d - 0.06, h, 0.025))
        m.add(M + 'panel', box(-0.20, cz - d / 2 - 0.005, 0.36, 0.01, 0.07, 0.15))
        for lz in (cz - 0.12, cz + 0.12):
            m.add(M + 'latch', box(w / 2 + 0.015, lz, 0.03, 0.06, 0.20, 0.08))
        for x in (0.12, 0.30):
            standing_mag(m, x, -0.31)
        gap('chrifle c: standing mags apart (x)', 0.12 + 0.0835, 0.30 - 0.08)
        gap('chrifle c: leaning mags vs crate panel (z)', -0.31 + 0.0385 + 0.30 * math.sin(math.radians(10)), cz - d / 2 - 0.01)
    return m

CR = dict(case='#A8A898', base='#303030', tip='#303030', mag='#606060', panel='#F0F0F0', latch='#606060')
gmodel('chrifle', chrifle, {
    'fmj': dict(head='#509810', headLight='#60B020', label='#407810', crate='#509810', crateDark='#306008', **CR),
    'conc': dict(head='#902028', headLight='#B03838', label='#782020', crate='#902028', crateDark='#601818', **CR),
    'ion': dict(head='#4820A8', headLight='#5830C0', label='#381888', crate='#4820A8', crateDark='#281068', **CR)},
    label='G1 Charged intermediate rifle (GAE art)',
    art='a = _a (1 round), b = _e (2 mags), c = _f (crate + 2 mags); _b/_c (2, 3 rounds) and _d (1 mag) have no model',
    binds=('Ammo_ChargedIntermediateRifle -> fmj (SEX_Rifle_FMJ)', 'Ammo_ChargedIntermediateRifle_AP -> conc (SEX_Rifle_AP)',
           'Ammo_ChargedIntermediateRifle_Ion -> ion (the def shows SEX_Rifle_HP, purple; SEX_Rifle_ION blue is unused)'))

# ---------------------------------------------------------------- G2 charged demolition cell (CE Things/Ammo/Mortar/Charged, 2 sprites)
def demo_shell(m, x, z, s=1.0, n=8, full=True):
    """Shell lying, nose north: olive body, grey band, black band with yellow dashes (stripes round the ring), silver
    ogive nose. Lengths from the art's centre column (0.85 inside the outline), r 0.105."""
    r = 0.105 * s
    haz = ('stripes', M + 'hazDark', M + 'hazYellow')
    parts = ([('base', 0.03, 0.95), ('body', 0.30, 1.0)] if full else [('body', 0.33, 1.0)])
    parts += [('band', 0.06, 1.0), ('hazDark', 0.02, 1.01), (haz, 0.09, 1.01), ('hazDark', 0.02, 1.01)]
    t = -0.425 * s
    secs = []
    for mat, ln, k in parts:
        secs.append((mat if isinstance(mat, tuple) else M + mat, r * k, r * k, t, t + ln * s)); t += ln * s
    if full:
        secs.append((M + 'nose', r, r, t, t + 0.05 * s)); t += 0.05 * s
    secs += [(M + 'nose', a, b, c, d) for a, b, c, d in ogive(r, t, 0.425 * s, 3 if n > 6 else 2)]
    gbody(m, secs, n, x, z)

def chdemo(stk):
    m = Mesh()
    if stk == 'a':
        demo_shell(m, 0.0, 0.0)
    else:                 # art b: 3 shells, the middle one forward (south) as drawn
        for x, z in ((-0.24, 0.04), (0.0, -0.05), (0.24, 0.04)):
            demo_shell(m, x, z, s=0.9, n=6, full=False)
        rr = 0.105 * 0.9 * 1.01
        gap('chdemo b: shells side by side', -0.24 + rr, 0.0 - rr)
    return m

gmodel('chdemo', chdemo, {'std': dict(base='#484848', body='#909078', band='#989898', hazDark='#303030', hazYellow='#D8C820', nose='#C8C8C8')},
       stacks='ab', label='G2 Charged demolition cell (CE Mortar/Charged art)', scale=0.6,
       art='a = Charged_a (1 shell), b = Charged_b (3 shells); CE has 2 sprites, so no c (as fuel cells M12)',
       binds=('Ammo_ChargedDemolition -> std, ModelDef scale 0.6 (GAE drawSize 0.6)',))

# ---------------------------------------------------------------- G3 lead slug (CE Things/Ammo/Medieval/Musket/Ball, 3 sprites)
def lball(m, x, z, y0=0.0, r=0.055, n=8):
    sphere(m, M + 'ball', x, y0 + r, z, r, n, 4)

def heap(m, x, z, r=0.09, h=0.07, n=7):
    """Black powder heap: low cone with a rounded shoulder."""
    m.add(M + 'powder', place(lathe([(r, 0.0), (r * 0.55, h * 0.6), (0.0, h)], n), at=(x, 0.0, z)))

def ball_pile(m, x, z, r=0.055):
    """3 balls on the ground in a triangle + 1 on top (the art's cluster of 4). Returns the pile's north edge z."""
    for px, pz in ((x - 1.1 * r, z - 0.64 * r), (x + 1.1 * r, z - 0.64 * r), (x, z + 1.27 * r)):
        lball(m, px, pz, 0.0, r)
    dy = math.sqrt((2 * r + 0.01) ** 2 - (1.27 * r) ** 2)
    lball(m, x, z, dy, r)
    gap('lead pile: ground balls apart', 2 * r, 2.2 * r)
    return z + 1.27 * r + r

def leadslug(stk):
    m = Mesh()
    if stk == 'a':        # art a: one lead ball + a little powder heap east of it
        lball(m, -0.04, 0.02, 0.0, 0.065)
        heap(m, 0.12, -0.04, 0.075, 0.055)
        gap('lead a: ball vs heap (radii vs centre distance)', 0.065 + 0.075, math.hypot(0.16, 0.06))
    elif stk == 'b':      # art b: leather pouch (loaf, rounded top) E-W at the back, ball pile front-west, heap front-east
        d, hs, cz = 0.28, 0.13, 0.10
        prof = [(0.0, -d / 2), (0.0, d / 2)] + [(hs + d / 2 * math.sin(math.pi * k / 6), d / 2 * math.cos(math.pi * k / 6)) for k in range(7)]
        m.add(M + 'pouch', place(prism(prof, -0.25, 0.25), axis='x', at=(-0.02, 0.0, cz)))
        north = ball_pile(m, -0.14, -0.19)
        heap(m, 0.14, -0.18, 0.09, 0.075)
        gap('lead b: ball pile vs pouch (z)', north, cz - d / 2)
        gap('lead b: heap vs pouch (z)', -0.18 + 0.09, cz - d / 2)
        gap('lead b: pile vs heap (x)', -0.14 + 1.1 * 0.055 + 0.055, 0.14 - 0.09)
    else:                 # art c: dark wooden box with a lighter lid seam, pile front-west, heap front-east (lid emblem dropped)
        w, d, h, cz = 0.80, 0.44, 0.28, 0.12
        m.add(M + 'wood', box(0.05, cz, w, d, 0.0, h))
        m.add(M + 'woodTrim', box(0.05, cz, w + 0.008, d + 0.008, h * 0.58, 0.03))
        north = ball_pile(m, -0.27, -0.26)
        heap(m, 0.21, -0.27, 0.11, 0.09)
        gap('lead c: ball pile vs box (z)', north, cz - d / 2 - 0.004)
        gap('lead c: heap vs box (z)', -0.27 + 0.11, cz - d / 2 - 0.004)
    return m

gmodel('leadslug', leadslug, {'std': dict(ball='#585858', powder='#303030', pouch='#806858', wood='#4A382E', woodTrim='#605048')},
       label='G3 Lead slug (CE Medieval/Musket/Ball art)', art='a = Ball_a, b = Ball_b, c = Ball_c',
       binds=('Ammo_LeadSlug -> std',))

# ---------------------------------------------------------------- G4 railgun sabot (GAE art SEX_RailgunSabot, 6 sprites a..f)
def sabot_rod(m, x, z, yaw=0.0, y0=0.0, L=0.88, r=0.175, nose=0.34, tip=0.025):
    """Lying 6-sided steel rod, a flat face up, tapering to a small flat point at +axis (north before yaw): one shell."""
    t0 = -L / 2
    gbody(m, [(M + 'sabot', [(r, t0), (r, t0 + L - nose), (tip, L / 2)])], 6, x, z, yaw, y0, phase=-0.5)

def clip(m, cx, cz, s=1.0):
    """Art d: a reddish rod with a hooked NE end lying across 4 short sabots whose noses point SE."""
    du = (math.sqrt(0.5), math.sqrt(0.5))     # along the rod (NE)
    dp = (du[1], -du[0])                     # across (SE)
    def P(a, p):
        return cx + a * du[0] + p * dp[0], cz + a * du[1] + p * dp[1]
    rs, Ls = 0.06 * s, 0.30 * s
    for i in range(4):
        sx, sz = P((-0.27 + 0.16 * i) * s, 0.05 * s)
        sabot_rod(m, sx, sz, yaw=135.0, L=Ls, r=rs, nose=0.12 * s, tip=0.012 * s)
    rr, rh = 0.022 * s, 0.085 * s
    yc = 2 * rs * S60 + 0.01 + rr
    (x0, z0), (x1, z1) = P(-0.42 * s, 0.0), P(0.26 * s, 0.0)
    pts = [(x0, yc, z0), (x1, yc, z1)]
    c = (x1 + rh * dp[0], z1 + rh * dp[1])
    u = (-dp[0], -dp[1])
    for k in range(1, 6):
        ph = math.radians(40.0 * k)
        pts.append((c[0] + rh * (math.cos(ph) * u[0] + math.sin(ph) * du[0]), yc, c[1] + rh * (math.cos(ph) * u[1] + math.sin(ph) * du[1])))
    tube(m, M + 'rod', pts, rr, 5)
    gap('sabot clip: sabots apart along the rod', 2 * rs, 0.16 * s)
    gap('sabot clip: rod above the sabots', 2 * rs * S60, yc - rr)

def sabot(stk):
    m = Mesh()
    if stk == 'a':        # art a: one big rod lying N-S (drawSize 0.9 = the ModelDef scale)
        sabot_rod(m, 0.0, 0.0)
    elif stk == 'b':      # art e: two clips side by side
        s = 0.9
        for p in (-0.145, 0.145):
            clip(m, -0.015 + p * math.sqrt(0.5), 0.02 - p * math.sqrt(0.5), s)
        gap('sabot b: clips apart (west noses vs east bases, across)', -0.145 + 0.20 * s, 0.145 - 0.10 * s)
    else:                 # art f: block of packed rods (checker ends, stepped top: west column one rod lower) on a pallet + 1 rod in front
        m.add(M + 'pallet', box(0.0, 0.12, 0.90, 0.56, 0.0, 0.05))
        grid_box(m, -0.12, 0.30, 0.05, 0.47, -0.04, 0.34, 3, 3, 3, M + 'blockLight', M + 'blockDark')
        grid_box(m, -0.27, -0.13, 0.05, 0.33, -0.04, 0.34, 1, 2, 3, M + 'blockLight', M + 'blockDark', off=1)
        sabot_rod(m, -0.12, -0.28, yaw=60.0, L=0.36, r=0.065, nose=0.14, tip=0.012)
        gap('sabot c: block columns apart (x)', -0.13, -0.12)
        gap('sabot c: lying rod vs pallet (z)', max(-0.28 + 0.04 * 0.5 + 0.065 * S60, -0.28 + 0.18 * 0.5 + 0.012 * S60), 0.12 - 0.28)
    return m

gmodel('sabot', sabot, {'std': dict(sabot='#787878', rod='#785050', pallet='#704848', blockLight='#888888', blockDark='#585858')},
       label='G4 Railgun sabot (GAE art)', scale=0.9,
       art='a = _a (1 rod), b = _e (2 clips), c = _f (rod block on a pallet); _b/_c (2, 3 rods) and _d (1 clip) have no model',
       binds=('Ammo_RailgunSabot -> std, ModelDef scale 0.9 (GAE drawSize 0.9)',))

# ---------------------------------------------------------------- G5 plasteel sling bullet (CE Neolithic/SlingBullet/Plasteel)
gmodel('sling', sling, {'plasteel': dict(pebble='#80A098', sack='#908060', sackDark='#786850')},
       label='G5 Plasteel sling bullets (N16 sling geometry, unchanged)', art='a = Plasteel_a, b = Plasteel_b, c = Plasteel_c',
       binds=('Ammo_SlingBullet_Plasteel -> plasteel',))

if __name__ == '__main__':
    args = sys.argv[3:]
    gae = args[:1] == ['GAE']
    if gae:
        PREFIX = 'ce_ammo_gae'
        SOURCE = "The Generic Ammo Experience's and Combat Extended's textures"
        todo = {k: v for k, v in GAE_MODELS.items() if len(args) == 1 or k in args[1:]}
    else:
        todo = {k: v for k, v in MODELS.items() if not args or k in args}
    reports = []
    for key, md in todo.items():
        shapes = {}
        for stk in md['stacks']:
            n0 = len(CHECKS)
            shapes[stk] = md['fn'](stk)
            for c in CHECKS[n0:]:
                c['model'] = '%s_%s' % (key, stk)
        for typ, cols in md['sets'].items():
            mtl = write_mtl(key, typ, cols)
            used = set()
            for stk, mesh in shapes.items():
                for p in mesh.parts:
                    used |= {mt[len(M):] for mt in (p[0] if isinstance(p[0], list) else [p[0]])}
                rep = write_obj(mesh, '%s_%s_%s_%s' % (PREFIX, key, typ, stk), mtl, '%s, colour set %s, stack sprite %s.' % (md['label'], typ, stk))
                rep['oversize'] = key in OVERSIZE
                reports.append(rep)
            missing = used - set(cols)
            if missing:
                raise SystemExit('missing colours %s for %s/%s' % (sorted(missing), key, typ))
    bad = [r for r in reports if r['open_edges'] or r['degenerate'] or r['bounds']['y'][0] < -1e-6
           or (not r['oversize'] and max(abs(r['bounds']['x'][0]), abs(r['bounds']['x'][1]), abs(r['bounds']['z'][0]), abs(r['bounds']['z'][1])) > 0.47)]
    heavy = [(r['name'], r['tris']) for r in reports if r['tris'] > 320]
    failed = [c for c in CHECKS if not c['ok']]
    out = {'objs': len(reports), 'bad': bad, 'over_320_tris': heavy, 'checks': CHECKS, 'failed_checks': failed,
           'tris': {r['name']: r['tris'] for r in reports}, 'bounds': {r['name']: r['bounds'] for r in reports}}
    if not gae:
        out['looks'], out['extra'] = LOOKS, EXTRA
    else:
        out['gae'] = {k: dict(label=v['label'], art=v['art'], scale=v['scale'], binds=v['binds'], sets=list(v['sets']), stacks=v['stacks'])
                      for k, v in todo.items()}
    if REPORT:
        json.dump(out, open(REPORT, 'w'), indent=1)
    print('%d OBJ, %d bad, %d over 320 tris, %d/%d clearance checks failed' % (len(reports), len(bad), len(heavy), len(failed), len(CHECKS)))
    for b in bad[:30]:
        print(' BAD', b['name'], b['open_edges'], b['degenerate'], b['bounds'])
    for h in heavy:
        print(' TRIS', h)
    for c in failed:
        print(' CHECK', c)
