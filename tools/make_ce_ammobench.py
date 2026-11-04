# IsoRim Waylith - Combat Extended Pack: CE's loading bench (AmmoBench; GunsmithingBench draws the same art). Blender
# preview round 1 (2026-11-03), built from CE's Things/Building/AmmoBench north/south/east art and masks.
# As IsoRim's vanilla 3x1 benches: footprint +-1.42 x +-0.42, top 0.62, facing north (worker on the cell south), so every
# art position is scaled by KX, KY from CE's slightly larger top (+-1.453 x +-0.457 cells) into that footprint.
# Bench body = the mask's red part (stuff_cebn_*, same factors as IsoRim's stuff, stuff_dark, stuff_deep); everything on it
# (tray, press, books, boxes, trays) is black in the mask, so fixed colours sampled from the art.
# Flush details (tray rim and mat, caliper, press openings, brass spot, tray fills) are colour zones in the faces of their
# part, never shells. Left out: book titles, the shotgun shells in front of the red box, the cases/powder scatter, the
# press's second stem, the hub's grey centre, the openings on the press's east side (behind the lever) and the brass spot on its back.
# Axes while building: x east, y north, z up; one unit = one cell; origin = footprint centre, on the ground.
# OBJ: +Y up, file -Z = game north. Each part is a closed shell (all convex but the press casting). Pieces smaller than 0.36
# (books, boxes, trays) are meant to go without an outline: outlineMinSize 0.36 in the binding, as IsoRim's chess table.
# usage: <Blender>\5.2\python\bin\python.exe -I make_ce_ammobench.py <abs Models dir> --isorim <abs IsoRim Models dir> [--checks <abs checks.json>]
# ce_mech_ammo_beacon.obj: IsoRim's Models/drop_beacon.obj unchanged, under this mtl (CE's mech ammo beacon draws the vanilla
# MechDropBeacon texture; a pack's models load from its own folder, so it cannot name IsoRim's file).
import json, math, os, sys

OUT = sys.argv[1]
CHECKS = sys.argv[sys.argv.index('--checks') + 1] if '--checks' in sys.argv else None
ISORIM = sys.argv[sys.argv.index('--isorim') + 1]
os.makedirs(OUT, exist_ok=True)
LIB, NAME = 'ce_bench.mtl', 'ce_ammobench'
TOP, GAP, STOREY = 0.62, 0.0105, 1.60
SX, SY = 1.42, 0.42
KX, KY = SX / 1.453, SY / 0.457
ON = TOP + GAP


def ax(px): return (px - 224) / 128.0 * KX       # north art column -> x
def ay(py): return (96 - py) / 128.0 * KY        # north art row -> y


def kd(h, lift=1.1):
    h = h.lstrip('#')
    return tuple(min(1.0, int(h[i:i + 2], 16) / 255.0 * lift) for i in (0, 2, 4))


# name: (hex sampled from CE's art, swatch)
COLOURS = {
    'cebn_rim': ('#333333', 'Metal'),       # tray frame
    'cebn_mat': ('#565656', 'Metal'),       # tray floor (51..91, gradient flattened)
    'cebn_caliper': ('#B8B8B8', 'Metal'),
    'cebn_teal': ('#00745F', 'Metal'),      # press casting
    'cebn_teal_dark': ('#005C49', 'Metal'),
    'cebn_steel': ('#8E8E8E', 'Metal'),     # ram, stem, lever
    'cebn_face': ('#B4B4B4', 'Metal'),      # press shoulder top
    'cebn_window': ('#E6E6E6', None),       # the frame's two openings
    'cebn_black': ('#2E2E2E', 'Metal'),     # press foot, lock ring, hub
    'cebn_brass': ('#CFA44D', None),        # cases (press, tray)
    'cebn_tan': ('#AD9666', None),          # tray boxes
    'cebn_powder': ('#4F4F4F', None),       # tray fill
    'cebn_book_teal': ('#007469', None),    # manual, primer box
    'cebn_book_teal_side': ('#00544C', None),
    'cebn_book_orange': ('#E49A1D', None),
    'cebn_book_orange_side': ('#A86E10', None),
    'cebn_book_red': ('#80210E', None),
    'cebn_book_red_side': ('#5A1608', None),
    'cebn_paper': ('#E2D9A8', None),        # page edges
}
# IsoRim's own colours of drop_beacon.obj under pack names (Kd as machinery.mtl, isorim.mtl; all but the core on the Metal swatch)
BEACON = {'machine_shell': ('cebn_bcn_shell', (0.55, 0.56, 0.43), 'Metal'), 'machine_dark': ('cebn_bcn_dark', (0.29, 0.31, 0.25), 'Metal'),
          'machine_core': ('cebn_bcn_core', (0.72, 0.76, 0.48), None), 'metal': ('cebn_bcn_metal', (0.60, 0.62, 0.65), 'Metal')}
STUFF = {'stuff_cebn_top': 1.0, 'stuff_cebn_edge': 0.8, 'stuff_cebn_deep': 0.62}    # IsoRim's stuff, stuff_dark, stuff_deep


def sub(a, b): return tuple(p - q for p, q in zip(a, b))
def dot(a, b): return sum(p * q for p, q in zip(a, b))
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def norm(a):
    l = math.sqrt(dot(a, a))
    return tuple(p / l for p in a) if l > 1e-12 else (0.0, 0.0, 0.0)


def ear_clip(pts):
    """Triangles (indices) of a simple planar polygon; a keyhole bridge's doubled corners are allowed. Collinear corners
    stay in, so the faces next to it keep matching edges (no T-junctions)."""
    n = (0.0, 0.0, 0.0)
    for i in range(len(pts)):
        a, b = pts[i], pts[(i + 1) % len(pts)]
        n = (n[0] + (a[1] - b[1]) * (a[2] + b[2]), n[1] + (a[2] - b[2]) * (a[0] + b[0]), n[2] + (a[0] - b[0]) * (a[1] + b[1]))
    n = norm(n)
    idx, tris = list(range(len(pts))), []
    area = lambda a, b, c: dot(cross(sub(pts[b], pts[a]), sub(pts[c], pts[a])), n)

    def inside(p, a, b, c):
        if p in (pts[a], pts[b], pts[c]):
            return False
        for u, v in ((a, b), (b, c), (c, a)):
            if dot(cross(sub(pts[v], pts[u]), sub(p, pts[u])), n) < -1e-9:
                return False
        return True

    while len(idx) > 3:
        best = None
        for k in range(len(idx)):
            a, b, c = idx[k - 1], idx[k], idx[(k + 1) % len(idx)]
            ar = area(a, b, c)
            if ar > 1e-9 and not any(inside(pts[j], a, b, c) for j in idx if j not in (a, b, c)):
                if best is None or ar > best[0]:        # the fattest ear keeps slivers away
                    best = (ar, k, a, b, c)
        if best is None:
            raise ValueError('polygon cannot be clipped: %r' % (pts,))
        tris.append(best[2:])
        idx.pop(best[1])
    tris.append(tuple(idx))
    return [t for t in tris if abs(area(*t)) > 1e-10]


class Part:
    def __init__(self, name, convex=True):
        self.name, self.convex, self.verts, self.index, self.tris = name, convex, [], {}, []

    def v(self, p):
        k = tuple(round(c, 4) for c in p)
        if k not in self.index:
            self.index[k] = len(self.verts)
            self.verts.append(k)
        return self.index[k]

    def poly(self, pts, mat, out):
        """A flat face from corners in order; `out` = a direction pointing out of the shell."""
        pts = [tuple(round(c, 4) for c in p) for p in pts]
        tr = ear_clip(pts)
        a, b, c = tr[0]
        if dot(cross(sub(pts[b], pts[a]), sub(pts[c], pts[a])), out) < 0:
            tr = [(c, b, a) for a, b, c in tr]
        for a, b, c in tr:
            self.tris.append(((self.v(pts[a]), self.v(pts[b]), self.v(pts[c])), mat))
        return self


def box(name, x0, x1, y0, y1, z0, z1, top, side=None, west=None, yaw=0.0):
    """A box; `side` colours the walls and the bottom, `west` that wall; yaw in degrees, counter-clockwise from above."""
    side = side or top
    a = math.radians(yaw)
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2

    def q(*c):
        return [(cx + (x - cx) * math.cos(a) - (y - cy) * math.sin(a), cy + (x - cx) * math.sin(a) + (y - cy) * math.cos(a), z) for x, y, z in c]

    r = lambda d: (d[0] * math.cos(a) - d[1] * math.sin(a), d[0] * math.sin(a) + d[1] * math.cos(a), d[2])
    p = Part(name)
    p.poly(q((x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)), top, (0, 0, 1))
    p.poly(q((x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0)), side, (0, 0, -1))
    p.poly(q((x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)), side, r((0, -1, 0)))
    p.poly(q((x0, y1, z0), (x1, y1, z0), (x1, y1, z1), (x0, y1, z1)), side, r((0, 1, 0)))
    p.poly(q((x1, y0, z0), (x1, y1, z0), (x1, y1, z1), (x1, y0, z1)), side, r((1, 0, 0)))
    p.poly(q((x0, y0, z0), (x0, y1, z0), (x0, y1, z1), (x0, y0, z1)), west or side, r((-1, 0, 0)))
    return p


def tray(name, x0, x1, y0, y1, z1, fill, rim=0.016):
    """A shallow tan box: filled top with a thin tan rim, flush."""
    p = Part(name)
    z0 = ON
    o = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    i = [(x0 + rim, y0 + rim), (x1 - rim, y0 + rim), (x1 - rim, y1 - rim), (x0 + rim, y1 - rim)]
    p.poly([(*c, z1) for c in i], fill, (0, 0, 1))
    for k in range(4):
        m = (k + 1) % 4
        p.poly([(*o[k], z1), (*o[m], z1), (*i[m], z1), (*i[k], z1)], 'cebn_tan', (0, 0, 1))
        d = norm((o[k][0] + o[m][0] - x0 - x1, o[k][1] + o[m][1] - y0 - y1, 0))
        p.poly([(*o[k], z0), (*o[m], z0), (*o[m], z1), (*o[k], z1)], 'cebn_tan', d)
    p.poly([(*c, z0) for c in o], 'cebn_tan', (0, 0, -1))
    return p


def ring(cx, cy, r, z, n=8, phase=math.pi / 8):
    return [(cx + r * math.cos(phase + 2 * math.pi * k / n), cy + r * math.sin(phase + 2 * math.pi * k / n), z) for k in range(n)]


# ---- the bench: top slab, four legs, the recessed body the art's dark band shows ----
TRX, TRY, RIMX, RIMY = ax(305), ay(88), ax(305) - ax(297), ay(88) - ay(94)      # tray: outer half width, back edge, rim widths
MX, MY = TRX - RIMX, TRY - RIMY
LEG, IN = 0.10, 0.006


def caliper():
    """The caliper in the mat, corners counter-clockwise: bar from (168, 140) up to (188, 118) of the north art, jaws to one side."""
    a, b = (ax(168), ay(140)), (ax(188), ay(118))
    u = norm((b[0] - a[0], b[1] - a[1], 0))
    w = (-u[1], u[0])
    hb, hj, jl = 0.015, 0.03, 0.085
    P = lambda s, t: (a[0] + u[0] * s + w[0] * t, a[1] + u[1] * s + w[1] * t)
    L = math.hypot(b[0] - a[0], b[1] - a[1]) + 0.03
    return [P(0, -hb), P(L - hj, -hb), P(L - hj, -jl), P(L, -jl), P(L, hb), P(0, hb)]


def keyhole(outer, hole):
    """Outer ring (counter-clockwise) and a counter-clockwise hole as one polygon, a doubled bridge between their nearest corners."""
    _, i, j = min((math.dist(o, h), i, j) for i, o in enumerate(outer) for j, h in enumerate(hole))
    hr = hole[j:] + hole[:j]
    hr = [hr[0]] + hr[:0:-1]
    return outer[:i + 1] + hr + [hr[0], outer[i]] + outer[i + 1:]


def slab():
    z0, z1 = 0.50, TOP
    p = Part('top')
    T, E = 'stuff_cebn_top', 'stuff_cebn_edge'
    up = (0, 0, 1)

    def at(*c): return [(x, y, z1) for x, y in c]

    p.poly(at((-SX, -SY), (-TRX, -SY), (-TRX, TRY), (-TRX, SY), (-SX, SY)), T, up)
    p.poly(at((SX, -SY), (TRX, -SY), (TRX, TRY), (TRX, SY), (SX, SY)), T, up)
    p.poly(at((-TRX, TRY), (TRX, TRY), (TRX, SY), (-TRX, SY)), T, up)
    p.poly(at((-TRX, -SY), (-MX, -SY), (-MX, MY), (MX, MY), (MX, -SY), (TRX, -SY), (TRX, TRY), (-TRX, TRY)), 'cebn_rim', up)
    h = caliper()
    p.poly(at(*keyhole([(-MX, -SY), (MX, -SY), (MX, MY), (-MX, MY)], h)), 'cebn_mat', up)
    p.poly(at(*h), 'cebn_caliper', up)
    p.poly([(-SX, -SY, z0), (SX, -SY, z0)] + [(x, -SY, z1) for x in (SX, TRX, MX, -MX, -TRX, -SX)], E, (0, -1, 0))
    p.poly([(-SX, SY, z0), (SX, SY, z0), (SX, SY, z1), (TRX, SY, z1), (-TRX, SY, z1), (-SX, SY, z1)], E, (0, 1, 0))
    p.poly([(SX, -SY, z0), (SX, SY, z0), (SX, SY, z1), (SX, -SY, z1)], E, (1, 0, 0))
    p.poly([(-SX, -SY, z0), (-SX, SY, z0), (-SX, SY, z1), (-SX, -SY, z1)], E, (-1, 0, 0))
    p.poly([(-SX, -SY, z0), (SX, -SY, z0), (SX, SY, z0), (-SX, SY, z0)], E, (0, 0, -1))
    return p


def legs():
    r = []
    for n, sx, sy in (('sw', -1, -1), ('se', 1, -1), ('nw', -1, 1), ('ne', 1, 1)):
        x0, x1 = sorted((sx * (SX - IN), sx * (SX - LEG)))   # a hair in from the top's corners: welded corners would bend its outline
        y0, y1 = sorted((sy * (SY - IN), sy * (SY - LEG)))
        r.append(box('leg_' + n, x0, x1, y0, y1, 0.0, 0.50, 'stuff_cebn_edge'))
    return r


def body():
    return box('body', -(SX - LEG), SX - LEG, -0.36, 0.36, 0.10, 0.50, 'stuff_cebn_deep')


# ---- the press: the foot, and one casting (frame block, shoulder, stem, die, hub, lever) so it keeps one outline ----
PX, PY = ax(352), ay(127)
FOOT_TOP = 0.70
BW, BD, BZ0, BZ1 = 0.132, 0.11, FOOT_TOP + GAP, 0.90
HX1, HY, HZ, HR = PX + 0.222, PY + 0.02, 0.84, 0.05


def foot():
    cx, cy, wx0, wy0, wx1, wy1, z0, z1, mat = PX, PY, 0.175, 0.135, 0.135, 0.12, ON, FOOT_TOP, 'cebn_black'
    p = Part('foot')
    b = [(cx - wx0, cy - wy0, z0), (cx + wx0, cy - wy0, z0), (cx + wx0, cy + wy0, z0), (cx - wx0, cy + wy0, z0)]
    t = [(cx - wx1, cy - wy1, z1), (cx + wx1, cy - wy1, z1), (cx + wx1, cy + wy1, z1), (cx - wx1, cy + wy1, z1)]
    p.poly(t, mat, (0, 0, 1))
    p.poly(b, mat, (0, 0, -1))
    for k, d in enumerate(((0, -1, 0), (1, 0, 0), (0, 1, 0), (-1, 0, 0))):
        m = (k + 1) % 4
        p.poly([b[k], b[m], t[m], t[k]], mat, d)
    return p


def press():
    p = Part('press', convex=False)
    T = 'cebn_teal'
    x0, x1, y0, y1 = PX - BW, PX + BW, PY - BD, PY + BD
    W, RAM = 0.095, 0.042                    # openings from x +-W to +-RAM, ram within +-RAM
    Z0, Z1, Z2 = 0.765, 0.84, 0.88           # row of the openings Z0..Z1, brass spot Z1..Z2
    B0, B1 = PX - 0.078, PX - 0.012          # brass spot, left of the ram as drawn
    X = lambda s: PX + s
    S = lambda *c: [(x, y0, z) for x, z in c]
    so = (0, -1, 0)
    # south face: the two openings beside the ram, the brass spot above
    p.poly(S((x0, BZ0), (x1, BZ0), (x1, Z0), (X(W), Z0), (X(RAM), Z0), (X(-RAM), Z0), (X(-W), Z0), (x0, Z0)), T, so)
    p.poly(S((x0, Z0), (X(-W), Z0), (X(-W), Z1), (x0, Z1)), T, so)
    p.poly(S((x1, Z0), (X(W), Z0), (X(W), Z1), (x1, Z1)), T, so)
    p.poly(S((X(-W), Z0), (X(-RAM), Z0), (X(-RAM), Z1), (B0, Z1), (X(-W), Z1)), 'cebn_window', so)
    p.poly(S((X(W), Z0), (X(RAM), Z0), (X(RAM), Z1), (X(W), Z1)), 'cebn_window', so)
    p.poly(S((X(-RAM), Z0), (X(RAM), Z0), (X(RAM), Z1), (B1, Z1), (X(-RAM), Z1)), 'cebn_steel', so)
    p.poly(S((x0, Z1), (X(-W), Z1), (B0, Z1), (B0, Z2), (x0, Z2)), T, so)
    p.poly(S((B0, Z1), (X(-RAM), Z1), (B1, Z1), (B1, Z2), (B0, Z2)), 'cebn_brass', so)
    p.poly(S((B1, Z1), (X(RAM), Z1), (X(W), Z1), (x1, Z1), (x1, Z2), (B1, Z2)), T, so)
    p.poly(S((x0, Z2), (B0, Z2), (B1, Z2), (x1, Z2), (x1, BZ1), (x0, BZ1)), T, so)
    p.poly([(x0, y1, BZ0), (x1, y1, BZ0), (x1, y1, BZ1), (x0, y1, BZ1)], T, (0, 1, 0))
    p.poly([(x0, y0, BZ0), (x1, y0, BZ0), (x1, y1, BZ0), (x0, y1, BZ0)], T, (0, 0, -1))
    # hub: a six-sided boss on the east end, one flat facing along the lever
    e = (0.0, -0.392, 0.675)                 # the lever's far end: y, z (x with the hub)
    phi = math.atan2(e[2] - HZ, e[1] - HY)
    hexa = lambda x: [(x, HY + HR * math.cos(phi + math.pi / 6 + k * math.pi / 3), HZ + HR * math.sin(phi + math.pi / 6 + k * math.pi / 3)) for k in range(6)]
    a, b = hexa(x1), hexa(HX1)
    p.poly([(x0, y0, BZ0), (x0, y1, BZ0), (x0, y1, BZ1), (x0, y0, BZ1), (x0, y0, Z2), (x0, y0, Z1), (x0, y0, Z0)], T, (-1, 0, 0))
    east = [(x1, y0, BZ0), (x1, y1, BZ0), (x1, y1, BZ1), (x1, y0, BZ1), (x1, y0, Z2), (x1, y0, Z1), (x1, y0, Z0)]
    p.poly(keyhole(east, a), T, (1, 0, 0))
    p.poly(b, 'cebn_black', (1, 0, 0))
    h = 0.021
    d = norm((0, e[1] - HY, e[2] - HZ))
    w = norm(cross((1, 0, 0), d))
    xm = (x1 + HX1) / 2
    ap = HR * math.cos(math.pi / 6)
    c0 = (xm, HY + d[1] * ap, HZ + d[2] * ap)        # the middle of the facet the lever grows from
    c2 = (xm, e[1], e[2])
    c1 = tuple(c0[i] + (c2[i] - c0[i]) * 0.62 for i in range(3))

    def sq(c):
        return [(c[0] - h, c[1] - w[1] * h, c[2] - w[2] * h), (c[0] + h, c[1] - w[1] * h, c[2] - w[2] * h),
                (c[0] + h, c[1] + w[1] * h, c[2] + w[2] * h), (c[0] - h, c[1] + w[1] * h, c[2] + w[2] * h)]

    A, B, C = sq(c0), sq(c1), sq(c2)
    for k in range(6):
        m = (k + 1) % 6
        o = norm((0, a[k][1] + a[m][1] - 2 * HY, a[k][2] + a[m][2] - 2 * HZ))
        quad = [a[k], a[m], b[m], b[k]]
        if k == 5:      # the facet between -30 and +30 degrees of the lever: it carries the lever's root
            hole = A if dot(cross(sub(A[1], A[0]), sub(A[2], A[0])), cross(sub(quad[1], quad[0]), sub(quad[2], quad[0]))) > 0 else A[::-1]
            p.poly(keyhole(quad, hole), 'cebn_black', o)
        else:
            p.poly(quad, 'cebn_black', o)
    p.poly(C, 'cebn_teal', d)
    for U, V, c, mat in ((A, B, c0, 'cebn_steel'), (B, C, c1, 'cebn_teal')):
        for k in range(4):
            n = (k + 1) % 4
            p.poly([U[k], U[n], V[n], V[k]], mat, norm(tuple(U[k][i] + U[n][i] - 2 * c[i] for i in range(3))))
    # shoulder, stem and die as one turned column out of the frame's top
    rings = [(0.092, BZ1), (0.092, 0.96), (0.03, 0.96), (0.03, 1.02), (0.074, 1.07), (0.074, 1.11), (0.074, 1.18)]
    mats = ['cebn_teal', 'cebn_face', 'cebn_steel', 'cebn_teal_dark', 'cebn_black', 'cebn_teal']
    R = [ring(PX, PY, r, z) for r, z in rings]
    for b_ in range(len(R) - 1):
        (r0, z0), (r1, z1) = rings[b_], rings[b_ + 1]
        for k in range(8):
            m = (k + 1) % 8
            rad = norm((R[b_][k][0] + R[b_][m][0] - 2 * PX, R[b_][k][1] + R[b_][m][1] - 2 * PY, 0))
            o = (rad[0] * (z1 - z0), rad[1] * (z1 - z0), r0 - r1)
            p.poly([R[b_][k], R[b_][m], R[b_ + 1][m], R[b_ + 1][k]], mats[b_], o)
    p.poly(R[-1], 'cebn_teal', (0, 0, 1))
    top = [(x0, y0, BZ1), (x1, y0, BZ1), (x1, y1, BZ1), (x0, y1, BZ1)]
    p.poly(keyhole(top, R[0]), T, (0, 0, 1))
    return p


# ---- what lies on the top ----
BOOK, BOXH = 0.038, 0.09


def books():
    def book(name, cx, cy, yaw, z, cover, side):
        return box(name, cx - 0.135, cx + 0.135, cy - 0.15, cy + 0.15, z, z + BOOK, cover, 'cebn_paper', west=side, yaw=yaw)

    z1 = ON + BOOK + GAP
    z2 = z1 + BOOK + GAP
    return [book('book_red', ax(92), ay(95), 14, ON, 'cebn_book_red', 'cebn_book_red_side'),
            book('book_orange', ax(83), ay(95), 14, z1, 'cebn_book_orange', 'cebn_book_orange_side'),
            book('book_teal', ax(97), ay(88), 7, z2, 'cebn_book_teal', 'cebn_book_teal_side')]


def boxes():
    y0, y1 = ay(80), ay(63)
    z = ON + BOXH + GAP
    return [box('box_red', ax(143), ax(170), y0, y1, ON, ON + BOXH, 'cebn_book_red', 'cebn_book_red_side'),
            box('box_orange', ax(170) + GAP, ax(190), y0, y1, ON, ON + BOXH, 'cebn_book_orange', 'cebn_book_orange_side'),
            box('box_teal', ax(152), ax(180), ay(78), ay(62), z, z + 0.06, 'cebn_book_teal', 'cebn_book_teal_side')]


def parts():
    r = [slab(), body()] + legs() + [foot(), press()] + books() + boxes()
    r.append(tray('tray_brass', ax(297), ax(333), ay(80), ay(54), ON + 0.06, 'cebn_brass'))
    r.append(tray('tray_powder', ax(373), ax(401), ay(82), ay(61), ON + 0.06, 'cebn_powder'))
    return r


# ---- checks ----
def open_edges(p):
    E = {}
    for (a, b, c), _ in p.tris:
        for e in ((a, b), (b, c), (c, a)):
            E[e] = E.get(e, 0) + 1
    return sum(1 for (a, b), n in E.items() if E.get((b, a), 0) != n)


def planes(p):
    r = []
    for (a, b, c), _ in p.tris:
        A, B, C = p.verts[a], p.verts[b], p.verts[c]
        n = norm(cross(sub(B, A), sub(C, A)))
        r.append((n, dot(n, A)))
    return r


def sdf(pl, q):
    return max(dot(n, q) - d for n, d in pl)


def samples(p):
    s = list(p.verts)
    for (a, b, c), _ in p.tris:
        A, B, C = p.verts[a], p.verts[b], p.verts[c]
        for u in range(5):
            for v in range(5 - u):
                w = 4 - u - v
                s.append(tuple((A[i] * u + B[i] * v + C[i] * w) / 4 for i in range(3)))
    return s


def gap(P, Q):
    """Conservative gap, from the convex one(s) of the two."""
    g = []
    if Q.convex:
        b = planes(Q)
        g.append(min(sdf(b, q) for q in samples(P)))
    if P.convex:
        a = planes(P)
        g.append(min(sdf(a, q) for q in samples(Q)))
    return min(g)


def write(ps):
    lines = ['# IsoRim model: ' + NAME, "# CE's loading bench, built facing north as CE's north art (worker on the cell south).",
             '# Generated by tools/make_ce_ammobench.py. One unit = one map cell, +Y up, file -Z = game north.', 'mtllib ' + LIB]
    vb = 1
    for p in ps:
        lines.append('o %s_%s' % (NAME, p.name))
        for x, y, z in p.verts:
            lines.append('v %.4f %.4f %.4f' % (x, z, -y))
        cur = None
        for t, m in sorted(p.tris, key=lambda q: q[1]):
            if m != cur:
                lines.append('usemtl ' + m)
                cur = m
            lines.append('f %d %d %d' % tuple(i + vb for i in t))
        vb += len(p.verts)
    with open(os.path.join(OUT, NAME + '.obj'), 'w', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    m = ['# IsoRim Waylith - Combat Extended Pack: the loading bench (tools/make_ce_ammobench.py). stuff_* take the build stuff;',
         "# fixed colours sampled from CE's art, lifted ~1.1x for the 3D shading as the pack's other buildings."]
    for n, k in STUFF.items():
        m += ['newmtl ' + n, 'Kd %.3f %.3f %.3f' % (k, k, k), 'map_Kd IsoRim/Swatches/{stuff}']
    for n, (h, sw) in COLOURS.items():
        m += ['newmtl ' + n, 'Kd %.3f %.3f %.3f' % kd(h)]
        if sw:
            m.append('map_Kd IsoRim/Swatches/' + sw)
    for n, k, sw in BEACON.values():
        m += ['newmtl ' + n, 'Kd %.3f %.3f %.3f' % k]
        if sw:
            m.append('map_Kd IsoRim/Swatches/' + sw)
    with open(os.path.join(OUT, LIB), 'w', newline='\n') as f:
        f.write('\n'.join(m) + '\n')
    b = ["# IsoRim Waylith - Combat Extended Pack: IsoRim's drop_beacon.obj under the pack's mtl (tools/make_ce_ammobench.py).", 'mtllib ' + LIB]
    for line in open(os.path.join(ISORIM, 'drop_beacon.obj'), encoding='utf-8'):
        q = line.split()
        if q and q[0] in ('mtllib', '#'):
            continue
        b.append('usemtl ' + BEACON[q[1]][0] if q and q[0] == 'usemtl' else line.rstrip())
    with open(os.path.join(OUT, 'ce_mech_ammo_beacon.obj'), 'w', newline='\n') as f:
        f.write('\n'.join(b) + '\n')


def main():
    ps = parts()
    write(ps)
    vs = [v for p in ps for v in p.verts]
    bounds = [[round(min(v[i] for v in vs), 4), round(max(v[i] for v in vs), 4)] for i in range(3)]
    res = {'tris': sum(len(p.tris) for p in ps), 'parts': {p.name: len(p.tris) for p in ps},
           'open_edges': {p.name: open_edges(p) for p in ps if open_edges(p)}, 'bounds_xyz': bounds, 'problems': [], 'gaps': {}}
    for i, P in enumerate(ps):
        for Q in ps[i + 1:]:
            if not (P.convex or Q.convex):
                continue
            g = gap(P, Q)
            if g < 0.02:
                res['gaps'][P.name + '/' + Q.name] = round(g, 4)
            if g < -1e-3:
                res['problems'].append('overlap %s / %s %.4f' % (P.name, Q.name, g))
    if res['open_edges']:
        res['problems'].append('open edges')
    if bounds[2][1] > STOREY or abs(bounds[0][0]) > 1.5 or abs(bounds[1][0]) > 0.5:
        res['problems'].append('bounds')
    print(json.dumps(res, indent=1))
    if CHECKS:
        json.dump(res, open(CHECKS, 'w'), indent=1)


main()
