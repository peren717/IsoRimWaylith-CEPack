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
    // Api.RegisterTargetGeometry: another mod's own volume and aim point for its things (a siege engine, a tall vehicle).
    //
    // A provider registered for a Thing type answers per thing: its volume (Bounds in ProjectZ's virtual space, any height,
    // any number of cells and storeys) and where a shooter aims at it (a virtual point and the real cell its range is
    // measured to). The volume is extra physical occupancy only: thingGrid, pathing and the thing's own cells are
    // unchanged. Index (per map) keeps every claimed thing's volume and the real cells (every storey's band) it covers.
    // With it:
    //  - projectiles (CE's CheckCellForCollision, a transpiler) also meet the things whose volume covers the cell, against
    //    that volume (CE_Utility.GetBoundsFor in the projectile's frame, the context set by a prefix and finalizer);
    //  - the pack's 3D sight and cover walks (Sight) are blocked by such a volume, and a shot at the thing aims at the
    //    provider's point: CE's aim sites (Aim), turret and AI views (Scope), range (Verb.OutOfRange postfix, to
    //    RangeCell) and the shot line's end (TryFindCEShootLineFromTo postfix).
    // Only on maps with floors (virtual space is ProjectZ's); elsewhere every thing stays as CE has it.
    //
    // Cost: the patches land on the first Register (PackZ.ApplyLate); with no live registration every hook returns on its
    // first test (Active) and allocates nothing. A provider that throws or answers something unusable (NaN, an empty
    // volume, a range cell off the map or in a void row) is switched off for good with one warning; its things go back to
    // the native path at once. Index changes replace a cell's list (never edit it), so a walk over an old list stays valid.
    internal static class TargetGeometry
    {
        // The part can work (its CE members were found at startup, the pack's aim and turret groups landed): Api.Capabilities.
        internal static bool Ready;

        // Patched and at least one live registration: the first test of every hook.
        internal static bool Active;

        private static bool patched;

        private sealed class Registration : IDisposable
        {
            internal string Owner;
            internal Type Type;
            internal ITargetGeometryProvider Provider;
            internal bool Live;

            public void Dispose()
            {
                if (!Live || !registrations.Remove(this))
                {
                    return;
                }
                Live = false;
                Changed();
                RebuildAll();
            }
        }

        private sealed class Entry
        {
            internal Registration Registration;
            internal Bounds Volume;
            internal Rot4 Rotation;
            internal IntVec3[] Cells;
        }

        private sealed class Index
        {
            internal readonly Dictionary<Thing, Entry> Entities = new Dictionary<Thing, Entry>();
            internal readonly Dictionary<IntVec3, List<Thing>> Cells = new Dictionary<IntVec3, List<Thing>>();
        }

        private static readonly List<Registration> registrations = new List<Registration>();
        private static readonly Dictionary<Type, Registration[]> byType = new Dictionary<Type, Registration[]>();   // (newest first; cleared on every change)
        private static readonly ConditionalWeakTable<Map, Index> indices = new ConditionalWeakTable<Map, Index>();
        private static readonly Registration[] NoRegistrations = new Registration[0];
        private static readonly List<IntVec3> cellScratch = new List<IntVec3>();

        // The frame CE_Utility.GetBoundsFor answers in (set while a projectile checks a cell, or a sight walk runs).
        internal struct State
        {
            public Map Map;
            public int Frame;
        }

        [ThreadStatic] private static State context;

        // Harness counters.
        public static int Claimed, Faults, VolumeHits, SightBlocks, RotationCalls;

        // ------------------------------------------------------------------ CE members (resolved by Prepare, not a type initializer)
        private static Func<ProjectileCE, Ray> shotLine;
        private static List<MethodInfo> spawns;
        private static MethodInfo despawn, rotation, bounds, collision, outOfRange, shootLine;
        private static List<MethodInfo> lambdas;

        // At startup (a PackZ group): every member the patches need, and CE's collision method still reading what the
        // transpiler swaps. Nothing is patched here.
        internal static void Prepare(Harmony h)
        {
            spawns = AccessTools.GetDeclaredMethods(typeof(GenSpawn)).Where(m => m.Name == "Spawn" && m.ReturnType == typeof(Thing)
                && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Thing)).ToList();
            if (spawns.Count == 0)
            {
                throw new MissingMethodException("GenSpawn.Spawn(Thing, ...)");
            }
            despawn = PackZ.Method(typeof(Thing), nameof(Thing.DeSpawn));
            rotation = AccessTools.PropertySetter(typeof(Thing), nameof(Thing.Rotation)) ?? throw new MissingMethodException("Thing.Rotation (set)");
            bounds = PackZ.Method(typeof(CE_Utility), nameof(CE_Utility.GetBoundsFor), new[] { typeof(Thing) });
            collision = PackZ.Method(typeof(ProjectileCE), "CheckCellForCollision");
            outOfRange = PackZ.Method(typeof(Verb), nameof(Verb.OutOfRange));
            PackZ.NeedParams(outOfRange, "root", "targ");
            shootLine = Aim.ByName(typeof(Verb_LaunchProjectileCE), "TryFindCEShootLineFromTo", 4);
            PackZ.NeedParams(shootLine, "root", "targ", "targetPos");
            PackZ.NeedParams(bounds, "thing");
            MethodInfo line = AccessTools.PropertyGetter(typeof(ProjectileCE), "ShotLine") ?? throw new MissingMethodException("ProjectileCE.ShotLine");
            shotLine = AccessTools.MethodDelegate<Func<ProjectileCE, Ray>>(line);
            // (the sort key lambda of CheckCellForCollision: candidates nearest first; not needed, its absence keeps CE's order)
            lambdas = AccessTools.GetDeclaredMethods(typeof(ProjectileCE)).Where(m => m.Name.StartsWith("<CheckCellForCollision>b__") && m.ReturnType == typeof(float)
                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Thing)).ToList();
            List<CodeInstruction> il = PatchProcessor.GetOriginalInstructions(collision);
            if (!il.Any(c => c.Calls(thingsAt)) || !il.Any(c => c.Calls(coverAt)))
            {
                throw new InvalidOperationException("ProjectileCE.CheckCellForCollision no longer reads ThingsListAtFast and HighestCoverAt");
            }
        }

        // On the first Register (all or nothing).
        private static void Patch(Harmony h)
        {
            foreach (MethodInfo m in spawns)
            {
                PackZ.Postfix(h, m, typeof(TargetGeometry), nameof(Spawn_Postfix));
            }
            PackZ.Prefix(h, despawn, typeof(TargetGeometry), nameof(DeSpawn_Prefix));
            PackZ.Postfix(h, rotation, typeof(TargetGeometry), nameof(Rotation_Postfix));
            PackZ.Postfix(h, bounds, typeof(TargetGeometry), nameof(Bounds_Postfix));
            h.Patch(collision, prefix: new HarmonyMethod(typeof(TargetGeometry), nameof(Collision_Prefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(TargetGeometry), nameof(Collision_Finalizer)));
            PackZ.Applied += 2;
            PackZ.Transpiler(h, collision, typeof(TargetGeometry), nameof(Collision_Transpiler));
            foreach (MethodInfo m in lambdas)
            {
                PackZ.Transpiler(h, m, typeof(TargetGeometry), nameof(Lambda_Transpiler));
            }
            h.Patch(outOfRange, postfix: new HarmonyMethod(typeof(TargetGeometry), nameof(Range_Postfix)) { priority = int.MinValue });   // (last: its answer stands)
            PackZ.Applied++;
            PackZ.Postfix(h, shootLine, typeof(TargetGeometry), nameof(Line_Postfix));
        }

        // ------------------------------------------------------------------ registrations
        internal static IDisposable Register(string owner, Type type, ITargetGeometryProvider provider)
        {
            if (string.IsNullOrEmpty(owner) || type == null || !typeof(Thing).IsAssignableFrom(type) || provider == null)
            {
                throw new ArgumentException("Target geometry needs an owner, a Thing type and a provider.");
            }
            foreach (Registration x in registrations)
            {
                if (x.Owner == owner && x.Type == type)
                {
                    throw new InvalidOperationException("Target geometry is already registered: " + owner + " / " + type.FullName);
                }
            }
            Registration r = new Registration { Owner = owner, Type = type, Provider = provider };
            if (!Ready || !ApiParts.OnMainThread("RegisterTargetGeometry (" + owner + ")"))
            {
                return r;   // (an inert token: Api.Capabilities tells the caller the part is off)
            }
            if (!patched)
            {
                patched = true;
                if (!PackZ.ApplyLate("Target geometry", Patch))
                {
                    Ready = false;
                    return r;
                }
            }
            r.Live = true;
            registrations.Add(r);
            Changed();
            RebuildAll();
            return r;
        }

        // The part goes off (a failed ProjectZ part, PackZ.CheckParts).
        internal static void Disable()
        {
            Ready = false;
            Active = false;
        }

        private static void Changed()
        {
            byType.Clear();
            bool any = false;
            foreach (Registration r in registrations)
            {
                any |= r.Live;
            }
            Active = Ready && patched && any;
        }

        // The live registrations for a thing's type, newest first (a later registration overrides an earlier one as a whole).
        private static Registration[] Matching(Type t)
        {
            if (byType.TryGetValue(t, out Registration[] found))
            {
                return found;
            }
            List<Registration> list = null;
            for (int i = registrations.Count - 1; i >= 0; i--)
            {
                Registration r = registrations[i];
                if (r.Live && r.Type.IsAssignableFrom(t))
                {
                    (list ??= new List<Registration>()).Add(r);
                }
            }
            found = list != null ? list.ToArray() : NoRegistrations;
            byType[t] = found;
            return found;
        }

        // A provider failed: its registration is off for good (one warning), its things go back to the native path (or to an
        // older registration of their type). The index is rebuilt as new objects: walks over the old ones stay valid.
        private static void Fault(Registration r, string what)
        {
            if (!r.Live)
            {
                return;
            }
            r.Live = false;
            registrations.Remove(r);
            Faults++;
            Changed();
            ApiParts.OwnerFault("Target geometry", r.Owner, what);
            RebuildAll();
        }

        private static void RebuildAll()
        {
            if (Current.Game == null || Find.Maps == null)
            {
                return;
            }
            foreach (Map map in Find.Maps)
            {
                indices.Remove(map);
                if (!Active || Layout.Of(map) == null)
                {
                    continue;
                }
                List<Thing> all = map.listerThings.AllThings;
                for (int i = 0; i < all.Count; i++)
                {
                    if (Matching(all[i].GetType()).Length != 0)
                    {
                        Add(all[i]);
                    }
                }
            }
        }

        // Api.NotifyTargetGeometryChanged: the provider's answer for this thing changed (it moved, grew, turned).
        internal static void Refresh(Thing thing)
        {
            if (!Active || thing == null || !ApiParts.OnMainThread("NotifyTargetGeometryChanged"))
            {
                return;
            }
            if (Matching(thing.GetType()).Length != 0)
            {
                Add(thing);
            }
            else
            {
                Remove(thing);
            }
        }

        // The volume from the newest registration that claims the thing (a provider failing is switched off, the next tried).
        private static void Add(Thing thing)
        {
            Map map = thing.Map;
            Layout l = Layout.Of(map);
            if (l == null || !thing.Spawned || thing.Destroyed)
            {
                Remove(thing);
                return;
            }
            Registration owner = null;
            Bounds b = default;
            foreach (Registration r in Matching(thing.GetType()))
            {
                bool claimed;
                try
                {
                    claimed = r.Provider.TryGetVolume(thing, map, out b);
                }
                catch (Exception e)
                {
                    Fault(r, "threw in TryGetVolume for " + thing + ": " + PackZ.Reason(e));
                    continue;
                }
                if (!claimed)
                {
                    continue;
                }
                if (!ValidVolume(b))
                {
                    Fault(r, "returned an unusable volume for " + thing + " (" + b + ")");
                    continue;
                }
                owner = r;
                break;
            }
            // (after the providers ran: a fault on the way rebuilt the index, maybe with this thing in it already)
            Remove(thing);
            if (owner == null || !owner.Live)
            {
                return;
            }
            Index index = indices.GetValue(map, _ => new Index());
            Vector3 min = b.min, max = b.max;
            int bottom = Mathf.Max(l.min, Mathf.FloorToInt(min.y / ProjectZ.Api.StoreyHeight));
            int top = Mathf.Min(l.max, Mathf.FloorToInt(max.y / ProjectZ.Api.StoreyHeight));
            int x0 = Mathf.Max(0, Mathf.FloorToInt(min.x)), x1 = Mathf.Min(l.width - 1, Mathf.FloorToInt(max.x));
            int z0 = Mathf.Max(0, Mathf.FloorToInt(min.z)), z1 = Mathf.Min(l.rows - 1, Mathf.FloorToInt(max.z));
            cellScratch.Clear();
            for (int k = bottom; k <= top; k++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        IntVec3 c = l.Cell(new IntVec3(x, 0, z), k);
                        if (!c.IsValid)
                        {
                            continue;
                        }
                        List<Thing> list = index.Cells.TryGetValue(c, out List<Thing> old) ? new List<Thing>(old) : new List<Thing>(1);
                        list.Add(thing);
                        index.Cells[c] = list;
                        cellScratch.Add(c);
                    }
                }
            }
            index.Entities[thing] = new Entry { Registration = owner, Volume = b, Rotation = thing.Rotation, Cells = cellScratch.ToArray() };
            Claimed++;
        }

        private static void Remove(Thing thing)
        {
            Map map = thing.Map;
            if (map == null || !indices.TryGetValue(map, out Index index) || !index.Entities.TryGetValue(thing, out Entry old))
            {
                return;
            }
            index.Entities.Remove(thing);
            foreach (IntVec3 c in old.Cells)
            {
                if (!index.Cells.TryGetValue(c, out List<Thing> list))
                {
                    continue;
                }
                if (list.Count <= 1)
                {
                    index.Cells.Remove(c);
                }
                else
                {
                    List<Thing> rest = new List<Thing>(list);
                    rest.Remove(thing);
                    index.Cells[c] = rest;
                }
            }
        }

        private static bool ValidVolume(Bounds b)
        {
            Vector3 min = b.min, max = b.max;
            return Finite(min) && Finite(max) && b.size.x > 0f && b.size.y > 0f && b.size.z > 0f;
        }

        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        // ------------------------------------------------------------------ lookups
        private static bool TryEntry(Thing thing, Map map, out Entry entry)
        {
            entry = null;
            return thing != null && map != null && thing.Spawned && thing.Map == map && indices.TryGetValue(map, out Index index)
                && index.Entities.TryGetValue(thing, out entry) && entry.Registration.Live;
        }

        // Api.ResolveTargetAim: the provider's aim at a claimed thing (NotApplicable: the native path).
        internal static GeometryStatus Resolve(Thing target, TargetQuery query, out TargetAim aim)
        {
            aim = default;
            if (!Ready)
            {
                return GeometryStatus.Unavailable;
            }
            if (!Active || !TryEntry(target, query.Map, out Entry entry))
            {
                return GeometryStatus.NotApplicable;
            }
            Registration r = entry.Registration;
            bool ok;
            try
            {
                ok = r.Provider.TryGetAim(target, query, out aim);
            }
            catch (Exception e)
            {
                Fault(r, "threw in TryGetAim for " + target + ": " + PackZ.Reason(e));
                aim = default;
                return GeometryStatus.NotApplicable;
            }
            if (!ok)
            {
                aim = default;
                return GeometryStatus.NotApplicable;
            }
            Layout l = Layout.Of(query.Map);
            if (!Finite(aim.PointVirtual) || !aim.RangeCell.InBounds(query.Map) || l == null || l.FrameOf(aim.RangeCell) == Layout.None)
            {
                Fault(r, "returned an unusable aim for " + target + " (point " + aim.PointVirtual + ", range cell " + aim.RangeCell + ")");
                aim = default;
                return GeometryStatus.NotApplicable;
            }
            return GeometryStatus.Success;
        }

        // The aim of a verb at a thing (root: the cell the verb fires from).
        internal static bool TryAim(Thing target, Verb verb, IntVec3 root, out TargetAim aim)
        {
            if (!Active || target == null)
            {
                aim = default;
                return false;
            }
            Thing caster = verb?.caster;
            return Resolve(target, new TargetQuery(QueryContext.MapOf(caster), caster, root, verb, (verb as Verb_LaunchProjectileCE)?.Projectile), out aim) == GeometryStatus.Success;
        }

        // The aim of a viewer without a verb of its own at hand (Scope: a turret, a searcher), with its current verb.
        internal static bool TryAimFrom(Thing target, Thing viewer, Map map, out TargetAim aim)
        {
            if (!Active || target == null || viewer == null)
            {
                aim = default;
                return false;
            }
            Verb verb = (viewer as IAttackTargetSearcher)?.CurrentEffectiveVerb;
            return Resolve(target, new TargetQuery(map, viewer, viewer.Position, verb, (verb as Verb_LaunchProjectileCE)?.Projectile), out aim) == GeometryStatus.Success;
        }

        // The claimed things whose volume covers a real cell (null: none). The list is never edited: do not change it.
        internal static List<Thing> At(Map map, IntVec3 cell)
        {
            if (!Active || map == null || !indices.TryGetValue(map, out Index index))
            {
                return null;
            }
            return index.Cells.TryGetValue(cell, out List<Thing> list) ? list : null;
        }

        // A thing's bounds in frame `frame`: its volume if claimed, else CE's own.
        internal static Bounds BoundsIn(Thing thing, Map map, int frame)
        {
            if (Active && TryEntry(thing, map, out Entry entry))
            {
                return InFrame(entry.Volume, Layout.Of(map), frame);
            }
            return Aim.boundsFor(thing);
        }

        // A claimed thing's volume in frame `frame` (false: not claimed now).
        internal static bool TryVolumeIn(Thing thing, Map map, int frame, out Bounds b)
        {
            if (Active && TryEntry(thing, map, out Entry entry))
            {
                b = InFrame(entry.Volume, Layout.Of(map), frame);
                return true;
            }
            b = default;
            return false;
        }

        // A virtual volume in a storey's frame (CE units of height over that storey's floor, its band's rows).
        private static Bounds InFrame(Bounds b, Layout l, int frame)
        {
            b.center = l.FromVirtual(b.center, frame);
            b.size = new Vector3(b.size.x, b.size.y / Layout.CellsPerUnit, b.size.z);
            return b;
        }

        // Sight's walks (Sight.CanHitCell_Prefix): GetBoundsFor answers claimed volumes in the walk's frame meanwhile.
        internal static State Enter(Map map, int frame)
        {
            State old = context;
            if (Active)
            {
                context = new State { Map = map, Frame = frame };
            }
            return old;
        }

        internal static void Exit(State old) => context = old;

        // ------------------------------------------------------------------ projectiles (CheckCellForCollision)
        private static readonly MethodInfo thingsAt = AccessTools.Method(typeof(ThingGrid), nameof(ThingGrid.ThingsListAtFast), new[] { typeof(IntVec3) });
        private static readonly MethodInfo coverAt = AccessTools.Method(typeof(LightingTracker), nameof(LightingTracker.HighestCoverAt));
        private static readonly MethodInfo drawPos = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos));
        private static readonly MethodInfo trueCenter = AccessTools.Method(typeof(GenThing), nameof(GenThing.TrueCenter), new[] { typeof(Thing) });

        public static void Collision_Prefix(ProjectileCE __instance, out State __state)
        {
            __state = context;
            if (!Active)
            {
                return;
            }
            Map map = __instance.Map;
            Layout l = Layout.Of(map);
            if (l != null)
            {
                context = new State { Map = map, Frame = l.FrameOf(Frames.exactGet(__instance)) };
            }
        }

        public static void Collision_Finalizer(State __state) => context = __state;

        // CheckCellForCollision: the things at a cell (ThingsListAtFast), the cell's highest cover (its early out) and a
        // candidate's point (DrawPos, TrueCenter) also see claimed volumes. Each swap is CE's own call when nothing is claimed.
        public static IEnumerable<CodeInstruction> Collision_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int things = 0;
            foreach (CodeInstruction ci in instructions)
            {
                string mine = ci.Calls(thingsAt) ? nameof(ThingsAt) : ci.Calls(coverAt) ? nameof(CoverAt) : ci.Calls(drawPos) ? nameof(DrawPosFor)
                    : ci.Calls(trueCenter) ? nameof(CenterFor) : null;
                if (mine == null)
                {
                    yield return ci;
                    continue;
                }
                things += mine == nameof(ThingsAt) ? 1 : 0;
                // the call's arguments are on the stack: add the projectile (this) and call the pack's version
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci).MoveBlocksFrom(ci);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TargetGeometry), mine));
            }
            if (things == 0)
            {
                throw new InvalidOperationException("ProjectileCE.CheckCellForCollision: no ThingsListAtFast call");
            }
        }

        // The candidates' sort key (a lambda of the projectile): a claimed thing's point on the shot line.
        public static IEnumerable<CodeInstruction> Lambda_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(drawPos))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci).MoveBlocksFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TargetGeometry), nameof(DrawPosFor)));
                    continue;
                }
                yield return ci;
            }
        }

        // CE's things at the cell, and the claimed things whose volume covers it (a new list only then).
        public static List<Thing> ThingsAt(ThingGrid grid, IntVec3 cell, ProjectileCE projectile)
        {
            List<Thing> native = grid.ThingsListAtFast(cell);
            if (!Active)
            {
                return native;
            }
            List<Thing> extra = At(projectile.Map, cell);
            if (extra == null)
            {
                return native;
            }
            List<Thing> all = null;
            foreach (Thing t in extra)
            {
                if (TryEntry(t, projectile.Map, out _) && !native.Contains(t))
                {
                    (all ??= new List<Thing>(native)).Add(t);
                }
            }
            return all ?? native;
        }

        public static float CoverAt(LightingTracker tracker, IntVec3 cell, ProjectileCE projectile)
        {
            float height = tracker.HighestCoverAt(cell);
            if (!Active)
            {
                return height;
            }
            List<Thing> extra = At(projectile.Map, cell);
            if (extra != null)
            {
                int frame = Layout.Of(projectile.Map).FrameOf(cell);
                foreach (Thing t in extra)
                {
                    if (TryVolumeIn(t, projectile.Map, frame, out Bounds b))
                    {
                        height = Mathf.Max(height, b.max.y);
                    }
                }
            }
            return height;
        }

        public static Vector3 DrawPosFor(Thing thing, ProjectileCE projectile) => Active && TryEntry(thing, projectile.Map, out _) ? ContactPoint(thing, projectile) : thing.DrawPos;

        public static Vector3 CenterFor(Thing thing, ProjectileCE projectile) => Active && TryEntry(thing, projectile.Map, out _) ? ContactPoint(thing, projectile) : thing.TrueCenter();

        // Where the shot line meets a claimed volume (its centre if it misses), in the projectile's frame.
        private static Vector3 ContactPoint(Thing thing, ProjectileCE projectile)
        {
            Layout l = Layout.Of(projectile.Map);
            Ray ray = shotLine(projectile);
            Bounds b = BoundsIn(thing, projectile.Map, l.FrameOf(Frames.exactGet(projectile)));
            if (b.IntersectRay(ray, out float distance))
            {
                VolumeHits++;
                return ray.GetPoint(distance);
            }
            return b.center;
        }

        // CE_Utility.GetBoundsFor while a projectile or a sight walk asks: a claimed thing's volume in that frame.
        public static void Bounds_Postfix(Thing thing, ref Bounds __result)
        {
            if (!Active)
            {
                return;
            }
            State c = context;
            if (c.Map != null && TryEntry(thing, c.Map, out Entry entry))
            {
                __result = InFrame(entry.Volume, Layout.Of(c.Map), c.Frame);
            }
        }

        // ------------------------------------------------------------------ the index follows the things
        public static void Spawn_Postfix(Thing __result)
        {
            if (!Active || __result == null || Matching(__result.GetType()).Length == 0)
            {
                return;
            }
            Add(__result);
        }

        public static void DeSpawn_Prefix(Thing __instance)
        {
            if (!Active)
            {
                return;
            }
            Remove(__instance);
        }

        public static void Rotation_Postfix(Thing __instance)
        {
            if (!Active || !__instance.Spawned || Matching(__instance.GetType()).Length == 0)
            {
                return;
            }
            RotationCalls++;
            if (TryEntry(__instance, __instance.Map, out Entry entry) && entry.Rotation == __instance.Rotation)
            {
                return;   // (a claimed thing facing as it was: nothing to do)
            }
            Add(__instance);
        }

        // ------------------------------------------------------------------ range and the shot line
        // Verb.OutOfRange (last): a claimed target's range is measured to its RangeCell (ProjectZ's range distance, CE's
        // minimum range by the 3D distance).
        public static void Range_Postfix(Verb __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            if (!Active || !targ.HasThing || !(__instance is Verb_LaunchProjectileCE) || !TryAim(targ.Thing, __instance, root, out TargetAim aim))
            {
                return;
            }
            Map map = QueryContext.MapOf(__instance.caster);
            __result = ProjectZ.Api.RangeDistance(root, aim.RangeCell, map) > __instance.EffectiveRange
                || ProjectZ.Api.Distance3D(root, aim.RangeCell, map) < __instance.verbProps.EffectiveMinRange(targ, __instance.caster);
        }

        // TryFindCEShootLineFromTo: the line's end point at a claimed target is the provider's aim, in root's frame.
        public static void Line_Postfix(Verb_LaunchProjectileCE __instance, IntVec3 root, LocalTargetInfo targ, ref Vector3 targetPos)
        {
            if (!Active || !targ.HasThing || !TryAim(targ.Thing, __instance, root, out TargetAim aim))
            {
                return;
            }
            Map map = QueryContext.MapOf(__instance.caster);
            Layout l = Layout.Of(map);
            if (l != null)
            {
                targetPos = l.FromVirtual(aim.PointVirtual, ProjectZ.Api.FloorOf(root, map));
            }
        }
    }
}
