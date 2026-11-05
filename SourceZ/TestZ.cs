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
    // Test harness only (dormant unless the game runs with -quicktest -cepacktest=<mode>): Combat Extended between floors,
    // run as the player plays it. Every check builds its own fixture in its own area of a quick-test map with floors
    // (IsoRim -isorimseed, ProjectZ floors from its settings or -cepackfloors=N), seeds the game's random numbers
    // (Rand.Seed = fixed per check), gives real orders (the targeting gizmo: Verb.OrderForceTarget; a manned turret's
    // OrderAttack) and runs real game ticks (TickManager.DoSingleTick, the game paused between). It reads what happened from
    // CE's own calls (every launch and impact, with the frame it happened in) and checks it.
    //   modes: suite (plan §5 checks 1-7, 12; P3 areas 8-11, 12b; 15 slab edge), p4 (P4 turrets, CIWS, AI), fixb (M13: other mods' shields and CIWS targets, world shells), areas, shields, edge, save (13: save and reload mid-burst), fight (14: a same-floor fight's hash; 14b with explosions)
    //   api / apiload (the public Api: TestApi), perf (a fixed big fight's tick and frame cost: TestPerf)
    //   firing (lead, the fired line and Sight per shot: TestZFiring), cereads (CE's reads against Docs/CeReads.tsv, no map needed: TestCeReads)
    // Output: <savedata>/renders/<-cepacktag>/log.txt, done.txt ("fails=N errors=M").
    [StaticConstructorOnStartup]
    public static partial class TestZ
    {
        public static bool Active;
        private static string mode = "suite";
        private static string outDir;
        private static int fails, errors, passes;
        private static IEnumerator<int> script;
        private static int ticksLeft;
        private static bool done;
        private static readonly List<(Pawn pawn, IntVec3 home)> held = new List<(Pawn, IntVec3)>();

        // what CE did (harness patches)
        public sealed class Shot
        {
            public int id, tick, frame;
            public string def, by, hit, line;
            public IntVec3 ground;
            public float height, angle;   // (angle: the launch angle in degrees)
            public bool overhead;
        }

        public static readonly List<Shot> Impacts = new List<Shot>();
        public static readonly List<Shot> Launches = new List<Shot>();
        public static int InvariantTicks, InvariantBad;

        // per projectile (id): its highest point in cells over the ground and its ticks in the air (Invariant, every tick)
        public static readonly Dictionary<int, (float apex, int air)> Flights = new Dictionary<int, (float, int)>();
        private static string invariantFirst;

        static TestZ()
        {
            if (!GenCommandLine.CommandLineArgPassed("quicktest") || !GenCommandLine.TryGetCommandLineArg("cepacktest", out string m))
            {
                return;
            }
            Active = true;
            mode = m;
            string tag = GenCommandLine.TryGetCommandLineArg("cepacktag", out string t) ? t : "cepack_" + m;
            outDir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", tag);
            Directory.CreateDirectory(outDir);
            foreach (string f in new[] { "log.txt", "done.txt" })
            {
                string p = Path.Combine(outDir, f);
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
            Harmony h = new Harmony("peren.isorimwaylith.cepack.testz");
            h.Patch(AccessTools.Method(typeof(Game), nameof(Game.UpdatePlay)), postfix: new HarmonyMethod(typeof(TestZ), nameof(Update)));
            h.Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)), prefix: new HarmonyMethod(typeof(TestZ), nameof(Tick_Prefix)));
            h.Patch(AccessTools.Method(typeof(ProjectileCE), nameof(ProjectileCE.Impact)), prefix: new HarmonyMethod(typeof(TestZ), nameof(Impact_Prefix)));
            System.Reflection.MethodInfo launch = typeof(ProjectileCE).GetMethods().First(x => x.Name == "Launch" && x.GetParameters().Length == 3 && x.DeclaringType == typeof(ProjectileCE));
            h.Patch(launch, postfix: new HarmonyMethod(typeof(TestZ), nameof(Launch_Postfix)));
            h.Patch(AccessTools.Method(typeof(ProjectileCE), nameof(ProjectileCE.Throw)), postfix: new HarmonyMethod(typeof(TestZ), nameof(Launch_Postfix)));
            h.Patch(Aim.ByName(typeof(Verb_LaunchProjectileCE), "ShiftVecReportFor", 2), postfix: new HarmonyMethod(typeof(TestZ), nameof(Report_Postfix)));
            PatchAreas(h);
            if (mode == "firing")
            {
                PatchFiring(h);
            }
            // the pack's warnings and errors logged before this hook (its startup lines)
            foreach (LogMessage lmsg in Log.Messages)
            {
                if (lmsg.type != LogMessageType.Message && lmsg.text != null && lmsg.text.Contains("[IsoRim CE Pack"))
                {
                    packLines.Add(lmsg.text);
                    File.AppendAllText(Path.Combine(outDir, "log.txt"), "startup log (before the hook): " + lmsg.text + "\n");
                }
            }
            foreach (string lm in new[] { "Warning", "Error" })
            {
                h.Patch(AccessTools.Method(typeof(Log), lm, new[] { typeof(string) }), postfix: new HarmonyMethod(typeof(TestZ), nameof(Log_Postfix)));
            }
            if (GenCommandLine.TryGetCommandLineArg("cepackfloors", out string fl) && int.TryParse(fl, out int floors))
            {
                AccessTools.Field(AccessTools.TypeByName("ProjectZ.Dev.TestRunner"), "FloorsOverride").SetValue(null, floors);
            }
            if (GenCommandLine.CommandLineArgPassed("cap17hide"))
            {
                try
                {
                    Traverse.Create(Waylith.EngineApplication.Instance).Property("Window").Property("Visible").SetValue(false);
                }
                catch (Exception)
                {
                }
            }
            Out("test active: mode " + mode + ", out " + outDir + ", pack Z healthy " + PackZ.Healthy + ", flight " + PackZ.FlightOn + ", switched off " + PackZ.SwitchedOff
                + ", patches " + PackZ.Applied + ", frame swaps " + Safe(() => Aim.Swapped + " (per method: " + string.Join(", ", Aim.SwapsPer.Select(kv => kv.Key + " " + kv.Value)) + ")")
                + ", scoped methods per type: " + Safe(() => string.Join(", ", Scope.UsersPer.Select(kv => kv.Key + " " + kv.Value))));
            Out("mods: "+ string.Join(", ", ModsConfig.ActiveModsInLoadOrder.Where(x => !x.Official).Select(x => x.PackageIdPlayerFacing)));
        }

        // A value of a pack class whose type initializer may have failed (fault runs): its text, or the failure.
        private static string Safe(Func<string> f)
        {
            try
            {
                return f();
            }
            catch (Exception e)
            {
                return "(unavailable: " + e.GetType().Name + ")";
            }
        }

        // CE's projectile fields for the harness itself (not through Frames: a fault run may have Frames broken).
        private static AccessTools.FieldRef<ProjectileCE, Vector3> tExact, tVel, tLast;

        internal static Vector3 TExact(ProjectileCE p) => (tExact ??= AccessTools.FieldRefAccess<ProjectileCE, Vector3>("exactPosition"))(p);

        internal static Vector3 TVel(ProjectileCE p) => (tVel ??= AccessTools.FieldRefAccess<ProjectileCE, Vector3>("velocity"))(p);

        internal static Vector3 TLast(ProjectileCE p) => (tLast ??= AccessTools.FieldRefAccess<ProjectileCE, Vector3>("LastPos"))(p);

        // Frames' projectile moves so far (0 when Frames is unavailable in a fault run).
        private static int FrameMoves()
        {
            try
            {
                return Frames.RebasesUp + Frames.RebasesDown + Frames.PlaneHits + Frames.EdgeExits + Frames.Normalised;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static bool inLog;

        public static void Log_Postfix(string text)
        {
            if (inLog || done || text == null || text.StartsWith("[CE pack test]"))
            {
                return;
            }
            inLog = true;
            try
            {
                bool packLine = text.Contains("[IsoRim CE Pack");
                if (packLine)
                {
                    packLines.Add(text);
                }
                if (script != null && !(packLine && mode == "fault") && !(throwNoise && (text.StartsWith("Tried setting a non-Ballistic TrajectoryWorker") || text.Contains("lerpPosition value changed"))) && !text.StartsWith("Modifying the initial rand seed") && !ExpectedWarning(text) && !text.StartsWith("A hidden ritual precept was missing") && !(mode == "firing" && text.Contains("on unwalkable cell"))
                    && !(worldShotNoise && text.StartsWith("[CE] Tried to fire projectile to unreachable target cell")))
                {
                    errors++;   // (warnings during startup are listed, not counted: IsoRim's known static-field notes)
                }
                File.AppendAllText(Path.Combine(outDir, "log.txt"), (script != null ? "LOG WARN/ERR: " : "startup log: ") + text + "\n");
            }
            finally
            {
                inLog = false;
            }
        }

        // Fixed random numbers for a check.

        private static void Seed(int seed)
        {
            Rand.Seed = seed;
        }

        private static void Out(string s)
        {
            File.AppendAllText(Path.Combine(outDir, "log.txt"), s + "\n");
            inLog = true;
            Log.Message("[CE pack test] " + s);
            inLog = false;
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                passes++;
            }
            else
            {
                fails++;
            }
            Out("CHECK " + (ok ? "PASS" : "FAIL") + " " + name + ": " + detail);
        }

        // ------------------------------------------------------------------ recording
        private static Shot Record(ProjectileCE p, Thing hit)
        {
            Map map = p.Map;
            Layout L = Layout.Of(map);
            Vector3 e = TExact(p);
            int k = L != null ? L.FrameOf(p.Position) : 0;
            IntVec3 pos = p.Position;
            return new Shot
            {
                id = p.thingIDNumber,
                tick = Find.TickManager.TicksGame,
                frame = k,
                def = p.def.defName,
                by = p.launcher?.ThingID ?? "-",
                hit = hit != null ? hit.def.defName + "#" + hit.thingIDNumber : "-",
                ground = L != null ? L.Ground(pos) : pos,
                height = e.y,
                angle = p.shotAngle != 0f ? p.shotAngle * Mathf.Rad2Deg : Mathf.Atan2(TVel(p).y, new Vector2(TVel(p).x, TVel(p).z).magnitude) * Mathf.Rad2Deg,   // (a throw: from its heading)
                overhead = p.def.projectile.flyOverhead,
            };
        }

        public static void Impact_Prefix(ProjectileCE __instance, Thing hitThing)
        {
            if (!Active || __instance.Map == null)
            {
                return;
            }
            Shot s = Record(__instance, hitThing);
            Impacts.Add(s);
            if (Impacts.Count < 400 && !Quiet)
            {
                Out("  impact " + s.def + " id " + s.id + " tick " + s.tick + " frame " + s.frame + " ground " + s.ground + " h " + s.height.ToString("0.00") + " hit " + s.hit);
            }
        }

        public static void Launch_Postfix(ProjectileCE __instance)
        {
            if (!Active || __instance.Map == null)
            {
                return;
            }
            Shot s = Record(__instance, null);
            s.line = __instance.intendedTarget.Thing != null ? __instance.intendedTarget.Thing.def.defName + "#" + __instance.intendedTarget.Thing.thingIDNumber : "-";
            Launches.Add(s);
        }

        // Frame invariant (check 1), every tick: each CE projectile in flight stands on its storey's band (never a void row),
        // its exact point is in that band's rows, and its height is within that storey (above the top floor: any height).
        private static void Invariant(Map map)
        {
            Layout L = Layout.Of(map);
            if (L == null)
            {
                return;
            }
            InvariantTicks++;
            float S = Layout.S;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile))
            {
                if (!(t is ProjectileCE p) || p.landed || p.Destroyed)
                {
                    continue;
                }
                Vector3 e = TExact(p);
                int k = L.FrameOf(p.Position);
                int ke = L.FrameOf(e);
                if (k != Layout.None)
                {
                    float hv = k * ProjectZ.Api.StoreyHeight + e.y * Layout.CellsPerUnit;   // (cells over the ground)
                    Flights.TryGetValue(p.thingIDNumber, out (float apex, int air) f);
                    Flights[p.thingIDNumber] = (Mathf.Max(f.apex, hv), f.air + 1);
                }
                string bad = null;
                if (k == Layout.None)
                {
                    bad = "position in a void row";
                }
                else if (ke != k)
                {
                    bad = "exact point in frame " + ke + ", position in " + k;
                }
                else if (e.y < -0.05f && p.FlightTicks > 0)
                {
                    bad = "below its floor";
                }
                else if (e.y > S + 0.05f && k < L.max)
                {
                    bad = "above its storey";
                }
                if (bad != null)
                {
                    InvariantBad++;
                    invariantFirst ??= p.def.defName + " id " + p.thingIDNumber + " tick " + Find.TickManager.TicksGame + ": " + bad + " (pos " + p.Position + " exact " + e.ToString("F3") + ")";
                }
            }
        }

        // ------------------------------------------------------------------ the loop
        // Every game tick is the harness's own (DoSingleTick from Update): the game's own ticking is held, also in the frames
        // right after a load.
        private static bool manualTick;

        public static bool Tick_Prefix() => !Active || done || manualTick || Find.CurrentMap == null || script == null;
        public static void Update()
        {
            if (!Active || done)
            {
                return;
            }
            try
            {
                Map map = Find.CurrentMap;
                if (map == null || LongEventHandler.AnyEventNowOrWaiting)
                {
                    return;
                }
                if (script == null)
                {
                    script = Script();
                }
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                if (ticksLeft < 0)
                {
                    ticksLeft++;   // (a check waiting rendered frames, not ticks: a negative wait; posture checks)
                    return;
                }
                if (ticksLeft > 0)
                {
                    int n = Mathf.Min(ticksLeft, 250);
                    for (int i = 0; i < n; i++)
                    {
                        manualTick = true;
                        long perfAt = PerfOn ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                        long perfBytes = PerfOn ? GC.GetAllocatedBytesForCurrentThread() : 0;
                        Find.TickManager.DoSingleTick();
                        if (PerfOn)
                        {
                            PerfAlloc += GC.GetAllocatedBytesForCurrentThread() - perfBytes;
                            PerfTickTicks += System.Diagnostics.Stopwatch.GetTimestamp() - perfAt;
                            PerfTicks++;
                        }
                        manualTick = false;
                        Hold();
                        FireTick();
                        Invariant(Find.CurrentMap);
                    }
                    ticksLeft -= n;
                    return;
                }
                if (!script.MoveNext())
                {
                    Finish();
                    return;
                }
                ticksLeft = script.Current;
            }
            catch (Exception e)
            {
                errors++;
                Out("EXCEPTION " + e);
                Finish();
            }
        }

        private static void Finish()
        {
            if (done)
            {
                return;
            }
            Out("invariant: " + InvariantTicks + " ticks checked, " + InvariantBad + " bad" + (invariantFirst != null ? ", first: " + invariantFirst : ""));
            try
            {
                FaultCounters();
            }
            catch (Exception e)
            {
                Out("fault counters unavailable: " + e.GetType().Name);
            }
            foreach (Action a in new Action[] { Counters, () => Out("areas: " + AreaCounters()), P4Counters, FixBCounters })
            {
                try
                {
                    a();
                }
                catch (Exception e)
                {
                    Out("counters unavailable (a pack class failed to initialise: a fault run): " + e.GetType().Name);
                }
            }
            Out("SUMMARY passes=" + passes + " fails=" + fails + " errors=" + errors);
            done = true;
            File.WriteAllText(Path.Combine(outDir, "done.txt"), DateTime.Now.ToString("s") + " fails=" + fails + " errors=" + errors);
        }

        private static void Counters()
        {
            Out("counters: rebases up " + Frames.RebasesUp + " down " + Frames.RebasesDown + ", plane hits " + Frames.PlaneHits + ", edge exits " + Frames.EdgeExits
                + ", normalised " + Frames.Normalised + ", sight walks " + Sight.Walks + " blocked " + Sight.Blocked + ", grenade angles " + Throws.Angles
                + ", high arcs " + Aim.HighArcs + " (lowest lobs " + Aim.LowLobs + "), throws lobbed " + Throws.Lobs + " (full-speed high arcs " + Throws.HighLobs + ", refused " + Throws.Refused + "), slow shots refused (sight " + Aim.SightRefused + ", aim " + Aim.ArcsRefused + ", launches " + Aim.LaunchesRefused + "), spotting " + Aim.SpottingChecks + ", raycasts across " + Frames.RayCastsAcross
                + ", pod drops moved " + PodDrops.Moved + " (fallbacks " + PodDrops.Fallbacks + "), gate refusals " + Gate.Refused + ", log-once keys " + string.Join(",", PackZ.OnceKeys));
        }

        // Targets stay where the fixture put them (an animal shot at would run).
        private static void Hold()
        {
            foreach ((Pawn p, IntVec3 home) in held)
            {
                if (p == null || !p.Spawned || p.Dead || p.Downed)
                {
                    continue;
                }
                if (p.Position != home)
                {
                    p.Position = home;
                    p.Notify_Teleported(true, false);
                }
                if (p.CurJobDef != JobDefOf.Wait)
                {
                    p.mindState.mentalStateHandler.Reset();
                    p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 99999), JobCondition.InterruptForced);
                }
            }
        }

        // ------------------------------------------------------------------ fixtures
        private static Map Map => Find.CurrentMap;
        private static Layout L => Layout.Of(Map);
        private static TerrainDef Def(string name) => DefDatabase<TerrainDef>.GetNamed(name);
        private static RoofDef Roof(string name) => DefDatabase<RoofDef>.GetNamed(name);

        private static IntVec3 On(int gx, int gz, int floor) => L != null ? L.Cell(new IntVec3(gx, 0, gz), floor) : (floor == 0 ? new IntVec3(gx, 0, gz) : IntVec3.Invalid);

        // Everything but harness pawns gone over the ground block and the cells over it; soil, open sky, open air above.
        private static void Wipe(int x0, int z0, int x1, int z1)
        {
            Map map = Map;
            int top = L != null ? L.max : 0;
            for (int k = L != null ? L.min : 0; k <= top; k++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        IntVec3 c = On(x, z, k);
                        if (!c.IsValid || !c.InBounds(map))
                        {
                            continue;
                        }
                        foreach (Thing t in c.GetThingList(map).ToList())
                        {
                            if (!t.Destroyed && t.def.destroyable && !(t is Pawn p && held.Any(hp => hp.pawn == p)))
                            {
                                t.Destroy(DestroyMode.Vanish);
                            }
                        }
                        map.terrainGrid.SetTerrain(c, k == 0 ? TerrainDefOf.Soil : Def("PZ_OpenAir"));
                        map.roofGrid.SetRoof(c, null);
                        map.fogGrid.Unfog(c);
                        map.snowGrid.SetDepth(c, 0f);
                    }
                }
            }
        }

        // A floor laid on floor k over the ground block (ProjectZ's slab terrain up there, the floor's roof under it).
        // slab: the slab def (the wood slab; PZ_UpperFloor_Plasteel is an armoured slab no CE blast breaks: SlabHits).
        private static void Slab(int x0, int z0, int x1, int z1, int k = 1, string slab = "PZ_UpperFloor")
        {
            TerrainDef def = Def(slab);
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    IntVec3 up = On(x, z, k), below = On(x, z, k - 1);
                    Map.terrainGrid.SetTerrain(up, def);
                    Map.roofGrid.SetRoof(below, Roof("PZ_FloorRoof"));
                }
            }
        }

        // A stairwell's opening over ground cells: open air up there, the stairwell roof (no ceiling) under it.
        private static void Opening(int x0, int z0, int x1, int z1, int k = 1)
        {
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    Map.terrainGrid.SetTerrain(On(x, z, k), Def("PZ_OpenAir"));
                    Map.roofGrid.SetRoof(On(x, z, k - 1), Roof("PZ_StairwellRoof"));
                }
            }
        }

        private static Thing Build(string def, IntVec3 c, Rot4? rot = null)
        {
            ThingDef d = DefDatabase<ThingDef>.GetNamed(def);
            Thing t = ThingMaker.MakeThing(d, d.MadeFromStuff ? GenStuff.DefaultStuffFor(d) : null);
            t.SetFaction(Faction.OfPlayer);
            return GenSpawn.Spawn(t, c, Map, rot ?? Rot4.North);
        }

        private static int pawnSeed = 7000;

        private static Pawn Colonist(IntVec3 c, string weapon)
        {
            Pawn p = null;
            for (int i = 0; i < 30; i++)
            {
                Rand.PushState(pawnSeed++);
                try
                {
                    p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                        mustBeCapableOfViolence: true, allowAddictions: false));
                }
                finally
                {
                    Rand.PopState();
                }
                if (!p.WorkTagIsDisabled(WorkTags.Violent) && p.health.capacities.CapableOf(PawnCapacityDefOf.Sight) && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                {
                    break;
                }
            }
            p.skills.GetSkill(SkillDefOf.Shooting).Level = 20;
            p.equipment.DestroyAllEquipment();
            if (weapon != null)
            {
                ThingDef wd = DefDatabase<ThingDef>.GetNamed(weapon);
                ThingWithComps w = (ThingWithComps)ThingMaker.MakeThing(wd, wd.MadeFromStuff ? GenStuff.DefaultStuffFor(wd) : null);
                p.equipment.AddEquipment(w);
            }
            GenSpawn.Spawn(p, c, Map);
            p.drafter.Drafted = true;
            return p;
        }

        private static Pawn Target(IntVec3 c, string kind = "Muffalo")
        {
            Pawn p;
            Rand.PushState(pawnSeed++);
            try
            {
                p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed(kind), null, forceGenerateNewPawn: true));
            }
            finally
            {
                Rand.PopState();
            }
            GenSpawn.Spawn(p, c, Map);
            held.Add((p, c));
            return p;
        }

        private static void Gone(params Thing[] things)
        {
            foreach (Thing t in things)
            {
                held.RemoveAll(h => h.pawn == t);
                if (t is Pawn gp && gp.equipment != null)
                {
                    foreach (ThingWithComps eq in gp.equipment.AllEquipmentListForReading.Where(e => e.Destroyed).ToList())
                    {
                        gp.equipment.GetDirectlyHeldThings().Remove(eq);   // (a one-use launcher spent: nothing to drop)
                    }
                }
                if (t != null && !t.Destroyed)
                {
                    t.Destroy(DestroyMode.Vanish);
                }
            }
        }

        private static Verb VerbOf(Pawn p) => p.equipment?.PrimaryEq?.PrimaryVerb;

        private static string CanHit(Pawn shooter, LocalTargetInfo t)
        {
            Verb v = VerbOf(shooter);
            if (v is Verb_LaunchProjectileCE ce)
            {
                bool ok = ce.CanHitTarget(t, out string report);
                return ok ? "yes" : "no (" + report + ")";
            }
            return v.CanHitTarget(t) ? "yes" : "no";
        }

        private static void Order(Pawn shooter, LocalTargetInfo t) => VerbOf(shooter).OrderForceTarget(t);

        private static List<Shot> By(Thing launcher, List<Shot> from, int since = 0) => from.Where(s => s.by == launcher.ThingID && s.tick >= since).ToList();

        private static string Hits(List<Shot> shots) => string.Join("; ", shots.Take(12).Select(s => "f" + s.frame + " " + s.ground + " h" + s.height.ToString("0.0") + " " + s.hit));

        // ------------------------------------------------------------------ the script
        private static IEnumerator<int> Script()
        {
            yield return 5;
            Map map = Map;
            Find.Storyteller.storytellerComps.Clear();   // no raids or events during the checks
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                p.Destroy(DestroyMode.Vanish);
            }
            Out("map " + map.Size + ", floors " + (L != null ? L.min + ".." + L.max + ", ground rows " + L.rows + ", shift 1 " + (L.Has(1) ? L.Shift(1).ToString() : "-") : "none")
                + ", S (CE units per storey) " + Layout.S.ToString("0.000") + ", cells per CE unit " + Layout.CellsPerUnit.ToString("0.000")
                + ", IsoRim wall " + IsoRim.Api.WallHeight.ToString("0.000") + ", storey " + ProjectZ.Api.StoreyHeight.ToString("0.000"));
            IEnumerable<IEnumerator<int>> checks;
            switch (mode)
            {
                case "fight":
                    checks = new[] { Fight(), FightBlast() };
                    break;
                case "areas":
                    checks = new[] { Explosions(), Suppression(), Danger(), ShieldChecks(), SameFloorAreas() };
                    break;
                case "shields":
                    checks = new[] { ShieldChecks() };
                    break;
                case "edge":
                    checks = new[] { SlabEdge() };
                    break;
                case "p4":
                    checks = P4Checks();
                    break;
                case "fixb":
                    checks = FixBChecks();
                    break;
                case "m230lf":
                    checks = M230Checks();
                    break;
                case "fault":
                    checks = FaultChecks();
                    break;
                case "save":
                    checks = new[] { SaveReload(true), SaveReload(false) };
                    break;
                case "posture":
                    checks = PostureChecks();
                    break;
                case "drafted":
                    checks = DraftedChecks();
                    break;
                case "bench":
                    checks = BenchChecks();
                    break;
                case "popup":
                    checks = PopupChecks();
                    break;
                case "perf":
                    checks = new[] { PerfFight() };
                    break;
                case "api":
                    checks = ApiChecks();
                    break;
                case "apiload":
                    checks = new[] { ApiLoad() };
                    break;
                case "firing":
                    checks = FiringChecks();
                    break;
                case "cereads":
                    checks = new[] { CeReads() };
                    break;
                default:
                    checks = new[] { Balcony(), SlabBlocks(), SlabMidBurst(), Stairwell(), Parapet(), Mortar(), Grenades(), Instant(), Explosions(), Suppression(), Danger(), ShieldChecks(), SameFloorAreas(), SlabEdge() };
                    break;
            }
            int n = 0;
            foreach (IEnumerator<int> c in checks)
            {
                Seed(1000 + n++);
                while (true)
                {
                    bool more;
                    try
                    {
                        more = c.MoveNext();
                    }
                    catch (Exception e)
                    {
                        errors++;
                        Out("EXCEPTION in check: " + e);
                        break;
                    }
                    if (!more)
                    {
                        break;
                    }
                    yield return c.Current;
                }
            }
            if (mode == "suite" || mode == "p4" || mode == "fixb" || mode == "m230lf")
            {
                Check("1 frame invariant", InvariantBad == 0 && InvariantTicks > 0, InvariantTicks + " ticks checked, " + InvariantBad + " bad" + (invariantFirst != null ? ", first: " + invariantFirst : ""));
            }
        }

        // ---- 2. balcony: shooter on floor 1 at an open edge, target on the ground; a sandbag right in front of the target.
        private static IEnumerator<int> Balcony()
        {
            Out("--- 2 balcony down-shot");
            if (L == null || !L.Has(1))
            {
                Check("2 balcony", false, "needs a map with floors");
                yield break;
            }
            Wipe(5, 5, 20, 32);
            Slab(10, 10, 14, 14);
            Thing bag = Build("Sandbags", On(12, 25, 0));
            Pawn target = Target(On(12, 27, 0));
            Pawn shooter = Colonist(On(12, 14, 1), "Gun_AssaultRifle");
            yield return 30;
            string can = CanHit(shooter, target);
            int t0 = Find.TickManager.TicksGame;
            int down0 = Frames.RebasesDown;
            Order(shooter, target);
            reportCover.Clear();
            watchCover = true;
            yield return 600;
            watchCover = false;
            List<Shot> launched = By(shooter, Launches, t0);
            List<Shot> hits = By(shooter, Impacts, t0);
            int onTarget = hits.Count(s => s.hit.EndsWith("#" + target.thingIDNumber));
            int onBag = hits.Count(s => s.hit.EndsWith("#" + bag.thingIDNumber));
            int ground = hits.Count(s => s.frame == 0);
            Out("  can hit " + can + ", launched " + launched.Count + ", impacts " + hits.Count + " (target " + onTarget + ", sandbag " + onBag + ", on the ground band " + ground
                + "), crossings down " + (Frames.RebasesDown - down0) + ", aim cover seen " + string.Join(",", reportCover.Distinct()));
            Out("  impacts: " + Hits(hits));
            Check("2 balcony: line, shots, crossings, hits on the target", can == "yes" && launched.Count > 0 && Frames.RebasesDown > down0 && onTarget > 0,
                "can hit " + can + ", launched " + launched.Count + ", crossings " + (Frames.RebasesDown - down0) + ", target hits " + onTarget + "/" + hits.Count);
            Check("2 balcony: the sandbag below is passed over (not the aim's cover)", !reportCover.Contains(bag.ThingID), "aim cover " + (reportCover.Count == 0 ? "none" : string.Join(",", reportCover.Distinct())) + ", sandbag hits " + onBag);
            Gone(shooter, target, bag);
        }

        // ShiftVecReportFor's cover as CE's aim used it (the real flow).
        private static bool watchCover;
        private static readonly List<string> reportCover = new List<string>();

        public static void Report_Postfix(ShiftVecReport __result)
        {
            if (watchCover && __result != null)
            {
                reportCover.Add(__result.cover != null ? __result.cover.ThingID : "none");
            }
        }

        // ---- 3a. slab: the target under a slab, the shooter on floor 1: no line (the order refused).
        private static IEnumerator<int> SlabBlocks()
        {
            Out("--- 3 slab blocks the line");
            Wipe(25, 5, 40, 32);
            Slab(29, 10, 35, 28);   // one slab: the shooter stands on it, the target is under it
            Pawn target = Target(On(32, 25, 0));
            Pawn shooter = Colonist(On(32, 14, 1), "Gun_AssaultRifle");
            yield return 30;
            string can = CanHit(shooter, target);
            int t0 = Find.TickManager.TicksGame;
            Order(shooter, target);
            yield return 300;
            List<Shot> launched = By(shooter, Launches, t0);
            Check("3 slab: no line to a target under a slab, no shot fired", can.StartsWith("no") && launched.Count == 0, "can hit " + can + ", launched " + launched.Count);
            Gone(shooter, target);
        }

        // ---- 3b. a forced burst stops on the slab: suppressive fire at a target in the open; mid-burst a slab is laid over
        // it; the rest of the burst (CE keeps firing at the last spot under suppressive fire) must stop on the slab.
        private static IEnumerator<int> SlabMidBurst()
        {
            Out("--- 3 forced burst stops on the slab");
            Wipe(45, 5, 60, 34);
            Slab(50, 10, 54, 14);
            Pawn target = Target(On(52, 26, 0));
            Pawn shooter = Colonist(On(52, 14, 1), "Gun_AssaultRifle");
            CompFireModes fm = shooter.equipment.Primary.TryGetComp<CompFireModes>();
            if (fm != null)
            {
                fm.CurrentAimMode = AimMode.SuppressFire;
            }
            yield return 30;
            int t0 = Find.TickManager.TicksGame;
            Order(shooter, target);
            int guard = 0;
            while (By(shooter, Launches, t0).Count == 0 && guard++ < 400)
            {
                yield return 1;
            }
            Slab(49, 15, 55, 30);   // joins the platform: no gap to shoot through
            int tSlab = Find.TickManager.TicksGame;
            Out("  slab laid at tick " + tSlab + " after " + By(shooter, Launches, t0).Count + " launches");
            yield return 400;
            List<Shot> after = By(shooter, Impacts, tSlab + 1).Where(s => s.ground.z >= 15 && s.ground.z <= 30 && s.ground.x >= 49 && s.ground.x <= 55).ToList();
            List<Shot> launchedAfter = By(shooter, Launches, tSlab + 1);
            int below = after.Count(s => s.frame == 0);
            int onSlab = after.Count(s => s.frame == 1);
            Out("  after the slab: launched " + launchedAfter.Count + ", impacts over the slab area " + after.Count + " (on the slab " + onSlab + ", under it " + below + "): " + Hits(after));
            Check("3 slab: nothing launched after the slab reaches under it", below == 0, "launched after " + launchedAfter.Count + ", on the slab " + onSlab + ", under it " + below
                + (launchedAfter.Count == 0 ? " (burst stopped: CE interrupts it without a line)" : ""));
            Gone(shooter, target);
        }

        // ---- 4. stairwell opening: ground shooter shoots up through an opening at a target on floor 1.
        private static IEnumerator<int> Stairwell()
        {
            Out("--- 4 stairwell opening");
            Wipe(64, 3, 92, 24);
            Slab(68, 5, 76, 20);    // the shooter stands under it
            Thing stairs = Build("PZ_Stairs", On(72, 12, 0), Rot4.South);   // a real stairwell: ProjectZ opens the floor over the ramp (landing to the south)
            Out("  stairs at " + stairs.OccupiedRect() + " rot " + stairs.Rotation);
            Slab(80, 5, 88, 20);    // control: the same without the opening
            Pawn target = Target(On(72, 15, 1));
            Pawn target2 = Target(On(84, 15, 1));
            Pawn shooter = Colonist(On(72, 8, 0), "Gun_AssaultRifle");
            Pawn shooter2 = Colonist(On(84, 8, 0), "Gun_AssaultRifle");
            yield return 30;
            Out("  column x=72: " + string.Join(" ", Enumerable.Range(8, 9).Select(z => z + ":" + (ProjectZ.Api.PlaneSolid(new IntVec3(72, 0, z), 1, Map) ? "solid" : "open")
                + "(" + Map.roofGrid.RoofAt(On(72, z, 0))?.defName + "/" + Map.terrainGrid.TerrainAt(On(72, z, 1)).defName + ")")));
            Out("  shooter crouching " + shooter.IsCrouching() + ", CE shot height " + ((Verb_LaunchProjectileCE)VerbOf(shooter)).ShotHeight.ToString("0.00"));
            Sight.Debug = Out;
            string can = CanHit(shooter, target);
            Sight.Debug = null;
            string can2 = CanHit(shooter2, target2);
            int t0 = Find.TickManager.TicksGame;
            int up0 = Frames.RebasesUp;
            Order(shooter, target);
            yield return 600;
            List<Shot> hits = By(shooter, Impacts, t0);
            int onTarget = hits.Count(s => s.hit.EndsWith("#" + target.thingIDNumber));
            Out("  impacts: " + Hits(hits));
            Check("4 stairwell: a shot up through the opening hits", can == "yes" && onTarget > 0 && Frames.RebasesUp > up0,
                "can hit " + can + ", crossings up " + (Frames.RebasesUp - up0) + ", target hits " + onTarget + "/" + hits.Count);
            Check("4 stairwell control: no opening, no line", can2.StartsWith("no"), "can hit " + can2);
            Gone(shooter, shooter2, target, target2, stairs);
        }

        // ---- 5. shooting up at a parapet: ground shooter, target on floor 1 behind sandbags (cover per CE bounds) and
        // behind a wall (no line).
        private static IEnumerator<int> Parapet()
        {
            Out("--- 5 shooting up at a parapet");
            Wipe(95, 3, 124, 24);
            Slab(98, 16, 106, 20);
            Slab(112, 16, 120, 20);
            Thing bag = Build("Sandbags", On(102, 16, 1));
            Thing wall = Build("Wall", On(116, 16, 1));
            Pawn target = Target(On(102, 17, 1));
            Pawn target2 = Target(On(116, 17, 1));
            Pawn shooter = Colonist(On(102, 11, 0), "Gun_AssaultRifle");
            Pawn shooter2 = Colonist(On(116, 11, 0), "Gun_AssaultRifle");
            yield return 30;
            string can = CanHit(shooter, target);
            string can2 = CanHit(shooter2, target2);
            int t0 = Find.TickManager.TicksGame;
            reportCover.Clear();
            watchCover = true;
            Order(shooter, target);
            yield return 600;
            watchCover = false;
            List<Shot> hits = By(shooter, Impacts, t0);
            int onTarget = hits.Count(s => s.hit.EndsWith("#" + target.thingIDNumber));
            int onBag = hits.Count(s => s.hit.EndsWith("#" + bag.thingIDNumber));
            int beyond = hits.Count(s => s.frame == 1 && s.ground.z > 17);
            Out("  impacts: " + Hits(hits));
            Out("  aim cover seen " + string.Join(",", reportCover.Distinct()) + " (sandbag " + bag.ThingID + ")");
            Check("5 parapet (sandbags): line, the parapet is the aim's cover, shots land on floor 1", can == "yes" && reportCover.Contains(bag.ThingID) && hits.Count(s => s.frame == 1) > 0,
                "can hit " + can + ", target hits " + onTarget + ", sandbag hits " + onBag + ", on floor 1 past the target " + beyond + ", impacts " + hits.Count);
            Check("5 parapet (wall): no line", can2.StartsWith("no"), "can hit " + can2);
            Gone(shooter, shooter2, target, target2, bag, wall);
        }

        // ---- 6. mortar at a target under a slab: the shells land on the slab top; does a heavy hit break it (SlabDamage)?
        private static IEnumerator<int> Mortar()
        {
            Out("--- 6 mortar on a target under a slab");
            Wipe(2, 52, 38, 122);
            Slab(6, 92, 34, 118);
            Building_TurretGunCE mortar = (Building_TurretGunCE)Build("Turret_Mortar", On(20, 60, 0));
            Pawn gunner = Colonist(On(20, 58, 0), null);
            gunner.drafter.Drafted = false;
            mortar.CompAmmo?.ResetAmmoCount(mortar.CompAmmo.SelectedAmmo);
            gunner.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, mortar));
            yield return 200;
            IntVec3 aim = On(20, 105, 0);
            int t0 = Find.TickManager.TicksGame;
            mortar.OrderAttack(new LocalTargetInfo(aim));
            Out("  ordered: forced " + mortar.ForcedTarget + ", manned " + (mortar.mannableComp?.MannedNow));
            int guard = 0;
            while (By(gunner, Impacts, t0).Concat(By(mortar, Impacts, t0)).Count(s => s.overhead) < 3 && guard++ < 40)
            {
                yield return 200;
                if (mortar.CompAmmo != null && !mortar.CompAmmo.CanBeFiredNow)
                {
                    mortar.CompAmmo.ResetAmmoCount(mortar.CompAmmo.SelectedAmmo);   // (harness: the shells are loaded; ammo is not the subject)
                }
                if (guard % 5 == 0)
                {
                    Out("  mortar: manned " + mortar.mannableComp?.MannedNow + ", forced " + mortar.ForcedTarget + ", gunner job " + gunner.CurJobDef + ", warmup " + mortar.burstWarmupTicksLeft + ", cooldown " + mortar.burstCooldownTicksLeft + ", verb " + mortar.AttackVerb?.state + ", overhead impacts " + By(gunner, Impacts, t0).Concat(By(mortar, Impacts, t0)).Count(s => s.overhead));
                }
            }
            List<Shot> hits = By(gunner, Impacts, t0).Concat(By(mortar, Impacts, t0)).Where(s => s.overhead).ToList();
            List<Shot> underSlab = hits.Where(s => s.ground.x >= 6 && s.ground.x <= 34 && s.ground.z >= 92 && s.ground.z <= 118).ToList();
            int broken = 0;
            for (int x = 6; x <= 34; x++)
            {
                for (int z = 92; z <= 118; z++)
                {
                    if (Map.terrainGrid.TerrainAt(On(x, z, 1)) != Def("PZ_UpperFloor"))
                    {
                        broken++;
                    }
                }
            }
            Out("  shells landed " + hits.Count + ": " + Hits(hits));
            Check("6 mortar: shells over the slab land on the slab top (floor 1), or through a hole a shell broke", underSlab.Count > 0 && underSlab.Count(s => s.frame == 1) > 0 && underSlab.All(s => s.frame == 1 || !ProjectZ.Api.HasFloorAt(s.ground, 1, Map)),
                "landed " + hits.Count + ", over the slab " + underSlab.Count + " (on floor 1 " + underSlab.Count(s => s.frame == 1) + ", through a hole to the ground " + underSlab.Count(s => s.frame == 0) + ")");
            Out("  REPORT 6 SlabDamage under CE: " + broken + " of 783 slab cells no longer a floor after " + underSlab.Count + " shells on the slab");
            mortar.ResetForcedTarget();
            Gone(gunner, mortar);

            // a mortar on floor 1 at a ground target: the spotter's sight (direct or indirect fire) is ProjectZ's 3D line
            Out("--- 6b mortar on floor 1, target on the ground");
            Wipe(40, 52, 50, 122);
            Slab(40, 58, 46, 64);
            Building_TurretGunCE mortar2 = (Building_TurretGunCE)Build("Turret_Mortar", On(43, 62, 1));
            Pawn gunner2 = Colonist(On(43, 60, 1), null);
            gunner2.drafter.Drafted = false;
            gunner2.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, mortar2));
            yield return 200;
            int spot0 = Aim.SpottingChecks;
            t0 = Find.TickManager.TicksGame;
            mortar2.OrderAttack(new LocalTargetInfo(On(45, 105, 0)));
            guard = 0;
            while (By(gunner2, Impacts, t0).Concat(By(mortar2, Impacts, t0)).Count(s => s.overhead) < 1 && guard++ < 20)
            {
                yield return 200;
                if (mortar2.CompAmmo != null && !mortar2.CompAmmo.CanBeFiredNow)
                {
                    mortar2.CompAmmo.ResetAmmoCount(mortar2.CompAmmo.SelectedAmmo);
                }
            }
            List<Shot> hits2 = By(gunner2, Impacts, t0).Concat(By(mortar2, Impacts, t0)).Where(s => s.overhead).ToList();
            Out("  shells landed " + hits2.Count + ": " + Hits(hits2));
            Check("6b mortar on floor 1: spotting on ProjectZ's 3D line, the shell lands on the ground near the target", Aim.SpottingChecks > spot0 && hits2.Count > 0 && hits2[0].frame == 0
                && (hits2[0].ground - new IntVec3(45, 0, 105)).LengthHorizontal < 15f, "spotting checks " + (Aim.SpottingChecks - spot0) + ", " + Hits(hits2));
            mortar2.ResetForcedTarget();
            Gone(gunner2, mortar2);
        }

        // ---- 7. grenades: thrown from the ground onto floor 1, and from floor 1 down to the ground.
        private static IEnumerator<int> Grenades()
        {
            Out("--- 7 grenades up and down");
            Wipe(52, 54, 84, 82);
            Slab(56, 66, 64, 70);
            Slab(71, 66, 79, 70);
            Pawn upper = Colonist(On(60, 60, 0), "Weapon_GrenadeFrag");
            IntVec3 upTarget = On(60, 67, 1);
            yield return 30;
            string can = CanHit(upper, upTarget);
            int t0 = Find.TickManager.TicksGame;
            Order(upper, upTarget);
            int guard = 0;
            while (By(upper, Impacts, t0).Count == 0 && guard++ < 10)
            {
                yield return 100;
            }
            List<Shot> up = By(upper, Impacts, t0);
            Out("  up: can hit " + can + ", " + Hits(up));
            Check("7 grenade up: lands on floor 1 near the target", up.Count > 0 && up[0].frame == 1 && (up[0].ground - L.Ground(upTarget)).LengthHorizontal <= 3f,
                "can hit " + can + ", " + (up.Count > 0 ? "frame " + up[0].frame + " at " + up[0].ground + " (target " + L.Ground(upTarget) + ")" : "no impact"));
            string fUp = FlightOf(upper, t0, out float apexUp, out int airUp);
            Out("  up flight: " + fUp);
            Check("7 grenade up: a low lob (apex within floor 1's storey, at most " + MaxLobAir + " ticks in the air)", up.Count > 0 && LowLob(apexUp, airUp, 1), fUp);
            Pawn lower = Colonist(On(75, 70, 1), "Weapon_GrenadeFrag");
            IntVec3 downTarget = On(75, 76, 0);
            yield return 30;
            can = CanHit(lower, downTarget);
            t0 = Find.TickManager.TicksGame;
            Order(lower, downTarget);
            guard = 0;
            while (By(lower, Impacts, t0).Count == 0 && guard++ < 10)
            {
                yield return 100;
            }
            List<Shot> down = By(lower, Impacts, t0);
            Out("  down: can hit " + can + ", " + Hits(down));
            Check("7 grenade down: lands on the ground near the target", down.Count > 0 && down[0].frame == 0 && (down[0].ground - downTarget).LengthHorizontal <= 3f,
                "can hit " + can + ", " + (down.Count > 0 ? "frame " + down[0].frame + " at " + down[0].ground : "no impact"));
            Out("  down flight (CE's own arc): " + FlightOf(lower, t0, out _, out _));
            Gone(upper, lower);

            // 7b, 7c: the other lob weapons (CE's ShotAngle: the great bow at a target on floor 1, CE's grenade launcher at a
            // cell on floor 1), each from the ground 10 cells before the target
            Out("--- 7b/7c lobs up: great bow, grenade launcher");
            Wipe(86, 54, 128, 84);
            Slab(94, 67, 106, 72);   // (the bow: the edge 1 cell before its target, as in 7; a pawn further in is out of sight)
            Slab(110, 65, 122, 72);   // (the launcher: the edge 3 cells before its target cell; a line to a cell on floor 1 rises past the floor level over an open cell, so a slab edge 5 or 6 cells before it hides the cell from the ground; CE's spread may clip the edge with a round of the burst)
            Pawn archer = Colonist(On(100, 58, 0), "Bow_Great");
            Pawn targetB = Target(On(100, 68, 1));
            Pawn grenadier = Colonist(On(116, 58, 0), "CE_GrenadeLauncher");
            IntVec3 cellC = On(116, 68, 1);
            yield return 30;
            string canB = CanHit(archer, targetB), canC = CanHit(grenadier, cellC);
            t0 = Find.TickManager.TicksGame;
            int lobs0 = Aim.LowLobs;
            Order(archer, targetB);
            Order(grenadier, cellC);
            guard = 0;
            while ((By(archer, Impacts, t0).Count == 0 || By(grenadier, Impacts, t0).Count == 0) && guard++ < 10)
            {
                yield return 100;
            }
            foreach ((string name, Pawn p, IntVec3 at, string c) in new[] { ("7b great bow", archer, L.Ground(targetB.Position), canB), ("7c grenade launcher", grenadier, L.Ground(cellC), canC) })
            {
                List<Shot> hits = By(p, Impacts, t0);
                string fl = FlightOf(p, t0, out float apex, out int air);
                Out("  " + name + ": can hit " + c + ", " + Hits(hits) + "; flight " + fl);
                Check(name + " up: lands on floor 1 near the target on a low lob (apex within floor 1's storey, at most " + MaxLobAir + " ticks in the air)",
                    c == "yes" && hits.Any(h => h.frame == 1 && (h.ground - at).LengthHorizontal <= 3f) && LowLob(apex, air, 1),
                    "can hit " + c + ", " + (hits.Count > 0 ? "frame " + hits[0].frame + " at " + hits[0].ground + " (target " + at + ")" : "no impact") + "; " + fl);
            }
            Out("  lowest lobs taken by CE's ShotAngle in 7b/7c: " + (Aim.LowLobs - lobs0));
            Gone(archer, targetB, grenadier);
        }

        // A lob up to floor kt is low: its highest point stays within storey kt (under floor kt + 1's floor plane) and it is
        // in the air at most MaxLobAir ticks (2.5 s; the user's aim: about 1-2 s, CE's full-speed high arc took about 12 s).
        private const int MaxLobAir = 150;

        private static bool LowLob(float apex, int air, int kt) => apex > 0f && apex <= (kt + 1) * ProjectZ.Api.StoreyHeight && air > 0 && air <= MaxLobAir;

        // The first projectile launched by a shooter since a tick: its highest point (cells over the ground) and ticks in the air.
        private static string FlightOf(Thing shooter, int since, out float apex, out int air)
        {
            apex = 0f;
            air = 0;
            Shot l = By(shooter, Launches, since).FirstOrDefault();
            if (l == null || !Flights.TryGetValue(l.id, out (float apex, int air) f))
            {
                return "no flight recorded";
            }
            apex = f.apex;
            air = f.air;
            return "launch angle " + l.angle.ToString("0.0") + " deg, apex " + apex.ToString("0.00") + " cells over the ground (storey " + ProjectZ.Api.StoreyHeight.ToString("0.00")
                + "), in the air " + air + " ticks (" + (air / 60f).ToString("0.00") + " s)";
        }

        // ---- 12. lasers and instant shots, homing: does Combat Extended itself have a case without other mods?
        private static IEnumerator<int> Instant()
        {
            Out("--- 12 lasers / instant shots, homing");
            List<ThingDef> instant = DefDatabase<ThingDef>.AllDefs.Where(d => d.projectile is ProjectilePropertiesCE pp && pp.isInstant && !d.IsBlueprint).ToList();
            List<ThingDef> guns = DefDatabase<ThingDef>.AllDefs.Where(d => d.IsRangedWeapon && d.Verbs.Any(v => v.defaultProjectile != null && instant.Contains(v.defaultProjectile))).ToList();
            List<ThingDef> homing = DefDatabase<ThingDef>.AllDefs.Where(d => d.projectile is ProjectilePropertiesCE pp && (pp.TrajectoryWorker is HomingBulletTrajectoryWorker || pp.TrajectoryWorker is SmartRocketTrajectoryWorker)).ToList();
            Out("  REPORT 12: instant projectile defs " + instant.Count + " (" + string.Join(",", instant.Take(8).Select(d => d.defName)) + "), weapons firing them " + guns.Count
                + " (" + string.Join(",", guns.Take(8).Select(d => d.defName)) + "), homing / smart rocket defs " + homing.Count + " (" + string.Join(",", homing.Take(8).Select(d => d.defName)) + ")");
            if (guns.Count == 0)
            {
                yield break;
            }
            IEnumerator<int> laser = Laser(guns.FirstOrDefault(d => d.defName == "Gun_BeamRepeater") ?? guns[0]);
            while (laser.MoveNext())
            {
                yield return laser.Current;
            }
        }

        // ---- 12. an instant laser (CE's RayCast) down from floor 1 and up to floor 1.
        private static IEnumerator<int> Laser(ThingDef gun)
        {
            Wipe(130, 3, 160, 34);
            Slab(134, 10, 138, 14);
            Slab(148, 18, 156, 22);
            Pawn target = Target(On(136, 26, 0));
            Pawn shooter = Colonist(On(136, 14, 1), gun.defName);
            Pawn target2 = Target(On(152, 18, 1));
            Pawn shooter2 = Colonist(On(152, 12, 0), gun.defName);
            yield return 30;
            int Hurt(Pawn p) => p.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
            string can = CanHit(shooter, target);
            int across0 = Frames.RayCastsAcross, hurt0 = Hurt(target);
            Order(shooter, target);
            yield return 400;
            int across1 = Frames.RayCastsAcross;
            Check("12 laser down (" + gun.defName + "): the beam crosses into the ground storey and hits", can == "yes" && across1 > across0 && Hurt(target) > hurt0,
                "can hit " + can + ", ray steps across planes " + (across1 - across0) + ", target injuries " + hurt0 + " -> " + Hurt(target));
            string can2 = CanHit(shooter2, target2);
            int hurt2 = Hurt(target2);
            Order(shooter2, target2);
            yield return 400;
            Check("12 laser up: the beam crosses into floor 1 and hits", can2 == "yes" && Frames.RayCastsAcross > across1 && Hurt(target2) > hurt2,
                "can hit " + can2 + ", ray steps across planes " + (Frames.RayCastsAcross - across1) + ", target injuries " + hurt2 + " -> " + Hurt(target2));
            Gone(shooter, target, shooter2, target2);
        }

        // ---- 13. save and reload mid-burst (bullets crossing): the same outcome as the run without the reload.
        private const string SaveName = "cepack_p12_mid";
        private static Game savedGame;

        private static IEnumerator<int> SaveReload(bool across)
        {
            Out("--- 13 save and reload mid-burst (" + (across ? "floor 1 to the ground" : "control: on the ground") + ")");
            int x = across ? 122 : 142;
            Wipe(x - 7, 5, x + 8, 34);
            Slab(x - 2, 10, x + 2, 14);
            Pawn target = Target(On(x, 27, 0));
            Pawn shooter = Colonist(On(x, across ? 14 : 15, across ? 1 : 0), "Gun_AssaultRifle");
            yield return 30;
            int t0 = Find.TickManager.TicksGame;
            Order(shooter, target);
            int guard = 0;
            // until a bullet of the burst is in flight (it crosses the plane on its way down)
            while (guard++ < 600 && !Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).OfType<ProjectileCE>().Any(p => p.launcher == shooter && !p.landed && p.FlightTicks >= 1 && L.FrameOf(p.Position) == (across ? 1 : 0)))
            {
                yield return 1;
            }
            List<ProjectileCE> flying = Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).OfType<ProjectileCE>().Where(p => !p.landed).ToList();
            Out("  saving at tick " + Find.TickManager.TicksGame + " with " + flying.Count + " CE projectiles in flight: "
                + string.Join("; ", flying.Select(p => p.def.defName + " id " + p.thingIDNumber + " frame " + L.FrameOf(p.Position) + " " + TExact(p).ToString("F2") + " ticks " + p.FlightTicks + "/" + p.ticksToImpact + " worker " + p.TrajectoryWorker?.GetType().Name)));
            HashSet<int> inFlight = new HashSet<int>(flying.Select(p => p.thingIDNumber));
            held.Clear();
            GameDataSaveLoader.SaveGame(SaveName);
            int tSave = Find.TickManager.TicksGame;
            held.Add((target, target.Position));
            Seed(4242);
            int i0 = Impacts.Count;
            for (int dbg = 0; dbg < 4; dbg++)
            {
                yield return 1;
                Out("  live +" + (dbg + 1) + ": layout " + (L != null) + ", edge exits " + Frames.EdgeExits + ", rebases " + Frames.RebasesDown + ": " + string.Join("; ", Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).OfType<ProjectileCE>().Select(p => p.thingIDNumber + " " + TExact(p).ToString("F2") + " pos " + p.Position + " landed " + p.landed + " ticks " + p.FlightTicks + "/" + p.ticksToImpact)));
            }
            yield return 240;
            List<Shot> a = Impacts.Skip(i0).ToList();
            savedGame = Current.Game;
            int targetId = target.thingIDNumber;
            held.Clear();
            GameDataSaveLoader.LoadGame(SaveName);
            while (Current.Game == savedGame || Find.CurrentMap == null || LongEventHandler.AnyEventNowOrWaiting)
            {
                yield return 0;
            }
            Pawn target2 = Map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == targetId);
            if (target2 != null)
            {
                held.Add((target2, target2.Position));
            }
            Out("  loaded at tick " + Find.TickManager.TicksGame + " (saved at " + tSave + "), projectiles in flight "
                + Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).OfType<ProjectileCE>().Count(p => !p.landed) + ": " + string.Join("; ", Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).OfType<ProjectileCE>().Select(p => p.def.defName + " id " + p.thingIDNumber + " frame " + L.FrameOf(p.Position) + " " + TExact(p).ToString("F2") + " landed " + p.landed + " ticks " + p.FlightTicks + "/" + p.ticksToImpact + " worker " + p.TrajectoryWorker?.GetType().Name + " spawned " + p.Spawned)));
            Seed(4242);
            int i1 = Impacts.Count;
            for (int dbg = 0; dbg < 4; dbg++)
            {
                yield return 1;
                Out("  after load +" + (dbg + 1) + ": layout " + (L != null) + ", edge exits " + Frames.EdgeExits + ", rebases " + Frames.RebasesDown + ": " + string.Join("; ", Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).OfType<ProjectileCE>().Select(p => p.thingIDNumber + " " + TExact(p).ToString("F2") + " pos " + p.Position + " landed " + p.landed + " ticks " + p.FlightTicks + "/" + p.ticksToImpact)));
            }
            yield return 240;
            List<Shot> b = Impacts.Skip(i1).ToList();
            string Key(Shot s) => s.id + ":f" + s.frame + ":" + s.ground + ":" + s.hit;
            List<string> fa = a.Where(s => inFlight.Contains(s.id)).Select(Key).ToList();
            List<string> fb = b.Where(s => inFlight.Contains(s.id)).Select(Key).ToList();
            Out("  in flight at the save, live run:   " + string.Join(" | ", fa));
            Out("  in flight at the save, after load: " + string.Join(" | ", fb));
            Out("  later shots: live " + a.Count(s => !inFlight.Contains(s.id)) + " impacts (" + string.Join(" | ", a.Where(s => !inFlight.Contains(s.id)).Take(8).Select(s => "f" + s.frame + ":" + s.ground + ":" + s.hit))
                + "), after load " + b.Count(s => !inFlight.Contains(s.id)) + " (" + string.Join(" | ", b.Where(s => !inFlight.Contains(s.id)).Take(8).Select(s => "f" + s.frame + ":" + s.ground + ":" + s.hit)) + ")");
            Check("13 save/reload " + (across ? "across floors" : "(control, one floor)") + ": the bullets in flight at the save end the same", fa.Count > 0 && fa.SequenceEqual(fb), fa.Count + " vs " + fb.Count + " impacts");
            Check("13 save/reload " + (across ? "across floors" : "(control)") + ": frame invariant through the reload", InvariantBad == 0, InvariantBad + " bad" + (invariantFirst != null ? ", first: " + invariantFirst : ""));
            try
            {
                string path = GenFilePaths.FilePathForSavedGame(SaveName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Out("  deleted " + path);
                }
            }
            catch (Exception e)
            {
                Out("  could not delete the save: " + e.Message);
            }
            held.Clear();
            foreach (Pawn p in Map.mapPawns.AllPawnsSpawned.ToList())
            {
                p.Destroy(DestroyMode.Vanish);
            }
        }

        // ---- 14. a same-floor CE fight (ground only): its outcome hash, to compare with the pack's cross-floor part off
        // (-cepacknoz) and on a map without floors (-cepackfloors=1).
        private static IEnumerator<int> Fight()
        {
            Out("--- 14 same-floor fight");
            Wipe(30, 112, 50, 145);
            Thing bag = Build("Sandbags", On(40, 128, 0));
            Pawn target = Target(On(40, 135, 0));
            Pawn shooter = Colonist(On(40, 118, 0), "Gun_AssaultRifle");
            Pawn shooter2 = Colonist(On(36, 118, 0), "Gun_AssaultRifle");
            yield return 30;
            int t0 = Find.TickManager.TicksGame;
            int changed0 = PackZ.Changed, moved0 = FrameMoves();
            Order(shooter, target);
            Order(shooter2, target);
            yield return 900;
            List<Shot> hits = Impacts.Where(s => s.tick >= t0).ToList();
            string line = string.Join("|", hits.Select(s => (s.tick - t0) + ":" + s.def + ":" + s.frame + ":" + s.ground + ":" + s.height.ToString("0.000") + ":" + (s.hit == "-" ? "-" : s.hit.Substring(0, s.hit.IndexOf('#')))));
            ulong h = 1469598103934665603UL;
            foreach (char ch in line)
            {
                h = (h ^ ch) * 1099511628211UL;
            }
            Out("  impacts " + hits.Count + ": " + Hits(hits));
            Out("HASH fight=" + h.ToString("x16") + " impacts=" + hits.Count + " launches=" + Launches.Count(s => s.tick >= t0));
            Check("14 same-floor fight ran", hits.Count > 0, hits.Count + " impacts");
            int changed = PackZ.Changed - changed0, moved = FrameMoves() - moved0;
            Check("14 same-floor fight: the pack changed none of CE's values (aim, sight, cover, heights, distances)", changed == 0, changed + " changed values, " + moved + " projectile frame moves (a shot arcing over a storey plane in open air: a pure translation)");
            Gone(shooter, shooter2, target, bag);
        }
    }
}
