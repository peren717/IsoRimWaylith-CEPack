using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // Sight, cover and smoke between floors (plan §1.4): CE walks raw cells from shooter to target, which between floors
    // crosses the void rows and ignores slabs. For pairs on two floors (only) the pack walks ProjectZ's 3D line instead
    // (Api 6 WalkLine3D: per ground column the cell on the floor the line is in, its height range there and any solid plane
    // crossed) and applies CE's own per-cell rule to the cells of every storey the line passes in that column, with CE's
    // ray moved into that storey's frame. A solid plane crossed (a slab, a roof) blocks. Pairs on one floor: CE's own.
    //
    // CE's per-cell rule is copied from CE 1.6 (2890901044 DLL, Verb_LaunchProjectileCE.CanHitCellFromCellIgnoringRange
    // and GetHighestCoverAndSmokeForTarget); diff it on CE updates. One change between floors (D6): CE's quick pass
    // "cover lower than the shot's start height" uses the line's own lowest height in that column, so cover the 3D line
    // passes over is ignored (as IsoRimZ's height cover).
    internal static class Sight
    {
        private static readonly List<ProjectZ.Api.LineStep> steps = new List<ProjectZ.Api.LineStep>();
        public static int Walks, Blocked;
        public static Action<string> Debug;   // harness

        // cells over the floor: the aim heights at a cell target on a higher floor (ProjectZ's Los3D aims: head, chest,
        // waist)
        private static readonly float[] CellAimsUp = { 1.10f, 0.80f, 0.50f };

        public static void Patch(Harmony h)
        {
            Type v = typeof(Verb_LaunchProjectileCE);
            PackZ.Prefix(h, Aim.ByName(v, "CanHitCellFromCellIgnoringRange", 3), typeof(Sight), nameof(CanHitCell_Prefix));
            PackZ.Prefix(h, Aim.ByName(v, "GetHighestCoverAndSmokeForTarget", 4), typeof(Sight), nameof(Cover_Prefix));
        }

        // The target point CE aims its sight line at (CE: CanHitCellFromCellIgnoringRange), in the shooter's frame.
        private static Vector3 SightTarget(Thing targetThing, IntVec3 targetLoc, int up, Layout L, int ks, int kt, int aim)
        {
            Vector3 val;
            if (targetThing != null)
            {
                Vector3 d = targetThing.DrawPos;
                val = new Vector3(d.x, new CollisionVertical(targetThing).Max, d.z);
                if (targetThing is Pawn pawn)
                {
                    val += pawn.Drawer.leaner.LeanOffset * 0.6f;
                }
            }
            else
            {
                val = targetLoc.ToVector3Shifted();
                // CE aims a cell at its ground. A cell on a higher floor is aimed at ProjectZ's aim heights over it (as
                // every target of the game's own verbs: head, chest, waist): the line then has to rise past that floor's
                // level over an open cell before the target (an opening, a stairwell, past the slab's edge), not touch
                // it exactly at the target from under the slab.
                val.y = up > 0 ? CellAimsUp[aim] / Layout.CellsPerUnit : 0f;
            }
            val = L.TranslateRows(val, kt, ks);
            val.y += up * Layout.S;
            return val;
        }

        // CE: CanHitCellFromCellIgnoringRange, between floors.
        public static bool CanHitCell_Prefix(Verb_LaunchProjectileCE __instance, Vector3 shotSource, IntVec3 targetLoc, Thing targetThing, ref bool __result)
        {
            Thing caster = __instance.caster;
            Map map = caster?.Map;
            Layout L = Layout.Of(map);
            if (L == null)
            {
                return true;
            }
            // (a thing another mod gives its own aim and volume: always the 3D walk, to its range cell)
            TargetAim targetAim = default;
            bool custom = TargetGeometry.Active && TargetGeometry.TryAim(targetThing, __instance, Aim.RootOf(__instance), out targetAim);
            if (custom)
            {
                targetLoc = targetAim.RangeCell;
            }
            int ks = L.FrameOf(shotSource), kt = L.FrameOf(targetLoc);
            if ((!custom && ks == kt) || ks == Layout.None || kt == Layout.None)
            {
                return true;
            }
            if (!PackZ.Healthy)
            {
                __result = false;
                return false;
            }
            PackZ.Changed++;
            TargetGeometry.State before = TargetGeometry.Enter(map, ks);
            try
            {
                __result = CanHit(__instance, shotSource, targetLoc, targetThing, ks, kt, L, map);
            }
            finally
            {
                TargetGeometry.Exit(before);
            }
            if (!__result)
            {
                Blocked++;
            }
            return false;
        }

        private static bool CanHit(Verb_LaunchProjectileCE verb, Vector3 shotSource, IntVec3 targetLoc, Thing targetThing, int ks, int kt, Layout L, Map map)
        {
            Thing caster = verb.caster;
            if (verb.verbProps.mustCastOnOpenGround && (!targetLoc.Standable(map) || map.thingGrid.CellContains(targetLoc, ThingCategory.Pawn)))
            {
                return false;
            }
            if (!verb.verbProps.requireLineOfSight)
            {
                return true;
            }
            int up = kt - ks;
            if (targetThing != null)
            {
                float sh = shotSource.y;
                verb.AdjustShotHeight(caster, targetThing, ref sh);
                shotSource.y = sh;
            }
            // (a cell on a higher floor: ProjectZ's aim heights over it in turn, any clear one = a line, as for the game's verbs)
            int aims = targetThing == null && up > 0 ? CellAimsUp.Length : 1;
            for (int ai = 0; ai < aims; ai++)
            {
                Vector3 point = TargetGeometry.Active && TargetGeometry.TryAim(targetThing, verb, Aim.RootOf(verb), out TargetAim aim)
                    ? L.FromVirtual(aim.PointVirtual, ks) : SightTarget(targetThing, targetLoc, up, L, ks, kt, ai);
                if (Line(verb, shotSource, point, targetLoc, targetThing, up, ks, L, map, caster))
                {
                    return true;
                }
            }
            return false;
        }

        // One sight line from shotSource to target (frame ks): ProjectZ's 3D walk (a solid plane crossed blocks), CE's
        // per-cell rule on the cells of every storey it passes, and for a slow shot up the arc rule.
        private static bool Line(Verb_LaunchProjectileCE verb, Vector3 shotSource, Vector3 target, IntVec3 targetLoc, Thing targetThing, int up, int ks, Layout L, Map map, Thing caster)
        {
            Walks++;
            int n = ProjectZ.Api.WalkLine3D(L.ToVirtual(shotSource, ks), L.ToVirtual(target, ks), map, steps, true);
            if (n == 0)
            {
                return false;
            }
            for (int i = 0; i < n; i++)
            {
                if (steps[i].solidPlane != int.MinValue)
                {
                    if (Debug != null) { Debug("    sight: solid plane " + steps[i].solidPlane + " in column " + L.Ground(steps[i].cell) + ", line " + L.ToVirtual(shotSource, ks).ToString("F2") + " -> " + L.ToVirtual(target, ks).ToString("F2") + ", steps " + string.Join(" ", steps.Take(n).Select(x => L.Ground(x.cell).z + "f" + x.floor + "[" + x.lo.ToString("0.00") + "," + x.hi.ToString("0.00") + "]"))); }
                    return false;   // a slab or roof between
                }
            }
            Ray line = new Ray(shotSource, target - shotSource);
            AimMode? aimMode = verb.CompFireModes?.CurrentAimMode;
            Pawn shooterPawn = verb.ShooterPawn;
            LightingTracker lighting = map.GetLightingTracker();
            float storey = ProjectZ.Api.StoreyHeight;
            float k = Layout.CellsPerUnit;
            IntVec3 gTarget = L.Ground(targetLoc);
            IntVec3 gSource = L.Ground(shotSource.ToIntVec3());
            for (int i = 1; i < n - 1; i++)
            {
                ProjectZ.Api.LineStep s = steps[i];
                IntVec3 g = L.Ground(s.cell);
                if (!g.IsValid || g == gTarget || g == gSource)
                {
                    continue;
                }
                int fLo = Mathf.Max(L.min, s.floor + Mathf.FloorToInt(s.lo / storey));
                int fHi = Mathf.Min(L.max, s.floor + Mathf.FloorToInt(s.hi / storey));
                for (int f = fLo; f <= fHi; f++)
                {
                    IntVec3 cell = L.Cell(g, f);
                    if (!cell.IsValid || !cell.InBounds(map))
                    {
                        continue;
                    }
                    // the line's lowest height in this column over floor f, in CE units
                    float lowCE = (s.lo + (s.floor - f) * storey) / k;
                    Vector3 src = L.Translate(shotSource, ks, f);
                    Ray ray = new Ray(src, line.direction);
                    if (TargetGeometry.Active && !VolumesClear(verb, cell, f, ray, targetThing, caster, aimMode, map))
                    {
                        return false;
                    }
                    if (!CellClear(verb, cell, lowCE, ray, src, targetLoc, targetThing, caster, shooterPawn, aimMode, lighting, map, L))
                    {
                        if (Debug != null)
                        {
                            Debug("    sight: blocked in " + cell + " by " + (cell.GetFirstPawn(map) ?? cell.GetCover(map)));
                        }
                        return false;
                    }
                }
            }
            if (up > 0)
            {
                // a slow shot up (a grenade launcher, a bow): only if one of its arcs gets past the floor planes on the
                // way (Aim's arc rule, aimed at the target's middle as CE aims)
                Vector3 mid = target;
                mid.y = up * Layout.S;
                if (targetThing != null)
                {
                    CollisionVertical cv = new CollisionVertical(targetThing);
                    mid.y += (cv.HeightRange.min + cv.HeightRange.max) / 2f;
                }
                if (!Aim.SlowShotClears(verb, shotSource, mid, L, map, ks))
                {
                    Debug?.Invoke("    sight: no arc of the slow shot gets past the floor planes");
                    return false;
                }
            }
            return true;
        }

        // Other mods' volumes over a cell (Api target geometry) block the line like full cover (not for suppressive fire or
        // a verb that ignores partial cover, as CE treats partial blockers); the target and the shooter never block.
        private static bool VolumesClear(Verb_LaunchProjectileCE verb, IntVec3 cell, int f, Ray ray, Thing targetThing, Thing caster, AimMode? aimMode, Map map)
        {
            List<Thing> volumes = TargetGeometry.At(map, cell);
            if (volumes == null || verb.VerbPropsCE.ignorePartialLoSBlocker || aimMode == AimMode.SuppressFire)
            {
                return true;
            }
            foreach (Thing extra in volumes)
            {
                if (extra != targetThing && extra != caster && TargetGeometry.TryVolumeIn(extra, map, f, out Bounds b) && b.IntersectRay(ray))
                {
                    TargetGeometry.SightBlocks++;
                    return false;
                }
            }
            return true;
        }

        // CE's per-cell rule (the predicate in CanHitCellFromCellIgnoringRange) on one cell of storey f, the ray in f's frame.
        private static bool CellClear(Verb_LaunchProjectileCE verb, IntVec3 cell, float lowCE, Ray shotLine, Vector3 src, IntVec3 targetLoc, Thing targetThing,
            Thing caster, Pawn shooterPawn, AimMode? aimMode, LightingTracker lighting, Map map, Layout L)
        {
            if (lighting.HighestCoverAt(cell) < lowCE)
            {
                return true;
            }
            Thing thing = cell.GetFirstPawn(map) ?? cell.GetCover(map);
            if (thing == null || thing == shooterPawn || thing == caster || thing == targetThing || thing.IsPlant() || (thing is Pawn && thing.HostileTo(caster)))
            {
                return true;
            }
            if (thing is Pawn { Downed: false } pawn2 && thing.Faction != null && shooterPawn?.Faction != null
                && (shooterPawn.Faction == pawn2.Faction || shooterPawn.Faction.RelationKindWith(pawn2.Faction) == FactionRelationKind.Ally) && !pawn2.AdjacentTo8WayOrInside(caster))
            {
                return false;
            }
            if ((verb.VerbPropsCE.ignorePartialLoSBlocker || aimMode == AimMode.SuppressFire) && thing.def.Fillage != FillCategory.Full)
            {
                return true;
            }
            Bounds b = TargetGeometry.BoundsIn(thing, map, L.FrameOf(cell));
            if (thing.def.Fillage != FillCategory.Full && thing.AdjacentTo8WayOrInside(caster))
            {
                float toTarget = (L.Ground(cell) - L.Ground(targetLoc)).LengthHorizontal;
                float fromSource = (L.Ground(src.ToIntVec3()) - L.Ground(targetLoc)).LengthHorizontal;
                if (fromSource > toTarget)
                {
                    return thing is Pawn || b.size.y < src.y;
                }
            }
            return !b.IntersectRay(shotLine);
        }

        // CE: GetHighestCoverAndSmokeForTarget between floors. The walk runs from the target back to the shooter (CE's
        // order); cover counts in the target's storey only and only where the 3D line passes below its top (D6); smoke
        // anywhere on the line; "roofed" (CE: the target height is capped at 2 under a roof) from roofs over the target's
        // storey on the way.
        public static bool Cover_Prefix(Verb_LaunchProjectileCE __instance, LocalTargetInfo target, ref Thing cover, ref float smokeDensity, ref bool roofed, ref bool __result)
        {
            Thing caster = __instance.caster;
            Map map = caster?.Map;
            Layout L = Layout.Of(map);
            if (L == null)
            {
                return true;
            }
            TargetAim targetAim = default;
            bool custom = TargetGeometry.Active && TargetGeometry.TryAim(target.Thing, __instance, Aim.RootOf(__instance), out targetAim);
            IntVec3 reference = custom ? targetAim.RangeCell : target.Cell;
            int ks = L.FrameOf(caster.Position), kt = L.FrameOf(reference);
            if ((!custom && ks == kt) || ks == Layout.None || kt == Layout.None)
            {
                return true;
            }
            PackZ.Changed++;
            cover = null;
            smokeDensity = 0f;
            roofed = false;
            __result = false;
            Vector3 src = caster.TrueCenter();
            src.y = __instance.ShotHeight;
            Thing thing = target.Thing;
            Vector3 dst = L.TranslateRows(thing != null ? thing.TrueCenter() : target.Cell.ToVector3Shifted(), kt, ks);
            // the line to the lowest point CE aims at (a pawn's BottomHeight): cover it passes over hides nothing CE aims at
            dst.y = (kt - ks) * Layout.S + (thing != null ? new CollisionVertical(thing).BottomHeight : 0f);
            if (custom)
            {
                dst = L.FromVirtual(targetAim.PointVirtual, ks);   // (another mod's aim point)
            }
            int n = ProjectZ.Api.WalkLine3D(L.ToVirtual(src, ks), L.ToVirtual(dst, ks), map, steps, false);
            if (n < 3)
            {
                return false;
            }
            bool instant = __instance.Projectile?.projectile is ProjectilePropertiesCE pp && pp.isInstant;
            int count = instant ? n : n / 2;
            float storey = ProjectZ.Api.StoreyHeight;
            float k = Layout.CellsPerUnit;
            float best = 0f;
            for (int j = 0; j < count; j++)
            {
                ProjectZ.Api.LineStep s = steps[n - 1 - j];
                IntVec3 g = L.Ground(s.cell);
                if (!g.IsValid)
                {
                    continue;
                }
                int fLo = Mathf.Max(L.min, s.floor + Mathf.FloorToInt(s.lo / storey));
                int fHi = Mathf.Min(L.max, s.floor + Mathf.FloorToInt(s.hi / storey));
                for (int f = fLo; f <= fHi; f++)
                {
                    IntVec3 c = L.Cell(g, f);
                    if (!c.IsValid || !c.InBounds(map) || (f == ks && c.AdjacentTo8Way(caster.Position)))
                    {
                        continue;
                    }
                    if (c.AnyGas(map, GasType.BlindSmoke))
                    {
                        smokeDensity += 0.7f;
                    }
                    if (f != kt)
                    {
                        continue;
                    }
                    if (!roofed)
                    {
                        roofed = map.roofGrid.RoofAt(c) != null;
                    }
                    if (!instant && j > n / 2)
                    {
                        continue;
                    }
                    Pawn firstPawn = c.GetFirstPawn(map);
                    Thing t3 = firstPawn ?? c.GetCover(map);
                    if (t3 != null && (thing == null || !t3.Equals(thing)) && t3.def.Fillage == FillCategory.Partial && !t3.IsPlant() && !(t3 is Building_TrapExplosive))
                    {
                        float max = new CollisionVertical(t3).Max;
                        float lowCE = (s.lo + (s.floor - f) * storey) / k;
                        if (max > lowCE && (cover == null || best < max))
                        {
                            cover = t3;
                            best = max;
                        }
                    }
                }
            }
            __result = cover != null;
            return false;
        }
    }
}
