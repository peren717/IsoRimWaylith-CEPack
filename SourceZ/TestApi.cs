using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRimCEPackZ
{
    // Test harness only (mode "api", -cepacktest=api; "apiload" for the second half of the save check): the pack's public
    // Api (Api.cs) as another mod uses it. A fake "siege engine" provider and a fake surface rule, registered by the test
    // only (owners "cepack.test.*"): sandbags stand in for the engine, with a volume and aim point of the test's choosing.
    // Real flow as in TestZ: drafted colonists and real turrets fire real CE projectiles on real ticks; each check its own
    // fixture, its own seed (the runner's) and its own registration, disposed at its end.
    //   A0 nothing patched before the first registration; A1-A5 target geometry (aim and volume same floor and between
    //   floors, collisions off the footprint, sight blocked, range by RangeCell); P1-P6 placement queries against the real
    //   turret built at the same cell; S1 surface explosions (slab terrain damaged only when the rule says yes); F1-F5
    //   broken providers and rules (a warning, the native path, the game goes on); V1-V3 saves (with and without the rule);
    //   "apiload" (run with -cepacknoz): the save with surface data loads without the pack's Z part.
    public static partial class TestZ
    {
        private const string Owner = "cepack.test.siege";

        // The fake provider: engines by thing (virtual volume, virtual aim point, range cell); a mode breaks it on purpose.
        private sealed class FakeEngine
        {
            public Bounds Volume;
            public Vector3 Aim;
            public IntVec3 RangeCell;
        }

        private sealed class FakeProvider : ITargetGeometryProvider
        {
            public readonly Dictionary<Thing, FakeEngine> Engines = new Dictionary<Thing, FakeEngine>();
            public string Mode = "ok";   // ok, nanaim, nanvolume, throwaim, throwvolume
            public int VolumeAsks, AimAsks;

            public bool TryGetVolume(Thing target, Map map, out Bounds volumeVirtual)
            {
                volumeVirtual = default;
                if (!Engines.TryGetValue(target, out FakeEngine e))
                {
                    return false;
                }
                VolumeAsks++;
                if (Mode == "throwvolume")
                {
                    throw new InvalidOperationException("test provider broken on purpose (volume)");
                }
                volumeVirtual = e.Volume;
                if (Mode == "nanvolume")
                {
                    volumeVirtual.center = new Vector3(float.NaN, 1f, 1f);
                }
                return true;
            }

            public bool TryGetAim(Thing target, TargetQuery query, out TargetAim aim)
            {
                aim = default;
                if (!Engines.TryGetValue(target, out FakeEngine e))
                {
                    return false;
                }
                AimAsks++;
                if (Mode == "throwaim")
                {
                    throw new InvalidOperationException("test provider broken on purpose (aim)");
                }
                aim = new TargetAim(Mode == "nanaim" ? new Vector3(e.Aim.x, float.NaN, e.Aim.z) : e.Aim, e.RangeCell);
                return true;
            }
        }

        // The fake rule: yes for contacts whose ground column lies in a yes box, no elsewhere (or it throws).
        private sealed class FakeRule : ISurfaceExplosionRule
        {
            public readonly List<CellRect> Yes = new List<CellRect>();
            public readonly List<SurfaceContact> Seen = new List<SurfaceContact>();
            public bool Throws;

            public bool ShouldDamageTerrain(SurfaceExplosionContext context)
            {
                Seen.Add(context.Contact);
                if (Throws)
                {
                    throw new InvalidOperationException("test rule broken on purpose");
                }
                return Yes.Any(r => r.Contains(context.Contact.GroundCell));
            }
        }

        // An engine at ground cell (gx, gz) of floor k: a virtual box [x0, x1) x [y0, y1) x [z0, z1) and an aim point at
        // height ay over the cell's centre (absolute cells), its range cell on the storey of the aim point.
        private static FakeEngine Engine(float x0, float x1, float y0, float y1, float z0, float z1, int gx, int gz, float ay)
        {
            Bounds b = new Bounds();
            b.SetMinMax(new Vector3(x0, y0, z0), new Vector3(x1, y1, z1));
            int k = Mathf.Clamp(Mathf.FloorToInt(ay / ProjectZ.Api.StoreyHeight), L.min, L.max);
            return new FakeEngine { Volume = b, Aim = new Vector3(gx + 0.5f, ay, gz + 0.5f), RangeCell = L.Cell(new IntVec3(gx, 0, gz), k) };
        }

        private static IEnumerable<IEnumerator<int>> ApiChecks()
        {
            Func<IEnumerator<int>>[] all = { ApiLazy, ApiAimSame, ApiAimCross, ApiCollide, ApiSight, ApiRange, ApiPlacement, ApiSurface, ApiFaults, ApiSaves, ApiDone };
            string[] only = GenCommandLine.TryGetCommandLineArg("cepackapi", out string sel) && sel != null ? sel.Split(',') : null;
            for (int i = 0; i < all.Length; i++)
            {
                yield return only == null || only.Contains((i + 1).ToString()) ? all[i]() : Nothing();
            }
        }

        // Owners the test registers: their warnings are expected (Log_Postfix does not count them).
        internal static bool ExpectedWarning(string text) => text.Contains("cepack.test.")
            || (explosionSaveNoise && text.Contains("pathRelToParent=/damagedThings"))   // (CE's own: ExplosionCE saves damagedThings twice)
            || (text.Contains(Launcher) && (text.StartsWith("Spawning destroyed thing") || text.StartsWith("Couldn't add thing") || text.StartsWith("Spawned thing with 0 stackCount")));   // (CE's own: a spent one-use launcher dropped; also in other modes)

        private static bool explosionSaveNoise;

        // perf -cepackperfreg: a provider for every pawn and a rule, both claiming nothing (the Api's cost with a user
        // installed but no siege engine on the map). Reports how often the rotation hook ran.
        static partial void PerfRegistrations(List<IDisposable> into)
        {
            if (!GenCommandLine.CommandLineArgPassed("cepackperfreg"))
            {
                return;
            }
            into.Add(Api.RegisterTargetGeometry("cepack.test.perf", typeof(Pawn), new FakeProvider()));
            into.Add(Api.RegisterSurfaceExplosionRule("cepack.test.perf", new FakeRule()));
            int r0 = TargetGeometry.RotationCalls;
            into.Add(new Report(() => Out("PERF registrations: " + ApiState() + ", rotation hook calls " + (TargetGeometry.RotationCalls - r0) + ", rules asked " + SurfaceExplosions.RulesAsked)));
        }

        private sealed class Report : IDisposable
        {
            private readonly Action a;

            public Report(Action a)
            {
                this.a = a;
            }

            public void Dispose() => a();
        }

        private static bool PatchedByPack(System.Reflection.MethodBase m)
        {
            Patches info = m != null ? Harmony.GetPatchInfo(m) : null;
            return info != null && info.Owners.Contains(PackZ.H.Id);
        }

        private static System.Reflection.MethodBase SpawnMethod => AccessTools.GetDeclaredMethods(typeof(GenSpawn)).First(m => m.Name == "Spawn" && m.ReturnType == typeof(Thing)
            && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Thing));

        private static string ApiState() => "capabilities " + Api.Capabilities + ", active: geometry " + TargetGeometry.Active + ", surfaces " + SurfaceExplosions.Active
            + "; Thing.Rotation patched " + PatchedByPack(AccessTools.PropertySetter(typeof(Thing), nameof(Thing.Rotation))) + ", GenSpawn.Spawn " + PatchedByPack(SpawnMethod)
            + ", CheckCellForCollision " + PatchedByPack(AccessTools.Method(typeof(ProjectileCE), "CheckCellForCollision"))
            + ", Explosion.AffectCell " + PatchedByPack(AccessTools.Method(typeof(Explosion), "AffectCell"))
            + ", ProjectileCE.Tick " + PatchedByPack(AccessTools.Method(typeof(ProjectileCE), nameof(ProjectileCE.Tick)))
            + ", AdjustedRange " + PatchedByPack(AccessTools.Method(typeof(VerbProperties), nameof(VerbProperties.AdjustedRange)));

        // ---- A0: before any registration nothing of the Api is patched (the cost with no user); Capabilities as at startup.
        private static IEnumerator<int> ApiLazy()
        {
            Out("--- A0 nothing registered");
            Out("  " + ApiState());
            bool none = !PatchedByPack(AccessTools.PropertySetter(typeof(Thing), nameof(Thing.Rotation))) && !PatchedByPack(SpawnMethod)
                && !PatchedByPack(AccessTools.Method(typeof(Explosion), "AffectCell")) && !PatchedByPack(AccessTools.Method(typeof(VerbProperties), nameof(VerbProperties.AdjustedRange)))
                && !TargetGeometry.Active && !SurfaceExplosions.Active;
            Check("A0 nothing registered: no Api patch applied, every hook inactive", none, ApiState());
            Check("A0 Api capabilities: all three parts ready", Api.Capabilities == (ExtensionCapabilities.PlacementQueries | ExtensionCapabilities.TargetGeometry | ExtensionCapabilities.SurfaceExplosions),
                "capabilities " + Api.Capabilities);
            yield return 1;
        }

        // Shots of a shooter at a thing over some ticks (orders again when the shooter stops).
        private static IEnumerable<int> FireAt(Pawn shooter, LocalTargetInfo target, int ticks)
        {
            int t0 = Find.TickManager.TicksGame;
            Loaded(shooter);
            Order(shooter, target);
            string first = shooter.CurJobDef?.defName;
            int reorders = 0;
            for (int t = 0; t < ticks; t += 50)
            {
                yield return 50;
                if (shooter.CurJob == null || shooter.CurJobDef == JobDefOf.Wait_Combat || shooter.CurJobDef == JobDefOf.Wait)
                {
                    Order(shooter, target);
                    reorders++;
                }
            }
            Out("  (fire " + shooter.LabelShort + " at " + target + ": ticks " + t0 + ".." + Find.TickManager.TicksGame + ", job after the order " + first + ", now " + shooter.CurJobDef + ", orders again " + reorders + ", launched " + By(shooter, Launches, t0).Count + ")");
        }

        // A fresh CE gun starts with an empty magazine (the first order would reload for most of a short check): loaded
        // here, as the harness loads turrets (ammo is not the subject).
        private static void Loaded(Pawn shooter)
        {
            CompAmmoUser ammo = shooter.equipment?.Primary?.TryGetComp<CompAmmoUser>();
            if (ammo != null && ammo.HasMagazine && ammo.CurMagCount <= 0)
            {
                ammo.ResetAmmoCount(ammo.SelectedAmmo);
            }
        }

        private static string HitsOn(List<Shot> hits, Thing t, float above) => hits.Count(s => s.hit.EndsWith("#" + t.thingIDNumber)) + " on it ("
            + hits.Count(s => s.hit.EndsWith("#" + t.thingIDNumber) && s.height > above) + " above " + above.ToString("0.00") + " CE units)";

        // ---- A1: same floor: the engine's aim point over its native height; the shots hit the volume up there.
        private static IEnumerator<int> ApiAimSame()
        {
            Out("--- A1 custom aim and volume, same floor");
            Wipe(10, 10, 45, 45);
            Thing engine = Build("Sandbags", On(25, 35, 0));
            Pawn shooter = Colonist(On(25, 15, 0), Rifle);
            FakeProvider provider = new FakeProvider();
            provider.Engines[engine] = Engine(24f, 27f, 0f, 1.6f, 34f, 37f, 25, 35, 1.4f);
            float native = Aim.boundsFor(engine).max.y;
            IDisposable reg = Api.RegisterTargetGeometry(Owner, typeof(Building), provider);
            Out("  registered: " + ApiState() + "; engine native top " + native.ToString("0.00") + " CE units, claimed " + TargetGeometry.Claimed);
            yield return 30;
            string can = CanHit(shooter, engine);
            Verb_LaunchProjectileCE verb = (Verb_LaunchProjectileCE)VerbOf(shooter);
            TargetGeometry.TryAim(engine, verb, shooter.Position, out TargetAim aim);
            int t0 = Find.TickManager.TicksGame;
            foreach (int w in FireAt(shooter, engine, 300))
            {
                yield return w;
            }
            List<Shot> on = By(shooter, Impacts, t0);
            reg.Dispose();
            engine.HitPoints = engine.MaxHitPoints;
            yield return 60;
            int t1 = Find.TickManager.TicksGame;
            foreach (int w in FireAt(shooter, engine, 300))
            {
                yield return w;
            }
            List<Shot> off = By(shooter, Impacts, t1);
            Out("  registered: can hit " + can + ", aim " + aim.PointVirtual.ToString("F2") + " range cell " + aim.RangeCell + ", impacts " + on.Count + ", " + HitsOn(on, engine, native + 0.05f) + ": " + Hits(on));
            Out("  disposed:   impacts " + off.Count + ", " + HitsOn(off, engine, native + 0.05f) + ": " + Hits(off));
            int high = on.Count(s => s.hit.EndsWith("#" + engine.thingIDNumber) && s.height > native + 0.05f);
            int highOff = off.Count(s => s.hit.EndsWith("#" + engine.thingIDNumber) && s.height > native + 0.05f);
            Check("A1 same floor: shots aim at the provider's point and hit the volume above the engine's own top", can == "yes" && high > 0, "can hit " + can + ", hits above the native top " + high + "/" + on.Count);
            Check("A1 same floor, disposed: back to CE's own aim (no hit above the engine's own top)", highOff == 0 && off.Count(s => s.hit.EndsWith("#" + engine.thingIDNumber)) > 0,
                "hits above the native top " + highOff + ", on the engine " + off.Count(s => s.hit.EndsWith("#" + engine.thingIDNumber)) + "/" + off.Count);
            Gone(shooter, engine);
        }

        // ---- A2: between floors: (a) a shooter on a deck shoots down at the engine's aim point; (b) a tall engine whose aim
        // point is in floor 1's storey (open air over it): a ground shooter's shots cross the plane and hit its volume there.
        private static IEnumerator<int> ApiAimCross()
        {
            Out("--- A2 custom aim and volume between floors");
            Wipe(50, 10, 90, 45);
            Slab(60, 12, 64, 16);
            Thing engine = Build("Sandbags", On(62, 35, 0));
            Pawn shooter = Colonist(On(62, 16, 1), Rifle);
            Thing tall = Build("Sandbags", On(80, 35, 0));
            Pawn shooter2 = Colonist(On(80, 15, 0), Rifle);
            FakeProvider provider = new FakeProvider();
            provider.Engines[engine] = Engine(61f, 64f, 0f, 1.5f, 34f, 37f, 62, 35, 1.2f);
            provider.Engines[tall] = Engine(79f, 82f, 0f, 3.0f, 34f, 37f, 80, 35, 2.3f);
            float native = Aim.boundsFor(engine).max.y;
            IDisposable reg = Api.RegisterTargetGeometry(Owner, typeof(Building), provider);
            yield return 30;
            string can = CanHit(shooter, engine), can2 = CanHit(shooter2, tall);
            int t0 = Find.TickManager.TicksGame;
            Loaded(shooter2);
            Order(shooter2, tall);
            foreach (int w in FireAt(shooter, engine, 400))
            {
                yield return w;
                if (shooter2.CurJob == null || shooter2.CurJobDef == JobDefOf.Wait_Combat)
                {
                    Order(shooter2, tall);
                }
            }
            List<Shot> down = By(shooter, Impacts, t0), up = By(shooter2, Impacts, t0);
            reg.Dispose();
            engine.HitPoints = engine.MaxHitPoints;
            tall.HitPoints = tall.MaxHitPoints;
            yield return 60;
            int t1 = Find.TickManager.TicksGame;
            Loaded(shooter2);
            Order(shooter2, tall);
            foreach (int w in FireAt(shooter, engine, 400))
            {
                yield return w;
                if (shooter2.CurJob == null || shooter2.CurJobDef == JobDefOf.Wait_Combat)
                {
                    Order(shooter2, tall);
                }
            }
            List<Shot> downOff = By(shooter, Impacts, t1), upOff = By(shooter2, Impacts, t1);
            Out("  (a) deck to ground: can hit " + can + ", " + HitsOn(down, engine, native + 0.05f) + ": " + Hits(down) + " | disposed " + HitsOn(downOff, engine, native + 0.05f));
            Out("  (b) tall engine: can hit " + can2 + ", impacts on floor 1 " + up.Count(s => s.frame == 1) + ", on it on floor 1 " + up.Count(s => s.frame == 1 && s.hit.EndsWith("#" + tall.thingIDNumber))
                + ": " + Hits(up) + " | disposed: on floor 1 " + upOff.Count(s => s.frame == 1) + ", on it " + upOff.Count(s => s.hit.EndsWith("#" + tall.thingIDNumber)));
            Check("A2a deck to ground: hits on the volume above the engine's own top, in the ground storey", can == "yes"
                && down.Any(s => s.frame == 0 && s.hit.EndsWith("#" + engine.thingIDNumber) && s.height > native + 0.05f), "can hit " + can + ", " + HitsOn(down, engine, native + 0.05f));
            Check("A2b tall engine: the ground shooter's shots cross into floor 1 and hit its volume there", can2 == "yes"
                && up.Any(s => s.frame == 1 && s.hit.EndsWith("#" + tall.thingIDNumber)), "can hit " + can2 + ", on it on floor 1 " + up.Count(s => s.frame == 1 && s.hit.EndsWith("#" + tall.thingIDNumber)));
            Check("A2 disposed: CE's own aim again (no hit above the top, nothing on floor 1)", !downOff.Any(s => s.hit.EndsWith("#" + engine.thingIDNumber) && s.height > native + 0.05f)
                && !upOff.Any(s => s.frame == 1), HitsOn(downOff, engine, native + 0.05f) + ", floor 1 impacts " + upOff.Count(s => s.frame == 1));
            Gone(shooter, shooter2, engine, tall);
        }

        // ---- A3: a volume off the engine's own cell stops a shot at a target behind it (CheckCellForCollision).
        private static IEnumerator<int> ApiCollide()
        {
            Out("--- A3 projectiles meet the volume off the engine's cell");
            Wipe(95, 10, 125, 50);
            Thing engine = Build("Sandbags", On(105, 30, 0));
            Pawn target = Target(On(108, 42, 0));
            Pawn shooter = Colonist(On(108, 15, 0), Rifle);
            FakeProvider provider = new FakeProvider();
            provider.Engines[engine] = Engine(104f, 111f, 0f, 2.0f, 29f, 32f, 105, 30, 1.0f);
            IDisposable reg = Api.RegisterTargetGeometry(Owner, typeof(Building), provider);
            yield return 30;
            int vh0 = TargetGeometry.VolumeHits;
            int t0 = Find.TickManager.TicksGame;
            foreach (int w in FireAt(shooter, target, 300))
            {
                yield return w;
            }
            List<Shot> on = By(shooter, Impacts, t0);
            int volumeHits = TargetGeometry.VolumeHits - vh0;
            reg.Dispose();
            engine.HitPoints = engine.MaxHitPoints;
            yield return 60;
            int t1 = Find.TickManager.TicksGame;
            foreach (int w in FireAt(shooter, target, 300))
            {
                yield return w;
            }
            List<Shot> off = By(shooter, Impacts, t1);
            int e1 = on.Count(s => s.hit.EndsWith("#" + engine.thingIDNumber)), e2 = off.Count(s => s.hit.EndsWith("#" + engine.thingIDNumber));
            int m1 = on.Count(s => s.hit.EndsWith("#" + target.thingIDNumber)), m2 = off.Count(s => s.hit.EndsWith("#" + target.thingIDNumber));
            Out("  registered: engine hits " + e1 + ", target hits " + m1 + ", contact points on the volume " + volumeHits + ": " + Hits(on));
            Out("  disposed:   engine hits " + e2 + ", target hits " + m2 + ": " + Hits(off));
            Check("A3 a shot past the engine's cell meets its volume (the engine is hit, not the target behind)", e1 > 0 && e1 >= m1, "engine " + e1 + ", target " + m1 + "/" + on.Count);
            Check("A3 disposed: the shots pass the engine's cell (target hit, engine not)", e2 == 0 && m2 > 0, "engine " + e2 + ", target " + m2 + "/" + off.Count);
            Gone(shooter, target, engine);
        }

        // ---- A4: sight between floors is blocked by a volume across the line (and only while it is registered).
        private static IEnumerator<int> ApiSight()
        {
            Out("--- A4 sight between floors blocked by a volume");
            Wipe(130, 10, 165, 45);
            Slab(143, 12, 147, 16);
            Thing engine = Build("Sandbags", On(140, 25, 0));
            Pawn target = Target(On(145, 36, 0));
            Pawn shooter = Colonist(On(145, 16, 1), Rifle);
            FakeProvider provider = new FakeProvider();
            provider.Engines[engine] = Engine(139f, 150f, 0f, 3.0f, 24f, 27f, 140, 25, 1.0f);
            IDisposable reg = Api.RegisterTargetGeometry(Owner, typeof(Building), provider);
            yield return 30;
            int sb0 = TargetGeometry.SightBlocks;
            string can = CanHit(shooter, target);
            int blocks = TargetGeometry.SightBlocks - sb0;
            reg.Dispose();
            yield return 5;
            string can2 = CanHit(shooter, target);
            Out("  registered: can hit " + can + " (volume blocks " + blocks + "); disposed: can hit " + can2);
            Check("A4 a volume across the line between floors blocks the sight", can.StartsWith("no") && blocks > 0, "can hit " + can + ", blocks " + blocks);
            Check("A4 disposed: the line is clear again", can2 == "yes", "can hit " + can2);
            Out("  REPORT A4: on one floor CE's own sight walks run (the pack only walks pairs on two floors or at a claimed target), so a volume blocks lines on one floor only through projectiles (A3)");
            Gone(shooter, target, engine);
        }

        // ---- A5: range is measured to the RangeCell: a far engine in range by its near range cell, and the reverse.
        private static IEnumerator<int> ApiRange()
        {
            Out("--- A5 range by the range cell");
            Wipe(170, 10, 225, 80);
            Pawn shooter = Colonist(On(180, 12, 0), "Gun_Revolver");
            float range = VerbOf(shooter).EffectiveRange;
            int far = Mathf.CeilToInt(range) + 10, near = Mathf.FloorToInt(range) - 4;
            Thing farEngine = Build("Sandbags", On(180, 12 + far, 0));
            Thing nearEngine = Build("Sandbags", On(200, 12 + near, 0));
            Pawn shooter2 = Colonist(On(200, 12, 0), "Gun_Revolver");
            FakeProvider provider = new FakeProvider();
            FakeEngine fe = Engine(179f, 182f, 0f, 1.5f, 11f + far, 14f + far, 180, 12 + far, 1.0f);
            fe.RangeCell = On(180, 12 + near, 0);
            FakeEngine ne = Engine(199f, 202f, 0f, 1.5f, 11f + near, 14f + near, 200, 12 + near, 1.0f);
            ne.RangeCell = On(200, 12 + far, 0);
            provider.Engines[farEngine] = fe;
            provider.Engines[nearEngine] = ne;
            IDisposable reg = Api.RegisterTargetGeometry(Owner, typeof(Building), provider);
            yield return 30;
            string canFar = CanHit(shooter, farEngine), canNear = CanHit(shooter2, nearEngine);
            int t0 = Find.TickManager.TicksGame;
            foreach (int w in FireAt(shooter, farEngine, 200))
            {
                yield return w;
            }
            int launched = By(shooter, Launches, t0).Count;
            reg.Dispose();
            yield return 5;
            string canFarOff = CanHit(shooter, farEngine), canNearOff = CanHit(shooter2, nearEngine);
            Out("  revolver range " + range.ToString("0.0") + ": far engine (" + far + " cells, range cell " + near + ") can hit " + canFar + ", launched " + launched
                + "; near engine (" + near + " cells, range cell " + far + ") can hit " + canNear + "; disposed: far " + canFarOff + ", near " + canNearOff);
            Check("A5 range to the range cell: the far engine is in range (shots fired), the near one out of range", canFar == "yes" && launched > 0 && canNear.StartsWith("no"),
                "far " + canFar + " (launched " + launched + "), near " + canNear);
            Check("A5 disposed: range to the engine itself again", canFarOff.StartsWith("no") && canNearOff == "yes", "far " + canFarOff + ", near " + canNearOff);
            Gone(shooter, shooter2, farEngine, nearEngine);
        }

        // ---- P1-P6: placement queries, each compared with the real turret built at the same cell (its own targeting).
        private static IEnumerator<int> ApiPlacement()
        {
            Out("--- P placement queries");
            Wipe(10, 55, 125, 110);
            Slab(60, 60, 70, 100);    // P3: the slab over the query cell and under the target; P4: the deck
            ThingDef mini = DefDatabase<ThingDef>.GetNamed("Turret_AutoMiniTurret");
            (string name, IntVec3 cell, IntVec3 target, string wall, FixedObstruction want, bool inRange)[] cases =
            {
                ("P1 clear, ground to ground", On(20, 60, 0), On(20, 76, 0), null, FixedObstruction.None, true),
                ("P2 wall in the way", On(35, 60, 0), On(35, 76, 0), "Wall", FixedObstruction.Building, true),
                ("P3 slab overhead, target on floor 1", On(65, 62, 0), On(65, 90, 1), null, FixedObstruction.FloorPlane, true),
                ("P4 clear, deck to ground", On(66, 70, 1), On(80, 86, 0), null, FixedObstruction.None, true),
                ("P5 out of range", On(12, 105, 0), On(82, 105, 0), null, FixedObstruction.None, false),
            };
            foreach ((string name, IntVec3 cell, IntVec3 targetCell, string wall, FixedObstruction want, bool inRange) in cases)
            {
                Thing w = wall != null ? Build(wall, new IntVec3((cell.x + targetCell.x) / 2, 0, (cell.z + targetCell.z) / 2)) : null;
                Pawn raider = Raider(targetCell);
                yield return 5;
                GeometryStatus open = Api.OpenPlacement(new PlacementSpec(Map, mini, null, null, cell, Rot4.North, Faction.OfPlayer), out IPlacementQuery q);
                WeaponGeometry weapon = default;
                RangeArcResult ra = default;
                FixedCorridorResult fc = default;
                GeometryStatus s1 = GeometryStatus.Unavailable, s2 = GeometryStatus.Unavailable, s3 = GeometryStatus.Unavailable;
                if (open == GeometryStatus.Success)
                {
                    s1 = q.ReadWeapon(out weapon);
                    s2 = q.CheckRangeAndArc(raider, out ra);
                    s3 = q.TraceFixedCorridor(raider, out fc);
                    q.Dispose();
                }
                // the real turret at the cell, its own targeting
                Building_TurretGunCE turret = (Building_TurretGunCE)Build("Turret_AutoMiniTurret", cell);
                turret.TryGetComp<CompCanBeDormant>()?.WakeUp();
                Load(turret);
                int t0 = Find.TickManager.TicksGame;
                for (int i = 0; i < 30; i++)
                {
                    yield return 10;
                    Load(turret);
                }
                int launched = By(turret, Launches, t0).Count;
                bool realFires = launched > 0;
                Out("  " + name + ": open " + open + ", weapon " + s1 + " range " + weapon.EffectiveRange.ToString("0.0") + " direct " + weapon.DirectFire + ", range/arc " + s2 + " " + ra
                    + ", corridor " + s3 + " " + fc.Obstruction + " at " + fc.Cell + " " + fc.ContactVirtual.ToString("F2") + "; real turret launched " + launched);
                bool ok = open == GeometryStatus.Success && s1 == GeometryStatus.Success && s2 == GeometryStatus.Success && s3 == GeometryStatus.Success
                    && fc.Obstruction == want && ra.InRange == inRange && ra.WithinArc && realFires == (fc.Clear && ra.InRange);
                Check(name + ": the query's answer (" + want + ", in range " + inRange + ") and the real turret agree", ok,
                    "corridor " + fc.Obstruction + ", in range " + ra.InRange + " (" + ra.MaximumRangeDistance.ToString("0.0") + "/" + weapon.EffectiveRange.ToString("0.0") + "), real turret fires " + realFires);
                Gone(turret, raider, w);
                yield return 5;
            }
            // P6: the fire arc of a manned turret (CE's CompFireArc), the query against the built turret's own test
            ThingDef flak = DefDatabase<ThingDef>.GetNamed("Turret_Flak");   // (CE: fire arc 10-135 degrees)
            IntVec3 at = On(110, 80, 0);
            Pawn front = Raider(On(110, 95, 0)), back = Raider(On(110, 66, 0));
            GeometryStatus o6 = Api.OpenPlacement(new PlacementSpec(Map, flak, GenStuff.DefaultStuffFor(flak), null, at, Rot4.North, Faction.OfPlayer), out IPlacementQuery q6);
            RangeArcResult rf = default, rb = default;
            Traverse arcSetting = Traverse.Create(Controller.settings).Field("enableArcOfFire");
            bool arcWas = arcSetting.GetValue<bool>();
            arcSetting.SetValue(true);   // (CE's setting "arc of fire", off by default; restored below, never written)
            if (o6 == GeometryStatus.Success)
            {
                q6.CheckRangeAndArc(front, out rf);
                q6.CheckRangeAndArc(back, out rb);
                q6.Dispose();
            }
            Building_TurretGunCE built = (Building_TurretGunCE)Build("Turret_Flak", at, Rot4.North);
            CompFireArc arc = built.CompFireArc;
            if (arc != null && arc.CurrentSpan < 0f)
            {
                arc.CurrentSpan = arc.Props.spanRange.max;
            }
            bool realFront = arc?.WithinFireArc(front) ?? true, realBack = arc?.WithinFireArc(back) ?? true;
            arcSetting.SetValue(arcWas);
            Out("  P6 flak gun facing north: query front " + rf.WithinArc + " back " + rb.WithinArc + "; built turret (CE's own test) front " + realFront + " back " + realBack + ", arc comp " + (arc != null));
            Check("P6 fire arc: the query answers as CE's own test on the built turret", o6 == GeometryStatus.Success && rf.WithinArc == realFront && rb.WithinArc == realBack && realFront != realBack,
                "query " + rf.WithinArc + "/" + rb.WithinArc + ", built " + realFront + "/" + realBack);
            // nested queries never share a turret; a disposed query is inert
            Api.OpenPlacement(new PlacementSpec(Map, mini, null, null, On(20, 60, 0), Rot4.North, Faction.OfPlayer), out IPlacementQuery a);
            Api.OpenPlacement(new PlacementSpec(Map, mini, null, null, On(30, 60, 0), Rot4.North, Faction.OfPlayer), out IPlacementQuery b);
            a.ReadWeapon(out WeaponGeometry wa);
            b.Dispose();
            a.Dispose();
            GeometryStatus after = a.ReadWeapon(out _);
            Check("P nested queries and dispose: two at once work, a disposed query answers InvalidInput", wa.EffectiveRange > 0f && after == GeometryStatus.InvalidInput, "range " + wa.EffectiveRange + ", after dispose " + after);
            Gone(built, front, back);
        }

        // ---- S1: grenade launchers under a slab fire at a far target; rounds rising into the slab's underside burst there.
        // Three slabs: one before the rule (CE as before), one where the rule says yes, one where it says no.
        private static int Broken(int x0, int z0, int x1, int z1)
        {
            int n = 0;
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    if (Map.terrainGrid.TerrainAt(On(x, z, 1)) != Def("PZ_UpperFloor"))
                    {
                        n++;
                    }
                }
            }
            return n;
        }

        // Three rocket launchers on the ground fire at cells on floor 1, 3 cells past a slab's edge (as check 7c): the line
        // clears the edge, CE's spread makes some rockets meet the slab's underside at the edge and burst there (a rocket's
        // blast is strong enough to break a wood slab; CE's 40 mm grenade, 20 at the slab cell, is not).
        private const string Launcher = "Gun_TripleRocket";

        // A spent launcher (one-use under CE) is replaced, its magazine loaded.
        private static void Rearm(Pawn s)
        {
            if (s.Spawned && (s.equipment.Primary == null || s.equipment.Primary.Destroyed))
            {
                s.equipment.DestroyAllEquipment();
                ThingDef wd = DefDatabase<ThingDef>.GetNamed(Launcher);
                s.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(wd, wd.MadeFromStuff ? GenStuff.DefaultStuffFor(wd) : null));
            }
            Loaded(s);
        }
        private static List<(Pawn shooter, IntVec3 cell)> Garage(int x, int z)
        {
            Slab(x - 4, z + 10, x + 4, z + 18);
            List<(Pawn, IntVec3)> list = new List<(Pawn, IntVec3)>();
            for (int i = -1; i <= 1; i++)
            {
                list.Add((Colonist(On(x + 2 * i, z, 0), Launcher), On(x + 2 * i, z + 13, 1)));
            }
            return list;
        }

        // Every launcher fires at its cell for some ticks (ordered again when it stops).
        private static IEnumerable<int> FireAll(List<(Pawn shooter, IntVec3 cell)> fixture, int ticks)
        {
            foreach ((Pawn s, IntVec3 c) in fixture)
            {
                Rearm(s);
                Order(s, c);
            }
            for (int t = 0; t < ticks; t += 50)
            {
                yield return 50;
                foreach ((Pawn s, IntVec3 c) in fixture)
                {
                    if (s.Spawned && (s.CurJob == null || s.CurJobDef == JobDefOf.Wait_Combat || s.CurJobDef == JobDefOf.Wait))
                    {
                        Rearm(s);
                        Order(s, c);
                    }
                }
            }
        }

        private static int Rounds(List<(Pawn shooter, IntVec3 cell)> fixture, int since) => fixture.Sum(f => By(f.shooter, Launches, since).Count);

        private static void Gone(List<(Pawn shooter, IntVec3 cell)> fixture) => Gone(fixture.Select(f => (Thing)f.shooter).ToArray());

        private static IEnumerator<int> ApiSurface()
        {
            Out("--- S1 surface explosions at a slab's underside");
            Wipe(130, 55, 230, 110);
            // no rule: nothing recorded (the part is inactive), CE as before
            List<(Pawn, IntVec3)> none = Garage(140, 60);
            yield return 30;
            int c0 = SurfaceExplosions.ContactsSeen, t0 = Find.TickManager.TicksGame;
            foreach (int w in FireAll(none, 900))
            {
                yield return w;
            }
            Out("  no rule: rounds " + Rounds(none, t0) + ", contacts recorded " + (SurfaceExplosions.ContactsSeen - c0) + ", slab cells broken " + Broken(136, 70, 144, 78));
            Check("S1 no rule: nothing recorded, the rounds fly as CE has them", SurfaceExplosions.ContactsSeen == c0 && Rounds(none, t0) > 0, "contacts " + (SurfaceExplosions.ContactsSeen - c0) + ", rounds " + Rounds(none, t0));
            Gone(none);
            // the rule says yes for this fixture
            FakeRule rule = new FakeRule();
            rule.Yes.Add(CellRect.FromLimits(160, 50, 172, 90));
            IDisposable reg = Api.RegisterSurfaceExplosionRule(Owner, rule);
            List<(Pawn, IntVec3)> yesFix = Garage(166, 60);
            yield return 30;
            int added0 = SurfaceExplosions.TerrainAdded, hits0 = SurfaceExplosions.TerrainHits;
            t0 = Find.TickManager.TicksGame;
            foreach (int w in FireAll(yesFix, 900))
            {
                yield return w;
            }
            yield return 600;   // (a slab cell is reached at its affect tick)
            List<SurfaceContact> below = rule.Seen.Where(c => c.IncidentSide == SurfaceSide.Below).ToList();
            int brokenAt = below.Select(c => c.SurfaceCell).Distinct().Count(c => Map.terrainGrid.TerrainAt(c) != Def("PZ_UpperFloor"));
            int added = SurfaceExplosions.TerrainAdded - added0, steps = SurfaceExplosions.TerrainHits - hits0;
            Out("  rule yes: rounds " + Rounds(yesFix, t0) + ", asked " + rule.Seen.Count + " (below " + below.Count + ": " + string.Join(" ", below.Take(6).Select(c => c.SurfaceCell + (c.HasFloor ? " floor" : " roof"))) + "), slab cells added "
                + added + ", terrain steps " + steps + ", broken at a contact " + brokenAt + ", last added " + SurfaceExplosions.LastAdded + ", its damage " + SurfaceExplosions.LastDamage);
            Check("S1 rule yes: a burst under the slab runs the terrain step on the slab cell and breaks it", below.Count > 0 && added > 0 && steps > 0 && brokenAt > 0,
                "below contacts " + below.Count + ", added " + added + ", steps " + steps + ", broken " + brokenAt);
            Gone(yesFix);
            // the rule says no here
            int seen0 = rule.Seen.Count;
            List<(Pawn, IntVec3)> noFix = Garage(196, 60);
            yield return 30;
            added0 = SurfaceExplosions.TerrainAdded;
            t0 = Find.TickManager.TicksGame;
            foreach (int w in FireAll(noFix, 900))
            {
                yield return w;
            }
            int belowNo = rule.Seen.Skip(seen0).Count(c => c.IncidentSide == SurfaceSide.Below);
            Out("  rule no: rounds " + Rounds(noFix, t0) + ", asked " + (rule.Seen.Count - seen0) + " (below " + belowNo + "), slab cells added " + (SurfaceExplosions.TerrainAdded - added0));
            Check("S1 rule no: the rule is asked, no slab cell joins the blast", belowNo > 0 && SurfaceExplosions.TerrainAdded == added0, "below contacts " + belowNo + ", added " + (SurfaceExplosions.TerrainAdded - added0));
            reg.Dispose();
            Gone(noFix);
        }

        // ---- F1-F5: broken providers and rules: one warning each, the native path, no exception into the game.
        private static IEnumerator<int> ApiFaults()
        {
            Out("--- F broken providers and rules");
            Wipe(10, 115, 125, 170);
            int errors0 = errors;
            string[] modes = { "nanaim", "throwaim", "nanvolume", "throwvolume" };
            for (int i = 0; i < modes.Length; i++)
            {
                int x = 20 + 25 * i;
                Thing engine = Build("Sandbags", On(x, 140, 0));
                Pawn shooter = Colonist(On(x, 120, 0), Rifle);
                FakeProvider provider = new FakeProvider { Mode = modes[i] };
                provider.Engines[engine] = Engine(x - 1f, x + 2f, 0f, 1.6f, 139f, 142f, x, 140, 1.4f);
                string owner = "cepack.test.broken." + modes[i];
                int faults0 = TargetGeometry.Faults;
                IDisposable reg = null;
                string thrown = "none";
                try
                {
                    reg = Api.RegisterTargetGeometry(owner, typeof(Building), provider);
                }
                catch (Exception e)
                {
                    thrown = e.GetType().Name;
                }
                yield return 10;
                Thing extra = Build("Sandbags", On(x + 3, 130, 0));   // (a spawn while the broken provider is registered)
                int t0 = Find.TickManager.TicksGame;
                foreach (int w in FireAt(shooter, engine, 250))
                {
                    yield return w;
                }
                List<Shot> hits = By(shooter, Impacts, t0);
                bool warned = PackZ.OnceKeys.Any(k => k.Contains(owner));
                Out("  F" + (i + 1) + " " + modes[i] + ": register threw " + thrown + ", faults " + (TargetGeometry.Faults - faults0) + ", warned " + warned + ", asks volume " + provider.VolumeAsks + " aim " + provider.AimAsks
                    + ", active now " + TargetGeometry.Active + ", impacts " + hits.Count + " (on the engine " + hits.Count(s => s.hit.EndsWith("#" + engine.thingIDNumber)) + ")");
                Check("F" + (i + 1) + " provider " + modes[i] + ": one warning, switched off, the shots fly as CE has them", thrown == "none" && TargetGeometry.Faults - faults0 == 1 && warned
                    && !TargetGeometry.Active && hits.Count > 0, "thrown " + thrown + ", faults " + (TargetGeometry.Faults - faults0) + ", warned " + warned + ", active " + TargetGeometry.Active + ", impacts " + hits.Count);
                reg?.Dispose();
                Gone(shooter, engine, extra);
            }
            // F5: a rule that throws (a launcher bursting under a slab, as S1)
            FakeRule rule = new FakeRule { Throws = true };
            rule.Yes.Add(CellRect.FromLimits(0, 0, 250, 250));
            IDisposable rr = Api.RegisterSurfaceExplosionRule("cepack.test.broken.rule", rule);
            List<(Pawn, IntVec3)> fix = Garage(110, 120);
            int sf0 = SurfaceExplosions.Faults, t5 = Find.TickManager.TicksGame;
            foreach (int w in FireAll(fix, 900))
            {
                yield return w;
            }
            int broken = rule.Seen.Count(c => Map.terrainGrid.TerrainAt(c.SurfaceCell) != Def("PZ_UpperFloor") && c.IncidentSide == SurfaceSide.Below);
            bool warnedRule = PackZ.OnceKeys.Any(k => k.Contains("cepack.test.broken.rule"));
            Out("  F5 throwing rule: asked " + rule.Seen.Count + ", faults " + (SurfaceExplosions.Faults - sf0) + ", warned " + warnedRule + ", active " + SurfaceExplosions.Active + ", slab broken at the contact " + broken + ", rounds " + Rounds(fix, t5));
            Check("F5 rule that throws: asked once, one warning, switched off, the slab as CE has it", rule.Seen.Count == 1 && SurfaceExplosions.Faults - sf0 == 1 && warnedRule && !SurfaceExplosions.Active && broken == 0,
                "asked " + rule.Seen.Count + ", faults " + (SurfaceExplosions.Faults - sf0) + ", warned " + warnedRule + ", broken " + broken);
            rr.Dispose();
            Check("F no exception reached the game (errors during the fault checks)", errors == errors0, (errors - errors0) + " errors");
            Gone(fix);
        }

        // ---- V1-V3: saves. V1 with the rule, saved while a burst has the slab cell pending: the save has the data (and no
        // empty nodes), the slab breaks after the reload as in the live run. V2 without the rule, saved mid-flight: no data
        // in the save. V3 the V1 save loaded with the rule gone: nothing breaks (apiload: the same without the pack's Z part).
        private const string RuleSave = "cepack_api_rule", PlainSave = "cepack_api_plain";

        private static string SaveText(string name)
        {
            string path = GenFilePaths.FilePathForSavedGame(name);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        private static int Count(string text, string word)
        {
            int n = 0;
            for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.Ordinal))
            {
                n++;
            }
            return n;
        }

        // The harness's earlier pawns, gone to the world pawns, may still hold a stance on their verb: cleared before a save
        // (else the save warns about an unsaved verb reference; not the Api's matter).
        private static void CalmWorldPawns()
        {
            foreach (Pawn p in Find.WorldPawns.AllPawnsAliveOrDead)
            {
                p.stances?.CancelBusyStanceHard();
            }
        }

        private static IEnumerable<int> Reload(string name)
        {
            Game old = Current.Game;
            held.Clear();
            GameDataSaveLoader.LoadGame(name);
            while (Current.Game == old || Find.CurrentMap == null || LongEventHandler.AnyEventNowOrWaiting)
            {
                yield return 0;
            }
        }

        private static IEnumerator<int> ApiSaves()
        {
            Out("--- V saves with and without the rule");
            Wipe(130, 115, 230, 175);
            FakeRule rule = new FakeRule();
            rule.Yes.Add(CellRect.FromLimits(0, 0, 250, 250));
            IDisposable reg = Api.RegisterSurfaceExplosionRule(Owner, rule);
            List<(Pawn, IntVec3)> fix = Garage(150, 120);
            foreach ((Pawn s, IntVec3 c) in fix)
            {
                Rearm(s);
                Order(s, c);
            }
            int guard = 0;
            // tick by tick until a burst has the slab cell pending
            while (SurfaceExplosions.PendingCells() == 0 && guard++ < 2400)
            {
                yield return 1;
                if (guard % 50 == 0)
                {
                    foreach ((Pawn s, IntVec3 c) in fix)
                    {
                        if (s.CurJob == null || s.CurJobDef == JobDefOf.Wait_Combat || s.CurJobDef == JobDefOf.Wait)
                        {
                            Rearm(s);
                            Order(s, c);
                        }
                    }
                }
            }
            List<IntVec3> pending = SurfaceExplosions.PendingList();
            Out("  pending slab cells " + pending.Count + " (" + string.Join(" ", pending) + ") after " + guard + " ticks");
            CalmWorldPawns();
            explosionSaveNoise = true;   // (saved mid-blast: CE's own double damagedThings list warns on the reload)
            GameDataSaveLoader.SaveGame(RuleSave);
            string text = SaveText(RuleSave);
            int nodes = Count(text, "<cepackSurfaceTerrain>") + Count(text, "<cepackSurfaceContact>"), nulls = Count(text, "<cepackSurfaceTerrain IsNull") + Count(text, "<cepackSurfaceContact IsNull");
            yield return 600;
            List<bool> live = pending.Select(c => Map.terrainGrid.TerrainAt(c) != Def("PZ_UpperFloor")).ToList();
            foreach (int w in Reload(RuleSave))
            {
                yield return w;
            }
            yield return 600;
            List<bool> loaded = pending.Select(c => Map.terrainGrid.TerrainAt(c) != Def("PZ_UpperFloor")).ToList();
            Out("  V1 save: data nodes " + nodes + ", empty nodes " + nulls + "; pending cells broken live " + string.Join(",", live) + ", after the reload " + string.Join(",", loaded));
            Check("V1 with the rule: the save holds the pending slab cell (no empty nodes) and it breaks after the reload as live", pending.Count > 0 && nodes > 0 && nulls == 0
                && live.Any(b => b) && live.SequenceEqual(loaded), "pending " + pending.Count + ", nodes " + nodes + ", empty " + nulls + ", live " + string.Join(",", live) + ", loaded " + string.Join(",", loaded));
            reg.Dispose();
            Gone(Map.mapPawns.AllPawnsSpawned.Cast<Thing>().ToArray());   // (Gone: a spent launcher is not dropped)

            // V2: no rule, saved with rounds in flight
            Wipe(130, 115, 230, 175);
            List<(Pawn, IntVec3)> fix2 = Garage(200, 120);
            foreach ((Pawn s, IntVec3 c) in fix2)
            {
                Rearm(s);
                Order(s, c);
            }
            guard = 0;
            while (!Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).OfType<ProjectileCE>().Any(p => !p.landed && fix2.Any(f => f.Item1 == p.launcher)) && guard++ < 600)
            {
                yield return 1;
            }
            int flying = Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).OfType<ProjectileCE>().Count(p => !p.landed);
            CalmWorldPawns();
            GameDataSaveLoader.SaveGame(PlainSave);
            string text2 = SaveText(PlainSave);
            int any2 = Count(text2, "cepackSurface");
            int e0 = errors;
            foreach (int w in Reload(PlainSave))
            {
                yield return w;
            }
            yield return 120;
            Out("  V2 save without the rule: rounds in flight " + flying + ", surface nodes in the save " + any2 + ", errors after the reload " + (errors - e0));
            Check("V2 without the rule: no surface data in the save, the reload runs", flying > 0 && any2 == 0 && errors == e0, "in flight " + flying + ", nodes " + any2 + ", errors " + (errors - e0));

            // V3: the V1 save, no rule now
            e0 = errors;
            foreach (int w in Reload(RuleSave))
            {
                yield return w;
            }
            yield return 240;
            Check("V3 the save with surface data loads and runs with the rule gone", errors == e0 && Find.CurrentMap != null, (errors - e0) + " errors");
            Gone(Map.mapPawns.AllPawnsSpawned.Cast<Thing>().ToArray());   // (Gone: a spent launcher is not dropped)
            DeleteSave(PlainSave);   // (RuleSave stays for the apiload run, which deletes it)
        }

        private static void DeleteSave(string name)
        {
            try
            {
                string path = GenFilePaths.FilePathForSavedGame(name);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Out("  deleted " + path);
                }
            }
            catch (Exception e)
            {
                Out("  could not delete the save " + name + ": " + e.Message);
            }
        }

        // ---- the end: every registration disposed, every hook inactive again.
        private static IEnumerator<int> ApiDone()
        {
            Out("--- A9 all disposed");
            Out("  " + ApiState() + "; geometry claimed " + TargetGeometry.Claimed + ", faults " + TargetGeometry.Faults + ", volume contacts " + TargetGeometry.VolumeHits + ", sight blocks " + TargetGeometry.SightBlocks
                + "; surfaces contacts " + SurfaceExplosions.ContactsSeen + ", rules asked " + SurfaceExplosions.RulesAsked + ", terrain cells added " + SurfaceExplosions.TerrainAdded + ", terrain steps " + SurfaceExplosions.TerrainHits
                + ", last added " + SurfaceExplosions.LastAdded + "; placement opened " + PlacementQuery.Opened + ", failures " + PlacementQuery.Failures + "; rotation hook calls " + TargetGeometry.RotationCalls);
            Check("A9 every registration disposed: every hook inactive", !TargetGeometry.Active && !SurfaceExplosions.Active, ApiState());
            yield return 1;
        }

        // ---- apiload (with -cepacknoz): the V1 save (surface data in it) loads without the pack's Z part.
        private static IEnumerator<int> ApiLoad()
        {
            Out("--- apiload: the save with surface data, the pack's Z part " + (PackZ.SwitchedOff ? "off" : "on"));
            if (!File.Exists(GenFilePaths.FilePathForSavedGame(RuleSave)))
            {
                Check("apiload", false, "no save " + RuleSave + " (run mode api first)");
                yield break;
            }
            int e0 = errors;
            explosionSaveNoise = true;
            foreach (int w in Reload(RuleSave))
            {
                yield return w;
            }
            yield return 300;
            Check("apiload: the save with surface data loads and runs " + (PackZ.SwitchedOff ? "without the pack's Z part" : "with it"), errors == e0 && Find.CurrentMap != null, (errors - e0) + " errors");
            DeleteSave(RuleSave);
        }
    }
}
