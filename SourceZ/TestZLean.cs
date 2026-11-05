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
using Verse.AI.Group;

namespace IsoRimCEPackZ
{
    // Harness, lean shots between floors (mode firing): a shooter behind cover that only a lean gets past, on another floor
    // than the target, beside the same layout on one floor. ProjectZ's ShootLean_Patch gives the game's lean utility the
    // target's column on the shooter's floor, and CE's TryFindCEShootLineFromTo calls that utility, so these check that CE's
    // path gets the list the same floor would give, and that a shot it approves is fired and not stopped.
    // -cepacknolean sets ShootLean_Patch.Off (the game's own directions), -cepacknofire leaves FireLine out and
    // -cepacknoleanfix judges planes from the lean point, to show what they catch. -cepackleanall runs every cover in every
    // direction (the full matrix); every row goes to renders/<tag>/lean.tsv.
    public static partial class TestZ
    {
        private enum LeanCfg { Same, Same1, Down, Down2, Up }

        // Corner: a wall end ahead. Gap: a window in a wall line. Door, Shut: an open or closed door in that gap. Low: sandbags.
        // SlabSide: Corner, the lean cell past the slab's edge. Hole: Corner, the lean cell over an opening. Bare: no cover.
        private enum LeanCover { Corner, Gap, Door, Shut, Low, SlabSide, Hole, Bare }

        private sealed class LeanRow
        {
            public LeanCfg cfg;
            public LeanCover cover;
            public int dir, side, D, launched, hits, own, walls, wiped;
            public bool listOk, ceGate, gameGate, leanLine, offFloor;
            public string list, want, sources, ends, can;
        }

        private static readonly List<LeanRow> leanRows = new List<LeanRow>();
        private static readonly List<(LeanCover cover, int D, bool ce, bool lean, int launched, int own)> leanCells = new List<(LeanCover, int, bool, bool, int, int)>();
        private static readonly HashSet<IntVec3> leanSources = new HashSet<IntVec3>();
        private static readonly string[] DirNames = { "N", "E", "S", "W" };

        private static void PatchLean(Harmony h)
        {
            h.Patch(Aim.ByName(typeof(Verb_LaunchProjectileCE), "TryFindCEShootLineFromTo", 4), postfix: new HarmonyMethod(typeof(TestZ), nameof(LeanLine_Postfix)));
            if (GenCommandLine.CommandLineArgPassed("cepacknolean"))
            {
                AccessTools.Field(AccessTools.TypeByName("ShootLean_Patch"), "Off").SetValue(null, true);
            }
        }

        // Every line the fixture's gunner finds from its own cell, in the real flow: where the shot was judged from.
        public static void LeanLine_Postfix(Verb_LaunchProjectileCE __instance, IntVec3 root, ref ShootLine resultingLine, bool __result)
        {
            if (__result && fireGunner != null && __instance.caster == fireGunner && root == fireGunner.Position)
            {
                leanSources.Add(resultingLine.Source);
            }
        }

        // A cell by forward and side offsets from the shooter, facing dir (0 N, 1 E, 2 S, 3 W; side + is to its right).
        private static IntVec3 LeanCell(int dir, int f, int s, int k)
        {
            switch (dir)
            {
                case 0: return On(FX + s, FZ + f, k);
                case 1: return On(FX + f, FZ - s, k);
                case 2: return On(FX - s, FZ - f, k);
                default: return On(FX - f, FZ + s, k);
            }
        }

        private static void LeanSlab(int dir, int f0, int f1, int s0, int s1, int k)
        {
            IntVec3 a = L.Ground(LeanCell(dir, f0, s0, k)), b = L.Ground(LeanCell(dir, f1, s1, k));
            Slab(Mathf.Min(a.x, b.x), Mathf.Min(a.z, b.z), Mathf.Max(a.x, b.x), Mathf.Max(a.z, b.z), k);
        }

        private static void LeanArena(LeanCfg cfg, LeanCover cover, int dir, int side, int D, out int ks, out int kt)
        {
            // (this many fixtures reach the game's autosave, which then trips over the fixtures' destroyed pawns)
            Traverse.Create(Find.Autosaver).Field("ticksSinceSave").SetValue(0);
            Wipe(FX - 25, FZ - 15, FX + 25, FZ + 40);
            ClearRoofs();
            leanWiped = SolidPlanes();
            int w = side == 0 ? 1 : side;   // (a target in the shooter's column: the wall runs to the left)
            ks = cfg == LeanCfg.Same1 || cfg == LeanCfg.Down ? 1 : cfg == LeanCfg.Down2 ? 2 : 0;
            kt = cfg == LeanCfg.Same1 || cfg == LeanCfg.Up ? 1 : 0;
            switch (cfg)
            {
                case LeanCfg.Same1:
                    LeanSlab(dir, -12, 12, -12, 12, 1);
                    break;
                case LeanCfg.Down:
                case LeanCfg.Down2:
                    LeanSlab(dir, -4, 1, cover == LeanCover.SlabSide && w < 0 ? 0 : -4, cover == LeanCover.SlabSide && w > 0 ? 0 : 4, ks);
                    if (cover == LeanCover.Hole)
                    {
                        IntVec3 g = L.Ground(LeanCell(dir, 0, w, ks));
                        Opening(g.x, g.z, g.x, g.z, ks);
                    }
                    break;
                case LeanCfg.Up:
                    LeanSlab(dir, D, D + 2, -6, 6, 1);
                    break;
            }
            switch (cover)
            {
                case LeanCover.Gap:
                case LeanCover.Door:
                case LeanCover.Shut:
                    for (int s = -3; s <= 3; s++)
                    {
                        if (s != w)
                        {
                            Build("Wall", LeanCell(dir, 1, s, ks));
                        }
                    }
                    if (cover != LeanCover.Gap)
                    {
                        Building_Door door = (Building_Door)Build("Door", LeanCell(dir, 1, w, ks));
                        if (cover == LeanCover.Door)
                        {
                            Traverse.Create(door).Field("openInt").SetValue(true);
                            Traverse.Create(door).Field("holdOpenInt").SetValue(true);
                        }
                    }
                    break;
                case LeanCover.Bare:
                    break;
                case LeanCover.Low:
                    for (int s = -1; s <= 1; s++)
                    {
                        Build("Sandbags", LeanCell(dir, 1, s, ks));
                    }
                    break;
                default:
                    for (int s = 0; s <= 2; s++)
                    {
                        Build("Wall", LeanCell(dir, 1, -s * w, ks));
                    }
                    break;
            }
        }

        private static int leanWiped;

        // Wipe can leave the last fixture's planes solid (the roofs under its slabs): every fixture here starts on open planes
        // (leanWiped: the count left, gate L0).
        private static void ClearRoofs()
        {
            for (int k = L.min; k <= L.max; k++)
            {
                for (int x = FX - 25; x <= FX + 25; x++)
                {
                    for (int z = FZ - 15; z <= FZ + 40; z++)
                    {
                        Map.roofGrid.SetRoof(On(x, z, k), null);
                    }
                }
            }
        }

        // Solid planes over the arena's middle (fixture sanity: a wiped arena has none).
        private static int SolidPlanes()
        {
            int n = 0;
            for (int k = 1; k <= L.max; k++)
            {
                for (int x = FX - 16; x <= FX + 16; x++)
                {
                    for (int z = FZ - 14; z <= FZ + 16; z++)
                    {
                        n += ProjectZ.Api.PlaneSolid(new IntVec3(x, 0, z), k, Map) ? 1 : 0;
                    }
                }
            }
            return n;
        }

        private static string Cells(IEnumerable<IntVec3> cells, IntVec3 root) => string.Join(" ", cells.Select(c => c == root ? "root" : (c - root).x + "," + (c - root).z));

        private static IEnumerator<int> LeanFixture(LeanCfg cfg, LeanCover cover, int dir, int side, int D, int offset = 3, string weapon = "Gun_AssaultRifle")
        {
            string id = "lean " + cfg + " " + cover + " " + DirNames[dir] + " side " + side + " D " + D;
            LeanArena(cfg, cover, dir, side, D, out int ks, out int kt);
            Pawn target = Target(LeanCell(dir, D, side * offset, kt), "Muffalo");
            Pawn shooter = Colonist(LeanCell(dir, 0, 0, ks), weapon);
            yield return 30;   // (a drafted pawn has settled into its posture: CE's shot height is the crouch's)
            shooter.Drawer?.tweener?.ResetTweenedPosToRoot();
            target.Drawer?.tweener?.ResetTweenedPosToRoot();
            IntVec3 root = shooter.Position;
            Verb_LaunchProjectileCE v = (Verb_LaunchProjectileCE)VerbOf(shooter);
            // the list CE gets (its own call's arguments) against the same floor's (the target's column on the shooter's floor)
            List<IntVec3> got = new List<IntVec3>(), want = new List<IntVec3>();
            ShootLeanUtility.LeanShootingSourcesFromTo(root, target.OccupiedRect().ClosestCellTo(root), Map, got);
            ShootLeanUtility.LeanShootingSourcesFromTo(root, L.Cell(L.Ground(target.Position), ks), Map, want);
            LeanRow row = new LeanRow { wiped = leanWiped, cfg = cfg, cover = cover, dir = dir, side = side, D = D, list = Cells(got, root), want = Cells(want, root) };
            row.listOk = got.SequenceEqual(want);
            row.ceGate = v.TryFindCEShootLineFromTo(root, target, out ShootLine line);
            row.gameGate = v.TryFindShootLineFromTo(root, target, out ShootLine _);
            row.leanLine = row.ceGate && line.Source != root;
            leanSources.Clear();
            string state = leanWiped + " h " + v.ShotHeight.ToString("0.00");
            int t0 = Find.TickManager.TicksGame;
            FireResult r = new FireResult();
            IEnumerator<int> f = FireAt(id, shooter, target, 6, r, 700);
            while (f.MoveNext())
            {
                yield return f.Current;
            }
            List<Shot> ends = By(shooter, Impacts, t0);
            row.can = r.can;
            row.launched = r.launched;
            row.hits = r.hits;
            row.own = r.own;
            row.walls = ends.Count(s => s.hit.StartsWith("Wall") || s.hit.StartsWith("Door") || s.hit.StartsWith("Sandbags"));
            row.ends = string.Join(",", ends.GroupBy(s => s.hit.Split('#')[0] + "@f" + s.frame).Select(g => g.Key + "x" + g.Count()));
            row.offFloor = leanSources.Any(c => L.FrameOf(c) != ks);
            row.sources = Cells(leanSources, root);
            leanRows.Add(row);
            File.AppendAllText(Path.Combine(outDir, "lean.tsv"), string.Join("\t", cfg, cover, DirNames[dir], side, D, row.listOk, row.list, row.want, row.ceGate, row.gameGate, row.leanLine, row.sources, row.offFloor, r.launched, r.hits, r.own, row.walls, row.ends, state) + "\n");
            FireEnd(shooter, target);
        }

        // A cell target (no fired height is sought for it): what Sight approves, and where the shots end.
        private static IEnumerator<int> LeanCellShots(LeanCover cover, int D, int offset)
        {
            LeanArena(LeanCfg.Down, cover, 0, 1, D, out int ks, out int kt);
            IntVec3 cell = LeanCell(0, D, offset, kt);
            Pawn shooter = Colonist(LeanCell(0, 0, 0, ks), "Gun_AssaultRifle");
            yield return 30;
            shooter.Drawer?.tweener?.ResetTweenedPosToRoot();
            Verb_LaunchProjectileCE v = (Verb_LaunchProjectileCE)VerbOf(shooter);
            bool ce = v.TryFindCEShootLineFromTo(shooter.Position, cell, out ShootLine line);
            bool game = v.TryFindShootLineFromTo(shooter.Position, cell, out ShootLine _);
            bool lean = ce && line.Source != shooter.Position;
            fireGunner = shooter;
            int t0 = Find.TickManager.TicksGame, t = 0;
            if (ce)
            {
                Order(shooter, cell);
            }
            while (ce && By(shooter, Launches, t0).Count < 6 && t < 500)
            {
                yield return 10;
                t += 10;
            }
            shooter.jobs.EndCurrentJob(JobCondition.InterruptForced);
            yield return 80;
            fireGunner = null;
            List<Shot> ends = By(shooter, Impacts, t0);
            string fate = string.Join(",", ends.GroupBy(s => s.hit.Split('#')[0] + "@f" + s.frame).Select(g => g.Key + "x" + g.Count()));
            File.AppendAllText(Path.Combine(outDir, "leancell.tsv"), string.Join("\t", cover, D, offset, ce, game, lean, By(shooter, Launches, t0).Count, ends.Count(s => s.frame == ks), fate, v.ShotHeight.ToString("0.00")) + "\n");
            leanCells.Add((cover, D, ce, lean, By(shooter, Launches, t0).Count, ends.Count(s => s.frame == ks)));
            FireEnd(shooter);
        }

        // The target's own lean cells (CalcShootableCellsOf: ShootLeanUtility's call with the two ends swapped).
        private static IEnumerator<int> LeanTargetCells()
        {
            int wrong = 0, n = 0;
            string text = "";
            foreach (int dir in new[] { 0, 1, 2, 3 })
            {
                Wipe(FX - 25, FZ - 15, FX + 25, FZ + 40);
                ClearRoofs();
                LeanSlab(dir, -4, 1, -4, 4, 1);
                IntVec3 tc = LeanCell(dir, 8, 3, 0);
                // (a wall end between the target and the shooter: it leans out past it)
                Build("Wall", LeanCell(dir, 7, 3, 0));
                Build("Wall", LeanCell(dir, 7, 4, 0));
                Pawn target = Target(tc, "Muffalo");
                Pawn shooter = Colonist(LeanCell(dir, 0, 0, 1), "Gun_AssaultRifle");
                yield return 5;
                List<IntVec3> got = new List<IntVec3>(), want = new List<IntVec3>();
                ShootLeanUtility.CalcShootableCellsOf(got, target, shooter.Position);
                ShootLeanUtility.LeanShootingSourcesFromTo(tc, L.Cell(L.Ground(shooter.Position), 0), Map, want);
                n++;
                wrong += got.SequenceEqual(want) ? 0 : 1;
                text += DirNames[dir] + ": " + Cells(got, tc) + " (want " + Cells(want, tc) + "); ";
                FireEnd(shooter, target);
            }
            Check("L5 the target's own lean cells between floors are the same floor's", n == 4 && wrong == 0, text);
        }

        private static IEnumerable<IEnumerator<int>> LeanChecks()
        {
            bool all = GenCommandLine.CommandLineArgPassed("cepackleanall");
            bool three = L != null && L.Has(2);
            List<LeanCfg> cfgs = new List<LeanCfg> { LeanCfg.Same, LeanCfg.Same1, LeanCfg.Down, LeanCfg.Up };
            if (three)
            {
                cfgs.Add(LeanCfg.Down2);
            }
            foreach (LeanCfg cfg in cfgs)
            {
                int D = cfg == LeanCfg.Up ? 6 : cfg == LeanCfg.Down2 ? 12 : 8, offset = cfg == LeanCfg.Down2 ? 4 : 3;
                foreach (int dir in new[] { 0, 1, 2, 3 })
                {
                    foreach (int side in new[] { 1, -1 })
                    {
                        yield return LeanFixture(cfg, LeanCover.Corner, dir, side, D, offset);
                        foreach (LeanCover c in new[] { LeanCover.Gap, LeanCover.Door, LeanCover.Shut, LeanCover.Low })
                        {
                            if (all || (dir == 0 && side == 1))
                            {
                                yield return LeanFixture(cfg, c, dir, side, D, offset);
                            }
                        }
                    }
                    yield return LeanFixture(cfg, LeanCover.Corner, dir, 0, D, offset);
                }
            }
            if (three && all)
            {
                // (two floors up at D 8 the line from a crouched shooter meets the slab, with or without cover)
                yield return LeanFixture(LeanCfg.Down2, LeanCover.Bare, 0, 1, 8);
                yield return LeanFixture(LeanCfg.Down2, LeanCover.Corner, 0, 1, 8);
            }
            foreach (LeanCover c in new[] { LeanCover.SlabSide, LeanCover.Hole })
            {
                foreach (int dir in new[] { 0, 1, 2, 3 })
                {
                    foreach (int D in new[] { 2, 3, 4, 5, 6, 8 })
                    {
                        yield return LeanFixture(LeanCfg.Down, c, dir, 1, D, Mathf.Max(1, D / 2));
                    }
                }
            }
            foreach (LeanCover c in new[] { LeanCover.Corner, LeanCover.SlabSide, LeanCover.Hole })
            {
                foreach (int D in new[] { 2, 3, 4, 5, 6, 8 })
                {
                    yield return LeanCellShots(c, D, Mathf.Max(1, D / 2));
                }
            }
            yield return LeanTargetCells();
            yield return LeanAi();
            yield return LeanGates();
        }

        // An enemy on one slab cell behind a wall end, by its own AI, at a colonist on the ground: the lean is its only line.
        private static IEnumerator<int> LeanAi()
        {
            Wipe(FX - 25, FZ - 15, FX + 25, FZ + 40);
            ClearRoofs();
            LeanSlab(0, 0, 1, 0, 0, 1);
            Build("Wall", LeanCell(0, 1, 0, 1)).SetFaction(null);   // (a colony's wall it would trash instead of shooting)
            Pawn enemy = Raider(LeanCell(0, 0, 0, 1));
            held.RemoveAll(h => h.pawn == enemy);
            ThingDef gun = DefDatabase<ThingDef>.GetNamed("Gun_AssaultRifle");
            enemy.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(gun));
            LordMaker.MakeNewLord(enemy.Faction, new LordJob_AssaultColony(enemy.Faction, false, false, false, false, false), Map, new[] { enemy });
            Pawn colonist = Colonist(LeanCell(0, 8, 3, 0), null);
            colonist.drafter.Drafted = true;
            held.Add((colonist, colonist.Position));
            fireGunner = enemy;
            fireTarget = colonist;
            fireName = "L6";
            leanSources.Clear();
            int t0 = Find.TickManager.TicksGame;
            for (int i = 0; i < 90 && By(enemy, Launches, t0).Count < 8; i++)
            {
                yield return 10;
            }
            List<Shot> ends = By(enemy, Impacts, t0);
            int launched = By(enemy, Launches, t0).Count, own = ends.Count(s => s.hit == "-" && s.frame == 1);
            IntVec3 at = enemy.Position;
            Check("L6 enemy AI behind a wall shoots across the floors, its shots not stopped on its slab", launched > 0 && own * 2 < launched && leanSources.Any(c => c != at), "at " + L.Ground(at) + ", lines from " + Cells(leanSources, at) + ", launched " + launched + ", on its own storey " + own + ", ends " + string.Join(",", ends.GroupBy(s => s.hit.Split('#')[0] + "@f" + s.frame).Select(g => g.Key + "x" + g.Count())));
            fireGunner = null;
            fireTarget = null;
            enemy.GetLord()?.Notify_PawnLost(enemy, PawnLostCondition.Vanished);
            FireEnd(enemy, colonist);
        }

        // The gates over the fixtures above. Hit rates only at a loose floor.
        private static IEnumerator<int> LeanGates()
        {
            bool Far(LeanRow r) => r.cfg == LeanCfg.Down || r.cfg == LeanCfg.Up || (r.cfg == LeanCfg.Down2 && r.D == 12);
            bool Leans(LeanRow r) => r.side != 0 && (r.cover == LeanCover.Corner || r.cover == LeanCover.Gap || r.cover == LeanCover.Door);
            List<LeanRow> cross = leanRows.Where(r => r.cfg != LeanCfg.Same && r.cfg != LeanCfg.Same1).ToList();
            List<LeanRow> leans = cross.Where(r => Far(r) && Leans(r)).ToList();
            List<LeanRow> same = leanRows.Where(r => !(r.cfg != LeanCfg.Same && r.cfg != LeanCfg.Same1) && Leans(r)).ToList();
            List<LeanRow> edge = leanRows.Where(r => r.cover == LeanCover.SlabSide || r.cover == LeanCover.Hole).ToList();
            string Bad(IEnumerable<LeanRow> rows) => string.Join("; ", rows.Take(6).Select(r => r.cfg + " " + r.cover + " " + DirNames[r.dir] + " " + r.side + " D " + r.D + ": list " + r.list + " want " + r.want + ", ce " + r.ceGate + ", game " + r.gameGate + ", shots " + r.launched + ", own " + r.own));
            bool Fires(LeanRow r) => r.ceGate && r.gameGate && r.leanLine && r.launched >= 6 && !r.offFloor && r.own == 0 && r.hits * 2 >= r.launched;
            Check("L0 every fixture starts on open planes", leanRows.All(r => r.wiped == 0), leanRows.Count + " fixtures, solid planes left at most " + leanRows.Max(r => r.wiped));
            Check("L1 the lean list CE gets between floors is the same floor's", cross.Count > 0 && cross.All(r => r.listOk), cross.Count + " fixtures, wrong " + cross.Count(r => !r.listOk) + "; " + Bad(cross.Where(r => !r.listOk)));
            Check("L2 a shot only a lean gets past: approved, fired from the shooter's floor, not stopped, hits at a loose floor", leans.Count > 0 && leans.All(Fires), leans.Count + " fixtures, failing " + leans.Count(r => !Fires(r)) + "; " + Bad(leans.Where(r => !Fires(r))));
            Check("L3 the same layouts on one floor", same.Count > 0 && same.All(Fires), same.Count + " fixtures, failing " + same.Count(r => !Fires(r)) + "; " + Bad(same.Where(r => !Fires(r))));
            bool Kept(LeanRow r) => !r.ceGate || (r.launched > 0 && r.own * 10 <= r.launched);
            Check("L4 lean cell past a slab's edge or over an opening: an approved shot is fired and not stopped on the shooter's storey", edge.Count > 0 && edge.All(Kept), edge.Count + " fixtures, approved " + edge.Count(r => r.ceGate) + ", failing " + edge.Count(r => !Kept(r)) + "; " + Bad(edge.Where(r => !Kept(r))));
            var cells = leanCells.Where(c => c.ce).ToList();
            Check("L7 a cell target by lean: an approved shot is not stopped on the shooter's storey", cells.All(c => c.own * 10 <= c.launched), cells.Count + " approved, of them leaning " + cells.Count(c => c.lean) + "; stopped: " + string.Join(", ", cells.Where(c => c.own * 10 > c.launched).Select(c => c.cover + " D " + c.D + " " + c.own + "/" + c.launched)));
            yield break;
        }
    }
}
