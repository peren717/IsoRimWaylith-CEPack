# IsoRim Waylith - Combat Extended Pack: Api notes

The pack's IsoRimZ part (`1.7/IsoRimZ/Assemblies/IsoRimWaylith_CEPackZ.dll`, loaded only with IsoRimZ) has a small public
Api for mods that build on Combat Extended between floors. Every call is declared, with a short comment, in
[`SourceZ/Api.cs`](../SourceZ/Api.cs). This file holds the rules around the calls, an example per part and the inventory
of patches.

## Adding to the Api

Rules for anyone adding Api to this pack. Tests and the PR flow: [Testing.md](Testing.md).

**When to add**

1. Propose first: in a PR or issue, say what is needed, who uses it and roughly how it is called. Write code once both sides agree.
2. Generic mechanisms only. A mod's own logic stays in that mod; nothing here is written for one mod alone.
3. If the existing Api can do it, don't add more.
4. If it needs new Api in IsoRim or IsoRimZ, ask for it there. Don't work around them from here.

**Shape**

5. Add, don't change: released types, methods and meanings stay, and old callers keep compiling and running. If something really must change, keep the old one and mark it obsolete.
6. Every addition raises `Api.Version`, and the docs say which version added it. Callers check `Api.Version` and `Capabilities`.
7. Don't claim a version number in a PR: it is set in order at merge.

**No effect when unused**

8. Nothing registered: no patches applied, or the hook returns on its first line.
9. Hot paths (every frame, every projectile, every explosion cell) allocate nothing and use no closures.
10. Nothing registered: nothing is written to saves.

**Failure stays with the caller**

11. A callback that throws or returns something unusable (NaN, an empty volume, a cell off the map): one warning, that registration is switched off, and the game goes on CE's own path.
12. Throw only for the caller's own mistakes at registration, such as a null argument or a duplicate owner.
13. Check every CE member you use at startup. If a CE update no longer matches, that part goes off; shooting must not break.
14. Save data is written only while a rule is live, and a save loads fine with the rule or the pack gone.
15. Main thread only. A call from another thread is ignored, with one warning.

**What a PR brings**

16. A section in this file: purpose, an example, limits, the patches it uses.
17. A test mode with a fake caller in real fights, including deliberate failures.
18. The full suite results, and a cost comparison with `main`: with nothing registered, within noise.

## The parts

The Api has three parts:

| Part | Calls | What it is for |
|---|---|---|
| Placement queries | `OpenPlacement`, `IPlacementQuery` | How a CE turret would see from a cell before it is built (range, fire arc, the fixed corridor). |
| Target geometry | `RegisterTargetGeometry`, `NotifyTargetGeometryChanged`, `ResolveTargetAim` | Your own volume and aim point for your things (a siege engine, a tall vehicle). |
| Surface explosions | `RegisterSurfaceExplosionRule` | Whether a CE blast against a floor slab also damages that slab's terrain. |

## Version and availability

Reference `IsoRimWaylith_CEPackZ.dll` (keep the reference optional if the pack is optional for your mod) and check the
version before you call anything. A member that is missing fails the whole method that names it when the method first
runs, so call the Api from methods of its own:

```csharp
[StaticConstructorOnStartup]
public static class MySiegeCompat
{
    static MySiegeCompat()
    {
        if (ModsConfig.IsActive("Peren.IsoRimWaylith.CEPack") && ModsConfig.IsActive("Peren.IsoRimProjectZ"))
        {
            Register();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Register()
    {
        if (IsoRimCEPackZ.Api.Version < 1)
        {
            return;
        }
        IsoRimCEPackZ.ExtensionCapabilities parts = IsoRimCEPackZ.Api.Capabilities;
        if ((parts & IsoRimCEPackZ.ExtensionCapabilities.TargetGeometry) != 0)
        {
            token = IsoRimCEPackZ.Api.RegisterTargetGeometry("My.Mod", typeof(MySiegeEngine), new MyEngineGeometry());
        }
    }

    private static IDisposable token;
}
```

`Api.Capabilities` tells which parts work in this game. A part is off when the CE members it needs were not found at
startup (a CE update; the log's pack line names it), when a pack group it builds on failed (the turret views for all
three, the flight frames for target geometry and surface explosions, the 3D areas and slab hits for surface explosions),
or when ProjectZ's combat parts the pack builds on are off. A registration made while its part is off is accepted and
does nothing.

## Rules for every part

- **Main thread.** Every call belongs to the game's main thread. A call from another thread is ignored (one warning).
- **Register at startup.** Nothing of a part is patched until it is first used: the first `RegisterTargetGeometry`, the
  first `RegisterSurfaceExplosionRule`, the first `OpenPlacement`. A game without a user of a part has none of its
  patches. The first use applies them at once (a short hitch); a `[StaticConstructorOnStartup]` is the right place.
  If they cannot be applied (a CE update), they are undone, one warning is logged and the part stays off.
- **Tokens.** `Register...` returns a token; `Dispose` removes the registration. Tokens and query handles are not save
  data: register again in every game session (a static constructor does that).
- **Caller errors throw at the call.** A missing owner, type, provider or rule (`ArgumentException`) and an owner that
  registered the same thing already (`InvalidOperationException`). Nothing else throws.
- **Fail-soft.** A provider or rule that throws, or answers something unusable, is switched off for good, with one
  warning naming its owner; its things go back to Combat Extended's own behaviour at once. Unusable means: a volume that
  is not finite or has no size on some axis; an aim point that is not finite, or a range cell off the map or in the void
  rows between floors. Combat Extended's own flow never sees the exception.

## Coordinates

ProjectZ's virtual space (ProjectZ Api 6): `x`, `z` over the ground rows, `y` the absolute height in cells (floor `k`'s
floor at `k * ProjectZ.Api.StoreyHeight`). `Vector3` and `Bounds` values of this Api are virtual (`...Virtual` in the
names). `IntVec3` values are real map cells, on the band of the floor they belong to (`ProjectZ.Api` converts).

Target geometry and surface explosions work on maps with floors only; on other maps everything stays as Combat
Extended has it.

## Placement queries

```csharp
PlacementSpec spec = new PlacementSpec(map, turretDef, stuff, null, cell, Rot4.North, Faction.OfPlayer);
if (IsoRimCEPackZ.Api.OpenPlacement(spec, out IPlacementQuery query) == GeometryStatus.Success)
{
    using (query)
    {
        query.ReadWeapon(out WeaponGeometry weapon);
        if (query.CheckRangeAndArc(target, out RangeArcResult range) == GeometryStatus.Success && range.InRange && range.WithinArc
            && query.TraceFixedCorridor(target, out FixedCorridorResult corridor) == GeometryStatus.Success && corridor.Clear)
        {
            // this cell can engage the target
        }
    }
}
```

- The query leases an unspawned turret of the def from a pool (per map, def and stuff), stands it on the cell with the
  rotation, faction, operator and ammo asked for, and runs Combat Extended's own code on it: the verb's range and
  minimum range, CE's fire arc (`CompFireArc`), the threat test. Between floors the pack's turret views apply, as for a
  built turret. `Dispose` gives the turret back unchanged; two open queries never share a turret.
- `CheckRangeAndArc`: `MaximumRangeDistance` is CE's range distance (between floors ProjectZ's), `MinimumRangeDistance`
  the 3D distance CE's minimum range is judged by. A target with target geometry is measured to its range cell.
- `TraceFixedCorridor` (direct fire only, else `NotApplicable`): the straight line from the turret's shot height to the
  target (its top; with target geometry its aim point) on ProjectZ's 3D walk. The first full-fillage building (an open
  door does not count), solid floor plane (a slab, a roof) or the map edge is reported. Pawns, partial cover and smoke are
  not fixed and are left out: the corridor is about the cell, not about the moment.
- Statuses: `InvalidInput` for a missing map, a cell off the map or in a void row, a target on another map, ammunition
  not in the turret's set; `NotApplicable` for a def that is not a `Building_TurretGunCE` or a verb that is not CE's;
  `Unavailable` when the part is off or CE threw inside the query (one warning per kind).

## Target geometry

```csharp
public sealed class MyEngineGeometry : ITargetGeometryProvider
{
    public bool TryGetVolume(Thing target, Map map, out Bounds volumeVirtual)
    {
        volumeVirtual = default;
        if (!(target is MySiegeEngine engine))
        {
            return false;
        }
        // a 3 x 3 cell tower, 2.5 storeys tall, standing on the ground column g of the engine (virtual cells)
        IntVec3 g = engine.GroundColumn;   // (your mod's own: x, z over the ground rows)
        volumeVirtual.SetMinMax(new Vector3(g.x - 1, 0f, g.z - 1), new Vector3(g.x + 2, 2.5f * ProjectZ.Api.StoreyHeight, g.z + 2));
        return true;
    }

    public bool TryGetAim(Thing target, TargetQuery query, out TargetAim aim)
    {
        aim = default;
        if (!(target is MySiegeEngine engine))
        {
            return false;
        }
        // aim at the top deck, in floor 1's storey; range is measured to the deck's real cell on floor 1
        IntVec3 g = engine.GroundColumn;
        aim = new TargetAim(new Vector3(g.x + 0.5f, 1.5f * ProjectZ.Api.StoreyHeight, g.z + 0.5f), engine.DeckCell);
        return true;
    }
}
```

A provider is registered for a `Thing` type and answers per thing; `false` leaves a thing on Combat Extended's path. The
newest registration whose `TryGetVolume` claims a thing owns it; the same provider is then asked `TryGetAim`. Both are
called on the main thread, often (every aim and sight test at the thing, every turret and AI view of it): keep them cheap
and free of side effects.

What the pack does with a claimed thing:

| Where | With the volume | With the aim |
|---|---|---|
| Projectiles (`ProjectileCE.CheckCellForCollision`) | Every cell the volume covers (any storey) holds the thing for collisions; CE's bounds test uses the volume (in the projectile's frame); the candidates' order and min-collision distance use the point where the shot line meets it. | |
| The pack's 3D sight and cover walks (`Sight`) | A volume across a line blocks it as full cover (not for suppressive fire or verbs that ignore partial cover). | A shot at the thing walks the 3D line to the aim point, also on one floor. |
| CE's aim (`Aim`: target height, the frame swaps) | | The aim point (in the shooter's frame) is the target point and height. |
| Turrets and CE's AI (`Scope`) | | Their view of the thing is the aim point. |
| Range (`Verb.OutOfRange`) | | Measured to `RangeCell` (ProjectZ's range distance; CE's minimum range by the 3D distance). |
| The shot line's end (`TryFindCEShootLineFromTo`) | | The aim point. |

Limits:

- **One floor, other targets.** Combat Extended's own sight walks run for pairs on one floor (the pack walks pairs on
  two floors, and lines at a claimed thing). A volume therefore stops shots on one floor at other targets through the
  projectiles only (the bullet hits the volume), not by refusing the line.
- **When the index updates.** Spawn, despawn and `Thing.Rotation` changes refresh a thing. Call
  `NotifyTargetGeometryChanged(thing)` whenever your answer changes in any other way (the thing moved, grew, opened).
  Small property setters can be compiled into their callers, so a rotation set from such a caller may be missed:
  call it after turning your thing too.
- `ResolveTargetAim` returns your provider's aim for a claimed thing (`NotApplicable` for anything else), the same answer
  the pack uses.

## Surface explosions

```csharp
public sealed class SlabDamageRule : ISurfaceExplosionRule
{
    public bool ShouldDamageTerrain(SurfaceExplosionContext context) =>
        context.Contact.IncidentSide == SurfaceSide.Below && context.ProjectileDef?.defName == "My_SiegeShell";
}
```

- A **contact** is where a projectile met a floor plane: a step stopped at a solid plane, a direct shot into a slab's
  underside, a laser stopped by one. A contact going down counts once CE accepts the ground impact there. A blast of that
  projectile (or of an explosive comp on it) starts on the contact's side of the plane (`IncidentCell`; under a slab just
  under CE's wall collision height).
- The rules are asked once per blast that started at a contact (`StartExplosionCE`). If one says yes, the slab cell
  (`SurfaceCell`) joins the blast's cells, terrain only: the game's terrain step runs for it (ProjectZ's slab damage,
  judged at vanilla level by the pack's slab rule) and nothing else (no pawns or things on the slab, no fire, no
  neighbour cells). A blast on a slab's top already reaches that slab: the rule matters for blasts under slabs and on
  roofs over upper floors.
- The rule is read-only policy: never edit the explosion or deal damage from it.
- **Saves.** While a rule is registered, a contact that is still to explode and a blast's pending slab cells are written
  to the save (`cepackSurfaceContact` on the projectile, `cepackSurfaceTerrain` on the blast), and only where they exist.
  A save loaded without the rule, or without the pack, ignores those nodes.

## Inventory: patches per part

All patches land on the part's first use (none at startup). Afterwards every hook returns on its first test while no
registration is live, and allocates nothing.

| Part | Patched method | Kind |
|---|---|---|
| Placement | `VerbProperties.AdjustedRange` | transpiler (`Thing.Map` read through `QueryContext`) |
| | `Verb_LaunchProjectileCE.ShooterPawn` | transpiler (the turret's operator through `QueryContext`) |
| Target geometry | `GenSpawn.Spawn` (every overload returning the thing) | postfix |
| | `Thing.DeSpawn` | prefix |
| | `Thing.Rotation` (set) | postfix |
| | `CE_Utility.GetBoundsFor(Thing)` | postfix (in a projectile's or sight walk's frame) |
| | `ProjectileCE.CheckCellForCollision` | prefix + finalizer (the frame), transpiler (things, cover, points) |
| | `ProjectileCE.<CheckCellForCollision>b__*` (the sort key lambda, if found) | transpiler |
| | `Verb.OutOfRange` | postfix (last) |
| | `Verb_LaunchProjectileCE.TryFindCEShootLineFromTo` | postfix |
| Surface explosions | `Impact`, `Tick`, `RayCast` of every `ProjectileCE` type | prefix + finalizer (the blast's source) |
| | `Impact(Thing)` of every `ProjectileCE` type | prefix (a contact going down becomes final) |
| | `CompExplosiveCE`, `CompExplosive`, `AmmoThing`, `CompAbilityEffect_ExplosionCE`: `Explode`, `Detonate`, `TryDetonate`, `Apply` | prefix + finalizer |
| | `GenExplosionCE.DoExplosion` | prefix (the blast's start) |
| | `ExplosionCE.StartExplosionCE` | prefix (the rules), postfix (the slab cell) |
| | `ExplosionCE.MergeWith` | postfix |
| | `Explosion.AffectCell` | prefix + postfix |
| | `ProjectileCE.ExposeData`, `ExplosionCE.ExposeData` | postfix (saves) |

The pack's own groups call into the parts at run time (`Aim`, `Sight`, `Scope`, `Frames`): each such call is behind the
part's `Active` flag (a static bool), and the parts' classes have no type initializer that could fail, so a part that is
off can never break the groups that call it.

**CE updates.** Every member a part patches or reads is looked up at startup (`Prepare`: by name and parameter count,
the parameter names its patches bind, and the calls its transpilers swap). A missing one turns the part off with a line
in the pack's startup log; nothing is patched for it. The compiler's lambda name `<CheckCellForCollision>b__*` is the
one lookup that may silently find nothing after a CE rebuild: the candidates' sort order then stays CE's own.

## Harness

`-quicktest -cepacktest=api` (`SourceZ/TestApi.cs`): a fake siege-engine provider and a fake rule, registered by the
test only, in real fights (drafted colonists and real turrets firing real CE projectiles, real ticks), each check with
its own fixture and seed: nothing patched before the first registration; aim and volume on one floor and between floors;
projectiles meeting a volume off the engine's cell; sight blocked by a volume; range to the range cell; placement
queries against the real turret built at the same cell (clear, a wall, a slab overhead, a deck, out of range, the fire
arc); slab terrain damaged only when the rule says yes; broken providers and rules (NaN, exceptions); saves with and
without the rule. `-cepacktest=apiload -cepacknoz` loads the save with surface data without the pack's Z part.
`-cepacktest=perf` measures a fixed big fight's tick time, allocations and frame time (`-cepackperfreg`: with
registrations that claim nothing).
