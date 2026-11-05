using System;
using System.Collections.Generic;
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
    // Harness, fire between floors (mode firing): the lead, the fired line (FireLine) and Sight judging it. Real drafted
    // pawns, real orders and ticks, fixed seeds, one fixture per check. The game is held paused between ticks, so a walking
    // target's tween is reset every tick (else DrawPos stays stale). The gates are per-shot bounds; hit rates change from run
    // to run even on one seed, so a hit rate is only a loose floor. -cepacknofire leaves FireLine out, to show what they catch.
    public static partial class TestZ
    {
        private sealed class FireShot
        {
            public string name;
            public int id;
            public float lead, leadBound, dist, lineH, bodyLo, bodyHi, tol;
            public bool midStep, leadOnPath;
        }

        private static readonly List<FireShot> fireShots = new List<FireShot>();
        private static Thing fireGunner;
        private static Pawn fireTarget, fireMover;
        private static IntVec3 moveFrom, moveTo;
        private static string fireName;
        private static ShiftVecReport fireReport;
        private static Vector2 fireLead;

        private static void PatchFiring(Harmony h)
        {
            h.Patch(Aim.ByName(typeof(Verb_LaunchProjectileCE), "ShiftVecReportFor", 2), postfix: new HarmonyMethod(typeof(TestZ), nameof(FireReport_Postfix)));
            h.Patch(AccessTools.Method(typeof(ShiftVecReport), nameof(ShiftVecReport.GetRandLeadVec)), postfix: new HarmonyMethod(typeof(TestZ), nameof(FireLead_Postfix)));
            h.Patch(typeof(ProjectileCE).GetMethods().First(x => x.Name == "Launch" && x.GetParameters().Length == 3 && x.DeclaringType == typeof(ProjectileCE)), postfix: new HarmonyMethod(typeof(TestZ), nameof(FireLaunch_Postfix)));
            h.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.PreApplyDamage)), prefix: new HarmonyMethod(typeof(TestZ), nameof(FireDamage_Prefix)));
            h.Patch(AccessTools.Method(typeof(ProjectileCE), nameof(ProjectileCE.RayCast)), prefix: new HarmonyMethod(AccessTools.Method(typeof(TestZ), nameof(FireRay_Prefix)), Priority.First));
            PatchLean(h);
        }

        public static void FireReport_Postfix(ShiftVecReport __result) => fireReport = __result;

        // (an instant shot never launches: its ray is the launch)
        public static void FireRay_Prefix(Thing launcher)
        {
            if (launcher != null)
            {
                Launches.Add(new Shot { by = launcher.ThingID, tick = Find.TickManager.TicksGame, def = "ray", hit = "-" });
            }
        }

        public static void FireLead_Postfix(Vector2 __result) => fireLead = __result;

        // (the target and the shooter stay whole: a fixture is about the shots)
        public static bool FireDamage_Prefix(Pawn __instance, ref bool absorbed)
        {
            if (__instance == fireTarget || __instance == fireGunner)
            {
                absorbed = true;
                return false;
            }
            return true;
        }

        // Every shot of the fixture's gunner: the lead, and the height its launch line reaches at the predicted target point.
        public static void FireLaunch_Postfix(ProjectileCE __instance)
        {
            if (fireGunner == null || fireTarget == null || __instance.launcher != fireGunner || fireReport == null)
            {
                return;
            }
            ProjectileCE p = __instance;
            Layout l = Layout.Of(p.Map);
            int ks = l.FrameOf(fireGunner.Position), kt = l.FrameOf(fireTarget.Position);
            Vector3 c = fireGunner.TrueCenter();
            Vector3 t = l.TranslateRows(fireTarget.TrueCenter(), kt, ks);
            Vector2 pt = new Vector2(t.x, t.z) + fireLead;
            float D = (pt - new Vector2(c.x, c.z)).magnitude;
            float cos = Mathf.Cos(p.shotAngle);
            float drop = p.GravityPerWidth * D * D / (2f * p.shotSpeed * p.shotSpeed * cos * cos);
            CollisionVertical body = new CollisionVertical(fireTarget);
            // (a stair step: the next cell is on the next floor; the lead must still point along the ground)
            Pawn_PathFollower pather = fireTarget.pather;
            bool midStep = pather != null && pather.Moving && l.FrameOf(pather.nextCell) != kt;
            Vector3 step = midStep ? (l.Ground(pather.nextCell) - l.Ground(fireTarget.Position)).ToVector3() : Vector3.zero;
            fireShots.Add(new FireShot
            {
                midStep = midStep,
                leadOnPath = fireLead.x * step.x + fireLead.y * step.z >= 0f,
                name = fireName,
                id = p.thingIDNumber,
                lead = fireLead.magnitude,
                leadBound = fireReport.leadDist + fireReport.leadShift + 0.3f,
                dist = D,
                lineH = p.shotHeight + D * Mathf.Tan(p.shotAngle) - drop - (kt - ks) * Layout.S,
                bodyLo = body.Min,
                bodyHi = body.Max,
                // (CE's own scatter on the angle: sway, spread and a burst's recoil)
                tol = 0.3f + D * Mathf.Tan((fireReport.swayDegrees + fireReport.spreadDegrees + 2f) * Mathf.Deg2Rad),
            });
        }

        // Every tick: the walking target goes on (back to its start at the end of its path) and the tween follows the cell.
        private static void FireTick()
        {
            if (fireMover != null && fireMover.Spawned && !fireMover.Dead)
            {
                bool arrived = fireMover.Position == moveTo;
                if (arrived)
                {
                    fireMover.pather.StopDead();
                    fireMover.Position = moveFrom;
                    fireMover.Notify_Teleported(true, false);
                }
                if (fireMover.CurJobDef != JobDefOf.Goto || arrived || !fireMover.pather.Moving)
                {
                    Job j = JobMaker.MakeJob(JobDefOf.Goto, moveTo);
                    j.locomotionUrgency = LocomotionUrgency.Jog;
                    fireMover.jobs.StartJob(j, JobCondition.InterruptForced);
                }
                fireMover.Drawer?.tweener?.ResetTweenedPosToRoot();
            }
            (fireGunner as Pawn)?.Drawer?.tweener?.ResetTweenedPosToRoot();
            CompAmmoUser ammo = (fireGunner as Pawn)?.equipment?.Primary?.TryGetComp<CompAmmoUser>();
            if (ammo != null && ammo.HasMagazine && ammo.CurMagCount <= 1)
            {
                ammo.ResetAmmoCount(ammo.SelectedAmmo);
            }
        }

        // The arena: a clear block at (100..150, 100..170), the shooter at (125, 125) firing north.
        private const int FX = 125, FZ = 125;

        private enum Cfg { Same, Edge, Back, Edge2, Up, Up2 }

        // The fixture's floors and slabs: Edge: the shooter at the north lip of its floor-1 slab; Back: two cells from it; Up:
        // the target on a slab on floor 1 (Up2: 2) whose south lip is at the target's cell.
        private static void Arena(Cfg cfg, int D, out int ks, out int kt)
        {
            Wipe(FX - 25, FZ - 15, FX + 25, FZ + 40);
            ks = cfg == Cfg.Edge || cfg == Cfg.Back ? 1 : cfg == Cfg.Edge2 ? 2 : 0;
            kt = cfg == Cfg.Up ? 1 : cfg == Cfg.Up2 ? 2 : 0;
            if (ks > 0)
            {
                Slab(FX - 3, FZ - 5, FX + 3, FZ + (cfg == Cfg.Back ? 2 : 0), ks);
            }
            if (kt > 0)
            {
                Slab(FX - 10, FZ + D, FX + 10, FZ + D + 2, kt);
            }
        }

        // One fixture: gunner ordered at the target until n shots are out or it is refused. Returns the shots' record through
        // the lists; own: shots stopped on a plane in the shooter's own storey.
        private sealed class FireResult
        {
            public string can;
            public int launched, hits, own, noHeight;
            public List<FireShot> shots;
            public string Text => "can hit " + can + ", launched " + launched + ", on target " + hits + ", stopped in the shooter's storey " + own + ", fired with no clear height " + noHeight;
        }

        private static IEnumerator<int> FireAt(string name, Pawn shooter, Pawn target, int n, FireResult r, int limit = 3000)
        {
            fireName = name;
            fireGunner = shooter;
            fireTarget = target;
            int ks = L.FrameOf(shooter.Position);
            yield return 25;
            Verb_LaunchProjectileCE v = (Verb_LaunchProjectileCE)VerbOf(shooter);
            r.can = CanHit(shooter, target);
            if (r.can != "yes")
            {
                Sight.Debug = Out;
                CanHit(shooter, target);
                Sight.Debug = null;
            }
            int t0 = Find.TickManager.TicksGame, t = 0, noHeight0 = FireLine.NoHeight;
            Order(shooter, target);
            while (By(shooter, Launches, t0).Count < n && t < limit)
            {
                yield return 10;
                t += 10;
                if (shooter.CurJobDef != JobDefOf.AttackStatic && v.CanHitTarget(target))
                {
                    v.OrderForceTarget(target);
                }
                if (r.can != "yes" && t >= 600 && By(shooter, Launches, t0).Count == 0)
                {
                    break;   // (refused: no line)
                }
            }
            shooter.jobs.EndCurrentJob(JobCondition.InterruptForced);
            yield return 80;
            List<Shot> ends = By(shooter, Impacts, t0);
            r.launched = By(shooter, Launches, t0).Count;
            r.noHeight = FireLine.NoHeight - noHeight0;
            r.hits = ends.Count(s => s.hit.EndsWith("#" + target.thingIDNumber));
            r.own = ends.Count(s => s.hit == "-" && s.frame == ks && L.FrameOf(target.Position) != ks);
            r.shots = fireShots.Where(s => s.name == name).ToList();
            fireGunner = null;
            fireTarget = null;
            Out("  " + name + ": " + r.Text);
        }

        private static string Bounds(FireResult r, out bool leadOk, out bool lineOk)
        {
            float worstLead = r.shots.Count == 0 ? 0f : r.shots.Max(s => s.lead - s.leadBound);
            float worstLine = r.shots.Count == 0 ? 0f : r.shots.Max(s => Mathf.Max(s.bodyLo - s.lineH, s.lineH - s.bodyHi) - s.tol);
            leadOk = worstLead <= 0f;
            lineOk = worstLine <= 0f;
            return r.shots.Count + " shots, lead over its bound by at most " + worstLead.ToString("0.00") + ", line outside the body by at most " + worstLine.ToString("0.00") + " beyond CE's scatter";
        }

        private static Pawn WalkingTarget(IntVec3 from, IntVec3 to, string kind = "Wolf_Timber")
        {
            Pawn p = Target(from, kind);
            held.RemoveAll(h => h.pawn == p);
            fireMover = p;
            moveFrom = from;
            moveTo = to;
            return p;
        }

        private static void FireEnd(params Thing[] things)
        {
            fireMover = null;
            Gone(things);
        }

        // ---- F1: a walking target on another floor: the lead stays within CE's own bound, the line ends on the body, shot by
        // shot, also in a locked burst. Not stopped by the shooter's own slab.
        private static IEnumerator<int> FireWalking(string id, Cfg cfg, string weapon, string kind = "Wolf_Timber")
        {
            Out("--- " + id + " walking target, " + cfg + ", " + weapon);
            Arena(cfg, 6, out int ks, out int kt);
            // (from two floors up a target nearer than about 4 cells has no line past the lip)
            Pawn target = cfg == Cfg.Up || cfg == Cfg.Up2 ? WalkingTarget(On(FX - 8, FZ + 6, kt), On(FX + 8, FZ + 6, kt), kind) : WalkingTarget(On(FX, FZ + 14, kt), On(FX, FZ + (cfg == Cfg.Edge2 ? 5 : 2), kt), kind);
            Pawn shooter = Colonist(On(FX, FZ, ks), weapon);
            FireResult r = new FireResult();
            IEnumerator<int> f = FireAt(id, shooter, target, 30, r);
            while (f.MoveNext())
            {
                yield return f.Current;
            }
            string b = Bounds(r, out bool leadOk, out bool lineOk);
            Check(id + " lead per shot within CE's bound (" + cfg + ")", r.launched >= 10 && leadOk, b);
            Check(id + " line per shot ends on the body (" + cfg + ")", r.launched >= 10 && lineOk, b);
            // (a walking target comes close to the lip, where CE's scatter can take a shot into it)
            Check(id + " at most one shot in ten stopped in the shooter's storey, hits at a loose floor (" + cfg + ")", r.own * 10 <= r.launched && r.hits * 5 >= r.launched, r.Text);
            FireEnd(shooter, target);
        }

        // ---- F2: a fixed target close to the slab's edge: every approved shot has a fired line past the planes (the exact
        // gate: no shot fired without a clear height); its scatter may still take one in ten into the lip.
        private static IEnumerator<int> FireFixed(string id, Cfg cfg, int D, string weapon)
        {
            Out("--- " + id + " fixed target, " + cfg + ", D " + D + ", " + weapon);
            Arena(cfg, D, out int ks, out int kt);
            Pawn target = Target(On(FX, FZ + D, kt), "Wolf_Timber");
            Pawn shooter = Colonist(On(FX, FZ, ks), weapon);
            FireResult r = new FireResult();
            IEnumerator<int> f = FireAt(id, shooter, target, 12, r);
            while (f.MoveNext())
            {
                yield return f.Current;
            }
            Check(id + " approved, every shot's line clears, hits at a loose floor (" + cfg + " D " + D + ")", r.can == "yes" && r.launched > 0 && r.noHeight == 0 && r.own * 10 <= r.launched && r.hits * 3 >= r.launched, r.Text);
            FireEnd(shooter, target);
        }

        // ---- F3: Sight refuses when no height on the target clears, and only then: set back from the lip, a target right
        // under the slab's edge has no line; further out it has one.
        private static IEnumerator<int> FireRefused()
        {
            Out("--- F3 Sight: no height clears");
            Arena(Cfg.Back, 0, out int ks, out int kt);
            Pawn target = Target(On(FX, FZ + 4, kt), "Wolf_Timber");
            Pawn shooter = Colonist(On(FX, FZ, ks), "Gun_AssaultRifle");
            yield return 25;
            string near = CanHit(shooter, target);
            int r0 = FireLine.Refused;
            string again = CanHit(shooter, target);
            FireEnd(target);
            target = Target(On(FX, FZ + 12, kt), "Wolf_Timber");
            yield return 5;
            string far = CanHit(shooter, target);
            Check("F3 set back: a target at the lip refused, far out approved", near != "yes" && again != "yes" && far == "yes", "near " + near + ", far " + far + ", refused by the fired line " + (FireLine.Refused - r0));
            FireEnd(shooter, target);
        }

        // ---- F4: the same floor runs none of it.
        private static IEnumerator<int> FireSameFloor()
        {
            Out("--- F4 same floor: no adjustment");
            Arena(Cfg.Same, 0, out int _, out int _);
            Pawn target = WalkingTarget(On(FX, FZ + 14, 0), On(FX, FZ + 2, 0));
            Pawn shooter = Colonist(On(FX, FZ, 0), "Gun_AssaultRifle");
            int a0 = FireLine.Adjusted, raised0 = FireLine.Raised;
            FireResult r = new FireResult();
            IEnumerator<int> f = FireAt("F4", shooter, target, 15, r);
            while (f.MoveNext())
            {
                yield return f.Current;
            }
            Check("F4 same floor: zero adjustments", r.launched > 0 && FireLine.Adjusted == a0 && FireLine.Raised == raised0, r.Text + ", adjusted " + (FireLine.Adjusted - a0));
            FireEnd(shooter, target);
        }

        // ---- F5: the stairs. A pawn on a ramp stands on cover to CE: its body is raised, so Sight's line clears a slab the
        // old aim height did not. The shots must then clear it too.
        private static IEnumerator<int> FireStairs()
        {
            Out("--- F5 target on a ramp, the shooter's slab between");
            Wipe(FX - 25, FZ - 15, FX + 25, FZ + 40);
            Slab(FX - 2, FZ - 2, FX + 2, FZ + 2);
            Slab(FX + 5, FZ - 6, FX + 11, FZ + 9);
            Thing stairs = Build("PZ_Stairs", On(FX + 8, FZ + 6, 0), Rot4.South);
            int done = 0;
            foreach (int z in new[] { 4, 6 })
            {
                Pawn target = Target(On(FX + 8, FZ + z, 0), "Wolf_Timber");
                Pawn shooter = Colonist(On(FX, FZ, 1), "Gun_BoltActionRifle");
                FireResult r = new FireResult();
                IEnumerator<int> f = FireAt("F5 z " + z, shooter, target, 8, r);
                while (f.MoveNext())
                {
                    yield return f.Current;
                }
                Out("  body " + new CollisionVertical(target).Min.ToString("0.00") + ".." + new CollisionVertical(target).Max.ToString("0.00"));
                Check("F5 target on the ramp z " + z + ": approved shots not stopped by the shooter's slab", r.can != "yes" || (r.own == 0 && r.noHeight == 0), r.Text);
                done += r.launched;
                FireEnd(shooter, target);
            }
            Out("  ramp shots " + done);
            Gone(stairs);
        }

        // ---- F6: an auto turret on floor 1 at a walking raider on the ground.
        private static IEnumerator<int> FireTurret()
        {
            Out("--- F6 turret at a walking raider on the ground");
            Arena(Cfg.Edge, 0, out int ks, out int kt);
            Building_TurretGunCE turret = (Building_TurretGunCE)Build("Turret_AutoMiniTurret", On(FX, FZ, ks));
            turret.TryGetComp<CompCanBeDormant>()?.WakeUp();
            Load(turret);
            Pawn raider = Raider(On(FX, FZ + 16, kt));
            held.RemoveAll(h => h.pawn == raider);
            fireMover = raider;
            moveFrom = raider.Position;
            moveTo = On(FX, FZ + 3, kt);
            fireGunner = turret;
            fireTarget = raider;
            fireName = "F6";
            int t0 = Find.TickManager.TicksGame, a0 = FireLine.Adjusted;
            for (int i = 0; i < 80; i++)
            {
                yield return 10;
                Load(turret);
            }
            List<Shot> ends = By(turret, Impacts, t0);
            int launched = By(turret, Launches, t0).Count, hits = ends.Count(s => s.hit.EndsWith("#" + raider.thingIDNumber)), own = ends.Count(s => s.hit == "-" && s.frame == ks);
            List<FireShot> shots = fireShots.Where(s => s.name == "F6").ToList();
            string b = Bounds(new FireResult { shots = shots }, out bool leadOk, out bool lineOk);
            Check("F6 turret: fires through the fired line, lead in bound, most shots past its slab, hits", launched >= 3 && FireLine.Adjusted > a0 && leadOk && own * 4 <= launched && hits > 0, "launched " + launched + ", adjusted " + (FireLine.Adjusted - a0) + ", hits " + hits + ", own " + own + "; " + b);
            fireGunner = null;
            fireTarget = null;
            FireEnd(turret, raider);
        }

        // ---- F7: an enemy on the slab, by its own AI (a real assault lord), at a colonist on the ground.
        private static IEnumerator<int> FireEnemy()
        {
            Out("--- F7 enemy AI shooter on floor 1");
            Arena(Cfg.Same, 0, out int _, out int _);
            int ks = 1, kt = 0;
            Slab(FX - 3, FZ - 1, FX + 3, FZ);   // (a catwalk: the AI picks its own cell on it, none far from the lip)
            Pawn enemy = Raider(On(FX, FZ, ks));
            held.RemoveAll(h => h.pawn == enemy);
            ThingDef gun = DefDatabase<ThingDef>.GetNamed("Gun_AssaultRifle");
            enemy.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(gun));
            LordMaker.MakeNewLord(enemy.Faction, new LordJob_AssaultColony(enemy.Faction, false, false, false, false, false), Map, new[] { enemy });
            Pawn colonist = Colonist(On(FX, FZ + 10, kt), null);
            colonist.drafter.Drafted = true;
            held.Add((colonist, colonist.Position));
            fireGunner = enemy;
            fireTarget = colonist;
            fireName = "F7";
            int t0 = Find.TickManager.TicksGame, a0 = FireLine.Adjusted;
            for (int i = 0; i < 90 && By(enemy, Launches, t0).Count < 12; i++)
            {
                yield return 10;
            }
            List<Shot> ends = By(enemy, Impacts, t0);
            int launched = By(enemy, Launches, t0).Count, hits = ends.Count(s => s.hit.EndsWith("#" + colonist.thingIDNumber)), own = ends.Count(s => s.hit == "-" && s.frame == ks);
            // (an AI shooter stands where it likes and shoots badly: some shots may meet the lip, most must not)
            Check("F7 enemy AI: shoots across the floors through the fired line, most shots past its slab", launched > 0 && FireLine.Adjusted > a0 && own * 2 < launched && hits > 0, "at " + L.Ground(enemy.Position) + ", launched " + launched + ", adjusted " + (FireLine.Adjusted - a0) + ", hits " + hits + ", own " + own + ", job " + enemy.CurJobDef);
            fireGunner = null;
            fireTarget = null;
            enemy.GetLord()?.Notify_PawnLost(enemy, PawnLostCondition.Vanished);
            FireEnd(enemy, colonist);
        }

        // ---- F8: cover on the line: a sandbag right in front of the target. CE aims over it; the shots end on the target or
        // the bag, never on the slab.
        private static IEnumerator<int> FireCover()
        {
            Out("--- F8 cover on the line");
            Arena(Cfg.Edge, 0, out int ks, out int kt);
            Thing bag = Build("Sandbags", On(FX, FZ + 7, kt));
            Pawn target = Target(On(FX, FZ + 8, kt), "Muffalo");
            Pawn shooter = Colonist(On(FX, FZ, ks), "Gun_AssaultRifle");
            FireResult r = new FireResult();
            IEnumerator<int> f = FireAt("F8", shooter, target, 12, r);
            while (f.MoveNext())
            {
                yield return f.Current;
            }
            Check("F8 cover on the line: approved, none stopped on the slab, hits", r.can == "yes" && r.own == 0 && r.hits > 0, r.Text);
            FireEnd(shooter, target, bag);
        }

        // ---- F9: a shooter on a ramp (its own floor is the ground, its body on the ramp's cover).
        private static IEnumerator<int> FireFromStairs()
        {
            Out("--- F9 shooter on a ramp");
            Wipe(FX - 25, FZ - 15, FX + 25, FZ + 40);
            Slab(FX + 5, FZ - 6, FX + 11, FZ + 2);
            Slab(FX + 12, FZ + 3, FX + 16, FZ + 7);
            Thing stairs = Build("PZ_Stairs", On(FX + 8, FZ + 6, 0), Rot4.South);
            Pawn target = Target(On(FX + 12, FZ + 5, 1), "Wolf_Timber");
            Pawn shooter = Colonist(On(FX + 8, FZ + 5, 0), "Gun_AssaultRifle");
            FireResult r = new FireResult();
            IEnumerator<int> f = FireAt("F9", shooter, target, 10, r, 1200);
            while (f.MoveNext())
            {
                yield return f.Current;
            }
            Check("F9 shooter on a ramp: approved, shots go, none stopped under the slab", r.can == "yes" && r.launched > 0 && r.own == 0, r.Text);
            FireEnd(shooter, target, stairs);
        }

        // ---- F10: what keeps its own path: lobs and flares run nothing of FireLine.
        private static IEnumerator<int> FireOthers()
        {
            foreach (string gun in new[] { "Bow_Great", "CE_GrenadeLauncher", "CE_FlareGun" })
            {
                if (DefDatabase<ThingDef>.GetNamedSilentFail(gun) == null)
                {
                    Out("--- F10 " + gun + ": no such def here");
                    continue;
                }
                foreach (int D in new[] { 8, 2 })
                {
                    Out("--- F10 " + gun + " from a slab to the ground, D " + D);
                    Arena(Cfg.Edge, 0, out int ks, out int kt);
                    Pawn target = Target(On(FX, FZ + D, kt), "Muffalo");
                    Pawn shooter = Colonist(On(FX, FZ, ks), gun);
                    int a0 = FireLine.Adjusted;
                    FireResult r = new FireResult();
                    IEnumerator<int> f = FireAt("F10 " + gun + " " + D, shooter, target, 4, r, 900);
                    while (f.MoveNext())
                    {
                        yield return f.Current;
                    }
                    Check("F10 " + gun + " D " + D + ": keeps its own path (no fired line)", FireLine.Adjusted == a0, r.Text + ", adjusted " + (FireLine.Adjusted - a0));
                    FireEnd(shooter, target);
                }
            }
        }

        // ---- F11: save and load in the middle of a burst at a walking target: the burst's later shots aim as before.
        private static IEnumerator<int> FireSave()
        {
            Out("--- F11 save and load mid-burst");
            Arena(Cfg.Edge, 0, out int ks, out int kt);
            Pawn target = WalkingTarget(On(FX, FZ + 14, kt), On(FX, FZ + 2, kt));
            Pawn shooter = Colonist(On(FX, FZ, ks), "Gun_AssaultRifle");
            fireName = "F11";
            fireGunner = shooter;
            fireTarget = target;
            yield return 25;
            int t0 = Find.TickManager.TicksGame;
            Order(shooter, target);
            int guard = 0;
            while (guard++ < 800 && !(By(shooter, Launches, t0).Count >= 1 && ((Verb_LaunchProjectileCE)VerbOf(shooter)).MidBurst))
            {
                yield return 1;
            }
            int shooterId = shooter.thingIDNumber, targetId = target.thingIDNumber;
            bool mid = ((Verb_LaunchProjectileCE)VerbOf(shooter)).MidBurst;
            const string name = "cepack_firing";
            GameDataSaveLoader.SaveGame(name);
            Game before = Current.Game;
            fireGunner = null;
            fireTarget = null;
            fireMover = null;
            GameDataSaveLoader.LoadGame(name);
            while (Current.Game == before || Find.CurrentMap == null || LongEventHandler.AnyEventNowOrWaiting)
            {
                yield return 0;
            }
            shooter = Map.mapPawns.AllPawnsSpawned.First(p => p.thingIDNumber == shooterId);
            target = Map.mapPawns.AllPawnsSpawned.First(p => p.thingIDNumber == targetId);
            fireGunner = shooter;
            fireTarget = target;
            fireMover = target;
            int t1 = Find.TickManager.TicksGame, n0 = fireShots.Count(s => s.name == "F11");
            Order(shooter, target);
            for (int i = 0; i < 40 && By(shooter, Launches, t1).Count < 6; i++)
            {
                yield return 10;
            }
            List<FireShot> shots = fireShots.Where(s => s.name == "F11").Skip(n0).ToList();
            string b = Bounds(new FireResult { shots = shots }, out bool leadOk, out bool lineOk);
            Check("F11 after a load mid-burst: shots go on, lead and line in bound", mid && shots.Count > 0 && leadOk && lineOk, "saved mid-burst " + mid + "; " + b);
            fireGunner = null;
            fireTarget = null;
            string path = GenFilePaths.FilePathForSavedGame(name);
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
            FireEnd(shooter, target);
        }

        private static IEnumerable<IEnumerator<int>> FiringChecks()
        {
            bool three = L != null && L.Has(2);
            yield return FireSave();
            yield return FireWalking("F1a", Cfg.Edge, "Gun_AssaultRifle");
            yield return FireWalking("F1b", Cfg.Edge, "Gun_BoltActionRifle");
            if (three)
            {
                yield return FireWalking("F1d", Cfg.Edge2, "Gun_AssaultRifle");   // (before the Up fixtures: a wiped slab can stay solid to ProjectZ's plane test)
            }
            yield return FireWalking("F1c", Cfg.Up, "Gun_AssaultRifle");
            yield return FireFixed("F2a", Cfg.Edge, 2, "Gun_AssaultRifle");
            yield return FireFixed("F2b", Cfg.Edge, 3, "Gun_BoltActionRifle");
            yield return FireFixed("F2c", Cfg.Back, 10, "Gun_AssaultRifle");
            yield return FireFixed("F2d", Cfg.Up, 6, "Gun_BoltActionRifle");
            if (three)
            {
                yield return FireFixed("F2e", Cfg.Edge2, 5, "Gun_AssaultRifle");
            }
            yield return FireRefused();
            yield return FireSameFloor();
            yield return FireStairs();
            yield return FireStairStep();
            yield return FireTurret();
            yield return FireEnemy();
            yield return FireCover();
            yield return FireFromStairs();
            yield return FireWalkingHumanlike();
            yield return FireOthers();
            yield return FireInstantWalking();
            foreach (IEnumerator<int> lean in LeanChecks())
            {
                yield return lean;
            }
        }

        // ---- F14: a target climbing a ramp to the shooter's floor: the lead on a stair step points along the ground.
        private static IEnumerator<int> FireStairStep()
        {
            Out("--- F14 a target climbing a ramp");
            Wipe(FX - 25, FZ - 15, FX + 25, FZ + 40);
            Slab(FX - 2, FZ - 2, FX + 2, FZ + 2);
            Slab(FX + 5, FZ - 6, FX + 11, FZ + 2);
            Thing stairs = Build("PZ_Stairs", On(FX + 8, FZ + 6, 0), Rot4.South);
            Pawn target = WalkingTarget(On(FX + 8, FZ + 14, 0), On(FX + 8, FZ - 5, 1));
            Pawn shooter = Colonist(On(FX, FZ, 1), "Gun_AssaultRifle");
            FireResult r = new FireResult();
            IEnumerator<int> f = FireAt("F14", shooter, target, 40, r);
            while (f.MoveNext())
            {
                yield return f.Current;
            }
            List<FireShot> mid = r.shots.Where(s => s.midStep).ToList();
            string b = Bounds(r, out bool leadOk, out bool _);
            Check("F14 target on a ramp: lead in bound; on a stair step along the ground", r.launched > 0 && leadOk && mid.All(s => s.leadOnPath), r.Text + "; shots on a stair step " + mid.Count + "; " + b);
            FireEnd(shooter, target, stairs);
        }

        // ---- F13: an instant weapon's burst at a walking target between floors runs the fired line and hits as on one
        // floor (CE locks its angle too; the old angle went over the target). A bow (one shot, no burst) is measured beside.
        private static IEnumerator<int> FireInstantWalking()
        {
            foreach (string gun in new[] { "Gun_BeamRepeater", "Bow_Great" })
            {
                foreach (Cfg cfg in new[] { Cfg.Same, Cfg.Edge })
                {
                    Out("--- F13 " + gun + " at a walking target, " + cfg);
                    Arena(cfg, 0, out int ks, out int kt);
                    Pawn target = WalkingTarget(On(FX, FZ + 14, kt), On(FX, FZ + 2, kt));
                    Pawn shooter = Colonist(On(FX, FZ, ks), gun);
                    int a0 = FireLine.Adjusted;
                    FireResult r = new FireResult();
                    IEnumerator<int> f = FireAt("F13 " + gun + " " + cfg, shooter, target, 24, r, 1500);
                    while (f.MoveNext())
                    {
                        yield return f.Current;
                    }
                    bool instant = gun == "Gun_BeamRepeater";
                    Check("F13 " + gun + " at a walking target (" + cfg + "): shots go" + (instant && cfg == Cfg.Edge ? ", through the fired line, half on target" : ""), r.launched > 0 && (!instant || cfg != Cfg.Edge || (FireLine.Adjusted > a0 && r.hits * 2 >= r.launched)), r.Text + ", adjusted " + (FireLine.Adjusted - a0));
                    FireEnd(shooter, target);
                }
            }
        }

        // ---- F12: a humanlike target (CE's aim at a person differs: a shooter's aim mode, armour-aware for AI).
        private static IEnumerator<int> FireWalkingHumanlike()
        {
            Out("--- F12 a walking raider on the ground, a colonist on the slab");
            Arena(Cfg.Edge, 0, out int ks, out int kt);
            Pawn raider = Raider(On(FX, FZ + 14, kt));
            held.RemoveAll(h => h.pawn == raider);
            fireMover = raider;
            moveFrom = raider.Position;
            moveTo = On(FX, FZ + 2, kt);
            Pawn shooter = Colonist(On(FX, FZ, ks), "Gun_AssaultRifle");
            FireResult r = new FireResult();
            IEnumerator<int> f = FireAt("F12", shooter, raider, 20, r);
            while (f.MoveNext())
            {
                yield return f.Current;
            }
            string b = Bounds(r, out bool leadOk, out bool lineOk);
            Check("F12 humanlike target: lead and line in bound, none stopped on the slab", r.launched >= 8 && leadOk && lineOk && r.own == 0, r.Text + "; " + b);
            FireEnd(shooter, raider);
        }
    }
}
