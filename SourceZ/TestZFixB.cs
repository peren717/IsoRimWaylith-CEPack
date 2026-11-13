using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CombatExtended;
using CombatExtended.Compatibility;
using CombatExtended.WorldObjects;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // Harness, M13 checks (mode fixb): other mods' shields (BlockerRegistry), other mods' CIWS targets (CompCIWSTarget)
    // and world shells (arriving, fired at a world target, the shelled site's map) between floors, real orders and real
    // ticks (see TestZ). The other mods are stood in for by test-only code registered from the harness: a shield blocker
    // as CE's own compat classes write them (a flat circle round a cell, CE's registry callbacks) and a drone thing
    // carrying a CompCIWSTarget (a CIWS verb for it is added to the charge blaster turret's gun for the check).
    public static partial class TestZ
    {
        private static IEnumerable<IEnumerator<int>> FixBChecks()
        {
            string[] only = GenCommandLine.TryGetCommandLineArg("cepackonly", out string o) ? o.Split(',') : null;
            var all = new (string id, Func<IEnumerator<int>> run)[]
            {
                ("b1", BlockerDomes), ("b1d", BlockerLanding), ("b1e", BlockerSameFloor),
                ("b2a", () => CompCiws(0)), ("b2b", () => CompCiws(1)), ("b2c", () => CompCiws(2)),
                ("w1", WorldArrival), ("w2", WorldFire), ("o1", OwnerMinCollision),
            };
            Check("M13 patch groups landed (other mods' shields, CIWS comp targets)", !PackZ.Failed.Any() && Ciws.On, "failed " + string.Join(",", PackZ.Failed) + ", CIWS " + Ciws.On);
            foreach ((string id, Func<IEnumerator<int>> run) in all)
            {
                if (only == null || only.Contains(id))
                {
                    yield return run();
                }
            }
        }

        private static void FixBCounters()
        {
            Out("M13 counters: blockers entries " + Blockers.Entries + ", offers " + Blockers.Offers + ", met " + Blockers.Met + " (landing " + Blockers.Landings + "), zone sets " + Blockers.ZoneSets
                + "; frame views " + FrameView.Moves + "; CIWS comp views " + Ciws.CompViews + ", refused at a plane " + Ciws.CompPlaneRefused + "; test dome intercepts " + domeHits.Count
                + "; shells to the world " + worldShells.Count + ", attrition blasts " + attrition.Count);
        }

        // ------------------------------------------------------------------ o1: the owner's answer is followed
        // A shot from floor 1 down at the ground: its minimum collision distance is CE's own GetMinCollisionDistance (called
        // through a delegate, no copy). For the check CE's method is patched to answer a marker value; the shot must take it.
        private const float MinCollisionMarker = 7.77f;
        private static bool minMark;
        private static readonly List<(string by, float min)> minSeen = new List<(string, float)>();

        public static void MinCollision_Postfix(ref float __result)
        {
            if (minMark)
            {
                __result = MinCollisionMarker;
            }
        }

        public static void MinSeen_Postfix(ProjectileCE __instance)
        {
            if (minMark)
            {
                minSeen.Add((__instance.launcher?.ThingID ?? "-", __instance.minCollisionDistance));
            }
        }

        private static IEnumerator<int> OwnerMinCollision()
        {
            Out("--- o1 CE's own minimum collision distance, between floors");
            // (check 2's balcony, moved to a block of its own)
            Wipe(110, 171, 125, 198);
            Slab(115, 176, 119, 180);
            Pawn target = Target(On(117, 193, 0));
            Pawn shooter = Colonist(On(117, 180, 1), "Gun_AssaultRifle");
            Harmony h = new Harmony("peren.isorimwaylith.cepack.testz.o1");
            MethodInfo launch = typeof(ProjectileCE).GetMethods().First(x => x.Name == "Launch" && x.GetParameters().Length == 3 && x.DeclaringType == typeof(ProjectileCE));
            h.Patch(AccessTools.Method(typeof(Verb_LaunchProjectileCE), "GetMinCollisionDistance", new[] { typeof(float) }), postfix: new HarmonyMethod(typeof(TestZ), nameof(MinCollision_Postfix)));
            h.Patch(launch, postfix: new HarmonyMethod(typeof(TestZ), nameof(MinSeen_Postfix)) { priority = Priority.Last });
            yield return 30;
            string can = CanHit(shooter, target);
            minSeen.Clear();
            minMark = true;
            int t0 = Find.TickManager.TicksGame;
            Order(shooter, target);
            yield return 300;
            minMark = false;
            h.UnpatchAll(h.Id);
            List<Shot> launched = By(shooter, Launches, t0);
            List<float> mine = minSeen.Where(s => s.by == shooter.ThingID).Select(s => s.min).ToList();
            Out("  can hit " + can + ", launched " + launched.Count + ", minimum collision distances " + string.Join(",", mine.Select(v => v.ToString("0.00"))));
            Check("o1 a shot between floors takes CE's own (patched) minimum collision distance", launched.Count > 0 && mine.Count > 0 && mine.All(v => Mathf.Abs(v - MinCollisionMarker) < 1e-4f),
                "can hit " + can + ", launched " + launched.Count + ", distances " + string.Join(",", mine.Select(v => v.ToString("0.00"))) + " (marker " + MinCollisionMarker + ")");
            Gone(shooter, target);
        }

        // ------------------------------------------------------------------ the test shield blocker (an "other mod")
        private sealed class Dome
        {
            public Map map;
            public IntVec3 cell;
            public int radius, id;
        }

        private static readonly List<Dome> domes = new List<Dome>();
        private static readonly List<(int proj, string by, int dome, int tick, bool landing)> domeHits = new List<(int, string, int, int, bool)>();
        private static bool blockerRegistered;
        private static int domeIds;

        private delegate void InterceptFn(ProjectileCE p, object interceptor, Vector3 at, bool destroy);
        private static InterceptFn interceptFn;

        private static void RegisterTestBlocker()
        {
            if (blockerRegistered)
            {
                return;
            }
            blockerRegistered = true;
            interceptFn = AccessTools.MethodDelegate<InterceptFn>(AccessTools.Method(typeof(ProjectileCE), nameof(ProjectileCE.InterceptProjectile), new[] { typeof(object), typeof(Vector3), typeof(bool) }));
            AccessTools.Method(typeof(BlockerRegistry), nameof(BlockerRegistry.RegisterCheckForCollisionBetweenCallback)).Invoke(null, new object[] { new Func<ProjectileCE, Vector3, Vector3, bool>(DomeBetween) });
            BlockerRegistry.RegisterImpactSomethingCallback(DomeLanding);
            BlockerRegistry.RegisterShieldZonesCallback(DomeZones);
            Out("  test blocker registered (step, landing and zone callbacks)");
        }

        private static Dome AddDome(IntVec3 cell, int radius)
        {
            RegisterTestBlocker();
            Dome d = new Dome { map = Map, cell = cell, radius = radius, id = ++domeIds };
            domes.Add(d);
            return d;
        }

        // As CE's ED-Shields compat: direct fire only, a flat circle round the shield's cell, entering from outside.
        private static bool DomeBetween(ProjectileCE p, Vector3 from, Vector3 to)
        {
            if (p.def.projectile.flyOverhead)
            {
                return false;
            }
            if (domeFault == 2 && Blockers.Offering)
            {
                throw new NullReferenceException("test shield mod: fails on a projectile offered from another floor");   // (M5, fault mode)
            }
            foreach (Dome d in domes)
            {
                if (d.map != p.Map)
                {
                    continue;
                }
                float cx = d.cell.x + 0.5f, cz = d.cell.z + 0.5f, r2 = d.radius * d.radius;
                float fx = from.x - cx, fz = from.z - cz, dx = to.x - from.x, dz = to.z - from.z;
                float c = fx * fx + fz * fz - r2;
                if (c <= 0f)
                {
                    continue;   // starts inside
                }
                float a = dx * dx + dz * dz, b = 2f * (fx * dx + fz * dz);
                float disc = b * b - 4f * a * c;
                if (a < 1e-8f || disc < 0f)
                {
                    continue;
                }
                float t = (-b - Mathf.Sqrt(disc)) / (2f * a);
                if (t < 0f || t > 1f)
                {
                    continue;
                }
                domeHits.Add((p.thingIDNumber, p.launcher?.ThingID ?? "-", d.id, Find.TickManager.TicksGame, false));
                interceptFn(p, d, Vector3.Lerp(from, to, t), false);
                return true;
            }
            return false;
        }

        // As CE's ED-Shields compat: a shell coming down within the circle is destroyed.
        private static bool DomeLanding(ProjectileCE p, Thing launcher)
        {
            if (!p.def.projectile.flyOverhead)
            {
                return false;
            }
            foreach (Dome d in domes)
            {
                if (d.map == p.Map && p.Position.DistanceToSquared(d.cell) <= d.radius * d.radius)
                {
                    domeHits.Add((p.thingIDNumber, p.launcher?.ThingID ?? "-", d.id, Find.TickManager.TicksGame, true));
                    interceptFn(p, d, TExact(p), true);
                    return true;
                }
            }
            return false;
        }

        private static IEnumerable<IEnumerable<IntVec3>> DomeZones(Thing t)
        {
            if (domeFault == 1 && !(t is Pawn))
            {
                throw new InvalidCastException("test shield mod: the thing to protect is not a pawn");   // (M5, fault mode)
            }
            return domes.Where(d => d.map == t.Map).Select(d => domeFault == 2 ? GenRadial.RadialCellsAround(d.cell, d.radius, true).Reverse().ToList() : GenRadial.RadialCellsAround(d.cell, d.radius, true)).ToList();
        }

        private static List<(int proj, string by, int dome, int tick, bool landing)> DomeHitsBy(Thing shooter, int since) => domeHits.Where(h => h.by == shooter.ThingID && h.tick >= since).ToList();

        // the impacts (stops) of the projectiles a dome stopped
        private static List<Shot> DomeStops(Thing shooter, int since)
        {
            HashSet<int> ids = new HashSet<int>(DomeHitsBy(shooter, since).Select(h => h.proj));
            return By(shooter, Impacts, since).Where(s => ids.Contains(s.id)).ToList();
        }

        // ---- B1 a/b/c: the 11 fixtures with test domes. a: a dome on the ground round a target, the shooter on a balcony
        // (shots meet the dome while still on floor 1); b: a dome on the ground under a slab, shots over the slab on floor 1
        // pass through its sphere (stopped up there: domes pass slabs); c: a small dome (radius 2) under a slab, shots over
        // it on floor 1 pass above its sphere (a sphere, not CE's flat circle): not stopped.
        private static IEnumerator<int> BlockerDomes()
        {
            Out("--- B1 other mods' shields (BlockerRegistry) between floors");
            Wipe(180, 128, 200, 160);
            Slab(186, 129, 194, 135);
            Pawn targetA = Target(On(190, 142, 0));
            Dome domeA = AddDome(On(190, 142, 0), 6);
            Pawn shooterA = Colonist(On(190, 135, 1), "Gun_AssaultRifle");
            Wipe(224, 128, 245, 170);
            Slab(226, 136, 244, 166);
            Dome domeB = AddDome(On(235, 151, 0), 6);
            Pawn targetB = Target(On(235, 160, 1));
            Pawn shooterB = Colonist(On(235, 140, 1), "Gun_AssaultRifle");
            Wipe(202, 128, 222, 170);
            Slab(204, 132, 220, 168);
            Dome domeC = AddDome(On(212, 150, 0), 2);
            Pawn targetC = Target(On(212, 158, 1));
            Pawn shooterC = Colonist(On(212, 142, 1), "Gun_AssaultRifle");
            yield return 30;
            string canA = CanHit(shooterA, targetA), canB = CanHit(shooterB, targetB), canC = CanHit(shooterC, targetC);
            int t0 = Find.TickManager.TicksGame, met0 = Blockers.Met;
            Order(shooterA, targetA);
            Order(shooterB, targetB);
            Order(shooterC, targetC);
            yield return 500;
            List<Shot> sa = DomeStops(shooterA, t0), sb = DomeStops(shooterB, t0);
            int ia = DomeHitsBy(shooterA, t0).Count, ib = DomeHitsBy(shooterB, t0).Count, ic = DomeHitsBy(shooterC, t0).Count;
            int hitA = By(shooterA, Impacts, t0).Count(s => s.hit.EndsWith("#" + targetA.thingIDNumber));
            int hitB = By(shooterB, Impacts, t0).Count(s => s.hit.EndsWith("#" + targetB.thingIDNumber));
            int hitC = By(shooterC, Impacts, t0).Count(s => s.hit.EndsWith("#" + targetC.thingIDNumber));
            Out("  B1a: can hit " + canA + ", launched " + By(shooterA, Launches, t0).Count + ", dome stops " + ia + " (on floor 1 " + sa.Count(s => s.frame == 1) + "), target hits " + hitA + ": " + Hits(sa));
            Out("  B1b: can hit " + canB + ", launched " + By(shooterB, Launches, t0).Count + ", dome stops " + ib + " (on floor 1 " + sb.Count(s => s.frame == 1) + "), target hits " + hitB + ": " + Hits(sb));
            Out("  B1c: can hit " + canC + ", launched " + By(shooterC, Launches, t0).Count + ", dome stops " + ic + ", target hits " + hitC + "; blockers met across " + (Blockers.Met - met0));
            Check("B1a ground test dome, shots from floor 1: stopped where they meet the dome (some up on floor 1)", canA == "yes" && ia > 0 && sa.Any(s => s.frame == 1) && hitA == 0,
                "can hit " + canA + ", stops " + ia + " on floor 1 " + sa.Count(s => s.frame == 1) + ", target hits " + hitA);
            Check("B1b test dome under a slab: shots over the slab are stopped where they pass through its sphere (up on floor 1)", canB == "yes" && ib > 0 && sb.Count == ib && sb.All(s => s.frame == 1) && hitB == 0,
                "can hit " + canB + ", stops " + ib + " on floor 1 " + sb.Count(s => s.frame == 1) + ", target hits " + hitB);
            Check("B1c small test dome under a slab: shots over it on floor 1 pass above its sphere", canC == "yes" && ic == 0 && hitC > 0, "can hit " + canC + ", stops " + ic + ", target hits " + hitC);
            Gone(shooterA, shooterB, shooterC, targetA, targetB, targetC);
            domes.Remove(domeA);
            domes.Remove(domeB);
            domes.Remove(domeC);
        }

        // ---- B1d: mortar shells coming down on a slab over a ground test dome (its landing callback): stopped there.
        private static IEnumerator<int> BlockerLanding()
        {
            Out("--- B1d shells coming down on a slab over a ground test dome");
            Wipe(20, 10, 70, 120);
            Slab(35, 95, 55, 115);
            Dome dome = AddDome(On(45, 105, 0), 6);
            Building_TurretGunCE mortar = (Building_TurretGunCE)Build("Turret_Mortar", On(45, 20, 0));
            Pawn gunner = Gunner(On(45, 18, 0), mortar);
            int t0 = Find.TickManager.TicksGame, land0 = Blockers.Landings;
            IEnumerator<int> fire = FireShells(mortar, gunner, new LocalTargetInfo(On(45, 105, 1)), 3, t0);
            while (fire.MoveNext())
            {
                yield return fire.Current;
            }
            List<Shot> shells = Of(gunner, mortar, Launches, t0).Where(s => s.overhead).ToList();
            List<(int proj, string by, int dome, int tick, bool landing)> stops = domeHits.Where(h => h.landing && h.tick >= t0 && h.dome == dome.id).ToList();
            List<Shot> burst = Of(gunner, mortar, Impacts, t0).Where(s => s.overhead).ToList();
            int inside = burst.Count(s => (s.ground - dome.cell).LengthHorizontal <= dome.radius - 0.5f);
            Out("  shells " + shells.Count + ", stopped coming down " + stops.Count + " (landings across " + (Blockers.Landings - land0) + "), burst " + burst.Count + " (" + Hits(burst) + "), inside the dome " + inside);
            Check("B1d shells coming down on the slab over a ground test dome are stopped (none bursts inside it)", shells.Count > 0 && stops.Count > 0 && Blockers.Landings > land0 && inside == 0,
                "shells " + shells.Count + ", stopped " + stops.Count + ", burst inside " + inside);
            Gone(gunner, mortar);
            domes.Remove(dome);
        }

        // ---- B1e: control, all on the ground: CE's own callback stops the shots; the pack changes nothing.
        private static IEnumerator<int> BlockerSameFloor()
        {
            Out("--- B1e control: test dome, shooter and target on the ground");
            Wipe(60, 176, 100, 214);
            Pawn target = Target(On(80, 204, 0));
            Dome dome = AddDome(On(80, 204, 0), 5);
            Pawn shooter = Colonist(On(80, 190, 0), "Gun_AssaultRifle");
            yield return 30;
            int t0 = Find.TickManager.TicksGame, ch0 = PackZ.Changed, met0 = Blockers.Met, off0 = Blockers.Offers;
            Order(shooter, target);
            yield return 400;
            int stops = DomeHitsBy(shooter, t0).Count, hits = By(shooter, Impacts, t0).Count(s => s.hit.EndsWith("#" + target.thingIDNumber));
            int changed = PackZ.Changed - ch0;
            Out("  launched " + By(shooter, Launches, t0).Count + ", dome stops " + stops + ", target hits " + hits + ", changed " + changed + ", offers across " + (Blockers.Offers - off0));
            Check("B1e control: same-floor test dome stops the shots as CE has it, the pack changed nothing", stops > 0 && hits == 0 && changed == 0 && Blockers.Met == met0 && Blockers.Offers == off0,
                "stops " + stops + ", target hits " + hits + ", changed " + changed + ", offers " + (Blockers.Offers - off0));
            Gone(shooter, target);
            domes.Remove(dome);
        }

        // ------------------------------------------------------------------ the test CIWS target (an "other mod")
        // A drone flying a straight line at a fixed height (x, z cells; y CE height units over its storey's floor, in its
        // thing's frame), shot down by a CIWS with a VerbCIWS_Comp verb for it.
        // (PredictedPositions is Unity-typed, which this assembly can't override against the stubs: a small subclass made
        // at run time overrides it with DronePath.)
        public abstract class TestCiwsTarget : CompCIWSTarget
        {
            public Vector3 start, vel;
            public int t0;
            public float hitRadius = 1f;
            public int hits;

            public Vector3 Now => start + vel * (Find.TickManager.TicksGame - t0);

            public override bool IsFriendlyTo(Thing thing) => false;

            // the bullet's step (LastPos -> ExactPosition, raw rows and local heights) passing within hitRadius
            public override bool? CanCollideWith(ProjectileCE_CIWS p, out float dist)
            {
                Vector3 a = TLast(p), b = TExact(p), c = Now;
                Vector3 d = b - a;
                float len = d.magnitude;
                float t = len < 1e-5f ? 0f : Mathf.Clamp01(Vector3.Dot(c - a, d) / (len * len));
                Vector3 q = a + d * t;
                dist = -1f;
                if ((q - c).sqrMagnitude > hitRadius * hitRadius)
                {
                    return false;
                }
                hits++;
                dist = len * t;
                return true;
            }
        }

        public static IEnumerable<Vector3> DronePath(TestCiwsTarget c)
        {
            Vector3 s = c.Now;
            for (int i = 0; i < 240; i++)
            {
                yield return s + c.vel * i;
            }
        }

        private static Type droneType;

        private static Type DroneType()
        {
            if (droneType != null)
            {
                return droneType;
            }
            System.Reflection.Emit.AssemblyBuilder ab = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("IsoRimCEPackTestDrone"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            System.Reflection.Emit.ModuleBuilder mb = ab.DefineDynamicModule("IsoRimCEPackTestDrone");
            System.Reflection.Emit.TypeBuilder tb = mb.DefineType("IsoRimCEPackTestDrone", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, typeof(TestCiwsTarget));
            MethodInfo getter = typeof(CompCIWSTarget).GetProperty("PredictedPositions").GetGetMethod();
            System.Reflection.Emit.MethodBuilder m = tb.DefineMethod(getter.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName, getter.ReturnType, Type.EmptyTypes);
            System.Reflection.Emit.ILGenerator il = m.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(TestZ), nameof(DronePath)));
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            tb.DefineMethodOverride(m, getter);
            tb.DefineDefaultConstructor(MethodAttributes.Public);
            droneType = tb.CreateType();
            return droneType;
        }

        public class TestCiwsVerb : VerbCIWS_Comp<TestCiwsTarget>
        {
        }

        public class TestCiwsVerbProps : VerbProperties_CIWS
        {
            protected override IEnumerable<ThingDef> InitAllTargets() => Enumerable.Empty<ThingDef>();
        }

        // A charge blaster CIWS (mechanoid) with the comp-target verb added to its gun (a copy of its CIWS verb).
        private static Building_CIWS_CE CompCiwsTurret(IntVec3 c)
        {
            ThingDef turret = DefDatabase<ThingDef>.GetNamed("Turret_AutoChargeBlaster");
            ThingDef gun = turret.building.turretGunDef;
            VerbProperties src = gun.Verbs.First(v => v is VerbProperties_CIWS);
            TestCiwsVerbProps mine = new TestCiwsVerbProps();
            for (Type t = typeof(VerbProperties_CIWS); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.Name != "allTargets")
                    {
                        f.SetValue(mine, f.GetValue(src));
                    }
                }
            }
            mine.verbClass = typeof(TestCiwsVerb);
            List<VerbProperties> verbs = (List<VerbProperties>)AccessTools.Field(typeof(ThingDef), "verbs").GetValue(gun);
            verbs.Add(mine);
            Building_CIWS_CE b;
            try
            {
                b = (Building_CIWS_CE)BuildAs("Turret_AutoChargeBlaster", c, Faction.OfMechanoids);
                b.GunCompEq.AllVerbs.Count();   // (the gun's verbs are made now, with the added one)
            }
            finally
            {
                verbs.Remove(mine);
            }
            return b;
        }

        private static (Thing drone, TestCiwsTarget comp) Drone(IntVec3 at, Vector3 start, Vector3 vel)
        {
            ThingWithComps t = (ThingWithComps)ThingMaker.MakeThing(ThingDefOf.Steel);
            t.stackCount = 75;
            TestCiwsTarget comp = (TestCiwsTarget)Activator.CreateInstance(DroneType());
            comp.parent = t;
            comp.start = start;
            comp.vel = vel;
            comp.t0 = Find.TickManager.TicksGame;
            comp.Initialize(new CompProperties_CIWSTarget { compClass = DroneType() });
            FieldInfo cf = AccessTools.Field(typeof(ThingWithComps), "comps");
            List<ThingComp> comps = (List<ThingComp>)cf.GetValue(t);
            if (comps == null)
            {
                comps = new List<ThingComp>();
                cf.SetValue(t, comps);
            }
            comps.Add(comp);
            GenSpawn.Spawn(t, at, Map);
            return (t, comp);
        }

        // ---- B2: a CIWS on floor 1 and a drone on the ground's frame: a) flying through open air just under floor 1's plane,
        // beside the CIWS's slab: engaged and hit; b) flying low under the CIWS's slab: never engaged (every line crosses the
        // slab); c) control, CIWS and drone on the ground: engaged and hit, the pack changes nothing.
        private static IEnumerator<int> CompCiws(int which)
        {
            string name = which == 0 ? "B2a" : which == 1 ? "B2b" : "B2c";
            Out("--- " + name + " CIWS against a comp target (CompCIWSTarget) " + (which == 0 ? "across floor 1's open plane" : which == 1 ? "under the CIWS's slab" : "on the ground (control)"));
            float S = Layout.S;
            Building_CIWS_CE ciws;
            Thing drone;
            TestCiwsTarget comp;
            if (which == 0)
            {
                Wipe(135, 25, 170, 95);
                Slab(147, 57, 153, 63);
                ciws = CompCiwsTurret(On(150, 60, 1));
                IntVec3 g = On(158, 60, 0);
                (drone, comp) = Drone(g, new Vector3(158.5f, S - 0.12f, On(158, 35, 0).z + 0.5f), new Vector3(0f, 0f, 0.15f));
                comp.hitRadius = 1.8f;   // (a bigger drone: bullets meet it more often while still over the plane)
            }
            else if (which == 1)
            {
                Wipe(95, 5, 145, 55);
                Slab(100, 10, 140, 50);
                ciws = CompCiwsTurret(On(120, 30, 1));
                IntVec3 g = On(120, 25, 0);
                (drone, comp) = Drone(g, new Vector3(108.5f, 1.0f, On(120, 25, 0).z + 0.5f), new Vector3(0.08f, 0f, 0f));
            }
            else
            {
                Wipe(5, 150, 45, 214);
                ciws = CompCiwsTurret(On(20, 180, 0));
                IntVec3 g = On(28, 180, 0);
                (drone, comp) = Drone(g, new Vector3(28.5f, 0.9f, On(28, 160, 0).z + 0.5f), new Vector3(0f, 0f, 0.12f));   // (low: the bullets stay under floor 1's plane)
            }
            Verb mine = ciws.GunCompEq.AllVerbs.FirstOrDefault(v => v is TestCiwsVerb);
            int t0 = Find.TickManager.TicksGame, ch0 = PackZ.Changed, tr0 = Ciws.Translated, bl0 = Ciws.BlockedPoints, cv0 = Ciws.CompViews, pr0 = Ciws.CompPlaneRefused, cc0 = Ciws.CompViewContacts;
            Out("  CIWS " + ciws.Position + " (frame " + L.FrameOf(ciws.Position) + "), comp verb " + (mine != null) + ", drone " + drone.Position + " start " + comp.start.ToString("F2") + " (S " + S.ToString("0.000") + ")");
            for (int i = 0; i < 40 && drone.Spawned; i++)
            {
                yield return 10;
            }
            List<Shot> atDrone = By(ciws, Launches, t0).Where(s => s.line.EndsWith("#" + drone.thingIDNumber)).ToList();
            List<Shot> hits = By(ciws, Impacts, t0).Where(s => s.hit.EndsWith("#" + drone.thingIDNumber)).ToList();
            int changed = PackZ.Changed - ch0;
            Out("  CIWS launches at the drone " + atDrone.Count + ", hits " + hits.Count + " (" + Hits(hits) + "), comp contacts " + comp.hits + ", translated " + (Ciws.Translated - tr0) + ", blocked points " + (Ciws.BlockedPoints - bl0)
                + ", comp views " + (Ciws.CompViews - cv0) + " (contacts " + (Ciws.CompViewContacts - cc0) + ", refused at a plane " + (Ciws.CompPlaneRefused - pr0) + "), changed " + changed + ", drone spawned " + drone.Spawned);
            if (which == 0)
            {
                Check("B2a CIWS on floor 1 engages and hits a comp target flying through open air below its floor (aim and contact seen in its frame)",
                    mine != null && atDrone.Count > 0 && hits.Count > 0 && Ciws.Translated > tr0 && Ciws.CompViewContacts > cc0,
                    "launches " + atDrone.Count + ", hits " + hits.Count + ", translated " + (Ciws.Translated - tr0) + ", contacts seen in the drone's frame " + (Ciws.CompViewContacts - cc0));
            }
            else if (which == 1)
            {
                Check("B2b CIWS on a slab does not engage a comp target under the slab", mine != null && atDrone.Count == 0 && hits.Count == 0 && Ciws.BlockedPoints > bl0,
                    "launches " + atDrone.Count + ", hits " + hits.Count + ", blocked points " + (Ciws.BlockedPoints - bl0));
            }
            else
            {
                Check("B2c control: CIWS and comp target on the ground: engaged and hit, the pack changed nothing", mine != null && atDrone.Count > 0 && hits.Count > 0 && changed == 0,
                    "launches " + atDrone.Count + ", hits " + hits.Count + ", changed " + changed);
            }
            Gone(ciws, drone);
            for (int guard = 0; guard < 20 && Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).OfType<ProjectileCE>().Any(p => p.launcher == ciws && !p.Destroyed); guard++)
            {
                yield return 10;
            }
        }

        // ------------------------------------------------------------------ world shells
        private static readonly List<(int tick, string by, int frame, IntVec3 ground, float height)> worldShells = new List<(int, string, int, IntVec3, float)>();
        private static readonly List<(IntVec3 centre, int sizeZ, int maxZ, int cells)> attrition = new List<(IntVec3, int, int, int)>();

        private static bool worldPatched;

        private static void PatchWorld()
        {
            if (worldPatched)
            {
                return;
            }
            worldPatched = true;
            Harmony h = new Harmony("peren.isorimwaylith.cepack.testz.world");
            h.Patch(AccessTools.Method(typeof(ProjectileCE), "CreateShellWorldObject"), prefix: new HarmonyMethod(typeof(TestZ), nameof(WorldShell_Prefix)));
            h.Patch(AccessTools.Method(typeof(WorldObjectDamageWorker), nameof(WorldObjectDamageWorker.ExplosionCellsToHit)), postfix: new HarmonyMethod(typeof(TestZ), nameof(AttritionCells_Postfix)));
        }

        public static void WorldShell_Prefix(ProjectileCE __instance)
        {
            Layout L = Layout.Of(__instance.Map);
            Vector3 e = TExact(__instance);
            int k = L != null ? L.FrameOf(e) : 0;
            worldShells.Add((Find.TickManager.TicksGame, __instance.launcher?.ThingID ?? "-", k, L != null && k != Layout.None ? L.Ground(e.ToIntVec3()) : e.ToIntVec3(), e.y));
        }

        public static void AttritionCells_Postfix(IntVec3 center, Map map, IEnumerable<IntVec3> __result)
        {
            List<IntVec3> cs = __result?.ToList() ?? new List<IntVec3>();
            attrition.Add((center, map.Size.z, cs.Count > 0 ? cs.Max(c => c.z) : center.z, cs.Count));
        }

        private static TravelingShell SendShell(string shellDef, PlanetTile from, GlobalTargetInfo target, Thing launcher)
        {
            TravelingShell s = (TravelingShell)WorldObjectMaker.MakeWorldObject(CE_WorldObjectDefOf.TravelingShell);
            s.Tile = from;
            s.SpawnSetup();
            Find.World.worldObjects.Add(s);
            s.launcher = launcher;   // (CE's Launch reads the launcher's map: a shell always has one)
            s.shellDef = DefDatabase<ThingDef>.GetNamed(shellDef);
            s.globalSource = new GlobalTargetInfo(from);
            s.globalTarget = target;
            if (!s.TryTravel(from, target.Tile))
            {
                s.Destroy();
                return null;
            }
            return s;
        }

        // A free tile min..max tiles away; north > 0: one whose shells leave this map through its north edge (CE's exit
        // cell: the ray from the shooter along home - target, world coordinates), north < 0: through its south edge.
        private static PlanetTile TileNear(int min, int max, int north = 0)
        {
            PlanetTile home = Map.Tile;
            Vector3 hc = Find.WorldGrid.GetTileCenter(home);
            return TileFinder.TryFindPassableTileWithTraversalDistance(home, min, max, out PlanetTile t, x =>
            {
                if (Find.WorldObjects.AnyWorldObjectAt(x))
                {
                    return false;
                }
                Vector3 d = (hc - Find.WorldGrid.GetTileCenter(x)).normalized;
                return north == 0 || (north > 0 ? d.z > 0.6f : d.z < -0.6f);
            }, tileFinderMode: TileFinderMode.Near) ? t : PlanetTile.Invalid;
        }

        // CE's counter-raid (a shelled settlement's answer) picks the storyteller's main comp; the harness cleared them: an
        // inert one (it never makes an incident; the settlement is gone before any raid would come).
        public class InertStorytellerComp : StorytellerComp_RandomMain
        {
            public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
            {
                yield break;
            }
        }

        private static void InertStoryteller()
        {
            if (!Find.Storyteller.storytellerComps.Any(c => c is InertStorytellerComp))
            {
                Find.Storyteller.storytellerComps.Add(new InertStorytellerComp { props = new StorytellerCompProperties_RandomMain() });
            }
        }

        // ---- W1: world shells arriving on this map: one aimed at a cell on floor 1 over a slab (lands on the slab), and a
        // salvo without a cell (CE's random impact cell): they come down over the ground rows, most of them in the middle
        // half of the map (CE's Gaussian round the map's middle, as on a map without floors), none on a band edge row.
        private static IEnumerator<int> WorldArrival()
        {
            Out("--- W1 world shells arriving on a map with floors");
            PatchWorld();
            Wipe(198, 18, 212, 32);
            Slab(200, 20, 210, 30);
            PlanetTile from = TileNear(2, 4);
            if (!from.Valid)
            {
                Check("W1 world shells arriving", false, "no free tile near home");
                yield break;
            }
            try
            {
                UnityEngine.Random.InitState(4242);   // (CE's Gaussian draws Unity's random numbers)
            }
            catch (Exception)
            {
            }
            Pawn launcher = Colonist(On(225, 5, 0), null);   // (stands in for the shooter on the other map)
            launcher.drafter.Drafted = false;
            int t0 = Find.TickManager.TicksGame;
            IntVec3 aim = On(205, 25, 1);
            SendShell("Bullet_81mmMortarShell_Smoke", from, new GlobalTargetInfo(aim, Map), launcher);
            IEnumerator<int> wait = WaitShells(t0);
            while (wait.MoveNext())
            {
                yield return wait.Current;
            }
            int t1 = Find.TickManager.TicksGame;
            for (int i = 0; i < 16; i++)
            {
                SendShell("Bullet_81mmMortarShell_Smoke", from, new GlobalTargetInfo(Map.Parent), launcher);
            }
            wait = WaitShells(t1);
            while (wait.MoveNext())
            {
                yield return wait.Current;
            }
            List<Shot> arrived = Launches.Where(s => s.tick >= t0 && s.def == "Bullet_81mmMortarShell_Smoke").ToList();
            List<Shot> down = Impacts.Where(s => s.tick >= t0 && s.def == "Bullet_81mmMortarShell_Smoke").ToList();
            Shot first = down.FirstOrDefault(s => s.tick < t1);
            List<Shot> rest = down.Where(s => s.tick >= t1).ToList();
            int rows = L.rows;
            int middle = rest.Count(s => Mathf.Abs(s.ground.z - rows / 2f) <= rows / 4f);
            int edge = rest.Count(s => s.ground.z <= 0 || s.ground.z >= rows - 1);
            Out("  arrived " + arrived.Count + " (frames " + string.Join(",", arrived.Select(s => s.frame)) + "), came down " + down.Count + "; aimed: " + (first != null ? "f" + first.frame + " " + first.ground + " h" + first.height.ToString("0.00") : "none")
                + "; random ones: rows " + string.Join(",", rest.Select(s => s.ground.z)) + " (middle half " + middle + "/" + rest.Count + ", edge rows " + edge + ")");
            Check("W1a a world shell aimed at a floor-1 cell over a slab lands on the slab", first != null && first.frame == 1 && (first.ground - L.Ground(aim)).LengthHorizontal <= 3f,
                first != null ? "f" + first.frame + " " + first.ground + " (aim " + L.Ground(aim) + ")" : "no impact");
            Check("W1b world shells without a cell come down over the ground's middle (CE's random impact cell over the ground rows)", rest.Count >= 12 && middle >= rest.Count * 3 / 4 && edge == 0,
                "came down " + rest.Count + ", middle half " + middle + ", edge rows " + edge);
            Gone(launcher);
        }

        // Until the world shells sent since t0 have arrived and come down.
        private static IEnumerator<int> WaitShells(int t0)
        {
            for (int guard = 0; guard < 60; guard++)
            {
                yield return 50;
                bool travelling = Find.WorldObjects.AllWorldObjects.OfType<TravelingShell>().Any();
                bool flying = Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).OfType<ProjectileCE>().Any(p => !p.landed && !p.Destroyed && p.def.defName == "Bullet_81mmMortarShell_Smoke");
                if (!travelling && !flying && Find.TickManager.TicksGame - t0 > 200)
                {
                    yield break;
                }
            }
        }

        // A hostile settlement on a tile (CE gives settlements health and hostility).
        private static Settlement Site(PlanetTile tile, Faction enemy, string name)
        {
            Settlement site = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            site.SetFaction(enemy);
            site.Tile = tile;
            site.Name = name;
            Find.WorldObjects.Add(site);
            return site;
        }

        // Mortars on the ground (m0) and on floor 1 (m1) fire `shells` each at a settlement; what left the map, the damage.
        // CE's own warning for some world shots' exit cells (seen with the pack's cross-floor part off too, a mortar on the
        // ground firing south): listed, not counted, while mortars shell world targets.
        private static bool worldShotNoise;

        private static IEnumerator<int> ShellSite(string name, Settlement site, Building_TurretGunCE m0, Pawn g0, Building_TurretGunCE m1, Pawn g1, int shells)
        {
            HealthComp health = site.GetComponent<HealthComp>();
            int t0 = Find.TickManager.TicksGame, ws0 = worldShells.Count;
            float h0 = health?.Health ?? -1f;
            Load(m0);
            Load(m1);
            worldShotNoise = true;
            bool o0 = m0.TryAttackWorldTarget(new GlobalTargetInfo(site), LocalTargetInfo.Invalid);
            bool o1 = m1.TryAttackWorldTarget(new GlobalTargetInfo(site), LocalTargetInfo.Invalid);
            bool ownFloor = L.FrameOf(m0.ForcedTarget.Cell) == L.FrameOf(m0.Position) && L.FrameOf(m1.ForcedTarget.Cell) == L.FrameOf(m1.Position);
            Vector3 d = (Find.WorldGrid.GetTileCenter(Map.Tile) - Find.WorldGrid.GetTileCenter(site.Tile)).normalized;
            Out("  " + name + ": site " + site.Tile + " (distance " + ShellingUtility.GetDistancePlanetTiles(Map.Tile, site.Tile) + " tiles, mortar range " + m0.MaxWorldRange + ", exit direction " + d.ToString("F2") + "), orders taken " + o0 + "/" + o1
                + ", forced targets " + m0.ForcedTarget.Cell + " / " + m1.ForcedTarget.Cell + ", health " + h0.ToString("0.000"));
            for (int guard = 0; guard < 60; guard++)
            {
                yield return 50;
                Load(m0);
                Load(m1);
                if (Of(g0, m0, Launches, t0).Count >= shells && Of(g1, m1, Launches, t0).Count >= shells)
                {
                    break;
                }
            }
            m0.ResetForcedTarget();
            m1.ResetForcedTarget();
            worldShotNoise = false;
            for (int guard = 0; guard < 40 && (Find.WorldObjects.AllWorldObjects.OfType<TravelingShell>().Any() || InFlight(g0, m0) || InFlight(g1, m1)); guard++)
            {
                yield return 50;
            }
            int l0 = Of(g0, m0, Launches, t0).Count, l1 = Of(g1, m1, Launches, t0).Count;
            var left = worldShells.Skip(ws0).ToList();
            int left0 = left.Count(w => w.by == g0.ThingID || w.by == m0.ThingID), left1 = left.Count(w => w.by == g1.ThingID || w.by == m1.ThingID);
            float h1 = health?.Health ?? -1f;
            Out("  " + name + ": launched ground " + l0 + ", floor 1 " + l1 + "; left the map ground " + left0 + ", floor 1 " + left1 + " (" + string.Join("; ", left.Take(8).Select(w => "f" + w.frame + " " + w.ground + " h" + w.height.ToString("0.0"))) + "); burst on this map "
                + Hits(Of(g0, m0, Impacts, t0).Concat(Of(g1, m1, Impacts, t0)).ToList()) + "; settlement health " + h0.ToString("0.000") + " -> " + h1.ToString("0.000") + ", recent shells " + (health?.recentShells.Count ?? -1));
            Check(name + " mortars on the ground and on floor 1 shell a world target: their exit cells on their own floor, their shells leave the map and damage the settlement", o0 && o1 && ownFloor && l0 > 0 && l1 > 0 && left0 == l0 && left1 == l1 && h1 < h0,
                "orders " + o0 + "/" + o1 + ", exit cells on the own floor " + ownFloor + ", launched " + l0 + "/" + l1 + ", left the map " + left0 + "/" + left1 + ", health " + h0.ToString("0.000") + " -> " + h1.ToString("0.000"));
        }

        // ---- W2/W3: mortars on the ground and on floor 1 shell a hostile settlement two to four tiles away (a world
        // target): their shells leave the map and fly there, the settlement takes damage (CE's WorldObjectDamageWorker,
        // numbers only). Then the settlement's map is generated (with floors): CE's attrition (the same worker's own blasts
        // over the map) runs while ProjectZ has the map at its ground size, so every blast stays in the ground rows.
        private static IEnumerator<int> WorldFire()
        {
            Out("--- W2 mortars on two floors shell settlements north and south (world targets); W3 a shelled site's map (attrition)");
            PatchWorld();
            InertStoryteller();
            PlanetTile tileN = TileNear(2, 6, 1), tileS = TileNear(2, 6, -1);
            Faction enemy = Enemy;
            if (!tileN.Valid || !tileS.Valid || enemy == null)
            {
                Check("W2 world fire", false, "no free tile north " + tileN + " / south " + tileS + " or enemy faction " + enemy);
                yield break;
            }
            Settlement siteN = Site(tileN, enemy, "CE pack test site N");
            Settlement siteS = Site(tileS, enemy, "CE pack test site S");
            Wipe(50, 40, 90, 80);
            Slab(66, 56, 74, 64);
            Building_TurretGunCE m0 = (Building_TurretGunCE)Build("Turret_Mortar", On(56, 60, 0));
            Pawn g0 = Gunner(On(56, 58, 0), m0);
            Building_TurretGunCE m1 = (Building_TurretGunCE)Build("Turret_Mortar", On(70, 60, 1));
            Pawn g1 = Gunner(On(70, 58, 1), m1);
            yield return 200;
            IEnumerator<int> n = ShellSite("W2n (north)", siteN, m0, g0, m1, g1, 3);
            while (n.MoveNext())
            {
                yield return n.Current;
            }
            IEnumerator<int> s = ShellSite("W2s (south)", siteS, m0, g0, m1, g1, 2);
            while (s.MoveNext())
            {
                yield return s.Current;
            }
            Gone(g0, m0, g1, m1);
            if (!siteS.Destroyed)
            {
                siteS.Destroy();
            }
            Settlement site = siteN;
            HealthComp health = site.GetComponent<HealthComp>();

            // W3: the shelled settlement's map
            if (health == null || health.Health >= 0.98f)
            {
                Check("W3 attrition on the settlement's map", false, "settlement not damaged enough for CE's attrition (health " + (health?.Health ?? -1f) + ")");
                yield break;
            }
            int a0 = attrition.Count;
            Map gen = null;
            try
            {
                gen = GetOrGenerateMapUtility.GetOrGenerateMap(site.Tile, new IntVec3(150, 1, 150), null);
            }
            catch (Exception e)
            {
                errors++;
                Out("  map generation failed: " + e);
            }
            yield return 1;
            if (gen != null)
            {
                Layout lg = Layout.Of(gen);
                int groundRows = lg != null ? lg.rows : gen.Size.z;
                var blasts = attrition.Skip(a0).ToList();
                int annexBad = 0;
                if (lg != null)
                {
                    TerrainDef air = DefDatabase<TerrainDef>.GetNamed("PZ_OpenAir");
                    for (int z = groundRows; z < gen.Size.z; z++)
                    {
                        for (int x = 0; x < gen.Size.x; x++)
                        {
                            IntVec3 c = new IntVec3(x, 0, z);
                            if (c.GetTerrain(gen) != air || c.Roofed(gen) || c.GetThingList(gen).Count > 0)
                            {
                                annexBad++;
                            }
                        }
                    }
                }
                bool inGround = blasts.All(b => b.sizeZ == groundRows && b.centre.z < groundRows && b.maxZ < groundRows);
                Out("  generated map " + gen.Size + ", floors " + (lg != null ? lg.min + ".." + lg.max + ", ground rows " + groundRows : "none") + "; attrition blasts " + blasts.Count + ": "
                    + string.Join("; ", blasts.Take(8).Select(b => b.centre + " size z " + b.sizeZ + " max z " + b.maxZ + " cells " + b.cells)) + "; upper bands and void rows not open air, roofed or with things: " + annexBad);
                Check("W3 the shelled settlement's map (with floors): CE's attrition blasts stay in the ground rows (the map is at its ground size meanwhile), the floors above are untouched",
                    lg != null && blasts.Count > 0 && inGround && annexBad == 0,
                    "floors " + (lg != null) + ", blasts " + blasts.Count + ", all in the ground rows " + inGround + ", annex cells touched " + annexBad);
                Current.Game.DeinitAndRemoveMap(gen, false);
            }
            else
            {
                Check("W3 attrition on the settlement's map", false, "no map generated");
            }
            if (!site.Destroyed)
            {
                site.Destroy();
            }
        }
    }
}
