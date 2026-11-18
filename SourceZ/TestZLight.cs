using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRimCEPackZ
{
    // Harness, mode fixb l1 and s1 (real orders and real ticks, see TestZ):
    //   l1  a shot between floors: the light and the weather in CE's report are CE's own for the real target cell
    //       (Aim.LightCell_Transpiler), and the report is computed once per shot. -cepacknolightcell must fail it; mode
    //       fault -cepackfault=lightcell runs it as M8b with the swap's group failed (CE's values for the shooter-frame cell).
    //   s1  mortar spotting at a slab edge (Aim.Spotting, the pack's one rule for seeing a cell on another floor: a cell
    //       below is aimed at its ground, a thing at its middle): CE's indirect-fire penalty is on for a target hidden by
    //       the spotter's slab and off for one in the open, read from CE's own report of the real mortar shot.
    public static partial class TestZ
    {
        // ---- the reports of CE's shots (Verb_LaunchProjectileCE.TryCastShot), per verb
        private sealed class CastReports
        {
            public int calls;                  // the base ShiftVecReportFor(target, cell) during the cast (the pack's own included)
            public ShiftVecReport first;       // the first one (TryCastShot's)
            public IntVec3 cell;               // its targetCell
            public ShiftVecReport real, frame; // l1: CE's report for the real target cell, and for targetCell with the swap off
            public ShiftVecReport mortar;      // s1: the mortar's own report (Verb_ShootMortarCE.ShiftVecReportFor)
            public string scene;               // l1: weather and roofs at the cast
        }

        private static readonly Dictionary<Verb, List<CastReports>> casts = new Dictionary<Verb, List<CastReports>>();
        private static readonly Stack<CastReports> castNow = new Stack<CastReports>();
        private static bool watchCasts, inDirect, directReports;

        private static Harmony PatchCasts(string id, bool direct)
        {
            Harmony h = new Harmony("peren.isorimwaylith.cepack.testz." + id);
            h.Patch(AccessTools.Method(typeof(Verb_LaunchProjectileCE), nameof(Verb_LaunchProjectileCE.TryCastShot)), prefix: new HarmonyMethod(typeof(TestZ), nameof(Cast_Prefix)),
                finalizer: new HarmonyMethod(typeof(TestZ), nameof(Cast_Finalizer)));
            h.Patch(Aim.ByName(typeof(Verb_LaunchProjectileCE), "ShiftVecReportFor", 2), postfix: new HarmonyMethod(typeof(TestZ), nameof(CastReport_Postfix)) { priority = Priority.Last });
            h.Patch(Aim.ByName(typeof(Verb_ShootMortarCE), "ShiftVecReportFor", 2), postfix: new HarmonyMethod(typeof(TestZ), nameof(CastMortarReport_Postfix)) { priority = Priority.Last });
            casts.Clear();
            castNow.Clear();
            directReports = direct;
            watchCasts = true;
            return h;
        }

        private static void UnpatchCasts(Harmony h)
        {
            watchCasts = false;
            h.UnpatchAll(h.Id);
        }

        public static void Cast_Prefix(Verb_LaunchProjectileCE __instance)
        {
            if (!watchCasts)
            {
                return;
            }
            CastReports r = new CastReports();
            if (!casts.TryGetValue(__instance, out List<CastReports> list))
            {
                casts[__instance] = list = new List<CastReports>();
            }
            list.Add(r);
            castNow.Push(r);
        }

        public static Exception Cast_Finalizer(Exception __exception)
        {
            if (watchCasts && castNow.Count > 0)
            {
                castNow.Pop();
            }
            return __exception;
        }

        public static void CastReport_Postfix(Verb_LaunchProjectileCE __instance, LocalTargetInfo target, IntVec3 targetCell, ShiftVecReport __result)
        {
            if (!watchCasts || inDirect || castNow.Count == 0 || __result == null)
            {
                return;
            }
            CastReports r = castNow.Peek();
            r.calls++;
            if (r.first != null)
            {
                return;
            }
            r.first = __result;
            r.cell = targetCell;
            Map m = __instance.caster.Map;
            r.scene = "weather " + m.weatherManager.curWeather.defName + " x" + m.weatherManager.CurWeatherAccuracyMultiplier.ToString("0.00") + ", roofed: shooter " + m.roofGrid.Roofed(__instance.caster.Position)
                + " real cell " + m.roofGrid.Roofed(target.Cell) + " targetCell " + m.roofGrid.Roofed(targetCell) + ", glow real " + m.glowGrid.GroundGlowAt(target.Cell).ToString("0.00") + " targetCell " + m.glowGrid.GroundGlowAt(targetCell).ToString("0.00");
            if (!directReports)
            {
                return;
            }
            // (test only) CE's own report for the real target cell, and for CE's targetCell with the swap off
            inDirect = true;
            bool off = Aim.NoLightCell;
            try
            {
                r.real = __instance.ShiftVecReportFor(target, target.Cell);
                Aim.NoLightCell = true;
                r.frame = __instance.ShiftVecReportFor(target, targetCell);
            }
            finally
            {
                Aim.NoLightCell = off;
                inDirect = false;
            }
        }

        public static void CastMortarReport_Postfix(ShiftVecReport __result)
        {
            if (watchCasts && !inDirect && castNow.Count > 0 && castNow.Peek().mortar == null)
            {
                castNow.Peek().mortar = __result;
            }
        }

        private static string Shifts(ShiftVecReport r) => r == null ? "-" : "light " + r.lightingShift.ToString("R") + " weather " + r.weatherShift.ToString("R");

        // ------------------------------------------------------------------ l1
        private static IEnumerator<int> LightCell() => LightCellShot("l1", true);

        // A shooter on floor 1 under a roof, in fog; the target on the ground beyond the slab, under a plain roof (no floor
        // over it) with a torch by it. CE's real target cell is roofed (no weather shift) and lit; its shooter-frame cell
        // (the target's ground cell on floor 1) is open, so the two cells answer differently (the log shows both).
        // corrected: the report's light and weather are CE's for the real cell; else (the group failed) CE's for the
        // shooter-frame cell. Either way: one report per shot.
        private static IEnumerator<int> LightCellShot(string id, bool corrected)
        {
            Out("--- " + id + " light and weather of a shot between floors (" + (corrected ? "CE's for the real target cell" : "correction off: CE's for the shooter-frame cell") + ")");
            Wipe(128, 172, 146, 200);
            Slab(135, 176, 139, 181);
            IntVec3 shooterCell = On(137, 180, 1), targetCell = On(137, 193, 0), frameCell = On(137, 193, 1);
            RoofDef roof = RoofDefOf.RoofConstructed;
            Map.roofGrid.SetRoof(shooterCell, roof);
            Map.roofGrid.SetRoof(targetCell, roof);
            WeatherManager wm = Map.weatherManager;
            (WeatherDef cur, WeatherDef last, int age) weather = (wm.curWeather, wm.lastWeather, wm.curWeatherAge);
            WeatherDef fog = DefDatabase<WeatherDef>.GetNamed("Fog");
            wm.curWeather = wm.lastWeather = fog;
            wm.curWeatherAge = 4000;
            Thing torch = Build("TorchLamp", On(139, 193, 0));
            torch.TryGetComp<CompRefuelable>()?.Refuel(torch.TryGetComp<CompRefuelable>().Props.fuelCapacity);
            Pawn target = Target(targetCell);
            Pawn shooter = Colonist(shooterCell, "Gun_AssaultRifle");
            yield return 30;
            string can = CanHit(shooter, target);
            Harmony h = PatchCasts(id, true);
            int t0 = Find.TickManager.TicksGame, swaps0 = Aim.LightCellSwaps;
            Order(shooter, target);
            yield return 300;
            UnpatchCasts(h);
            wm.curWeather = weather.cur;
            wm.lastWeather = weather.last;
            wm.curWeatherAge = weather.age;
            List<Shot> launched = By(shooter, Launches, t0);
            List<CastReports> mine = casts.TryGetValue(VerbOf(shooter), out List<CastReports> l) ? l.Where(c => c.first != null).ToList() : new List<CastReports>();
            bool stayed = shooter.Position == shooterCell && target.Position == targetCell;
            Out("  can hit " + can + ", launched " + launched.Count + ", casts " + mine.Count + ", in place " + stayed + ", swaps " + (Aim.LightCellSwaps - swaps0) + ", glow real "
                + Map.glowGrid.GroundGlowAt(targetCell).ToString("0.00") + " frame " + Map.glowGrid.GroundGlowAt(frameCell).ToString("0.00") + ", weather x" + fog.accuracyMultiplier);
            foreach (CastReports c in mine.Take(4))
            {
                Out("  cast: " + c.scene + "; calls " + c.calls + ", targetCell " + c.cell + " (frame cell " + frameCell + "), report " + Shifts(c.first) + "; CE real cell " + Shifts(c.real) + "; CE targetCell " + Shifts(c.frame));
            }
            bool fixture = mine.Count > 0 && mine.All(c => c.cell == frameCell && c.real != null && c.frame != null
                && (c.real.lightingShift != c.frame.lightingShift || c.real.weatherShift != c.frame.weatherShift));
            bool follows = mine.Count > 0 && mine.All(c =>
            {
                ShiftVecReport want = corrected ? c.real : c.frame;
                return want != null && c.first.lightingShift == want.lightingShift && c.first.weatherShift == want.weatherShift;
            });
            string got = "can hit " + can + ", launched " + launched.Count + ", casts " + mine.Count + ", in place " + stayed + ", shooter-frame cell given and answering differently " + fixture;
            Check(id + (corrected ? " a shot between floors: light and weather are CE's own for the real target cell" : " the correction's group failed: the shot between floors fires, light and weather CE's for the shooter-frame cell"),
                launched.Count > 0 && stayed && fixture && follows, got + ", follows " + follows);
            Check(id + " CE's report is computed once per shot", mine.Count > 0 && mine.All(c => c.calls == 1), "calls per cast " + string.Join(",", mine.Select(c => c.calls)));
            Map.roofGrid.SetRoof(shooterCell, null);
            Map.roofGrid.SetRoof(targetCell, null);
            Gone(shooter, target, torch);
        }

        // ------------------------------------------------------------------ s1
        // Mortars on floor 1 fire at the ground, each its own strip (slab, mortar, gunner, target), all at once:
        //   a  a target 1 cell inside the far edge of the mortar's slab: hidden (the line meets the slab)  -> penalty
        //   b  a target in the open, the slab ending just past the mortar                                    -> no penalty
        //   c  a target in the open behind the slab's edge, the edge placed between where the line to the cell's
        //      ground and the line to a thing's middle cross the floor-1 plane: the cell is hidden by the edge (penalty),
        //      the thing standing on it is not (no penalty). The edge row comes from the gunner's eyes and the target's
        //      height (CE's CollisionVertical) and ProjectZ's storey, logged.
        // Each strip once with a cell target and once with a thing (a muffalo). The penalty is CE's indirectFireShift in
        // CE's own report of the real shot (Verb_ShootMortarCE.ShiftVecReportFor, called by CE's TryCastShot).
        private const int SpotDist = 40;

        private static IEnumerator<int> SlabEdgeSpotting()
        {
            Out("--- s1 mortar spotting at a slab edge (a cell below aimed at its ground, a thing at its middle)");
            const int z0 = 108, zt = z0 + SpotDist;
            Wipe(147, z0 - 8, 175, zt + 6);
            var strips = new List<(string id, int x, bool thing, string fix)>
            {
                ("s1a cell under the slab's far edge", 150, false, "a"), ("s1a thing under the slab's far edge", 154, true, "a"),
                ("s1b cell in the open", 158, false, "b"), ("s1b thing in the open", 162, true, "b"),
                ("s1c cell behind the slab edge", 166, false, "c"), ("s1c thing behind the slab edge", 170, true, "c"),
            };
            var run = new List<(string id, bool thing, string fix, Building_TurretGunCE mortar, Pawn gunner, Pawn target, IntVec3 cell)>();
            foreach ((string id, int x, bool thing, string fix) in strips)
            {
                Slab(x - 1, z0 - 4, x + 1, fix == "a" ? zt + 1 : z0 + 1);
                Building_TurretGunCE mortar = (Building_TurretGunCE)Build("Turret_Mortar", On(x, z0, 1));
                Pawn gunner = Colonist(On(x, z0 - 2, 1), null);
                gunner.drafter.Drafted = false;
                mortar.CompAmmo?.ResetAmmoCount(mortar.CompAmmo.SelectedAmmo);
                gunner.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, mortar));
                IntVec3 cell = On(x, zt, 0);
                run.Add((id, thing, fix, mortar, gunner, thing ? Target(cell) : null, cell));
            }
            yield return 200;
            // strip c: the slab edge between the two plane crossings (cells from the mortar's centre along the line)
            var c0 = run.First(r => r.fix == "c");
            float k = Layout.CellsPerUnit, storey = ProjectZ.Api.StoreyHeight;
            float eye = new CollisionVertical(c0.gunner).shotHeight * k;
            float mid = run.Where(r => r.thing).Select(r => { CollisionVertical cv = new CollisionVertical(r.target); return (cv.Min + cv.Max) / 2f * k; }).Max();
            float dist = SpotDist, atGround = dist * eye / (storey + eye), atMiddle = dist * eye / (storey + eye - mid);
            int edge = Mathf.FloorToInt(0.5f + (atGround + atMiddle) / 2f);   // the last slab row, rows from the mortar's
            bool geometry = atMiddle - atGround >= 2f && edge >= 2 && edge < SpotDist - 2;
            Out("  strip c geometry: eye " + eye.ToString("0.000") + " cells over floor 1, thing middle " + mid.ToString("0.000") + ", storey " + storey.ToString("0.000")
                + ": the floor-1 plane is crossed " + atGround.ToString("0.00") + " cells out (to the ground) and " + atMiddle.ToString("0.00") + " (to a thing's middle); slab to row +" + edge + ", usable " + geometry);
            foreach (var r in run.Where(r => r.fix == "c"))
            {
                Slab(r.mortar.Position.x - 1, z0 + 2, r.mortar.Position.x + 1, z0 + edge);
            }
            int spot0 = Aim.SpottingChecks;
            Harmony h = PatchCasts("s1", false);
            foreach (var r in run)
            {
                r.mortar.OrderAttack(r.thing ? new LocalTargetInfo(r.target) : new LocalTargetInfo(r.cell));
            }
            int guard = 0;
            while (run.Any(r => !Reported(r.mortar)) && guard++ < 20)
            {
                yield return 200;
                foreach (var r in run)
                {
                    if (r.mortar.CompAmmo != null && !r.mortar.CompAmmo.CanBeFiredNow)
                    {
                        r.mortar.CompAmmo.ResetAmmoCount(r.mortar.CompAmmo.SelectedAmmo);   // (harness: the shells are loaded; ammo is not the subject)
                    }
                }
            }
            UnpatchCasts(h);
            Out("  spotting checks " + (Aim.SpottingChecks - spot0));
            foreach (var r in run)
            {
                ShiftVecReport rep = casts.TryGetValue(r.mortar.AttackVerb, out List<CastReports> l) ? l.FirstOrDefault(c => c.mortar != null)?.mortar : null;
                bool hidden = r.fix == "a" || (r.fix == "c" && !r.thing);
                string got = rep == null ? "no shot (manned " + r.mortar.mannableComp?.MannedNow + ", gunner job " + r.gunner.CurJobDef + ")"
                    : "indirectFireShift " + rep.indirectFireShift.ToString("R") + ", shotDist " + rep.shotDist.ToString("0.00") + ", " + Shifts(rep) + (r.fix == "c" ? ", slab to row +" + edge : "");
                Check(r.id + ": " + (hidden ? "hidden, CE's indirect-fire penalty" : "seen, no indirect-fire penalty"),
                    rep != null && (r.fix != "c" || geometry) && (hidden ? rep.indirectFireShift > 0f : rep.indirectFireShift == 0f), got);
            }
            foreach (var r in run)
            {
                r.mortar.ResetForcedTarget();
                Gone(r.gunner, r.mortar, r.target);
            }
        }

        private static bool Reported(Building_TurretGunCE mortar) => casts.TryGetValue(mortar.AttackVerb, out List<CastReports> l) && l.Any(c => c.mortar != null);
    }
}
