using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // Harness, fallback checks (mode fault; fallback audit H1, H2, M1-M6). Run once per fault with -cepackfault=<name>
    // (PackZ.Fault: a CE member renamed, a group failing partway, a target read not found, a ProjectZ part off) and once
    // without one (the real-geometry and third-party cases: throws and slow shots up under a slab, a failing shield mod).
    // Every run ends with the same-floor fights (14, 14b): they must run as Combat Extended has them (the pack changes
    // nothing on one floor), whatever failed. Real orders and real ticks, fixed seeds, one fixture per check (see TestZ).
    public static partial class TestZ
    {
        private const string PackZId = "peren.isorimwaylith.cepack.z", PackId = "peren.isorimwaylith.cepack";

        // every warning or error line of the pack (both parts), startup included
        private static readonly List<string> packLines = new List<string>();

        private static string FaultName => GenCommandLine.TryGetCommandLineArg("cepackfault", out string f) ? f : "";

        private static IEnumerable<IEnumerator<int>> FaultChecks()
        {
            string f = FaultName;
            Out("fault run: " + (f == "" ? "(none: real cases)" : f) + "; pack healthy " + PackZ.Healthy + ", flight " + PackZ.FlightOn + ", parts off " + PackZ.PartsOff
                + ", failed " + string.Join(",", PackZ.Failed) + "; problems: " + string.Join(" | ", PackZ.Problems) + "; skipped " + string.Join(",", PackZ.Skipped));
            Out("pack log lines so far: " + packLines.Count + (packLines.Count > 0 ? "\n    " + string.Join("\n    ", packLines) : ""));
            switch (f)
            {
                case "":
                    yield return SightToHigherFloor();
                    yield return ThrowUnderSlab();
                    yield return SlowShotUnderSlab();
                    yield return FailingShieldMod();
                    break;
                case "frames":
                    StartupCheck("H2a Frames' CE members missing", !PackZ.FlightOn && !PackZ.Healthy && PackZ.Failed.Contains("Flight")
                        && new[] { "Areas", "Shields", "Other mods' shields", "CIWS", "Turrets", "AI", "Flight extras" }.All(PackZ.Skipped.Contains)
                        && NoPatches(PackZId, Method(typeof(ProjectileCE), "Tick"), Method(typeof(ProjectileCE), "CheckIntercept"), Getter(typeof(ExplosionCE), "ExplosionCellsToHit")), 1);
                    yield return CrossRefused("H2a");
                    break;
                case "aim":
                    StartupCheck("H2b Aim's CE member missing (the refusal lives in its own class)", !PackZ.Healthy && PackZ.Failed.Contains("Aim") && PackZ.Failed.Contains("Sight") && PackZ.Failed.Contains("Throws")
                        && HasPatch(PackZId, CanHitTargetFrom3()) && NoPatches(PackZId, Method(typeof(Verb_LaunchProjectileCE), "ShiftTarget", 3)), 1);
                    yield return CrossRefused("H2b");
                    break;
                case "turrets":
                    StartupCheck("H2c Turrets' CE member missing: the group fails at startup, no turret patch left", PackZ.Healthy && PackZ.Problems.Any(p => p.StartsWith("Turrets ("))
                        && NoPatches(PackZId, Method(typeof(Building_TurretGunCE), "OrderAttack", 1), Method(typeof(Building_TurretGunCE), "TryFindNewTarget", 0)), 1);
                    yield return SameFloorMortar();
                    break;
                case "shields":
                    StartupCheck("H2d Shields' CE member missing: no shield patch (CE's shields as CE has them, on every map)", PackZ.Healthy && PackZ.Problems.Any(p => p.StartsWith("Shields ("))
                        && NoPatches(PackZId, Method(typeof(ProjectileCE), "CheckIntercept")), 1);
                    break;
                case "draw":
                    StartupCheck("H2e drawing: a CE member missing stops the drawing at startup (CE draws as usual), no drawing patch",
                        NoPatches(PackId, Method(typeof(ProjectileCE), "DrawAt"), Method(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings))) && packLines.Count(l => l.Contains("[IsoRim CE Pack] Setup failed")) == 1, 1);
                    break;
                case "flightgroup":
                    StartupCheck("M2 key group failing partway: its applied patches undone, shots between floors refused", !PackZ.FlightOn && !PackZ.Healthy
                        && PackZ.Problems.Any(p => p.StartsWith("Flight (") && p.Contains("2 patches undone")) && NoPatches(PackZId, Method(typeof(ProjectileCE), "Tick")), 1);
                    yield return CrossRefused("M2 key");
                    break;
                case "areasgroup":
                    StartupCheck("M2 group failing partway: its applied patches undone (CE's own blasts), the rest on", PackZ.Healthy && PackZ.FlightOn
                        && PackZ.Problems.Any(p => p.StartsWith("Areas (") && p.Contains("4 patches undone"))
                        && NoPatches(PackZId, Getter(typeof(ExplosionCE), "ExplosionCellsToHit"), Method(typeof(ExplosionCE), "GetDamageAmountAtCE"), Method(typeof(ExplosionCE), "GetArmorPenetrationAtCE"), Method(typeof(ProjectileCE), "Impact"))
                        && HasPatch(PackZId, Method(typeof(ProjectileCE), "CheckIntercept")), 1);
                    break;
                case "aimswap":
                    StartupCheck("M3 an aim method without a target read found: the aim group fails (undone), shots between floors refused", !PackZ.Healthy && PackZ.Failed.Contains("Aim")
                        && PackZ.Problems.Any(p => p.StartsWith("Aim (") && p.Contains("ShiftTarget")) && NoPatches(PackZId, Method(typeof(Verb_LaunchProjectileCE), "ShiftTarget", 3), Method(typeof(Verb_LaunchProjectileCE), "ShiftVecReportFor", 2)), 1);
                    yield return CrossRefused("M3");
                    break;
                case "scoperead":
                    {
                        List<string> unread = PackZ.OnceKeys.Where(k => k.StartsWith("scope:")).ToList();
                        int lines = packLines.Count(l => l.Contains("could not be read"));
                        Check("M4 methods that can't be read: one line per method", unread.Count > 0 && unread.All(k => k.Contains("CompFireSelection")) && lines == unread.Count && unread.Distinct().Count() == unread.Count,
                            unread.Count + " methods (" + string.Join(", ", unread) + "), " + lines + " lines");
                        Check("M4 a type of CE's AI with no target reads found fails the AI group (checked per type), the rest on", PackZ.Healthy && PackZ.Problems.Any(p => p.StartsWith("AI (") && p.Contains("CompFireSelection")) && Turrets.On && !Ai.On,
                            "problems: " + string.Join(" | ", PackZ.Problems) + ", turrets " + Turrets.On + ", AI " + Ai.On);
                        Check("M4 log lines: the per-method lines and one summary line", packLines.Count == lines + 1, packLines.Count + " pack lines");
                    }
                    break;
                case "mincollision":
                    // CE's target-centre read for the minimum collision distance not found (a CE update): only that non-key
                    // group fails (the startup summary line), nothing of it is left on TryCastShot, shots between floors go on
                    StartupCheck("M7 the minimum collision distance read not found: only that group fails (undone), shots between floors on", PackZ.Healthy && PackZ.FlightOn
                        && !PackZ.Failed.Any() && PackZ.Problems.Count == 1 && PackZ.Problems[0].StartsWith("Min collision distance (")
                        && NoPatches(PackZId, Method(typeof(Verb_LaunchProjectileCE), "TryCastShot", 0)), 1);
                    yield return MinCollisionShot("M7b", false);
                    break;
                case "lightcell":
                    // CE's light and roof reads of the target cell not found (a CE update): only that non-key group fails (the
                    // startup summary line), its transpiler is not on ShiftVecReportFor, shots between floors go on
                    StartupCheck("M8 the light and roof reads of the target cell not found: only that group fails, shots between floors on", PackZ.Healthy && PackZ.FlightOn
                        && !PackZ.Failed.Any() && PackZ.Problems.Count == 1 && PackZ.Problems[0].StartsWith("Light and weather at the target cell (")
                        && !HasTranspiler(Method(typeof(Verb_LaunchProjectileCE), "ShiftVecReportFor", 2), nameof(Aim.LightCell_Transpiler)), 1);
                    yield return LightCellShot("M8b", false);
                    break;
                case "partoff":
                    StartupCheck("M6 a ProjectZ part the pack builds on is off: shots between floors refused", PackZ.PartsOff && !PackZ.Healthy && !ProjectZ.Api.CombatPartOn(ProjectZ.CombatPart.TargetPicking), 1);
                    yield return CrossRefused("M6");
                    break;
                default:
                    Check("fault name known", false, f);
                    break;
            }
            yield return Fight();
            yield return FightBlast();
            yield return FaultLogsEnd();
        }

        // ---- startup state of a fault run, with the number of pack log lines it should have left (one: the summary)
        private static void StartupCheck(string name, bool ok, int lines)
        {
            Check(name, ok, "healthy " + PackZ.Healthy + ", flight " + PackZ.FlightOn + ", failed " + string.Join(",", PackZ.Failed) + ", problems: " + string.Join(" | ", PackZ.Problems) + ", skipped " + string.Join(",", PackZ.Skipped));
            Check(name + ": one log line", packLines.Count == lines, packLines.Count + " pack lines");
        }

        // At the end: no pack line more than at startup (nothing failed at run time, no repeated lines).
        private static IEnumerator<int> FaultLogsEnd()
        {
            yield return 1;
            Out("pack log lines at the end: " + packLines.Count + ", once keys " + string.Join(",", PackZ.OnceKeys));
            Check("no error or warning other than the pack's own fallback lines during the checks", errors == 0, errors + " other log errors or warnings");
        }

        private static MethodBase Method(Type t, string name, int args = -1) => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == name && (args < 0 || m.GetParameters().Length == args));

        private static MethodBase Getter(Type t, string name) => AccessTools.PropertyGetter(t, name);

        private static MethodBase CanHitTargetFrom3() => Method(typeof(Verb_LaunchProjectileCE), "CanHitTargetFrom", 3);

        private static bool HasTranspiler(MethodBase m, string name)
        {
            Patches p = m != null ? Harmony.GetPatchInfo(m) : null;
            return p != null && p.Transpilers.Any(t => t.PatchMethod.Name == name);
        }

        private static bool HasPatch(string owner, MethodBase m)
        {
            Patches p = m != null ? Harmony.GetPatchInfo(m) : null;
            return p != null && p.Owners.Contains(owner);
        }

        private static bool NoPatches(string owner, params MethodBase[] ms)
        {
            bool ok = true;
            foreach (MethodBase m in ms)
            {
                if (m == null)
                {
                    Out("  (method not found for the patch check)");
                    ok = false;
                    continue;
                }
                bool has = HasPatch(owner, m);
                Out("  patches of " + owner + " on " + m.DeclaringType?.Name + "." + m.Name + ": " + (has ? "YES" : "none"));
                ok &= !has;
            }
            return ok;
        }

        // ---- a shot between floors is refused (CE's "no line of sight"), none fired: the balcony fixture of check 2
        private static IEnumerator<int> CrossRefused(string name)
        {
            Out("--- " + name + " a shot between floors is refused");
            if (L == null || !L.Has(1))
            {
                Check(name + " refused", false, "needs a map with floors");
                yield break;
            }
            Wipe(5, 5, 20, 32);
            Slab(10, 10, 14, 14);
            Pawn target = Target(On(12, 27, 0));
            Pawn shooter = Colonist(On(12, 14, 1), "Gun_AssaultRifle");
            yield return 30;
            string can = CanHit(shooter, target);
            int t0 = Find.TickManager.TicksGame;
            Order(shooter, target);
            yield return 300;
            int launched = By(shooter, Launches, t0).Count;
            Check(name + ": a shot from floor 1 at the ground is refused, none fired", can.StartsWith("no") && launched == 0, "can hit " + can + ", launched " + launched + ", gate refusals " + Gate.Refused);
            Gone(shooter, target);
        }

        // ---- turrets fault: a manned mortar ordered at a ground cell on its own floor fires as CE has it
        private static IEnumerator<int> SameFloorMortar()
        {
            Out("--- H2c a turret order on one floor (the turret group failed)");
            Wipe(20, 150, 70, 230);
            Building_TurretGunCE mortar = (Building_TurretGunCE)Build("Turret_Mortar", On(45, 155, 0));
            Pawn gunner = Gunner(On(45, 153, 0), mortar);
            int t0 = Find.TickManager.TicksGame, e0 = errors;
            IEnumerator<int> fire = FireShells(mortar, gunner, new LocalTargetInfo(On(45, 210, 0)), 2, t0);
            while (fire.MoveNext())
            {
                yield return fire.Current;
            }
            int shells = Of(gunner, mortar, Launches, t0).Count(s => s.overhead);
            Check("H2c a mortar order on one floor fires (no error from a turret patch)", shells > 0 && errors == e0, "shells " + shells + ", errors " + (errors - e0));
            Gone(gunner, mortar);
        }

        // ---- H1: a frag grenade thrown up to floor 1 from under a slab. Three fixtures: the slab over the thrower runs to
        // the target's floor (gap 0) or leaves an open gap of 1 or 2 cells before it. No arc that gets there may rise into
        // the slab over the thrower: the throw is a lob through the gap or refused; it never bursts in the thrower's storey.
        private static IEnumerator<int> ThrowUnderSlab()
        {
            Out("--- H1 grenade thrown up from under a slab");
            if (L == null || !L.Has(1))
            {
                Check("H1", false, "needs a map with floors");
                yield break;
            }
            int refused0 = Throws.Refused, high0 = Throws.HighLobs, lobs0 = Throws.Lobs;
            bool anyRefused = false;
            // CE's own hand grenades shoot (Verb_ShootCEOneUse: Aim's arcs, M1); CE's throw verb (Verb_ThrowGrenade, its
            // FindAngle: Throws) is other mods' grenades. The frag grenade is thrown with it for these fixtures, as such a
            // mod's grenade would be (restored after).
            VerbProperties vp = DefDatabase<ThingDef>.GetNamed("Weapon_GrenadeFrag").Verbs[0];
            Type verbWas = vp.verbClass;
            ProjectileProperties pp = DefDatabase<ThingDef>.GetNamed("Weapon_GrenadeFrag").Verbs[0].defaultProjectile.projectile;
            float speedWas = pp.speed;
            vp.verbClass = typeof(Verb_ThrowGrenade);
            throwNoise = true;   // (CE warns about the frag grenade's trajectory worker when thrown: the test's own swap)
            try
            {
            // (d: cells to the target; gap: open cells between the slab over the thrower and the target's floor; arm: the
            // thrower has lost an arm, a weaker throw: only steep arcs reach a farther target, near the end of its reach)
            // (weak: a softer grenade, its projectile speed 9 instead of 12 for that fixture, as another mod's grenade:
            // near the end of its reach only steep arcs get there, and those rise into the slab before the gap)
            var fixtures = new (int d, int gap, bool arm)[] { (7, 0, false), (7, 1, false), (7, 2, false), (9, 2, true) };
            for (int fi = 0; fi < fixtures.Length; fi++)
            {
                (int d, int gap, bool arm) = fixtures[fi];
                int gx = 140 + fi * 10, gz = 20;
                Wipe(gx - 4, gz - 4, gx + 4, gz + d + 5);
                Slab(gx - 3, gz - 3, gx + 3, gz + d - 2 - gap);   // over the thrower, up to the gap
                Slab(gx - 3, gz + d - 1, gx + 3, gz + d + 3);     // the target's floor
                Pawn thrower = Colonist(On(gx, gz, 0), "Weapon_GrenadeFrag");
                IntVec3 target = On(gx, gz + d, 1);
                yield return 30;
                pp.speed = arm ? 9f : speedWas;
                Verb_LaunchProjectileCE tv = VerbOf(thrower) as Verb_LaunchProjectileCE;
                float manip = thrower.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation);
                float tvv = tv != null ? Traverse.Create(tv).Property("ShotSpeed").GetValue<float>() * manip / 60f : 0f, tg = tv != null ? tv.projectilePropsCE.GravityPerHeight / 3600f : 0f;
                Out("  fixture " + fi + ": d " + d + ", gap " + gap + ", weak " + arm + ", throw speed " + tvv.ToString("0.000") + " per tick, level reach " + (tg > 0f ? (tvv * tvv / tg).ToString("0.0") : "-") + " cells, range " + tv?.EffectiveRange.ToString("0.0"));
                int hurt0 = Hurt(thrower);
                string can = CanHit(thrower, target);
                int t0 = Find.TickManager.TicksGame, r0 = Throws.Refused;
                Aim.LobDebug = Out;
                Order(thrower, target);
                yield return 400;
                Aim.LobDebug = null;
                pp.speed = speedWas;
                List<Shot> launched = By(thrower, Launches, t0), hits = By(thrower, Impacts, t0);
                bool refused = Throws.Refused > r0;
                anyRefused |= refused;
                // (the grenade itself: the first launch; its fragments fly on from where it burst)
                Shot nade = launched.FirstOrDefault();
                Shot burst = nade != null ? hits.FirstOrDefault(s => s.id == nade.id) : null;
                bool ownStorey = burst != null && burst.frame == 0 && ((burst.ground - L.Ground(thrower.Position)).LengthHorizontal <= 5f || burst.height >= CollisionVertical.WallCollisionHeight - 0.05f && burst.ground.z <= gz + d - 2 - gap);   // (or under the slab: its underside)
                bool landed = burst != null && burst.frame == 1 && (burst.ground - L.Ground(target)).LengthHorizontal <= 3f;
                Out("  fixture " + fi + ": can hit " + can + ", launched " + launched.Count + ", refused " + refused + ", grenade " + (burst != null ? "f" + burst.frame + " " + burst.ground + " h" + burst.height.ToString("0.0") : "-") + "; flight " + FlightOf(thrower, t0, out _, out _) + "; thrower hurt " + hurt0 + " -> " + Hurt(thrower));
                Check("H1 fixture " + fi + " (d " + d + ", gap " + gap + (arm ? ", weak throw" : "") + "): no burst in the thrower's storey (a lob onto floor 1, or no throw)", !ownStorey && Hurt(thrower) == hurt0 && (!refused || launched.Count == 0),
                    "can hit " + can + ", launched " + launched.Count + ", refused " + refused + ", landed on floor 1 " + landed + ", in the thrower's storey " + ownStorey);
                Gone(thrower);
            }
            }
            finally
            {
                vp.verbClass = verbWas;
                pp.speed = speedWas;
                throwNoise = false;
            }
            Out("  throws: refused " + (Throws.Refused - refused0) + ", lobs " + (Throws.Lobs - lobs0) + ", full-speed high arcs " + (Throws.HighLobs - high0));
            Check("H1 the refusal path was met (a fixture with no arc past the slab)", anyRefused && PackZ.OnceKeys.Count(k => k == "throw") == 1 && packLines.Count(l => l.Contains("A throw up")) == 1,
                "refused " + (Throws.Refused - refused0) + ", lines " + packLines.Count(l => l.Contains("A throw up")));
        }

        // ---- M1: CE's grenade launcher (a slow direct-fire shot) from under a slab at a cell on floor 1, the same three
        // fixtures: a lob through the gap, or no shot (no line: Sight's arc rule; or the shot not launched); never a round
        // into the slab over the shooter.
        private static IEnumerator<int> SlowShotUnderSlab()
        {
            Out("--- M1 grenade launcher up from under a slab");
            int sight0 = Aim.SightRefused, arcs0 = Aim.ArcsRefused, launch0 = Aim.LaunchesRefused;
            // (weak: the launcher's round slowed for that fixture (a fraction of its speed), as a weaker launcher mod's: near the
            // end of its reach only steep arcs get there, and those rise into the slab before the gap)
            var shots = new (int gap, float weak)[] { (0, 1f), (1, 1f), (2, 1f), (3, 1f), (2, 0.4f), (2, 0.25f), (2, 0.18f) };
            for (int fi = 0; fi < shots.Length; fi++)
            {
                (int gap, float speedFactor) = shots[fi];
                bool weak = speedFactor < 1f;
                int gx = fi < 5 ? 196 + fi * 11 : 196 + (fi - 5) * 11, gz = fi < 5 ? 20 : 45;
                Wipe(gx - 5, gz - 5, gx + 5, gz + 16);
                Slab(gx - 3, gz - 3, gx + 3, gz + 8 - gap);
                Slab(gx - 3, gz + 9, gx + 3, gz + 13);
                Pawn grenadier = Colonist(On(gx, gz, 0), "CE_GrenadeLauncher");
                IntVec3 target = On(gx, gz + 10, 1);
                yield return 30;
                // (the round the verb fires: CE's Projectile, the loaded ammo's with the ammo system off too)
                ProjectileProperties lp = ((Verb_LaunchProjectileCE)VerbOf(grenadier)).Projectile.projectile;
                float lpWas = lp.speed;
                lp.speed = lpWas * speedFactor;
                Out("  gap " + gap + (weak ? " (weak)" : "") + ": round " + ((Verb_LaunchProjectileCE)VerbOf(grenadier)).Projectile.defName + " speed " + lp.speed.ToString("0.0"));
                string can = CanHit(grenadier, target);
                int t0 = Find.TickManager.TicksGame, hurt0 = Hurt(grenadier);
                Order(grenadier, target);
                yield return 400;
                lp.speed = lpWas;
                List<Shot> launched = By(grenadier, Launches, t0), hits = By(grenadier, Impacts, t0);
                // (the rounds themselves, not their fragments)
                HashSet<int> rounds = new HashSet<int>(launched.Where(s => !s.def.StartsWith("Fragment")).Select(s => s.id));
                List<Shot> bursts = hits.Where(s => rounds.Contains(s.id)).ToList();
                bool ownStorey = bursts.Any(s => s.frame == 0 && (s.ground - L.Ground(grenadier.Position)).LengthHorizontal <= 5f);
                // (under the slab: a burst in the ground storey at CE's roof height or above, the slab's underside)
                bool underSlab = bursts.Any(s => s.frame == 0 && s.height >= CollisionVertical.WallCollisionHeight - 0.05f && s.ground.z <= gz + 8 - gap);   // (the slab over the shooter, up to its last cell before the gap; a short round under the target floor's front edge is CE's spread)
                bool landed = bursts.Any(s => s.frame == 1 && (s.ground - L.Ground(target)).LengthHorizontal <= 3f);
                Out("  gap " + gap + (weak ? " (weak)" : "") + ": can hit " + can + ", rounds " + rounds.Count + ", bursts " + Hits(bursts) + "; flight " + FlightOf(grenadier, t0, out _, out _) + "; arcs refused at a roof edge so far " + Aim.RoofEdges);
                Check("M1 gap " + gap + (weak ? " weak" : "") + ": no round into the slab over the shooter or under its edge (a lob onto floor 1, or no shot)", !ownStorey && !underSlab && (gap < 3 || weak || rounds.Count > 0),   // (gap 3: a lob that fits is fired; CE's spread may still miss)
                    "can hit " + can + ", rounds " + rounds.Count + ", landed on floor 1 " + landed + ", near the shooter in its storey " + ownStorey + ", under the slab " + underSlab + ", shooter injuries " + hurt0 + " -> " + Hurt(grenadier));
                Gone(grenadier);
            }
            int refusals = Aim.SightRefused - sight0 + Aim.ArcsRefused - arcs0;
            Out("  slow shots refused: sight " + (Aim.SightRefused - sight0) + ", aim " + (Aim.ArcsRefused - arcs0) + ", launches " + (Aim.LaunchesRefused - launch0));
            Check("M1 the refusal path was met (a fixture with no arc past the slab)", refusals > 0, refusals + " refusals");
        }

        // ---- M5: another mod's shield whose callbacks fail: its zone callback on a projectile (not a pawn), then its step
        // callback when offered a projectile from another floor; its zones listed without the centre first. The shots fly
        // on (that shield stays on its own floor), one log line per failure, no error from a projectile's tick.
        private static IEnumerator<int> FailingShieldMod()
        {
            Out("--- M5 another mod's shield with failing callbacks");
            if (L == null || !L.Has(1))
            {
                Check("M5", false, "needs a map with floors");
                yield break;
            }
            foreach (int part in new[] { 1, 2 })
            {
                int gx = 150 + part * 30;
                Wipe(gx - 10, 128, gx + 10, 160);
                Slab(gx - 4, 129, gx + 4, 135);
                Pawn target = Target(On(gx, 142, 0));
                Dome dome = AddDome(On(gx, 142, 0), 6);
                Pawn shooter = Colonist(On(gx, 135, 1), "Gun_AssaultRifle");
                domeFault = part;
                yield return 30;
                Out("  part " + part + ": can hit " + CanHit(shooter, target));
                int t0 = Find.TickManager.TicksGame, e0 = errors, cb0 = Blockers.CallbackErrors, off0 = Blockers.Offers, moved0 = Blockers.CentresMoved;
                Order(shooter, target);
                yield return 400;
                List<(IntVec3 ground, int floor, float radius)> zs = Blockers.ZonesRead;
                int launched = By(shooter, Launches, t0).Count;
                Out("  part " + part + ": launched " + launched + ", callback errors " + (Blockers.CallbackErrors - cb0) + ", offers " + (Blockers.Offers - off0) + ", centres moved " + (Blockers.CentresMoved - moved0)
                    + ", zones read " + string.Join(", ", zs.Select(z => z.ground + " f" + z.floor + " r" + z.radius.ToString("0.0"))) + ", other errors " + (errors - e0));
                if (part == 1)
                {
                    Check("M5 a shield mod's zone callback failing on a projectile: shots fly on, one log line, no tick errors", launched > 0 && Blockers.CallbackErrors > cb0 && errors == e0
                        && PackZ.OnceKeys.Count(k => k == "blocker:zones") == 1 && packLines.Count(l => l.Contains("shield zones failed")) == 1,
                        "launched " + launched + ", callback errors " + (Blockers.CallbackErrors - cb0) + ", other errors " + (errors - e0));
                }
                else
                {
                    Check("M5 a shield mod's step callback failing when offered a shot from another floor: shots fly on, one log line, no tick errors", launched > 0 && Blockers.Offers > off0 && errors == e0
                        && PackZ.OnceKeys.Count(k => k == "blocker:offer") == 1 && packLines.Count(l => l.Contains("shield failed on a projectile")) == 1,
                        "launched " + launched + ", offers " + (Blockers.Offers - off0) + ", other errors " + (errors - e0));
                    Check("M5 zone cells listed without the centre first: the dome is centred on the zone's middle", Blockers.CentresMoved > moved0 && zs.Any(z => z.ground == L.Ground(dome.cell) && z.floor == 0 && Mathf.Abs(z.radius - 6f) < 0.6f),
                        "centres moved " + (Blockers.CentresMoved - moved0) + ", zones " + string.Join(", ", zs.Select(z => z.ground + " f" + z.floor + " r" + z.radius.ToString("0.0"))) + " (dome " + L.Ground(dome.cell) + ")");
                }
                domeFault = 0;
                Gone(shooter, target);
                domes.Remove(dome);
            }
        }

        // ---- sight up to a higher floor, a pawn and a cell target: a line reaches a target on floor 1 only where it rises
        // past floor 1's level over an open cell. (a) the shooter on the ground outside the slab's edge, the target at the
        // edge: can hit. (b) the shooter under the slab beside an opening, the target past it: can hit (up through the
        // opening). (c) the shooter under a solid slab, the target on it in the middle: can't (the line would meet the
        // slab's underside, also a cell target's line that reaches the floor level exactly at the target). Checked with
        // the CE verb (the pack's sight walk) and ProjectZ's own sight for the game's verbs (CanSeeBetweenFloors), and
        // ProjectZ's line at a cell's floor level (LineClear3D, height 0) under the solid slab.
        private static IEnumerator<int> SightToHigherFloor()
        {
            Out("--- sight up to floor 1: slab edge, opening, solid slab (pawn and cell targets)");
            if (L == null || !L.Has(1))
            {
                Check("sight up", false, "needs a map with floors");
                yield break;
            }
            int gz = 45;
            // (a) edge: slab z gz..gz+8, target at the edge (gz), shooter on the ground 8 cells out
            int xa = 70;
            Wipe(xa - 4, gz - 12, xa + 4, gz + 10);
            Slab(xa - 3, gz, xa + 3, gz + 8);
            Pawn ta = Target(On(xa, gz, 1));
            IntVec3 ca = On(xa, gz, 1);   // (the cell targets: the pawn's own cell, the line to it passes no pawn)
            Pawn sa = Colonist(On(xa, gz - 8, 0), "Gun_AssaultRifle");
            // (b) opening: a real stairwell (ProjectZ opens the floor over its ramp) in a slab z gz..gz+15, the shooter
            // under the slab south of it, the target on floor 1 north of it (as check 4)
            int xb = 85;
            Wipe(xb - 5, gz - 4, xb + 5, gz + 17);
            Slab(xb - 4, gz, xb + 4, gz + 15);
            Thing stairs = Build("PZ_Stairs", On(xb, gz + 7, 0), Rot4.South);
            Pawn tb = Target(On(xb, gz + 10, 1));
            IntVec3 cb = On(xb, gz + 10, 1);
            Pawn sb = Colonist(On(xb, gz + 3, 0), "Gun_AssaultRifle");
            // (c) solid: slab z gz..gz+12, shooter under it at gz+2, target on it at gz+8
            int xc = 100;
            Wipe(xc - 4, gz - 4, xc + 4, gz + 14);
            Slab(xc - 3, gz, xc + 3, gz + 12);
            Pawn tc = Target(On(xc, gz + 8, 1));
            IntVec3 cc = On(xc, gz + 8, 1);
            Pawn sc = Colonist(On(xc, gz + 2, 0), "Gun_AssaultRifle");
            yield return 30;
            foreach ((string name, Pawn s, Pawn t, IntVec3 c, bool expect) in new[] { ("a edge", sa, ta, ca, true), ("b opening", sb, tb, cb, true), ("c solid slab", sc, tc, cc, false) })
            {
                Sight.Debug = Out;
                string pawnCE = CanHit(s, t), cellCE = CanHit(s, c);
                Sight.Debug = null;
                List<ProjectZ.Api.LineStep> st = new List<ProjectZ.Api.LineStep>();
                IntVec3 gs0 = L.Ground(s.Position), gt0 = L.Ground(t.Position);
                int n0 = ProjectZ.Api.WalkLine3D(new Vector3(gs0.x + 0.5f, 1.1f, gs0.z + 0.5f), new Vector3(gt0.x + 0.5f, ProjectZ.Api.StoreyHeight + 1.1f, gt0.z + 0.5f), Map, st, false);
                Out("    walk " + string.Join(" ", st.Take(n0).Select(x => L.Ground(x.cell).z + "f" + x.floor + "[" + x.lo.ToString("0.00") + "," + x.hi.ToString("0.00") + "]" + (x.solidPlane != int.MinValue ? "S" + x.solidPlane : "") + (ProjectZ.Api.PlaneSolid(L.Ground(x.cell), 1, Map) ? "#" : "o"))));
                bool pawnPZ = ProjectZ.Api.CanSeeBetweenFloors(s.Position, t.Position, Map), cellPZ = ProjectZ.Api.CanSeeBetweenFloors(s.Position, c, Map);
                bool cellFloor = ProjectZ.Api.LineClear3D(s.Position, 1.1f, c, 0f, Map);
                Out("  " + name + ": CE verb pawn " + pawnCE + ", cell " + cellCE + "; ProjectZ sight pawn " + pawnPZ + ", cell " + cellPZ + "; ProjectZ line to the cell's floor level " + cellFloor);
                bool ok = (pawnCE == "yes") == expect && (cellCE == "yes") == expect && pawnPZ == expect && cellPZ == expect && (expect || !cellFloor);
                Check("sight up " + name + ": " + (expect ? "can" : "can't") + " hit, pawn and cell, CE verb and ProjectZ", ok,
                    "CE pawn " + pawnCE + ", cell " + cellCE + "; ProjectZ pawn " + pawnPZ + ", cell " + cellPZ + ", line at the floor level " + cellFloor);
            }
            Gone(sa, ta, sb, tb, sc, tc, stairs);
        }

        private static void FaultCounters()
        {
            Out("fallback counters: throws refused " + Throws.Refused + ", slow shots refused (sight " + Aim.SightRefused + ", aim " + Aim.ArcsRefused + ", launches " + Aim.LaunchesRefused
                + "), shield mod callback errors " + Blockers.CallbackErrors + ", zone centres moved " + Blockers.CentresMoved + ", gate refusals " + Gate.Refused + ", once keys " + string.Join(",", PackZ.OnceKeys));
        }

        // The test shield mod's failure (M5): 1 = its zone callback fails for anything but a pawn; 2 = its step callback
        // fails while the pack offers it a projectile from another floor, and its zones list the centre last.
        private static int domeFault;

        private static bool throwNoise;
    }
}
