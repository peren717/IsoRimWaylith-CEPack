using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // Api.RegisterSurfaceExplosionRule: a CE blast against a floor slab may also damage that slab's terrain.
    //
    // A blast on a slab's top already reaches the slab cell (it is the blast's own cell; SlabHits judges it). A blast
    // against a slab's underside starts in the storey below, so the slab terrain is not one of its cells. With a rule:
    //  - Contact: Frames tells where a projectile met a plane (a step clipped at a solid plane, a direct shot into a
    //    slab's underside, a laser stopped by one). A contact going down becomes final only when CE accepts the ground
    //    impact there (ImpactContact_Prefix); one going up is final at once.
    //  - Source: while a projectile (or an explosive comp of one) runs Impact, Tick or RayCast, it is the source of any
    //    blast started meanwhile (prefix + finalizer, nesting kept). A blast from a source with a final contact on the same
    //    map starts in the contact's incident cell (under the slab: just under CE's wall collision height).
    //  - The rules are asked once per blast (StartExplosionCE). If one says yes, the contact's slab cell joins the blast's
    //    cells after CE's own expansion, in its affect-tick order, marked terrain only: AffectCell then runs the game's
    //    terrain step for it (DamageWorker.ExplosionDamageTerrain: ProjectZ's slab damage, with SlabHits' cap) and nothing
    //    else (no pawns or things on the slab, no fire, no neighbour seeding). Merged blasts carry the data along.
    //  - Saves: a final contact (its projectile) and a blast's pending slab cells are written while a rule is registered
    //    and only where they exist. Without the rule or the pack, the game ignores those nodes.
    //
    // Cost: the patches land on the first Register (PackZ.ApplyLate); with no live rule every hook returns on its first
    // test and allocates nothing. A rule that throws is switched off for good with one warning.
    internal static class SurfaceExplosions
    {
        // The part can work (its CE members were found at startup, the pack's area and slab groups landed).
        internal static bool Ready;

        // Patched and at least one live rule: the first test of every hook.
        internal static bool Active;

        private static bool patched;

        private const string ContactKey = "cepackSurfaceContact", TerrainKey = "cepackSurfaceTerrain";

        private sealed class Rule : IDisposable
        {
            internal string Owner;
            internal ISurfaceExplosionRule Policy;
            internal bool Live;

            public void Dispose()
            {
                if (!Live)
                {
                    return;
                }
                Live = false;
                rules.Remove(this);
                Changed();
            }
        }

        private static readonly List<Rule> rules = new List<Rule>();

        // A projectile's contact with a plane (virtual point; the real cells of its slab and of the blast's start).
        private sealed class ContactData : IExposable
        {
            internal Map Map;
            internal IntVec3 Ground, Surface, Incident;
            internal int Upper;
            internal Vector3 Point, Normal;
            internal bool Below, Floor, Final;

            public ContactData()
            {
            }

            internal SurfaceContact Contact => new SurfaceContact(Map, Ground, Upper, Surface, Incident, Point, Normal, Below ? SurfaceSide.Below : SurfaceSide.Above, Floor);

            public void ExposeData()
            {
                Scribe_References.Look(ref Map, "map");
                Scribe_Values.Look(ref Ground, "ground");
                Scribe_Values.Look(ref Surface, "surface");
                Scribe_Values.Look(ref Incident, "incident");
                Scribe_Values.Look(ref Upper, "upper");
                Scribe_Values.Look(ref Point, "point");
                Scribe_Values.Look(ref Normal, "normal");
                Scribe_Values.Look(ref Below, "below");
                Scribe_Values.Look(ref Floor, "floor");
                Scribe_Values.Look(ref Final, "final");
            }
        }

        // A blast's slab cells: still to do, done, and the ones added by the rule (terrain only).
        private sealed class TerrainData : IExposable
        {
            internal HashSet<IntVec3> Pending = new HashSet<IntVec3>(), Processed = new HashSet<IntVec3>(), TerrainOnly = new HashSet<IntVec3>();

            public TerrainData()
            {
            }

            public void ExposeData()
            {
                Scribe_Collections.Look(ref Pending, "pending", LookMode.Value);
                Scribe_Collections.Look(ref Processed, "processed", LookMode.Value);
                Scribe_Collections.Look(ref TerrainOnly, "terrainOnly", LookMode.Value);
                Pending ??= new HashSet<IntVec3>();
                Processed ??= new HashSet<IntVec3>();
                TerrainOnly ??= new HashSet<IntVec3>();
            }
        }

        private static readonly ConditionalWeakTable<ProjectileCE, ContactData> contacts = new ConditionalWeakTable<ProjectileCE, ContactData>();
        private static readonly Dictionary<ExplosionCE, TerrainData> targets = new Dictionary<ExplosionCE, TerrainData>();
        private static readonly List<ExplosionCE> pruneScratch = new List<ExplosionCE>();
        [ThreadStatic] private static ProjectileCE source;

        // Harness counters.
        public static int ContactsSeen, RulesAsked, TerrainAdded, TerrainHits, Faults;
        public static string LastAdded = "-", LastDamage = "-";

        // ------------------------------------------------------------------ CE members (resolved by Prepare, not a type initializer)
        private static Action<DamageWorker, Explosion, IntVec3> damageTerrain;
        private static AccessTools.FieldRef<Explosion, List<IntVec3>> affectCells;
        private static Func<Explosion, IntVec3, int> affectTick;
        private static List<MethodInfo> sourceMethods, impactMethods, compMethods;
        private static MethodInfo doExplosion, start, merge, affect, projectileSave, explosionSave;

        // At startup (a PackZ group): every member the patches need. Nothing is patched here.
        internal static void Prepare(Harmony h)
        {
            sourceMethods = new List<MethodInfo>();
            impactMethods = new List<MethodInfo>();
            foreach (Type t in GenTypes.AllTypes.Where(t => !t.ContainsGenericParameters && typeof(ProjectileCE).IsAssignableFrom(t)))
            {
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t).Where(m => !m.IsStatic && !m.IsAbstract && (m.Name == "Impact" || m.Name == "Tick" || m.Name == "RayCast")))
                {
                    sourceMethods.Add(m);
                    if (m.Name == "Impact" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Thing))
                    {
                        impactMethods.Add(m);
                    }
                }
            }
            if (!impactMethods.Any(m => m.DeclaringType == typeof(ProjectileCE)))
            {
                throw new MissingMethodException("ProjectileCE.Impact(Thing)");
            }
            compMethods = new List<MethodInfo>();
            foreach (Type t in new[] { typeof(CompExplosiveCE), typeof(CompExplosive), typeof(AmmoThing), typeof(CompAbilityEffect_ExplosionCE) })
            {
                compMethods.AddRange(AccessTools.GetDeclaredMethods(t).Where(m => !m.IsStatic && !m.IsAbstract && (m.Name == "Explode" || m.Name == "Detonate" || m.Name == "TryDetonate" || m.Name == "Apply")));
            }
            doExplosion = PackZ.Method(typeof(GenExplosionCE), nameof(GenExplosionCE.DoExplosion));
            PackZ.NeedParams(doExplosion, "center", "map", "height");
            start = PackZ.Method(typeof(ExplosionCE), nameof(ExplosionCE.StartExplosionCE));
            merge = PackZ.Method(typeof(ExplosionCE), nameof(ExplosionCE.MergeWith));
            PackZ.NeedParams(merge, "other", "merged");
            affect = PackZ.Method(typeof(Explosion), "AffectCell");
            PackZ.NeedParams(affect, "c");
            projectileSave = PackZ.Method(typeof(ProjectileCE), nameof(ProjectileCE.ExposeData));
            explosionSave = PackZ.Method(typeof(ExplosionCE), nameof(ExplosionCE.ExposeData));
            damageTerrain = AccessTools.MethodDelegate<Action<DamageWorker, Explosion, IntVec3>>(PackZ.Method(typeof(DamageWorker), "ExplosionDamageTerrain"));
            affectCells = AccessTools.FieldRefAccess<Explosion, List<IntVec3>>("cellsToAffect");
            affectTick = AccessTools.MethodDelegate<Func<Explosion, IntVec3, int>>(PackZ.Method(typeof(Explosion), "GetCellAffectTick"));
        }

        // On the first Register (all or nothing).
        private static void Patch(Harmony h)
        {
            foreach (MethodInfo m in sourceMethods)
            {
                h.Patch(m, prefix: new HarmonyMethod(typeof(SurfaceExplosions), nameof(Source_Prefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(SurfaceExplosions), nameof(Source_Finalizer)));
                PackZ.Applied += 2;
            }
            foreach (MethodInfo m in impactMethods)
            {
                PackZ.Prefix(h, m, typeof(SurfaceExplosions), nameof(ImpactContact_Prefix));
            }
            foreach (MethodInfo m in compMethods)
            {
                h.Patch(m, prefix: new HarmonyMethod(typeof(SurfaceExplosions), nameof(Component_Prefix)), finalizer: new HarmonyMethod(typeof(SurfaceExplosions), nameof(Source_Finalizer)));
                PackZ.Applied += 2;
            }
            PackZ.Prefix(h, doExplosion, typeof(SurfaceExplosions), nameof(Origin_Prefix));
            PackZ.Prefix(h, start, typeof(SurfaceExplosions), nameof(Start_Prefix));
            PackZ.Postfix(h, start, typeof(SurfaceExplosions), nameof(Start_Postfix));
            PackZ.Postfix(h, merge, typeof(SurfaceExplosions), nameof(Merge_Postfix));
            PackZ.Prefix(h, affect, typeof(SurfaceExplosions), nameof(Affect_Prefix));
            PackZ.Postfix(h, affect, typeof(SurfaceExplosions), nameof(Affect_Postfix));
            PackZ.Postfix(h, projectileSave, typeof(SurfaceExplosions), nameof(ProjectileSave_Postfix));
            PackZ.Postfix(h, explosionSave, typeof(SurfaceExplosions), nameof(ExplosionSave_Postfix));
        }

        // ------------------------------------------------------------------ rules
        internal static IDisposable Register(string owner, ISurfaceExplosionRule rule)
        {
            if (string.IsNullOrEmpty(owner) || rule == null)
            {
                throw new ArgumentException("A surface explosion rule needs an owner and a rule.");
            }
            foreach (Rule x in rules)
            {
                if (x.Owner == owner)
                {
                    throw new InvalidOperationException("Surface explosion rule already registered: " + owner);
                }
            }
            Rule r = new Rule { Owner = owner, Policy = rule };
            if (!Ready || !ApiParts.OnMainThread("RegisterSurfaceExplosionRule (" + owner + ")"))
            {
                return r;   // (an inert token: Api.Capabilities tells the caller the part is off)
            }
            if (!patched)
            {
                patched = true;
                if (!PackZ.ApplyLate("Surface explosions", Patch))
                {
                    Ready = false;
                    return r;
                }
            }
            r.Live = true;
            rules.Add(r);
            Changed();
            return r;
        }

        internal static void Disable()
        {
            Ready = false;
            Active = false;
        }

        private static void Changed()
        {
            Active = Ready && patched && rules.Count > 0;
        }

        private static void Fault(Rule r, string what)
        {
            if (!r.Live)
            {
                return;
            }
            r.Live = false;
            rules.Remove(r);
            Faults++;
            Changed();
            ApiParts.OwnerFault("Surface explosions", r.Owner, what);
        }

        // ------------------------------------------------------------------ contacts (from Frames)
        // A projectile's tick begins: a contact that is not final (or whose projectile flies on) is gone.
        internal static void BeginStep(ProjectileCE p)
        {
            if (!Active)
            {
                return;
            }
            if (contacts.TryGetValue(p, out ContactData value) && (!value.Final || !p.landed))
            {
                contacts.Remove(p);
            }
        }

        // Frames met a plane: upper = the plane's floor, point in frame `frame`, below = the projectile came from under it,
        // final = no CE impact test follows (a direct hit on the underside, a laser).
        internal static void Contact(ProjectileCE p, Layout l, int upper, Vector3 point, int frame, bool below, bool final)
        {
            if (!Active)
            {
                return;
            }
            Vector3 v = l.ToVirtual(point, frame);
            v.y = upper * ProjectZ.Api.StoreyHeight;
            IntVec3 g = new IntVec3(Mathf.FloorToInt(v.x), 0, Mathf.FloorToInt(v.z));
            bool floor = ProjectZ.Api.HasFloorAt(g, upper, p.Map);
            ContactData data = contacts.GetValue(p, _ => new ContactData());
            data.Map = p.Map;
            data.Ground = g;
            data.Upper = upper;
            data.Surface = l.Cell(g, floor ? upper : upper - 1);
            data.Incident = l.Cell(g, below ? upper - 1 : upper);
            data.Point = v;
            data.Normal = below ? Vector3.down : Vector3.up;
            data.Below = below;
            data.Floor = floor;
            data.Final = final;
            ContactsSeen++;
        }

        // ProjectileCE.Impact: a contact going down was only a proposal until CE accepted its ground impact there (landed,
        // nothing hit, at the plane's height); native CE can meet a roof's bounds just before the plane itself, so the
        // exact point is kept and its column read again.
        public static void ImpactContact_Prefix(ProjectileCE __instance, Thing __0)
        {
            if (!Active || !contacts.TryGetValue(__instance, out ContactData contact) || contact.Final)
            {
                return;
            }
            Vector3 exact = Frames.exactGet(__instance);
            bool planeHeight = contact.Below ? exact.y >= CollisionVertical.WallCollisionHeight - 0.01f : exact.y <= 0.01f;
            Layout l = Layout.Of(contact.Map);
            if (__0 != null || !__instance.landed || !planeHeight || l == null)
            {
                contacts.Remove(__instance);
                return;
            }
            contact.Final = true;
            contact.Point = l.ToVirtual(exact, l.FrameOf(exact));
            IntVec3 g = new IntVec3(Mathf.FloorToInt(contact.Point.x), 0, Mathf.FloorToInt(contact.Point.z));
            contact.Ground = g;
            contact.Floor = ProjectZ.Api.HasFloorAt(g, contact.Upper, contact.Map);
            contact.Surface = l.Cell(g, contact.Floor ? contact.Upper : contact.Upper - 1);
            contact.Incident = l.Cell(g, contact.Below ? contact.Upper - 1 : contact.Upper);
        }

        // ------------------------------------------------------------------ the blast's source
        public static void Source_Prefix(ProjectileCE __instance, out ProjectileCE __state)
        {
            __state = source;
            if (Active)
            {
                source = __instance;
            }
        }

        public static void Component_Prefix(object __instance, out ProjectileCE __state)
        {
            __state = source;
            if (Active)
            {
                source = (__instance as ThingComp)?.parent as ProjectileCE;
            }
        }

        public static void Source_Finalizer(ProjectileCE __state) => source = __state;

        private static bool SourceContact(out ContactData data)
        {
            data = null;
            ProjectileCE p = source;
            return p != null && contacts.TryGetValue(p, out data) && data.Final;
        }

        // GenExplosionCE.DoExplosion: a blast of a source with a final contact starts on the contact's side of the plane.
        public static void Origin_Prefix(ref IntVec3 center, Map map, ref float height)
        {
            if (!Active || !SourceContact(out ContactData contact) || contact.Map != map)
            {
                return;
            }
            center = contact.Incident;
            if (contact.Below)
            {
                height = MathF.BitDecrement(CollisionVertical.WallCollisionHeight);
            }
        }

        // ------------------------------------------------------------------ the slab cell joins the blast
        public static void Start_Prefix(ExplosionCE __instance)
        {
            if (!Active)
            {
                return;
            }
            if (targets.TryGetValue(__instance, out TerrainData previous))
            {
                previous.TerrainOnly.Clear();
            }
            if (!SourceContact(out ContactData contact) || contact.Map != __instance.Map || contact.Incident != __instance.Position)
            {
                return;
            }
            SurfaceExplosionContext context = new SurfaceExplosionContext(__instance, contact.Contact, source.def);
            bool yes = false;
            for (int i = rules.Count - 1; i >= 0 && !yes; i--)
            {
                Rule r = rules[i];
                RulesAsked++;
                try
                {
                    yes = r.Policy.ShouldDamageTerrain(context);
                }
                catch (Exception e)
                {
                    Fault(r, "threw in ShouldDamageTerrain: " + PackZ.Reason(e));
                }
            }
            if (!yes)
            {
                return;
            }
            Prune();
            if (!targets.TryGetValue(__instance, out TerrainData data))
            {
                data = new TerrainData();
                targets[__instance] = data;
            }
            if (!data.Processed.Contains(contact.Surface))
            {
                data.Pending.Add(contact.Surface);
            }
        }

        // Blasts that are over (despawned) leave the table.
        private static void Prune()
        {
            pruneScratch.Clear();
            foreach (ExplosionCE e in targets.Keys)
            {
                if (e.Destroyed || !e.Spawned)
                {
                    pruneScratch.Add(e);
                }
            }
            foreach (ExplosionCE e in pruneScratch)
            {
                targets.Remove(e);
            }
        }

        // After CE's neighbour expansion and the damage worker's ExplosionStart: the slab cell is added (terrain only, so
        // it never seeds upper-floor neighbours, fire or start effects), in the queue's affect-tick order.
        public static void Start_Postfix(ExplosionCE __instance)
        {
            if (targets.Count == 0 || !__instance.Spawned || !targets.TryGetValue(__instance, out TerrainData data) || data.Pending.Count == 0)
            {
                return;
            }
            List<IntVec3> queue = affectCells(__instance);
            foreach (IntVec3 cell in data.Pending)
            {
                if (data.Processed.Contains(cell) || queue.Contains(cell))
                {
                    continue;
                }
                data.TerrainOnly.Add(cell);
                int tick = affectTick(__instance, cell);
                int at = queue.Count;
                while (at > 0 && affectTick(__instance, queue[at - 1]) < tick)
                {
                    at--;
                }
                queue.Insert(at, cell);
                TerrainAdded++;
                LastAdded = cell + " affect tick " + tick + " (now " + Find.TickManager.TicksGame + ")";
            }
        }

        private static bool SlabCell(IntVec3 cell, Map map)
        {
            Layout l = Layout.Of(map);
            if (l == null || !cell.InBounds(map))
            {
                return false;
            }
            int k = l.FrameOf(cell);
            if (k == Layout.None || k <= l.min || !ProjectZ.Api.HasFloorAt(l.Ground(cell), k, map))
            {
                return false;
            }
            return map.terrainGrid.TopTerrainAt(cell).destroyOnBombDamageThreshold >= 0f || map.terrainGrid.UnderTerrainAt(cell)?.destroyOnBombDamageThreshold >= 0f;
        }

        // Explosion.AffectCell: a terrain-only cell gets the game's terrain step and nothing else (a missing slab: skipped).
        public static bool Affect_Prefix(Explosion __instance, IntVec3 c, out bool __state)
        {
            __state = false;
            if (targets.Count == 0 || !(__instance is ExplosionCE ce) || !targets.TryGetValue(ce, out TerrainData data) || !data.Pending.Contains(c))
            {
                return true;
            }
            if (!data.TerrainOnly.Contains(c))
            {
                __state = true;   // (a cell of the blast anyway: CE's own step, then marked done)
                return true;
            }
            data.Pending.Remove(c);
            data.Processed.Add(c);
            if (SlabCell(c, ce.Map))
            {
                TerrainHits++;
                LastDamage = ce.GetDamageAmountAt(c) + " (" + ce.damType.defName + ", " + c + ")";
                damageTerrain(ce.damType.Worker, ce, c);
            }
            return false;
        }

        public static void Affect_Postfix(Explosion __instance, IntVec3 c, bool __state)
        {
            if (!__state || !(__instance is ExplosionCE ce) || !targets.TryGetValue(ce, out TerrainData data))
            {
                return;
            }
            data.Pending.Remove(c);
            data.Processed.Add(c);
        }

        // ExplosionCE.MergeWith: the merged blast takes over both blasts' slab cells.
        public static void Merge_Postfix(ExplosionCE __instance, ExplosionCE other, ExplosionCE merged, bool __result)
        {
            if (!__result || targets.Count == 0 || merged == null)
            {
                return;
            }
            Fold(__instance, merged);
            Fold(other, merged);
            if (targets.TryGetValue(merged, out TerrainData combined))
            {
                combined.Pending.ExceptWith(combined.Processed);
            }
        }

        private static void Fold(ExplosionCE from, ExplosionCE merged)
        {
            if (from == null || from == merged || !targets.TryGetValue(from, out TerrainData data))
            {
                return;
            }
            if (!targets.TryGetValue(merged, out TerrainData into))
            {
                into = new TerrainData();
                targets[merged] = into;
            }
            into.Pending.UnionWith(data.Pending);
            into.Processed.UnionWith(data.Processed);
            into.TerrainOnly.UnionWith(data.TerrainOnly);
            targets.Remove(from);
        }

        // Harness: the slab cells live blasts still have to do.
        internal static int PendingCells()
        {
            int n = 0;
            foreach (KeyValuePair<ExplosionCE, TerrainData> kv in targets)
            {
                n += kv.Key.Spawned ? kv.Value.Pending.Count : 0;
            }
            return n;
        }

        internal static List<IntVec3> PendingList() => targets.Where(kv => kv.Key.Spawned).SelectMany(kv => kv.Value.Pending).ToList();

        // ------------------------------------------------------------------ saves
        // Written only with a live rule and only where there is something (a final contact, pending slab cells); read
        // whenever the node is there (a missing node: nothing).
        public static void ProjectileSave_Postfix(ProjectileCE __instance)
        {
            contacts.TryGetValue(__instance, out ContactData data);
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (Active && data != null && data.Final)
                {
                    Scribe_Deep.Look(ref data, ContactKey);
                }
                return;
            }
            Scribe_Deep.Look(ref data, ContactKey);
            if (data != null)
            {
                contacts.AddOrUpdate(__instance, data);
            }
        }

        public static void ExplosionSave_Postfix(ExplosionCE __instance)
        {
            targets.TryGetValue(__instance, out TerrainData data);
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (Active && data != null && data.Pending.Count > 0)
                {
                    Scribe_Deep.Look(ref data, TerrainKey);
                }
                return;
            }
            Scribe_Deep.Look(ref data, TerrainKey);
            if (data != null)
            {
                targets[__instance] = data;
            }
        }
    }
}
