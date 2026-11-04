using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRimCEPackZ
{
    // Test harness only (mode "bench", -cepacktest=bench): CE's loading bench model (Defs/CE_Models_Bench.xml) in the real game
    // flow. Four facings and three stuffs side by side; a colonist building it from a real blueprint and working a real bill at
    // it (real jobs, real ticks, CE's ammo system on); things on its top; in a roofed room at night; the mech ammo beacon next to
    // the vanilla one. Renders go to the run's folder for the review sheet.
    public static partial class TestZ
    {
        private static IEnumerable<IEnumerator<int>> BenchChecks()
        {
            yield return BenchBindings();
            yield return BenchLooks();
            yield return BenchBuildAndBill();
            yield return BenchRoofNight();
        }

        private static void Hour(int hour)
        {
            TickManager tm = Find.TickManager;
            int baseTicks = tm.TicksGame - tm.TicksGame % GenDate.TicksPerHour + GenDate.TicksPerDay;
            for (int k = 0; k < 24; k++)
            {
                tm.DebugSetTicksGame(baseTicks + k * GenDate.TicksPerHour + GenDate.TicksPerHour / 2);
                if (GenLocalDate.HourOfDay(Map) == hour)
                {
                    break;
                }
            }
        }

        private static Thing BuildOf(string def, string stuff, IntVec3 c, Rot4 rot)
        {
            ThingDef d = DefDatabase<ThingDef>.GetNamed(def);
            Thing t = ThingMaker.MakeThing(d, stuff == null ? null : DefDatabase<ThingDef>.GetNamed(stuff));
            t.SetFaction(Faction.OfPlayer);
            return GenSpawn.Spawn(t, c, Map, rot);
        }

        private static Thing Stack(string def, int n, IntVec3 c)
        {
            Thing t = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(def));
            t.stackCount = n;
            t = GenSpawn.Spawn(t, c, Map);
            t.SetForbidden(false, false);
            return t;
        }

        private static IEnumerator<int> BenchBindings()
        {
            Out("--- bench 1 bindings");
            foreach (string d in new[] { "AmmoBench", "GunsmithingBench", "CombatExtended_MechAmmoBeacon" })
            {
                Check("bench model " + d, IsoRim.Api.HasModel(DefDatabase<ThingDef>.GetNamed(d)), "bound");
            }
            yield break;
        }

        private static IEnumerator<int> BenchLooks()
        {
            Out("--- bench 2 facings and stuffs");
            Wipe(88, 88, 128, 122);
            string[] stuffs = { "Steel", "WoodLog", "Plasteel" };
            int[] xs = { 94, 100, 106, 112 };
            Rot4[] rots = { Rot4.North, Rot4.East, Rot4.South, Rot4.West };
            for (int s = 0; s < 3; s++)
            {
                for (int r = 0; r < 4; r++)
                {
                    BuildOf("AmmoBench", stuffs[s], new IntVec3(xs[r], 0, 96 + 7 * s), rots[r]);
                }
            }
            BuildOf("GunsmithingBench", "Steel", new IntVec3(120, 0, 96), Rot4.North);
            BuildOf("TableMachining", null, new IntVec3(120, 0, 103), Rot4.North);
            BuildOf("HandTailoringBench", "WoodLog", new IntVec3(120, 0, 110), Rot4.North);
            Thing beacon = BuildOf("CombatExtended_MechAmmoBeacon", null, new IntVec3(118, 0, 92), Rot4.North);
            Thing vanilla = BuildOf("MechDropBeacon", null, new IntVec3(122, 0, 92), Rot4.North);
            yield return 5;
            Hour(11);
            hourSet = true;
            Map.weatherManager.curWeather = WeatherDefOf.Clear;
            Map.weatherManager.lastWeather = WeatherDefOf.Clear;
            Map.weatherManager.curWeatherAge = 100000;
            yield return 5;
            foreach (int t in new[] { 0, 4 })
            {
                foreach (int w in Snap("b2_all_t" + t, new IntVec3(106, 0, 103), 30f, t))
                {
                    yield return w;
                }
                for (int s = 0; s < 3; s++)
                {
                    foreach (int w in Snap("b2_" + stuffs[s] + "_t" + t, new IntVec3(103, 0, 96 + 7 * s), 11f, t))
                    {
                        yield return w;
                    }
                }
            }
            for (int r = 0; r < 4; r++)
            {
                foreach (int w in Snap("b2_close_" + rots[r].ToStringHuman(), new IntVec3(xs[r], 0, 103), 4.5f, 0))
                {
                    yield return w;
                }
            }
            foreach (int w in Snap("b2_beacons", new IntVec3(120, 0, 92), 7f, 0))
            {
                yield return w;
            }
            foreach (int w in Snap("b2_neighbours", new IntVec3(120, 0, 103), 19f, 0))
            {
                yield return w;
            }
            IsoRim.Api.Dev.View3DActive = false;
            yield return 5;
            foreach (int w in Snap("b2_off", new IntVec3(103, 0, 103), 16f, 0))
            {
                yield return w;
            }
            IsoRim.Api.Dev.View3DActive = true;
            yield return 5;
            Check("bench beacons stand", beacon.Spawned && vanilla.Spawned, "ok");
        }

        private static Pawn Worker(IntVec3 c)
        {
            Pawn p = null;
            for (int i = 0; i < 40; i++)
            {
                p = Colonist(c, null);
                if (!p.WorkTagIsDisabled(WorkTags.Constructing) && !p.WorkTagIsDisabled(WorkTags.Crafting) && !p.WorkTagIsDisabled(WorkTags.ManualSkilled))
                {
                    break;
                }
                p.Destroy(DestroyMode.Vanish);
            }
            p.drafter.Drafted = false;
            p.skills.GetSkill(SkillDefOf.Construction).Level = 20;
            p.skills.GetSkill(SkillDefOf.Crafting).Level = 20;
            p.workSettings.EnableAndInitialize();
            foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!p.WorkTypeIsDisabled(w))
                {
                    p.workSettings.SetPriority(w, w.defName == "Construction" || w.defName == "Smithing" || w.defName == "Hauling" ? 1 : 0);
                }
            }
            return p;
        }

        private static void Fed(Pawn p)
        {
            if (p.needs?.food != null)
            {
                p.needs.food.CurLevelPercentage = 1f;
            }
            if (p.needs?.rest != null)
            {
                p.needs.rest.CurLevelPercentage = 1f;
            }
            if (p.needs?.joy != null)
            {
                p.needs.joy.CurLevelPercentage = 1f;
            }
        }

        private static IEnumerator<int> BenchBuildAndBill()
        {
            Out("--- bench 3 build and bill");
            Wipe(130, 130, 156, 152);
            IntVec3 at = new IntVec3(142, 0, 142);
            ThingDef bench = DefDatabase<ThingDef>.GetNamed("AmmoBench");
            Pawn p = Worker(new IntVec3(142, 0, 136));
            Out("worker " + p.LabelShort + ", stuff Steel");
            for (int i = 0; i < 3; i++)
            {
                Stack("Steel", 75, new IntVec3(136 + i, 0, 138));
            }
            Stack("ComponentIndustrial", 5, new IntVec3(139, 0, 138));
            GenConstruct.PlaceBlueprintForBuild(bench, at, Map, Rot4.North, Faction.OfPlayer, DefDatabase<ThingDef>.GetNamed("Steel"));
            yield return 2;
            foreach (int w in Snap("b3_blueprint", at, 9f, 0))
            {
                yield return w;
            }
            Building built = null;
            bool frameShot = false;
            string jobs = "";
            for (int i = 0; i < 1500 && built == null; i++)
            {
                Fed(p);
                yield return 20;
                string j = p.CurJobDef?.defName ?? "-";
                if (!jobs.EndsWith(j))
                {
                    jobs += " " + j;
                }
                Frame f = at.GetThingList(Map).OfType<Frame>().FirstOrDefault();
                if (f != null && !frameShot && f.workDone > f.WorkToBuild * 0.4f)
                {
                    frameShot = true;
                    p.Drawer.tweener.ResetTweenedPosToRoot();
                    foreach (int w in Snap("b3_frame", at, 9f, 0))
                    {
                        yield return w;
                    }
                }
                built = at.GetThingList(Map).OfType<Building>().FirstOrDefault(b => b.def == bench);
            }
            Out("jobs:" + jobs);
            Check("bench built by the colonist", built != null && built.Stuff?.defName == "Steel", built != null ? built + " at tick " + Find.TickManager.TicksGame : "not built");
            if (built == null)
            {
                yield break;
            }
            foreach (int w in Snap("b3_built", at, 9f, 0))
            {
                yield return w;
            }

            // a real bill: CE's own cartridge recipe, its research finished, the steel lying by the bench
            Building_WorkTable table = (Building_WorkTable)built;
            RecipeDef rec = DefDatabase<RecipeDef>.GetNamed("MakeAmmo_556x45mmNATO_FMJ");
            Check("bench offers the recipe", bench.AllRecipes.Contains(rec), rec.defName + ", " + bench.AllRecipes.Count + " recipes");
            foreach (ResearchProjectDef r in new[] { rec.researchPrerequisite }.Concat(rec.researchPrerequisites ?? new List<ResearchProjectDef>()).Where(x => x != null))
            {
                Find.ResearchManager.FinishProject(r);
            }
            foreach (IngredientCount ing in rec.ingredients)
            {
                Out("ingredient " + ing.Summary);
                ThingDef d = ing.filter.AllowedThingDefs.First();
                if (d != DefDatabase<ThingDef>.GetNamed("Steel"))
                {
                    Stack(d.defName, Mathf.CeilToInt(ing.GetBaseCount()) * 2, new IntVec3(144, 0, 138));
                }
            }
            Bill bill = rec.MakeNewBill();
            table.billStack.AddBill(bill);
            IntVec3 cell = table.InteractionCell;
            bool working = false;
            int startTick = 0;
            jobs = "";
            ThingDef product = rec.products[0].thingDef;
            bool madeIt = false;
            for (int i = 0; i < 3000 && !madeIt; i++)
            {
                Fed(p);
                yield return 10;
                string j = p.CurJobDef?.defName ?? "-";
                if (!jobs.EndsWith(j))
                {
                    jobs += " " + j;
                }
                if (!working && p.CurJobDef == JobDefOf.DoBill && p.Position == cell && p.CurJob.GetTarget(TargetIndex.A).Thing == table
                    && !p.pather.Moving && table.OccupiedRect().Cells.Any(c => c.GetThingList(Map).Any(t => t.def.category == ThingCategory.Item)))
                {
                    working = true;
                    startTick = Find.TickManager.TicksGame;
                    yield return 120;
                    p.Drawer.tweener.ResetTweenedPosToRoot();   // (ticks run paused here: the drawn position does not follow by itself)
                    Check("worker on the interaction cell", p.Position == cell && p.CurJobDef == JobDefOf.DoBill, "at " + p.Position + ", cell " + cell + ", facing " + p.Rotation + ", job " + p.CurJobDef?.defName);
                    Out("on the bench: " + string.Join(", ", table.OccupiedRect().Cells.SelectMany(c => c.GetThingList(Map)).Where(t => t != table).Select(t => t + " at " + t.Position)));
                    foreach (int turn in new[] { 0, 4 })
                    {
                        foreach (int w in Snap("b3_work_t" + turn, at, 6f, turn))
                        {
                            yield return w;
                        }
                    }
                    foreach (int w in Snap("b3_work_play", at, 12f, 0))
                    {
                        yield return w;
                    }
                }
                madeIt = Map.listerThings.ThingsOfDef(product).Any();
            }
            Out("jobs:" + jobs);
            Check("worked the bill at the bench", working, working ? "from tick " + startTick : "never seen working");
            Check("cartridges made", madeIt, madeIt ? product.defName + " x" + Map.listerThings.ThingsOfDef(product).Sum(t => t.stackCount) + " at " + Map.listerThings.ThingsOfDef(product)[0].Position : "none");
            p.Drawer.tweener.ResetTweenedPosToRoot();
            foreach (int w in Snap("b3_done", at, 9f, 0))
            {
                yield return w;
            }

            // things on the top, one on each cell (the press stands on the east one)
            Stack("Steel", 40, at + IntVec3.West);
            Stack("ComponentIndustrial", 3, at);
            Stack("WoodLog", 25, at + IntVec3.East);
            yield return 5;
            p.Drawer.tweener.ResetTweenedPosToRoot();
            foreach (int turn in new[] { 0, 4 })
            {
                foreach (int w in Snap("b3_items_t" + turn, at, 6f, turn))
                {
                    yield return w;
                }
            }
            p.Destroy(DestroyMode.Vanish);
        }

        private static IEnumerator<int> BenchRoofNight()
        {
            Out("--- bench 4 roofed room at night");
            Wipe(158, 158, 182, 182);
            IsoRim.IsoRimSettings st = IsoRim.IsoRimMod.Settings;
            bool roofs = st.showRoofs;
            st.showRoofs = false;      // (first the room from inside; its roof after)
            Map.mapDrawer.RegenerateEverythingNow();
            for (int x = 164; x <= 176; x++)
            {
                for (int z = 164; z <= 174; z++)
                {
                    bool edge = x == 164 || x == 176 || z == 164 || z == 174;
                    if (edge)
                    {
                        Build("Wall", new IntVec3(x, 0, z));
                    }
                    else
                    {
                        Map.terrainGrid.SetTerrain(new IntVec3(x, 0, z), DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));
                    }
                    Map.roofGrid.SetRoof(new IntVec3(x, 0, z), RoofDefOf.RoofConstructed);
                }
            }
            Thing a = BuildOf("AmmoBench", "Steel", new IntVec3(168, 0, 172), Rot4.North);
            Thing b = BuildOf("AmmoBench", "WoodLog", new IntVec3(174, 0, 168), Rot4.West);
            Thing torch = Build("TorchLamp", new IntVec3(171, 0, 169));
            torch.TryGetComp<CompRefuelable>()?.Refuel(20f);
            Pawn p = Colonist(new IntVec3(168, 0, 171), null);
            p.drafter.Drafted = false;
            p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 6000), JobCondition.InterruptForced);
            Hour(22);
            yield return 30;
            Fed(p);
            p.Drawer.tweener.ResetTweenedPosToRoot();
            Check("bench room is roofed", new IntVec3(168, 0, 171).Roofed(Map) && a.Spawned && b.Spawned, "hour " + GenLocalDate.HourOfDay(Map));
            IntVec3 mid = new IntVec3(170, 0, 169);
            foreach (int t in new[] { 0, 4 })
            {
                foreach (int w in Snap("b4_night_t" + t, mid, 13f, t))
                {
                    yield return w;
                }
            }
            foreach (int w in Snap("b4_night_close", new IntVec3(168, 0, 172), 6f, 0))
            {
                yield return w;
            }
            st.showRoofs = true;
            Map.mapDrawer.RegenerateEverythingNow();
            yield return 5;
            foreach (int w in Snap("b4_night_roofs", mid, 13f, 0))
            {
                yield return w;
            }
            st.showRoofs = false;
            Map.mapDrawer.RegenerateEverythingNow();
            Hour(11);
            yield return 30;
            foreach (int w in Snap("b4_day", mid, 13f, 0))
            {
                yield return w;
            }
            st.showRoofs = roofs;
        }
    }
}
