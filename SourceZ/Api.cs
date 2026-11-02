using System;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // The pack's public Api (the IsoRimZ part, IsoRimWaylith_CEPackZ.dll) for mods that build on Combat Extended between
    // floors (first user: RimSiege). Docs/Api.md has the rules and examples; this file the calls.
    //
    //  - Placement queries: how a CE turret would see from a cell before it is built (range, fire arc, the fixed corridor).
    //  - Target geometry: another mod's volume and aim point for its things (projectiles, sight, cover and range use them).
    //  - Surface explosions: a rule says whether a blast against a floor slab also damages that slab's terrain.
    //
    // Points and bounds are ProjectZ virtual cells (x, z over the ground rows, y the absolute height in cells); IntVec3
    // values are real map cells (a floor's band). Calls and registrations belong to the game's main thread. Query handles
    // and registration tokens are not save data. Nothing is patched for a part until it is first used, so a game without
    // a user pays nothing; a provider or rule that throws or answers something unusable is switched off with one warning
    // and the game goes on as Combat Extended has it.
    [Flags]
    public enum ExtensionCapabilities { None = 0, PlacementQueries = 1, TargetGeometry = 2, SurfaceExplosions = 4 }

    // Unavailable: the part is off (see Capabilities) or the query failed inside CE (one warning in the log).
    // InvalidInput: the arguments make no sense (no map, a cell off the map or in a void row, a thing on another map).
    // NotApplicable: the part does not apply (not a CE turret, not direct fire, a thing no provider claims).
    public enum GeometryStatus { Unavailable, Success, InvalidInput, NotApplicable }

    public enum FixedObstruction { Unavailable, None, MapEdge, Building, FloorPlane }

    public static class Api
    {
        // Raised when something is added.
        public static int Version => 1;

        // The parts that work in this game (their CE members were found, the pack's groups they build on landed).
        public static ExtensionCapabilities Capabilities =>
            (PlacementQuery.Ready ? ExtensionCapabilities.PlacementQueries : 0) |
            (TargetGeometry.Ready ? ExtensionCapabilities.TargetGeometry : 0) |
            (SurfaceExplosions.Ready ? ExtensionCapabilities.SurfaceExplosions : 0);

        // A query turret for spec (Success: dispose the query when done, it goes back to a pool).
        public static GeometryStatus OpenPlacement(PlacementSpec spec, out IPlacementQuery query) => PlacementQuery.Open(spec, out query);

        // Volume and aim for things of targetType (and its subclasses). A later registration overrides an earlier one for
        // the things it claims. Dispose the token to remove it. Throws ArgumentException (missing argument) and
        // InvalidOperationException (owner and type registered already): caller errors, at the call.
        public static IDisposable RegisterTargetGeometry(string owner, Type targetType, ITargetGeometryProvider provider) =>
            TargetGeometry.Register(owner, targetType, provider);

        // The provider's answer for this thing changed (moved, resized, turned some other way than Thing.Rotation).
        public static void NotifyTargetGeometryChanged(Thing target) => TargetGeometry.Refresh(target);

        // The aim a registered provider gives for a thing (NotApplicable: nobody claims it, the native aim applies).
        public static GeometryStatus ResolveTargetAim(Thing target, TargetQuery query, out TargetAim aim) =>
            TargetGeometry.Resolve(target, query, out aim);

        // A rule asked for every CE blast that met a floor plane. Dispose the token to remove it. Throws as
        // RegisterTargetGeometry for caller errors.
        public static IDisposable RegisterSurfaceExplosionRule(string owner, ISurfaceExplosionRule rule) =>
            SurfaceExplosions.Register(owner, rule);
    }

    // Placement queries ------------------------------------------------------------------------------------------------
    // TurretDef: a Building_TurretGunCE def; Ammunition: one of its ammo set (null: what it has, else the set's first);
    // Faction and Operator: who would own and man it (the operator's faction wins).
    public readonly record struct PlacementSpec(Map Map, ThingDef TurretDef, ThingDef Stuff, ThingDef Ammunition,
        IntVec3 Cell, Rot4 Rotation, Faction Faction, Pawn Operator = null);

    public readonly record struct WeaponGeometry(float EffectiveRange, float MinimumRangeInCover,
        ThingDef Projectile, bool DirectFire, bool Mannable, bool Incendiary);

    // MaximumRangeDistance: CE's range distance to the target (between floors ProjectZ's); MinimumRangeDistance: the 3D
    // distance CE's minimum range is judged by; WithinArc: CE's fire arc (CompFireArc), true without one.
    public readonly record struct RangeArcResult(float MaximumRangeDistance, float MinimumRangeDistance,
        float MinimumRange, bool InRange, bool WithinArc, bool ThreatDisabled);

    // The first fixed thing on the straight line from the turret's shot height to the target: a full-fillage building
    // (not an open door), a solid floor plane (a slab, a roof) or the map edge. Pawns and partial cover are not fixed.
    public readonly record struct FixedCorridorResult(FixedObstruction Obstruction, IntVec3 Cell, Vector3 ContactVirtual)
    {
        public bool Clear => Obstruction == FixedObstruction.None;
    }

    public interface IPlacementQuery : IDisposable
    {
        GeometryStatus ReadWeapon(out WeaponGeometry weapon);
        GeometryStatus CheckRangeAndArc(LocalTargetInfo target, out RangeArcResult result);
        GeometryStatus TraceFixedCorridor(LocalTargetInfo target, out FixedCorridorResult result);
    }

    // Target geometry --------------------------------------------------------------------------------------------------
    // Who asks: the shooter, the cell it fires from (Root), its verb and projectile (null when a turret or CE's AI looks
    // without a verb at hand).
    public readonly record struct TargetQuery(Map Map, Thing Shooter, IntVec3 Root, Verb Verb, ThingDef Projectile);

    // PointVirtual: the point shots aim at; RangeCell: the real cell range is measured to (on the storey of the point).
    public readonly record struct TargetAim(Vector3 PointVirtual, IntVec3 RangeCell);

    public interface ITargetGeometryProvider
    {
        // False leaves the thing on the native path. Both methods belong to the same selected provider: the one whose
        // TryGetVolume claimed the thing is asked TryGetAim. Called on the main thread, often: keep them cheap and free
        // of side effects. An exception, a non-finite or empty volume, or an aim off the map switches the provider off.
        bool TryGetVolume(Thing target, Map map, out Bounds volumeVirtual);
        bool TryGetAim(Thing target, TargetQuery query, out TargetAim aim);
    }

    // Surface explosions -----------------------------------------------------------------------------------------------
    public enum SurfaceSide { Below, Above }

    // Where a projectile met a floor plane: its ground column, the plane (UpperFloor: the floor whose plane it is), the
    // slab cell (SurfaceCell: on UpperFloor if it has a floor there, else the roof's storey below), the cell the blast
    // starts in (IncidentCell: the side the projectile came from) and the contact point and normal.
    public readonly record struct SurfaceContact(Map Map, IntVec3 GroundCell, int UpperFloor,
        IntVec3 SurfaceCell, IntVec3 IncidentCell, Vector3 PointVirtual, Vector3 NormalVirtual,
        SurfaceSide IncidentSide, bool HasFloor);

    public readonly record struct SurfaceExplosionContext(ExplosionCE Explosion, SurfaceContact Contact, ThingDef ProjectileDef);

    public interface ISurfaceExplosionRule
    {
        // Read-only policy: true lets the blast also damage the contact's slab terrain (once, terrain only: no pawns or
        // things on the slab, no fire). Never edit the explosion or apply damage here. An exception switches the rule off.
        bool ShouldDamageTerrain(SurfaceExplosionContext context);
    }

    // The parts' shared rules for misbehaving callers (not public).
    internal static class ApiParts
    {
        // False (and one warning) off the main thread: the call is ignored.
        internal static bool OnMainThread(string call)
        {
            if (UnityData.IsInMainThread)
            {
                return true;
            }
            PackZ.Once("api thread " + call, "Api: " + call + " was called off the main thread and is ignored.");
            return false;
        }

        // A provider or rule failed: one warning per owner and part (the caller switches the registration off).
        internal static void OwnerFault(string part, string owner, string what)
        {
            PackZ.Once("api " + part + " " + owner, part + ": " + owner + " " + what + ". Its registration is switched off; the game goes on as Combat Extended has it.");
        }
    }
}
