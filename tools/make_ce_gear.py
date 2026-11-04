# IsoRim Waylith - Combat Extended Pack, gear models v1 (2026-10-30, Blender preview round 1): CE's worn apparel on
# IsoRim's 3D figures. Analysis: model-review\ce_gear\ce_gear_analysis.md; user decisions 2026-10-30: CompositeVest and
# CE GasMask get their own models (vanilla flak vest / gas mask still exist under CE, so they must look different),
# ImprovGasMask its own model, ExoFrame an open frame over the clothes, KidBackpack reuses the Backpack model (no file).
#
# Built from CE's own worn art (Textures/Things/Apparel/*): big simple shapes, small art details dropped.
# Slots and frames (IsoRim Docs/Api.md part 3, ApparelModelDef): file axes as IsoRim's person_*.obj (one unit = one cell,
# +y up, the figure faces -z of the file, +x = the figure's right):
#   Body  (one OBJ per build, made on person_<build>_upper/_lower, IsoRim scales it by BodyScales):
#         ce_gear_tacvest_<build>      CE_Apparel_TacVest   (Webbing layer, drawn over shirt and coat)
#         ce_gear_compositevest_<build> CE_Apparel_CompositeVest (Middle layer)
#         ce_gear_exoframe_<build>     CE_Apparel_ExoFrame  (Shell layer; open frame, use under=Whole at integration)
#   Belt, mount pack (back plane z = 0, the pack behind it at +z, heights as worn; IsoRim pushes it onto the back):
#         ce_gear_backpack (also KidBackpack), ce_gear_tribalpack, ce_gear_radiopack
#   Head  (made on person_head.obj, same origin): ce_gear_platehelmet
#   Face: ce_gear_gasmask, ce_gear_improvgasmask      Eyes: ce_gear_nv_simple, ce_gear_nv_advanced
#   Shield (grip at the origin, face toward -z): ce_gear_shield_melee, ce_gear_shield_ballistic
# Body garments are lofted on the figure's octagon rings with IsoRim's own apparel kit (make_recon_plate.py /
# make_coats.py from the IsoRim tools folder, imported read-only via --kit), like IsoRim's flak vest, coats and straps.
# Materials (ce_gear.mtl): stuff_ceg_* take the item's colour (stuff / colour generator); ceg_* are fixed colours sampled
# from CE's art (lifted 1.1x as the pack's other colours).
# Checks (checks.json): closed parts (open / non-manifold edges), bounds, tris; body garments: points inside the scaled
# body, true head clearance (make_coats.fit_checks); head items: points inside person_head; packs: the pack as placed on
# the male and hulk back vs person_head.
# usage: <Blender>\5.2\python\bin\python.exe -I make_ce_gear.py <abs IsoRim17\Models> <abs out Models dir>
#        [--kit <abs IsoRim tools dir>] [--checks <abs checks.json>] [item ...]
import json, math, os, sys

FIG = sys.argv[1]
OUT = sys.argv[2]
KIT = sys.argv[sys.argv.index('--kit') + 1] if '--kit' in sys.argv else r'C:\Users\Yeyian\.claude\projects\D--SummerGames-my-game\tools'
CHECKS = sys.argv[sys.argv.index('--checks') + 1] if '--checks' in sys.argv else os.path.join(OUT, '..', 'tools', 'ce_gear_checks.json')
sys.path.insert(0, KIT)
import make_recon_plate as rp          # noqa: E402  (IsoRim's apparel kit, read only)
import make_coats as mc                 # noqa: E402
from make_recon_plate import Mesh, Ring, read_rings, KEY_U   # noqa: E402

os.makedirs(OUT, exist_ok=True)
BUILDS = ['male', 'female', 'thin', 'fat', 'hulk']
LIB = 'ce_gear.mtl'


def kd(hexcol, lift=1.1):
    h = hexcol.lstrip('#')
    return tuple(min(1.0, int(h[i:i + 2], 16) / 255.0 * lift) for i in (0, 2, 4))


# stuff_*: tinted by the item's colour (the Kd is the shade under it); others fixed. (name: (Kd or hex, metal swatch?))
COLOURS = {
    'stuff_ceg_light': ((1.0, 1.0, 1.0), False),     # white art parts: pouches, packs, vest plate, helmet, shield face
    'stuff_ceg_mid': ((0.80, 0.80, 0.80), False),    # flaps, pockets (the art's light grey shading)
    'stuff_ceg_deep': ((0.62, 0.62, 0.62), False),   # pack bottoms, knot
    'stuff_ceg_shield': ((0.69, 0.69, 0.69), False), # ballistic shield face (#B0B0B0 under the stuff mask)
    'ceg_strap': ('#202020', False),                 # tac vest / composite vest straps and belt
    'ceg_exo_main': ('#4C4C4C', True),               # exoframe plates
    'ceg_exo_dark': ('#303030', True),               # exoframe frame bars, spine, waist ring
    'ceg_exo_core': ('#707070', True),               # exoframe round cores
    'ceg_radio': ('#605838', False),                 # radio pack body (camo dropped: flat olive)
    'ceg_radio_dark': ('#303020', False),            # radio bands
    'ceg_black': ('#1C1C1C', False),                 # antenna, visor slit
    'ceg_mask': ('#606060', False),                  # CE gas mask rubber
    'ceg_mask_dark': ('#303030', False),             # straps, goggles
    'ceg_mask_light': ('#808080', False),            # filter can, valve
    'ceg_lens': ('#383038', False),                  # gas mask / goggle glass
    'ceg_cloth': ('#706860', False),                 # improvised mask cloth
    'ceg_nv_strap': ('#684820', False),              # simple NV brown harness
    'ceg_nv_harness': ('#383038', False),            # advanced NV dark harness
    'ceg_nv_body': ('#505050', True),                # NV housings
    'ceg_nv_tube': ('#606060', True),
    'ceg_nv_lens': ('#386828', False),               # green lenses
    'ceg_shield_boss': ('#808080', True),            # melee shield boss (mask black: not tinted)
    'ceg_slit': ('#2A2A2A', False),                  # ballistic shield view slit
    'stuff_coat_brown': ((0.560, 0.400, 0.270), False),       # vanilla bandolier copies (IsoRim coats.mtl values)
    'stuff_coat_brown_dark': ((0.400, 0.285, 0.195), False),
}
ORDER = list(COLOURS)


def write_mtl():
    with open(os.path.join(OUT, LIB), 'w', newline='\n') as o:
        o.write('# IsoRim Waylith - Combat Extended Pack, gear models (tools/make_ce_gear.py). stuff_* take the item colour;\n')
        o.write('# fixed colours sampled from CE\'s worn art (lifted 1.1x); metal parts carry IsoRim\'s Metal swatch.\n')
        for n, (c, metal) in COLOURS.items():
            rgb = c if isinstance(c, tuple) else kd(c)
            o.write('newmtl %s\nKd %.3f %.3f %.3f\n' % (n, rgb[0], rgb[1], rgb[2]))
            if metal:
                o.write('map_Kd IsoRim/Swatches/Metal\n')


def write(M, name, header):
    with open(os.path.join(OUT, name + '.obj'), 'w', newline='\n') as o:
        o.write('# IsoRim Waylith - Combat Extended Pack: %s (tools/make_ce_gear.py)\n' % name)
        o.write('# %s\n' % header)
        o.write('# One unit = one cell, +Y up, the figure faces -Z. Closed parts, clockwise from outside.\n')
        o.write('mtllib %s\n' % LIB)
        for v in M.v:
            o.write('v %.4f %.4f %.4f\n' % v)
        cur = None
        for a, b, c, m in sorted(M.f, key=lambda f: ORDER.index(f[3]) if f[3] in ORDER else 99):
            if m != cur:
                o.write('usemtl %s\n' % m)
                cur = m
            o.write('f %d %d %d\n' % (a + 1, c + 1, b + 1))


# ------------------------------------------------------------------ small shapes in file space
def prism_xy(M, poly, z0, z1, mat, s1=1.0, side=None):
    """closed prism: convex polygon in (x, y) at z0, the same scaled by s1 about its centre at z1."""
    f0 = len(M.f)
    cx = sum(p[0] for p in poly) / len(poly)
    cy = sum(p[1] for p in poly) / len(poly)
    A = [M.add((x, y, z0)) for x, y in poly]
    B = [M.add((cx + (x - cx) * s1, cy + (y - cy) * s1, z1)) for x, y in poly]
    ca, cb = M.add((cx, cy, z0)), M.add((cx, cy, z1))
    n = len(poly)
    for i in range(n):
        j = (i + 1) % n
        M.quad(A[i], A[j], B[j], B[i], side or mat)
        M.tri(A[j], A[i], ca, mat)
        M.tri(B[i], B[j], cb, mat)
    M.close_from(f0)


def cyl_z(M, cx, cy, z0, z1, r, mat, n=8):
    mc.disc(M, (cx, cy, (z0 + z1) / 2), r, abs(z1 - z0), mat, n=n, axis='z')


def rounded(w, y0, y1, rt, rb=0.0, steps=2):
    """convex outline in (x, y): half width w, bottom y0, top y1, corner radii rt (top) and rb (bottom)."""
    pts = []
    for k in range(steps + 1):                       # bottom right corner
        a = -math.pi / 2 + (math.pi / 2) * k / steps
        pts.append((w - rb + rb * math.cos(a), y0 + rb + rb * math.sin(a)))
    for k in range(steps + 1):                       # top right
        a = (math.pi / 2) * k / steps
        pts.append((w - rt + rt * math.cos(a), y1 - rt + rt * math.sin(a)))
    for k in range(steps + 1):                       # top left
        a = math.pi / 2 + (math.pi / 2) * k / steps
        pts.append((-w + rt + rt * math.cos(a), y1 - rt + rt * math.sin(a)))
    for k in range(steps + 1):                       # bottom left
        a = math.pi + (math.pi / 2) * k / steps
        pts.append((-w + rb + rb * math.cos(a), y0 + rb + rb * math.sin(a)))
    out = []
    for p in pts:
        if not out or (abs(p[0] - out[-1][0]) > 1e-5 or abs(p[1] - out[-1][1]) > 1e-5):
            out.append(p)
    if abs(out[0][0] - out[-1][0]) < 1e-5 and abs(out[0][1] - out[-1][1]) < 1e-5:
        out.pop()
    return out


def arc_strap(M, ring_front_z, ring_back_z, yc, ry, d_in, d_out, half_w, mat, x=0.0, n=8):
    """a strap over the crown of the head: from the front (z = -front) over the top to the back, in the y-z plane."""
    outer, inner = [], []
    for xx in (x - half_w, x + half_w):
        oc, ic = [], []
        for k in range(n + 1):
            t = math.pi * k / n
            rz = ring_front_z if t < math.pi / 2 else ring_back_z
            oc.append((xx, yc + (ry + d_out) * math.sin(t), -(rz + d_out) * math.cos(t)))
            ic.append((xx, yc + (ry + d_in) * math.sin(t), -(rz + d_in) * math.cos(t)))
        outer.append(oc)
        inner.append(ic)
    rp.slab(M, outer, inner, mat)



def in_octagon(r, x, z):
    cs = rp.corners(r)
    n = len(cs)
    sign = 0
    for i in range(n):
        (x0, z0), (x1, z1) = cs[i], cs[(i + 1) % n]
        c = (x1 - x0) * (z - z0) - (z1 - z0) * (x - x0)
        if abs(c) < 1e-12:
            continue
        sg = 1 if c > 0 else -1
        if sign == 0:
            sign = sg
        elif sg != sign:
            return False
    return True


def body_top(E, x, z, lo=0.55, hi=0.745):
    """the body surface height over (x, z): the highest ring that still holds the point."""
    top = lo
    for k in range(int((hi - lo) / 0.001) + 1):
        y = lo + 0.001 * k
        if in_octagon(E.at(y), abs(x), z):
            top = y
    return top


def slanted_strap(M, E, u_bot, u_top, y0, y1, du, d_in, d_out, mat, rows=4):
    """a strap on the body from perimeter u_bot at y0 to u_top at y1 (straps leaning outward, as the south art)."""
    outer, inner = [], []
    for side in (-1, 1):
        oc, ic = [], []
        for j in range(rows + 1):
            y = y0 + (y1 - y0) * j / rows
            u = u_bot + (u_top - u_bot) * j / rows + side * du
            oc.append(rp.surf_n(E, u, y, d_out))
            ic.append(rp.surf_n(E, u, y, d_in))
        outer.append(oc)
        inner.append(ic)
    rp.slab(M, outer, inner, mat)


def strap_u(F, xt=0.195, y=0.63, d=0.03):
    xt += 0.20 * max(0.0, F.s[0] - 1.0)          # wide builds: a little further out (their head clearance)
    """the front perimeter u where a strap stands xt out from the middle once scaled (keeps the arc off the head)."""
    want = xt / F.s[0]
    for k in range(101):
        u = 0.5 + 0.01 * k
        if rp.surf(F.E, u, y, d)[0] >= want:
            return u
    return 1.5


def shoulder_arc(M, E, u, y0, d_in, d_out, half_w, mat, n=10):
    """a strap over the shoulder top (east art: the arc) at the x of perimeter u: from the front at y0 over the shoulder
    slope to the back at y0, d_in..d_out above the body surface under it (the octagon narrows to 0.12 at the neck)."""
    f = rp.surf(E, u, y0, d_out)
    xs = abs(f[0])
    sgn = 1 if f[0] >= 0 else -1
    zf_o, zb_o = rp.surf(E, u, y0, d_out)[2], rp.surf(E, 4.0 - u, y0, d_out)[2]
    zf_i, zb_i = rp.surf(E, u, y0, d_in)[2], rp.surf(E, 4.0 - u, y0, d_in)[2]
    outer, inner = [], []
    cols = (sgn * (xs - half_w), sgn * (xs + half_w))
    for xx in cols:
        oc, ic = [], []
        for k in range(n + 1):
            c = k / n
            zo = zf_o + (zb_o - zf_o) * c
            zi = zf_i + (zb_i - zf_i) * c
            if 0 < k < n:
                h = max(body_top(E, x2, z2) for x2 in (xs - half_w, xs, xs + half_w) for z2 in (zi, (zi + zo) / 2))
                yo, yi = max(y0, h + d_out), max(y0, h + d_in)
            else:
                yo = yi = y0
            oc.append((xx, yo, zo))
            ic.append((xx, yi, zi))
        outer.append(oc)
        inner.append(ic)
    rp.slab(M, outer, inner, mat)


# ------------------------------------------------------------------ body garments (per build)
def tacvest(F):
    """CE tactical rigging (TacRigging_<Build>): a dark belt round the waist; four white pouches across the belly in two
    pairs with the belt showing between the pairs (south art), one wide pouch on the back (north art); two dark straps
    from the belt up the front, over the shoulders and down the back to the back pouch (east art: the arc), left out in 3D:
    pushed out over armour they tangled (user 2026-11-03)."""
    M = Mesh()
    E = F.E
    S, L, P = 'ceg_strap', 'stuff_ceg_light', 'stuff_ceg_mid'
    mc.loft(M, E, 0, 0, 0.300, 0.345, 0.010, 0.026, S, closed=True, extra_u=(1.0, -1.0, 3.0, -3.0))
    # front pouches: two pairs, flap on the top third
    for uc in (-0.80, -0.30, 0.30, 0.80):
        mc.loft(M, E, uc - 0.22, uc + 0.22, 0.245, 0.455, 0.024, 0.088, L)
        mc.loft(M, E, uc - 0.23, uc + 0.23, 0.390, 0.463, 0.086, 0.096, P)
    # back pouch: one wide
    mc.loft(M, E, 3.25, 4.75, 0.250, 0.455, 0.024, 0.082, L, extra_u=(4.0,))
    mc.loft(M, E, 3.23, 4.77, 0.392, 0.463, 0.080, 0.090, P, extra_u=(4.0,))
    return M


def compositevest(F):
    """CE composite vest (CompositeVest_<Build>): a white plate front and back (the colour generator's colour) from the
    belly to the chest, top corners cut; dark shoulder straps over the shoulders; two dark buckle straps across each side
    joining the plates (east art), sticking out a little past the plate edges (south art)."""
    M = Mesh()
    E = F.E
    S, L = 'ceg_strap', 'stuff_ceg_light'
    def top(u, c):
        a = abs(mc.un(u - c))
        return 0.655 - 0.050 * max(0.0, (a - 0.85) / 0.55)
    mc.loft(M, E, -1.40, 1.40, 0.335, lambda u: top(u, 0.0), 0.012, 0.046, L, extra_u=(0.85, -0.85, 0.0))
    mc.loft(M, E, 2.60, 5.40, 0.335, lambda u: top(u, 4.0), 0.012, 0.046, L, extra_u=(3.15, 4.85, 4.0))
    for s in (1, -1):
        us = strap_u(F)
        uf, ub = us * s, (4.0 - us) * s
        for uc in (uf, ub):
            mc.loft(M, E, min(uc - 0.11, uc + 0.11), max(uc - 0.11, uc + 0.11), 0.60, 0.645, 0.012, 0.060, S)
        shoulder_arc(M, E, uf, 0.630, 0.016, 0.036, 0.028, S)
        u0, u1 = (1.25, 2.75) if s > 0 else (-2.75, -1.25)
        for y in (0.425, 0.515):
            mc.loft(M, E, u0, u1, y, y + 0.034, 0.012, 0.058, S, extra_u=(2.0 * s,))
    return M


def exoframe(F):
    """CE exoframe (Exoframe_<Build>): an open dark frame over the clothes. Two big shoulder plates lifted over the
    shoulders, a chest yoke with a round core (south), a back yoke with a core and a spine down to a lower core at the
    waist (north), a waist ring, a small hip unit at the front, a bar along each side (east), two struts from the waist
    down the back of the hips (north art's two lower plates)."""
    M = Mesh()
    E = F.E
    Pm, Pd, Pc = 'ceg_exo_main', 'ceg_exo_dark', 'ceg_exo_core'
    D0, D1 = 0.016, 0.046
    # chest yoke (front), a notch at the neck
    mc.loft(M, E, -1.25, 1.25, 0.50, lambda u: 0.685 - 0.04 * max(0.0, 1 - abs(u) / 0.45), D0, D1, Pm, extra_u=(0.45, -0.45, 0.0))
    p = rp.surf(E, 0.0, 0.565, D1)
    cyl_z(M, 0.0, 0.565, p[2] - 0.016, p[2] + 0.004, 0.050, Pc)
    # back yoke + spine + cores
    mc.loft(M, E, 2.75, 5.25, 0.52, 0.690, D0, D1, Pm, extra_u=(4.0,))
    mc.loft(M, E, 3.72, 4.28, 0.26, 0.53, D0, 0.040, Pd, extra_u=(4.0,))
    for y, r in ((0.60, 0.050), (0.33, 0.044)):
        q = rp.surf(E, 4.0, y, 0.046 if y > 0.5 else 0.040)
        cyl_z(M, 0.0, y, q[2] - 0.004, q[2] + 0.016, r, Pc)
    # waist ring and hip unit
    mc.loft(M, E, 0, 0, 0.270, 0.320, 0.010, 0.030, Pd, closed=True, extra_u=(1.0, -1.0, 3.0, -3.0))
    mc.loft(M, E, -0.38, 0.38, 0.305, 0.405, 0.020, 0.048, Pd, extra_u=(0.0,))
    for s in (1, -1):
        # side bar joining the yokes
        u0, u1 = (1.15, 2.85) if s > 0 else (-2.85, -1.15)
        mc.loft(M, E, u0, u1, 0.505, 0.548, D0, 0.040, Pd, extra_u=(2.0 * s,))
        # hip strut down the back of the hip
        uc = 3.30 * s
        mc.loft(M, E, min(uc - 0.14, uc + 0.14), max(uc - 0.14, uc + 0.14), 0.13, 0.285, 0.014, 0.040, Pm)
        # shoulder plate (the art's "ears"): a big block on each shoulder, its broad face to the front (south / north art)
        # and to the side (east art), leaning out 20 degrees, its top at the chin past the head's side
        A = E.at(0.66)[0] * F.s[0]                 # scaled shoulder half width
        cx = max(A + 0.040, 0.265)                 # scaled centre x
        hx, hy, hz = 0.050, 0.088, 0.088           # half sizes (scaled space)
        cy = 0.772
        tilt = math.radians(20)
        pts = []
        for sy in (-1, 1):
            for (sx, sz) in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
                lx, ly = sx * hx, sy * hy
                x = cx + lx * math.cos(tilt) + ly * math.sin(tilt)
                y = cy - lx * math.sin(tilt) + ly * math.cos(tilt)
                pts.append((s * x / F.s[0], y, sz * hz / F.s[2]))
        if s < 0:
            pts = pts[0:1] + pts[3:0:-1] + pts[4:5] + pts[7:4:-1]
        rp.box_pts(M, pts, Pm)
    return M


# ------------------------------------------------------------------ packs (pack mount: back plane z = 0)
def backpack():
    """CE backpack (Backpack north/east/south): a white rucksack, rounded top that shows over the shoulders from the
    front (south art), a front pocket and a top flap (north art); stuff colour, the art's grey shading as darker stuff."""
    M = Mesh()
    prism_xy(M, rounded(0.150, 0.390, 0.765, 0.085, 0.030), 0.010, 0.140, 'stuff_ceg_light', s1=0.94)
    prism_xy(M, rounded(0.106, 0.430, 0.570, 0.030, 0.030), 0.120, 0.172, 'stuff_ceg_mid', s1=0.92)
    prism_xy(M, rounded(0.128, 0.635, 0.752, 0.060, 0.010), 0.110, 0.146, 'stuff_ceg_mid', s1=0.96)
    return M


def tribalpack():
    """CE tribal pack (Tribalpack): a round drawstring sack, cinched at the top into a knot (north / east art)."""
    M = Mesh()
    rp.ellipsoid(M, (0.0, 0.535, 0.072), (0.130, 0.150, 0.068), 'stuff_ceg_light', nseg=10, nring=5)
    rp.ellipsoid(M, (0.0, 0.705, 0.060), (0.050, 0.055, 0.040), 'stuff_ceg_deep', nseg=8, nring=3)
    return M


def radiopack():
    """CE radio pack (RadioPack): an olive box with two dark bands, a long whip antenna up from the top left corner
    (the figure's left, -x; north art). Camo pattern dropped."""
    M = Mesh()
    rp.box(M, (0.0, 0.530, 0.060), (0.105, 0.150, 0.056), 'ceg_radio')
    for y in (0.455, 0.610):
        rp.box(M, (0.0, y, 0.060), (0.109, 0.014, 0.060), 'ceg_radio_dark')
    rp.box(M, (-0.084, 0.695, 0.098), (0.020, 0.015, 0.018), 'ceg_radio_dark')
    rp.box(M, (-0.092, 0.850, 0.106), (0.008, 0.145, 0.008), 'ceg_black')
    return M


# ------------------------------------------------------------------ head items (person_head frame)
def head_ring():
    return Ring(read_rings(os.path.join(FIG, 'person_head.obj')))


def straight_ring(head, below):
    """the head's rings, but straight down below `below` (a visor / tail that hangs, not follows the chin)."""
    T = {}
    for y in sorted(set(head.heights() + [0.70, 0.76, 0.80, 0.84, 0.88, below])):
        T[y] = head.at(max(y, below))
    return Ring(T)


def platehelmet():
    """CE plate helmet (a sallet; PlateHelmet north/east/south): a rounded dome, a long rear tail flaring out over the
    neck (east art), a visor over the face down to the chin with a dark eye slit (south art)."""
    M = Mesh()
    head = head_ring()
    H = straight_ring(head, 0.93)
    D_IN, D_OUT = 0.030, 0.054
    def rim(u, outer):
        a = abs(mc.un(u))
        if a <= 1.6:
            return 0.795            # visor to the chin
        if a <= 2.6:
            return 0.860            # sides
        return 0.835 if outer else 0.845   # rear tail
    def flare(u, y):
        a = abs(mc.un(u))
        k = max(0.0, (a - 2.3) / 1.7)          # back: grows to the rear
        return 0.085 * k * max(0.0, (0.97 - y) / 0.135)
    us = sorted(set([0.0, 4.0 - 1e-6] + KEY_U + [1.0, -1.0, 1.6, -1.6, 2.0, -2.0, 2.6, -2.6, 3.0, -3.0]))
    us = [u for u in us if -4 < u < 4]
    levels = [0.93, 1.02, 1.10, 1.16, 1.215]
    O, I = [], []
    for u in us:
        def rows(y0):
            return [y0, (y0 + 0.93) / 2] + levels
        O.append([rp.surf_n(H, u, y, D_OUT + flare(u, y)) for y in rows(rim(u, True))])
        I.append([rp.surf_n(H, u, y, D_IN + flare(u, y)) for y in rows(rim(u, False))])
    f0 = len(M.f)
    Oi = [[M.add(p) for p in col] for col in O]
    Ii = [[M.add(p) for p in col] for col in I]
    n, nr = len(us), len(O[0])
    pt_o = M.add((0.0, 1.25 + D_OUT * 0.85, 0.0))
    pt_i = M.add((0.0, 1.25 + D_IN * 0.85, 0.0))
    L = 'stuff_ceg_light'
    for i in range(n):
        i1 = (i + 1) % n
        for j in range(nr - 1):
            M.quad(Oi[i][j], Oi[i1][j], Oi[i1][j + 1], Oi[i][j + 1], L)
            M.quad(Ii[i][j], Ii[i][j + 1], Ii[i1][j + 1], Ii[i1][j], L)
        M.tri(Oi[i][nr - 1], Oi[i1][nr - 1], pt_o, L)
        M.tri(Ii[i1][nr - 1], Ii[i][nr - 1], pt_i, L)
        M.quad(Oi[i][0], Ii[i][0], Ii[i1][0], Oi[i1][0], L)
    M.close_from(f0)
    # eye slit: a dark band set into the visor front
    rp.wrap_plate(M, H, -1.15, 1.15, 0.952, 0.982, D_OUT - 0.012, D_OUT + 0.004, 'ceg_black', extra_u=(0.0,), )
    return M


def gasmask():
    """CE gas mask (GasMask north/east/south/west): a dark grey full-face respirator over the face, two round dark
    lenses, a round snout with a light valve, a filter can on the figure's left cheek (south art: image right), dark
    straps round the back of the head (north art). Different from IsoRim's vanilla gas mask (hood shape, side filter)."""
    M = Mesh()
    head = head_ring()
    G, Gd, Gl, Ln = 'ceg_mask', 'ceg_mask_dark', 'ceg_mask_light', 'ceg_lens'
    def top(u):
        return 1.050 - 0.05 * min(1.0, abs(u) / 1.7)
    mc.loft(M, head, -1.75, 1.75, 0.790, top, 0.010, 0.034, G, extra_u=(0.0, 0.9, -0.9))
    for x in (0.082, -0.082):
        zf = -(head.at(0.952)[3] + 0.034)
        cyl_z(M, x, 0.952, zf - 0.012, zf + 0.010, 0.052, Gd, n=10)
        cyl_z(M, x, 0.952, zf - 0.018, zf - 0.004, 0.041, Ln, n=10)
    zf = -(head.at(0.86)[3] + 0.034)
    cyl_z(M, 0.0, 0.852, zf - 0.050, zf + 0.012, 0.050, G, n=10)
    cyl_z(M, 0.0, 0.852, zf - 0.062, zf - 0.044, 0.030, Gl, n=8)
    cyl_z(M, -0.112, 0.845, zf - 0.060, zf - 0.004, 0.044, Gl, n=10)
    for y in (0.955, 1.065):
        mc.loft(M, head, 1.70, 6.30, y, y + 0.030, 0.004, 0.016, Gd)
    return M


def improvgasmask():
    """CE improvised gas mask (ImprovGasMask): a cloth wrap over the nose and mouth, goggles on a dark band round the
    head with two dark lenses, a small round filter at the mouth."""
    M = Mesh()
    head = head_ring()
    mc.loft(M, head, -2.25, 2.25, 0.790, lambda u: 0.915 - 0.02 * min(1.0, abs(u) / 2.0), 0.010, 0.030, 'ceg_cloth', extra_u=(0.0, 1.0, -1.0))
    mc.loft(M, head, 0, 0, 0.930, 0.975, 0.004, 0.016, 'ceg_mask_dark', closed=True, extra_u=(1.0, -1.0, 3.0, -3.0))
    for x in (0.080, -0.080):
        zf = -(head.at(0.952)[3] + 0.016)
        cyl_z(M, x, 0.952, zf - 0.026, zf + 0.008, 0.044, 'ceg_mask_dark', n=8)
        cyl_z(M, x, 0.952, zf - 0.032, zf - 0.020, 0.035, 'ceg_lens', n=8)
    zf = -(head.at(0.86)[3] + 0.030)
    cyl_z(M, 0.0, 0.860, zf - 0.034, zf + 0.008, 0.036, 'ceg_mask_light', n=8)
    return M


def nv_harness(M, head, mat):
    """head band at the brow + a strap over the crown (both NV art: the band and the T on the back)."""
    mc.loft(M, head, 0, 0, 1.040, 1.078, 0.006, 0.020, mat, closed=True, extra_u=(1.0, -1.0, 3.0, -3.0))
    r = head.at(1.06)
    arc_strap(M, r[3], r[3], 1.06, 0.19, 0.008, 0.022, 0.022, mat)


def nv_simple():
    """CE simple night vision (SimpleNightVision): a brown harness, one dark monocular in front of the figure's right
    eye (+x; south art: image left) on a small mount from the band, green lens."""
    M = Mesh()
    head = head_ring()
    nv_harness(M, head, 'ceg_nv_strap')
    zf = -(head.at(0.952)[3] + 0.020)
    cyl_z(M, 0.082, 0.950, zf - 0.120, zf, 0.038, 'ceg_nv_body', n=8)
    cyl_z(M, 0.082, 0.950, zf - 0.128, zf - 0.112, 0.029, 'ceg_nv_lens', n=8)
    zb = -(head.at(1.06)[3] + 0.020)
    rp.box(M, (0.082, 1.012, zb - 0.034), (0.020, 0.050, 0.022), 'ceg_nv_body')
    return M


def nv_advanced():
    """CE advanced night vision (AdvancedNightVision): a dark harness, a mount block on the forehead, four short tubes
    in a row in front of the eyes with green lenses (south art)."""
    M = Mesh()
    head = head_ring()
    nv_harness(M, head, 'ceg_nv_harness')
    zb = -(head.at(1.06)[3] + 0.020)
    rp.box(M, (0.0, 1.060, zb - 0.026), (0.055, 0.026, 0.026), 'ceg_nv_body')
    rp.box(M, (0.0, 0.995, zb - 0.046), (0.130, 0.026, 0.024), 'ceg_nv_body')
    zf = -(head.at(0.952)[3] + 0.024)
    for x in (-0.099, -0.033, 0.033, 0.099):
        cyl_z(M, x, 0.950, zf - 0.090, zf, 0.031, 'ceg_nv_tube', n=8)
        cyl_z(M, x, 0.950, zf - 0.097, zf - 0.084, 0.023, 'ceg_nv_lens', n=8)
    return M


# ------------------------------------------------------------------ shields (grip at the origin, face toward -z)
def shield_melee():
    """CE melee shield (CE_Shield, one picture for all sides): an octagonal shield in the stuff colour (mask red), a grey
    boss (mask black); planks and rim lines dropped. Diameter 0.56 (256 px art, shield ~0.70 of it, node scale 0.8)."""
    M = Mesh()
    mc.disc(M, (0.0, 0.0, -0.030), 0.290, 0.030, 'stuff_ceg_light', n=8, axis='z')
    rp.ellipsoid(M, (0.0, 0.0, -0.046), (0.075, 0.075, 0.042), 'ceg_shield_boss', nseg=10, nring=3)
    return M


def shield_ballistic():
    """CE ballistic shield (CE_BallisticShield): a tall grey riot shield (stuff, mask red) bowed outward, rounded
    corners, a dark view slit near the top. Rivets dropped. 0.44 x 0.73 (art proportions x node scale 0.8)."""
    M = Mesh()
    W, Y0, Y1, T = 0.220, -0.250, 0.480, 0.026
    def front(x):
        return -0.030 - 0.036 * (1 - (x / W) ** 2)
    xs = [-W + 2 * W * k / 8 for k in range(9)]
    def ylim(x):
        a = abs(x)
        top = Y1 - max(0.0, a - 0.150) * 0.85
        bot = Y0 + max(0.0, a - 0.160) * 0.75
        return bot, top
    rows = 6
    outer, inner = [], []
    for x in xs:
        b, t = ylim(x)
        oc, ic = [], []
        for j in range(rows + 1):
            y = b + (t - b) * j / rows
            oc.append((x, y, front(x)))
            ic.append((x, y, front(x) + T))
        outer.append(oc)
        inner.append(ic)
    rp.slab(M, outer, inner, 'stuff_ceg_shield')
    so, si = [], []
    for x in (-0.125, -0.0625, 0.0, 0.0625, 0.125):
        so.append([(x, 0.300, front(x) - 0.006), (x, 0.355, front(x) - 0.006)])
        si.append([(x, 0.300, front(x) + 0.008), (x, 0.355, front(x) + 0.008)])
    rp.slab(M, so, si, 'ceg_slit')
    return M


# ------------------------------------------------------------------ checks
def head_inside(M):
    hv, hf = mc.read_obj_tris(os.path.join(FIG, 'person_head.obj'))
    return sum(1 for p in M.v if mc.inside(p, hv, hf))


def head_min_dist(points):
    hv, hf = mc.read_obj_tris(os.path.join(FIG, 'person_head.obj'))
    best = 9.0
    for p in points:
        if p[1] < 0.70:
            continue
        best = min(best, min(mc.pt_tri(p, hv[a], hv[b], hv[c]) for a, b, c in hf))
    return round(best, 4)


def pack_back(build):
    """IsoRim WornGear.Back of the bare figure (upper/lower, y 0.36-0.76, |x| <= 0.18) + gap, scaled as GearOnBody."""
    back = 0.0
    for part in ('upper', 'lower'):
        vs, _ = mc.read_obj_tris(os.path.join(FIG, 'person_%s_%s.obj' % (build, part)))
        back = max([back] + [z for x, y, z in vs if 0.36 <= y <= 0.76 and abs(x) <= 0.18])
    return (back + 0.006) * mc.SCALE[build][2]


ITEMS = {
    'tacvest': ('body', tacvest, 'CE_Apparel_TacVest: Body (Webbing layer, over shirt and coat)'),
    'compositevest': ('body', compositevest, 'CE_Apparel_CompositeVest: Body (Middle layer)'),
    'exoframe': ('body', exoframe, 'CE_Apparel_ExoFrame: Body (Shell layer, open frame; under=Whole)'),
    'backpack': ('pack', backpack, 'CE_Apparel_Backpack + CE_Apparel_KidBackpack: Belt, mount pack'),
    'tribalpack': ('pack', tribalpack, 'CE_Apparel_TribalBackpack: Belt, mount pack'),
    'radiopack': ('pack', radiopack, 'CE_Apparel_RadioPack: Belt, mount pack'),
    'platehelmet': ('head', platehelmet, 'CE_Apparel_PlateHelmet: Head'),
    'gasmask': ('head', gasmask, 'CE_Apparel_GasMask: Face (hides Beard)'),
    'improvgasmask': ('head', improvgasmask, 'CE_Apparel_ImprovGasMask: Face (hides Beard)'),
    'nv_simple': ('head', nv_simple, 'CE_Apparel_SimpleNightVision: Eyes'),
    'nv_advanced': ('head', nv_advanced, 'CE_Apparel_AdvancedNightVision: Eyes'),
    'shield_melee': ('shield', shield_melee, 'CE_Apparel_MeleeShield: Shield (grip at the origin, face -z)'),
    'shield_ballistic': ('shield', shield_ballistic, 'CE_Apparel_BallisticShield: Shield (grip at the origin, face -z)'),
}


def copy_bandolier():
    """vanilla Apparel_Bandolier under CE: CE moves it to its Webbing layer, where IsoRim's own bandolier shape no longer
    applies; the pack binds copies of IsoRim's person_<build>_bandolier.obj (geometry unchanged, materials from
    ce_gear.mtl with IsoRim's coats.mtl values)."""
    for b in BUILDS:
        src = os.path.join(FIG, 'person_%s_bandolier.obj' % b)
        lines = open(src, encoding='utf-8').read().replace('\r', '').split('\n')
        out = ['# IsoRim Waylith - Combat Extended Pack: ce_gear_bandolier_%s (copy of IsoRim Models/person_%s_bandolier.obj,' % (b, b),
               '# tools/make_ce_gear.py; vanilla Apparel_Bandolier is on CE\'s Webbing layer under CE)', 'mtllib %s' % LIB]
        out += [l for l in lines if l and not l.startswith('mtllib') and not l.startswith('#')]
        with open(os.path.join(OUT, 'ce_gear_bandolier_%s.obj' % b), 'w', newline='\n') as o:
            o.write('\n'.join(out) + '\n')


def main():
    only = [a for a in sys.argv[3:] if not a.startswith('--') and a not in (KIT, CHECKS)]
    write_mtl()
    if not only or 'bandolier' in only:
        copy_bandolier()
    res = json.load(open(CHECKS)) if os.path.exists(CHECKS) else {}
    for key, (kind, fn, header) in ITEMS.items():
        if only and key not in only:
            continue
        if kind == 'body':
            for b in BUILDS:
                F = mc.Fig(FIG, b)
                M = fn(F)
                name = 'ce_gear_%s_%s' % (key, b)
                write(M, name, header + ', build ' + b)
                r = rp.check(M)
                r.update(mc.fit_checks(M, FIG, b))
                res[name] = r
        else:
            M = fn()
            name = 'ce_gear_%s' % key
            write(M, name, header)
            r = rp.check(M)
            if kind == 'head':
                r['points_inside_head'] = head_inside(M)
            if kind == 'pack':
                for b in ('male', 'hulk', 'fat'):
                    off = pack_back(b)
                    r['head_dist_' + b] = head_min_dist([(x, y, z + off) for x, y, z in M.v])
                    r['pack_offset_' + b] = round(off, 3)
            res[name] = r
    with open(CHECKS, 'w') as o:
        json.dump(res, o, indent=1)
    for k in sorted(res):
        v = res[k]
        extra = ' '.join('%s=%s' % (x, v[x]) for x in ('points_inside_body', 'head_clearance', 'points_inside_head', 'head_dist_male', 'head_dist_hulk') if x in v)
        print('[ce_gear] %-30s tris %4d parts %2d open %d nonman %d degen %d  %s' % (k, v['tris'], v['parts'], v['open_edges'], v['nonmanifold_edges'], v['degenerate'], extra))


if __name__ == '__main__':
    main()
