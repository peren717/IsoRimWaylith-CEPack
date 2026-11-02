using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRimCEPackZ
{
    // Api.OpenPlacement: how a CE turret would see from a cell before it is built (a placement preview).
    //
    // A query leases one unspawned turret of the def (a pool per map and def), moves it to the cell, gives it the
    // faction, rotation and ammo asked for and runs CE's own code on it: the verb's range and minimum range, the fire arc
    // (CompFireArc), the threat test. Two transpilers let CE read the unspawned turret as if it stood there:
    // VerbProperties.AdjustedRange (its Map) and Verb_LaunchProjectileCE.ShooterPawn (its operator), through
    // QueryContext; the pack's Scope makes distances and points between floors as for a built turret. Dispose puts the
    // turret back as it was. Nested queries never share a turret.
    // TraceFixedCorridor walks ProjectZ's 3D line from the turret's shot height to the target (a claimed target: its aim
    // point) and reports the first full-fillage building (not an open door), solid floor plane or map edge.
    //
    // Cost: the two transpilers land on the first OpenPlacement (PackZ.ApplyLate); afterwards each of their reads costs
    // one static int test while no query is open. An exception inside CE during a query is caught: the call answers
    // Unavailable and logs one warning (per kind).
    internal sealed class PlacementQuery : IPlacementQuery
    {
        // The part can work (its CE members were found at startup, the pack's turret group landed).
        internal static bool Ready;

        private static bool patched;

        private static readonly ConditionalWeakTable<Map, Dictionary<(ThingDef, ThingDef), Stack<Building_TurretGunCE>>> pools =
            new ConditionalWeakTable<Map, Dictionary<(ThingDef, ThingDef), Stack<Building_TurretGunCE>>>();

        // Harness counters.
        public static int Opened, Failures;

        private PlacementSpec spec;
        private Building_TurretGunCE gun;
        private readonly AmmoDef selected, ammunition;
        private readonly IntVec3 position;
        private readonly Rot4 rotation;
        private readonly Faction originalFaction;
        private readonly Stack<Building_TurretGunCE> pool;

        // ------------------------------------------------------------------ CE members (resolved by Prepare, not a type initializer)
        private static AccessTools.FieldRef<Thing, Faction> faction;
        private static MethodInfo adjustedRange, shooterPawn, mapGet, mapHeldGet, operatorGet;

        internal static void Prepare(Harmony h)
        {
            faction = AccessTools.FieldRefAccess<Thing, Faction>("factionInt");
            adjustedRange = PackZ.Method(typeof(VerbProperties), nameof(VerbProperties.AdjustedRange));
            shooterPawn = AccessTools.PropertyGetter(typeof(Verb_LaunchProjectileCE), nameof(Verb_LaunchProjectileCE.ShooterPawn)) ?? throw new MissingMethodException("Verb_LaunchProjectileCE.ShooterPawn");
            mapGet = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Map));
            mapHeldGet = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.MapHeld));
            operatorGet = PackZ.Method(typeof(CE_Utility), nameof(CE_Utility.TryGetTurretOperator));
            if (!PatchProcessor.GetOriginalInstructions(adjustedRange).Any(c => c.Calls(mapGet) || c.Calls(mapHeldGet)))
            {
                throw new InvalidOperationException("VerbProperties.AdjustedRange no longer reads Thing.Map");
            }
            if (!PatchProcessor.GetOriginalInstructions(shooterPawn).Any(c => c.Calls(operatorGet)))
            {
                throw new InvalidOperationException("Verb_LaunchProjectileCE.ShooterPawn no longer calls CE_Utility.TryGetTurretOperator");
            }
        }

        // On the first OpenPlacement.
        private static void Patch(Harmony h)
        {
            PackZ.Transpiler(h, adjustedRange, typeof(PlacementQuery), nameof(MapReads_Transpiler));
            PackZ.Transpiler(h, shooterPawn, typeof(PlacementQuery), nameof(OperatorReads_Transpiler));
        }

        internal static void Disable() => Ready = false;

        // ------------------------------------------------------------------ open and dispose
        internal static GeometryStatus Open(PlacementSpec spec, out IPlacementQuery query)
        {
            query = null;
            if (!Ready || !ApiParts.OnMainThread("OpenPlacement"))
            {
                return GeometryStatus.Unavailable;
            }
            if (!patched)
            {
                patched = true;
                if (!PackZ.ApplyLate("Placement queries", Patch))
                {
                    Ready = false;
                    return GeometryStatus.Unavailable;
                }
            }
            if (spec.Map == null || !spec.Cell.InBounds(spec.Map) || (Layout.Of(spec.Map) is Layout l && l.FrameOf(spec.Cell) == Layout.None))
            {
                return GeometryStatus.InvalidInput;
            }
            if (spec.TurretDef?.thingClass == null || !typeof(Building_TurretGunCE).IsAssignableFrom(spec.TurretDef.thingClass))
            {
                return GeometryStatus.NotApplicable;
            }
            try
            {
                Dictionary<(ThingDef, ThingDef), Stack<Building_TurretGunCE>> byDef = pools.GetValue(spec.Map, _ => new Dictionary<(ThingDef, ThingDef), Stack<Building_TurretGunCE>>());
                (ThingDef, ThingDef) key = (spec.TurretDef, spec.Stuff);
                if (!byDef.TryGetValue(key, out Stack<Building_TurretGunCE> idle))
                {
                    idle = new Stack<Building_TurretGunCE>();
                    byDef.Add(key, idle);
                }
                Building_TurretGunCE gun = idle.Count != 0 ? idle.Pop() : (Building_TurretGunCE)ThingMaker.MakeThing(spec.TurretDef, spec.Stuff);
                if (!(gun.AttackVerb is Verb_LaunchProjectileCE))
                {
                    idle.Push(gun);
                    return GeometryStatus.NotApplicable;
                }
                if (spec.Ammunition != null && !InSet(gun, spec.Ammunition))
                {
                    idle.Push(gun);
                    return GeometryStatus.InvalidInput;
                }
                query = new PlacementQuery(spec, gun, idle);
                Opened++;
                return GeometryStatus.Success;
            }
            catch (Exception e)
            {
                return Failed("OpenPlacement", e);
            }
        }

        private static bool InSet(Building_TurretGunCE gun, ThingDef ammo)
        {
            List<AmmoLink> types = gun.CompAmmo?.Props.ammoSet?.ammoTypes;
            if (!(ammo is AmmoDef) || types == null)
            {
                return false;
            }
            foreach (AmmoLink link in types)
            {
                if (link.ammo == ammo)
                {
                    return true;
                }
            }
            return false;
        }

        // An exception inside CE during a query: one warning per call and kind, the call answers Unavailable.
        private static GeometryStatus Failed(string call, Exception e)
        {
            Failures++;
            PackZ.Once("placement " + call + " " + e.GetType().Name, "Api: " + call + " failed inside Combat Extended (" + PackZ.Reason(e) + "); it answers Unavailable.");
            return GeometryStatus.Unavailable;
        }

        private PlacementQuery(PlacementSpec spec, Building_TurretGunCE gun, Stack<Building_TurretGunCE> pool)
        {
            this.spec = spec;
            this.gun = gun;
            this.pool = pool;
            position = gun.Position;
            rotation = gun.Rotation;
            originalFaction = gun.Faction;
            if (gun.CompAmmo is CompAmmoUser ammo)
            {
                selected = ammo.SelectedAmmo;
                ammunition = ammo.CurrentAmmo;
                AmmoDef choice = spec.Ammunition as AmmoDef ?? ammunition ?? ammo.Props.ammoSet?.ammoTypes?.FirstOrDefault()?.ammo;
                ammo.SelectedAmmo = choice;
                ammo.CurrentAmmo = choice;
            }
            gun.Position = spec.Cell;
            gun.Rotation = spec.Rotation;
            faction(gun) = spec.Operator?.Faction ?? spec.Faction;
            if (gun.CompFireArc is CompFireArc arc && arc.CurrentSpan < 0f)
            {
                arc.CurrentSpan = arc.Props.spanRange.max;
            }
        }

        public void Dispose()
        {
            if (gun == null)
            {
                return;
            }
            if (gun.CompAmmo is CompAmmoUser ammo)
            {
                ammo.SelectedAmmo = selected;
                ammo.CurrentAmmo = ammunition;
            }
            gun.Position = position;
            gun.Rotation = rotation;
            faction(gun) = originalFaction;
            pool.Push(gun);
            gun = null;
            spec = default;
        }

        private bool BadTarget(LocalTargetInfo target) => !target.IsValid || !target.Cell.InBounds(spec.Map) || (target.HasThing && target.Thing.Map != spec.Map);

        // ------------------------------------------------------------------ the queries
        // Each runs with the turret named in QueryContext (its map and operator) and in a Scope (the pack's 3D views).
        public GeometryStatus ReadWeapon(out WeaponGeometry weapon)
        {
            weapon = default;
            if (gun == null)
            {
                return GeometryStatus.InvalidInput;
            }
            QueryContext.State before = QueryContext.Enter(gun, spec.Map, spec.Operator);
            Scope.State scope = Scope.Enter(gun);
            try
            {
                weapon = Weapon();
                return GeometryStatus.Success;
            }
            catch (Exception e)
            {
                weapon = default;
                return Failed("ReadWeapon", e);
            }
            finally
            {
                Scope.Exit(scope);
                QueryContext.Exit(before);
            }
        }

        private WeaponGeometry Weapon()
        {
            Verb_LaunchProjectileCE verb = (Verb_LaunchProjectileCE)gun.AttackVerb;
            bool direct = !gun.def.building.IsMortar && !(verb is Verb_ShootMortarCE) && !verb.Projectile.projectile.flyOverhead;
            return new WeaponGeometry(verb.EffectiveRange, verb.VerbPropsCE.minRangeInCover, verb.Projectile, direct, gun.TryGetComp<CompMannable>() != null, verb.IsIncendiary_Ranged());
        }

        public GeometryStatus CheckRangeAndArc(LocalTargetInfo target, out RangeArcResult result)
        {
            result = default;
            if (gun == null || BadTarget(target))
            {
                return GeometryStatus.InvalidInput;
            }
            QueryContext.State before = QueryContext.Enter(gun, spec.Map, spec.Operator);
            Scope.State scope = Scope.Enter(gun);
            try
            {
                result = RangeAndArc(target);
                return GeometryStatus.Success;
            }
            catch (Exception e)
            {
                result = default;
                return Failed("CheckRangeAndArc", e);
            }
            finally
            {
                Scope.Exit(scope);
                QueryContext.Exit(before);
            }
        }

        private RangeArcResult RangeAndArc(LocalTargetInfo target)
        {
            Verb_LaunchProjectileCE verb = (Verb_LaunchProjectileCE)gun.AttackVerb;
            IntVec3 cell = target.Cell;
            if (target.HasThing && TargetGeometry.Active
                && TargetGeometry.Resolve(target.Thing, new TargetQuery(spec.Map, gun, spec.Cell, verb, verb.Projectile), out TargetAim aim) == GeometryStatus.Success)
            {
                cell = aim.RangeCell;
            }
            bool cross = ProjectZ.Api.HasFloors(spec.Map) && !ProjectZ.Api.SameFloor(spec.Cell, cell, spec.Map);
            float maximum = cross ? ProjectZ.Api.RangeDistance(spec.Cell, cell, spec.Map)
                : Mathf.Sqrt((target.HasThing && cell == target.Cell ? target.Thing.OccupiedRect() : CellRect.SingleCell(cell)).ClosestDistSquaredTo(spec.Cell));
            float minimumDistance = ProjectZ.Api.Distance3D(spec.Cell, cell, spec.Map);
            float minimum = verb.verbProps.EffectiveMinRange(target, gun);
            bool threatOff = target.Thing is IAttackTarget attack && attack.ThreatDisabled(spec.Operator != null ? (IAttackTargetSearcher)spec.Operator : gun);
            return new RangeArcResult(maximum, minimumDistance, minimum, maximum <= verb.EffectiveRange && minimumDistance >= minimum,
                gun.CompFireArc?.WithinFireArc(target) ?? true, threatOff);
        }

        public GeometryStatus TraceFixedCorridor(LocalTargetInfo target, out FixedCorridorResult result)
        {
            result = default;
            GeometryStatus status = ReadWeapon(out WeaponGeometry weapon);
            if (status != GeometryStatus.Success)
            {
                return status;
            }
            if (!weapon.DirectFire)
            {
                return GeometryStatus.NotApplicable;
            }
            if (BadTarget(target))
            {
                return GeometryStatus.InvalidInput;
            }
            QueryContext.State before = QueryContext.Enter(gun, spec.Map, spec.Operator);
            Scope.State scope = Scope.Enter(gun);
            try
            {
                result = Trace(target);
                return GeometryStatus.Success;
            }
            catch (Exception e)
            {
                result = default;
                return Failed("TraceFixedCorridor", e);
            }
            finally
            {
                Scope.Exit(scope);
                QueryContext.Exit(before);
            }
        }

        private static readonly List<ProjectZ.Api.LineStep> steps = new List<ProjectZ.Api.LineStep>();
        private static readonly HashSet<Building> seen = new HashSet<Building>();

        private FixedCorridorResult Trace(LocalTargetInfo target)
        {
            Verb_LaunchProjectileCE verb = (Verb_LaunchProjectileCE)gun.AttackVerb;
            Layout l = Layout.Of(spec.Map);
            int sourceFrame = l?.FrameOf(spec.Cell) ?? 0;
            Vector3 from = gun.TrueCenter();
            from.y = verb.ShotHeight;
            Vector3 to = target.HasThing ? target.Thing.DrawPos : target.Cell.ToVector3Shifted();
            to.y = target.HasThing ? new CollisionVertical(target.Thing).Max : 0f;
            int targetFrame = l?.FrameOf(target.Cell) ?? 0;
            Vector3 a = l != null ? l.ToVirtual(from, sourceFrame) : new Vector3(from.x, from.y * Layout.CellsPerUnit, from.z);
            Vector3 b = l != null ? l.ToVirtual(to, targetFrame) : new Vector3(to.x, to.y * Layout.CellsPerUnit, to.z);
            if (target.HasThing && TargetGeometry.Active
                && TargetGeometry.Resolve(target.Thing, new TargetQuery(spec.Map, gun, spec.Cell, verb, verb.Projectile), out TargetAim aim) == GeometryStatus.Success)
            {
                b = aim.PointVirtual;
            }
            steps.Clear();
            if (ProjectZ.Api.WalkLine3D(a, b, spec.Map, steps, true) == 0)
            {
                return new FixedCorridorResult(FixedObstruction.MapEdge, target.Cell, b);
            }
            Vector3 delta = b - a;
            float closest = float.PositiveInfinity;
            FixedCorridorResult hit = new FixedCorridorResult(FixedObstruction.None, IntVec3.Invalid, default);
            seen.Clear();
            foreach (ProjectZ.Api.LineStep step in steps)
            {
                // a solid plane crossed: where the line meets its height
                if (step.solidPlane != int.MinValue && delta.y != 0f)
                {
                    float t = (step.solidPlane * ProjectZ.Api.StoreyHeight - a.y) / delta.y;
                    if (t >= 0f && t <= 1f && t < closest)
                    {
                        closest = t;
                        hit = new FixedCorridorResult(FixedObstruction.FloorPlane, step.cell, a + delta * t);
                    }
                }
                // every storey the line passes in this column: its full-fillage building, against CE's bounds in that frame
                IntVec3 ground = l?.Ground(step.cell) ?? step.cell;
                int low = l != null ? Mathf.Max(l.min, step.floor + Mathf.FloorToInt(step.lo / ProjectZ.Api.StoreyHeight)) : 0;
                int high = l != null ? Mathf.Min(l.max, step.floor + Mathf.FloorToInt(step.hi / ProjectZ.Api.StoreyHeight)) : 0;
                for (int k = low; k <= high; k++)
                {
                    IntVec3 cell = l?.Cell(ground, k) ?? ground;
                    if (!cell.InBounds(spec.Map))
                    {
                        return new FixedCorridorResult(FixedObstruction.MapEdge, cell, a);
                    }
                    Building building = cell.GetEdifice(spec.Map);
                    if (building == null || building == target.Thing || building == gun || !seen.Add(building) || building.def.Fillage != FillCategory.Full
                        || (building is Building_Door door && door.Open))
                    {
                        continue;
                    }
                    Vector3 src = l != null ? l.FromVirtual(a, k) : new Vector3(a.x, a.y / Layout.CellsPerUnit, a.z);
                    Vector3 dst = l != null ? l.FromVirtual(b, k) : new Vector3(b.x, b.y / Layout.CellsPerUnit, b.z);
                    Ray ray = new Ray(src, dst - src);
                    if (Aim.boundsFor(building).IntersectRay(ray, out float distance) && distance <= (dst - src).magnitude)
                    {
                        float t = distance / (dst - src).magnitude;
                        if (t < closest)
                        {
                            closest = t;
                            hit = new FixedCorridorResult(FixedObstruction.Building, cell, a + delta * t);
                        }
                    }
                }
            }
            return hit;
        }

        // ------------------------------------------------------------------ the transpilers
        // AdjustedRange: Thing.Map / Thing.MapHeld -> QueryContext (the query turret's map).
        public static IEnumerable<CodeInstruction> MapReads_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo mapOf = AccessTools.Method(typeof(QueryContext), nameof(QueryContext.MapOf));
            MethodInfo mapHeldOf = AccessTools.Method(typeof(QueryContext), nameof(QueryContext.MapHeldOf));
            int n = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(mapGet) || ci.Calls(mapHeldGet))
                {
                    ci.operand = ci.Calls(mapGet) ? mapOf : mapHeldOf;
                    ci.opcode = OpCodes.Call;
                    n++;
                }
                yield return ci;
            }
            if (n == 0)
            {
                throw new InvalidOperationException("VerbProperties.AdjustedRange: no Thing.Map read");
            }
        }

        // ShooterPawn: CE_Utility.TryGetTurretOperator -> QueryContext (the query's operator).
        public static IEnumerable<CodeInstruction> OperatorReads_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo mine = AccessTools.Method(typeof(QueryContext), nameof(QueryContext.OperatorOf));
            int n = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(operatorGet))
                {
                    ci.operand = mine;
                    n++;
                }
                yield return ci;
            }
            if (n == 0)
            {
                throw new InvalidOperationException("Verb_LaunchProjectileCE.ShooterPawn: no TryGetTurretOperator call");
            }
        }
    }
}
