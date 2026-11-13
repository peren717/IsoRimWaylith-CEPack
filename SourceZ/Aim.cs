using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRimCEPackZ
{
    // Aiming between floors (plan §1.3, §2b): the target seen in the shooter's frame.
    //
    // CE aims from the shooter's centre at the target's centre or cell in raw map rows; a target on another floor is then
    // a whole band away. One mechanism, the frame transpiler, rewrites CE's aim sites (the methods listed in Patch) so that
    // every target point they read is seen in the shooter's frame (TargetInShooterFrame: the rows moved by the bands
    // between; Layout.TranslateRows) and every cell-to-cell distance is the ground distance (GroundSub): in those methods
    //   Thing.TrueCenter(), Thing.DrawPos, IntVec3.ToVector3Shifted(), LocalTargetInfo.CenterVector3   -> in the shooter's frame
    //   IntVec3 - IntVec3, IntVec3.DistanceTo                                                           -> over the ground
    // (the shooter's own points are unchanged; on one floor every one of these is CE's own value). The target's height is
    // raised by the storeys between in GetTargetHeight (a postfix: (kt - ks) * S), and AdjustShotHeight works with those
    // heights too. Downstream CE's own maths then holds for rotation, distance and miss radius. Two things do not follow
    // from the frames and have their own step: CE's lead predicts a pawn in the pawn's own band, so its report's shot source
    // is moved there (Report_Postfix), and its vertical angle is flat-world (FireLine). The bullet leaves the shooter's band
    // at the muzzle and reaches the target's band through Frames (crossing the planes).
    // The shooter's frame is the caster's floor, or the floor of the cell CE is testing a shot from (TryFindCEShootLineFromTo:
    // root, a cast position on another floor). CE's lean cells need no read here: it asks the game's ShootLeanUtility, and
    // ProjectZ's ShootLean_Patch moves the target onto the shooter's floor there (mode firing, L1); Sight then keeps a lean to
    // the cover it passes (FireLine.Leaves).
    internal static class Aim
    {
        [ThreadStatic] private static int rootFrame;
        [ThreadStatic] private static bool rootSet;
        [ThreadStatic] private static IntVec3 rootCell;
        [ThreadStatic] private static Verb rootVerb;

        // The cell a verb fires from now: root while TryFindCEShootLineFromTo tests one, else the caster's cell.
        internal static IntVec3 RootOf(Verb verb) => rootSet ? rootCell : verb.caster.Position;

        public static void Patch(Harmony h)
        {
            Type v = typeof(Verb_LaunchProjectileCE);
            MethodInfo tryFind = ByName(v, "TryFindCEShootLineFromTo", 4);
            PackZ.Prefix(h, tryFind, typeof(Aim), nameof(Root_Prefix));
            h.Patch(tryFind, finalizer: new HarmonyMethod(typeof(Aim), nameof(Root_Finalizer)));
            foreach (MethodInfo m in new[]
            {
                tryFind,
                ByName(v, "ShiftTarget", 3),
                ByName(v, "ShiftVecReportFor", 2),
                CanHitTargetFromOut(),
                AccessTools.PropertyGetter(v, "ShotSpeed"),
                ByName(v, "KeepBurstOnNoShootLine", 2),
                ByName(typeof(Verb_ShootCE), "WarmupComplete", 0),
                ByName(typeof(Verb_ShootCE), "CanHitTargetFrom", 2),
                ByName(typeof(Verb_ShootCE), "RecalculateWarmupTicks", 0),
                AccessTools.PropertyGetter(typeof(Verb_ShootCE), "AimAngle"),
                ByName(typeof(Verb_ShootFlareCE), "ShiftVecReportFor", 1),
                ByName(typeof(Verb_ThrowGrenade), "TryCastShot", 0),
            })
            {
                if (m == null)
                {
                    throw new MissingMethodException("a CE aim method");
                }
                PackZ.Transpiler(h, m, typeof(Aim), nameof(Frame_Transpiler));
            }
            PackZ.Postfix(h, ByName(v, "GetTargetHeight", 4), typeof(Aim), nameof(TargetHeight_Postfix));
            PackZ.Prefix(h, ByName(v, "AdjustShotHeight", 3), typeof(Aim), nameof(AdjustShotHeight_Prefix));
            try
            {
                ceReport = PackZ.NonVirtual<Func<Verb_LaunchProjectileCE, LocalTargetInfo, IntVec3, ShiftVecReport>>(ByName(v, "ShiftVecReportFor", 2));
            }
            catch (Exception e)
            {
                // (only the light and weather of shots between floors are then CE's for the shooter-frame cell)
                PackZ.Once("cereport", "Combat Extended's ShiftVecReportFor could not be bound (" + PackZ.Reason(e) + "): light and weather of shots between floors are read at the shooter-frame cell.");
            }
            PackZ.Postfix(h, ByName(v, "ShiftVecReportFor", 2), typeof(Aim), nameof(Report_Postfix));
            PackZ.Transpiler(h, ByName(typeof(Verb_ShootMortarCE), "ShiftVecReportFor", 2), typeof(Aim), nameof(Spotting_Transpiler));
            PackZ.Prefix(h, ByName(v, "ShotAngle", 2), typeof(Aim), nameof(ShotAngle_Prefix));
            PackZ.Postfix(h, ByName(v, "ShotAngle", 2), typeof(Aim), nameof(ShotAngle_Postfix));
            PackZ.Prefix(h, LaunchMethod(8), typeof(Aim), nameof(LaunchFull_Prefix));
            PackZ.Prefix(h, LaunchMethod(3), typeof(Aim), nameof(LaunchInner_Prefix));
            shotSpeed =AccessTools.MethodDelegate<Func<Verb_LaunchProjectileCE, float>>(AccessTools.PropertyGetter(v, "ShotSpeed"));
            FireLine.Patch(h);
        }

        // Not key (a failure leaves these reads as CE has them: a lead on a stair step, the order of the lean cells): the
        // pawn's next cell in CE's lead, and the ground distance in TryFindCEShootLineFromTo's sort of the lean cells.
        public static void PatchExtras(Harmony h)
        {
            foreach (string name in new[] { "PredictPawnPos", nameof(ShiftVecReport.GetRandLeadVec) })
            {
                PackZ.Transpiler(h, AccessTools.Method(typeof(ShiftVecReport), name), typeof(Aim), nameof(NextCell_Transpiler));
            }
            MethodInfo distance = AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceTo), new[] { typeof(IntVec3), typeof(IntVec3) });
            foreach (MethodInfo m in Closures(typeof(Verb_LaunchProjectileCE), "TryFindCEShootLineFromTo"))
            {
                if (PatchProcessor.GetOriginalInstructions(m).Any(ci => ci.operand is MethodInfo mi && mi == distance))
                {
                    PackZ.Transpiler(h, m, typeof(Aim), nameof(RootDistance_Transpiler));
                }
            }
        }

        // The compiler's lambdas of a method (the name may find nothing after a CE rebuild: the order then stays CE's own).
        private static IEnumerable<MethodInfo> Closures(Type t, string method) => new[] { t }.Concat(t.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            .SelectMany(x => x.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.StartsWith("<" + method + ">"));

        private static Func<Verb_LaunchProjectileCE, float> shotSpeed;

        internal static float Speed(Verb_LaunchProjectileCE verb) => shotSpeed(verb);

        // CE's ballistics: the weapon's reach is set by its throw (see LobRangeFactor).
        internal static bool Slow(Verb_LaunchProjectileCE verb, ProjectilePropertiesCE props)
        {
            float v = shotSpeed(verb), g = props.GravityPerWidth;
            return g > 0f && v * v <= LobRangeFactor * g * verb.verbProps.range;
        }
        public static int HighArcs, LowArcsBlocked;   // (LowArcsBlocked: aimed low arcs that rise into a slab, fast or slow)

        // A slow direct-fire shot (a thrown grenade) at a target on a higher floor: CE's low arc rises through the floor
        // plane of the target's storey; where that is a solid slab the shot would hit the slab's underside. Then the high
        // arc is taken if its own way up passes an open plane (a lob onto the floor).
        // Slow, by CE's own ballistics: the weapon's reach is set by its throw, not by its aim, i.e. the shot's level
        // maximum range v^2 / g (ShotSpeed, GravityPerWidth) is at most LobRangeFactor times the verb's range. CE stock
        // (default projectiles): the hand grenades 5-7.3, the stick bomb 10.5, pila 7.4, the great bow 7.5 and CE's grenade
        // launcher 8.5 lob; next come the incinerator 18.6, the short and recurve bows and the incendiary launcher 35-37 and
        // rifles about 260.
        // Any faster shot keeps CE's low arc: its high arc would be a near-vertical shot into the sky (a rifle's climbs
        // thousands of cells, and CE locks a burst's angle, so the whole burst went up). The low arc is then judged as
        // physics has it: it runs almost on the aim line, and the aim point moves per shot (CE's range estimate, miss circle
        // and a random height on the target's body), so near a slab edge some shots cross the plane over the slab's first
        // cells and hit its edge, as cover would stop them.
        public const float LobRangeFactor = 15f;

        public static void ShotAngle_Postfix(Verb_LaunchProjectileCE __instance, Vector3 source, Vector3 targetPos, ref float __result)
        {
            Map map = __instance.caster?.Map;
            Layout L = Layout.Of(map);
            float S = Layout.S;
            if (L == null || targetPos.y < S || float.IsNaN(__result))
            {
                return;
            }
            ProjectilePropertiesCE props = __instance.projectilePropsCE;
            if (props == null || props.flyOverhead || props.isInstant)
            {
                return;
            }
            int ks = rootSet ? rootFrame : L.FrameOf(source);
            if (ks == Layout.None || ks >= L.max)
            {
                return;
            }
            float v = shotSpeed(__instance), g = props.GravityPerWidth;
            Vector2 flat = new Vector2(targetPos.x - source.x, targetPos.z - source.z);
            float d = flat.magnitude;
            if (d < 0.5f || v <= 0f || g <= 0f)
            {
                return;
            }
            flat /= d;
            // (a slow shot's low arc by the lob's own rule, LobClear: every plane between over open cells, with its margin
            // and CE's roof band under a slab; a fast one's where it first rises through the plane, as physics has it)
            bool slow = v * v <= LobRangeFactor * g * __instance.verbProps.range;
            if (slow ? LobClear(L, map, source, flat, d, __result, v, g, ks, TargetFrame(L, targetPos, ks)) : PlaneOpenAt(L, map, source, flat, __result, v, g, S, ks))
            {
                return;
            }
            LowArcsBlocked++;
            if (!slow)
            {
                return;   // fast: no lob (see above); the low arc meets the slab
            }
            switch (SlowArc(L, map, source, targetPos, flat, d, v, g, ks, __result, out float angle, out float lobU))
            {
                case Arc.Lob:
                    // (the speed reaches the projectile through Launch: CE launches at no less than the def's speed)
                    PackZ.Changed++;
                    __result = angle;
                    lobVerb = __instance;
                    lobSpeed = lobU;
                    HighArcs++;
                    LowLobs++;
                    return;
                case Arc.High:
                    PackZ.Changed++;
                    __result = angle;
                    HighArcs++;
                    return;
                case Arc.None:
                    // no arc gets past the slab: the shot is not fired (LaunchFull_Prefix), never the arc known to hit it.
                    // Sight refuses such a shot before it is ordered (SlowShotClears); this is the aim point of one shot.
                    PackZ.Changed++;
                    ArcsRefused++;
                    refuseVerb = __instance;
                    PackZ.Once("arc", "A slow shot up to another floor found no arc past the floor slab on its way: not fired (noted once).");
                    return;
            }
        }

        public static int ArcsRefused;
        private static Verb_LaunchProjectileCE refuseVerb;

        internal enum Arc { Unreachable, Lob, High, None }

        // The storey of a target point above the shooter's (heights in the shooter's frame; at least one up).
        private static int TargetFrame(Layout L, Vector3 targetPos, int ks) => Mathf.Min(L.max, ks + Mathf.Max(1, Mathf.FloorToInt((targetPos.y + 0.01f) / Layout.S)));

        // A slow shot whose low arc rises into a solid floor plane (the caller checked it): the lowest lob onto the target,
        // else CE's full-speed high arc when it rises through every floor plane between the two storeys over open cells
        // (LobClear, the lob's own rule); None: no arc clears (Unreachable: out of the shot's reach, CE's rules decide).
        internal static Arc SlowArc(Layout L, Map map, Vector3 source, Vector3 targetPos, Vector2 flat, float d, float v, float g, int ks, float low, out float angle, out float lobU)
        {
            float dh = targetPos.y - source.y;
            int kt = TargetFrame(L, targetPos, ks);
            if (LowestLob(L, map, source, flat, d, dh, g, v, ks, kt, low, out angle, out lobU))
            {
                return Arc.Lob;
            }
            lobU = 0f;
            float disc = v * v * v * v - g * (g * d * d + 2f * dh * v * v);
            if (disc < 0f)
            {
                angle = low;
                return Arc.Unreachable;
            }
            angle = Mathf.Atan((v * v + Mathf.Sqrt(disc)) / (g * d));
            if (LobClear(L, map, source, flat, d, angle, v, g, ks, kt))
            {
                return Arc.High;
            }
            angle = low;
            return Arc.None;
        }

        // Sight between floors (the line passed): a slow direct-fire shot up has an arc past the floor planes on its way
        // (CE's low arc, a lob or the high arc: ShotAngle's rule, from the shooter's shot height to the target's middle).
        // False: no line for that weapon (CE's "no line of sight", the order is refused as for a slab in the way).
        internal static bool SlowShotClears(Verb_LaunchProjectileCE verb, Vector3 source, Vector3 targetPos, Layout L, Map map, int ks)
        {
            ProjectilePropertiesCE props = verb.projectilePropsCE;
            if (verb is Verb_ThrowGrenade || props == null || props.flyOverhead || props.isInstant || shotSpeed == null || targetPos.y < Layout.S || ks == Layout.None || ks >= L.max)
            {
                return true;   // (a throw works out its own arc: Throws, FindAngle refuses one with none past the slab)
            }
            float v = shotSpeed(verb), g = props.GravityPerWidth;
            Vector2 flat = new Vector2(targetPos.x - source.x, targetPos.z - source.z);
            float d = flat.magnitude;
            if (d < 0.5f || v <= 0f || g <= 0f || v * v > LobRangeFactor * g * verb.verbProps.range)
            {
                return true;
            }
            float dh = targetPos.y - source.y;
            float disc = v * v * v * v - g * (g * d * d + 2f * dh * v * v);
            if (disc < 0f)
            {
                return true;   // out of its reach: CE's own rules
            }
            flat /= d;
            float low = Mathf.Atan((v * v - Mathf.Sqrt(disc)) / (g * d));
            if (LobClear(L, map, source, flat, d, low, v, g, ks, TargetFrame(L, targetPos, ks)))
            {
                return true;
            }
            if (SlowArc(L, map, source, targetPos, flat, d, v, g, ks, low, out _, out _) != Arc.None)
            {
                return true;
            }
            SightRefused++;
            return false;
        }

        public static int SightRefused;

        // ---- the lowest lob (user decision 2026-10-30: a lob up a floor flies about 1-2 s, not CE's full-speed high arc)
        // Of the arcs that land on the target (each launch angle has one speed that lands there), the lowest whose way up
        // rises through every floor plane between the two storeys over an open cell, and still over open plane LobMargin
        // further on (aim slack), at a speed the weapon reaches. Angles in LobStep steps from just over the given minimum
        // (CE's own low arc, or its lift over cover). Heights from the source's floor (source.y: the launch height there);
        // d, dh, g and the speeds in the caller's units (CE's ShotAngle: per second; a throw: per tick).
        public const float LobMargin = 0.5f;
        private const float LobStep = 0.5f * Mathf.Deg2Rad, LobTop = 88f * Mathf.Deg2Rad;
        public static int LowLobs;

        // Harness: -cepackoldlob keeps the full-speed high arc (the P2 lob) to compare.
        private static readonly bool OldLobs = GenCommandLine.CommandLineArgPassed("cepackoldlob");

        internal static bool LowestLob(Layout L, Map map, Vector3 source, Vector2 flat, float d, float dh, float g, float vMax, int ks, int kt, float aMin,
            out float angle, out float speed)
        {
            angle = speed = 0f;
            if (OldLobs || L == null || kt <= ks || d < 0.5f || g <= 0f || vMax <= 0f)
            {
                return false;
            }
            for (float a = Mathf.Max(aMin, Mathf.Atan2(dh, d)) + LobStep; a < LobTop; a += LobStep)
            {
                float c = Mathf.Cos(a);
                float rise = d * Mathf.Tan(a) - dh;
                if (rise <= 0f)
                {
                    continue;
                }
                float u = Mathf.Sqrt(g * d * d / (2f * c * c * rise));
                if (u > vMax)
                {
                    continue;
                }
                if (LobClear(L, map, source, flat, d, a, u, g, ks, kt))
                {
                    angle = a;
                    speed = u;
                    return true;
                }
            }
            return false;
        }

        internal static bool LobClear(Layout L, Map map, Vector3 source, Vector2 flat, float d, float a, float u, float g, int ks, int kt)
        {
            float c = Mathf.Cos(a);
            float A = g / (2f * u * u * c * c), B = Mathf.Tan(a);
            string dbg = null;
            for (int k = ks + 1; k <= kt; k++)
            {
                float H = (k - ks) * Layout.S - source.y;
                float disc = B * B - 4f * A * H;
                if (disc < 0f)
                {
                    return false;
                }
                float x = (B - Mathf.Sqrt(disc)) / (2f * A);
                if (x > d || PlaneSolidAt(L, map, source, flat, x, ks, k) || PlaneSolidAt(L, map, source, flat, Mathf.Min(x + LobMargin, d), ks, k))
                {
                    return false;
                }
                // under a roofed (slabbed) cell CE's roof collision starts at its roof height (CollisionVertical
                // .WallCollisionHeight over the storey's floor, under the plane): from where the arc rises past that
                // height to where it passes the plane, every cell must be open (not the last slab cell before an opening)
                float Hr = (k - 1 - ks) * Layout.S + CollisionVertical.WallCollisionHeight - source.y;
                float discR = B * B - 4f * A * Hr;
                float xr = Hr <= 0f || discR < 0f ? 0f : Mathf.Max(0f, (B - Mathf.Sqrt(discR)) / (2f * A));
                dbg = LobDebug == null ? null : dbg + "    lob " + (a * Mathf.Rad2Deg).ToString("0.0") + " deg u " + u.ToString("0.000") + " plane " + k + " at x " + x.ToString("0.00") + ", roof height from x " + xr.ToString("0.00") + " (source " + source.ToString("F2") + ", flat " + flat.ToString("F2") + ")";
                for (float s = xr; s < x; s += RoofStep)
                {
                    if (PlaneSolidAt(L, map, source, flat, s, ks, k))
                    {
                        RoofEdges++;
                        return false;
                    }
                }
            }
            if (dbg != null)
            {
                LobDebug(dbg);
            }
            return true;
        }

        private const float RoofStep = 0.2f;
        public static int RoofEdges;   // harness: arcs refused for passing a roof's height under a slab before its opening
        public static Action<string> LobDebug;   // harness

        private static bool PlaneSolidAt(Layout L, Map map, Vector3 source, Vector2 flat, float x, int ks, int k)
        {
            IntVec3 g0 = new IntVec3(Mathf.FloorToInt(source.x + flat.x * x), 0, Mathf.FloorToInt(source.z + flat.y * x) - L.Shift(ks));
            return ProjectZ.Api.PlaneSolid(g0, k, map);
        }

        // The lob's speed for CE's launch (ShotAngle picked the lob; a locked burst keeps it with its angle): CE's Launch
        // sets the projectile's speed to at least its def's speed, then its inner Launch builds the flight from that field.
        private static Verb_LaunchProjectileCE lobVerb;
        private static float lobSpeed;
        private static ProjectileCE lobProjectile;
        private static float lobProjectileSpeed;

        public static void ShotAngle_Prefix(Verb_LaunchProjectileCE __instance)
        {
            if (lobVerb == __instance)
            {
                lobVerb = null;
            }
            if (refuseVerb == __instance)
            {
                refuseVerb = null;
            }
        }

        public static int LaunchesRefused;

        public static bool LaunchFull_Prefix(ProjectileCE __instance, Thing launcher, Thing equipment)
        {
            Verb_LaunchProjectileCE r = refuseVerb;
            if (r != null && (equipment != null ? equipment == r.EquipmentSource : launcher == r.caster))
            {
                // ShotAngle found no arc past the slab for this shot: not launched (the round is spent, as a shot into cover)
                refuseVerb = null;
                LaunchesRefused++;
                if (!__instance.Destroyed)
                {
                    __instance.Destroy();
                }
                return false;
            }
            Verb_LaunchProjectileCE v = lobVerb;
            if (v != null && (equipment != null ? equipment == v.EquipmentSource : launcher == v.caster))
            {
                lobProjectile = __instance;
                lobProjectileSpeed = lobSpeed;
            }
            return true;
        }

        public static void LaunchInner_Prefix(ProjectileCE __instance)
        {
            if (lobProjectile != null && lobProjectile == __instance)
            {
                __instance.shotSpeed = lobProjectileSpeed;
                lobProjectile = null;
            }
        }

        private static MethodInfo LaunchMethod(int args) => typeof(ProjectileCE).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "Launch" && m.GetParameters().Length == args) ?? throw new MissingMethodException("ProjectileCE.Launch/" + args);

        // The arc (CE's flat-earth parabola: y = h0 + x tan a - g x^2 / (2 v^2 cos^2 a)) first rises through height S over
        // an open plane (or never reaches it).
        private static bool PlaneOpenAt(Layout L, Map map, Vector3 source, Vector2 flat, float angle, float v, float g, float S, int ks)
        {
            float c = Mathf.Cos(angle);
            float A = g / (2f * v * v * c * c), B = Mathf.Tan(angle), C = S - source.y;
            float disc = B * B - 4f * A * C;
            if (disc < 0f || A <= 0f)
            {
                return true;
            }
            float x = (B - Mathf.Sqrt(disc)) / (2f * A);
            if (x < 0f)
            {
                return true;
            }
            IntVec3 g0 = new IntVec3(Mathf.FloorToInt(source.x + flat.x * x), 0, Mathf.FloorToInt(source.z + flat.y * x) - L.Shift(ks));
            return !ProjectZ.Api.PlaneSolid(g0, ks + 1, map);
        }

        internal static MethodInfo ByName(Type t, string name, int args)
        {
            MethodInfo m = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args);
            if (m == null)
            {
                throw new MissingMethodException(t.Name + "." + name + "/" + args);
            }
            return m;
        }

        private static MethodInfo CanHitTargetFromOut() => typeof(Verb_LaunchProjectileCE).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .First(m => m.Name == "CanHitTargetFrom" && m.GetParameters().Length == 3);

        // ------------------------------------------------------------------ frames
        // The shooter's floor for verb v (None: no floors, or a void row).
        internal static int ShooterFrame(Verb v, Layout L)
        {
            if (rootSet)
            {
                return rootFrame;
            }
            Thing c = v?.caster;
            return c == null ? Layout.None : L.FrameOf(c.Position);
        }

        // A target point (any band) seen in the shooter's frame: its rows moved onto the shooter's band.
        public static Vector3 TargetInShooterFrame(Vector3 p, Verb v)
        {
            Layout L = Layout.Of(QueryContext.MapOf(v?.caster));
            if (L == null)
            {
                return p;
            }
            int ks = ShooterFrame(v, L);
            int kp = L.FrameOf(p);
            if (kp != ks && kp != Layout.None && ks != Layout.None)
            {
                PackZ.Changed++;
            }
            return L.TranslateRows(p, kp, ks);
        }

        // Storeys from the shooter up to the target (0 on one floor, no floors, void rows).
        internal static int FloorsUp(Verb v, IntVec3 target)
        {
            Layout L = Layout.Of(QueryContext.MapOf(v?.caster));
            if (L == null)
            {
                return 0;
            }
            int ks = ShooterFrame(v, L);
            int kt = L.FrameOf(target);
            return ks == Layout.None || kt == Layout.None ? 0 : kt - ks;
        }

        // A thing another mod gives its own aim (Api target geometry): that point, in the shooter's frame.
        private static bool CustomPoint(Thing t, Verb v, out Vector3 p)
        {
            p = default;
            if (!TargetGeometry.Active || !TargetGeometry.TryAim(t, v, RootOf(v), out TargetAim aim))
            {
                return false;
            }
            Layout l = Layout.Of(QueryContext.MapOf(v.caster));
            if (l == null)
            {
                return false;
            }
            p = l.FromVirtual(aim.PointVirtual, ShooterFrame(v, l));
            return true;
        }

        public static Vector3 TrueCenterIn(Thing t, Verb v) => CustomPoint(t, v, out Vector3 p) ? p : TargetInShooterFrame(t.TrueCenter(), v);

        public static Vector3 DrawPosIn(Thing t, Verb v) => CustomPoint(t, v, out Vector3 p) ? p : TargetInShooterFrame(t.DrawPos, v);

        public static Vector3 ShiftedIn(ref IntVec3 c, Verb v) => TargetInShooterFrame(c.ToVector3Shifted(), v);

        public static Vector3 CenterIn(ref LocalTargetInfo t, Verb v) => t.HasThing && CustomPoint(t.Thing, v, out Vector3 p) ? p : TargetInShooterFrame(t.CenterVector3, v);

        // a - b over the ground (cells on two floors: their ground cells); a - b when on one floor.
        public static IntVec3 GroundSub(IntVec3 a, IntVec3 b, Verb v)
        {
            Layout L = Layout.Of(QueryContext.MapOf(v?.caster));
            if (L == null)
            {
                return a - b;
            }
            int ka = L.FrameOf(a), kb = L.FrameOf(b);
            if (ka == kb || ka == Layout.None || kb == Layout.None)
            {
                return a - b;
            }
            PackZ.Changed++;
            return L.Ground(a) - L.Ground(b);
        }

        public static float GroundDistance(IntVec3 a, IntVec3 b, Verb v) => GroundSub(a, b, v).LengthHorizontal;

        public static bool CrossFloor(Verb v, IntVec3 target) => FloorsUp(v, target) != 0;

        // ------------------------------------------------------------------ the frame transpiler
        private static readonly Dictionary<MethodBase, MethodInfo> swaps = new Dictionary<MethodBase, MethodInfo>
        {
            { AccessTools.Method(typeof(GenThing), nameof(GenThing.TrueCenter), new[] { typeof(Thing) }), AccessTools.Method(typeof(Aim), nameof(TrueCenterIn)) },
            { AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos)), AccessTools.Method(typeof(Aim), nameof(DrawPosIn)) },
            { AccessTools.Method(typeof(IntVec3), nameof(IntVec3.ToVector3Shifted)), AccessTools.Method(typeof(Aim), nameof(ShiftedIn)) },
            { AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.CenterVector3)), AccessTools.Method(typeof(Aim), nameof(CenterIn)) },
            { AccessTools.Method(typeof(IntVec3), "op_Subtraction", new[] { typeof(IntVec3), typeof(IntVec3) }), AccessTools.Method(typeof(Aim), nameof(GroundSub)) },
            { AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceTo), new[] { typeof(IntVec3), typeof(IntVec3) }), AccessTools.Method(typeof(Aim), nameof(GroundDistance)) },
        };

        public static int Swapped;

        // The swaps per aim method (its last transpile; harness). A method where none is found fails its group (a CE update
        // read the target another way there): the aim group is key, so shots between floors are then refused.
        public static readonly Dictionary<string, int> SwapsPer = new Dictionary<string, int>();

        public static IEnumerable<CodeInstruction> Frame_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            if (__originalMethod.IsStatic || !typeof(Verb).IsAssignableFrom(__originalMethod.DeclaringType))
            {
                throw new InvalidOperationException("frame transpiler on " + __originalMethod.Name);
            }
            string key = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
            bool fault = Fault.Is("aimswap") && __originalMethod.Name == "ShiftTarget";   // harness: a method CE changed
            int n = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (!fault && (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodBase mb && swaps.TryGetValue(mb, out MethodInfo mine))
                {
                    // the call's arguments are on the stack: add the verb (this) and call the frame version
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci).MoveBlocksFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, mine);
                    n++;
                    continue;
                }
                yield return ci;
            }
            SwapsPer[key] = n;
            if (n == 0)
            {
                throw new InvalidOperationException("frame transpiler: no target read found in " + key);
            }
            Swapped += n;
        }

        // TryFindCEShootLineFromTo tests a shot from root, which may be a cell on another floor than the caster.
        public static void Root_Prefix(Verb_LaunchProjectileCE __instance, IntVec3 root, out (bool, int, IntVec3, Verb) __state)
        {
            __state = (rootSet, rootFrame, rootCell, rootVerb);
            rootCell = root;
            rootVerb = __instance;
            Layout L = Layout.Of(__instance.caster?.Map);
            if (L == null)
            {
                return;
            }
            rootFrame = L.FrameOf(root);
            rootSet = true;
        }

        public static Exception Root_Finalizer(Exception __exception, (bool, int, IntVec3, Verb) __state)
        {
            rootSet = __state.Item1;
            rootFrame = __state.Item2;
            rootCell = __state.Item3;
            rootVerb = __state.Item4;
            return __exception;
        }

        // A lambda of TryFindCEShootLineFromTo has no verb to hand: the verb of the shot being tested.
        public static float RootGroundDistance(IntVec3 a, IntVec3 b) => rootVerb != null ? GroundDistance(a, b, rootVerb) : a.DistanceTo(b);

        public static IEnumerable<CodeInstruction> RootDistance_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo distance = AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceTo), new[] { typeof(IntVec3), typeof(IntVec3) });
            MethodInfo mine = AccessTools.Method(typeof(Aim), nameof(RootGroundDistance));
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == OpCodes.Call && ci.operand is MethodInfo mi && mi == distance)
                {
                    ci.operand = mine;
                }
                yield return ci;
            }
        }

        // ------------------------------------------------------------------ the next cell in CE's lead
        // On a stair step a pawn's next cell is on the next floor's band while it is still drawn on its own; CE's lead takes
        // its heading from that cell. Seen on the pawn's own band (ShiftVecReport: PredictPawnPos, GetRandLeadVec).
        public static IntVec3 NextCellOf(Pawn_PathFollower pather, ShiftVecReport report)
        {
            IntVec3 next = pather.nextCell;
            Pawn p = report.targetPawn;
            Layout L = Layout.Of(p?.Map);
            if (L == null)
            {
                return next;
            }
            int kp = L.FrameOf(p.Position), kn = L.FrameOf(next);
            if (kp == kn || kp == Layout.None || kn == Layout.None)
            {
                return next;
            }
            PackZ.Changed++;
            return L.Cell(L.Ground(next), kp);
        }

        private static IntVec3 nextCellHeld;

        public static ref IntVec3 NextCellRef(Pawn_PathFollower pather, ShiftVecReport report)
        {
            nextCellHeld = NextCellOf(pather, report);
            return ref nextCellHeld;
        }

        public static IEnumerable<CodeInstruction> NextCell_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            FieldInfo next = AccessTools.Field(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.nextCell));
            int found = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Ldfld || ci.opcode == OpCodes.Ldflda) && ci.operand is FieldInfo fi && fi == next)
                {
                    string mine = ci.opcode == OpCodes.Ldfld ? nameof(NextCellOf) : nameof(NextCellRef);
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci).MoveBlocksFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Aim), mine));
                    found++;
                    continue;
                }
                yield return ci;
            }
            if (found == 0)
            {
                throw new InvalidOperationException("ShiftVecReport." + __originalMethod.Name + ": no read of a pawn's next cell");
            }
        }

        // ------------------------------------------------------------------ heights
        // GetTargetHeight: CE's local height over the target's floor, plus the storeys between (the shooter's frame).
        public static void TargetHeight_Postfix(Verb_LaunchProjectileCE __instance, LocalTargetInfo target, ref float __result)
        {
            // (a thing another mod gives its own aim: that point's height in the shooter's frame, plus CE's aim offset)
            if (TargetGeometry.Active && CustomPoint(target.Thing, __instance, out Vector3 custom))
            {
                __result = custom.y + __instance.projectilePropsCE.aimHeightOffset;
                return;
            }
            int up = FloorsUp(__instance, target.Cell);
            if (up != 0)
            {
                PackZ.Changed++;
                __result += up * Layout.S;
            }
        }

        private delegate bool CoverFn(Verb_LaunchProjectileCE verb, LocalTargetInfo target, out Thing cover, out float smoke, out bool roofed);

        internal static readonly Func<Thing, Bounds> boundsFor = AccessTools.MethodDelegate<Func<Thing, Bounds>>(AccessTools.Method(typeof(CE_Utility), Fault.Name(nameof(CE_Utility.GetBoundsFor), "aim"), new[] { typeof(Thing) }));
        private static CoverFn coverFn;

        // Copied from CE Verb_LaunchProjectileCE.AdjustShotHeight (watched, CopyWatches): raise the shot over the cover in
        // front of a taller target, with the target's and its cover's heights in the shooter's frame and the ground distance
        // (CE's bounds heights are over each thing's own floor, its distance is DistanceTo: neither is answerable by a
        // scope). One floor: CE's own.
        public static bool AdjustShotHeight_Prefix(Verb_LaunchProjectileCE __instance, Thing caster, LocalTargetInfo target, ref float shotHeight, ref float __result)
        {
            int up = FloorsUp(__instance, target.Cell);
            if (up == 0)
            {
                return true;
            }
            PackZ.Changed++;
            coverFn ??= AccessTools.MethodDelegate<CoverFn>(AccessTools.Method(typeof(Verb_LaunchProjectileCE), "GetHighestCoverAndSmokeForTarget"));
            coverFn(__instance, target, out Thing cover, out float _, out bool _);
            float rise = up * Layout.S;
            float casterTop = boundsFor(caster).max.y;
            float coverTop = rise + (cover != null ? boundsFor(cover).max.y : 0f);
            float targetTop = rise + (target.Thing != null ? boundsFor(target.Thing).max.y : 0f);
            float num = (targetTop - coverTop) / 2f + coverTop;
            if (num > shotHeight)
            {
                if (num > casterTop)
                {
                    num = casterTop;
                }
                float dist = GroundDistance(target.Cell, caster.Position, __instance);
                __result = 0f - Mathf.Atan2(num - shotHeight, Mathf.Max(0.01f, dist));
                shotHeight = num;
                return false;
            }
            __result = 0f;
            return false;
        }

        // ShiftVecReportFor(target, cell): TryCastShot passes the target cell in the shooter's frame; the light and the
        // weather are those of the real target cell: taken from CE's own report for that cell (the base method, called
        // non-virtually, so a subclass's additions such as the mortar's spotting don't run twice).
        // The report's shot source: its one reader is CE's lead (GetRandLeadVec), which takes it from a position in the
        // target's band. So for a pawn on another floor it is moved there, and it is NOT the true shot source any more.
        // Recheck on CE updates that nothing else reads it (the CE reads snapshot, mode cereads, fails if a reader appears).
        public static void Report_Postfix(Verb_LaunchProjectileCE __instance, LocalTargetInfo target, IntVec3 targetCell, ShiftVecReport __result)
        {
            if (__result != null && target.Thing is Pawn)
            {
                Layout L = Layout.Of(__instance.caster?.Map);
                if (L != null)
                {
                    int ks = ShooterFrame(__instance, L), kt = L.FrameOf(target.Cell);
                    if (ks != kt && ks != Layout.None && kt != Layout.None)
                    {
                        PackZ.Changed++;
                        shotSourceRef(__result).y += L.Shift(kt) - L.Shift(ks);
                    }
                }
            }
            if (__result == null || !target.IsValid || target.Cell == targetCell || !CrossFloor(__instance, target.Cell))
            {
                return;
            }
            if (ceReport == null)
            {
                return;
            }
            ShiftVecReport real = ceReport(__instance, target, target.Cell);   // (this postfix returns at once on that call)
            if (real == null)
            {
                return;
            }
            PackZ.Changed++;
            __result.lightingShift = real.lightingShift;
            __result.weatherShift = real.weatherShift;
        }

        private static readonly AccessTools.FieldRef<ShiftVecReport, Vector2> shotSourceRef = AccessTools.FieldRefAccess<ShiftVecReport, Vector2>("shotSource");

        // CE's Verb_LaunchProjectileCE.ShiftVecReportFor(target, cell), non-virtual (bound in Patch).
        private static Func<Verb_LaunchProjectileCE, LocalTargetInfo, IntVec3, ShiftVecReport> ceReport;

        // ------------------------------------------------------------------ mortar spotting (D3: LineClear3D)
        public static IEnumerable<CodeInstruction> Spotting_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo los = AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight),
                new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(bool), typeof(Func<IntVec3, bool>), typeof(int), typeof(int) });
            MethodInfo mine = AccessTools.Method(typeof(Aim), nameof(Spotting));
            int found = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == OpCodes.Call && ci.operand is MethodInfo mi && mi == los)
                {
                    // (the verb and its target on top of the call's arguments: the heights are theirs)
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci);
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    ci.operand = mine;
                    found++;
                }
                yield return ci;
            }
            if (found == 0)
            {
                throw new InvalidOperationException("Verb_ShootMortarCE.ShiftVecReportFor: no GenSight.LineOfSight call");
            }
        }

        public static int SpottingChecks;

        // The spotter's sight from the mortar to the target: GenSight on one floor, ProjectZ's 3D line between floors, from
        // the spotter's eyes to the target's middle (heights from CE's CollisionVertical; a cell target: Sight's one rule for
        // aiming at a cell on another floor, Sight.CellAims).
        public static bool Spotting(IntVec3 start, IntVec3 end, Map map, bool skipFirstCell, Func<IntVec3, bool> validator, int halfXOffset, int halfZOffset,
            Verb_ShootMortarCE verb, LocalTargetInfo target)
        {
            Layout L = Layout.Of(map);
            if (L != null)
            {
                int ks = L.FrameOf(start), kt = L.FrameOf(end);
                if (ks != kt && ks != Layout.None && kt != Layout.None)
                {
                    PackZ.Changed++;
                    SpottingChecks++;
                    float k = Layout.CellsPerUnit;
                    float eye = SpotterHeight(verb) * k;
                    if (target.Thing != null)
                    {
                        CollisionVertical cv = new CollisionVertical(target.Thing);
                        return ProjectZ.Api.LineClear3D(start, eye, end, (cv.Min + cv.Max) / 2f * k, map, validator);
                    }
                    int up = kt - ks;
                    for (int i = 0; i < Sight.CellAims(up); i++)
                    {
                        if (ProjectZ.Api.LineClear3D(start, eye, end, Sight.CellAimHeight(up, i), map, validator))
                        {
                            return true;
                        }
                    }
                    return false;
                }
            }
            return GenSight.LineOfSight(start, end, map, skipFirstCell, validator, halfXOffset, halfZOffset);
        }

        // The spotter's eyes in CE units: CE's shot height of the verb's ShooterPawn (CE's own pick: the caster pawn or the
        // turret's operator), else the mortar verb's own.
        private static float SpotterHeight(Verb_ShootMortarCE verb)
        {
            Pawn crew = verb.ShooterPawn;
            return crew != null ? new CollisionVertical(crew).shotHeight : verb.ShotHeight;
        }

    }
}
